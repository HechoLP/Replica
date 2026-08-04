using Replica.Core.Matching;

namespace Replica.Core.Services;

public interface IApplicationIdentityNormalizer
{
    NormalizedApplicationIdentity Normalize(ApplicationDescriptor application);

    string NormalizeName(string value);

    string NormalizePublisher(string? value);

    string NormalizeInstallLocation(string? value);
}
