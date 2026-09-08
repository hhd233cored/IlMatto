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
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

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

            entries = snapshot.Entries
                .Where(item => item.MessageIndex >= 0 && item.RowHeight is >= 24 and <= 12000)
                .GroupBy(item => item.MessageIndex)
                .ToDictionary(group => group.Key, group => group.Last());
            return entries.Count > 0;
        }
        catch
        {
            // A corrupt or locked layout cache must never affect ordinary chat.
            entries = new Dictionary<int, ManagerMessageLayoutCacheEntry>();
            return false;
        }
    }

    public static void Save(string sessionId, int widthBucket, IEnumerable<ManagerMessageLayoutCacheEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || widthBucket <= 0) return;

        var cleanEntries = entries
            .Where(item => item.MessageIndex >= 0 && item.RowHeight is >= 24 and <= 12000)
            .GroupBy(item => item.MessageIndex)
            .Select(group => group.Last())
            .OrderBy(item => item.MessageIndex)
            .Take(MaxEntries)
            .ToList();
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
