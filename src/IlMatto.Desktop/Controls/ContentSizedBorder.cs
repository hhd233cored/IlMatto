using System.Windows;
using System.Windows.Controls;
using WpfSize = System.Windows.Size;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Measures its child at content width first, then remeasures it at the
/// available maximum when the content is too wide. This is useful for chat
/// bubbles because FlowDocumentScrollViewer otherwise tends to report the
/// whole grid column as its desired width.
/// </summary>
public sealed class ContentSizedBorder : Border
{
    /// <summary>
    /// Long chat replies almost always exceed the bubble cap. Measuring them
    /// once without a width constraint only lays out an unusable extra-long
    /// line, so callers can opt into one constrained measure instead.
    /// </summary>
    public static readonly DependencyProperty MeasureAtMaxWidthProperty = DependencyProperty.Register(
        nameof(MeasureAtMaxWidth), typeof(bool), typeof(ContentSizedBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool MeasureAtMaxWidth
    {
        get => (bool)GetValue(MeasureAtMaxWidthProperty);
        set => SetValue(MeasureAtMaxWidthProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        if (Child is null)
        {
            return base.MeasureOverride(constraint);
        }

        var horizontalChrome = BorderThickness.Left + BorderThickness.Right + Padding.Left + Padding.Right;
        var verticalChrome = BorderThickness.Top + BorderThickness.Bottom + Padding.Top + Padding.Bottom;
        var availableContentHeight = ToContentSize(constraint.Height, verticalChrome);
        var maximumContentWidth = ToContentSize(MaxWidth, horizontalChrome);

        if (!double.IsInfinity(constraint.Width))
        {
            maximumContentWidth = Math.Min(maximumContentWidth, ToContentSize(constraint.Width, horizontalChrome));
        }

        // The first pass is useful only for short replies, where it produces a
        // compact bubble. Avoid it for known long replies once the row has a
        // real width; the constrained pass below is the final layout anyway.
        if (MeasureAtMaxWidth && maximumContentWidth > 0 && !double.IsInfinity(maximumContentWidth))
        {
            Child.Measure(new WpfSize(maximumContentWidth, availableContentHeight));
            var constrainedWidth = Math.Min(Math.Max(0, Child.DesiredSize.Width), maximumContentWidth);
            return new WpfSize(
                constrainedWidth + horizontalChrome,
                Child.DesiredSize.Height + verticalChrome);
        }

        // An unconstrained first pass gives short messages their natural width.
        Child.Measure(new WpfSize(double.PositiveInfinity, availableContentHeight));
        var contentWidth = Child.DesiredSize.Width;
        if (double.IsNaN(contentWidth) || double.IsInfinity(contentWidth))
        {
            contentWidth = maximumContentWidth;
        }

        contentWidth = Math.Min(Math.Max(0, contentWidth), maximumContentWidth);
        Child.Measure(new WpfSize(contentWidth, availableContentHeight));

        return new WpfSize(
            contentWidth + horizontalChrome,
            Child.DesiredSize.Height + verticalChrome);
    }

    private static double ToContentSize(double value, double chrome)
    {
        if (double.IsInfinity(value)) return double.PositiveInfinity;
        if (double.IsNaN(value)) return 0;
        return Math.Max(0, value - chrome);
    }
}
