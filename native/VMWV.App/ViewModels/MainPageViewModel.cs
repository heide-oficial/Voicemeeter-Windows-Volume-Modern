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
using VMWV.Infrastructure.Windows.Globalization;
using VMWV_App.Models;
using VMWV_App.Localization;

namespace VMWV_App.ViewModels;

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxDiagnosticEntries = 200;
    private static readonly TimeSpan EngineRestartSettleDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan EndpointRetryDelay = TimeSpan.FromMilliseconds(400);
    private readonly JsonSettingsStore _settingsStore;
    private readonly IAudioEndpointService _audioEndpointService;
    private readonly IVoicemeeterClient _voicemeeterClient;
    private readonly IStartupService _startupService;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _engineRestartLock = new(1, 1);
    private readonly SemaphoreSlim _volumeRestoreLock = new(1, 1);
    private readonly Channel<VolumeRestoreRequest> _volumeRestoreRequests = Channel.CreateBounded<VolumeRestoreRequest>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly VolumeRecoveryCoordinator _volumeRecovery = new();
    private readonly object _voicemeeterSyncLock = new();
    private readonly object _selectedTargetsLock = new();
    private readonly Dictionary<string, VoicemeeterBindingTarget> _voicemeeterTargets = [];
    private readonly Task _volumeRestoreWorker;
    private IReadOnlyList<VoicemeeterBindingTarget> _selectedTargets = [];
    private readonly SettingsWriter _settingsWriter;
    private readonly AudioMonitor _audioMonitor;
    private readonly ConnectionMonitor _connectionMonitor;
    private readonly UpdateCoordinator _updates;
    private readonly DiagnosticLogWriter _logWriter = new(AppSettingsPaths.DefaultLogsFolder);
    private readonly AsyncWorkGroup _work = new();
    private Task? _disposeTask;
    private Task? _initializeTask;
    private bool _audioSeeded;
    private AppSettings _settings;
    private int? _pendingVolume;
    private bool? _pendingMute;
    private bool _isLoading;
    private bool _isInitialized;
    private bool _restartOnLaunchApplied;
    private bool _volumeSyncWorkerRunning;
    private bool _muteSyncWorkerRunning;
    private CancellationTokenSource? _deviceRecoveryDebounce;
    private int _lastObservedVolume;
    private bool _lastObservedMute;
    private string _lastObservedDeviceId = string.Empty;

    public MainPageViewModel(
        IAudioEndpointService audioEndpointService,
        IVoicemeeterClient voicemeeterClient,
        IStartupService startupService,
        IUpdateService updateService)
    {
        _audioEndpointService = audioEndpointService;
        _voicemeeterClient = voicemeeterClient;
        _startupService = startupService;
        _settingsStore = new JsonSettingsStore(AppSettingsPaths.DefaultSettingsPath);
        _settings = _settingsStore.LoadOrCreate();
        _settingsWriter = new SettingsWriter(_settingsStore);
        _settingsWriter.SaveFailed += (_, ex) => RunOnUiThread(() => AddLog(T("Log.Settings"), TF("Log.SettingsSaveFailed", ex.Message), "settings.error"));
        _audioMonitor = new AudioMonitor(_audioEndpointService, () => _settings.PollingRate);
        _connectionMonitor = new ConnectionMonitor(_voicemeeterClient);
        _updates = new UpdateCoordinator(updateService, AppInfo.Version,
            Path.Combine(Path.GetDirectoryName(AppSettingsPaths.DefaultSettingsPath)!, "update-cache.json"));
        _work.Failed += (_, ex) => RunOnUiThread(() => AddLog(T("Log.Runtime"), ex.Message, "runtime.error"));
        _audioMonitor.SnapshotChanged += OnMonitoredSnapshot;
        _audioMonitor.HealthChanged += OnMonitorHealthChanged;
        _audioMonitor.Failed += (_, ex) => RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.PollingFailed", ex.Message), "audio.error"));
        _connectionMonitor.Ready += OnConnectionReady;
        _connectionMonitor.HealthChanged += OnMonitorHealthChanged;
        _connectionMonitor.Failed += (_, ex) => RunOnUiThread(() => ReportVoicemeeterCommandFailure(T("Common.ConnectVoicemeeter"), ex));
        _updates.Changed += (_, state) => RunOnUiThread(ApplyUpdateState);
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

    public ObservableCollection<BindingTargetItem> DefinedStripBindings { get; } = [];

    public ObservableCollection<BindingTargetItem> DefinedBusBindings { get; } = [];

    public ObservableCollection<DiagnosticLogEntry> Diagnostics { get; } = [];

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

    [ObservableProperty]
    public partial bool CheckUpdatesAutomatically { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; set; }

    [ObservableProperty]
    public partial string LastUpdateCheckText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GainValidationMessage { get; set; } = string.Empty;

    public bool HasSettingsReadError => _settingsStore.LoadError is not null;

    public bool CanEditSettings => !HasSettingsReadError && !_shutdown.IsCancellationRequested;

    public string SettingsReadError => _settingsStore.LoadError?.Message ?? string.Empty;

    public string VersionText => $"v{AppInfo.VersionText}";

    public Task InitializeAsync() => _initializeTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        _isInitialized = true;
        if (_settingsStore.LoadError is { } error)
        {
            StatusTitle = T("Settings.Validation.ReadFailed");
            StatusMessage = error.Message;
            StatusSeverity = InfoBarSeverity.Error;
            AddLog(T("Log.Settings"), error.Message, "settings.error");
            return;
        }
        ValidateGainInputs();
        await SyncStartupRegistrationAsync();
        _audioMonitor.Start();
        _connectionMonitor.Start();
        _updates.Start(CheckUpdatesAutomatically);
        ApplyUpdateState();
    }

    private void OnMonitoredSnapshot(object? sender, AudioEndpointSnapshot snapshot) => RunOnUiThread(() =>
    {
        if (_shutdown.IsCancellationRequested) return;
        ApplyAudioSnapshot(snapshot);
        if (snapshot.DeviceId.Length == 0) return;
        if (!_audioSeeded)
        {
            _audioSeeded = true;
            _lastObservedVolume = snapshot.Volume;
            _lastObservedMute = snapshot.IsMuted;
            _lastObservedDeviceId = snapshot.DeviceId;
            _volumeRecovery.Seed(snapshot.Volume, RememberVolume ? _settings.InitialVolume : null);
            QueueCurrentAudioSync();
            AddLog(T("Log.Audio"), TF("Log.Monitoring", snapshot.DisplayName));
            return;
        }
        if (_lastObservedDeviceId != snapshot.DeviceId)
        {
            var previousId = _lastObservedDeviceId;
            _lastObservedDeviceId = snapshot.DeviceId;
            OnAudioDeviceChanged(this, new AudioDeviceChangedEventArgs([snapshot.DeviceId],
                previousId.Length == 0 ? [] : [previousId], AudioDeviceChangeKind.DefaultOutput));
        }
        if (snapshot.Volume != _lastObservedVolume)
            OnAudioVolumeChanged(this, new AudioVolumeChangedEventArgs(_lastObservedVolume, snapshot.Volume));
        if (snapshot.IsMuted != _lastObservedMute)
            OnAudioMuteChanged(this, new AudioMuteChangedEventArgs(_lastObservedMute, snapshot.IsMuted));
    });

    private void OnConnectionReady(object? sender, IReadOnlyList<VoicemeeterBindingTarget> targets) => RunOnUiThread(() =>
    {
        if (_shutdown.IsCancellationRequested || _connectionMonitor.IsPaused) return;
        _voicemeeterTargets.Clear();
        foreach (var target in targets) _voicemeeterTargets[target.Id] = target;
        LoadBindingTargets(targets.Where(target => target.IsAvailable));
        StatusTitle = T("Status.VoicemeeterConnected");
        StatusMessage = TF("Status.ConnectedTo", _voicemeeterClient.Edition);
        StatusSeverity = InfoBarSeverity.Success;
        AddLog(T("Log.Voicemeeter"), StatusMessage);
        if (!_restartOnLaunchApplied && _settings.IsToggleEnabled("restart_audio_engine_on_app_launch"))
        {
            _restartOnLaunchApplied = true;
            _work.Run(() => RestartAudioEngineCoreAsync(T("Log.RestartLaunch"), _shutdown.Token));
        }
        else QueueCurrentAudioSync();
    });

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task RefreshStatusAsync()
    {
        try
        {
            await _audioMonitor.RefreshAsync(_shutdown.Token);
            await _connectionMonitor.RefreshAsync(_shutdown.Token);
            ApplyAudioSnapshot(_audioEndpointService.Current);
            StatusTitle = T("Status.RefreshedTitle");
            StatusMessage = T("Status.RefreshedMessage");
            StatusSeverity = InfoBarSeverity.Success;
            AddLog(T("Log.Status"), StatusMessage);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusTitle = T("Common.Error");
            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task ConnectVoicemeeterAsync()
    {
        await ConnectVoicemeeterAsync(isAutomatic: false);
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
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
        try { await _connectionMonitor.ConnectAsync(_shutdown.Token); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure(T("Common.ConnectVoicemeeter"), ex);
        }
    }

    private async Task DisconnectVoicemeeterAsync()
    {
        try
        {
            await _connectionMonitor.DisconnectAsync(_shutdown.Token);
            _voicemeeterTargets.Clear();
            UpdateSelectedTargetsCache();
            RefreshLiveDiagnostics();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure(T("Command.Disconnect"), ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task ShowVoicemeeterAsync()
    {
        try
        {
            await EnsureVoicemeeterConnectedAsync();
            await _voicemeeterClient.ShowAsync(_shutdown.Token);
            AddLog(T("Log.Voicemeeter"), T("Log.ShowSent"));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure(T("Command.Show"), ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task RestartAudioEngineAsync()
    {
        try
        {
            await EnsureVoicemeeterConnectedAsync();
            await RestartAudioEngineCoreAsync(T("Log.RestartCommand"), _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure(T("Command.RestartEngine"), ex);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_isLoading || !CanEditSettings)
        {
            return;
        }

        _work.Run(() => SetStartWithWindowsAsync(value));
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
        if (_isLoading || !CanEditSettings)
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
        if (_isLoading || !CanEditSettings)
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
        if (_isLoading || !CanEditSettings)
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
        _work.Run(RefreshBindingTargetLocalizationAsync);
        SaveSettings(T("Setting.Language"), logSuccess: false);
    }

    partial void OnHideSupportPageChanged(bool value) =>
        SaveBoolean(value, setting => setting.HideSupportPage = value, T("Setting.SupportVisibility"));

    partial void OnSyncMuteChanged(bool value)
    {
        SaveBoolean(value, setting => setting.SyncMute = value, T("Setting.SyncMute"), sync: value);
        if (!value) _muteSyncFailure = null;
        RefreshLiveDiagnostics();
    }
    partial void OnRememberVolumeChanged(bool value) => SaveBoolean(value, setting => setting.RememberVolume = value, T("Setting.RememberVolume"));
    partial void OnLimitDbGainToZeroChanged(bool value) => SaveBoolean(value, setting => setting.LimitDbGainToZero = value, T("Setting.LimitGain"), sync: true);
    partial void OnLinearVolumeScaleChanged(bool value) => SaveToggle("linear_volume_scale", value, T("Setting.LinearScale"), sync: true);
    partial void OnPreventVolumeSpikesChanged(bool value) => SaveToggle("apply_volume_fix", value, T("Setting.PreventSpikes"));
    partial void OnRestartOnDeviceChangeChanged(bool value) => SaveToggle("restart_audio_engine_on_device_change", value, T("Setting.RestartDevice"));
    partial void OnRestartOnAnyDeviceChangeChanged(bool value) => SaveToggle("restart_audio_engine_on_any_device_change", value, T("Setting.RestartAnyDevice"));
    partial void OnRestartOnResumeChanged(bool value) => SaveToggle("restart_audio_engine_on_resume", value, T("Setting.RestartResume"));
    partial void OnApplyCrackleFixChanged(bool value) => SaveToggle("apply_crackle_fix", value, T("Setting.CrackleFix"));

    partial void OnGainMinChanged(double value) => SaveGainInputs(T("Setting.MinimumGain"));
    partial void OnGainMaxChanged(double value) => SaveGainInputs(T("Setting.MaximumGain"));
    partial void OnPollingRateChanged(double value)
    {
        if (!double.IsFinite(value) || value < 25 || value > 10000) return;
        SaveNumber(setting => setting.PollingRate = (int)Math.Round(value), T("Setting.FallbackPolling"));
    }

    private bool ValidateGainInputs()
    {
        var valid = VolumeMapper.IsValidRange(GainMin, GainMax);
        GainValidationMessage = valid ? string.Empty : T("Settings.Validation.GainRange");
        RefreshLiveDiagnostics();
        return valid;
    }

    private void SaveGainInputs(string label)
    {
        if (_isLoading || !CanEditSettings || !ValidateGainInputs()) return;
        _settings.GainMin = GainMin;
        _settings.GainMax = GainMax;
        SaveSettings(label);
        QueueCurrentAudioSync();
    }

    partial void OnCheckUpdatesAutomaticallyChanged(bool value)
    {
        if (_isLoading || !CanEditSettings) return;
        _updates.SetAutomatic(value);
        SaveBoolean(value, setting => setting.CheckUpdatesAutomatically = value, T("Settings.Updates.Automatic"));
        _work.Run(() => _updates.CheckAsync(false));
    }

    private async Task SetStartWithWindowsAsync(bool value)
    {
        try
        {
            await _startupService.SetEnabledAsync(value, _shutdown.Token);
            SaveBoolean(value, setting => setting.StartWithWindows = value, T("Setting.StartWithWindows"), saveImmediately: true);
            AddLog(T("Log.Startup"), T(value ? "Log.StartupEnabled" : "Log.StartupDisabled"));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
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
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
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
        CheckUpdatesAutomatically = _settings.CheckUpdatesAutomatically;
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
        UpdateLocalizedOptions(
            LogoVariantOptions,
            ("Color", T("Option.Color")),
            ("Black", T("Option.Black")),
            ("White", T("Option.White")));

        UpdateLocalizedOptions(
            LayoutModeOptions,
            ("Compact", T("Option.Compact")),
            ("Expanded", T("Option.Expanded")));
        OnPropertyChanged(nameof(Languages));
    }

    private static void UpdateLocalizedOptions(
        ObservableCollection<LanguageOption> options,
        params (string Code, string DisplayName)[] localizedOptions)
    {
        var canUpdateInPlace = options.Count == localizedOptions.Length
            && options.Select(option => option.Code)
                .SequenceEqual(localizedOptions.Select(option => option.Code), StringComparer.Ordinal);
        if (canUpdateInPlace)
        {
            for (var index = 0; index < options.Count; index++)
            {
                options[index].DisplayName = localizedOptions[index].DisplayName;
            }

            return;
        }

        options.Clear();
        foreach (var option in localizedOptions)
        {
            options.Add(new LanguageOption(option.Code, option.DisplayName));
        }
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
        OnPropertyChanged(nameof(CheckUpdatesAutomatically));
        VoicemeeterStatus = LocalizeConnectionState(_voicemeeterClient.State);
        VoicemeeterDetail = IsVoicemeeterConnected ? _voicemeeterClient.Edition
            : T(_connectionMonitor.IsPaused ? "Status.NativeClientDisconnected" : "Status.ReconnectSession");
        OnPropertyChanged(nameof(ApplyCrackleFix));
        ApplyAudioSnapshot(_audioEndpointService.Current);
        ApplyUpdateState();
        ValidateGainInputs();
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

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private Task CheckForUpdatesAsync() => _updates.CheckAsync(true);

    public void RefreshRegionalFormats()
    {
        ApplyUpdateState();
        RefreshLiveDiagnostics();
        foreach (var entry in Diagnostics) entry.RefreshTimeText();
    }

    private void ApplyUpdateState()
    {
        var state = _updates.State;
        IsCheckingForUpdates = state.Status == UpdateStatus.Checking;
        IsUpdateAvailable = state.Status == UpdateStatus.Available;
        UpdateTitle = state.Status switch
        {
            UpdateStatus.Checking => T("Settings.Updates.CheckingTitle"),
            UpdateStatus.Available => TF("Settings.Updates.AvailableTitle", state.Result!.LatestVersion),
            UpdateStatus.Current => T("Settings.Updates.CurrentTitle"),
            UpdateStatus.Failed => T("Settings.Updates.FailedTitle"),
            _ => T("Settings.Updates.NotChecked")
        };
        UpdateMessage = state.Status switch
        {
            UpdateStatus.Checking => T("Settings.Updates.CheckingMessage"),
            UpdateStatus.Available => TF("Settings.Updates.AvailableMessage", AppInfo.VersionText),
            UpdateStatus.Current => TF("Settings.Updates.CurrentMessage", AppInfo.VersionText),
            UpdateStatus.Failed => TF("Settings.Updates.FailedMessage", AppInfo.VersionText),
            _ => string.Empty
        };
        LastUpdateCheckText = TF("Settings.Updates.LastChecks",
            state.LastAttempt is { } attempt ? WindowsRegionalFormats.FormatDateTime(attempt) : T("Common.Never"),
            state.LastSuccess is { } success ? WindowsRegionalFormats.FormatDateTime(success) : T("Common.Never"));
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
            CanEditSettings,
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
        if (_isLoading || !CanEditSettings)
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

    private void SaveBoolean(bool value, Action<AppSettings> update, string label, bool saveImmediately = false, bool sync = false)
    {
        if (_isLoading || !CanEditSettings)
        {
            return;
        }

        update(_settings);
        SaveSettings(label, saveImmediately);
        if (_isInitialized && sync) QueueCurrentAudioSync();
    }

    private void SaveToggle(string settingId, bool value, string label, bool sync = false)
    {
        if (_isLoading || !CanEditSettings)
        {
            return;
        }

        _settings.SetToggle(settingId, value);
        SaveSettings(label);
        if (_isInitialized && sync) QueueCurrentAudioSync();
    }

    private void SaveNumber(Action<AppSettings> update, string label)
    {
        if (_isLoading || !CanEditSettings)
        {
            return;
        }

        update(_settings);
        SaveSettings(label);
    }

    private void SaveSettings(string label, bool saveImmediately = false, bool logSuccess = true)
    {
        if (_shutdown.IsCancellationRequested || _settingsStore.LoadError is not null) return;
        try
        {
            var payload = _settingsStore.CreateSavePayload(_settings);
            _work.Run(() => _settingsWriter.QueueAsync(payload, saveImmediately));
            if (logSuccess) AddLog(T("Log.Settings"), TF("Log.SettingSaved", label));
        }
        catch (Exception ex)
        {
            AddLog(T("Log.Settings"), TF("Log.SettingsSaveFailed", ex.Message), "settings.error");
        }
    }

    private void QueueSettingsSave(string payload)
    {
        if (!_shutdown.IsCancellationRequested) _work.Run(() => _settingsWriter.QueueAsync(payload));
    }

    private void UpdateDefinedBindings()
    {
        var active = BindingTargets.Where(item => item.IsEnabled).ToList();
        DefinedStripBindings.Clear();
        DefinedBusBindings.Clear();
        foreach (var item in active)
        {
            if (item.Id.StartsWith("Strip_", StringComparison.OrdinalIgnoreCase))
            {
                DefinedStripBindings.Add(item);
            }
            else if (item.Id.StartsWith("Bus_", StringComparison.OrdinalIgnoreCase))
            {
                DefinedBusBindings.Add(item);
            }
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

    private void AddLog(string category, string message, string eventId = "activity")
    {
        if (App.DispatcherQueue is not null && !App.DispatcherQueue.HasThreadAccess)
        {
            App.DispatcherQueue.TryEnqueue(() => AddLog(category, message, eventId));
            return;
        }

        var entry = new DiagnosticLogEntry(DateTimeOffset.Now, category, message, eventId);
        AddLogEntry(Diagnostics, entry, MaxDiagnosticEntries);
        _logWriter.Enqueue(new DiagnosticRecord(entry.Time, eventId, category, message));
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

    private static bool IsVolumeChanged(DiagnosticLogEntry entry) => entry.EventId == "volume.changed";

    private void AttachServiceEvents()
    {
        _audioEndpointService.VolumeChanged += OnAudioVolumeChanged;
        _audioEndpointService.MuteChanged += OnAudioMuteChanged;
        _audioEndpointService.DeviceChanged += OnAudioDeviceChanged;
        _voicemeeterClient.ConnectionStateChanged += OnVoicemeeterConnectionStateChanged;
    }

    private void OnAudioVolumeChanged(object? sender, AudioVolumeChangedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (App.DispatcherQueue is { HasThreadAccess: false })
        {
            RunOnUiThread(() => OnAudioVolumeChanged(sender, args));
            return;
        }
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
            ApplyAudioSnapshot(_audioEndpointService.Current with { Volume = args.NewVolume });
            AddLog(T("Log.Audio"), TF("Log.VolumeChanged", args.OldVolume, args.NewVolume), "volume.changed");
        });

        QueueVolumeSync(args.NewVolume);
    }

    private void OnAudioMuteChanged(object? sender, AudioMuteChangedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (App.DispatcherQueue is { HasThreadAccess: false })
        {
            RunOnUiThread(() => OnAudioMuteChanged(sender, args));
            return;
        }
        _lastObservedMute = args.IsMuted;
        RunOnUiThread(() =>
        {
            ApplyAudioSnapshot(_audioEndpointService.Current with { IsMuted = args.IsMuted });
            AddLog(T("Log.Audio"), T(args.IsMuted ? "Common.Muted" : "Common.Unmuted"));
        });

        if (SyncMute)
        {
            QueueMuteSync(args.IsMuted);
        }
    }

    private void OnAudioDeviceChanged(object? sender, AudioDeviceChangedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (App.DispatcherQueue is { HasThreadAccess: false })
        {
            RunOnUiThread(() => OnAudioDeviceChanged(sender, args));
            return;
        }
        _lastObservedDeviceId = _audioEndpointService.Current.DeviceId;
        RunOnUiThread(() =>
        {
            ApplyAudioSnapshot(_audioEndpointService.Current);
            AddLog(T("Log.Audio"), TF("Log.DeviceChanged", args.Added.Count, args.Removed.Count));
        });

        if (RestartOnAnyDeviceChange || RestartOnDeviceChange && args.Kind == AudioDeviceChangeKind.DefaultOutput)
        {
            QueueAudioDeviceRecovery();
        }
    }

    public void QueueSystemResume() => _work.Run(HandleSystemResumeAsync);

    private async Task HandleSystemResumeAsync()
    {
        if (!CanEditSettings) return;
        AddLog(T("Log.System"), T("Log.ResumeDetected"));
        try
        {
            await _audioMonitor.RefreshAsync(_shutdown.Token);
            RunOnUiThread(() => ApplyAudioSnapshot(_audioEndpointService.Current));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.ResumeRefreshFailed", ex.Message)));
        }

        if (RestartOnResume && !_connectionMonitor.IsPaused && _voicemeeterClient.State == VoicemeeterConnectionState.Connected)
        {
            try
            {
                await RestartAudioEngineCoreAsync(T("Log.RestartResume"), _shutdown.Token);
                return;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RunOnUiThread(() => AddLog(T("Log.Audio"), TF("Log.ResumeRecoveryFailed", ex.Message)));
            }
        }

        try { await _connectionMonitor.RefreshAsync(_shutdown.Token); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception ex) { ReportVoicemeeterCommandFailure(T("Common.ConnectVoicemeeter"), ex); }
        QueueCurrentAudioSync();
    }

    private void QueueAudioDeviceRecovery()
    {
        CancellationTokenSource debounce;
        lock (_voicemeeterSyncLock)
        {
            _deviceRecoveryDebounce?.Cancel();
            _deviceRecoveryDebounce = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            debounce = _deviceRecoveryDebounce;
        }

        _work.Run(() => RecoverFromAudioDeviceChangeAsync(debounce));
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
            VoicemeeterDetail = args.Message ?? (args.NewState == VoicemeeterConnectionState.Connected
                ? _voicemeeterClient.Edition : T("Status.NativeClientDisconnected"));
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
            RefreshLiveDiagnostics();
        });
    }

    private void QueueVolumeSync(int windowsVolume)
    {
        if (_shutdown.IsCancellationRequested || _connectionMonitor.IsPaused || _voicemeeterClient.State != VoicemeeterConnectionState.Connected)
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
            _work.Run(ProcessPendingVolumeSyncAsync);
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
        if (_connectionMonitor.IsPaused || _shutdown.IsCancellationRequested) return;
        if (_voicemeeterClient.State != VoicemeeterConnectionState.Connected)
        {
            return;
        }

        if (!VolumeMapper.IsValidRange(_settings.GainMin, _settings.GainMax)) return;
        var gain = VolumeMapper.ToVoicemeeterGain(
            windowsVolume,
            _settings.GainMin,
            _settings.GainMax,
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
            ClearVolumeSyncFailure();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() =>
            {
                LastVoicemeeterError = ex.Message;
                _volumeSyncFailure = ex.Message;
                RefreshLiveDiagnostics();
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
            _work.Run(ProcessPendingMuteSyncAsync);
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
        if (!SyncMute || _connectionMonitor.IsPaused || _shutdown.IsCancellationRequested) return;
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
            ClearMuteSyncFailure();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() =>
            {
                LastVoicemeeterError = ex.Message;
                _muteSyncFailure = ex.Message;
                RefreshLiveDiagnostics();
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

        if (selectedTargets.Count == 0)
        {
            _volumeSyncFailure = null;
            _muteSyncFailure = null;
        }

        var activeNames = BindingTargets
            .Where(item => item.IsEnabled)
            .Select(item => item.Name)
            .ToList();
        ActiveTargetsText = activeNames.Count == 0
            ? T("Status.NoActiveTargets")
            : TF("Status.ActiveTargets", activeNames.Count, string.Join(", ", activeNames));
        RefreshLiveDiagnostics();
    }

    private async Task EnsureVoicemeeterConnectedAsync()
    {
        if (!CanEditSettings) throw new InvalidOperationException(T("Settings.Validation.ReadFailed"));
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
        AddLog(T("Log.Voicemeeter"), $"{TF("Status.CommandFailed", command)}: {ex.Message}", "voicemeeter.error");
        RequestVoicemeeterRecovery();
    }

    private void QueueCurrentAudioSync()
    {
        if (_shutdown.IsCancellationRequested) return;
        if (App.DispatcherQueue is { HasThreadAccess: false })
        {
            RunOnUiThread(QueueCurrentAudioSync);
            return;
        }
        var snapshot = _audioEndpointService.Current;
        if (snapshot.DeviceId.Length == 0)
        {
            if (RememberVolume && _settings.InitialVolume is int rememberedVolume)
            {
                QueueVolumeSync(rememberedVolume);
            }

            return;
        }

        if (_audioSeeded && snapshot.Volume != _lastObservedVolume)
            OnAudioVolumeChanged(this, new AudioVolumeChangedEventArgs(_lastObservedVolume, snapshot.Volume));
        else
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
                ApplyAudioSnapshot(_audioEndpointService.Current with { Volume = normalized });
                AddLog(T("Log.Audio"), TF("Log.RestoredVolume", normalized, reason));
            });

            if (syncToVoicemeeter)
            {
                QueueVolumeSync(normalized);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
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
        if (_connectionMonitor.IsPaused || _shutdown.IsCancellationRequested
            || _voicemeeterClient.State == VoicemeeterConnectionState.Connected)
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

        // ConnectionMonitor probes and retries until the engine becomes available.
    }

    private void ApplyAudioSnapshot(AudioEndpointSnapshot snapshot)
    {
        WindowsAudioStatus = snapshot.DeviceId.Length == 0 ? T("Common.Unavailable") : $"{snapshot.Volume}%";
        WindowsAudioDetail = snapshot.DeviceId.Length == 0
            ? T("Status.NoEndpoint")
            : snapshot.IsMuted || snapshot.Volume == 0
                ? $"{snapshot.DisplayName} - {T("Common.Muted")}" : snapshot.DisplayName;
        RefreshLiveDiagnostics();
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

    public ValueTask DisposeAsync() => new(_disposeTask ??= StopAsync());

    private async Task StopAsync()
    {
        _shutdown.Cancel();
        OnPropertyChanged(nameof(CanEditSettings));
        _deviceRecoveryDebounce?.Cancel();
        _volumeRestoreRequests.Writer.TryComplete();
        _audioEndpointService.VolumeChanged -= OnAudioVolumeChanged;
        _audioEndpointService.MuteChanged -= OnAudioMuteChanged;
        _audioEndpointService.DeviceChanged -= OnAudioDeviceChanged;
        _voicemeeterClient.ConnectionStateChanged -= OnVoicemeeterConnectionStateChanged;
        _audioMonitor.HealthChanged -= OnMonitorHealthChanged;
        _connectionMonitor.HealthChanged -= OnMonitorHealthChanged;
        await _audioMonitor.DisposeAsync();
        await _connectionMonitor.DisposeAsync();
        await _updates.DisposeAsync();
        var commands = new IAsyncRelayCommand[] { RefreshStatusCommand, ConnectVoicemeeterCommand,
            ToggleVoicemeeterConnectionCommand, ShowVoicemeeterCommand, RestartAudioEngineCommand, CheckForUpdatesCommand };
        try
        {
            await Task.WhenAll(commands.Select(command => command.ExecutionTask ?? Task.CompletedTask));
        }
        catch (OperationCanceledException) { }
        if (_initializeTask is not null)
        {
            try { await _initializeTask; }
            catch (OperationCanceledException) { }
        }
        await _work.StopAsync();
        await _volumeRestoreWorker;
        await _settingsWriter.DisposeAsync();
        await _voicemeeterClient.DisposeAsync();
        await _audioEndpointService.DisposeAsync();
        await _logWriter.DisposeAsync();
    }

    private sealed record VolumeRestoreRequest(int Volume, string Reason, bool SyncToVoicemeeter);
}
