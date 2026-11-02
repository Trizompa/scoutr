using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace ScoutR;

/// <summary>One pinned folder shown in Quick access or Working folders. <see cref="Group"/> says which list.</summary>
public sealed record QuickLink(string Group, string Name, string Path, ImageSource? Icon)
{
    public static QuickLink For(string group, string path)
    {
        string name = ShellInfo.DisplayName(path) ?? System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (name.Length == 0) name = path;
        return new QuickLink(group, name, path, ShellInfo.FolderIcon(path));
    }
}

/// <summary>
/// A list of pinned folders saved as JSON in %LocalAppData%\ScoutR (kept across updates).
/// Quick access starts with Desktop, Downloads, Documents; Working folders starts empty.
/// </summary>
public sealed class PinList
{
    public const string Quick = "quick", Work = "work";

    private readonly string _file;
    public string Group { get; }
    public List<string> Paths { get; }

    public PinList(string group)
    {
        Group = group;
        _file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScoutR", group == Quick ? "quickaccess.json" : "workingfolders.json");
        Paths = Load() ?? (group == Quick ? Defaults() : new List<string>());
    }

    private static List<string> Defaults() => new List<string>
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ShellInfo.DownloadsPath(),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    }.Where(p => !string.IsNullOrEmpty(p)).ToList();

    private List<string>? Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file));
        }
        catch
        {
            // Unreadable file: fall back to the defaults.
        }
        return null;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(Paths, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not fatal: the pins just won't survive a restart.
        }
    }

    private int IndexOf(string path) => Paths.FindIndex(p =>
        string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public bool Contains(string path) => IndexOf(path) >= 0;

    public bool CanMove(string path, int delta)
    {
        int i = IndexOf(path), j = i + delta;
        return i >= 0 && j >= 0 && j < Paths.Count;
    }

    public void Add(string path)
    {
        if (Contains(path) || !Directory.Exists(path)) return;
        Paths.Add(path);
        Save();
    }

    public void Remove(string path)
    {
        int i = IndexOf(path);
        if (i < 0) return;
        Paths.RemoveAt(i);
        Save();
    }

    public void Move(string path, int delta)
    {
        if (!CanMove(path, delta)) return;
        int i = IndexOf(path), j = i + delta;
        (Paths[i], Paths[j]) = (Paths[j], Paths[i]);
        Save();
    }

    public List<QuickLink> Links() => Paths.Where(Directory.Exists).Select(p => QuickLink.For(Group, p)).ToList();
}
