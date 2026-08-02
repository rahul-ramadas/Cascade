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

    private string WriteStaged(string content = "new build")
    {
        string p = UpdateInstaller.StagedPath(_exe);
        File.WriteAllText(p, content);
        return p;
    }

    // ---------------- naming and versions ----------------

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
    public void A_build_must_not_compare_as_older_than_itself()
    {
        // The running app parses "2026.8.1" (Revision -1); the same file's version resource reads
        // "2026.8.1.0" (Revision 0). Left alone, -1 < 0 makes every build look out of date forever.
        var running = new Version(2026, 8, 1);
        var fromFile = new Version(2026, 8, 1, 0);
        Assert.True(running < fromFile, "the trap this normalisation exists for has changed");

        Assert.Equal(UpdateInstaller.Normalize(fromFile), UpdateInstaller.Normalize(running));
        Assert.False(UpdateInstaller.Normalize(fromFile) > UpdateInstaller.Normalize(running));
    }

    [Fact]
    public void A_version_is_read_from_a_real_executable_without_running_it()
    {
        string realExe = Environment.ProcessPath!;   // the test host: a genuine signed binary with a version
        Assert.NotNull(UpdateInstaller.VersionOf(realExe));
        Assert.Null(UpdateInstaller.VersionOf(_exe));               // a text file has no version resource
        Assert.Null(UpdateInstaller.VersionOf(Path.Combine(_dir, "absent.exe")));
    }

    // ---------------- sweeping ----------------

    [Fact]
    public void Sweep_removes_the_superseded_executable_and_abandoned_downloads()
    {
        string old = UpdateInstaller.OldPath(_exe);
        File.WriteAllText(old, "previous");
        string part = UpdateInstaller.PartPath(_exe);
        File.WriteAllText(part, "half a download");

        UpdateInstaller.Sweep(_exe);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(part));
        Assert.True(File.Exists(_exe));
    }

    [Fact]
    public void Sweep_leaves_a_staged_build_alone()
    {
        // Only the lock holder may judge a staged build; sweeping it away from under one would throw away a
        // complete, verified download.
        string staged = WriteStaged();
        UpdateInstaller.Sweep(_exe);
        Assert.True(File.Exists(staged));
    }

    // ---------------- the swap ----------------

    [Fact]
    public void Apply_installs_the_new_build_and_hands_back_the_old_one()
    {
        string staged = WriteStaged("the new build");

        string? old = UpdateInstaller.Apply(_exe, staged);

        Assert.NotNull(old);
        Assert.Equal("the new build", File.ReadAllText(_exe));
        Assert.Equal("running build", File.ReadAllText(old!));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void A_failed_install_leaves_the_working_executable_where_it_was()
    {
        // The most damaging possible outcome is losing the executable entirely, so the install is one
        // operation that either happens or does not.
        string staged = WriteStaged("the new build");
        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(UpdateInstaller.Apply(_exe, staged));

        Assert.True(File.Exists(_exe));
        Assert.Equal("running build", File.ReadAllText(_exe));
    }

    [Fact]
    public void An_old_image_still_in_use_does_not_block_the_install()
    {
        // A process from an earlier generation is still running from Cascade.old.exe, so that name cannot
        // be reused - but the update must still go in.
        string inUse = UpdateInstaller.OldPath(_exe);
        File.WriteAllText(inUse, "an earlier generation");
        string staged = WriteStaged("the new build");

        // Sharing reads still denies deletion, which is the condition under test.
        using (File.Open(inUse, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string? old = UpdateInstaller.Apply(_exe, staged);

            Assert.NotNull(old);
            Assert.NotEqual(inUse, old);
            Assert.Equal("the new build", File.ReadAllText(_exe));
            Assert.Equal("an earlier generation", File.ReadAllText(inUse));
        }
    }

    [Fact]
    public void The_staged_build_being_held_as_a_recently_run_image_does_not_block_the_install()
    {
        // The staged file was executed moments earlier to verify it, and Windows holds the image open for
        // a while after that process exits - with exactly this sharing. Renaming is still permitted, which
        // is why the install must not depend on ReplaceFileW alone.
        string staged = WriteStaged("the new build");

        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            string? old = UpdateInstaller.Apply(_exe, staged);

            Assert.NotNull(old);
            Assert.Equal("the new build", File.ReadAllText(_exe));
            Assert.Equal("running build", File.ReadAllText(old!));
        }
    }

    [Fact]
    public void The_running_image_being_held_open_does_not_block_the_install()
    {
        // Windows keeps a running executable open for reading and deleting, and so do scanners. That is
        // more sharing than ReplaceFileW will accept, so on some machines - a GitHub runner among them -
        // the tidy swap fails and the install has to fall back to renaming the image out of the way.
        string staged = WriteStaged("the new build");

        using (File.Open(_exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            string? old = UpdateInstaller.Apply(_exe, staged);

            Assert.NotNull(old);
            Assert.Equal("the new build", File.ReadAllText(_exe));
            Assert.Equal("running build", File.ReadAllText(old!));
        }
    }

    [Fact]
    public void Apply_does_nothing_when_there_is_no_staged_build()
        => Assert.Null(UpdateInstaller.Apply(_exe, UpdateInstaller.StagedPath(_exe)));

    [Theory]
    [InlineData("Cascade.old.exe", true)]
    [InlineData("Cascade.old-123.exe", true)]
    [InlineData("Cascade.exe", false)]
    [InlineData("Cascade.new.exe", false)]
    [InlineData("settings.json", false)]
    public void Only_a_superseded_image_may_be_named_on_the_cleanup_command_line(string name, bool allowed)
        => Assert.Equal(allowed, UpdateInstaller.IsSupersededImagePath(_exe, Path.Combine(_dir, name)));

    [Fact]
    public void Cleanup_refuses_a_path_outside_the_install_directory()
        => Assert.False(UpdateInstaller.IsSupersededImagePath(_exe, Path.Combine(_dir, "sub", "Cascade.old.exe")));

    // ---------------- the lock ----------------

    [Fact]
    public void Only_one_process_at_a_time_may_update_an_installed_copy()
    {
        using (var first = InstallLock.TryAcquire(_exe))
        {
            Assert.NotNull(first);
            Assert.Null(InstallLock.TryAcquire(_exe));   // a second instance must not proceed
        }

        using var afterRelease = InstallLock.TryAcquire(_exe);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public void The_lock_leaves_nothing_behind()
    {
        InstallLock.TryAcquire(_exe)!.Dispose();
        Assert.False(File.Exists(InstallLock.PathFor(_exe)));
    }

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
    public async Task A_newer_release_is_downloaded_and_installed_while_running()
    {
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source);

        await svc.RunAsync(CancellationToken.None);

        Assert.Equal(new Version(2026, 8, 1), svc.PendingVersion);
        Assert.Equal("downloaded build", File.ReadAllText(_exe));
        Assert.Equal("running build", File.ReadAllText(UpdateInstaller.OldPath(_exe)));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.False(File.Exists(UpdateInstaller.StagedPath(_exe)));
    }

    [Fact]
    public async Task A_release_that_is_not_newer_is_left_alone()
    {
        var source = new FakeSource { Latest = Release(Current) };
        var svc = Service(source);

        await svc.RunAsync(CancellationToken.None);

        Assert.Null(svc.PendingVersion);
        Assert.Equal(0, source.Downloads);
        Assert.Equal("running build", File.ReadAllText(_exe));
    }

    [Fact]
    public async Task Forcing_installs_the_same_version_so_the_path_can_be_exercised()
    {
        var source = new FakeSource { Latest = Release(Current) };
        var svc = Service(source, force: true);

        await svc.RunAsync(CancellationToken.None);

        Assert.Equal(Current, svc.PendingVersion);
        Assert.Equal("downloaded build", File.ReadAllText(_exe));
    }

    [Fact]
    public async Task A_download_that_will_not_run_is_thrown_away()
    {
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source, verify: (_, _) => Task.FromResult(false));

        await svc.RunAsync(CancellationToken.None);

        Assert.Null(svc.PendingVersion);
        Assert.Equal("running build", File.ReadAllText(_exe));   // nothing was installed
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.False(File.Exists(UpdateInstaller.StagedPath(_exe)));
        Assert.NotNull(svc.LastError);
    }

    [Fact]
    public async Task A_failing_check_is_silent_but_remembered()
    {
        var source = new FakeSource { Failure = new HttpRequestException("no network") };
        var svc = Service(source);

        await svc.RunAsync(CancellationToken.None);   // must not throw

        Assert.Null(svc.PendingVersion);
        Assert.Contains("no network", svc.LastError);
        Assert.Equal("running build", File.ReadAllText(_exe));
    }

    [Fact]
    public async Task An_instance_that_loses_the_lock_does_not_even_ask_for_a_release()
    {
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source);

        using (InstallLock.TryAcquire(_exe))          // another instance is already updating
            await svc.RunAsync(CancellationToken.None);

        Assert.Equal(0, source.LatestCalls);
        Assert.Equal("running build", File.ReadAllText(_exe));
    }

    [Fact]
    public async Task A_staged_build_of_unknown_provenance_is_replaced_rather_than_trusted()
    {
        // Whatever this is, its version cannot be read, so it must not be installed on faith.
        WriteStaged("something left behind");
        var source = new FakeSource { Latest = Release(new Version(2026, 8, 1)) };
        var svc = Service(source);

        await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, source.Downloads);
        Assert.Equal("downloaded build", File.ReadAllText(_exe));
    }

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
                if (path.StartsWith("/repos/", StringComparison.Ordinal) && path.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    SeenAuthorization = ctx.Request.Headers["Authorization"];
                    SeenUserAgent = ctx.Request.Headers["User-Agent"];
                    byte[] body = Encoding.UTF8.GetBytes(Json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(body);
                }
                else if (path.StartsWith("/repos/", StringComparison.Ordinal) && path.Contains("/releases/assets/"))
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
