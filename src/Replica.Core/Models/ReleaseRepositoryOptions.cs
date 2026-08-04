namespace Replica.Core.Models;

public sealed record ReleaseRepositoryOptions(string Owner, string Repository)
{
    public static ReleaseRepositoryOptions Replica { get; } = new("HechoLP", "Replica");
}
