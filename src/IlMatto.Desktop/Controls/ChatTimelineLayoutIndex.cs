using IlMatto.Desktop.Models;
using IlMatto.Desktop.Infrastructure;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Runtime-only measured-height cache. Heights are keyed by the stable message
/// model object and a 32px content-width bucket; they intentionally never
/// become conversation persistence data.
/// </summary>
internal sealed class ChatMessageLayoutCache
{
    private readonly Dictionary<ManagerChatEntry, EntryHeights> _entries = new();

    public static int GetWidthBucket(double width) =>
        Math.Max(1, (int)Math.Round(Math.Max(1, width) / 32d, MidpointRounding.AwayFromZero));

    public double GetHeight(ManagerChatEntry entry, double width)
    {
        var bucket = GetWidthBucket(width);
        return TryGetMeasuredHeight(entry, width, out var height)
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
        if (!double.IsFinite(height) || height < 24 || entry.IsStreamingText || entry.IsThinking) return;
        if (!_entries.TryGetValue(entry, out var cached))
        {
            cached = new EntryHeights();
            _entries.Add(entry, cached);
        }
        cached.Measured[GetWidthBucket(width)] = Math.Clamp(height, 24, 12000);
    }

    public void ImportMeasuredHeights(
        IReadOnlyList<ManagerChatEntry> entries,
        double width,
        IReadOnlyDictionary<int, ManagerMessageLayoutCacheEntry> persisted)
    {
        var bucket = GetWidthBucket(width);
        foreach (var pair in persisted)
        {
            if (pair.Key < 0 || pair.Key >= entries.Count) continue;
            var height = pair.Value.RowHeight;
            if (!double.IsFinite(height) || height < 24) continue;

            var entry = entries[pair.Key];
            if (entry.IsStreamingText || entry.IsThinking) continue;
            if (!_entries.TryGetValue(entry, out var cached))
            {
                cached = new EntryHeights();
                _entries.Add(entry, cached);
            }

            cached.Measured[bucket] = Math.Clamp(height, 24, 12000);
        }
    }

    public void Invalidate(ManagerChatEntry entry) => _entries.Remove(entry);

    public void ForgetMissingEntries(ISet<ManagerChatEntry> liveEntries)
    {
        foreach (var entry in _entries.Keys.Where(entry => !liveEntries.Contains(entry)).ToArray())
            _entries.Remove(entry);
    }

    private static double EstimateHeight(ManagerChatEntry entry, double availableWidth)
    {
        if (entry.IsTransientStatus)
            return 40 + (entry.ShowDateSeparator ? 38 : 0);

        // Deliberately slightly high: a row shrinking once after its first
        // natural measure is less disruptive than a collapsed row expanding
        // while the user is moving a scrollbar thumb.
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

/// <summary>
/// A Fenwick-backed logical coordinate system for the complete conversation.
/// It derives every message's top/bottom from cached outer height; absolute
/// coordinates themselves are never cached.
/// </summary>
internal sealed class ChatTimelineLayoutIndex
{
    private readonly ChatMessageLayoutCache _cache;
    private readonly List<ManagerChatEntry> _entries = new();
    private readonly List<double> _heights = new();
    private FenwickTree _tree = new(0);
    private int _widthBucket = -1;

    public ChatTimelineLayoutIndex(ChatMessageLayoutCache cache) => _cache = cache;

    public int Count => _entries.Count;
    public double TotalHeight => _tree.Total;
    public ManagerChatEntry this[int index] => _entries[index];

    public bool Synchronize(IReadOnlyList<ManagerChatEntry> entries, double width)
    {
        var bucket = ChatMessageLayoutCache.GetWidthBucket(width);
        var sameEntries = _entries.Count == entries.Count && _entries.SequenceEqual(entries);
        if (sameEntries && _widthBucket == bucket) return false;

        _entries.Clear();
        _entries.AddRange(entries);
        _heights.Clear();
        foreach (var entry in entries) _heights.Add(_cache.GetHeight(entry, width));
        _tree = new FenwickTree(_heights);
        _widthBucket = bucket;
        _cache.ForgetMissingEntries(entries.ToHashSet());
        return true;
    }

    public int IndexOf(ManagerChatEntry entry) => _entries.FindIndex(candidate => ReferenceEquals(candidate, entry));
    public double GetHeight(int index) => index >= 0 && index < _heights.Count ? _heights[index] : 0;
    public double GetTop(int index) => index <= 0 ? 0 : _tree.PrefixSum(Math.Min(index, _heights.Count));

    public bool IsMeasured(int index, double width) => index >= 0 && index < _entries.Count &&
        _cache.TryGetMeasuredHeight(_entries[index], width, out _);

    public double UpdateMeasuredHeight(int index, double width, double measuredHeight)
    {
        if (index < 0 || index >= _heights.Count || !double.IsFinite(measuredHeight) || measuredHeight < 24) return 0;
        var entry = _entries[index];
        _cache.RecordMeasuredHeight(entry, width, measuredHeight);
        var newHeight = _cache.GetHeight(entry, width);
        var delta = newHeight - _heights[index];
        if (Math.Abs(delta) < 0.5) return 0;
        _heights[index] = newHeight;
        _tree.Add(index, delta);
        return delta;
    }

    public double Invalidate(ManagerChatEntry entry, double width)
    {
        var index = IndexOf(entry);
        if (index < 0) return 0;
        _cache.Invalidate(entry);
        var newHeight = _cache.GetHeight(entry, width);
        var delta = newHeight - _heights[index];
        if (Math.Abs(delta) < 0.5) return 0;
        _heights[index] = newHeight;
        _tree.Add(index, delta);
        return delta;
    }

    public int FindIndexAtOffset(double offset)
    {
        if (_heights.Count == 0) return -1;
        var clamped = Math.Clamp(offset, 0, Math.Max(0, TotalHeight - 0.001));
        return Math.Clamp(_tree.FindPrefixIndex(clamped), 0, _heights.Count - 1);
    }

    private sealed class FenwickTree
    {
        private readonly double[] _values;

        public FenwickTree(int length) => _values = new double[length + 1];
        public FenwickTree(IReadOnlyList<double> values) : this(values.Count)
        {
            for (var index = 0; index < values.Count; index++) Add(index, values[index]);
        }

        public double Total => PrefixSum(_values.Length - 1);

        public void Add(int zeroBasedIndex, double delta)
        {
            for (var index = zeroBasedIndex + 1; index < _values.Length; index += index & -index)
                _values[index] += delta;
        }

        public double PrefixSum(int exclusiveEnd)
        {
            var sum = 0d;
            for (var index = Math.Min(exclusiveEnd, _values.Length - 1); index > 0; index -= index & -index)
                sum += _values[index];
            return sum;
        }

        public int FindPrefixIndex(double offset)
        {
            var index = 0;
            var bit = HighestPowerOfTwoLessThan(_values.Length - 1);
            var remaining = offset;
            while (bit != 0)
            {
                var next = index + bit;
                if (next < _values.Length && _values[next] <= remaining)
                {
                    index = next;
                    remaining -= _values[next];
                }
                bit >>= 1;
            }
            return Math.Min(index, _values.Length - 2);
        }

        private static int HighestPowerOfTwoLessThan(int value)
        {
            var bit = 1;
            while ((bit << 1) <= value) bit <<= 1;
            return bit;
        }
    }
}
