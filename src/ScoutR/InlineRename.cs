using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ScoutR;

/// <summary>
/// Text box drawn over a file name to rename it in place, like Explorer (F2). Enter or clicking away
/// confirms, Esc cancels. Lives in the list's adorner layer, so it scrolls with the row.
/// </summary>
internal sealed class InlineRename : Adorner
{
    private readonly TextBox _box;
    private readonly VisualCollection _visuals;
    private readonly TaskCompletionSource<string?> _done = new();
    private readonly double _width;
    private bool _closed;
    private bool _armed;

    private InlineRename(FrameworkElement target, string name, bool stemOnly, double width) : base(target)
    {
        _width = width;
        _box = new TextBox
        {
            Text = name,
            Padding = new Thickness(1, 0, 1, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("AccentBrush"),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _visuals = new VisualCollection(this) { _box };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Close(_box.Text.Trim()); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        };
        _box.LostKeyboardFocus += (_, _) => { if (_armed) Close(_box.Text.Trim()); };
        // Focus once everything else has settled (a context menu closing gives focus back to the row).
        _box.Loaded += (_, _) => _box.Dispatcher.BeginInvoke(() =>
        {
            _box.Focus();
            int dot = stemOnly ? name.LastIndexOf('.') : -1;
            if (dot > 0) _box.Select(0, dot); else _box.SelectAll();
            _armed = true;
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Edits <paramref name="name"/> over <paramref name="target"/>; null when cancelled.</summary>
    public static Task<string?> Edit(FrameworkElement target, string name, bool stemOnly, double width)
    {
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return Task.FromResult<string?>(null);
        var adorner = new InlineRename(target, name, stemOnly, width);
        layer.Add(adorner);
        return adorner._done.Task;
    }

    private void Close(string? result)
    {
        if (_closed) return;
        _closed = true;
        AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);
        _done.TrySetResult(string.IsNullOrEmpty(result) ? null : result);
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _box.Measure(new Size(_width, double.PositiveInfinity));
        return new Size(_width, _box.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double h = Math.Max(_box.DesiredSize.Height, AdornedElement.RenderSize.Height + 4);
        _box.Arrange(new Rect(-3, (AdornedElement.RenderSize.Height - h) / 2, _width, h));
        return finalSize;
    }
}
