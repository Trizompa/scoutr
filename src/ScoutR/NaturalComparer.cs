using System.Runtime.InteropServices;

namespace ScoutR;

/// <summary>Explorer's order: "2" before "10", case ignored.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);
}
