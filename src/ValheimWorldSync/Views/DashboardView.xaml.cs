using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using ValheimWorldSync.Core;

namespace ValheimWorldSync.Views;

public partial class DashboardView : UserControl
{
    private readonly AppSettings settings;
    private readonly SyncEngine engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool refreshing;

    public event Action? SettingsRequested;

    public DashboardView(AppSettings settings, SyncEngine engine)
    {
        InitializeComponent();
        this.settings = settings;
        this.engine = engine;

        foreach (var line in Log.Recent) LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();

        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            Log.Message += OnLog;
            engine.StateChanged += OnEngineChanged;
            timer.Start();
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            Log.Message -= OnLog;
            engine.StateChanged -= OnEngineChanged;
            timer.Stop();
        };
    }

    private void OnLog(string line) => Dispatcher.InvokeAsync(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void OnEngineChanged() => Dispatcher.InvokeAsync(async () => await RefreshAsync());

    private async Task RefreshAsync()
    {
        WorldTitle.Text = settings.WorldName;
        WorldSubtitle.Text = $"Playing as {settings.PlayerName}  ·  {settings.SharedFolder}";
        SetupButton.IsEnabled = !engine.IsBusy && !engine.InSession;
        RefreshButton.IsEnabled = !engine.IsBusy;

        if (engine.InSession)
        {
            SetDot("SystemFillColorSuccessBrush");
            HostText.Text = "You're hosting";
            SharedText.Text = "Your friends can join you now.";
            LocalText.Text = "The world is uploaded automatically when you close Valheim.";
            ShowPlay("Playing…", "Keep this window open (minimized is fine) until the upload is done.", enabled: false);
            return;
        }

        if (engine.IsBusy)
        {
            ShowPlay("Working…", "See the activity below.", enabled: false);
            return;
        }

        if (refreshing) return;
        refreshing = true;
        try
        {
            SharedStatus status;
            try { status = await engine.GetStatusAsync(); }
            catch (Exception ex) { status = new SharedStatus($"Couldn't check the shared folder: {ex.Message}", null, null, null, LocalWorldState.Unknown); }
            if (engine.IsBusy || engine.InSession) return; // Play was pressed while we were checking.

            SharedText.Text = status.State == null
                ? "No version has been uploaded yet."
                : $"Shared world: version {status.State.Version}, saved by {status.State.UpdatedBy} {Format.Ago(status.State.UpdatedAt)}.";
            LocalText.Text = status.Local switch
            {
                LocalWorldState.UpToDate => "This PC has the latest version.",
                LocalWorldState.UpdateAvailable => "A newer version is downloaded when you play.",
                LocalWorldState.LocalChanges => "This PC has progress that isn't in the shared version. You'll be asked what to do when you play.",
                LocalWorldState.Missing => "Not on this PC yet — it's downloaded when you play.",
                _ => "",
            };

            WarningText.Text = $"⚠  There's also a Steam Cloud copy of \"{settings.WorldName}\" in Valheim. Always pick the locally stored one, " +
                               "or remove the Steam Cloud copy in Valheim (Manage saves).";
            WarningText.Visibility = status.HasSteamCloudCopy && status.Problem == null ? Visibility.Visible : Visibility.Collapsed;

            if (status.Problem != null)
            {
                SetDot("SystemFillColorCriticalBrush");
                HostText.Text = "Can't reach the shared folder";
                SharedText.Text = status.Problem;
                LocalText.Text = "";
                ShowPlay("Play", "Fix the problem above first.", enabled: true);
            }
            else if (status.ActiveHost is { } host)
            {
                SetDot("SystemFillColorSuccessBrush");
                HostText.Text = $"{host.PlayerName} is hosting";
                SharedText.Text = $"Online since {Format.Time(host.ClaimedAt)}.";
                LocalText.Text = "";
                ShowPlay($"Join {host.PlayerName}", "Starts Valheim so you can join them. Nothing is synced.", enabled: true);
            }
            else if (status.StaleHost is { } stale)
            {
                SetDot("SystemFillColorCautionBrush");
                HostText.Text = $"{stale.PlayerName}'s last session didn't finish";
                SharedText.Text = $"Their app stopped around {Format.Time(stale.Heartbeat)} before uploading. Ask them to open Valheim World Sync. " + SharedText.Text;
                ShowPlay("Play & host", "Gets the latest world, starts Valheim, and uploads it when you quit.", enabled: true);
            }
            else
            {
                SetDot("TextFillColorTertiaryBrush");
                HostText.Text = "Nobody is hosting right now";
                ShowPlay("Play & host", "Gets the latest world, starts Valheim, and uploads it when you quit.", enabled: true);
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    private void SetDot(string brushKey) => HostDot.SetResourceReference(Shape.FillProperty, brushKey);

    private void ShowPlay(string text, string hint, bool enabled)
    {
        PlayButton.Content = text;
        PlayButton.IsEnabled = enabled;
        PlayHint.Text = hint;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        await engine.PlayAsync();
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Setup_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OpenShared_Click(object sender, RoutedEventArgs e) => OpenFolder(settings.SharedFolder);

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.BackupsDir);
        OpenFolder(AppPaths.BackupsDir);
    }

    private static void OpenFolder(string? path)
    {
        if (path != null && Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
