using System.Drawing;
using System.Text.Json;

namespace Cascade.App;

public enum MarkerVisibilityMode { Always, Never, WhenInUse }

/// <summary>User preferences, persisted to <c>%APPDATA%\Cascade\settings.json</c>.</summary>
public sealed class AppSettings
{
    public string FontFamily { get; set; } = "Consolas";
    public float FontSize { get; set; } = 10f;
    public int ZoomPercent { get; set; } = 100;

    public int ForegroundArgb { get; set; } = Color.FromArgb(30, 30, 30).ToArgb();
    public int BackgroundArgb { get; set; } = Color.White.ToArgb();
    public int LineNumberArgb { get; set; } = Color.FromArgb(150, 150, 150).ToArgb();
    public int GutterBackArgb { get; set; } = Color.FromArgb(246, 246, 246).ToArgb();
    public int SelectionBackArgb { get; set; } = Color.FromArgb(0, 120, 215).ToArgb();
    public int SelectionForeArgb { get; set; } = Color.White.ToArgb();
    public int DimForegroundArgb { get; set; } = Color.FromArgb(188, 188, 188).ToArgb();

    public int TabSize { get; set; } = 4;
    public bool ShowLineNumbers { get; set; } = true;
    public MarkerVisibilityMode MarkerVisibility { get; set; } = MarkerVisibilityMode.WhenInUse;
    public bool RemoteLean { get; set; }

    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentFilterFiles { get; set; } = new();

    /// <summary>The filter file (.cascade or .tat) last loaded/saved; auto-loaded on the next launch.</summary>
    public string? LastFilterFile { get; set; }

    /// <summary>When true, <see cref="LastFilterFile"/> is reloaded automatically at startup.</summary>
    public bool AutoLoadLastFilterFile { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public float EffectiveFontSize => Math.Max(4f, FontSize * ZoomPercent / 100f);

    [System.Text.Json.Serialization.JsonIgnore] public Color Foreground => Color.FromArgb(ForegroundArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color Background => Color.FromArgb(BackgroundArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color LineNumberColor => Color.FromArgb(LineNumberArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color GutterBack => Color.FromArgb(GutterBackArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color SelectionBack => Color.FromArgb(SelectionBackArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color SelectionFore => Color.FromArgb(SelectionForeArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color DimForeground => Color.FromArgb(DimForegroundArgb);

    private static string SettingsDir =>
        Environment.GetEnvironmentVariable("CASCADE_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cascade");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public void AddRecentFile(string path) => AddRecent(RecentFiles, path);
    public void AddRecentFilterFile(string path) => AddRecent(RecentFilterFiles, path);

    private static void AddRecent(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        while (list.Count > 12) list.RemoveAt(list.Count - 1);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    /// <summary>The 8 marker colors (index 0..7).</summary>
    public static readonly Color[] MarkerColors =
    {
        Color.FromArgb(0xE6,0x39,0x46), Color.FromArgb(0xF7,0x7F,0x00), Color.FromArgb(0xFC,0xBF,0x49),
        Color.FromArgb(0x43,0xAA,0x8B), Color.FromArgb(0x27,0x7D,0xA1), Color.FromArgb(0x57,0x75,0x90),
        Color.FromArgb(0x9B,0x5D,0xE5), Color.FromArgb(0xF1,0x5B,0xB5)
    };
}
