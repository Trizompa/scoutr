using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace ScoutR;

/// <summary>
/// The single folder tree, like Explorer's navigation pane: it does not follow the panes by itself;
/// the "Show folder" button expands it to the active pane's folder.
/// </summary>
public sealed class TreeModel
{
    public ObservableCollection<TreeNodeVm> Roots { get; } = new();
    public TreeNodeVm Root { get; }

    /// <summary>True while the tree is being changed by code, so it does not navigate a pane.</summary>
    public bool Suppress { get; private set; }

    /// <summary>Raised when the user clicks a folder in the tree.</summary>
    public event Action<string>? UserNavigated;

    public TreeModel()
    {
        Root = new TreeNodeVm(this, Loc.Get("ThisPC"), "", isRoot: true);
        Roots.Add(Root);
        Root.IsExpanded = true;
    }

    internal void NodeSelected(string path)
    {
        if (!Suppress) UserNavigated?.Invoke(path);
    }

    /// <summary>
    /// "Show folder": expands the tree down to <paramref name="path"/> and selects it, like Explorer's
    /// "Expand to open folder". Other open branches are left as they are.
    /// </summary>
    public void Reveal(string path)
    {
        Suppress = true;
        try
        {
            _revealed.Clear();
            _prevSelected = FindSelected(Root);
            if (path.Length == 0) { ClearSelection(Root); Root.IsSelected = true; }
            else WalkCore(path, select: true);
            IsRevealed = true;
        }
        finally
        {
            Suppress = false;
        }
    }

    // What "Show folder" opened, so "Collapse" can put the tree back as it was.
    private readonly List<TreeNodeVm> _revealed = new();
    private TreeNodeVm? _prevSelected;

    /// <summary>True after "Show folder", until "Collapse" undoes it.</summary>
    public bool IsRevealed { get; private set; }

    /// <summary>"Collapse": closes the branches "Show folder" opened and restores the previous selection.</summary>
    public void Unreveal()
    {
        Suppress = true;
        try
        {
            for (int i = _revealed.Count - 1; i >= 0; i--) _revealed[i].IsExpanded = false;
            ClearSelection(Root);
            if (_prevSelected is not null) _prevSelected.IsSelected = true;
            _revealed.Clear();
            _prevSelected = null;
            IsRevealed = false;
        }
        finally
        {
            Suppress = false;
        }
    }

    private static TreeNodeVm? FindSelected(TreeNodeVm node)
    {
        if (node.IsSelected) return node;
        if (!node.IsExpanded) return null;
        foreach (var child in node.Children)
            if (!child.IsDummy && FindSelected(child) is { } found) return found;
        return null;
    }

    private void Expand(TreeNodeVm node)
    {
        if (node.IsExpanded) return;
        _revealed.Add(node);
        node.IsExpanded = true;
    }

    private void WalkCore(string path, bool select)
    {
        var node = Root;
        Expand(node);

        foreach (string segment in Chain(path))
        {
            node.EnsureLoaded();
            var child = node.Children.FirstOrDefault(c =>
                !c.IsDummy && string.Equals(c.Path, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) break;
            node = child;
            Expand(node);
        }

        if (select) node.IsSelected = true;
    }

    private static void ClearSelection(TreeNodeVm node)
    {
        node.IsSelected = false;
        foreach (var child in node.Children)
            if (!child.IsDummy) ClearSelection(child);
    }

    /// <summary>C:\Users\Me -> C:\, C:\Users, C:\Users\Me</summary>
    private static IEnumerable<string> Chain(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        string root = Path.GetPathRoot(path) ?? "";
        if (root.Length == 0) yield break;

        yield return root;
        string current = root;
        string rest = path.Substring(root.Length);
        foreach (string part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            yield return current;
        }
    }
}

public sealed class TreeNodeVm : ObservableObject
{
    private readonly TreeModel _model;
    private bool _loaded;
    private bool _expanded;
    private bool _selected;

    public TreeNodeVm(TreeModel model, string name, string path, bool isRoot = false, bool isDummy = false)
    {
        _model = model;
        Name = name;
        Path = path;
        IsRoot = isRoot;
        IsDummy = isDummy;
        // A placeholder child makes the expander arrow appear; real children load on first expand.
        if (!isDummy) Children.Add(new TreeNodeVm(model, "", "", isDummy: true));
    }

    public string Name { get; }
    public string Path { get; }
    public bool IsRoot { get; }
    public bool IsDummy { get; }
    public ObservableCollection<TreeNodeVm> Children { get; } = new();

    public bool IsFolderShape => !IsRoot && !IsDummy;

    /// <summary>Windows' own folder / drive icon (open folder when expanded).</summary>
    public System.Windows.Media.ImageSource? Icon =>
        IsDrive ? ShellInfo.DriveIcon(Path) :
        CustomIcon ? ShellInfo.FolderIcon(Path) :
        IsExpanded ? ShellIcons.FolderOpen : ShellIcons.Folder;

    /// <summary>Folder with its own Windows icon (desktop.ini).</summary>
    public bool CustomIcon { get; init; }

    public bool ShowShellIcon => IsFolderShape && ShellIcons.Available;
    public bool ShowDrawnIcon => IsFolderShape && !ShellIcons.Available;

    private bool IsDrive => !IsRoot && Path.Length <= 3 && Path.EndsWith('\\');

    public string Glyph => IsRoot ? "\uE7F4" : Path.Length <= 3 && Path.EndsWith('\\') ? "\uEDA2" : "\uE8B7";

    public Brush GlyphBrush => IsRoot ? Palette.Root : Glyph == "\uEDA2" ? Palette.Drive : Palette.Folder;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (Set(ref _expanded, value))
            {
                OnPropertyChanged(nameof(Icon));
                if (value) EnsureLoaded();
            }
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && value) _model.NodeSelected(Path);
        }
    }

    public void EnsureLoaded()
    {
        if (_loaded || IsDummy) return;
        _loaded = true;
        Children.Clear();

        try
        {
            if (IsRoot)
            {
                foreach (var drive in FsItem.VisibleDrives())
                {
                    var item = FsItem.FromDrive(drive);
                    Children.Add(new TreeNodeVm(_model, item.Name, drive.Name));
                }
            }
            else
            {
                var dirs = new DirectoryInfo(Path).EnumerateDirectories()
                    .Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                    .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
                foreach (var d in dirs)
                    Children.Add(new TreeNodeVm(_model, d.Name, d.FullName) { CustomIcon = ShellInfo.HasCustomIcon(d) });
            }
        }
        catch
        {
            // Unreadable folder: leave it without children.
        }
    }

}
