namespace VMWV.Core.Settings;

public sealed class SettingsWriter : IAsyncDisposable
{
    private readonly JsonSettingsStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private long _revision;
    private bool _closing;

    public SettingsWriter(JsonSettingsStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<Exception>? SaveFailed;

    public Task QueueAsync(string payload, bool immediately = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var revision = ++_revision;
            _tail = SaveAsync(_tail, payload, revision, immediately);
            return _tail;
        }
    }

    private async Task SaveAsync(Task previous, string payload, long revision, bool immediately)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        lock (_gate) { if (revision != _revision) return; }
        if (!immediately && !_closing)
            await Task.Delay(TimeSpan.FromMilliseconds(250), _time).ConfigureAwait(false);
        lock (_gate) { if (revision != _revision) return; }
        try { await _store.SavePayloadAsync(payload, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke(this, ex);
        }
    }

    public Task FlushAsync() { lock (_gate) return _tail; }

    public async ValueTask DisposeAsync()
    {
        Task tail;
        lock (_gate) { _closing = true; tail = _tail; }
        await tail.ConfigureAwait(false);
    }
}
