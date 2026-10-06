using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ValheimWorldSync.Core;

public static partial class Valheim
{
    public const string SteamAppId = "892970";

    public static string? SteamPath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path && Directory.Exists(path)
                ? Path.GetFullPath(path.Replace('/', '\\'))
                : null;
        }
    }

    public static bool IsSteamInstalled => SteamPath != null;

    public static bool IsGameInstalled
    {
        get
        {
            if (SteamPath is not { } steam) return false;
            var libraries = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                libraries.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
            return libraries.Any(lib => File.Exists(Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf")));
        }
    }

    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("valheim");
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
    }

    public static void LaunchViaSteam() =>
        Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });

    /// <summary>Valheim's Steam Cloud world folders (one per Steam account on this PC).</summary>
    public static IEnumerable<string> SteamCloudWorldDirs()
    {
        if (SteamPath is not { } steam) yield break;
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) yield break;
        foreach (var user in Directory.EnumerateDirectories(userdata))
        {
            var worlds = Path.Combine(user, SteamAppId, "remote", "worlds");
            if (Directory.Exists(worlds)) yield return worlds;
        }
    }

    /// <summary>The Steam name of the most recently logged in account, used as default player name.</summary>
    public static string? SteamPersonaName()
    {
        try
        {
            if (SteamPath is not { } steam) return null;
            var file = Path.Combine(steam, "config", "loginusers.vdf");
            if (!File.Exists(file)) return null;
            string? fallback = null;
            foreach (Match user in VdfUserBlock().Matches(File.ReadAllText(file)))
            {
                var body = user.Groups[1].Value;
                var name = Regex.Match(body, "\"PersonaName\"\\s+\"([^\"]*)\"").Groups[1].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Regex.IsMatch(body, "\"MostRecent\"\\s+\"1\"")) return name;
                fallback ??= name;
            }
            return fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"\\d{17}\"\\s*\\{([^}]*)\\}")]
    private static partial Regex VdfUserBlock();
}

public static class GoogleDrive
{
    public const string DownloadUrl = "https://www.google.com/drive/download/";

    public static bool IsInstalled
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (Directory.Exists(Path.Combine(programFiles, "Google", "Drive File Stream"))) return true;
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Google\DriveFS");
            return key != null;
        }
    }

    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("GoogleDriveFS");
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
    }

    /// <summary>The drive letter Google Drive for Desktop is mounted on (e.g. G:\), if any.</summary>
    public static DriveInfo? FindDrive()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    return drive;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static bool IsOnGoogleDrive(string path)
    {
        var drive = FindDrive();
        return drive != null && path.StartsWith(drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase);
    }
}
