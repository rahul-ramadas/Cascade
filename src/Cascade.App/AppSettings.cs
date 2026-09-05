using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.Core.IO;

namespace Cascade.App;

public enum MarkerVisibilityMode { Always, Never, WhenInUse }

/// <summary>Where the preferences and the per-machine state both live. Overridable with
/// <c>CASCADE_SETTINGS_DIR</c> so tests never touch the user's real configuration.</summary>
internal static class SettingsFolder
{
    public static string Dir =>
        Environment.GetEnvironmentVariable("CASCADE_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cascade");

    /// <summary>Moves a file that would not parse out of the way rather than letting the next save
    /// overwrite it. What is in it is usually readable by hand, and a silent reset to defaults is not
    /// something anyone can recover from.</summary>
    public static void SetAside(string path)
    {
        try { File.Move(path, path + ".bad", overwrite: true); } catch { /* best-effort */ }
    }
}

/// <summary>Machine-agnostic user preferences, persisted to <c>%APPDATA%\Cascade\settings.json</c>. Holds
/// nothing that is tied to one machine - see <see cref="MachineState"/> - so the whole file can be carried
/// to another machine as-is.</summary>
public sealed class AppSettings
{
    public string FontFamily { get; set; } = "Consolas";
    public float FontSize { get; set; } = 10f;
    public int ZoomPercent { get; set; } = 100;

    /// <summary>Pixels added to each log line on top of the font's own line height. Zero packs the lines as
    /// tightly as the typeface asks for, which is what a log reader usually wants; a point or two of air
    /// makes a dense trace easier to scan across.</summary>
    public int ExtraLineSpacing { get; set; }

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

    /// <summary>Whether the margin carries how long it was since the line above. On by default: a log whose
    /// clock could be read is one where the answer is worth having, and a column nobody was told about is a
    /// column nobody uses. Off costs nothing and stays off, since this is a preference like any other.</summary>
    public bool ShowElapsedGutter { get; set; } = true;

    /// <summary>Whether the status bar measures whatever is selected.</summary>
    public bool ShowElapsedInStatusBar { get; set; } = true;

    /// <summary>When true, <see cref="MachineState.LastFilterFile"/> is reloaded automatically at startup.</summary>
    public bool AutoLoadLastFilterFile { get; set; } = true;

    /// <summary>Where a new filter goes among its siblings: the top of the list, or the end of it.</summary>
    public bool AddNewFiltersAtTop { get; set; } = true;

    /// <summary>Whether the filter presets pane shares the filter pane.</summary>
    public bool ShowFilterPresets { get; set; } = true;

    /// <summary>Whether the match map replaces the log view's vertical scrollbar.</summary>
    public bool ShowMatchMap { get; set; } = true;

    /// <summary>Whether long lines are broken to fit the width instead of running off the side.</summary>
    public bool WordWrap { get; set; }

    /// <summary>Whether hovering a line names the filters that matched it.</summary>
    public bool ShowFilterTooltips { get; set; } = true;

    /// <summary>Behind every occurrence of the find term on a visible line.</summary>
    public int FindHighlightArgb { get; set; } = Color.FromArgb(255, 236, 150).ToArgb();

    /// <summary>Behind the occurrences on the line the search actually landed on.</summary>
    public int FindCurrentArgb { get; set; } = Color.FromArgb(255, 170, 60).ToArgb();

    /// <summary>Whether to leave a memory dump behind when the window stops answering. Off by default: it is
    /// for the machine where a freeze reproduces, and a dump of a process holding a large log is not small.</summary>
    public bool HangWatchdog { get; set; }

    /// <summary>How long the window may go without answering before that counts as a hang. Windows waits
    /// five seconds before it calls a window not responding, but a reader notices long before that, so this
    /// is deliberately shorter than the point at which the shell starts drawing over the app.</summary>
    public int HangWatchdogSeconds { get; set; } = 2;

    /// <summary>Whether the window answers Windows UI Automation and screen readers. Off by default
    /// because handing out a provider is what arms the teardown that freezes the app for seconds on a
    /// machine whose security software inspects thread creation - see <see cref="Automation"/>.</summary>
    public bool Automation { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public float EffectiveFontSize => Math.Max(4f, FontSize * ZoomPercent / 100f);

    [System.Text.Json.Serialization.JsonIgnore] public Color Foreground => Color.FromArgb(ForegroundArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color Background => Color.FromArgb(BackgroundArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color LineNumberColor => Color.FromArgb(LineNumberArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color GutterBack => Color.FromArgb(GutterBackArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color SelectionBack => Color.FromArgb(SelectionBackArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color SelectionFore => Color.FromArgb(SelectionForeArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color DimForeground => Color.FromArgb(DimForegroundArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color FindHighlight => Color.FromArgb(FindHighlightArgb);
    [System.Text.Json.Serialization.JsonIgnore] public Color FindCurrent => Color.FromArgb(FindCurrentArgb);

    private static string SettingsPath => Path.Combine(SettingsFolder.Dir, "settings.json");

    /// <summary>Where the settings actually live, so the user can be told.</summary>
    public static string FilePath => SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (JsonException) { SettingsFolder.SetAside(SettingsPath); }
        catch { /* unreadable right now - leave it where it is and use defaults for this run */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Indented));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Writes every preference to <paramref name="path"/> - the same content as the settings file.
    /// Nothing machine-specific is in here, so the result can be imported on another machine as-is.</summary>
    public void ExportTo(string path)
        => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Indented));

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Replaces every preference from a previously exported file, leaving this machine's state
    /// (recent files, last filter file) alone. Throws if the file is not one.</summary>
    public void ImportFrom(string path)
    {
        AppSettings? loaded;
        try { loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("That is not a Cascade settings file.", ex); }
        if (loaded is null) throw new InvalidDataException("That settings file is empty.");
        CopyFrom(loaded);
    }

    /// <summary>Copies every persisted property in place - the whole application already holds a reference to
    /// this instance, so it must be updated rather than replaced. Done by reflection so that adding a setting
    /// cannot silently leave it out of an import.</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var p in Persisted) p.SetValue(this, p.GetValue(other));
    }

    internal static IEnumerable<PropertyInfo> Persisted =>
        typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    /// <summary>The 8 marker colors (index 0..7).</summary>
    public static readonly Color[] MarkerColors =
    {
        Color.FromArgb(0xE6,0x39,0x46), Color.FromArgb(0xF7,0x7F,0x00), Color.FromArgb(0xFC,0xBF,0x49),
        Color.FromArgb(0x43,0xAA,0x8B), Color.FromArgb(0x27,0x7D,0xA1), Color.FromArgb(0x57,0x75,0x90),
        Color.FromArgb(0x9B,0x5D,0xE5), Color.FromArgb(0xF1,0x5B,0xB5)
    };
}
