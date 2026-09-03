using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>
/// Private execution history.  It is deliberately outside conversation.json,
/// protected for the current Windows user, and never used to build main-agent
/// history or prompts.
/// </summary>
public static class TaskTraceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto", "task-traces");

    public static void Append(string sessionId, ManagerTaskTraceEvent traceEvent)
    {
        if (!IsSafeId(sessionId) || !IsSafeId(traceEvent.TaskId)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, sessionId));
            var path = Path.Combine(Root, sessionId, traceEvent.TaskId + ".trace");
            var events = Read(path);
            events.Add(traceEvent);
            Write(path, events);
        }
        catch { /* Audit persistence must not interrupt an active coding task. */ }
    }

    public static void DeleteConversation(string sessionId)
    {
        if (!IsSafeId(sessionId)) return;
        try
        {
            var directory = Path.Combine(Root, sessionId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch { }
    }

    public static void CleanupExpired(int retentionDays = 30)
    {
        if (retentionDays < 1 || !Directory.Exists(Root)) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var path in Directory.EnumerateFiles(Root, "*.trace", SearchOption.AllDirectories))
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
        }
        catch { }
    }

    private static List<ManagerTaskTraceEvent> Read(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<ManagerTaskTraceEvent>>(bytes, Options) ?? new();
        }
        catch { return new(); }
    }

    private static void Write(string path, List<ManagerTaskTraceEvent> events)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(events, Options);
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, true);
    }

    private static bool IsSafeId(string? value) => !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
}
