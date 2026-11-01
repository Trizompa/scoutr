using System.IO;
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VbRecycle = Microsoft.VisualBasic.FileIO.RecycleOption;
using VbUi = Microsoft.VisualBasic.FileIO.UIOption;

namespace ScoutR;

/// <summary>Plain file operations. No UI here; callers run these off the UI thread.</summary>
public static class FileOps
{
    private static string Trim(string p) => p.Length > 3 ? p.TrimEnd('\\', '/') : p;

    public static string UniqueTarget(string dir, string name, bool isDir)
    {
        string first = Path.Combine(dir, name);
        if (!File.Exists(first) && !Directory.Exists(first)) return first;

        string ext = isDir ? "" : Path.GetExtension(name);
        string stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        for (int i = 1; ; i++)
        {
            string suffix = i == 1 ? " - Copy" : $" - Copy ({i})";
            string candidate = Path.Combine(dir, stem + suffix + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Copies or moves one file/folder into destDir. Name clashes get a " - Copy" suffix.</summary>
    public static string Transfer(string source, string destDir, bool move)
    {
        source = Trim(source);
        destDir = Trim(destDir);
        bool isDir = Directory.Exists(source);
        string name = Path.GetFileName(source);

        if (move && string.Equals(Path.GetDirectoryName(source), destDir, StringComparison.OrdinalIgnoreCase))
            return source; // already there

        if (isDir)
        {
            string src = Path.GetFullPath(source);
            string dst = Path.GetFullPath(destDir);
            bool inside = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) ||
                          dst.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (inside) throw new IOException($"Cannot copy or move '{name}' into itself.");
        }

        string target = UniqueTarget(destDir, name, isDir);

        if (isDir)
        {
            bool sameRoot = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target),
                StringComparison.OrdinalIgnoreCase);
            if (move && sameRoot)
            {
                Directory.Move(source, target);
            }
            else
            {
                CopyDirectory(source, target);
                if (move) Directory.Delete(source, true);
            }
        }
        else if (move)
        {
            File.Move(source, target);
        }
        else
        {
            File.Copy(source, target);
        }

        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    public static void Recycle(string path)
    {
        path = Trim(path);
        if (Directory.Exists(path))
            VbFileSystem.DeleteDirectory(path, VbUi.OnlyErrorDialogs, VbRecycle.SendToRecycleBin);
        else
            VbFileSystem.DeleteFile(path, VbUi.OnlyErrorDialogs, VbRecycle.SendToRecycleBin);
    }

    public static string Rename(string path, string newName)
    {
        path = Trim(path);
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("The name contains invalid characters.");

        string target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (string.Equals(path, target, StringComparison.Ordinal)) return path;

        bool caseOnly = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException($"'{newName}' already exists.");

        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return target;
    }

    public static string CreateFolder(string parent, string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("The name contains invalid characters.");
        // Like Explorer: "New folder", "New folder (2)", "New folder (3)"...
        string target = Path.Combine(parent, name);
        for (int i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(parent, $"{name} ({i})");
        Directory.CreateDirectory(target);
        return target;
    }
}
