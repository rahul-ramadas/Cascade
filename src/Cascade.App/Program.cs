using System.Runtime.InteropServices;
using System.Text;
using Cascade.Core.Updating;

namespace Cascade.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Started by the previous version as it exited, purely to delete the executable it was running from
        // (Windows will not let a process delete its own image). Returns before any UI exists, so nothing
        // appears on screen. The path is checked: this must not become a way to delete an arbitrary file.
        if (args.Length > 0 && args[0].Equals("--cleanup", StringComparison.OrdinalIgnoreCase))
            return args.Length >= 3 && int.TryParse(args[1], out int pid)
                   && UpdateInstaller.IsSupersededImagePath(AppInfo.ExePath, args[2])
                ? UpdateInstaller.RunCleanup(pid, args[2])
                : 2;

        // Also how a freshly downloaded update proves it is a working build before it is allowed to replace
        // the running one.
        if (args.Length > 0 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsoleIfNotRedirected();
            Console.WriteLine(AppInfo.InformationalVersion);
            return 0;
        }

        if (args.Length > 0 && IsHelp(args[0]))
        {
            AttachConsoleIfNotRedirected();
            Console.WriteLine(HelpText);
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(-1); // attach to the launching console so output is visible
            InitialiseUi(); // the render checks build real controls
            FailFastOnUiException();
            return SelfTest.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0].Equals("--rendershots", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            InitialiseUi();
            FailFastOnUiException();
            return RenderShots.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0].Equals("--screens", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            InitialiseUi();
            FailFastOnUiException();
            return UiShots.Run(args.Skip(1).ToArray());
        }

        InitialiseUi();

        string crashLog = Path.Combine(Path.GetTempPath(), "cascade_crash.log");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(crashLog, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(crashLog, e.ExceptionObject as Exception);

        var settings = AppSettings.Load();
        var state = MachineState.Load();

        Automation.Configure(settings.Automation);
        _ = Automation.PayTheStartupCost();

        // Tidy up after previous runs first, and do it whether or not updating is switched on: turning
        // updates off must not strand a superseded executable or a half-finished download forever.
        try { UpdateInstaller.Sweep(AppInfo.ExePath); } catch { /* best effort */ }

        // Checked once, at startup, on a background thread. Installing does not disturb this session - the
        // process keeps running from its moved-aside image - and the new build takes effect at next launch.
        var updater = AppUpdater.Create();
        using var updateCts = new CancellationTokenSource();
        if (updater is not null)
            _ = Task.Run(async () =>
            {
                await updater.RunAsync(updateCts.Token);
                AppUpdater.LogOutcome(updater);
            });

        try
        {
            Application.Run(new MainForm(settings, state, args, updater));
        }
        catch (Exception ex)
        {
            LogCrash(crashLog, ex);
            throw;
        }
        finally
        {
            updateCts.Cancel();
            UpdateInstaller.CleanUpSupersededImages(AppInfo.ExePath);
        }
        return 0;
    }


    /// <summary>Attaching to the launching console re-points the standard handles, which would send output
    /// to the terminal instead of the pipe when a parent is capturing it - as update verification does.</summary>
    private static void AttachConsoleIfNotRedirected()
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
    }

    private static bool IsHelp(string a) =>        a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        a == "/?";

    /// <summary>
    /// Describes what the argument parser actually does, not what it was once meant to do. Every switch
    /// below is recognised only as the FIRST argument, which is worth saying out loud because
    /// <c>Cascade.exe file.log --version</c> opens the log rather than printing a version.
    /// </summary>
    private const string HelpText = """
        Cascade - a fast, hierarchical-filtering log and text analyzer.

        Usage:
          Cascade.exe [file] [/Filters:<path>] [/demo]

          file              A file to open. Ignored if it does not exist.
          /Filters:<path>   A .cascade or .tat filter file to load. Also stops the last-used
                            filter file being loaded automatically.
          /demo             Enable the first four filters and select the first.

          Only the last file and the last /Filters: are used; neither accumulates.

        Diagnostics (each must be the FIRST argument, and none of them open a window):

          --help, -h, /?    Show this text.
          --version         Print the version and exit.
          --selftest [file] [/Filters:<path>] [--only=<text>]
                            Run headless engine, settings and rendering checks.
                            --only runs just the check groups whose name contains <text>.
                            Log: %TEMP%\cascade_selftest.log. Exit 0 pass, 1 fail, 2 error.
          --screens [outDir] [file] [file.tat]
                            Render every dialog and the main window to PNGs.
                            Create outDir first; otherwise %TEMP%\cascade_shots is used.
          --cleanup <pid> <path>
                            Internal. Started by the previous version as it exits, to delete
                            the executable it was running from.

        Environment:

          CASCADE_SETTINGS_DIR    Directory holding settings.json and state.json
                                  (default %APPDATA%\Cascade).
          CASCADE_AUTOMATION=1    Answer screen readers and UI automation. Off by default:
                                  handing out a provider is what makes closing a window
                                  stall on machines that inspect thread creation.
          CASCADE_UPDATE=off      Disable checking for updates at startup.
          CASCADE_UPDATE_FORCE=1  Install the latest release even if it is not newer.
          CASCADE_UPDATE_REPO     owner/name to update from.
          CASCADE_UPDATE_API      API root to update from.
          CASCADE_UPDATE_TOKEN    Credential to use instead of asking git.
          CASCADE_UPDATE_LOG      File to append what each update attempt came to.

        Preferences are stored in %APPDATA%\Cascade\settings.json and can be exported and
        imported from File > Settings. Recent files and the last filter file are kept
        separately in state.json, which is never exported.
        """;

    /// <summary>A headless run has nobody to dismiss anything, so a UI exception must end it. Left to
    /// itself WinForms puts up its error dialog and waits for ever, which reads as a hung harness with no
    /// clue as to why - and on a hidden desktop the dialog cannot even be seen.</summary>
    private static void FailFastOnUiException()
        => Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

    /// <summary>
    /// Everything a window needs before one is built. Beyond the generated
    /// <see cref="ApplicationConfiguration"/> call this raises the double-buffering limit, which is the
    /// single largest cost in a full-window repaint if it is left alone.
    /// <para><b>WinForms caches ONE off-screen buffer, and only up to
    /// <see cref="BufferedGraphicsContext.MaximumBuffer"/>, which defaults to 225 x 96 pixels.</b> Every
    /// control larger than that - i.e. the log view on any real screen - gets a freshly allocated bitmap
    /// that is thrown away again at the end of each frame. MEASURED on a 3840 x 2054 view: a control that
    /// painted NOTHING AT ALL still cost 10.4 ms a frame, which fell to 4.2 ms (the blit, which is real
    /// work) once the limit covered the window.</para>
    /// The size is a cap, not an allocation: the buffer that is actually kept is the size of the largest
    /// control that asks for one, so this costs one screen-sized bitmap (~32 MB at 4K) and no more.
    /// </summary>
    internal static void InitialiseUi()
    {
        ApplicationConfiguration.Initialize();
        var desktop = SystemInformation.VirtualScreen;
        if (desktop.Width > 0 && desktop.Height > 0)
            BufferedGraphicsManager.Current.MaximumBuffer = new Size(desktop.Width + 1, desktop.Height + 1);
    }

    private static void LogCrash(string path, Exception? ex)
    {
        try { File.WriteAllText(path, DateTime.Now + Environment.NewLine + ex); } catch { /* ignore */ }
    }
}