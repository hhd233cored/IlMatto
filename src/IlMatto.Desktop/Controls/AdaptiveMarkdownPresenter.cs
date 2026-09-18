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
        var isStreaming = DeferWhileStreaming;
        var kind = isStreaming
            ? MarkdownPresentationKind.Plain
            : MarkdownClassifier.Classify(markdown);

        if (kind is MarkdownPresentationKind.Plain)
        {
            // Emoji.Wpf is intentionally reserved for actual emoji. Its color
            // glyph substitution is valuable for 🙂. Streaming keeps using the
            // TextBlock path; completed messages use the selectable inline
            // renderer so color emoji remain selectable as well.
            if (EmojiTextSupport.ContainsEmoji(markdown))
            {
                if (!isStreaming)
                {
                    if (Content is InlineMarkdownTextBlock completedEmojiText)
                    {
                        completedEmojiText.Markdown = markdown;
                        return;
                    }

                    Content = new InlineMarkdownTextBlock
                    {
                        Markdown = markdown,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    };
                    return;
                }

                if (Content is EmojiTextBlock emojiTextBlock)
                {
                    emojiTextBlock.Text = markdown;
                    return;
                }

                var createdEmojiTextBlock = new EmojiTextBlock
                {
                    Text = markdown,
                    ColorBlend = true,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                createdEmojiTextBlock.ContextMenu = MessageCopyContextMenu.Create(() => createdEmojiTextBlock.Text ?? string.Empty);
                Content = createdEmojiTextBlock;
                return;
            }

            // Keep the active streaming bubble on the cheapest visual. Once a
            // turn finishes, replace it with the read-only TextBox so the
            // completed prose can be selected without a FlowDocument.
            if (isStreaming)
            {
                if (Content is TextBlock textBlock && Content is not EmojiTextBlock)
                {
                    textBlock.Text = markdown;
                    return;
                }

                Content = new TextBlock
                {
                    Text = markdown,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                return;
            }

            if (Content is SelectableMessageTextBox selectableTextBox)
            {
                selectableTextBox.Text = markdown;
                return;
            }

            Content = new SelectableMessageTextBox
            {
                Text = markdown,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            return;
        }

        if (kind is MarkdownPresentationKind.Inline)
        {
            if (Content is InlineMarkdownTextBlock existing)
            {
                existing.Markdown = markdown;
                return;
            }

            Content = new InlineMarkdownTextBlock
            {
                Markdown = markdown,
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
internal enum MarkdownPresentationKind
{
    Plain,
    Inline,
    Complex,
}

internal static class MarkdownClassifier
{
    private static readonly System.Text.RegularExpressions.Regex HeadingPrefix = new(@"^#{1,6}\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BulletPrefix = new(@"^[-*+]\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex OrderedListPrefix = new(@"^\d{1,9}[.)]\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex QuotePrefix = new(@"^>\s?", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static MarkdownPresentationKind Classify(string? value)
    {
        if (string.IsNullOrEmpty(value)) return MarkdownPresentationKind.Plain;

        // These signals can change a paragraph into a block or formula. They
        // must keep the complete FlowDocument path for visual compatibility.
        if (value.IndexOfAny(['|', '$', '\\', '<', '>']) >= 0 || value.Contains("![", StringComparison.Ordinal))
            return MarkdownPresentationKind.Complex;

        var hasInlineSyntax = value.IndexOfAny(['`', '*', '_', '~', '[', ']']) >= 0;

        foreach (var sourceLine in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = sourceLine.TrimStart();
            if (line.StartsWith("```", StringComparison.Ordinal) || HeadingPrefix.IsMatch(line) ||
                BulletPrefix.IsMatch(line) || OrderedListPrefix.IsMatch(line) || QuotePrefix.IsMatch(line))
                return MarkdownPresentationKind.Complex;
        }

        return hasInlineSyntax ? MarkdownPresentationKind.Inline : MarkdownPresentationKind.Plain;
    }
}
