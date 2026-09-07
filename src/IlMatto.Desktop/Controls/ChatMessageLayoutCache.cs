using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Runtime-only height memory for the Manager chat timeline.  It deliberately
/// uses the message object as the key: the data is never serialized and is
/// discarded when the desktop process exits.
/// </summary>
internal sealed class ChatMessageLayoutCache
{
    private readonly Dictionary<ManagerChatEntry, EntryHeights> _entries = new();

    public static int GetWidthBucket(double width)
        => Math.Max(1, (int)Math.Round(Math.Max(1, width) / 32d, MidpointRounding.AwayFromZero));

    public double GetHeight(ManagerChatEntry entry, double width)
    {
        var bucket = GetWidthBucket(width);
        return _entries.TryGetValue(entry, out var cached) && cached.Measured.TryGetValue(bucket, out var height)
            ? height
            : EstimateHeight(entry, bucket * 32d);
    }

    public bool TryGetMeasuredHeight(ManagerChatEntry entry, double width, out double height)
    {
        var bucket = GetWidthBucket(width);
        if (_entries.TryGetValue(entry, out var cached) && cached.Measured.TryGetValue(bucket, out height)) return true;
        height = 0;
        return false;
    }

    public void RecordMeasuredHeight(ManagerChatEntry entry, double width, double height)
    {
        // FlowDocument can briefly report a tiny/zero desired height while a
        // recycled view is waiting for rendering. Never allow that transient
        // state to poison the persistent-in-process layout estimate.
        if (!double.IsFinite(height) || height < 24 || entry.IsStreamingText) return;

        var bucket = GetWidthBucket(width);
        if (!_entries.TryGetValue(entry, out var cached))
        {
            cached = new EntryHeights();
            _entries.Add(entry, cached);
        }

        cached.Measured[bucket] = Math.Clamp(height, 24, 12000);
    }

    public void ForgetMissingEntries(ISet<ManagerChatEntry> liveEntries)
    {
        foreach (var entry in _entries.Keys.Where(entry => !liveEntries.Contains(entry)).ToArray())
            _entries.Remove(entry);
    }

    private static double EstimateHeight(ManagerChatEntry entry, double availableWidth)
    {
        if (entry.IsTransientStatus)
            return 40 + (entry.ShowDateSeparator ? 38 : 0);

        // The estimates intentionally err a little high. A modest correction
        // after first measurement is much less distracting than a collapsed
        // row that expands while the scrollbar is being dragged.
        var bubbleWidth = Math.Max(150, availableWidth * 0.6666667 - 78);
        var charactersPerLine = Math.Max(14, (int)(bubbleWidth / 14.2));
        var text = entry.Text ?? string.Empty;
        var textLines = EstimateWrappedLineCount(text, charactersPerLine);
        var contentHeight = 30d + textLines * 24;

        if (entry.Attachments.Count > 0)
        {
            var attachmentsPerRow = Math.Max(1, (int)(bubbleWidth / 188));
            contentHeight += Math.Ceiling(entry.Attachments.Count / (double)attachmentsPerRow) * 148 + 6;
        }

        var visibleOperations = entry.Segments.Sum(segment => segment.Operations.Count(operation => operation.IsVisibleOperation));
        if (visibleOperations > 0) contentHeight += 30 + Math.Min(visibleOperations, 6) * 28;
        if (entry.CodeResult is not null) contentHeight += 132;

        // Avatar, role and bottom row margin are part of the outer item.
        var rowHeight = Math.Max(54, contentHeight + 30);
        if (entry.ShowDateSeparator) rowHeight += 48;
        return Math.Clamp(rowHeight, 48, 3200);
    }

    private static int EstimateWrappedLineCount(string text, int charactersPerLine)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var lines = 0;
        foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
            lines += Math.Max(1, (int)Math.Ceiling(Math.Max(1, line.Length) / (double)charactersPerLine));
        return Math.Clamp(lines, 1, 120);
    }

    private sealed class EntryHeights
    {
        public Dictionary<int, double> Measured { get; } = new();
    }
}
