using System.IO;

namespace ValheimWorldSync.Core;

public interface IUserPrompt
{
    /// <summary>Shows a message with buttons; returns the index of the clicked button, or -1 when closed.</summary>
    Task<int> AskAsync(string title, string message, params string[] buttons);
}

/// <summary>An error with a message meant for the player, not a stack trace.</summary>
public sealed class UserFacingException(string message) : Exception(message);

public enum LocalWorldState { Unknown, UpToDate, UpdateAvailable, LocalChanges, Missing }

public sealed record SharedStatus(
    string? Problem,
    HostClaim? ActiveHost,
    HostClaim? StaleHost,
    SharedState? State,
    LocalWorldState Local);

public sealed class SyncEngine(AppSettings settings, IUserPrompt prompt)
{
    /// <summary>How long to wait after claiming the host, so Google Drive can show us a simultaneous claim.</summary>
    private static readonly TimeSpan ClaimSettleTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GameStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DriveSyncTimeout = TimeSpan.FromMinutes(10);

    private string? cachedFingerprint;
    private string? cachedHash;

    public bool IsBusy { get; private set; }
    public bool InSession => settings.ActiveSession != null;

    public event Action? StateChanged;
    public event Action? GameLaunched;
    public event Action? SessionFinished;

    private SharedFolder Shared => new(settings.SharedFolder ?? throw new UserFacingException("The app isn't set up yet."));
    private string World => settings.WorldName ?? throw new UserFacingException("The app isn't set up yet.");
    private HostRegistry Hosts => new(Shared, settings.MachineId);
    private static string LocalDir => AppPaths.LocalWorldsDir;

    // ───────────────────────────── Status ─────────────────────────────

    public async Task<SharedStatus> GetStatusAsync()
    {
        var shared = Shared;
        if (!shared.Exists)
            return new SharedStatus($"Can't find the shared folder ({shared.Root}). Is Google Drive for Desktop running and signed in?", null, null, null, LocalWorldState.Unknown);

        SharedState? state;
        try { state = shared.ReadState(); }
        catch (Exception ex) { return new SharedStatus($"Couldn't read the shared folder: {ex.Message}", null, null, null, LocalWorldState.Unknown); }

        var hosts = Hosts;
        var active = hosts.GetActiveOtherHost();
        var stale = active == null ? hosts.GetStaleOtherHost() : null;

        var local = LocalWorldState.Unknown;
        try
        {
            var localHash = await LocalHashAsync();
            local = localHash == "" ? LocalWorldState.Missing
                : state == null || localHash == state.Hash ? LocalWorldState.UpToDate
                : localHash == settings.LastSyncedHash ? LocalWorldState.UpdateAvailable
                : LocalWorldState.LocalChanges;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new SharedStatus(null, active, stale, state, local);
    }

    private async Task<string> LocalHashAsync()
    {
        var fingerprint = WorldStore.Fingerprint(LocalDir, World);
        if (fingerprint != cachedFingerprint)
        {
            cachedHash = WorldStore.HashOf(await WorldStore.ScanAsync(LocalDir, World));
            cachedFingerprint = fingerprint;
        }
        return cachedHash!;
    }

    // ───────────────────────────── Play ─────────────────────────────

    public async Task PlayAsync()
    {
        if (IsBusy || InSession) return;
        SetBusy(true);
        var claimed = false;
        try
        {
            await EnsureReadyAsync();
            var hosts = Hosts;

            if (hosts.GetActiveOtherHost() is { } host)
            {
                await JoinAsync(host);
                return;
            }

            if (Valheim.IsRunning)
            {
                await prompt.AskAsync("Valheim is already running",
                    "Close Valheim first. The app has to get the latest world before you start hosting, and it can't do that while the game is open.",
                    "OK");
                return;
            }

            if (hosts.GetStaleOtherHost() is { } stale)
            {
                var choice = await prompt.AskAsync($"{stale.PlayerName}'s session didn't finish",
                    $"{stale.PlayerName} was hosting until about {Format.Time(stale.Heartbeat)}, but their app stopped before it could upload the world " +
                    "(the game or PC probably crashed, or the internet dropped).\n\n" +
                    $"Best is to ask {stale.PlayerName} to open Valheim World Sync — it will upload their progress automatically.\n\n" +
                    "If you host anyway, you'll play on the last uploaded version and their latest progress can be lost.",
                    "Wait for them", "Host anyway");
                if (choice != 1) return;
                hosts.Delete(stale);
                Log.Info($"Removed {stale.PlayerName}'s unfinished session.");
            }

            var now = DateTimeOffset.UtcNow;
            var claim = new HostClaim
            {
                MachineId = settings.MachineId,
                SessionId = Guid.NewGuid().ToString("N"),
                PlayerName = settings.PlayerName,
                ClaimedAt = now,
                Heartbeat = now,
            };
            hosts.WriteOwn(claim);
            claimed = true;
            Log.Info($"Letting your friends know you're hosting… (waiting {ClaimSettleTime.TotalSeconds:0} seconds so nobody starts at the same moment)");
            await Task.Delay(ClaimSettleTime);

            var winner = hosts.ResolveWinner();
            if (winner != null && winner.MachineId != settings.MachineId)
            {
                hosts.DeleteOwn();
                claimed = false;
                Log.Info($"{winner.PlayerName} started hosting at the same moment and was slightly earlier.");
                await JoinAsync(winner);
                return;
            }

            var baseState = await SyncBeforePlayAsync();
            if (baseState == null)
            {
                hosts.DeleteOwn();
                claimed = false;
                Log.Info("Cancelled.");
                return;
            }

            settings.ActiveSession = new ActiveSession { SessionId = claim.SessionId, StartedAt = now, BaseVersion = baseState.Version };
            settings.Save();
            claimed = false; // From here on the session is tracked in settings and recovered if anything goes wrong.

            Log.Info("Starting Valheim through Steam…");
            Valheim.LaunchViaSteam();
            Log.Info($"In Valheim: choose your character, pick the world \"{World}\", tick \"Start server\" and press Start so your friends can join.");
            GameLaunched?.Invoke();

            await MonitorSessionAsync(claim, alreadyRunning: false);
        }
        catch (Exception ex)
        {
            await ReportAsync(ex);
            if (claimed) TryRun(() => Hosts.DeleteOwn());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task JoinAsync(HostClaim host)
    {
        var choice = await prompt.AskAsync($"{host.PlayerName} is hosting",
            $"{host.PlayerName} has been hosting \"{World}\" since {Format.Time(host.ClaimedAt)}.\n\n" +
            $"The app will start Valheim. Join {host.PlayerName} through Steam (Friends list → Join Game) or with the join code in Valheim (Join Game tab).\n\n" +
            "Nothing is synced when you join someone else — their app takes care of it.",
            "Launch Valheim", "Cancel");
        if (choice != 0) return;
        if (Valheim.IsRunning)
        {
            Log.Info($"Valheim is already running — join {host.PlayerName} from the Join Game tab.");
            return;
        }
        Log.Info($"Starting Valheim so you can join {host.PlayerName}.");
        Valheim.LaunchViaSteam();
        GameLaunched?.Invoke();
    }

    /// <summary>Called when the app starts: finishes a hosting session that was interrupted (app closed, PC crashed...).</summary>
    public async Task RecoverAsync()
    {
        if (!InSession || IsBusy) return;
        SetBusy(true);
        try
        {
            if (Valheim.IsRunning)
            {
                Log.Info("You're still hosting — the world will be uploaded when you close Valheim.");
                var claim = Hosts.ReadOwn() ?? new HostClaim
                {
                    MachineId = settings.MachineId,
                    SessionId = settings.ActiveSession!.SessionId,
                    PlayerName = settings.PlayerName,
                    ClaimedAt = settings.ActiveSession.StartedAt,
                };
                await MonitorSessionAsync(claim, alreadyRunning: true);
            }
            else
            {
                Log.Info("Your last hosting session wasn't uploaded yet. Uploading it now…");
                await FinishSessionAsync();
            }
        }
        catch (Exception ex)
        {
            await ReportAsync(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task MonitorSessionAsync(HostClaim claim, bool alreadyRunning)
    {
        var hosts = Hosts;
        var seen = alreadyRunning;
        var waitingSince = DateTime.UtcNow;
        var lastBeat = DateTime.MinValue;
        var goneChecks = 0;
        var heartbeatFailed = false;

        while (true)
        {
            if (DateTime.UtcNow - lastBeat >= HostRegistry.HeartbeatInterval)
            {
                claim.Heartbeat = DateTimeOffset.UtcNow;
                try
                {
                    hosts.WriteOwn(claim);
                    heartbeatFailed = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (!heartbeatFailed) Log.Info($"Couldn't update the shared folder ({ex.Message}). Will keep trying.");
                    heartbeatFailed = true;
                }
                lastBeat = DateTime.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));

            if (Valheim.IsRunning)
            {
                if (!seen) Log.Info("Valheim is running. Have fun! Keep this app open — it uploads the world when you quit the game.");
                seen = true;
                goneChecks = 0;
                continue;
            }

            if (!seen)
            {
                if (DateTime.UtcNow - waitingSince < GameStartTimeout) continue;
                var choice = await prompt.AskAsync("Valheim didn't start",
                    "Valheim hasn't started yet. Is Steam asking for something (login, update)?",
                    "Keep waiting", "Stop hosting");
                if (choice == 0)
                {
                    waitingSince = DateTime.UtcNow;
                    continue;
                }
                hosts.DeleteOwn();
                settings.ActiveSession = null;
                settings.Save();
                Log.Info("Stopped hosting. Nothing was changed.");
                return;
            }

            // Double-check, so a brief gap (e.g. the game restarting itself) isn't treated as quitting.
            if (++goneChecks < 2) continue;
            break;
        }

        Log.Info("Valheim was closed. Uploading the world to Google Drive…");
        await Task.Delay(TimeSpan.FromSeconds(3)); // Let the game's last save settle on disk.
        await FinishSessionAsync();
    }

    private async Task FinishSessionAsync()
    {
        var session = settings.ActiveSession!;
        await EnsureReadyAsync();
        var uploaded = await UploadAsync(session.BaseVersion);

        TryRun(() => Hosts.DeleteOwn());
        settings.ActiveSession = null;
        settings.Save();

        if (uploaded is { } state)
            Log.Info($"Done! Version {state.Version} is saved to Google Drive. Leave your PC on for a minute so Google Drive can finish uploading.");
        SessionFinished?.Invoke();
    }

    // ───────────────────────────── Sync ─────────────────────────────

    /// <summary>Makes sure this PC has the right version before hosting. Returns null when the player cancelled.</summary>
    internal async Task<SharedState?> SyncBeforePlayAsync()
    {
        var shared = Shared;
        var state = shared.ReadState();
        Log.Info("Checking for a newer version of the world…");
        var local = await WorldStore.ScanAsync(LocalDir, World);
        var localHash = WorldStore.HashOf(local);

        if (state == null)
        {
            if (localHash == "")
                throw new UserFacingException($"There's no world in the shared folder yet, and no world called \"{World}\" on this PC.");
            return await UploadAsync(0);
        }

        if (localHash == state.Hash)
        {
            MarkSynced(state);
            Log.Info($"You already have the latest version (version {state.Version}).");
            return state;
        }

        if (localHash == "" || localHash == settings.LastSyncedHash)
            return await DownloadAsync(state);

        string message;
        string[] buttons;
        if (state.Hash == settings.LastSyncedHash)
        {
            message = $"Your copy of \"{World}\" on this PC has progress that was never uploaded " +
                      "(maybe an upload failed, or someone played it without this app).\n\n" +
                      $"Upload your copy and host on it? The current shared version {state.Version} is backed up either way.";
            buttons = ["Upload mine", "Use shared version", "Cancel"];
        }
        else if (settings.LastSyncedHash == null)
        {
            message = $"There's already a world called \"{World}\" on this PC, and it's different from the shared one " +
                      $"(version {state.Version}, saved by {state.UpdatedBy} {Format.Ago(state.UpdatedAt)}).\n\n" +
                      "Usually you want the shared version. Your own copy is backed up either way.";
            buttons = ["Use shared version", "Upload mine", "Cancel"];
        }
        else
        {
            message = $"Both your copy of \"{World}\" and the shared copy changed since you last synced. " +
                      $"The shared copy is version {state.Version}, saved by {state.UpdatedBy} {Format.Ago(state.UpdatedAt)}.\n\n" +
                      "They can't be merged — pick one. The other one is backed up.";
            buttons = ["Use shared version", "Upload mine", "Cancel"];
        }

        var choice = await prompt.AskAsync("Two different versions", message, buttons);
        if (choice < 0 || buttons[choice] == "Cancel") return null;
        if (buttons[choice] == "Use shared version") return await DownloadAsync(state);

        await WorldStore.BackupAsync(shared.WorldDir, World, $"shared-v{state.Version}-replaced");
        return await UploadAsync(state.Version);
    }

    private async Task<SharedState> DownloadAsync(SharedState state)
    {
        var shared = Shared;
        Log.Info($"Downloading version {state.Version} (saved by {state.UpdatedBy} {Format.Ago(state.UpdatedAt)})…");

        // Google Drive may still be syncing the files the other player uploaded. Wait until they match state.json.
        var deadline = DateTime.UtcNow + DriveSyncTimeout;
        List<WorldFile> remote;
        while (true)
        {
            remote = await WorldStore.ScanAsync(shared.WorldDir, World);
            if (WorldStore.HashOf(remote) == state.Hash) break;
            if (DateTime.UtcNow > deadline)
                throw new UserFacingException("Google Drive hasn't finished syncing the latest world to this PC yet. Check that Google Drive is running and try again in a few minutes.");
            Log.Info("Waiting for Google Drive to finish syncing the world files…");
            await Task.Delay(TimeSpan.FromSeconds(15));
            state = shared.ReadState() ?? state;
        }

        if (await WorldStore.BackupAsync(LocalDir, World, "before-download") is { } backup)
            Log.Info($"Backed up your local copy to {backup}");

        Directory.CreateDirectory(LocalDir);
        var copied = await WorldStore.MirrorAsync(remote, LocalDir, World);

        var local = await WorldStore.ScanAsync(LocalDir, World);
        if (WorldStore.HashOf(local) != state.Hash)
            throw new UserFacingException("The downloaded world doesn't match the shared version. Please try again.");

        MarkSynced(state);
        Log.Info($"Downloaded version {state.Version} ({copied} file(s) updated).");
        return state;
    }

    /// <summary>Uploads the local world. Returns the new shared state, or the existing one if nothing changed.</summary>
    internal async Task<SharedState?> UploadAsync(int baseVersion)
    {
        var shared = Shared;
        var state = shared.ReadState();
        var local = await WorldStore.ScanAsync(LocalDir, World);
        var localHash = WorldStore.HashOf(local);

        if (localHash == "")
            throw new UserFacingException($"Couldn't find the world \"{World}\" in {LocalDir}. Was it saved as a Steam Cloud world? In Valheim, make sure the world is stored locally.");

        if (state != null && state.Hash == localHash)
        {
            MarkSynced(state);
            Log.Info("The world didn't change — nothing to upload.");
            return state;
        }

        if (state != null && state.Version != baseVersion)
        {
            // Someone else uploaded while we were playing: two people were hosting at the same time.
            var other = state.UpdatedBy;
            var choice = await prompt.AskAsync("Someone else also saved the world",
                $"While you were playing, {other} also uploaded \"{World}\" ({Format.Ago(state.UpdatedAt)}). " +
                "That means you were both hosting at the same time, and the two versions can't be merged.\n\n" +
                "Which version should be kept? The other one is saved in the backups folder either way.",
                "Keep mine", $"Keep {other}'s");
            if (choice == 1)
            {
                await WorldStore.BackupAsync(LocalDir, World, "my-version-not-uploaded");
                return await DownloadAsync(state);
            }
            await WorldStore.BackupAsync(shared.WorldDir, World, $"shared-v{state.Version}-replaced");
        }

        Log.Info("Uploading the world to Google Drive…");
        Directory.CreateDirectory(shared.WorldDir);
        var copied = await WorldStore.MirrorAsync(local, shared.WorldDir, World);

        var remote = await WorldStore.ScanAsync(shared.WorldDir, World);
        if (WorldStore.HashOf(remote) != localHash)
            throw new UserFacingException("Uploading to the shared folder failed (the files don't match). Your world is safe on this PC — try again from the app.");

        var newState = new SharedState
        {
            Version = (state?.Version ?? 0) + 1,
            Hash = localHash,
            UpdatedBy = settings.PlayerName,
            UpdatedAt = DateTimeOffset.UtcNow,
            Files = [.. local.Select(f => f.ToEntry())],
        };
        shared.WriteState(newState);
        MarkSynced(newState);
        Log.Info($"Uploaded version {newState.Version} ({copied} file(s) changed).");
        return newState;
    }

    private void MarkSynced(SharedState state)
    {
        settings.LastSyncedHash = state.Hash;
        settings.LastSyncedVersion = state.Version;
        settings.Save();
    }

    // ───────────────────────────── Setup ─────────────────────────────

    /// <summary>First-time setup by the person who owns the world: puts it in the shared folder.</summary>
    public async Task CreateSharedWorldAsync(WorldInfo source)
    {
        var shared = Shared;
        var world = source.Name;
        await PrepareLocalFolderAsync(world);

        if (source.IsSteamCloud)
        {
            Log.Info($"Copying \"{world}\" from Steam Cloud to this PC's local saves…");
            if (await WorldStore.BackupAsync(LocalDir, world, "before-copy-from-steam-cloud") is { } backup)
                Log.Info($"Backed up the existing local copy to {backup}");
            await WorldStore.BackupAsync(source.WorldsDir, world, "steam-cloud-original");
            var files = await WorldStore.ScanAsync(source.WorldsDir, world);
            Directory.CreateDirectory(LocalDir);
            await WorldStore.MirrorAsync(files, LocalDir, world);
        }

        shared.WriteConfig(new SharedConfig { WorldName = world, CreatedBy = settings.PlayerName, CreatedAt = DateTimeOffset.UtcNow });
        Directory.CreateDirectory(shared.HostsDir);
        settings.LastSyncedHash = null;
        settings.LastSyncedVersion = 0;
        settings.Save();

        var state = await UploadAsync(shared.ReadState()?.Version ?? 0);
        Log.Info($"\"{world}\" is now shared (version {state?.Version}).");
    }

    /// <summary>Setup for friends joining an existing shared world.</summary>
    public async Task PrepareJoinAsync(string world)
    {
        await PrepareLocalFolderAsync(world);
        settings.LastSyncedHash = null;
        settings.LastSyncedVersion = 0;
        settings.Save();
        Log.Info($"Ready. \"{world}\" will be downloaded the first time you press Play.");
    }

    private static async Task PrepareLocalFolderAsync(string world)
    {
        if (Valheim.IsRunning)
            throw new UserFacingException("Please close Valheim first, then try again.");
        if (WorldStore.IsLinked(LocalDir, world)) await ReplaceLinkAsync(world);
        Directory.CreateDirectory(LocalDir);
    }

    /// <summary>
    /// Replaces a linked world folder with a real one. If the link pointed at actual world files,
    /// they're copied into the new folder, so nothing is lost. The link's target is never touched.
    /// </summary>
    private static async Task ReplaceLinkAsync(string world)
    {
        var linked = await WorldStore.ScanAsync(LocalDir, world);
        string? backup = null;
        if (WorldStore.HasWorldData(linked.Select(f => f.ToEntry())))
            backup = await WorldStore.BackupAsync(LocalDir, world, "linked-folder");

        WorldStore.RemoveLink(LocalDir, world);
        Log.Info($"Removed the old link at {Path.Combine(LocalDir, world)} (nothing it pointed to was deleted).");

        if (backup != null)
        {
            var files = await WorldStore.ScanAsync(backup, world);
            await WorldStore.MirrorAsync(files, LocalDir, world);
            Log.Info("Copied the world files from the linked folder into a normal folder.");
        }
    }

    private async Task EnsureReadyAsync()
    {
        var shared = Shared;
        if (!shared.Exists)
            throw new UserFacingException($"Can't find the shared folder:\n{shared.Root}\n\nMake sure Google Drive for Desktop is running and you're signed in.");
        var config = shared.ReadConfig()
            ?? throw new UserFacingException("The shared folder doesn't contain a shared world (sharedsave.json is missing). Did Google Drive finish syncing?");
        if (!string.Equals(config.WorldName, World, StringComparison.Ordinal))
        {
            settings.WorldName = config.WorldName;
            settings.LastSyncedHash = null;
            settings.Save();
        }
        if (WorldStore.IsLinked(LocalDir, World))
        {
            var choice = await prompt.AskAsync("Old link found",
                $"{Path.Combine(LocalDir, World)} is a link to another folder (probably from an earlier manual setup). " +
                "Syncing can't work safely like that.\n\nReplace it with a normal folder? Nothing in the linked folder is deleted.",
                "Replace link", "Cancel");
            if (choice != 0) throw new UserFacingException("The world folder is still a link, so syncing was stopped.");
            await ReplaceLinkAsync(World);
        }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private async Task ReportAsync(Exception ex)
    {
        var message = ex is UserFacingException ? ex.Message : $"Something went wrong: {ex.Message}";
        Log.Info(message);
        if (ex is not UserFacingException) Log.Info(ex.ToString());
        await prompt.AskAsync("Valheim World Sync", message, "OK");
    }

    private static void TryRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Info($"Warning: {ex.Message}"); }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        StateChanged?.Invoke();
    }
}

public static class Format
{
    public static string Time(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("ddd d MMM HH:mm");
    }

    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 2 => "1 minute ago",
            < 60 => $"{(int)span.TotalMinutes} minutes ago",
            < 120 => "1 hour ago",
            < 24 * 60 => $"{(int)span.TotalHours} hours ago",
            _ => $"on {when.ToLocalTime():ddd d MMM HH:mm}",
        };
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };
}
