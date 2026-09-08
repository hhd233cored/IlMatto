using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// A compact inline Markdown renderer. It intentionally avoids
/// FlowDocument; block-level Markdown is classified out before this control is
/// created. Emoji runs are substituted with the same Emoji.Wpf color glyphs
/// used by the full Markdown renderer.
/// </summary>
public sealed class InlineMarkdownTextBlock : TextBlock
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(InlineMarkdownTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));

    public InlineMarkdownTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        TextAlignment = TextAlignment.Left;
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
        Inlines.Clear();
        foreach (var part in InlineMarkdownParser.GetOrCreatePlan(markdown))
        {
            switch (part.Kind)
            {
                case InlineMarkdownKind.Text:
                    AddTextWithEmoji(Inlines, part.Text);
                    break;
                case InlineMarkdownKind.Code:
                    AddTextWithEmoji(Inlines, part.Text, run =>
                    {
                        run.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono");
                        run.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249));
                        run.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 64, 175));
                    });
                    break;
                case InlineMarkdownKind.Strike:
                    AddTextWithEmoji(Inlines, part.Text, run => run.TextDecorations = System.Windows.TextDecorations.Strikethrough);
                    break;
                case InlineMarkdownKind.Strong:
                    AddTextWithEmoji(Inlines, part.Text, run => run.FontWeight = FontWeights.Bold);
                    break;
                case InlineMarkdownKind.Emphasis:
                    AddTextWithEmoji(Inlines, part.Text, run => run.FontStyle = FontStyles.Italic);
                    break;
                case InlineMarkdownKind.Link:
                {
                    var hyperlink = new Hyperlink
                    {
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
                        TextDecorations = System.Windows.TextDecorations.Underline,
                    };
                    AddTextWithEmoji(hyperlink.Inlines, part.Text);
                    Inlines.Add(hyperlink);
                    break;
                }
            }
        }
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
