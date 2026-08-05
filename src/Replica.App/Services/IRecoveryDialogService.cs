namespace Replica.App.Services;

public interface IRecoveryDialogService
{
    string? SelectRecoverySnapshot();

    char[]? RequestPassword(string title, string message);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);
}
