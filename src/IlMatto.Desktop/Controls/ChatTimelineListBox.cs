using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfListBox = System.Windows.Controls.ListBox;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// ListBox shell for the Manager history. The message records stay in the
/// existing observable collection; this control only owns process-local layout
/// state and exposes the fast-scroll mode to its virtualizing panel.
/// </summary>
public sealed class ChatTimelineListBox : WpfListBox
{
    public static readonly DependencyProperty IsFastScrollingProperty = DependencyProperty.Register(
        nameof(IsFastScrolling), typeof(bool), typeof(ChatTimelineListBox),
        new FrameworkPropertyMetadata(false, OnTimelineModeChanged));

    private readonly ChatMessageLayoutCache _layoutCache = new();

    public bool IsFastScrolling
    {
        get => (bool)GetValue(IsFastScrollingProperty);
        set => SetValue(IsFastScrollingProperty, value);
    }

    internal ChatMessageLayoutCache LayoutCache => _layoutCache;

    protected override DependencyObject GetContainerForItemOverride() => new ChatTimelineListBoxItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is ChatTimelineListBoxItem;

    private static void OnTimelineModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ChatTimelineListBox list) return;
        if (VisualTreeHelper.GetChildrenCount(list) == 0) return;
        list.InvalidateMeasure();
    }
}

public sealed class ChatTimelineListBoxItem : ListBoxItem
{
}
