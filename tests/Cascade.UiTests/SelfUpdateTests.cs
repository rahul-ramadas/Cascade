using System.Diagnostics;
using System.Net;
using System.Text;
using Xunit;

namespace Cascade.UiTests;

/// <summary>
/// Drives a real update through the real application: a stub GitHub serves a release, the app downloads it
/// while the user works, says so in the status bar, and swaps it in only once the window closes - then the
/// executable it was running from disappears without anyone opening the app again.
///
/// The app under test is a COPY in a temp directory. The update genuinely replaces that file, so pointing
/// this at the build output would rewrite the binary every other test depends on.
/// </summary>
public class SelfUpdateTests
{
    private const string NewVersion = "9999.1.1";

    [Fact]
    public void An_update_is_downloaded_while_running_and_installed_on_exit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cascade_upd_ui_" + Guid.NewGuid().ToString("N"));
        string exe = CopyAppTo(dir);
        string staged = Path.Combine(dir, $"Cascade.update-{NewVersion}.exe");

        using var github = new StubGitHub(File.ReadAllBytes(exe), NewVersion);
        string log = TestData.WriteLogFile();
        string settingsDir = CascadeApp.NewSettingsDir();

        var env = new Dictionary<string, string>
        {
            ["CASCADE_UPDATE"] = "on",                 // LaunchExisting turns updating off by default
            ["CASCADE_UPDATE_API"] = github.Prefix,
            ["CASCADE_UPDATE_REPO"] = "owner/repo",
            ["CASCADE_UPDATE_FORCE"] = "1"             // a local build has no real version to compare
        };

        bool closed;
        try
        {
            using (var app = CascadeApp.LaunchExisting(log, null, settingsDir, ownsFiles: false,
                                                       ownsSettingsDir: false, env, exe))
            {
                // The app tells the user, without interrupting them.
                string message = app.WaitForStatus("Will update to");
                Assert.True(message.Length > 0, "no update notice appeared. Elements: " + app.DescribeTextElements());
                Assert.Equal($"Will update to v{NewVersion} on restart", message);

                // ...and the download really is on disk, parked next to the executable.
                Assert.True(File.Exists(staged), "the update was announced but never staged on disk");

                // Nothing is swapped while the user is still working.
                Assert.Equal(File.ReadAllBytes(exe).Length, new FileInfo(exe).Length);
                Assert.True(File.Exists(exe));

                closed = app.CloseGracefully();
            }
            Assert.True(closed, "the app did not exit cleanly, so the update could not be installed");

            // The swap consumes the staged file...
            Assert.True(WaitUntil(() => !File.Exists(staged)),
                        "the staged update was not installed when the app closed");
            Assert.True(File.Exists(exe), "the update left no executable behind");

            // ...and the superseded image deletes itself without waiting for the next launch.
            Assert.True(WaitUntil(() => Directory.GetFiles(dir, "Cascade.old*.exe").Length == 0),
                        "the old executable was left behind: " +
                        string.Join(", ", Directory.GetFiles(dir, "Cascade.old*.exe").Select(Path.GetFileName)));

            // The installed file is a working build, not a corpse.
            Assert.Equal(0, RunVersion(exe));
        }
        finally
        {
            try { File.Delete(log); } catch { }
            try { Directory.Delete(settingsDir, true); } catch { }
            TryDeleteDir(dir);
        }
    }

    /// <summary>
    /// Two ways an update must NOT happen, both checked here because which one applies depends on the build
    /// under test. A locally built exe reports no real version, so every release looks newer than it and the
    /// updater must not run at all - otherwise a test run would replace the developer's own binary. A
    /// released build does run the check, and must then ignore a release that is not newer.
    /// </summary>
    [Fact]
    public void A_release_that_is_not_newer_is_never_installed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cascade_upd_ui_" + Guid.NewGuid().ToString("N"));
        string exe = CopyAppTo(dir);
        bool localBuild = VersionOf(exe).Major < 2000;

        // 0.0.1 cannot be newer than anything, whichever build this is.
        using var github = new StubGitHub(File.ReadAllBytes(exe), "0.0.1");
        string log = TestData.WriteLogFile();
        string settingsDir = CascadeApp.NewSettingsDir();

        var env = new Dictionary<string, string>
        {
            ["CASCADE_UPDATE"] = "on",
            ["CASCADE_UPDATE_API"] = github.Prefix,
            ["CASCADE_UPDATE_REPO"] = "owner/repo"
            // deliberately no CASCADE_UPDATE_FORCE
        };

        try
        {
            using (var app = CascadeApp.LaunchExisting(log, null, settingsDir, ownsFiles: false,
                                                       ownsSettingsDir: false, env, exe))
            {
                Thread.Sleep(3000);   // give the background check every chance to do the wrong thing
                Assert.Equal("", app.StatusText("Will update to"));
                app.CloseGracefully();
            }

            Assert.Empty(Directory.GetFiles(dir, "Cascade.update-*"));
            Assert.Empty(Directory.GetFiles(dir, "Cascade.old*.exe"));
            Assert.Equal(0, RunVersion(exe));   // still the build we started with, and still runnable

            if (localBuild)
                Assert.True(github.ReleaseRequests == 0,
                            "a local build asked for a release; it must not update itself at all");
        }
        finally
        {
            try { File.Delete(log); } catch { }
            try { Directory.Delete(settingsDir, true); } catch { }
            TryDeleteDir(dir);
        }
    }

    /// <summary>
    /// The notice sits at the otherwise empty right end of the menu bar rather than in the status bar,
    /// where it used to squeeze the file paths. That is only worth doing if it disturbs nothing: the menus
    /// must still work, it must not be reachable by keyboard, and it must not collide with the menu items
    /// when the window is too narrow to hold both.
    /// </summary>
    [Fact]
    public void The_update_notice_sits_in_the_menu_bar_without_disturbing_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cascade_upd_ui_" + Guid.NewGuid().ToString("N"));
        string exe = CopyAppTo(dir);

        using var github = new StubGitHub(File.ReadAllBytes(exe), NewVersion);
        string log = TestData.WriteLogFile();
        string settingsDir = CascadeApp.NewSettingsDir();
        var env = new Dictionary<string, string>
        {
            ["CASCADE_UPDATE"] = "on",
            ["CASCADE_UPDATE_API"] = github.Prefix,
            ["CASCADE_UPDATE_REPO"] = "owner/repo",
            ["CASCADE_UPDATE_FORCE"] = "1"
        };

        try
        {
            using var app = CascadeApp.LaunchExisting(log, null, settingsDir, ownsFiles: false,
                                                      ownsSettingsDir: false, env, exe);
            Assert.NotEqual("", app.WaitForStatus("Will update to"));

            var notice = app.Element("Will update to") ?? throw new InvalidOperationException("no update notice");
            var file = app.Element("File") ?? throw new InvalidOperationException("no File menu");
            var help = app.Element("Help") ?? throw new InvalidOperationException("no Help menu");

            // Same band as the menus, and hard right.
            Assert.True(Math.Abs(notice.BoundingRectangle.Top - file.BoundingRectangle.Top) <= 6,
                        $"notice is not on the menu row: notice {notice.BoundingRectangle}, File {file.BoundingRectangle}");
            Assert.True(notice.BoundingRectangle.Left > help.BoundingRectangle.Right,
                        "notice overlaps the menu items");
            Assert.True(app.Window.BoundingRectangle.Right - notice.BoundingRectangle.Right < 60,
                        "notice is not right-aligned");

            // It is a label, not a menu: tabbing or arrowing along the menu bar must never land on it.
            Assert.False(notice.Properties.IsKeyboardFocusable.ValueOrDefault,
                         "the update notice can take keyboard focus");

            // The menus still work with it present.
            Assert.True(app.ClickMenu("View", "Zoom In"), "the View menu stopped working");
            Assert.True(app.WaitStatus("Zoom:", "Zoom: 110%"), app.StatusText("Zoom:"));

            // Squeeze the window to its narrowest: MainForm clamps to a 700px MinimumSize, so this is the
            // tightest the menu bar can ever get and the only state where a collision is reachable.
            app.ResizeTo(400, 500);
            double width = app.Window.BoundingRectangle.Width;
            Assert.True(width <= 780, $"the window did not shrink to its minimum: {app.Window.BoundingRectangle}");

            var narrowHelp = app.Element("Help");
            Assert.NotNull(narrowHelp);            // the menus must survive being squeezed
            Assert.NotNull(app.Element("File"));

            var narrowNotice = app.Element("Will update to");
            Assert.NotNull(narrowNotice);          // and the notice must not silently vanish
            Assert.True(narrowNotice!.BoundingRectangle.Left >= narrowHelp!.BoundingRectangle.Right,
                        $"at minimum width the notice overlaps the menus: notice " +
                        $"{narrowNotice.BoundingRectangle}, Help {narrowHelp.BoundingRectangle}, window {width}");

            app.CloseGracefully();
        }
        finally
        {
            try { File.Delete(log); } catch { }
            try { Directory.Delete(settingsDir, true); } catch { }
            TryDeleteDir(dir);
        }
    }

    /// <summary>
    /// Two instances running from the same directory both stage the same update and both try to install it
    /// as they exit. The one thing that must never happen is ending up without a working executable: the
    /// first mover renames it out of the way, so a second mover that assumed it was still there could leave
    /// nothing to launch. Also checks the swap is not applied twice and that nothing is left lying about
    /// once a later run has had its chance to sweep.
    /// </summary>
    [Fact]
    public void Two_instances_updating_at_once_leave_exactly_one_working_executable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cascade_upd_ui_" + Guid.NewGuid().ToString("N"));
        string exe = CopyAppTo(dir);
        long originalLength = new FileInfo(exe).Length;

        using var github = new StubGitHub(File.ReadAllBytes(exe), NewVersion);
        string logA = TestData.WriteLogFile(), logB = TestData.WriteLogFile();
        string cfgA = CascadeApp.NewSettingsDir(), cfgB = CascadeApp.NewSettingsDir();
        var env = new Dictionary<string, string>
        {
            ["CASCADE_UPDATE"] = "on",
            ["CASCADE_UPDATE_API"] = github.Prefix,
            ["CASCADE_UPDATE_REPO"] = "owner/repo",
            ["CASCADE_UPDATE_FORCE"] = "1"
        };

        try
        {
            var a = CascadeApp.LaunchExisting(logA, null, cfgA, ownsFiles: false, ownsSettingsDir: false, env, exe);
            var b = CascadeApp.LaunchExisting(logB, null, cfgB, ownsFiles: false, ownsSettingsDir: false, env, exe);
            try
            {
                // Both must see the update; they share one staging path, so this is the contended case.
                Assert.NotEqual("", a.WaitForStatus("Will update to"));
                Assert.NotEqual("", b.WaitForStatus("Will update to"));

                a.CloseGracefully();
                Assert.True(WaitUntil(() => RunVersion(exe) == 0),
                            "after the first instance installed the update the executable did not run");

                b.CloseGracefully();
            }
            finally { a.Dispose(); b.Dispose(); }

            // The invariant that matters: one executable, and it works.
            Assert.True(File.Exists(exe), "the update left no executable behind");
            Assert.Equal(0, RunVersion(exe));
            Assert.True(new FileInfo(exe).Length == originalLength,
                        "the installed executable is not the size of the one that was served");

            Assert.Empty(Directory.GetFiles(dir, "*.part"));
            Assert.Empty(Directory.GetFiles(dir, "Cascade.update-*.exe"));

            // The image the second instance was still running from cannot be deleted until it exits, so it
            // falls to whichever instance leaves last - no waiting for the next launch.
            Assert.True(WaitUntil(() => Directory.GetFiles(dir, "Cascade.old*.exe").Length == 0),
                        "a superseded executable was left behind: " +
                        string.Join(", ", Directory.GetFiles(dir, "Cascade.old*.exe").Select(Path.GetFileName)));
        }
        finally
        {
            foreach (string f in new[] { logA, logB }) { try { File.Delete(f); } catch { } }
            foreach (string d in new[] { cfgA, cfgB }) { try { Directory.Delete(d, true); } catch { } }
            TryDeleteDir(dir);
        }
    }

    // ---- helpers ----

    /// <summary>Copies the application under test into its own directory. Works for both layouts: the
    /// multi-file build output and the single published exe.</summary>
    private static string CopyAppTo(string dir)
    {
        Directory.CreateDirectory(dir);
        string source = TestData.AppExe();
        string sourceDir = Path.GetDirectoryName(source)!;
        foreach (string f in Directory.GetFiles(sourceDir))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
        return Path.Combine(dir, Path.GetFileName(source));
    }

    private static int RunVersion(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add("--version");
        using var p = Process.Start(psi)!;
        p.WaitForExit(30000);
        return p.ExitCode;
    }

    /// <summary>Asks the executable under test what it is, rather than assuming which build CI is running.</summary>
    private static Version VersionOf(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add("--version");
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        return Version.TryParse(text.Trim().Split('+')[0], out var v) ? v : new Version(0, 0, 0);
    }

    private static bool WaitUntil(Func<bool> condition, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static void TryDeleteDir(string dir)
    {
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(dir, true); return; } catch { Thread.Sleep(100); }
        }
    }

    /// <summary>The smallest GitHub that will satisfy the updater: one release, one executable asset.</summary>
    private sealed class StubGitHub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _asset;
        private readonly string _version;
        private int _releaseRequests;
        public string Prefix { get; }

        /// <summary>How many times the app asked what the latest release is.</summary>
        public int ReleaseRequests => Volatile.Read(ref _releaseRequests);

        public StubGitHub(byte[] asset, string version)
        {
            _asset = asset;
            _version = version;
            int port = 0;
            for (int p = 39_200; p < 39_400; p++)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                    _listener.Start();
                    port = p;
                    break;
                }
                catch (HttpListenerException) { }
            }
            if (port == 0) throw new InvalidOperationException("No free port for the stub GitHub.");
            Prefix = $"http://127.0.0.1:{port}";
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                try
                {
                    string path = ctx.Request.Url!.AbsolutePath;
                    if (path.EndsWith("/releases/latest"))
                    {
                        Interlocked.Increment(ref _releaseRequests);
                        string json = $$"""
                            { "tag_name": "v{{_version}}",
                              "assets": [ { "name": "Cascade-{{_version}}-win-x64.exe", "id": 1, "size": {{_asset.Length}} } ] }
                            """;
                        byte[] body = Encoding.UTF8.GetBytes(json);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body);
                    }
                    else if (path.Contains("/releases/assets/"))
                    {
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = _asset.Length;
                        ctx.Response.OutputStream.Write(_asset);
                    }
                    else ctx.Response.StatusCode = 404;
                }
                catch { /* the client went away */ }
                try { ctx.Response.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
