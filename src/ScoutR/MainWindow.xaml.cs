using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScoutR;

public partial class MainWindow : Window
{
    private readonly PaneViewModel _a;
    private readonly PaneViewModel _b;
    private readonly TreeModel _tree = new();
    private readonly Updater _updater = new();
    private PaneViewModel _active;
    private bool _busy;
    private (List<string> Paths, bool Cut)? _clipboard;

    private readonly AppSettings _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        if (_settings.TreeWidth > 60) TreeColumn.Width = new GridLength(_settings.TreeWidth);
        ShowPreview(_settings.ShowPreview);
        Closing += (_, _) => SaveSettings();
        // Settings are saved: end the process at once. Unloading Inventor Apprentice can hang, and a
        // ScoutR left running in the background would block the next update.
        Closed += (_, _) => System.Diagnostics.Process.GetCurrentProcess().Kill();
        if (AppIcon.Load() is { } icon) Icon = icon; // Explorer icon, recoloured green

        // Always start maximized (also after an update restart).
        WindowState = WindowState.Maximized;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string downloads = Path.Combine(profile, "Downloads");

        // Last folders from the previous run, when they still exist.
        static string Pick(string? saved, string fallback) =>
            !string.IsNullOrEmpty(saved) && Directory.Exists(saved) ? saved : fallback;
        _a = new PaneViewModel(Pick(_settings.A.Folder, docs));
        _b = new PaneViewModel(Pick(_settings.B.Folder, Directory.Exists(downloads) ? downloads : profile));
        _active = _a;
        _a.IsActive = true;

        ViewA.DataContext = _a;
        ViewB.DataContext = _b;
        Tree.ItemsSource = _tree.Roots;
        RefreshPins();

        foreach (var pane in new[] { _a, _b })
        {
            pane.Navigated += _ => SyncTree();
            pane.Error += (_, msg) => Status(Loc.Get("Msg_Error", msg));
        }

        ViewA.SelectedItemsChanged += (_, _) => { UpdateCommands(); UpdateProps(); UpdatePreview(); };
        ViewB.SelectedItemsChanged += (_, _) => { UpdateCommands(); UpdateProps(); UpdatePreview(); };
        PropsPanel.CloseRequested += (_, _) => ShowProps(false);
        PropsPanel.SizeChanged += (_, _) => FitPreviewHeight();
        SizeChanged += (_, _) => PropsPanel.MaxHeight = Math.Max(150, ActualHeight * 0.6);
        PropsPanel.Hidden = new HashSet<string>(_settings.HiddenIProps, StringComparer.OrdinalIgnoreCase);
        PropsPanel.HiddenChanged += (_, _) =>
        {
            _settings.HiddenIProps = PropsPanel.Hidden.ToList();
            _settings.Save();
        };
        PropsPanel.Saved += (_, _) => ReloadKeepingSelection();
        ViewA.Activated += (_, _) => SetActive(_a);
        ViewB.Activated += (_, _) => SetActive(_b);

        foreach (var view in new[] { ViewA, ViewB })
        {
            view.FilesDropped += (pane, paths, dest, move) =>
                _ = RunAsync(() => TransferAll(paths, dest, move), move ? "Msg_Moved" : "Msg_Copied");
            view.DragFinished += (_, _) => { _a.Reload(); _b.Reload(); };
            var menu = new ContextMenu();
            view.ItemList.ContextMenu = menu;
            view.ItemList.ContextMenuOpening += (_, e) =>
            {
                // The column headers have their own menu (choose columns).
                for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : null)
                    if (d is System.Windows.Controls.GridViewColumnHeader) return;
                BuildMenu(menu);
            };
        }

        // Clicking a folder in the tree navigates the active pane.
        _tree.UserNavigated += path => _active.Navigate(path);
    }

    private PaneViewModel Other => _active == _a ? _b : _a;
    private PaneView ActiveView => _active == _a ? ViewA : ViewB;
    private PaneView OtherView => _active == _a ? ViewB : ViewA;

    private void SaveSettings()
    {
        ViewA.SaveLayout(_settings.A);
        ViewB.SaveLayout(_settings.B);
        _settings.TreeWidth = TreeColumn.ActualWidth;
        _settings.ShowPreview = Preview.Visibility == Visibility.Visible;
        if (_settings.ShowPreview && !PreviewColumn.Width.IsStar) _settings.PreviewWidth = PreviewColumn.ActualWidth;
        _settings.Save();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ViewA.ApplyLayout(_settings.A);
        ViewB.ApplyLayout(_settings.B);
        SyncTree();
        Status(Loc.Get("Msg_Ready"));
        UpdateCommands();
        VersionText.Text = "v" + AppInfo.Version;
        ViewA.FocusList();

        await CheckForUpdatesAsync(manual: false);
    }

    // ---- Updates ----

    private string? _availableVersion;
    private bool _checkingUpdates;

    private void ShowUpdateButton()
    {
        UpdateBtn.IsEnabled = true;
        UpdateBtn.Tag = _availableVersion is null ? null : "available";
        UpdateBtnLabel.Text = _availableVersion is null
            ? Loc.Get("Update_Check")
            : Loc.Get("Update_Install", _availableVersion);
    }

    /// <summary>Looks for a newer release. Silent at startup, reports the result when the user clicked.</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        UpdateBtn.IsEnabled = false;
        UpdateBtnLabel.Text = Loc.Get("Update_Checking");

        try
        {
            if (!_updater.IsInstalled)
            {
                if (manual) Status(Loc.Get("Update_NotInstalled"));
                return;
            }

            _availableVersion = await _updater.CheckAsync();

            if (_availableVersion is not null) Status(Loc.Get("Update_Install", _availableVersion));
            else if (manual)
                Status(_updater.Error is null
                    ? Loc.Get("Update_UpToDate", AppInfo.Version)
                    : Loc.Get("Update_Failed", _updater.Error));
        }
        finally
        {
            _checkingUpdates = false;
            ShowUpdateButton();
        }
    }

    private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_availableVersion is null)
        {
            await CheckForUpdatesAsync(manual: true);
            return;
        }

        UpdateBtn.IsEnabled = false;
        Status(Loc.Get("Update_Downloading"));
        try
        {
            SaveSettings(); // the update restart skips the normal close
            await _updater.ApplyAsync(WindowState == WindowState.Maximized); // restarts the app on success
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
            UpdateBtn.IsEnabled = true;
        }
    }

    // ---- Active pane + tree sync ----

    private void SetActive(PaneViewModel pane)
    {
        if (_active == pane) return;
        _active = pane;
        _a.IsActive = pane == _a;
        _b.IsActive = pane == _b;
        SyncTree();
        UpdateProps();
        UpdatePreview();
    }

    /// <summary>After a dialog: the pane the command started in stays the active one and gets the focus back.</summary>
    private void RestoreActive(PaneViewModel pane, PaneView view)
    {
        SetActive(pane);
        view.FocusList();
    }

    private void SyncTree()
    {
        Title = "ScoutR"; // just the app name (folder and version are shown elsewhere)
        UpdateCommands();
    }

    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item) item.BringIntoView();
        e.Handled = true;
    }

    /// <summary>"Show folder" expands the tree to the active folder; pressed again ("Collapse") it undoes that.</summary>
    private void ShowInTree_Click(object sender, RoutedEventArgs e)
    {
        if (_tree.IsRevealed) _tree.Unreveal();
        else _tree.Reveal(_active.CurrentPath);
        UpdateShowInTree();
    }

    private void UpdateShowInTree()
    {
        bool open = _tree.IsRevealed;
        ShowInTreeBtn.Content = Loc.Get(open ? "Btn_CollapseTree" : "Btn_ShowInTree");
        ShowInTreeBtn.Tag = open ? "\uE70E" : "\uE8A7"; // chevron up / open folder
        ShowInTreeBtn.ToolTip = Loc.Get(open ? "Tip_CollapseTree" : "Tip_ShowInTree");
    }

    // ---- Quick access (pinned folders) ----

    private readonly PinList _quick = new(PinList.Quick);
    private readonly PinList _work = new(PinList.Work);

    private PinList ListFor(string group) => group == PinList.Work ? _work : _quick;

    private void RefreshPins()
    {
        QuickList.ItemsSource = _quick.Links();
        var work = _work.Links();
        WorkList.ItemsSource = work;
        WorkEmpty.Visibility = work.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pin(string group, string path) { ListFor(group).Add(path); RefreshPins(); }
    private void Unpin(string group, string path) { ListFor(group).Remove(path); RefreshPins(); }
    private void MovePin(string group, string path, int delta) { ListFor(group).Move(path, delta); RefreshPins(); }

    /// <summary>Right-click on a pinned folder: open, move up/down, unpin.</summary>
    private void QuickLink_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: QuickLink link } button) return;
        var list = ListFor(link.Group);
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(MenuEntry("Ctx_Open", null, true, () => { _active.Navigate(link.Path); ActiveView.FocusList(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Ctx_MoveUp", null, list.CanMove(link.Path, -1), () => MovePin(link.Group, link.Path, -1)));
        menu.Items.Add(MenuEntry("Ctx_MoveDown", null, list.CanMove(link.Path, +1), () => MovePin(link.Group, link.Path, +1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(link.Group == PinList.Work ? "Ctx_UnpinWork" : "Ctx_Unpin", null, true,
            () => Unpin(link.Group, link.Path)));
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Folders dropped on Quick access / Working folders get pinned there (the panel's Tag says which).</summary>
    private const string PinFormat = "ScoutR.Pin";
    private Point? _pinDragStart;

    /// <summary>Pressing on a link: remember where, so a move can start a drag (to reorder).</summary>
    private void Pin_MouseDown(object sender, MouseButtonEventArgs e) => _pinDragStart = e.GetPosition(this);

    private void Pin_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pinDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
            sender is not Button { Tag: QuickLink link } button) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pinDragStart = null;
        // Only our own format: dropping a link on a pane must never move the real folder.
        DragDrop.DoDragDrop(button, new DataObject(PinFormat, link), DragDropEffects.Move);
    }

    /// <summary>Index of the link under the mouse (drop goes before it), or the end of the list.</summary>
    private static int DropIndex(DragEventArgs e, PinList list)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not Button)
            hit = hit is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(hit) : null;
        if (hit is Button { Tag: QuickLink over } b)
        {
            int i = list.Paths.FindIndex(p => string.Equals(p, over.Path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return e.GetPosition(b).Y > b.ActualHeight / 2 ? i + 1 : i;
        }
        return list.Paths.Count;
    }

    private Border? _dropLine;

    private void HideDropLine()
    {
        if (_dropLine is not null) _dropLine.Visibility = Visibility.Collapsed;
        _dropLine = null;
    }

    /// <summary>Shows the line above or below the link under the mouse, where the drop will land.</summary>
    private void ShowDropLine(DragEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not Button)
            hit = hit is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(hit) : null;

        Border? line = null;
        if (hit is Button b)
        {
            bool below = e.GetPosition(b).Y > b.ActualHeight / 2;
            line = b.Template.FindName(below ? "LineBottom" : "LineTop", b) as Border;
        }
        if (line == _dropLine) return;
        HideDropLine();
        _dropLine = line;
        if (line is not null) line.Visibility = Visibility.Visible;
    }

    private void Pins_DragLeave(object sender, DragEventArgs e) => HideDropLine();

    /// <summary>Folders dropped on Quick access / My Worklist get pinned; links dragged inside get reordered.</summary>
    private void Pins_DragOver(object sender, DragEventArgs e)
    {
        ShowDropLine(e);
        if (e.Data.GetDataPresent(PinFormat)) e.Effects = DragDropEffects.Move;
        else
        {
            bool folders = e.Data.GetData(DataFormats.FileDrop) is string[] p && p.Any(Directory.Exists);
            // Pinning only adds a link; nothing is copied. Use whichever effect the source allows.
            e.Effects = !folders ? DragDropEffects.None
                : (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
                : DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void Pins_Drop(object sender, DragEventArgs e)
    {
        HideDropLine();
        if (sender is not FrameworkElement { Tag: string group }) return;
        var list = ListFor(group);
        int index = DropIndex(e, list);

        if (e.Data.GetData(PinFormat) is QuickLink link)
            list.MoveTo(link.Path, index); // same list: reorder; other list: pin it there too
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (string p in paths.Where(Directory.Exists)) list.MoveTo(p, index++);

        RefreshPins();
        e.Handled = true;
    }

    // ---- iProperties panel ----


    // ---- Preview panel ----

    private void Preview_Click(object sender, RoutedEventArgs e) => ShowPreview(Preview.Visibility != Visibility.Visible);

    private void ShowPreview(bool show)
    {
        if (!show && Preview.Visibility == Visibility.Visible && !PreviewColumn.Width.IsStar)
            _settings.PreviewWidth = PreviewColumn.ActualWidth;
        Preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = show ? new GridLength(_settings.PreviewWidth > 100 ? _settings.PreviewWidth : 320) : new GridLength(0);
        PreviewColumn.MinWidth = show ? 150 : 0;
        BtnPreview.IsChecked = show;
        UpdateBottom();
        if (show) UpdatePreview();
    }

    /// <summary>The strip under the panes holds the preview and/or the iProperties.</summary>
    private void UpdateBottom()
    {
        bool preview = Preview.Visibility == Visibility.Visible, props = PropsPanel.Visibility == Visibility.Visible;
        PropsSplitter.Visibility = preview || props ? Visibility.Visible : Visibility.Collapsed;
        // Height fits the visible fields (up to 60% of the window, then it scrolls); the preview alone keeps its own.
        PropsRow.Height = preview || props ? GridLength.Auto : new GridLength(0);
        PreviewSplitter.Visibility = preview && props ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitColumn.Width = preview && props ? GridLength.Auto : new GridLength(0);
        // Alone the preview takes the whole width; next to the iProperties it keeps its column.
        if (preview && !props) PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        else if (preview && PreviewColumn.Width.IsStar)
            PreviewColumn.Width = new GridLength(_settings.PreviewWidth > 100 ? _settings.PreviewWidth : 320);
        FitPreviewHeight();
    }

    /// <summary>As tall as the iProperties next to it, never less than a usable size.</summary>
    private void FitPreviewHeight() =>
        Preview.Height = PropsPanel.Visibility == Visibility.Visible ? Math.Max(280, PropsPanel.ActualHeight) : 280;

    private void UpdatePreview()
    {
        if (Preview.Visibility != Visibility.Visible) return;
        var sel = ActiveView.SelectedItems;
        Preview.Show(sel.Count == 1 ? sel[0] : null);
    }

    private void IProps_Click(object sender, RoutedEventArgs e) => ShowProps(PropsPanel.Visibility != Visibility.Visible);

    private void ShowProps(bool show)
    {
        PropsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateBottom();
        PropsPanel.MaxHeight = Math.Max(150, ActualHeight * 0.6);
        BtnIProps.IsChecked = show; // ribbon button stays "on" while the panel is open
        if (show) UpdateProps();
    }

    /// <summary>The panel follows the single selected file of the active pane.</summary>
    private bool _holdProps;

    /// <summary>Refreshes both panes (new dates/sizes) without losing the selection or the iProperties panel.</summary>
    private void ReloadKeepingSelection()
    {
        _holdProps = true;
        try
        {
            foreach (var (vm, view) in new[] { (_a, ViewA), (_b, ViewB) })
            {
                var sel = view.SelectedItems.Select(i => i.FullPath.TrimEnd('\\', '/')).ToList();
                vm.Reload();
                view.SelectPaths(sel);
            }
        }
        finally
        {
            _holdProps = false;
        }
    }

    private void UpdateProps()
    {
        if (_holdProps || PropsPanel.Visibility != Visibility.Visible) return;
        var sel = ActiveView.SelectedItems;
        string? path = sel.Count == 1 && !sel[0].IsDirectory ? sel[0].FullPath : null;
        _ = PropsPanel.ShowFileAsync(path);
    }

    private void QuickLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickLink link }) _active.Navigate(link.Path);
        ActiveView.FocusList();
    }

    private void Status(string text) => StatusText.Text = text;

    /// <summary>Greys out ribbon buttons that cannot act on the current selection.</summary>
    private void UpdateCommands()
    {
        int n = ActiveView.SelectedItems.Count(i => !i.IsDrive);
        bool inFolder = _active.CurrentPath.Length > 0;
        bool otherInFolder = Other.CurrentPath.Length > 0;

        BtnCut.IsEnabled = n > 0;
        BtnCopy.IsEnabled = n > 0;
        BtnPaste.IsEnabled = _clipboard is not null && inFolder;
        BtnCopyTo.IsEnabled = n > 0 && otherInFolder;
        BtnMoveTo.IsEnabled = n > 0 && otherInFolder;
        BtnDelete.IsEnabled = n > 0;
        BtnRename.IsEnabled = n == 1;
        BtnNewFolder.IsEnabled = inFolder;
    }

    private void Swap_Click(object sender, RoutedEventArgs e) => OtherView.FocusList();

    // ---- Keyboard ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool inText = e.OriginalSource is TextBox;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.F2: Rename(); e.Handled = true; break;
            case Key.F5: _ = TransferToOtherAsync(move: false); e.Handled = true; break;
            case Key.F6: _ = TransferToOtherAsync(move: true); e.Handled = true; break;
            case Key.F7: NewFolder(); e.Handled = true; break;
            case Key.Delete when !inText: _ = DeleteAsync(); e.Handled = true; break;
            case Key.Tab when !inText && Keyboard.Modifiers == ModifierKeys.None:
                OtherView.FocusList();
                e.Handled = true;
                break;
            case Key.C when ctrl && !inText: PutOnClipboard(cut: false); e.Handled = true; break;
            case Key.X when ctrl && !inText: PutOnClipboard(cut: true); e.Handled = true; break;
            case Key.V when ctrl && !inText: _ = PasteAsync(); e.Handled = true; break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !inText: CopyAsPath(); e.Handled = true; break;
            case Key.Z when ctrl && !inText: _ = UndoAsync(); e.Handled = true; break;
            case Key.System when e.SystemKey == Key.P && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                ShowPreview(Preview.Visibility != Visibility.Visible);
                e.Handled = true;
                break;
            case Key.System when e.SystemKey == Key.P:
                ShowProps(PropsPanel.Visibility != Visibility.Visible);
                e.Handled = true;
                break;
            case Key.System when e.SystemKey == Key.Enter && !inText:
                var sel = ActiveView.SelectedItems;
                string target = sel.Count == 1 ? sel[0].FullPath : _active.CurrentPath;
                if (target.Length > 0) ShellVerb(target, "properties");
                e.Handled = true;
                break;
        }
    }

    // ---- Toolbar handlers ----

    private void Cut_Click(object sender, RoutedEventArgs e) => PutOnClipboard(cut: true);
    private void CopyClip_Click(object sender, RoutedEventArgs e) => PutOnClipboard(cut: false);
    private void Paste_Click(object sender, RoutedEventArgs e) => _ = PasteAsync();
    private void CopyTo_Click(object sender, RoutedEventArgs e) => _ = TransferToOtherAsync(move: false);
    private void MoveTo_Click(object sender, RoutedEventArgs e) => _ = TransferToOtherAsync(move: true);
    private void Delete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync();
    private void Rename_Click(object sender, RoutedEventArgs e) => Rename();
    private void NewFolder_Click(object sender, RoutedEventArgs e) => NewFolder();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _a.Reload();
        _b.Reload();
    }

    // ---- Context menu ----

    private static MenuItem MenuEntry(string key, string? gesture, bool enabled, Action action)
    {
        var item = new MenuItem { Header = Loc.Get(key), InputGestureText = gesture ?? "", IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Right-click menu: acts on the selection, or on the current folder if nothing is selected.</summary>
    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var view = ActiveView;
        var sel = view.SelectedItems;
        int n = sel.Count(i => !i.IsDrive);
        bool inFolder = _active.CurrentPath.Length > 0;
        bool otherInFolder = Other.CurrentPath.Length > 0;

        if (sel.Count > 0)
        {
            var first = sel[0];
            menu.Items.Add(new MenuItem { Header = Loc.Get("Ctx_Open"), FontWeight = FontWeights.Bold });
            ((MenuItem)menu.Items[0]).Click += (_, _) => _active.Open(first);
            if (!first.IsDirectory && sel.Count == 1)
                menu.Items.Add(MenuEntry("Ctx_OpenWith", null, true, () => ShellVerb(first.FullPath, "openas")));
            if (sel.Count == 1 && ShellLink.IsShortcut(first.FullPath))
                menu.Items.Add(MenuEntry("Ctx_GoToTarget", null, true, () => GoToShortcutTarget(first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_Cut", "Ctrl+X", n > 0, () => PutOnClipboard(cut: true)));
            menu.Items.Add(MenuEntry("Btn_Copy", "Ctrl+C", n > 0, () => PutOnClipboard(cut: false)));
            menu.Items.Add(MenuEntry("Ctx_CopyAsPath", "Ctrl+Shift+C", n > 0, CopyAsPath));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_CopyToOther", "F5", n > 0 && otherInFolder, () => _ = TransferToOtherAsync(false)));
            menu.Items.Add(MenuEntry("Btn_MoveToOther", "F6", n > 0 && otherInFolder, () => _ = TransferToOtherAsync(true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_Delete", "Del", n > 0, () => _ = DeleteAsync()));
            menu.Items.Add(MenuEntry("Btn_Rename", "F2", n == 1, Rename));
            menu.Items.Add(new Separator());
            if (sel.Count == 1 && !first.IsDirectory && InventorProps.IsInventorFile(first.FullPath))
            {
                menu.Items.Add(MenuEntry("Ctx_IProps", "Alt+P", true, () => ShowProps(true)));
                string ext = Path.GetExtension(first.FullPath);
                if (ext.Equals(".iam", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ipt", StringComparison.OrdinalIgnoreCase))
                {
                    menu.Items.Add(MenuEntry("Ctx_CopyDesign", null, true, () => CopyDesign(first.FullPath)));
                    menu.Items.Add(MenuEntry("Ctx_RenameWithDrawing", null, true, () => _ = RenameAsync(true)));
                    if (ext.Equals(".iam", StringComparison.OrdinalIgnoreCase))
                        menu.Items.Add(MenuEntry("Ctx_Bom", null, true, () => new BomWindow(this, first.FullPath, _settings).Show()));
                }
                menu.Items.Add(new Separator());
            }
            if (sel.Count == 1 && first.IsDirectory)
            {
                string f = first.FullPath;
                bool inQuick = _quick.Contains(f), inWork = _work.Contains(f);
                menu.Items.Add(MenuEntry(inQuick ? "Ctx_Unpin" : "Ctx_Pin", null, true,
                    () => { if (inQuick) Unpin(PinList.Quick, f); else Pin(PinList.Quick, f); }));
                menu.Items.Add(MenuEntry(inWork ? "Ctx_UnpinWork" : "Ctx_PinWork", null, true,
                    () => { if (inWork) Unpin(PinList.Work, f); else Pin(PinList.Work, f); }));
            }
            menu.Items.Add(MenuEntry("Ctx_Properties", "Alt+Enter", sel.Count == 1,
                () => ShellVerb(first.FullPath, "properties")));
        }
        else
        {
            menu.Items.Add(MenuEntry("Btn_Refresh", null, true, () => _active.Reload()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_Paste", "Ctrl+V", _clipboard is not null && inFolder, () => _ = PasteAsync()));
            menu.Items.Add(MenuEntry("Ctx_PasteShortcut", null, _clipboard is not null && inFolder, PasteShortcut));
            var newMenu = new MenuItem { Header = Loc.Get("Ctx_New"), IsEnabled = inFolder };
            newMenu.Items.Add(MenuEntry("Ctx_NewFolder", "F7", true, NewFolder));
            newMenu.Items.Add(MenuEntry("Ctx_NewTextFile", null, true, NewTextFile));
            newMenu.Items.Add(MenuEntry("Ctx_NewShortcut", null, true, NewShortcut));
            menu.Items.Add(newMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Ctx_Properties", null, inFolder,
                () => ShellVerb(_active.CurrentPath, "properties")));
        }
    }

    /// <summary>The Properties sheet opens a moment later where Windows likes; move it to the middle of the pane.</summary>
    private static void CenterNewWindow(HashSet<IntPtr> before, PaneView pane)
    {
        var topLeft = pane.PointToScreen(new Point(0, 0));
        var bottomRight = pane.PointToScreen(new Point(pane.ActualWidth, pane.ActualHeight));
        var area = new Rect(topLeft, bottomRight);

        int tries = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var added = ShellInfo.OwnWindows().Where(h => !before.Contains(h)).ToList();
            if (added.Count > 0)
            {
                foreach (var h in added) ShellInfo.CenterOn(h, area);
                timer.Stop();
            }
            else if (++tries > 100) timer.Stop(); // ~3 s
        };
        timer.Start();
    }

    private void ShellVerb(string path, string verb)
    {
        try
        {
            var before = ShellInfo.OwnWindows();
            ShellInfo.Verb(path, verb, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (verb == "properties") CenterNewWindow(before, ActiveView);
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    // ---- File operations ----

    private List<string> SelectedPaths() => ActiveView.SelectedItems
        .Where(i => !i.IsDrive)
        .Select(i => i.FullPath)
        .ToList();

    /// <summary>Runs file work off the UI thread. The work returns (items done, first error or null).</summary>
    /// <summary>Copy design (Inventor assembly + the files it uses, re-linked); selects the new assembly.</summary>
    private void CopyDesign(string top)
    {
        var dlg = new CopyDesignWindow(this, top);
        if (dlg.ShowDialog() != true || dlg.Result is not { } result) return;
        _a.Reload();
        _b.Reload();
        ActiveView.SelectPaths(new[] { result });
        Status(Loc.Get("Copy_Done"));
    }

    private async Task RunAsync(Func<(int Done, string? Error)> work, string doneKey)
    {
        if (_busy) return;
        _busy = true;
        Status(Loc.Get("Msg_Working"));
        try
        {
            var (done, error) = await Task.Run(work);
            Status(error is null ? Loc.Get(doneKey, done) : Loc.Get("Msg_Error", error));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
        finally
        {
            _busy = false;
            _a.Reload();
            _b.Reload();
        }
    }

    private (int, string?) TransferAll(IEnumerable<string> sources, string destDir, bool move)
    {
        int done = 0;
        string? error = null;
        var steps = new List<UndoStep>();
        foreach (string source in sources)
        {
            try
            {
                string target = FileOps.Transfer(source, destDir, move);
                if (!string.Equals(target, source.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    steps.Add(move ? new UndoStep.Moved(source.TrimEnd('\\'), target) : new UndoStep.Created(target));
                done++;
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }
        Dispatcher.Invoke(() => _undo.Push(steps));
        return (done, error);
    }

    private async Task TransferToOtherAsync(bool move)
    {
        var sources = SelectedPaths();
        string dest = Other.CurrentPath;
        if (sources.Count == 0 || dest.Length == 0) return;

        await RunAsync(() => TransferAll(sources, dest, move), move ? "Msg_Moved" : "Msg_Copied");
    }

    private void PutOnClipboard(bool cut)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        _clipboard = (paths, cut);
        UpdateCommands();
        Status(Loc.Get("Msg_Clipboard", paths.Count));
    }

    private async Task PasteAsync()
    {
        if (_clipboard is not { } clip || _active.CurrentPath.Length == 0) return;
        string dest = _active.CurrentPath;
        if (clip.Cut) _clipboard = null;
        UpdateCommands();

        await RunAsync(() => TransferAll(clip.Paths, dest, clip.Cut), "Msg_Pasted");
    }

    private async Task DeleteAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        var answer = MessageBox.Show(this, Loc.Get("Confirm_Delete", paths.Count), Loc.Get("Confirm_DeleteTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        await RunAsync(() =>
        {
            int done = 0;
            string? error = null;
            var steps = new List<UndoStep>();
            foreach (string p in paths)
            {
                try
                {
                    FileOps.Recycle(p);
                    steps.Add(new UndoStep.Recycled(p.TrimEnd('\\')));
                    done++;
                }
                catch (Exception ex)
                {
                    error ??= ex.Message;
                }
            }
            Dispatcher.Invoke(() => _undo.Push(steps));
            return (done, error);
        }, "Msg_Deleted");
    }

    private async void Rename() => await RenameAsync(false);

    private async Task RenameAsync(bool withDrawings)
    {
        var pane = _active;
        var view = ActiveView;
        var items = view.SelectedItems;
        if (items.Count != 1 || items[0].IsDrive) return;

        var item = items[0];
        string? name = await view.EditNameAsync(item, stemOnly: !item.IsDirectory);
        RestoreActive(pane, view);
        if (name is null || name == item.Name) return;

        try
        {
            if (withDrawings && await RenameModelAsync(item.FullPath, name) is string renamed)
            {
                _a.Reload();
                _b.Reload();
                view.SelectPaths(new[] { renamed });
                return;
            }
            string target = FileOps.Rename(item.FullPath, name);
            _undo.Push(new UndoStep.Moved(item.FullPath.TrimEnd('\\'), target));
            _a.Reload();
            _b.Reload();
            view.SelectPaths(new[] { target });
            Status(Loc.Get("Msg_Renamed"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    /// <summary>
    /// .ipt/.iam renamed (same extension): its drawings in the same folder are found, a drawing with the
    /// model's name is renamed too, the links are kept and the Part Number follows the new name.
    /// Null when Apprentice is missing or the name is not a model name (plain rename).
    /// </summary>
    private async Task<string?> RenameModelAsync(string path, string name)
    {
        string ext = Path.GetExtension(path);
        if (!(ext.Equals(".ipt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".iam", StringComparison.OrdinalIgnoreCase)) ||
            !Path.GetExtension(name).Equals(ext, StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            return null;

        Status(Loc.Get("Msg_SearchingDrawings"));
        List<DesignFile> drawings;
        try
        {
            drawings = await InventorProps.FindDrawingsAsync(Path.GetDirectoryName(path)!, new[] { path },
                new Progress<string>(_ => { }), subfolders: false);
        }
        catch
        {
            return null; // no Apprentice: plain rename
        }
        string target = Path.Combine(Path.GetDirectoryName(path)!, name);
        var (newDrawings, warning) = await InventorProps.RenameWithDrawingsAsync(path, target,
            drawings.Select(d => d.Path).ToList());
        Status(newDrawings.Count == 0 ? Loc.Get("Msg_Renamed")
            : Loc.Get("Msg_RenamedWithDrawings", string.Join(", ", newDrawings.Select(Path.GetFileName))));
        if (warning is not null)
            MessageBox.Show(this, warning, "ScoutR", MessageBoxButton.OK, MessageBoxImage.Information);
        return target;
    }

    // ---- Undo (Ctrl+Z) ----

    private readonly UndoStack _undo = new();

    private async Task UndoAsync()
    {
        if (!_undo.CanUndo)
        {
            Status(Loc.Get("Msg_NothingToUndo"));
            return;
        }
        await RunAsync(() => _undo.UndoLast(), "Msg_Undone");
    }

    // ---- Shortcuts / paths ----

    /// <summary>Selected paths as text, quoted, one per line (like Explorer's "Copy as path").</summary>
    private void CopyAsPath()
    {
        var paths = ActiveView.SelectedItems.Where(i => !i.IsDrive || i.FullPath.Length > 0)
            .Select(i => $"\"{i.FullPath.TrimEnd('\\')}\"").ToList();
        if (paths.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
            Status(Loc.Get("Msg_PathCopied"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    /// <summary>Opens the shortcut target's folder in the active pane, with the target selected.</summary>
    private void GoToShortcutTarget(string lnk)
    {
        try
        {
            string? target = ShellLink.Target(lnk);
            if (target is null || !(File.Exists(target) || Directory.Exists(target)))
            {
                Status(Loc.Get("Msg_TargetMissing"));
                return;
            }
            string? dir = Path.GetDirectoryName(target.TrimEnd('\\'));
            if (dir is null) { _active.Navigate(target); return; } // a drive
            var view = ActiveView;
            if (_active.Navigate(dir))
                Dispatcher.BeginInvoke(() => { view.SelectPaths(new[] { target }); view.FocusList(); },
                    System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    /// <summary>"Paste shortcut": a .lnk in the current folder for each copied/cut item.</summary>
    private void PasteShortcut()
    {
        if (_clipboard is not { } clip || _active.CurrentPath.Length == 0) return;
        var view = ActiveView;
        try
        {
            var made = clip.Paths.Select(p => ShellLink.Create(_active.CurrentPath, p)).ToList();
            _undo.Push(made.Select(m => (UndoStep)new UndoStep.Created(m)).ToList());
            _a.Reload();
            _b.Reload();
            view.SelectPaths(made);
            Status(Loc.Get("Msg_Created"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    private void NewShortcut()
    {
        if (_active.CurrentPath.Length == 0) return;
        try
        {
            ShellLink.NewWithWizard(_active.CurrentPath, Loc.Get("NewShortcutDefault"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    private void NewFolder() => CreateNew(folder: true);

    private void NewTextFile() => CreateNew(folder: false);

    /// <summary>New folder or new text file in the active pane; the name is asked first.</summary>
    private void CreateNew(bool folder)
    {
        // Fix the target before the dialog opens: focus changes while it is open must not move it.
        var pane = _active;
        var view = ActiveView;
        string dir = pane.CurrentPath;
        if (dir.Length == 0) return;

        string? name = folder
            ? InputDialog.Ask(this, Loc.Get("Prompt_NewFolderTitle"), Loc.Get("Prompt_NewFolderLabel", dir),
                Loc.Get("NewFolderDefault"), selectStemOnly: false)
            : InputDialog.Ask(this, Loc.Get("Prompt_NewFileTitle"), Loc.Get("Prompt_NewFileLabel", dir),
                Loc.Get("NewFileDefault"), selectStemOnly: true);
        RestoreActive(pane, view);
        if (name is null) return;

        try
        {
            string target = folder ? FileOps.CreateFolder(dir, name) : FileOps.CreateFile(dir, name);
            _undo.Push(new UndoStep.Created(target));
            _a.Reload();
            _b.Reload();
            view.SelectPaths(new[] { target });
            Status(Loc.Get("Msg_Created"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }
}
