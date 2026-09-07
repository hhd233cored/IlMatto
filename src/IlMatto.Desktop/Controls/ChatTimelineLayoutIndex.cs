using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Prefix-height index for a single in-memory chat timeline. It lets the
/// virtualizing panel map scrollbar pixels to messages without creating every
/// Markdown view first.
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
    public int WidthBucket => _widthBucket;
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
        return true;
    }

    public double GetHeight(int index) => index >= 0 && index < _heights.Count ? _heights[index] : 0;
    public double GetTop(int index) => index <= 0 ? 0 : _tree.PrefixSum(Math.Min(index, _heights.Count));

    public int FindIndexAtOffset(double offset)
    {
        if (_heights.Count == 0) return -1;
        var clamped = Math.Clamp(offset, 0, Math.Max(0, TotalHeight - 0.001));
        return Math.Clamp(_tree.FindPrefixIndex(clamped), 0, _heights.Count - 1);
    }

    public double UpdateMeasuredHeight(int index, double width, double measuredHeight)
    {
        if (index < 0 || index >= _heights.Count || !double.IsFinite(measuredHeight) || measuredHeight < 24) return 0;
        var entry = _entries[index];
        if (entry.IsStreamingText) return 0;
        _cache.RecordMeasuredHeight(entry, width, measuredHeight);
        var newHeight = _cache.GetHeight(entry, width);
        var delta = newHeight - _heights[index];
        if (Math.Abs(delta) < 0.5) return 0;
        _heights[index] = newHeight;
        _tree.Add(index, delta);
        return delta;
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

        // Returns the index that owns an offset: prefix(index) <= offset < prefix(index + 1).
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
