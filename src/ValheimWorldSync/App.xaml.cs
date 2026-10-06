using System.Windows;
using System.Windows.Threading;
using ValheimWorldSync.Core;

namespace ValheimWorldSync;

public partial class App : Application
{
    private Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstance = new Mutex(true, @"Local\ValheimWorldSync.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Valheim World Sync is already running. Check your taskbar.", "Valheim World Sync",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

#if DEBUG
        // Lets a developer point the app at a sandbox instead of the real saves.
        if (Environment.GetEnvironmentVariable("VWS_DATA_DIR") is { Length: > 0 } dataDir) AppPaths.DataDir = dataDir;
        if (Environment.GetEnvironmentVariable("VWS_WORLDS_DIR") is { Length: > 0 } worldsDir) AppPaths.LocalWorldsDir = worldsDir;
#endif
        DispatcherUnhandledException += OnUnhandledException;
        new MainWindow().Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Info($"Unexpected error: {e.Exception}");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in {AppPaths.LogFile}",
            "Valheim World Sync", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
