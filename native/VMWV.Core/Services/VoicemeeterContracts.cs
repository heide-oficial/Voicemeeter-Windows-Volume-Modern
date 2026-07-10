namespace VMWV.Core.Services;

public interface IVoicemeeterClient : IAsyncDisposable
{
    event EventHandler? ParametersChanged;
    event EventHandler<VoicemeeterConnectionStateChangedEventArgs>? ConnectionStateChanged;

    VoicemeeterConnectionState State { get; }
    string Edition { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VoicemeeterBindingTarget>> GetBindingTargetsAsync(CancellationToken cancellationToken);
    Task SetGainAsync(VoicemeeterBindingTarget target, double gain, CancellationToken cancellationToken);
    Task SetGainAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, double gain, CancellationToken cancellationToken);
    Task SetMuteAsync(VoicemeeterBindingTarget target, bool isMuted, CancellationToken cancellationToken);
    Task SetMuteAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, bool isMuted, CancellationToken cancellationToken);
    Task RestartAudioEngineAsync(CancellationToken cancellationToken);
    Task ShowAsync(CancellationToken cancellationToken);
}

public enum VoicemeeterConnectionState
{
    Disconnected,
    WaitingForProcess,
    Connecting,
    Recovering,
    Connected,
    Error
}

public sealed record VoicemeeterConnectionStateChangedEventArgs(
    VoicemeeterConnectionState OldState,
    VoicemeeterConnectionState NewState,
    string? Message);

public sealed record VoicemeeterBindingTarget(
    string Id,
    string Kind,
    int Index,
    string FriendlyName,
    string? DeviceName,
    bool IsAvailable);
