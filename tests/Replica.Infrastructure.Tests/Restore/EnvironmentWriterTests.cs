using Replica.Core.Execution;
using Replica.Infrastructure.Restore;

namespace Replica.Infrastructure.Tests.Restore;

public sealed class EnvironmentWriterTests
{
    [Fact]
    public async Task WriteAsync_SkipsDuplicatePathAndPreservesOriginalOrder()
    {
        FakeEnvironmentStore store = new("C:\\One;C:\\Two");
        FakeNotifier notifier = new();
        EnvironmentWriter writer = new(store, notifier);

        EnvironmentWriteResult result = await writer.WriteAsync(
            new EnvironmentWriteRequest(
                "PATH",
                "c:\\two\\",
                EnvironmentVariableScope.User,
                true,
                false),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Skipped, result.State);
        Assert.Equal("PathEntryAlreadyPresent", result.ReasonCode);
        Assert.Equal("C:\\One;C:\\Two", store.Value);
        Assert.Equal(0, store.SetCount);
        Assert.Equal(0, notifier.NotifyCount);
    }

    [Fact]
    public async Task WriteAsync_AppendsNewPathWithoutReplacingExistingEntries()
    {
        FakeEnvironmentStore store = new("C:\\One;C:\\Two");
        FakeNotifier notifier = new();
        EnvironmentWriter writer = new(store, notifier);

        EnvironmentWriteResult result = await writer.WriteAsync(
            new EnvironmentWriteRequest(
                "PATH",
                "C:\\Three",
                EnvironmentVariableScope.User,
                true,
                false),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Succeeded, result.State);
        Assert.Equal("C:\\One;C:\\Two;C:\\Three", store.Value);
        Assert.Equal(1, store.SetCount);
        Assert.Equal(1, notifier.NotifyCount);
    }

    [Fact]
    public async Task WriteAsync_RejectsMachineScopeWithoutElevation()
    {
        FakeEnvironmentStore store = new(null);
        EnvironmentWriter writer = new(store, new FakeNotifier());

        EnvironmentWriteResult result = await writer.WriteAsync(
            new EnvironmentWriteRequest(
                "JAVA_HOME",
                "C:\\Java",
                EnvironmentVariableScope.Machine,
                false,
                false),
            CancellationToken.None);

        Assert.Equal(RestoreExecutionState.Failed, result.State);
        Assert.Equal("MachineScopeRequiresElevation", result.ReasonCode);
        Assert.Equal(0, store.SetCount);
    }

    [Fact]
    public async Task WriteAsync_RejectsWholePathReplacement()
    {
        FakeEnvironmentStore store = new("C:\\Existing");
        EnvironmentWriter writer = new(store, new FakeNotifier());

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(
            new EnvironmentWriteRequest(
                "PATH",
                "C:\\Replacement",
                EnvironmentVariableScope.User,
                false,
                false),
            CancellationToken.None));

        Assert.Equal("C:\\Existing", store.Value);
        Assert.Equal(0, store.SetCount);
    }

    [Fact]
    public async Task WriteAsync_RejectsSensitiveEnvironmentName()
    {
        FakeEnvironmentStore store = new(null);
        EnvironmentWriter writer = new(store, new FakeNotifier());

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(
            new EnvironmentWriteRequest(
                "SERVICE_API_KEY_VALUE",
                "secret",
                EnvironmentVariableScope.User,
                false,
                false),
            CancellationToken.None));

        Assert.Equal(0, store.SetCount);
    }

    private sealed class FakeEnvironmentStore(string? value) : IEnvironmentVariableStore
    {
        public string? Value { get; private set; } = value;

        public int SetCount { get; private set; }

        public string? Get(string name, EnvironmentVariableScope scope) => Value;

        public void Set(string name, string value, EnvironmentVariableScope scope)
        {
            Value = value;
            SetCount++;
        }
    }

    private sealed class FakeNotifier : IEnvironmentChangeNotifier
    {
        public int NotifyCount { get; private set; }

        public void NotifyEnvironmentChanged() => NotifyCount++;
    }
}
