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

    /// <summary>A .tat with a single enabled include filter matching "MATCH".</summary>
    public static string WriteFilterFile()
    {
        const string xml =
            "<TextAnalysisTool.NET version=\"2025-11-21\" showOnlyFilteredLines=\"False\">\n" +
            "  <filters>\n" +
            "    <filter enabled=\"y\" excluding=\"n\" description=\"match rows\" foreColor=\"FF0000\" " +
            "type=\"matches_text\" case_sensitive=\"n\" regex=\"n\" text=\"MATCH\" />\n" +
            "  </filters>\n" +
            "</TextAnalysisTool.NET>\n";
        string path = Path.Combine(Path.GetTempPath(), "cascade_uitest_" + Guid.NewGuid().ToString("N") + ".tat");
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Path to the built Cascade.exe (same build config as this test assembly).</summary>
    public static string AppExe()
    {
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
