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
        { "#Type", "Part Number", "Revision Number", "#Item", "#Qty", "#TotalQty", "Description", "Reference",
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
        "#Qty" => row.Qty == 0 ? "" : row.Qty.ToString(), // flat view: no parent, only the total
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
        _columns = (_settings.BomColumnSet.Count > 0 ? _settings.BomColumnSet.ToList() : DefaultColumns.ToList())
            .Where(k => k != "#####_QTY").ToList();
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
        return order.Select((k, i) => sum[k] with { Item = (i + 1).ToString(), Qty = 0 }).ToList();
    }

    // ---- Sorting: click a column header (again = reverse). Sub-items stay under their assembly. ----

    private string? _sortKey;
    private bool _sortDesc;

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col } || !_keys.TryGetValue(col, out string? key)) return;
        if (key == _sortKey) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = false; }
        foreach (var (c, k) in _keys) c.Header = Label(k) + (k == _sortKey ? (_sortDesc ? " ▼" : " ▲") : "");
        ShowRows();
    }

    private List<BomRow> Sorted(List<BomRow> rows)
    {
        if (_sortKey is null) return rows;
        var result = new List<BomRow>(rows.Count);
        SortBlock(rows, 0, rows.Count, result);
        return result;
    }

    // rows[from..to) is a run of siblings, each followed by its own sub-items (deeper level).
    private void SortBlock(List<BomRow> rows, int from, int to, List<BomRow> result)
    {
        var blocks = new List<(int Start, int End)>();
        for (int i = from; i < to;)
        {
            int j = i + 1;
            while (j < to && rows[j].Level > rows[i].Level) j++;
            blocks.Add((i, j));
            i = j;
        }
        var ordered = blocks.OrderBy(b => rows[b.Start], Comparer<BomRow>.Create(Compare)).ToList();
        if (_sortDesc) ordered.Reverse();
        foreach (var (start, end) in ordered)
        {
            result.Add(rows[start]);
            SortBlock(rows, start + 1, end, result);
        }
    }

    private int Compare(BomRow a, BomRow b)
    {
        string key = _sortKey!;
        if (key == "#Item") return CompareItems(a.Item, b.Item);
        if (key is "#Qty") return a.Qty.CompareTo(b.Qty);
        if (key is "#TotalQty") return a.TotalQty.CompareTo(b.TotalQty);
        // Same order as Explorer: digits compared as numbers ("9" < "10", "12x145" < "49259").
        return StrCmpLogicalW(Text(a, key), Text(b, key));
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    private static int CompareItems(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = int.TryParse(x[i], out int p) && int.TryParse(y[i], out int q) ? p.CompareTo(q)
                : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    // Columns that mean nothing when each file is listed once with its total.
    private static readonly HashSet<string> FlatHidden = new(StringComparer.OrdinalIgnoreCase)
        { "#Type", "#Qty", "#####_QTY", "#Structure", "Vendor", "#Path" };

    /// <summary>Columns chosen for the structured view; the flat view shows them minus <see cref="FlatHidden"/>.</summary>
    private List<string> _columns = new();
    private bool? _builtFlat;

    private void BuildColumns()
    {
        bool flat = FlatBox.IsChecked == true;
        if (_builtFlat == flat) return;
        _builtFlat = flat;
        BomGrid.Columns.Clear();
        _keys.Clear();
        foreach (string k in _columns.Where(k => !flat || !FlatHidden.Contains(k))) AddColumn(k);
    }

    private void ShowRows()
    {
        BuildColumns();
        var rows = Sorted(Current());
        BomList.ItemsSource = rows.Select(r => new BomLine(r)).ToList();
        FitColumns();
        StatusText.Text = Loc.Get(FlatBox.IsChecked == true ? "Bom_CountFlat" : "Bom_Count", rows.Count);
    }

    /// <summary>
    /// Auto-fit every column to its header and all rows (measured as text, so rows not on screen count too).
    /// </summary>
    private void FitColumns()
    {
        if (BomList.ItemsSource is not List<BomLine> lines) return;
        var face = new System.Windows.Media.Typeface(BomList.FontFamily, BomList.FontStyle, BomList.FontWeight, BomList.FontStretch);
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Measure(string text, bool bold = false) => text.Length == 0 ? 0 :
            new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                bold ? new System.Windows.Media.Typeface(face.FontFamily, face.Style, FontWeights.SemiBold, face.Stretch) : face,
                BomList.FontSize, System.Windows.Media.Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        foreach (var col in BomGrid.Columns)
        {
            string key = _keys[col];
            double w = Measure(Label(key) + " ▼", bold: true);
            foreach (var line in lines)
                w = Math.Max(w, Measure(line[key]) + (key == "#Item" ? line.Row.Level * 16 : 0));
            col.Width = Math.Min(600, Math.Ceiling(w) + 24);
        }
    }

    private void Flat_Click(object sender, RoutedEventArgs e)
    {
        SaveColumns(); // remember the current view's columns before switching
        _settings.BomFlat = FlatBox.IsChecked == true;
        _settings.Save();
        ShowRows();
    }

    private void AddColumn(string key)
    {
        var col = new GridViewColumn { Header = Label(key), Width = double.NaN }; // auto-fit to the content
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
        var shown = Shown();
        if (_builtFlat == true)
        {
            // Flat view: keep the hidden structured-only columns where they were, take the rest as shown.
            var merged = new List<string>();
            int next = 0;
            foreach (string k in _columns)
            {
                if (FlatHidden.Contains(k)) merged.Add(k);
                else if (shown.Contains(k)) { while (next < shown.Count && merged.Contains(shown[next])) next++; if (next < shown.Count) merged.Add(shown[next++]); }
            }
            merged.AddRange(shown.Where(k => !merged.Contains(k)));
            _columns = merged;
        }
        else
        {
            _columns = shown;
        }
        _settings.BomColumnSet = _columns.ToList();
        _settings.Save();
    }

    /// <summary>Menu with a tick per field: Item, Qty, File, then every iProperty found in the BOM.</summary>
    private void Fields_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FieldsBtn, MaxHeight = Math.Max(300, ActualHeight - 80) };
        var all = new List<string> { "#Type", "#Item", "#Qty", "#TotalQty", "#Structure", "#File", "#Path", "#Appearance" };
        all.AddRange(InventorProps.ProjectNames.Where(n => _fields.Contains(n, StringComparer.OrdinalIgnoreCase)));
        all.AddRange(InventorProps.SummaryNames.Where(n => _fields.Contains(n, StringComparer.OrdinalIgnoreCase)));
        // Custom properties: long names are leftovers (add-ins, imports), not real fields.
        all.AddRange(_fields.Where(f => f.Length <= 25 && !all.Contains(f, StringComparer.OrdinalIgnoreCase)).OrderBy(f => f));
        foreach (string key in all)
        {
            var item = new MenuItem { Header = Label(key), IsCheckable = true, StaysOpenOnClick = true,
                                      IsChecked = _keys.ContainsValue(key) };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) { AddColumn(key); FitColumns(); }
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
        var rows = Sorted(Current()).Select(r => (IReadOnlyList<XlsxWriter.Cell>)keys.Select(k => k switch
        {
            "#Item" => new XlsxWriter.Cell(r.Item, r.Level),
            "#Qty" => new XlsxWriter.Cell(r.Qty == 0 ? null : r.Qty),
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
