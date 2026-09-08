using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Cascade.AppTests;

/// <summary>
/// The single STA thread every check runs on.
///
/// <para>WinForms controls belong to the thread that created them, and the app itself runs on one STA
/// thread, so reproducing that exactly is both the closest thing to production and the simplest thing to
/// reason about. Tests marshal onto it and their exceptions are marshalled back with the original stack
/// intact, so a failure reads the same as any other xUnit failure.</para>
///
/// <para>Deliberately ONE thread rather than one per test class. A good deal of what these checks look at
/// is process-wide - the settings directory, the automation switch, the hang watchdog, this process's GDI
/// handle count, and <c>LineGridControl.AnyViewOwesAFrameForTesting</c>, which <see cref="Checks.Pump"/>
/// waits on - so two windows being driven at once would make several groups answer about each other.
/// Concurrency is bought at the level above instead: this assembly is its own CI job, running beside the
/// engine tests rather than after them.</para>
/// </summary>
internal static class Sta
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly Lazy<Thread> Worker = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Thread Start()
    {
        var ready = new ManualResetEventSlim();
        ExceptionDispatchInfo? startupFailure = null;
        var thread = new Thread(() =>
        {
            // Set before signalling either way: a Prepare that throws must surface as that exception on the
            // first test, not as a test host that waits for ever on a thread which is never coming.
            try { Prepare(); }
            catch (Exception ex) { startupFailure = ExceptionDispatchInfo.Capture(ex); }
            finally { ready.Set(); }

            foreach (var job in Work.GetConsumingEnumerable()) job();
        })
        {
            IsBackground = true,          // never hold the test host open
            Name = "Cascade app checks (STA)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        startupFailure?.Throw();
        return thread;
    }

    /// <summary>Everything the app does to its UI thread before it builds a window. Skipping any of it
    /// would measure a differently configured WinForms from the one that ships - the DPI mode decides every
    /// pixel size, and the buffer cap decides what a repaint costs.</summary>
    private static void Prepare()
    {
        Cascade.App.Program.InitialiseUi();

        // A headless run has nobody to dismiss anything. Left alone, WinForms puts up its error dialog and
        // waits for ever, which reads as a hung test host with no clue as to why.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
    }

    // THESE CHECKS STILL TAKE THE KEYBOARD, and it is worth writing down what was tried, because the
    // remedies all look obvious and none of them works.
    //
    // MEASURED with scripts/Measure-Focus.ps1, which samples GetForegroundWindow alongside a run: the
    // foreground belongs to this process for 86% of one - about a hundred windows, every one of them
    // invisible, each taking the keystrokes meant for whatever the developer was actually doing. Showing a
    // window activates it; zero opacity does not change that, and neither does parking it past the last
    // monitor (Hidden.Show does both anyway, so nothing is ever SEEN, and no stray click can land on one).
    // The UI suite is the same at 82%, for the same reason one level up - it launches the real executable.
    // On CI it costs nothing, because nobody is typing there.
    //
    //   * SetThreadDesktop onto a desktop of our own: fails with ERROR_BUSY (170) on a .NET STA thread even
    //     as its first act. Starting a thread as STA initialises the apartment, which creates the hidden
    //     OLE window - and a window is exactly what that API refuses to move a thread away from. Confirmed
    //     on a bare probe with nothing else on the thread.
    //   * Starting the whole test host on such a desktop (STARTUPINFO.lpDesktop, which is the technique
    //     that does work for a child process): the run began normally and then aborted part way in.
    //   * WS_EX_NOACTIVATE on every window, applied through a CBT hook at HCBT_CREATEWND: no measurable
    //     difference (88% against 90%), and it broke a check. WinForms sets its own extended styles from
    //     CreateParams and shows with SW_SHOW regardless.
    //
    // So it is left alone deliberately, rather than half-fixed. What is worth trying next is the one thing
    // not tried: giving the product's DialogBase and MainForm a ShowWithoutActivation of their own, gated
    // on a test-only switch - the supported way to say this, and the only one the framework honours.

    /// <summary>Runs <paramref name="job"/> on the STA thread and waits for it. An exception it throws is
    /// rethrown here with its original stack trace, so xUnit reports the failure where it happened.</summary>
    public static void Run(Action job)
    {
        _ = Worker.Value;   // starts the thread, and surfaces a failure to configure WinForms at all
        var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;

        Work.Add(() =>
        {
            try { job(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });

        done.Wait();
        failure?.Throw();
    }

    public static T Run<T>(Func<T> job)
    {
        T result = default!;
        Run(() => { result = job(); });
        return result;
    }
}
