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

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = ItemList.SelectedItems.Count;
        SelText.Text = n > 0 ? Loc.Get("Status_Selected", n) : "";
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm?.NavigateFromText(AddressBox.Text);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddressBox.Text = Vm?.DisplayPath ?? "";
            FocusList();
            e.Handled = true;
        }
    }

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    private void Back_Click(object sender, RoutedEventArgs e) => Vm?.GoBack();

    private void Up_Click(object sender, RoutedEventArgs e) => Vm?.GoUp();
}
