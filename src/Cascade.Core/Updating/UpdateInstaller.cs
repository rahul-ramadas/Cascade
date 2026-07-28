using System.Diagnostics;

namespace Cascade.Core.Updating;

/// <summary>
/// The on-disk half of updating: where the staged and superseded executables live, and the rename dance
/// that swaps them.
///
/// Windows refuses to DELETE a running executable but happily RENAMES it (verified: delete fails with
/// ACCESS_DENIED, rename succeeds and the process keeps running from the renamed file). So an update is
/// "move the running exe out of the way, move the new one into its place" - no helper script, no reboot.
/// The superseded image cannot be deleted until its process exits, so the exiting app hands that job to a
/// short-lived <c>--cleanup</c> instance of the new exe.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>How long the cleanup helper waits for the old process, then for the file to be released.</summary>
    private const int WaitForExitMs = 30_000;
    private const int DeleteWindowMs = 15_000;

    private static string Stem(string exePath) => Path.GetFileNameWithoutExtension(exePath);
    private static string Dir(string exePath) => Path.GetDirectoryName(Path.GetFullPath(exePath))!;

    /// <summary>Where a download for <paramref name="version"/> is parked until the app exits. The version
    /// is in the file name so a staged update survives a kill without needing a sidecar to describe it.</summary>
    public static string StagedPath(string exePath, Version version)
        => Path.Combine(Dir(exePath), $"{Stem(exePath)}.update-{version}.exe");

    /// <summary>Where the running executable is renamed to when the update is applied.</summary>
    public static string OldPath(string exePath)
        => Path.Combine(Dir(exePath), $"{Stem(exePath)}.old.exe");

    /// <summary>The version encoded in a staged file name, or null if it is not one.</summary>
    public static Version? StagedVersionOf(string exePath, string candidatePath)
    {
        string name = Path.GetFileName(candidatePath);
        string prefix = Stem(exePath) + ".update-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        string middle = name[prefix.Length..^4];
        return Version.TryParse(middle, out var v) ? v : null;
    }

    /// <summary>Every staged update currently on disk, newest first.</summary>
    public static IEnumerable<(string Path, Version Version)> Staged(string exePath)
    {
        string dir = Dir(exePath);
        if (!Directory.Exists(dir)) yield break;
        var found = new List<(string, Version)>();
        foreach (string f in Directory.EnumerateFiles(dir, $"{Stem(exePath)}.update-*.exe"))
            if (StagedVersionOf(exePath, f) is { } v) found.Add((f, v));
        found.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        foreach (var x in found) yield return x;
    }

    /// <summary>
    /// Removes what previous runs left behind: the superseded executable (normally already deleted by the
    /// cleanup helper, but not if the app was killed), abandoned partial downloads, and staged updates that
    /// are no longer newer than what is running - which is exactly what a staged update becomes once it has
    /// been applied.
    /// </summary>
    public static void Sweep(string exePath, Version current)
    {
        string dir = Dir(exePath);
        if (!Directory.Exists(dir)) return;

        foreach (string f in Safe(() => Directory.EnumerateFiles(dir, $"{Stem(exePath)}.old*.exe")))
            TryDelete(f);
        foreach (string f in Safe(() => Directory.EnumerateFiles(dir, $"{Stem(exePath)}.update-*.part")))
            TryDelete(f);
        foreach (var (path, version) in Staged(exePath))
            if (version <= current) TryDelete(path);
    }

    /// <summary>The newest staged update worth applying, or null. <paramref name="force"/> accepts one that
    /// is not actually newer, which is how the update path is exercised without publishing a release.</summary>
    public static string? FindStaged(string exePath, Version current, bool force)
    {
        foreach (var (path, version) in Staged(exePath))
            if (force || version > current) return path;
        return null;
    }

    /// <summary>
    /// Swaps <paramref name="stagedPath"/> into <paramref name="exePath"/>, returning the path the running
    /// image was moved to (to be deleted once this process exits), or null if the swap did not happen.
    /// If the second move fails the first is rolled back, so a failed update never leaves no executable.
    /// </summary>
    public static string? Apply(string exePath, string stagedPath)
    {
        if (!File.Exists(stagedPath)) return null;

        string oldPath = OldPath(exePath);
        if (File.Exists(oldPath) && !TryDelete(oldPath))
            oldPath = Path.Combine(Dir(exePath), $"{Stem(exePath)}.old-{DateTime.UtcNow.Ticks}.exe");

        try { File.Move(exePath, oldPath); }
        catch { return null; }

        try { File.Move(stagedPath, exePath); }
        catch
        {
            // Put the running image back; better an un-updated app than a missing one.
            try { File.Move(oldPath, exePath); } catch { /* nothing further we can do */ }
            return null;
        }
        return oldPath;
    }

    /// <summary>Starts the freshly installed executable just long enough for it to delete the image this
    /// process is still running from. No window: the cleanup branch returns before any UI is created.</summary>
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
        catch { /* the startup sweep will get it next time */ }
    }

    /// <summary>
    /// Hands every superseded image to a cleanup helper as this process exits. Covers more than the swap
    /// this process just made: a second instance keeps the old image alive, so its deletion fails and it
    /// falls to whichever instance leaves last.
    /// </summary>
    public static void CleanUpSupersededImages(string exePath)
    {
        if (!File.Exists(exePath)) return;
        foreach (string f in Safe(() => Directory.EnumerateFiles(Dir(exePath), $"{Stem(exePath)}.old*.exe")))
            LaunchCleanup(exePath, f);
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
