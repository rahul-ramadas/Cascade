using System.Diagnostics;
using System.Net.Http;
using Cascade.Core.Updating;

namespace Cascade.App;

/// <summary>
/// Wires the updater to this application: which repository to watch, when updating is allowed at all, and
/// how a downloaded file is proved to be a working build before it is trusted.
/// </summary>
internal static class AppUpdater
{
    /// <summary>Set to "off" to disable updating entirely. The UI tests use it so a run never touches the
    /// network, and it is the escape hatch if updating ever misbehaves.</summary>
    public const string DisableVariable = "CASCADE_UPDATE";

    /// <summary>Set to "1" to install the latest release even when it is not newer, and to let a local build
    /// update itself. This is how the whole path is exercised without publishing anything.</summary>
    public const string ForceVariable = "CASCADE_UPDATE_FORCE";

    /// <summary>Points the updater at a different API root - a local stub in the end-to-end test.</summary>
    public const string ApiVariable = "CASCADE_UPDATE_API";

    /// <summary>Overrides the "owner/name" repository.</summary>
    public const string RepoVariable = "CASCADE_UPDATE_REPO";

    /// <summary>Set to a file path to have each run append what the update attempt did. Updating is silent
    /// by design, which makes "it just did not update" impossible to answer on a machine you cannot poke at
    /// - a CI runner, or a user's.</summary>
    public const string LogVariable = "CASCADE_UPDATE_LOG";

    private const string DefaultRepo = "rahul-ramadas/Cascade";
    private const string DefaultApi = "https://api.github.com";
    private const int VerifyTimeoutMs = 20_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>The updater for this run, or null when updating does not apply.</summary>
    public static UpdateService? Create()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(DisableVariable), "off", StringComparison.OrdinalIgnoreCase))
            return null;

        bool force = Environment.GetEnvironmentVariable(ForceVariable) == "1";

        // A locally built exe has no meaningful version, so every release looks newer; it would replace the
        // developer's own build with a download. Only an explicit force overrides that.
        if (AppInfo.IsDevBuild && !force) return null;
        if (string.IsNullOrEmpty(AppInfo.ExePath)) return null;

        string repo = Environment.GetEnvironmentVariable(RepoVariable) is { Length: > 0 } r ? r : DefaultRepo;
        string api = Environment.GetEnvironmentVariable(ApiVariable) is { Length: > 0 } a ? a : DefaultApi;

        var source = new GitHubReleaseSource(Http, repo, ct => TokenFor(api, ct), api);
        var options = new UpdateOptions
        {
            ExePath = AppInfo.ExePath,
            CurrentVersion = AppInfo.Version,
            Force = force
        };
        return new UpdateService(source, options, VerifyAsync);
    }

    /// <summary>Appends what the update attempt came to, when CASCADE_UPDATE_LOG names a file.</summary>
    public static void LogOutcome(UpdateService updater)
    {
        if (Environment.GetEnvironmentVariable(LogVariable) is not { Length: > 0 } path) return;
        try
        {
            File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} exe={AppInfo.ExePath} running={AppInfo.Version} " +
                $"onDisk={UpdateInstaller.VersionOf(AppInfo.ExePath)?.ToString() ?? "?"} " +
                $"pending={updater.PendingVersion?.ToString() ?? "none"} " +
                $"error={updater.LastError ?? "none"}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break a run */ }
    }

    /// <summary>
    /// The user's git credential is only ever sent to GitHub itself. CASCADE_UPDATE_API can point the
    /// updater anywhere, so without this check anyone able to set that variable could collect a token that
    /// carries the scopes of the user's git, not of this app.
    /// </summary>
    private static Task<string?> TokenFor(string api, CancellationToken ct)
    {
        bool isGitHub = Uri.TryCreate(api, UriKind.Absolute, out var uri)
                        && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
        return isGitHub
            ? GitCredentialToken.GetAsync("github.com", ct)
            : Task.FromResult(Environment.GetEnvironmentVariable(GitCredentialToken.EnvironmentVariable));
    }

    /// <summary>
    /// Proves a downloaded file is a working build before it is allowed to replace the running one. First
    /// the cheap structural check - an HTML error page or a truncated download is not a PE image at all -
    /// and only then is it actually run.
    ///
    /// The process is killed if it does not answer promptly. A build that hangs or opens a window instead
    /// of printing its version must not be left running in the background, and must not be installed.
    /// </summary>
    private static async Task<bool> VerifyAsync(string path, CancellationToken ct)
    {
        if (!IsWindowsExecutable(path)) return false;

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--version");

            proc = Process.Start(psi);
            if (proc is null) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(VerifyTimeoutMs);

            string output = await proc.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (proc.ExitCode != 0) return false;
            string first = output.Split('\n')[0].Trim().Split('+')[0];
            return Version.TryParse(first, out _);
        }
        catch { return false; }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            proc?.Dispose();
        }
    }

    /// <summary>True if the file really is a Windows executable (MZ header pointing at a PE signature).</summary>
    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[64];
            if (fs.Read(head) < 64 || head[0] != (byte)'M' || head[1] != (byte)'Z') return false;

            int peOffset = BitConverter.ToInt32(head[0x3C..0x40]);
            if (peOffset <= 0 || peOffset > fs.Length - 4) return false;

            fs.Position = peOffset;
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == (byte)'P' && sig[1] == (byte)'E' && sig[2] == 0 && sig[3] == 0;
        }
        catch { return false; }
    }
}
