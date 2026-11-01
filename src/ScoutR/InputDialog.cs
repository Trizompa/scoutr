using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ScoutR;

/// <summary>Small "type a name" dialog, built in code.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box = new();

    private InputDialog(Window owner, string title, string label, string initial, bool selectStemOnly)
    {
        Owner = owner;
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var ok = new Button { Content = Loc.Get("Dlg_OK"), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.Get("Dlg_Cancel"), Width = 80, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;

        _box.Text = initial;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _box.Focus();
            int dot = initial.LastIndexOf('.');
            _box.Select(0, selectStemOnly && dot > 0 ? dot : initial.Length);
        };
    }

    /// <summary>Returns the trimmed text, or null if cancelled or empty.</summary>
    public static string? Ask(Window owner, string title, string label, string initial, bool selectStemOnly)
    {
        var dlg = new InputDialog(owner, title, label, initial, selectStemOnly);
        if (dlg.ShowDialog() != true) return null;
        string v = dlg._box.Text.Trim();
        return v.Length == 0 ? null : v;
    }
}
