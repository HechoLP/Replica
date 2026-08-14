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
            "예기치 않은 문제가 발생해 추가 작업을 중단했습니다. 실행 결과와 Rollback Journal을 확인하세요.");
    }
}
