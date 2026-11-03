using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace ScoutR;

/// <summary>One BOM line as shown: the row plus an indexer the grid columns bind to.</summary>
public sealed class BomLine
{
    public BomLine(BomRow row) => Row = row;
    public BomRow Row { get; }
    public Thickness Indent => new(Row.Level * 16, 0, 0, 0);
    public string this[string key] => BomWindow.Text(Row, key);
}

/// <summary>Structured BOM of an assembly, with the columns the user picks, exported to Excel.</summary>
public partial class BomWindow : Window
{
    public static readonly string[] DefaultColumns =
        { "#Type", "Part Number", "Revision Number", "#Item", "#Qty", "#TotalQty", "#####_QTY", "Description", "Reference",
          "Material", "Thickness", "Laser", "Bend", "Roll", "#Structure", "Vendor", "#Path" };

    private readonly string _top;
    private readonly AppSettings _settings;
    private readonly Dictionary<GridViewColumn, string> _keys = new();
    private List<BomRow> _rows = new();
    private List<string> _fields = new();

    public BomWindow(Window owner, string top, AppSettings settings)
    {
        InitializeComponent();
        Owner = owner;
        _top = top;
        _settings = settings;
        FileText.Text = top;
        Title = Loc.Get("Bom_Title") + " – " + Path.GetFileName(top);
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) => SaveColumns();
    }

    public static string Text(BomRow row, string key) => key switch
    {
        "#Item" => row.Item,
        "#Qty" => row.Qty.ToString(),
        "#TotalQty" => row.TotalQty.ToString(),
        "#Structure" => row.Structure,
        "#Path" => row.Path,
        "#Type" => row.Path.Length == 0 ? "Virtual"
            : Path.GetExtension(row.Path).Equals(".iam", StringComparison.OrdinalIgnoreCase) ? "Assembly" : "Part",
        _ => row.Props.TryGetValue(key, out string? v) ? v : "",
    };

    private static string Label(string key) => key switch
    {
        "#Item" => Loc.Get("Bom_Item"),
        "#File" => Loc.Get("Bom_File"),
        "#Structure" => "BOM Structure",
        "#Type" => "Component Type",
        "#Path" => "File Path",
        "#Qty" => "Item QTY",
        "#TotalQty" => "Total QTY",
        "#Appearance" => "Appearance",
        "Revision Number" => "REV",
        _ => key,
    };

    private async Task LoadAsync()
    {
        StatusText.Text = Loc.Get("IProps_Loading");
        try
        {
            (_rows, _fields) = await InventorProps.ReadBomAsync(_top,
                new Progress<string>(f => StatusText.Text = Loc.Get("IProps_Loading") + " " + f));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Get("Msg_Error", ex.Message);
            return;
        }
        var keys = _settings.BomColumnSet.Count > 0 ? _settings.BomColumnSet : DefaultColumns.ToList();
        foreach (string k in keys) AddColumn(k);
        FlatBox.IsChecked = _settings.BomFlat;
        FlatBox.IsEnabled = FieldsBtn.IsEnabled = ExportBtn.IsEnabled = true;
        ShowRows();
    }

    /// <summary>Rows on screen: the structure, or each file once with its total quantity.</summary>
    private List<BomRow> Current() => FlatBox.IsChecked == true ? Flat(_rows) : _rows;

    private static List<BomRow> Flat(List<BomRow> rows)
    {
        var order = new List<string>();
        var sum = new Dictionary<string, BomRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            string key = r.Path.Length > 0 ? r.Path : "virtual|" + Text(r, "Part Number");
            if (sum.TryGetValue(key, out var known))
                sum[key] = known with { TotalQty = known.TotalQty + r.TotalQty };
            else
            {
                order.Add(key);
                sum[key] = r with { Level = 0 };
            }
        }
        return order.Select((k, i) => sum[k] with { Item = (i + 1).ToString(), Qty = sum[k].TotalQty }).ToList();
    }

    private void ShowRows()
    {
        var rows = Current();
        BomList.ItemsSource = rows.Select(r => new BomLine(r)).ToList();
        StatusText.Text = Loc.Get(FlatBox.IsChecked == true ? "Bom_CountFlat" : "Bom_Count", rows.Count);
    }

    private void Flat_Click(object sender, RoutedEventArgs e)
    {
        _settings.BomFlat = FlatBox.IsChecked == true;
        _settings.Save();
        ShowRows();
    }

    private void AddColumn(string key)
    {
        var col = new GridViewColumn { Header = Label(key), Width = key switch { "#Item" => 90, "#Qty" or "#TotalQty" or "Revision Number" => 70, "Description" => 260, "Part Number" => 180, "#Path" => 400, "#Type" => 100, _ => 90 } };
        if (key == "#Item")
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding("[#Item]"));
            tb.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(BomLine.Indent)));
            col.CellTemplate = new DataTemplate { VisualTree = tb };
        }
        else
        {
            col.DisplayMemberBinding = new Binding($"[{key}]");
        }
        _keys[col] = key;
        BomGrid.Columns.Add(col);
    }

    private List<string> Shown() => BomGrid.Columns.Select(c => _keys[c]).ToList();

    private void SaveColumns()
    {
        if (BomGrid.Columns.Count == 0) return;
        _settings.BomColumnSet = Shown();
        _settings.Save();
    }

    /// <summary>Menu with a tick per field: Item, Qty, File, then every iProperty found in the BOM.</summary>
    private void Fields_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FieldsBtn };
        var all = new List<string> { "#Type", "#Item", "#Qty", "#TotalQty", "#Structure", "#File", "#Path", "#Appearance" };
        all.AddRange(InventorProps.ProjectNames.Where(n => _fields.Contains(n, StringComparer.OrdinalIgnoreCase)));
        all.AddRange(InventorProps.SummaryNames.Where(n => _fields.Contains(n, StringComparer.OrdinalIgnoreCase)));
        all.AddRange(_fields.Where(f => !all.Contains(f, StringComparer.OrdinalIgnoreCase)).OrderBy(f => f));
        foreach (string key in all)
        {
            var item = new MenuItem { Header = Label(key), IsCheckable = true, StaysOpenOnClick = true,
                                      IsChecked = _keys.ContainsValue(key) };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) AddColumn(key);
                else if (_keys.FirstOrDefault(p => p.Value == key).Key is { } col)
                {
                    BomGrid.Columns.Remove(col);
                    _keys.Remove(col);
                }
                SaveColumns();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = Path.GetFileNameWithoutExtension(_top) + (FlatBox.IsChecked == true ? " BOM totals.xlsx" : " BOM.xlsx"),
            InitialDirectory = Path.GetDirectoryName(_top),
        };
        if (dlg.ShowDialog(this) != true) return;

        var keys = Shown();
        var rows = Current().Select(r => (IReadOnlyList<XlsxWriter.Cell>)keys.Select(k => k switch
        {
            "#Item" => new XlsxWriter.Cell(r.Item, r.Level),
            "#Qty" => new XlsxWriter.Cell(r.Qty),
            "#TotalQty" => new XlsxWriter.Cell(r.TotalQty),
            _ => new XlsxWriter.Cell(Text(r, k)),
        }).ToList()).ToList();
        var widths = BomGrid.Columns.Select(c => Math.Max(6, c.ActualWidth / 7)).ToList();
        try
        {
            XlsxWriter.Write(dlg.FileName, Path.GetFileNameWithoutExtension(_top), keys.Select(Label).ToList(), rows, widths);
            StatusText.Text = Loc.Get("Bom_Exported", Path.GetFileName(dlg.FileName));
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Get("Msg_Error", ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
