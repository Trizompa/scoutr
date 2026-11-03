using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ScoutR;

/// <summary>
/// The writing part of a Copy design (Part Numbers, re-linking), run in a separate, short-lived ScoutR
/// process. Inventor Apprentice keeps every document it ever loaded in memory for the whole process — even
/// across instances — and once an original has been loaded through its drawing, the copy (which shares the
/// original's internal id) refuses property edits. A fresh process starts clean every time.
/// </summary>
public sealed record CopyJob(Dictionary<string, string> Map, string Find, string Replace, List<string> RenamePn)
{
    public const string Arg = "--copy-job";

    /// <summary>Runs the job in a child ScoutR process; returns the warnings, throws on failure.</summary>
    public async Task<List<string>> RunIsolatedAsync(IProgress<string> progress)
    {
        string jobFile = Path.Combine(Path.GetTempPath(), $"scoutr-copy-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(this));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(Arg);
            psi.ArgumentList.Add(jobFile);

            using var child = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the copy.");
            var warnings = new List<string>();
            string? error = null;
            bool done = false;
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string kind = line[..tab], text = line[(tab + 1)..].Replace("\\n", "\n");
                switch (kind)
                {
                    case "P": progress.Report(text); break;
                    case "W": warnings.Add(text); break;
                    case "E": error = text; break;
                    case "D": done = true; break;
                }
            }
            await child.WaitForExitAsync();
            if (error is not null) throw new InvalidOperationException(error);
            if (!done) throw new InvalidOperationException($"The copy process ended unexpectedly ({child.ExitCode}).");
            return warnings;
        }
        finally
        {
            try { File.Delete(jobFile); } catch { }
        }
    }

    /// <summary>Entry point of the child process: runs the job and reports through standard output.</summary>
    public static void RunChild(string jobFile)
    {
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Send(string kind, string text) => output.WriteLine(kind + "\t" + text.Replace("\r", "").Replace("\n", "\\n"));
        try
        {
            var job = JsonSerializer.Deserialize<CopyJob>(File.ReadAllText(jobFile))
                      ?? throw new InvalidDataException("Empty copy job.");
            // JSON loses the case-insensitive comparer: Inventor may spell a path with other casing.
            var map = new Dictionary<string, string>(job.Map, StringComparer.OrdinalIgnoreCase);
            var warnings = InventorProps.CopyDesignAsync(map, job.Find, job.Replace,
                    new HashSet<string>(job.RenamePn, StringComparer.OrdinalIgnoreCase),
                    new LineProgress(name => Send("P", name)))
                .GetAwaiter().GetResult();
            foreach (string w in warnings) Send("W", w);
            Send("D", "");
        }
        catch (Exception ex)
        {
            Send("E", ex.Message);
        }
        output.Flush();
        // Apprentice can hang while unloading: end at once, everything is written.
        Process.GetCurrentProcess().Kill();
    }

    private sealed class LineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
