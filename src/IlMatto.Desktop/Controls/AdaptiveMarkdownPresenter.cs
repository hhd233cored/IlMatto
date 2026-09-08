using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using EmojiTextBlock = Emoji.Wpf.TextBlock;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Renders ordinary conversational prose without creating a FlowDocument.
/// Markdown remains the compatibility path whenever the source contains a
/// construct whose presentation would differ from plain text.
/// </summary>
public sealed class AdaptiveMarkdownPresenter : ContentControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(AdaptiveMarkdownPresenter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnPresentationChanged));

    public static readonly DependencyProperty DeferWhileStreamingProperty = DependencyProperty.Register(
        nameof(DeferWhileStreaming), typeof(bool), typeof(AdaptiveMarkdownPresenter),
        new FrameworkPropertyMetadata(false, OnPresentationChanged));

    public AdaptiveMarkdownPresenter()
    {
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Top;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>
    /// Streaming content stays on the lightweight path. Once streaming ends,
    /// the final text is classified and escalated to Markdown only when needed.
    /// </summary>
    public bool DeferWhileStreaming
    {
        get => (bool)GetValue(DeferWhileStreamingProperty);
        set => SetValue(DeferWhileStreamingProperty, value);
    }

    private static void OnPresentationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((AdaptiveMarkdownPresenter)dependencyObject).RefreshContent();

    private void RefreshContent()
    {
        var markdown = Markdown ?? string.Empty;
        var usePlainText = DeferWhileStreaming || MarkdownClassifier.IsPlainText(markdown);

        if (usePlainText)
        {
            if (Content is EmojiTextBlock existing)
            {
                existing.Text = markdown;
                return;
            }

            Content = new EmojiTextBlock
            {
                Text = markdown,
                ColorBlend = true,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            return;
        }

        if (Content is MarkdownViewer viewer)
        {
            viewer.Markdown = markdown;
            viewer.DeferWhileStreaming = false;
            return;
        }

        Content = new MarkdownViewer
        {
            Markdown = markdown,
            DeferWhileStreaming = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
    }
}

/// <summary>Conservative classifier: a false negative keeps the existing renderer.</summary>
internal static class MarkdownClassifier
{
    private static readonly Regex OrderedListPrefix = new(@"^\d{1,9}[.)]\s+", RegexOptions.Compiled);

    public static bool IsPlainText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;

        // These characters are all meaningful in the supported inline
        // Markdown subset. Treating ambiguous prose as Markdown is slower but
        // preserves output; treating Markdown as plain text would be wrong.
        if (value.IndexOfAny(['`', '*', '_', '~', '[', ']', '\\', '|', '$', '<', '>']) >= 0)
            return false;

        foreach (var sourceLine in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = sourceLine.TrimStart();
            if (line.StartsWith("# ", StringComparison.Ordinal) ||
                line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("### ", StringComparison.Ordinal) ||
                line.StartsWith("+ ", StringComparison.Ordinal) ||
                OrderedListPrefix.IsMatch(line))
                return false;
        }

        return true;
    }
}
