using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ScoutR;

/// <summary>One editable row in the iProperties panel.</summary>
public sealed class IPropRow : ObservableObject
{
    private string _value;

    public IPropRow(IPropValue source)
    {
        Source = source;
        Original = InventorProps.Format(source.Value);
        _value = Original;
    }

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

    public bool IsDirty => !ReadOnly && _value != Original;

    public void Revert() => Value = Original;

    public void Accept()
    {
        Original = _value;
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

    public bool IsDirty => AllRows.Any(r => r.IsDirty);

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
        try
        {
            var props = await InventorProps.ReadAsync(path);
            if (token != _token) return; // the selection moved on meanwhile

            Fill(Summary, props.Where(p => p.Set is InventorProps.SummarySet or InventorProps.DocSummarySet),
                InventorProps.SummaryNames);
            Fill(Project, props.Where(p => p.Set == InventorProps.ProjectSet), InventorProps.ProjectNames);
            Fill(Custom, props.Where(p => p.Set == InventorProps.CustomSet).OrderBy(p => p.Label), null);
            NoCustom.Visibility = Custom.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            InfoText.Text = readOnly ? Loc.Get("IProps_ReadOnlyFile") : "";
            Body.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (token == _token) InfoText.Text = Loc.Get("Msg_Error", ex.Message);
        }
    }

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
        if (dirty.Count == 0) return;

        try
        {
            var changes = dirty
                .Select(r => (r.Source.Set, r.Source.Name, InventorProps.Parse(r.Value, r.Source.Value)))
                .ToList();
            SaveBtn.IsEnabled = false;
            InfoText.Text = Loc.Get("IProps_Saving");
            await InventorProps.WriteAsync(_path, changes);
            foreach (var r in dirty) r.Accept();
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
        foreach (var r in AllRows) r.Revert();
        InfoText.Text = "";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
