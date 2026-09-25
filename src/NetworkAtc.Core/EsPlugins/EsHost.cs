using System.Diagnostics;
using System.IO.Pipes;

namespace NetworkAtc.Core.EsPlugins;

/// <summary>
/// The 32-bit plugin host process and the pipe to it. EuroScope plugins are 32-bit DLLs; Network-ATC is
/// 64-bit, so the plugins live in their own process (NetworkAtc.EsHost.exe) and talk to us over a named pipe.
/// Messages arrive on a background thread.
/// </summary>
public sealed class EsHost : IAsyncDisposable
{
    private readonly object _writeLock = new();
    private Stream? _pipe;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A message from the host (type, payload reader).</summary>
    public event Action<EsMsg, EsReader>? Received;

    /// <summary>The host went away (the reason).</summary>
    public event Action<string>? Closed;

    public bool IsRunning => _pipe != null;

    /// <summary>The host next to the program: esbridge\NetworkAtc.EsHost.exe.</summary>
    public static string? FindHostExe(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, "esbridge", "NetworkAtc.EsHost.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Starts the host process and waits until it is ready.</summary>
    public async Task StartAsync(string hostExe, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("EuroScope plugins only work on Windows");
        string name = $"natc-es-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var process = Process.Start(new ProcessStartInfo(hostExe, name)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(hostExe)!,
        }) ?? throw new InvalidOperationException("Could not start the plugin host");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            await server.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("The plugin host did not respond");
        }
        _process = process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Close($"the plugin host exited (code {SafeExitCode(process)})");
        Attach(server);
        await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>Uses an already connected stream (tests connect a fake host this way).</summary>
    public void Attach(Stream pipe)
    {
        _pipe = pipe;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(pipe, _cts.Token));
    }

    public Task Ready => _ready.Task;

    private async Task ReadLoopAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                int length = BitConverter.ToInt32(header, 0);
                if (length is <= 0 or > 256 * 1024 * 1024) throw new InvalidDataException("Invalid frame from the plugin host");
                var frame = new byte[length];
                await pipe.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
                var type = (EsMsg)frame[0];
                if (type == EsMsg.Ready) _ready.TrySetResult();
                try { Received?.Invoke(type, new EsReader(frame, 1)); }
                catch (Exception e) when (e is not OutOfMemoryException) { /* a broken handler must not stop the pipe */ }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
            Close(e is OperationCanceledException ? "" : "connection to the plugin host lost");
        }
    }

    public void Send(EsWriter w)
    {
        var frame = w.Frame();
        lock (_writeLock)
        {
            if (_pipe == null) return;
            try
            {
                _pipe.Write(frame);
                _pipe.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Reported by the read loop.
            }
        }
    }

    private void Close(string reason)
    {
        Stream? pipe;
        lock (_writeLock)
        {
            pipe = _pipe;
            _pipe = null;
        }
        if (pipe == null) return;
        _ready.TrySetException(new IOException(reason));
        try { pipe.Dispose(); } catch (IOException) { }
        Closed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe != null) Send(new EsWriter(EsMsg.Quit));
        _cts?.Cancel();
        if (_process is { } p)
        {
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await p.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
            }
            p.Dispose();
        }
        Close("");
    }
}
