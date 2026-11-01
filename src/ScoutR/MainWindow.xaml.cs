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

        ViewA.Activated += (_, _) => SetActive(_a);
        ViewB.Activated += (_, _) => SetActive(_b);

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
        ViewA.FocusList();

        string? version = await _updater.CheckAsync();
        if (version is not null)
        {
            UpdateButton.Content = Loc.Get("Update_Available", version);
            UpdateButton.Visibility = Visibility.Visible;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        Status(Loc.Get("Update_Downloading"));
        try
        {
            await _updater.ApplyAsync(); // restarts the app on success
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
            UpdateButton.IsEnabled = true;
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

    private void SyncTree()
    {
        _tree.Reveal(Other.CurrentPath);
        _tree.SelectPath(_active.CurrentPath);
        _tree.MarkOther(Other.CurrentPath);
        Title = $"{_active.FolderName} - ScoutR";
    }

    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item) item.BringIntoView();
        e.Handled = true;
    }

    private void Status(string text) => StatusText.Text = text;

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
        Status(Loc.Get("Msg_Clipboard", paths.Count));
    }

    private async Task PasteAsync()
    {
        if (_clipboard is not { } clip || _active.CurrentPath.Length == 0) return;
        string dest = _active.CurrentPath;
        if (clip.Cut) _clipboard = null;

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
        var items = ActiveView.SelectedItems;
        if (items.Count != 1 || items[0].IsDrive) return;

        var item = items[0];
        string? name = InputDialog.Ask(this, Loc.Get("Prompt_RenameTitle"), Loc.Get("Prompt_RenameLabel"),
            item.Name, selectStemOnly: !item.IsDirectory);
        if (name is null || name == item.Name) return;

        try
        {
            string target = FileOps.Rename(item.FullPath, name);
            _a.Reload();
            _b.Reload();
            ActiveView.SelectPaths(new[] { target });
            Status(Loc.Get("Msg_Renamed"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }

    private void NewFolder()
    {
        if (_active.CurrentPath.Length == 0) return;

        string? name = InputDialog.Ask(this, Loc.Get("Prompt_NewFolderTitle"), Loc.Get("Prompt_NewFolderLabel"),
            Loc.Get("NewFolderDefault"), selectStemOnly: false);
        if (name is null) return;

        try
        {
            string target = FileOps.CreateFolder(_active.CurrentPath, name);
            _a.Reload();
            _b.Reload();
            ActiveView.SelectPaths(new[] { target });
            Status(Loc.Get("Msg_Created"));
        }
        catch (Exception ex)
        {
            Status(Loc.Get("Msg_Error", ex.Message));
        }
    }
}
