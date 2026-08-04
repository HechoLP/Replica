namespace Replica.App.Services;

public interface IGlobalExceptionHandler
{
    void Handle(Exception exception, string source);
}
