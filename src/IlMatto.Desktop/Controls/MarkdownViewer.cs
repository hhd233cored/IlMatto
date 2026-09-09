using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Emoji.Wpf;
using MediaBrush = System.Windows.Media.Brush;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfSize = System.Windows.Size;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Lightweight offline Markdown viewer for agent replies. It covers the common
/// conversational subset and renders inline/display LaTeX as Cambria Math text.
/// </summary>
public sealed class MarkdownViewer : FlowDocumentScrollViewer
{
    private static readonly MediaFontFamily ProseFontFamily = new("华文中宋");
    private static readonly MediaFontFamily EmojiFontFamily = new("Segoe UI Emoji");

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));
    public static readonly DependencyProperty DeferMarkdownRenderProperty = DependencyProperty.Register(
        nameof(DeferMarkdownRender), typeof(bool), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(false, OnDeferMarkdownRenderChanged));
    public static readonly DependencyProperty DeferWhileStreamingProperty = DependencyProperty.Register(
        nameof(DeferWhileStreaming), typeof(bool), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(false, OnDeferMarkdownRenderChanged));
    public static readonly DependencyProperty RenderPermitProperty = DependencyProperty.Register(
        nameof(RenderPermit), typeof(bool), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(true, OnDeferMarkdownRenderChanged));

    private static readonly Regex InlineToken = new(
        @"(?<!\\)(?<strong>\*\*(?:\\.|[^*\\\r\n])+\*\*|__(?:\\.|[^_\\\r\n])+__)|(?<math>\$\$.*?\$\$|\$[^$\r\n]+\$|\\\([^\r\n]*?\\\)|\\\[[^\r\n]*?\\\])|(?<code>`[^`\r\n]+`)|(?<strike>~~(?:\\.|[^~\\\r\n])+~~)|(?<em>\*(?:\\.|[^*\\\r\n])+\*|_(?:\\.|[^_\\\r\n])+_)|(?<link>\[[^\]]+\]\([^\)]+\))",
        RegexOptions.Compiled);
    private string _requestedMarkdown = string.Empty;
    private string? _renderedMarkdown;
    private bool _renderPending;
    private long _renderVersion;
    private string? _naturalWidthMarkdown;
    private double _naturalWidthFontSize = double.NaN;
    private double _naturalWidthPixelsPerDip = double.NaN;
    private double _naturalWidth;
    private double _deferredNaturalWidth;
    private static int _isWarmedUp;

    public MarkdownViewer()
    {
        IsToolBarVisible = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // Keep the viewer keyboard-focusable so WPF's built-in text selection
        // and Ctrl+C command work for rendered assistant messages.
        Focusable = true;
        IsTabStop = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        var contextMenu = new ContextMenu();
        var copyItem = new MenuItem { Header = "复制" };
        copyItem.Command = System.Windows.Input.ApplicationCommands.Copy;
        copyItem.CommandTarget = this;
        contextMenu.Items.Add(copyItem);
        var selectAllItem = new MenuItem { Header = "全选" };
        selectAllItem.Command = System.Windows.Input.ApplicationCommands.SelectAll;
        selectAllItem.CommandTarget = this;
        contextMenu.Items.Add(selectAllItem);
        ContextMenu = contextMenu;
        Document = CreateDocument();
    }

    /// <summary>
    /// Initializes the WPF FlowDocument and color-emoji paths during an idle
    /// frame. This moves one-time typeface and glyph setup away from the first
    /// history-session switch without retaining a UI object afterwards.
    /// </summary>
    public static void WarmUp()
    {
        if (Interlocked.Exchange(ref _isWarmedUp, 1) != 0) return;
        try
        {
            var viewer = new MarkdownViewer
            {
                Markdown = "预热 🙂",
                FontSize = 14,
                Width = 320,
            };
            viewer.Measure(new WpfSize(320, double.PositiveInfinity));
            viewer.Arrange(new Rect(0, 0, 320, viewer.DesiredSize.Height));
            viewer.UpdateLayout();
        }
        catch
        {
            // Warming up is an optional performance improvement. Rendering an
            // actual chat message remains the correctness fallback.
        }
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>
    /// When true, newly realized viewer instances keep an empty document until
    /// the chat scroll velocity returns to a normal level.
    /// </summary>
    public bool DeferMarkdownRender
    {
        get => (bool)GetValue(DeferMarkdownRenderProperty);
        set => SetValue(DeferMarkdownRenderProperty, value);
    }

    /// <summary>
    /// Keeps the hidden Markdown view dormant while its companion TextBlock is
    /// showing live streamed text. This is intentionally separate from scroll
    /// velocity so either reason can defer an expensive render.
    /// </summary>
    public bool DeferWhileStreaming
    {
        get => (bool)GetValue(DeferWhileStreamingProperty);
        set => SetValue(DeferWhileStreamingProperty, value);
    }

    /// <summary>
    /// The chat recovery scheduler uses this independent permit to restore at
    /// most a couple of Markdown documents per UI frame after a fast scroll.
    /// It is intentionally not persisted and defaults to normal rendering.
    /// </summary>
    public bool RenderPermit
    {
        get => (bool)GetValue(RenderPermitProperty);
        set => SetValue(RenderPermitProperty, value);
    }

    internal bool IsRenderPending => _renderPending;

    internal void SetRenderPermit(bool value) => SetCurrentValue(RenderPermitProperty, value);

    /// <summary>Build one deferred document under the caller's frame budget.</summary>
    internal bool ResumePendingRender()
    {
        SetCurrentValue(RenderPermitProperty, true);
        if (IsRenderDeferred || !_renderPending) return false;
        _renderVersion++;
        RenderMarkdownNow();
        return true;
    }

    private bool IsRenderDeferred => DeferMarkdownRender || DeferWhileStreaming || !RenderPermit;

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (MarkdownViewer)d;
        viewer.RequestMarkdownRender(e.NewValue as string ?? string.Empty);
    }

    private static void OnDeferMarkdownRenderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (MarkdownViewer)d;
        if (!viewer.IsRenderDeferred && viewer._renderPending) viewer.SchedulePendingRender();
    }

    private void RequestMarkdownRender(string markdown)
    {
        _requestedMarkdown = markdown;
        _naturalWidthMarkdown = null;
        _deferredNaturalWidth = EstimateNaturalWidth(markdown);
        _renderVersion++;
        if (IsRenderDeferred)
        {
            _renderPending = true;
            // Preserve a correct, already rendered document when a fast scroll
            // begins.  Replacing it with an empty FlowDocument is what used to
            // collapse the row and make the scroll thumb repeatedly jump. A
            // recycled viewer bound to a *different* message still clears its
            // old document, while the parent virtualizer reserves its cached
            // height until recovery permits the new render.
            if (!string.Equals(_renderedMarkdown, markdown, StringComparison.Ordinal))
            {
                Document = CreateDocument();
                _renderedMarkdown = null;
            }
            InvalidateMeasure();
            return;
        }
        RenderMarkdownNow();
    }

    private void SchedulePendingRender()
    {
        var version = ++_renderVersion;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (version != _renderVersion || IsRenderDeferred || !_renderPending) return;
            RenderMarkdownNow();
        }, DispatcherPriority.ContextIdle);
    }

    private void RenderMarkdownNow()
    {
        _renderPending = false;
        Document = MarkdownDocument.Build(_requestedMarkdown);
        _renderedMarkdown = _requestedMarkdown;
        InvalidateMeasure();
    }

    /// <summary>
    /// FlowDocumentScrollViewer can report the width of its layout slot as its
    /// desired width. Measure it from the text's natural line width first so a
    /// short chat message does not inherit the whole star column.
    /// </summary>
    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var naturalWidth = _renderPending ? _deferredNaturalWidth : MeasureNaturalWidth(Markdown);
        var width = naturalWidth;
        if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
        if (!double.IsInfinity(MaxWidth)) width = Math.Min(width, MaxWidth);
        width = Math.Max(0, width);

        var measured = base.MeasureOverride(new WpfSize(width, availableSize.Height));
        return new WpfSize(width, measured.Height);
    }

    private double MeasureNaturalWidth(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return 0;

        var proseTypeface = new Typeface(ProseFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var emojiTypeface = new Typeface(EmojiFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var brush = Foreground ?? System.Windows.Media.Brushes.Black;
        var pixelsPerDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        var fontSize = FontSize > 0 ? FontSize : 14;
        if (string.Equals(_naturalWidthMarkdown, markdown, StringComparison.Ordinal) &&
            _naturalWidthFontSize.Equals(fontSize) && _naturalWidthPixelsPerDip.Equals(pixelsPerDip))
            return _naturalWidth;
        var maximum = 0d;

        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var measureText = StripMarkdownForMeasure(line);
            if (measureText.Length == 0) continue;
            var lineWidth = 0d;
            foreach (var segment in SplitEmojiSegments(measureText))
            {
                var formatted = new FormattedText(
                    segment.Text,
                    CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    segment.IsEmoji ? emojiTypeface : proseTypeface,
                    fontSize,
                    brush,
                    pixelsPerDip);
                lineWidth += formatted.WidthIncludingTrailingWhitespace;
            }
            maximum = Math.Max(maximum, lineWidth);
        }

        _naturalWidthMarkdown = markdown;
        _naturalWidthFontSize = fontSize;
        _naturalWidthPixelsPerDip = pixelsPerDip;
        _naturalWidth = Math.Ceiling(maximum);
        return _naturalWidth;
    }

    private double EstimateNaturalWidth(string markdown)
    {
        var longestLine = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(line => line.Length).DefaultIfEmpty(0).Max();
        return Math.Clamp(longestLine * Math.Max(7, FontSize * 0.8), 96, 520);
    }

    private static string StripMarkdownForMeasure(string value)
    {
        var result = value.Trim();
        if (result.StartsWith("```", StringComparison.Ordinal)) return string.Empty;
        result = Regex.Replace(result, @"^#{1,6}\s+", string.Empty);
        result = Regex.Replace(result, @"^[-*+]\s+", string.Empty);
        result = Regex.Replace(result, @"^\d+[.)]\s+", string.Empty);
        result = Regex.Replace(result, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        result = Regex.Replace(result, @"[`*_~]", string.Empty);
        return result;
    }

    private readonly record struct TextSegment(string Text, bool IsEmoji);

    private static void AddTextWithEmoji(InlineCollection inlines, string text, Action<Run>? configure = null)
    {
        foreach (var segment in SplitEmojiSegments(text))
        {
            var run = new Run(segment.Text);
            configure?.Invoke(run);
            if (segment.IsEmoji) run.FontFamily = EmojiFontFamily;
            inlines.Add(run);
        }
    }

    private static IReadOnlyList<TextSegment> SplitEmojiSegments(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<TextSegment>();

        var runes = text.EnumerateRunes().ToArray();
        var segments = new List<TextSegment>();
        var buffer = new StringBuilder();
        var hasSegment = false;
        var currentIsEmoji = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            segments.Add(new TextSegment(buffer.ToString(), currentIsEmoji));
            buffer.Clear();
        }

        for (var index = 0; index < runes.Length;)
        {
            var sequenceLength = EmojiSequenceLength(runes, index);
            var isEmoji = sequenceLength > 0;
            sequenceLength = isEmoji ? sequenceLength : 1;

            if (!hasSegment || currentIsEmoji != isEmoji)
            {
                Flush();
                currentIsEmoji = isEmoji;
                hasSegment = true;
            }

            for (var offset = 0; offset < sequenceLength; offset++) buffer.Append(runes[index + offset].ToString());
            index += sequenceLength;
        }

        Flush();
        return segments;
    }

    private static int EmojiSequenceLength(IReadOnlyList<System.Text.Rune> runes, int index)
    {
        var value = runes[index].Value;
        if (IsKeycapBase(value))
        {
            var length = 1;
            if (index + length < runes.Count && IsVariationSelector(runes[index + length].Value)) length++;
            return index + length < runes.Count && runes[index + length].Value == 0x20E3 ? length + 1 : 0;
        }

        if (IsRegionalIndicator(value))
        {
            return index + 1 < runes.Count && IsRegionalIndicator(runes[index + 1].Value) ? 2 : 1;
        }

        if (!IsEmojiBase(value)) return 0;

        var sequenceLength = 1;
        ConsumeEmojiModifiers(runes, index, ref sequenceLength);
        while (index + sequenceLength < runes.Count && runes[index + sequenceLength].Value == 0x200D)
        {
            var joinedIndex = index + sequenceLength + 1;
            if (joinedIndex >= runes.Count || !IsEmojiBase(runes[joinedIndex].Value)) break;
            sequenceLength += 2;
            ConsumeEmojiModifiers(runes, index, ref sequenceLength);
        }

        return sequenceLength;
    }

    private static void ConsumeEmojiModifiers(IReadOnlyList<System.Text.Rune> runes, int index, ref int sequenceLength)
    {
        while (index + sequenceLength < runes.Count)
        {
            var value = runes[index + sequenceLength].Value;
            if (!IsVariationSelector(value) && !IsEmojiModifier(value)) break;
            sequenceLength++;
        }
    }

    private static bool IsEmojiBase(int value) =>
        value is >= 0x1F000 and <= 0x1FAFF
        || value is >= 0x2300 and <= 0x23FF
        || value is >= 0x2600 and <= 0x27BF
        || value is >= 0x2B00 and <= 0x2BFF
        || value is >= 0x3030 and <= 0x303D
        || value is >= 0x3297 and <= 0x3299
        || value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
        || value is >= 0x2194 and <= 0x2199
        || value is >= 0x21A9 and <= 0x21AA;

    private static bool IsEmojiModifier(int value) => value is >= 0x1F3FB and <= 0x1F3FF;

    private static bool IsRegionalIndicator(int value) => value is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsKeycapBase(int value) => value is >= '0' and <= '9' or '#' or '*';

    private static bool IsVariationSelector(int value) => value is 0xFE0E or 0xFE0F;

    private static FlowDocument CreateDocument() => new()
    {
        PagePadding = new Thickness(0),
        ColumnWidth = double.PositiveInfinity,
        MaxPageWidth = double.PositiveInfinity,
        IsColumnWidthFlexible = true
    };

    private static class MarkdownDocument
    {
        // FlowDocument instances are UI objects and must never be shared
        // between viewers. Cache the immutable block-level render plan instead
        // so recycled chat containers do not repeat line scanning, fenced-code
        // detection, table detection, or block classification.
        private const int MaximumCacheEntries = 96;
        private const int MaximumCachedCharacters = 1_200_000;
        private const int MaximumCacheableMessageCharacters = 96_000;
        private static readonly object CacheGate = new();
        private static readonly Dictionary<string, LinkedListNode<CacheEntry>> RenderPlanCache = new(StringComparer.Ordinal);
        private static readonly LinkedList<CacheEntry> RenderPlanLru = new();
        private static int _cachedCharacters;
        private const int MaximumInlineCacheEntries = 256;
        private const int MaximumInlineCachedCharacters = 320_000;
        private const int MaximumCacheableInlineCharacters = 24_000;
        private static readonly object InlineCacheGate = new();
        private static readonly Dictionary<string, LinkedListNode<InlineCacheEntry>> InlinePlanCache = new(StringComparer.Ordinal);
        private static readonly LinkedList<InlineCacheEntry> InlinePlanLru = new();
        private static int _inlineCachedCharacters;

        private abstract record MarkdownBlock;
        private sealed record ParagraphLinesBlock(string[] Lines) : MarkdownBlock;
        private sealed record CodeBlockModel(string Language, string Text) : MarkdownBlock;
        private sealed record HorizontalRuleBlock : MarkdownBlock;
        private sealed record TableBlockModel(string[] Rows) : MarkdownBlock;
        private sealed record HeadingBlock(string Text, int Level) : MarkdownBlock;
        private sealed record BulletBlock(string Text) : MarkdownBlock;
        private sealed record NumberedBlock(string Text, string Number) : MarkdownBlock;
        private sealed record QuoteBlock(string Text) : MarkdownBlock;
        private sealed record MarkdownRenderPlan(IReadOnlyList<MarkdownBlock> Blocks);
        private sealed record CacheEntry(string Markdown, MarkdownRenderPlan Plan);
        private enum InlineKind { Text, Math, Code, Strike, Strong, Emphasis, Link }
        private sealed record InlinePart(InlineKind Kind, string Text);
        private sealed record InlineCacheEntry(string Markdown, IReadOnlyList<InlinePart> Parts);

        public static FlowDocument Build(string markdown)
        {
            var plan = GetOrCreatePlan(markdown ?? string.Empty);
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity,
                MaxPageWidth = double.PositiveInfinity,
                IsColumnWidthFlexible = true,
                // Use 华文中宋 for the rendered conversation body. Inline code,
                // LaTeX and fenced code blocks still provide their own
                // monospace/math fonts below.
                FontFamily = ProseFontFamily,
                FontWeight = FontWeights.Normal,
                FontSize = 14,
                TextAlignment = TextAlignment.Left,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(23, 32, 51))
            };

            foreach (var block in plan.Blocks)
            {
                switch (block)
                {
                    case ParagraphLinesBlock paragraphLines:
                        document.Blocks.Add(ParagraphLines(paragraphLines.Lines, 0, 14, false));
                        break;
                    case CodeBlockModel code:
                        document.Blocks.Add(CodeBlock(code.Language, code.Text));
                        break;
                    case HorizontalRuleBlock:
                        document.Blocks.Add(HorizontalRule());
                        break;
                    case TableBlockModel table:
                        document.Blocks.Add(TableBlock(table.Rows));
                        break;
                    case HeadingBlock heading:
                    {
                        var paragraph = Paragraph(heading.Text, 0, heading.Level <= 2 ? 18 : 16, true);
                        paragraph.Margin = new Thickness(0, 8, 0, 4);
                        document.Blocks.Add(paragraph);
                        break;
                    }
                    case BulletBlock bullet:
                        document.Blocks.Add(BulletParagraph(bullet.Text, 18));
                        break;
                    case NumberedBlock numbered:
                        document.Blocks.Add(Paragraph(numbered.Text, 18, 14, false, $"{numbered.Number}. "));
                        break;
                    case QuoteBlock quote:
                    {
                        var paragraph = Paragraph(quote.Text, 12, 14, false);
                        paragraph.Foreground = new SolidColorBrush(MediaColor.FromRgb(83, 97, 116));
                        paragraph.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(147, 197, 253));
                        paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                        paragraph.Padding = new Thickness(8, 0, 0, 0);
                        document.Blocks.Add(paragraph);
                        break;
                    }
                }
            }

            // Paragraphs keep a small bottom margin so adjacent Markdown
            // blocks remain visually separated. The final block is different:
            // its bottom margin appears as an extra blank line at the bottom
            // of an agent bubble, unlike a normal TextBlock message.
            switch (document.Blocks.LastBlock)
            {
                case Paragraph lastParagraph:
                {
                    var margin = lastParagraph.Margin;
                    lastParagraph.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
                    break;
                }
                case Table lastTable:
                {
                    var margin = lastTable.Margin;
                    lastTable.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
                    break;
                }
            }

            // WPF's default text renderer often falls back to monochrome glyph
            // outlines for Segoe UI Emoji. Emoji.Wpf replaces supported emoji
            // runs with vector inlines built from the color glyph layers that
            // Windows ships with the font. The replacement scans the whole
            // document, so skip it for ordinary prose that has no real emoji.
            if (EmojiTextSupport.ContainsEmoji(markdown))
            {
                try
                {
                    document.SubstituteGlyphs();
                }
                catch (Exception)
                {
                    // Rendering the response must remain best-effort: the
                    // Segoe UI Emoji fallback is still preferable to losing text.
                }
            }
            return document;
        }

        private static MarkdownRenderPlan GetOrCreatePlan(string markdown)
        {
            if (markdown.Length > MaximumCacheableMessageCharacters) return Parse(markdown);

            lock (CacheGate)
            {
                if (RenderPlanCache.TryGetValue(markdown, out var cached))
                {
                    RenderPlanLru.Remove(cached);
                    RenderPlanLru.AddFirst(cached);
                    return cached.Value.Plan;
                }
            }

            var parsed = Parse(markdown);
            lock (CacheGate)
            {
                // Another viewer can finish the same parse while this one was
                // outside the lock. Reuse its plan rather than adding a second
                // entry with an identical string key.
                if (RenderPlanCache.TryGetValue(markdown, out var existing))
                {
                    RenderPlanLru.Remove(existing);
                    RenderPlanLru.AddFirst(existing);
                    return existing.Value.Plan;
                }

                var node = new LinkedListNode<CacheEntry>(new CacheEntry(markdown, parsed));
                RenderPlanLru.AddFirst(node);
                RenderPlanCache.Add(markdown, node);
                _cachedCharacters += markdown.Length;
                while (RenderPlanCache.Count > MaximumCacheEntries || _cachedCharacters > MaximumCachedCharacters)
                {
                    var oldest = RenderPlanLru.Last;
                    if (oldest is null) break;
                    RenderPlanLru.RemoveLast();
                    RenderPlanCache.Remove(oldest.Value.Markdown);
                    _cachedCharacters -= oldest.Value.Markdown.Length;
                }
                return parsed;
            }
        }

        private static MarkdownRenderPlan Parse(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var inCode = false;
            var codeLanguage = string.Empty;
            var codeLines = new List<string>();
            var paragraphLines = new List<string>();
            var blocks = new List<MarkdownBlock>();
            void FlushParagraph()
            {
                if (paragraphLines.Count == 0) return;
                blocks.Add(new ParagraphLinesBlock(paragraphLines.ToArray()));
                paragraphLines.Clear();
            }

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    if (inCode)
                    {
                        blocks.Add(new CodeBlockModel(codeLanguage, string.Join("\n", codeLines)));
                        codeLines.Clear();
                        codeLanguage = string.Empty;
                    }
                    else
                    {
                        var fenceInfo = line.TrimStart()[3..].Trim();
                        codeLanguage = NormalizeCodeLanguage(fenceInfo);
                    }
                    inCode = !inCode;
                    continue;
                }
                if (inCode) { codeLines.Add(line); continue; }
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph();
                    continue;
                }

                if (IsHorizontalRule(line))
                {
                    FlushParagraph();
                    blocks.Add(new HorizontalRuleBlock());
                    continue;
                }

                if (lineIndex + 1 < lines.Length && IsTableRow(line) && IsTableSeparator(lines[lineIndex + 1]))
                {
                    FlushParagraph();
                    var tableRows = new List<string> { line, lines[++lineIndex] };
                    while (lineIndex + 1 < lines.Length && IsTableRow(lines[lineIndex + 1]) && !IsTableSeparator(lines[lineIndex + 1]))
                        tableRows.Add(lines[++lineIndex]);
                    blocks.Add(new TableBlockModel(tableRows.ToArray()));
                    continue;
                }

                var trimmed = line.TrimStart();
                var heading = Regex.Match(trimmed, "^(#{1,6})\\s+(.*)$");
                if (heading.Success)
                {
                    FlushParagraph();
                    var level = heading.Groups[1].Value.Length;
                    blocks.Add(new HeadingBlock(heading.Groups[2].Value, level));
                    continue;
                }

                var bullet = Regex.Match(trimmed, "^(?:[-*+]\\s+)(.*)$");
                if (bullet.Success)
                {
                    FlushParagraph();
                    blocks.Add(new BulletBlock(bullet.Groups[1].Value));
                    continue;
                }

                var numbered = Regex.Match(trimmed, "^(\\d+)[.)]\\s+(.*)$");
                if (numbered.Success)
                {
                    FlushParagraph();
                    blocks.Add(new NumberedBlock(numbered.Groups[2].Value, numbered.Groups[1].Value));
                    continue;
                }

                var quote = Regex.Match(trimmed, "^>\\s?(.*)$");
                if (quote.Success)
                {
                    FlushParagraph();
                    blocks.Add(new QuoteBlock(quote.Groups[1].Value));
                    continue;
                }

                paragraphLines.Add(line);
            }
            FlushParagraph();
            if (inCode && codeLines.Count > 0) blocks.Add(new CodeBlockModel(codeLanguage, string.Join("\n", codeLines)));
            return new MarkdownRenderPlan(blocks);
        }

        private static bool IsTableRow(string line) => line.Contains('|') && SplitTableRow(line).Count >= 2;

        private static bool IsHorizontalRule(string line)
        {
            var value = line.Trim();
            if (value.Length < 3) return false;
            var marker = value[0];
            if (marker is not ('-' or '*' or '_')) return false;
            return value.All(character => character == marker || char.IsWhiteSpace(character));
        }

        private static bool IsTableSeparator(string line)
        {
            var cells = SplitTableRow(line);
            return cells.Count >= 2 && cells.All(cell => Regex.IsMatch(cell.Trim(), @"^:?-{3,}:?$"));
        }

        private static List<string> SplitTableRow(string line)
        {
            var value = line.Trim();
            if (value.StartsWith("|", StringComparison.Ordinal)) value = value[1..];
            if (value.EndsWith("|", StringComparison.Ordinal) && !value.EndsWith("\\|", StringComparison.Ordinal)) value = value[..^1];
            var cells = new List<string>();
            var current = new System.Text.StringBuilder();
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\\' && index + 1 < value.Length && value[index + 1] == '|')
                {
                    current.Append('|');
                    index++;
                }
                else if (character == '|')
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                }
                else current.Append(character);
            }
            cells.Add(current.ToString().Trim());
            return cells;
        }

        private static Table TableBlock(IReadOnlyList<string> rows)
        {
            var headers = SplitTableRow(rows[0]);
            var alignments = SplitTableRow(rows[1]).Select(GetTableAlignment).ToArray();
            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 6, 0, 10),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1)
            };
            for (var index = 0; index < headers.Count; index++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            group.Rows.Add(TableRow(headers, alignments, true));
            for (var rowIndex = 2; rowIndex < rows.Count; rowIndex++) group.Rows.Add(TableRow(SplitTableRow(rows[rowIndex]), alignments, false));
            return table;
        }

        private static TableRow TableRow(IReadOnlyList<string> values, IReadOnlyList<TextAlignment> alignments, bool header)
        {
            var row = new TableRow();
            var count = Math.Max(values.Count, alignments.Count);
            for (var index = 0; index < count; index++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(8, 5, 8, 5), TextAlignment = index < alignments.Count ? alignments[index] : TextAlignment.Left };
                AddInline(paragraph.Inlines, index < values.Count ? values[index] : string.Empty, header);
                var cell = new TableCell(paragraph)
                {
                    BorderBrush = new SolidColorBrush(MediaColor.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = header ? new SolidColorBrush(MediaColor.FromRgb(241, 245, 249)) : System.Windows.Media.Brushes.Transparent
                };
                row.Cells.Add(cell);
            }
            return row;
        }

        private static TextAlignment GetTableAlignment(string separator)
        {
            var value = separator.Trim();
            if (value.StartsWith(":", StringComparison.Ordinal) && value.EndsWith(":", StringComparison.Ordinal)) return TextAlignment.Center;
            if (value.EndsWith(":", StringComparison.Ordinal)) return TextAlignment.Right;
            return TextAlignment.Left;
        }

        private static BlockUIContainer HorizontalRule()
        {
            var rule = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 8, 0, 10),
                Background = FreezeBrush(MediaColor.FromRgb(203, 213, 225)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            return new BlockUIContainer(rule);
        }

        private static Paragraph BulletParagraph(string text, double left)
        {
            var task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
            if (!task.Success) return Paragraph(text, left, 14, false, "• ");
            var paragraph = new Paragraph { Margin = new Thickness(left, 0, 0, 4), TextAlignment = TextAlignment.Left };
            paragraph.Inlines.Add(new InlineUIContainer(new System.Windows.Controls.CheckBox { IsChecked = task.Groups[1].Value is "x" or "X", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }));
            AddInline(paragraph.Inlines, task.Groups[2].Value, false);
            return paragraph;
        }

        // Approximate the Visual Studio light-theme palette while keeping the
        // renderer dependency-free. These are classification colors, not a
        // full language-service implementation.
        private static readonly MediaBrush CodeTextBrush = FreezeBrush(MediaColor.FromRgb(30, 30, 30));
        private static readonly MediaBrush MathBrush = FreezeBrush(MediaColor.FromRgb(126, 70, 170));
        private static readonly MediaBrush CodeKeywordBrush = FreezeBrush(MediaColor.FromRgb(0, 0, 255));
        private static readonly MediaBrush CodeTypeBrush = FreezeBrush(MediaColor.FromRgb(38, 127, 153));
        private static readonly MediaBrush CodeStringBrush = FreezeBrush(MediaColor.FromRgb(163, 21, 21));
        private static readonly MediaBrush CodeNumberBrush = FreezeBrush(MediaColor.FromRgb(9, 134, 88));
        private static readonly MediaBrush CodeCommentBrush = FreezeBrush(MediaColor.FromRgb(0, 128, 0));
        private const double CodeFontSize = 10;
        private const double CodeLineHeight = 15;

        private static readonly HashSet<string> CodeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "abstract", "as", "async", "await", "base", "bool", "break", "case", "catch", "char", "class",
            "const", "continue", "def", "default", "del", "do", "else", "elif", "enum", "event", "except",
            "false", "finally", "float", "for", "foreach", "from", "function", "if", "in", "import", "interface",
            "internal", "is", "lambda", "let", "namespace", "new", "null", "None", "not", "of", "or", "out",
            "override", "pass", "private", "protected", "public", "raise", "readonly", "return", "select", "static",
            "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while",
            "with", "yield"
        };

        // C#, C++ and Java share enough declaration/control-flow syntax for a
        // small additional set to provide useful highlighting without pulling
        // a compiler or grammar package into the desktop client.
        private static readonly HashSet<string> CLikeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "alignas", "alignof", "and", "asm", "assert", "atomic_cancel", "atomic_commit", "atomic_noexcept",
            "bitand", "bitor", "compl", "concept", "consteval", "constexpr", "constinit", "const_cast", "co_await",
            "co_return", "co_yield", "delete", "dynamic_cast", "extends", "final", "friend", "implements", "instanceof",
            "module", "native", "noexcept", "nullptr", "package", "requires", "synchronized", "template",
            "this", "throws", "transient", "typename", "using", "virtual", "super", "strictfp", "static_cast",
            "reinterpret_cast", "dynamic_cast", "operator", "sizeof", "decltype", "thread_local", "union", "export",
            "sealed", "record", "delegate", "extern", "explicit", "implicit", "fixed", "unsafe", "checked", "unchecked",
            "lock", "nameof", "global", "get", "set", "init", "value", "required", "scoped", "when", "where"
        };

        private static readonly HashSet<string> CLikeTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "auto", "bool", "boolean", "byte", "char", "decimal", "double", "float", "int", "long", "nint", "nuint",
            "object", "sbyte", "short", "size_t", "string", "uint", "ulong", "ushort", "var", "void"
        };

        private static readonly HashSet<string> TypeScriptKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "abstract", "as", "asserts", "declare", "implements", "infer", "interface", "keyof", "namespace", "never",
            "readonly", "satisfies", "type", "unknown"
        };

        private static readonly HashSet<string> CLikeTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ArrayList", "Console", "Dictionary", "Exception", "IOException", "List", "Map", "Math", "Object", "String",
            "StringBuilder", "System", "Task", "Thread", "Vector", "Collections", "unordered_map", "vector", "size_t",
            "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t", "FILE"
        };

        private static BlockUIContainer CodeBlock(string language, string text)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 5, 0, 7),
                BorderBrush = FreezeBrush(MediaColor.FromRgb(213, 221, 229)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Background = FreezeBrush(MediaColor.FromRgb(247, 249, 251)),
                ClipToBounds = true,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new WpfTextBlock
            {
                Text = DisplayCodeLanguage(language),
                FontFamily = new MediaFontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = FreezeBrush(MediaColor.FromRgb(91, 103, 117)),
                Margin = new Thickness(12, 7, 12, 7)
            };
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            var separator = new Border
            {
                Height = 1,
                Background = FreezeBrush(MediaColor.FromRgb(220, 227, 234)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            Grid.SetRow(separator, 1);
            layout.Children.Add(separator);

            var codeLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (codeLines.Length == 0) codeLines = new[] { string.Empty };
            var codeGrid = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < codeLines.Length; index++)
            {
                codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var number = new WpfTextBlock
                {
                    Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                    TextAlignment = TextAlignment.Right,
                    Foreground = FreezeBrush(MediaColor.FromRgb(138, 150, 165)),
                    FontFamily = new MediaFontFamily("Cascadia Mono"),
                    FontSize = CodeFontSize,
                    LineHeight = CodeLineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(12, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetRow(number, index);
                Grid.SetColumn(number, 0);
                codeGrid.Children.Add(number);

                var line = new WpfTextBlock
                {
                    FontFamily = new MediaFontFamily("Cascadia Mono"),
                    FontSize = CodeFontSize,
                    LineHeight = CodeLineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = CodeTextBrush,
                    Margin = new Thickness(4, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                AddSyntaxHighlightedCodeLine(line.Inlines, codeLines[index], language);
                Grid.SetRow(line, index);
                Grid.SetColumn(line, 1);
                codeGrid.Children.Add(line);
            }

            var codeHost = new Border
            {
                Child = codeGrid,
                Padding = new Thickness(0, 9, 0, 10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            Grid.SetRow(codeHost, 2);
            layout.Children.Add(codeHost);

            card.Child = layout;
            return new BlockUIContainer(card);
        }

        private static string NormalizeCodeLanguage(string fenceInfo)
        {
            if (string.IsNullOrWhiteSpace(fenceInfo)) return string.Empty;
            var language = fenceInfo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            language = language.Trim().Trim('`').ToLowerInvariant();
            return language switch
            {
                "py" => "python",
                "js" => "javascript",
                "ts" => "typescript",
                "cs" => "csharp",
                "c#" => "csharp",
                "sh" or "shell" or "zsh" => "bash",
                "yml" => "yaml",
                _ => language
            };
        }

        private static string DisplayCodeLanguage(string language) => language switch
        {
            "" => "Code",
            "csharp" => "C#",
            "cpp" => "C++",
            "css" => "CSS",
            "html" => "HTML",
            "javascript" => "JavaScript",
            "json" => "JSON",
            "python" => "Python",
            "typescript" => "TypeScript",
            "yaml" => "YAML",
            "bash" => "Bash",
            _ => language.Length == 1 ? language.ToUpperInvariant() : char.ToUpperInvariant(language[0]) + language[1..]
        };

        private static void AddSyntaxHighlightedCodeLine(InlineCollection inlines, string text, string language)
        {
            if (text.Length == 0)
            {
                inlines.Add(new Run(" ") { Foreground = CodeTextBrush });
                return;
            }

            var position = 0;
            while (position < text.Length)
            {
                if (IsCommentStart(text, position, language))
                {
                    AddCodeRun(inlines, text[position..], CodeCommentBrush);
                    return;
                }

                var character = text[position];
                if (character is '"' or '\'' or '`')
                {
                    var quote = character;
                    var end = position + 1;
                    while (end < text.Length)
                    {
                        if (text[end] == '\\')
                        {
                            end += Math.Min(2, text.Length - end);
                            continue;
                        }
                        if (text[end] == quote)
                        {
                            end++;
                            break;
                        }
                        end++;
                    }
                    AddCodeRun(inlines, text[position..end], CodeStringBrush);
                    position = end;
                    continue;
                }

                if (char.IsDigit(character) && (position == 0 || !IsIdentifierCharacter(text[position - 1])))
                {
                    var end = position + 1;
                    while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or '_' or '+' or '-')) end++;
                    AddCodeRun(inlines, text[position..end], CodeNumberBrush);
                    position = end;
                    continue;
                }

                if (IsIdentifierCharacter(character))
                {
                    var end = position + 1;
                    while (end < text.Length && IsIdentifierCharacter(text[end])) end++;
                    var identifier = text[position..end];
                    var brush = IsCodeKeyword(identifier, language)
                        ? CodeKeywordBrush
                        : IsLikelyTypeName(identifier, language, text, position, end) ? CodeTypeBrush : CodeTextBrush;
                    AddCodeRun(inlines, identifier, brush);
                    position = end;
                    continue;
                }

                var plainEnd = position + 1;
                while (plainEnd < text.Length
                    && !IsIdentifierCharacter(text[plainEnd])
                    && text[plainEnd] is not ('"' or '\'' or '`')
                    && !IsCommentStart(text, plainEnd, language)
                    && !(char.IsDigit(text[plainEnd]) && (plainEnd == 0 || !IsIdentifierCharacter(text[plainEnd - 1]))))
                    plainEnd++;
                AddCodeRun(inlines, text[position..plainEnd], CodeTextBrush);
                position = plainEnd;
            }
        }

        private static bool IsCommentStart(string text, int position, string language)
        {
            if (position + 1 < text.Length && text[position] == '/' && (text[position + 1] is '/' or '*')) return true;
            return text[position] == '#' && (language is "python" or "bash" or "ruby" or "yaml" or "shell");
        }

        private static bool IsCodeKeyword(string identifier, string language)
        {
            if (CodeKeywords.Contains(identifier)) return true;
            if (language is "typescript" && TypeScriptKeywords.Contains(identifier)) return true;
            return IsCLikeLanguage(language) && (CLikeKeywords.Contains(identifier) || CLikeTypeKeywords.Contains(identifier));
        }

        private static bool IsLikelyTypeName(string identifier, string language, string line, int start, int end)
        {
            if (!IsCLikeLanguage(language)) return false;
            if (CLikeTypeNames.Contains(identifier)) return true;

            // Do not classify every PascalCase identifier as a type: that would
            // incorrectly color properties, variables, and method names. Only
            // use a type color in a few cheap, high-confidence declaration/use
            // contexts.
            var previous = PreviousIdentifier(line, start);
            if (previous is "class" or "struct" or "interface" or "enum" or "record" or "new" or "typeof" or "default" or "as" or "is")
                return true;

            var next = NextIdentifier(line, end);
            if (next is not null
                && (previous is "public" or "private" or "protected" or "internal" or "static" or "readonly" or "const" or "ref" or "out"))
                return true;

            return start > 0 && (line[start - 1] is '<' or ',');
        }

        private static bool IsCLikeLanguage(string language) => language is "csharp" or "cpp" or "c" or "java";

        private static string? PreviousIdentifier(string line, int start)
        {
            var end = start - 1;
            while (end >= 0 && char.IsWhiteSpace(line[end])) end--;
            if (end < 0 || !IsIdentifierCharacter(line[end])) return null;
            var begin = end;
            while (begin > 0 && IsIdentifierCharacter(line[begin - 1])) begin--;
            return line[begin..(end + 1)];
        }

        private static string? NextIdentifier(string line, int start)
        {
            var index = start;
            while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
            if (index >= line.Length || !IsIdentifierCharacter(line[index])) return null;
            var end = index + 1;
            while (end < line.Length && IsIdentifierCharacter(line[end])) end++;
            return line[index..end];
        }

        private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

        private static void AddCodeRun(InlineCollection inlines, string text, MediaBrush foreground)
        {
            if (text.Length == 0) return;
            inlines.Add(new Run(text)
            {
                FontFamily = new MediaFontFamily("Cascadia Mono"),
                FontSize = CodeFontSize,
                Foreground = foreground
            });
        }

        private static MediaBrush FreezeBrush(MediaColor color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Paragraph Paragraph(string text, double left, double size, bool bold, string prefix = "")
        {
            var paragraph = new Paragraph { Margin = new Thickness(left, 0, 0, 4), FontSize = size, TextAlignment = TextAlignment.Left };
            if (!string.IsNullOrEmpty(prefix)) paragraph.Inlines.Add(new Run(prefix) { FontWeight = FontWeights.Normal });
            AddInline(paragraph.Inlines, text, bold);
            return paragraph;
        }

        private static Paragraph ParagraphLines(IReadOnlyList<string> lines, double left, double size, bool bold)
        {
            var paragraph = new Paragraph { Margin = new Thickness(left, 0, 0, 4), FontSize = size, TextAlignment = TextAlignment.Left };
            for (var index = 0; index < lines.Count; index++)
            {
                if (index > 0) paragraph.Inlines.Add(new LineBreak());
                AddInline(paragraph.Inlines, lines[index].Trim(), bold);
            }
            return paragraph;
        }

        private static void AddInline(InlineCollection inlines, string text, bool paragraphBold)
        {
            foreach (var part in GetOrCreateInlinePlan(text))
            {
                switch (part.Kind)
                {
                    case InlineKind.Text:
                        AddTextWithEmoji(inlines, part.Text, run => run.FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.Normal);
                        break;
                    case InlineKind.Math:
                        AddMathInline(inlines, part.Text, paragraphBold);
                        break;
                    case InlineKind.Code:
                        inlines.Add(new Run(part.Text) { FontFamily = new MediaFontFamily("Cascadia Mono"), Background = new SolidColorBrush(MediaColor.FromRgb(241, 245, 249)), Foreground = new SolidColorBrush(MediaColor.FromRgb(30, 64, 175)) });
                        break;
                    case InlineKind.Strike:
                        AddTextWithEmoji(inlines, part.Text, run => run.TextDecorations = System.Windows.TextDecorations.Strikethrough);
                        break;
                    case InlineKind.Strong:
                        // Parse nested inline syntax again so formulas such as
                        // **$N_A \cdot N_B = 1$** keep both math layout and
                        // the surrounding bold style.
                        AddInline(inlines, part.Text, true);
                        break;
                    case InlineKind.Emphasis:
                        AddTextWithEmoji(inlines, UnescapeMarkdownEscapes(part.Text), run => run.FontStyle = FontStyles.Italic);
                        break;
                    case InlineKind.Link:
                    {
                        var hyperlink = new Hyperlink
                        {
                            Foreground = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)),
                            TextDecorations = System.Windows.TextDecorations.Underline
                        };
                        AddTextWithEmoji(hyperlink.Inlines, part.Text);
                        inlines.Add(hyperlink);
                        break;
                    }
                }
            }
        }

        private static IReadOnlyList<InlinePart> GetOrCreateInlinePlan(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<InlinePart>();
            if (text.Length > MaximumCacheableInlineCharacters) return ParseInlinePlan(text);

            lock (InlineCacheGate)
            {
                if (InlinePlanCache.TryGetValue(text, out var cached))
                {
                    InlinePlanLru.Remove(cached);
                    InlinePlanLru.AddFirst(cached);
                    return cached.Value.Parts;
                }
            }

            var parsed = ParseInlinePlan(text);
            lock (InlineCacheGate)
            {
                if (InlinePlanCache.TryGetValue(text, out var existing))
                {
                    InlinePlanLru.Remove(existing);
                    InlinePlanLru.AddFirst(existing);
                    return existing.Value.Parts;
                }

                var node = new LinkedListNode<InlineCacheEntry>(new InlineCacheEntry(text, parsed));
                InlinePlanLru.AddFirst(node);
                InlinePlanCache.Add(text, node);
                _inlineCachedCharacters += text.Length;
                while (InlinePlanCache.Count > MaximumInlineCacheEntries || _inlineCachedCharacters > MaximumInlineCachedCharacters)
                {
                    var oldest = InlinePlanLru.Last;
                    if (oldest is null) break;
                    InlinePlanLru.RemoveLast();
                    InlinePlanCache.Remove(oldest.Value.Markdown);
                    _inlineCachedCharacters -= oldest.Value.Markdown.Length;
                }
                return parsed;
            }
        }

        private static IReadOnlyList<InlinePart> ParseInlinePlan(string text)
        {
            var parts = new List<InlinePart>();
            var position = 0;
            void AddText(string value)
            {
                if (!string.IsNullOrEmpty(value)) parts.Add(new InlinePart(InlineKind.Text, NormalizeProseWhitespace(UnescapeMarkdownEscapes(value))));
            }

            foreach (Match match in InlineToken.Matches(text))
            {
                if (match.Index > position) AddText(text[position..match.Index]);
                if (match.Groups["math"].Success)
                    parts.Add(new InlinePart(InlineKind.Math, FormatLatex(match.Value)));
                else if (match.Groups["code"].Success)
                    parts.Add(new InlinePart(InlineKind.Code, match.Value[1..^1]));
                else if (match.Groups["strike"].Success)
                    parts.Add(new InlinePart(InlineKind.Strike, match.Value[2..^2]));
                else if (match.Groups["strong"].Success)
                    parts.Add(new InlinePart(InlineKind.Strong, match.Value[2..^2]));
                else if (match.Groups["em"].Success)
                    parts.Add(new InlinePart(InlineKind.Emphasis, match.Value[1..^1]));
                else
                {
                    var link = Regex.Match(match.Value, @"^\[([^\]]+)\]\(([^\)]+)\)$");
                    parts.Add(new InlinePart(InlineKind.Link, link.Success ? link.Groups[1].Value : match.Value));
                }
                position = match.Index + match.Length;
            }
            if (position < text.Length) AddText(text[position..]);
            return parts;
        }

        private static string NormalizeProseWhitespace(string value) => Regex.Replace(value, @"[ \t]{2,}", " ");

        private static string UnescapeMarkdownEscapes(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var builder = new StringBuilder(value.Length);
            for (var position = 0; position < value.Length; position++)
            {
                if (value[position] == '\\' && position + 1 < value.Length && "\\`*_[]~()".Contains(value[position + 1]))
                {
                    builder.Append(value[++position]);
                    continue;
                }
                builder.Append(value[position]);
            }
            return builder.ToString();
        }

        private static void AddMathInline(InlineCollection inlines, string formula, bool paragraphBold)
        {
            var normal = new StringBuilder();
            void FlushNormal()
            {
                if (normal.Length == 0) return;
                inlines.Add(new Run(normal.ToString())
                {
                    FontFamily = new MediaFontFamily("Cambria Math"),
                    FontStyle = FontStyles.Italic,
                    Foreground = MathBrush,
                    FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.Normal
                });
                normal.Clear();
            }

            for (var position = 0; position < formula.Length; position++)
            {
                var marker = formula[position];
                if (marker is not ('_' or '^'))
                {
                    // Braces used only for a simple math group are layout
                    // markers and should not appear in the rendered formula.
                    if (marker is not ('{' or '}')) normal.Append(marker);
                    continue;
                }

                var operandStart = position + 1;
                if (operandStart >= formula.Length)
                {
                    normal.Append(marker);
                    continue;
                }

                string operand;
                if (formula[operandStart] == '{')
                {
                    var groupEnd = FindMathGroupEnd(formula, operandStart);
                    if (groupEnd < 0)
                    {
                        normal.Append(marker);
                        continue;
                    }

                    operand = formula[(operandStart + 1)..groupEnd].Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal);
                    position = groupEnd;
                }
                else
                {
                    operand = formula[operandStart].ToString();
                    position = operandStart;
                }

                FlushNormal();
                inlines.Add(new Run(operand)
                {
                    FontFamily = new MediaFontFamily("Cambria Math"),
                    FontStyle = FontStyles.Italic,
                    Foreground = MathBrush,
                    FontSize = 10.5,
                    BaselineAlignment = marker == '^' ? BaselineAlignment.Superscript : BaselineAlignment.Subscript,
                    FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.Normal
                });
            }

            FlushNormal();
        }

        private static int FindMathGroupEnd(string formula, int openingBrace)
        {
            var depth = 0;
            for (var position = openingBrace; position < formula.Length; position++)
            {
                if (formula[position] == '{') depth++;
                else if (formula[position] == '}' && --depth == 0) return position;
            }
            return -1;
        }

        private static string FormatLatex(string value)
        {
            var formula = value.Trim();
            if (formula.StartsWith("$$", StringComparison.Ordinal)) formula = formula[2..^2];
            else if (formula.StartsWith("$", StringComparison.Ordinal)) formula = formula[1..^1];
            else if (formula.StartsWith("\\(", StringComparison.Ordinal)) formula = formula[2..^2];
            else if (formula.StartsWith("\\[", StringComparison.Ordinal)) formula = formula[2..^2];

            formula = Regex.Replace(formula, @"\\frac\{([^{}]*)\}\{([^{}]*)\}", "($1)/($2)");
            formula = Regex.Replace(formula, @"\\sqrt\{([^{}]*)\}", "√($1)");
            formula = Regex.Replace(formula, @"\\text\{([^{}]*)\}", "$1");
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["\\alpha"] = "α", ["\\beta"] = "β", ["\\gamma"] = "γ", ["\\delta"] = "δ", ["\\epsilon"] = "ε", ["\\theta"] = "θ", ["\\lambda"] = "λ", ["\\mu"] = "μ", ["\\pi"] = "π", ["\\sigma"] = "σ", ["\\phi"] = "φ", ["\\omega"] = "ω",
                ["\\Gamma"] = "Γ", ["\\Delta"] = "Δ", ["\\Theta"] = "Θ", ["\\Lambda"] = "Λ", ["\\Xi"] = "Ξ", ["\\Pi"] = "Π", ["\\Sigma"] = "Σ", ["\\Phi"] = "Φ", ["\\Psi"] = "Ψ", ["\\Omega"] = "Ω",
                ["\\notin"] = "∉", ["\\subseteq"] = "⊆", ["\\supseteq"] = "⊇", ["\\leq"] = "≤", ["\\geq"] = "≥", ["\\neq"] = "≠", ["\\approx"] = "≈", ["\\equiv"] = "≡", ["\\cong"] = "≅", ["\\simeq"] = "≃", ["\\asymp"] = "≍", ["\\propto"] = "∝", ["\\sim"] = "∼", ["\\ll"] = "≪", ["\\gg"] = "≫",
                ["\\in"] = "∈", ["\\subset"] = "⊂", ["\\supset"] = "⊃", ["\\cup"] = "∪", ["\\cap"] = "∩", ["\\emptyset"] = "∅", ["\\forall"] = "∀", ["\\exists"] = "∃", ["\\partial"] = "∂", ["\\nabla"] = "∇", ["\\land"] = "∧", ["\\lor"] = "∨", ["\\neg"] = "¬",
                ["\\Leftrightarrow"] = "⇔", ["\\Rightarrow"] = "⇒", ["\\Leftarrow"] = "⇐", ["\\iff"] = "⇔", ["\\implies"] = "⇒", ["\\mapsto"] = "↦",
                ["\\times"] = "×", ["\\cdot"] = "·", ["\\pm"] = "±", ["\\mp"] = "∓", ["\\infty"] = "∞", ["\\sum"] = "∑", ["\\prod"] = "∏", ["\\int"] = "∫", ["\\oint"] = "∮", ["\\sqrt"] = "√", ["\\rightarrow"] = "→", ["\\leftarrow"] = "←", ["\\leftrightarrow"] = "↔", ["\\to"] = "→",
                ["\\cdots"] = "⋯", ["\\ldots"] = "…", ["\\dots"] = "…", ["\\ell"] = "ℓ", ["\\Re"] = "ℜ", ["\\Im"] = "ℑ",
                ["\\left"] = "", ["\\right"] = "", ["\\, "] = " ", ["\\,"] = " ", ["\\; "] = " ", ["\\;"] = " "
            };
            foreach (var replacement in replacements) formula = formula.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
            return formula.Trim();
        }
    }
}
