using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Cascade.App;

/// <summary>How much of the process a hang dump carries.</summary>
internal enum DumpDetail
{
    /// <summary>Thread stacks and modules. Small, and enough to say WHERE the window is stuck.</summary>
    Stacks,
    /// <summary>The above plus every page the process wrote itself, which is what holds the managed heap.
    /// Deliberately the default: it leaves out file-backed pages, and Cascade maps whole log files, so
    /// <see cref="Everything"/> on a real trace would write gigabytes nobody can post anywhere.</summary>
    Heap,
    /// <summary>Every readable page, mapped log included. Only worth asking for on a small file.</summary>
    Everything,
}

/// <summary>
/// Watches the UI thread from a background thread and, when it stops answering for long enough, leaves
/// evidence behind: a report naming what the stall looked like, and a minidump beside it. Off unless asked
/// for - it exists for the machine where a freeze reproduces and the developer's does not.
///
/// The UI thread proves it is alive by calling <see cref="Beat"/> from the refresh timer. That is a stronger
/// signal than asking the window whether it answers, because a thread wedged inside one long operation stops
/// pumping messages entirely, which is exactly the case worth catching.
///
/// KNOWN BLIND SPOT, and the report says so: a blocking garbage collection suspends this thread too, so a
/// pause the GC caused is only noticed once it is over and the dump shows whatever ran next. The GC pause
/// total recorded either side of the stall is what tells that case apart from a genuinely stuck thread.
/// </summary>
internal sealed class HangWatchdog : IDisposable
{
    private const int SampleMs = 500;
    private const int MaxDumps = 3;

    private readonly Form _form;
    private readonly int _thresholdMs;
    private readonly string _dir;
    private readonly DumpDetail _detail;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;

    private long _lastBeat;
    private IntPtr _window;
    private Health _healthy;
    private bool _reported;
    private bool _stopped;
    private int _dumps;
    private string? _pendingReport;

    /// <summary>What the process looked like at one moment, so a stall can be described by what changed
    /// across it rather than by absolute numbers nobody can calibrate.</summary>
    private readonly record struct Health(TimeSpan GcPause, int Gen2, uint PageFaults, long WorkingSet);

    private HangWatchdog(Form form, int thresholdMs, string dir, DumpDetail detail)
    {
        _form = form;
        _thresholdMs = thresholdMs;
        _dir = dir;
        _detail = detail;
        _lastBeat = Environment.TickCount64;
        _healthy = Sample();
        _thread = new Thread(Watch) { IsBackground = true, Name = "Cascade.HangWatchdog" };
        _thread.Start();
    }

    /// <summary>Starts watching, or returns null when nobody asked for it.</summary>
    public static HangWatchdog? Start(Form form, AppSettings settings)
    {
        if (!IsWanted(settings)) return null;
        string dir = Folder;
        try { Directory.CreateDirectory(dir); } catch { return null; }
        return new HangWatchdog(form, SecondsToWait(settings) * 1000, dir, WantedDetail());
    }

    /// <summary>Where dumps and reports are written.</summary>
    internal static string Folder =>
        Environment.GetEnvironmentVariable("CASCADE_HANG_DIR") is { Length: > 0 } d ? d : Path.GetTempPath();

    /// <summary>The environment wins over the preference, so the watchdog can be turned on for one run on a
    /// machine whose settings must not be disturbed - and off again when it turns out to be the noisy one.</summary>
    internal static bool IsWanted(AppSettings settings) =>
        Environment.GetEnvironmentVariable("CASCADE_HANG_WATCHDOG") switch
        {
            "1" => true,
            "0" => false,
            _ => settings.HangWatchdog,
        };

    /// <summary>Five seconds by default because that is the rule Windows itself uses to call a window not
    /// responding - so it fires when, and only when, the user can see something is wrong.</summary>
    internal static int SecondsToWait(AppSettings settings)
    {
        int seconds = settings.HangWatchdogSeconds;
        if (int.TryParse(Environment.GetEnvironmentVariable("CASCADE_HANG_SECONDS"),
                         NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromEnv))
            seconds = fromEnv;
        return Math.Clamp(seconds, 1, 600);
    }

    internal static DumpDetail WantedDetail() =>
        Environment.GetEnvironmentVariable("CASCADE_HANG_DUMP") switch
        {
            "stacks" => DumpDetail.Stacks,
            "everything" => DumpDetail.Everything,
            _ => DumpDetail.Heap,
        };

    /// <summary>Called from the UI thread's refresh timer: the whole of what "still alive" means here.</summary>
    public void Beat()
    {
        Volatile.Write(ref _lastBeat, Environment.TickCount64);
        // Asking a control for its handle is only safe on the thread that owns it, so it is taken here.
        if (Volatile.Read(ref _window) == IntPtr.Zero && _form.IsHandleCreated)
            Volatile.Write(ref _window, _form.Handle);
    }

    /// <summary>The dump written since this was last asked, once. Lets the window say so when it comes back,
    /// rather than leaving the evidence to be discovered.</summary>
    public string? TakeReport() => Interlocked.Exchange(ref _pendingReport, null);

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _stop.Set();
        try { _thread.Join(2000); } catch { /* exiting anyway */ }
        _stop.Dispose();
    }

    private void Watch()
    {
        while (!_stop.Wait(SampleMs))
        {
            long stalled = Environment.TickCount64 - Volatile.Read(ref _lastBeat);
            if (stalled < _thresholdMs)
            {
                _reported = false;
                _healthy = Sample();   // the last moment the window was known to be answering
                continue;
            }
            if (_reported || _dumps >= MaxDumps) continue;
            _reported = true;
            _dumps++;
            try { Capture(stalled); }
            catch (Exception ex) { TryWrite(Path.Combine(_dir, "cascade_hang.log"), "watchdog failed: " + ex + "\n"); }
        }
    }

    private void Capture(long stalledMs)
    {
        var now = Sample();
        // The ordinal is part of the name because two hangs can fall in the same second, and without it the
        // second dump would quietly overwrite the first.
        string stem = Path.Combine(_dir,
            $"cascade_hang_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}_{_dumps}");

        // The report goes first: if writing a dump of a process this big fails, this is still enough to say
        // what kind of stall it was.
        string report = Describe(stalledMs, now);
        TryWrite(stem + ".txt", report);
        TryWrite(Path.Combine(_dir, "cascade_hang.log"), report + "\n");

        string dump = stem + ".dmp";
        Volatile.Write(ref _pendingReport, Path.GetFileName(WriteDump(dump) ? dump : stem + ".txt"));
    }

    private string Describe(long stalledMs, Health now)
    {
        var pause = now.GcPause - _healthy.GcPause;
        int gen2 = now.Gen2 - _healthy.Gen2;
        uint faults = now.PageFaults - _healthy.PageFaults;
        IntPtr window = Volatile.Read(ref _window);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Cascade stopped answering for {stalledMs:N0} ms (limit {_thresholdMs:N0} ms)\n");
        sb.Append(CultureInfo.InvariantCulture, $"  when          {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append(CultureInfo.InvariantCulture, $"  version       {AppInfo.InformationalVersion}\n");
        sb.Append(CultureInfo.InvariantCulture, $"  process       {Environment.ProcessId}, {Environment.ProcessorCount} cores\n");
        sb.Append(CultureInfo.InvariantCulture, $"  window        {(window == IntPtr.Zero ? "not created" : IsHungAppWindow(window) ? "reported hung by Windows" : "still answering Windows")}\n");
        sb.Append(CultureInfo.InvariantCulture, $"  gc pause      {pause.TotalMilliseconds:N0} ms during the stall, {now.GcPause.TotalMilliseconds:N0} ms since launch\n");
        sb.Append(CultureInfo.InvariantCulture, $"  gc runs       gen2 +{gen2} during the stall\n");
        sb.Append(CultureInfo.InvariantCulture, $"  managed heap  {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB\n");
        sb.Append(CultureInfo.InvariantCulture, $"  page faults   +{faults:N0} during the stall\n");
        sb.Append(CultureInfo.InvariantCulture, $"  working set   {now.WorkingSet / (1024 * 1024):N0} MB\n");
        sb.Append(CultureInfo.InvariantCulture, $"  dump          {_dumps} of at most {MaxDumps} this session\n");

        // The one reading a dump cannot do for itself: this thread is suspended by a blocking collection, so
        // when the pause accounts for the stall the stacks in the dump are of whatever ran after it.
        sb.Append(pause.TotalMilliseconds >= stalledMs * 0.5
            ? "  VERDICT       garbage collection accounts for most of the stall; the stacks in the dump are of whatever ran AFTER it\n"
            : "  VERDICT       the UI thread was busy or blocked; its stack in the dump is where it stopped\n");
        return sb.ToString();
    }

    private bool WriteDump(string path)
    {
        // Writing a dump suspends every other thread until it is done, so this lengthens the very freeze it
        // is recording. That is the price of catching the stack in the act, and the app is already stuck.
        uint type = _detail switch
        {
            DumpDetail.Stacks => MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules,
            DumpDetail.Everything => MiniDumpWithFullMemory | MiniDumpWithDataSegs | MiniDumpWithHandleData
                                     | MiniDumpWithUnloadedModules | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo,
            _ => MiniDumpWithPrivateReadWriteMemory | MiniDumpWithDataSegs | MiniDumpWithHandleData
                 | MiniDumpWithUnloadedModules | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo,
        };
        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            if (MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, file.SafeFileHandle, type,
                                  IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                return true;
        }
        catch { /* fall through to the delete below */ }
        try { File.Delete(path); } catch { /* nothing else to try */ }
        return false;
    }

    private static Health Sample()
    {
        uint faults = 0;
        long workingSet = 0;
        var counters = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb))
        {
            faults = counters.PageFaultCount;
            workingSet = (long)counters.WorkingSetSize;
        }
        return new Health(GC.GetTotalPauseDuration(), GC.CollectionCount(2), faults, workingSet);
    }

    private static void TryWrite(string path, string text)
    {
        try { File.AppendAllText(path, text); } catch { /* best-effort: this is the diagnostic, not the app */ }
    }

    private const uint MiniDumpWithDataSegs = 0x0001;
    private const uint MiniDumpWithFullMemory = 0x0002;
    private const uint MiniDumpWithHandleData = 0x0004;
    private const uint MiniDumpWithUnloadedModules = 0x0020;
    private const uint MiniDumpWithPrivateReadWriteMemory = 0x0200;
    private const uint MiniDumpWithFullMemoryInfo = 0x0800;
    private const uint MiniDumpWithThreadInfo = 0x1000;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeHandle file, uint type,
                                                 IntPtr exception, IntPtr userStream, IntPtr callback);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}
