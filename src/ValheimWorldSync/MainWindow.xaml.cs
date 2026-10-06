using System.ComponentModel;
using System.Windows;
using ValheimWorldSync.Core;
using ValheimWorldSync.Views;

namespace ValheimWorldSync;

public partial class MainWindow : Window
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly SyncEngine engine;
    private bool recovered;

    public MainWindow()
    {
        InitializeComponent();
        engine = new SyncEngine(settings, new DialogPrompt(this));
        engine.GameLaunched += () => WindowState = WindowState.Minimized;
        engine.SessionFinished += () =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };

        if (settings.IsConfigured) ShowDashboard();
        else ShowSetup();
    }

    private void ShowSetup()
    {
        var setup = new SetupView(settings, engine, canCancel: settings.IsConfigured);
        setup.Completed += ShowDashboard;
        Host.Content = setup;
    }

    private void ShowDashboard()
    {
        var dashboard = new DashboardView(settings, engine);
        dashboard.SettingsRequested += ShowSetup;
        Host.Content = dashboard;

        if (!recovered)
        {
            recovered = true;
            Dispatcher.InvokeAsync(engine.RecoverAsync);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (engine.InSession || engine.IsBusy)
        {
            var message = engine.InSession
                ? "You're hosting right now. If you close this app, the world is NOT uploaded when you quit Valheim — " +
                  "only the next time you open this app.\n\nYour friends will see your session as unfinished until then."
                : "The app is busy syncing. Closing it now can leave the shared world half-updated.";
            var choice = ChoiceDialog.Show(this, "Close Valheim World Sync?", message, "Keep running", "Close anyway");
            if (choice != 1) e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
