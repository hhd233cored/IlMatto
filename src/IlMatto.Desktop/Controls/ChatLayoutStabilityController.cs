using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IlMatto.Desktop.Models;
using WpfListBox = System.Windows.Controls.ListBox;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// A deliberately small, Virtuoso-inspired stabilizer for the native WPF
/// virtualized chat list. It never owns scrolling or item realization: WPF's
/// <see cref="VirtualizingStackPanel"/> continues to do that. Instead, it
/// remembers final row heights per in-memory message and width bucket, uses
/// them as a temporary hint while the scrollbar thumb is dragged, and only
/// compensates an already-idle viewport for a resize above it.
/// </summary>
internal sealed class ChatLayoutStabilityController : IDisposable
{
    private const double HeightChangeEpsilon = 0.5;
    private static readonly TimeSpan ScrollSettlingDelay = TimeSpan.FromMilliseconds(140);

    private readonly WpfListBox _list;
    private readonly ScrollViewer _scrollViewer;
    private readonly ConditionalWeakTable<ManagerChatEntry, HeightBuckets> _heightCache = new();
    private readonly Dictionary<ListBoxItem, ContainerState> _containerStates = new();
    private readonly Dictionary<ManagerChatEntry, PendingHeight> _pendingHeights = new();

    private bool _isThumbDragging;
    private bool _refreshQueued;
    private bool _anchorCompensationQueued;
    private bool _applyingAnchorCompensation;
    private bool _isDisposed;
    private double _pendingAnchorDelta;
    private DateTime _lastScrollActivityUtc = DateTime.MinValue;

    public ChatLayoutStabilityController(WpfListBox list, ScrollViewer scrollViewer)
    {
        _list = list;
        _scrollViewer = scrollViewer;
    }

    public void Attach()
    {
        _list.ItemContainerGenerator.StatusChanged += ItemContainerGeneratorOnStatusChanged;
        _list.PreviewMouseWheel += ListOnPreviewMouseWheel;
        _list.PreviewKeyDown += ListOnPreviewKeyDown;
        _scrollViewer.ScrollChanged += ScrollViewerOnScrollChanged;
        QueueRefresh();
    }

    public void BeginThumbDrag()
    {
        if (_isThumbDragging) return;
        _isThumbDragging = true;
        _lastScrollActivityUtc = DateTime.UtcNow;
        RefreshRealizedContainers();
    }

    public void CompleteThumbDrag()
    {
        if (!_isThumbDragging) return;
        _isThumbDragging = false;
        _lastScrollActivityUtc = DateTime.UtcNow;

        foreach (var (entry, pending) in _pendingHeights)
            _heightCache.GetOrCreateValue(entry).Set(pending.WidthBucket, pending.Height);
        _pendingHeights.Clear();

        // A cached MinHeight is only a drag-time reservation. Remove it in one
        // pass after the thumb is released so expanders and dynamic content can
        // resume their natural WPF layout immediately.
        foreach (var (container, state) in _containerStates)
        {
            if (!state.HeightHintApplied) continue;
            container.ClearValue(FrameworkElement.MinHeightProperty);
            state.HeightHintApplied = false;
        }
        _list.InvalidateMeasure();
        QueueRefresh();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _list.ItemContainerGenerator.StatusChanged -= ItemContainerGeneratorOnStatusChanged;
        _list.PreviewMouseWheel -= ListOnPreviewMouseWheel;
        _list.PreviewKeyDown -= ListOnPreviewKeyDown;
        _scrollViewer.ScrollChanged -= ScrollViewerOnScrollChanged;
        foreach (var (container, _) in _containerStates)
        {
            container.SizeChanged -= ContainerOnSizeChanged;
            container.DataContextChanged -= ContainerOnDataContextChanged;
            container.ClearValue(FrameworkElement.MinHeightProperty);
        }
        _containerStates.Clear();
        _pendingHeights.Clear();
    }

    private void ItemContainerGeneratorOnStatusChanged(object? sender, EventArgs e) => QueueRefresh();

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
            if (_isDisposed) return;
            _refreshQueued = false;
            RefreshRealizedContainers();
        }, DispatcherPriority.Render);
    }

    private void RefreshRealizedContainers()
    {
        foreach (var entry in _list.Items.OfType<ManagerChatEntry>())
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(entry) is not ListBoxItem container) continue;
            PrepareContainer(container, entry);
        }
    }

    private void PrepareContainer(ListBoxItem container, ManagerChatEntry entry)
    {
        if (!_containerStates.TryGetValue(container, out var state))
        {
            state = new ContainerState();
            _containerStates.Add(container, state);
            container.SizeChanged += ContainerOnSizeChanged;
            container.DataContextChanged += ContainerOnDataContextChanged;
        }

        if (!ReferenceEquals(state.Entry, entry))
        {
            state.Entry = entry;
            state.LastHeight = double.NaN;
            state.WidthBucket = GetWidthBucket();
            state.HeightHintApplied = false;
            container.ClearValue(FrameworkElement.MinHeightProperty);
        }

        ApplyDragHeightHint(container, state);
    }

    private void ContainerOnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBoxItem container || !_containerStates.TryGetValue(container, out var state)) return;
        state.Entry = e.NewValue as ManagerChatEntry;
        state.LastHeight = double.NaN;
        state.WidthBucket = GetWidthBucket();
        state.HeightHintApplied = false;
        container.ClearValue(FrameworkElement.MinHeightProperty);
        ApplyDragHeightHint(container, state);
    }

    private void ApplyDragHeightHint(ListBoxItem container, ContainerState state)
    {
        if (!_isThumbDragging || state.Entry is null) return;
        var bucket = GetWidthBucket();
        state.WidthBucket = bucket;
        if (!_heightCache.TryGetValue(state.Entry, out var buckets) ||
            !buckets.TryGet(bucket, out var height))
            return;

        // The hint reserves a known-good height while WPF recycles the
        // container. It never suppresses larger real content, so Markdown and
        // images still render immediately during a drag.
        container.MinHeight = height;
        state.HeightHintApplied = true;
    }

    private void ContainerOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListBoxItem container || !_containerStates.TryGetValue(container, out var state)) return;
        var entry = container.DataContext as ManagerChatEntry;
        if (entry is null) return;
        if (!ReferenceEquals(state.Entry, entry)) PrepareContainer(container, entry);

        var measuredHeight = e.NewSize.Height;
        if (double.IsNaN(measuredHeight) || double.IsInfinity(measuredHeight) || measuredHeight <= 0) return;
        var previousHeight = state.LastHeight;
        state.LastHeight = measuredHeight;
        state.WidthBucket = GetWidthBucket();

        // Do not teach the cache a temporary text-only streaming row or an
        // in-progress thinking panel. The terminal layout will record the
        // complete height once it is stable.
        if (!entry.IsStreamingText && !entry.IsThinking)
        {
            if (_isThumbDragging)
                _pendingHeights[entry] = new PendingHeight(state.WidthBucket, measuredHeight);
            else
                _heightCache.GetOrCreateValue(entry).Set(state.WidthBucket, measuredHeight);
        }

        if (double.IsNaN(previousHeight) || Math.Abs(measuredHeight - previousHeight) < HeightChangeEpsilon) return;
        QueueSafeAnchorCompensation(container, previousHeight, measuredHeight);
    }

    private void QueueSafeAnchorCompensation(ListBoxItem container, double previousHeight, double measuredHeight)
    {
        // Never fight a thumb drag or an active wheel/key scroll. The latter is
        // what causes the repeated thumb corrections seen with variable-height
        // WPF rows. At the bottom, the existing follow-tail code owns position.
        if (_isThumbDragging || IsAtBottom() || DateTime.UtcNow - _lastScrollActivityUtc < ScrollSettlingDelay) return;

        try
        {
            var topInViewport = container.TranslatePoint(new System.Windows.Point(), _scrollViewer).Y;
            if (topInViewport + previousHeight > 0) return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _pendingAnchorDelta += measuredHeight - previousHeight;
        if (_anchorCompensationQueued || _list.Dispatcher.HasShutdownStarted) return;
        _anchorCompensationQueued = true;
        _ = _list.Dispatcher.BeginInvoke(() =>
        {
            _anchorCompensationQueued = false;
            var delta = _pendingAnchorDelta;
            _pendingAnchorDelta = 0;
            if (Math.Abs(delta) < HeightChangeEpsilon || _isThumbDragging || IsAtBottom() ||
                DateTime.UtcNow - _lastScrollActivityUtc < ScrollSettlingDelay)
                return;

            _applyingAnchorCompensation = true;
            try
            {
                var maximum = Math.Max(0, _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight);
                _scrollViewer.ScrollToVerticalOffset(Math.Clamp(_scrollViewer.VerticalOffset + delta, 0, maximum));
            }
            finally
            {
                _applyingAnchorCompensation = false;
            }
        }, DispatcherPriority.Render);
    }

    private bool IsAtBottom() => _scrollViewer.ExtentHeight <= _scrollViewer.ViewportHeight ||
                                 _scrollViewer.VerticalOffset >= _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight - 8;

    private int GetWidthBucket()
    {
        var availableWidth = Math.Max(64, _list.ActualWidth - _list.Padding.Left - _list.Padding.Right);
        return Math.Max(64, (int)Math.Round(availableWidth / 32d, MidpointRounding.AwayFromZero) * 32);
    }

    private sealed class HeightBuckets
    {
        private readonly Dictionary<int, double> _heights = new();

        public void Set(int widthBucket, double height) => _heights[widthBucket] = height;

        public bool TryGet(int widthBucket, out double height) => _heights.TryGetValue(widthBucket, out height);
    }

    private sealed class ContainerState
    {
        public ManagerChatEntry? Entry { get; set; }
        public double LastHeight { get; set; } = double.NaN;
        public int WidthBucket { get; set; }
        public bool HeightHintApplied { get; set; }
    }

    private readonly record struct PendingHeight(int WidthBucket, double Height);
}
