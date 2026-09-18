using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;
using WpfListBox = System.Windows.Controls.ListBox;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Keeps a normal WPF ListBox small while its scroll extent represents the
/// whole conversation. The items source contains one spacer above the material
/// range, the materialized rows, and one spacer below it. Row heights come from
/// <see cref="ChatTimelineLayoutIndex"/>, so no absolute item coordinates need
/// to be stored or corrected while a thumb is moving.
/// </summary>
internal sealed class ChatTimelineController : IDisposable
{
    private const double HeightEpsilon = 0.5;

    private readonly WpfListBox _list;
    private readonly ScrollViewer _scrollViewer;
    private readonly ObservableCollection<object> _items;
    private readonly ChatMessageLayoutCache _layoutCache = new();
    private readonly ChatTimelineLayoutIndex _layout;
    private readonly ManagerLayoutCacheWriter _layoutCacheWriter;
    private readonly ChatTimelineSpacer _topSpacer = new();
    private readonly ChatTimelineSpacer _bottomSpacer = new();
    private readonly Dictionary<ManagerChatEntry, ChatTimelineMessageRow> _rows = new();
    private readonly HashSet<ManagerChatEntry> _freshEntries = new();
    private readonly Dictionary<ManagerChatEntry, double> _persistentBubbleHeights = new();
    private readonly Dictionary<ManagerChatEntry, double> _persistentBubbleWidths = new();
    private readonly PriorityQueue<ChatTimelineMessageRow, double> _renderQueue = new();

    private ObservableCollection<ManagerChatEntry>? _entries;
    private int _firstMaterialized = -1;
    private int _lastMaterialized = -1;
    private int _lastWidthBucket = -1;
    private int _persistentBubbleWidthBucket = -1;
    private int _renderGeneration;
    private string? _sessionId;
    private bool _isThumbDragging;
    private bool _isDisposed;
    private bool _refreshQueued;
    private bool _anchorCompensationQueued;
    private double _pendingAnchorDelta;

    /// <summary>
    /// The air timeline initially creates fixed-height shells. Once a row is
    /// close to the viewport, the render scheduler promotes it to the full
    /// content template. The full-list diagnostic path remains available in
    /// the window for comparing Markdown costs.
    /// </summary>
    public bool PlaceholderOnly { get; set; }

    public ChatTimelineController(WpfListBox list, ScrollViewer scrollViewer, ObservableCollection<object> items)
    {
        _list = list;
        _scrollViewer = scrollViewer;
        _items = items;
        _layout = new ChatTimelineLayoutIndex(_layoutCache);
        _layoutCacheWriter = new ManagerLayoutCacheWriter(list.Dispatcher);
    }

    public void Attach()
    {
        _list.SizeChanged += ListOnSizeChanged;
        _scrollViewer.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(ScrollThumbOnDragStarted), true);
        _scrollViewer.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(ScrollThumbOnDragCompleted), true);
        _scrollViewer.ScrollChanged += ScrollViewerOnScrollChanged;
    }

    public void SetEntries(ObservableCollection<ManagerChatEntry> entries)
        => SetEntries(entries, null);

    public void SetEntries(ObservableCollection<ManagerChatEntry> entries, string? sessionId)
    {
        if (ReferenceEquals(_entries, entries) && string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            SynchronizeLayout(preserveAnchor: true);
            RefreshVisibleItems();
            return;
        }

        _ = _layoutCacheWriter.FlushAsync();
        UnsubscribeEntries();
        CancelQueuedRendering();
        _entries = entries;
        _pendingAnchorDelta = 0;
        _sessionId = sessionId;
        _entries.CollectionChanged += EntriesOnCollectionChanged;
        foreach (var entry in entries) entry.PropertyChanged += EntryOnPropertyChanged;

        _rows.Clear();
        _freshEntries.Clear();
        _persistentBubbleHeights.Clear();
        _persistentBubbleWidths.Clear();
        _persistentBubbleWidthBucket = -1;
        _firstMaterialized = _lastMaterialized = -1;
        _items.Clear();
        ImportPersistentHeights(entries, sessionId);
        SynchronizeLayout(preserveAnchor: false);
        // A newly selected short historical session must never inherit the
        // previous session's large pixel offset. The window schedules its
        // normal ScrollToEnd afterwards, once this fresh extent exists.
        SetVerticalOffsetSafely(0);
        RefreshVisibleItems();
    }

    // Drag state only prevents automatic offset compensation from fighting
    // the user's thumb. Rendering keeps its normal distance-prioritized queue.
    public void BeginThumbDrag() => _isThumbDragging = true;

    public void CompleteThumbDrag() => _isThumbDragging = false;

    public void RecordNaturalHeight(ChatTimelineMessageRow row, ChatMessageMeasuredEventArgs measurement)
    {
        if (_isDisposed || _entries is null || !ReferenceEquals(row.Entry, measurement.Entry)) return;
        if (row.Index < 0 || row.Index >= _layout.Count || !ReferenceEquals(_layout[row.Index], row.Entry)) return;

        var anchor = _layout.FindIndexAtOffset(_scrollViewer.VerticalOffset + _pendingAnchorDelta);
        // Row heights are keyed by the timeline viewport, not by the narrower
        // two-column bubble slot that produced a presenter measurement.
        var delta = _layout.UpdateMeasuredHeight(row.Index, GetContentWidth(), measurement.Height);
        row.ReservedHeight = _layout.GetHeight(row.Index);
        row.ReservedContentHeight = GetContentHeight(row.Entry, row.Index);
        QueuePersistedMeasurement(row);
        if (Math.Abs(delta) < HeightEpsilon) return;

        UpdateSpacerHeights();
        QueueSafeAnchorCompensation(row.Index, anchor, delta);
    }

    public void RecordNaturalContentHeight(ChatTimelineMessageRow row, ChatMessageMeasuredEventArgs measurement)
    {
        if (_isDisposed || _entries is null || !ReferenceEquals(row.Entry, measurement.Entry)) return;
        if (row.Index < 0 || row.Index >= _layout.Count || !ReferenceEquals(_layout[row.Index], row.Entry)) return;

        // Header/date/font metrics are not fixed constants. Measure the whole
        // arranged container so the live row and its replacement spacer occupy
        // exactly the same space. A pending layout will report again afterwards.
        if (!row.RenderContent || _list.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container ||
            !container.IsMeasureValid || !container.IsArrangeValid || container.ActualHeight < 24)
            return;
        RecordNaturalHeight(row, new ChatMessageMeasuredEventArgs(row.Entry, container.ActualHeight, container.ActualWidth));
    }

    public void RecordNaturalBubbleSize(ChatTimelineMessageRow row, double width, double height)
    {
        if (_isDisposed || _entries is null || row.Index < 0 ||
            row.Index >= _layout.Count || !ReferenceEquals(_layout[row.Index], row.Entry) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width < 24 || height < 24)
            return;

        row.ReservedBubbleWidth = Math.Clamp(width, 24, 12000);
        row.ReservedBubbleHeight = Math.Clamp(height, 24, 12000);
        _persistentBubbleWidthBucket = ChatMessageLayoutCache.GetWidthBucket(GetContentWidth());
        _persistentBubbleHeights[row.Entry] = row.ReservedBubbleHeight;
        _persistentBubbleWidths[row.Entry] = row.ReservedBubbleWidth;
        QueuePersistedMeasurement(row);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _list.SizeChanged -= ListOnSizeChanged;
        _scrollViewer.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(ScrollThumbOnDragStarted));
        _scrollViewer.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(ScrollThumbOnDragCompleted));
        _scrollViewer.ScrollChanged -= ScrollViewerOnScrollChanged;
        UnsubscribeEntries();
        CancelQueuedRendering();
        _layoutCacheWriter.Dispose();
        _items.Clear();
    }

    private void EntriesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var entry in e.OldItems.OfType<ManagerChatEntry>()) entry.PropertyChanged -= EntryOnPropertyChanged;
        if (e.NewItems is not null)
            foreach (var entry in e.NewItems.OfType<ManagerChatEntry>()) entry.PropertyChanged += EntryOnPropertyChanged;

        if (e.OldItems is not null)
            foreach (var entry in e.OldItems.OfType<ManagerChatEntry>())
            {
                _rows.Remove(entry);
                _freshEntries.Remove(entry);
                _persistentBubbleHeights.Remove(entry);
                _persistentBubbleWidths.Remove(entry);
            }

        if (e.NewItems is not null)
            foreach (var entry in e.NewItems.OfType<ManagerChatEntry>()) _freshEntries.Add(entry);

        SynchronizeLayout(preserveAnchor: true);
        QueueRefresh();
    }

    private void EntryOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ManagerChatEntry entry || _entries is null) return;
        if (e.PropertyName is not (nameof(ManagerChatEntry.Text) or nameof(ManagerChatEntry.IsStreamingText) or
            nameof(ManagerChatEntry.IsThinking) or nameof(ManagerChatEntry.ShowDateSeparator) or
            nameof(ManagerChatEntry.CodeResult) or nameof(ManagerChatEntry.Runtime) or
            nameof(ManagerChatEntry.IsPendingAgent)))
            return;

        _layout.Invalidate(entry, GetContentWidth());
        if (_rows.TryGetValue(entry, out var row))
        {
            row.ReservedHeight = _layout.GetHeight(row.Index);
            row.ReservedContentHeight = GetContentHeight(entry, row.Index);
            row.ReservedBubbleHeight = GetBubbleHeight(entry, row.Index);
            row.ReservedBubbleWidth = GetBubbleWidth(entry);
            // Streaming updates remain live, while a completed message is
            // promoted by the viewport scheduler instead of rebuilding a
            // distant row immediately.
            if (entry.IsStreamingText || entry.IsThinking) row.RenderContent = true;
        }
        UpdateSpacerHeights();
        QueueRefresh();
    }

    private void ListOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SynchronizeLayout(preserveAnchor: true);
        QueueRefresh();
    }

    private void ScrollThumbOnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (IsVerticalScrollThumb(e.OriginalSource as DependencyObject)) BeginThumbDrag();
    }

    private void ScrollThumbOnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (IsVerticalScrollThumb(e.OriginalSource as DependencyObject)) CompleteThumbDrag();
    }

    private bool IsVerticalScrollThumb(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, _scrollViewer);
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ScrollBar bar)
                return bar.Orientation == System.Windows.Controls.Orientation.Vertical && ReferenceEquals(bar.TemplatedParent, _scrollViewer);
        return false;
    }

    private void ScrollViewerOnScrollChanged(object sender, ScrollChangedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_isDisposed || _refreshQueued || _list.Dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        _ = _list.Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (!_isDisposed) RefreshVisibleItems();
        }, DispatcherPriority.Render);
    }

    private void SynchronizeLayout(bool preserveAnchor)
    {
        if (_entries is null) return;
        var widthBucket = ChatMessageLayoutCache.GetWidthBucket(GetContentWidth());
        if (widthBucket != _lastWidthBucket && widthBucket != _persistentBubbleWidthBucket)
        {
            _persistentBubbleHeights.Clear();
            _persistentBubbleWidths.Clear();
            _persistentBubbleWidthBucket = -1;
        }
        var oldAnchor = preserveAnchor && _layout.Count > 0 ? _layout.FindIndexAtOffset(_scrollViewer.VerticalOffset) : -1;
        var oldAnchorEntry = oldAnchor >= 0 ? _layout[oldAnchor] : null;
        var relativeOffset = oldAnchor >= 0 ? _scrollViewer.VerticalOffset - _layout.GetTop(oldAnchor) : 0;
        var changed = _layout.Synchronize(_entries, GetContentWidth());
        _lastWidthBucket = widthBucket;
        if (!changed || oldAnchorEntry is null) return;

        var newAnchor = _layout.IndexOf(oldAnchorEntry);
        if (newAnchor < 0) return;
        SetVerticalOffsetSafely(_layout.GetTop(newAnchor) + relativeOffset);
    }

    private void RefreshVisibleItems()
    {
        if (_isDisposed || _entries is null) return;
        SynchronizeLayout(preserveAnchor: true);
        if (_layout.Count == 0)
        {
            CancelQueuedRendering();
            _items.Clear();
            _firstMaterialized = _lastMaterialized = -1;
            return;
        }

        var viewport = Math.Max(1, _scrollViewer.ViewportHeight > 0 ? _scrollViewer.ViewportHeight : _list.ActualHeight);
        var offset = Math.Clamp(_scrollViewer.VerticalOffset, 0, Math.Max(0, _layout.TotalHeight - viewport));
        var overscan = Math.Max(viewport, 360);
        var start = _layout.FindIndexAtOffset(Math.Max(0, offset - overscan));
        var end = _layout.FindIndexAtOffset(Math.Min(Math.Max(0, _layout.TotalHeight - 0.001), offset + viewport + overscan));
        ReconcileMaterializedRange(start, end);
        QueueVisiblePlaceholderRowsForRendering();
    }

    private void ReconcileMaterializedRange(int start, int end)
    {
        if (_layout.Count == 0) return;
        start = Math.Clamp(start, 0, _layout.Count - 1);
        end = Math.Clamp(end, start, _layout.Count - 1);

        // Rows that are no longer near the viewport keep their model and
        // measured geometry, but release the expensive content template. A
        // later visit will enqueue them again instead of retaining a large
        // Markdown visual tree for the whole conversation.
        foreach (var row in _rows.Values)
        {
            if (!row.Entry.IsStreamingText && !row.Entry.IsThinking &&
                (row.Index < start || row.Index > end))
            {
                row.RenderContent = false;
                _freshEntries.Remove(row.Entry);
            }
        }

        // A large jump (especially during startup or thumb dragging) can make
        // the old and new view windows disjoint. Removing rows one by one in
        // that case eventually tries to remove the bottom spacer as a row.
        // Rebuild the small materialized window instead. The same recovery is
        // used if a deferred collection update left the item source out of
        // sync with the remembered range.
        if (_firstMaterialized < 0 || end < _firstMaterialized || start > _lastMaterialized ||
            !HasConsistentMaterializedItems())
        {
            RebuildMaterializedRange(start, end);
        }
        else
        {
            while (_firstMaterialized < start)
            {
                _items.RemoveAt(1);
                _firstMaterialized++;
            }
            while (_lastMaterialized > end)
            {
                _items.RemoveAt(_items.Count - 2);
                _lastMaterialized--;
            }
            while (_firstMaterialized > start)
            {
                _firstMaterialized--;
                _items.Insert(1, GetOrCreateRow(_firstMaterialized));
            }
            while (_lastMaterialized < end)
            {
                _lastMaterialized++;
                _items.Insert(_items.Count - 1, GetOrCreateRow(_lastMaterialized));
            }
        }
        UpdateSpacerHeights();
    }

    private void RebuildMaterializedRange(int start, int end)
    {
        _items.Clear();
        _items.Add(_topSpacer);
        for (var index = start; index <= end; index++) _items.Add(GetOrCreateRow(index));
        _items.Add(_bottomSpacer);
        _firstMaterialized = start;
        _lastMaterialized = end;
    }

    private bool HasConsistentMaterializedItems()
    {
        if (_firstMaterialized < 0 || _lastMaterialized < _firstMaterialized ||
            _lastMaterialized >= _layout.Count)
            return false;

        var rowCount = _lastMaterialized - _firstMaterialized + 1;
        if (_items.Count != rowCount + 2 || !ReferenceEquals(_items[0], _topSpacer) ||
            !ReferenceEquals(_items[^1], _bottomSpacer))
            return false;

        for (var offset = 0; offset < rowCount; offset++)
        {
            if (_items[offset + 1] is not ChatTimelineMessageRow row ||
                row.Index != _firstMaterialized + offset ||
                !ReferenceEquals(row.Entry, _layout[_firstMaterialized + offset]))
                return false;
        }

        return true;
    }

    private ChatTimelineMessageRow GetOrCreateRow(int index)
    {
        var entry = _layout[index];
        if (_rows.TryGetValue(entry, out var row))
        {
            row.Index = index;
            row.ReservedHeight = _layout.GetHeight(index);
            row.ReservedContentHeight = GetContentHeight(entry, index);
            row.ReservedBubbleHeight = GetBubbleHeight(entry, index);
            row.ReservedBubbleWidth = GetBubbleWidth(entry);
            return row;
        }

        // New messages stay on the natural-layout path while they are active.
        // Historical rows start as fixed shells and are promoted by the
        // viewport scheduler once they are close enough to the user.
        var renderContent = entry.IsStreamingText || entry.IsThinking || _freshEntries.Contains(entry);
        row = new ChatTimelineMessageRow(entry, index, _layout.GetHeight(index), GetContentHeight(entry, index),
            GetBubbleHeight(entry, index), GetBubbleWidth(entry), renderContent);
        _rows.Add(entry, row);
        return row;
    }

    private void UpdateSpacerHeights()
    {
        if (_firstMaterialized < 0 || _lastMaterialized < _firstMaterialized) return;
        _topSpacer.Height = _layout.GetTop(_firstMaterialized);
        _bottomSpacer.Height = Math.Max(0, _layout.TotalHeight - _layout.GetTop(_lastMaterialized + 1));
    }

    private void QueueVisiblePlaceholderRowsForRendering()
    {
        CancelQueuedRendering();
        if (PlaceholderOnly || _firstMaterialized < 0) return;

        var viewport = Math.Max(1, _scrollViewer.ViewportHeight > 0 ? _scrollViewer.ViewportHeight : _list.ActualHeight);
        var offset = Math.Clamp(_scrollViewer.VerticalOffset, 0, Math.Max(0, _layout.TotalHeight - viewport));
        var visibleStart = _layout.FindIndexAtOffset(offset);
        var visibleEnd = _layout.FindIndexAtOffset(Math.Min(
            Math.Max(0, _layout.TotalHeight - 0.001), offset + viewport));
        var rows = _items.OfType<ChatTimelineMessageRow>()
            .Where(row => !row.RenderContent)
            .Select(row =>
            {
                var distance = row.Index < visibleStart
                    ? visibleStart - row.Index
                    : row.Index > visibleEnd
                        ? row.Index - visibleEnd
                        : 0;
                return (row, distance);
            })
            .OrderBy(item => item.distance)
            .ThenBy(item => item.row.Index);
        foreach (var (row, distance) in rows)
            _renderQueue.Enqueue(row, distance);
        ScheduleRenderBatch(_renderGeneration);
    }

    private void CancelQueuedRendering()
    {
        _renderGeneration++;
        _renderQueue.Clear();
    }

    private void ScheduleRenderBatch(int generation)
    {
        if (_renderQueue.Count == 0 || _isDisposed || _list.Dispatcher.HasShutdownStarted) return;
        _ = _list.Dispatcher.BeginInvoke(() => RenderBatch(generation), DispatcherPriority.Render);
    }

    private void RenderBatch(int generation)
    {
        if (_isDisposed || generation != _renderGeneration) return;
        var rendered = 0;
        while (rendered < 2 && _renderQueue.Count > 0)
        {
            if (!_renderQueue.TryDequeue(out var row, out _)) break;
            if (!_items.Contains(row) || row.RenderContent ||
                row.Index < _firstMaterialized || row.Index > _lastMaterialized)
                continue;
            row.RenderContent = true;
            rendered++;
        }
        ScheduleRenderBatch(generation);
    }

    private void QueueSafeAnchorCompensation(int changedIndex, int anchor, double delta)
    {
        if (_isThumbDragging || IsAtBottom()) return;
        if (changedIndex >= anchor) return;

        _pendingAnchorDelta += delta;
        if (_anchorCompensationQueued || _list.Dispatcher.HasShutdownStarted) return;
        _anchorCompensationQueued = true;
        _ = _list.Dispatcher.BeginInvoke(() =>
        {
            _anchorCompensationQueued = false;
            var deltaToApply = _pendingAnchorDelta;
            _pendingAnchorDelta = 0;
            if (_isDisposed || Math.Abs(deltaToApply) < HeightEpsilon || _isThumbDragging || IsAtBottom())
                return;
            SetVerticalOffsetSafely(_scrollViewer.VerticalOffset + deltaToApply);
        }, DispatcherPriority.Render);
    }

    private void SetVerticalOffsetSafely(double offset)
    {
        var maximum = Math.Max(0, _layout.TotalHeight - _scrollViewer.ViewportHeight);
        _scrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, maximum));
    }

    private bool IsAtBottom() => _scrollViewer.ExtentHeight <= _scrollViewer.ViewportHeight ||
                                 _scrollViewer.VerticalOffset >= _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight - 8;

    private double GetContentWidth() => _scrollViewer.ViewportWidth > 0
        ? _scrollViewer.ViewportWidth
        : Math.Max(1, _list.ActualWidth - _list.Padding.Left - _list.Padding.Right);

    private void QueuePersistedMeasurement(ChatTimelineMessageRow row)
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || row.Entry.IsTransientStatus || row.Entry.IsPendingAgent ||
            row.Entry.IsStreamingText || row.Entry.IsThinking || row.Index < 0)
            return;

        var contentWidth = GetContentWidth();
        _layoutCacheWriter.Enqueue(_sessionId, ManagerLayoutCacheStore.GetWidthBucket(contentWidth),
            new ManagerMessageLayoutCacheEntry
            {
                MessageIndex = row.Index,
                RowHeight = row.ReservedHeight,
                BubbleHeight = row.ReservedBubbleHeight,
                BubbleWidth = row.ReservedBubbleWidth,
            });
    }

    private void ImportPersistentHeights(IReadOnlyList<ManagerChatEntry> entries, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var width = GetContentWidth();
        var widthBucket = ChatMessageLayoutCache.GetWidthBucket(width);
        if (!ManagerLayoutCacheStore.TryLoad(
                sessionId,
                widthBucket,
                out var persisted))
            return;

        _layoutCache.ImportMeasuredHeights(entries, width, persisted);
        foreach (var pair in persisted)
        {
            if (pair.Key < 0 || pair.Key >= entries.Count) continue;
            var bubbleHeight = pair.Value.BubbleHeight;
            if (!double.IsFinite(bubbleHeight) || bubbleHeight < 24) continue;
            _persistentBubbleHeights[entries[pair.Key]] = Math.Clamp(bubbleHeight, 24, 12000);

            var bubbleWidth = pair.Value.BubbleWidth;
            if (double.IsFinite(bubbleWidth) && bubbleWidth >= 24)
                _persistentBubbleWidths[entries[pair.Key]] = Math.Clamp(bubbleWidth, 24, 12000);
        }
        _persistentBubbleWidthBucket = widthBucket;
    }

    private double GetBubbleHeight(ManagerChatEntry entry, int index)
    {
        var widthBucket = ChatMessageLayoutCache.GetWidthBucket(GetContentWidth());
        if (_persistentBubbleWidthBucket == widthBucket && _persistentBubbleHeights.TryGetValue(entry, out var cached))
            return cached;

        var rowHeight = _layout.GetHeight(index);
        var nonBubbleHeight = entry.ShowDateSeparator ? 78 : 30;
        return Math.Clamp(rowHeight - nonBubbleHeight, 28, Math.Max(28, rowHeight));
    }

    private double GetContentHeight(ManagerChatEntry entry, int index)
    {
        var rowHeight = _layout.GetHeight(index);
        var nonContentHeight = entry.ShowDateSeparator ? 78 : 30;
        var contentHeight = Math.Clamp(rowHeight - nonContentHeight, 28, Math.Max(28, rowHeight));

        // The air row renders the name/time header outside the presenter. The
        // persisted row height also includes that header, so reserve its
        // stable footprint here before attaching Markdown and images.
        if (!entry.IsTransientStatus) contentHeight = Math.Max(28, contentHeight - 28);
        return contentHeight;
    }

    private double GetBubbleWidth(ManagerChatEntry entry)
    {
        var widthBucket = ChatMessageLayoutCache.GetWidthBucket(GetContentWidth());
        if (_persistentBubbleWidthBucket == widthBucket && _persistentBubbleWidths.TryGetValue(entry, out var cached))
            return cached;

        var maximum = Math.Max(150, GetContentWidth() * 0.6666667);
        return Math.Min(280, maximum);
    }

    private void UnsubscribeEntries()
    {
        if (_entries is null) return;
        _entries.CollectionChanged -= EntriesOnCollectionChanged;
        foreach (var entry in _entries) entry.PropertyChanged -= EntryOnPropertyChanged;
        _entries = null;
    }
}
