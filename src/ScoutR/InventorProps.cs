using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;

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
    private static object? _app;

    private static object App()
    {
        if (_app is null)
        {
            // The class id is the same in every Inventor version; the ProgIDs vary, so they are only a fallback.
            var types = new[]
            {
                Type.GetTypeFromCLSID(ApprenticeClsid, throwOnError: false),
                Type.GetTypeFromProgID(ProgId),
                Type.GetTypeFromProgID("Inventor.ApprenticeServer"),
            };
            Exception? last = null;
            foreach (var type in types)
            {
                if (type is null) continue;
                try { _app = Activator.CreateInstance(type)!; break; }
                catch (Exception ex) { last = ex; } // e.g. "class not registered": try the next one
            }
            if (_app is null)
                throw new InvalidOperationException(Loc.Get("IProps_NoApprentice") +
                                                    (last is null ? "" : $" ({last.Message})"));
        }
        return _app;
    }

    // Plain IDispatch calls (late binding). Inventor's collections don't support "foreach" over COM,
    // so they are walked with Count / Item(i), 1-based.
    private static object? Get(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);

    private static void SetValue(object o, string name, object? value) =>
        o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });

    private static object? Call(object o, string name, params object?[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);

    private static IEnumerable<object> Items(object collection)
    {
        int count = Convert.ToInt32(Get(collection, "Count"));
        for (int i = 1; i <= count; i++)
            if (Get(collection, "Item", i) is { } item) yield return item;
    }

    private static object Open(string path) =>
        Call((object)App(), "Open", path) ?? throw new IOException("Inventor Apprentice could not open the file.");

    private static void Close(object doc)
    {
        try { Call(doc, "Close", true); } catch { /* already closed */ }
    }

    public static Task<List<IPropValue>> ReadAsync(string path) => Worker.Run(() =>
    {
        var list = new List<IPropValue>();
        object doc = Open(path);
        try
        {
            foreach (object set in Items(Get(doc, "PropertySets")!))
            {
                string setName = Get(set, "Name") as string ?? "";
                bool custom = setName == CustomSet;
                if (!custom && setName != SummarySet && setName != DocSummarySet && setName != ProjectSet) continue;

                foreach (object p in Items(set))
                {
                    string name = Get(p, "Name") as string ?? "";
                    if (!custom && !SummaryNames.Contains(name) && !ProjectNames.Contains(name)) continue;
                    string label = name;
                    try { if (Get(p, "DisplayName") is string d && !string.IsNullOrWhiteSpace(d)) label = d; } catch { }
                    object? value = null;
                    try { value = Get(p, "Value"); } catch { /* some properties have no value yet */ }
                    list.Add(new IPropValue(setName, name, label, value, ReadOnlyNames.Contains(name)));
                }
            }
        }
        finally
        {
            Close(doc);
        }
        return list;
    });

    /// <summary>Writes the changed values back into the file.</summary>
    public static Task WriteAsync(string path, IReadOnlyList<(string Set, string Name, object? Value)> changes) =>
        Worker.Run(() =>
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new IOException(Loc.Get("IProps_ReadOnlyFile"));

            object doc = Open(path);
            try
            {
                object sets = Get(doc, "PropertySets")!;
                foreach (var change in changes)
                {
                    object? target = Items(sets)
                        .Where(set => Get(set, "Name") as string == change.Set)
                        .SelectMany(Items)
                        .FirstOrDefault(p => Get(p, "Name") as string == change.Name);
                    if (target is null) throw new InvalidOperationException($"'{change.Name}' not found.");
                    SetValue(target, "Value", change.Value);
                }
                Call(sets, "FlushToFile");
            }
            finally
            {
                Close(doc);
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
