using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace IlMatto.Desktop.Infrastructure;

public sealed class PipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCancellation;

    public event EventHandler<HostEvent>? EventReceived;
    public event EventHandler<string>? TransportError;

    public PipeClient(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(8_000, cancellationToken);
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ReadLoopAsync(_readCancellation.Token);
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        if (_writer is null) throw new InvalidOperationException("Agent host is not connected.");
        var json = JsonSerializer.Serialize(message, JsonWire.Options);
        await _writeLock.WaitAsync(cancellationToken);
        try { await _writer.WriteLineAsync(json); await _writer.FlushAsync(cancellationToken); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var message = JsonSerializer.Deserialize<HostEvent>(line, JsonWire.Options);
                    if (message is not null) EventReceived?.Invoke(this, message);
                }
                catch (JsonException ex) { TransportError?.Invoke(this, $"协议消息无效：{ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TransportError?.Invoke(this, ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _readCancellation?.Cancel();
        if (_pipe is not null) await _pipe.DisposeAsync();
        _writeLock.Dispose();
        _readCancellation?.Dispose();
    }
}

public sealed class AgentHostProcess : IAsyncDisposable
{
    private Process? _process;
    public PipeClient? Pipe { get; private set; }

    public async Task<PipeClient> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Pipe is not null) return Pipe;
        var pipeName = $"IlMatto-{Guid.NewGuid():N}";
        var scriptPath = FindHostScript();
        var info = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        info.ArgumentList.Add(scriptPath);
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Node Agent Host。");
        _ = DrainOutputAsync(_process.StandardError, isError: true);
        _ = DrainOutputAsync(_process.StandardOutput, isError: false);
        Pipe = new PipeClient(pipeName);
        await Pipe.ConnectAsync(cancellationToken);
        return Pipe;
    }

    private static string FindHostScript()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "IlMatto.AgentHost", "dist", "index.js")),
            Path.Combine(AppContext.BaseDirectory, "AgentHost", "dist", "index.js")
        };
        var script = candidates.FirstOrDefault(File.Exists);
        return script ?? throw new FileNotFoundException("找不到 Agent Host。请先运行 src/IlMatto.AgentHost\"npm run build\"。");
    }

    private static async Task DrainOutputAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync() is { } line) Debug.WriteLine(isError ? $"[host] {line}" : $"[host-out] {line}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Pipe is not null)
        {
            try { await Pipe.SendAsync(new ShutdownMessage()); } catch { }
            await Pipe.DisposeAsync();
        }
        if (_process is { HasExited: false })
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _process.WaitForExitAsync(timeout.Token); } catch { try { _process.Kill(entireProcessTree: true); } catch { } }
        }
        _process?.Dispose();
    }
}
