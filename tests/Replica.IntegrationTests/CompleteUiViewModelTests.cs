using System.Globalization;
using Replica.App.Services;
using Replica.App.ViewModels;
using Replica.Core.Diffing;
using Replica.Core.History;
using Replica.Core.Planning;
using Replica.Core.Portable;
using Replica.Core.Recovery;
using Replica.Core.Scanning;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Core.Updates;

namespace Replica.IntegrationTests;

public sealed class CompleteUiViewModelTests
{
    [Fact]
    public async Task SnapshotBuilder_LightweightCreatesReviewedWriteRequestWithoutSelectedFiles()
    {
        FakeSnapshotWriter writer = new();
        SnapshotBuilderViewModel viewModel = CreateBuilder(writer, out ReplicaUiSession session);
        session.LatestScan = CreateScan();
        viewModel.Configure(SnapshotType.Lightweight);
        viewModel.DestinationPath = @"C:\Snapshots\light.replica";
        viewModel.IsFinalApprovalChecked = true;

        await viewModel.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(writer.Request);
        Assert.Equal(SnapshotType.Lightweight, writer.Request.SnapshotType);
        Assert.Empty(writer.Request.Recovery.SelectedFolders);
        Assert.Equal(2, writer.Request.Inventory.Applications.Count);
        Assert.DoesNotContain(writer.Request.Inventory.Environment.Variables, variable =>
            variable.Name.Equals("API_TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SnapshotBuilder_RecoveryIncludesOnlyExplicitlySelectedFolder()
    {
        FakeSnapshotWriter writer = new();
        FakePortableDialogs portableDialogs = new() { Folder = @"C:\Users\Tester\Saved Games\Replica" };
        SnapshotBuilderViewModel viewModel = CreateBuilder(writer, out ReplicaUiSession session, portableDialogs);
        session.LatestScan = CreateScan();
        viewModel.Configure(SnapshotType.Recovery);
        viewModel.AddSelectedFolderCommand.Execute(ReplicaSelectedFolderCategory.GameSave);
        await viewModel.EstimateCommand.ExecuteAsync(null);
        viewModel.DestinationPath = @"C:\Snapshots\recovery.replica";
        viewModel.IsFinalApprovalChecked = true;

        await viewModel.CreateCommand.ExecuteAsync(null);

        ReplicaSelectedFolder folder = Assert.Single(writer.Request!.Recovery.SelectedFolders);
        Assert.Equal(portableDialogs.Folder, folder.SourcePath);
        Assert.Equal(ReplicaSelectedFolderCategory.GameSave, folder.Category);
    }

    [Fact]
    public async Task SnapshotBuilder_RecoveryRequiresCurrentEstimateAndOfflinePackRequiresVerifiedInstaller()
    {
        FakeSnapshotWriter writer = new();
        FakePortableDialogs dialogs = new() { Installer = @"C:\Downloads\Setup-x64.exe" };
        SnapshotBuilderViewModel viewModel = CreateBuilder(writer, out ReplicaUiSession session, dialogs);
        session.LatestScan = CreateScan();
        viewModel.Configure(SnapshotType.Recovery);
        viewModel.DestinationPath = @"C:\Snapshots\recovery.replica";
        viewModel.IsFinalApprovalChecked = true;

        Assert.False(viewModel.CreateCommand.CanExecute(null));

        viewModel.Configure(SnapshotType.OfflineRecoveryPack);
        viewModel.DestinationPath = @"C:\Snapshots\offline.replica";
        viewModel.IsFinalApprovalChecked = true;

        Assert.True(viewModel.IsOfflineRecoveryPack);
        Assert.False(viewModel.CreateCommand.CanExecute(null));

        viewModel.InstallerProvenance = "https://vendor.example/download";
        await viewModel.AddOfflineInstallerCommand.ExecuteAsync(null);
        await viewModel.EstimateCommand.ExecuteAsync(null);
        viewModel.IsFinalApprovalChecked = true;
        await viewModel.CreateCommand.ExecuteAsync(null);

        ReplicaOfflineInstaller installer = Assert.Single(writer.Request!.Recovery.OfflineInstallers);
        Assert.Equal(dialogs.Installer, installer.SourcePath);
        Assert.Equal(new string('B', 64), installer.ExpectedSha256);
        Assert.Equal("CN=Trusted Vendor", installer.Publisher);
    }

    [Fact]
    public void SnapshotBuilder_ScopeChangeInvalidatesPreviousFinalApproval()
    {
        SnapshotBuilderViewModel viewModel = CreateBuilder(new FakeSnapshotWriter(), out ReplicaUiSession session);
        session.LatestScan = CreateScan();
        viewModel.DestinationPath = @"C:\Snapshots\reviewed.replica";
        viewModel.IsFinalApprovalChecked = true;

        viewModel.Categories.First(category => category.Id == "fonts").IsSelected = false;

        Assert.False(viewModel.IsFinalApprovalChecked);
        Assert.False(viewModel.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task SnapshotBuilder_DestinationPickerUsesPersistedDefaultDirectory()
    {
        FakePortableDialogs dialogs = new() { Folder = @"D:\Replica" };
        FakePortableSettings settings = new() { DefaultDirectory = @"D:\Backups" };
        SnapshotBuilderViewModel viewModel = CreateBuilder(
            new FakeSnapshotWriter(),
            out _,
            dialogs,
            settings);

        await viewModel.SelectDestinationCommand.ExecuteAsync(null);

        Assert.Equal(settings.DefaultDirectory, dialogs.LastInitialDirectory);
        Assert.StartsWith(dialogs.Folder, viewModel.DestinationPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiffViewer_FilterAndSearchKeepOnlyRequestedDifference()
    {
        DiffViewerViewModel viewModel = new();
        viewModel.Display(new EnvironmentDiffResult(
            DiffRestoreMode.Safe,
            [
                CreateDiff(DiffType.Missing, "Visual Studio Code"),
                CreateDiff(DiffType.Extra, "Contoso Player"),
            ],
            new EnvironmentSimilarityScore(50, 100, 0, 0, 0, [])));

        viewModel.SelectedFilter = "누락";
        viewModel.SearchText = "code";

        DiffItemRowViewModel row = Assert.Single(viewModel.Items);
        Assert.Equal("Visual Studio Code", row.Name);
        Assert.Single(viewModel.TreeGroups);
    }

    [Fact]
    public void RestorePlan_ModeSelectionExplainsSafetyPolicyWithoutExecution()
    {
        RestoreDryRunViewModel viewModel = new();

        viewModel.SelectModeCommand.Execute(DiffRestoreMode.Exact);

        Assert.Equal(DiffRestoreMode.Exact, viewModel.SelectedMode);
        Assert.Contains("수동", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Null(viewModel.ReviewedPlan);
    }

    [Fact]
    public async Task Settings_SaveAppliesThemeAndUpdateChannel()
    {
        FakeThemeService themes = new();
        FakeUpdatePreferences updates = new();
        SettingsViewModel viewModel = new(
            themes,
            new FakePortableSettings(),
            updates,
            new FakePortableDialogs());
        viewModel.SelectedTheme = ThemeMode.Dark;
        viewModel.UpdateChannel = UpdateChannel.Beta;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ThemeMode.Dark, themes.CurrentTheme);
        Assert.Equal(UpdateChannel.Beta, updates.Preference.Channel);
    }

    private static SnapshotBuilderViewModel CreateBuilder(
        FakeSnapshotWriter writer,
        out ReplicaUiSession session,
        FakePortableDialogs? portableDialogs = null,
        FakePortableSettings? portableSettings = null)
    {
        session = new ReplicaUiSession();
        return new SnapshotBuilderViewModel(
            session,
            writer,
            new FakeEstimator(),
            new FakeOfflineInstallerInspectionService(),
            new FakeAppVersionService(),
            new FakeHistoryService(),
            portableDialogs ?? new FakePortableDialogs(),
            portableSettings ?? new FakePortableSettings(),
            new FakeRecoveryDialogs());
    }

    private static EnvironmentScanResult CreateScan() => new(
        new WindowsEnvironmentInfo(
            "Windows 11 Pro",
            "10.0",
            "26100",
            "X64",
            "ko-KR",
            "Korea Standard Time",
            "TEST-PC",
            false,
            true,
            true,
            true),
        [
            new ScannedApplication("Visual Studio Code", "1.99.0", "Microsoft", null, "Microsoft.VisualStudioCode", "winget", "User", "X64", "Exe", true),
            new ScannedApplication("Git", "2.50.0", "Git", null, "Git.Git", "winget", "Machine", "X64", "Exe", true),
        ],
        [
            new ScannedEnvironmentVariable("EDITOR", "code", "User", false, null),
            new ScannedEnvironmentVariable("API_TOKEN", null, "User", true, "SensitiveName"),
        ],
        [new ScannedPathEntry(@"C:\Tools", "User", 0, false, true)],
        [new ScannedFont("Cascadia Code", "Regular", "User", @"C:\Fonts\cascadia.ttf", true)],
        ["vscode"],
        [],
        new EnvironmentScanSummary(2, 2, 0, 2, 1, 0));

    private static DiffItem CreateDiff(DiffType type, string name) => new(
        type,
        DiffArea.Applications,
        name,
        name,
        "1.0",
        null,
        null,
        type == DiffType.Missing,
        type == DiffType.Extra,
        false,
        DiffRiskLevel.Low,
        false,
        false,
        0,
        type.ToString());

    private sealed class FakeSnapshotWriter : ISnapshotWriter
    {
        public ReplicaSnapshotWriteRequest? Request { get; private set; }

        public Task<ReplicaSnapshotManifest> WriteAsync(
            ReplicaSnapshotWriteRequest request,
            IProgress<ReplicaSnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            progress?.Report(new ReplicaSnapshotProgress(ReplicaSnapshotStage.Completed, 1, 1, 1, 1));
            return Task.FromResult(new ReplicaSnapshotManifest(
                ReplicaSnapshotManifest.CurrentSchemaVersion,
                request.ProductVersion,
                Guid.NewGuid(),
                request.SnapshotType,
                DateTimeOffset.UtcNow,
                request.Machine.MachineName,
                request.Machine.Windows.Version,
                request.Machine.Architecture,
                request.Machine.Locale,
                request.Capabilities,
                request.Exclusions,
                new ReplicaSnapshotMetadata(request.Machine, [])));
        }
    }

    private sealed class FakeEstimator : ISnapshotSelectionEstimator
    {
        public Task<ReplicaSelectionEstimate> EstimateAsync(
            IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
            IReadOnlyList<ReplicaOfflineInstaller> offlineInstallers,
            CancellationToken cancellationToken) => Task.FromResult(
                new ReplicaSelectionEstimate([], [], 0));
    }

    private sealed class FakeAppVersionService : IAppVersionService
    {
        public Version CurrentVersion { get; } = new(0, 1, 0);

        public string DisplayVersion => "0.1.0";
    }

    private sealed class FakePortableDialogs : IPortableSnapshotDialogService
    {
        public string? Folder { get; set; }

        public string? Installer { get; set; }

        public string? LastInitialDirectory { get; private set; }

        public string? SelectSnapshot() => null;

        public string? SelectFolder(string title, string? initialDirectory)
        {
            LastInitialDirectory = initialDirectory;
            return Folder;
        }

        public bool ConfirmInstallerDownload(string destinationDirectory, string versionDescription) => false;

        public string? SelectOfflineInstaller(string? initialDirectory) => Installer;
    }

    private sealed class FakeOfflineInstallerInspectionService : IOfflineInstallerInspectionService
    {
        public Task<OfflineInstallerInspection> InspectAsync(
            string installerPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new OfflineInstallerInspection(
                installerPath,
                "Example Setup",
                "1.0",
                "x64",
                1234,
                new string('B', 64),
                OfflineInstallerSignatureStatus.Trusted,
                "CN=Trusted Vendor",
                new string('C', 64),
                "Trusted"));
        }
    }

    private sealed class FakeRecoveryDialogs : IRecoveryDialogService
    {
        public string? SelectRecoverySnapshot() => null;

        public string? SelectOfflineInstallerExportFolder() => null;

        public char[]? RequestPassword(string title, string message) => null;

        public bool Confirm(string title, string message) => false;

        public void ShowError(string title, string message) { }
    }

    private sealed class FakeHistoryService : ISnapshotHistoryService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SnapshotHistoryEntry> AddSnapshotAsync(string snapshotPath, ReadOnlyMemory<char> password, int? environmentScore, CancellationToken cancellationToken) =>
            Task.FromResult<SnapshotHistoryEntry>(null!);

        public Task<IReadOnlyList<SnapshotHistoryEntry>> GetSnapshotsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SnapshotHistoryEntry>>([]);

        public Task UpdateSnapshotAsync(Guid snapshotId, SnapshotHistoryUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteSnapshotAsync(Guid snapshotId, bool deleteSnapshotFile, bool userConfirmed, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SnapshotComparisonResult> CompareAsync(Guid fromSnapshotId, Guid toSnapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PastStateRestorePlan> CreatePastStateRestorePlanAsync(Guid snapshotId, IReadOnlyList<RecoveryPathMapping> mappings, ReadOnlyMemory<char> password, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordRestoreAsync(RestoreHistoryRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordRollbackAsync(RollbackHistoryRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePackageMatchingOverrideAsync(PackageMatchingOverride matchingOverride, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageMatchingOverride>> GetPackageMatchingOverridesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackageMatchingOverride>>([]);
    }

    private sealed class FakeThemeService : IThemeService
    {
        public ThemeMode CurrentTheme { get; private set; } = ThemeMode.Light;

        public void ApplyTheme(ThemeMode theme) => CurrentTheme = theme;
    }

    private sealed class FakeLanguageService : IAppLanguageService
    {
        public string CurrentLanguageCode { get; private set; } = "ko-KR";

        public void ApplyLanguage(string languageCode) => CurrentLanguageCode = languageCode;
    }

    private sealed class FakePortableSettings : IPortableSnapshotSettingsService
    {
        public string? DefaultDirectory { get; set; }

        public Task<PortableSnapshotSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PortableSnapshotSettings(DefaultDirectory));

        public Task SaveDefaultDirectoryAsync(string directoryPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeUpdatePreferences : IUpdatePreferenceService
    {
        public UpdatePreference Preference { get; private set; } = UpdatePreference.Default;

        public Task<UpdatePreference> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Preference);

        public Task SaveAsync(UpdatePreference preference, CancellationToken cancellationToken)
        {
            Preference = preference;
            return Task.CompletedTask;
        }
    }
}
