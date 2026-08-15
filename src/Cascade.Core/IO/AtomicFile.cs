using System.Text;

namespace Cascade.Core.IO;

/// <summary>
/// Writes a file so that being killed part-way cannot leave a half-written one. The content goes to a
/// temporary file beside the target and is swapped in with a single rename, so a reader afterwards sees
/// either the whole old file or the whole new one - never a truncated prefix, which is what a plain
/// <see cref="File.WriteAllText(string, string?)"/> leaves behind if the process dies mid-write.
///
/// Deliberately no flush-to-disk: surviving a killed process only needs the bytes to reach the file
/// cache, and an fsync per save would put a disk round trip on the UI thread for the sake of surviving
/// a power cut instead.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
        => Write(path, writer => writer.Write(contents), encoding);

    /// <summary>The same swap, for content too big to hold as a string - an export of a whole log runs to
    /// gigabytes. The writer is handed out rather than the text handed in, so nothing is ever fully in
    /// memory; throwing out of <paramref name="write"/> (which is how a cancelled export leaves) takes the
    /// half-written temporary with it and leaves the target as it was.</summary>
    public static void Write(string path, Action<TextWriter> write, Encoding? encoding = null)
    {
        string full = Path.GetFullPath(path);
        // Beside the target, so the rename stays on one volume and cannot degrade into a copy.
        string temp = full + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, false, encoding ?? new UTF8Encoding(false)))
                write(writer);
            Swap(temp, full);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* nothing else to do */ }
            throw;
        }
    }

    /// <summary>Anything holding the target open - a virus scanner, a backup agent, an editor - denies the
    /// delete the swap needs, and those holds are usually gone in a moment. Brief enough to stay on a UI
    /// thread; the caller still sees the failure if it does not clear.</summary>
    private static void Swap(string temp, string target)
    {
        for (int attempt = 5; ; attempt--)
        {
            try { File.Move(temp, target, overwrite: true); return; }
            catch (Exception e) when (attempt > 1 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }
}
