using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScoutR;

/// <summary>
/// Preview of the selected file on the right of the panes, like Explorer's preview pane:
/// the shell thumbnail (Inventor, images, PDF...) or the first part of a text file.
/// </summary>
public sealed class PreviewPanel : Border
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".xml", ".json", ".ini", ".cfg", ".md", ".cs", ".py", ".js", ".bat", ".cmd",
        ".ps1", ".html", ".htm", ".css", ".yml", ".yaml", ".sql", ".reg", ".nc", ".tap", ".gcode",
    };

    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Visibility = Visibility.Collapsed, Background = Brushes.Transparent,
    };
    private readonly TextBlock _name = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _none = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    };
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private string? _pending, _shown;

    public PreviewPanel()
    {
        Background = Brushes.White;
        Padding = new Thickness(10);
        _info.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _none.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _none.Text = Loc.Get("Preview_None");

        var dock = new DockPanel();
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(_name);
        footer.Children.Add(_info);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        var body = new Grid();
        body.Children.Add(_image);
        body.Children.Add(_text);
        body.Children.Add(_none);
        dock.Children.Add(body);
        Child = dock;

        // Waits a moment so moving through the list with the arrows doesn't load every file.
        _delay.Tick += (_, _) => { _delay.Stop(); Load(_pending); };
    }

    /// <summary>Shows the file (null: nothing selected or several items).</summary>
    public void Show(FsItem? item)
    {
        _pending = item?.FullPath;
        if (_pending == _shown) return;
        _delay.Stop();
        _delay.Start();
    }

    private void Load(string? path)
    {
        _shown = path;
        _image.Source = null;
        _text.Text = "";
        _text.Visibility = Visibility.Collapsed;
        _none.Visibility = Visibility.Collapsed;
        _name.Text = path is null ? "" : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        _info.Text = "";
        if (path is null) { _none.Visibility = Visibility.Visible; return; }

        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                _info.Text = FsItem.FormatSize(fi.Length) + "  ·  " + fi.LastWriteTime.ToString("g");
                if (TextExt.Contains(fi.Extension))
                {
                    _text.Text = ReadStart(path);
                    _text.Visibility = Visibility.Visible;
                    return;
                }
            }
            else if (Directory.Exists(path))
                _info.Text = Directory.GetLastWriteTime(path).ToString("g");
        }
        catch { /* locked or vanished */ }

        int size = (int)Math.Clamp(Math.Max(ActualWidth, ActualHeight * 0.7), 128, 1024);
        _image.Source = ShellInfo.Thumbnail(path, size);
        if (_image.Source is null) _none.Visibility = Visibility.Visible;
    }

    private static string ReadStart(string path)
    {
        const int max = 64 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buf = new char[max];
        int read = reader.ReadBlock(buf, 0, max);
        var s = new string(buf, 0, read);
        return read == max ? s + "\n…" : s;
    }
}
