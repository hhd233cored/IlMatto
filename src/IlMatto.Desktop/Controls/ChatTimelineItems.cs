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

    /// <summary>
    /// Placeholder rows must occupy exactly the cached logical height. Live
    /// rows return NaN so WPF uses the natural height of the newly rendered
    /// message instead of keeping the old shell height as a hard clip.
    /// </summary>
    public double LayoutHeight => RenderContent ? double.NaN : ReservedHeight;

    partial void OnReservedHeightChanged(double value) => OnPropertyChanged(nameof(LayoutHeight));
    partial void OnRenderContentChanged(bool value) => OnPropertyChanged(nameof(LayoutHeight));
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
        // Placeholder rows are measured from ReservedHeight below. Once a row
        // is live, its natural content must be allowed to report a larger
        // height instead of being clipped by the previous reservation.
        ClipToBounds = false;
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
        if (!RenderContent || Message is null)
        {
            // Do not measure the placeholder template's visual tree. Its
            // dimensions are already known by the timeline cache.
            return new WpfSize(
                NormalizeWidth(ReservedBubbleWidth),
                NormalizeHeight(ReservedHeight));
        }

        // Live rows intentionally use natural layout. The parent timeline
        // keeps a MinHeight reservation, so a stale cache cannot clip a new
        // message; the measured result is then fed back to the logical index.
        var availableWidth = ResolveNaturalWidth(constraint);
        var desired = base.MeasureOverride(new WpfSize(availableWidth, double.PositiveInfinity));
        var naturalWidth = NormalizeWidth(desired.Width);
        var naturalHeight = Math.Max(24, desired.Height);
        // The height cache is keyed by the width used for wrapping, not by
        // the natural width of a short bubble. A short message may naturally
        // be only 120px wide while it was correctly measured inside a 680px
        // layout slot.
        var layoutWidth = double.IsFinite(availableWidth) ? availableWidth : naturalWidth;
        QueueMeasurement(naturalHeight, layoutWidth);
        return new WpfSize(naturalWidth, naturalHeight);
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
            if (version != _measurementVersion || PresentationSource.FromVisual(this) is null ||
                !RenderContent || !ReferenceEquals(entry, Message)) return;
            NaturalHeightMeasured?.Invoke(this, new ChatMessageMeasuredEventArgs(entry, height, width));
        }, DispatcherPriority.Render);
    }

    private double ResolveNaturalWidth(WpfSize constraint)
    {
        var width = double.IsFinite(constraint.Width) && constraint.Width > 0
            ? constraint.Width
            : double.PositiveInfinity;
        if (double.IsFinite(MaxWidth) && MaxWidth > 0)
            width = Math.Min(width, MaxWidth);
        return width;
    }

    private static double NormalizeWidth(double value) =>
        double.IsFinite(value) && value > 0 ? value : 1;

    private static double NormalizeHeight(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 24, 12000) : 48;
}
