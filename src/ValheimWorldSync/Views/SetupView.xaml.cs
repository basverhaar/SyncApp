using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using ValheimWorldSync.Core;

namespace ValheimWorldSync.Views;

public partial class SetupView : UserControl
{
    private enum Step { Welcome, Requirements, Role, Folder, World, Name, Finish }

    private sealed record WorldItem(WorldInfo Info)
    {
        public string Name => Info.Name;
        public string Details => $"{Info.Location}  ·  last played {Format.Time(Info.LastWrite)}  ·  {Format.Size(Info.Size)}";
    }

    private readonly AppSettings settings;
    private readonly SyncEngine engine;
    private Step step = Step.Welcome;
    private bool requirementsOk;
    private SharedConfig? folderConfig;
    private bool finished;
    private bool working;

    public event Action? Completed;

    public SetupView(AppSettings settings, SyncEngine engine, bool canCancel)
    {
        InitializeComponent();
        this.settings = settings;
        this.engine = engine;

        CancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        FolderBox.Text = settings.SharedFolder ?? "";
        NameBox.Text = string.IsNullOrWhiteSpace(settings.PlayerName)
            ? Valheim.SteamPersonaName() ?? Environment.UserName
            : settings.PlayerName;
        RoleJoin.IsChecked = settings.IsConfigured;

        Log.Message += OnLog;
        Unloaded += (_, _) => Log.Message -= OnLog;
        ShowStep(Step.Welcome);
    }

    private bool Creating => RoleCreate.IsChecked == true;

    // ───────────────────────────── Navigation ─────────────────────────────

    private void ShowStep(Step next)
    {
        step = next;
        StepWelcome.Visibility = Vis(step == Step.Welcome);
        StepRequirements.Visibility = Vis(step == Step.Requirements);
        StepRole.Visibility = Vis(step == Step.Role);
        StepFolder.Visibility = Vis(step == Step.Folder);
        StepWorld.Visibility = Vis(step == Step.World);
        StepName.Visibility = Vis(step == Step.Name);
        StepFinish.Visibility = Vis(step == Step.Finish);
        Scroller.ScrollToTop();

        switch (step)
        {
            case Step.Requirements: CheckRequirements(); break;
            case Step.Folder: PrepareFolderStep(); break;
            case Step.World: PrepareWorldStep(); break;
            case Step.Name: NameBox.Focus(); NameBox.SelectAll(); break;
        }

        BackButton.Visibility = Vis(step != Step.Welcome && !(step == Step.Finish && finished));
        NextButton.Content = step switch
        {
            Step.Welcome => "Get started",
            Step.Name => "Finish setup",
            Step.Finish => finished ? "Go to the app" : "Try again",
            _ => "Next",
        };
        StepLabel.Text = step is Step.Welcome or Step.Finish ? "" : $"Step {(int)step} of 5";
        UpdateNext();
    }

    private void UpdateNext()
    {
        NextButton.IsEnabled = !working && step switch
        {
            Step.Requirements => requirementsOk,
            Step.Role => RoleCreate.IsChecked == true || RoleJoin.IsChecked == true,
            Step.Folder => ValidateFolder(),
            Step.World => !Creating || WorldList.SelectedItem != null,
            Step.Name => !string.IsNullOrWhiteSpace(NameBox.Text),
            _ => true,
        };
        CancelButton.IsEnabled = !working;
        BackButton.IsEnabled = !working;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (step)
        {
            case Step.Name:
                ShowStep(Step.Finish);
                await ApplyAsync();
                ShowStep(Step.Finish);
                break;
            case Step.Finish:
                if (finished) Completed?.Invoke();
                else
                {
                    await ApplyAsync();
                    ShowStep(Step.Finish);
                }
                break;
            default:
                ShowStep(step + 1);
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(step - 1);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Completed?.Invoke();

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OpenLink_Click(object sender, RoutedEventArgs e) => OpenUrl((string)((FrameworkElement)sender).Tag);

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ───────────────────────────── Requirements ─────────────────────────────

    private void Recheck_Click(object sender, RoutedEventArgs e) => CheckRequirements();

    private void CheckRequirements()
    {
        RequirementList.Children.Clear();
        var steam = Valheim.IsSteamInstalled;
        var game = steam && Valheim.IsGameInstalled;
        var driveInstalled = GoogleDrive.IsInstalled;
        var driveRunning = driveInstalled && GoogleDrive.IsRunning;
        var driveMounted = driveRunning && GoogleDrive.FindDrive() != null;

        AddRequirement(steam, "Steam is installed", "Steam is needed to start Valheim.",
            "Download Steam", "https://store.steampowered.com/about/");
        AddRequirement(game, "Valheim is installed", "Install Valheim in Steam.",
            "Install Valheim", $"steam://install/{Valheim.SteamAppId}");
        AddRequirement(driveInstalled, "Google Drive for Desktop is installed",
            "This makes your Google Drive show up as a drive (like G:) on this PC. Install it and sign in with your Google account.",
            "Download Google Drive", GoogleDrive.DownloadUrl);
        AddRequirement(driveMounted, "Google Drive is running and signed in",
            driveRunning
                ? "Google Drive is running, but no Google Drive disk was found. Open Google Drive from the system tray and sign in."
                : "Start Google Drive for Desktop (Start menu → Google Drive) and sign in.",
            null, null);

        requirementsOk = steam && game && driveInstalled && driveMounted;
        UpdateNext();
    }

    private void AddRequirement(bool ok, string title, string help, string? linkText, string? url)
    {
        var mark = new TextBlock
        {
            Text = ok ? "✔" : "✖",
            FontSize = 18,
            Width = 32,
            VerticalAlignment = VerticalAlignment.Top,
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!ok)
        {
            text.Children.Add(new TextBlock { Text = help, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 2, 0, 0) });
            if (linkText != null && url != null)
            {
                var link = new Hyperlink(new Run(linkText));
                link.Click += (_, _) => OpenUrl(url);
                text.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 6, 0, 0) });
            }
        }

        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(text);
        RequirementList.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Child = row });
    }

    // ───────────────────────────── Role & folder ─────────────────────────────

    private void Role_Checked(object sender, RoutedEventArgs e) => UpdateNext();

    private void PrepareFolderStep()
    {
        FolderCreateHelp.Visibility = Vis(Creating);
        FolderJoinHelp.Visibility = Vis(!Creating);
        lastValidation = null;
        ValidateFolder();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(FolderBox.Text) ? FolderBox.Text : GoogleDrive.FindDrive()?.RootDirectory.FullName;
        var dialog = new OpenFolderDialog { Title = "Select the shared Google Drive folder", InitialDirectory = start };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateNext();

    private (string Path, bool Creating, bool Valid)? lastValidation;

    private bool ValidateFolder()
    {
        var path = FolderBox.Text.Trim();
        // Validation writes a probe file to Google Drive, so don't repeat it for the same input.
        if (lastValidation is { } last && last.Path == path && last.Creating == Creating) return last.Valid;

        var (valid, warning, message) = CheckFolder(path);
        FolderMessage.Text = message;
        FolderMessage.SetResourceReference(TextBlock.ForegroundProperty,
            path.Length == 0 ? "TextFillColorSecondaryBrush"
            : !valid ? "SystemFillColorCriticalBrush"
            : warning ? "SystemFillColorCautionBrush"
            : "SystemFillColorSuccessBrush");
        lastValidation = (path, Creating, valid);
        return valid;
    }

    private (bool Valid, bool Warning, string Message) CheckFolder(string path)
    {
        folderConfig = null;
        if (path.Length == 0)
            return (false, false, "Click Browse… and select the folder.");
        if (!Directory.Exists(path))
            return (false, false, "That folder doesn't exist (yet). If you just created or added it, give Google Drive a moment and try again.");
        if (Path.GetFullPath(path).StartsWith(AppPaths.ValheimDataDir, StringComparison.OrdinalIgnoreCase))
            return (false, false, "That's Valheim's own save folder. Select the folder in Google Drive instead.");
        if (!CanWrite(path))
            return (false, false, "Can't write to this folder. Make sure your friend gave you the Editor role.");

        try { folderConfig = new SharedFolder(path).ReadConfig(); }
        catch (Exception ex) { return (false, false, $"Couldn't read sharedsave.json: {ex.Message}"); }

        if (Creating && folderConfig != null)
            return (false, false, $"This folder already contains the shared world \"{folderConfig.WorldName}\". Go back and choose \"Join a world a friend shared\".");
        if (!Creating && folderConfig == null)
            return (false, false, "This folder doesn't contain a shared world yet. Check with your friend that it's the right folder and that they finished their setup, or wait a moment for Google Drive to sync.");

        var message = folderConfig != null
            ? $"✔  Found the shared world \"{folderConfig.WorldName}\" (set up by {folderConfig.CreatedBy})."
            : "✔  Looks good.";
        var warning = !GoogleDrive.IsOnGoogleDrive(path);
        if (warning) message += "\n\nNote: this folder doesn't seem to be on your Google Drive disk, so it won't be shared with your friends.";
        return (true, warning, message);
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────────────────────── World ─────────────────────────────

    private void PrepareWorldStep()
    {
        WorldCreatePanel.Visibility = Vis(Creating);
        WorldJoinPanel.Visibility = Vis(!Creating);
        WorldWarning.Text = "";

        if (Creating)
        {
            LoadWorlds();
            return;
        }

        var name = folderConfig!.WorldName;
        SharedState? state = null;
        try { state = new SharedFolder(FolderBox.Text.Trim()).ReadState(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }

        WorldJoinText.Text = state == null
            ? $"This shared folder contains the world \"{name}\", set up by {folderConfig.CreatedBy}."
            : $"This shared folder contains the world \"{name}\" — version {state.Version}, last saved by {state.UpdatedBy} {Format.Ago(state.UpdatedAt)}.";

        if (WorldStore.Discover(AppPaths.LocalWorldsDir, false).Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            WorldWarning.Text = $"You already have a world called \"{name}\" on this PC. The first time you press Play, it's backed up and replaced with the shared version.";
        else if (WorldStore.IsLinked(AppPaths.LocalWorldsDir, name))
            WorldWarning.Text = LinkWarning(name);
    }

    private void RefreshWorlds_Click(object sender, RoutedEventArgs e) => LoadWorlds();

    private void LoadWorlds()
    {
        var worlds = WorldStore.Discover(AppPaths.LocalWorldsDir, false)
            .Concat(Valheim.SteamCloudWorldDirs().SelectMany(d => WorldStore.Discover(d, true)))
            .OrderByDescending(w => w.LastWrite)
            .Select(w => new WorldItem(w))
            .ToList();

        var previous = (WorldList.SelectedItem as WorldItem)?.Info;
        WorldList.ItemsSource = worlds;
        WorldList.SelectedItem = worlds.FirstOrDefault(w => w.Info == previous) ?? (worlds.Count == 1 ? worlds[0] : null);
        NoWorldsText.Visibility = Vis(worlds.Count == 0);
        UpdateNext();
    }

    private void WorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        WorldWarning.Text = WorldList.SelectedItem is WorldItem { Info: var w } && WorldStore.IsLinked(AppPaths.LocalWorldsDir, w.Name)
            ? LinkWarning(w.Name)
            : "";
        UpdateNext();
    }

    private static string LinkWarning(string world) =>
        $"{Path.Combine(AppPaths.LocalWorldsDir, world)} is a link to another folder (probably from an earlier manual setup). " +
        "That's not safe with several players, so the app replaces it with a normal folder. Nothing in the linked folder is deleted.";

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateNext();

    // ───────────────────────────── Apply ─────────────────────────────

    private void OnLog(string line) => Dispatcher.InvokeAsync(() =>
    {
        if (step != Step.Finish) return;
        FinishLog.AppendText(line + Environment.NewLine);
        FinishLog.ScrollToEnd();
    });

    private async Task ApplyAsync()
    {
        working = true;
        finished = false;
        FinishTitle.Text = "Setting things up…";
        HowToPlay.Visibility = Visibility.Collapsed;
        FinishLog.Clear();
        UpdateNext();

        var previous = (settings.SharedFolder, settings.WorldName, settings.PlayerName);
        try
        {
            settings.SharedFolder = FolderBox.Text.Trim();
            settings.PlayerName = NameBox.Text.Trim();

            if (Creating)
            {
                var world = ((WorldItem)WorldList.SelectedItem).Info;
                settings.WorldName = world.Name;
                await engine.CreateSharedWorldAsync(world);
            }
            else
            {
                settings.WorldName = folderConfig!.WorldName;
                if (previous.SharedFolder != settings.SharedFolder || previous.WorldName != settings.WorldName)
                    await engine.PrepareJoinAsync(settings.WorldName);
                else
                    settings.Save();
            }

            finished = true;
            FinishTitle.Text = "You're all set!";
            HowToWorld.Text = $"\"{settings.WorldName}\"";
            HowToPlay.Visibility = Visibility.Visible;
            if (Creating)
                Log.Info("Now share the Google Drive folder with your friends and send them this app.");
        }
        catch (Exception ex)
        {
            (settings.SharedFolder, settings.WorldName, settings.PlayerName) = previous;
            settings.Save();
            FinishTitle.Text = "Setup didn't finish";
            Log.Info(ex is UserFacingException ? ex.Message : $"Error: {ex.Message}");
        }
        finally
        {
            working = false;
            UpdateNext();
        }
    }
}
