using System.Diagnostics;
using System.Text;

// UI Automation tests drive a single real desktop window; never run them in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cascade.UiTests;

/// <summary>Locates and builds a deterministic test file + filter set for the UI tests.</summary>
internal static class TestData
{
    /// <summary>1000 lines; every 5th line (0-based) contains "MATCH", the rest "other".</summary>
    public const int LineCount = 1000;
    public const int MatchEvery = 5;
    public static int MatchCount => LineCount / MatchEvery; // 200

    public static bool IsMatchLine(int zeroBasedLine) => zeroBasedLine % MatchEvery == 0;

    public static string WriteLogFile()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < LineCount; i++)
            sb.Append(IsMatchLine(i) ? "MATCH line " : "other line ").Append(i).Append('\n');
        string path = Path.Combine(Path.GetTempPath(), "cascade_uitest_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// As <see cref="WriteLogFile()"/>, but every line is padded out to <paramref name="minWidth"/>
    /// characters so the view has something to scroll horizontally. Must be wide enough to overflow any
    /// screen the tests might run on, or a large monitor fits the whole line and there is nothing to scroll.
    /// </summary>
    public static string WriteLogFile(int minWidth)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < LineCount; i++)
        {
            string head = (IsMatchLine(i) ? "MATCH line " : "other line ") + i + " ";
            sb.Append(head).Append('=', Math.Max(0, minWidth - head.Length)).Append('\n');
        }
        string path = Path.Combine(Path.GetTempPath(), "cascade_uitest_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>A .tat with a single enabled include filter matching "MATCH".</summary>
    public static string WriteFilterFile() => WriteFilterFile("MATCH");

    /// <summary>A .tat with one enabled include filter per match text, in the given order.</summary>
    public static string WriteFilterFile(params string[] texts)
    {
        var sb = new StringBuilder();
        sb.Append("""<TextAnalysisTool.NET version="2025-11-21" showOnlyFilteredLines="False">""").Append('\n');
        sb.Append("  <filters>\n");
        foreach (string t in texts)
            sb.Append($"""    <filter enabled="y" excluding="n" description="{t}" foreColor="FF0000" type="matches_text" case_sensitive="n" regex="n" text="{t}" />""").Append('\n');
        sb.Append("  </filters>\n");
        sb.Append("</TextAnalysisTool.NET>\n");
        string path = Path.Combine(Path.GetTempPath(), "cascade_uitest_" + Guid.NewGuid().ToString("N") + ".tat");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Path to the Cascade.exe under test. Defaults to the build output next to this test assembly.
    /// Set CASCADE_TEST_EXE to point the whole suite at a different one - CI aims it at the PUBLISHED
    /// single-file exe, because that is what ships and it resolves its assemblies and embedded resources
    /// out of the bundle rather than from files on disk.
    /// </summary>
    public static string AppExe()
    {
        if (Environment.GetEnvironmentVariable("CASCADE_TEST_EXE") is { Length: > 0 } chosen)
        {
            if (!File.Exists(chosen)) throw new FileNotFoundException("CASCADE_TEST_EXE points at a file that does not exist: " + chosen);
            return chosen;
        }

        string config =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cascade.slnx"))) dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("Could not locate repo root (Cascade.slnx).");
        string exe = Path.Combine(dir.FullName, "src", "Cascade.App", "bin", config, "net10.0-windows", "Cascade.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Cascade.exe not found. Build the app first: " + exe);
        return exe;
    }
}
