using System.Globalization;

namespace Replica.Core.Services;

public interface ILocalizationService
{
    CultureInfo CurrentCulture { get; }

    string GetString(string resourceKey);
}
