using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));

    private static readonly Regex InlineToken = new(
        @"(?<math>\$\$.*?\$\$|\$[^$\r\n]+\$|\\\([^\r\n]*?\\\)|\\\[[^\r\n]*?\\\])|(?<code>`[^`\r\n]+`)|(?<strike>~~[^~\r\n]+~~)|(?<strong>\*\*[^*\r\n]+\*\*|__[^_\r\n]+__)|(?<em>\*[^*\r\n]+\*|_[^_\r\n]+_)|(?<link>\[[^\]]+\]\([^\)]+\))",
        RegexOptions.Compiled);

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

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (MarkdownViewer)d;
        viewer.Document = MarkdownDocument.Build(e.NewValue as string ?? string.Empty);
        viewer.InvalidateMeasure();
    }

    /// <summary>
    /// FlowDocumentScrollViewer can report the width of its layout slot as its
    /// desired width. Measure it from the text's natural line width first so a
    /// short chat message does not inherit the whole star column.
    /// </summary>
    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var naturalWidth = MeasureNaturalWidth(Markdown);
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

        var typeface = new Typeface(
            new MediaFontFamily("SimSun"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        var brush = Foreground ?? System.Windows.Media.Brushes.Black;
        var pixelsPerDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        var maximum = 0d;

        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var measureText = StripMarkdownForMeasure(line);
            if (measureText.Length == 0) continue;
            var formatted = new FormattedText(
                measureText,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                FontSize > 0 ? FontSize : 14,
                brush,
                pixelsPerDip);
            maximum = Math.Max(maximum, formatted.WidthIncludingTrailingWhitespace);
        }

        return Math.Ceiling(maximum);
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

    private static FlowDocument CreateDocument() => new()
    {
        PagePadding = new Thickness(0),
        ColumnWidth = double.PositiveInfinity,
        MaxPageWidth = double.PositiveInfinity,
        IsColumnWidthFlexible = true
    };

    private static class MarkdownDocument
    {
        public static FlowDocument Build(string markdown)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity,
                MaxPageWidth = double.PositiveInfinity,
                IsColumnWidthFlexible = true,
                // Use 宋体 for the rendered conversation body. Inline code,
                // LaTeX and fenced code blocks still provide their own
                // monospace/math fonts below.
                FontFamily = new MediaFontFamily("SimSun"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextAlignment = TextAlignment.Left,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(23, 32, 51))
            };

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var inCode = false;
            var codeLines = new List<string>();
            var paragraphLines = new List<string>();
            void FlushParagraph()
            {
                if (paragraphLines.Count == 0) return;
                document.Blocks.Add(Paragraph(string.Join(" ", paragraphLines.Select(line => line.Trim())), 0, 14, false));
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
                        document.Blocks.Add(CodeBlock(string.Join("\n", codeLines)));
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
                    document.Blocks.Add(TableBlock(tableRows));
                    continue;
                }

                var trimmed = line.TrimStart();
                var heading = Regex.Match(trimmed, "^(#{1,6})\\s+(.*)$");
                if (heading.Success)
                {
                    FlushParagraph();
                    var level = heading.Groups[1].Value.Length;
                    var paragraph = Paragraph(heading.Groups[2].Value, 0, level <= 2 ? 18 : 16, true);
                    paragraph.Margin = new Thickness(0, 8, 0, 4);
                    document.Blocks.Add(paragraph);
                    continue;
                }

                var bullet = Regex.Match(trimmed, "^(?:[-*+]\\s+)(.*)$");
                if (bullet.Success)
                {
                    FlushParagraph();
                    document.Blocks.Add(BulletParagraph(bullet.Groups[1].Value, 18));
                    continue;
                }

                var numbered = Regex.Match(trimmed, "^(\\d+)[.)]\\s+(.*)$");
                if (numbered.Success)
                {
                    FlushParagraph();
                    document.Blocks.Add(Paragraph(numbered.Groups[2].Value, 18, 14, false, $"{numbered.Groups[1].Value}. "));
                    continue;
                }

                var quote = Regex.Match(trimmed, "^>\\s?(.*)$");
                if (quote.Success)
                {
                    FlushParagraph();
                    var paragraph = Paragraph(quote.Groups[1].Value, 12, 14, false, "│ ");
                    paragraph.Foreground = new SolidColorBrush(MediaColor.FromRgb(83, 97, 116));
                    paragraph.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(147, 197, 253));
                    paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                    paragraph.Padding = new Thickness(8, 0, 0, 0);
                    document.Blocks.Add(paragraph);
                    continue;
                }

                paragraphLines.Add(line);
            }
            FlushParagraph();
            if (inCode && codeLines.Count > 0) document.Blocks.Add(CodeBlock(string.Join("\n", codeLines)));
            return document;
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
            if (!string.IsNullOrEmpty(prefix)) paragraph.Inlines.Add(new Run(prefix) { FontWeight = FontWeights.SemiBold });
            AddInline(paragraph.Inlines, text, bold);
            return paragraph;
        }

        private static void AddInline(InlineCollection inlines, string text, bool paragraphBold)
        {
            var position = 0;
            foreach (Match match in InlineToken.Matches(text))
            {
                if (match.Index > position) inlines.Add(new Run(NormalizeProseWhitespace(text[position..match.Index])) { FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.SemiBold });
                if (match.Groups["math"].Success)
                {
                    inlines.Add(new Run(FormatLatex(match.Value)) { FontFamily = new MediaFontFamily("Cambria Math"), FontStyle = FontStyles.Italic, Foreground = new SolidColorBrush(MediaColor.FromRgb(126, 70, 170)), FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.SemiBold });
                }
                else if (match.Groups["code"].Success)
                {
                    inlines.Add(new Run(match.Value[1..^1]) { FontFamily = new MediaFontFamily("Cascadia Mono"), Background = new SolidColorBrush(MediaColor.FromRgb(241, 245, 249)), Foreground = new SolidColorBrush(MediaColor.FromRgb(30, 64, 175)) });
                }
                else if (match.Groups["strike"].Success)
                {
                    inlines.Add(new Run(match.Value[2..^2]) { TextDecorations = System.Windows.TextDecorations.Strikethrough });
                }
                else if (match.Groups["strong"].Success)
                {
                    inlines.Add(new Run(match.Value[2..^2]) { FontWeight = FontWeights.Bold });
                }
                else if (match.Groups["em"].Success)
                {
                    inlines.Add(new Run(match.Value[1..^1]) { FontStyle = FontStyles.Italic });
                }
                else
                {
                    var link = Regex.Match(match.Value, @"^\[([^\]]+)\]\(([^\)]+)\)$");
                    inlines.Add(new Hyperlink(new Run(link.Success ? link.Groups[1].Value : match.Value)) { Foreground = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)), TextDecorations = System.Windows.TextDecorations.Underline });
                }
                position = match.Index + match.Length;
            }
            if (position < text.Length) inlines.Add(new Run(NormalizeProseWhitespace(text[position..])) { FontWeight = paragraphBold ? FontWeights.Bold : FontWeights.SemiBold });
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
