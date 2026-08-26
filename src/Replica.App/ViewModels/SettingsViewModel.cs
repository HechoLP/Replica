using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Core.Updates;

namespace Replica.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IPortableSnapshotDialogService dialogs;
    private readonly IPortableSnapshotSettingsService portableSettings;
    private readonly IThemeService themes;
    private readonly IUpdatePreferenceService updates;

    [ObservableProperty]
    private ThemeMode _selectedTheme = ThemeMode.Light;

    [ObservableProperty]
    private string _defaultSnapshotDirectory = string.Empty;

    [ObservableProperty]
    private UpdateChannel _updateChannel = UpdateChannel.Stable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectSnapshotDirectoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "설정을 불러오지 않았습니다.";

    public SettingsViewModel(
        IThemeService themes,
        IPortableSnapshotSettingsService portableSettings,
        IUpdatePreferenceService updates,
        IPortableSnapshotDialogService dialogs)
    {
        this.themes = themes;
        this.portableSettings = portableSettings;
        this.updates = updates;
        this.dialogs = dialogs;
        Themes = Enum.GetValues<ThemeMode>();
        UpdateChannels = Enum.GetValues<UpdateChannel>();
    }

    public IReadOnlyList<ThemeMode> Themes { get; }

    public IReadOnlyList<UpdateChannel> UpdateChannels { get; }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            PortableSnapshotSettings portable = await portableSettings.GetAsync(cancellationToken);
            UpdatePreference update = await updates.GetAsync(cancellationToken);
            DefaultSnapshotDirectory = portable.DefaultSnapshotDirectory ?? string.Empty;
            UpdateChannel = update.Channel;
            SelectedTheme = themes.CurrentTheme;
            StatusText = "설정을 불러왔습니다.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void SelectSnapshotDirectory()
    {
        string? selected = dialogs.SelectFolder("기본 Snapshot 폴더 선택", DefaultSnapshotDirectory);
        if (selected is not null)
        {
            DefaultSnapshotDirectory = selected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            if (!string.IsNullOrWhiteSpace(DefaultSnapshotDirectory))
            {
                await portableSettings.SaveDefaultDirectoryAsync(
                    DefaultSnapshotDirectory,
                    cancellationToken);
            }

            UpdatePreference current = await updates.GetAsync(cancellationToken);
            await updates.SaveAsync(
                new UpdatePreference(UpdateChannel, current.SkippedVersionTag),
                cancellationToken);
            themes.ApplyTheme(SelectedTheme);
            StatusText = "기본 폴더·Theme·업데이트 채널을 저장하고 적용했습니다. 현재 표시 언어는 한국어입니다.";
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            StatusText = "설정 작업을 취소했습니다.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = "설정을 저장하지 못했습니다. 선택한 폴더가 존재하고 접근 가능한지 확인하세요. 기존 설정은 유지됩니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy;
}
