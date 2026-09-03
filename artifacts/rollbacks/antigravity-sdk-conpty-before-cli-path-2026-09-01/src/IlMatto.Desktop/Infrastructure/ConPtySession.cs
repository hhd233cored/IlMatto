using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IlMatto.Desktop.Infrastructure;

public sealed record InteractiveCliStartOptions(
    string Executable,
    string WorkingDirectory,
    string AgentName,
    string LogPath,
    string? ConversationId,
    string? Model,
    string Effort,
    int TimeoutSeconds);

/// <summary>
/// Minimal Windows ConPTY wrapper for one hidden interactive AGY process.
/// It intentionally exposes only byte-oriented prompt controls required by
/// the ManagerHost protocol; no arbitrary process or window control is
/// surfaced to the rest of the desktop application.
/// </summary>
public sealed class ConPtySession : IAsyncDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const short DefaultColumns = 160;
    private const short DefaultRows = 48;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _outputCancellation;
    private FileStream? _input;
    private FileStream? _output;
    private SafeFileHandle? _inputHandle;
    private SafeFileHandle? _outputHandle;
    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private int _exitRaised;
    private int _exitNotificationEnabled;
    private bool _started;

    public event Action<string>? OutputReceived;
    public event Action<int?>? Exited;

    public bool IsRunning => _started && _processHandle != IntPtr.Zero;

    public void Start(InteractiveCliStartOptions options)
    {
        if (_started) return;
        if (string.IsNullOrWhiteSpace(options.Executable)) throw new ArgumentException("agy 可执行文件不能为空。", nameof(options));
        if (!Directory.Exists(options.WorkingDirectory)) throw new DirectoryNotFoundException(options.WorkingDirectory);

        _exitRaised = 0;
        Volatile.Write(ref _exitNotificationEnabled, 0);

        SafeFileHandle? ptyInputRead = null;
        SafeFileHandle? ptyOutputWrite = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeValue = IntPtr.Zero;
        PROCESS_INFORMATION processInfo = default;
        try
        {
            Ensure(CreatePipe(out ptyInputRead, out _inputHandle, IntPtr.Zero, 0), "CreatePipe(input)");
            Ensure(CreatePipe(out _outputHandle, out ptyOutputWrite, IntPtr.Zero, 0), "CreatePipe(output)");
            Ensure(SetHandleInformation(_inputHandle, HandleFlagInherit, 0), "SetHandleInformation(input)");
            Ensure(SetHandleInformation(_outputHandle, HandleFlagInherit, 0), "SetHandleInformation(output)");
            Ensure(CreatePseudoConsole(new COORD(DefaultColumns, DefaultRows), ptyInputRead, ptyOutputWrite, 0, out _pseudoConsole) == 0, "CreatePseudoConsole");

            var attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            if (attributeSize == IntPtr.Zero) ThrowLastError("InitializeProcThreadAttributeList(size)");
            attributeList = Marshal.AllocHGlobal(attributeSize);
            Ensure(InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize), "InitializeProcThreadAttributeList");
            attributeValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(attributeValue, _pseudoConsole);
            Ensure(UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, attributeValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");

            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                AttributeList = attributeList,
            };
            var commandLine = BuildCommandLine(options);
            Ensure(CreateProcess(
                null,
                new StringBuilder(commandLine),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                IntPtr.Zero,
                options.WorkingDirectory,
                ref startup,
                out processInfo), "CreateProcess(agy)");

            _processHandle = processInfo.Process;
            if (processInfo.Thread != IntPtr.Zero)
            {
                CloseHandle(processInfo.Thread);
                processInfo.Thread = IntPtr.Zero;
            }
            // CreatePipe creates synchronous handles. Passing isAsync:true
            // makes FileStream reject them before the interactive session can
            // start, which was surfaced to the user as AGY_INTERACTIVE_EXITED.
            _input = new FileStream(_inputHandle, FileAccess.Write, 8192, isAsync: false);
            _output = new FileStream(_outputHandle, FileAccess.Read, 8192, isAsync: false);
            _outputCancellation = new CancellationTokenSource();
            _started = true;
            Volatile.Write(ref _exitNotificationEnabled, 1);
            _ = ReadOutputAsync(_output, _outputCancellation.Token);
        }
        catch
        {
            Volatile.Write(ref _exitNotificationEnabled, 0);
            _outputCancellation?.Cancel();
            _input?.Dispose(); _output?.Dispose();
            _input = null; _output = null;
            _inputHandle?.Dispose(); _outputHandle?.Dispose();
            _inputHandle = null; _outputHandle = null;
            if (_processHandle != IntPtr.Zero)
            {
                try { TerminateProcess(_processHandle, 1); } catch { }
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            else if (processInfo.Process != IntPtr.Zero)
            {
                try { TerminateProcess(processInfo.Process, 1); } catch { }
                CloseHandle(processInfo.Process);
            }
            if (_pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }
            _started = false;
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (attributeValue != IntPtr.Zero) Marshal.FreeHGlobal(attributeValue);
            // The pseudo console owns these ends after CreatePseudoConsole.
            ptyInputRead?.Dispose();
            ptyOutputWrite?.Dispose();
        }
    }

    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        // In AGY's interactive prompt Ctrl+J inserts a newline without
        // submitting. ConPTY transports that control as the LF byte.
        return WriteBytesAsync(Encoding.UTF8.GetBytes(normalized), cancellationToken);
    }

    public Task WriteControlVAsync(CancellationToken cancellationToken = default) => WriteBytesAsync(new byte[] { 0x16 }, cancellationToken);

    public Task SubmitAsync(CancellationToken cancellationToken = default) => WriteBytesAsync(new byte[] { 0x0D }, cancellationToken);

    public Task CancelAsync(CancellationToken cancellationToken = default) => WriteBytesAsync(new byte[] { 0x1B }, cancellationToken);

    private async Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (_input is null || !_started) throw new InvalidOperationException("交互式 Antigravity CLI 尚未启动。");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_input is null || !_started) throw new InvalidOperationException("交互式 Antigravity CLI 尚未启动。");
            await _input.WriteAsync(bytes.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadOutputAsync(FileStream output, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(output, new UTF8Encoding(false, false), false, 8192, true);
            var buffer = new char[8192];
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                EmitChunks(new string(buffer, 0, count));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { OutputReceived?.Invoke($"\n[IlMatto ConPTY error] {exception.Message}\n"); }
        finally
        {
            _started = false;
            RaiseExited(TryGetExitCode());
        }
    }

    private void EmitChunks(string text)
    {
        const int maxUtf8Bytes = 16 * 1024;
        var chunk = new StringBuilder();
        foreach (var character in text)
        {
            if (chunk.Length > 0 && Encoding.UTF8.GetByteCount(chunk.ToString()) + Encoding.UTF8.GetByteCount(new[] { character }) > maxUtf8Bytes)
            {
                OutputReceived?.Invoke(chunk.ToString());
                chunk.Clear();
            }
            chunk.Append(character);
        }
        if (chunk.Length > 0) OutputReceived?.Invoke(chunk.ToString());
    }

    private void RaiseExited(int? exitCode)
    {
        if (Volatile.Read(ref _exitNotificationEnabled) == 0) return;
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0) Exited?.Invoke(exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started && _pseudoConsole == IntPtr.Zero) return;
        Volatile.Write(ref _exitNotificationEnabled, 0);
        _outputCancellation?.Cancel();
        if (_processHandle != IntPtr.Zero)
        {
            try { TerminateProcess(_processHandle, 1); } catch { }
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        if (_input is not null) await _input.DisposeAsync();
        if (_output is not null) await _output.DisposeAsync();
        _input = null; _output = null;
        _inputHandle?.Dispose(); _outputHandle?.Dispose();
        _inputHandle = null; _outputHandle = null;
        if (_pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
        _outputCancellation?.Dispose(); _outputCancellation = null;
        _started = false;
        _writeGate.Dispose();
    }

    private int? TryGetExitCode()
    {
        if (_processHandle == IntPtr.Zero || !GetExitCodeProcess(_processHandle, out var exitCode) || exitCode == StillActive)
            return null;
        return unchecked((int)exitCode);
    }

    private static string BuildCommandLine(InteractiveCliStartOptions options)
    {
        var args = new List<string>
        {
            "--agent", options.AgentName,
            "--sandbox",
            "--print-timeout", $"{Math.Max(10, options.TimeoutSeconds)}s",
            "--log-file", options.LogPath,
        };
        if (!string.IsNullOrWhiteSpace(options.ConversationId)) args.AddRange(new[] { "--conversation", options.ConversationId });
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            args.Add("--model"); args.Add(options.Model);
        }
        if (ShouldPassEffort(options.Model)) args.AddRange(new[] { "--effort", options.Effort });

        var executable = options.Executable.Trim();
        var command = string.Join(' ', new[] { QuoteArgument(executable) }.Concat(args.Select(QuoteArgument)));
        var extension = Path.GetExtension(executable);
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrEmpty(extension) && !Path.IsPathRooted(executable)))
            return $"cmd.exe /d /s /c \"{command}\"";
        return command;
    }

    private static bool ShouldPassEffort(string? model) => string.IsNullOrWhiteSpace(model) ||
        !(model.Contains("-low", StringComparison.OrdinalIgnoreCase) || model.Contains("-medium", StringComparison.OrdinalIgnoreCase) || model.Contains("-high", StringComparison.OrdinalIgnoreCase));

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static void Ensure(bool success, string operation)
    {
        if (!success) ThrowLastError(operation);
    }

    private static void ThrowLastError(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operation} 失败");

    [StructLayout(LayoutKind.Sequential)] private readonly struct COORD(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved3;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private const uint HandleFlagInherit = 0x00000001;
    private const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
