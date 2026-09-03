using System.Text.Json;
using System.Text.Json.Serialization;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

public sealed class ManagerMainAgentConfig
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "antigravity";
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("executable")] public string? Executable { get; set; }
    [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }
    [JsonPropertyName("sdkSessionRef")] public string? SdkSessionRef { get; set; }
    [JsonPropertyName("legacyCliConversationId")] public string? LegacyCliConversationId { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("effort")] public string? Effort { get; set; }
    [JsonPropertyName("timeoutSeconds")] public int? TimeoutSeconds { get; set; }
    [JsonPropertyName("toolPermission")] public string? ToolPermission { get; set; }
    [JsonPropertyName("terminalSandbox")] public bool? TerminalSandbox { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("modelId")] public string? ModelId { get; set; }
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; set; }
}

public sealed class ManagerCodingAgentConfig
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "pi";
    [JsonPropertyName("executable")] public string? Executable { get; set; }
    [JsonPropertyName("threadId")] public string? ThreadId { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("effort")] public string? Effort { get; set; }
    [JsonPropertyName("approvalPolicy")] public string? ApprovalPolicy { get; set; }
    [JsonPropertyName("sandboxMode")] public string? SandboxMode { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("modelId")] public string? ModelId { get; set; }
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; set; }
    [JsonPropertyName("autoApproveSafeCommands")] public bool AutoApproveSafeCommands { get; set; }
    [JsonPropertyName("autoApproveGitOperations")] public bool AutoApproveGitOperations { get; set; }
    [JsonPropertyName("executionPolicy")] public string? ExecutionPolicy { get; set; }
    [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }
}

public sealed record ManagerImageAttachmentMessage(
    string Path,
    string? DisplayName = null,
    string? MimeType = null,
    [property: JsonPropertyName("type")] string Type = "image",
    string? AttachmentId = null,
    int? Order = null);

public sealed record ManagerCompanionHistoryMessage(
    string Role,
    string Text);

public sealed record StartManagerSessionMessage(
    string SessionId, string WorkspacePath, ManagerMainAgentConfig MainAgent, ManagerCodingAgentConfig CodingAgent,
    bool AwaitingCodingDecision = false,
    [property: JsonPropertyName("companionProfile")] ManagerCompanionProfile? CompanionProfile = null,
    [property: JsonPropertyName("conversationHistory")] IReadOnlyList<ManagerCompanionHistoryMessage>? ConversationHistory = null,
    [property: JsonPropertyName("executorProfiles")] Dictionary<string, ManagerCodingAgentConfig>? ExecutorProfiles = null,
    [property: JsonPropertyName("type")] string Type = "start_manager_session");

public sealed record SendManagerMessage(string SessionId, string Text, IReadOnlyList<ManagerImageAttachmentMessage>? Attachments = null, string? Executor = null, [property: JsonPropertyName("draftId")] string? DraftId = null, [property: JsonPropertyName("type")] string Type = "send_manager_message");
public sealed record ApproveCodingToolMessage(string SessionId, string CallId, bool Approved, [property: JsonPropertyName("type")] string Type = "approve_coding_tool");
public sealed record ResolveCodingInteractionMessage(string SessionId, string RequestId, bool Approved, Dictionary<string, object?>? Values = null, [property: JsonPropertyName("type")] string Type = "resolve_coding_interaction");
public sealed record CancelManagerTurnMessage(string SessionId, [property: JsonPropertyName("type")] string Type = "cancel_manager_turn");
public sealed record RequestVerificationMessage(string SessionId, string TaskId, [property: JsonPropertyName("type")] string Type = "request_verification");
public sealed record ProbeCodexMessage(string SessionId, string Executable, string WorkspacePath, [property: JsonPropertyName("type")] string Type = "probe_codex");
public sealed record StartCodexLoginMessage(string SessionId, string Executable, string WorkspacePath, [property: JsonPropertyName("type")] string Type = "start_codex_login");
public sealed record ListAgentModelsMessage(string SessionId, string Provider, [property: JsonPropertyName("type")] string Type = "list_agent_models");
public sealed record DeleteManagerSessionMessage(string SessionId, string WorkspacePath, ManagerMainAgentConfig? MainAgent, ManagerCodingAgentConfig? CodingAgent, string? PiSessionFile, string? CoordinatorSessionFile, string? CodexThreadId, [property: JsonPropertyName("type")] string Type = "delete_manager_session");
public sealed record ManagerShutdownMessage(string? SessionId = null, [property: JsonPropertyName("type")] string Type = "shutdown");

public sealed class ManagerHostEvent
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("taskId")] public string? TaskId { get; init; }
    [JsonPropertyName("callId")] public string? CallId { get; init; }
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("mainProvider")] public string? MainProvider { get; init; }
    [JsonPropertyName("codingProvider")] public string? CodingProvider { get; init; }
    [JsonPropertyName("layer")] public string? Layer { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("draftId")] public string? DraftId { get; init; }
    [JsonPropertyName("workspacePath")] public string? WorkspacePath { get; init; }
    [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("action")] public string? Action { get; init; }
    [JsonPropertyName("final")] public bool? Final { get; init; }
    [JsonPropertyName("tool")] public string? Tool { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("details")] public string? Details { get; init; }
    [JsonPropertyName("diff")] public string? Diff { get; init; }
    [JsonPropertyName("output")] public string? Output { get; init; }
    [JsonPropertyName("command")] public string? Command { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("fields")] public JsonElement? Fields { get; init; }
    [JsonPropertyName("ok")] public bool? Ok { get; init; }
    [JsonPropertyName("autoApproved")] public bool? AutoApproved { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("operation")] public string? Operation { get; init; }
    [JsonPropertyName("event")] public ManagerTaskTraceEvent? TraceEvent { get; init; }
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("conversationId")] public string? ConversationId { get; init; }
    [JsonPropertyName("attachmentPath")] public string? AttachmentPath { get; init; }
    [JsonPropertyName("executable")] public string? Executable { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("effort")] public string? Effort { get; init; }
    [JsonPropertyName("agentName")] public string? AgentName { get; init; }
    [JsonPropertyName("logPath")] public string? LogPath { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("policy")] public string? Policy { get; init; }
    [JsonPropertyName("available")] public bool? Available { get; init; }
    [JsonPropertyName("authenticated")] public bool? Authenticated { get; init; }
    [JsonPropertyName("antigravityAvailable")] public bool? AntigravityAvailable { get; init; }
    [JsonPropertyName("mainSessionRef")] public string? MainSessionRef { get; init; }
    [JsonPropertyName("codingSessionRef")] public string? CodingSessionRef { get; init; }
    [JsonPropertyName("agyConversationId")] public string? AgyConversationId { get; init; }
    [JsonPropertyName("antigravityTransport")] public string? AntigravityTransport { get; init; }
    [JsonPropertyName("piSessionFile")] public string? PiSessionFile { get; init; }
    [JsonPropertyName("result")] public ManagerCodeResult? Result { get; init; }
    [JsonPropertyName("contextTokens")] public long? ContextTokens { get; init; }
    [JsonPropertyName("contextWindow")] public long? ContextWindow { get; init; }
    [JsonPropertyName("cacheReadTokens")] public long? CacheReadTokens { get; init; }
    [JsonPropertyName("antigravityCacheReadTokens")] public long? AntigravityCacheReadTokens { get; init; }
    [JsonPropertyName("models")] public List<CodexModelInfo>? Models { get; init; }
    [JsonPropertyName("toolPermission")] public string? ToolPermission { get; init; }
    [JsonPropertyName("terminalSandbox")] public bool? TerminalSandbox { get; init; }
}

public sealed class ManagerTaskTraceEvent
{
    [JsonPropertyName("taskId")] public string TaskId { get; init; } = "";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("timestamp")] public string Timestamp { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("details")] public JsonElement? Details { get; init; }
}

public sealed class CodexModelInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("efforts")] public List<string> Efforts { get; init; } = new();
}
