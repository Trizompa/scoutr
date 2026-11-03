using Velopack;
using Velopack.Sources;

namespace ScoutR;

/// <summary>Checks GitHub Releases for a newer version and applies it.</summary>
public sealed class Updater
{
    private const string RepoUrl = "https://github.com/Trizompa/scoutr";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _pending;

    /// <summary>False when started from the IDE or an unpacked build: updates only work for the packaged app.</summary>
    public bool IsInstalled
    {
        get
        {
            try { return _manager.IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>Message of the last failed check, or null if the last check worked.</summary>
    public string? Error { get; private set; }

    /// <summary>Returns the new version string, or null if up to date, not installed or offline.</summary>
    public async Task<string?> CheckAsync()
    {
        Error = null;
        try
        {
            if (!IsInstalled) return null;

            _pending = await _manager.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            Error = ex.Message; // Updates must never break the app.
            return null;
        }
    }

    /// <summary>Downloads and installs the update, then restarts. A maximized window stays maximized.</summary>
    public async Task ApplyAsync(bool maximized)
    {
        if (_pending is null) return;
        await _manager.DownloadUpdatesAsync(_pending);
        // The updater waits for ScoutR to exit, then replaces the files and restarts it. ScoutR ends itself
        // hard: a normal exit can hang while Inventor Apprentice unloads, and a ScoutR still running
        // keeps its files locked, so the update would silently not happen.
        _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: false, restart: true,
            restartArgs: maximized ? new[] { "--maximized" } : Array.Empty<string>());
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
