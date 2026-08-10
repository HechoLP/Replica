using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replica.App.Services;
using Replica.Core.Portable;
using Replica.Core.Services;
using Replica.Core.Updates;

namespace Replica.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppLanguageService languages;
    private readonly IPortableSnapshotDialogService dialogs;
    private readonly IPortableSnapshotSettingsService portableSettings;
    private readonly IThemeService themes;
    private readonly IUpdatePreferenceService updates;

    [ObservableProperty]
    private LanguageOptionViewModel? _selectedLanguage;

    [ObservableProperty]
    private ThemeMode _selectedTheme = ThemeMode.Light;

    [ObservableProperty]
    private string _defaultSnapshotDirectory = string.Empty;

    [ObservableProperty]
    private int _logRetentionDays = 14;

    [ObservableProperty]
    private UpdateChannel _updateChannel = UpdateChannel.Stable;

    [ObservableProperty]
    private bool _privacyModeEnabled = true;

    [ObservableProperty]
    private bool _showAdvancedSettings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectSnapshotDirectoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "설정을 불러오지 않았습니다.";

    public SettingsViewModel(
        IThemeService themes,
        IAppLanguageService languages,
        IPortableSnapshotSettingsService portableSettings,
        IUpdatePreferenceService updates,
        IPortableSnapshotDialogService dialogs)
    {
        this.themes = themes;
        this.languages = languages;
        this.portableSettings = portableSettings;
        this.updates = updates;
        this.dialogs = dialogs;
        Languages =
        [
            new("ko-KR", "한국어"),
            new("en-US", "English"),
        ];
        SelectedLanguage = Languages[0];
        Themes = Enum.GetValues<ThemeMode>();
        UpdateChannels = Enum.GetValues<UpdateChannel>();
    }

    public IReadOnlyList<LanguageOptionViewModel> Languages { get; }

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
            SelectedLanguage = Languages.First(option =>
                option.Code.Equals(languages.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));
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
        if (SelectedLanguage is null || LogRetentionDays is < 1 or > 365)
        {
            StatusText = "로그 보존 기간은 1~365일이어야 합니다.";
            return;
        }

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
            languages.ApplyLanguage(SelectedLanguage.Code);
            StatusText = "설정을 저장하고 Theme·언어·업데이트 Channel을 적용했습니다.";
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
            StatusText = $"설정을 처리하지 못했습니다. ({exception.GetType().Name})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy;
}

public sealed record LanguageOptionViewModel(string Code, string DisplayName);
