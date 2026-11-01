using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace ScoutR;

/// <summary>
/// The single folder tree. It shows only the active pane: each pane keeps its own set of
/// expanded folders, and ShowFor() swaps to the set of the pane that just became active.
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

    private readonly Dictionary<string, HashSet<string>> _saved = new();
    private string? _context;

    /// <summary>
    /// Shows the tree for one pane (<paramref name="context"/> is a pane id) and selects <paramref name="path"/>.
    /// When the pane differs from the last call, the previous pane's open folders are saved and
    /// replaced with this pane's own, so the two panes never mix.
    /// </summary>
    public void ShowFor(string context, string path)
    {
        Suppress = true;
        try
        {
            if (_context != context)
            {
                if (_context is not null)
                {
                    var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectExpanded(Root, open);
                    _saved[_context] = open;
                }

                CollapseAll(Root);
                _context = context;

                if (_saved.TryGetValue(context, out var mine))
                    foreach (string folder in mine) WalkCore(folder, select: false);
            }

            ClearSelection(Root);
            WalkCore(path, select: true);
        }
        finally
        {
            Suppress = false;
        }
    }

    private void WalkCore(string path, bool select)
    {
        var node = Root;
        node.IsExpanded = true;

        foreach (string segment in Chain(path))
        {
            node.EnsureLoaded();
            var child = node.Children.FirstOrDefault(c =>
                !c.IsDummy && string.Equals(c.Path, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) break;
            node = child;
            node.IsExpanded = true;
        }

        if (select) node.IsSelected = true;
    }

    private static void CollectExpanded(TreeNodeVm node, HashSet<string> into)
    {
        foreach (var child in node.Children)
        {
            if (child.IsDummy) continue;
            if (child.IsExpanded) into.Add(child.Path);
            CollectExpanded(child, into);
        }
    }

    private static void CollapseAll(TreeNodeVm node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsDummy) continue;
            child.IsExpanded = false;
            CollapseAll(child);
        }
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

    public string Glyph => IsRoot ? "\uE7F4" : Path.Length <= 3 && Path.EndsWith('\\') ? "\uEDA2" : "\uE8B7";

    public Brush GlyphBrush => IsRoot ? Palette.Root : Glyph == "\uEDA2" ? Palette.Drive : Palette.Folder;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (Set(ref _expanded, value) && value) EnsureLoaded();
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
                foreach (var drive in DriveInfo.GetDrives())
                {
                    var item = FsItem.FromDrive(drive);
                    Children.Add(new TreeNodeVm(_model, item.Name, drive.Name));
                }
            }
            else
            {
                var dirs = new DirectoryInfo(Path).EnumerateDirectories()
                    .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
                foreach (var d in dirs)
                    Children.Add(new TreeNodeVm(_model, d.Name, d.FullName));
            }
        }
        catch
        {
            // Unreadable folder: leave it without children.
        }
    }

}
