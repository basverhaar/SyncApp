using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ValheimWorldSync.Core;

namespace ValheimWorldSync.Views;

/// <summary>Lets the player pick an automatic backup or a backup file. Returns the path and a description, or null.</summary>
public partial class RestoreDialog : Window
{
    public sealed record Item(string Path, string Title, string Detail);

    private (string Path, string Description)? result;

    private RestoreDialog(string world)
    {
        InitializeComponent();
        IntroText.Text = $"Pick a version of \"{world}\" to make the shared version again. The current version is backed up first, so you can undo this.";

        var items = WorldStore.ListBackups(world)
            .Select(b => new Item(b.Dir, b.Created.ToString("dddd d MMMM yyyy, HH:mm"), $"{Describe(b.Reason)}  ·  {Format.Size(b.Size)}"))
            .ToList();
        BackupList.ItemsSource = items;
        if (items.Count == 0)
        {
            BackupList.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private static string Describe(string reason) => reason switch
    {
        "before download" => "Your copy, saved before downloading a newer version",
        "before restore" => "Saved before restoring a backup",
        "my version not uploaded" => "Your version that wasn't uploaded (two hosts at once)",
        "steam cloud copy" or "steam cloud original" => "Steam Cloud copy",
        "steam cloud session not uploaded" => "Session played on an outdated Steam Cloud copy",
        _ when reason.StartsWith("shared v") && reason.EndsWith(" replaced") =>
            $"Shared version {reason["shared v".Length..^" replaced".Length]}, saved before it was replaced",
        "" => "Backup",
        _ => char.ToUpper(reason[0]) + reason[1..],
    };

    public static (string Path, string Description)? Show(Window owner, string world)
    {
        var dialog = new RestoreDialog(world) { Owner = owner };
        dialog.ShowDialog();
        return dialog.result;
    }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = BackupList.SelectedItem != null;

    private void BackupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BackupList.SelectedItem != null) Restore_Click(sender, e);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not Item item) return;
        result = (item.Path, $"the backup from {item.Title}");
        Close();
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a backup",
            Filter = "Valheim world backups (*.zip, *.fwl, *.fwl2)|*.zip;*.fwl;*.fwl2|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        result = (dialog.FileName, Path.GetFileName(dialog.FileName));
        Close();
    }
}
