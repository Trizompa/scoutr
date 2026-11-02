using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace ScoutR;

/// <summary>One iProperty: which property set it lives in, its name, its value.</summary>
public sealed record IPropValue(string Set, string Name, string Label, object? Value, bool ReadOnly);

/// <summary>
/// Reads and writes Inventor iProperties through Inventor Apprentice, the free component installed with
/// Inventor (no Inventor window needed). All calls run on one STA thread, the only kind COM accepts.
/// </summary>
public static class InventorProps
{
    private const string ProgId = "Inventor.ApprenticeServerComponent";
    private static readonly Guid ApprenticeClsid = new("C343ED84-A129-11D3-B799-0060B0F159EF");

    public const string SummarySet = "Inventor Summary Information";
    public const string DocSummarySet = "Inventor Document Summary Information";
    public const string ProjectSet = "Design Tracking Properties";
    public const string CustomSet = "Inventor User Defined Properties";

    // Properties shown, in this order. Everything in the custom set is shown too.
    public static readonly string[] SummaryNames =
        { "Title", "Subject", "Author", "Revision Number", "Keywords", "Comments", "Category", "Manager", "Company" };
    public static readonly string[] ProjectNames =
        { "Part Number", "Stock Number", "Description", "Project", "Designer", "Engineer", "Vendor", "Cost Center",
          "Material", "Mass" };
    private static readonly HashSet<string> ReadOnlyNames = new(StringComparer.OrdinalIgnoreCase) { "Material", "Mass" };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ipt", ".iam", ".idw", ".ipn" };

    public static bool IsInventorFile(string path) => Extensions.Contains(Path.GetExtension(path));

    private static readonly StaWorker Worker = new();
    private static dynamic? _app;

    private static dynamic App()
    {
        if (_app is null)
        {
            // The class id is the same in every Inventor version; the ProgIDs vary, so they are only a fallback.
            var type = Type.GetTypeFromCLSID(ApprenticeClsid, throwOnError: false)
                       ?? Type.GetTypeFromProgID(ProgId)
                       ?? Type.GetTypeFromProgID("Inventor.ApprenticeServer")
                       ?? throw new InvalidOperationException(Loc.Get("IProps_NoApprentice"));
            _app = Activator.CreateInstance(type)!;
        }
        return _app;
    }

    public static Task<List<IPropValue>> ReadAsync(string path) => Worker.Run(() =>
    {
        var list = new List<IPropValue>();
        dynamic doc = App().Open(path);
        try
        {
            foreach (dynamic set in doc.PropertySets)
            {
                string setName = set.Name;
                bool custom = setName == CustomSet;
                if (!custom && setName != SummarySet && setName != DocSummarySet && setName != ProjectSet) continue;

                foreach (dynamic p in set)
                {
                    string name = p.Name;
                    if (!custom && !SummaryNames.Contains(name) && !ProjectNames.Contains(name)) continue;
                    string label = name;
                    try { string d = p.DisplayName; if (!string.IsNullOrWhiteSpace(d)) label = d; } catch { }
                    object? value = null;
                    try { value = p.Value; } catch { /* some properties have no value yet */ }
                    list.Add(new IPropValue(setName, name, label, value, ReadOnlyNames.Contains(name)));
                }
            }
        }
        finally
        {
            doc.Close(true);
        }
        return list;
    });

    /// <summary>Writes the changed values back into the file.</summary>
    public static Task WriteAsync(string path, IReadOnlyList<(string Set, string Name, object? Value)> changes) =>
        Worker.Run(() =>
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new IOException(Loc.Get("IProps_ReadOnlyFile"));

            dynamic doc = App().Open(path);
            try
            {
                foreach (var change in changes)
                {
                    dynamic? target = null;
                    foreach (dynamic set in doc.PropertySets)
                    {
                        if ((string)set.Name != change.Set) continue;
                        foreach (dynamic p in set)
                            if ((string)p.Name == change.Name) { target = p; break; }
                        break;
                    }
                    if (target is null) throw new InvalidOperationException($"'{change.Name}' not found.");
                    target.Value = change.Value;
                }
                doc.PropertySets.FlushToFile();
            }
            finally
            {
                doc.Close(true);
            }
            return true;
        });

    /// <summary>Text shown in the panel for a value.</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        DateTime d when d.Year <= 1601 => "", // Inventor's "no date"
        DateTime d => d.ToString("d", CultureInfo.CurrentCulture),
        double v => v.ToString("0.###", CultureInfo.CurrentCulture),
        float v => v.ToString("0.###", CultureInfo.CurrentCulture),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "",
    };

    /// <summary>Turns edited text back into the type the property had (text, number, date, yes/no).</summary>
    public static object? Parse(string text, object? original) => original switch
    {
        double => double.Parse(text, CultureInfo.CurrentCulture),
        float => float.Parse(text, CultureInfo.CurrentCulture),
        int => int.Parse(text, CultureInfo.CurrentCulture),
        bool => bool.Parse(text),
        DateTime => DateTime.Parse(text, CultureInfo.CurrentCulture),
        _ => text,
    };
}

/// <summary>A single background STA thread that runs queued work in order.</summary>
internal sealed class StaWorker
{
    private readonly BlockingCollection<Action> _queue = new();

    public StaWorker()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = "Inventor Apprentice" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
