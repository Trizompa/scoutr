using System.IO;
using System.Windows.Media;

namespace ScoutR;

/// <summary>One entry of the Quick access list above the folder tree.</summary>
public sealed record QuickLink(string Name, string Path, ImageSource? Icon)
{
    public static List<QuickLink> Defaults()
    {
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShellInfo.DownloadsPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        var list = new List<QuickLink>();
        foreach (string p in paths)
        {
            if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) continue;
            string name = ShellInfo.DisplayName(p) ?? System.IO.Path.GetFileName(p);
            list.Add(new QuickLink(name, p, ShellInfo.FolderIcon(p)));
        }
        return list;
    }
}
