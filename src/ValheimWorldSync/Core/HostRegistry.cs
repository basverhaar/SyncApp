using System.IO;

namespace ValheimWorldSync.Core;

/// <summary>
/// Keeps track of who is hosting, using one small file per PC in the shared folder.
/// Each PC only ever writes its own file, so Google Drive never has to merge conflicting edits.
/// </summary>
public sealed class HostRegistry(SharedFolder shared, string machineId)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>A host that hasn't written a heartbeat for this long is considered gone (crash, lost internet...).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(4);

    private string FileFor(string id) => Path.Combine(shared.HostsDir, id + ".json");

    public List<HostClaim> ReadAll()
    {
        var claims = new List<HostClaim>();
        if (!Directory.Exists(shared.HostsDir)) return claims;
        foreach (var file in Directory.EnumerateFiles(shared.HostsDir, "*.json"))
        {
            try
            {
                if (JsonFile.Read(file, JsonCtx.Default.HostClaim) is { } claim && !string.IsNullOrEmpty(claim.MachineId))
                    claims.Add(claim);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Half-synced or conflicting copy from Google Drive; skip it.
            }
        }
        return claims;
    }

    public static bool IsFresh(HostClaim claim) => DateTimeOffset.UtcNow - claim.Heartbeat < StaleAfter;

    public HostClaim? GetActiveOtherHost() =>
        ReadAll().Where(c => c.MachineId != machineId && IsFresh(c)).MinBy(c => c.ClaimedAt);

    public HostClaim? GetStaleOtherHost() =>
        ReadAll().Where(c => c.MachineId != machineId && !IsFresh(c)).MaxBy(c => c.Heartbeat);

    /// <summary>When two people claim at about the same time, the earliest claim wins.</summary>
    public HostClaim? ResolveWinner() =>
        ReadAll().Where(IsFresh)
            .OrderBy(c => c.ClaimedAt)
            .ThenBy(c => c.MachineId, StringComparer.Ordinal)
            .FirstOrDefault();

    public HostClaim? ReadOwn()
    {
        try { return JsonFile.Read(FileFor(machineId), JsonCtx.Default.HostClaim); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    public void WriteOwn(HostClaim claim) => JsonFile.Write(FileFor(machineId), claim, JsonCtx.Default.HostClaim);

    public void DeleteOwn() => Delete(machineId);

    public void Delete(HostClaim claim) => Delete(claim.MachineId);

    private void Delete(string id)
    {
        var file = FileFor(id);
        if (File.Exists(file)) File.Delete(file);
    }
}
