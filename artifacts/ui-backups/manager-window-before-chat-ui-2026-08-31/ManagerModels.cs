using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IlMatto.Desktop.Models;

public partial class ManagerConversationItem : ObservableObject
{
    public ManagerConversationItem(string sessionId, string title) { SessionId = sessionId; Title = title; }
    public string SessionId { get; }
    [ObservableProperty] private string title;
    [ObservableProperty] private string workspacePath = "";
    [ObservableProperty] private string? antigravityConversationId;
    [ObservableProperty] private string? piSessionFile;
    [ObservableProperty] private ManagerMainAgentBinding? mainAgent;
    [ObservableProperty] private ManagerCodingAgentBinding? codingAgent;
    [ObservableProperty] private ManagerCompanionProfile companionProfile = new();
    [ObservableProperty] private DateTime updatedAt = DateTime.Now;
    public ObservableCollection<ManagerChatEntry> Messages { get; } = new();
    public string ProviderLabel => $"陪伴 Agent（{MainAgent?.DisplayName ?? "Antigravity"}） → {CodingAgent?.DisplayName ?? "Pi"}";
    partial void OnMainAgentChanged(ManagerMainAgentBinding? value) => OnPropertyChanged(nameof(ProviderLabel));
    partial void OnCodingAgentChanged(ManagerCodingAgentBinding? value) => OnPropertyChanged(nameof(ProviderLabel));
}

public sealed class ManagerCompanionProfile
{
    public string CharacterPrompt { get; set; } = "你是一个温和、自然、尊重边界的陪伴型 RP 角色；优先理解用户的情绪，用户没有明确求助时不主动说教或提供解决方案。";
    public string UserProfile { get; set; } = "";
    public string RelationshipSummary { get; set; } = "你们刚开始建立关系，不假设未记录的共同经历。";

    public ManagerCompanionProfile Clone() => new()
    {
        CharacterPrompt = CharacterPrompt,
        UserProfile = UserProfile,
        RelationshipSummary = RelationshipSummary,
    };
}

public sealed class ManagerMainAgentBinding
{
    public string Provider { get; set; } = "antigravity";
    public string CliPath { get; set; } = "agy";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "medium";
    public int TimeoutSeconds { get; set; } = 120;
    public string BaseUrl { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public string? SessionRef { get; set; }
    public string DisplayName => Provider == "openai_compatible" ? "API Manager" : "Antigravity";
}

public sealed class ManagerCodingAgentBinding
{
    public string Provider { get; set; } = "pi";
    public string CliPath { get; set; } = "codex";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "medium";
    public string BaseUrl { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public string? SessionRef { get; set; }
    public bool AutoApproveSafeCommands { get; set; }
    public bool AutoApproveGitOperations { get; set; }
    public string DisplayName => Provider == "codex" ? "Codex" : "Pi";
}

public partial class ManagerChatEntry : ObservableObject
{
    public ManagerChatEntry(string role, string source, string text)
    {
        Role = role;
        Source = source;
        Text = text;
        if (!string.IsNullOrEmpty(text)) Segments.Add(new ChatSegment("text", text));
    }
    public string Role { get; }
    public string Source { get; }
    [ObservableProperty] private string text;
    /// <summary>
    /// The same text/operation timeline used by the Pi workbench. Keeping the
    /// timeline on the entry (rather than a single thinking field) means a
    /// thought or tool call stays exactly where it happened in the streamed
    /// response.
    /// </summary>
    public ObservableCollection<ChatSegment> Segments { get; } = new();
    [ObservableProperty] private string thinkingText = "";
    [ObservableProperty] private bool isThinking;
    [ObservableProperty] private bool thinkingExpanded;
    [ObservableProperty] private ManagerCodeResult? codeResult;
    public bool HasThinking => !string.IsNullOrWhiteSpace(ThinkingText);
    public string ThinkingDisplayTitle
    {
        get
        {
            var excerpt = Excerpt(ThinkingText);
            if (IsThinking) return string.IsNullOrWhiteSpace(excerpt) ? "正在思考" : $"正在思考 {excerpt}";
            return string.IsNullOrWhiteSpace(excerpt) ? "" : excerpt;
        }
    }
    public bool IsPi => Source == "pi";
    public bool IsCodingAgent => Source is "pi" or "codex";

    public void Append(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Text += value;
        var last = Segments.LastOrDefault();
        if (last?.IsText == true) last.Text += value;
        else Segments.Add(new ChatSegment("text", value));
    }

    /// <summary>Replace streamed text while keeping the timeline binding in sync.</summary>
    public void ReplaceText(string value)
    {
        Text = value ?? "";
        var textSegments = Segments.Where(segment => segment.IsText).ToList();
        if (textSegments.Count == 0)
        {
            if (!string.IsNullOrEmpty(Text)) Segments.Add(new ChatSegment("text", Text));
            return;
        }
        textSegments[0].Text = Text;
        foreach (var extra in textSegments.Skip(1)) extra.Text = "";
    }

    public ChatSegment AppendOperation(ProcessItem operation)
    {
        var group = Segments.LastOrDefault();
        if (group?.IsOperations != true)
        {
            group = new ChatSegment("operations") { IsExpanded = operation.IsThinking };
            Segments.Add(group);
        }
        group.Operations.Add(operation);
        if (operation.IsThinking) group.IsExpanded = true;
        return group;
    }

    public ProcessItem? FindOperation(string? callId)
    {
        if (string.IsNullOrWhiteSpace(callId)) return null;
        return Segments.SelectMany(segment => segment.Operations).LastOrDefault(item => item.CallId == callId);
    }

    public void AppendThinking(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (string.Equals(ThinkingText, "正在分析…", StringComparison.Ordinal) && !string.Equals(value, "正在分析…", StringComparison.Ordinal)) ThinkingText = "";
        ThinkingText += value;
        IsThinking = true;
        var group = Segments.LastOrDefault();
        var operation = group?.IsOperations == true
            ? group.Operations.LastOrDefault(item => item.IsThinking && item.Status == "进行中")
            : null;
        if (operation is null)
        {
            operation = new ProcessItem("思路", "正在思考", "进行中");
            group = AppendOperation(operation);
        }
        operation.Details += value;
        operation.IsExpanded = true;
        if (group is not null) group.IsExpanded = true;
        ThinkingExpanded = true;
        OnPropertyChanged(nameof(HasThinking));
        OnPropertyChanged(nameof(ThinkingDisplayTitle));
    }

    public void CompleteThinking(string status = "完成")
    {
        if (string.Equals(ThinkingText, "正在分析…", StringComparison.Ordinal)) ThinkingText = "";
        IsThinking = false;
        ThinkingExpanded = false;
        var operation = Segments.SelectMany(segment => segment.Operations)
            .LastOrDefault(item => item.IsThinking && item.Status == "进行中");
        if (operation is not null)
        {
            operation.Status = status;
            operation.IsExpanded = false;
            var group = Segments.LastOrDefault(segment => segment.Operations.Contains(operation));
            if (group is not null && group.LatestOperation == operation) group.IsExpanded = false;
        }
        OnPropertyChanged(nameof(HasThinking));
        OnPropertyChanged(nameof(ThinkingDisplayTitle));
    }

    partial void OnThinkingTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinking));
        OnPropertyChanged(nameof(ThinkingDisplayTitle));
    }

    partial void OnIsThinkingChanged(bool value) => OnPropertyChanged(nameof(ThinkingDisplayTitle));

    private static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = string.Join(" ", value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0));
        const int maxLength = 96;
        return normalized.Length > maxLength ? normalized[..maxLength].TrimEnd() + "…" : normalized;
    }
}

public sealed class ManagerCodeResult
{
    public string Status { get; set; } = "";
    public string SummaryForUser { get; set; } = "";
    public List<ManagerTechnicalDecision> TechnicalDecisions { get; set; } = new();
    public List<ManagerFileChange> FilesChanged { get; set; } = new();
    public List<ManagerValidation> Validation { get; set; } = new();
    public List<string> Questions { get; set; } = new();
    public bool NeedsUserDecision { get; set; }
    public string StatusLabel => Status switch { "completed" => "已完成", "blocked" => "需要你的决定", "cancelled" => "已取消", _ => "失败" };
}

public sealed class ManagerTechnicalDecision { public string Decision { get; set; } = ""; public string Reason { get; set; } = ""; }
public sealed class ManagerFileChange { public string Path { get; set; } = ""; public int Additions { get; set; } public int Deletions { get; set; } }
public sealed class ManagerValidation { public string Command { get; set; } = ""; public string Status { get; set; } = ""; public string Summary { get; set; } = ""; }

public partial class ManagerActivity : ObservableObject
{
    public ManagerActivity(string kind, string title, string status, string? callId = null) { Kind = kind; Title = title; Status = status; CallId = callId; }
    public string Kind { get; }
    public string? CallId { get; }
    [ObservableProperty] private string title;
    [ObservableProperty] private string status;
    [ObservableProperty] private string details = "";
}
