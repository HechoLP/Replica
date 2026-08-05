using Replica.Core.Recovery;
using Replica.Infrastructure.Paths;
using Replica.Infrastructure.Recovery;

namespace Replica.Infrastructure.Tests.Recovery;

public sealed class JsonRecoverySessionStoreTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"ReplicaRecoveryStoreTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoad_PreservesRestartResumeState()
    {
        ReplicaPathProvider paths = new(_testRoot);
        JsonRecoverySessionStore store = new(paths);
        RecoveryWizardSession session = CreateSession();

        await store.SaveAsync(session, default);
        RecoveryWizardSession? loaded = await store.LoadAsync(session.SessionId, default);

        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
        Assert.Equal(session.SnapshotId, loaded.SnapshotId);
        Assert.Equal(session.Status, loaded.Status);
        Assert.Equal(session.CurrentStep, loaded.CurrentStep);
        Assert.Equal(session.SnapshotEncrypted, loaded.SnapshotEncrypted);
        Assert.True(loaded!.RestartApproved);
        Assert.Contains("install", loaded.CompletedActionIds);
    }

    [Fact]
    public async Task Load_RejectsTamperedState()
    {
        ReplicaPathProvider paths = new(_testRoot);
        JsonRecoverySessionStore store = new(paths);
        RecoveryWizardSession session = CreateSession();
        await store.SaveAsync(session, default);
        string statePath = Path.Combine(
            paths.RecoveryDirectory,
            "Sessions",
            session.SessionId,
            "session.json");
        byte[] state = await File.ReadAllBytesAsync(statePath);
        state[^2] ^= 1;
        await File.WriteAllBytesAsync(statePath, state);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(
            session.SessionId,
            default));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("..\\escape")]
    public async Task Load_RejectsInvalidSessionIdentifier(string sessionId)
    {
        JsonRecoverySessionStore store = new(new ReplicaPathProvider(_testRoot));

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(sessionId, default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static RecoveryWizardSession CreateSession()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new RecoveryWizardSession(
            Guid.NewGuid().ToString("N"),
            "C:\\Snapshots\\recovery.replica",
            Guid.NewGuid(),
            RecoveryWizardStatus.AwaitingResumeConfirmation,
            RecoveryWizardStep.ResumeAfterRestart,
            "OLD-PC",
            "11 24H2",
            "x64",
            "ko-KR",
            true,
            null,
            [],
            [],
            [],
            [],
            ["install"],
            [],
            80,
            null,
            true,
            true,
            now,
            now);
    }
}
