using Replica.Core.Models;
using Replica.Core.Services;

namespace Replica.Infrastructure.Updates;

public sealed class PlaceholderReleaseProvider : IReleaseProvider
{
    public PlaceholderReleaseProvider(ReleaseRepositoryOptions repository)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ReleaseRepositoryOptions Repository { get; }

    public Task<ReleaseInfo?> GetLatestReleaseAsync(
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReleaseInfo?>(null);
    }
}
