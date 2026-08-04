using System.Reflection;
using Replica.Core.Services;

namespace Replica.Infrastructure.Updates;

public sealed class AppVersionService : IAppVersionService
{
    public AppVersionService()
        : this(Assembly.GetEntryAssembly() ?? typeof(AppVersionService).Assembly)
    {
    }

    public AppVersionService(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        CurrentVersion = assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        DisplayVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? CurrentVersion.ToString(3)
            : informationalVersion.Split('+', StringSplitOptions.TrimEntries)[0];
    }

    public Version CurrentVersion { get; }

    public string DisplayVersion { get; }
}
