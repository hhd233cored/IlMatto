using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Emoji.Wpf;
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
        @"(?<math>\$\$.*?\$\$|\$[^$\r\n]+\$|\\\([^\r\n]*?\\\)|\\\[[^\r\n]*?\\\])|(?<code>`[^`\r\n]+`)|(?<strike>~~[^~\r\n]+~~)|(?<strong>\*\*[^*\r\n]+\*\*|__[^_\r\n]+__)|(?<em>\*[^*\r\n]+\*|_[^_\r\n]+_)|(?<link>\[[^\]]+\]\([^\)]+\))",
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
        private sealed record CodeBlockModel(string Text) : MarkdownBlock;
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
                        document.Blocks.Add(CodeBlock(code.Text));
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
                        var paragraph = Paragraph(quote.Text, 12, 14, false, "│ ");
                        paragraph.Foreground = new SolidColorBrush(MediaColor.FromRgb(83, 97, 116));
                        paragraph.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(147, 197, 253));
                        paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                        paragraph.Padding = new Thickness(8, 0, 0, 0);
                        document.Blocks.Add(paragraph);
                        break;
                    }
                }
            }

            // WPF's default text renderer often falls back to monochrome glyph
            // outlines for Segoe UI Emoji. Emoji.Wpf replaces supported emoji
            // runs with vector inlines built from the color glyph layers that
            // Windows ships with the font. Keep the normal font runs above as a
            // safe fallback if substitution is unavailable on a particular OS.
            try
            {
                document.SubstituteGlyphs();
            }
            catch (Exception)
            {
                // Rendering the response must remain best-effort: the existing
                // Segoe UI Emoji fallback is still preferable to losing text.
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
                        blocks.Add(new CodeBlockModel(string.Join("\n", codeLines)));
                        codeLines.Clear();
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
            if (inCode && codeLines.Count > 0) blocks.Add(new CodeBlockModel(string.Join("\n", codeLines)));
            return new MarkdownRenderPlan(blocks);
        }

        private static bool IsTableRow(string line) => line.Contains('|') && SplitTableRow(line).Count >= 2;

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

        private static Paragraph BulletParagraph(string text, double left)
        {
            var task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
            if (!task.Success) return Paragraph(text, left, 14, false, "• ");
            var paragraph = new Paragraph { Margin = new Thickness(left, 0, 0, 4), TextAlignment = TextAlignment.Left };
            paragraph.Inlines.Add(new InlineUIContainer(new System.Windows.Controls.CheckBox { IsChecked = task.Groups[1].Value is "x" or "X", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }));
            AddInline(paragraph.Inlines, task.Groups[2].Value, false);
            return paragraph;
        }

        private static Paragraph CodeBlock(string text)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 5, 0, 7), Padding = new Thickness(10, 7, 10, 7), TextAlignment = TextAlignment.Left, Background = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250)) };
            paragraph.Inlines.Add(new Run(text) { FontFamily = new MediaFontFamily("Cascadia Mono"), FontSize = 13 });
            return paragraph;
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
                        inlines.Add(new Run(part.Text) { FontFamily = new MediaFontFamily("Cambria Math"), FontStyle = FontStyles.Italic, Foreground = new SolidColorBrush(MediaColor.FromRgb(126, 70, 170)), FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.Normal });
                        break;
                    case InlineKind.Code:
                        inlines.Add(new Run(part.Text) { FontFamily = new MediaFontFamily("Cascadia Mono"), Background = new SolidColorBrush(MediaColor.FromRgb(241, 245, 249)), Foreground = new SolidColorBrush(MediaColor.FromRgb(30, 64, 175)) });
                        break;
                    case InlineKind.Strike:
                        AddTextWithEmoji(inlines, part.Text, run => run.TextDecorations = System.Windows.TextDecorations.Strikethrough);
                        break;
                    case InlineKind.Strong:
                        AddTextWithEmoji(inlines, part.Text, run => run.FontWeight = FontWeights.Bold);
                        break;
                    case InlineKind.Emphasis:
                        AddTextWithEmoji(inlines, part.Text, run => run.FontStyle = FontStyles.Italic);
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
                if (!string.IsNullOrEmpty(value)) parts.Add(new InlinePart(InlineKind.Text, NormalizeProseWhitespace(value)));
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
                ["\\times"] = "×", ["\\cdot"] = "·", ["\\leq"] = "≤", ["\\geq"] = "≥", ["\\neq"] = "≠", ["\\pm"] = "±", ["\\infty"] = "∞", ["\\sum"] = "∑", ["\\int"] = "∫", ["\\rightarrow"] = "→", ["\\to"] = "→",
                ["\\left"] = "", ["\\right"] = "", ["\\, "] = " ", ["\\,"] = " ", ["\\; "] = " ", ["\\;"] = " "
            };
            foreach (var replacement in replacements) formula = formula.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
            return formula.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Trim();
        }
    }
}
