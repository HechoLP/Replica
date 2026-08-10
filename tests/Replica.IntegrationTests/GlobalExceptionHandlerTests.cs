using Microsoft.Extensions.Logging;
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
            new RecordingLogger<GlobalExceptionHandler>());

        Exception? exception = Record.Exception(
            () => handler.Handle(new InvalidOperationException("test"), "unit test"));

        Assert.Null(exception);
        Assert.Contains("시스템 변경은 수행되지 않았습니다", dialog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_NeverWritesExceptionMessageOrSourceToLogs()
    {
        const string secret = "TOKEN=super-secret-value";
        RecordingLogger<GlobalExceptionHandler> logger = new();
        GlobalExceptionHandler handler = new(new RecordingDialogService(), logger);

        handler.Handle(new InvalidOperationException(secret), $"dispatcher-{secret}");

        Assert.Null(logger.Exception);
        Assert.DoesNotContain(secret, logger.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, logger.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public string Message { get; private set; } = string.Empty;

        public void ShowMessage(string title, string message)
        {
            Message = message;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public Exception? Exception { get; private set; }

        public string Message { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
            Message = formatter(state, exception);
        }
    }
}
