namespace VMWV.Core.Services;

public sealed class ConnectionMonitor : IAsyncDisposable
{
    private readonly IVoicemeeterClient _client;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private volatile bool _paused;
    private volatile MonitorHealth _health = new(MonitorStatus.Starting);
    public bool IsPaused => _paused;
    public MonitorHealth Health => _health;

    public ConnectionMonitor(IVoicemeeterClient client, TimeProvider? time = null)
    {
        _client = client;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<IReadOnlyList<VoicemeeterBindingTarget>>? Ready;
    public event EventHandler<Exception>? Failed;
    public event EventHandler<MonitorHealth>? HealthChanged;
    public void Start() => _worker ??= RunAsync();

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _paused = false;
        await ReconcileAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _paused = true;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetHealth(new(MonitorStatus.Paused));
            await _client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => ReconcileAsync(true, cancellationToken);

    private async Task ReconcileAsync(bool refreshTargets, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_paused) return;
            if (_health.Status == MonitorStatus.Paused) SetHealth(new(MonitorStatus.Starting));
            var connected = _client.State == VoicemeeterConnectionState.Connected;
            if (connected) await _client.RefreshAsync(cancellationToken).ConfigureAwait(false);
            else await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (_paused) return;
            if (!connected || refreshTargets)
            {
                var targets = await _client.GetBindingTargetsAsync(cancellationToken).ConfigureAwait(false);
                if (!_paused) Ready?.Invoke(this, targets);
            }
            if (!_paused) SetHealth(new(MonitorStatus.Ready));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_paused) SetHealth(new(MonitorStatus.Retrying, _health.FailedAttempts + 1, ex.Message));
            throw;
        }
        finally { _gate.Release(); }
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
            try { await ReconcileAsync(false, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Failed?.Invoke(this, ex); }
            var seconds = _health.FailedAttempts switch { 0 or 1 => 1, 2 => 2, 3 => 5, _ => 10 };
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), _time, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _paused = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _stop.Dispose();
    }
}
