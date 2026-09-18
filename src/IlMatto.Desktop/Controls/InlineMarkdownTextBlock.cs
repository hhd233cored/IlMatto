using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using WpfRichTextBox = System.Windows.Controls.RichTextBox;
using WpfSize = System.Windows.Size;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// A compact inline Markdown renderer with native text selection. The class
/// keeps its existing name for compatibility with the adaptive presenter, but
/// uses a borderless read-only RichTextBox so formatted replies can be dragged
/// and copied just like plain replies.
/// </summary>
public sealed class InlineMarkdownTextBlock : WpfRichTextBox
{
    private static readonly MediaFontFamily EmojiFontFamily = new("Segoe UI Emoji");
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(InlineMarkdownTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));

    public InlineMarkdownTextBlock()
    {
        IsReadOnly = true;
        IsTabStop = false;
        Focusable = true;
        AcceptsReturn = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ScrollViewer.SetCanContentScroll(this, false);
        BorderThickness = new Thickness(0);
        BorderBrush = System.Windows.Media.Brushes.Transparent;
        Background = System.Windows.Media.Brushes.Transparent;
        Padding = new Thickness(0);
        FocusVisualStyle = null;
        Cursor = System.Windows.Input.Cursors.IBeam;
        IsInactiveSelectionHighlightEnabled = true;
        SelectionBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(105, 55, 125, 190));
        SelectionTextBrush = System.Windows.Media.Brushes.Black;

        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            MaxPageWidth = double.PositiveInfinity,
            IsColumnWidthFlexible = true,
        };
        ContextMenu = CreateContextMenu();
    }

    private string? _naturalWidthMarkdown;
    private MediaFontFamily? _naturalWidthFontFamily;
    private double _naturalWidthFontSize = double.NaN;
    private FontWeight _naturalWidthFontWeight;
    private System.Windows.FontStyle _naturalWidthFontStyle;
    private System.Windows.FontStretch _naturalWidthFontStretch;
    private double _naturalWidthPixelsPerDip = double.NaN;
    private double _naturalWidth;

    /// <summary>
    /// RichTextBox/FlowDocument normally reports the width of its layout slot
    /// even for a short line. Emoji messages take this renderer path, so that
    /// default would make only messages containing emoji stretch to the full
    /// chat column. Measure the longest rendered line first and let the parent
    /// bubble apply the responsive maximum when the line is genuinely long.
    /// </summary>
    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var naturalWidth = MeasureNaturalWidth(Markdown);
        var width = naturalWidth;
        if (!double.IsInfinity(availableSize.Width))
            width = Math.Min(width, Math.Max(0, availableSize.Width));

        // The returned width is intentional rather than base.DesiredSize.Width:
        // FlowDocument can still report the whole available column after it is
        // measured, which is precisely the stretching behavior this override
        // prevents. Its measured height remains authoritative for wrapping.
        var measured = base.MeasureOverride(new WpfSize(width, availableSize.Height));
        return new WpfSize(width, measured.Height);
    }

    private double MeasureNaturalWidth(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized)) return 0;

        var fontSize = FontSize > 0 ? FontSize : 14;
        var pixelsPerDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        if (string.Equals(_naturalWidthMarkdown, normalized, StringComparison.Ordinal) &&
            Equals(_naturalWidthFontFamily, FontFamily) &&
            _naturalWidthFontSize.Equals(fontSize) &&
            _naturalWidthFontWeight == FontWeight &&
            _naturalWidthFontStyle == FontStyle &&
            _naturalWidthFontStretch == FontStretch &&
            _naturalWidthPixelsPerDip.Equals(pixelsPerDip))
            return _naturalWidth;

        var proseTypeface = new Typeface(
            FontFamily ?? new MediaFontFamily("Segoe UI"),
            FontStyle,
            FontWeight,
            FontStretch);
        var brush = Foreground ?? System.Windows.Media.Brushes.Black;
        var maximum = 0d;
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0) continue;
            var lineWidth = 0d;
            foreach (var segment in SplitEmojiSegments(line))
            {
                var typeface = segment.IsEmoji
                    ? new Typeface(EmojiFontFamily, FontStyle, FontWeight, FontStretch)
                    : proseTypeface;
                var formatted = new FormattedText(
                    segment.Text,
                    CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    brush,
                    pixelsPerDip);
                lineWidth += formatted.WidthIncludingTrailingWhitespace;
                if (segment.IsEmoji)
                {
                    // Emoji.Wpf substitutes color glyphs after FlowDocument
                    // layout. Segoe UI Emoji's FormattedText width is a little
                    // narrower for supplementary-plane pairs such as 😄🥰,
                    // which can otherwise wrap the second glyph onto a new
                    // line despite the measured width appearing to fit.
                    var emojiScalars = segment.Text.EnumerateRunes()
                        .Count(rune => EmojiTextSupport.ContainsEmoji(rune.ToString()));
                    lineWidth += emojiScalars * Math.Max(4d, fontSize * 0.7d);
                }
            }
            maximum = Math.Max(maximum, lineWidth);
        }

        _naturalWidthMarkdown = normalized;
        _naturalWidthFontFamily = FontFamily;
        _naturalWidthFontSize = fontSize;
        _naturalWidthFontWeight = FontWeight;
        _naturalWidthFontStyle = FontStyle;
        _naturalWidthFontStretch = FontStretch;
        _naturalWidthPixelsPerDip = pixelsPerDip;
        _naturalWidth = Math.Ceiling(maximum);
        return _naturalWidth;
    }

    private static IReadOnlyList<EmojiMeasureSegment> SplitEmojiSegments(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<EmojiMeasureSegment>();

        var runes = text.EnumerateRunes().ToArray();
        var segments = new List<EmojiMeasureSegment>();
        var buffer = new StringBuilder();
        var hasSegment = false;
        var currentIsEmoji = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            segments.Add(new EmojiMeasureSegment(buffer.ToString(), currentIsEmoji));
            buffer.Clear();
        }

        foreach (var rune in runes)
        {
            var isEmoji = EmojiTextSupport.ContainsEmoji(rune.ToString());
            if (!hasSegment || currentIsEmoji != isEmoji)
            {
                Flush();
                currentIsEmoji = isEmoji;
                hasSegment = true;
            }
            buffer.Append(rune.ToString());
        }

        Flush();
        return segments;
    }

    private readonly record struct EmojiMeasureSegment(string Text, bool IsEmoji);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Document is null) return;

        if (e.Property == System.Windows.Controls.Control.FontFamilyProperty ||
            e.Property == System.Windows.Controls.Control.FontSizeProperty ||
            e.Property == System.Windows.Controls.Control.FontWeightProperty ||
            e.Property == System.Windows.Controls.Control.ForegroundProperty)
            SyncDocumentProperties();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((InlineMarkdownTextBlock)dependencyObject).Render(e.NewValue as string ?? string.Empty);

    private void Render(string markdown)
    {
        // Agent responses commonly carry one transport newline at the end.
        // Treat terminal newlines as Markdown's trailing whitespace rather
        // than creating a visible empty line. Internal newlines remain intact.
        markdown = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        SyncDocumentProperties();
        Document.Blocks.Clear();

        var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = TextAlignment.Left };
        foreach (var part in InlineMarkdownParser.GetOrCreatePlan(markdown))
        {
            switch (part.Kind)
            {
                case InlineMarkdownKind.Text:
                    AddTextWithEmoji(paragraph.Inlines, part.Text);
                    break;
                case InlineMarkdownKind.Code:
                    AddTextWithEmoji(paragraph.Inlines, part.Text, run =>
                    {
                        run.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono");
                        run.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249));
                        run.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 64, 175));
                    });
                    break;
                case InlineMarkdownKind.Strike:
                    AddTextWithEmoji(paragraph.Inlines, part.Text, run => run.TextDecorations = System.Windows.TextDecorations.Strikethrough);
                    break;
                case InlineMarkdownKind.Strong:
                    AddTextWithEmoji(paragraph.Inlines, part.Text, run => run.FontWeight = FontWeights.Bold);
                    break;
                case InlineMarkdownKind.Emphasis:
                    AddTextWithEmoji(paragraph.Inlines, part.Text, run => run.FontStyle = FontStyles.Italic);
                    break;
                case InlineMarkdownKind.Link:
                {
                    var hyperlink = new Hyperlink
                    {
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
                        TextDecorations = System.Windows.TextDecorations.Underline,
                    };
                    AddTextWithEmoji(hyperlink.Inlines, part.Text);
                    paragraph.Inlines.Add(hyperlink);
                    break;
                }
            }
        }
        Document.Blocks.Add(paragraph);
    }

    private void SyncDocumentProperties()
    {
        Document.PagePadding = new Thickness(0);
        Document.ColumnWidth = double.PositiveInfinity;
        Document.MaxPageWidth = double.PositiveInfinity;
        Document.IsColumnWidthFlexible = true;
        Document.FontFamily = FontFamily;
        Document.FontSize = FontSize;
        Document.FontWeight = FontWeight;
        Document.Foreground = Foreground;
        Document.TextAlignment = System.Windows.TextAlignment.Left;
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Command = ApplicationCommands.Copy;
        copy.CommandTarget = this;
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = "全选" };
        selectAll.Command = ApplicationCommands.SelectAll;
        selectAll.CommandTarget = this;
        menu.Items.Add(selectAll);
        return menu;
    }

    private static void AddTextWithEmoji(InlineCollection inlines, string value, Action<Run>? configure = null)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var run = new Run(lines[index]);
            configure?.Invoke(run);
            inlines.Add(run);
            if (EmojiTextSupport.ContainsEmoji(lines[index]))
            {
                try { Emoji.Wpf.FlowDocumentExtensions.SubstituteGlyphs(run); }
                catch { /* Keep the text glyph fallback if color substitution is unavailable. */ }
            }
            if (index < lines.Length - 1) inlines.Add(new LineBreak());
        }
    }

}

internal enum InlineMarkdownKind { Text, Code, Strike, Strong, Emphasis, Link }

internal sealed record InlineMarkdownPart(InlineMarkdownKind Kind, string Text);

/// <summary>Immutable cached plan shared by the lightweight inline renderer.</summary>
internal static class InlineMarkdownParser
{
    private const int MaximumCacheEntries = 256;
    private const int MaximumCachedCharacters = 320_000;
    private const int MaximumCacheableCharacters = 24_000;
    private static readonly Regex InlineToken = new(
        @"(?<code>`[^`\r\n]+`)|(?<strike>~~[^~\r\n]+~~)|(?<strong>\*\*[^*\r\n]+\*\*|__[^_\r\n]+__)|(?<em>\*[^*\r\n]+\*|_[^_\r\n]+_)|(?<link>\[[^\]]+\]\([^\)]+\))",
        RegexOptions.Compiled);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> Lru = new();
    private static int _cachedCharacters;

    public static IReadOnlyList<InlineMarkdownPart> GetOrCreatePlan(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return Array.Empty<InlineMarkdownPart>();
        if (markdown.Length > MaximumCacheableCharacters) return Parse(markdown);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(markdown, out var cached))
            {
                Lru.Remove(cached);
                Lru.AddFirst(cached);
                return cached.Value.Parts;
            }
        }

        var parsed = Parse(markdown);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(markdown, out var existing))
            {
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return existing.Value.Parts;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(markdown, parsed));
            Cache.Add(markdown, node);
            Lru.AddFirst(node);
            _cachedCharacters += markdown.Length;
            while (Cache.Count > MaximumCacheEntries || _cachedCharacters > MaximumCachedCharacters)
            {
                var oldest = Lru.Last;
                if (oldest is null) break;
                Lru.RemoveLast();
                Cache.Remove(oldest.Value.Markdown);
                _cachedCharacters -= oldest.Value.Markdown.Length;
            }
            return parsed;
        }
    }

    private static IReadOnlyList<InlineMarkdownPart> Parse(string markdown)
    {
        var parts = new List<InlineMarkdownPart>();
        var position = 0;
        void AddText(string value)
        {
            if (!string.IsNullOrEmpty(value))
                parts.Add(new InlineMarkdownPart(InlineMarkdownKind.Text, Regex.Replace(value, @"[ \t]{2,}", " ")));
        }

        foreach (Match match in InlineToken.Matches(markdown))
        {
            if (match.Index > position) AddText(markdown[position..match.Index]);
            if (match.Groups["code"].Success)
                parts.Add(new InlineMarkdownPart(InlineMarkdownKind.Code, match.Value[1..^1]));
            else if (match.Groups["strike"].Success)
                parts.Add(new InlineMarkdownPart(InlineMarkdownKind.Strike, match.Value[2..^2]));
            else if (match.Groups["strong"].Success)
                parts.Add(new InlineMarkdownPart(InlineMarkdownKind.Strong, match.Value[2..^2]));
            else if (match.Groups["em"].Success)
                parts.Add(new InlineMarkdownPart(InlineMarkdownKind.Emphasis, match.Value[1..^1]));
            else
            {
                var link = Regex.Match(match.Value, @"^\[([^\]]+)\]\([^\)]+\)$");
                parts.Add(new InlineMarkdownPart(InlineMarkdownKind.Link, link.Success ? link.Groups[1].Value : match.Value));
            }
            position = match.Index + match.Length;
        }
        if (position < markdown.Length) AddText(markdown[position..]);
        return parts;
    }

    private sealed record CacheEntry(string Markdown, IReadOnlyList<InlineMarkdownPart> Parts);
}
