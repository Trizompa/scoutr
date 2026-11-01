using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Windows.Markup;

namespace ScoutR;

/// <summary>String lookup. English is the base; add Resources/Strings.&lt;culture&gt;.resx for other languages.</summary>
public static class Loc
{
    private static readonly ResourceManager Rm =
        new("ScoutR.Resources.Strings", Assembly.GetExecutingAssembly());

    public static string Get(string key) =>
        Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Get(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>XAML usage: Text="{l:T Btn_Copy}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TExtension() { }

    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
