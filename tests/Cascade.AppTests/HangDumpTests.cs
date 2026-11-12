using System.Runtime.InteropServices;
using System.Text;
using Cascade.App;

namespace Cascade.AppTests;

/// <summary>
/// Recording a hang has to come back. The watchdog's dump used to be taken of the live process, which dbghelp
/// does by holding every other thread still until it has finished - and when one of them had been stopped
/// inside the loader lock, which dbghelp then needed, it waited for ever and took the whole process with it.
/// That wedged the test host on CI over and over, in the check that records a real stall.
/// </summary>
public class HangDumpTests
{
    [Fact]
    public void A_dump_taken_while_other_threads_load_libraries_comes_back()
    {
        // Loading and freeing a library, and starting and ending a thread, are what take the loader lock, and a
        // busy app does both all the time. Four threads doing nothing else make it all but certain that a dump
        // which holds threads still catches one of them inside it: the live-process dump never came back from
        // its FIRST attempt under this. Which also means that if that comes back, this does not fail - it
        // hangs, with everything in the test host stopped, and it is --blame-hang that names it.
        string library = Path.Combine(Environment.SystemDirectory, "dbgeng.dll");
        using var stop = new CancellationTokenSource();
        var churn = new List<Thread>();
        for (int i = 0; i < 2; i++)
            churn.Add(new Thread(() => { while (!stop.IsCancellationRequested) NativeLibrary.Free(NativeLibrary.Load(library)); }));
        for (int i = 0; i < 2; i++)
            churn.Add(new Thread(() => { while (!stop.IsCancellationRequested) { var t = new Thread(() => { }); t.Start(); t.Join(); } }));
        churn.ForEach(t => { t.IsBackground = true; t.Start(); });

        string dir = Path.Combine(Path.GetTempPath(), "cascade_hangdump_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 5; i++)
            {
                string path = Path.Combine(dir, $"stall_{i}.dmp");
                Assert.True(HangWatchdog.WriteDumpForTesting(path, DumpDetail.Stacks, out string failure), failure);

                byte[] head = new byte[4];
                using (var f = File.OpenRead(path)) f.ReadExactly(head);
                Assert.Equal("MDMP", Encoding.ASCII.GetString(head));
            }
        }
        finally
        {
            stop.Cancel();
            churn.ForEach(t => t.Join());
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
