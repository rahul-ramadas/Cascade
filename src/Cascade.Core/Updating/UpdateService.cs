namespace Cascade.Core.Updating;

/// <summary>How the running application describes itself to the updater.</summary>
public sealed class UpdateOptions
{
    /// <summary>Full path of the running executable (<c>Environment.ProcessPath</c>).</summary>
    public required string ExePath { get; init; }

    /// <summary>Version of the running executable.</summary>
    public required Version CurrentVersion { get; init; }

    /// <summary>Accept a release that is not newer, and apply a staged update regardless of its version.
    /// Exists so the whole path can be exercised without publishing a release.</summary>
    public bool Force { get; init; }
}

/// <summary>
/// Checks for a newer release once at startup and installs it while the app runs.
///
/// Installing does not disturb the session: the running process keeps going from its moved-aside image,
/// and the new build takes effect at the next launch. Doing it now rather than on the way out means a kill,
/// a dropped RDP session or a power cut later cannot lose an update that was already downloaded.
///
/// Only one process per installed copy does this, decided by <see cref="InstallLock"/>. Every failure is
/// silent by design - no network, no credential, a private repository the user cannot see - and
/// <see cref="LastError"/> keeps the reason for the About box rather than putting it in the user's face.
/// </summary>
public sealed class UpdateService
{
    private readonly IReleaseSource _source;
    private readonly UpdateOptions _options;
    private readonly Func<string, CancellationToken, Task<bool>> _verify;

    private volatile Version? _pendingVersion;
    private volatile string? _lastError;

    /// <param name="verify">Proves a downloaded file is a working build before it is allowed to replace the
    /// running one. The application runs it with <c>--version</c>; tests substitute their own.</param>
    public UpdateService(IReleaseSource source, UpdateOptions options,
                         Func<string, CancellationToken, Task<bool>> verify)
    {
        _source = source;
        _options = options;
        _verify = verify;
    }

    /// <summary>The version installed on disk when it is newer than the one running - whoever installed it.
    /// It takes effect at the next launch.</summary>
    public Version? PendingVersion => _pendingVersion;

    /// <summary>Why the last check produced nothing, or null if it succeeded or has not run.</summary>
    public string? LastError => _lastError;

    /// <summary>Re-reads what is on disk, which another instance may have replaced. Only ever raises the
    /// notice: once a newer build is installed it cannot become un-installed.</summary>
    public void RefreshPending()
    {
        var installed = UpdateInstaller.VersionOf(_options.ExePath);
        var running = UpdateInstaller.Normalize(_options.CurrentVersion);
        if (installed is not null && installed > running) _pendingVersion = installed;
    }

    /// <summary>
    /// The whole update, start to finish. Never throws and never blocks the UI thread; run it as a
    /// background task at startup and forget about it.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        RefreshPending();
        using var gate = InstallLock.TryAcquire(_options.ExePath);
        if (gate is null) return;

        try { await UpdateAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down; the next launch sweeps up */ }
        catch (Exception ex) { _lastError = ex.Message; }

        RefreshPending();
    }

    private async Task UpdateAsync(CancellationToken ct)
    {
        string exe = _options.ExePath;
        var installed = UpdateInstaller.VersionOf(exe) ?? UpdateInstaller.Normalize(_options.CurrentVersion);
        string staged = UpdateInstaller.StagedPath(exe);

        // A verified download left behind by a killed session is still good, if it is still newer.
        if (File.Exists(staged))
        {
            var already = UpdateInstaller.VersionOf(staged);
            if (already is not null && (_options.Force || already > installed)) { Install(exe, staged, already); return; }
            TryDelete(staged);
        }

        var release = await _source.GetLatestAsync(ct).ConfigureAwait(false);
        if (release is null) { _lastError = "No release information available."; return; }

        if (!_options.Force && release.Version <= installed) { _lastError = null; return; }

        string part = UpdateInstaller.PartPath(exe);
        try
        {
            await _source.DownloadAssetAsync(release, part, ct).ConfigureAwait(false);

            if (!await _verify(part, ct).ConfigureAwait(false))
            {
                _lastError = "The downloaded update did not run correctly and was discarded.";
                TryDelete(part);
                return;
            }
            MoveWithRetry(part, staged);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        Install(exe, staged, UpdateInstaller.VersionOf(staged) ?? release.Version);
    }

    private void Install(string exe, string staged, Version? version)
    {
        if (UpdateInstaller.Apply(exe, staged) is null)
        {
            _lastError = "The update was downloaded but could not be installed.";
            return;
        }
        _pendingVersion = version;
        _lastError = null;
    }

    /// <summary>A just-written executable is often held for a moment by antivirus or the search indexer,
    /// and losing the download to that would mean fetching it all over again.</summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(from, to, overwrite: true); return; }
            catch when (attempt < 20) { Thread.Sleep(100); }
        }
    }

    /// <summary>
    /// Deleting a rejected download usually fails on the first attempt: it was just being run to verify it,
    /// and Windows releases the image a moment after the process dies.
    /// </summary>
    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch { Thread.Sleep(100); }
        }
    }
}
