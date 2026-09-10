using System.Diagnostics;
using System.Windows.Threading;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>Coalesces UI save requests and serializes detached snapshots one at a time.</summary>
internal sealed class ManagerConversationWriter : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<Action> _capture;
    private Task? _flushTask;
    private bool _dirty;
    private bool _disposed;

    public ManagerConversationWriter(Dispatcher dispatcher, Func<Action> capture)
    {
        _capture = capture;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += OnTick;
    }

    public void RequestSave()
    {
        _timer.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dirty = true;
        // Do not restart: a steady stream of requests must still reach disk.
        if (!_timer.IsEnabled) _timer.Start();
    }

    public Task FlushAsync()
    {
        _timer.Dispatcher.VerifyAccess();
        _timer.Stop();
        return _flushTask is { IsCompleted: false } ? _flushTask : _flushTask = FlushCoreAsync();
    }

    private async Task FlushCoreAsync()
    {
        while (_dirty)
        {
            var write = _capture();
            _dirty = false;
            try { await Task.Run(write); }
            catch
            {
                _dirty = true;
                throw;
            }
        }
        _timer.Stop();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        try { await FlushAsync(); }
        catch (Exception exception)
        {
            // Retain the dirty flag. A later request/explicit shutdown flush
            // retries the latest snapshot instead of silently losing it.
            Debug.WriteLine($"[conversation-save] 保存失败：{exception.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _disposed = true;
    }
}
