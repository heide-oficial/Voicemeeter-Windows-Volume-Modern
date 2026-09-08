using System.Text;

namespace VMWV.Core.Services;

public sealed record DiagnosticRecord(DateTimeOffset Time, string EventId, string Category, string Message);

public sealed class DiagnosticLogWriter : IAsyncDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly LinkedList<DiagnosticRecord> _pending = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Task _worker;
    private bool _closing;
    private int _dropped;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;
    public Exception? LastError { get; private set; }

    public DiagnosticLogWriter(string directory)
    {
        _directory = directory;
        _worker = Task.Run(WriteLoopAsync);
    }

    public void Enqueue(DiagnosticRecord record)
    {
        lock (_gate)
        {
            if (_closing) return;
            if (_pending.Count >= 1024)
            {
                // Prefer retaining errors over repetitive activity when storage is slow.
                var disposable = _pending.First;
                while (disposable is not null && disposable.Value.EventId.EndsWith(".error", StringComparison.Ordinal))
                    disposable = disposable.Next;
                _dropped++;
                if (disposable is not null) _pending.Remove(disposable);
                else if (record.EventId.EndsWith(".error", StringComparison.Ordinal)) _pending.RemoveFirst();
                else return;
            }
            _pending.AddLast(record);
            if (_pending.Count >= 100) Signal();
        }
    }

    private void Signal()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    private async Task WriteLoopAsync()
    {
        while (true)
        {
            await _signal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            DiagnosticRecord[] batch;
            int dropped;
            bool closing;
            lock (_gate)
            {
                batch = _pending.ToArray();
                _pending.Clear();
                dropped = _dropped;
                _dropped = 0;
                closing = _closing;
            }
            try
            {
                if (batch.Length > 0 || dropped > 0)
                {
                    Directory.CreateDirectory(_directory);
                    var text = new StringBuilder();
                    foreach (var record in batch)
                        text.AppendLine($"{record.Time:O}\t{record.EventId}\t{record.Category}\t{record.Message}");
                    if (dropped > 0) text.AppendLine($"{DateTimeOffset.Now:O}\tlog.overflow\t{dropped} records omitted.");
                    await File.AppendAllTextAsync(Path.Combine(_directory, $"{DateTimeOffset.Now:yyyy-MM-dd}.log"), text.ToString()).ConfigureAwait(false);
                    ApplyRetention();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex; }
            if (closing) break;
        }
    }

    private void ApplyRetention()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRetention < TimeSpan.FromSeconds(30)) return;
        _lastRetention = now;
        var files = new DirectoryInfo(_directory).GetFiles("*.log").OrderBy(file => file.LastWriteTimeUtc).ToList();
        long total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= now.AddDays(-7).UtcDateTime && total <= 50 * 1024 * 1024) break;
            total -= file.Length;
            file.Delete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _closing = true; Signal(); }
        await _worker.ConfigureAwait(false);
    }
}
