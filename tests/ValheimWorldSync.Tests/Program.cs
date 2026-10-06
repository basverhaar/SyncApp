// End-to-end checks of the sync logic, simulating two PCs and a shared folder in a temp directory.
// Usage: ValheimWorldSync.Tests [path to a sample worlds folder] [world name]
using System.Diagnostics;
using System.IO;
using ValheimWorldSync.Core;

var root = Path.Combine(Path.GetTempPath(), "vws-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
var failures = 0;

void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
    if (!condition) failures++;
}

// ── Sample world ──
var world = args.Length > 1 ? args[1] : "TestWorld";
var sampleDir = Path.Combine(root, "sample");
Console.WriteLine($"Sample: {(args.Length > 0 ? args[0] : "(generated)")}  world: {world}");
if (args.Length > 0)
{
    var sourceWorld = Path.Combine(args[0], world);
    if (args.Length < 2 || !Directory.Exists(sourceWorld)) throw new ArgumentException($"World folder not found: {sourceWorld}");
    foreach (var f in Directory.EnumerateFiles(sourceWorld, "*", SearchOption.AllDirectories))
    {
        var dest = Path.GetFullPath(Path.Combine(sampleDir, world, Path.GetRelativePath(sourceWorld, f)));
        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Refusing to write outside the test folder: {dest}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(f, dest);
    }
}
else
{
    Directory.CreateDirectory(Path.Combine(sampleDir, world));
    File.WriteAllText(Path.Combine(sampleDir, world, "_main.1.fwl2"), "meta");
    File.WriteAllText(Path.Combine(sampleDir, world, "_main.1.db2"), "data");
    File.WriteAllText(Path.Combine(sampleDir, world, "00_00__1_1.chunk"), "chunk");
}
File.WriteAllText(Path.Combine(sampleDir, world, "cacheMinimapMeta"), "cache"); // must be ignored

var sharedRoot = Path.Combine(root, "shared");
Directory.CreateDirectory(sharedRoot);
var shared = new SharedFolder(sharedRoot);

var prompt = new ScriptedPrompt();
Machine NewMachine(string name)
{
    var dir = Path.Combine(root, name);
    var settings = new AppSettings { PlayerName = name, SharedFolder = sharedRoot, WorldName = world };
    return new Machine(name, Path.Combine(dir, "worlds_local"), Path.Combine(dir, "data"), settings, new SyncEngine(settings, prompt));
}
var a = NewMachine("Alice");
var b = NewMachine("Bob");

async Task<string> LocalHash(Machine m) => WorldStore.HashOf(await WorldStore.ScanAsync(m.WorldsDir, world));
string ChunkFile(Machine m) => Directory.EnumerateFiles(Path.Combine(m.WorldsDir, world), "*.chunk").Order().First();

// 1. Alice shares a Steam Cloud world.
a.Use();
var discovered = WorldStore.Discover(sampleDir, isSteamCloud: true);
Check(discovered.Count == 1 && discovered[0].Name == world, "discovers the sample world");
await a.Engine.CreateSharedWorldAsync(discovered[0]);
var state = shared.ReadState()!;
Check(state.Version == 1 && state.UpdatedBy == "Alice", "creating the shared world uploads version 1");
Check(await LocalHash(a) == state.Hash, "Alice's local copy matches the shared version");
Check(!File.Exists(Path.Combine(shared.WorldDir, world, "cacheMinimapMeta")), "minimap cache is not uploaded");
Check(shared.ReadConfig()?.WorldName == world, "sharedsave.json names the world");

// 2. Bob joins and syncs before hosting: download.
b.Use();
await b.Engine.PrepareJoinAsync(world);
var bs = await b.Engine.SyncBeforePlayAsync();
Check(bs?.Version == 1 && await LocalHash(b) == state.Hash, "Bob downloads version 1");

// 3. Bob plays: changes a chunk, adds and removes files, then uploads.
var removed = Directory.EnumerateFiles(Path.Combine(b.WorldsDir, world), "*.chunk").Order().Last();
File.AppendAllText(ChunkFile(b), "more progress");
File.WriteAllText(Path.Combine(b.WorldsDir, world, "99_99__1_1.chunk"), "new area");
if (Directory.EnumerateFiles(Path.Combine(b.WorldsDir, world), "*.chunk").Count() > 2) File.Delete(removed);
var v2 = await b.Engine.UploadAsync(1);
Check(v2?.Version == 2 && v2.UpdatedBy == "Bob", "Bob uploads version 2");
Check(WorldStore.HashOf(await WorldStore.ScanAsync(shared.WorldDir, world)) == v2!.Hash, "shared files exactly match version 2 (incl. deletions)");

// 4. Alice syncs before hosting: gets Bob's changes.
a.Use();
await a.Engine.SyncBeforePlayAsync();
Check(await LocalHash(a) == v2.Hash, "Alice downloads version 2");
Check(Directory.EnumerateDirectories(Path.Combine(a.DataDir, "Backups", world)).Any(), "Alice's old copy was backed up first");

// 5. Already up to date: nothing happens, no prompt.
prompt.Asked.Clear();
await a.Engine.SyncBeforePlayAsync();
Check(prompt.Asked.Count == 0, "no questions when already up to date");

// 6. Double host: both changed the world from version 2, Bob uploads first, then Alice ("Keep mine").
b.Use();
await b.Engine.SyncBeforePlayAsync();
File.AppendAllText(ChunkFile(b), "bob v3");
var v3 = await b.Engine.UploadAsync(2);
a.Use();
File.AppendAllText(ChunkFile(a), "alice parallel");
prompt.Answers.Enqueue(0); // Keep mine
var v4 = await a.Engine.UploadAsync(2);
Check(prompt.Asked.LastOrDefault() == "Someone else also saved the world", "double hosting is detected on upload");
Check(v3?.Version == 3 && v4?.Version == 4 && v4.UpdatedBy == "Alice", "Alice's version wins as version 4");
Check(Directory.EnumerateDirectories(Path.Combine(a.DataDir, "Backups", world)).Any(d => d.EndsWith("shared-v3-replaced")), "Bob's version 3 is kept as a backup");

// 7. Bob has local changes and the shared copy changed too: asked, picks shared version.
b.Use();
File.AppendAllText(ChunkFile(b), "bob offline");
prompt.Answers.Enqueue(0); // Use shared version
await b.Engine.SyncBeforePlayAsync();
Check(prompt.Asked.LastOrDefault() == "Two different versions", "conflicting local + shared changes ask the player");
Check(await LocalHash(b) == v4!.Hash, "Bob gets version 4 after choosing the shared version");

// 8. Upload when nothing changed.
var same = await b.Engine.UploadAsync(4);
Check(same?.Version == 4, "uploading an unchanged world doesn't create a new version");

// 9. A linked world folder is replaced by a real folder without touching the target.
var linkTarget = Path.Combine(root, "link-target");
foreach (var f in Directory.EnumerateFiles(Path.Combine(b.WorldsDir, world)))
{
    Directory.CreateDirectory(Path.Combine(linkTarget, world));
    File.Copy(f, Path.Combine(linkTarget, world, Path.GetFileName(f)));
}
var c = NewMachine("Carol");
c.Use();
Directory.CreateDirectory(c.WorldsDir);
var linkPath = Path.Combine(c.WorldsDir, world);
var linked = TryCreateLink(linkPath, Path.Combine(linkTarget, world));
if (linked)
{
    Check(WorldStore.IsLinked(c.WorldsDir, world), "detects the linked folder");
    await c.Engine.PrepareJoinAsync(world);
    Check(!WorldStore.IsLinked(c.WorldsDir, world), "link is replaced with a normal folder");
    Check(await LocalHash(c) == v4.Hash, "the world files from the link were kept");
    Check(Directory.EnumerateFiles(Path.Combine(linkTarget, world)).Any(), "the link's target folder is untouched");
}
else Console.WriteLine("SKIP  link test (couldn't create a link)");

// 10. Host claims.
var hostsA = new HostRegistry(shared, a.Settings.MachineId);
var hostsB = new HostRegistry(shared, b.Settings.MachineId);
var now = DateTimeOffset.UtcNow;
hostsA.WriteOwn(new HostClaim { MachineId = a.Settings.MachineId, PlayerName = "Alice", ClaimedAt = now.AddSeconds(-5), Heartbeat = now });
hostsB.WriteOwn(new HostClaim { MachineId = b.Settings.MachineId, PlayerName = "Bob", ClaimedAt = now, Heartbeat = now });
Check(hostsB.GetActiveOtherHost()?.PlayerName == "Alice", "Bob sees Alice hosting");
Check(hostsA.ResolveWinner()?.PlayerName == "Alice" && hostsB.ResolveWinner()?.PlayerName == "Alice", "earliest claim wins on both PCs");
hostsA.WriteOwn(new HostClaim { MachineId = a.Settings.MachineId, PlayerName = "Alice", ClaimedAt = now.AddHours(-1), Heartbeat = now.AddMinutes(-10) });
Check(hostsB.GetActiveOtherHost() == null && hostsB.GetStaleOtherHost()?.PlayerName == "Alice", "a host without heartbeat is reported as stale");
hostsA.DeleteOwn();
hostsB.DeleteOwn();
Check(hostsA.ReadAll().Count == 0, "claims are removed");

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
try { Directory.Delete(root, true); } catch { }
return failures == 0 ? 0 : 1;

static bool TryCreateLink(string link, string target)
{
    try
    {
        Directory.CreateSymbolicLink(link, target);
        return true;
    }
    catch
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false });
        p!.WaitForExit();
        return Directory.Exists(link);
    }
}

sealed record Machine(string Name, string WorldsDir, string DataDir, AppSettings Settings, SyncEngine Engine)
{
    public void Use()
    {
        AppPaths.LocalWorldsDir = WorldsDir;
        AppPaths.DataDir = DataDir;
    }
}

sealed class ScriptedPrompt : IUserPrompt
{
    public Queue<int> Answers { get; } = new();
    public List<string> Asked { get; } = [];

    public Task<int> AskAsync(string title, string message, params string[] buttons)
    {
        Asked.Add(title);
        var answer = Answers.Count > 0 ? Answers.Dequeue() : -1;
        Console.WriteLine($"      prompt: {title} -> {(answer >= 0 ? buttons[answer] : "(closed)")}");
        return Task.FromResult(answer);
    }
}
