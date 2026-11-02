using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ScoutR;

/// <summary>One editable row in the iProperties panel.</summary>
public sealed class IPropRow : ObservableObject
{
    private string _value;

    public IPropRow(IPropValue source, bool isNew = false)
    {
        Source = source;
        IsNew = isNew;
        Original = InventorProps.Format(source.Value, source.Name);
        _value = Original;
    }

    /// <summary>Custom property added in the panel, not yet written to the file.</summary>
    public bool IsNew { get; private set; }

    public IPropValue Source { get; }
    public string Label => Source.Label;
    public bool ReadOnly => Source.ReadOnly;
    public string Original { get; private set; }

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool IsDirty => IsNew || (!ReadOnly && _value != Original);

    public void Revert() => Value = Original;

    public void Accept()
    {
        Original = _value;
        IsNew = false;
        OnPropertyChanged(nameof(IsDirty));
    }
}

/// <summary>Bottom panel showing (and editing) the iProperties of the selected Inventor file.</summary>
public partial class IPropsPanel : UserControl
{
    public ObservableCollection<IPropRow> Summary { get; } = new();
    public ObservableCollection<IPropRow> Project { get; } = new();
    public ObservableCollection<IPropRow> Custom { get; } = new();

    private string? _path;
    private int _token;
    private List<IPropValue> _props = new();
    private readonly List<IPropRow> _deleted = new();

    /// <summary>Standard fields the user hid (shared with the saved settings).</summary>
    public HashSet<string> Hidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the user shows/hides a field, so the choice can be saved.</summary>
    public event EventHandler? HiddenChanged;

    public event EventHandler? CloseRequested;

    /// <summary>Raised after a save, so the panes can refresh (the file's date changes).</summary>
    public event EventHandler? Saved;

    public IPropsPanel()
    {
        InitializeComponent();
        DataContext = this;
        ShowMessage(Loc.Get("IProps_SelectFile"));
    }

    private IEnumerable<IPropRow> AllRows => Summary.Concat(Project).Concat(Custom);

    public bool IsDirty => _deleted.Count > 0 || AllRows.Any(r => r.IsDirty);

    private void ShowMessage(string text)
    {
        Body.Visibility = Visibility.Collapsed;
        FileText.Text = "";
        InfoText.Text = text;
    }

    private void UpdateButtons()
    {
        bool dirty = IsDirty;
        SaveBtn.IsEnabled = dirty;
        RevertBtn.IsEnabled = dirty;
    }

    private void Clear()
    {
        foreach (var row in AllRows) row.PropertyChanged -= Row_Changed;
        Summary.Clear();
        Project.Clear();
        Custom.Clear();
        _deleted.Clear();
        UpdateButtons();
    }

    private void Row_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPropRow.IsDirty)) UpdateButtons();
    }

    /// <summary>Shows the properties of <paramref name="path"/> (or a hint if it is not an Inventor file).</summary>
    public async Task ShowFileAsync(string? path)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;

        // Unsaved edits: offer to save them before switching file.
        if (_path is not null && IsDirty &&
            MessageBox.Show(Window.GetWindow(this)!, Loc.Get("IProps_SaveChanges", Path.GetFileName(_path)), "ScoutR",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await SaveAsync();

        _path = path;
        int token = ++_token;
        Clear();

        if (path is null || !InventorProps.IsInventorFile(path))
        {
            ShowMessage(Loc.Get("IProps_SelectFile"));
            return;
        }

        FileText.Text = Path.GetFileName(path);
        InfoText.Text = Loc.Get("IProps_Loading");
        Thumb.Source = ShellInfo.Thumbnail(path, 256);
        try
        {
            var props = await InventorProps.ReadAsync(path);
            if (token != _token) return; // the selection moved on meanwhile

            _props = props;
            FillAll();

            bool readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            InfoText.Text = readOnly ? Loc.Get("IProps_ReadOnlyFile") : "";
            Body.Visibility = Visibility.Visible;
            _ = Dispatcher.InvokeAsync(FitThumbnail, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            if (token == _token) InfoText.Text = Loc.Get("Msg_Error", ex.Message);
        }
    }

    /// <summary>Thumbnail top level with the first field and as tall as about 6 fields.</summary>
    private void FitThumbnail()
    {
        // Custom is as wide as the other groups plus its delete buttons, even when it is empty.
        CustomCol.MinWidth = Math.Max(SummaryCol.ActualWidth, ProjectCol.ActualWidth) + 26;

        var list = SummaryItems.Items.Count > 0 ? SummaryItems : ProjectItems;
        if (list.Items.Count == 0 ||
            list.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement row ||
            row.ActualHeight <= 0) return;
        double top = row.TranslatePoint(new Point(0, 0), Body).Y + 2; // the row's own top margin
        double size = Math.Round(6 * row.ActualHeight - 4);
        ThumbBox.Margin = new Thickness(0, Math.Max(0, top), 0, 0);
        ThumbBox.Width = ThumbBox.Height = size;
    }

    private void FillAll()
    {
        Clear();
        Fill(Summary, _props.Where(p => p.Set is InventorProps.SummarySet or InventorProps.DocSummarySet
                                        && !Hidden.Contains(p.Name)), InventorProps.SummaryNames);
        Fill(Project, _props.Where(p => p.Set == InventorProps.ProjectSet && !Hidden.Contains(p.Name)),
            InventorProps.ProjectNames);
        Fill(Custom, _props.Where(p => p.Set == InventorProps.CustomSet).OrderBy(p => p.Label), null);
        UpdateNoCustom();
        _ = Dispatcher.InvokeAsync(FitThumbnail, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateNoCustom() =>
        NoCustom.Visibility = Custom.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Fill(ObservableCollection<IPropRow> into, IEnumerable<IPropValue> props, string[]? order)
    {
        var list = props.ToList();
        if (order is not null)
            list = list.OrderBy(p => Array.IndexOf(order, p.Name) is var i && i < 0 ? int.MaxValue : i).ToList();
        foreach (var p in list)
        {
            var row = new IPropRow(p);
            row.PropertyChanged += Row_Changed;
            into.Add(row);
        }
    }

    private async Task SaveAsync()
    {
        if (_path is null) return;
        var dirty = AllRows.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0 && _deleted.Count == 0) return;

        try
        {
            var changes = _deleted
                .Select(r => new IPropChange(r.Source.Set, r.Source.Name, null, IPropChangeKind.Delete))
                .Concat(dirty.Select(r => new IPropChange(r.Source.Set, r.Source.Name,
                    r.IsNew ? r.Value : InventorProps.Parse(r.Value, r.Source.Value),
                    r.IsNew ? IPropChangeKind.Add : IPropChangeKind.Update)))
                .ToList();
            SaveBtn.IsEnabled = false;
            InfoText.Text = Loc.Get("IProps_Saving");
            await InventorProps.WriteAsync(_path, changes);
            foreach (var r in dirty) r.Accept();
            _deleted.Clear();
            InfoText.Text = Loc.Get("IProps_Saved");
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            InfoText.Text = Loc.Get("Msg_Error", ex.Message);
        }
        UpdateButtons();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        FillAll(); // drops added rows, brings back deleted ones, restores values
        InfoText.Text = "";
    }

    /// <summary>"+ Add": new custom property; written to the file on Save.</summary>
    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null) return;
        string? name = InputDialog.Ask(Window.GetWindow(this)!, Loc.Get("IProps_AddTitle"), Loc.Get("IProps_AddLabel"),
            "", selectStemOnly: false);
        if (name is null) return;
        if (Custom.Any(r => string.Equals(r.Source.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            InfoText.Text = Loc.Get("IProps_Exists", name);
            return;
        }
        var row = new IPropRow(new IPropValue(InventorProps.CustomSet, name, name, "", false), isNew: true);
        row.PropertyChanged += Row_Changed;
        Custom.Add(row);
        UpdateNoCustom();
        UpdateButtons();
    }

    /// <summary>✕ on a custom property: removed from the file on Save.</summary>
    private void DeleteCustom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IPropRow row }) return;
        row.PropertyChanged -= Row_Changed;
        Custom.Remove(row);
        if (!row.IsNew) _deleted.Add(row);
        UpdateNoCustom();
        UpdateButtons();
    }

    /// <summary>Menu with a tick for every standard field: untick to hide it.</summary>
    private void Fields_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FieldsBtn };
        void AddGroup(string title, IEnumerable<string> names)
        {
            menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (string name in names)
            {
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = !Hidden.Contains(name),
                                          StaysOpenOnClick = true };
                item.Click += (_, _) =>
                {
                    if (item.IsChecked) Hidden.Remove(name); else Hidden.Add(name);
                    HiddenChanged?.Invoke(this, EventArgs.Empty);
                    if (!IsDirty && _props.Count > 0) FillAll();
                };
                menu.Items.Add(item);
            }
        }
        AddGroup(Loc.Get("IProps_Summary"), InventorProps.SummaryNames);
        menu.Items.Add(new Separator());
        AddGroup(Loc.Get("IProps_Project"), InventorProps.ProjectNames);
        menu.IsOpen = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
