using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IlMatto.Desktop.Models;
using WpfSize = System.Windows.Size;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Message-level virtualizer with an estimated/actual prefix-height index.
/// Unlike WPF's variable-height VirtualizingStackPanel, its extent is derived
/// from every persisted message before the corresponding Markdown controls are
/// realized. That keeps the scroll thumb stable during a long jump.
/// </summary>
public sealed class AnchoredVirtualizingStackPanel : VirtualizingPanel, IScrollInfo
{
    private ChatTimelineLayoutIndex? _layout;
    private IReadOnlyList<ManagerChatEntry> _entries = Array.Empty<ManagerChatEntry>();
    private ScrollViewer? _scrollOwner;
    private WpfSize _viewport;
    private WpfSize _extent;
    private Vector _offset;
    private int _firstRealized = -1;
    private int _lastRealized = -1;
    private bool _isMeasuring;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get => _scrollOwner; set => _scrollOwner = value; }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        if (_isMeasuring) return availableSize;
        _isMeasuring = true;
        try
        {
            var owner = ItemsControl.GetItemsOwner(this) as ChatTimelineListBox;
            var entries = owner?.Items.Cast<ManagerChatEntry>().ToArray() ?? Array.Empty<ManagerChatEntry>();
            var width = ResolveWidth(availableSize, owner);
            EnsureLayout(owner, entries, width);

            _viewport = new WpfSize(width, double.IsInfinity(availableSize.Height) ? 0 : Math.Max(0, availableSize.Height));
            _extent = new WpfSize(width, _layout?.TotalHeight ?? 0);
            SetVerticalOffsetCore(_offset.Y, invalidate: false);
            _scrollOwner?.InvalidateScrollInfo();

            if (_layout is null || _layout.Count == 0 || _viewport.Height <= 0)
            {
                ClearRealized();
                return availableSize;
            }

            var overscan = owner?.IsFastScrolling == true ? 0 : Math.Max(_viewport.Height, 280);
            var start = _layout.FindIndexAtOffset(Math.Max(0, _offset.Y - overscan));
            var end = _layout.FindIndexAtOffset(Math.Min(_layout.TotalHeight - 0.001, _offset.Y + _viewport.Height + overscan));
            RealizeRange(start, end);

            var anchorIndex = _layout.FindIndexAtOffset(_offset.Y);
            var pendingAnchorDelta = 0d;
            for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
            {
                var messageIndex = _firstRealized + childIndex;
                if (messageIndex < 0 || messageIndex >= _layout.Count) continue;
                var child = InternalChildren[childIndex];
                child.Measure(new WpfSize(width, double.PositiveInfinity));
                if (owner?.IsFastScrolling == true || HasPendingMarkdown(child)) continue;
                var delta = _layout.UpdateMeasuredHeight(messageIndex, width, child.DesiredSize.Height);
                if (messageIndex < anchorIndex) pendingAnchorDelta += delta;
            }

            if (Math.Abs(pendingAnchorDelta) >= 0.5)
                SetVerticalOffsetCore(_offset.Y + pendingAnchorDelta, invalidate: false);

            _extent = new WpfSize(width, _layout.TotalHeight);
            _scrollOwner?.InvalidateScrollInfo();
            return availableSize;
        }
        finally
        {
            _isMeasuring = false;
        }
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        if (_layout is null) return finalSize;
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var messageIndex = _firstRealized + childIndex;
            if (messageIndex < 0 || messageIndex >= _layout.Count) continue;
            var height = _layout.GetHeight(messageIndex);
            var top = _layout.GetTop(messageIndex) - _offset.Y;
            InternalChildren[childIndex].Arrange(new Rect(0, top, finalSize.Width, height));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        ClearRealized();
        InvalidateMeasure();
        base.OnItemsChanged(sender, args);
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight * 0.9);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight * 0.9);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 96);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 96);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset) => SetVerticalOffsetCore(offset, invalidate: true);

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element || _layout is null) return rectangle;
        var childIndex = InternalChildren.IndexOf(element);
        if (childIndex < 0) return rectangle;
        var messageIndex = _firstRealized + childIndex;
        SetVerticalOffset(_layout.GetTop(messageIndex));
        return new Rect(rectangle.X, 0, rectangle.Width, rectangle.Height);
    }

    private void EnsureLayout(ChatTimelineListBox? owner, IReadOnlyList<ManagerChatEntry> entries, double width)
    {
        if (owner is null)
        {
            _entries = Array.Empty<ManagerChatEntry>();
            _layout = null;
            return;
        }

        _layout ??= new ChatTimelineLayoutIndex(owner.LayoutCache);
        var oldAnchor = _layout.Count > 0 ? _layout.FindIndexAtOffset(_offset.Y) : -1;
        var oldAnchorEntry = oldAnchor >= 0 ? _layout[oldAnchor] : null;
        var anchorRelativeOffset = oldAnchor >= 0 ? _offset.Y - _layout.GetTop(oldAnchor) : 0;
        var changed = _layout.Synchronize(entries, width);
        _entries = entries;
        if (!changed || oldAnchorEntry is null) return;

        var newAnchor = FindEntryIndex(entries, oldAnchorEntry);
        if (newAnchor >= 0)
            SetVerticalOffsetCore(_layout.GetTop(newAnchor) + anchorRelativeOffset, invalidate: false);
    }

    private static double ResolveWidth(WpfSize availableSize, ChatTimelineListBox? owner)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0) return availableSize.Width;
        return Math.Max(1, owner?.ActualWidth ?? 1);
    }

    private static int FindEntryIndex(IReadOnlyList<ManagerChatEntry> entries, ManagerChatEntry entry)
    {
        for (var index = 0; index < entries.Count; index++)
            if (ReferenceEquals(entries[index], entry)) return index;
        return -1;
    }

    private void RealizeRange(int start, int end)
    {
        if (start < 0 || end < start) { ClearRealized(); return; }
        if (_firstRealized == -1)
        {
            RealizeInitialRange(start, end);
            return;
        }

        while (_firstRealized < start) RemoveFirst();
        while (_lastRealized > end) RemoveLast();
        while (_firstRealized > start) Prepend(_firstRealized - 1);
        while (_lastRealized < end) Append(_lastRealized + 1);
    }

    private void RealizeInitialRange(int start, int end)
    {
        var generator = ItemContainerGenerator;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(start), GeneratorDirection.Forward, true))
        {
            for (var index = start; index <= end; index++)
            {
                var child = generator.GenerateNext(out var newlyRealized) as UIElement;
                if (child is null) break;
                if (newlyRealized) AddInternalChild(child);
                generator.PrepareItemContainer(child);
            }
        }
        _firstRealized = start;
        _lastRealized = start + InternalChildren.Count - 1;
    }

    private void Append(int itemIndex)
    {
        var generator = ItemContainerGenerator;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(itemIndex), GeneratorDirection.Forward, true))
        {
            var child = generator.GenerateNext(out var newlyRealized) as UIElement;
            if (child is null) return;
            if (newlyRealized) AddInternalChild(child);
            generator.PrepareItemContainer(child);
        }
        if (_firstRealized < 0) _firstRealized = itemIndex;
        _lastRealized = Math.Max(_lastRealized, itemIndex);
    }

    private void Prepend(int itemIndex)
    {
        var generator = ItemContainerGenerator;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(itemIndex), GeneratorDirection.Forward, true))
        {
            var child = generator.GenerateNext(out var newlyRealized) as UIElement;
            if (child is null) return;
            if (newlyRealized) InsertInternalChild(0, child);
            generator.PrepareItemContainer(child);
        }
        _firstRealized = itemIndex;
        if (_lastRealized < itemIndex) _lastRealized = itemIndex;
    }

    private void RemoveFirst()
    {
        if (InternalChildren.Count == 0) { _firstRealized = _lastRealized = -1; return; }
        ItemContainerGenerator.Remove(new GeneratorPosition(0, 0), 1);
        RemoveInternalChildRange(0, 1);
        _firstRealized++;
        if (_firstRealized > _lastRealized) _firstRealized = _lastRealized = -1;
    }

    private void RemoveLast()
    {
        if (InternalChildren.Count == 0) { _firstRealized = _lastRealized = -1; return; }
        var childIndex = InternalChildren.Count - 1;
        ItemContainerGenerator.Remove(new GeneratorPosition(childIndex, 0), 1);
        RemoveInternalChildRange(childIndex, 1);
        _lastRealized--;
        if (_lastRealized < _firstRealized) _firstRealized = _lastRealized = -1;
    }

    private void ClearRealized()
    {
        if (InternalChildren.Count > 0)
        {
            ItemContainerGenerator.Remove(new GeneratorPosition(0, 0), InternalChildren.Count);
            RemoveInternalChildRange(0, InternalChildren.Count);
        }
        _firstRealized = _lastRealized = -1;
    }

    private void SetVerticalOffsetCore(double offset, bool invalidate)
    {
        var maximum = Math.Max(0, _extent.Height - _viewport.Height);
        var clamped = Math.Clamp(double.IsFinite(offset) ? offset : 0, 0, maximum);
        if (Math.Abs(_offset.Y - clamped) < 0.1) return;
        _offset = new Vector(0, clamped);
        if (invalidate) InvalidateMeasure();
        _scrollOwner?.InvalidateScrollInfo();
    }

    private static bool HasPendingMarkdown(DependencyObject element)
    {
        if (element is MarkdownViewer { IsRenderPending: true }) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (HasPendingMarkdown(VisualTreeHelper.GetChild(element, index))) return true;
        return false;
    }
}
