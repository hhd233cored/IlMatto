using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfGridLength = System.Windows.GridLength;
using System.Windows.Threading;

namespace IlMatto.Desktop;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const string CredentialTarget = "IlMatto/OpenAICompatible/default";
    private readonly AgentHostProcess _hostProcess = new();
    private readonly DispatcherTimer _persistTimer;
    private PipeClient? _pipe;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private ChatEntry? _currentAssistant;
    private ProcessItem? _thinkingProcess;
    private ProcessItem? _compactionProcess;
    private string? _pendingInitialPrompt;
    private bool _sessionStarted;
    private bool _approvalSubmitting;
    private readonly Dictionary<string, string?> _pendingDiffs = new(StringComparer.Ordinal);
    // Pi can issue more than one approval request while several tool calls are
    // pending. Keep the first request in the fixed approval card and queue the
    // rest instead of overwriting the visible request.
    private readonly Queue<ApprovalRequest> _approvalQueue = new();

    public MainViewModel()
    {
        _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            SaveConversationsNow();
        };
        var savedSettings = SettingsStore.Load();
        BaseUrl = savedSettings.BaseUrl;
        ModelId = savedSettings.ModelId;
        WorkspacePath = savedSettings.WorkspacePath;
        AutoApproveSafeCommands = savedSettings.AutoApproveSafeCommands;
        AutoApproveGitOperations = savedSettings.AutoApproveGitOperations;

        var savedKey = CredentialStore.Read(CredentialTarget);
        if (!string.IsNullOrEmpty(savedKey)) ApiKey = savedKey;

        var restoredConversations = ConversationStore.Load();
        if (restoredConversations.Count > 0)
        {
            foreach (var conversation in restoredConversations) Conversations.Add(conversation);
            // Rewrite older histories after loading them through the bounded model so
            // a previously huge tool output does not keep bloating future launches.
            PersistConversations();
        }
        else
            Conversations.Add(CreateConversation());
        foreach (var command in DefaultSlashCommands()) SlashCommands.Add(command);
        SelectedConversation = Conversations[0];
    }

    [ObservableProperty] private string workspacePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    [ObservableProperty] private string baseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string modelId = "gpt-4o-mini";
    [ObservableProperty] private string apiKey = "";
    [ObservableProperty] private bool autoApproveSafeCommands;
    [ObservableProperty] private bool autoApproveGitOperations;
    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private string status = "未连接";
    [ObservableProperty] private string contextMetricsLabel = "上下文：未开始";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isApprovalVisible;
    [ObservableProperty] private ApprovalRequest? pendingApproval;
    [ObservableProperty] private ConversationItem? selectedConversation;
    [ObservableProperty] private ObservableCollection<ChatEntry> chatEntries = new();
    [ObservableProperty] private ObservableCollection<ActivityItem> activities = new();
    [ObservableProperty] private ObservableCollection<ProcessItem> processes = new();
    [ObservableProperty] private ObservableCollection<SlashCommandItem> slashCommands = new();
    [ObservableProperty] private ObservableCollection<SlashCommandItem> filteredSlashCommands = new();
    [ObservableProperty] private bool isCommandMenuVisible;
    [ObservableProperty] private SlashCommandItem? selectedSlashCommand;
    [ObservableProperty] private string currentConversationTitle = "新对话";
    [ObservableProperty] private bool isProcessPanelVisible = true;
    [ObservableProperty] private WpfGridLength processPanelWidth = new(310);
    [ObservableProperty] private bool isGitRepository;
    [ObservableProperty] private string gitStatusMessage = "选择 Git 工作区后可查看状态。";
    [ObservableProperty] private string gitRoot = "";
    [ObservableProperty] private string gitBranch = "";
    [ObservableProperty] private string gitTracking = "";
    [ObservableProperty] private string gitDiffText = "选择一个变更文件以查看 Diff。";
    [ObservableProperty] private bool gitDiffIsStaged;
    [ObservableProperty] private bool gitDiffTruncated;
    [ObservableProperty] private GitFileChange? selectedGitFile;

    public string ProcessPanelToggleGlyph => IsProcessPanelVisible ? "◀" : "▶";
    public string ApprovalModeLabel => AutoApproveGitOperations ? "本地 Git 自动执行 · 其他受控工具仍按设置审批" : AutoApproveSafeCommands ? "自动批准安全命令 · 高风险仍需确认" : "受控工具 · 逐次审批";

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<GitFileChange> StagedGitChanges { get; } = new();
    public ObservableCollection<GitFileChange> UnstagedGitChanges { get; } = new();
    public ObservableCollection<GitFileChange> UntrackedGitChanges { get; } = new();
    public ObservableCollection<GitBranch> GitBranches { get; } = new();
    public ObservableCollection<GitCommit> GitCommits { get; } = new();
    public ObservableCollection<GitRemote> GitRemotes { get; } = new();
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(InputText) && (Directory.Exists(WorkspacePath) || InputText.TrimStart().StartsWith('/'));
    public int PendingApprovalCount => _approvalQueue.Count + (PendingApproval is null ? 0 : 1);
    public string PendingApprovalQueueLabel => PendingApprovalCount > 1 ? $"还有 {PendingApprovalCount - 1} 项待批准" : "";

    public Task InitializeAsync() => RefreshGitAsync();

    private ConversationItem CreateConversation()
    {
        return new ConversationItem(Guid.NewGuid().ToString("N")) { WorkspacePath = Path.GetFullPath(WorkspacePath) };
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (TryHandleLocalCommand(text)) { InputText = ""; return; }
        if (SelectedConversation is null) await NewSessionAsync();
        if (SelectedConversation is null) return;

        InputText = "";
        if (SelectedConversation.Title == "新对话")
        {
            SelectedConversation.Title = text.Length > 34 ? $"{text[..34]}…" : text;
            CurrentConversationTitle = SelectedConversation.Title;
        }
        SelectedConversation.UpdatedAt = DateTime.Now;
        ChatEntries.Add(new ChatEntry("你", text));
        _currentAssistant = new ChatEntry("Pi", "");
        ChatEntries.Add(_currentAssistant);
        // Create the inline thinking row before the first host event arrives.
        // This keeps the previous Pi-like “正在思考” presentation even when
        // a compatible endpoint does not emit native reasoning deltas.
        EnsureInlineOperation("正在思考", "进行中");
        if (_inlineOperation is not null)
        {
            if (string.IsNullOrWhiteSpace(_inlineOperation.Details)) _inlineOperation.Details = "正在分析…";
            _inlineOperation.IsExpanded = true;
        }
        if (_inlineSegment is not null) _inlineSegment.IsExpanded = true;
        PersistConversations();
        IsBusy = true;
        _pendingInitialPrompt = text;
        NotifyCanSendChanged();
        try
        {
            await EnsureSessionAsync();
            await _pipe!.SendAsync(new SendMessage(_sessionId, text));
        }
        catch (Exception ex)
        {
            CompleteThinkingOperation("失败");
            AddError(ex.Message);
            IsBusy = false;
            NotifyCanSendChanged();
        }
        finally { _pendingInitialPrompt = null; }
    }

    [RelayCommand]
    private async Task ApproveToolAsync() => await SubmitApprovalAsync(true);

    [RelayCommand]
    private async Task RejectToolAsync() => await SubmitApprovalAsync(false);

    private async Task SubmitApprovalAsync(bool approved)
    {
        if (_approvalSubmitting || PendingApproval is null || _pipe is null) return;
        _approvalSubmitting = true;
        var approval = PendingApproval;
        // Keep PendingApproval populated until the response is written. A new
        // request can arrive while SendAsync is yielding; keeping this marker
        // makes that request join the queue instead of becoming the active item
        // and then being skipped by ActivateNextApproval().
        IsApprovalVisible = false;
        Activities.Add(new ActivityItem(approval.Summary, approved ? "已批准" : "已拒绝", approval.Details));
        // A tool can have both its running row and a separate approval row.
        // Update every matching row (including the inline row) immediately so
        // none of them remains stuck at “等待确认” after the user responds.
        var approvalStatus = approved ? "已批准" : "已拒绝";
        foreach (var process in Processes.Where(item => item.CallId == approval.CallId))
        {
            process.Status = approvalStatus;
        }
        if (_currentAssistant is not null)
        {
            foreach (var operation in _currentAssistant.Segments.SelectMany(segment => segment.Operations).Where(item => item.CallId == approval.CallId))
            {
                operation.Status = approvalStatus;
            }
        }
        try
        {
            await _pipe.SendAsync(new ApproveToolCallMessage(approval.SessionId, approval.CallId, approved));
            ActivateNextApproval();
        }
        catch (Exception ex)
        {
            ClearPendingApprovals();
            AddError($"发送审批结果失败：{ex.Message}");
        }
        finally
        {
            _approvalSubmitting = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_pipe is null) return;
        await _pipe.SendAsync(new CancelMessage(_sessionId));
        ClearPendingApprovals();
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var conversation = CreateConversation();
        Conversations.Insert(0, conversation);
        SelectedConversation = conversation;
        PersistConversations();
        if (_pipe is null) return;
        await EnsureSessionAsync();
        await RefreshGitAsync();
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItem? conversation)
    {
        if (conversation is null) return;
        if (_pipe is not null && !string.IsNullOrWhiteSpace(conversation.PiSessionFile))
        {
            try { await _pipe.SendAsync(new DeleteSessionMessage(conversation.SessionId, conversation.PiSessionFile)); } catch (Exception ex) { AddError($"删除会话文件失败：{ex.Message}"); }
        }
        else ConversationStore.TryDeletePiSession(conversation.PiSessionFile);
        var index = Conversations.IndexOf(conversation);
        Conversations.Remove(conversation);
        if (Conversations.Count == 0) Conversations.Add(CreateConversation());
        if (ReferenceEquals(SelectedConversation, conversation))
            SelectedConversation = Conversations[Math.Min(index, Conversations.Count - 1)];
        PersistConversations();
    }

    [RelayCommand]
    private void BrowseWorkspace()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择 Agent 工作区",
            SelectedPath = WorkspacePath,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) ApplySettings(BaseUrl, ModelId, ApiKey, dialog.SelectedPath, AutoApproveSafeCommands, AutoApproveGitOperations);
    }

    public void ApplySettings(string baseUrl, string modelId, string apiKey, string workspacePath, bool autoApproveSafeCommands = false, bool autoApproveGitOperations = false)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        ModelId = string.IsNullOrWhiteSpace(modelId) ? "gpt-4o-mini" : modelId.Trim();
        ApiKey = apiKey.Trim();
        WorkspacePath = Path.GetFullPath(workspacePath);
        AutoApproveSafeCommands = autoApproveSafeCommands;
        AutoApproveGitOperations = autoApproveGitOperations;
        // Update only the Pi workbench fields so newer Manager settings (for
        // example Browser MCP permissions) survive a legacy settings save.
        var settings = SettingsStore.Load();
        settings.BaseUrl = BaseUrl;
        settings.ModelId = ModelId;
        settings.WorkspacePath = WorkspacePath;
        settings.AutoApproveSafeCommands = AutoApproveSafeCommands;
        settings.AutoApproveGitOperations = AutoApproveGitOperations;
        SettingsStore.Save(settings);
        if (!string.IsNullOrWhiteSpace(ApiKey)) CredentialStore.Write(CredentialTarget, ApiKey);
        else CredentialStore.Delete(CredentialTarget);
        if (SelectedConversation is not null && !string.Equals(SelectedConversation.WorkspacePath, WorkspacePath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedConversation.WorkspacePath = WorkspacePath;
            SelectedConversation.PiSessionFile = null;
        }
        _sessionStarted = false;
        NotifyCanSendChanged();
        _ = RefreshGitAsync();
    }

    private async Task EnsureSessionAsync()
    {
        if (_pipe is null)
        {
            Status = "正在启动 Agent Host…";
            _pipe = await _hostProcess.StartAsync();
            _pipe.EventReceived += OnHostEvent;
            _pipe.TransportError += OnTransportError;
        }
        if (_sessionStarted) return;
        if (!string.IsNullOrWhiteSpace(ApiKey)) CredentialStore.Write(CredentialTarget, ApiKey);
        var conversation = SelectedConversation;
        var effectiveWorkspace = Path.GetFullPath(string.IsNullOrWhiteSpace(conversation?.WorkspacePath) ? WorkspacePath : conversation.WorkspacePath);
        if (conversation is not null && string.IsNullOrWhiteSpace(conversation.WorkspacePath)) conversation.WorkspacePath = effectiveWorkspace;
        var restoreTranscript = conversation?.PiSessionFile is null ? BuildRestoreTranscript(_pendingInitialPrompt) : null;
        await _pipe.SendAsync(new StartSessionMessage(_sessionId, effectiveWorkspace, NormalizeBaseUrl(BaseUrl), ModelId, ApiKey, AutoApproveSafeCommands, AutoApproveGitOperations, conversation?.PiSessionFile, restoreTranscript));
        _sessionStarted = true;
        await _pipe.SendAsync(new GetCommandsMessage(_sessionId));
        await _pipe.SendAsync(new GetGitOverviewMessage(_sessionId));
    }

    private static string NormalizeBaseUrl(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "https://api.openai.com/v1" : value.Trim().Trim('`', '"', '\'').TrimEnd('/');
        return normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^"/chat/completions".Length]
            : normalized;
    }

    private void OnHostEvent(object? sender, HostEvent message)
    {
        _ = WpfApplication.Current.Dispatcher.InvokeAsync(() => ApplyHostEvent(message));
    }

    private void ApplyHostEvent(HostEvent message)
    {
        if (message.SessionId is not null && message.SessionId != _sessionId && message.Type is not "host_ready" and not "error") return;
        if (SelectedConversation is not null) SelectedConversation.UpdatedAt = DateTime.Now;

        switch (message.Type)
        {
            case "host_ready": Status = $"Host 已连接（v{message.Version}）"; break;
            case "session_ready":
                if (SelectedConversation is not null)
                {
                    SelectedConversation.PiSessionFile = message.SessionFile;
                    if (!string.IsNullOrWhiteSpace(message.WorkspacePath)) SelectedConversation.WorkspacePath = message.WorkspacePath;
                }
                Status = message.Restored == true ? message.LegacyRestored == true ? "已迁移旧会话上下文" : "已恢复会话上下文" : "会话已准备";
                break;
            case "session_deleted": break;
            case "slash_commands":
                if (message.Commands is not null)
                {
                    foreach (var command in message.Commands)
                    {
                        if (SlashCommands.All(item => !string.Equals(item.Name, command.Name, StringComparison.OrdinalIgnoreCase)))
                            SlashCommands.Add(new SlashCommandItem(command.Name, command.Description ?? "", command.Source ?? "extension"));
                    }
                    UpdateCommandSuggestions();
                }
                break;
            case "assistant_delta":
                CompleteInlineThinkingOperations("完成");
                if (_currentAssistant is null) { _currentAssistant = new ChatEntry("Pi", ""); ChatEntries.Add(_currentAssistant); }
                _currentAssistant.AppendText(message.Text ?? "");
                break;
            case "assistant_completed":
                if (_currentAssistant is null) ChatEntries.Add(_currentAssistant = new ChatEntry("Pi", message.Text ?? ""));
                else if (string.IsNullOrEmpty(_currentAssistant.Text)) _currentAssistant.AppendText(message.Text ?? "");
                if (_currentAssistant is not null) _currentAssistant.IsCompleted = true;
                CompleteThinkingOperation("完成");
                break;
            case "tool_approval_request":
                EnqueueApproval(new ApprovalRequest(message.SessionId ?? _sessionId, message.CallId ?? "", message.Tool ?? "tool", message.Summary ?? "请求操作", message.Details ?? "", message.Diff));
                break;
            case "tool_started":
                {
                    var toolTitle = message.Tool ?? "tool";
                    var commandDetails = string.IsNullOrWhiteSpace(message.Command) ? message.CallId ?? "" : message.Command;
                    Activities.Add(new ActivityItem(toolTitle, "执行中", commandDetails));
                    Processes.Add(new ProcessItem("工具", toolTitle, "执行中", commandDetails, message.CallId, message.Command));
                    if (IsVisibleOperationTool(message.Tool))
                    {
                        StartInlineOperation(toolTitle, "执行中", message.CallId, message.Command);
                        _inlineOperation!.CommandLine = message.Command ?? "";
                        _inlineOperation.Details = commandDetails;
                        _inlineOperation.IsExpanded = string.Equals(message.Tool, "run_command", StringComparison.OrdinalIgnoreCase);
                        if (string.Equals(message.Tool, "run_command", StringComparison.OrdinalIgnoreCase) && _inlineSegment is not null) _inlineSegment.IsExpanded = true;
                    }
                }
                break;
            case "tool_output":
                if (IsVisibleOperationTool(message.Tool) && !string.IsNullOrWhiteSpace(message.CallId)) AppendToolOutput(message.CallId!, message.Text ?? "");
                break;
            case "tool_completed":
                var uiOutput = LimitForDisplay(message.Output);
                var completionStatus = message.Ok == true ? message.AutoApproved == true ? "自动完成" : "完成" : "失败";
                var completedDiff = message.CallId is not null && _pendingDiffs.Remove(message.CallId, out var pendingDiff) ? pendingDiff : null;
                completedDiff ??= message.Diff;
                if (message.Ok == true && string.Equals(message.Tool, "apply_patch", StringComparison.OrdinalIgnoreCase)) RecordEditedFile(completedDiff, message.Output, message.Summary);
                var completionDetails = string.IsNullOrWhiteSpace(message.Summary) ? "" : message.Summary!;
                if (!string.IsNullOrWhiteSpace(completedDiff)) completionDetails = WithDiffPreview(completionDetails, completedDiff);
                Activities.Add(new ActivityItem(message.Tool ?? "tool", completionStatus, completionDetails, uiOutput));
                var matchingProcesses = Processes.Where(item => item.CallId == message.CallId).ToList();
                foreach (var process in matchingProcesses)
                {
                    process.Status = completionStatus;
                    var output = message.Output ?? "";
                    if (output.Length > 4000) output = output[..4000] + "\n…（输出已截断）";
                    var commandDetails = process.Kind == "工具" && !string.IsNullOrWhiteSpace(process.CommandLine) ? $"{completionDetails}\n命令：{process.CommandLine}" : completionDetails;
                    process.Details = string.IsNullOrWhiteSpace(output) ? commandDetails : $"{commandDetails}\n{output}";
                }
                if (IsVisibleOperationTool(message.Tool))
                {
                    EnsureInlineOperation(message.Tool ?? "操作", completionStatus, message.CallId);
                    var inlineCommand = !string.IsNullOrWhiteSpace(_inlineOperation!.CommandLine) ? $"{completionDetails}\n命令：{_inlineOperation.CommandLine}" : completionDetails;
                    _inlineOperation.Details = string.IsNullOrWhiteSpace(uiOutput) ? inlineCommand : $"{inlineCommand}\n{uiOutput}";
                    _inlineOperation.IsExpanded = false;
                    _inlineSegment!.IsExpanded = false;
                }
                break;
            case "git_overview":
                ApplyGitOverview(message.Overview);
                break;
            case "git_diff":
                GitDiffIsStaged = string.Equals(message.Scope, "staged", StringComparison.Ordinal);
                GitDiffTruncated = message.Truncated == true;
                GitDiffText = string.IsNullOrWhiteSpace(message.Content) ? "该范围没有可显示的 Diff。" : LimitForDisplay(message.Content, 64_000);
                break;
            case "session_metrics":
                ContextMetricsLabel = FormatContextMetrics(message);
                break;
            case "context_compaction_start":
                _compactionProcess ??= new ProcessItem("压缩", "上下文压缩", "进行中");
                if (!Processes.Contains(_compactionProcess)) Processes.Add(_compactionProcess);
                _compactionProcess.Details = message.TokensBefore is long before ? $"压缩前约 {before:N0} token" : "正在整理会话历史…";
                _compactionProcess.IsExpanded = false;
                IsBusy = true;
                Status = "正在压缩上下文…";
                break;
            case "context_compaction_end":
                if (_compactionProcess is not null)
                {
                    _compactionProcess.Status = message.Aborted == true ? "已取消" : string.IsNullOrWhiteSpace(message.ErrorMessage) ? "完成" : "失败";
                    _compactionProcess.Details = LimitForDisplay(!string.IsNullOrWhiteSpace(message.ErrorMessage) ? message.ErrorMessage : message.Summary ?? "上下文压缩完成", 4_000);
                    _compactionProcess.IsExpanded = false;
                    _compactionProcess = null;
                }
                if (!string.IsNullOrWhiteSpace(message.ErrorMessage)) AddError($"上下文压缩失败：{message.ErrorMessage} (COMPACTION_FAILED)");
                break;
            case "thinking_delta":
                _thinkingProcess ??= new ProcessItem("思路", "Agent 思路", "进行中");
                if (!Processes.Contains(_thinkingProcess)) Processes.Add(_thinkingProcess);
                if (string.Equals(_thinkingProcess.Details, "正在分析…", StringComparison.Ordinal)) _thinkingProcess.Details = "";
                _thinkingProcess.Details += message.Text ?? "";
                _thinkingProcess.IsExpanded = true;
                EnsureInlineOperation("正在思考", "进行中");
                if (string.Equals(_inlineOperation!.Details, "正在分析…", StringComparison.Ordinal)) _inlineOperation.Details = "";
                _inlineOperation!.Details += message.Text ?? "";
                _inlineOperation.IsExpanded = true;
                if (_inlineSegment is not null) _inlineSegment.IsExpanded = true;
                break;
            case "command_result":
                if (_currentAssistant is not null && string.IsNullOrEmpty(_currentAssistant.Text)) { ChatEntries.Remove(_currentAssistant); _currentAssistant = null; }
                Processes.Add(new ProcessItem("指令", $"/{message.Command}", "完成", message.Message ?? ""));
                Activities.Add(new ActivityItem($"/{message.Command}", "完成", message.Message ?? ""));
                break;
            case "session_state":
                Status = message.State switch { "thinking" => "Agent 思考中…", "waiting_approval" => "等待你的批准", "compacting" => "正在压缩上下文…", "idle" => "就绪", "cancelled" => "已取消", "error" => "出错", _ => message.State ?? "" };
                IsBusy = message.State is "thinking" or "waiting_approval" or "compacting";
                if (message.State == "thinking" && _thinkingProcess is null)
                {
                    _thinkingProcess = new ProcessItem("思路", "Agent 思路", "进行中");
                    Processes.Add(_thinkingProcess);
                }
                if (message.State == "thinking")
                {
                    // Some OpenAI-compatible endpoints do not expose native
                    // reasoning deltas. Still create a live thinking row so the
                    // streaming transcript reflects the current agent phase.
                    EnsureInlineOperation("正在思考", "进行中");
                    if (string.IsNullOrWhiteSpace(_thinkingProcess?.Details)) _thinkingProcess!.Details = "正在分析…";
                    if (string.IsNullOrWhiteSpace(_inlineOperation?.Details)) _inlineOperation!.Details = "正在分析…";
                    if (_inlineOperation is not null) _inlineOperation.IsExpanded = true;
                    if (_inlineSegment is not null) _inlineSegment.IsExpanded = true;
                }
                if (message.State is "idle" or "cancelled" or "error")
                {
                    // A provider may finish a turn without sending a final
                    // assistant text delta (for example, a tool-only turn).
                    // Close every live thinking row at the lifecycle boundary
                    // so it cannot remain stuck at “正在思考”.
                    CompleteInlineThinkingOperations(message.State == "idle" ? "完成" : message.State == "cancelled" ? "已取消" : "失败");
                    if (_thinkingProcess is not null) _thinkingProcess.Status = message.State == "idle" ? "完成" : message.State == "cancelled" ? "已取消" : "失败";
                    _thinkingProcess = null;
                    if (_inlineOperation is not null)
                    {
                        _inlineOperation.Status = message.State == "idle" ? "完成" : message.State == "cancelled" ? "已取消" : "失败";
                        _inlineOperation.IsExpanded = false;
                        _inlineOperation = null;
                        _inlineSegment = null;
                    }
                    _compactionProcess = null;
                    if (message.State is "cancelled" or "error") ClearPendingApprovals();
                }
                NotifyCanSendChanged();
                break;
            case "error":
                CompleteThinkingOperation("失败");
                if (_compactionProcess is not null)
                {
                    _compactionProcess.Status = "失败";
                    _compactionProcess.Details = message.Message ?? "上下文压缩失败";
                    _compactionProcess.IsExpanded = false;
                    _compactionProcess = null;
                }
                ClearPendingApprovals();
                AddError($"{message.Message} ({message.Code})");
                _sessionStarted = false;
                IsBusy = false;
                NotifyCanSendChanged();
                break;
        }
        PersistConversations();
    }

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        if (value is null) return;
        _sessionId = value.SessionId;
        _sessionStarted = false;
        if (!string.IsNullOrWhiteSpace(value.WorkspacePath)) WorkspacePath = value.WorkspacePath;
        ChatEntries = value.Messages;
        Activities = value.Activities;
        Processes = value.Processes;
        CurrentConversationTitle = value.Title;
        _currentAssistant = null;
        ClearPendingApprovals();
        _inlineOperation = null;
        _inlineSegment = null;
        _compactionProcess = null;
        ContextMetricsLabel = "上下文：未开始";
        _pendingDiffs.Clear();
        NotifyCanSendChanged();
        if (_pipe is not null) _ = RefreshGitAsync();
    }

    private void OnTransportError(object? sender, string message) => _ = WpfApplication.Current.Dispatcher.InvokeAsync(() => { ClearPendingApprovals(); AddError(message); });
    private void AddError(string message) { Activities.Add(new ActivityItem("系统", "错误", message)); Status = "连接错误"; }

    private static bool IsVisibleOperationTool(string? tool) =>
        string.Equals(tool, "run_command", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tool, "apply_patch", StringComparison.OrdinalIgnoreCase);

    private void AppendToolOutput(string callId, string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        var processItems = Processes.Where(item => item.CallId == callId).ToList();
        foreach (var process in processItems) process.Details = AppendBounded(process.Details, chunk);

        var operation = _currentAssistant?.Segments.SelectMany(segment => segment.Operations).LastOrDefault(item => item.CallId == callId);
        if (operation is not null)
        {
            operation.Details = AppendBounded(operation.Details, chunk);
            operation.IsExpanded = true;
        }
    }

    private static string AppendBounded(string current, string chunk)
    {
        const int maxLength = 16_000;
        var separator = string.IsNullOrEmpty(current) || current.EndsWith('\n') ? "" : "\n";
        var value = current + separator + chunk;
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "\n…（输出已截断）";
    }

    private void EnqueueApproval(ApprovalRequest approval)
    {
        if (string.IsNullOrWhiteSpace(approval.CallId)) return;
        if (PendingApproval?.CallId == approval.CallId || _approvalQueue.Any(item => item.CallId == approval.CallId)) return;

        if (PendingApproval is null)
        {
            ActivateApproval(approval);
        }
        else
        {
            _approvalQueue.Enqueue(approval);
            NotifyApprovalQueueChanged();
        }

        if (!string.IsNullOrWhiteSpace(approval.CallId)) _pendingDiffs[approval.CallId] = approval.Diff;
        var approvalDetails = WithDiffPreview(approval.Details, approval.Diff);
        Activities.Add(new ActivityItem(approval.Summary, "等待批准", approvalDetails));
        Processes.Add(new ProcessItem("审批", approval.Summary, "等待批准", approvalDetails, approval.CallId));
        if (IsVisibleOperationTool(approval.Tool))
        {
            EnsureInlineOperation(approval.Summary, "等待批准", approval.CallId);
            if (_inlineOperation is not null)
            {
                _inlineOperation.Details = approvalDetails;
                // Keep the pending diff visible in the streaming transcript while
                // the approval card is waiting for the user's decision.
                _inlineOperation.IsExpanded = true;
            }
            if (_inlineSegment is not null) _inlineSegment.IsExpanded = true;
        }
    }

    private void ActivateApproval(ApprovalRequest approval)
    {
        PendingApproval = approval;
        IsApprovalVisible = true;
        if (_currentAssistant is not null) _currentAssistant.PendingApproval = approval;
    }

    private void ActivateNextApproval()
    {
        if (_approvalQueue.Count == 0)
        {
            PendingApproval = null;
            IsApprovalVisible = false;
            if (_currentAssistant is not null) _currentAssistant.PendingApproval = null;
            NotifyApprovalQueueChanged();
            return;
        }

        ActivateApproval(_approvalQueue.Dequeue());
        NotifyApprovalQueueChanged();
    }

    private void ClearPendingApprovals()
    {
        _approvalQueue.Clear();
        PendingApproval = null;
        IsApprovalVisible = false;
        if (_currentAssistant is not null) _currentAssistant.PendingApproval = null;
        NotifyApprovalQueueChanged();
    }

    private void NotifyApprovalQueueChanged()
    {
        OnPropertyChanged(nameof(PendingApprovalCount));
        OnPropertyChanged(nameof(PendingApprovalQueueLabel));
    }

    partial void OnPendingApprovalChanged(ApprovalRequest? value)
    {
        OnPropertyChanged(nameof(PendingApprovalCount));
        OnPropertyChanged(nameof(PendingApprovalQueueLabel));
    }

    private void RecordEditedFile(string? diff, string? output, string? summary)
    {
        if (_currentAssistant is null) return;
        var pathMatch = diff is not null
            ? Regex.Match(diff, @"^\+\+\+ b/(.+)$", RegexOptions.Multiline)
            : Regex.Match(output ?? summary ?? "", @"(?:Applied change to|修改)\s+(.+?)(?:\.|$)", RegexOptions.Multiline);
        if (!pathMatch.Success) return;
        var added = 0;
        var removed = 0;
        if (!string.IsNullOrEmpty(diff))
        {
            foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
                if (line.StartsWith("+", StringComparison.Ordinal)) added++;
                if (line.StartsWith("-", StringComparison.Ordinal)) removed++;
            }
        }
        _currentAssistant.AddEditedFile(NormalizeEditedPath(pathMatch.Groups[1].Value), added, removed);
    }

    private static string WithDiffPreview(string details, string? diff)
    {
        var baseDetails = string.IsNullOrWhiteSpace(details) ? "请求审批的具体内容未提供。" : details;
        if (string.IsNullOrWhiteSpace(diff)) return baseDetails;
        return $"{baseDetails}\n\n{LimitForDisplay(diff, 12_000)}";
    }

    private string NormalizeEditedPath(string value)
    {
        var candidate = value.Trim().Trim('`').Replace('\\', '/');
        var workspace = SelectedConversation?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace)) workspace = WorkspacePath;
        if (!string.IsNullOrWhiteSpace(workspace) && Path.IsPathRooted(candidate))
        {
            try
            {
                var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(candidate);
                var relative = Path.GetRelativePath(root, full);
                if (!relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    candidate = relative.Replace('\\', '/');
            }
            catch { }
        }
        return candidate.TrimStart('/');
    }
    private void CompleteThinkingOperation(string status)
    {
        if (_thinkingProcess is not null) _thinkingProcess.Status = status;
        CompleteInlineThinkingOperations(status);
    }

    private void CompleteInlineThinkingOperations(string status)
    {
        if (_currentAssistant is not null)
        {
            foreach (var segment in _currentAssistant.Segments.Where(segment => segment.IsOperations))
            {
                var thinkingOperations = segment.Operations
                    .Where(operation => operation.IsThinking && operation.Status == "进行中")
                    .ToList();
                foreach (var operation in thinkingOperations)
                {
                    operation.Status = status;
                    operation.IsExpanded = false;
                }

                // Match completed run_command/apply_patch behavior: keep the
                // thinking text available on demand, but collapse the whole
                // operation group when thinking is its latest visible item.
                // If a tool was appended after thinking, leave that group's
                // current tool expansion untouched.
                if (thinkingOperations.Count > 0 && segment.LatestOperation?.IsThinking == true)
                    segment.IsExpanded = false;
            }
        }
        if (_inlineOperation?.IsThinking == true)
        {
            _inlineOperation.Status = status;
            _inlineOperation.IsExpanded = false;
        }
    }
    [RelayCommand]
    private async Task RefreshGitAsync()
    {
        try
        {
            await EnsureSessionAsync();
            await _pipe!.SendAsync(new GetGitOverviewMessage(_sessionId));
        }
        catch (Exception ex)
        {
            IsGitRepository = false;
            GitStatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ShowWorkingGitDiffAsync()
    {
        GitDiffIsStaged = false;
        await RequestGitDiffAsync("working", SelectedGitFile?.Path);
    }

    [RelayCommand]
    private async Task ShowStagedGitDiffAsync()
    {
        GitDiffIsStaged = true;
        await RequestGitDiffAsync("staged", SelectedGitFile?.Path);
    }

    partial void OnSelectedGitFileChanged(GitFileChange? value) => _ = RequestGitDiffAsync(GitDiffIsStaged ? "staged" : "working", value?.Path);

    private async Task RequestGitDiffAsync(string scope, string? relativePath)
    {
        try
        {
            await EnsureSessionAsync();
            await _pipe!.SendAsync(new GetGitDiffMessage(_sessionId, scope, relativePath));
        }
        catch (Exception ex)
        {
            GitDiffTruncated = false;
            GitDiffText = ex.Message;
        }
    }

    private void ApplyGitOverview(GitOverview? overview)
    {
        overview ??= new GitOverview { IsRepository = false, Message = "无法读取 Git 状态。" };
        IsGitRepository = overview.IsRepository;
        GitStatusMessage = overview.IsRepository ? "本地仓库状态（仅显示当前工作区，不访问网络）" : overview.Message ?? "当前工作区不在 Git 仓库中。";
        GitRoot = overview.Root ?? "";
        GitBranch = overview.Branch ?? "";
        GitTracking = overview.IsRepository ? $"{overview.Upstream ?? "未设置上游"} · ↑ {overview.Ahead}  ↓ {overview.Behind}" : "";
        ReplaceCollection(StagedGitChanges, overview.Staged);
        ReplaceCollection(UnstagedGitChanges, overview.Unstaged);
        ReplaceCollection(UntrackedGitChanges, overview.Untracked);
        ReplaceCollection(GitBranches, overview.Branches);
        ReplaceCollection(GitCommits, overview.Commits);
        ReplaceCollection(GitRemotes, overview.Remotes);
        if (!overview.IsRepository)
        {
            SelectedGitFile = null;
            GitDiffTruncated = false;
            GitDiffText = overview.Message ?? "当前工作区不在 Git 仓库中。";
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source is null) return;
        foreach (var item in source) target.Add(item);
    }
    private static string LimitForDisplay(string? value, int maxLength = 12_000)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
        return value[..maxLength].TrimEnd() + "\n…（输出已截断）";
    }

    private List<RestoreTranscriptMessage>? BuildRestoreTranscript(string? pendingPrompt)
    {
        if (SelectedConversation is null) return null;
        var messages = SelectedConversation.Messages
            .Where(message => (message.Role == "你" || message.Role == "Pi") && !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => new RestoreTranscriptMessage { Role = message.Role == "你" ? "user" : "assistant", Text = message.Text })
            .ToList();
        if (!string.IsNullOrWhiteSpace(pendingPrompt) && messages.Count > 0 && messages[^1].Role == "user" && string.Equals(messages[^1].Text, pendingPrompt, StringComparison.Ordinal)) messages.RemoveAt(messages.Count - 1);
        return messages.Count == 0 ? null : messages;
    }

    private static string FormatContextMetrics(HostEvent message)
    {
        var context = message.ContextTokens is long tokens && message.ContextWindow is long window
            ? $"上下文：{FormatTokenCount(tokens)} / {FormatTokenCount(window)}"
            : "上下文：未知";
        var cache = message.CacheStatsAvailable == true
            ? $"本轮缓存命中：{FormatTokenCount(message.CacheReadTokens ?? 0)}"
            : "服务未提供缓存统计";
        return $"{context} · {cache}";
    }

    private static string FormatTokenCount(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.#}m";
        if (value >= 1_000) return $"{value / 1_000d:0.#}k";
        return value.ToString("N0");
    }
    private void PersistConversations()
    {
        _persistTimer.Stop();
        _persistTimer.Start();
    }
    private void SaveConversationsNow()
    {
        try { ConversationStore.Save(Conversations); } catch { }
    }
    private void NotifyCanSendChanged() => SendCommand.NotifyCanExecuteChanged();
    partial void OnInputTextChanged(string value) { NotifyCanSendChanged(); UpdateCommandSuggestions(); }
    partial void OnWorkspacePathChanged(string value) => NotifyCanSendChanged();
    partial void OnAutoApproveSafeCommandsChanged(bool value) => OnPropertyChanged(nameof(ApprovalModeLabel));
    partial void OnAutoApproveGitOperationsChanged(bool value) => OnPropertyChanged(nameof(ApprovalModeLabel));
    partial void OnIsBusyChanged(bool value) => NotifyCanSendChanged();
    partial void OnIsProcessPanelVisibleChanged(bool value)
    {
        ProcessPanelWidth = value ? new WpfGridLength(310) : new WpfGridLength(0);
        OnPropertyChanged(nameof(ProcessPanelToggleGlyph));
    }

    private ProcessItem? _inlineOperation;
    private ChatSegment? _inlineSegment;

    private void StartInlineOperation(string title, string status, string? callId = null, string? commandLine = null)
    {
        if (_currentAssistant is null) return;
        _inlineOperation = new ProcessItem("", title, status, callId: callId, commandLine: commandLine);
        _inlineSegment = _currentAssistant.AppendOperation(_inlineOperation);
        if (string.Equals(title, "正在思考", StringComparison.Ordinal))
        {
            _inlineOperation.IsExpanded = true;
            _inlineSegment.IsExpanded = true;
        }
    }

    private void EnsureInlineOperation(string title, string status, string? callId = null)
    {
        if (_currentAssistant is null) return;
        if (!string.IsNullOrWhiteSpace(callId))
        {
            _inlineSegment = _currentAssistant.Segments.LastOrDefault(segment => segment.Operations.Any(item => item.CallId == callId));
            _inlineOperation = _inlineSegment?.Operations.LastOrDefault(item => item.CallId == callId);
        }
        if (callId is null && (_currentAssistant.Segments.LastOrDefault()?.IsText == true || _inlineOperation is null || _inlineOperation.Title != "正在思考"))
        {
            StartInlineOperation(title, status, callId);
            return;
        }
        if (_inlineOperation is null || !_currentAssistant.Segments.SelectMany(segment => segment.Operations).Contains(_inlineOperation) || (_inlineOperation.Status is "完成" or "失败" or "已取消"))
        {
            StartInlineOperation(title, status, callId);
            return;
        }
        _inlineOperation.Title = title;
        _inlineOperation.Status = status;
        if (string.Equals(title, "正在思考", StringComparison.Ordinal))
        {
            _inlineOperation.IsExpanded = true;
            if (_inlineSegment is not null) _inlineSegment.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void ChooseSlashCommand(SlashCommandItem? command)
    {
        if (command is null) return;
        InputText = $"/{command.Name} ";
        IsCommandMenuVisible = false;
    }

    [RelayCommand]
    private void ToggleProcessPanel() => IsProcessPanelVisible = !IsProcessPanelVisible;

    private void UpdateCommandSuggestions()
    {
        var value = InputText.TrimStart();
        FilteredSlashCommands.Clear();
        if (!value.StartsWith('/') || value.Contains(' ')) { IsCommandMenuVisible = false; return; }
        var prefix = value[1..];
        foreach (var command in SlashCommands.Where(item => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) FilteredSlashCommands.Add(command);
        IsCommandMenuVisible = FilteredSlashCommands.Count > 0;
    }

    private bool TryHandleLocalCommand(string text)
    {
        if (!text.StartsWith('/')) return false;
        var command = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        switch (command)
        {
            case "/new": _ = NewSessionAsync(); return true;
            case "/name":
                var nameParts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (nameParts.Length > 1 && SelectedConversation is not null)
                {
                    SelectedConversation.Title = nameParts[1].Trim();
                    CurrentConversationTitle = SelectedConversation.Title;
                    PersistConversations();
                    return true;
                }
                return false;
            case "/settings":
            case "/model": SettingsRequested?.Invoke(); return true;
            case "/quit": QuitRequested?.Invoke(); return true;
            default: return false;
        }
    }

    private static IEnumerable<SlashCommandItem> DefaultSlashCommands() => new[]
    {
        new SlashCommandItem("settings", "打开设置", "builtin"), new SlashCommandItem("model", "选择模型", "builtin"),
        new SlashCommandItem("scoped-models", "管理模型循环", "builtin"), new SlashCommandItem("export", "导出会话", "builtin"),
        new SlashCommandItem("import", "导入会话", "builtin"), new SlashCommandItem("share", "分享会话", "builtin"),
        new SlashCommandItem("copy", "复制最后一条回复", "builtin"), new SlashCommandItem("name", "设置会话名称", "builtin"),
        new SlashCommandItem("session", "显示会话信息", "builtin"), new SlashCommandItem("changelog", "查看更新日志", "builtin"),
        new SlashCommandItem("hotkeys", "查看快捷键", "builtin"), new SlashCommandItem("fork", "创建分支", "builtin"),
        new SlashCommandItem("clone", "复制会话", "builtin"), new SlashCommandItem("tree", "浏览会话树", "builtin"),
        new SlashCommandItem("login", "配置认证", "builtin"), new SlashCommandItem("logout", "移除认证", "builtin"),
        new SlashCommandItem("new", "新建会话", "builtin"), new SlashCommandItem("compact", "压缩上下文", "builtin"),
        new SlashCommandItem("resume", "恢复会话", "builtin"), new SlashCommandItem("reload", "重新加载", "builtin"),
        new SlashCommandItem("quit", "退出 IlMatto", "builtin"), new SlashCommandItem("help", "显示指令帮助", "builtin"),
        new SlashCommandItem("commands", "显示可用指令", "builtin")
    };

    public async ValueTask DisposeAsync()
    {
        _persistTimer.Stop();
        SaveConversationsNow();
        await _hostProcess.DisposeAsync();
    }
}
