using System.Diagnostics;
using System.Windows.Threading;
using IlMatto.Desktop.Infrastructure;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Debounces measured air-bubble geometry and persists it off the UI thread.
/// Pending values are grouped by session and width bucket so changing
/// conversations cannot mix measurements or discard a previous session's
/// queued updates.
/// </summary>
internal sealed class ManagerLayoutCacheWriter : IDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _flushTimer;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, Dictionary<int, ManagerMessageLayoutCacheEntry>> _pending = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _disposed;

    public ManagerLayoutCacheWriter(Dispatcher dispatcher)
    {
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = FlushDelay,
        };
        _flushTimer.Tick += FlushTimerOnTick;
    }

    public void Enqueue(string? sessionId, int widthBucket, ManagerMessageLayoutCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || widthBucket <= 0 ||
            entry.MessageIndex < 0 || !double.IsFinite(entry.RowHeight) || entry.RowHeight < 24)
            return;

        lock (_gate)
        {
            if (_disposed) return;
            var key = new CacheKey(sessionId, widthBucket);
            if (!_pending.TryGetValue(key, out var updates))
            {
                updates = new Dictionary<int, ManagerMessageLayoutCacheEntry>();
                _pending.Add(key, updates);
            }

            // Copy the value because callers reuse the row model while the
            // background writer may still be waiting for the debounce window.
            updates[entry.MessageIndex] = new ManagerMessageLayoutCacheEntry
            {
                MessageIndex = entry.MessageIndex,
                RowHeight = entry.RowHeight,
                BubbleHeight = entry.BubbleHeight,
                BubbleWidth = entry.BubbleWidth,
            };
        }

        _flushTimer.Stop();
        _flushTimer.Start();
    }

    public Task FlushAsync()
    {
        _flushTimer.Stop();
        return FlushPendingAsync();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _flushTimer.Stop();
        _flushTimer.Tick -= FlushTimerOnTick;
        try { FlushPendingAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { Debug.WriteLine($"[layout-cache] 刷新失败：{exception.Message}"); }
    }

    private async void FlushTimerOnTick(object? sender, EventArgs e)
    {
        _flushTimer.Stop();
        await FlushPendingAsync().ConfigureAwait(false);
    }

    private async Task FlushPendingAsync()
    {
        Dictionary<CacheKey, List<ManagerMessageLayoutCacheEntry>> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Values.ToList());
            _pending.Clear();
        }

        try
        {
            await _flushGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    foreach (var pair in batch)
                        ManagerLayoutCacheStore.MergeAndSave(pair.Key.SessionId, pair.Key.WidthBucket, pair.Value);
                }).ConfigureAwait(false);
            }
            finally
            {
                _flushGate.Release();
            }
        }
        catch (Exception exception)
        {
            // Layout persistence is an optimization. A locked or unavailable
            // cache must never interrupt ordinary chat or window shutdown.
            Debug.WriteLine($"[layout-cache] 批量保存失败：{exception.Message}");
        }
    }

    private readonly record struct CacheKey(string SessionId, int WidthBucket);
}
