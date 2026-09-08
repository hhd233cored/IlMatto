using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IlMatto.Desktop.Models;
using WpfSize = System.Windows.Size;

namespace IlMatto.Desktop.Controls;

/// <summary>Top and bottom logical space around the currently materialized messages.</summary>
internal sealed partial class ChatTimelineSpacer : ObservableObject
{
    [ObservableProperty] private double height;
}

/// <summary>
/// A lightweight item that reserves the message's known logical height before
/// its expensive Markdown content is attached to the WPF visual tree.
/// </summary>
internal sealed partial class ChatTimelineMessageRow : ObservableObject
{
    public ChatTimelineMessageRow(ManagerChatEntry entry, int index, double reservedHeight, double reservedContentHeight, double reservedBubbleHeight, double reservedBubbleWidth, bool renderContent)
    {
        Entry = entry;
        Index = index;
        this.reservedHeight = reservedHeight;
        this.reservedContentHeight = reservedContentHeight;
        this.reservedBubbleHeight = reservedBubbleHeight;
        this.reservedBubbleWidth = reservedBubbleWidth;
        this.renderContent = renderContent;
    }

    public ManagerChatEntry Entry { get; }
    public int Index { get; set; }
    [ObservableProperty] private double reservedHeight;
    [ObservableProperty] private double reservedContentHeight;
    [ObservableProperty] private double reservedBubbleHeight;
    [ObservableProperty] private double reservedBubbleWidth;
    [ObservableProperty] private bool renderContent;
}

/// <summary>Chooses between a fixed-height logical spacer and a chat message row.</summary>
public sealed class ChatTimelineItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SpacerTemplate { get; set; }
    public DataTemplate? MessageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ChatTimelineSpacer => SpacerTemplate,
        ChatTimelineMessageRow => MessageTemplate,
        _ => base.SelectTemplate(item, container)
    };
}

public sealed class ChatMessageMeasuredEventArgs : EventArgs
{
    public ChatMessageMeasuredEventArgs(ManagerChatEntry entry, double height, double width)
    {
        Entry = entry;
        Height = height;
        Width = width;
    }

    public ManagerChatEntry Entry { get; }
    public double Height { get; }
    public double Width { get; }
}

/// <summary>
/// Reserves the timeline's logical row height independently from the expensive
/// message template. With content disabled it creates no Markdown tree at all;
/// with content enabled it reports the template's natural outer height so the
/// index can replace a prior estimate.
/// </summary>
public sealed class ReservedMessagePresenter : ContentControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ManagerChatEntry), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty ReservedHeightProperty = DependencyProperty.Register(
        nameof(ReservedHeight), typeof(double), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(48d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RenderContentProperty = DependencyProperty.Register(
        nameof(RenderContent), typeof(bool), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(true, OnPresentationPropertyChanged));

    public static readonly DependencyProperty FullContentTemplateProperty = DependencyProperty.Register(
        nameof(FullContentTemplate), typeof(DataTemplate), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty PlaceholderTemplateProperty = DependencyProperty.Register(
        nameof(PlaceholderTemplate), typeof(DataTemplate), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty ReservedBubbleHeightProperty = DependencyProperty.Register(
        nameof(ReservedBubbleHeight), typeof(double), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(48d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ReservedBubbleWidthProperty = DependencyProperty.Register(
        nameof(ReservedBubbleWidth), typeof(double), typeof(ReservedMessagePresenter),
        new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private int _measurementVersion;

    public ReservedMessagePresenter()
    {
        // The logical timeline owns a row's outer height. A newly encountered
        // Markdown document may briefly be taller than its estimate, but must
        // not paint into the following reserved row before the measured height
        // has been committed to the layout index.
        ClipToBounds = true;
    }

    public ManagerChatEntry? Message
    {
        get => (ManagerChatEntry?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public double ReservedHeight
    {
        get => (double)GetValue(ReservedHeightProperty);
        set => SetValue(ReservedHeightProperty, value);
    }

    public bool RenderContent
    {
        get => (bool)GetValue(RenderContentProperty);
        set => SetValue(RenderContentProperty, value);
    }

    public DataTemplate? FullContentTemplate
    {
        get => (DataTemplate?)GetValue(FullContentTemplateProperty);
        set => SetValue(FullContentTemplateProperty, value);
    }

    public DataTemplate? PlaceholderTemplate
    {
        get => (DataTemplate?)GetValue(PlaceholderTemplateProperty);
        set => SetValue(PlaceholderTemplateProperty, value);
    }

    public double ReservedBubbleHeight
    {
        get => (double)GetValue(ReservedBubbleHeightProperty);
        set => SetValue(ReservedBubbleHeightProperty, value);
    }

    public double ReservedBubbleWidth
    {
        get => (double)GetValue(ReservedBubbleWidthProperty);
        set => SetValue(ReservedBubbleWidthProperty, value);
    }

    public event EventHandler<ChatMessageMeasuredEventArgs>? NaturalHeightMeasured;

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        var reserved = NormalizeHeight(ReservedHeight);
        if (!RenderContent || Message is null)
            return new WpfSize(ResolveWidth(constraint), reserved);

        // Measure the full template only to learn its next cached height. The
        // outer desired height must remain the layout index's reservation: WPF
        // derives ScrollViewer.ExtentHeight from this value while the
        // controller derives its logical offsets from the same value. Returning
        // the natural height here would create two competing scroll coordinate
        // systems and make the scrollbar thumb jump during a fast traversal.
        var desired = base.MeasureOverride(new WpfSize(ResolveWidth(constraint), double.PositiveInfinity));
        var naturalHeight = Math.Max(24, desired.Height);
        QueueMeasurement(naturalHeight, ResolveWidth(constraint));
        return new WpfSize(ResolveWidth(constraint), reserved);
    }

    private static void OnPresentationPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var presenter = (ReservedMessagePresenter)d;
        presenter.Content = presenter.Message;
        presenter.ContentTemplate = presenter.RenderContent
            ? presenter.FullContentTemplate
            : presenter.PlaceholderTemplate;
        presenter.InvalidateMeasure();
    }

    private void QueueMeasurement(double height, double width)
    {
        var entry = Message;
        if (entry is null || Dispatcher.HasShutdownStarted) return;
        var version = ++_measurementVersion;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (version != _measurementVersion || !RenderContent || !ReferenceEquals(entry, Message)) return;
            NaturalHeightMeasured?.Invoke(this, new ChatMessageMeasuredEventArgs(entry, height, width));
        }, DispatcherPriority.Render);
    }

    private static double ResolveWidth(WpfSize constraint) =>
        double.IsFinite(constraint.Width) && constraint.Width > 0 ? constraint.Width : 1;

    private static double NormalizeHeight(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 24, 12000) : 48;
}
