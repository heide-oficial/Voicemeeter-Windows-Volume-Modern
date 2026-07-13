using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using VMWV.Core.Services;
using VMWV.Core.Settings;
using VMWV.Core.Volume;
using VMWV_App.Models;

namespace VMWV_App.ViewModels;

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxDiagnosticEntries = 200;
    private const int MaxRecentEventEntries = 8;
    private static readonly TimeSpan SettingsSaveDebounceDelay = TimeSpan.FromMilliseconds(250);
    private readonly JsonSettingsStore _settingsStore;
    private readonly IAudioEndpointService _audioEndpointService;
    private readonly IVoicemeeterClient _voicemeeterClient;
    private readonly IStartupService _startupService;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _voicemeeterConnectionLock = new(1, 1);
    private readonly SemaphoreSlim _autoConnectSignal = new(0, 1);
    private readonly object _voicemeeterSyncLock = new();
    private readonly object _settingsSaveLock = new();
    private readonly object _selectedTargetsLock = new();
    private readonly Dictionary<string, VoicemeeterBindingTarget> _voicemeeterTargets = [];
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
        IStartupService startupService)
    {
        _audioEndpointService = audioEndpointService;
        _voicemeeterClient = voicemeeterClient;
        _startupService = startupService;

        _settingsStore = new JsonSettingsStore(AppSettingsPaths.DefaultSettingsPath);
        _settings = _settingsStore.LoadOrCreate();
        LoadFromSettings();
        LoadBindingTargets();
        AttachServiceEvents();
        AddLog("Startup", $"Settings loaded from {AppSettingsPaths.DefaultSettingsPath}");
        AddLog("Runtime", "Native audio and Voicemeeter services configured.");
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

    public IReadOnlyList<string> LogoVariants { get; } = ["Color", "Black", "White"];

    public IReadOnlyList<string> LayoutModes { get; } = ["Compact", "Expanded"];

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Starting native services";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Windows audio service will start when the page is ready.";

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string AppStatus { get; set; } = "Ready";

    [ObservableProperty]
    public partial string AppStatusDetail { get; set; } = "Single native process";

    [ObservableProperty]
    public partial string WindowsAudioStatus { get; set; } = "Starting";

    [ObservableProperty]
    public partial string WindowsAudioDetail { get; set; } = "Waiting for default endpoint";

    [ObservableProperty]
    public partial string VoicemeeterStatus { get; set; } = "Disconnected";

    [ObservableProperty]
    public partial string VoicemeeterDetail { get; set; } = "Native client not connected";

    [ObservableProperty]
    public partial string DefinedBindingsStatus { get; set; } = "None";

    [ObservableProperty]
    public partial string DefinedBindingsDetail { get; set; } = "No active bindings";

    [ObservableProperty]
    public partial string ActiveTargetsText { get; set; } = "No active targets";

    [ObservableProperty]
    public partial string LastVolumeSyncText { get; set; } = "Volume sync pending";

    [ObservableProperty]
    public partial string LastMuteSyncText { get; set; } = "Mute sync pending";

    [ObservableProperty]
    public partial string LastVoicemeeterError { get; set; } = "No Voicemeeter errors";

    [ObservableProperty]
    public partial bool HasDefinedStripBindings { get; set; }

    [ObservableProperty]
    public partial bool HasDefinedBusBindings { get; set; }

    [ObservableProperty]
    public partial bool IsVoicemeeterConnected { get; set; }

    [ObservableProperty]
    public partial string VoicemeeterConnectionActionText { get; set; } = "Connect to Voicemeeter";

    [ObservableProperty]
    public partial string ConnectionStatusText { get; set; } = "Voicemeeter disconnected";

    [ObservableProperty]
    public partial string LogoVariant { get; set; } = "Color";

    [ObservableProperty]
    public partial string LogoImagePath { get; set; } = "ms-appx:///Assets/Brand/logo.png";

    [ObservableProperty]
    public partial string LayoutMode { get; set; } = "Compact";

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

    public string VersionText => "v1.1.1";

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
            _lastObservedMute = _audioEndpointService.Current.IsMuted;
            StartFallbackPolling();
            StatusTitle = "Windows audio connected";
            StatusMessage = "Default endpoint is monitored with Core Audio callbacks.";
            StatusSeverity = InfoBarSeverity.Success;
            AddLog("Audio", $"Monitoring {_audioEndpointService.Current.DisplayName}.");
        }
        catch (Exception ex)
        {
            WindowsAudioStatus = "Error";
            WindowsAudioDetail = ex.Message;
            StatusTitle = "Audio service failed";
            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Error;
            AddLog("Audio", $"Failed to start: {ex.Message}");
        }

        StartAutoConnect();
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        AddLog("Status", "Status refreshed.");
        StatusTitle = "Status refreshed";
        StatusMessage = "The native shell is responsive and settings are available.";
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

            VoicemeeterStatus = "Connecting";
            VoicemeeterDetail = "Waiting for Voicemeeter process";
            StatusTitle = "Connecting to Voicemeeter";
            StatusMessage = isAutomatic
                ? "Trying to connect to Voicemeeter automatically."
                : "Open Voicemeeter if it is not already running.";
            StatusSeverity = InfoBarSeverity.Informational;
            AddLog("Voicemeeter", isAutomatic ? "Automatic connection attempt." : "Connecting.");

            await _voicemeeterClient.ConnectAsync(_shutdown.Token);
            await RefreshVoicemeeterTargetsAsync();
            QueueCurrentAudioSync();

            VoicemeeterStatus = "Connected";
            VoicemeeterDetail = _voicemeeterClient.Edition;
            IsVoicemeeterConnected = true;
            ConnectionStatusText = "Voicemeeter connected";
            VoicemeeterConnectionActionText = "Disconnect";
            StatusTitle = "Voicemeeter connected";
            StatusMessage = $"Connected to {_voicemeeterClient.Edition}.";
            StatusSeverity = InfoBarSeverity.Success;
            AddLog("Voicemeeter", $"Connected to {_voicemeeterClient.Edition}.");
            if (!_restartOnLaunchApplied && _settings.IsToggleEnabled("restart_audio_engine_on_app_launch"))
            {
                _restartOnLaunchApplied = true;
                await _voicemeeterClient.RestartAudioEngineAsync(_shutdown.Token);
                AddLog("Voicemeeter", "Restart audio engine on app launch applied.");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("Voicemeeter", "Connection cancelled.");
        }
        catch (Exception ex)
        {
            VoicemeeterStatus = "Error";
            VoicemeeterDetail = ex.Message;
            IsVoicemeeterConnected = false;
            ConnectionStatusText = "Voicemeeter disconnected";
            VoicemeeterConnectionActionText = "Connect to Voicemeeter";
            StatusTitle = "Voicemeeter connection failed";
            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Error;
            LastVoicemeeterError = ex.Message;
            AddLog("Voicemeeter", $"Connection failed: {ex.Message}");
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
            VoicemeeterStatus = "Disconnected";
            VoicemeeterDetail = "Native client not connected";
            IsVoicemeeterConnected = false;
            ConnectionStatusText = "Voicemeeter disconnected";
            VoicemeeterConnectionActionText = "Connect to Voicemeeter";
            StatusTitle = "Voicemeeter disconnected";
            StatusMessage = "Disconnected from Voicemeeter.";
            StatusSeverity = InfoBarSeverity.Warning;
            LastVolumeSyncText = "Volume sync paused";
            LastMuteSyncText = "Mute sync paused";
            AddLog("Voicemeeter", "Disconnected.");
        }
        catch (OperationCanceledException)
        {
            AddLog("Voicemeeter", "Disconnect cancelled.");
        }
        catch (Exception ex)
        {
            ReportVoicemeeterCommandFailure("Disconnect", ex);
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
            AddLog("Voicemeeter", "Show command sent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure("Show", ex);
        }
    }

    [RelayCommand]
    private async Task RestartAudioEngineAsync()
    {
        try
        {
            await EnsureVoicemeeterConnectedAsync();
            await _voicemeeterClient.RestartAudioEngineAsync(_shutdown.Token);
            AddLog("Voicemeeter", "Restart audio engine command sent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportVoicemeeterCommandFailure("Restart audio engine", ex);
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
        SaveBoolean(value, setting => setting.CloseToTray = value, "Close to tray", saveImmediately: true);
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
        SaveSettings("Logo variant");

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
        SaveSettings("Layout mode");
    }

    partial void OnSyncMuteChanged(bool value) => SaveBoolean(value, setting => setting.SyncMute = value, "Sync mute");
    partial void OnRememberVolumeChanged(bool value) => SaveBoolean(value, setting => setting.RememberVolume = value, "Remember volume");
    partial void OnLimitDbGainToZeroChanged(bool value) => SaveBoolean(value, setting => setting.LimitDbGainToZero = value, "Limit gain");
    partial void OnLinearVolumeScaleChanged(bool value) => SaveToggle("linear_volume_scale", value, "Linear volume scale");
    partial void OnPreventVolumeSpikesChanged(bool value) => SaveToggle("apply_volume_fix", value, "Prevent volume spikes");
    partial void OnRestartOnDeviceChangeChanged(bool value) => SaveToggle("restart_audio_engine_on_device_change", value, "Restart on audio device change");
    partial void OnRestartOnAnyDeviceChangeChanged(bool value) => SaveToggle("restart_audio_engine_on_any_device_change", value, "Restart on any device change");
    partial void OnRestartOnResumeChanged(bool value) => SaveToggle("restart_audio_engine_on_resume", value, "Restart on resume");
    partial void OnApplyCrackleFixChanged(bool value) => SaveToggle("apply_crackle_fix", value, "Crackle fix");

    partial void OnGainMinChanged(double value) => SaveNumber(setting => setting.GainMin = value, "Minimum gain");
    partial void OnGainMaxChanged(double value) => SaveNumber(setting => setting.GainMax = value, "Maximum gain");
    partial void OnPollingRateChanged(double value) => SaveNumber(setting => setting.PollingRate = (int)Math.Round(value), "Fallback polling");

    private async Task SetStartWithWindowsAsync(bool value)
    {
        try
        {
            await _startupService.SetEnabledAsync(value, _shutdown.Token);
            SaveBoolean(value, setting => setting.StartWithWindows = value, "Start with Windows", saveImmediately: true);
            AddLog("Startup", value ? "Windows startup registration enabled." : "Windows startup registration disabled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog("Startup", $"Failed to update Windows startup registration: {ex.Message}");
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
            AddLog("Startup", _settings.StartWithWindows
                ? "Windows startup registration verified."
                : "Windows startup registration disabled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog("Startup", $"Failed to verify Windows startup registration: {ex.Message}");
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
        _isLoading = false;
    }

    private void LoadBindingTargets()
    {
        _voicemeeterTargets.Clear();
        BindingTargets.Clear();
        StripBindingTargets.Clear();
        BusBindingTargets.Clear();
        for (var index = 0; index <= 7; index++)
        {
            AddBindingTarget($"Strip_{index}", $"Input Strip {index}", "Voicemeeter strip", "\uE8D6", "Input strip");
        }

        for (var index = 0; index <= 7; index++)
        {
            AddBindingTarget($"Bus_{index}", $"Output Bus {index}", "Voicemeeter bus", "\uE9D9", "Output bus");
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
            AddBindingTarget(
                target.Id,
                target.FriendlyName,
                isStrip ? "Voicemeeter strip" : "Voicemeeter bus",
                isStrip ? "\uE8D6" : "\uE9D9",
                isStrip ? "Input strip" : "Output bus");
        }

        UpdateBindingTargetAvailability();
        UpdateDefinedBindings();
        UpdateSelectedTargetsCache();
    }

    private void AddBindingTarget(string id, string name, string detail, string glyph, string iconName)
    {
        var item = new BindingTargetItem(
            id,
            name,
            detail,
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

        AddLog("Settings", $"{label} saved.");
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
            RunOnUiThread(() => AddLog("Settings", $"Failed to save settings: {ex.Message}"));
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
                RunOnUiThread(() => AddLog("Audio", $"Fallback polling failed: {ex.Message}"));
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
                VoicemeeterStatus = "Disconnected";
                VoicemeeterDetail = $"Automatic retry in {delay.TotalSeconds:0}s";
                StatusTitle = "Voicemeeter disconnected";
                StatusMessage = "Waiting for Voicemeeter to become available.";
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

        DefinedBindingsStatus = active.Count == 0 ? "None" : active.Count.ToString();
        DefinedBindingsDetail = active.Count == 0
            ? "No active bindings"
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
        _lastObservedVolume = args.NewVolume;
        if (RememberVolume)
        {
            _settings.InitialVolume = args.NewVolume;
            QueueSettingsSave(_settingsStore.CreateSavePayload(_settings));
        }

        RunOnUiThread(() =>
        {
            WindowsAudioStatus = $"{args.NewVolume}%";
            WindowsAudioDetail = _audioEndpointService.Current.DisplayName;
            AddLog("Audio", $"Volume changed {args.OldVolume}% -> {args.NewVolume}%.");
        });

        QueueVolumeSync(args.NewVolume);
    }

    private void OnAudioMuteChanged(object? sender, AudioMuteChangedEventArgs args)
    {
        _lastAudioCallback = DateTimeOffset.Now;
        _lastObservedMute = args.IsMuted;
        RunOnUiThread(() =>
        {
            WindowsAudioDetail = $"{_audioEndpointService.Current.DisplayName} - {(args.IsMuted ? "Muted" : "Unmuted")}";
            AddLog("Audio", args.IsMuted ? "Muted." : "Unmuted.");
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
            AddLog("Audio", $"Device changed. Added: {args.Added.Count}, removed: {args.Removed.Count}.");
        });

        if (RestartOnDeviceChange || RestartOnAnyDeviceChange)
        {
            QueueAudioDeviceRecovery();
        }
    }

    public async Task HandleSystemResumeAsync()
    {
        AddLog("System", "Windows resume detected.");
        try
        {
            await _audioEndpointService.RefreshAsync(_shutdown.Token);
            RunOnUiThread(() => ApplyAudioSnapshot(_audioEndpointService.Current));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() => AddLog("Audio", $"Failed to refresh endpoint after resume: {ex.Message}"));
        }

        if (RestartOnResume)
        {
            await RestartAudioEngineAsync();
        }

        _manualDisconnectRequested = false;
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
                await RestartAudioEngineAsync();
                QueueCurrentAudioSync();
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
            RunOnUiThread(() => AddLog("Audio", $"Device recovery failed: {ex.Message}"));
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
            VoicemeeterStatus = args.NewState.ToString();
            VoicemeeterDetail = args.Message ?? _voicemeeterClient.Edition;
            IsVoicemeeterConnected = args.NewState == VoicemeeterConnectionState.Connected;
            if (!string.IsNullOrWhiteSpace(args.Message) && args.NewState == VoicemeeterConnectionState.Error)
            {
                LastVoicemeeterError = args.Message;
            }

            ConnectionStatusText = IsVoicemeeterConnected
                ? "Voicemeeter connected"
                : "Voicemeeter disconnected";
            VoicemeeterConnectionActionText = IsVoicemeeterConnected
                ? "Disconnect"
                : "Connect to Voicemeeter";
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
                LastVolumeSyncText = $"{targets.Count} target(s), {gain:0.0} dB at {DateTimeOffset.Now:HH:mm:ss}";
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() =>
            {
                LastVoicemeeterError = ex.Message;
                AddLog("Voicemeeter", $"Failed to sync gain: {ex.Message}");
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
                LastMuteSyncText = $"{targets.Count} target(s), {(isMuted ? "muted" : "unmuted")} at {DateTimeOffset.Now:HH:mm:ss}";
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunOnUiThread(() =>
            {
                LastVoicemeeterError = ex.Message;
                AddLog("Voicemeeter", $"Failed to sync mute: {ex.Message}");
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

        ActiveTargetsText = selectedTargets.Count == 0
            ? "No active targets"
            : string.Join(", ", selectedTargets.Select(target => target.FriendlyName));
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
        StatusTitle = $"{command} failed";
        StatusMessage = ex.Message;
        StatusSeverity = InfoBarSeverity.Error;
        LastVoicemeeterError = ex.Message;
        AddLog("Voicemeeter", $"{command} failed: {ex.Message}");
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

    private void RequestVoicemeeterRecovery()
    {
        if (_manualDisconnectRequested || _shutdown.IsCancellationRequested)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            VoicemeeterStatus = "Recovering";
            VoicemeeterDetail = "Voicemeeter session will be reconnected.";
            IsVoicemeeterConnected = false;
            ConnectionStatusText = "Voicemeeter disconnected";
            VoicemeeterConnectionActionText = "Connect to Voicemeeter";
        });

        SignalAutoConnect();
    }

    private void ApplyAudioSnapshot(AudioEndpointSnapshot snapshot)
    {
        WindowsAudioStatus = snapshot.DeviceId.Length == 0 ? "Unavailable" : $"{snapshot.Volume}%";
        WindowsAudioDetail = snapshot.DeviceId.Length == 0
            ? "No default render endpoint"
            : $"{snapshot.DisplayName} - {(snapshot.IsMuted ? "Muted" : "Unmuted")}";
    }

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
        _shutdown.Cancel();
        _deviceRecoveryDebounce?.Cancel();
        _deviceRecoveryDebounce?.Dispose();
        _deviceRecoveryDebounce = null;
        _audioEndpointService.VolumeChanged -= OnAudioVolumeChanged;
        _audioEndpointService.MuteChanged -= OnAudioMuteChanged;
        _audioEndpointService.DeviceChanged -= OnAudioDeviceChanged;
        _voicemeeterClient.ConnectionStateChanged -= OnVoicemeeterConnectionStateChanged;
        await _voicemeeterClient.DisposeAsync();
        await _audioEndpointService.DisposeAsync();
        _shutdown.Dispose();
    }
}
