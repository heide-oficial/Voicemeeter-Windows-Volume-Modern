using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VMWV.Core.Services;

namespace VMWV.Infrastructure.Windows.Audio;

public sealed class WindowsAudioEndpointService : IAudioEndpointService
{
    private readonly Lock _sync = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private EndpointNotificationClient? _notificationClient;
    private int _volume;
    private bool _isMuted;
    private bool _isStarted;

    public event EventHandler<AudioVolumeChangedEventArgs>? VolumeChanged;

    public event EventHandler<AudioMuteChangedEventArgs>? MuteChanged;

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    public AudioEndpointSnapshot Current { get; private set; } =
        new(string.Empty, "No audio endpoint", 0, false);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_isStarted)
            {
                return Task.CompletedTask;
            }

            _enumerator = new MMDeviceEnumerator();
            _notificationClient = new EndpointNotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            TryAttachDefaultEndpoint();
            _isStarted = true;
        }

        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureEnumerator();
            TryAttachDefaultEndpoint();
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureStarted();
            var normalizedVolume = Math.Clamp(volume, 0, 100);
            _device!.AudioEndpointVolume.MasterVolumeLevelScalar = normalizedVolume / 100f;
        }

        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureStarted();
            _device!.AudioEndpointVolume.Mute = isMuted;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_device is not null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
                _device = null;
            }

            if (_enumerator is not null && _notificationClient is not null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }

            _enumerator?.Dispose();
            _enumerator = null;
            _notificationClient = null;
            _isStarted = false;
        }

        return ValueTask.CompletedTask;
    }

    private bool TryAttachDefaultEndpoint()
    {
        if (_enumerator is null)
        {
            throw new InvalidOperationException("Audio endpoint enumerator has not been created.");
        }

        if (_device is not null)
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            _device.Dispose();
        }

        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            UpdateSnapshotFromEndpoint();
            return true;
        }
        catch
        {
            _device?.Dispose();
            _device = null;
            UpdateSnapshotFromEndpoint();
            return false;
        }
    }

    private void UpdateSnapshotFromEndpoint()
    {
        if (_device is null)
        {
            Current = new AudioEndpointSnapshot(string.Empty, "No audio endpoint", 0, false);
            return;
        }

        _volume = ToVolumePercent(_device.AudioEndpointVolume.MasterVolumeLevelScalar);
        _isMuted = _device.AudioEndpointVolume.Mute;
        Current = new AudioEndpointSnapshot(
            _device.ID,
            _device.FriendlyName,
            _volume,
            _isMuted);
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        AudioVolumeChangedEventArgs? volumeArgs = null;
        AudioMuteChangedEventArgs? muteArgs = null;

        lock (_sync)
        {
            var newVolume = ToVolumePercent(data.MasterVolume);
            if (newVolume != _volume)
            {
                volumeArgs = new AudioVolumeChangedEventArgs(_volume, newVolume);
                _volume = newVolume;
            }

            if (data.Muted != _isMuted)
            {
                muteArgs = new AudioMuteChangedEventArgs(_isMuted, data.Muted);
                _isMuted = data.Muted;
            }

            if (_device is not null)
            {
                Current = Current with
                {
                    Volume = _volume,
                    IsMuted = _isMuted
                };
            }
        }

        if (volumeArgs is not null)
        {
            VolumeChanged?.Invoke(this, volumeArgs);
        }

        if (muteArgs is not null)
        {
            MuteChanged?.Invoke(this, muteArgs);
        }
    }

    private void OnDefaultDeviceChanged(string? newDeviceId)
    {
        var removed = Current.DeviceId.Length == 0 ? [] : new[] { Current.DeviceId };
        var added = string.IsNullOrWhiteSpace(newDeviceId) ? [] : new[] { newDeviceId };

        lock (_sync)
        {
            if (_enumerator is null)
            {
                return;
            }

            TryAttachDefaultEndpoint();
        }

        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(added, removed));
    }

    private void OnDeviceAdded(string deviceId)
    {
        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs([deviceId], []));
    }

    private void OnDeviceRemoved(string deviceId)
    {
        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs([], [deviceId]));
    }

    private void EnsureStarted()
    {
        if (!_isStarted || _device is null)
        {
            throw new InvalidOperationException("Windows audio endpoint service has not started.");
        }
    }

    private void EnsureEnumerator()
    {
        if (_enumerator is not null && _notificationClient is not null)
        {
            return;
        }

        _enumerator = new MMDeviceEnumerator();
        _notificationClient = new EndpointNotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        _isStarted = true;
    }

    private static int ToVolumePercent(float scalar) =>
        Math.Clamp((int)Math.Round(scalar * 100, MidpointRounding.AwayFromZero), 0, 100);

    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly WindowsAudioEndpointService _owner;

        public EndpointNotificationClient(WindowsAudioEndpointService owner)
        {
            _owner = owner;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active)
            {
                _owner.OnDeviceAdded(deviceId);
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) =>
            _owner.OnDeviceAdded(pwstrDeviceId);

        public void OnDeviceRemoved(string deviceId) =>
            _owner.OnDeviceRemoved(deviceId);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                _owner.OnDefaultDeviceChanged(defaultDeviceId);
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
