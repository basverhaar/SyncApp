using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ValheimWorldSync.Core;

/// <summary>A world found in one of Valheim's save folders.</summary>
public sealed record WorldInfo(string Name, string WorldsDir, bool IsSteamCloud, DateTime LastWrite, long Size)
{
    public string Location => IsSteamCloud ? "Steam Cloud" : "This PC";
}

/// <summary>
/// An automatic backup: a folder in this PC's backups folder, or (when <see cref="SharedBy"/> is set)
/// a .zip in the shared folder's backups\ folder.
/// </summary>
public sealed record BackupInfo(string Location, DateTime Created, string Reason, long Size, string? SharedBy = null, string? Hash = null)
{
    public bool IsShared => SharedBy != null;
}

/// <summary>A world file with its location on disk.</summary>
public sealed record WorldFile(string Relative, string FullPath, long Size, string Sha256)
{
    public FileEntry ToEntry() => new(Relative, Size, Sha256);
}

/// <summary>
/// Reads and writes worlds inside a "worlds folder" (Valheim's worlds_local, a Steam Cloud worlds folder,
/// or the world\ folder inside the shared Google Drive folder — they all use the same layout).
///
/// A world called X consists of:
///  - the folder X\ (newer Valheim versions save worlds as chunk files in here), and
///  - the files X.db / X.fwl (+ .old) used by older Valheim versions.
/// Minimap cache files are left out: Valheim regenerates them.
/// </summary>
public static class WorldStore
{
    private static readonly string[] LegacyExtensions = [".db", ".fwl", ".db.old", ".fwl.old"];

    public static bool IsExcluded(string fileName) =>
        fileName.StartsWith("cacheMinimap", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Relative, string FullPath)> EnumerateFiles(string worldsDir, string world)
    {
        var folder = Path.Combine(worldsDir, world);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (IsExcluded(Path.GetFileName(file))) continue;
                yield return (world + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'), file);
            }
        }
        foreach (var ext in LegacyExtensions)
        {
            var file = Path.Combine(worldsDir, world + ext);
            if (File.Exists(file)) yield return (world + ext, file);
        }
    }

    public static bool HasWorldData(IEnumerable<FileEntry> files) =>
        files.Any(f => f.Path.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase) ||
                       f.Path.EndsWith(".fwl2", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cheap check (names, sizes, timestamps) used to avoid re-hashing a world that hasn't changed.</summary>
    public static string Fingerprint(string worldsDir, string world) =>
        string.Join('\n', EnumerateFiles(worldsDir, world)
            .Select(f => new FileInfo(f.FullPath))
            .Select(i => $"{i.FullName}|{i.Length}|{i.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal));

    public static Task<List<WorldFile>> ScanAsync(string worldsDir, string world, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var result = new List<WorldFile>();
            foreach (var (relative, fullPath) in EnumerateFiles(worldsDir, world).ToList())
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                result.Add(new WorldFile(relative, fullPath, stream.Length, hash));
            }
            return result;
        }, ct);

    public static string HashOf(IEnumerable<WorldFile> files) => Manifest.HashOf(files.Select(f => f.ToEntry()));

    /// <summary>
    /// Makes the world in <paramref name="targetDir"/> identical to <paramref name="source"/>:
    /// copies new/changed files and removes files that no longer exist in the source.
    /// </summary>
    public static Task<int> MirrorAsync(IReadOnlyList<WorldFile> source, string targetDir, string world, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var existing = (await ScanAsync(targetDir, world, ct))
                .ToDictionary(f => f.Relative, StringComparer.OrdinalIgnoreCase);
            var copied = 0;

            foreach (var file in source)
            {
                ct.ThrowIfCancellationRequested();
                if (existing.Remove(file.Relative, out var current) && current.Sha256 == file.Sha256) continue;

                var target = Path.Combine(targetDir, file.Relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.FullPath, target, overwrite: true);
                copied++;
            }

            foreach (var leftover in existing.Values)
                File.Delete(leftover.FullPath);

            RemoveEmptyFolders(Path.Combine(targetDir, world));
            return copied;
        }, ct);

    private static void RemoveEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder) || IsLink(folder)) return;
        foreach (var sub in Directory.GetDirectories(folder))
        {
            RemoveEmptyFolders(sub);
            if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
        }
    }

    /// <summary>Copies the world's files to the backups folder and keeps only the newest 10 backups per world.</summary>
    public static Task<string?> BackupAsync(string worldsDir, string world, string reason) =>
        Task.Run(() =>
        {
            var files = EnumerateFiles(worldsDir, world).ToList();
            if (files.Count == 0) return null;

            var root = Path.Combine(AppPaths.BackupsDir, world);
            var target = Path.Combine(root, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{reason}");
            foreach (var (relative, fullPath) in files)
            {
                var dest = Path.Combine(target, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(fullPath, dest, overwrite: true);
            }

            foreach (var old in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.Name).Skip(10))
                old.Delete(recursive: true);

            return (string?)target;
        });

    /// <summary>The automatic backups of a world on this PC, newest first.</summary>
    public static List<BackupInfo> ListBackups(string world)
    {
        var root = new DirectoryInfo(Path.Combine(AppPaths.BackupsDir, world));
        if (!root.Exists) return [];
        var result = new List<BackupInfo>();
        foreach (var dir in root.GetDirectories().OrderByDescending(d => d.Name))
        {
            // Folder names look like 2026-10-13_20-15-00_before-download.
            var (created, reason, _) = ParseBackupName(dir.Name, dir.CreationTime);
            var size = EnumerateFiles(dir.FullName, world).Sum(f => new FileInfo(f.FullPath).Length);
            if (size > 0) result.Add(new BackupInfo(dir.FullName, created, reason, size));
        }
        return result;
    }

    /// <summary>The file name for a backup in the shared folder, e.g. 2026-10-13_20-15-00_before-download_Bas_1A2B3C4D5E6F.zip.</summary>
    public static string SharedBackupName(string localBackupDir, string playerName, string hash) =>
        $"{SharedBackupPrefix(localBackupDir, playerName)}{hash[..12]}.zip";

    /// <summary>The start of <see cref="SharedBackupName"/>: everything except the hash.</summary>
    public static string SharedBackupPrefix(string localBackupDir, string playerName)
    {
        var player = string.Concat(playerName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
        return $"{Path.GetFileName(localBackupDir)}_{(player == "" ? "unknown" : player)}_";
    }

    /// <summary>The backups in the shared folder's backups\&lt;world&gt; folder, newest first.</summary>
    public static List<BackupInfo> ListSharedBackups(string sharedBackupsDir)
    {
        var root = new DirectoryInfo(sharedBackupsDir);
        if (!root.Exists) return [];
        var result = new List<BackupInfo>();
        foreach (var file in root.GetFiles("*.zip").OrderByDescending(f => f.Name))
        {
            var (created, reason, rest) = ParseBackupName(Path.GetFileNameWithoutExtension(file.Name), file.CreationTime);
            var split = rest.LastIndexOf('_');
            var (by, hash) = split > 0 ? (rest[..split], rest[(split + 1)..]) : (rest, "");
            result.Add(new BackupInfo(file.FullName, created, reason, file.Length, by == "" ? "someone" : by, hash));
        }
        return result;
    }

    /// <summary>Keeps only the newest <paramref name="keep"/> backups in the shared folder.</summary>
    public static void PruneSharedBackups(string sharedBackupsDir, int keep)
    {
        foreach (var old in ListSharedBackups(sharedBackupsDir).Skip(keep))
            File.Delete(old.Location);
    }

    private static (DateTime Created, string Reason, string Remainder) ParseBackupName(string name, DateTime fallback)
    {
        var parts = name.Split('_', 4);
        if (parts.Length < 2 || !DateTime.TryParseExact($"{parts[0]}_{parts[1]}", "yyyy-MM-dd_HH-mm-ss",
                null, System.Globalization.DateTimeStyles.None, out var created))
            created = fallback;
        return (created, parts.Length >= 3 ? parts[2].Replace('-', ' ') : "", parts.Length == 4 ? parts[3] : "");
    }

    /// <summary>Writes the world's files to a .zip, in the same layout as a worlds folder.</summary>
    public static Task ExportZipAsync(IReadOnlyList<WorldFile> files, string zipPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var temp = zipPath + ".tmp";
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(file.Relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(file.FullPath);
                    using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }
            File.Move(temp, zipPath, overwrite: true);
        }, ct);

    /// <summary>
    /// Finds the worlds folder that contains <paramref name="world"/>: one of <paramref name="candidates"/>,
    /// or (when <paramref name="recursive"/>) any folder below them. Returns null and the worlds that were found instead.
    /// </summary>
    public static string? FindWorldsDir(IEnumerable<string> candidates, string world, bool recursive, out List<string> otherWorlds)
    {
        var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        otherWorlds = [];
        foreach (var candidate in candidates.Where(Directory.Exists))
        {
            var dirs = recursive
                ? Directory.EnumerateDirectories(candidate, "*", SearchOption.AllDirectories).Prepend(candidate)
                : [candidate];
            foreach (var dir in dirs)
            {
                var found = Discover(dir, isSteamCloud: false);
                if (found.Any(w => w.Name.Equals(world, StringComparison.OrdinalIgnoreCase))) return dir;
                others.UnionWith(found.Select(w => w.Name));
            }
        }
        otherWorlds = [.. others.Order()];
        return null;
    }

    /// <summary>True when the world's folder is a symbolic link or junction (e.g. a manual link into Google Drive).</summary>
    public static bool IsLinked(string worldsDir, string world) => IsLink(Path.Combine(worldsDir, world));

    private static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Removes a linked world folder. Only the link is removed, never the files it points to.</summary>
    public static void RemoveLink(string worldsDir, string world)
    {
        var path = Path.Combine(worldsDir, world);
        if (!IsLink(path)) return;
        Directory.Delete(path, recursive: false);
    }

    /// <summary>Finds all worlds in a worlds folder (ignores Valheim's own *_backup_* copies).</summary>
    public static List<WorldInfo> Discover(string worldsDir, bool isSteamCloud)
    {
        var result = new List<WorldInfo>();
        if (!Directory.Exists(worldsDir)) return result;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(worldsDir))
                if (Directory.EnumerateFiles(dir, "*.fwl2").Any())
                    names.Add(Path.GetFileName(dir));
            foreach (var file in Directory.EnumerateFiles(worldsDir, "*.fwl"))
                names.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var name in names.Where(n => !n.Contains("_backup_", StringComparison.OrdinalIgnoreCase)))
        {
            var infos = EnumerateFiles(worldsDir, name).Select(f => new FileInfo(f.FullPath)).ToList();
            if (infos.Count == 0) continue;
            result.Add(new WorldInfo(name, worldsDir, isSteamCloud, infos.Max(i => i.LastWriteTime), infos.Sum(i => i.Length)));
        }
        return result;
    }
}
