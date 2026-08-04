using System.Windows;
using Replica.Core.Services;

namespace Replica.App.Services;

public sealed class DialogService : IDialogService
{
    public void ShowMessage(string title, string message)
    {
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            MessageBoxResult.OK,
            MessageBoxOptions.None);
    }
}
