namespace VMWV.Core.Services;

public sealed class AudioMonitor : IAsyncDisposable
{
    private readonly IAudioEndpointService _audio;
    private readonly Func<int> _pollingRate;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Task? _worker;
    private AudioEndpointSnapshot? _previous;
    private DateTimeOffset _lastFallbackActivity = DateTimeOffset.MinValue;
    private volatile MonitorHealth _health = new(MonitorStatus.Starting);
    private long _lastCallbackTicks;

    public MonitorHealth Health => _health;

    public AudioMonitor(IAudioEndpointService audio, Func<int> pollingRate, TimeProvider? time = null)
    {
        _audio = audio;
        _pollingRate = pollingRate;
        _time = time ?? TimeProvider.System;
        _audio.VolumeChanged += OnVolumeNotification;
        _audio.MuteChanged += OnMuteNotification;
    }

    private void OnVolumeNotification(object? sender, AudioVolumeChangedEventArgs args) => Interlocked.Exchange(ref _lastCallbackTicks, _time.GetUtcNow().UtcTicks);
    private void OnMuteNotification(object? sender, AudioMuteChangedEventArgs args) => Interlocked.Exchange(ref _lastCallbackTicks, _time.GetUtcNow().UtcTicks);

    public event EventHandler<AudioEndpointSnapshot>? SnapshotChanged;
    public event EventHandler<Exception>? Failed;
    public event EventHandler<MonitorHealth>? HealthChanged;
    public void Start() => _worker ??= RunAsync();

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _audio.StartAsync(cancellationToken).ConfigureAwait(false);
            await _audio.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = _audio.Current;
            if (_previous != snapshot)
            {
                if (_time.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastCallbackTicks) > TimeSpan.TicksPerSecond)
                    _lastFallbackActivity = _time.GetUtcNow();
                _previous = snapshot;
                SnapshotChanged?.Invoke(this, snapshot);
            }
            SetHealth(new(MonitorStatus.Ready));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetHealth(new(MonitorStatus.Retrying, _health.FailedAttempts + 1, ex.Message));
            throw;
        }
        finally { _refresh.Release(); }
    }

    private void SetHealth(MonitorHealth health)
    {
        if (_health == health) return;
        _health = health;
        HealthChanged?.Invoke(this, health);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await RefreshAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Failed?.Invoke(this, ex); }
            var delay = _health.FailedAttempts switch
            {
                1 => 1000, 2 => 2000, 3 => 5000, > 3 => 10000,
                _ => _time.GetUtcNow() - _lastFallbackActivity < TimeSpan.FromSeconds(5)
                    ? Math.Clamp(_pollingRate(), 25, 1000) : 1000
            };
            try { await Task.Delay(TimeSpan.FromMilliseconds(delay), _time, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _audio.VolumeChanged -= OnVolumeNotification;
        _audio.MuteChanged -= OnMuteNotification;
        if (_worker is not null) await _worker.ConfigureAwait(false);
        await _refresh.WaitAsync().ConfigureAwait(false);
        _refresh.Release();
        _stop.Dispose();
    }
}
