using System.Text.Json;
using System.IO;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

public static class ConversationStore
{
    private const int MaxPersistedDetailsLength = 16_000;
    private const int MaxPersistedOutputLength = 12_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto", "conversations.json");

    public static IReadOnlyList<ConversationItem> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return Array.Empty<ConversationItem>();
            var snapshots = JsonSerializer.Deserialize<List<ConversationSnapshot>>(File.ReadAllText(StorePath), JsonOptions) ?? new();
            return snapshots.Select(ToConversation).ToList();
        }
        catch { return Array.Empty<ConversationItem>(); }
    }

    public static void Save(IEnumerable<ConversationItem> conversations)
    {
        var directory = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = StorePath + ".tmp";
        var snapshots = conversations.Take(100).Select(FromConversation).ToList();
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshots, JsonOptions));
        File.Move(temporaryPath, StorePath, overwrite: true);
    }

    public static bool TryDeletePiSession(string? sessionFile)
    {
        if (string.IsNullOrWhiteSpace(sessionFile)) return true;
        try
        {
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto", "sessions"));
            var candidate = Path.GetFullPath(sessionFile);
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(candidate)) File.Delete(candidate);
            return true;
        }
        catch { return false; }
    }

    private static ConversationItem ToConversation(ConversationSnapshot snapshot)
    {
        var conversation = new ConversationItem(snapshot.SessionId, string.IsNullOrWhiteSpace(snapshot.Title) ? "新对话" : snapshot.Title)
        {
            UpdatedAt = snapshot.UpdatedAt,
            WorkspacePath = snapshot.WorkspacePath ?? "",
            PiSessionFile = snapshot.PiSessionFile
        };
        foreach (var message in snapshot.Messages ?? new())
        {
            var chat = new ChatEntry(message.Role, message.Text);
            chat.IsCompleted = message.IsCompleted;
            foreach (var editedFile in message.EditedFiles ?? new()) chat.EditedFiles.Add(new EditedFileItem(ToWorkspaceRelative(snapshot.WorkspacePath, editedFile.Path), editedFile.Added, editedFile.Removed));
            if (message.Segments is { Count: > 0 })
            {
                chat.Segments.Clear();
                foreach (var segment in message.Segments)
                {
                    var restored = new ChatSegment(segment.Kind, segment.Text) { IsExpanded = segment.IsExpanded };
                    foreach (var operation in segment.Operations ?? new())
                        restored.Operations.Add(ToProcess(operation));
                    chat.Segments.Add(restored);
                }
            }
            else if (message.InlineOperations is { Count: > 0 })
            {
                var restored = new ChatSegment("operations");
                foreach (var operation in message.InlineOperations)
                    restored.Operations.Add(ToProcess(operation));
                chat.Segments.Add(restored);
            }
            conversation.Messages.Add(chat);
        }
        foreach (var activity in snapshot.Activities ?? new())
        {
            var status = activity.Status is "进行中" or "执行中" or "等待批准" or "等待确认" ? "会话中断" : activity.Status;
            conversation.Activities.Add(new ActivityItem(activity.Title, status, Limit(activity.Details, MaxPersistedDetailsLength), LimitNullable(activity.Output, MaxPersistedOutputLength)));
        }
        foreach (var process in snapshot.Processes ?? new()) conversation.Processes.Add(ToProcess(process));
        return conversation;
    }

    private static ConversationSnapshot FromConversation(ConversationItem conversation) => new()
    {
        SessionId = conversation.SessionId,
        WorkspacePath = conversation.WorkspacePath,
        PiSessionFile = conversation.PiSessionFile,
        Title = conversation.Title,
        UpdatedAt = conversation.UpdatedAt,
        Messages = conversation.Messages.Select(message => new ChatSnapshot
        {
            Role = message.Role,
            Text = message.Text,
            IsCompleted = message.IsCompleted,
            EditedFiles = message.EditedFiles.Select(file => new EditedFileSnapshot { Path = file.Path, Added = file.Added, Removed = file.Removed }).ToList(),
            Segments = message.Segments.Select(segment => new SegmentSnapshot
            {
                Kind = segment.Kind,
                Text = segment.Text,
                IsExpanded = segment.IsExpanded,
                Operations = segment.Operations.Select(FromProcess).ToList()
            }).ToList()
        }).ToList(),
        Activities = conversation.Activities.Select(activity => new ActivitySnapshot { Title = activity.Title, Status = activity.Status, Details = Limit(activity.Details, MaxPersistedDetailsLength), Output = LimitNullable(activity.Output, MaxPersistedOutputLength) }).ToList(),
        Processes = conversation.Processes.Select(FromProcess).ToList()
    };

    private static ProcessItem ToProcess(ProcessSnapshot process)
    {
        // Older conversation snapshots could be saved between the final text
        // delta and session_state=idle. A reopened conversation is no longer
        // running, so stale thinking rows should not remain "进行中" forever.
        var status = process.Status is "进行中" or "执行中" or "等待批准" or "等待确认"
            ? "会话中断"
            : process.Status;
        return new ProcessItem(process.Kind, process.Title, status, Limit(process.Details, MaxPersistedDetailsLength), process.CallId, process.CommandLine) { IsExpanded = process.IsExpanded };
    }
    private static ProcessSnapshot FromProcess(ProcessItem process) => new() { Kind = process.Kind, Title = process.Title, Status = process.Status, Details = Limit(process.Details, MaxPersistedDetailsLength), CallId = process.CallId, CommandLine = process.CommandLine, IsExpanded = process.IsExpanded };
    private static string Limit(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= maxLength) return value;
        return value[..maxLength].TrimEnd() + "\n…（内容已截断）";
    }

    private static string? LimitNullable(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value[..maxLength].TrimEnd() + "\n鈥︼紙鍐呭宸叉埅鏂級";
    }

    private static string ToWorkspaceRelative(string? workspace, string value)
    {
        var candidate = (value ?? "").Trim().Trim('`').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathRooted(candidate)) return candidate.TrimStart('/');
        try
        {
            var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(candidate);
            var relative = Path.GetRelativePath(root, full);
            if (!relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return relative.Replace('\\', '/');
        }
        catch { }
        return candidate.TrimStart('/');
    }

    private sealed class ConversationSnapshot
    {
        public string SessionId { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public string? WorkspacePath { get; set; }
        public string? PiSessionFile { get; set; }
        public List<ChatSnapshot>? Messages { get; set; }
        public List<ActivitySnapshot>? Activities { get; set; }
        public List<ProcessSnapshot>? Processes { get; set; }
    }

    private sealed class ChatSnapshot
    {
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsCompleted { get; set; }
        public List<EditedFileSnapshot>? EditedFiles { get; set; }
        public List<SegmentSnapshot>? Segments { get; set; }
        // Kept for conversations written by the previous inline-operation layout.
        public List<ProcessSnapshot>? InlineOperations { get; set; }
    }
    private sealed class EditedFileSnapshot { public string Path { get; set; } = ""; public int Added { get; set; } public int Removed { get; set; } }
    private sealed class SegmentSnapshot
    {
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsExpanded { get; set; }
        public List<ProcessSnapshot>? Operations { get; set; }
    }
    private sealed class ActivitySnapshot { public string Title { get; set; } = ""; public string Status { get; set; } = ""; public string Details { get; set; } = ""; public string? Output { get; set; } }
    private sealed class ProcessSnapshot { public string Kind { get; set; } = ""; public string Title { get; set; } = ""; public string Status { get; set; } = ""; public string Details { get; set; } = ""; public string? CallId { get; set; } public string? CommandLine { get; set; } public bool IsExpanded { get; set; } }
}
