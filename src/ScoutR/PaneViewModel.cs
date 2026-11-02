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

    /// <summary>After moving up to a parent folder: the child folder we came from (selected, like Explorer).</summary>
    public string? CameFrom { get; private set; }

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
            list = Sort(Enumerate(path, ""));
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            return false;
        }

        if (record && !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
            _back.Push(CurrentPath);

        // Going up to a parent (Up, breadcrumb, Back): remember the child we came from, to select it.
        CameFrom = null;
        string old = CurrentPath;
        string prefix = path.Length == 0 ? "" : path.TrimEnd('\\') + "\\";
        if (old.Length > path.Length && old.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = old.Substring(prefix.Length);
            int cut = rest.IndexOf('\\');
            CameFrom = prefix + (cut < 0 ? rest : rest.Substring(0, cut));
        }
        else if (path.Length == 0 && old.Length > 0)
        {
            CameFrom = Path.GetPathRoot(old); // back to This PC: select the drive
        }

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
            Items = Sort(Enumerate(CurrentPath, Filter));
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

    // ---- Sorting (column header click) ----

    public enum SortKey { Name, Modified, Type, Size }

    public SortKey SortBy { get; private set; } = SortKey.Name;
    public bool SortDescending { get; private set; }

    /// <summary>Click on a column: sort by it, or flip the order if it is already the sort column.</summary>
    /// <summary>Restores a saved sort without the click toggling.</summary>
    public void ApplySort(SortKey key, bool descending)
    {
        SortBy = key;
        SortDescending = descending;
        Items = Sort(Items);
    }

    public void SetSort(SortKey key)
    {
        if (SortBy == key) SortDescending = !SortDescending;
        else
        {
            SortBy = key;
            SortDescending = key is SortKey.Modified or SortKey.Size; // newest / biggest first, like Explorer
        }
        Items = Sort(Items);
    }

    /// <summary>Folders always come first (like Explorer); within each group the chosen column decides.</summary>
    private List<FsItem> Sort(List<FsItem> items)
    {
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        IEnumerable<FsItem> folders = items.Where(i => i.IsDirectory), files = items.Where(i => !i.IsDirectory);

        IOrderedEnumerable<FsItem> Order(IEnumerable<FsItem> src) => (SortBy, SortDescending) switch
        {
            (SortKey.Modified, false) => src.OrderBy(i => i.Modified ?? DateTime.MinValue),
            (SortKey.Modified, true) => src.OrderByDescending(i => i.Modified ?? DateTime.MinValue),
            (SortKey.Size, false) => src.OrderBy(i => i.Size ?? -1),
            (SortKey.Size, true) => src.OrderByDescending(i => i.Size ?? -1),
            (SortKey.Type, false) => src.OrderBy(i => i.TypeText, cmp),
            (SortKey.Type, true) => src.OrderByDescending(i => i.TypeText, cmp),
            (_, false) => src.OrderBy(i => i.Name, cmp),
            _ => src.OrderByDescending(i => i.Name, cmp),
        };

        // Ties (same size, same type...) are broken by name.
        return Order(folders).ThenBy(i => i.Name, cmp).Concat(Order(files).ThenBy(i => i.Name, cmp)).ToList();
    }

    private static List<FsItem> Enumerate(string path, string filter)
    {
        var list = new List<FsItem>();

        if (path.Length == 0)
        {
            foreach (var drive in FsItem.VisibleDrives())
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
                if ((info.Attributes & FileAttributes.Hidden) != 0) continue; // like Explorer: System alone is still shown (customised folders)
            }
            catch
            {
                continue;
            }

            if (filter.Length > 0 && !info.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(FsItem.FromInfo(info));
        }

        return list;
    }
}
