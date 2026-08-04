namespace Replica.Core.Models;

public sealed record ReleaseInfo(
    Version Version,
    string TagName,
    Uri ReleasePage,
    bool IsPrerelease);
