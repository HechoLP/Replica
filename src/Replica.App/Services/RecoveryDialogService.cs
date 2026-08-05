using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Replica.App.Services;

public sealed class RecoveryDialogService : IRecoveryDialogService
{
    public string? SelectRecoverySnapshot()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Recovery Snapshot 선택",
            Filter = "Replica Snapshot (*.replica)|*.replica",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public char[]? RequestPassword(string title, string message)
    {
        PasswordBox password = new() { MinWidth = 300 };
        Window owner = Application.Current.MainWindow;
        Window dialog = new()
        {
            Title = title,
            Owner = owner,
            Width = 420,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildContent(message, password),
        };
        return dialog.ShowDialog() == true ? password.Password.ToCharArray() : null;

        static UIElement BuildContent(string message, PasswordBox password)
        {
            StackPanel panel = new() { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });
            panel.Children.Add(password);
            StackPanel buttons = new()
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            Button cancel = new() { Content = "취소", MinWidth = 76, IsCancel = true };
            Button confirm = new()
            {
                Content = "확인",
                MinWidth = 76,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true,
            };
            confirm.Click += (_, _) => Window.GetWindow(confirm)!.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);
            return panel;
        }
    }

    public bool Confirm(string title, string message) => MessageBox.Show(
        message,
        title,
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) => MessageBox.Show(
        message,
        title,
        MessageBoxButton.OK,
        MessageBoxImage.Error);
}
