using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;
using System.Threading.Channels;
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
    private bool _disposed;
    private int _generation;
    private AudioEndpointVolumeNotificationDelegate? _volumeHandler;
    private readonly Channel<Action> _notifications = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _notificationWorker;

    public WindowsAudioEndpointService() => _notificationWorker = Task.Run(ProcessNotificationsAsync);

    private async Task ProcessNotificationsAsync()
    {
        await foreach (var notification in _notifications.Reader.ReadAllAsync())
        {
            if (_disposed) continue;
            try { notification(); }
            catch (Exception ex) when (IsRecoverableEndpointFailure(ex)) { }
        }
    }

    public event EventHandler<AudioVolumeChangedEventArgs>? VolumeChanged;

    public event EventHandler<AudioMuteChangedEventArgs>? MuteChanged;

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    public AudioEndpointSnapshot Current { get; private set; } =
        new(string.Empty, "No audio endpoint", 0, false);

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => StartCoreAsync(cancellationToken), cancellationToken);

    private Task StartCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isStarted)
            {
                return Task.CompletedTask;
            }

            try
            {
                EnsureEnumerator();
                TryAttachDefaultEndpoint();
                _isStarted = true;
            }
            catch
            {
                _isStarted = false;
                _enumerator?.Dispose();
                _enumerator = null;
                _notificationClient = null;
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        Task.Run(() => RefreshCoreAsync(cancellationToken), cancellationToken);

    private Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureEnumerator();
            using var currentDefault = TryGetDefaultEndpoint();
            if (_device is null || currentDefault?.ID != _device.ID)
            {
                TryAttachDefaultEndpoint();
            }
            else
            {
                try
                {
                    UpdateSnapshotFromEndpoint();
                }
                catch (Exception ex) when (IsRecoverableEndpointFailure(ex))
                {
                    TryAttachDefaultEndpoint();
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken) =>
        Task.Run(() => SetVolumeCoreAsync(volume, cancellationToken), cancellationToken);

    private Task SetVolumeCoreAsync(int volume, CancellationToken cancellationToken)
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

    public Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken) =>
        Task.Run(() => SetMuteCoreAsync(isMuted, cancellationToken), cancellationToken);

    private Task SetMuteCoreAsync(bool isMuted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureStarted();
            _device!.AudioEndpointVolume.Mute = isMuted;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(Task.Run(DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _notifications.Writer.TryComplete();
            if (_device is not null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= _volumeHandler;
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

        await _notificationWorker.ConfigureAwait(false);
    }

    private MMDevice? TryGetDefaultEndpoint()
    {
        try { return _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (Exception ex) when (IsRecoverableEndpointFailure(ex)) { return null; }
    }

    private bool TryAttachDefaultEndpoint()
    {
        if (_enumerator is null)
        {
            throw new InvalidOperationException("Audio endpoint enumerator has not been created.");
        }

        var generation = ++_generation;
        if (_device is not null)
        {
            var previous = _device;
            _device = null;
            previous.AudioEndpointVolume.OnVolumeNotification -= _volumeHandler;
            previous.Dispose();
        }

        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _volumeHandler = data => _notifications.Writer.TryWrite(() => OnVolumeNotification(data, generation));
            _device.AudioEndpointVolume.OnVolumeNotification += _volumeHandler;
            UpdateSnapshotFromEndpoint();
            return true;
        }
        catch (Exception ex) when (IsRecoverableEndpointFailure(ex))
        {
            _device?.Dispose();
            _device = null;
            UpdateSnapshotFromEndpoint();
            // A stopped audio service can leave the COM enumerator unusable.
            // Recreate it on the next monitored attempt, outside native callbacks.
            try
            {
                if (_notificationClient is not null)
                    _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
            catch (Exception cleanupError) when (IsRecoverableEndpointFailure(cleanupError)) { }
            _enumerator.Dispose();
            _enumerator = null;
            _notificationClient = null;
            _isStarted = false;
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

    private void OnVolumeNotification(AudioVolumeNotificationData data, int generation)
    {
        AudioVolumeChangedEventArgs? volumeArgs = null;
        AudioMuteChangedEventArgs? muteArgs = null;

        lock (_sync)
        {
            if (_disposed || generation != _generation || _device is null) return;
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

        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(added, removed, AudioDeviceChangeKind.DefaultOutput));
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
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    private static bool IsRecoverableEndpointFailure(Exception exception) =>
        exception is COMException or InvalidComObjectException or ObjectDisposedException;

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
                _owner._notifications.Writer.TryWrite(() => _owner.OnDeviceAdded(deviceId));
            }
            else
            {
                _owner._notifications.Writer.TryWrite(() => _owner.OnDeviceRemoved(deviceId));
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) =>
            _owner._notifications.Writer.TryWrite(() => _owner.OnDeviceAdded(pwstrDeviceId));

        public void OnDeviceRemoved(string deviceId) =>
            _owner._notifications.Writer.TryWrite(() => _owner.OnDeviceRemoved(deviceId));

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                _owner._notifications.Writer.TryWrite(() => _owner.OnDefaultDeviceChanged(defaultDeviceId));
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
