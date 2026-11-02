using System.IO;

namespace ScoutR;

/// <summary>One file used by the assembly being copied (found by walking its references).</summary>
public sealed record DesignFile(string Path, bool Missing, int Depth);

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

    /// <summary>
    /// Copies the files (old path → new path), then rewrites the references inside each copy so that it
    /// uses the new files. In the copies listed in <paramref name="renamePartNumber"/> (new paths),
    /// "Part Number" is renamed the same way.
    /// </summary>
    public static Task<int> CopyDesignAsync(IReadOnlyDictionary<string, string> map, string find, string replace,
        ISet<string> renamePartNumber, IProgress<string> progress) => Worker.Run(() =>
    {
        // 1. Plain file copies, never over an existing file.
        foreach (var (from, to) in map)
            if (File.Exists(to)) throw new IOException(Loc.Get("Copy_Exists", to));
        foreach (var (from, to) in map)
        {
            progress.Report(Path.GetFileName(to));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            var attr = File.GetAttributes(to);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, attr & ~FileAttributes.ReadOnly);
        }

        // 2. Re-link each copy: parts first, assemblies after (a copy keeps the original's internal id,
        //    which is what lets ReplaceReference accept it).
        int relinked = 0;
        foreach (string to in map.Values.Where(IsInventorFile)
                     .OrderBy(p => Path.GetExtension(p).Equals(".ipt", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            progress.Report(Path.GetFileName(to));
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

                bool props = renamePartNumber.Contains(to) && RenamePartNumber(doc, find, replace);

                if (refs)
                {
                    object save = Get(App(), "FileSaveAs")!;
                    Call(save, "AddFileToSave", doc, to);
                    Call(save, "ExecuteSave");
                    relinked++;
                }
                else if (props)
                {
                    Call(Get(doc, "PropertySets")!, "FlushToFile");
                }
            }
            finally
            {
                Close(doc);
            }
        }
        return relinked;
    });

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
