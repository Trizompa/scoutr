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

    public MainWindow()
    {
        InitializeComponent();

        // After an update the app restarts with --maximized if it was maximized before.
        if (Environment.GetCommandLineArgs().Contains("--maximized")) WindowState = WindowState.Maximized;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string downloads = Path.Combine(profile, "Downloads");

        _a = new PaneViewModel(docs);
        _b = new PaneViewModel(Directory.Exists(downloads) ? downloads : profile);
        _active = _a;
        _a.IsActive = true;

        ViewA.DataContext = _a;
        ViewB.DataContext = _b;
        Tree.ItemsSource = _tree.Roots;

        foreach (var pane in new[] { _a, _b })
        {
            pane.Navigated += _ => SyncTree();
            pane.Error += (_, msg) => Status(Loc.Get("Msg_Error", msg));
        }

        ViewA.SelectedItemsChanged += (_, _) => UpdateCommands();
        ViewB.SelectedItemsChanged += (_, _) => UpdateCommands();
        ViewA.Activated += (_, _) => SetActive(_a);
        ViewB.Activated += (_, _) => SetActive(_b);

        foreach (var view in new[] { ViewA, ViewB })
        {
            var menu = new ContextMenu();
            view.ItemList.ContextMenu = menu;
            view.ItemList.ContextMenuOpening += (_, _) => BuildMenu(menu);
        }

        // Clicking a folder in the tree navigates the active pane.
        _tree.UserNavigated += path => _active.Navigate(path);
    }

    private PaneViewModel Other => _active == _a ? _b : _a;
    private PaneView ActiveView => _active == _a ? ViewA : ViewB;
    private PaneView OtherView => _active == _a ? ViewB : ViewA;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
    }

    /// <summary>After a dialog: the pane the command started in stays the active one and gets the focus back.</summary>
    private void RestoreActive(PaneViewModel pane, PaneView view)
    {
        SetActive(pane);
        view.FocusList();
    }

    private void SyncTree()
    {
        _tree.ShowFor(_active == _a ? "A" : "B", _active.CurrentPath);
        Title = $"{_active.FolderName} - ScoutR {AppInfo.Version}";
        UpdateCommands();
    }

    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item) item.BringIntoView();
        e.Handled = true;
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
            case Key.System when e.SystemKey == Key.Enter && !inText:
                var sel = ActiveView.SelectedItems;
                ShellVerb(sel.Count == 1 ? sel[0].FullPath : _active.CurrentPath, "properties");
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
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_Cut", "Ctrl+X", n > 0, () => PutOnClipboard(cut: true)));
            menu.Items.Add(MenuEntry("Btn_Copy", "Ctrl+C", n > 0, () => PutOnClipboard(cut: false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_CopyToOther", "F5", n > 0 && otherInFolder, () => _ = TransferToOtherAsync(false)));
            menu.Items.Add(MenuEntry("Btn_MoveToOther", "F6", n > 0 && otherInFolder, () => _ = TransferToOtherAsync(true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_Delete", "Del", n > 0, () => _ = DeleteAsync()));
            menu.Items.Add(MenuEntry("Btn_Rename", "F2", n == 1, Rename));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Ctx_Properties", "Alt+Enter", sel.Count == 1,
                () => ShellVerb(first.FullPath, "properties")));
        }
        else
        {
            menu.Items.Add(MenuEntry("Btn_Refresh", null, true, () => _active.Reload()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Btn_Paste", "Ctrl+V", _clipboard is not null && inFolder, () => _ = PasteAsync()));
            menu.Items.Add(MenuEntry("Btn_NewFolder", "F7", inFolder, NewFolder));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Ctx_Properties", null, inFolder,
                () => ShellVerb(_active.CurrentPath, "properties")));
        }
    }

    private void ShellVerb(string path, string verb)
    {
        try
        {
            ShellInfo.Verb(path, verb, new System.Windows.Interop.WindowInteropHelper(this).Handle);
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

    private static (int, string?) TransferAll(IEnumerable<string> sources, string destDir, bool move)
    {
        int done = 0;
        string? error = null;
        foreach (string source in sources)
        {
            try
            {
                FileOps.Transfer(source, destDir, move);
                done++;
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }
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
            foreach (string p in paths)
            {
                try
                {
                    FileOps.Recycle(p);
                    done++;
                }
                catch (Exception ex)
                {
                    error ??= ex.Message;
                }
            }
            return (done, error);
        }, "Msg_Deleted");
    }

    private void Rename()
    {
        var pane = _active;
        var view = ActiveView;
        var items = view.SelectedItems;
        if (items.Count != 1 || items[0].IsDrive) return;

        var item = items[0];
        string? name = InputDialog.Ask(this, Loc.Get("Prompt_RenameTitle"), Loc.Get("Prompt_RenameLabel"),
            item.Name, selectStemOnly: !item.IsDirectory);
        RestoreActive(pane, view);
        if (name is null || name == item.Name) return;

        try
        {
            string target = FileOps.Rename(item.FullPath, name);
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

    private void NewFolder()
    {
        // Fix the target before the dialog opens: focus changes while it is open must not move the folder.
        var pane = _active;
        var view = ActiveView;
        string folder = pane.CurrentPath;
        if (folder.Length == 0) return;

        string? name = InputDialog.Ask(this, Loc.Get("Prompt_NewFolderTitle"), Loc.Get("Prompt_NewFolderLabel", folder),
            Loc.Get("NewFolderDefault"), selectStemOnly: false);
        RestoreActive(pane, view);
        if (name is null) return;

        try
        {
            string target = FileOps.CreateFolder(folder, name);
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
