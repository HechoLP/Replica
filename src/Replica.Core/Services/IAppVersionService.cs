namespace Replica.Core.Services;

public interface IAppVersionService
{
    Version CurrentVersion { get; }

    string DisplayVersion { get; }
}
