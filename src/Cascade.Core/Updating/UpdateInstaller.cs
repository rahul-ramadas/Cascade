using System.Diagnostics;

namespace Cascade.Core.Updating;

/// <summary>
/// The on-disk half of updating.
///
/// Installing is ONE call: <see cref="File.Replace(string,string,string)"/> puts the new build at the
/// executable's name and moves the running image aside in the same operation, so there is never an instant
/// with no executable. Windows permits this on a running image where an overwriting Move fails with
/// ACCESS_DENIED, and the process carries on from the moved-aside file. That file cannot be deleted until
/// every process running it has exited, which is what the <c>--cleanup</c> helper is for.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>How long the cleanup helper waits for the old process, then for the file to be released.</summary>
    private const int WaitForExitMs = 30_000;
    private const int DeleteWindowMs = 15_000;

    private static string Stem(string exePath) => Path.GetFileNameWithoutExtension(exePath);
    private static string Dir(string exePath) => Path.GetDirectoryName(Path.GetFullPath(exePath))!;

    /// <summary>Where a verified download waits. A stable name is safe because its version is read from the
    /// file itself, so a copy left behind by a killed session can still be judged rather than guessed at.</summary>
    public static string StagedPath(string exePath) => Path.Combine(Dir(exePath), $"{Stem(exePath)}.new.exe");

    /// <summary>Where a download is written before it has been verified. Per-process: the lock makes a
    /// collision unlikely, not impossible, and a shared name once wedged updating permanently.</summary>
    public static string PartPath(string exePath)
        => Path.Combine(Dir(exePath), $"{Stem(exePath)}.new.{Environment.ProcessId}.part");

    /// <summary>Where the running image is moved when an update is installed.</summary>
    public static string OldPath(string exePath) => Path.Combine(Dir(exePath), $"{Stem(exePath)}.old.exe");

    /// <summary>
    /// The version of a build on disk, read from its version resource without running it.
    /// </summary>
    public static Version? VersionOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            string? product = info.ProductVersion?.Split('+')[0].Trim();
            if (Version.TryParse(product, out var v) || Version.TryParse(info.FileVersion, out v))
                return Normalize(v);
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Compares versions on three components only. The running build parses its version from
    /// "2026.8.1+sha" and gets Revision -1, while the same file's FileVersion reads "2026.8.1.0" and gets
    /// Revision 0 - and -1 &lt; 0, so without this every build looks older than itself.
    /// </summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// Removes what earlier runs left behind: superseded images whose processes have gone, and abandoned
    /// partial downloads. Deliberately does not touch the staged build - only the lock holder may judge
    /// that, and deleting one out from under it would throw away a good download.
    /// </summary>
    public static void Sweep(string exePath)
    {
        string dir = Dir(exePath);
        if (!Directory.Exists(dir)) return;

        foreach (string f in Safe(() => Directory.EnumerateFiles(dir, $"{Stem(exePath)}.old*.exe")))
            TryDelete(f);
        foreach (string f in Safe(() => Directory.EnumerateFiles(dir, $"{Stem(exePath)}.new.*.part")))
            TryDelete(f);
    }

    /// <summary>
    /// Installs <paramref name="stagedPath"/> under the executable's own name, returning the path the
    /// running image was moved to, or null if nothing was installed.
    /// </summary>
    public static string? Apply(string exePath, string stagedPath)
    {
        if (!File.Exists(stagedPath) || !File.Exists(exePath)) return null;

        string oldPath = FreeOldPath(exePath);
        try
        {
            File.Replace(stagedPath, exePath, oldPath, ignoreMetadataErrors: true);
            return oldPath;
        }
        catch { return null; }
    }

    /// <summary>A backup name that is free. The usual one may still be in use by a process from an earlier
    /// generation, and Windows will not let that file be replaced while it runs.</summary>
    private static string FreeOldPath(string exePath)
    {
        string preferred = OldPath(exePath);
        if (!File.Exists(preferred) || TryDelete(preferred)) return preferred;
        return Path.Combine(Dir(exePath), $"{Stem(exePath)}.old-{DateTime.UtcNow.Ticks}.exe");
    }

    /// <summary>Starts the installed executable just long enough to delete an image that is still in use.
    /// No window: the cleanup branch returns before any UI is created.</summary>
    public static void LaunchCleanup(string exePath, string oldPath)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Dir(exePath)
            };
            psi.ArgumentList.Add("--cleanup");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(oldPath);
            Process.Start(psi);
        }
        catch { /* the next startup sweep will get it */ }
    }

    /// <summary>
    /// Deletes superseded images as this process exits, handing over to a helper only for those that are
    /// still in use - most likely by this process itself, which cannot delete its own image.
    /// </summary>
    public static void CleanUpSupersededImages(string exePath)
    {
        if (!File.Exists(exePath)) return;
        foreach (string f in Safe(() => Directory.EnumerateFiles(Dir(exePath), $"{Stem(exePath)}.old*.exe")))
            if (!TryDelete(f)) LaunchCleanup(exePath, f);
    }

    /// <summary>True if <paramref name="path"/> is one this app may be asked to delete on the command line.</summary>
    public static bool IsSupersededImagePath(string exePath, string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), Dir(exePath), StringComparison.OrdinalIgnoreCase))
                return false;
            string name = Path.GetFileName(full);
            return name.StartsWith($"{Stem(exePath)}.old", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The <c>--cleanup</c> entry point: wait for the old app to exit, then delete its image.</summary>
    public static int RunCleanup(int pid, string oldPath)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(WaitForExitMs);
        }
        catch { /* already gone, which is the case we are waiting for */ }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < DeleteWindowMs)
        {
            if (!File.Exists(oldPath) || TryDelete(oldPath)) return 0;
            Thread.Sleep(50);
        }
        return 1;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; } catch { return false; }
    }

    private static IEnumerable<string> Safe(Func<IEnumerable<string>> get)
    {
        try { return get().ToList(); } catch { return Array.Empty<string>(); }
    }
}
