using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ValheimWorldSync.Core;

/// <summary>One file of a world, identified by its path relative to the worlds folder ('/' separated).</summary>
public sealed record FileEntry(string Path, long Size, string Sha256);

/// <summary>Stored in the shared folder as sharedsave.json. Says which world the group shares.</summary>
public sealed class SharedConfig
{
    public int FormatVersion { get; set; } = 1;
    public string WorldName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Stored in the shared folder as state.json. Describes the latest uploaded version of the world.</summary>
public sealed class SharedState
{
    public int Version { get; set; }
    public string Hash { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public List<FileEntry> Files { get; set; } = [];
}

/// <summary>Stored in the shared folder as hosts/&lt;machine id&gt;.json while someone is hosting.</summary>
public sealed class HostClaim
{
    public string MachineId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public DateTimeOffset ClaimedAt { get; set; }
    public DateTimeOffset Heartbeat { get; set; }
}

/// <summary>A hosting session on this PC that hasn't been uploaded yet.</summary>
public sealed class ActiveSession
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>The shared version this session started from; used to detect a second host.</summary>
    public int BaseVersion { get; set; }
}

public static class Manifest
{
    /// <summary>A single hash for a whole world. Empty string means "no files".</summary>
    public static string HashOf(IEnumerable<FileEntry> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            sb.Append(f.Path.ToLowerInvariant()).Append('|').Append(f.Size).Append('|').Append(f.Sha256).Append('\n');
        return sb.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SharedConfig))]
[JsonSerializable(typeof(SharedState))]
[JsonSerializable(typeof(HostClaim))]
internal partial class JsonCtx : JsonSerializerContext;
