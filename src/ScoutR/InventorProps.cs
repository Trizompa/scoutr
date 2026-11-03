using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace ScoutR;

public enum IPropChangeKind { Update, Add, Delete }

/// <summary>One change to write: a new value, a new custom property, or a deleted one.</summary>
public sealed record IPropChange(string Set, string Name, object? Value, IPropChangeKind Kind);

/// <summary>One iProperty: which property set it lives in, its name, its value.</summary>
public sealed record IPropValue(string Set, string Name, string Label, object? Value, bool ReadOnly);

/// <summary>
/// Reads and writes Inventor iProperties through Inventor Apprentice, the free component installed with
/// Inventor (no Inventor window needed). All calls run on one STA thread, the only kind COM accepts.
/// </summary>
public static partial class InventorProps
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
        { "Part Number", "Stock Number", "Description", "Project", "Designer", "Engineer", "Checked By",
          "Engr Approved By", "Mfg Approved By", "Vendor", "Cost Center", "Catalog Web Link", "Material", "Mass" };
    private static readonly HashSet<string> ReadOnlyNames = new(StringComparer.OrdinalIgnoreCase) { "Material", "Mass" };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ipt", ".iam", ".idw", ".ipn" };

    public static bool IsInventorFile(string path) => Extensions.Contains(Path.GetExtension(path));

    private static readonly StaWorker Worker = new();
    private static object? _app;

    // A Copy design (or similar batch job) runs on its own Apprentice instance, created for the job and
    // released after it: documents it loads (the originals, then the copies, which share their internal
    // ids) never mix with the ones held by the iProperties panel or by an earlier job.
    private static object? _scoped;

    private static object App() => _scoped ?? (_app ??= CreateApprentice());

    private static object CreateApprentice()
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
            try { return Activator.CreateInstance(type)!; }
            catch (Exception ex) { last = ex; } // e.g. "class not registered": try the next one
        }
        throw new InvalidOperationException(Loc.Get("IProps_NoApprentice") +
                                            (last is null ? "" : $" ({last.Message})"));
    }

    /// <summary>Runs <paramref name="work"/> (on the worker thread) with a fresh Apprentice, then releases it.</summary>
    private static T WithFreshApprentice<T>(Func<T> work)
    {
        object app = CreateApprentice();
        var previous = _scoped;
        _scoped = app;
        try
        {
            return work();
        }
        finally
        {
            _scoped = previous;
            try { Call(app, "Close"); } catch { /* closes every document it still holds */ }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app); } catch { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // Plain IDispatch calls (late binding). Inventor's collections don't support "foreach" over COM,
    // so they are walked with Count / Item(i), 1-based.
    private static object? Get(object o, string name, params object[] args) =>
        Invoke(o, name, BindingFlags.GetProperty, args);

    private static void SetValue(object o, string name, object? value) =>
        Invoke(o, name, BindingFlags.SetProperty, new[] { value });

    private static object? Call(object o, string name, params object?[] args) =>
        Invoke(o, name, BindingFlags.InvokeMethod, args);

    // Late-bound calls wrap Inventor's own error in "Exception has been thrown by the target of an
    // invocation": unwrap it so the real message (e.g. "file is read-only") is shown.
    private static object? Invoke(object o, string name, BindingFlags flags, object?[] args)
    {
        try
        {
            return o.GetType().InvokeMember(name, flags, null, o, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException($"{name}: {ex.InnerException.Message}", ex.InnerException);
        }
    }

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

    /// <summary>Writes the changes (edited, added and deleted properties) back into the file.</summary>
    public static Task WriteAsync(string path, IReadOnlyList<IPropChange> changes) =>
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
                    object set = Items(sets).FirstOrDefault(x => Get(x, "Name") as string == change.Set)
                                 ?? throw new InvalidOperationException($"'{change.Set}' not found.");
                    if (change.Kind == IPropChangeKind.Add)
                    {
                        Call(set, "Add", change.Value ?? "", change.Name); // PropertySet.Add(value, name)
                        continue;
                    }

                    object target = Items(set).FirstOrDefault(x => Get(x, "Name") as string == change.Name)
                                    ?? throw new InvalidOperationException($"'{change.Name}' not found.");
                    if (change.Kind == IPropChangeKind.Delete) Call(target, "Delete");
                    else SetValue(target, "Value", change.Value);
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
    public static string Format(object? value, string? name = null)
    {
        // Inventor stores the Mass iProperty in grams: shown in kg, as in Inventor.
        if (string.Equals(name, "Mass", StringComparison.OrdinalIgnoreCase) && value is double or float or int)
            return (Convert.ToDouble(value, CultureInfo.CurrentCulture) / 1000)
                .ToString("#,##0.###", CultureInfo.CurrentCulture) + " kg";
        return Format(value);
    }

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
