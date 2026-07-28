using System.Net;
using System.Net.Http;
using System.Text;
using Cascade.Core.Updating;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>
/// The updater replaces the executable the user runs, so every one of these guards a way that could go
/// wrong quietly: staging the wrong version, installing an unusable download, or - worst - leaving no
/// working executable behind at all.
/// </summary>
public class UpdateTests : IDisposable
{
    private readonly string _dir;
    private readonly string _exe;
    private static readonly Version Current = new(2026, 7, 6);

    public UpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cascade_upd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "Cascade.exe");
        File.WriteAllText(_exe, "running build");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteStaged(Version v, string content = "new build")
    {
        string p = UpdateInstaller.StagedPath(_exe, v);
        File.WriteAllText(p, content);
        return p;
    }

    // ---------------- naming and parsing ----------------

    [Theory]
    [InlineData("v2026.7.6", true, "2026.7.6")]
    [InlineData("2026.7.6", true, "2026.7.6")]
    [InlineData("V1.2.3.4", true, "1.2.3.4")]
    [InlineData("nightly", false, null)]
    [InlineData("", false, null)]
    public void Release_tags_parse_with_or_without_the_v_prefix(string tag, bool ok, string? expected)
    {
        Assert.Equal(ok, GitHubReleaseSource.TryParseTag(tag, out var v));
        if (ok) Assert.Equal(Version.Parse(expected!), v);
    }

    [Fact]
    public void A_staged_file_name_carries_its_version_so_it_survives_a_kill()
    {
        string staged = UpdateInstaller.StagedPath(_exe, new Version(2026, 8, 1));
        Assert.Equal(_dir, Path.GetDirectoryName(staged));
        Assert.Equal(new Version(2026, 8, 1), UpdateInstaller.StagedVersionOf(_exe, staged));
    }

    [Fact]
    public void Unrelated_files_are_not_mistaken_for_staged_updates()
    {
        Assert.Null(UpdateInstaller.StagedVersionOf(_exe, Path.Combine(_dir, "Cascade.exe")));
        Assert.Null(UpdateInstaller.StagedVersionOf(_exe, Path.Combine(_dir, "Cascade.update-nightly.exe")));
        Assert.Null(UpdateInstaller.StagedVersionOf(_exe, Path.Combine(_dir, "Other.update-1.2.3.exe")));
    }

    // ---------------- sweeping ----------------

    [Fact]
    public void Sweep_removes_the_superseded_executable_and_abandoned_downloads()
    {
        string old = UpdateInstaller.OldPath(_exe);
        File.WriteAllText(old, "previous");
        string part = UpdateInstaller.StagedPath(_exe, new Version(2026, 9, 1)) + ".part";
        File.WriteAllText(part, "half a download");

        UpdateInstaller.Sweep(_exe, Current);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(part));
        Assert.True(File.Exists(_exe));
    }

    [Fact]
    public void Sweep_discards_a_staged_update_that_is_no_longer_newer()
    {
        // This is what an applied update becomes: the staged copy is now the running version.
        string stale = WriteStaged(new Version(2026, 7, 6));
        string older = WriteStaged(new Version(2025, 1, 1));

        UpdateInstaller.Sweep(_exe, Current);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(older));
    }

    [Fact]
    public void Sweep_keeps_a_staged_update_that_is_still_newer()
    {
        string keep = WriteStaged(new Version(2026, 8, 1));
        UpdateInstaller.Sweep(_exe, Current);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void The_newest_staged_update_is_the_one_chosen()
    {
        WriteStaged(new Version(2026, 8, 1));
        string newest = WriteStaged(new Version(2026, 12, 3));
        WriteStaged(new Version(2026, 7, 9));

        Assert.Equal(newest, UpdateInstaller.FindStaged(_exe, Current, force: false));
    }

    [Fact]
    public void A_staged_update_that_is_not_newer_is_only_chosen_when_forced()
    {
        string same = WriteStaged(Current);
        Assert.Null(UpdateInstaller.FindStaged(_exe, Current, force: false));
        Assert.Equal(same, UpdateInstaller.FindStaged(_exe, Current, force: true));
    }

    // ---------------- the swap ----------------

    [Fact]
    public void Apply_puts_the_new_build_in_place_and_hands_back_the_old_one()
    {
        string staged = WriteStaged(new Version(2026, 8, 1), "the new build");

        string? old = UpdateInstaller.Apply(_exe, staged);

        Assert.NotNull(old);
        Assert.Equal("the new build", File.ReadAllText(_exe));
        Assert.Equal("running build", File.ReadAllText(old!));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void A_failed_swap_leaves_the_working_executable_where_it_was()
    {
        // The most damaging possible outcome is moving the running exe away and then failing to install the
        // replacement, which would leave nothing to launch next time.
        string staged = WriteStaged(new Version(2026, 8, 1), "the new build");
        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string? old = UpdateInstaller.Apply(_exe, staged);
            Assert.Null(old);
        }

        Assert.True(File.Exists(_exe));
        Assert.Equal("running build", File.ReadAllText(_exe));
    }

    [Fact]
    public void Apply_does_nothing_when_there_is_no_staged_file()
        => Assert.Null(UpdateInstaller.Apply(_exe, Path.Combine(_dir, "Cascade.update-2026.8.1.exe")));

    // ---------------- the service ----------------

    private sealed class FakeSource : IReleaseSource
    {
        public ReleaseInfo? Latest;
        public string Payload = "downloaded build";
        public int LatestCalls;
        public int Downloads;
        public Exception? Failure;

        public Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
        {
            LatestCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Latest);
        }

        public Task DownloadAssetAsync(ReleaseInfo release, string destinationPath, CancellationToken ct)
        {
            Downloads++;
            File.WriteAllText(destinationPath, Payload);
            return Task.CompletedTask;
        }
    }

    private UpdateService Service(FakeSource source, bool force = false,
                                  Func<string, CancellationToken, Task<bool>>? verify = null)
        => new(source,
               new UpdateOptions { ExePath = _exe, CurrentVersion = Current, Force = force },
               verify ?? ((_, _) => Task.FromResult(true)));

    private static ReleaseInfo Release(Version v) => new(v, "v" + v, 42, "Cascade.exe", 0);

    [Fact]
    public async Task A_newer_release_is_downloaded_and_staged()
    {
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source);

        await svc.CheckAsync(CancellationToken.None);

        Assert.Equal(new Version(2026, 8, 1), svc.PendingVersion);
        Assert.Equal("downloaded build", File.ReadAllText(UpdateInstaller.StagedPath(_exe, new Version(2026, 8, 1))));
        Assert.Equal("running build", File.ReadAllText(_exe));  // nothing is swapped while running
    }

    [Fact]
    public async Task A_release_that_is_not_newer_is_left_alone()
    {
        var source = new FakeSource { Latest = Release(Current) };
        var svc = Service(source);

        await svc.CheckAsync(CancellationToken.None);

        Assert.Null(svc.PendingVersion);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task Forcing_installs_the_same_version_so_the_path_can_be_exercised()
    {
        var source = new FakeSource { Latest = Release(Current) };
        var svc = Service(source, force: true);

        await svc.CheckAsync(CancellationToken.None);

        Assert.Equal(Current, svc.PendingVersion);
        Assert.Equal(1, source.Downloads);
    }

    [Fact]
    public async Task A_download_that_will_not_run_is_thrown_away()
    {
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source, verify: (_, _) => Task.FromResult(false));

        await svc.CheckAsync(CancellationToken.None);

        Assert.Null(svc.PendingVersion);
        Assert.Empty(Directory.GetFiles(_dir, "Cascade.update-*"));
        Assert.NotNull(svc.LastError);
    }

    [Fact]
    public async Task A_failing_check_is_silent_but_remembered()
    {
        var source = new FakeSource { Failure = new HttpRequestException("no network") };
        var svc = Service(source);

        await svc.CheckAsync(CancellationToken.None);   // must not throw

        Assert.Null(svc.PendingVersion);
        Assert.Contains("no network", svc.LastError);
    }

    [Fact]
    public async Task An_update_staged_by_a_killed_session_is_adopted_without_asking_again()
    {
        WriteStaged(new Version(2026, 8, 1));
        var source = new FakeSource { Failure = new InvalidOperationException("must not be consulted") };
        var svc = Service(source);

        await svc.CheckAsync(CancellationToken.None);

        Assert.Equal(new Version(2026, 8, 1), svc.PendingVersion);
        Assert.Equal(0, source.LatestCalls);
    }

    [Fact]
    public async Task The_staged_update_is_installed_on_exit_and_reported()
    {
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source);
        await svc.CheckAsync(CancellationToken.None);

        var installed = svc.ApplyPending();

        Assert.Equal(new Version(2026, 8, 1), installed);
        Assert.Equal("downloaded build", File.ReadAllText(_exe));
        Assert.True(File.Exists(UpdateInstaller.OldPath(_exe)));
    }

    [Fact]
    public void Exiting_with_nothing_staged_installs_nothing()
        => Assert.Null(Service(new FakeSource()).ApplyPending());

    // ---------------- the GitHub source, against a real HTTP server ----------------

    private sealed class StubGitHub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public string Prefix { get; }
        public string? SeenAuthorization, SeenUserAgent, SeenAssetAccept;
        public byte[] Asset = Encoding.UTF8.GetBytes("MZ fake executable payload");
        public long ReportedSize = -1;   // -1 means "report the real length"
        public string Json = "";

        public StubGitHub()
        {
            int port = 0;
            for (int p = 39_000; p < 39_200; p++)
            {
                try { _listener.Prefixes.Clear(); _listener.Prefixes.Add($"http://127.0.0.1:{p}/"); _listener.Start(); port = p; break; }
                catch (HttpListenerException) { }
            }
            if (port == 0) throw new InvalidOperationException("No free port for the stub server.");
            Prefix = $"http://127.0.0.1:{port}";
            _loop = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }

                string path = ctx.Request.Url!.AbsolutePath;
                if (path.StartsWith("/repos/") && path.EndsWith("/releases/latest"))
                {
                    SeenAuthorization = ctx.Request.Headers["Authorization"];
                    SeenUserAgent = ctx.Request.Headers["User-Agent"];
                    byte[] body = Encoding.UTF8.GetBytes(Json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(body);
                }
                else if (path.StartsWith("/repos/") && path.Contains("/releases/assets/"))
                {
                    SeenAssetAccept = ctx.Request.Headers["Accept"];
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.OutputStream.Write(Asset);
                }
                else ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
            try { _loop.Wait(2000); } catch { }
        }
    }

    private static string ReleaseJson(string tag, string assetName, long id, long size)
        => $$"""
             { "tag_name": "{{tag}}",
               "assets": [ { "name": "notes.txt", "id": 1, "size": 3 },
                           { "name": "{{assetName}}", "id": {{id}}, "size": {{size}} } ] }
             """;

    [Fact]
    public async Task The_github_source_reads_a_release_and_downloads_its_executable()
    {
        using var stub = new StubGitHub();
        stub.Json = ReleaseJson("v2026.9.4", "Cascade-2026.9.4-win-x64.exe", 777, stub.Asset.Length);
        using var http = new HttpClient();
        var src = new GitHubReleaseSource(http, "owner/repo", _ => Task.FromResult<string?>("secret-token"), stub.Prefix);

        var release = await src.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(2026, 9, 4), release!.Version);
        Assert.Equal(777, release.AssetId);                       // the .exe, not the notes.txt listed first
        Assert.Equal("Cascade-2026.9.4-win-x64.exe", release.AssetName);

        string dest = Path.Combine(_dir, "downloaded.bin");
        await src.DownloadAssetAsync(release, dest, CancellationToken.None);
        Assert.Equal(stub.Asset, File.ReadAllBytes(dest));

        // A private asset is only served to a request that is authenticated and asks for the raw bytes.
        Assert.Equal("Bearer secret-token", stub.SeenAuthorization);
        Assert.Contains("Cascade-Updater", stub.SeenUserAgent);
        Assert.Equal("application/octet-stream", stub.SeenAssetAccept);
    }

    [Fact]
    public async Task A_truncated_download_is_rejected_rather_than_installed()
    {
        using var stub = new StubGitHub();
        // The release claims more bytes than the server actually sends.
        stub.Json = ReleaseJson("v2026.9.4", "Cascade.exe", 777, stub.Asset.Length + 500);
        using var http = new HttpClient();
        var src = new GitHubReleaseSource(http, "owner/repo", _ => Task.FromResult<string?>(null), stub.Prefix);

        var release = await src.GetLatestAsync(CancellationToken.None);
        string dest = Path.Combine(_dir, "truncated.bin");

        await Assert.ThrowsAsync<IOException>(() => src.DownloadAssetAsync(release!, dest, CancellationToken.None));
    }

    [Fact]
    public async Task A_release_with_no_executable_asset_is_ignored()
    {
        using var stub = new StubGitHub();
        stub.Json = """{ "tag_name": "v2026.9.4", "assets": [ { "name": "notes.txt", "id": 1, "size": 3 } ] }""";
        using var http = new HttpClient();
        var src = new GitHubReleaseSource(http, "owner/repo", _ => Task.FromResult<string?>(null), stub.Prefix);

        Assert.Null(await src.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_repository_simply_yields_no_release()
    {
        using var stub = new StubGitHub();   // every unknown path answers 404, like a private repo does
        using var http = new HttpClient();
        var src = new GitHubReleaseSource(http, "owner/repo", _ => Task.FromResult<string?>(null),
                                          stub.Prefix + "/nope");

        Assert.Null(await src.GetLatestAsync(CancellationToken.None));
    }
}
