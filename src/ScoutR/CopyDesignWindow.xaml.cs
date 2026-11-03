using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ScoutR;

/// <summary>One row of the Copy design list: a used file, copied under a new name or reused as it is.</summary>
public sealed class DesignRow : ObservableObject
{
    private bool _copy;
    private string _newPath = "";
    private string _newName = "";
    private bool _hasProblem;

    public DesignRow(DesignFile file, string root, bool copyUsed = true)
    {
        Path = file.Path;
        Missing = file.Missing;
        IsDrawing = file.IsDrawing;
        ModelStates = file.ModelStates;
        Indent = new Thickness(file.Depth * 16, 0, 0, 0);
        // Copied by default: what lives next to the top assembly or in its folders (sketch, frame,
        // skeleton, members). Reused: Content Center, libraries, anything elsewhere, missing files.
        InRoot = Path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
                 Path.IndexOf("\\Content Center Files\\", StringComparison.OrdinalIgnoreCase) < 0;
        OldName = InRoot ? System.IO.Path.GetRelativePath(root, Path) : Path;
        // For a single part, the files it uses (derived parts, skeleton) are reused, not copied.
        _copy = InRoot && !Missing && (copyUsed || file.Depth == 0 || file.IsDrawing);
        // Frame Generator members ("... - 001.ipt") keep the Part Number Inventor generated for them.
        _renamePn = !System.Text.RegularExpressions.Regex.IsMatch(
            System.IO.Path.GetFileNameWithoutExtension(Path), @"\s-\s*\d+$");
    }

    private bool _renamePn;

    /// <summary>Also rename the "Part Number" iProperty of the copy (find → replace).</summary>
    public bool RenamePn { get => _renamePn; set => Set(ref _renamePn, value); }

    public string Path { get; }
    public bool Missing { get; }
    public bool InRoot { get; }
    public Thickness Indent { get; }
    public string OldName { get; }
    public bool CanCopy => !Missing;
    public bool CanRenamePn => _copy && (InventorProps.IsInventorFile(Path) || IsDrawing);
    public bool IsDrawing { get; }
    public int ModelStates { get; }

    public bool Copy
    {
        get => _copy;
        set { if (Set(ref _copy, value)) { OnPropertyChanged(nameof(CanRenamePn)); Changed?.Invoke(); } }
    }

    public string NewPath { get => _newPath; set => Set(ref _newPath, value); }
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public bool HasProblem { get => _hasProblem; set => Set(ref _hasProblem, value); }

    public event Action? Changed;
}

/// <summary>
/// Copy design: copies an Inventor assembly and the files it uses under new names (find → replace on the
/// path, so "Fabrication1" → "Fabrication2" also gives the new folder), and re-links the copies.
/// </summary>
public partial class CopyDesignWindow : Window
{
    private readonly string _top;
    private readonly string _root;
    private List<DesignRow> _rows = new();
    private bool _busy;

    /// <summary>The new top file, once the copy is done.</summary>
    public string? Result { get; private set; }

    public CopyDesignWindow(Window owner, string top)
    {
        InitializeComponent();
        Owner = owner;
        _top = top;
        _root = System.IO.Path.GetDirectoryName(top)!;
        RootText.Text = _root;
        FindBox.Text = System.IO.Path.GetFileNameWithoutExtension(top);
        ReplaceBox.Text = FindBox.Text;
        if (!System.IO.Path.GetExtension(top).Equals(".iam", StringComparison.OrdinalIgnoreCase))
            Title = Loc.Get("Copy_PartTitle");
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = Loc.Get("Copy_Reading");
        try
        {
            var files = await InventorProps.CollectAsync(_top);
            bool assembly = System.IO.Path.GetExtension(_top).Equals(".iam", StringComparison.OrdinalIgnoreCase);
            _rows = files.Select(f => new DesignRow(f, _root, copyUsed: assembly)).ToList();
            foreach (var r in _rows) r.Changed += Refresh;
            FileList.ItemsSource = _rows;
            Refresh();
            ReplaceBox.Focus();
            ReplaceBox.SelectAll();

            // Then the drawings that show any of these files (searched in the folder and its subfolders).
            var progress = new Progress<string>(n => StatusText.Text = Loc.Get("Copy_SearchingDrawings", n));
            _busy = true; // no Copy until the search is over, or the drawings would be left out
            CopyBtn.IsEnabled = false;
            List<DesignFile> drawings;
            try
            {
                drawings = await InventorProps.FindDrawingsAsync(_root, _rows.Select(r => r.Path).ToList(), progress);
            }
            finally
            {
                _busy = false;
            }
            foreach (var d in drawings.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
            {
                var row = new DesignRow(d, _root);
                row.Changed += Refresh;
                _rows.Add(row);
            }
            FileList.ItemsSource = null;
            FileList.ItemsSource = _rows;
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Get("Msg_Error", ex.Message);
        }
    }

    private void Rule_Changed(object sender, TextChangedEventArgs e) => Refresh();

    /// <summary>Works out the new names and checks them; the Copy button is enabled only when all is fine.</summary>
    private void Refresh()
    {
        if (!IsInitialized || _rows.Count == 0) return;
        string find = FindBox.Text, replace = ReplaceBox.Text;
        string? problem = null;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in _rows)
        {
            r.HasProblem = false;
            if (!r.Copy)
            {
                r.NewPath = r.Path;
                r.NewName = Loc.Get(r.Missing ? "Copy_Missing" : "Copy_Reuse");
                r.HasProblem = r.Missing;
                continue;
            }
            string rel = System.IO.Path.GetRelativePath(_root, r.Path);
            string newRel = find.Length == 0 ? rel : rel.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
            r.NewPath = System.IO.Path.Combine(_root, newRel);
            r.NewName = newRel;

            string? issue = null;
            if (string.Equals(newRel, rel, StringComparison.OrdinalIgnoreCase)) issue = Loc.Get("Copy_SameName");
            else if (File.Exists(r.NewPath)) issue = Loc.Get("Copy_Exists", newRel);
            else if (!targets.Add(r.NewPath)) issue = Loc.Get("Copy_Duplicate", newRel);
            if (issue is not null)
            {
                r.HasProblem = true;
                problem ??= issue;
            }
        }

        bool topCopied = _rows[0].Copy;
        if (!topCopied) problem ??= Loc.Get("Copy_TopNeeded");
        int n = _rows.Count(r => r.Copy);
        StatusText.Text = problem ?? Loc.Get("Copy_Summary", n, _rows.Count - n);
        CopyBtn.IsEnabled = problem is null && !_busy;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var map = _rows.Where(r => r.Copy).ToDictionary(r => r.Path, r => r.NewPath, StringComparer.OrdinalIgnoreCase);
        _busy = true;
        CopyBtn.IsEnabled = false;
        FileList.IsEnabled = FindBox.IsEnabled = ReplaceBox.IsEnabled = false;
        var renamePn = new HashSet<string>(_rows.Where(r => r.Copy && r.RenamePn).Select(r => r.NewPath),
            StringComparer.OrdinalIgnoreCase);
        var progress = new Progress<string>(name => StatusText.Text = Loc.Get("Copy_Working", name));
        try
        {
            var warnings = await InventorProps.CopyDesignAsync(map, FindBox.Text, ReplaceBox.Text, renamePn, progress);
            if (warnings.Count > 0)
                MessageBox.Show(this, Loc.Get("Copy_PnWarning", string.Join("\n", warnings)), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            Result = map[_top];
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _busy = false;
            StatusText.Text = Loc.Get("Msg_Error", ex.Message);
            MessageBox.Show(this, Loc.Get("Copy_Failed", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            CancelBtn.Content = Loc.Get("IProps_Close");
        }
    }
}
