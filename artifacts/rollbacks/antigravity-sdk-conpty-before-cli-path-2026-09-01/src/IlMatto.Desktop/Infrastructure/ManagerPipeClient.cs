using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace IlMatto.Desktop.Infrastructure;

public sealed class ManagerPipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCancellation;
    public event EventHandler<ManagerHostEvent>? EventReceived;
    public event EventHandler<string>? TransportError;
    public ManagerPipeClient(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(8_000, cancellationToken);
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ReadLoopAsync(_readCancellation.Token);
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        if (_writer is null) throw new InvalidOperationException("Manager Host 未连接。");
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
                    var message = JsonSerializer.Deserialize<ManagerHostEvent>(line, JsonWire.Options);
                    if (message is not null) EventReceived?.Invoke(this, message);
                }
                catch (JsonException exception) { TransportError?.Invoke(this, $"Manager 协议消息无效：{exception.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { TransportError?.Invoke(this, exception.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _readCancellation?.Cancel();
        if (_pipe is not null) await _pipe.DisposeAsync();
        _writeLock.Dispose();
        _readCancellation?.Dispose();
    }
}

public sealed class ManagerHostProcess : IAsyncDisposable
{
    private Process? _process;
    public ManagerPipeClient? Pipe { get; private set; }

    public async Task<ManagerPipeClient> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Pipe is not null) return Pipe;
        var pipeName = $"IlMatto-Manager-{Guid.NewGuid():N}";
        var scriptPath = FindHostScript();
        var info = new ProcessStartInfo
        {
            FileName = "node", UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        info.ArgumentList.Add(scriptPath); info.ArgumentList.Add("--pipe"); info.ArgumentList.Add(pipeName);
        _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Manager Host。");
        _ = DrainAsync(_process.StandardError, true); _ = DrainAsync(_process.StandardOutput, false);
        Pipe = new ManagerPipeClient(pipeName);
        await Pipe.ConnectAsync(cancellationToken);
        return Pipe;
    }

    private static string FindHostScript()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "IlMatto.ManagerHost", "dist", "index.js")),
            Path.Combine(AppContext.BaseDirectory, "ManagerHost", "dist", "index.js")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("找不到 Manager Host。请先运行 src/IlMatto.ManagerHost\\npm run build。");
    }

    private static async Task DrainAsync(StreamReader reader, bool error)
    {
        while (await reader.ReadLineAsync() is { } line) Debug.WriteLine(error ? $"[manager] {line}" : $"[manager-out] {line}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Pipe is not null) { try { await Pipe.SendAsync(new ManagerShutdownMessage()); } catch { } await Pipe.DisposeAsync(); }
        if (_process is { HasExited: false })
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await _process.WaitForExitAsync(timeout.Token); }
            catch { try { _process.Kill(true); } catch { } }
        }
        _process?.Dispose();
    }
}
