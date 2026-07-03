namespace VMWV.Core.Services;

public interface IAudioEndpointService : IAsyncDisposable
{
    event EventHandler<AudioVolumeChangedEventArgs>? VolumeChanged;
    event EventHandler<AudioMuteChangedEventArgs>? MuteChanged;
    event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    AudioEndpointSnapshot Current { get; }

    Task StartAsync(CancellationToken cancellationToken);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
    Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken);
}

public sealed record AudioEndpointSnapshot(
    string DeviceId,
    string DisplayName,
    int Volume,
    bool IsMuted);

public sealed record AudioVolumeChangedEventArgs(int OldVolume, int NewVolume);

public sealed record AudioMuteChangedEventArgs(bool WasMuted, bool IsMuted);

public sealed record AudioDeviceChangedEventArgs(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);
