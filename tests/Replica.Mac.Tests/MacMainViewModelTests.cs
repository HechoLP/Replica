using Replica.Core.Platforms;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Core.Updates;
using Replica.Mac.Infrastructure.Snapshots;
using Replica.Mac.Infrastructure.Updates;
using Replica.Mac.Services;
using Replica.Mac.ViewModels;

namespace Replica.Mac.Tests;

public sealed class MacMainViewModelTests
{
    [Fact]
    public async Task UpdateCheckUsesStableChannelByDefault()
    {
        RecordingUpdateService updateService = new();
        MainViewModel viewModel = CreateViewModel(updateService);

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateChannel.Stable, updateService.ReceivedChannel);
        Assert.Equal("최신 버전입니다.", viewModel.Status);
    }

    [Fact]
    public async Task UnexpectedServiceFailureIsConvertedToSafeStatus()
    {
        MainViewModel viewModel = CreateViewModel(new ThrowingUpdateService());

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        Assert.Contains("안전하게 중단", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task AssociatedSnapshotPathUsesTheValidatedSnapshotReader()
    {
        RecordingSnapshotReader reader = new();
        MainViewModel viewModel = CreateViewModel(new RecordingUpdateService(), reader);
        string path = Path.Combine(Path.GetTempPath(), "associated.replica");

        await viewModel.OpenSnapshotPathAsync(path);

        Assert.Equal(Path.GetFullPath(path), reader.ReceivedPath);
        Assert.Contains("무결성 검증을 통과", viewModel.Status, StringComparison.Ordinal);
    }

    private static MainViewModel CreateViewModel(
        IMacUpdateService updateService,
        ISnapshotReader? snapshotReader = null)
    {
        return new MainViewModel(
            new UnusedScanner(),
            new UnusedSnapshotService(),
            snapshotReader ?? new UnusedSnapshotReader(),
            updateService,
            new UnusedFileDialogs());
    }

    private sealed class RecordingUpdateService : IMacUpdateService
    {
        public UpdateChannel? ReceivedChannel { get; private set; }

        public Task<MacUpdateInfo> CheckAsync(
            string currentVersion,
            UpdateChannel channel,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedChannel = channel;
            return Task.FromResult(new MacUpdateInfo(
                false,
                currentVersion,
                null,
                null,
                null,
                null,
                null,
                "최신 버전입니다."));
        }
    }

    private sealed class ThrowingUpdateService : IMacUpdateService
    {
        public Task<MacUpdateInfo> CheckAsync(
            string currentVersion,
            UpdateChannel channel,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Sensitive implementation detail");
        }
    }

    private sealed class UnusedScanner : IPlatformEnvironmentScanner
    {
        public Task<PlatformScanResult> ScanAsync(
            IProgress<PlatformScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedSnapshotService : IMacLightweightSnapshotService
    {
        public Task<ReplicaSnapshotManifest> CreateAsync(
            string destinationPath,
            string productVersion,
            PlatformScanResult scan,
            IProgress<ReplicaSnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedSnapshotReader : ISnapshotReader
    {
        public Task<ReplicaSnapshotReadResult> ReadAsync(
            ReplicaSnapshotReadRequest request,
            IProgress<ReplicaSnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingSnapshotReader : ISnapshotReader
    {
        public string? ReceivedPath { get; private set; }

        public Task<ReplicaSnapshotReadResult> ReadAsync(
            ReplicaSnapshotReadRequest request,
            IProgress<ReplicaSnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedPath = request.SnapshotPath;
            ReplicaWindowsInfo windows = new("macOS", "13.0", "22A", "Arm64", "en-US", "UTC", []);
            ReplicaMachineInfo machine = new(
                "Mac",
                windows,
                "Arm64",
                "en-US",
                new ReplicaPlatformInfo(
                    ReplicaPlatformFamily.MacOS,
                    windows.Edition,
                    windows.Version,
                    windows.Build,
                    windows.Architecture,
                    windows.Locale,
                    windows.TimeZone,
                    windows.Capabilities));
            ReplicaSnapshotManifest manifest = new(
                ReplicaSnapshotManifest.CurrentSchemaVersion,
                "0.2.0",
                Guid.NewGuid(),
                SnapshotType.Lightweight,
                DateTimeOffset.UtcNow,
                machine.MachineName,
                windows.Version,
                machine.Architecture,
                machine.Locale,
                [],
                [],
                new ReplicaSnapshotMetadata(machine, []),
                SourcePlatform: ReplicaPlatformFamily.MacOS);
            return Task.FromResult(new ReplicaSnapshotReadResult(
                manifest,
                new ReplicaSnapshotInventory(
                    [],
                    new ReplicaEnvironmentInventory([], []),
                    windows,
                    [],
                    []),
                new ReplicaRecoveryOptions("RenameAndKeepBoth", 0, null, [], []),
                [],
                []));
        }
    }

    private sealed class UnusedFileDialogs : IMacFileDialogService
    {
        public Task<string?> PickSnapshotToOpenAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> PickSnapshotDestinationAsync(
            string suggestedName,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> OpenUriAsync(Uri uri)
        {
            throw new NotSupportedException();
        }
    }
}
