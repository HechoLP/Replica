using Microsoft.Extensions.Logging.Abstractions;
using Replica.App.Services;
using Replica.Core.Services;

namespace Replica.IntegrationTests;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public void Handle_ReportsSafeFailureMessage()
    {
        RecordingDialogService dialog = new();
        GlobalExceptionHandler handler = new(
            dialog,
            NullLogger<GlobalExceptionHandler>.Instance);

        Exception? exception = Record.Exception(
            () => handler.Handle(new InvalidOperationException("test"), "unit test"));

        Assert.Null(exception);
        Assert.Contains("시스템 변경은 수행되지 않았습니다", dialog.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public string Message { get; private set; } = string.Empty;

        public void ShowMessage(string title, string message)
        {
            Message = message;
        }
    }
}
