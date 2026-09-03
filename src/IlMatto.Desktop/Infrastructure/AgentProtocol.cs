using System.Text.Json;
using System.Text.Json.Serialization;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

public sealed record StartSessionMessage(
    string SessionId,
    string WorkspacePath,
    string BaseUrl,
    string ModelId,
    string? ApiKey,
    bool AutoApproveSafeCommands = false,
    bool AutoApproveGitOperations = false,
    string? SessionFile = null,
    List<RestoreTranscriptMessage>? RestoreTranscript = null,
    [property: JsonPropertyName("type")] string Type = "start_session");

public sealed class RestoreTranscriptMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "user";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

public sealed record DeleteSessionMessage(
    string SessionId,
    string? SessionFile = null,
    [property: JsonPropertyName("type")] string Type = "delete_session");

public sealed record SendMessage(
    string SessionId,
    string Text,
    [property: JsonPropertyName("type")] string Type = "send_message");

public sealed record ApproveToolCallMessage(
    string SessionId,
    string CallId,
    bool Approved,
    [property: JsonPropertyName("type")] string Type = "approve_tool_call");

public sealed record CancelMessage(
    string SessionId,
    [property: JsonPropertyName("type")] string Type = "cancel");

public sealed record ShutdownMessage(
    string? SessionId = null,
    [property: JsonPropertyName("type")] string Type = "shutdown");

public sealed record GetCommandsMessage(
    string SessionId,
    [property: JsonPropertyName("type")] string Type = "get_commands");

public sealed record GetGitOverviewMessage(
    string SessionId,
    [property: JsonPropertyName("type")] string Type = "get_git_overview");

public sealed record GetGitDiffMessage(
    string SessionId,
    string Scope,
    string? Path = null,
    [property: JsonPropertyName("type")] string Type = "get_git_diff");

public sealed class SlashCommandInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
}

public sealed class HostEvent
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("callId")] public string? CallId { get; init; }
    [JsonPropertyName("tool")] public string? Tool { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("details")] public string? Details { get; init; }
    [JsonPropertyName("diff")] public string? Diff { get; init; }
    [JsonPropertyName("output")] public string? Output { get; init; }
    [JsonPropertyName("ok")] public bool? Ok { get; init; }
    [JsonPropertyName("autoApproved")] public bool? AutoApproved { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; init; }
    [JsonPropertyName("restored")] public bool? Restored { get; init; }
    [JsonPropertyName("legacyRestored")] public bool? LegacyRestored { get; init; }
    [JsonPropertyName("workspacePath")] public string? WorkspacePath { get; init; }
    [JsonPropertyName("commands")] public List<SlashCommandInfo>? Commands { get; init; }
    [JsonPropertyName("command")] public string? Command { get; init; }
    [JsonPropertyName("overview")] public GitOverview? Overview { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("truncated")] public bool? Truncated { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("tokensBefore")] public long? TokensBefore { get; init; }
    [JsonPropertyName("aborted")] public bool? Aborted { get; init; }
    [JsonPropertyName("willRetry")] public bool? WillRetry { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("contextTokens")] public long? ContextTokens { get; init; }
    [JsonPropertyName("contextWindow")] public long? ContextWindow { get; init; }
    [JsonPropertyName("contextPercent")] public double? ContextPercent { get; init; }
    [JsonPropertyName("cacheReadTokens")] public long? CacheReadTokens { get; init; }
    [JsonPropertyName("cacheWriteTokens")] public long? CacheWriteTokens { get; init; }
    [JsonPropertyName("cacheStatsAvailable")] public bool? CacheStatsAvailable { get; init; }
}

public static class JsonWire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
