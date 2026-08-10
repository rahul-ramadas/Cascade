using System.ComponentModel;
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
    // Worst-case detection is the limit plus one of these, so it is kept well under the limit itself - a
    // stall has to still be going when the dump is taken, or the stacks are of whatever ran after it.
    private const int SampleMs = 250;
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

    /// <summary>Two seconds by default. Windows waits five before it calls a window not responding, but a
    /// freeze is perceivable well before the shell starts drawing over the app, and the point of this is to
    /// catch the stall a reader complains about rather than the one the desktop has already given up on.</summary>
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
            // A diagnostic that cannot record a stall must still never disturb the app it is watching.
            try { Capture(stalled); }
            catch { /* the report is written first, so most failures have already left something behind */ }
        }
    }

    private void Capture(long stalledMs)
    {
        var now = Sample();
        // The ordinal is part of the name because two hangs can fall in the same second, and without it the
        // second dump would quietly overwrite the first.
        string stem = Path.Combine(_dir,
            $"cascade_hang_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}_{_dumps}");

        // The report is written twice: once now, so a process that dies while the dump is being taken still
        // leaves evidence, and again afterwards to say what became of the dump. It is the same file both
        // times, so nothing accumulates.
        string report = stem + ".txt";
        string dump = stem + ".dmp";
        TryWrite(report, Describe(stalledMs, now, "still being taken"));
        bool wrote = WriteDump(dump, out string outcome);
        TryWrite(report, Describe(stalledMs, now, outcome));
        Volatile.Write(ref _pendingReport, Path.GetFileName(wrote ? dump : report));
    }

    private string Describe(long stalledMs, Health now, string dumpOutcome)
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
        sb.Append(CultureInfo.InvariantCulture, $"  dump          {_dumps} of at most {MaxDumps} this session - {dumpOutcome}\n");

        // The one reading a dump cannot do for itself: this thread is suspended by a blocking collection, so
        // when the pause accounts for the stall the stacks in the dump are of whatever ran after it.
        sb.Append(pause.TotalMilliseconds >= stalledMs * 0.5
            ? "  VERDICT       garbage collection accounts for most of the stall; the stacks in the dump are of whatever ran AFTER it\n"
            : "  VERDICT       the UI thread was busy or blocked; its stack in the dump is where it stopped\n");
        return sb.ToString();
    }

    private bool WriteDump(string path, out string outcome)
    {
        // Writing a dump suspends every other thread until it is done, so this lengthens the very freeze it
        // is recording. That is the price of catching the stack in the act, and the app is already stuck.
        //
        // Taking a dump of one's own process is asking dbghelp to read a heap it is itself allocating from,
        // and it fails part way often enough to matter - MEASURED at 1 run in 5. A second attempt is cheap
        // and usually lands, and thread stacks are the fallback when it does not.
        string refused = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (TryWriteDump(path, Flags(_detail), out refused))
            {
                outcome = $"{_detail} dump written ({Length(path)})"
                          + (attempt > 0 ? " on the second attempt" : "");
                return true;
            }
        }
        // Whatever turned the fuller dump down, thread stacks are a fraction of the size and ask far less of
        // the process, so they are worth one more try before giving up on the useful half of the evidence.
        if (_detail != DumpDetail.Stacks && TryWriteDump(path, Flags(DumpDetail.Stacks), out _))
        {
            outcome = $"{_detail} dump refused ({refused}); wrote thread stacks only ({Length(path)})";
            return true;
        }
        outcome = $"NO DUMP WRITTEN: {refused}";
        return false;
    }

    private static string Length(string path)
    {
        try { return $"{new FileInfo(path).Length / 1024:N0} KB"; } catch { return "size unknown"; }
    }

    private static uint Flags(DumpDetail detail) => detail switch
    {
        DumpDetail.Stacks => MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules,
        DumpDetail.Everything => MiniDumpWithFullMemory | MiniDumpWithDataSegs | MiniDumpWithHandleData
                                 | MiniDumpWithUnloadedModules | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo,
        _ => MiniDumpWithPrivateReadWriteMemory | MiniDumpWithDataSegs | MiniDumpWithHandleData
             | MiniDumpWithUnloadedModules | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo,
    };

    /// <summary>Test seam: given what a dump is being asked for, whether to refuse it. It makes the real call
    /// fail rather than standing in for the failure, so everything that follows - including reading what
    /// Windows gave as the reason - is the same code a refusal on a real machine goes through.</summary>
    internal static Func<uint, bool>? RefuseDumpForTesting;

    internal static uint FlagsForTesting(DumpDetail detail) => Flags(detail);

    private static bool TryWriteDump(string path, uint type, out string failure)
    {
        try
        {
            // Refusing still goes through dbghelp, with a handle it cannot use: the call fails for real, so
            // what follows - reading the reason Windows gives and explaining it - is the same code a refusal
            // on a real machine runs. Some dump kinds will write something even from a bad handle, hence the
            // second half of the condition.
            bool refuse = RefuseDumpForTesting?.Invoke(type) == true;
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            if (MiniDumpWriteDump(refuse ? IntPtr.Zero : GetCurrentProcess(), (uint)Environment.ProcessId,
                                  file.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) && !refuse)
            {
                failure = "";
                return true;
            }
            failure = Explain(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { failure = $"{ex.GetType().Name}: {ex.Message}"; }
        try { File.Delete(path); } catch { /* nothing else to try */ }
        return false;
    }

    /// <summary>What Windows said, in words as well as in numbers. dbghelp reports its failures as HRESULTs
    /// rather than plain codes, so those are unwrapped - otherwise the one error that actually turns up here
    /// reads as a meaningless negative number. The two worth naming are that dumping your own process is a
    /// pattern security software blocks, and that a dump of a program holding a large file is not small.</summary>
    internal static string Explain(int error)
    {
        if ((error & 0xFFFF0000u) == 0x80070000u) error &= 0xFFFF;   // HRESULT_FROM_WIN32
        string hint = error switch
        {
            5 or 299 => " - reading one's own process to dump it is a pattern security software interferes with",
            112 => " - no room left where the dump was going",
            _ => "",
        };
        return $"{new Win32Exception(error).Message} ({error}){hint}";
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
        try { File.WriteAllText(path, text); } catch { /* best-effort: this is the diagnostic, not the app */ }
    }

    private const uint MiniDumpWithDataSegs = 0x0001;
    private const uint MiniDumpWithFullMemory = 0x0002;
    private const uint MiniDumpWithHandleData = 0x0004;
    private const uint MiniDumpWithUnloadedModules = 0x0020;
    private const uint MiniDumpWithPrivateReadWriteMemory = 0x0200;
    private const uint MiniDumpWithFullMemoryInfo = 0x0800;
    private const uint MiniDumpWithThreadInfo = 0x1000;

    // By name alone these would be searched for beside the executable first, and Cascade is a single file
    // people copy about - so the folder it happens to be sitting in decides which dbghelp gets loaded.
    [DllImport("dbghelp.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeHandle file, uint type,
                                                 IntPtr exception, IntPtr userStream, IntPtr callback);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
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
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}
