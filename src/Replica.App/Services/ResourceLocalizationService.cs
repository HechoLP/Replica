using System.Globalization;
using System.Windows;
using Replica.Core.Services;

namespace Replica.App.Services;

public sealed class ResourceLocalizationService : ILocalizationService
{
    public CultureInfo CurrentCulture => CultureInfo.CurrentUICulture;

    public string GetString(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        return Application.Current?.TryFindResource(resourceKey) as string
            ?? resourceKey;
    }
}
