using System.Text.Json;
using Cascade.Core.IO;

namespace Cascade.App;

/// <summary>State that only means anything on the machine that recorded it - file paths, so far. Kept out
/// of <see cref="AppSettings"/> and in its own <c>state.json</c> so that exported preferences carry to
/// another machine without dragging along paths that do not exist there.</summary>
public sealed class MachineState
{
    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentFilterFiles { get; set; } = new();

    /// <summary>The filter file (.cascade or .tat) last loaded/saved; auto-loaded on the next launch when
    /// <see cref="AppSettings.AutoLoadLastFilterFile"/> is set.</summary>
    public string? LastFilterFile { get; set; }

    /// <summary>Where the state actually lives, so the user can be told.</summary>
    public static string FilePath => Path.Combine(SettingsFolder.Dir, "state.json");

    public void AddRecentFile(string path) => AddRecent(RecentFiles, path);
    public void AddRecentFilterFile(string path) => AddRecent(RecentFilterFiles, path);

    private static void AddRecent(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        while (list.Count > 12) list.RemoveAt(list.Count - 1);
    }

    /// <summary>Falls back to the settings file, where all of this used to live, so that upgrading does not
    /// silently empty someone's recent-files list.</summary>
    public static MachineState Load() => Read(FilePath) ?? Read(AppSettings.FilePath) ?? new MachineState();

    private static MachineState? Read(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<MachineState>(File.ReadAllText(path));
        }
        catch (JsonException) { SettingsFolder.SetAside(path); }
        catch { /* unreadable right now - leave it where it is and use defaults for this run */ }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder.Dir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
