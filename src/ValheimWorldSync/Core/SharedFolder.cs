using System.IO;

namespace ValheimWorldSync.Core;

/// <summary>
/// The shared Google Drive folder:
///   sharedsave.json  - which world this is
///   state.json       - latest uploaded version (hashes of every file)
///   world\           - the world files
///   hosts\           - one file per PC that is currently hosting
/// </summary>
public sealed class SharedFolder(string root)
{
    public string Root { get; } = root;
    public string ConfigFile => Path.Combine(Root, "sharedsave.json");
    public string StateFile => Path.Combine(Root, "state.json");
    public string WorldDir => Path.Combine(Root, "world");
    public string HostsDir => Path.Combine(Root, "hosts");

    public bool Exists => Directory.Exists(Root);

    public SharedConfig? ReadConfig() => JsonFile.Read(ConfigFile, JsonCtx.Default.SharedConfig);
    public void WriteConfig(SharedConfig config) => JsonFile.Write(ConfigFile, config, JsonCtx.Default.SharedConfig);

    public SharedState? ReadState() => JsonFile.Read(StateFile, JsonCtx.Default.SharedState);
    public void WriteState(SharedState state) => JsonFile.Write(StateFile, state, JsonCtx.Default.SharedState);
}
