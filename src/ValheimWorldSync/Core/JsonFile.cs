using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ValheimWorldSync.Core;

internal static class JsonFile
{
    /// <summary>Returns null when the file doesn't exist. Throws when it exists but can't be read.</summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> info) where T : class
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize(stream, info);
    }

    public static void Write<T>(string path, T value, JsonTypeInfo<T> info, bool atomic = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(value, info);
        if (!atomic)
        {
            File.WriteAllBytes(path, json);
            return;
        }
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
