using System.Windows;
using System.Windows.Controls;
using ValheimWorldSync.Core;

namespace ValheimWorldSync.Views;

public partial class ChoiceDialog : Window
{
    private int result = -1;

    private ChoiceDialog(string title, string message, string[] buttons)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 110,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 6, 16, 6),
                IsDefault = i == 0,
                IsCancel = buttons[i] is "Cancel" or "OK" || (buttons.Length == 1),
            };
            if (i == 0 && TryFindResource("AccentButtonStyle") is Style accent) button.Style = accent;
            button.Click += (_, _) =>
            {
                result = index;
                Close();
            };
            ButtonPanel.Children.Add(button);
        }
    }

    public static int Show(Window? owner, string title, string message, params string[] buttons)
    {
        var dialog = new ChoiceDialog(title, message, buttons);
        if (owner is { IsLoaded: true })
        {
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
        return dialog.result;
    }
}

/// <summary>Lets the sync engine ask the player questions using <see cref="ChoiceDialog"/>.</summary>
public sealed class DialogPrompt(Window owner) : IUserPrompt
{
    public Task<int> AskAsync(string title, string message, params string[] buttons) =>
        owner.Dispatcher.InvokeAsync(() => ChoiceDialog.Show(owner, title, message, buttons)).Task;
}
