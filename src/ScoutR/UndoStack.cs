using System.IO;

namespace ScoutR;

/// <summary>One file-system change that Ctrl+Z can take back.</summary>
public abstract record UndoStep
{
    /// <summary>A file or folder that did not exist before (new, copied): undone by sending it to the Recycle Bin.</summary>
    public sealed record Created(string Path) : UndoStep;

    /// <summary>Moved or renamed: undone by moving it back.</summary>
    public sealed record Moved(string From, string To) : UndoStep;

    /// <summary>Sent to the Recycle Bin: undone by restoring it from there.</summary>
    public sealed record Recycled(string Path) : UndoStep;
}

/// <summary>
/// Ctrl+Z for file operations done in ScoutR, like Explorer: each operation (one rename, one paste of
/// several files...) is one entry; the last one is undone first.
/// </summary>
public sealed class UndoStack
{
    private const int Max = 50;
    private readonly List<List<UndoStep>> _entries = new();

    public bool CanUndo => _entries.Count > 0;

    public void Push(List<UndoStep> steps)
    {
        if (steps.Count == 0) return;
        _entries.Add(steps);
        if (_entries.Count > Max) _entries.RemoveAt(0);
    }

    public void Push(UndoStep step) => Push(new List<UndoStep> { step });

    /// <summary>Undoes the last operation (runs on a background thread); returns how many items, and the first error.</summary>
    public (int Done, string? Error) UndoLast()
    {
        if (_entries.Count == 0) return (0, null);
        var steps = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);

        int done = 0;
        string? error = null;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            try
            {
                switch (steps[i])
                {
                    case UndoStep.Created c:
                        FileOps.Recycle(c.Path);
                        break;
                    case UndoStep.Moved m:
                        FileOps.MoveExact(m.To, m.From);
                        break;
                    case UndoStep.Recycled r:
                        RecycleBin.Restore(r.Path);
                        break;
                }
                done++;
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }
        return (done, error);
    }
}

/// <summary>Restores an item ScoutR sent to the Recycle Bin, through the Windows shell.</summary>
internal static class RecycleBin
{
    public static void Restore(string originalPath)
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new IOException("Shell not available.");
        object shell = Activator.CreateInstance(type)!;
        dynamic bin = ((dynamic)shell).NameSpace(10); // the Recycle Bin
        string folder = Path.GetDirectoryName(originalPath) ?? "";
        string name = Path.GetFileName(originalPath);
        string stem = Path.GetFileNameWithoutExtension(originalPath);

        dynamic? best = null;
        DateTime bestDate = DateTime.MinValue;
        dynamic items = bin.Items();
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic item = items.Item(i);
            string from = item.ExtendedProperty("System.Recycle.DeletedFrom") as string ?? "";
            if (!string.Equals(from.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            string shown = item.Name as string ?? "";
            if (!string.Equals(shown, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(shown, stem, StringComparison.OrdinalIgnoreCase)) continue; // extensions may be hidden
            DateTime when = item.ExtendedProperty("System.Recycle.DateDeleted") is DateTime d ? d : DateTime.MinValue;
            if (best is null || when > bestDate)
            {
                best = item;
                bestDate = when;
            }
        }
        if (best is null) throw new IOException($"'{name}' is no longer in the Recycle Bin.");
        best.InvokeVerb("undelete");
    }
}
