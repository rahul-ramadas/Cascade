using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// Keeps Windows UI Automation out of the app, by answering WM_GETOBJECT itself - with "no object" -
/// before WinForms can hand out a provider.
/// <para>
/// This is not tidiness. WinForms only tears a provider down if it created one, and that teardown
/// (<c>Control.ReleaseUiaProvider</c> during WM_DESTROY) makes COM stand up an in-process RPC worker
/// thread. ROOT-CAUSED from a user-mode dump plus a kernel dump: on a managed machine the security
/// software inspects thread creation inside NtCreateThreadEx, so closing a dialog stalls the UI thread
/// for seconds. No provider, no teardown, no thread, no stall.
/// </para>
/// <para>
/// Off means the window is opaque to screen readers too, so it is a real cost - hence the setting and
/// the variable. The UI tests drive the app through automation and switch it back on.
/// </para>
/// </summary>
internal sealed class Automation : NativeWindow
{
    public const string Variable = "CASCADE_AUTOMATION";

    private const int WmGetObject = 0x003D;

    /// <summary>The two things a client asks a window for: the UI Automation provider, and the older
    /// MSAA object that UI Automation falls back to and bridges. Refusing only the first would leave
    /// WinForms building an accessible object anyway, which is what arms the teardown.</summary>
    private const int UiaRootObjectId = -25;
    private const int ObjIdClient = -4;

    private static bool? _wanted;

    /// <summary>Whether automation is allowed. Read before any window exists, so a window can be hooked
    /// while its handle is still to come.</summary>
    internal static bool Wanted => _wanted ??= FromVariable() ?? false;

    /// <summary>Applies the user's preference, unless the variable has already spoken for this run.</summary>
    internal static void Configure(bool fromSettings) => _wanted = FromVariable() ?? fromSettings;

    private static bool? FromVariable() =>
        Environment.GetEnvironmentVariable(Variable) switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

    /// <summary>Silences the window and everything that is ever put in it.</summary>
    internal static void Suppress(Control root)
    {
        if (Wanted) return;
        Hook(root);
    }

    /// <summary>
    /// Brings the automation stack into the process at startup, on a thread of our own, so the UI thread
    /// never has to.
    /// <para>
    /// MEASURED, whatever <see cref="Wanted"/> says and with no client attached at all: WinForms calls
    /// UiaReturnRawElementProvider while destroying EVERY window, and the first such call in a process
    /// makes COM stand up two worker threads. Two on the first window closed, none on any after it. On a
    /// machine that inspects thread creation that first one costs seconds, and without this it would be
    /// spent closing whichever dialog the user happened to open first.
    /// </para>
    /// <para>Refusing to answer clients does not avoid it - it only moves it later, onto the very gesture
    /// that has someone waiting on it. This is the part that fixes the freeze; the refusing is a
    /// preference.</para>
    /// </summary>
    internal static Thread PayTheStartupCost()
    {
        var thread = new Thread(static () =>
        {
            try
            {
                BeforeWarmUpForTesting?.Invoke();
                using var throwaway = new Form();
                _ = throwaway.Handle;   // and disposing it destroys the window, which is what does the work
            }
            catch { /* best effort: this is an optimisation, not a requirement */ }
        })
        {
            IsBackground = true,
            Name = "Cascade automation warm-up",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>Lets a check make the warm-up outlast the call that started it, which is the only way to
    /// tell apart "started it" from "waited for it" on a machine where it is instant.</summary>
    internal static Action? BeforeWarmUpForTesting;

    private static void Hook(Control c)
    {
        if (c.IsHandleCreated) Attach(c);
        c.HandleCreated += (s, _) => Attach((Control)s!);
        c.ControlAdded += (_, e) => { if (e.Control is { } added) Hook(added); };
        foreach (Control child in c.Controls) Hook(child);
    }

    private static void Attach(Control c)
    {
        var subclass = new Automation();
        subclass.AssignHandle(c.Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmGetObject && ((int)m.LParam == UiaRootObjectId || (int)m.LParam == ObjIdClient))
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Lets a check drive both answers, or clear the decision so the default is the one under
    /// test, without restarting the process.</summary>
    internal static IDisposable ForTesting(bool? wanted)
    {
        bool? previous = _wanted;
        _wanted = wanted;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => _wanted = previous;
    }
}
