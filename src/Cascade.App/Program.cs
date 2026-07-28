using System.Runtime.InteropServices;
using System.Text;

namespace Cascade.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
        try
        {
            Application.Run(new MainForm(settings, args));
        }
        catch (Exception ex)
        {
            LogCrash(crashLog, ex);
            throw;
        }
        return 0;
    }

    private static void LogCrash(string path, Exception? ex)
    {
        try { File.WriteAllText(path, DateTime.Now + Environment.NewLine + ex); } catch { /* ignore */ }
    }
}