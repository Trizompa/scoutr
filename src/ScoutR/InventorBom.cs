using System.IO;

namespace ScoutR;

/// <summary>One line of the structured BOM. <see cref="Props"/>: iProperty name → shown text.</summary>
public sealed record BomRow(string Item, int Level, int Qty, int TotalQty, string Path, string Structure,
    IReadOnlyDictionary<string, string> Props);

/// <summary>
/// Structured BOM (all levels) of an assembly, read through Apprentice like Inventor builds it:
/// suppressed and Reference components are left out, Phantom ones are dissolved into their parent,
/// Purchased sub-assemblies are not opened up. Equal components under one parent make one row.
/// </summary>
public static partial class InventorProps
{
    private const int BomDefault = 51969, BomPhantom = 51971, BomReference = 51972, BomPurchased = 51973;

    public static Task<(List<BomRow> Rows, List<string> Fields)> ReadBomAsync(string top, IProgress<string> progress) =>
        Worker.Run(() =>
        {
            var rows = new List<BomRow>();
            var fields = new List<string>();
            var cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            object doc = Open(top);
            try
            {
                Expand(Get(doc, "ComponentDefinition")!, 0, "", 1);
            }
            finally
            {
                Close(doc);
            }
            return (rows, fields);

            void Expand(object def, int level, string prefix, int multiplier)
            {
                // Group the components under this parent: same file (or same virtual part) = one row.
                var groups = new List<(string Key, object Def, object? Doc, int Structure, int Qty)>();
                Collect(def, groups);
                int n = 0;
                foreach (var g in groups)
                {
                    string item = prefix + (++n).ToString();
                    var props = Props(g.Key, g.Def, g.Doc);
                    rows.Add(new BomRow(item, level, g.Qty, g.Qty * multiplier, g.Doc is null ? "" : g.Key, StructureName(g.Structure), props));
                    if (g.Structure != BomPurchased && Occurrences(g.Def) is not null)
                        Expand(g.Def, level + 1, item + ".", g.Qty * multiplier);
                }
            }

            void Collect(object def, List<(string Key, object Def, object? Doc, int Structure, int Qty)> groups)
            {
                if (Occurrences(def) is not { } occs) return;
                foreach (object occ in Items(occs))
                {
                    try { if (Get(occ, "Suppressed") is true) continue; } catch { }
                    object cdef;
                    try { cdef = Get(occ, "Definition")!; } catch { continue; }
                    int structure = Structure(occ, cdef);
                    if (structure == BomReference) continue;
                    if (structure == BomPhantom)
                    {
                        Collect(cdef, groups); // its children belong to the parent
                        continue;
                    }

                    object? cdoc = null;
                    string key;
                    if (IsVirtual(cdef))
                    {
                        key = "virtual|" + (Get(occ, "Name") as string ?? "").Split(':')[0];
                    }
                    else
                    {
                        cdoc = Get(cdef, "Document");
                        key = cdoc is null ? "" : Get(cdoc, "FullFileName") as string ?? "";
                    }
                    int i = groups.FindIndex(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) groups[i] = groups[i] with { Qty = groups[i].Qty + 1 };
                    else groups.Add((key, cdef, cdoc, structure, 1));
                }
            }

            Dictionary<string, string> Props(string key, object def, object? cdoc)
            {
                if (cache.TryGetValue(key, out var known)) return known;
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (cdoc is not null) progress.Report(Path.GetFileName(key));
                try
                {
                    object sets = Get(cdoc ?? def, "PropertySets")!;
                    foreach (object set in Items(sets))
                    {
                        string setName = Get(set, "Name") as string ?? "";
                        bool custom = setName == CustomSet;
                        if (!custom && setName != SummarySet && setName != DocSummarySet && setName != ProjectSet) continue;
                        foreach (object p in Items(set))
                        {
                            string name = Get(p, "Name") as string ?? "";
                            if (!custom && !SummaryNames.Contains(name) && !ProjectNames.Contains(name)) continue;
                            object? value = null;
                            try { value = Get(p, "Value"); } catch { }
                            props[name] = Format(value, name);
                            if (!fields.Contains(name, StringComparer.OrdinalIgnoreCase)) fields.Add(name);
                        }
                    }
                }
                catch
                {
                    // unreadable: the row keeps its file name only
                }
                if (cdoc is not null)
                {
                    props["#File"] = Path.GetFileName(key);
                    if (IsFrameMember(cdoc, key)) props["#Frame"] = "Yes";
                    try
                    {
                        if (Get(cdoc, "ActiveAppearance") is { } app && Get(app, "DisplayName") is string a)
                            props["#Appearance"] = a;
                    }
                    catch { /* assemblies, or not exposed by Apprentice */ }
                }
                cache[key] = props;
                return props;
            }
        });

    /// <summary>
    /// Frame Generator member: the part carries Frame Generator's attribute set; if Apprentice can't read
    /// attributes, the "... - 001.ipt" name Frame Generator gives its members.
    /// </summary>
    private static bool IsFrameMember(object doc, string path)
    {
        if (!Path.GetExtension(path).Equals(".ipt", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            foreach (object set in Items(Get(doc, "AttributeSets")!))
                if (Get(set, "Name") is string n &&
                    (n.Contains("com.autodesk.FG", StringComparison.OrdinalIgnoreCase) ||
                     n.Contains("FrameMember", StringComparison.OrdinalIgnoreCase)))
                    return true;
        }
        catch
        {
            // attributes not available: fall back on the name
        }
        return System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"\s-\s*\d+$");
    }

    private static string StructureName(int s) => s switch
    {
        51970 => "Normal",
        51971 => "Phantom",
        51972 => "Reference",
        51973 => "Purchased",
        51974 => "Inseparable",
        51975 => "Varies",
        _ => "",
    };

    private static object? Occurrences(object def)
    {
        try { return Get(def, "Occurrences"); } catch { return null; }
    }

    // Only virtual components carry their own iProperties on the definition.
    private static bool IsVirtual(object def)
    {
        try { return Get(def, "PropertySets") is not null; } catch { return false; }
    }

    /// <summary>The component's BOM structure: its own override, else its file's.</summary>
    private static int Structure(object occ, object def)
    {
        try
        {
            int s = Convert.ToInt32(Get(occ, "BOMStructure"));
            if (s != BomDefault) return s;
        }
        catch { }
        try { return Convert.ToInt32(Get(def, "BOMStructure")); } catch { return 0; }
    }
}
