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
        if (Keyboard.Modifiers == ModifierKeys.None && IsOnEmptySpace(e))
        {
            ItemList.SelectedItems.Clear();
            ItemList.Focus();
        }
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
