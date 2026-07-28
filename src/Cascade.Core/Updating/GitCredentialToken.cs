using System.Diagnostics;

namespace Cascade.Core.Updating;

/// <summary>
/// Borrows the GitHub credential the user has already given to git, so the app needs no token of its own
/// and none is ever embedded in the binary.
///
/// <c>git credential fill</c> is git's documented interface to whatever helper is configured (Git Credential
/// Manager on Windows), and it returns the cached token on stdout. The interactive prompts are turned OFF:
/// left on, a background check at startup could pop a GitHub sign-in window at a user who did not ask for
/// one. No credential simply means no updates.
///
/// The token is read from a pipe, held only for the duration of a check, and never logged or written to
/// disk - it is the user's own GitHub credential, not the app's.
/// </summary>
public static class GitCredentialToken
{
    private const int TimeoutMs = 15_000;

    /// <summary>An override for tests and unattended machines; checked before git is consulted.</summary>
    public const string EnvironmentVariable = "CASCADE_UPDATE_TOKEN";

    /// <summary>
    /// Finds git.exe by absolute path, never by name.
    ///
    /// CreateProcess searches the calling image's OWN directory first, and this application is designed to
    /// be copied into shared folders and onto USB sticks - a git.exe dropped beside it would otherwise be
    /// executed, as the user, on every startup. That directory and the current directory are both refused.
    /// </summary>
    private static string? ResolveGit()
    {
        string? own = null;
        try { own = Path.GetDirectoryName(Environment.ProcessPath ?? ""); } catch { /* keep looking */ }

        foreach (string dir in SearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string full = Path.GetFullPath(dir.Trim().Trim('"'));
                if (own is { Length: > 0 } && string.Equals(full, own, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(full, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase)) continue;

                string candidate = Path.Combine(full, "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* an unusable PATH entry */ }
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            yield return dir;

        foreach (string root in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            if (Environment.GetEnvironmentVariable(root) is { Length: > 0 } p)
            {
                yield return Path.Combine(p, "Git", "cmd");
                yield return Path.Combine(p, "Git", "bin");
            }

        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
            yield return Path.Combine(local, "Programs", "Git", "cmd");
    }

    public static async Task<string?> GetAsync(string host, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
            return fromEnv;

        try { return await FromGitAsync(host, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static async Task<string?> FromGitAsync(string host, CancellationToken ct)
    {
        if (ResolveGit() is not { } git) return null;

        var psi = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("credential");
        psi.ArgumentList.Add("fill");
        // Fail instead of prompting: this runs unattended in the background at startup.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GCM_GUI_PROMPT"] = "false";

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutMs);

        try
        {
            await proc.StandardInput.WriteAsync($"protocol=https\nhost={host}\n\n").ConfigureAwait(false);
            proc.StandardInput.Close();

            string output = await proc.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;

            foreach (string line in output.Split('\n'))
            {
                string l = line.Trim('\r');
                if (l.StartsWith("password=", StringComparison.Ordinal))
                {
                    string token = l["password=".Length..];
                    return token.Length == 0 ? null : token;
                }
            }
            return null;
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return null;
        }
    }
}
