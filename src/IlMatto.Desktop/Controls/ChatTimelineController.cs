using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private static readonly TimeSpan ScrollSettlingDelay = TimeSpan.FromMilliseconds(140);

    private readonly WpfListBox _list;
    private readonly ScrollViewer _scrollViewer;
    private readonly ObservableCollection<object> _items;
    private readonly ChatMessageLayoutCache _layoutCache = new();
    private readonly ChatTimelineLayoutIndex _layout;
    private readonly ChatTimelineSpacer _topSpacer = new();
    private readonly ChatTimelineSpacer _bottomSpacer = new();
    private readonly Dictionary<ManagerChatEntry, ChatTimelineMessageRow> _rows = new();
    private readonly Queue<ChatTimelineMessageRow> _renderQueue = new();

    private ObservableCollection<ManagerChatEntry>? _entries;
    private int _firstMaterialized = -1;
    private int _lastMaterialized = -1;
    private int _lastWidthBucket = -1;
    private int _renderGeneration;
    private bool _isThumbDragging;
    private bool _isDisposed;
    private bool _refreshQueued;
    private bool _anchorCompensationQueued;
    private bool _applyingAnchorCompensation;
    private double _pendingAnchorDelta;
    private DateTime _lastScrollActivityUtc = DateTime.MinValue;

    public ChatTimelineController(WpfListBox list, ScrollViewer scrollViewer, ObservableCollection<object> items)
    {
        _list = list;
        _scrollViewer = scrollViewer;
        _items = items;
        _layout = new ChatTimelineLayoutIndex(_layoutCache);
    }

    public void Attach()
    {
        _list.SizeChanged += ListOnSizeChanged;
        _list.PreviewMouseWheel += ListOnPreviewMouseWheel;
        _list.PreviewKeyDown += ListOnPreviewKeyDown;
        _scrollViewer.ScrollChanged += ScrollViewerOnScrollChanged;
    }

    public void SetEntries(ObservableCollection<ManagerChatEntry> entries)
    {
        if (ReferenceEquals(_entries, entries))
        {
            SynchronizeLayout(preserveAnchor: true);
            RefreshVisibleItems();
            return;
        }

        UnsubscribeEntries();
        _entries = entries;
        _entries.CollectionChanged += EntriesOnCollectionChanged;
        foreach (var entry in entries) entry.PropertyChanged += EntryOnPropertyChanged;

        _rows.Clear();
        _firstMaterialized = _lastMaterialized = -1;
        _items.Clear();
        SynchronizeLayout(preserveAnchor: false);
        // A newly selected short historical session must never inherit the
        // previous session's large pixel offset. The window schedules its
        // normal ScrollToEnd afterwards, once this fresh extent exists.
        SetVerticalOffsetSafely(0);
        RefreshVisibleItems();
    }

    public void BeginThumbDrag()
    {
        if (_isThumbDragging) return;
        _isThumbDragging = true;
        _lastScrollActivityUtc = DateTime.UtcNow;
        CancelQueuedRendering();
    }

    public void CompleteThumbDrag()
    {
        if (!_isThumbDragging) return;
        _isThumbDragging = false;
        _lastScrollActivityUtc = DateTime.UtcNow;
        RefreshVisibleItems();
        QueueVisiblePlaceholderRowsForRendering();
    }

    public void RecordNaturalHeight(ChatTimelineMessageRow row, ChatMessageMeasuredEventArgs measurement)
    {
        if (_isDisposed || _entries is null || !ReferenceEquals(row.Entry, measurement.Entry)) return;
        if (row.Index < 0 || row.Index >= _layout.Count || !ReferenceEquals(_layout[row.Index], row.Entry)) return;

        var delta = _layout.UpdateMeasuredHeight(row.Index, measurement.Width, measurement.Height);
        if (Math.Abs(delta) < HeightEpsilon) return;

        row.ReservedHeight = _layout.GetHeight(row.Index);
        UpdateSpacerHeights();
        QueueSafeAnchorCompensation(row.Index, delta);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _list.SizeChanged -= ListOnSizeChanged;
        _list.PreviewMouseWheel -= ListOnPreviewMouseWheel;
        _list.PreviewKeyDown -= ListOnPreviewKeyDown;
        _scrollViewer.ScrollChanged -= ScrollViewerOnScrollChanged;
        UnsubscribeEntries();
        CancelQueuedRendering();
        _items.Clear();
    }

    private void EntriesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var entry in e.OldItems.OfType<ManagerChatEntry>()) entry.PropertyChanged -= EntryOnPropertyChanged;
        if (e.NewItems is not null)
            foreach (var entry in e.NewItems.OfType<ManagerChatEntry>()) entry.PropertyChanged += EntryOnPropertyChanged;

        if (e.OldItems is not null)
            foreach (var entry in e.OldItems.OfType<ManagerChatEntry>()) _rows.Remove(entry);

        SynchronizeLayout(preserveAnchor: true);
        QueueRefresh();
    }

    private void EntryOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ManagerChatEntry entry || _entries is null) return;
        if (e.PropertyName is not (nameof(ManagerChatEntry.Text) or nameof(ManagerChatEntry.IsStreamingText) or
            nameof(ManagerChatEntry.IsThinking) or nameof(ManagerChatEntry.ShowDateSeparator) or
            nameof(ManagerChatEntry.CodeResult) or nameof(ManagerChatEntry.Runtime)))
            return;

        _layout.Invalidate(entry, GetContentWidth());
        if (_rows.TryGetValue(entry, out var row))
        {
            row.ReservedHeight = _layout.GetHeight(row.Index);
            // A change to an existing message must be measured again. This is
            // also how the plain streamed text is promoted to final Markdown.
            row.RenderContent = true;
        }
        UpdateSpacerHeights();
        QueueRefresh();
    }

    private void ListOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var bucket = ChatMessageLayoutCache.GetWidthBucket(GetContentWidth());
        if (bucket != _lastWidthBucket)
        {
            SynchronizeLayout(preserveAnchor: true);
            _lastWidthBucket = bucket;
        }
        QueueRefresh();
    }

    private void ListOnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) =>
        _lastScrollActivityUtc = DateTime.UtcNow;

    private void ListOnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down or
            System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown or
            System.Windows.Input.Key.Home or System.Windows.Input.Key.End)
            _lastScrollActivityUtc = DateTime.UtcNow;
    }

    private void ScrollViewerOnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) > 0 && !_applyingAnchorCompensation)
            _lastScrollActivityUtc = DateTime.UtcNow;
        QueueRefresh();
    }

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
        var oldAnchor = preserveAnchor && _layout.Count > 0 ? _layout.FindIndexAtOffset(_scrollViewer.VerticalOffset) : -1;
        var oldAnchorEntry = oldAnchor >= 0 ? _layout[oldAnchor] : null;
        var relativeOffset = oldAnchor >= 0 ? _scrollViewer.VerticalOffset - _layout.GetTop(oldAnchor) : 0;
        var changed = _layout.Synchronize(_entries, GetContentWidth());
        _lastWidthBucket = ChatMessageLayoutCache.GetWidthBucket(GetContentWidth());
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
    }

    private void ReconcileMaterializedRange(int start, int end)
    {
        if (_layout.Count == 0) return;
        start = Math.Clamp(start, 0, _layout.Count - 1);
        end = Math.Clamp(end, start, _layout.Count - 1);

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
            return row;
        }

        var measured = _layout.IsMeasured(index, GetContentWidth());
        // Existing content remains alive during a drag. Only newly entered,
        // already-measured rows use a blank fixed-height shell until release.
        row = new ChatTimelineMessageRow(entry, index, _layout.GetHeight(index), renderContent: !_isThumbDragging || !measured);
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
        if (_firstMaterialized < 0) return;
        var anchor = _layout.FindIndexAtOffset(_scrollViewer.VerticalOffset);
        var rows = _items.OfType<ChatTimelineMessageRow>()
            .Where(row => !row.RenderContent)
            .OrderBy(row => Math.Abs(row.Index - anchor));
        foreach (var row in rows) _renderQueue.Enqueue(row);
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
        if (_isDisposed || generation != _renderGeneration || _isThumbDragging) return;
        var rendered = 0;
        while (rendered < 2 && _renderQueue.Count > 0)
        {
            var row = _renderQueue.Dequeue();
            if (!_items.Contains(row) || row.RenderContent) continue;
            row.RenderContent = true;
            rendered++;
        }
        ScheduleRenderBatch(generation);
    }

    private void QueueSafeAnchorCompensation(int changedIndex, double delta)
    {
        if (_isThumbDragging || IsAtBottom() || DateTime.UtcNow - _lastScrollActivityUtc < ScrollSettlingDelay) return;
        var anchor = _layout.FindIndexAtOffset(_scrollViewer.VerticalOffset);
        if (changedIndex >= anchor) return;

        _pendingAnchorDelta += delta;
        if (_anchorCompensationQueued || _list.Dispatcher.HasShutdownStarted) return;
        _anchorCompensationQueued = true;
        _ = _list.Dispatcher.BeginInvoke(() =>
        {
            _anchorCompensationQueued = false;
            var deltaToApply = _pendingAnchorDelta;
            _pendingAnchorDelta = 0;
            if (Math.Abs(deltaToApply) < HeightEpsilon || _isThumbDragging || IsAtBottom() ||
                DateTime.UtcNow - _lastScrollActivityUtc < ScrollSettlingDelay)
                return;
            SetVerticalOffsetSafely(_scrollViewer.VerticalOffset + deltaToApply);
        }, DispatcherPriority.Render);
    }

    private void SetVerticalOffsetSafely(double offset)
    {
        _applyingAnchorCompensation = true;
        try
        {
            var maximum = Math.Max(0, _layout.TotalHeight - _scrollViewer.ViewportHeight);
            _scrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, maximum));
        }
        finally
        {
            _applyingAnchorCompensation = false;
        }
    }

    private bool IsAtBottom() => _scrollViewer.ExtentHeight <= _scrollViewer.ViewportHeight ||
                                 _scrollViewer.VerticalOffset >= _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight - 8;

    private double GetContentWidth() => Math.Max(1, _list.ActualWidth - _list.Padding.Left - _list.Padding.Right);

    private void UnsubscribeEntries()
    {
        if (_entries is null) return;
        _entries.CollectionChanged -= EntriesOnCollectionChanged;
        foreach (var entry in _entries) entry.PropertyChanged -= EntryOnPropertyChanged;
        _entries = null;
    }
}
