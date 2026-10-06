using System.IO;
using System.Text.Json.Serialization;

namespace ValheimWorldSync.Core;

public static class AppPaths
{
    public static string DataDir { get; internal set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimWorldSync");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string BackupsDir => Path.Combine(DataDir, "Backups");
    public static string LogFile => Path.Combine(DataDir, "log.txt");

    public static string ValheimDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "IronGate", "Valheim");

    /// <summary>Where Valheim keeps worlds that are stored on this PC (not in Steam Cloud).</summary>
    public static string LocalWorldsDir { get; internal set; } = Path.Combine(ValheimDataDir, "worlds_local");
}

public sealed class AppSettings
{
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");
    public string PlayerName { get; set; } = "";
    public string? SharedFolder { get; set; }
    public string? WorldName { get; set; }

    /// <summary>Hash of the world as it was after the last successful download/upload on this PC.</summary>
    public string? LastSyncedHash { get; set; }
    public int LastSyncedVersion { get; set; }

    public ActiveSession? ActiveSession { get; set; }

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SharedFolder) &&
        !string.IsNullOrWhiteSpace(WorldName) &&
        !string.IsNullOrWhiteSpace(PlayerName);

    public static AppSettings Load()
    {
        try
        {
            return JsonFile.Read(AppPaths.SettingsFile, JsonCtx.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't read settings ({ex.Message}); starting fresh.");
            return new AppSettings();
        }
    }

    public void Save() => JsonFile.Write(AppPaths.SettingsFile, this, JsonCtx.Default.AppSettings, atomic: true);
}
