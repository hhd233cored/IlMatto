using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop;

public partial class ManagerViewModel : ObservableObject, IAsyncDisposable
{
    private const string LegacyPiCredentialTarget = "IlMatto/OpenAICompatible/default";
    private ManagerHostProcess? _host;
    private ManagerPipeClient? _pipe;
    private ManagerChatEntry? _streamingManager;
    private ManagerChatEntry? _streamingCoding;
    private bool _initialized;
    private TaskCompletionSource<ManagerHostEvent>? _codexProbeCompletion;
    private TaskCompletionSource<ManagerHostEvent>? _codexLoginCompletion;

    public ManagerViewModel()
    {
        var settings = SettingsStore.Load();
        LoadSettings(settings);
        PiApiKey = CredentialStore.Read(PiCredentialId) ?? CredentialStore.Read(LegacyPiCredentialTarget) ?? "";
        MainApiKey = CredentialStore.Read(MainApiCredentialId) ?? "";
        foreach (var conversation in ManagerConversationStore.Load())
        {
            NormalizeLegacyBinding(conversation);
            Conversations.Add(conversation);
        }
        if (Conversations.Count == 0) Conversations.Add(CreateConversation());
        SelectedConversation = Conversations.OrderByDescending(item => item.UpdatedAt).First();
    }

    public ObservableCollection<ManagerConversationItem> Conversations { get; } = new();
    public ObservableCollection<ManagerActivity> Activities { get; } = new();
    public ObservableCollection<ApprovalRequest> PendingApprovals { get; } = new();
    public ObservableCollection<ManagerChatEntry> ChatEntries => SelectedConversation?.Messages ?? _emptyMessages;
    private readonly ObservableCollection<ManagerChatEntry> _emptyMessages = new();

    [ObservableProperty] private ManagerConversationItem? selectedConversation;
    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private string interactionResponseText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string managerStatus = "准备就绪";
    [ObservableProperty] private bool mainProviderAvailable;
    [ObservableProperty] private bool mainProviderAuthenticated;
    [ObservableProperty] private string mainProviderVersion = "";
    [ObservableProperty] private string mainProviderStatusDetail = "";
    [ObservableProperty] private string codingProviderStatusDetail = "";
    [ObservableProperty] private long? managerCacheReadTokens;
    [ObservableProperty] private long? managerContextTokens;
    [ObservableProperty] private long? managerContextWindow;

    [ObservableProperty] private string defaultMainAgentProvider = "antigravity";
    [ObservableProperty] private string defaultCodingAgentProvider = "pi";
    [ObservableProperty] private string baseUrl = "";
    [ObservableProperty] private string modelId = "";
    [ObservableProperty] private string piApiKey = "";
    [ObservableProperty] private string piCredentialId = LegacyPiCredentialTarget;
    [ObservableProperty] private string mainApiBaseUrl = "";
    [ObservableProperty] private string mainApiModelId = "";
    [ObservableProperty] private string mainApiKey = "";
    [ObservableProperty] private string mainApiCredentialId = "IlMatto/MainAgent/OpenAICompatible/default";
    [ObservableProperty] private int mainApiTimeoutSeconds = 120;
    [ObservableProperty] private string workspacePath = "";
    [ObservableProperty] private bool autoApproveSafeCommands;
    [ObservableProperty] private bool autoApproveGitOperations;
    [ObservableProperty] private string antigravityCliPath = "";
    [ObservableProperty] private string antigravityModel = "";
    [ObservableProperty] private string antigravityEffort = "medium";
    [ObservableProperty] private int antigravityTimeoutSeconds = 120;
    [ObservableProperty] private string codexCliPath = "codex";
    [ObservableProperty] private string codexModel = "";
    [ObservableProperty] private string codexEffort = "medium";
    [ObservableProperty] private string companionCharacterPrompt = new ManagerCompanionProfile().CharacterPrompt;
    [ObservableProperty] private string companionUserProfile = "";
    [ObservableProperty] private string companionRelationshipSummary = new ManagerCompanionProfile().RelationshipSummary;

    public string ApiKey { get => PiApiKey; set => PiApiKey = value; }
    public ApprovalRequest? CurrentApproval => PendingApprovals.FirstOrDefault();
    public bool HasApproval => CurrentApproval is not null;
    public string PendingApprovalCountLabel => PendingApprovals.Count > 1 ? $"另有 {PendingApprovals.Count - 1} 项等待确认" : "";
    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(InputText);
    public string MainConnectionLabel => !MainProviderAvailable ? $"{CurrentMainDisplayName} 不可用" : MainProviderAuthenticated ? $"{CurrentMainDisplayName} 已连接 {MainProviderVersion}".Trim() : $"{CurrentMainDisplayName} 等待认证";
    public string ManagerCacheLabel => ManagerCacheReadTokens is long tokens ? $"缓存读取：{tokens:N0} tokens" : "缓存统计：服务未提供";
    public string ManagerContextLabel => ManagerContextTokens is long used ? $"上下文：{used:N0}{(ManagerContextWindow is long window ? $" / {window:N0}" : "")} tokens" : "上下文统计：服务未提供";
    public string WorkspaceLabel => string.IsNullOrWhiteSpace(SelectedConversation?.WorkspacePath) ? "未选择工作区" : SelectedConversation.WorkspacePath;
    public string CurrentMainDisplayName => $"陪伴 Agent（{SelectedConversation?.MainAgent?.DisplayName ?? "Antigravity"}）";
    public string CurrentCodingDisplayName => SelectedConversation?.CodingAgent?.DisplayName ?? "Pi";
    public string CurrentProviderLabel => SelectedConversation?.ProviderLabel ?? "Antigravity → Pi";

    public event Action? SettingsRequested;
    public event Action? OpenPiWorkbenchRequested;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await ActivateSelectedConversationAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var conversation = SelectedConversation;
        var text = InputText.Trim();
        if (conversation is null || string.IsNullOrWhiteSpace(text)) return;
        InputText = "";
        if (conversation.Title == "新对话") conversation.Title = text.Length > 32 ? text[..32] + "…" : text;
        conversation.Messages.Add(new ManagerChatEntry("你", "user", text));
        conversation.UpdatedAt = DateTime.Now;
        _streamingManager = null; _streamingCoding = null;
        IsBusy = true; ManagerStatus = $"{CurrentMainDisplayName} 正在协调";
        Save();
        try { await EnsurePipeAsync(); await _pipe!.SendAsync(new SendManagerMessage(conversation.SessionId, text)); }
        catch (Exception exception) { AddSystemMessage($"无法发送：{exception.Message}"); IsBusy = false; ManagerStatus = "错误"; }
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (IsBusy) await CancelAsync();
        var conversation = CreateConversation();
        Conversations.Insert(0, conversation); SelectedConversation = conversation; Save();
        await ActivateSelectedConversationAsync();
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ManagerConversationItem? conversation)
    {
        if (conversation is null) return;
        try
        {
            if (_pipe is not null)
                await _pipe.SendAsync(new DeleteManagerSessionMessage(
                    conversation.SessionId, conversation.WorkspacePath, BuildMainConfig(conversation, false), BuildCodingConfig(conversation, false),
                    conversation.CodingAgent?.Provider == "pi" ? conversation.CodingAgent.SessionRef : conversation.PiSessionFile,
                    conversation.MainAgent?.Provider == "openai_compatible" ? conversation.MainAgent.SessionRef : null,
                    conversation.CodingAgent?.Provider == "codex" ? conversation.CodingAgent.SessionRef : null));
        }
        catch (Exception exception) { AddSystemMessage($"删除 Provider 会话失败：{exception.Message}"); }
        var mainCredential = conversation.MainAgent?.CredentialId;
        var codingCredential = conversation.CodingAgent?.CredentialId;
        var index = Conversations.IndexOf(conversation); Conversations.Remove(conversation);
        if (Conversations.Count == 0) Conversations.Add(CreateConversation());
        if (ReferenceEquals(SelectedConversation, conversation)) SelectedConversation = Conversations[Math.Clamp(index, 0, Conversations.Count - 1)];
        CleanupCredentialIfUnused(mainCredential); CleanupCredentialIfUnused(codingCredential);
        Save(); await ActivateSelectedConversationAsync();
    }

    [RelayCommand] private async Task ApproveAsync() => await SubmitApprovalAsync(true);
    [RelayCommand] private async Task RejectAsync() => await SubmitApprovalAsync(false);

    private async Task SubmitApprovalAsync(bool approved)
    {
        var request = CurrentApproval;
        if (request is null || _pipe is null) return;
        Dictionary<string, object?>? values = null;
        if (approved && request.RequiresInput)
        {
            try
            {
                if (InteractionResponseText.TrimStart().StartsWith('{')) values = JsonSerializer.Deserialize<Dictionary<string, object?>>(InteractionResponseText);
                else values = new Dictionary<string, object?> { ["answer"] = InteractionResponseText };
            }
            catch (JsonException exception) { AddSystemMessage($"交互输入不是有效 JSON：{exception.Message}"); return; }
        }
        if (approved && request.Kind == "mcp_url" && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        PendingApprovals.Remove(request); InteractionResponseText = ""; NotifyApprovalChanged();
        if (request.Provider == "codex") await _pipe.SendAsync(new ResolveCodingInteractionMessage(request.SessionId, request.CallId, approved, values));
        else await _pipe.SendAsync(new ApproveCodingToolMessage(request.SessionId, request.CallId, approved));
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (SelectedConversation is not null && _pipe is not null) await _pipe.SendAsync(new CancelManagerTurnMessage(SelectedConversation.SessionId));
        IsBusy = false; ManagerStatus = "已取消";
    }

    [RelayCommand] private void OpenSettings() => SettingsRequested?.Invoke();
    [RelayCommand] private void OpenPiWorkbench() => OpenPiWorkbenchRequested?.Invoke();

    public void ApplySettings(AppSettings settings, string piApiKey, string mainApiKey)
    {
        if (ProviderProfileChanged(BaseUrl, ModelId, PiApiKey, settings.BaseUrl, settings.ModelId, piApiKey))
            settings.PiCredentialId = $"IlMatto/CodingAgent/Pi/{Guid.NewGuid():N}";
        else settings.PiCredentialId = PiCredentialId;
        if (ProviderProfileChanged(MainApiBaseUrl, MainApiModelId, MainApiKey, settings.MainApiBaseUrl, settings.MainApiModelId, mainApiKey))
            settings.MainApiCredentialId = $"IlMatto/MainAgent/OpenAICompatible/{Guid.NewGuid():N}";
        else settings.MainApiCredentialId = MainApiCredentialId;

        if (string.IsNullOrWhiteSpace(piApiKey)) CredentialStore.Delete(settings.PiCredentialId); else CredentialStore.Write(settings.PiCredentialId, piApiKey);
        if (string.IsNullOrWhiteSpace(mainApiKey)) CredentialStore.Delete(settings.MainApiCredentialId); else CredentialStore.Write(settings.MainApiCredentialId, mainApiKey);
        // The standalone Pi workbench keeps its existing credential target.
        if (string.IsNullOrWhiteSpace(piApiKey)) CredentialStore.Delete(LegacyPiCredentialTarget); else CredentialStore.Write(LegacyPiCredentialTarget, piApiKey);
        settings.DefaultCompanionProfile = new ManagerCompanionProfile
        {
            CharacterPrompt = CompanionCharacterPrompt.Trim(),
            UserProfile = CompanionUserProfile.Trim(),
            RelationshipSummary = CompanionRelationshipSummary.Trim(),
        };
        SettingsStore.Save(settings); LoadSettings(settings); PiApiKey = piApiKey; MainApiKey = mainApiKey;
    }

    public void OpenAntigravityLoginTerminal()
    {
        var executable = string.IsNullOrWhiteSpace(AntigravityCliPath) ? "agy" : AntigravityCliPath;
        var escapedExecutable = executable.Replace("'", "''");
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        info.ArgumentList.Add("-NoExit"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add($"& '{escapedExecutable}'; Write-Host ''; & '{escapedExecutable}' models"); Process.Start(info);
    }

    public void OpenCodexLoginTerminal()
    {
        var executable = string.IsNullOrWhiteSpace(CodexCliPath) ? "codex" : CodexCliPath;
        var escaped = executable.Replace("'", "''");
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        info.ArgumentList.Add("-NoExit"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add($"& '{escaped}' login; Write-Host ''; & '{escaped}' login status"); Process.Start(info);
    }

    public async Task<ManagerHostEvent> ProbeCodexAsync(string executable)
    {
        var conversation = SelectedConversation ?? throw new InvalidOperationException("没有活动会话。");
        await EnsurePipeAsync();
        _codexProbeCompletion = new TaskCompletionSource<ManagerHostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pipe!.SendAsync(new ProbeCodexMessage(conversation.SessionId, executable, conversation.WorkspacePath));
        return await _codexProbeCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task<ManagerHostEvent> StartCodexLoginAsync(string executable)
    {
        var conversation = SelectedConversation ?? throw new InvalidOperationException("没有活动会话。");
        await EnsurePipeAsync();
        _codexLoginCompletion = new TaskCompletionSource<ManagerHostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pipe!.SendAsync(new StartCodexLoginMessage(conversation.SessionId, executable, conversation.WorkspacePath));
        return await _codexLoginCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task EnsurePipeAsync()
    {
        if (_pipe is null)
        {
            _host = new ManagerHostProcess(); _pipe = await _host.StartAsync();
            _pipe.EventReceived += OnHostEvent; _pipe.TransportError += OnTransportError;
        }
        if (SelectedConversation is not null) await SendStartAsync(SelectedConversation);
    }

    private Task SendStartAsync(ManagerConversationItem conversation) => _pipe!.SendAsync(new StartManagerSessionMessage(
        conversation.SessionId, conversation.WorkspacePath,
        BuildMainConfig(conversation, true), BuildCodingConfig(conversation, true),
        conversation.Messages.LastOrDefault(message => message.CodeResult is not null)?.CodeResult is { NeedsUserDecision: true } or { Status: "blocked" },
        conversation.CompanionProfile));

    private async Task ActivateSelectedConversationAsync()
    {
        OnPropertyChanged(nameof(ChatEntries)); NotifyCurrentProviderChanged();
        Activities.Clear(); PendingApprovals.Clear(); NotifyApprovalChanged();
        _streamingManager = null; _streamingCoding = null;
        if (SelectedConversation is null) return;
        try { await EnsurePipeAsync(); }
        catch (Exception exception) { AddSystemMessage($"Manager Host 启动失败：{exception.Message}"); }
    }

    private void OnHostEvent(object? sender, ManagerHostEvent message) => System.Windows.Application.Current.Dispatcher.Invoke(() => HandleHostEvent(message));

    private void HandleHostEvent(ManagerHostEvent message)
    {
        var conversation = SelectedConversation;
        if (conversation is null || (message.SessionId is not null && message.SessionId != conversation.SessionId)) return;
        switch (message.Type)
        {
            case "manager_session_ready":
                if (conversation.MainAgent is not null && !string.IsNullOrWhiteSpace(message.MainSessionRef)) conversation.MainAgent.SessionRef = message.MainSessionRef;
                if (conversation.CodingAgent is not null && !string.IsNullOrWhiteSpace(message.CodingSessionRef)) conversation.CodingAgent.SessionRef = message.CodingSessionRef;
                conversation.AntigravityConversationId = conversation.MainAgent?.Provider == "antigravity" ? conversation.MainAgent.SessionRef : null;
                conversation.PiSessionFile = conversation.CodingAgent?.Provider == "pi" ? conversation.CodingAgent.SessionRef : null;
                if (message.AntigravityAvailable is not null) MainProviderAvailable = message.AntigravityAvailable == true;
                if (message.Authenticated is not null) MainProviderAuthenticated = message.Authenticated == true;
                MainProviderVersion = message.Version ?? MainProviderVersion;
                break;
            case "provider_status":
                if (message.Layer == "main")
                {
                    MainProviderAvailable = message.Available == true; MainProviderAuthenticated = message.Authenticated != false;
                    MainProviderVersion = message.Version ?? ""; MainProviderStatusDetail = message.Message ?? "";
                }
                else CodingProviderStatusDetail = message.Policy ?? message.Message ?? "";
                break;
            case "antigravity_status":
                MainProviderAvailable = message.Available == true; MainProviderAuthenticated = message.Authenticated == true;
                MainProviderVersion = message.Version ?? ""; MainProviderStatusDetail = message.Message ?? "";
                break;
            case "manager_state":
                ManagerStatus = StateLabel(message.State); IsBusy = message.State is "routing" or "responding" or "coding" or "waiting_approval";
                if (message.State is "cancelled" or "error") _streamingManager?.CompleteThinking();
                break;
            case "manager_delta":
                var managerSource = message.Source ?? "antigravity";
                _streamingManager ??= AddEntry(CurrentMainDisplayName, managerSource, ""); _streamingManager.Append(message.Text ?? "");
                _streamingManager.CompleteThinking();
                break;
            case "manager_thinking_delta":
                var thinkingSource = message.Source ?? "antigravity";
                _streamingManager ??= AddEntry(CurrentMainDisplayName, thinkingSource, "");
                _streamingManager.AppendThinking(message.Text ?? "");
                break;
            case "manager_completed":
                if (_streamingManager is null) _streamingManager = AddEntry(CurrentMainDisplayName, message.Source ?? "antigravity", message.Text ?? "");
                var completedManager = _streamingManager;
                completedManager.CompleteThinking();
                if (string.IsNullOrWhiteSpace(completedManager.Text) && !completedManager.HasThinking)
                    SelectedConversation?.Messages.Remove(completedManager);
                _streamingManager = null;
                if (!string.Equals(message.Action, "delegate_code", StringComparison.Ordinal)) IsBusy = false;
                break;
            case "manager_metrics":
                ManagerCacheReadTokens = message.CacheReadTokens ?? message.AntigravityCacheReadTokens;
                ManagerContextTokens = message.ContextTokens; ManagerContextWindow = message.ContextWindow;
                break;
            case "codex_account_status":
                CodingProviderStatusDetail = message.Authenticated == true
                    ? $"Codex 已登录 · {message.Models?.Count ?? 0} 个可用模型"
                    : "Codex CLI 可用，但尚未登录";
                _codexProbeCompletion?.TrySetResult(message); _codexProbeCompletion = null;
                break;
            case "codex_login_started":
                if (Uri.TryCreate(message.Url, UriKind.Absolute, out var loginUri))
                    Process.Start(new ProcessStartInfo(loginUri.ToString()) { UseShellExecute = true });
                CodingProviderStatusDetail = "已在浏览器中打开 Codex 登录，请完成授权。";
                _codexLoginCompletion?.TrySetResult(message); _codexLoginCompletion = null;
                break;
            case "codex_login_completed":
                CodingProviderStatusDetail = message.Ok == true ? "Codex 登录完成" : $"Codex 登录失败：{message.Message}";
                break;
            case "delegation_started":
                Activities.Add(new ManagerActivity("delegate", $"委派给 {CurrentCodingDisplayName} Coding Agent", "进行中"));
                _streamingCoding = AddEntry($"{CurrentCodingDisplayName} Coding Agent", conversation.CodingAgent?.Provider ?? "pi", "");
                break;
            case "coding_delta":
                _streamingCoding ??= AddEntry($"{CurrentCodingDisplayName} Coding Agent", message.Source ?? conversation.CodingAgent?.Provider ?? "pi", "");
                // Match the Pi workbench: once ordinary assistant text starts,
                // the inline thinking expander is completed and collapsed.
                _streamingCoding.CompleteThinking();
                _streamingCoding.Append(message.Text ?? "");
                break;
            case "coding_thinking_delta":
                // Manager conversations use the same inline thinking surface as
                // the original Pi workbench. Keep the right-side activity row
                // as the durable process history, while the current coding
                // reply shows its live reasoning directly above the text.
                _streamingCoding ??= AddEntry($"{CurrentCodingDisplayName} Coding Agent", message.Source ?? conversation.CodingAgent?.Provider ?? "pi", "");
                _streamingCoding.AppendThinking(message.Text ?? "");
                var thinking = Activities.LastOrDefault(item => item.Kind == "thinking" && item.Status == "进行中");
                if (thinking is null) { thinking = new ManagerActivity("thinking", $"{CurrentCodingDisplayName} 正在思考", "进行中"); Activities.Add(thinking); }
                thinking.Details += message.Text; break;
            case "coding_tool_approval_request":
                EnsureCodingOperation(message.Tool ?? "工具", "等待确认", message.CallId, message.Command, message.Details, message.Diff);
                AddApproval(new ApprovalRequest(conversation.SessionId, message.CallId ?? "", message.Tool ?? "", message.Summary ?? "等待确认", message.Details ?? "", message.Diff));
                break;
            case "coding_interaction_request":
                EnsureCodingOperation(message.Kind ?? "工具", "等待确认", message.RequestId, message.Command, message.Details, message.Diff);
                AddApproval(new ApprovalRequest(conversation.SessionId, message.RequestId ?? "", message.Kind ?? "codex", message.Title ?? "Codex 等待确认", message.Details ?? "", message.Diff, message.Provider ?? "codex", message.Kind ?? "command_approval", message.Fields?.GetRawText(), message.Url));
                break;
            case "coding_interaction_completed":
                RemoveApproval(message.RequestId); break;
            case "coding_tool_started":
                EnsureCodingOperation(message.Tool ?? "工具", "进行中", message.CallId, message.Command, message.Details, null);
                Activities.Add(new ManagerActivity("tool", message.Tool ?? "工具", "进行中", message.CallId) { Details = message.Command ?? "" });
                break;
            case "coding_tool_output":
                var outputActivity = Activities.LastOrDefault(item => item.CallId == message.CallId);
                if (outputActivity is not null) outputActivity.Details += message.Text;
                var outputOperation = _streamingCoding?.FindOperation(message.CallId);
                if (outputOperation is not null)
                {
                    outputOperation.Details = AppendBounded(outputOperation.Details, message.Text ?? "");
                    outputOperation.IsExpanded = true;
                    var outputGroup = _streamingCoding?.Segments.LastOrDefault(segment => segment.Operations.Contains(outputOperation));
                    if (outputGroup is not null) outputGroup.IsExpanded = true;
                }
                break;
            case "coding_tool_completed":
                foreach (var activity in Activities.Where(item => item.CallId == message.CallId)) activity.Status = message.Ok == true ? (message.AutoApproved == true ? "自动完成" : "完成") : "失败";
                var completedOperation = _streamingCoding?.FindOperation(message.CallId);
                if (completedOperation is not null)
                {
                    completedOperation.Status = message.Ok == true ? (message.AutoApproved == true ? "自动完成" : "完成") : "失败";
                    completedOperation.IsExpanded = false;
                    var completedGroup = _streamingCoding?.Segments.LastOrDefault(segment => segment.Operations.Contains(completedOperation));
                    if (completedGroup is not null && completedGroup.LatestOperation == completedOperation) completedGroup.IsExpanded = false;
                }
                RemoveApproval(message.CallId); break;
            case "code_result":
                _streamingCoding ??= AddEntry($"{CurrentCodingDisplayName} Coding Agent", conversation.CodingAgent?.Provider ?? "pi", "");
                _streamingCoding.CompleteThinking();
                _streamingCoding.CodeResult = message.Result;
                // Keep compatibility with transcripts produced before the
                // Codex bridge filtered structured output from assistant text.
                // Those entries contain one or more CodeResult JSON objects;
                // render the user-facing summary instead, just like Pi.
                if (message.Result is not null && LooksLikeCodeResultJson(_streamingCoding.Text))
                    _streamingCoding.ReplaceText(message.Result.SummaryForUser ?? "");
                if (string.IsNullOrWhiteSpace(_streamingCoding.Text)) _streamingCoding.ReplaceText(message.Result?.SummaryForUser ?? "");
                foreach (var item in Activities.Where(item => item.Status == "进行中")) item.Status = "完成";
                if (message.Result is { } result)
                {
                    foreach (var file in result.FilesChanged) Activities.Add(new ManagerActivity("diff", $"修改 {file.Path}", "完成") { Details = $"+{file.Additions}  -{file.Deletions}" });
                    foreach (var validation in result.Validation) Activities.Add(new ManagerActivity("validation", validation.Command, validation.Status == "passed" ? "通过" : validation.Status == "failed" ? "失败" : "跳过") { Details = validation.Summary });
                    Activities.Add(new ManagerActivity("result", $"{CurrentCodingDisplayName} 已提交结构化结果", result.StatusLabel) { Details = result.SummaryForUser ?? "" });
                }
                _streamingCoding = null; IsBusy = false; PendingApprovals.Clear(); NotifyApprovalChanged(); break;
            case "manager_error":
                if (_streamingManager is not null)
                {
                    var failedManager = _streamingManager;
                    failedManager.CompleteThinking();
                    if (string.IsNullOrWhiteSpace(failedManager.Text) && !failedManager.HasThinking) SelectedConversation?.Messages.Remove(failedManager);
                    _streamingManager = null;
                }
                if (_streamingCoding is not null)
                {
                    _streamingCoding.CompleteThinking();
                    _streamingCoding = null;
                }
                if (message.Code?.StartsWith("CODEX_", StringComparison.Ordinal) == true)
                {
                    var exception = new InvalidOperationException($"{message.Code}: {message.Message}");
                    _codexProbeCompletion?.TrySetException(exception); _codexProbeCompletion = null;
                    _codexLoginCompletion?.TrySetException(exception); _codexLoginCompletion = null;
                }
                AddSystemMessage($"{message.Code}: {message.Message}"); IsBusy = false; ManagerStatus = "错误"; break;
        }
        conversation.UpdatedAt = DateTime.Now; Save(); NotifyCurrentProviderChanged();
    }

    private ProcessItem? EnsureCodingOperation(string title, string status, string? callId, string? command, string? details, string? diff)
    {
        _streamingCoding ??= AddEntry($"{CurrentCodingDisplayName} Coding Agent", SelectedConversation?.CodingAgent?.Provider ?? "pi", "");
        // A worker can move directly from reasoning to a tool call without an
        // assistant text delta. Close the preceding thought in that case so a
        // completed operation is never left labelled “正在思考”.
        if (!title.StartsWith("正在思考", StringComparison.Ordinal)) _streamingCoding.CompleteThinking();
        var operation = _streamingCoding.FindOperation(callId);
        if (operation is null)
        {
            var kind = title.StartsWith("正在思考", StringComparison.Ordinal) ? "思路" : "工具";
            operation = new ProcessItem(kind, title, status, details ?? "", callId, command);
            _streamingCoding.AppendOperation(operation);
        }
        else
        {
            operation.Title = title;
            operation.Status = status;
            if (!string.IsNullOrWhiteSpace(command)) operation.CommandLine = command;
            if (!string.IsNullOrWhiteSpace(details)) operation.Details = details;
        }
        if (!string.IsNullOrWhiteSpace(diff))
            operation.Details = string.IsNullOrWhiteSpace(operation.Details) ? diff : $"{operation.Details}\n\n{diff}";
        operation.IsExpanded = status is "进行中" or "等待确认";
        var operationGroup = _streamingCoding.Segments.LastOrDefault(segment => segment.Operations.Contains(operation));
        if (operationGroup is not null) operationGroup.IsExpanded = operation.IsExpanded;
        return operation;
    }

    private static string AppendBounded(string current, string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return current;
        const int maxLength = 16_000;
        var separator = string.IsNullOrEmpty(current) || current.EndsWith('\n') ? "" : "\n";
        var value = current + separator + chunk;
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "\n…（输出已截断）";
    }

    private void AddApproval(ApprovalRequest request)
    {
        PendingApprovals.Add(request); NotifyApprovalChanged();
        Activities.Add(new ManagerActivity("tool", request.Summary, "等待确认", request.CallId) { Details = request.Details });
    }

    private ManagerMainAgentConfig BuildMainConfig(ManagerConversationItem conversation, bool includeSecret)
    {
        var binding = conversation.MainAgent ?? throw new InvalidOperationException("对话缺少主 Agent 配置。");
        return new ManagerMainAgentConfig
        {
            Provider = binding.Provider, Executable = binding.CliPath, ConversationId = binding.Provider == "antigravity" ? binding.SessionRef : null,
            Model = binding.Model, Effort = binding.Effort, TimeoutSeconds = binding.TimeoutSeconds,
            BaseUrl = binding.BaseUrl, ModelId = binding.ModelId,
            ApiKey = includeSecret && binding.Provider == "openai_compatible" ? CredentialStore.Read(binding.CredentialId) : null,
            SessionFile = binding.Provider == "openai_compatible" ? binding.SessionRef : null
        };
    }

    private ManagerCodingAgentConfig BuildCodingConfig(ManagerConversationItem conversation, bool includeSecret)
    {
        var binding = conversation.CodingAgent ?? throw new InvalidOperationException("对话缺少 Coding Agent 配置。");
        return new ManagerCodingAgentConfig
        {
            Provider = binding.Provider, Executable = binding.CliPath, ThreadId = binding.Provider == "codex" ? binding.SessionRef : null,
            Model = binding.Model, Effort = binding.Effort, BaseUrl = binding.BaseUrl, ModelId = binding.ModelId,
            ApiKey = includeSecret && binding.Provider == "pi" ? CredentialStore.Read(binding.CredentialId) ?? CredentialStore.Read(LegacyPiCredentialTarget) : null,
            SessionFile = binding.Provider == "pi" ? binding.SessionRef : null,
            AutoApproveSafeCommands = binding.AutoApproveSafeCommands, AutoApproveGitOperations = binding.AutoApproveGitOperations
        };
    }

    private ManagerChatEntry AddEntry(string role, string source, string text) { var entry = new ManagerChatEntry(role, source, text); SelectedConversation!.Messages.Add(entry); return entry; }
    private void AddSystemMessage(string text) { if (SelectedConversation is not null) SelectedConversation.Messages.Add(new ManagerChatEntry("IlMatto", "system", text)); Save(); }
    private static bool LooksLikeCodeResultJson(string? value)
    {
        var text = value?.TrimStart() ?? "";
        return text.StartsWith("{", StringComparison.Ordinal) &&
               (text.Contains("\"summaryForUser\"", StringComparison.Ordinal) || text.Contains("\"status\"", StringComparison.Ordinal));
    }
    private void RemoveApproval(string? callId) { var request = PendingApprovals.FirstOrDefault(item => item.CallId == callId); if (request is not null) PendingApprovals.Remove(request); NotifyApprovalChanged(); }
    private void NotifyApprovalChanged() { OnPropertyChanged(nameof(CurrentApproval)); OnPropertyChanged(nameof(HasApproval)); OnPropertyChanged(nameof(PendingApprovalCountLabel)); }
    private void NotifyCurrentProviderChanged()
    {
        OnPropertyChanged(nameof(WorkspaceLabel)); OnPropertyChanged(nameof(CurrentMainDisplayName)); OnPropertyChanged(nameof(CurrentCodingDisplayName));
        OnPropertyChanged(nameof(CurrentProviderLabel)); OnPropertyChanged(nameof(MainConnectionLabel)); OnPropertyChanged(nameof(ManagerCacheLabel)); OnPropertyChanged(nameof(ManagerContextLabel));
    }
    private void OnTransportError(object? sender, string error) => System.Windows.Application.Current.Dispatcher.Invoke(() => { AddSystemMessage($"Manager Host 连接异常：{error}"); IsBusy = false; });
    private void Save() => ManagerConversationStore.Save(Conversations);

    private ManagerConversationItem CreateConversation()
    {
        var item = new ManagerConversationItem(Guid.NewGuid().ToString("N"), "新对话")
        {
            WorkspacePath = WorkspacePath,
            CompanionProfile = new ManagerCompanionProfile
            {
                CharacterPrompt = CompanionCharacterPrompt.Trim(),
                UserProfile = CompanionUserProfile.Trim(),
                RelationshipSummary = CompanionRelationshipSummary.Trim(),
            },
        };
        item.MainAgent = DefaultMainAgentProvider == "openai_compatible"
            ? new ManagerMainAgentBinding { Provider = "openai_compatible", BaseUrl = MainApiBaseUrl, ModelId = MainApiModelId, CredentialId = MainApiCredentialId, TimeoutSeconds = MainApiTimeoutSeconds }
            : new ManagerMainAgentBinding { Provider = "antigravity", CliPath = AntigravityCliPath, Model = AntigravityModel, Effort = AntigravityEffort, TimeoutSeconds = AntigravityTimeoutSeconds };
        item.CodingAgent = DefaultCodingAgentProvider == "codex"
            ? new ManagerCodingAgentBinding { Provider = "codex", CliPath = CodexCliPath, Model = CodexModel, Effort = CodexEffort }
            : new ManagerCodingAgentBinding { Provider = "pi", BaseUrl = BaseUrl, ModelId = ModelId, CredentialId = PiCredentialId, AutoApproveSafeCommands = AutoApproveSafeCommands, AutoApproveGitOperations = AutoApproveGitOperations };
        return item;
    }

    private void NormalizeLegacyBinding(ManagerConversationItem conversation)
    {
        conversation.MainAgent ??= new ManagerMainAgentBinding { Provider = "antigravity", SessionRef = conversation.AntigravityConversationId };
        conversation.CodingAgent ??= new ManagerCodingAgentBinding { Provider = "pi", SessionRef = conversation.PiSessionFile };
        if (conversation.MainAgent.Provider == "antigravity")
        {
            if (string.IsNullOrWhiteSpace(conversation.MainAgent.CliPath) || conversation.MainAgent.CliPath == "agy") conversation.MainAgent.CliPath = AntigravityCliPath;
            if (string.IsNullOrWhiteSpace(conversation.MainAgent.Model)) conversation.MainAgent.Model = AntigravityModel;
            conversation.MainAgent.Effort = string.IsNullOrWhiteSpace(conversation.MainAgent.Effort) ? AntigravityEffort : conversation.MainAgent.Effort;
            conversation.MainAgent.TimeoutSeconds = conversation.MainAgent.TimeoutSeconds <= 0 ? AntigravityTimeoutSeconds : conversation.MainAgent.TimeoutSeconds;
        }
        if (conversation.CodingAgent.Provider == "pi" && string.IsNullOrWhiteSpace(conversation.CodingAgent.BaseUrl))
        {
            conversation.CodingAgent.BaseUrl = BaseUrl; conversation.CodingAgent.ModelId = ModelId; conversation.CodingAgent.CredentialId = PiCredentialId;
            conversation.CodingAgent.AutoApproveSafeCommands = AutoApproveSafeCommands; conversation.CodingAgent.AutoApproveGitOperations = AutoApproveGitOperations;
        }
    }

    private string StateLabel(string? state) => state switch { "routing" => $"{CurrentMainDisplayName} 正在协调", "responding" => $"{CurrentMainDisplayName} 正在回答", "coding" => $"{CurrentCodingDisplayName} Coding Agent 正在工作", "waiting_approval" => "等待你的确认", "cancelled" => "已取消", "error" => "错误", _ => "准备就绪" };

    private void LoadSettings(AppSettings settings)
    {
        DefaultMainAgentProvider = settings.DefaultMainAgentProvider; DefaultCodingAgentProvider = settings.DefaultCodingAgentProvider;
        BaseUrl = settings.BaseUrl; ModelId = settings.ModelId; PiCredentialId = settings.PiCredentialId;
        MainApiBaseUrl = settings.MainApiBaseUrl; MainApiModelId = settings.MainApiModelId; MainApiCredentialId = settings.MainApiCredentialId; MainApiTimeoutSeconds = settings.MainApiTimeoutSeconds;
        WorkspacePath = settings.WorkspacePath; AutoApproveSafeCommands = settings.AutoApproveSafeCommands; AutoApproveGitOperations = settings.AutoApproveGitOperations;
        AntigravityCliPath = settings.AntigravityCliPath; AntigravityModel = settings.AntigravityModel; AntigravityEffort = settings.AntigravityEffort; AntigravityTimeoutSeconds = settings.AntigravityTimeoutSeconds;
        CodexCliPath = settings.CodexCliPath; CodexModel = settings.CodexModel; CodexEffort = settings.CodexEffort;
        var companion = settings.DefaultCompanionProfile ?? new ManagerCompanionProfile();
        CompanionCharacterPrompt = companion.CharacterPrompt;
        CompanionUserProfile = companion.UserProfile;
        CompanionRelationshipSummary = companion.RelationshipSummary;
    }

    private void CleanupCredentialIfUnused(string? credentialId)
    {
        if (string.IsNullOrWhiteSpace(credentialId) || credentialId is LegacyPiCredentialTarget) return;
        if (credentialId == PiCredentialId || credentialId == MainApiCredentialId) return;
        if (Conversations.Any(item => item.MainAgent?.CredentialId == credentialId || item.CodingAgent?.CredentialId == credentialId)) return;
        CredentialStore.Delete(credentialId);
    }

    private static bool ProviderProfileChanged(string oldUrl, string oldModel, string oldKey, string newUrl, string newModel, string newKey) =>
        !string.Equals(oldUrl.Trim(), newUrl.Trim(), StringComparison.OrdinalIgnoreCase) || !string.Equals(oldModel.Trim(), newModel.Trim(), StringComparison.Ordinal) || oldKey != newKey;

    partial void OnSelectedConversationChanged(ManagerConversationItem? value) { NotifyCurrentProviderChanged(); if (_initialized) _ = ActivateSelectedConversationAsync(); }
    partial void OnSelectedConversationChanging(ManagerConversationItem? oldValue, ManagerConversationItem? newValue)
    {
        if (!IsBusy || oldValue is null || ReferenceEquals(oldValue, newValue)) return;
        oldValue.Messages.Add(new ManagerChatEntry("IlMatto", "system", "会话切换时任务仍在运行，已标记为会话中断；不会自动重放该任务。")); oldValue.UpdatedAt = DateTime.Now; IsBusy = false;
    }
    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnMainProviderAvailableChanged(bool value) => OnPropertyChanged(nameof(MainConnectionLabel));
    partial void OnMainProviderAuthenticatedChanged(bool value) => OnPropertyChanged(nameof(MainConnectionLabel));
    partial void OnMainProviderVersionChanged(string value) => OnPropertyChanged(nameof(MainConnectionLabel));
    partial void OnManagerCacheReadTokensChanged(long? value) => OnPropertyChanged(nameof(ManagerCacheLabel));
    partial void OnManagerContextTokensChanged(long? value) => OnPropertyChanged(nameof(ManagerContextLabel));
    partial void OnManagerContextWindowChanged(long? value) => OnPropertyChanged(nameof(ManagerContextLabel));

    public async ValueTask DisposeAsync()
    {
        if (IsBusy && SelectedConversation is not null) SelectedConversation.Messages.Add(new ManagerChatEntry("IlMatto", "system", "程序退出时任务仍在运行，已标记为会话中断；下次启动不会自动重放。"));
        Save();
        if (_pipe is not null) { _pipe.EventReceived -= OnHostEvent; _pipe.TransportError -= OnTransportError; }
        if (_host is not null) await _host.DisposeAsync();
    }
}
