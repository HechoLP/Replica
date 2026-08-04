using Replica.Core.Matching;

namespace Replica.Core.Services;

public interface IApplicationVersionComparer
{
    ApplicationVersionComparisonResult Compare(string? sourceVersion, string? targetVersion);
}
