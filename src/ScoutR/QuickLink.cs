using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace ScoutR;

/// <summary>One entry of the Quick access list above the folder tree.</summary>
public sealed record QuickLink(string Name, string Path, ImageSource? Icon)
{
    public static QuickLink For(string path)
    {
        string name = ShellInfo.DisplayName(path) ?? System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (name.Length == 0) name = path;
        return new QuickLink(name, path, ShellInfo.FolderIcon(path));
    }
}

/// <summary>
/// The pinned folders, saved in %LocalAppData%\ScoutR\quickaccess.json (kept across updates).
/// First run: Desktop, Downloads, Documents.
/// </summary>
public static class QuickAccess
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScoutR", "quickaccess.json");

    public static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Unreadable file: fall back to the defaults.
        }

        return new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShellInfo.DownloadsPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        }.Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public static void Save(List<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not fatal: the pins just won't survive a restart.
        }
    }
}
