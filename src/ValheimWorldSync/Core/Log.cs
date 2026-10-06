using System.IO;

namespace ValheimWorldSync.Core;

public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly List<string> RecentLines = [];

    public static event Action<string>? Message;

    public static IReadOnlyList<string> Recent
    {
        get { lock (Gate) return [.. RecentLines]; }
    }

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (Gate)
        {
            RecentLines.Add(line);
            if (RecentLines.Count > 300) RecentLines.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000) file.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Message?.Invoke(line);
    }
}
