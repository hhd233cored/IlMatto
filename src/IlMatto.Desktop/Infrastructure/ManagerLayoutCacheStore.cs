using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>
/// Persistent UI-only layout hints for one Manager conversation. This is
/// deliberately separate from conversations.json: heights are measured from
/// the current WPF theme and are only safe as placeholder hints.
/// </summary>
internal static class ManagerLayoutCacheStore
{
    private const int MaxEntries = 10_000;
    private const int WidthBucketSize = 32;
    private const double GeometryEpsilon = 0.5;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object FileLock = new();

    private static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IlMatto", "manager-sessions", "layout-cache");

    public static int GetWidthBucket(double contentWidth) =>
        Math.Max(1, (int)Math.Round(Math.Max(1, contentWidth) / WidthBucketSize, MidpointRounding.AwayFromZero));

    public static bool TryLoad(string sessionId, int widthBucket, out IReadOnlyDictionary<int, ManagerMessageLayoutCacheEntry> entries)
    {
        entries = new Dictionary<int, ManagerMessageLayoutCacheEntry>();
        if (string.IsNullOrWhiteSpace(sessionId) || widthBucket <= 0) return false;

        try
        {
            var path = GetPath(sessionId);
            if (!File.Exists(path)) return false;
            var snapshot = JsonSerializer.Deserialize<ManagerLayoutCacheSnapshot>(File.ReadAllText(path), Options);
            if (snapshot is null || !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal) || snapshot.WidthBucket != widthBucket)
                return false;

            entries = CleanEntries(snapshot.Entries)
                .ToDictionary(item => item.MessageIndex, item => item);
            return entries.Count > 0;
        }
        catch
        {
            // A corrupt or locked layout cache must never affect ordinary chat.
            entries = new Dictionary<int, ManagerMessageLayoutCacheEntry>();
            return false;
        }
    }

    public static bool Save(string sessionId, int widthBucket, IEnumerable<ManagerMessageLayoutCacheEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || widthBucket <= 0) return false;
        var cleanEntries = CleanEntries(entries);
        if (cleanEntries.Count == 0) return false;

        lock (FileLock)
        {
            if (TryReadSnapshot(sessionId, widthBucket, out var existing) &&
                AreEquivalent(existing.Entries, cleanEntries))
                return false;

            WriteSnapshot(sessionId, widthBucket, cleanEntries);
            return true;
        }
    }

    /// <summary>
    /// Updates only the supplied message measurements while preserving other
    /// messages already stored for the same session and width bucket. This is
    /// used by the air-bubble timeline, which measures only a small visible
    /// range at a time.
    /// </summary>
    public static bool MergeAndSave(string sessionId, int widthBucket, IEnumerable<ManagerMessageLayoutCacheEntry> updates)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || widthBucket <= 0) return false;

        var cleanUpdates = CleanEntries(updates);
        if (cleanUpdates.Count == 0) return false;

        lock (FileLock)
        {
            var merged = new Dictionary<int, ManagerMessageLayoutCacheEntry>();
            var hasMatchingSnapshot = TryReadSnapshot(sessionId, widthBucket, out var existing);
            if (hasMatchingSnapshot)
            {
                foreach (var entry in CleanEntries(existing.Entries))
                    merged[entry.MessageIndex] = entry;
            }

            foreach (var entry in cleanUpdates)
                merged[entry.MessageIndex] = entry;

            var cleanMerged = CleanEntries(merged.Values);
            if (hasMatchingSnapshot && AreEquivalent(existing.Entries, cleanMerged))
                return false;

            WriteSnapshot(sessionId, widthBucket, cleanMerged);
            return true;
        }
    }

    private static List<ManagerMessageLayoutCacheEntry> CleanEntries(IEnumerable<ManagerMessageLayoutCacheEntry> entries) =>
        (entries ?? Enumerable.Empty<ManagerMessageLayoutCacheEntry>())
            .Where(item => item.MessageIndex >= 0 && double.IsFinite(item.RowHeight) && item.RowHeight is >= 24 and <= 12000)
            .Select(item => new ManagerMessageLayoutCacheEntry
            {
                MessageIndex = item.MessageIndex,
                RowHeight = Math.Clamp(item.RowHeight, 24, 12000),
                BubbleHeight = NormalizeOptionalDimension(item.BubbleHeight),
                BubbleWidth = NormalizeOptionalDimension(item.BubbleWidth),
            })
            .GroupBy(item => item.MessageIndex)
            .Select(group => group.Last())
            .OrderBy(item => item.MessageIndex)
            .Take(MaxEntries)
            .ToList();

    private static bool AreEquivalent(
        IEnumerable<ManagerMessageLayoutCacheEntry> left,
        IEnumerable<ManagerMessageLayoutCacheEntry> right)
    {
        var leftEntries = CleanEntries(left);
        var rightEntries = CleanEntries(right);
        if (leftEntries.Count != rightEntries.Count) return false;

        for (var index = 0; index < leftEntries.Count; index++)
        {
            var oldEntry = leftEntries[index];
            var newEntry = rightEntries[index];
            if (oldEntry.MessageIndex != newEntry.MessageIndex ||
                !NearlyEqual(oldEntry.RowHeight, newEntry.RowHeight) ||
                !NearlyEqual(oldEntry.BubbleHeight, newEntry.BubbleHeight) ||
                !NearlyEqual(oldEntry.BubbleWidth, newEntry.BubbleWidth))
                return false;
        }

        return true;
    }

    private static bool NearlyEqual(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= GeometryEpsilon;

    private static double NormalizeOptionalDimension(double value) =>
        double.IsFinite(value) && value >= 24 ? Math.Clamp(value, 24, 12000) : 0;

    private static bool TryReadSnapshot(string sessionId, int widthBucket, out ManagerLayoutCacheSnapshot snapshot)
    {
        snapshot = new ManagerLayoutCacheSnapshot();
        try
        {
            var path = GetPath(sessionId);
            if (!File.Exists(path)) return false;
            var loaded = JsonSerializer.Deserialize<ManagerLayoutCacheSnapshot>(File.ReadAllText(path), Options);
            if (loaded is null || !string.Equals(loaded.SessionId, sessionId, StringComparison.Ordinal) ||
                loaded.WidthBucket != widthBucket)
                return false;
            snapshot = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteSnapshot(string sessionId, int widthBucket, IEnumerable<ManagerMessageLayoutCacheEntry> entries)
    {
        var cleanEntries = CleanEntries(entries);
        if (cleanEntries.Count == 0) return;

        var snapshot = new ManagerLayoutCacheSnapshot
        {
            SessionId = sessionId,
            WidthBucket = widthBucket,
            UpdatedAt = DateTimeOffset.UtcNow,
            Entries = cleanEntries,
        };

        var directory = RootPath;
        Directory.CreateDirectory(directory);
        var path = GetPath(sessionId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string GetPath(string sessionId) => Path.Combine(RootPath, SafeFileToken(sessionId) + ".json");

    private static string SafeFileToken(string value)
    {
        if (value.Length <= 128 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
            return value;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

internal sealed class ManagerLayoutCacheSnapshot
{
    public string SessionId { get; set; } = "";
    public int WidthBucket { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ManagerMessageLayoutCacheEntry> Entries { get; set; } = new();
}

internal sealed class ManagerMessageLayoutCacheEntry
{
    public int MessageIndex { get; set; }
    public double RowHeight { get; set; }
    public double BubbleHeight { get; set; }
    public double BubbleWidth { get; set; }
}
