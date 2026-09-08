namespace VMWV.Core.Services;

public sealed class ResumeGate(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private bool _suspended;
    private DateTimeOffset _lastResume = DateTimeOffset.MinValue;

    public void Suspend() { lock (_gate) _suspended = true; }

    public bool TryResume()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_suspended && now - _lastResume < TimeSpan.FromSeconds(5)) return false;
            _suspended = false;
            _lastResume = now;
            return true;
        }
    }
}
