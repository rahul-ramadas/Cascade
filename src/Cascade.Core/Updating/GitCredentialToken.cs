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

    public static async Task<string?> GetAsync(string host, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
            return fromEnv;

        try { return await FromGitAsync(host, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static async Task<string?> FromGitAsync(string host, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
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
