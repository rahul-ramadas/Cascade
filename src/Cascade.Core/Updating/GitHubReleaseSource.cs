using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cascade.Core.Updating;

/// <summary>
/// Reads releases from a GitHub repository, private or public.
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

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{_repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        await AuthorizeAsync(req, ct).ConfigureAwait(false);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;

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
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{_repo}/releases/assets/{release.AssetId}");
        req.Headers.Accept.ParseAdd("application/octet-stream");
        await AuthorizeAsync(req, ct).ConfigureAwait(false);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

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

    private async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        // GitHub rejects requests without one.
        req.Headers.UserAgent.ParseAdd("Cascade-Updater");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        string? token = await _token(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
