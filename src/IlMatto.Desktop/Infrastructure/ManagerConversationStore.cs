using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

public static class ManagerConversationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto", "manager-sessions", "conversations.json");

    public static IReadOnlyList<ManagerConversationItem> Load() => Load(StorePath);

    internal static IReadOnlyList<ManagerConversationItem> Load(string storePath)
    {
        try
        {
            if (!File.Exists(storePath)) return Array.Empty<ManagerConversationItem>();
            using var input = File.OpenRead(storePath);
            // Existing manually edited files may have a UTF-8/UTF-16/UTF-32
            // BOM. Preserve ReadAllText's encoding support for those files;
            // ordinary UTF-8 snapshots stay on the allocation-light stream path.
            Span<byte> prefix = stackalloc byte[4];
            var prefixLength = input.Read(prefix);
            input.Position = 0;
            List<Snapshot>? snapshots;
            if (prefixLength >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            {
                input.Position = 3;
                snapshots = JsonSerializer.Deserialize<List<Snapshot>>(input, Options);
            }
            else if (prefixLength >= 2 && ((prefix[0] == 0xFF && prefix[1] == 0xFE) || (prefix[0] == 0xFE && prefix[1] == 0xFF)) ||
                     prefixLength == 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF)
            {
                using var reader = new StreamReader(input);
                snapshots = JsonSerializer.Deserialize<List<Snapshot>>(reader.ReadToEnd(), Options);
            }
            else snapshots = JsonSerializer.Deserialize<List<Snapshot>>(input, Options);
            snapshots ??= new();
            return snapshots.Select(snapshot =>
            {
                var mainAgent = snapshot.MainAgent ?? new ManagerMainAgentBinding { Provider = "antigravity", SessionRef = snapshot.AntigravityConversationId };
                var codingAgent = snapshot.CodingAgent ?? new ManagerCodingAgentBinding { Provider = "pi", SessionRef = snapshot.PiSessionFile };
                if (string.Equals(mainAgent.Provider, "antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    var wasSdk = string.Equals(mainAgent.Transport, "sdk", StringComparison.OrdinalIgnoreCase);
                    if (wasSdk)
                    {
                        // The SDK session id is not a CLI conversation id.
                        // Preserve it for diagnostics, but only reuse an
                        // explicitly retained CLI id after the rollback.
                        mainAgent.SdkSessionRef ??= mainAgent.SessionRef;
                    }
                    else
                    {
                        mainAgent.LegacyCliConversationId ??= mainAgent.SessionRef ?? snapshot.AntigravityConversationId;
                    }
                    mainAgent.SessionRef = mainAgent.LegacyCliConversationId;
                    mainAgent.Transport = "cli";
                }
                var item = new ManagerConversationItem(snapshot.SessionId, string.IsNullOrWhiteSpace(snapshot.Title) ? "新对话" : snapshot.Title)
                {
                    WorkspacePath = snapshot.WorkspacePath ?? "", AntigravityConversationId = snapshot.AntigravityConversationId,
                    PiSessionFile = snapshot.PiSessionFile, UpdatedAt = snapshot.UpdatedAt,
                    DraftText = snapshot.DraftText ?? "",
                    CompanionProfile = snapshot.CompanionProfile is null ? new ManagerCompanionProfile() : new ManagerCompanionProfile
                    {
                        CharacterName = snapshot.CompanionProfile.CharacterName ?? "",
                        CharacterPrompt = snapshot.CompanionProfile.CharacterPrompt,
                        // A legacy user profile can still seed profile.md when
                        // an old conversation is resumed for the first time.
                        UserProfile = snapshot.CompanionProfile.UserProfile ?? "",
                    },
                    MainAgent = mainAgent,
                    CodingAgent = codingAgent
                };
                foreach (var message in snapshot.Messages ?? new())
                {
                    // A persisted transcript can outlive a process that was
                    // interrupted mid-turn. Never resurrect an active
                    // “正在思考” state after restart; keep the text as a
                    // completed, collapsible excerpt instead.
                    // Older coding builds streamed the structured CodeResult as
                    // ordinary chat text. Restore those entries in the same
                    // shape as the Pi workbench instead of showing raw JSON.
                    var restoredText = IsStructuredCodeResultText(message.Text)
                        ? message.CodeResult?.SummaryForUser ?? "代码任务已完成（历史结构化结果未保存）。"
                        : message.Text;
                    // Newer transcripts persist the complete Pi-style
                    // text/operation timeline. Older manager transcripts only
                    // had one text and one thinking field, so retain a small
                    // compatibility migration for those files.
                    var createdAt = message.CreatedAt ?? (snapshot.UpdatedAt == default ? DateTime.Now : snapshot.UpdatedAt);
                    var entry = new ManagerChatEntry(
                        message.Role,
                        message.Source,
                        "",
                        createdAt,
                        (message.Attachments ?? new()).Select((attachment, index) => new ManagerImageAttachment
                        {
                            AttachmentId = string.IsNullOrWhiteSpace(attachment.AttachmentId) ? Guid.NewGuid().ToString("N") : attachment.AttachmentId,
                            Path = attachment.Path,
                            DisplayName = attachment.DisplayName ?? System.IO.Path.GetFileName(attachment.Path),
                            MimeType = attachment.MimeType ?? "",
                            Order = attachment.Order ?? index,
                        }))
                    { CodeResult = message.CodeResult, ThinkingText = "", IsThinking = false, TaskId = message.TaskId, Runtime = RestoreRuntime(message.Runtime) };
                    if (!IsStructuredCodeResultText(message.Text) && message.Segments is { Count: > 0 })
                    {
                        entry.Text = restoredText;
                        foreach (var segment in message.Segments)
                        {
                            var segmentText = segment.Text ?? "";
                            if (message.Segments.Count == 1 && string.Equals(segmentText, entry.Text, StringComparison.Ordinal))
                                segmentText = entry.Text;
                            var restoredSegment = new ChatSegment(segment.Kind ?? "text", segmentText) { IsExpanded = segment.IsExpanded };
                            foreach (var operation in segment.Operations ?? new())
                            {
                                var restoredOperation = new ProcessItem(operation.Kind ?? "工具", operation.Title ?? "工具", operation.Status ?? "完成", operation.Details ?? "", operation.CallId, operation.CommandLine)
                                {
                                    IsExpanded = operation.IsExpanded
                                };
                                restoredSegment.Operations.Add(restoredOperation);
                            }
                            entry.Segments.Add(restoredSegment);
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(message.ThinkingText))
                        {
                            entry.AppendThinking(message.ThinkingText);
                            entry.CompleteThinking();
                        }
                        entry.Append(restoredText);
                    }
                    entry.ThinkingText = message.ThinkingText ?? "";
                    entry.IsThinking = false;
                    entry.ThinkingExpanded = false;
                    item.Messages.Add(entry);
                }
                return item;
            }).ToList();
        }
        catch { return Array.Empty<ManagerConversationItem>(); }
    }

    public static void Save(IEnumerable<ManagerConversationItem> conversations) => CreateSaveOperation(conversations)();

    // Capture on the UI thread. The returned operation owns a detached snapshot
    // and may serialize it in the background while the live models keep changing.
    public static Action CreateSaveOperation(IEnumerable<ManagerConversationItem> conversations) =>
        CreateSaveOperation(conversations, StorePath);

    internal static Action CreateSaveOperation(IEnumerable<ManagerConversationItem> conversations, string storePath)
    {
        var snapshots = conversations.OrderByDescending(item => item.ListTimestamp).ThenByDescending(item => item.UpdatedAt).Take(100).Select(item => new Snapshot
        {
            SessionId = item.SessionId, Title = item.Title, WorkspacePath = item.WorkspacePath,
            AntigravityConversationId = item.AntigravityConversationId, PiSessionFile = item.PiSessionFile, UpdatedAt = item.UpdatedAt,
            DraftText = Limit(item.DraftText),
            // New snapshots persist only the role card. UserProfile is kept
            // in the legacy DTO below solely for one-time migration.
            CompanionProfile = new CompanionProfileSnapshot { CharacterName = item.CompanionProfile.CharacterName, CharacterPrompt = item.CompanionProfile.CharacterPrompt },
            MainAgent = Clone(item.MainAgent), CodingAgent = Clone(item.CodingAgent),
            Messages = item.Messages.Select(message => new MessageSnapshot
            {
                Role = message.Role,
                Source = message.Source,
                TaskId = message.TaskId,
                CreatedAt = message.CreatedAt,
                Text = Limit(message.Text),
                ThinkingText = Limit(message.ThinkingText),
                IsThinking = message.IsThinking,
                CodeResult = Clone(message.CodeResult),
                Runtime = message.Runtime is null ? null : new RuntimeSnapshot
                {
                    TaskId = message.Runtime.TaskId,
                    TurnId = message.Runtime.TurnId,
                    StartedAt = message.Runtime.StartedAt.ToString("O"),
                    CompletedAt = message.Runtime.CompletedAt?.ToString("O"),
                    DurationMs = message.Runtime.DurationMs,
                    State = message.Runtime.State,
                },
                Attachments = message.Attachments.Select(attachment => new AttachmentSnapshot
                {
                    AttachmentId = attachment.AttachmentId,
                    Path = attachment.Path,
                    DisplayName = attachment.DisplayName,
                    MimeType = attachment.MimeType,
                    Order = attachment.Order,
                }).ToList(),
                Segments = message.Segments.Select(segment => new SegmentSnapshot
                {
                    Kind = segment.Kind,
                    Text = Limit(segment.Text),
                    IsExpanded = segment.IsExpanded,
                    Operations = segment.Operations.Select(operation => new OperationSnapshot
                    {
                        Kind = operation.Kind,
                        Title = operation.Title,
                        Status = operation.Status,
                        Details = Limit(operation.Details),
                        CallId = operation.CallId,
                        CommandLine = operation.CommandLine,
                        IsExpanded = operation.IsExpanded
                    }).ToList()
                }).ToList()
            }).ToList()
        }).ToList();
        return () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary)) JsonSerializer.Serialize(output, snapshots, Options);
                File.Move(temporary, storePath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        };
    }

    // These small mutable bindings/results are the only snapshot fields still
    // shared by reference with the UI. Keep their existing JSON shape intact.
    private static T? Clone<T>(T? value) where T : class => value is null ? null :
        JsonSerializer.SerializeToElement(value, Options).Deserialize<T>(Options);

    private static string Limit(string value) => value.Length <= 64_000 ? value : value[..64_000] + "\n…（内容已截断）";

    private static TaskRuntimeInfo? RestoreRuntime(RuntimeSnapshot? snapshot)
    {
        if (snapshot is null || !DateTimeOffset.TryParse(snapshot.StartedAt, out var startedAt)) return null;
        var runtime = new TaskRuntimeInfo(snapshot.TaskId, snapshot.TurnId, startedAt, snapshot.State ?? "completed");
        if (snapshot.CompletedAt is not null && DateTimeOffset.TryParse(snapshot.CompletedAt, out var completedAt))
            runtime.Mark(snapshot.State ?? "completed", completedAt, snapshot.DurationMs);
        else
        {
            // An unfinished snapshot must not come back as a live task. Freeze
            // its elapsed value at load time (graceful shutdown normally
            // persists the authoritative value; this also covers a crash).
            var frozenDuration = snapshot.DurationMs ?? Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            runtime.Mark(runtime.IsActive ? "cancelled" : snapshot.State ?? "completed", null, frozenDuration);
        }
        return runtime;
    }
    private static bool IsStructuredCodeResultText(string? value)
    {
        var text = value?.TrimStart() ?? "";
        return text.StartsWith("{", StringComparison.Ordinal) &&
               (text.Contains("\"summaryForUser\"", StringComparison.Ordinal) || text.Contains("\"status\"", StringComparison.Ordinal));
    }
    private sealed class Snapshot { public string SessionId { get; set; } = ""; public string Title { get; set; } = ""; public string? WorkspacePath { get; set; } public string? AntigravityConversationId { get; set; } public string? PiSessionFile { get; set; } public string? DraftText { get; set; } public CompanionProfileSnapshot? CompanionProfile { get; set; } public ManagerMainAgentBinding? MainAgent { get; set; } public ManagerCodingAgentBinding? CodingAgent { get; set; } public DateTime UpdatedAt { get; set; } public List<MessageSnapshot>? Messages { get; set; } }
    private sealed class CompanionProfileSnapshot
    {
        public string? CharacterName { get; set; }
        public string CharacterPrompt { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserProfile { get; set; }
    }
    private sealed class MessageSnapshot
    {
        public string Role { get; set; } = "";
        public string Source { get; set; } = "";
        public string? TaskId { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string Text { get; set; } = "";
        public string? ThinkingText { get; set; }
        public bool IsThinking { get; set; }
        public ManagerCodeResult? CodeResult { get; set; }
        public RuntimeSnapshot? Runtime { get; set; }
        public List<AttachmentSnapshot>? Attachments { get; set; }
        public List<SegmentSnapshot>? Segments { get; set; }
    }

    private sealed class RuntimeSnapshot
    {
        public string? TaskId { get; set; }
        public string? TurnId { get; set; }
        public string StartedAt { get; set; } = "";
        public string? CompletedAt { get; set; }
        public long? DurationMs { get; set; }
        public string? State { get; set; }
    }

    private sealed class AttachmentSnapshot
    {
        public string? AttachmentId { get; set; }
        public string Path { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? MimeType { get; set; }
        public int? Order { get; set; }
    }

    private sealed class SegmentSnapshot
    {
        public string Kind { get; set; } = "text";
        public string Text { get; set; } = "";
        public bool IsExpanded { get; set; }
        public List<OperationSnapshot>? Operations { get; set; }
    }

    private sealed class OperationSnapshot
    {
        public string Kind { get; set; } = "工具";
        public string Title { get; set; } = "工具";
        public string Status { get; set; } = "完成";
        public string Details { get; set; } = "";
        public string? CallId { get; set; }
        public string CommandLine { get; set; } = "";
        public bool IsExpanded { get; set; }
    }
}
