namespace Cascade.Core.Updating;

/// <summary>
/// Serialises updating between every process using one installed copy, so only one of them downloads and
/// installs.
///
/// A lock FILE in the install directory rather than a named mutex: it is keyed to the location by
/// construction (no path canonicalising, no hashing), the kernel releases it if the holder is killed, it
/// has no thread affinity - the update path is async and a Mutex cannot be released on a different thread -
/// and on a network share the server enforces it, where a machine-scoped mutex would not stop a second
/// machine updating the same copy.
///
/// Losing the race is not an error: it just means someone else is doing it this time.
/// </summary>
public sealed class InstallLock : IDisposable
{
    private readonly FileStream _stream;

    private InstallLock(FileStream stream) => _stream = stream;

    public static string PathFor(string exePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath))!,
                        Path.GetFileNameWithoutExtension(exePath) + ".update.lock");

    /// <summary>Null when another process holds it, or the directory cannot be written to.</summary>
    public static InstallLock? TryAcquire(string exePath)
    {
        try
        {
            return new InstallLock(new FileStream(PathFor(exePath), FileMode.Create, FileAccess.ReadWrite,
                                                  FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* the file goes with the handle either way */ }
    }
}
