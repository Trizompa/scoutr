using System.IO;
using System.Runtime.InteropServices;

namespace ScoutR;

/// <summary>
/// Failsafe for the Part Number of a copy: when Apprentice refuses the edit (e.g. a file with several
/// Model States), the full Inventor does it — the one already running, or an invisible one started for
/// this and closed afterwards. Every Model State gets the same find → replace on its own Part Number.
/// </summary>
public static partial class InventorProps
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    private static object? RunningInventor()
    {
        try
        {
            if (CLSIDFromProgID("Inventor.Application", out Guid clsid) != 0) return null;
            return GetActiveObject(ref clsid, IntPtr.Zero, out object app) == 0 ? app : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Renames the Part Numbers through Inventor; returns a warning per file that still failed.</summary>
    private static List<string> RenamePartNumbersWithInventor(IReadOnlyList<string> paths, string find, string replace,
        IProgress<string> progress)
    {
        var failed = new List<string>();
        object? inv = RunningInventor();
        bool started = false;
        try
        {
            if (inv is null)
            {
                progress.Report(Loc.Get("Copy_StartingInventor"));
                var type = Type.GetTypeFromProgID("Inventor.Application")
                           ?? throw new InvalidOperationException("Inventor is not installed.");
                inv = Activator.CreateInstance(type)!;
                started = true;
                try { SetValue(inv, "Visible", false); } catch { }
            }
            bool silent = false;
            try { silent = Get(inv, "SilentOperation") is true; SetValue(inv, "SilentOperation", true); } catch { }

            foreach (string path in paths)
            {
                progress.Report(Path.GetFileName(path));
                try
                {
                    RenameWithInventor(inv, path, find, replace);
                }
                catch (Exception ex)
                {
                    failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            try { SetValue(inv, "SilentOperation", silent); } catch { }
        }
        catch (Exception ex)
        {
            failed.AddRange(paths.Select(p => $"{Path.GetFileName(p)}: {ex.Message}"));
        }
        finally
        {
            if (started && inv is not null)
                try { Call(inv, "Quit"); } catch { }
        }
        return failed;
    }

    private static void RenameWithInventor(object inv, string path, string find, string replace)
    {
        object docs = Get(inv, "Documents")!;
        object doc = Call(docs, "Open", path, false)!; // opened invisibly
        try
        {
            // With Model States, the edits go through the factory document, one state at a time.
            object target = doc;
            object? states = null;
            try
            {
                object def = Get(doc, "ComponentDefinition")!;
                if (Get(def, "IsModelStateMember") is true) target = Get(def, "FactoryDocument")!;
                states = Get(Get(target, "ComponentDefinition")!, "ModelStates");
            }
            catch { /* no Model States (older Inventor, drawing) */ }

            int count = 0;
            try { if (states is not null) count = Convert.ToInt32(Get(states, "Count")); } catch { }

            if (count > 1)
            {
                object active = Get(states!, "ActiveModelState")!;
                foreach (object state in Items(states!).ToList())
                {
                    Call(state, "Activate");
                    RenameIn(target, find, replace);
                }
                Call(active, "Activate");
            }
            else
            {
                RenameIn(target, find, replace);
            }
            Call(target, "Save");
        }
        finally
        {
            try { Call(doc, "Close", true); } catch { }
        }
    }

    private static void RenameIn(object doc, string find, string replace)
    {
        object set = Get(Get(doc, "PropertySets")!, "Item", ProjectSet)!;
        object prop = Get(set, "Item", "Part Number")!;
        if (Get(prop, "Value") is string value && value.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0)
            SetValue(prop, "Value", value.Replace(find, replace, StringComparison.OrdinalIgnoreCase));
    }
}
