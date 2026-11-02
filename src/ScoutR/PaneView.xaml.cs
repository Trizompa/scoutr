using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScoutR;

public partial class PaneView : UserControl
{
    public PaneView()
    {
        InitializeComponent();
        PreviewMouseDown += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        GotKeyboardFocus += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the selection in this pane changes.</summary>
    public event EventHandler? SelectedItemsChanged;

    /// <summary>Raised when the user clicks or focuses this pane.</summary>
    public event EventHandler? Activated;

    private PaneViewModel? Vm => DataContext as PaneViewModel;

    public IReadOnlyList<FsItem> SelectedItems => ItemList.SelectedItems.Cast<FsItem>().ToList();

    public void FocusList()
    {
        if (ItemList.SelectedItem is null && ItemList.Items.Count > 0) ItemList.SelectedIndex = 0;
        ItemList.UpdateLayout();
        if (ItemList.SelectedItem is not null &&
            ItemList.ItemContainerGenerator.ContainerFromItem(ItemList.SelectedItem) is ListViewItem lvi)
            lvi.Focus();
        else
            ItemList.Focus();
    }

    /// <summary>Selects the items whose full path is in <paramref name="paths"/>.</summary>
    public void SelectPaths(IEnumerable<string> paths)
    {
        if (Vm is null) return;
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        ItemList.SelectedItems.Clear();
        FsItem? first = null;
        foreach (var item in Vm.Items)
        {
            if (!set.Contains(item.FullPath.TrimEnd('\\', '/'))) continue;
            ItemList.SelectedItems.Add(item);
            first ??= item;
        }
        if (first is not null) ItemList.ScrollIntoView(first);
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: FsItem item }) Vm?.Open(item);
    }

    private void ItemList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedItems.Count == 1)
        {
            Vm?.Open(SelectedItems[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            Vm?.GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Vm?.GoBack();
            e.Handled = true;
        }
    }

    /// <summary>Right-click on empty space clears the selection, so the menu acts on the folder itself.</summary>
    private void ItemList_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnEmptySpace(e)) ItemList.SelectedItems.Clear();
    }

    /// <summary>Left click on empty space clears the selection, like Explorer.</summary>
    private void ItemList_LeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedOnSelected = null;
        if (Keyboard.Modifiers == ModifierKeys.None && IsOnEmptySpace(e))
        {
            ItemList.SelectedItems.Clear();
            ItemList.Focus();
            return;
        }

        var row = RowAt(e.OriginalSource);
        if (row is null || e.ClickCount > 1) return;
        _dragStart = e.GetPosition(ItemList);

        // Pressing on an already selected row keeps the whole selection, so several items can be dragged.
        // A plain click (no drag) then selects just that row, on mouse up.
        if (row.IsSelected && Keyboard.Modifiers == ModifierKeys.None && ItemList.SelectedItems.Count > 1)
        {
            _pressedOnSelected = row;
            row.Focus();
            e.Handled = true;
        }
    }

    // ---- Sorting ----

    private readonly Dictionary<GridViewColumn, string> _headerText = new();

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || Vm is null) return;
        if (ItemList.View is not GridView grid) return;

        foreach (var c in grid.Columns)
            if (!_headerText.ContainsKey(c)) _headerText[c] = c.Header?.ToString() ?? "";

        int index = grid.Columns.IndexOf(column);
        var key = (PaneViewModel.SortKey)Math.Clamp(index, 0, 3); // Name, Date modified, Type, Size
        Vm.SetSort(key);

        // Small arrow on the sorted column, like Explorer.
        foreach (var c in grid.Columns)
            c.Header = c == column ? _headerText[c] + (Vm.SortDescending ? "  \u2193" : "  \u2191") : _headerText[c];
    }

    // ---- Drag & drop ----

    private Point? _dragStart;
    private ListViewItem? _pressedOnSelected;

    /// <summary>Files dropped on this pane from inside ScoutR or from Explorer: (paths, target folder, move).</summary>
    public event Action<PaneView, string[], string, bool>? FilesDropped;

    /// <summary>Raised after a drag that started here ends (the files may have moved).</summary>
    public event EventHandler? DragFinished;

    private ListViewItem? RowAt(object source)
    {
        var hit = source as DependencyObject;
        while (hit is not null && hit != ItemList)
        {
            if (hit is ListViewItem lvi) return lvi;
            hit = hit is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(hit)
                : LogicalTreeHelper.GetParent(hit);
        }
        return null;
    }

    private void ItemList_LeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedOnSelected is { } row)
        {
            ItemList.SelectedItems.Clear();
            row.IsSelected = true;
        }
        _pressedOnSelected = null;
        _dragStart = null;
    }

    private void ItemList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(ItemList) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStart = null;
        _pressedOnSelected = null;
        var paths = SelectedItems.Where(i => !i.IsDrive).Select(i => i.FullPath).ToArray();
        if (paths.Length == 0) return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(ItemList, data, DragDropEffects.Copy | DragDropEffects.Move);
        DragFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folder the drop goes to: the folder row under the mouse, else this pane's folder.</summary>
    private string? DropTarget(DragEventArgs e, string[] sources)
    {
        if (RowAt(e.OriginalSource) is { DataContext: FsItem { IsDirectory: true } item } &&
            !sources.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase))
            return item.FullPath;
        string here = Vm?.CurrentPath ?? "";
        return here.Length == 0 ? null : here;
    }

    /// <summary>Explorer rules: Ctrl = copy, Shift = move, otherwise move on the same drive and copy across drives.</summary>
    private static DragDropEffects Effect(DragEventArgs e, string[] sources, string target)
    {
        string root = System.IO.Path.GetPathRoot(target) ?? "";
        bool sameDrive = sources.All(p =>
            string.Equals(System.IO.Path.GetPathRoot(p), root, StringComparison.OrdinalIgnoreCase));

        // Dropping items back into the folder they are already in does nothing (unless Ctrl asks for a copy).
        bool sameFolder = sources.All(p => string.Equals(System.IO.Path.GetDirectoryName(p.TrimEnd('\\')),
            target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        DragDropEffects want =
            (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy :
            (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move :
            sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        if (sameFolder && want == DragDropEffects.Move) return DragDropEffects.None;
        return (e.AllowedEffects & want) != 0 ? want : e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move);
    }

    private static string[]? Sources(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void ItemList_DragOver(object sender, DragEventArgs e)
    {
        var sources = Sources(e);
        var target = sources is null ? null : DropTarget(e, sources);
        e.Effects = sources is null || target is null ? DragDropEffects.None : Effect(e, sources, target);
        e.Handled = true;
    }

    private void ItemList_Drop(object sender, DragEventArgs e)
    {
        var sources = Sources(e);
        var target = sources is null ? null : DropTarget(e, sources);
        if (sources is null || target is null) return;

        var effect = Effect(e, sources, target);
        e.Effects = effect;
        e.Handled = true;
        if (effect == DragDropEffects.None) return;
        Activated?.Invoke(this, EventArgs.Empty);
        FilesDropped?.Invoke(this, sources, target, effect == DragDropEffects.Move);
    }

    /// <summary>True when the click is not on a row, a column header or a scrollbar.</summary>
    private bool IsOnEmptySpace(MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit != ItemList)
        {
            if (hit is ListViewItem or GridViewColumnHeader or System.Windows.Controls.Primitives.ScrollBar)
                return false;
            hit = hit is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(hit)
                : LogicalTreeHelper.GetParent(hit);
        }
        return true;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = ItemList.SelectedItems.Count;
        SelText.Text = n > 0 ? Loc.Get("Status_Selected", n) : "";
        SelectedItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginEditAddress()
    {
        CrumbBar.Visibility = Visibility.Hidden;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = Vm?.DisplayPath ?? "";
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndEditAddress()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbBar.Visibility = Visibility.Visible;
    }

    /// <summary>Click on the empty part of the address bar: type a path, like Explorer.</summary>
    private void AddressBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (AddressBox.Visibility != Visibility.Visible) BeginEditAddress();
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target }) Vm?.Navigate(target);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string text = AddressBox.Text;
            EndEditAddress();
            Vm?.NavigateFromText(text);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEditAddress();
            FocusList();
            e.Handled = true;
        }
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEditAddress();

    private void Back_Click(object sender, RoutedEventArgs e) => Vm?.GoBack();

    private void Up_Click(object sender, RoutedEventArgs e) => Vm?.GoUp();
}
