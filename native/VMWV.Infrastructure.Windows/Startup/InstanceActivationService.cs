using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VMWV.Infrastructure.Windows.Startup;

public sealed class InstanceActivationService : IAsyncDisposable
{
    private readonly string _name;
    private readonly NamedPipeServerStream? _server;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private Task? _disposeTask;

    public InstanceActivationService(string? name = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        _name = name ?? $"VMWV.Activation.{identity.User!.Value}.{process.SessionId}";
        try
        {
            // A stable OS handle also covers different portable payload directories.
            _server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public bool IsPrimary => _server is not null;
    public event EventHandler<Exception>? Failed;

    public void Start(Func<Task> activate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can receive activations.");
        _worker ??= ListenAsync(activate);
    }

    public async Task RedirectAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".", _name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        while (!client.IsConnected)
        {
            try { await client.ConnectAsync(100, deadline.Token).ConfigureAwait(false); }
            catch (TimeoutException) { await Task.Delay(50, deadline.Token).ConfigureAwait(false); }
        }
        var pid = new byte[sizeof(int)];
        await client.ReadExactlyAsync(pid, deadline.Token).ConfigureAwait(false);
        AllowSetForegroundWindow(BitConverter.ToInt32(pid));
        await client.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
        var ack = new byte[1];
        await client.ReadExactlyAsync(ack, deadline.Token).ConfigureAwait(false);
        if (ack[0] != 1) throw new IOException("Application activation was not acknowledged.");
        await client.WriteAsync(new byte[] { 2 }, deadline.Token).ConfigureAwait(false);
    }

    private async Task ListenAsync(Func<Task> activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _server!.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await _server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), deadline.Token).ConfigureAwait(false);
                var request = new byte[1];
                await _server.ReadExactlyAsync(request, deadline.Token).ConfigureAwait(false);
                if (request[0] != 1) continue;
                await activate().WaitAsync(deadline.Token).ConfigureAwait(false);
                await _server.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                // Disconnect discards unread pipe data; wait until the client consumed the reply.
                await _server.ReadExactlyAsync(request, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // A failed window activation must not stop subsequent requests.
                if (!_stop.IsCancellationRequested) Failed?.Invoke(this, ex);
            }
            finally
            {
                if (_server!.IsConnected) _server.Disconnect();
            }
        }
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= StopAsync());

    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _server?.Dispose();
        _stop.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
