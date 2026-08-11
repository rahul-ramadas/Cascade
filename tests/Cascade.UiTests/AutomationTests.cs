using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Cascade.UiTests;

/// <summary>
/// Guards the fix for a freeze that was root-caused from a user-mode dump and then a kernel dump: WinForms
/// calls UiaReturnRawElementProvider while destroying EVERY window, the first such call in a process makes
/// COM stand up worker threads, and on a machine whose security software inspects thread creation that one
/// call costs seconds. The app pays it once at startup on a thread of its own, so no window the user closes
/// ever has to.
/// <para>
/// These run the app the way it ships - answering no automation - so they drive it by keyboard and read
/// what they need from the process itself rather than through a client.
/// </para>
/// </summary>
public class AutomationTests
{
    [Fact]
    public void The_automation_stack_is_brought_up_at_startup_by_the_app_itself()
    {
        using var app = Launch();
        Assert.True(WaitFor(() => Loaded(app.Process, "UIAutomationCore.dll"), 20_000),
            "UIAutomationCore.dll never loaded. Nothing warmed it up at startup, so the first window the "
            + "user closes will be the thing that does - which is the freeze this guards against.");
    }

    [Fact]
    public void Closing_a_dialog_costs_no_worker_thread()
    {
        using var app = Launch();
        Assert.True(WaitFor(() => Loaded(app.Process, "UIAutomationCore.dll"), 20_000), "the warm-up never ran");
        TakeForeground(app.Process);

        var before = ThreadIds(app.Process);
        for (int i = 1; i <= 3; i++)
        {
            OpenGoTo();
            Assert.True(WaitFor(() => TopLevelWindows(app.Process) >= 2, 5_000),
                $"the Go To dialog did not open on pass {i}; the check would have proved nothing.");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Assert.True(WaitFor(() => TopLevelWindows(app.Process) == 1, 5_000),
                $"the Go To dialog did not close on pass {i}.");
        }

        // Threads started in COM are the ones that cost: that is the call WinForms makes on the way out of
        // a window, and creating a thread is what the affected machines are slow at.
        string[] fresh = NewThreadsFrom(app.Process, before, "combase.dll");
        Assert.True(fresh.Length == 0,
            "closing a dialog started " + fresh.Length + " COM worker thread(s) (" + string.Join(", ", fresh)
            + "). The startup warm-up is not covering the path a dialog takes, so on a machine that "
            + "inspects thread creation this gesture is the one that freezes.");
    }

    // ---- driving ----

    private static void OpenGoTo()
    {
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        try { Keyboard.Type(VirtualKeyShort.KEY_G); }
        finally { Keyboard.Release(VirtualKeyShort.CONTROL); }
    }

    /// <summary>A key sent to the wrong window proves nothing, so this insists rather than hopes.</summary>
    private static void TakeForeground(Process p)
    {
        Assert.True(WaitFor(() =>
        {
            SetForegroundWindow(p.MainWindowHandle);
            _ = GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            return pid == (uint)p.Id;
        }, 10_000), "Cascade would not come to the front, so no keystroke would have reached it.");
    }

    // ---- reading the process ----

    private static bool Loaded(Process p, string module)
    {
        try
        {
            p.Refresh();
            return p.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, module, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static HashSet<int> ThreadIds(Process p)
    {
        p.Refresh();
        return p.Threads.Cast<ProcessThread>().Select(t => t.Id).ToHashSet();
    }

    private static string[] NewThreadsFrom(Process p, HashSet<int> before, string module)
    {
        p.Refresh();
        var (start, end) = Span(p, module);
        var found = new List<string>();
        foreach (ProcessThread t in p.Threads)
        {
            if (before.Contains(t.Id)) continue;
            long at;
            try { at = t.StartAddress.ToInt64(); } catch { continue; }
            if (at >= start && at < end) found.Add($"{t.Id} at {module}+0x{at - start:x}");
        }
        return found.ToArray();
    }

    private static (long Start, long End) Span(Process p, string module)
    {
        foreach (ProcessModule m in p.Modules)
            if (string.Equals(m.ModuleName, module, StringComparison.OrdinalIgnoreCase))
                return (m.BaseAddress.ToInt64(), m.BaseAddress.ToInt64() + m.ModuleMemorySize);
        return (0, 0);
    }

    private static int TopLevelWindows(Process p)
    {
        int count = 0;
        EnumWindows((window, param) =>
        {
            uint owner = GetWindowThreadProcessId(window, out uint pid);
            if (owner != 0 && pid == (uint)p.Id && IsWindowVisible(window)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private static bool WaitFor(Func<bool> condition, int millis)
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < millis; Thread.Sleep(100))
            if (condition()) return true;
        return condition();
    }

    // ---- the app under test, launched as it ships ----

    private static Run Launch()
    {
        string log = TestData.WriteLogFile();
        string cfg = CascadeApp.NewSettingsDir();
        Directory.CreateDirectory(cfg);
        var psi = new ProcessStartInfo(TestData.AppExe(), $"\"{log}\"") { UseShellExecute = false };
        psi.EnvironmentVariables["CASCADE_SETTINGS_DIR"] = cfg;
        psi.EnvironmentVariables["CASCADE_UPDATE"] = "off";
        // Deliberately NOT setting CASCADE_AUTOMATION: these tests want what a user gets.
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Cascade.exe.");
        var run = new Run(p, log, cfg);
        if (!WaitFor(() => { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; }, 30_000))
        {
            run.Dispose();
            throw new InvalidOperationException("Cascade's window never appeared.");
        }
        Thread.Sleep(1500); // let the window settle before anything is typed at it
        return run;
    }

    private sealed class Run(Process process, string log, string settingsDir) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { Process.Dispose(); } catch { /* ignore */ }
            try { File.Delete(log); } catch { /* ignore */ }
            try { Directory.Delete(settingsDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
