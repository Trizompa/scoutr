using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace ScoutR;

/// <summary>
/// The single folder tree. It always mirrors the active pane: SelectPath() expands and selects
/// that folder, MarkOther() shows a dot on the folder of the inactive pane.
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

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it.</summary>
    public void SelectPath(string path) => Walk(path, select: true);

    /// <summary>Expands the tree down to <paramref name="path"/> without selecting it.</summary>
    public void Reveal(string path) => Walk(path, select: false);

    public void MarkOther(string path) => Root.MarkOther(path);

    private void Walk(string path, bool select)
    {
        Suppress = true;
        try
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
        finally
        {
            Suppress = false;
        }
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
    private bool _other;

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

    /// <summary>True when the other (inactive) pane is showing this folder.</summary>
    public bool IsOther
    {
        get => _other;
        private set => Set(ref _other, value);
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

    public void MarkOther(string path)
    {
        IsOther = !IsDummy && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);
        if (!_loaded) return;
        foreach (var child in Children) child.MarkOther(path);
    }
}
