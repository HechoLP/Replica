using Microsoft.Extensions.Logging;
using Replica.Core.Services;

namespace Replica.App.Services;

public sealed class GlobalExceptionHandler : IGlobalExceptionHandler
{
    private readonly IDialogService _dialogService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IDialogService dialogService,
        ILogger<GlobalExceptionHandler> logger)
    {
        _dialogService = dialogService;
        _logger = logger;
    }

    public void Handle(Exception exception, string source)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        _logger.LogError(
            "Unhandled Replica exception of type {ExceptionType}.",
            exception.GetType().FullName ?? exception.GetType().Name);
        _dialogService.ShowMessage(
            "Replica",
            "예기치 않은 문제가 발생했습니다. 작업은 중단되었으며 시스템 변경은 수행되지 않았습니다.");
    }
}
