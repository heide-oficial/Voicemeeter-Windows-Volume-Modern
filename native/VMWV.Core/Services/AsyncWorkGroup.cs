namespace VMWV.Core.Services;

public sealed class AsyncWorkGroup
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = [];
    private bool _stopping;
    public event EventHandler<Exception>? Failed;

    public void Run(Func<Task> operation)
    {
        lock (_gate)
        {
            if (_stopping) return;
            var task = ObserveAsync(operation);
            _tasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_gate) _tasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ObserveAsync(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed?.Invoke(this, ex); }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return Task.WhenAll(_tasks.ToArray());
        }
    }
}
