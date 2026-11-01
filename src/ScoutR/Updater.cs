using Velopack;
using Velopack.Sources;

namespace ScoutR;

/// <summary>Checks GitHub Releases for a newer version and applies it.</summary>
public sealed class Updater
{
    private const string RepoUrl = "https://github.com/Trizompa/scoutr";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _pending;

    /// <summary>Returns the new version string, or null if up to date / not installed / offline.</summary>
    public async Task<string?> CheckAsync()
    {
        try
        {
            // Not running from a Velopack package (e.g. started from the IDE): nothing to do.
            if (!_manager.IsInstalled) return null;

            _pending = await _manager.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch
        {
            return null; // Updates must never break the app.
        }
    }

    public async Task ApplyAsync()
    {
        if (_pending is null) return;
        await _manager.DownloadUpdatesAsync(_pending);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
