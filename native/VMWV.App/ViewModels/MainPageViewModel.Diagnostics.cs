using CommunityToolkit.Mvvm.ComponentModel;
using VMWV.Core.Services;

namespace VMWV_App.ViewModels;

public partial class MainPageViewModel
{
    private string? _volumeSyncFailure;
    private string? _muteSyncFailure;

    [ObservableProperty]
    public partial string CurrentFailureText { get; set; } = T("Diagnostics.NoFailures");

    [ObservableProperty]
    public partial string VoicemeeterSessionText { get; set; } = T("Status.Disconnected");

    private void OnMonitorHealthChanged(object? sender, MonitorHealth health) => RunOnUiThread(() =>
    {
        if (_connectionMonitor.IsPaused)
        {
            _volumeSyncFailure = null;
            _muteSyncFailure = null;
        }
        RefreshLiveDiagnostics();
    });

    private void RefreshLiveDiagnostics()
    {
        if (_shutdown.IsCancellationRequested) return;
        var connection = _connectionMonitor.Health;
        var audio = _audioMonitor.Health;
        var paused = _connectionMonitor.IsPaused;
        var failures = new List<string>();
        if (HasSettingsReadError) failures.Add(SettingsReadError);
        if (GainValidationMessage.Length > 0) failures.Add(GainValidationMessage);
        if (audio.Error is { } audioError) failures.Add(TF("Diagnostics.AudioFailure", audioError));
        if (connection.Error is { } connectionError) failures.Add(TF("Diagnostics.ConnectionFailure", connectionError));
        if (!paused && _volumeSyncFailure is { } volumeError) failures.Add(TF("Diagnostics.VolumeFailure", volumeError));
        if (!paused && SyncMute && _muteSyncFailure is { } muteError) failures.Add(TF("Diagnostics.MuteFailure", muteError));
        if (audio.Status == MonitorStatus.Ready && _audioEndpointService.Current.DeviceId.Length == 0)
            failures.Add(T("Status.NoEndpoint"));
        CurrentFailureText = failures.Count == 0 ? T("Diagnostics.NoFailures") : string.Join("\n", failures);

        var state = _voicemeeterClient.State;
        VoicemeeterSessionText = state switch
        {
            VoicemeeterConnectionState.Connected => $"{T("Status.Connected")} - {_voicemeeterClient.Edition}",
            VoicemeeterConnectionState.WaitingForProcess => T("Status.WaitingVoicemeeter"),
            VoicemeeterConnectionState.Recovering => T("Status.Recovering"),
            _ => LocalizeConnectionState(state)
        };

    }

    private void ClearVolumeSyncFailure()
    {
        RunOnUiThread(() =>
        {
            if (_volumeSyncFailure is null) return;
            _volumeSyncFailure = null;
            RefreshLiveDiagnostics();
        });
    }

    private void ClearMuteSyncFailure()
    {
        RunOnUiThread(() =>
        {
            if (_muteSyncFailure is null) return;
            _muteSyncFailure = null;
            RefreshLiveDiagnostics();
        });
    }
}
