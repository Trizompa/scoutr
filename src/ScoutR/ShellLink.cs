using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ScoutR;

/// <summary>Windows shortcuts (.lnk) through WScript.Shell, late-bound.</summary>
public static class ShellLink
{
    public static bool IsShortcut(string path) =>
        Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    private static object Link(string lnk)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell");
        object shell = Activator.CreateInstance(type)!;
        return type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk })!;
    }

    /// <summary>Target of a shortcut, or null when it has none (e.g. shortcuts to Control Panel items).</summary>
    public static string? Target(string lnk)
    {
        object link = Link(lnk);
        string? t = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    /// <summary>Creates "&lt;name&gt; - Shortcut.lnk" in <paramref name="dir"/>, like Explorer's "Paste shortcut".</summary>
    public static string Create(string dir, string target)
    {
        string name = Path.GetFileName(target.TrimEnd('\\'));
        if (name.Length == 0) name = target.Substring(0, 1); // a drive: "C"
        string lnk = Path.Combine(dir, $"{name} - Shortcut.lnk");
        for (int i = 2; File.Exists(lnk); i++) lnk = Path.Combine(dir, $"{name} - Shortcut ({i}).lnk");

        object link = Link(lnk);
        var t = link.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
        string? work = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        if (work is not null) t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { work });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        return lnk;
    }

    /// <summary>Windows' own "New > Shortcut" wizard on an empty .lnk (it renames or removes the file).</summary>
    public static void NewWithWizard(string dir, string defaultName)
    {
        string lnk = FileOps.CreateFile(dir, defaultName + ".lnk");
        Process.Start(new ProcessStartInfo("rundll32.exe", $"appwiz.cpl,NewLinkHere \"{lnk}\"") { UseShellExecute = false });
    }
}
