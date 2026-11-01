using System.Diagnostics;
using System.IO;

namespace ScoutR;

/// <summary>One clickable segment of the address bar.</summary>
public sealed record Crumb(string Name, string Target);

/// <summary>State of one folder pane: current folder, its items, filter, history.</summary>
public sealed class PaneViewModel : ObservableObject
{
    private readonly Stack<string> _back = new();
    private string _currentPath = "";
    private string _filter = "";
    private bool _isActive;
    private List<FsItem> _items = new();

    public event Action<PaneViewModel>? Navigated;
    public event Action<PaneViewModel, string>? Error;

    public PaneViewModel(string startPath)
    {
        if (!Navigate(startPath, false)) Navigate("", false);
    }

    /// <summary>"" means "This PC" (the drive list).</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (Set(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(FolderName));
                OnPropertyChanged(nameof(Crumbs));
                OnPropertyChanged(nameof(SearchPlaceholder));
            }
        }
    }

    /// <summary>"This PC", then the drive, then each folder down to the current one.</summary>
    public List<Crumb> Crumbs
    {
        get
        {
            var list = new List<Crumb> { new(Loc.Get("ThisPC"), "") };
            if (CurrentPath.Length == 0) return list;

            string root = Path.GetPathRoot(CurrentPath) ?? "";
            if (root.Length == 0) return list;

            list.Add(new Crumb(root.TrimEnd('\\', '/'), root));
            string current = root;
            string rest = CurrentPath.Substring(root.Length);
            foreach (string part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                list.Add(new Crumb(part, current));
            }
            return list;
        }
    }

    public string SearchPlaceholder => Loc.Get("Search_Placeholder", FolderName);

    public string DisplayPath => CurrentPath.Length == 0 ? Loc.Get("ThisPC") : CurrentPath;

    public string FolderName
    {
        get
        {
            if (CurrentPath.Length == 0) return Loc.Get("ThisPC");
            string n = Path.GetFileName(CurrentPath.TrimEnd('\\', '/'));
            return n.Length == 0 ? CurrentPath : n;
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) Reload();
        }
    }

    public List<FsItem> Items
    {
        get => _items;
        private set
        {
            _items = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => Loc.Get("Status_Items", Items.Count);

    public bool CanGoBack => _back.Count > 0;

    public static string Normalize(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return "";
        if (path.Length == 2 && path[1] == ':') path += "\\";
        path = Path.GetFullPath(path);
        string root = Path.GetPathRoot(path) ?? "";
        if (path.Length > root.Length) path = path.TrimEnd('\\', '/');
        return path;
    }

    public bool Navigate(string path, bool record = true)
    {
        List<FsItem> list;
        try
        {
            path = Normalize(path);
            list = Enumerate(path, "");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            return false;
        }

        if (record && !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
            _back.Push(CurrentPath);

        CurrentPath = path;
        _filter = "";
        OnPropertyChanged(nameof(Filter));
        Items = list;
        OnPropertyChanged(nameof(CanGoBack));
        Navigated?.Invoke(this);
        return true;
    }

    public void NavigateFromText(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || string.Equals(t, Loc.Get("ThisPC"), StringComparison.CurrentCultureIgnoreCase))
            Navigate("");
        else
            Navigate(t);
        OnPropertyChanged(nameof(DisplayPath)); // restore the text box if navigation failed
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        string previous = _back.Pop();
        if (!Navigate(previous, false)) _back.Push(previous);
        OnPropertyChanged(nameof(CanGoBack));
    }

    public void GoUp()
    {
        if (CurrentPath.Length == 0) return;
        string? parent = Path.GetDirectoryName(CurrentPath);
        Navigate(parent ?? "");
    }

    public void Reload()
    {
        try
        {
            Items = Enumerate(CurrentPath, Filter);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    public void Open(FsItem item)
    {
        if (item.IsDirectory)
        {
            Navigate(item.FullPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    private static List<FsItem> Enumerate(string path, string filter)
    {
        var list = new List<FsItem>();

        if (path.Length == 0)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var item = FsItem.FromDrive(drive);
                if (filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    list.Add(item);
            }
            return list;
        }

        var dir = new DirectoryInfo(path);
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            try
            {
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            }
            catch
            {
                continue;
            }

            if (filter.Length > 0 && !info.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(FsItem.FromInfo(info));
        }

        return list
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
