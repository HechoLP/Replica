using Replica.Core.Snapshots;

namespace Replica.Core.Tests;

public sealed class SensitiveEnvironmentPolicyTests
{
    [Theory]
    [InlineData("SERVICE_TOKEN", "TOKEN")]
    [InlineData("SIGNING_KEY", "KEY")]
    [InlineData("DOCKER_AUTH_CONFIG", "AUTH")]
    [InlineData("CI_JOB_JWT", "JWT")]
    [InlineData("SESSION_COOKIE", "COOKIE")]
    [InlineData("DATABASE_CREDENTIAL", "CREDENTIAL")]
    public void FindSensitiveMarkerRecognizesCrossPlatformSecretNames(string name, string marker)
    {
        Assert.Equal(marker, SensitiveEnvironmentPolicy.FindSensitiveMarker(name));
    }

    [Theory]
    [InlineData("LANG")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("REPLICA_SETTING")]
    public void FindSensitiveMarkerDoesNotRejectOrdinaryNames(string name)
    {
        Assert.Null(SensitiveEnvironmentPolicy.FindSensitiveMarker(name));
    }
}
