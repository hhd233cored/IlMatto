using System.IO;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>
/// Deletes only IlMatto-owned, session-scoped files. Antigravity CLI history
/// and the shared companion profile are intentionally outside this scope.
/// </summary>
public static class ManagerSessionDataStore
{
    public static bool TryDeleteCoordinatorSession(string? sessionFile)
    {
        if (string.IsNullOrWhiteSpace(sessionFile)) return true;
        try
        {
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto", "manager-sessions", "coordinator"));
            var candidate = Path.GetFullPath(sessionFile);
            if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(candidate), ".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(candidate)) File.Delete(candidate);
            return true;
        }
        catch { return false; }
    }

    public static async Task DeleteSessionDataAsync(string sessionId)
    {
        if (!IsSafeSessionId(sessionId)) throw new InvalidOperationException("会话标识无效，无法删除本地会话数据。");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targets = new[]
        {
            Path.Combine(localAppData, "IlMatto", "companion-memory", "sessions", sessionId),
            Path.Combine(localAppData, "IlMatto", "manager-sessions", "attachments", sessionId),
            Path.Combine(localAppData, "IlMatto", "manager-runtime", "attachments", sessionId),
        };
        foreach (var target in targets)
        {
            await DeleteTargetWithRetryAsync(target);
        }
    }

    private static async Task DeleteTargetWithRetryAsync(string target)
    {
        Exception? lastException = null;
        foreach (var delay in new[] { 0, 120, 250, 500, 1000, 1500 })
        {
            if (delay > 0) await Task.Delay(delay);
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else if (File.Exists(target)) File.Delete(target);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }
        throw lastException ?? new IOException($"无法删除会话数据：{target}");
    }

    private static bool IsSafeSessionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 160 &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}
