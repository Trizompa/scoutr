using System.Reflection;

namespace ScoutR;

public static class AppInfo
{
    /// <summary>Version set by the release build (for example "0.1.2"); "dev" when run from the IDE.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var attr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        string v = attr?.InformationalVersion?.Split('+')[0] ?? "";
        return v.Length == 0 || v == "1.0.0" ? "dev" : v;
    }
}
