using System.IO;
using System.Text.Json;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>
/// Desktop half of the interactive CLI protocol. It is the only component
/// allowed to combine a ConPTY input operation with a clipboard lease.
/// </summary>
public sealed class InteractiveCliController : IAsyncDisposable
{
    private readonly ClipboardBroker _clipboard = new();
    private readonly Func<InteractiveCliResponseMessage, Task> _send;
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    private sealed class SessionState
    {
        public required string SessionId { get; init; }
        public required ConPtySession Pty { get; init; }
        public string? ConversationId { get; set; }
        public string? LastRequestId { get; set; }
        public ClipboardBroker.ClipboardLease? ClipboardLease { get; set; }
    }

    public InteractiveCliController(Func<InteractiveCliResponseMessage, Task> send) => _send = send;

    public async Task HandleAsync(ManagerHostEvent request)
    {
        if (_disposed || !string.Equals(request.Type, "interactive_cli_request", StringComparison.Ordinal)) return;
        var sessionId = request.SessionId ?? throw new InvalidOperationException("交互式 CLI 请求缺少 sessionId。");
        var requestId = request.RequestId ?? throw new InvalidOperationException("交互式 CLI 请求缺少 requestId。");
        try
        {
            switch (request.Operation)
            {
                case "start": await StartAsync(sessionId, requestId, request); break;
                case "paste_image": await PasteImageAsync(sessionId, requestId, request); break;
                case "write_text": await WriteTextAsync(sessionId, requestId, request); break;
                case "submit": await SubmitAsync(sessionId, requestId); break;
                case "cancel": await CancelAsync(sessionId, requestId); break;
                case "release_clipboard": await ReleaseClipboardAsync(sessionId, requestId); break;
                case "shutdown": await ShutdownAsync(sessionId, requestId); break;
                default: throw new InvalidOperationException($"未知的交互式 CLI 操作：{request.Operation}");
            }
        }
        catch (Exception exception)
        {
            if (_sessions.TryGetValue(sessionId, out var state)) ReleaseClipboard(state);
            await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "error", Code: "INTERACTIVE_CLI_FAILED", Message: exception.Message));
        }
    }

    private async Task StartAsync(string sessionId, string requestId, ManagerHostEvent request)
    {
        if (_sessions.Remove(sessionId, out var previous))
        {
            ReleaseClipboard(previous);
            await previous.Pty.DisposeAsync();
        }

        var pty = new ConPtySession();
        var state = new SessionState { SessionId = sessionId, Pty = pty, ConversationId = request.ConversationId, LastRequestId = requestId };
        _sessions[sessionId] = state;
        pty.OutputReceived += text => SendOutput(state, text);
        pty.Exited += code =>
        {
            var detail = code is null ? "交互式 Antigravity CLI 已退出。" : $"交互式 Antigravity CLI 已退出（代码 {code}）。";
            SendOutputEvent(state, "exited", detail, "AGY_INTERACTIVE_EXITED", detail);
        };
        try
        {
            pty.Start(new InteractiveCliStartOptions(
                request.Executable ?? "agy",
                request.WorkingDirectory ?? throw new InvalidOperationException("交互式 CLI 缺少工作目录。"),
                request.AgentName ?? "ilmatto-manager",
                request.LogPath ?? Path.Combine(Path.GetTempPath(), "ilmatto-antigravity.log"),
                request.ConversationId,
                request.Model,
                request.Effort ?? "medium",
                120));
            await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "ready", ConversationId: state.ConversationId));
        }
        catch
        {
            _sessions.Remove(sessionId);
            await pty.DisposeAsync();
            throw;
        }
    }

    private async Task PasteImageAsync(string sessionId, string requestId, ManagerHostEvent request)
    {
        var state = GetSession(sessionId);
        var path = request.AttachmentPath ?? throw new InvalidOperationException("图片请求缺少附件路径。");
        ValidateImage(path);
        ReleaseClipboard(state);
        state.ClipboardLease = _clipboard.BeginImagePaste(path);
        try
        {
            await state.Pty.WriteControlVAsync();
            state.LastRequestId = requestId;
            // Let the TUI consume the clipboard paste before the following
            // text write arrives on the same ConPTY input stream.
            await Task.Delay(120);
            await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "paste_dispatched"));
        }
        catch
        {
            ReleaseClipboard(state);
            throw;
        }
    }

    private async Task WriteTextAsync(string sessionId, string requestId, ManagerHostEvent request)
    {
        var state = GetSession(sessionId);
        await state.Pty.WriteTextAsync(request.Text ?? string.Empty);
        state.LastRequestId = requestId;
        await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "input_written"));
    }

    private async Task SubmitAsync(string sessionId, string requestId)
    {
        var state = GetSession(sessionId);
        await state.Pty.SubmitAsync();
        state.LastRequestId = requestId;
        await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "submitted"));
    }

    private async Task CancelAsync(string sessionId, string requestId)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            state.LastRequestId = requestId;
            await state.Pty.CancelAsync();
            ReleaseClipboard(state);
        }
        await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "ready"));
    }

    private async Task ReleaseClipboardAsync(string sessionId, string requestId)
    {
        if (_sessions.TryGetValue(sessionId, out var state)) ReleaseClipboard(state);
        await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "clipboard_released"));
    }

    private async Task ShutdownAsync(string sessionId, string requestId)
    {
        if (_sessions.Remove(sessionId, out var state))
        {
            ReleaseClipboard(state);
            await state.Pty.DisposeAsync();
        }
        await SendAsync(new InteractiveCliResponseMessage(sessionId, requestId, "ready"));
    }

    private SessionState GetSession(string sessionId) => _sessions.TryGetValue(sessionId, out var state)
        ? state
        : throw new InvalidOperationException("交互式 Antigravity CLI 会话尚未启动。");

    private void SendOutput(SessionState state, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendOutputEvent(state, "output", text, null);
    }

    private void SendOutputEvent(SessionState state, string eventName, string? text, string? code, string? message = null)
    {
        var requestId = state.LastRequestId ?? Guid.NewGuid().ToString("N");
        _ = SendAsync(new InteractiveCliResponseMessage(state.SessionId, requestId, eventName, text, state.ConversationId, code, message));
    }

    private void ReleaseClipboard(SessionState state)
    {
        var lease = state.ClipboardLease;
        state.ClipboardLease = null;
        try { lease?.Restore(); } catch { }
    }

    private static void ValidateImage(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) throw new FileNotFoundException("图片附件不存在。", fullPath);
        if (info.Length > 20L * 1024 * 1024) throw new InvalidOperationException("单张图片不能超过 20 MiB。");
        var extension = info.Extension.ToLowerInvariant();
        if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".svg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("不支持的图片格式。");
    }

    private async Task SendAsync(InteractiveCliResponseMessage message)
    {
        try { await _send(message); } catch { /* The pipe owner reports transport failures separately. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var sessions = _sessions.Values.ToList();
        _sessions.Clear();
        foreach (var state in sessions)
        {
            ReleaseClipboard(state);
            await state.Pty.DisposeAsync();
        }
    }
}
