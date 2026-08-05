using Replica.Core.Execution;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Recovery;

public sealed class RecoverySecurityBoundaryTests
{
    [Fact]
    public void RegistryAllowList_AllowsOnlyExactRecoveryRunValue()
    {
        BuiltInRegistryWriteAllowList allowList = new();
        RegistryWriteRequest approved = Request(
            "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            "ReplicaRecoveryResume");

        Assert.True(allowList.IsAllowed(approved));
        Assert.False(allowList.IsAllowed(approved with { ValueName = "OtherApplication" }));
        Assert.False(allowList.IsAllowed(approved with { Hive = "HKLM", IsElevated = true }));
        Assert.False(allowList.IsAllowed(approved with
        {
            KeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce",
        }));
    }

    private static RegistryWriteRequest Request(string keyPath, string valueName) => new(
        "HKCU",
        keyPath,
        valueName,
        RegistryValueDataKind.String,
        "\"Replica.exe\" --resume-recovery 0f8fad5bd9cb469fa16570867728950e",
        false);
}
