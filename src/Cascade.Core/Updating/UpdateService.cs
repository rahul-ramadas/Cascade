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
/// Checks for a newer release once, downloads it in the background, and parks it next to the executable.
/// Nothing is swapped while the app runs - the exchange happens after the message loop ends, so a long
/// session over a log file is never interrupted.
///
/// Every failure is silent by design: no network, no credential, a private repository the user cannot see,
/// a corrupt download - all simply mean "no update today". <see cref="LastError"/> keeps the reason for the
/// About box rather than putting it in the user's face.
/// </summary>
public sealed class UpdateService
{
    private readonly IReleaseSource _source;
    private readonly UpdateOptions _options;
    private readonly Func<string, CancellationToken, Task<bool>> _verify;

    private volatile Version? _pendingVersion;
    private volatile string? _pendingPath;
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

    /// <summary>Version waiting to be installed on exit, or null.</summary>
    public Version? PendingVersion => _pendingVersion;

    /// <summary>Why the last check produced nothing, or null if it succeeded or has not run.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Looks for a newer release and downloads it. Never throws and never blocks the UI thread; run it as a
    /// background task at startup and forget about it.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            // A staged update from a session that was killed before it could swap is still good.
            if (UpdateInstaller.FindStaged(_options.ExePath, _options.CurrentVersion, _options.Force) is { } already)
            {
                _pendingPath = already;
                _pendingVersion = UpdateInstaller.StagedVersionOf(_options.ExePath, already);
                return;
            }

            var release = await _source.GetLatestAsync(ct).ConfigureAwait(false);
            if (release is null) { _lastError ??= "No release information available."; return; }

            if (!_options.Force && release.Version <= _options.CurrentVersion)
            {
                _lastError = null;
                return;
            }

            string staged = UpdateInstaller.StagedPath(_options.ExePath, release.Version);
            // Per-process, so two instances downloading at once do not fight over one file.
            string part = $"{staged}.{Environment.ProcessId}.part";
            try
            {
                await _source.DownloadAssetAsync(release, part, ct).ConfigureAwait(false);

                if (!await _verify(part, ct).ConfigureAwait(false))
                {
                    _lastError = "The downloaded update did not run correctly and was discarded.";
                    TryDelete(part);
                    return;
                }

                try { File.Move(part, staged, overwrite: true); }
                catch when (File.Exists(staged)) { TryDelete(part); } // another instance staged the same build
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            _pendingPath = staged;
            _pendingVersion = release.Version;
            _lastError = null;
        }
        catch (OperationCanceledException) { /* shutting down; the sweep tidies up next launch */ }
        catch (Exception ex) { _lastError = ex.Message; }
    }

    /// <summary>
    /// Installs the staged update. Call after the message loop has ended - the swap renames the running
    /// executable, so doing it mid-session would be pointless and doing it during shutdown of a window is
    /// needlessly early. Returns the version installed, or null if there was nothing to do.
    /// </summary>
    public Version? ApplyPending()
    {
        try
        {
            string? staged = _pendingPath
                ?? UpdateInstaller.FindStaged(_options.ExePath, _options.CurrentVersion, _options.Force);
            if (staged is null) return null;

            var version = UpdateInstaller.StagedVersionOf(_options.ExePath, staged);
            return UpdateInstaller.Apply(_options.ExePath, staged) is null ? null : version;
        }
        catch { return null; }
    }

    /// <summary>
    /// Deleting a rejected download usually fails on the first attempt: it was just being run to verify it,
    /// and Windows releases the image a moment after the process dies. Retrying briefly means the leftover
    /// goes now rather than surviving until the next launch's sweep.
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
