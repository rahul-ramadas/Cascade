using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cascade.Core.Updating;

/// <summary>
/// Reads releases from a GitHub repository, private or public.
///
/// A credential is used when one can be had and simply left out when it cannot, so a public repository
/// works on a machine that has never signed in to GitHub. A credential that is REFUSED is not fatal either:
/// the request is retried anonymously, because a stale token in the user's credential store must not be the
/// reason a public download fails. What happened is kept in <see cref="Note"/> either way.
///
/// Two things about private repositories are easy to get wrong and are handled here: the plain
/// <c>browser_download_url</c> returns 404 even with a valid token, so assets must be fetched from the API
/// asset endpoint with <c>Accept: application/octet-stream</c>; and the request must carry a User-Agent or
/// GitHub rejects it outright.
/// </summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    private readonly HttpClient _http;
    private readonly string _repo;
    private readonly string _apiBase;
    private readonly Func<CancellationToken, Task<string?>> _token;

    private Task<string?>? _credential;
    private volatile string? _note;

    /// <param name="repo">"owner/name".</param>
    /// <param name="token">Supplies a credential, or null for anonymous access.</param>
    /// <param name="apiBase">Overridable so tests can point at a local server.</param>
    public GitHubReleaseSource(HttpClient http, string repo, Func<CancellationToken, Task<string?>> token,
                               string apiBase = "https://api.github.com")
    {
        _http = http;
        _repo = repo;
        _token = token;
        _apiBase = apiBase.TrimEnd('/');
    }

    /// <inheritdoc/>
    public string? Note => _note;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        string url = $"{_apiBase}/repos/{_repo}/releases/latest";
        using var res = await SendAsync(url, "application/vnd.github+json", ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw await FailureAsync(res, "look for the latest release", url).ConfigureAwait(false);

        using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagProp)) return null;
        string tag = tagProp.GetString() ?? "";
        if (!TryParseTag(tag, out var version)) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            long id = a.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
            long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            if (id != 0) return new ReleaseInfo(version, tag, id, name, size);
        }
        return null;
    }

    public async Task DownloadAssetAsync(ReleaseInfo release, string destinationPath, CancellationToken ct)
    {
        string url = $"{_apiBase}/repos/{_repo}/releases/assets/{release.AssetId}";
        using var res = await SendAsync(url, "application/octet-stream", ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw await FailureAsync(res, $"download {release.AssetName}", url).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using (var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        // A truncated download is the failure most likely to slip through, and the length is free to check.
        if (release.Size > 0 && new FileInfo(destinationPath).Length != release.Size)
            throw new IOException($"Update download was {new FileInfo(destinationPath).Length} bytes, expected {release.Size}.");
    }

    /// <summary>"v2026.7.6" or "2026.7.6" to a <see cref="Version"/>.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        string t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        return Version.TryParse(t, out version!);
    }

    /// <summary>
    /// One request, with the credential if there is one - and again without it if that credential is what
    /// was objected to. 401 says the token is not accepted; 403 covers a token whose scopes do not reach
    /// this repository as well as a rate limit, and an anonymous retry of a rate-limited request simply
    /// fails again, which costs one request and tells the truth.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string url, string accept, CancellationToken ct)
    {
        string? token = await CredentialAsync(ct).ConfigureAwait(false);
        var res = await SendOnceAsync(url, accept, token, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(token)) return res;
        if (res.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return res;

        var refused = res.StatusCode;
        res.Dispose();
        _note = $"The GitHub credential git had for this machine was refused ({(int)refused} {refused}); " +
                "the check carried on without it.";
        return await SendOnceAsync(url, accept, null, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(string url, string accept, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.UserAgent.ParseAdd("Cascade-Updater");   // GitHub rejects requests without one
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>Asked for once per check. Fetching it can mean running git and waiting on a credential
    /// helper, which is not worth doing twice for the two requests an update takes.</summary>
    private Task<string?> CredentialAsync(CancellationToken ct) => _credential ??= _token(ct);

    /// <summary>What went wrong, in terms of what was being attempted - "403 rate limit exceeded" on its own
    /// tells nobody which repository was unreachable. GitHub puts a sentence in the body; it is worth having,
    /// and it is short.</summary>
    private static async Task<UpdateCheckException> FailureAsync(HttpResponseMessage res, string doing, string url)
    {
        string detail = "";
        try
        {
            string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } said)
                detail = " - " + said;
        }
        catch { /* not JSON, or nothing to say */ }

        string hint = res.StatusCode switch
        {
            HttpStatusCode.NotFound => " The repository may be private, or have no published release yet.",
            HttpStatusCode.Unauthorized => " No usable GitHub credential was available.",
            HttpStatusCode.Forbidden => " This is usually the hourly request limit for unauthenticated callers.",
            _ => ""
        };
        return new UpdateCheckException(
            $"GitHub answered {(int)res.StatusCode} {res.ReasonPhrase ?? res.StatusCode.ToString()} to a request to " +
            $"{doing} ({url}){detail}.{hint}");
    }
}
