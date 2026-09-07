using System.Windows.Threading;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Restores deferred Markdown documents in a bounded number per rendered UI
/// frame. It is deliberately visual-only: a new scroll gesture or session
/// switch cancels the queue without touching the saved conversation data.
/// </summary>
internal sealed class ChatRenderRecoveryScheduler : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Queue<MarkdownViewer> _visible = new();
    private readonly Queue<MarkdownViewer> _prefetch = new();
    private int _generation;
    private bool _visibleCompleted;
    private Action? _onVisibleCompleted;

    public ChatRenderRecoveryScheduler(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start(IEnumerable<MarkdownViewer> visibleFirst, IEnumerable<MarkdownViewer> prefetch, Action? onVisibleCompleted)
    {
        Cancel();
        _onVisibleCompleted = onVisibleCompleted;
        EnqueueDistinct(_visible, visibleFirst);
        var visibleSet = _visible.ToHashSet();
        EnqueueDistinct(_prefetch, prefetch.Where(viewer => !visibleSet.Contains(viewer)));
        Schedule(_generation);
    }

    public void Cancel()
    {
        _generation++;
        _visible.Clear();
        _prefetch.Clear();
        _visibleCompleted = false;
        _onVisibleCompleted = null;
    }

    public void Dispose() => Cancel();

    private void Schedule(int generation)
    {
        _ = _dispatcher.BeginInvoke(() => RecoverFrame(generation), DispatcherPriority.Render);
    }

    private void RecoverFrame(int generation)
    {
        if (generation != _generation) return;

        var restored = 0;
        while (restored < 2 && TryDequeue(out var viewer))
        {
            if (viewer.IsLoaded && viewer.ResumePendingRender()) restored++;
        }

        if (!_visibleCompleted && _visible.Count == 0)
        {
            _visibleCompleted = true;
            _onVisibleCompleted?.Invoke();
        }

        if (_visible.Count > 0 || _prefetch.Count > 0) Schedule(generation);
    }

    private bool TryDequeue(out MarkdownViewer viewer)
    {
        if (_visible.Count > 0) { viewer = _visible.Dequeue(); return true; }
        if (_prefetch.Count > 0) { viewer = _prefetch.Dequeue(); return true; }
        viewer = null!;
        return false;
    }

    private static void EnqueueDistinct(Queue<MarkdownViewer> target, IEnumerable<MarkdownViewer> viewers)
    {
        var seen = new HashSet<MarkdownViewer>();
        foreach (var viewer in viewers)
            if (seen.Add(viewer)) target.Enqueue(viewer);
    }
}
