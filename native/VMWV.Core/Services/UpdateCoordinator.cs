using System.Text.Json;

namespace VMWV.Core.Services;

public enum UpdateStatus { NotChecked, Checking, Current, Available, Failed }
public sealed record UpdateState(UpdateStatus Status, DateTimeOffset? LastAttempt = null,
    DateTimeOffset? LastSuccess = null, UpdateCheckResult? Result = null);

public sealed class UpdateCoordinator : IAsyncDisposable
{
    private readonly IUpdateService _service;
    private readonly Version _version;
    private readonly string _cachePath;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _active;
    private Task? _worker;
    private volatile bool _automatic;
    public UpdateState State { get; private set; } = new(UpdateStatus.NotChecked);
    public event EventHandler<UpdateState>? Changed;

    public UpdateCoordinator(IUpdateService service, Version version, string cachePath, TimeProvider? time = null)
    {
        _service = service;
        _version = version;
        _cachePath = cachePath;
        _time = time ?? TimeProvider.System;
        try
        {
            State = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(cachePath)) ?? State;
            if (!Enum.IsDefined(State.Status)
                || State.Status is UpdateStatus.Current or UpdateStatus.Available && State.Result is null
                || State.Result is { } invalid && (invalid.LatestVersion is null
                    || invalid.ReleasePage is not { IsAbsoluteUri: true } uri || uri.Scheme != Uri.UriSchemeHttps))
                State = new(UpdateStatus.NotChecked);
            if (State.Status == UpdateStatus.Checking) State = State with { Status = UpdateStatus.Failed };
            if (State.Result is { } cached)
            {
                var result = cached with { IsUpdateAvailable = cached.LatestVersion > version };
                State = State with { Result = result, Status = State.Status == UpdateStatus.Failed
                    ? UpdateStatus.Failed : result.IsUpdateAvailable ? UpdateStatus.Available : UpdateStatus.Current };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
    }

    public void Start(bool automatic)
    {
        _automatic = automatic;
        _worker ??= RunAsync();
    }

    public void SetAutomatic(bool enabled) => _automatic = enabled;

    public Task CheckAsync(bool manual)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return Task.CompletedTask;
            if (_active is { IsCompleted: false }) return _active;
            if (!manual && (!_automatic || State.LastAttempt is { } last
                && _time.GetUtcNow() - last < TimeSpan.FromHours(24))) return Task.CompletedTask;
            return _active = CheckCoreAsync();
        }
    }

    private async Task CheckCoreAsync()
    {
        await Task.Yield();
        State = State with { Status = UpdateStatus.Checking, LastAttempt = _time.GetUtcNow() };
        Changed?.Invoke(this, State);
        await SaveCacheAsync().ConfigureAwait(false);
        try
        {
            var result = await _service.CheckAsync(_version, _stop.Token).ConfigureAwait(false);
            State = State with { Status = result.IsUpdateAvailable ? UpdateStatus.Available : UpdateStatus.Current,
                LastSuccess = _time.GetUtcNow(), Result = result };
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        catch (Exception) { State = State with { Status = UpdateStatus.Failed }; }
        Changed?.Invoke(this, State);
        await SaveCacheAsync().ConfigureAwait(false);
    }

    private async Task SaveCacheAsync()
    {
        var temporary = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(State), _stop.Token).ConfigureAwait(false);
            File.Move(temporary, _cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await CheckAsync(false).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromMinutes(1), _time, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        Task? active;
        lock (_gate) active = _active;
        if (active is not null) await active.ConfigureAwait(false);
        _stop.Dispose();
    }
}
