using System.IO;
using System.Text.Json;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

public static class ManagerConversationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto", "manager-sessions", "conversations.json");

    public static IReadOnlyList<ManagerConversationItem> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return Array.Empty<ManagerConversationItem>();
            var snapshots = JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(StorePath), Options) ?? new();
            return snapshots.Select(snapshot =>
            {
                var item = new ManagerConversationItem(snapshot.SessionId, string.IsNullOrWhiteSpace(snapshot.Title) ? "新对话" : snapshot.Title)
                {
                    WorkspacePath = snapshot.WorkspacePath ?? "", AntigravityConversationId = snapshot.AntigravityConversationId,
                    PiSessionFile = snapshot.PiSessionFile, UpdatedAt = snapshot.UpdatedAt,
                    CompanionProfile = snapshot.CompanionProfile ?? new ManagerCompanionProfile(),
                    MainAgent = snapshot.MainAgent ?? new ManagerMainAgentBinding { Provider = "antigravity", SessionRef = snapshot.AntigravityConversationId },
                    CodingAgent = snapshot.CodingAgent ?? new ManagerCodingAgentBinding { Provider = "pi", SessionRef = snapshot.PiSessionFile }
                };
                foreach (var message in snapshot.Messages ?? new())
                {
                    // A persisted transcript can outlive a process that was
                    // interrupted mid-turn. Never resurrect an active
                    // “正在思考” state after restart; keep the text as a
                    // completed, collapsible excerpt instead.
                    // Older Codex builds streamed the structured CodeResult as
                    // ordinary chat text. Restore those entries in the same
                    // shape as the Pi workbench instead of showing raw JSON.
                    var restoredText = IsStructuredCodeResultText(message.Text)
                        ? message.CodeResult?.SummaryForUser ?? "代码任务已完成（历史结构化结果未保存）。"
                        : message.Text;
                    // Newer transcripts persist the complete Pi-style
                    // text/operation timeline. Older manager transcripts only
                    // had one text and one thinking field, so retain a small
                    // compatibility migration for those files.
                    var entry = new ManagerChatEntry(message.Role, message.Source, "") { CodeResult = message.CodeResult, ThinkingText = "", IsThinking = false };
                    if (!IsStructuredCodeResultText(message.Text) && message.Segments is { Count: > 0 })
                    {
                        entry.Text = restoredText;
                        foreach (var segment in message.Segments)
                        {
                            var restoredSegment = new ChatSegment(segment.Kind ?? "text", segment.Text ?? "") { IsExpanded = segment.IsExpanded };
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

    public static void Save(IEnumerable<ManagerConversationItem> conversations)
    {
        var directory = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(directory);
        var snapshots = conversations.OrderByDescending(item => item.UpdatedAt).Take(100).Select(item => new Snapshot
        {
            SessionId = item.SessionId, Title = item.Title, WorkspacePath = item.WorkspacePath,
            AntigravityConversationId = item.AntigravityConversationId, PiSessionFile = item.PiSessionFile, UpdatedAt = item.UpdatedAt,
            CompanionProfile = item.CompanionProfile,
            MainAgent = item.MainAgent, CodingAgent = item.CodingAgent,
            Messages = item.Messages.Select(message => new MessageSnapshot
            {
                Role = message.Role,
                Source = message.Source,
                Text = Limit(message.Text),
                ThinkingText = Limit(message.ThinkingText),
                IsThinking = message.IsThinking,
                CodeResult = message.CodeResult,
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
        var temporary = StorePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshots, Options));
        File.Move(temporary, StorePath, true);
    }

    private static string Limit(string value) => value.Length <= 64_000 ? value : value[..64_000] + "\n…（内容已截断）";
    private static bool IsStructuredCodeResultText(string? value)
    {
        var text = value?.TrimStart() ?? "";
        return text.StartsWith("{", StringComparison.Ordinal) &&
               (text.Contains("\"summaryForUser\"", StringComparison.Ordinal) || text.Contains("\"status\"", StringComparison.Ordinal));
    }
    private sealed class Snapshot { public string SessionId { get; set; } = ""; public string Title { get; set; } = ""; public string? WorkspacePath { get; set; } public string? AntigravityConversationId { get; set; } public string? PiSessionFile { get; set; } public ManagerCompanionProfile? CompanionProfile { get; set; } public ManagerMainAgentBinding? MainAgent { get; set; } public ManagerCodingAgentBinding? CodingAgent { get; set; } public DateTime UpdatedAt { get; set; } public List<MessageSnapshot>? Messages { get; set; } }
    private sealed class MessageSnapshot
    {
        public string Role { get; set; } = "";
        public string Source { get; set; } = "";
        public string Text { get; set; } = "";
        public string? ThinkingText { get; set; }
        public bool IsThinking { get; set; }
        public ManagerCodeResult? CodeResult { get; set; }
        public List<SegmentSnapshot>? Segments { get; set; }
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
