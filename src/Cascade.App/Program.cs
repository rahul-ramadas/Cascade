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
        // appears on screen.
        if (args.Length > 0 && args[0].Equals("--cleanup", StringComparison.OrdinalIgnoreCase))
            return args.Length >= 3 && int.TryParse(args[1], out int pid)
                ? UpdateInstaller.RunCleanup(pid, args[2])
                : 2;

        // Also how a freshly downloaded update proves it is a working build before it is allowed to replace
        // the running one.
        if (args.Length > 0 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            Console.WriteLine(AppInfo.InformationalVersion);
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(-1); // attach to the launching console so output is visible
            return SelfTest.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0].Equals("--screens", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            ApplicationConfiguration.Initialize();
            return UiShots.Run(args.Skip(1).ToArray());
        }

        ApplicationConfiguration.Initialize();

        string crashLog = Path.Combine(Path.GetTempPath(), "cascade_crash.log");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(crashLog, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(crashLog, e.ExceptionObject as Exception);

        var settings = AppSettings.Load();

        // Tidy up after previous runs first, and do it whether or not updating is switched on: turning
        // updates off must not strand a superseded executable or a half-finished download forever.
        try { UpdateInstaller.Sweep(AppInfo.ExePath, AppInfo.Version); } catch { /* best effort */ }

        // Checked once, at startup, on a background thread. The swap itself happens after the message loop
        // ends so that a session spent reading a log is never interrupted by it.
        var updater = AppUpdater.Create();
        using var updateCts = new CancellationTokenSource();
        if (updater is not null) _ = Task.Run(() => updater.CheckAsync(updateCts.Token));

        try
        {
            Application.Run(new MainForm(settings, args, updater));
        }
        catch (Exception ex)
        {
            LogCrash(crashLog, ex);
            throw;
        }
        finally
        {
            updateCts.Cancel();
            updater?.ApplyPending();
        }
        return 0;
    }


    private static void LogCrash(string path, Exception? ex)
    {
        try { File.WriteAllText(path, DateTime.Now + Environment.NewLine + ex); } catch { /* ignore */ }
    }
}