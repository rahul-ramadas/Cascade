using System.Reflection;
using System.Runtime.InteropServices;

namespace Cascade.App;

/// <summary>
/// What this build is, taken from the assembly rather than written down anywhere - CI stamps
/// <c>-p:Version</c> at publish time, so the About box and the updater cannot drift out of step with the
/// binary they are running in.
/// </summary>
internal static class AppInfo
{
    /// <summary>"2026.7.6+f2a3e28..." on a released build, "1.0.0" on a local one.</summary>
    public static string InformationalVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>The numeric part, for comparing against a release.</summary>
    public static Version Version { get; } =
        Version.TryParse(InformationalVersion.Split('+')[0], out var v) ? v : new Version(0, 0, 0);

    /// <summary>Abbreviated commit, when the build carries one.</summary>
    public static string? Commit
    {
        get
        {
            int plus = InformationalVersion.IndexOf('+');
            if (plus < 0 || plus + 1 >= InformationalVersion.Length) return null;
            string sha = InformationalVersion[(plus + 1)..];
            return sha.Length > 7 ? sha[..7] : sha;
        }
    }

    /// <summary>Released builds carry a CalVer year; anything else was built locally and must never try to
    /// replace itself with a "newer" release.</summary>
    public static bool IsDevBuild => Version.Major < 2000;

    public static string DisplayVersion => IsDevBuild ? $"{Version} (local build)" : Version.ToString();

    /// <summary>Path of the running executable. Note <c>Assembly.Location</c> is empty under single-file.</summary>
    public static string ExePath => Environment.ProcessPath ?? "";

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
}
