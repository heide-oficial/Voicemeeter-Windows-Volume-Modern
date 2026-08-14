using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using VMWV.Core;
using VMWV.Core.Services;
using VMWV.Core.Settings;
using VMWV.Core.Voicemeeter;
using VMWV.Core.Volume;
using VMWV_App.Models;
using VMWV_App.Localization;

namespace VMWV_App.ViewModels;

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxDiagnosticEntries = 200;
    private const int MaxRecentEventEntries = 8;
    private static readonly TimeSpan SettingsSaveDebounceDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EngineRestartSettleDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan EndpointRetryDelay = TimeSpan.FromMilliseconds(400);
    private readonly JsonSettingsStore _settingsStore;
    private readonly IAudioEndpointService _audioEndpointService;
    private readonly IVoicemeeterClient _voicemeeterClient;
    private readonly IStartupService _startupService;
    private readonly IUpdateService _updateService;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _voicemeeterConnectionLock = new(1, 1);
    private readonly SemaphoreSlim _engineRestartLock = new(1, 1);
    private readonly SemaphoreSlim _volumeRestoreLock = new(1, 1);
    private readonly SemaphoreSlim _autoConnectSignal = new(0, 1);
    private readonly Channel<VolumeRestoreRequest> _volumeRestoreRequests = Channel.CreateBounded<VolumeRestoreRequest>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly VolumeRecoveryCoordinator _volumeRecovery = new();
    private readonly object _voicemeeterSyncLock = new();
    private readonly object _settingsSaveLock = new();
    private readonly object _selectedTargetsLock = new();
    private readonly Dictionary<string, VoicemeeterBindingTarget> _voicemeeterTargets = [];
    private readonly Task _volumeRestoreWorker;
    private IReadOnlyList<VoicemeeterBindingTarget> _selectedTargets = [];
    private AppSettings _settings;
    private int? _pendingVolume;
    private bool? _pendingMute;
    private CancellationTokenSource? _settingsSaveDebounce;
    private string? _pendingSettingsPayload;
    private bool _isLoading;
    private bool _isInitialized;
    private bool _autoConnectStarted;
    private bool _manualDisconnectRequested;
    private bool _fallbackPollingStarted;
    private bool _restartOnLaunchApplied;
    private bool _volumeSyncWorkerRunning;
    private bool _muteSyncWorkerRunning;
    private CancellationTokenSource? _deviceRecoveryDebounce;
    private DateTimeOffset _lastAudioCallback = DateTimeOffset.MinValue;
    private int _lastObservedVolume;
    private bool _lastObservedMute;

    public MainPageViewModel(
        IAudioEndpointService audioEndpointService,
        IVoicemeeterClient voicemeeterClient,
        IStartupService startupService,
        IUpdateService updateService)
    {
        _audioEndpointService = audioEndpointService;
        _voicemeeterClient = voicemeeterClient;
        _startupService = startupService;
        _updateService = updateService;
        _settingsStore = new JsonSettingsStore(AppSettingsPaths.DefaultSettingsPath);
        _settings = _settingsStore.LoadOrCreate();
        LoadFromSettings();
        LoadBindingTargets();
        AttachServiceEvents();
        _volumeRestoreWorker = ProcessVolumeRestoreRequestsAsync();
        AddLog(T("Log.Startup"), TF("Log.SettingsLoaded", AppSettingsPaths.DefaultSettingsPath));
        AddLog(T("Log.Runtime"), T("Log.ServicesConfigured"));
    }

    public ObservableCollection<BindingTargetItem> BindingTargets { get; } = [];

    public ObservableCollection<BindingTargetItem> StripBindingTargets { get; } = [];

    public ObservableCollection<BindingTargetItem> BusBindingTargets { get; } = [];

    [ObservableProperty]
    public partial bool HasStripBindingTargets { get; set; } = true;

    [ObservableProperty]
    public partial bool HasBusBindingTargets { get; set; } = true;

    public ObservableCollection<string> DefinedStripBindings { get; } = [];

    public ObservableCollection<string> DefinedBusBindings { get; } = [];

    public ObservableCollection<DiagnosticLogEntry> Diagnostics { get; } = [];

    public ObservableCollection<DiagnosticLogEntry> RecentEvents { get; } = [];

    public ObservableCollection<LanguageOption> LogoVariantOptions { get; } = [];

    public ObservableCollection<LanguageOption> LayoutModeOptions { get; } = [];

    public IReadOnlyList<LanguageOption> Languages => LocalizationService.Current.AvailableLanguages;

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = T("Status.StartingTitle");

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = T("Status.StartingMessage");

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string AppStatus { get; set; } = T("Status.Ready");

    [ObservableProperty]
    public partial string AppStatusDetail { get; set; } = T("Status.SingleProcess");

    [ObservableProperty]
    public partial string WindowsAudioStatus { get; set; } = T("Status.Starting");

    [ObservableProperty]
    public partial string WindowsAudioDetail { get; set; } = T("Status.WaitingEndpoint");

    [ObservableProperty]
    public partial string VoicemeeterStatus { get; set; } = T("Status.Disconnected");

    [ObservableProperty]
    public partial string VoicemeeterDetail { get; set; } = T("Status.NativeClientDisconnected");

    [ObservableProperty]
    public partial string DefinedBindingsStatus { get; set; } = T("Status.NoBindings");

    [ObservableProperty]
    public partial string DefinedBindingsDetail { get; set; } = T("Status.NoActiveBindings");

    [ObservableProperty]
    public partial string ActiveTargetsText { get; set; } = T("Status.NoActiveTargets");

    [ObservableProperty]
    public partial string LastVolumeSyncText { get; set; } = T("Status.VolumePending");

    [ObservableProperty]
    public partial string LastMuteSyncText { get; set; } = T("Status.MutePending");

    [ObservableProperty]
    public partial string LastVoicemeeterError { get; set; } = T("Status.NoVoicemeeterErrors");

    [ObservableProperty]
    public partial bool HasDefinedStripBindings { get; set; }

    [ObservableProperty]
    public partial bool HasDefinedBusBindings { get; set; }

    [ObservableProperty]
    public partial bool IsVoicemeeterConnected { get; set; }

    [ObservableProperty]
    public partial string VoicemeeterConnectionActionText { get; set; } = T("Common.ConnectVoicemeeter");

    [ObservableProperty]
    public partial string ConnectionStatusText { get; set; } = T("Status.VoicemeeterDisconnected");

    [ObservableProperty]
    public partial string LogoVariant { get; set; } = "Color";

    [ObservableProperty]
    public partial string LogoImagePath { get; set; } = "ms-appx:///Assets/Brand/logo.png";

    [ObservableProperty]
    public partial string LayoutMode { get; set; } = "Compact";

    [ObservableProperty]
    public partial string Language { get; set; } = "en-us";

    [ObservableProperty]
    public partial bool HideSupportPage { get; set; }

    [ObservableProperty]
    public partial string UpdateTitle { get; set; } = T("Settings.Updates.CheckingTitle");

    [ObservableProperty]
    public partial string UpdateMessage { get; set; } = T("Settings.Updates.CheckingMessage");

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial Uri LatestReleaseUri { get; set; } = new("https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases/latest");

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool SyncMute { get; set; }

    [ObservableProperty]
    public partial bool RememberVolume { get; set; }

    [ObservableProperty]
    public partial bool LimitDbGainToZero { get; set; }

    [ObservableProperty]
    public partial bool LinearVolumeScale { get; set; }

    [ObservableProperty]
    public partial bool PreventVolumeSpikes { get; set; }

    [ObservableProperty]
    public partial bool RestartOnDeviceChange { get; set; }

    [ObservableProperty]
    public partial bool RestartOnAnyDeviceChange { get; set; }

    [ObservableProperty]
    public partial bool RestartOnResume { get; set; }

    [ObservableProperty]
    public partial bool ApplyCrackleFix { get; set; }

    [ObservableProperty]
    public partial double GainMin { get; set; }

    [ObservableProperty]
    public partial double GainMax { get; set; }

    [ObservableProperty]
    public partial double PollingRate { get; set; }

    public string VersionText => $"v{AppInfo.VersionText}";

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        await SyncStartupRegistrationAsync();

        try
        {
            await _audioEndpointService.StartAsync(_shutdown.Token);
            ApplyAudioSnapshot(_audioEndpointService.Current);
            _lastObservedVolume = _audioEndpointService.Current.Volume;
            _volumeRecovery.Seed(
                RecoveryVolumeFromSnapshot(_audioEndpointService.Current),
                RememberVolume ? _settings.InitialVolume : null);
            _lastObservedMute = _audioEndpointService.Current.IsMuted;
            StartFallbackPolling();
            StatusTitle = T("Status.WindowsAudioConnected");
            StatusMessage = T("Status.EndpointCallbacks");
            StatusSeverity = InfoBarSeverity.Success;
            AddLog(T("Log.Audio"), TF("Log.Monitoring", _audioEndpointService.Current.DisplayName));
        }
        catch (Exception ex)
        {
            WindowsAudioStatus = T("Common.Error");
            WindowsAudioDetail = ex.Message;
            StatusTitle = T("Status.AudioServiceFailed");
            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Error;
            AddLog(T("Log.Audio"), TF("Log.FailedStart", ex.Message));
        }

        StartAutoConnect();
        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        AddLog(T("Log.Status"), T("Status.RefreshedTitle"));
        StatusTitle = T("Status.RefreshedTitle");
        StatusMessage = T("Status.RefreshedMessage");
        StatusSeverity = InfoBarSeverity.Success;
    }

    [RelayCommand]
    private async Task ConnectVoicemeeterAsync()
    {
        await ConnectVoicemeeterAsync(isAutomatic: false);
    }

    [RelayCommand]
    private async Task ToggleVoicemeeterConnectionAsync()
    {
        if (_voicemeeterClient.State == VoicemeeterConnectionState.Connected)
        {
            await DisconnectVoicemeeterAsync();
            return;
        }

        await ConnectVoicemeeterAsync(isAutomatic: false);
    }

    private async Task ConnectVoicemeeterAsync(bool isAutomatic)
    {
        var lockTaken = false;
        try
        {
            await _voicemeeterConnectionLock.WaitAsync(_shutdown.Token);
            lockTaken = true;
            if (!isAutomatic)
            {
                _manualDisconnectRequested = false;
                SignalAutoConnect();
            }

            if (_voicemeeterClient.State == VoicemeeterConnectionState.Connected)
            {
                await RefreshVoicemeeterTargetsAsync();
                QueueCurrentAudioSync();
                return;
            }

            VoicemeeterStatus = T("Status.Connecting");
            VoicemeeterDetail = T("Status.WaitingVoicemeeter");
            StatusTitle = T("Status.ConnectingTitle");
            StatusMessage = isAutomatic
                ? T("Status.AutoConnecting")
                : T("Status.OpenVoicemeeter");
            StatusSeverity = InfoBarSeverity.Informational;
            AddLog(T("Log.Voicemeeter"), isAutomatic ? T("Status.AutoConnecting") : T("Status.ConnectingTitle"));

            await _voicemeeterClient.ConnectAsync(_shutdown.Token);
            await RefreshVoicemeeterTargetsAsync();

            VoicemeeterStatus = T("Status.Connected");
            VoicemeeterDetail = _voicemeeterClient.Edition;
            IsVoicemeeterConnected = true;
            ConnectionStatusText = T("Status.VoicemeeterConnected");
            VoicemeeterConnectionActionText = T("Common.Disconnect");
            StatusTitle = T("Status.VoicemeeterConnected");
            StatusMessage = TF("Status.ConnectedTo", _voicemeeterClient.Edition);
            StatusSeverity = InfoBarSeverity.Success;
            AddLog(T("Log.Voicemeeter"), TF("Status.ConnectedTo", _voicemeeterClient.Edition));
            if (!_restartOnLaunchApplied && _settings.IsToggleEnabled("restart_audio_engine_on_app_launch"))
            {
                _restartOnLaunchApplied = true;
                await RestartAudioEngineCoreAsync(T("Log.RestartLaunch"), _shutdown.Token);
            }
            else
            {
                QueueCurrentAudioSync();
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(T("Log.Voicemeeter"), T("Log.ConnectionCancelled"));
        }
        catch (Exception ex)
        {
            VoicemeeterStatus = T("Common.Error");
            VoicemeeterDetail = ex.Message;
            IsVoicemeeterConnected = false;
            ConnectionStatusText = T("Status.VoicemeeterDisconnected");
            VoicemeeterConnectionActionText = T("Common.ConnectVoicemeeter");
            StatusTitle = T("Status.ConnectionFailed");
            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Error;
            LastVoicemeeterError = ex.Message;
            AddLog(T("Log.Voicemeeter"), $"{T("Status.ConnectionFailed")}: {ex.Message}");
        }
        finally
        {
            if (lockTaken)
            {
                _voicemeeterConnectionLock.Release();
            }
        }
    }

    private async Task DisconnectVoicemeeterAsync()
    {
        var lockTaken = false;
        try
        {
            _manualDisconnectRequested = true;
            await _voicemeeterConnectionLock.WaitAsync(_shutdown.Token);
            lockTaken = true;
            await _voicemeeterClient.DisconnectAsync(_shutdown.Token);
            _voicemeeterTargets.Clear();
            UpdateSelectedTargetsCache();
            VoicemeeterStatus = T("Status.Disconnected");
            VoicemeeterDetail = T("Status.NativeClientDisconnected");
            IsVoicemeeterConnected = false;
            ConnectionStatusText = T("Status.VoicemeeterDisconnected");
            VoicemeeterConnectionActionText = T("Common.ConnectVoicemeeter");
            StatusTitle = T("Status.VoicemeeterDisconnected");
            StatusMessage = T("Status.DisconnectedFromVoicemeeter");
            StatusSeverity = InfoBarSeverity.Warning;
            LastVolumeSyncText = T("Status.VolumePaused");
            LastMuteSyncText = T("Status.MutePaused");
            AddLog(T("Log.Voicemeeter"), T("Status.DisconnectedFromVoicemeeter"));
        }
        catch (OperationCanceledException)
        {
            AddLog(T("Log.Voicemeeter"), T("Log.DisconnectCancelled"));
        }
        catch (Exception ex)
        {
            ReportVoicemeeterCommandFailure(T("Command.Disconnect"), ex);
        }
        finally
        {
            if (lockTaken)
            {
                _voicemeeterConnectionLock.Release();
            }
        }
    }

    [RelayCommand]
    private async Task ShowVoicemeeterAsync()
    {
        try
        {
            await EnsureVoicemeeterConnectedAsync();
            await _voicemeeterClient.ShowAsync(_shutdown.Token);
            AddLog(T("Log.Voicemeeter"), T("Log.ShowSent"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure(T("Command.Show"), ex);
        }
    }

    [RelayCommand]
    private async Task RestartAudioEngineAsync()
    {
        try
        {
            await EnsureVoicemeeterConnectedAsync();
            await RestartAudioEngineCoreAsync(T("Log.RestartCommand"), _shutdown.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure(T("Command.RestartEngine"), ex);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _ = SetStartWithWindowsAsync(value);
    }
    partial void OnCloseToTrayChanged(bool value)
    {
        SaveBoolean(value, setting => setting.CloseToTray = value, T("Setting.CloseToTray"), saveImmediately: true);
        if (App.Window is VMWV_App.MainWindow mainWindow)
        {
            mainWindow.SetCloseToTray(value);
        }
    }
    partial void OnLogoVariantChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        var normalized = NormalizeLogoVariant(value);
        if (LogoVariant != normalized)
        {
            LogoVariant = normalized;
            return;
        }

        _settings.LogoVariant = normalized;
        ApplyLogoVariant(normalized);
        SaveSettings(T("Setting.LogoVariant"));

        if (App.Window is VMWV_App.MainWindow mainWindow)
        {
            mainWindow.ApplyBrandIcon(normalized);
        }
    }

    partial void OnLayoutModeChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        var normalized = NormalizeLayoutMode(value);
        if (LayoutMode != normalized)
        {
            LayoutMode = normalized;
            return;
        }

        _settings.LayoutMode = normalized;
        SaveSettings(T("Setting.LayoutMode"));
    }

    partial void OnLanguageChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        var normalized = NormalizeLanguage(value);
        if (Language != normalized)
        {
            Language = normalized;
            return;
        }

        _settings.Language = normalized;
        LocalizationService.Current.SetLanguage(normalized);
        RefreshLocalizedOptions();
        RefreshLocalizedState();
        _ = RefreshBindingTargetLocalizationAsync();
        SaveSettings(T("Setting.Language"));
    }

    partial void OnHideSupportPageChanged(bool value) =>
        SaveBoolean(value, setting => setting.HideSupportPage = value, T("Setting.SupportVisibility"));

    partial void OnSyncMuteChanged(bool value) => SaveBoolean(value, setting => setting.SyncMute = value, T("Setting.SyncMute"));
    partial void OnRememberVolumeChanged(bool value) => SaveBoolean(value, setting => setting.RememberVolume = value, T("Setting.RememberVolume"));
    partial void OnLimitDbGainToZeroChanged(bool value) => SaveBoolean(value, setting => setting.LimitDbGainToZero = value, T("Setting.LimitGain"));
    partial void OnLinearVolumeScaleChanged(bool value) => SaveToggle("linear_volume_scale", value, T("Setting.LinearScale"));
    partial void OnPreventVolumeSpikesChanged(bool value) => SaveToggle("apply_volume_fix", value, T("Setting.PreventSpikes"));
    partial void OnRestartOnDeviceChangeChanged(bool value) => SaveToggle("restart_audio_engine_on_device_change", value, T("Setting.RestartDevice"));
    partial void OnRestartOnAnyDeviceChangeChanged(bool value) => SaveToggle("restart_audio_engine_on_any_device_change", value, T("Setting.RestartAnyDevice"));
    partial void OnRestartOnResumeChanged(bool value) => SaveToggle("restart_audio_engine_on_resume", value, T("Setting.RestartResume"));
    partial void OnApplyCrackleFixChanged(bool value) => SaveToggle("apply_crackle_fix", value, T("Setting.CrackleFix"));

    partial void OnGainMinChanged(double value) => SaveNumber(setting => setting.GainMin = value, T("Setting.MinimumGain"));
    partial void OnGainMaxChanged(double value) => SaveNumber(setting => setting.GainMax = value, T("Setting.MaximumGain"));
    partial void OnPollingRateChanged(double value) => SaveNumber(setting => setting.PollingRate = (int)Math.Round(value), T("Setting.FallbackPolling"));

    private async Task SetStartWithWindowsAsync(bool value)
    {
        try
        {
            await _startupService.SetEnabledAsync(value, _shutdown.Token);
            SaveBoolean(value, setting => setting.StartWithWindows = value, T("Setting.StartWithWindows"), saveImmediately: true);
            AddLog(T("Log.Startup"), T(value ? "Log.StartupEnabled" : "Log.StartupDisabled"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog(T("Log.Startup"), TF("Log.StartupUpdateFailed", ex.Message));
            _isLoading = true;
            StartWithWindows = !value;
            _isLoading = false;
        }
    }

    private async Task SyncStartupRegistrationAsync()
    {
        try
        {
            await _startupService.SetEnabledAsync(_settings.StartWithWindows, _shutdown.Token);
            AddLog(T("Log.Startup"), T(_settings.StartWithWindows ? "Log.StartupVerified" : "Log.StartupDisabled"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog(T("Log.Startup"), TF("Log.StartupVerifyFailed", ex.Message));
        }
    }

    private void LoadFromSettings()
    {
        _isLoading = true;
        StartWithWindows = _settings.StartWithWindows;
        CloseToTray = _settings.CloseToTray;
        LogoVariant = NormalizeLogoVariant(_settings.LogoVariant);
        ApplyLogoVariant(LogoVariant);
        LayoutMode = NormalizeLayoutMode(_settings.LayoutMode);
        Language = NormalizeLanguage(_settings.Language);
        HideSupportPage = _settings.HideSupportPage;
        SyncMute = _settings.SyncMute;
        RememberVolume = _settings.RememberVolume;
        LimitDbGainToZero = _settings.LimitDbGainToZero;
        LinearVolumeScale = _settings.IsToggleEnabled("linear_volume_scale");
        PreventVolumeSpikes = _settings.IsToggleEnabled("apply_volume_fix");
        RestartOnDeviceChange = _settings.IsToggleEnabled("restart_audio_engine_on_device_change");
        RestartOnAnyDeviceChange = _settings.IsToggleEnabled("restart_audio_engine_on_any_device_change");
        RestartOnResume = _settings.IsToggleEnabled("restart_audio_engine_on_resume");
        ApplyCrackleFix = _settings.IsToggleEnabled("apply_crackle_fix");
        GainMin = _settings.GainMin;
        GainMax = _settings.GainMax;
        PollingRate = _settings.PollingRate;
        RefreshLocalizedOptions();
        _isLoading = false;
    }

    private void RefreshLocalizedOptions()
    {
        LogoVariantOptions.Clear();
        LogoVariantOptions.Add(new LanguageOption("Color", T("Option.Color")));
        LogoVariantOptions.Add(new LanguageOption("Black", T("Option.Black")));
        LogoVariantOptions.Add(new LanguageOption("White", T("Option.White")));

        LayoutModeOptions.Clear();
        LayoutModeOptions.Add(new LanguageOption("Compact", T("Option.Compact")));
        LayoutModeOptions.Add(new LanguageOption("Expanded", T("Option.Expanded")));
        OnPropertyChanged(nameof(Languages));
    }

    private void RefreshLocalizedState()
    {
        VoicemeeterConnectionActionText = IsVoicemeeterConnected
            ? T("Common.Disconnect")
            : T("Common.ConnectVoicemeeter");
        ConnectionStatusText = IsVoicemeeterConnected
            ? T("Status.VoicemeeterConnected")
            : T("Status.VoicemeeterDisconnected");

        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(SyncMute));
        OnPropertyChanged(nameof(RememberVolume));
        OnPropertyChanged(nameof(LimitDbGainToZero));
        OnPropertyChanged(nameof(LinearVolumeScale));
        OnPropertyChanged(nameof(PreventVolumeSpikes));
        OnPropertyChanged(nameof(RestartOnDeviceChange));
        OnPropertyChanged(nameof(RestartOnAnyDeviceChange));
        OnPropertyChanged(nameof(RestartOnResume));
        OnPropertyChanged(nameof(HideSupportPage));
    }

    private async Task RefreshBindingTargetLocalizationAsync()
    {
        try
        {
            if (_voicemeeterClient.State == VoicemeeterConnectionState.Connected)
            {
                await RefreshVoicemeeterTargetsAsync();
            }
            else
            {
                LoadBindingTargets();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AddLog(T("Log.Runtime"), TF("Log.BindingLocalizationFailed", ex.Message));
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await _updateService.CheckAsync(AppInfo.Version, _shutdown.Token);
            RunOnUiThread(() =>
            {
                LatestReleaseUri = result.ReleasePage;
                IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateTitle = result.IsUpdateAvailable
                    ? TF("Settings.Updates.AvailableTitle", result.LatestVersion)
                    : T("Settings.Updates.CurrentTitle");
                UpdateMessage = result.IsUpdateAvailable
                    ? TF("Settings.Updates.AvailableMessage", AppInfo.VersionText)
                    : TF("Settings.Updates.CurrentMessage", AppInfo.VersionText);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                IsUpdateAvailable = false;
                UpdateTitle = T("Settings.Updates.FailedTitle");
                UpdateMessage = TF("Settings.Updates.FailedMessage", AppInfo.VersionText);
                AddLog(T("Log.Runtime"), TF("Log.UpdateFailed", ex.Message));
            });
        }
    }

    private void LoadBindingTargets()
    {
        _voicemeeterTargets.Clear();
        BindingTargets.Clear();
        StripBindingTargets.Clear();
        BusBindingTargets.Clear();
        for (var index = 0; index <= 7; index++)
        {
            AddBindingTarget(
                $"Strip_{index}",
                TF("Bindings.StripIndex", index),
                TF("Bindings.StripIndex", index),
                T("Common.NoDevice"),
                "\uE8D6",
                T("Bindings.InputStrip"));
        }

        for (var index = 0; index <= 7; index++)
        {
            AddBindingTarget(
                $"Bus_{index}",
                TF("Bindings.BusIndex", index),
                TF("Bindings.BusIndex", index),
                T("Common.NoDevice"),
                "\uE9D9",
                T("Bindings.OutputBus"));
        }

        UpdateBindingTargetAvailability();
        UpdateDefinedBindings();
        UpdateSelectedTargetsCache();
    }

    private void LoadBindingTargets(IEnumerable<VoicemeeterBindingTarget> targets)
    {
        BindingTargets.Clear();
        StripBindingTargets.Clear();
        BusBindingTargets.Clear();

        foreach (var target in targets.OrderBy(target => target.Kind).ThenBy(target => target.Index))
        {
            var isStrip = target.Kind.Equals("Strip", StringComparison.OrdinalIgnoreCase);
            var display = VoicemeeterChannelNames.FormatDisplay(
                _voicemeeterClient.Edition,
                target.Kind,
                target.Index,
                target.FriendlyName,
                target.DeviceName);
            AddBindingTarget(
                target.Id,
                display.Title,
                display.IndexCaption,
                string.IsNullOrWhiteSpace(display.DeviceCaption) ? T("Common.NoDevice") : display.DeviceCaption,
                isStrip ? "\uE8D6" : "\uE9D9",
                T(isStrip ? "Bindings.InputStrip" : "Bindings.OutputBus"));
        }

        UpdateBindingTargetAvailability();
        UpdateDefinedBindings();
        UpdateSelectedTargetsCache();
    }

    private void AddBindingTarget(
        string id,
        string name,
        string detail,
        string deviceName,
        string glyph,
        string iconName)
    {
        var item = new BindingTargetItem(
            id,
            name,
            detail,
            deviceName,
            glyph,
            iconName,
            true,
            _settings.IsToggleEnabled(id),
            OnBindingTargetChanged);

        BindingTargets.Add(item);
        if (id.StartsWith("Strip_", StringComparison.OrdinalIgnoreCase))
        {
            StripBindingTargets.Add(item);
        }
        else if (id.StartsWith("Bus_", StringComparison.OrdinalIgnoreCase))
        {
            BusBindingTargets.Add(item);
        }

    }

    private async Task RefreshVoicemeeterTargetsAsync()
    {
        var targets = await _voicemeeterClient.GetBindingTargetsAsync(_shutdown.Token);
        _voicemeeterTargets.Clear();

        foreach (var target in targets)
        {
            _voicemeeterTargets[target.Id] = target;
        }

        LoadBindingTargets(targets.Where(target => target.IsAvailable));
    }

    private void OnBindingTargetChanged(BindingTargetItem item, bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.SetToggle(item.Id, value);
        SaveSettings($"{item.Name} binding");
        UpdateDefinedBindings();
        UpdateSelectedTargetsCache();
        if (value)
        {
            QueueCurrentAudioSync();
        }
    }

    private void SaveBoolean(bool value, Action<AppSettings> update, string label, bool saveImmediately = false)
    {
        if (_isLoading)
        {
            return;
        }

        update(_settings);
        SaveSettings(label, saveImmediately);
    }

    private void SaveToggle(string settingId, bool value, string label)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.SetToggle(settingId, value);
        SaveSettings(label);
    }

    private void SaveNumber(Action<AppSettings> update, string label)
    {
        if (_isLoading)
        {
            return;
        }

        update(_settings);
        SaveSettings(label);
    }

    private void SaveSettings(string label, bool saveImmediately = false)
    {
        if (saveImmediately)
        {
            _settingsStore.Save(_settings);
            ClearPendingSettingsSave();
        }
        else
        {
            var payload = _settingsStore.CreateSavePayload(_settings);
            QueueSettingsSave(payload);
        }

        AddLog(T("Log.Settings"), TF("Log.SettingSaved", label));
    }

    private void QueueSettingsSave(string payload)
    {
        CancellationTokenSource debounce;
        lock (_settingsSaveLock)
        {
            _pendingSettingsPayload = payload;
            _settingsSaveDebounce?.Cancel();
            _settingsSaveDebounce?.Dispose();
            _settingsSaveDebounce = new CancellationTokenSource();
            debounce = _settingsSaveDebounce;
        }

        _ = SaveSettingsAfterDebounceAsync(debounce);
    }

    private async Task SaveSettingsAfterDebounceAsync(CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(SettingsSaveDebounceDelay, debounce.Token);

            string? payload;
            lock (_settingsSaveLock)
            {
                if (!ReferenceEquals(_settingsSaveDebounce, debounce))
                {
                    return;
                }

                payload = _pendingSettingsPayload;
                _pendingSettingsPayload = null;
                _settingsSaveDebounce = null;
            }

            if (payload is not null)
            {
                await _settingsStore.SavePayloadAsync(payload, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => AddLog(T("Log.Settings"), TF("Log.SettingsSaveFailed", ex.Message)));
        }
        finally
        {
            debounce.Dispose();
        }
    }

    private async Task FlushSettingsSaveAsync()
    {
        string? payload;
        lock (_settingsSaveLock)
        {
            _settingsSaveDebounce?.Cancel();
            _settingsSaveDebounce = null;
            payload = _pendingSettingsPayload;
            _pendingSettingsPayload = null;
        }

        if (payload is not null)
        {
            await _settingsStore.SavePayloadAsync(payload, CancellationToken.None);
        }
    }

    private void ClearPendingSettingsSave()
    {
        lock (_settingsSaveLock)
        {
            _settingsSaveDebounce?.Cancel();
            _settingsSaveDebounce = null;
            _pendingSettingsPayload = null;
        }
    }

    private void StartAutoConnect()
    {
        if (_autoConnectStarted)
        {
            return;
        }

        _autoConnectStarted = true;
        _ = AutoConnectVoicemeeterAsync();
        SignalAutoConnect();
    }

    private void StartFallbackPolling()
    {
        if (_fallbackPollingStarted)
        {
            return;
        }

        _fallbackPollingStarted = true;
        _lastAudioCallback = DateTimeOffset.Now;
        _ = PollAudioFallbackAsync();
    }

    private async Task PollAudioFallbackAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Clamp((int)Math.Round(PollingRate), 25, 10_000));
            try
            {
                await Task.Delay(delay, _shutdown.Token);
                if (DateTimeOffset.Now - _lastAudioCallback < TimeSpan.FromSeconds(5))
                {
                    continue;
                }

                await _audioEndpointService.RefreshAsync(_shutdown.Token);
                var snapshot = _audioEndpointService.Current;
                if (snapshot.DeviceId.Length == 0)
                {
                    continue;
                }

                if (snapshot.Volume != _lastObservedVolume)
                {
                    var oldVolume = _lastObservedVolume;
                    _lastObservedVolume = snapshot.Volume;
                    OnAudioVolumeChanged(this, new AudioVolumeChangedEventArgs(oldVolume, snapshot.Volume));
                }

                if (snapshot.IsMuted != _lastObservedMute)
                {
                    var oldMute = _lastObservedMute;
                    _lastObservedMute = snapshot.IsMuted;
                    OnAudioMuteChanged(this, new AudioMuteChangedEventArgs(oldMute, snapshot.IsMuted));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.PollingFailed", ex.Message)));
            }
        }
    }

    private async Task AutoConnectVoicemeeterAsync()
    {
        var delay = TimeSpan.FromSeconds(10);
        while (!_shutdown.IsCancellationRequested)
        {
            if (_manualDisconnectRequested || _voicemeeterClient.State == VoicemeeterConnectionState.Connected)
            {
                try
                {
                    await _autoConnectSignal.WaitAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await ConnectVoicemeeterAsync(isAutomatic: true);
            if (_voicemeeterClient.State == VoicemeeterConnectionState.Connected)
            {
                delay = TimeSpan.FromSeconds(10);
                continue;
            }

            RunOnUiThread(() =>
            {
                VoicemeeterStatus = T("Status.Disconnected");
                VoicemeeterDetail = TF("Status.RetryIn", delay.TotalSeconds);
                StatusTitle = T("Status.VoicemeeterDisconnected");
                StatusMessage = T("Status.WaitingAvailability");
                StatusSeverity = InfoBarSeverity.Warning;
            });

            try
            {
                await Task.Delay(delay, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SignalAutoConnect()
    {
        try
        {
            _autoConnectSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void UpdateDefinedBindings()
    {
        var active = BindingTargets.Where(item => item.IsEnabled).ToList();
        var activeStrips = active.Where(item => item.Id.StartsWith("Strip_", StringComparison.OrdinalIgnoreCase)).Select(item => item.Name).ToList();
        var activeBuses = active.Where(item => item.Id.StartsWith("Bus_", StringComparison.OrdinalIgnoreCase)).Select(item => item.Name).ToList();

        DefinedStripBindings.Clear();
        foreach (var name in activeStrips)
        {
            DefinedStripBindings.Add(name);
        }

        DefinedBusBindings.Clear();
        foreach (var name in activeBuses)
        {
            DefinedBusBindings.Add(name);
        }

        HasDefinedStripBindings = DefinedStripBindings.Count > 0;
        HasDefinedBusBindings = DefinedBusBindings.Count > 0;

        DefinedBindingsStatus = active.Count == 0 ? T("Status.NoBindings") : active.Count.ToString();
        DefinedBindingsDetail = active.Count == 0
            ? T("Status.NoActiveBindings")
            : string.Join(", ", active.Select(item => item.Name));
    }

    private void UpdateBindingTargetAvailability()
    {
        HasStripBindingTargets = StripBindingTargets.Count > 0;
        HasBusBindingTargets = BusBindingTargets.Count > 0;
    }

    private void ApplyLogoVariant(string variant)
    {
        LogoImagePath = variant switch
        {
            "Black" => "ms-appx:///Assets/Brand/logo-black.png",
            "White" => "ms-appx:///Assets/Brand/logo-white.png",
            _ => "ms-appx:///Assets/Brand/logo.png"
        };
    }

    private static string NormalizeLogoVariant(string value) =>
        value switch
        {
            "Black" => "Black",
            "White" => "White",
            _ => "Color"
        };

    private static string NormalizeLayoutMode(string value) =>
        value switch
        {
            "Expanded" => "Expanded",
            _ => "Compact"
        };

    private static string NormalizeLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "en-us"
            : value.Trim().Replace('_', '-').ToLowerInvariant();

    private static string T(string key) => LocalizationService.Current.Get(key);

    private static string TF(string key, params object?[] arguments) =>
        LocalizationService.Current.Format(key, arguments);

    private static string LocalizeConnectionState(VoicemeeterConnectionState state) =>
        state switch
        {
            VoicemeeterConnectionState.Connected => T("Status.Connected"),
            VoicemeeterConnectionState.Connecting => T("Status.Connecting"),
            VoicemeeterConnectionState.Error => T("Common.Error"),
            _ => T("Status.Disconnected")
        };

    private static string LocalizeRecoveryReason(string? reason) =>
        reason switch
        {
            "engine restart" => T("Recovery.EngineRestart"),
            "sudden 100% spike" => T("Recovery.SuddenSpike"),
            _ => T("Recovery.VolumeRecovery")
        };

    private void AddLog(string category, string message)
    {
        if (App.DispatcherQueue is not null && !App.DispatcherQueue.HasThreadAccess)
        {
            App.DispatcherQueue.TryEnqueue(() => AddLog(category, message));
            return;
        }

        var entry = new DiagnosticLogEntry(DateTimeOffset.Now, category, message);
        AddLogEntry(Diagnostics, entry, MaxDiagnosticEntries);
        AddLogEntry(RecentEvents, entry, MaxRecentEventEntries);
        QueuePersistentLog(entry);
    }

    private static void AddLogEntry(ObservableCollection<DiagnosticLogEntry> entries, DiagnosticLogEntry entry, int maxEntries)
    {
        if (IsVolumeChanged(entry) && entries.Count > 0 && IsVolumeChanged(entries[0]))
        {
            entries[0] = entry;
            return;
        }

        entries.Insert(0, entry);
        while (entries.Count > maxEntries)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    private static bool IsVolumeChanged(DiagnosticLogEntry entry) =>
        entry.Category.Equals("Audio", StringComparison.OrdinalIgnoreCase)
        && entry.Message.StartsWith("Volume changed ", StringComparison.Ordinal);

    private static void QueuePersistentLog(DiagnosticLogEntry entry)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(AppSettingsPaths.DefaultLogsFolder);
                var logPath = Path.Combine(AppSettingsPaths.DefaultLogsFolder, $"{DateTimeOffset.Now:yyyy-MM-dd}.log");
                var line = $"{entry.Time:O}\t{entry.Category}\t{entry.Message}{Environment.NewLine}";
                File.AppendAllText(logPath, line);
            }
            catch
            {
            }
        });
    }

    private void AttachServiceEvents()
    {
        _audioEndpointService.VolumeChanged += OnAudioVolumeChanged;
        _audioEndpointService.MuteChanged += OnAudioMuteChanged;
        _audioEndpointService.DeviceChanged += OnAudioDeviceChanged;
        _voicemeeterClient.ConnectionStateChanged += OnVoicemeeterConnectionStateChanged;
    }

    private void OnAudioVolumeChanged(object? sender, AudioVolumeChangedEventArgs args)
    {
        _lastAudioCallback = DateTimeOffset.Now;
        var recoveryDecision = _volumeRecovery.ObserveVolumeChange(
            args.OldVolume,
            args.NewVolume,
            PreventVolumeSpikes);
        if (recoveryDecision.RestoreVolume is int restoreVolume)
        {
            var recoveryReason = LocalizeRecoveryReason(recoveryDecision.Reason);
            RunOnUiThread(() => AddLog(
                T("Log.Audio"),
                TF("Log.BlockedSpike", recoveryReason, restoreVolume)));
            QueueVolumeRestore(restoreVolume, recoveryReason, syncToVoicemeeter: true);
            return;
        }

        _lastObservedVolume = args.NewVolume;
        if (RememberVolume && recoveryDecision.ShouldRememberVolume)
        {
            _settings.InitialVolume = args.NewVolume;
            QueueSettingsSave(_settingsStore.CreateSavePayload(_settings));
        }

        RunOnUiThread(() =>
        {
            WindowsAudioStatus = $"{args.NewVolume}%";
            WindowsAudioDetail = _audioEndpointService.Current.DisplayName;
            AddLog(T("Log.Audio"), TF("Log.VolumeChanged", args.OldVolume, args.NewVolume));
        });

        QueueVolumeSync(args.NewVolume);
    }

    private void OnAudioMuteChanged(object? sender, AudioMuteChangedEventArgs args)
    {
        _lastAudioCallback = DateTimeOffset.Now;
        _lastObservedMute = args.IsMuted;
        RunOnUiThread(() =>
        {
            WindowsAudioDetail = $"{_audioEndpointService.Current.DisplayName} - {T(args.IsMuted ? "Common.Muted" : "Common.Unmuted")}";
            AddLog(T("Log.Audio"), T(args.IsMuted ? "Common.Muted" : "Common.Unmuted"));
        });

        if (SyncMute)
        {
            QueueMuteSync(args.IsMuted);
        }
    }

    private void OnAudioDeviceChanged(object? sender, AudioDeviceChangedEventArgs args)
    {
        RunOnUiThread(() =>
        {
            ApplyAudioSnapshot(_audioEndpointService.Current);
            AddLog(T("Log.Audio"), TF("Log.DeviceChanged", args.Added.Count, args.Removed.Count));
        });

        if (RestartOnDeviceChange || RestartOnAnyDeviceChange)
        {
            QueueAudioDeviceRecovery();
        }
    }

    public async Task HandleSystemResumeAsync()
    {
        AddLog(T("Log.System"), T("Log.ResumeDetected"));
        try
        {
            await _audioEndpointService.RefreshAsync(_shutdown.Token);
            RunOnUiThread(() => ApplyAudioSnapshot(_audioEndpointService.Current));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.ResumeRefreshFailed", ex.Message)));
        }

        _manualDisconnectRequested = false;
        if (RestartOnResume && _voicemeeterClient.State == VoicemeeterConnectionState.Connected)
        {
            try
            {
                await RestartAudioEngineCoreAsync(T("Log.RestartResume"), _shutdown.Token);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.ResumeRecoveryFailed", ex.Message)));
            }
        }

        RequestVoicemeeterRecovery();
        QueueCurrentAudioSync();
    }

    private void QueueAudioDeviceRecovery()
    {
        CancellationTokenSource debounce;
        lock (_voicemeeterSyncLock)
        {
            _deviceRecoveryDebounce?.Cancel();
            _deviceRecoveryDebounce?.Dispose();
            _deviceRecoveryDebounce = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            debounce = _deviceRecoveryDebounce;
        }

        _ = RecoverFromAudioDeviceChangeAsync(debounce);
    }

    private async Task RecoverFromAudioDeviceChangeAsync(CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), debounce.Token);
            await _audioEndpointService.RefreshAsync(debounce.Token);
            RunOnUiThread(() => ApplyAudioSnapshot(_audioEndpointService.Current));

            if (_voicemeeterClient.State == VoicemeeterConnectionState.Connected)
            {
                await RestartAudioEngineCoreAsync(T("Log.RestartDevice"), debounce.Token);
            }
            else
            {
                RequestVoicemeeterRecovery();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.DeviceRecoveryFailed", ex.Message)));
        }
        finally
        {
            lock (_voicemeeterSyncLock)
            {
                if (ReferenceEquals(_deviceRecoveryDebounce, debounce))
                {
                    _deviceRecoveryDebounce = null;
                }
            }

            debounce.Dispose();
        }
    }

    private void OnVoicemeeterConnectionStateChanged(object? sender, VoicemeeterConnectionStateChangedEventArgs args)
    {
        RunOnUiThread(() =>
        {
            VoicemeeterStatus = LocalizeConnectionState(args.NewState);
            VoicemeeterDetail = args.Message ?? _voicemeeterClient.Edition;
            IsVoicemeeterConnected = args.NewState == VoicemeeterConnectionState.Connected;
            if (!string.IsNullOrWhiteSpace(args.Message) && args.NewState == VoicemeeterConnectionState.Error)
            {
                LastVoicemeeterError = args.Message;
            }

            ConnectionStatusText = IsVoicemeeterConnected
                ? T("Status.VoicemeeterConnected")
                : T("Status.VoicemeeterDisconnected");
            VoicemeeterConnectionActionText = IsVoicemeeterConnected
                ? T("Common.Disconnect")
                : T("Common.ConnectVoicemeeter");
        });
    }

    private void QueueVolumeSync(int windowsVolume)
    {
        if (_voicemeeterClient.State != VoicemeeterConnectionState.Connected)
        {
            return;
        }

        var shouldStartWorker = false;
        lock (_voicemeeterSyncLock)
        {
            _pendingVolume = windowsVolume;
            if (!_volumeSyncWorkerRunning)
            {
                _volumeSyncWorkerRunning = true;
                shouldStartWorker = true;
            }
        }

        if (shouldStartWorker)
        {
            _ = ProcessPendingVolumeSyncAsync();
        }
    }

    private async Task ProcessPendingVolumeSyncAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                int windowsVolume;
                lock (_voicemeeterSyncLock)
                {
                    if (_pendingVolume is null)
                    {
                        _volumeSyncWorkerRunning = false;
                        return;
                    }

                    windowsVolume = _pendingVolume.Value;
                    _pendingVolume = null;
                }

                await SyncVolumeToVoicemeeterAsync(windowsVolume);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_shutdown.IsCancellationRequested)
            {
                lock (_voicemeeterSyncLock)
                {
                    _volumeSyncWorkerRunning = false;
                    _pendingVolume = null;
                }
            }
        }
    }

    private async Task SyncVolumeToVoicemeeterAsync(int windowsVolume)
    {
        if (_voicemeeterClient.State != VoicemeeterConnectionState.Connected)
        {
            return;
        }

        var gain = VolumeMapper.ToVoicemeeterGain(
            windowsVolume,
            GainMin,
            GainMax,
            LimitDbGainToZero,
            LinearVolumeScale);

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            await _voicemeeterClient.SetGainAsync(targets, gain, _shutdown.Token);
            RunOnUiThread(() =>
            {
                LastVolumeSyncText = TF("Status.GainSync", targets.Count, gain, DateTimeOffset.Now);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() =>
            {
                LastVoicemeeterError = ex.Message;
                AddLog(T("Log.Voicemeeter"), TF("Log.GainSyncFailed", ex.Message));
            });
            RequestVoicemeeterRecovery();
        }
    }

    private void QueueMuteSync(bool isMuted)
    {
        if (_voicemeeterClient.State != VoicemeeterConnectionState.Connected)
        {
            return;
        }

        var shouldStartWorker = false;
        lock (_voicemeeterSyncLock)
        {
            _pendingMute = isMuted;
            if (!_muteSyncWorkerRunning)
            {
                _muteSyncWorkerRunning = true;
                shouldStartWorker = true;
            }
        }

        if (shouldStartWorker)
        {
            _ = ProcessPendingMuteSyncAsync();
        }
    }

    private async Task ProcessPendingMuteSyncAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                bool isMuted;
                lock (_voicemeeterSyncLock)
                {
                    if (_pendingMute is null)
                    {
                        _muteSyncWorkerRunning = false;
                        return;
                    }

                    isMuted = _pendingMute.Value;
                    _pendingMute = null;
                }

                await SyncMuteToVoicemeeterAsync(isMuted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_shutdown.IsCancellationRequested)
            {
                lock (_voicemeeterSyncLock)
                {
                    _muteSyncWorkerRunning = false;
                    _pendingMute = null;
                }
            }
        }
    }

    private async Task SyncMuteToVoicemeeterAsync(bool isMuted)
    {
        if (_voicemeeterClient.State != VoicemeeterConnectionState.Connected)
        {
            return;
        }

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            await _voicemeeterClient.SetMuteAsync(targets, isMuted, _shutdown.Token);
            RunOnUiThread(() =>
            {
                LastMuteSyncText = TF(
                    "Status.MuteSync",
                    targets.Count,
                    T(isMuted ? "Common.Muted" : "Common.Unmuted"),
                    DateTimeOffset.Now);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() =>
            {
                LastVoicemeeterError = ex.Message;
                AddLog(T("Log.Voicemeeter"), TF("Log.MuteSyncFailed", ex.Message));
            });
            RequestVoicemeeterRecovery();
        }
    }

    private IReadOnlyList<VoicemeeterBindingTarget> SelectedTargets()
    {
        lock (_selectedTargetsLock)
        {
            return _selectedTargets;
        }
    }

    private void UpdateSelectedTargetsCache()
    {
        var selectedTargets = new List<VoicemeeterBindingTarget>();
        foreach (var item in BindingTargets)
        {
            if (item.IsEnabled && _voicemeeterTargets.TryGetValue(item.Id, out var target))
            {
                selectedTargets.Add(target);
            }
        }

        lock (_selectedTargetsLock)
        {
            _selectedTargets = selectedTargets;
        }

        var activeNames = BindingTargets
            .Where(item => item.IsEnabled)
            .Select(item => item.Name)
            .ToList();
        ActiveTargetsText = activeNames.Count == 0
            ? T("Status.NoActiveTargets")
            : TF("Status.ActiveTargets", activeNames.Count, string.Join(", ", activeNames));
    }

    private async Task EnsureVoicemeeterConnectedAsync()
    {
        if (_voicemeeterClient.State != VoicemeeterConnectionState.Connected)
        {
            await ConnectVoicemeeterAsync();
        }
    }

    private void ReportVoicemeeterCommandFailure(string command, Exception ex)
    {
        StatusTitle = TF("Status.CommandFailed", command);
        StatusMessage = ex.Message;
        StatusSeverity = InfoBarSeverity.Error;
        LastVoicemeeterError = ex.Message;
        AddLog(T("Log.Voicemeeter"), $"{TF("Status.CommandFailed", command)}: {ex.Message}");
        RequestVoicemeeterRecovery();
    }

    private void QueueCurrentAudioSync()
    {
        var snapshot = _audioEndpointService.Current;
        if (snapshot.DeviceId.Length == 0)
        {
            if (RememberVolume && _settings.InitialVolume is int rememberedVolume)
            {
                QueueVolumeSync(rememberedVolume);
            }

            return;
        }

        QueueVolumeSync(snapshot.Volume);
        if (SyncMute)
        {
            QueueMuteSync(snapshot.IsMuted);
        }
    }

    private async Task RestartAudioEngineCoreAsync(string successMessage, CancellationToken cancellationToken)
    {
        await _engineRestartLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _audioEndpointService.Current;
            var restoreVolume = _volumeRecovery.BeginEngineRestart(
                RecoveryVolumeFromSnapshot(snapshot),
                RememberVolume ? _settings.InitialVolume : null);

            await _voicemeeterClient.RestartAudioEngineAsync(cancellationToken);
            AddLog(T("Log.Voicemeeter"), successMessage);

            snapshot = await RefreshEndpointAfterEngineRestartAsync(cancellationToken);
            RunOnUiThread(() => ApplyAudioSnapshot(snapshot));
            if (restoreVolume is int safeVolume && snapshot.DeviceId.Length > 0)
            {
                await RestoreWindowsVolumeAsync(safeVolume, T("Recovery.EngineRestart"), syncToVoicemeeter: true, cancellationToken);
            }
            else
            {
                QueueCurrentAudioSync();
            }

            if (SyncMute && snapshot.DeviceId.Length > 0)
            {
                QueueMuteSync(snapshot.IsMuted);
            }
        }
        finally
        {
            _engineRestartLock.Release();
        }
    }

    private async Task<AudioEndpointSnapshot> RefreshEndpointAfterEngineRestartAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(EngineRestartSettleDelay, cancellationToken);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await _audioEndpointService.RefreshAsync(cancellationToken);
            var snapshot = _audioEndpointService.Current;
            if (snapshot.DeviceId.Length > 0)
            {
                return snapshot;
            }

            if (attempt < 5)
            {
                await Task.Delay(EndpointRetryDelay, cancellationToken);
            }
        }

        return _audioEndpointService.Current;
    }

    private void QueueVolumeRestore(int volume, string reason, bool syncToVoicemeeter)
    {
        _volumeRestoreRequests.Writer.TryWrite(new VolumeRestoreRequest(volume, reason, syncToVoicemeeter));
    }

    private async Task ProcessVolumeRestoreRequestsAsync()
    {
        try
        {
            await foreach (var request in _volumeRestoreRequests.Reader.ReadAllAsync(_shutdown.Token))
            {
                await RestoreWindowsVolumeAsync(
                    request.Volume,
                    request.Reason,
                    request.SyncToVoicemeeter,
                    _shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RestoreWindowsVolumeAsync(
        int volume,
        string reason,
        bool syncToVoicemeeter,
        CancellationToken cancellationToken)
    {
        await _volumeRestoreLock.WaitAsync(cancellationToken);
        try
        {
            var normalized = Math.Clamp(volume, 0, 100);
            await _audioEndpointService.SetVolumeAsync(normalized, cancellationToken);
            _volumeRecovery.RecordRestoredVolume(normalized);
            _lastObservedVolume = normalized;
            RunOnUiThread(() =>
            {
                WindowsAudioStatus = $"{normalized}%";
                WindowsAudioDetail = _audioEndpointService.Current.DisplayName;
                AddLog(T("Log.Audio"), TF("Log.RestoredVolume", normalized, reason));
            });

            if (syncToVoicemeeter)
            {
                QueueVolumeSync(normalized);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.RestoreVolumeFailed", reason, ex.Message)));
        }
        finally
        {
            _volumeRestoreLock.Release();
        }
    }

    private void RequestVoicemeeterRecovery()
    {
        if (_manualDisconnectRequested || _shutdown.IsCancellationRequested)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            VoicemeeterStatus = T("Status.Recovering");
            VoicemeeterDetail = T("Status.ReconnectSession");
            IsVoicemeeterConnected = false;
            ConnectionStatusText = T("Status.VoicemeeterDisconnected");
            VoicemeeterConnectionActionText = T("Common.ConnectVoicemeeter");
        });

        SignalAutoConnect();
    }

    private void ApplyAudioSnapshot(AudioEndpointSnapshot snapshot)
    {
        WindowsAudioStatus = snapshot.DeviceId.Length == 0 ? T("Common.Unavailable") : $"{snapshot.Volume}%";
        WindowsAudioDetail = snapshot.DeviceId.Length == 0
            ? T("Status.NoEndpoint")
            : $"{snapshot.DisplayName} - {T(snapshot.IsMuted ? "Common.Muted" : "Common.Unmuted")}";
    }

    private static int RecoveryVolumeFromSnapshot(AudioEndpointSnapshot snapshot) =>
        snapshot.DeviceId.Length == 0 ? -1 : snapshot.Volume;

    private static void RunOnUiThread(Action action)
    {
        if (App.DispatcherQueue is null || App.DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        App.DispatcherQueue.TryEnqueue(() => action());
    }

    public async ValueTask DisposeAsync()
    {
        await FlushSettingsSaveAsync();
        _volumeRestoreRequests.Writer.TryComplete();
        _shutdown.Cancel();
        _deviceRecoveryDebounce?.Cancel();
        _deviceRecoveryDebounce?.Dispose();
        _deviceRecoveryDebounce = null;
        _audioEndpointService.VolumeChanged -= OnAudioVolumeChanged;
        _audioEndpointService.MuteChanged -= OnAudioMuteChanged;
        _audioEndpointService.DeviceChanged -= OnAudioDeviceChanged;
        _voicemeeterClient.ConnectionStateChanged -= OnVoicemeeterConnectionStateChanged;
        await _volumeRestoreWorker;
        await _voicemeeterClient.DisposeAsync();
        await _audioEndpointService.DisposeAsync();
        _volumeRestoreLock.Dispose();
        _engineRestartLock.Dispose();
        _shutdown.Dispose();
    }

    private sealed record VolumeRestoreRequest(int Volume, string Reason, bool SyncToVoicemeeter);
}
