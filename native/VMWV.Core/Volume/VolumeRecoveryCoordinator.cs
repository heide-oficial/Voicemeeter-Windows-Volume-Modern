namespace VMWV.Core.Volume;

public sealed class VolumeRecoveryCoordinator
{
    private static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRestartGuardDuration = TimeSpan.FromSeconds(3);
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _restartGuardDuration;
    private DateTimeOffset _lastVolumeChange = DateTimeOffset.MinValue;
    private DateTimeOffset _restartGuardUntil = DateTimeOffset.MinValue;
    private int? _stableVolume;
    private int? _restartVolume;
    private int? _lastAcceptedVolume;
    private bool _hasObservedVolumeChange;

    public VolumeRecoveryCoordinator(
        TimeProvider? timeProvider = null,
        TimeSpan? quietPeriod = null,
        TimeSpan? restartGuardDuration = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        _restartGuardDuration = restartGuardDuration ?? DefaultRestartGuardDuration;
    }

    public void Seed(int currentVolume, int? rememberedVolume)
    {
        lock (_sync)
        {
            _stableVolume = IsSafeVolume(currentVolume)
                ? currentVolume
                : IsSafeVolume(rememberedVolume)
                    ? rememberedVolume
                    : null;
            _lastAcceptedVolume = IsValidVolume(currentVolume) ? currentVolume : null;
            _hasObservedVolumeChange = false;
            _lastVolumeChange = _timeProvider.GetUtcNow();
        }
    }

    public int? BeginEngineRestart(int currentVolume, int? rememberedVolume)
    {
        lock (_sync)
        {
            if (currentVolume == 100 && _hasObservedVolumeChange && _lastAcceptedVolume == 100)
            {
                _restartVolume = 100;
            }
            else if (IsSafeVolume(currentVolume))
            {
                _restartVolume = currentVolume;
            }
            else if (IsSafeVolume(_stableVolume))
            {
                _restartVolume = _stableVolume;
            }
            else
            {
                _restartVolume = IsSafeVolume(rememberedVolume) ? rememberedVolume : null;
            }

            _restartGuardUntil = _timeProvider.GetUtcNow() + _restartGuardDuration;
            if (IsSafeVolume(_restartVolume))
            {
                _stableVolume = _restartVolume;
            }

            return _restartVolume;
        }
    }

    public VolumeRecoveryDecision ObserveVolumeChange(int oldVolume, int newVolume, bool preventVolumeSpikes)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var guardActive = now < _restartGuardUntil;
            var quietJump = _lastVolumeChange == DateTimeOffset.MinValue
                || now - _lastVolumeChange >= _quietPeriod;
            var restoreVolume = ResolveSafeVolume(oldVolume);

            if (newVolume == 100
                && restoreVolume is int restoreTo
                && (guardActive || preventVolumeSpikes && quietJump))
            {
                return new VolumeRecoveryDecision(restoreTo, guardActive ? "engine restart" : "sudden 100% spike", false);
            }

            if (IsSafeVolume(newVolume))
            {
                _stableVolume = newVolume;
            }

            _lastAcceptedVolume = newVolume;
            _hasObservedVolumeChange = true;
            _lastVolumeChange = now;
            return new VolumeRecoveryDecision(null, null, true);
        }
    }

    public void RecordRestoredVolume(int volume)
    {
        lock (_sync)
        {
            var normalized = Math.Clamp(volume, 0, 100);
            if (IsSafeVolume(normalized))
            {
                _stableVolume = normalized;
            }

            _lastAcceptedVolume = normalized;
            _hasObservedVolumeChange = true;
            _lastVolumeChange = _timeProvider.GetUtcNow();
        }
    }

    private int? ResolveSafeVolume(int oldVolume)
    {
        if (_timeProvider.GetUtcNow() < _restartGuardUntil && IsSafeVolume(_restartVolume))
        {
            return _restartVolume;
        }

        if (IsSafeVolume(oldVolume))
        {
            return oldVolume;
        }

        return IsSafeVolume(_stableVolume) ? _stableVolume : null;
    }

    private static bool IsSafeVolume(int? volume) => volume is >= 0 and < 100;

    private static bool IsValidVolume(int? volume) => volume is >= 0 and <= 100;
}

public readonly record struct VolumeRecoveryDecision(
    int? RestoreVolume,
    string? Reason,
    bool ShouldRememberVolume);
