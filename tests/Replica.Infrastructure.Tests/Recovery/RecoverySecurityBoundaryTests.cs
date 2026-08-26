using Replica.Core.Execution;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Recovery;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Recovery;

public sealed class RecoverySecurityBoundaryTests
{
    [Fact]
    public async Task CleanupSessionPayloads_RemovesPlaintextButPreservesSessionMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ReplicaPayloadCleanup-{Guid.NewGuid():N}");
        try
        {
            ReplicaPathProvider paths = new(root);
            string sessionId = Guid.NewGuid().ToString("N");
            string sessionRoot = Path.Combine(paths.RecoveryDirectory, "Sessions", sessionId);
            string legacyPayload = Path.Combine(sessionRoot, "Payload");
            string leasedPayload = Path.Combine(sessionRoot, "Payloads", "payload-old");
            Directory.CreateDirectory(legacyPayload);
            Directory.CreateDirectory(leasedPayload);
            await File.WriteAllTextAsync(Path.Combine(legacyPayload, "secret.txt"), "secret");
            await File.WriteAllTextAsync(Path.Combine(leasedPayload, "secret.txt"), "secret");
            string sessionState = Path.Combine(sessionRoot, "session.json");
            await File.WriteAllTextAsync(sessionState, "state");
            RecoveryPayloadMaterializer materializer = new(paths);

            bool deleted = await materializer.CleanupSessionPayloadsAsync(
                sessionId,
                CancellationToken.None);

            Assert.True(deleted);
            Assert.False(Directory.Exists(legacyPayload));
            Assert.False(Directory.Exists(Path.Combine(sessionRoot, "Payloads")));
            Assert.True(File.Exists(sessionState));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupSessionPayloads_ReportsLockedPlaintextUntilDeletionSucceeds()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ReplicaPayloadLocked-{Guid.NewGuid():N}");
        try
        {
            ReplicaPathProvider paths = new(root);
            string sessionId = Guid.NewGuid().ToString("N");
            string payload = Path.Combine(
                paths.RecoveryDirectory,
                "Sessions",
                sessionId,
                "Payloads",
                "payload-locked");
            Directory.CreateDirectory(payload);
            string plaintext = Path.Combine(payload, "secret.txt");
            await File.WriteAllTextAsync(plaintext, "secret");
            RecoveryPayloadMaterializer materializer = new(paths);

            bool first;
            using (FileStream locked = new(
                plaintext,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                first = await materializer.CleanupSessionPayloadsAsync(
                    sessionId,
                    CancellationToken.None);
                Assert.True(File.Exists(plaintext));
            }

            bool second = await materializer.CleanupSessionPayloadsAsync(
                sessionId,
                CancellationToken.None);

            Assert.False(first);
            Assert.True(second);
            Assert.False(Directory.Exists(Path.Combine(
                paths.RecoveryDirectory,
                "Sessions",
                sessionId,
                "Payloads")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
