using System.IO;

namespace ScoutR;

/// <summary>One file used by the assembly being copied (found by walking its references).</summary>
public sealed record DesignFile(string Path, bool Missing, int Depth, bool IsDrawing = false);

/// <summary>
/// "Copy design": copies an assembly with the files it uses (Frame Generator sketch, skeleton, frame
/// assembly, members...) under new names, then points every copy at the other copies instead of the
/// originals, like Inventor's own Copy Design. The originals are never changed.
/// </summary>
public static partial class InventorProps
{
    /// <summary>The top file and every file it uses, directly or through sub-assemblies, each once.</summary>
    public static Task<List<DesignFile>> CollectAsync(string top) => Worker.Run(() =>
    {
        var list = new List<DesignFile> { new(top, false, 0) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { top };
        object doc = Open(top);
        try
        {
            Walk(Get(doc, "File")!, 1);
        }
        finally
        {
            Close(doc);
        }
        return list;

        void Walk(object file, int depth)
        {
            foreach (object d in Items(Get(file, "ReferencedFileDescriptors")!))
            {
                string path = Get(d, "FullFileName") as string ?? "";
                if (path.Length == 0 || !seen.Add(path)) continue;
                bool missing = false;
                try { missing = Get(d, "ReferenceMissing") is true; } catch { }
                list.Add(new DesignFile(path, missing, depth));
                if (missing) continue;
                try
                {
                    if (Get(d, "ReferencedFile") is { } child) Walk(child, depth + 1);
                }
                catch { /* unreadable reference: listed, not followed */ }
            }
        }
    });

    private static bool IsLinkable(string path) =>
        IsInventorFile(path) || Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drawings (.idw, Inventor .dwg) and presentations (.ipn) under <paramref name="root"/> that show any
    /// of <paramref name="models"/>: models don't know their drawings, so the folders are searched.
    /// Inventor's "OldVersions" backups are skipped; files Apprentice can't open (AutoCAD .dwg) too.
    /// </summary>
    public static Task<List<DesignFile>> FindDrawingsAsync(string root, IReadOnlyCollection<string> models,
        IProgress<string> progress) => Worker.Run(() =>
    {
        var wanted = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
        var found = new List<DesignFile>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".idw" or ".dwg" or ".ipn" &&
                            f.IndexOf("\\OldVersions\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                            !wanted.Contains(f))
                .ToList();
        }
        catch
        {
            return found;
        }

        foreach (string f in files)
        {
            progress.Report(Path.GetFileName(f));
            object? doc = null;
            try
            {
                doc = Open(f);
                bool uses = Items(Get(Get(doc, "File")!, "ReferencedFileDescriptors")!)
                    .Any(d => Get(d, "FullFileName") is string p && wanted.Contains(p));
                if (uses) found.Add(new DesignFile(f, false, 0, IsDrawing: true));
            }
            catch
            {
                // not an Inventor file, or unreadable: skipped
            }
            finally
            {
                if (doc is not null) Close(doc);
            }
        }
        return found;
    });

    /// <summary>
    /// Copies the files (old path → new path), then rewrites the references inside each copy so that it
    /// uses the new files. In the copies listed in <paramref name="renamePartNumber"/> (new paths),
    /// "Part Number" is renamed the same way.
    /// </summary>
    public static Task<List<string>> CopyDesignAsync(IReadOnlyDictionary<string, string> map, string find,
        string replace, ISet<string> renamePartNumber, IProgress<string> progress) => Worker.Run(() =>
    {
        // 1. Plain file copies, never over an existing file.
        foreach (var (from, to) in map)
            if (File.Exists(to)) throw new IOException(Loc.Get("Copy_Exists", to));
        var created = new List<string>();
        var newDirs = new List<string>();
        try
        {
            return CopyAndLink(map, find, replace, renamePartNumber, progress, created, newDirs);
        }
        catch
        {
            // Half-done copies are useless: remove what this run created (never the originals).
            foreach (string f in created)
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            foreach (string d in newDirs.OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            throw;
        }
    });

    private static List<string> CopyAndLink(IReadOnlyDictionary<string, string> map, string find, string replace,
        ISet<string> renamePartNumber, IProgress<string> progress, List<string> created, List<string> newDirs)
    {
        string step = "", file = "";
        try
        {
        step = "copy";
        foreach (var (from, to) in map)
        {
            file = Path.GetFileName(to);
            progress.Report(file);
            string dir = Path.GetDirectoryName(to)!;
            for (string? d = dir; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) newDirs.Add(d);
            Directory.CreateDirectory(dir);
            File.Copy(from, to);
            created.Add(to);
            var attr = File.GetAttributes(to);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, attr & ~FileAttributes.ReadOnly);
        }

        // 2. Part Numbers, on the fresh copies before anything else loads them (a file already held by
        //    an assembly or drawing refuses property edits). The re-link save keeps what is on disk.
        step = "Part Number";
        var warnings = new List<string>();
        foreach (string to in map.Values.Where(p => IsLinkable(p) && renamePartNumber.Contains(p)))
        {
            file = Path.GetFileName(to);
            progress.Report(file);
            object doc = Open(to);
            try
            {
                if (RenamePartNumber(doc, find, replace)) Call(Get(doc, "PropertySets")!, "FlushToFile");
            }
            catch (Exception ex)
            {
                // The copy itself is fine: report the Part Number, don't undo everything for it.
                warnings.Add($"{file}: {ex.Message}");
            }
            finally
            {
                Close(doc);
            }
        }
        // 3. Re-link each copy: parts first, assemblies after (a copy keeps the original's internal id,
        //    which is what lets ReplaceReference accept it).
        int relinked = 0;
        step = "re-link";
        foreach (string to in map.Values.Where(IsLinkable)
                     .OrderBy(p => Path.GetExtension(p).ToLowerInvariant() switch
                     {
                         ".ipt" => 0, ".iam" => 1, _ => 2, // drawings last
                     }))
        {
            file = Path.GetFileName(to);
            progress.Report(file);
            object doc = Open(to);
            try
            {
                bool refs = false;
                foreach (object d in Items(Get(Get(doc, "File")!, "ReferencedFileDescriptors")!).ToList())
                {
                    string path = Get(d, "FullFileName") as string ?? "";
                    if (map.TryGetValue(path, out string? target))
                    {
                        Call(d, "ReplaceReference", target);
                        refs = true;
                    }
                }

                if (refs)
                {
                    object save = Get(App(), "FileSaveAs")!;
                    Call(save, "AddFileToSave", doc, to);
                    Call(save, "ExecuteSave");
                    relinked++;
                }
            }
            finally
            {
                Close(doc);
            }
        }
        return warnings;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{step} – {file}: {ex.Message}", ex);
        }
    }

    private static bool RenamePartNumber(object doc, string find, string replace)
    {
        if (find.Length == 0) return false;
        object? set = Items(Get(doc, "PropertySets")!).FirstOrDefault(x => Get(x, "Name") as string == ProjectSet);
        object? prop = set is null ? null : Items(set).FirstOrDefault(x => Get(x, "Name") as string == "Part Number");
        if (prop is null || Get(prop, "Value") is not string value ||
            value.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0) return false;
        SetValue(prop, "Value", value.Replace(find, replace, StringComparison.OrdinalIgnoreCase));
        return true;
    }
}
