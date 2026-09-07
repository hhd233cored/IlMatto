using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop;

public sealed record CodexApprovalPolicyChoice(string Value, string DisplayName, string Description);
public sealed record CodexSandboxModeChoice(string Value, string DisplayName, string Description);
public sealed record ReasoningEffortChoice(string Value, string DisplayName, string Description);
/// <summary>
/// A model entry shown in the model picker.  Antigravity may publish the
/// same base model as several slugs (for example, a -high and a -medium
/// variant).  Those slugs are kept here so the UI can show one model while
/// still sending the exact id expected by the CLI for the selected effort.
/// </summary>
public sealed record AgentModelChoice(
    string Provider,
    string ModelId,
    string DisplayName,
    string Description,
    IReadOnlyDictionary<string, string>? EffortVariants = null,
    string? BaseModelId = null)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyVariants = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Variants => EffortVariants ?? EmptyVariants;
    public string FamilyId => string.IsNullOrWhiteSpace(BaseModelId) ? ModelId : BaseModelId;
    public bool HasEffortVariants => Variants.Count > 0;

    public bool MatchesModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return string.IsNullOrWhiteSpace(ModelId);
        return string.Equals(ModelId, modelId, StringComparison.OrdinalIgnoreCase) ||
               Variants.Values.Any(value => string.Equals(value, modelId, StringComparison.OrdinalIgnoreCase));
    }

    public string ResolveModelId(string? effort)
    {
        if (!string.IsNullOrWhiteSpace(effort) && Variants.TryGetValue(effort.Trim().ToLowerInvariant(), out var variant))
            return variant;
        return ModelId;
    }
}

public sealed class AgentModelGroup
{
    public AgentModelGroup(string provider, string displayName, IEnumerable<AgentModelChoice> models)
    {
        Provider = provider;
        DisplayName = displayName;
        Models = new ObservableCollection<AgentModelChoice>(models);
    }

    public string Provider { get; }
    public string DisplayName { get; }
    public ObservableCollection<AgentModelChoice> Models { get; }
}

public partial class ManagerViewModel : ObservableObject, IAsyncDisposable
{
    private const string LegacyPiCredentialTarget = "IlMatto/OpenAICompatible/default";
    private const int MaxImageAttachmentsPerMessage = 8;
    private const long MaxImageAttachmentBytes = 20L * 1024 * 1024;
    private const long MaxImageAttachmentsBytes = 64L * 1024 * 1024;

    private sealed class PendingManagerBubble
    {
        public PendingManagerBubble(string role, string source)
        {
            Role = role;
            Source = source;
        }

        public string Role { get; }
        public string Source { get; }
        public StringBuilder PendingText { get; } = new();
        public ManagerChatEntry? Entry { get; set; }
        public bool IsComplete { get; set; }
        public string CompletionAction { get; set; } = "respond";
        public bool IsFinal { get; set; } = true;
        public bool HasText => PendingText.Length > 0 || !string.IsNullOrEmpty(Entry?.Text);
    }

    /// <summary>
    /// Runtime-only state for one manager conversation. Transcript messages
    /// are persisted on the conversation itself; these fields keep streams,
    /// approvals and typewriter queues isolated while another conversation is
    /// selected in the UI.
    /// </summary>
    private sealed class SessionUiState
    {
        public bool IsBusy;
        public string ManagerStatus { get; set; } = "准备就绪";
        public ManagerChatEntry? StreamingManager;
        public ManagerChatEntry? StreamingCoding;
        public ManagerChatEntry? StreamingManagerStatus;
        public ManagerConversationItem? StreamingManagerStatusConversation;
        public Queue<PendingManagerBubble> ManagerBubbles { get; set; } = new();
        public PendingManagerBubble? ManagerInputBubble;
        public StringBuilder PendingCodingText { get; set; } = new();
        public string? ActiveCodingTaskId;
        public string? PendingCodexDraftId;
        public List<ManagerActivity> Activities { get; } = new();
        public List<ApprovalRequest> PendingApprovals { get; } = new();
        public List<ManagerHostEvent> BufferedEvents { get; } = new();
        public bool CodexBusy;
    }

    private sealed class AntigravityModelBuilder
    {
        public AntigravityModelBuilder(string familyId, string displayName)
        {
            FamilyId = familyId;
            DisplayName = displayName;
        }

        public string FamilyId { get; }
        public string DisplayName { get; set; }
        public string? BaseModelId { get; set; }
        public Dictionary<string, string> Variants { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Resolve(string effort)
        {
            if (Variants.TryGetValue(effort, out var variant)) return variant;
            return BaseModelId ?? Variants.Values.FirstOrDefault() ?? FamilyId;
        }
    }

    private ManagerHostProcess? _host;
    private ManagerPipeClient? _pipe;
    private ManagerChatEntry? _streamingManager;
    private ManagerChatEntry? _streamingManagerStatus;
    private ManagerConversationItem? _streamingManagerStatusConversation;
    private ManagerChatEntry? _streamingCoding;
    private Queue<PendingManagerBubble> _managerBubbles = new();
    private PendingManagerBubble? _managerInputBubble;
    private DispatcherTimer? _managerTypewriterTimer;
    private DispatcherTimer? _codingTextTimer;
    private StringBuilder _pendingCodingText = new();
    private bool _initialized;
    private TaskCompletionSource<ManagerHostEvent>? _codexProbeCompletion;
    private TaskCompletionSource<ManagerHostEvent>? _codexLoginCompletion;
    private string? _activeCodingTaskId;
    /** Draft id associated with the @codex text currently in the input box. */
    private string? _pendingCodexDraftId;
    private DispatcherTimer? _draftSaveTimer;
    private bool _draftDirty;
    private bool _isRestoringDraft;
    private readonly Dictionary<string, SessionUiState> _sessionUiStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingSessionDeletions = new(StringComparer.Ordinal);
    private bool _replayingBackgroundEvents;
    private DispatcherTimer? _taskDurationTimer;
    private readonly ObservableCollection<ManagerChatEntry> _emptyChatEntries = new();
    private readonly HashSet<string> _hostStartedSessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activatingSessionIds = new(StringComparer.Ordinal);
    private long _selectionGeneration;

    public ManagerViewModel()
    {
        var settings = SettingsStore.Load();
        LoadSettings(settings);
        SelectedModelProvider = "antigravity";
        SelectedModelId = AntigravityModel;
        TaskTraceStore.CleanupExpired(settings.TaskTraceRetentionDays);
        PiApiKey = CredentialStore.Read(PiCredentialId) ?? CredentialStore.Read(LegacyPiCredentialTarget) ?? "";
        MainApiKey = CredentialStore.Read(MainApiCredentialId) ?? "";
        foreach (var conversation in ManagerConversationStore.Load())
        {
            NormalizeLegacyBinding(conversation);
            RefreshMessageTimeMetadata(conversation);
            Conversations.Add(conversation);
        }
        SortConversationsByLastUserMessage();
        if (Conversations.Count == 0) Conversations.Add(CreateConversation());
        SelectedConversation = Conversations.First();
    }

    public ObservableCollection<ManagerConversationItem> Conversations { get; } = new();
    public ObservableCollection<ManagerActivity> Activities { get; } = new();
    public ObservableCollection<ApprovalRequest> PendingApprovals { get; } = new();
    public ObservableCollection<ManagerImageAttachment> PendingImageAttachments { get; } = new();
    public ObservableCollection<TaskExecutorChoice> TaskExecutors { get; } = new()
    {
        new("default", "自动（按会话默认）"), new("antigravity", "Antigravity"), new("pi", "Pi"), new("codex", "Codex"),
    };
    /// <summary>Codex's native approval policies plus IlMatto's always extension.</summary>
    public ObservableCollection<CodexApprovalPolicyChoice> CodexApprovalPolicies { get; } = new()
    {
        new("untrusted", "untrusted", "对不在信任范围内的命令进行确认。"),
        new("on-request", "on-request", "需要权限或跨越沙箱边界时请求确认。"),
        new("never", "never", "不显示审批提示；受限操作可能失败。"),
        new("always", "always", "自动接受 Codex 发起的审批请求。"),
    };
    /// <summary>Codex's native sandbox modes.</summary>
    public ObservableCollection<CodexSandboxModeChoice> CodexSandboxModes { get; } = new()
    {
        new("read-only", "read-only", "只能查看文件，不能修改工作区。"),
        new("workspace-write", "workspace-write", "允许在工作区内编辑文件并运行常规命令。"),
        new("danger-full-access", "danger-full-access", "不限制文件系统或网络访问。"),
    };
    /// <summary>Antigravity's native permission presets surfaced by the CLI.</summary>
    public ObservableCollection<CodexApprovalPolicyChoice> AntigravityApprovalPolicies { get; } = new()
    {
        new("request-review", "request-review", "需要用户批准工具和高风险操作。"),
        new("proceed-in-sandbox", "proceed-in-sandbox", "在沙箱中执行，必要时请求批准。"),
        new("always-proceed", "always-proceed", "自动继续执行工具操作。"),
        new("strict", "strict", "严格限制工具操作。"),
    };
    public ObservableCollection<CodexSandboxModeChoice> AntigravitySandboxModes { get; } = new()
    {
        new("disabled", "disabled", "不启用终端沙箱。"),
        new("enabled", "enabled", "在终端沙箱中运行命令。"),
    };
    public ObservableCollection<ReasoningEffortChoice> AntigravityReasoningEfforts { get; } = new()
    {
        new("low", "low", "更快响应，使用较少推理。"),
        new("medium", "medium", "在速度与推理深度之间平衡。"),
        new("high", "high", "更充分推理，响应时间更长。"),
    };
    public ObservableCollection<ReasoningEffortChoice> CodexReasoningEfforts { get; } = new()
    {
        new("minimal", "minimal", "使用最少推理。"),
        new("low", "low", "更快响应，使用较少推理。"),
        new("medium", "medium", "在速度与推理深度之间平衡。"),
        new("high", "high", "更充分推理，响应时间更长。"),
        new("xhigh", "xhigh", "使用最高推理强度。"),
    };
    /// <summary>
    /// The model menu is intentionally populated with a safe Auto entry first;
    /// real provider models arrive asynchronously from the ManagerHost probe.
    /// </summary>
    public ObservableCollection<AgentModelGroup> AgentModelGroups { get; } = new()
    {
        new("antigravity", "Antigravity", new[]
        {
            new AgentModelChoice("antigravity", "", "Auto", "使用 Antigravity 默认模型。"),
        }),
        new("codex", "Codex", new[]
        {
            new AgentModelChoice("codex", "", "Auto", "使用 Codex 默认模型。"),
        }),
    };
    /// <summary>
    /// Complete in-memory message timeline for the selected conversation.
    /// The chat control virtualizes its visual containers; this collection is
    /// intentionally not paged so the scrollbar represents the full session.
    /// </summary>
    public ObservableCollection<ManagerChatEntry> ChatEntries => SelectedConversation?.Messages ?? _emptyChatEntries;

    private void ResetChatWindow(ManagerConversationItem? conversation)
    {
        OnPropertyChanged(nameof(ChatEntries));
    }

    [ObservableProperty] private ManagerConversationItem? selectedConversation;
    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private string selectedTaskExecutor = "default";
    [ObservableProperty] private string interactionResponseText = "";
    [ObservableProperty] private bool isBusy;
    /// <summary>Runtime-only visual throttle for the native chat timeline.</summary>
    [ObservableProperty] private bool isFastChatScrolling;
    /// <summary>
    /// Separates the white fast-scroll cover from Markdown construction. The
    /// cover remains up while the view restores only the visible documents,
    /// rather than releasing every deferred FlowDocument at once.
    /// </summary>
    [ObservableProperty] private bool isChatMarkdownRenderingDeferred;
    [ObservableProperty] private string managerStatus = "准备就绪";
    [ObservableProperty] private bool mainProviderAvailable;
    [ObservableProperty] private bool mainProviderAuthenticated;
    [ObservableProperty] private string mainProviderVersion = "";
    [ObservableProperty] private string mainProviderStatusDetail = "";
    [ObservableProperty] private string codingProviderStatusDetail = "";
    [ObservableProperty] private long? managerCacheReadTokens;
    [ObservableProperty] private long? managerContextTokens;
    [ObservableProperty] private long? managerContextWindow;
    [ObservableProperty] private bool isProcessDrawerOpen;

    [ObservableProperty] private string defaultMainAgentProvider = "antigravity";
    [ObservableProperty] private string defaultCodingAgentProvider = "antigravity";
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
    [ObservableProperty] private int antigravityTimeoutSeconds;
    [ObservableProperty] private string antigravityExecutionPolicy = "approval";
    [ObservableProperty] private string antigravityToolPermission = "always-proceed";
    [ObservableProperty] private bool antigravityTerminalSandbox;
    [ObservableProperty] private string codexCliPath = "codex";
    [ObservableProperty] private string codexModel = "";
    [ObservableProperty] private string codexEffort = "medium";
    [ObservableProperty] private string codexApprovalPolicy = "on-request";
    [ObservableProperty] private string codexSandboxMode = "workspace-write";
    [ObservableProperty] private string selectedModelProvider = "antigravity";
    [ObservableProperty] private string selectedModelId = "";
    [ObservableProperty] private bool isModelMenuOpen;
    [ObservableProperty] private string companionCharacterName = "";
    [ObservableProperty] private string companionCharacterPrompt = new ManagerCompanionProfile().CharacterPrompt;
    [ObservableProperty] private string userId = "用户";
    [ObservableProperty] private string userAvatarPath = "";
    [ObservableProperty] private string agentAvatarPath = "";
    [ObservableProperty] private BitmapImage? userAvatarImage;
    [ObservableProperty] private BitmapImage? agentAvatarImage;
    // Legacy setting retained only as a one-time seed for profile.md.
    [ObservableProperty] private string companionUserProfile = "";

    public string ApiKey { get => PiApiKey; set => PiApiKey = value; }
    public ApprovalRequest? CurrentApproval => PendingApprovals.FirstOrDefault();
    public bool HasApproval => CurrentApproval is not null;
    public string PendingApprovalCountLabel => PendingApprovals.Count > 1 ? $"另有 {PendingApprovals.Count - 1} 项等待确认" : "";
    public bool CanSend => !IsBusy && (!string.IsNullOrWhiteSpace(InputText) || PendingImageAttachments.Count > 0);
    public bool CanCancel => IsBusy || (SelectedConversation is not null && GetSessionUiState(SelectedConversation).CodexBusy);
    public bool CanVerifyLastCodexTask => !IsBusy && LastCompletedCodexTaskId is not null;
    private string? LastCompletedCodexTaskId => SelectedConversation?.Messages
        .LastOrDefault(item => item.Source == "codex" && item.CodeResult?.Status == "completed" && !string.IsNullOrWhiteSpace(item.TaskId))?.TaskId;
    public bool HasPendingImageAttachments => PendingImageAttachments.Count > 0;
    public bool HasUserAvatar => UserAvatarImage is not null;
    public bool HasAgentAvatar => AgentAvatarImage is not null;
    public string UserAvatarText => FirstAvatarCharacter(UserId, "你");
    public bool IsCodexCodingAgent => SelectedConversation?.CodingAgent?.Provider == "codex";
    public string MainConnectionLabel => !MainProviderAvailable ? $"{CurrentMainDisplayName} 不可用" : MainProviderAuthenticated ? $"{CurrentMainDisplayName} 已连接 {MainProviderVersion}".Trim() : $"{CurrentMainDisplayName} 等待认证";
    public string ManagerCacheLabel => ManagerCacheReadTokens is long tokens ? $"缓存读取：{tokens:N0} tokens" : "缓存统计：服务未提供";
    public string ManagerContextLabel => ManagerContextTokens is long used ? $"上下文：{used:N0}{(ManagerContextWindow is long window ? $" / {window:N0}" : "")} tokens" : "上下文统计：服务未提供";
    public string WorkspaceLabel => string.IsNullOrWhiteSpace(SelectedConversation?.WorkspacePath) ? "未选择工作区" : SelectedConversation.WorkspacePath;
    public string CurrentCompanionName => SelectedConversation?.CompanionDisplayName ?? (string.IsNullOrWhiteSpace(CompanionCharacterName) ? "角色" : CompanionCharacterName.Trim());
    public string CurrentCompanionTitle => $"IlMatto · {CurrentCompanionName}";
    public string CurrentMainDisplayName => CurrentCompanionName;
    public string CurrentCodingDisplayName => SelectedConversation?.CodingAgent?.DisplayName ?? "Pi";
    public string CurrentProviderLabel => SelectedConversation?.ProviderLabel ?? $"{CurrentCompanionName}（Antigravity） → Pi";
    public string SelectedModelLabel
    {
        get
        {
            var group = AgentModelGroups.FirstOrDefault(item => item.Provider == SelectedModelProvider);
            var choice = group?.Models.FirstOrDefault(item => item.MatchesModelId(SelectedModelId));
            return choice?.DisplayName ?? (string.IsNullOrWhiteSpace(SelectedModelId) ? "Auto" : SelectedModelId);
        }
    }
    public string SelectedModelProviderLabel => SelectedModelProvider == "codex" ? "Codex" : "Antigravity";
    public string AntigravitySandboxMode
    {
        get => AntigravityTerminalSandbox ? "enabled" : "disabled";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            AntigravityTerminalSandbox = string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase);
        }
    }
    public string AntigravityApprovalPolicy
    {
        get => AntigravityToolPermission;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            AntigravityToolPermission = NormalizeAntigravityToolPermission(value);
        }
    }
    public bool IsCodexModelSelected => SelectedModelProvider == "codex";
    public bool IsAntigravityModelSelected => !IsCodexModelSelected;

    public event Action? SettingsRequested;
    public event Action? OpenPiWorkbenchRequested;
    public event Action? UserMessageSent;

    [RelayCommand]
    private void ToggleProcessDrawer()
    {
        IsProcessDrawerOpen = !IsProcessDrawerOpen;
    }

    [RelayCommand]
    private void CloseDrawers()
    {
        IsProcessDrawerOpen = false;
    }

    [RelayCommand]
    private void AddImageAttachment()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送给 Agent 的图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.svg",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var selectedPath in dialog.FileNames)
        {
            try
            {
                var attachment = ManagerImageAttachment.FromPath(selectedPath);
                var fileInfo = new FileInfo(attachment.Path);
                if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.Directory) != 0)
                    throw new InvalidOperationException("所选路径不是普通文件。");
                if (fileInfo.Length > MaxImageAttachmentBytes)
                    throw new InvalidOperationException("单张图片不能超过 20 MiB。");
                if (PendingImageAttachments.Any(item => string.Equals(item.Path, attachment.Path, StringComparison.OrdinalIgnoreCase))) continue;
                if (PendingImageAttachments.Count >= MaxImageAttachmentsPerMessage)
                    throw new InvalidOperationException("单条消息最多添加 8 张图片。");
                if (PendingImageAttachments.Sum(item => File.Exists(item.Path) ? new FileInfo(item.Path).Length : 0) + fileInfo.Length > MaxImageAttachmentsBytes)
                    throw new InvalidOperationException("单条消息的图片总大小不能超过 64 MiB。");
                attachment.Order = PendingImageAttachments.Count;
                PendingImageAttachments.Add(attachment);
            }
            catch (Exception exception) { AddSystemMessage($"无法添加图片：{exception.Message}"); }
        }
        NotifyPendingImageAttachmentsChanged();
    }

    public bool TryAddClipboardImage()
    {
        string? stagedPath = null;
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return false;
            var image = System.Windows.Clipboard.GetImage();
            if (image is null) return false;
            var stagingDirectory = GetAttachmentStagingDirectory();
            Directory.CreateDirectory(stagingDirectory);
            stagedPath = Path.Combine(stagingDirectory, $"clipboard-{Guid.NewGuid():N}.png");
            using (var stream = File.Create(stagedPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            var attachment = ManagerImageAttachment.FromPath(stagedPath);
            attachment.DisplayName = "粘贴的图片.png";
            attachment.IsStaged = true;
            AddPendingAttachment(attachment);
            return true;
        }
        catch (Exception exception)
        {
            if (stagedPath is not null) { try { File.Delete(stagedPath); } catch { } }
            AddSystemMessage($"无法读取剪贴板图片：{exception.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void PasteImageAttachment() => TryAddClipboardImage();

    private void AddPendingAttachment(ManagerImageAttachment attachment)
    {
        var fileInfo = new FileInfo(attachment.Path);
        if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.Directory) != 0)
            throw new InvalidOperationException("剪贴板内容不是普通图片文件。");
        if (fileInfo.Length > MaxImageAttachmentBytes)
            throw new InvalidOperationException("单张图片不能超过 20 MiB。");
        if (PendingImageAttachments.Count >= MaxImageAttachmentsPerMessage)
            throw new InvalidOperationException("单条消息最多添加 8 张图片。");
        if (PendingImageAttachments.Sum(item => File.Exists(item.Path) ? new FileInfo(item.Path).Length : 0) + fileInfo.Length > MaxImageAttachmentsBytes)
            throw new InvalidOperationException("单条消息的图片总大小不能超过 64 MiB。");
        attachment.Order = PendingImageAttachments.Count;
        PendingImageAttachments.Add(attachment);
        NotifyPendingImageAttachmentsChanged();
    }

    [RelayCommand]
    private void RemoveImageAttachment(ManagerImageAttachment? attachment)
    {
        if (attachment is null || !PendingImageAttachments.Remove(attachment)) return;
        DeleteStagedAttachment(attachment);
        ReindexPendingAttachments();
        NotifyPendingImageAttachmentsChanged();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _taskDurationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _taskDurationTimer.Tick += (_, _) => RefreshTaskDurations();
        _draftSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _draftSaveTimer.Tick += (_, _) => SaveDraftIfDirty();
        RestoreDraft(SelectedConversation);
        ResetDraftSaveTimer();
        await ActivateSelectedConversationAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var conversation = SelectedConversation;
        var text = InputText.Trim();
        var attachments = PendingImageAttachments.ToList();
        if (conversation is null || (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)) return;
        List<ManagerImageAttachment> storedAttachments;
        try
        {
            storedAttachments = CopyAttachmentsToConversation(conversation, attachments);
            foreach (var attachment in attachments) DeleteStagedAttachment(attachment);
        }
        catch (Exception exception)
        {
            AddSystemMessage($"无法保存图片：{exception.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) text = "请根据附加图片处理这个任务。";
        var draftId = HasCodexDirective(text) ? _pendingCodexDraftId : null;
        _pendingCodexDraftId = null;
        InputText = "";
        PendingImageAttachments.Clear();
        NotifyPendingImageAttachmentsChanged();
        var generateTitle = conversation.Title == "新对话";
        if (generateTitle) conversation.Title = text.Length > 32 ? text[..32] + "…" : text;
        AppendMessage(conversation, new ManagerChatEntry("你", "user", text, attachments: storedAttachments));
        conversation.NotifyMessageTimelineChanged();
        conversation.UpdatedAt = DateTime.Now;
        SortConversationsByLastUserMessage();
        UserMessageSent?.Invoke();
        var executor = string.Equals(SelectedTaskExecutor, "default", StringComparison.OrdinalIgnoreCase) ? null : SelectedTaskExecutor;
        var isCodexTurn = HasCodexDirective(text) || string.Equals(executor, "codex", StringComparison.OrdinalIgnoreCase);
        ResetManagerTypewriter();
        _streamingManager = null;
        // A normal Antigravity message may be sent while Codex is running.
        // Keep the live Codex entry so later deltas continue in the same
        // bubble instead of splitting the task into a second transcript item.
        IsBusy = !isCodexTurn;
        ManagerStatus = isCodexTurn ? "Codex 任务正在启动" : $"{CurrentMainDisplayName} 正在协调";
        Save();
        // Reserve the manager reply's position before the asynchronous host
        // request starts. Codex progress can arrive before Antigravity emits
        // its first visible delta; without a placeholder that race inserts the
        // Codex bubble above the manager bubble in the transcript.
        // Explicit Codex turns have no manager reply, so they must not reserve
        // an empty Antigravity bubble in the transcript.
        if (!HasCodexDirective(text) && !string.Equals(executor, "codex", StringComparison.OrdinalIgnoreCase))
            ReserveManagerBubble(conversation);
        try
        {
            await EnsureSessionReadyAsync(conversation);
            var messageAttachments = storedAttachments.Select(attachment => new ManagerImageAttachmentMessage(
                attachment.Path, attachment.DisplayName, attachment.MimeType, "image", attachment.AttachmentId, attachment.Order)).ToList();
            await _pipe!.SendAsync(new SendManagerMessage(conversation.SessionId, text, messageAttachments, executor, draftId, generateTitle));
        }
        catch (Exception exception) { AddSystemMessage($"无法发送：{exception.Message}"); IsBusy = false; ManagerStatus = "错误"; }
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var conversation = CreateConversation();
        Conversations.Insert(0, conversation); SelectedConversation = conversation; Save();
    }

    public void RenameSelectedConversation(string title)
    {
        var conversation = SelectedConversation;
        var normalized = title.Trim();
        if (conversation is null || normalized.Length == 0) return;
        conversation.Title = normalized.Length > 120 ? normalized[..120] + "…" : normalized;
        Save();
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ManagerConversationItem? conversation)
    {
        if (conversation is null) return;
        var providerDeleteRequested = false;
        try
        {
            if (_pipe is not null)
            {
                if (_pendingSessionDeletions.ContainsKey(conversation.SessionId))
                {
                    AddSystemMessage(conversation, "该会话正在删除，请稍候。");
                    return;
                }

                var deletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingSessionDeletions[conversation.SessionId] = deletion;
                await _pipe.SendAsync(new DeleteManagerSessionMessage(
                    conversation.SessionId, conversation.WorkspacePath, BuildMainConfig(conversation, false), BuildCodingConfig(conversation, false),
                    conversation.CodingAgent?.Provider == "pi" ? conversation.CodingAgent.SessionRef : conversation.PiSessionFile,
                    conversation.MainAgent?.Provider == "openai_compatible" ? conversation.MainAgent.SessionRef : null,
                    conversation.CodingAgent?.Provider == "codex" ? conversation.CodingAgent.SessionRef : null));
                providerDeleteRequested = true;
                await deletion.Task.WaitAsync(TimeSpan.FromSeconds(15));
                _pendingSessionDeletions.Remove(conversation.SessionId);
            }
        }
        catch (Exception exception)
        {
            _pendingSessionDeletions.Remove(conversation.SessionId);
            var prefix = exception is TimeoutException ? "删除会话超时" : providerDeleteRequested ? "Manager Host 删除会话失败" : "删除 Provider 会话失败";
            try { await ManagerSessionDataStore.DeleteSessionDataAsync(conversation.SessionId); }
            catch (Exception cleanupException) { prefix += $"；本地记忆清理也失败：{cleanupException.Message}"; }
            AddSystemMessage(conversation, $"{prefix}，会话已保留，可稍后重试：{exception.Message}");
            return;
        }

        var cleanupFailures = new List<string>();
        if (!ConversationStore.TryDeletePiSession(conversation.CodingAgent?.Provider == "pi" ? conversation.CodingAgent.SessionRef : conversation.PiSessionFile))
            cleanupFailures.Add("Pi 会话文件未能删除");
        if (!ManagerSessionDataStore.TryDeleteCoordinatorSession(conversation.MainAgent?.Provider == "openai_compatible" ? conversation.MainAgent.SessionRef : null))
            cleanupFailures.Add("API Manager 会话文件未能删除");
        try { await ManagerSessionDataStore.DeleteSessionDataAsync(conversation.SessionId); }
        catch (Exception exception) { cleanupFailures.Add($"本地会话数据未能删除：{exception.Message}"); }
        if (cleanupFailures.Count > 0)
        {
            AddSystemMessage(conversation, $"删除会话未完全成功，会话已保留，可稍后重试：{string.Join("；", cleanupFailures)}");
            return;
        }

        var mainCredential = conversation.MainAgent?.CredentialId;
        var codingCredential = conversation.CodingAgent?.CredentialId;
        TaskTraceStore.DeleteConversation(conversation.SessionId);
        var index = Conversations.IndexOf(conversation); Conversations.Remove(conversation);
        if (Conversations.Count == 0) Conversations.Add(CreateConversation());
        if (ReferenceEquals(SelectedConversation, conversation)) SelectedConversation = Conversations[Math.Clamp(index, 0, Conversations.Count - 1)];
        CleanupCredentialIfUnused(mainCredential); CleanupCredentialIfUnused(codingCredential);
        Save();
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
        StopManagerTypewriter();
        if (SelectedConversation is not null && _pipe is not null)
        {
            var runtime = GetSessionUiState(SelectedConversation);
            var target = runtime.IsBusy ? "antigravity" : runtime.CodexBusy ? "codex" : "all";
            await _pipe.SendAsync(new CancelManagerTurnMessage(SelectedConversation.SessionId, target));
        }
        IsBusy = false; ManagerStatus = "已取消";
    }

    [RelayCommand(CanExecute = nameof(CanVerifyLastCodexTask))]
    private async Task VerifyLastCodexTaskAsync()
    {
        var conversation = SelectedConversation;
        var taskId = LastCompletedCodexTaskId;
        if (conversation is null || string.IsNullOrWhiteSpace(taskId)) return;
        try
        {
            await EnsureSessionReadyAsync(conversation);
            IsBusy = true; ManagerStatus = "正在启动 Antigravity 独立验证";
            await _pipe!.SendAsync(new RequestVerificationMessage(conversation.SessionId, taskId));
        }
        catch (Exception exception)
        {
            AddSystemMessage($"无法启动验证：{exception.Message}"); IsBusy = false;
        }
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
        // The settings window has already copied its editable role-card text
        // into settings.DefaultCompanionProfile. Do not overwrite it with
        // the ViewModel's pre-dialog value here; doing so made every role-card
        // edit appear to save successfully while persisting the old text.
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
        await EnsureSessionReadyAsync(conversation);
        _codexProbeCompletion = new TaskCompletionSource<ManagerHostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pipe!.SendAsync(new ProbeCodexMessage(conversation.SessionId, executable, conversation.WorkspacePath));
        return await _codexProbeCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task<ManagerHostEvent> StartCodexLoginAsync(string executable)
    {
        var conversation = SelectedConversation ?? throw new InvalidOperationException("没有活动会话。");
        await EnsureSessionReadyAsync(conversation);
        _codexLoginCompletion = new TaskCompletionSource<ManagerHostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pipe!.SendAsync(new StartCodexLoginMessage(conversation.SessionId, executable, conversation.WorkspacePath));
        return await _codexLoginCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task EnsureHostAsync()
    {
        if (_pipe is null)
        {
            _hostStartedSessionIds.Clear();
            _activatingSessionIds.Clear();
            _host = new ManagerHostProcess(); _pipe = await _host.StartAsync();
            _pipe.EventReceived += OnHostEvent; _pipe.TransportError += OnTransportError;
        }
    }

    private async Task StartOrSyncSessionAsync(ManagerConversationItem conversation)
    {
        await EnsureHostAsync();
        await _pipe!.SendAsync(new StartManagerSessionMessage(
        conversation.SessionId, conversation.WorkspacePath,
        BuildMainConfig(conversation, true), BuildCodingConfig(conversation, true),
        conversation.Messages.LastOrDefault(message => message.CodeResult is not null)?.CodeResult is { NeedsUserDecision: true } or { Status: "blocked" },
        conversation.CompanionProfile, BuildConversationHistory(conversation), BuildExecutorProfiles(conversation, true)));
        _hostStartedSessionIds.Add(conversation.SessionId);
    }

    private async Task ActivateCachedSessionAsync(ManagerConversationItem conversation)
    {
        await EnsureHostAsync();
        if (!_hostStartedSessionIds.Contains(conversation.SessionId))
        {
            await StartOrSyncSessionAsync(conversation);
            return;
        }
        _activatingSessionIds.Add(conversation.SessionId);
        try { await _pipe!.SendAsync(new ActivateManagerSessionMessage(conversation.SessionId)); }
        catch { _activatingSessionIds.Remove(conversation.SessionId); throw; }
    }

    private async Task EnsureSessionReadyAsync(ManagerConversationItem conversation)
    {
        await EnsureHostAsync();
        if (_hostStartedSessionIds.Contains(conversation.SessionId))
        {
            _activatingSessionIds.Add(conversation.SessionId);
            try { await _pipe!.SendAsync(new ActivateManagerSessionMessage(conversation.SessionId)); }
            catch { _activatingSessionIds.Remove(conversation.SessionId); throw; }
            return;
        }
        await StartOrSyncSessionAsync(conversation);
    }

    private static IReadOnlyList<ManagerCompanionHistoryMessage> BuildConversationHistory(ManagerConversationItem conversation)
    {
        var visible = conversation.Messages
            .Where(message => message.IsUser || message.Source is "antigravity" or "api_manager")
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => new ManagerCompanionHistoryMessage(message.IsUser ? "user" : "assistant", message.Text))
            .TakeLast(40)
            .ToList();

        const int maxCharacters = 32_000;
        var remaining = maxCharacters;
        var result = new List<ManagerCompanionHistoryMessage>(visible.Count);
        foreach (var item in visible.AsEnumerable().Reverse())
        {
            if (remaining <= 0) break;
            var text = item.Text.Length <= remaining ? item.Text : item.Text[..remaining];
            result.Add(new ManagerCompanionHistoryMessage(item.Role, text));
            remaining -= text.Length;
        }
        result.Reverse();
        return result;
    }

    private SessionUiState GetSessionUiState(ManagerConversationItem conversation)
    {
        if (!_sessionUiStates.TryGetValue(conversation.SessionId, out var state))
        {
            state = new SessionUiState();
            _sessionUiStates[conversation.SessionId] = state;
        }
        return state;
    }

    private void CaptureSessionUiState(ManagerConversationItem conversation)
    {
        var state = GetSessionUiState(conversation);
        state.IsBusy = IsBusy;
        state.ManagerStatus = ManagerStatus;
        state.StreamingManager = _streamingManager;
        state.StreamingCoding = _streamingCoding;
        state.StreamingManagerStatus = _streamingManagerStatus;
        state.StreamingManagerStatusConversation = _streamingManagerStatusConversation;
        state.ManagerBubbles = _managerBubbles;
        state.ManagerInputBubble = _managerInputBubble;
        state.PendingCodingText = _pendingCodingText;
        state.ActiveCodingTaskId = _activeCodingTaskId;
        state.PendingCodexDraftId = _pendingCodexDraftId;
        state.CodexBusy = state.CodexBusy || _activeCodingTaskId is not null;
        state.Activities.Clear();
        state.Activities.AddRange(Activities);
        state.PendingApprovals.Clear();
        state.PendingApprovals.AddRange(PendingApprovals);
    }

    private void RestoreSessionUiState(ManagerConversationItem? conversation)
    {
        _managerTypewriterTimer?.Stop();
        _codingTextTimer?.Stop();
        _managerBubbles = new();
        _managerInputBubble = null;
        _pendingCodingText = new StringBuilder();
        _streamingManager = null;
        _streamingCoding = null;
        _streamingManagerStatus = null;
        _streamingManagerStatusConversation = null;
        _activeCodingTaskId = null;
        _pendingCodexDraftId = null;
        Activities.Clear();
        PendingApprovals.Clear();

        if (conversation is null)
        {
            IsBusy = false;
            ManagerStatus = "准备就绪";
            NotifyApprovalChanged();
            OnPropertyChanged(nameof(CanCancel));
            return;
        }

        var state = GetSessionUiState(conversation);
        IsBusy = state.IsBusy;
        ManagerStatus = state.ManagerStatus;
        _streamingManager = state.StreamingManager;
        _streamingCoding = state.StreamingCoding;
        _streamingManagerStatus = state.StreamingManagerStatus;
        _streamingManagerStatusConversation = state.StreamingManagerStatusConversation;
        _managerBubbles = state.ManagerBubbles;
        _managerInputBubble = state.ManagerInputBubble;
        _pendingCodingText = state.PendingCodingText;
        _activeCodingTaskId = state.ActiveCodingTaskId;
        _pendingCodexDraftId = state.PendingCodexDraftId;
        foreach (var activity in state.Activities) Activities.Add(activity);
        foreach (var approval in state.PendingApprovals) PendingApprovals.Add(approval);
        NotifyApprovalChanged();
        OnPropertyChanged(nameof(CanCancel));
        if (_managerBubbles.Count > 0) EnsureManagerTypewriterStarted();
        if (_pendingCodingText.Length > 0) EnsureCodingTextTimerStarted();
    }

    private void ReplayBufferedEvents(ManagerConversationItem conversation)
    {
        var state = GetSessionUiState(conversation);
        if (state.BufferedEvents.Count == 0) return;
        var events = state.BufferedEvents.ToList();
        state.BufferedEvents.Clear();
        _replayingBackgroundEvents = true;
        try
        {
            foreach (var message in events) HandleHostEvent(message);
        }
        finally { _replayingBackgroundEvents = false; }
    }

    private void SyncSessionUiState(ManagerConversationItem conversation)
    {
        var state = GetSessionUiState(conversation);
        state.IsBusy = IsBusy;
        state.ManagerStatus = ManagerStatus;
        state.StreamingManager = _streamingManager;
        state.StreamingCoding = _streamingCoding;
        state.StreamingManagerStatus = _streamingManagerStatus;
        state.StreamingManagerStatusConversation = _streamingManagerStatusConversation;
        state.ManagerBubbles = _managerBubbles;
        state.ManagerInputBubble = _managerInputBubble;
        state.PendingCodingText = _pendingCodingText;
        state.ActiveCodingTaskId = _activeCodingTaskId;
        state.PendingCodexDraftId = _pendingCodexDraftId;
        state.Activities.Clear();
        state.Activities.AddRange(Activities);
        state.PendingApprovals.Clear();
        state.PendingApprovals.AddRange(PendingApprovals);
        if (_activeCodingTaskId is not null) state.CodexBusy = true;
        EnsureTaskDurationTimer();
        OnPropertyChanged(nameof(CanCancel));
    }

    private void EnsureTaskDurationTimer()
    {
        if (_taskDurationTimer is null) return;
        if (!_taskDurationTimer.IsEnabled) _taskDurationTimer.Start();
    }

    private void RefreshTaskDurations()
    {
        var runtimes = new HashSet<TaskRuntimeInfo>();
        foreach (var conversation in Conversations)
        {
            foreach (var message in conversation.Messages)
                if (message.Runtime is not null) runtimes.Add(message.Runtime);
            if (_sessionUiStates.TryGetValue(conversation.SessionId, out var state))
            {
                if (state.StreamingManager?.Runtime is not null) runtimes.Add(state.StreamingManager.Runtime);
                if (state.StreamingCoding?.Runtime is not null) runtimes.Add(state.StreamingCoding.Runtime);
                if (state.StreamingManagerStatus?.Runtime is not null) runtimes.Add(state.StreamingManagerStatus.Runtime);
                foreach (var activity in state.Activities)
                    if (activity.Runtime is not null) runtimes.Add(activity.Runtime);
            }
        }
        foreach (var activity in Activities)
            if (activity.Runtime is not null) runtimes.Add(activity.Runtime);
        var active = false;
        foreach (var runtime in runtimes)
        {
            runtime.RefreshElapsed();
            active |= runtime.IsActive;
        }
        if (!active) _taskDurationTimer?.Stop();
    }

    private void ApplyManagerTaskTiming(ManagerHostEvent message)
    {
        var entry = _streamingManager;
        if (entry is null || string.IsNullOrWhiteSpace(message.StartedAt)) return;
        if (!DateTimeOffset.TryParse(message.StartedAt, out var startedAt)) return;
        if (entry.Runtime is null || !string.Equals(entry.Runtime.TurnId, message.TurnId, StringComparison.Ordinal))
            entry.Runtime = new TaskRuntimeInfo(null, message.TurnId, startedAt, message.State ?? "responding");
        var terminal = message.State is "idle" or "cancelled" or "error";
        if (terminal)
        {
            var ended = DateTimeOffset.TryParse(message.CompletedAt, out var completedAt) ? completedAt : DateTimeOffset.UtcNow;
            entry.Runtime.Mark(message.State ?? "idle", ended, message.DurationMs);
        }
        else
        {
            entry.Runtime.Mark(message.State ?? "responding");
            EnsureTaskDurationTimer();
        }
    }

    private void ApplyCodingTaskTiming(ManagerHostEvent message, ManagerChatEntry? entry = null)
    {
        var target = entry ?? _streamingCoding;
        if (target is null || string.IsNullOrWhiteSpace(message.StartedAt)) return;
        if (!DateTimeOffset.TryParse(message.StartedAt, out var startedAt)) return;
        var runtimeState = RuntimeStateForCoding(message.Status);
        if (target.Runtime is null || !string.Equals(target.Runtime.TaskId, message.TaskId, StringComparison.Ordinal))
            target.Runtime = new TaskRuntimeInfo(message.TaskId, null, startedAt, runtimeState);
        var terminal = message.Status is "completed" or "failed" or "cancelled" or "partial";
        if (terminal)
        {
            var ended = DateTimeOffset.TryParse(message.CompletedAt, out var completedAt) ? completedAt : DateTimeOffset.UtcNow;
            target.Runtime.Mark(runtimeState, ended, message.DurationMs);
        }
        else
        {
            target.Runtime.Mark(runtimeState);
            EnsureTaskDurationTimer();
        }
    }

    private static string RuntimeStateForCoding(string? state) => state switch
    {
        "queued" => "queued",
        "awaiting_user_input" => "awaiting_user_input",
        "completed" => "completed",
        "failed" => "failed",
        "cancelled" => "cancelled",
        "partial" => "partial",
        _ => "running",
    };

    private void RecordBackgroundEvent(ManagerConversationItem conversation, ManagerHostEvent message)
    {
        var state = GetSessionUiState(conversation);
        state.BufferedEvents.Add(message);
        switch (message.Type)
        {
            case "manager_state":
                state.IsBusy = message.State is "routing" or "responding" or "waiting_approval";
                state.ManagerStatus = message.State switch
                {
                    "responding" => "正在回答",
                    "routing" => "正在协调",
                    "waiting_approval" => "等待确认",
                    "cancelled" => "已取消",
                    "error" => "错误",
                    _ => "准备就绪",
                };
                break;
            case "delegation_started":
                state.CodexBusy = true;
                state.ActiveCodingTaskId = message.TaskId;
                break;
            case "coding_delta":
            case "coding_thinking_delta":
            case "coding_tool_started":
            case "coding_tool_output":
            case "coding_tool_completed":
                state.CodexBusy = true;
                state.ActiveCodingTaskId ??= message.TaskId;
                break;
            case "coding_completed":
                state.CodexBusy = false;
                state.ActiveCodingTaskId = null;
                break;
            case "provider_status" when message.Layer == "coding":
                state.CodexBusy = message.Policy is "queued" or "running" or "awaiting_user_input";
                break;
            case "manager_completed":
                state.IsBusy = false;
                break;
            case "manager_title" when !string.IsNullOrWhiteSpace(message.Title):
                conversation.Title = message.Title.Trim();
                break;
            case "manager_error":
                if (string.Equals(message.Provider, "codex", StringComparison.Ordinal))
                {
                    state.CodexBusy = false;
                    state.ActiveCodingTaskId = null;
                }
                else state.IsBusy = false;
                break;
        }
    }

    private Task ActivateSelectedConversationAsync()
    {
        var conversation = SelectedConversation;
        ResetChatWindow(conversation);
        RestoreSessionUiState(conversation);
        NotifyCurrentProviderChanged();
        if (conversation is null) return Task.CompletedTask;
        ReplayBufferedEvents(conversation);
        var generation = ++_selectionGeneration;
        _ = ActivateConversationInBackgroundAsync(conversation, generation);
        return Task.CompletedTask;
    }

    private async Task ActivateConversationInBackgroundAsync(ManagerConversationItem conversation, long generation)
    {
        try
        {
            await ActivateCachedSessionAsync(conversation);
        }
        catch (Exception exception)
        {
            _activatingSessionIds.Remove(conversation.SessionId);
            if (generation == _selectionGeneration)
                AddSystemMessage(conversation, $"Manager Host 启动失败：{exception.Message}");
        }
    }

    private void OnHostEvent(object? sender, ManagerHostEvent message) => System.Windows.Application.Current.Dispatcher.Invoke(() => HandleHostEvent(message));

    private void HandleHostEvent(ManagerHostEvent message)
    {
        if (message.Type == "manager_host_ready")
        {
            _hostStartedSessionIds.Clear();
            _activatingSessionIds.Clear();
            return;
        }

        if (message.Type == "manager_session_ready" && message.SessionId is not null)
        {
            _hostStartedSessionIds.Add(message.SessionId);
            _activatingSessionIds.Remove(message.SessionId);
        }

        if (message.Type == "manager_error" && message.Code == "SESSION_NOT_ACTIVE" && message.SessionId is not null && _activatingSessionIds.Remove(message.SessionId))
        {
            _hostStartedSessionIds.Remove(message.SessionId);
            var missingConversation = Conversations.FirstOrDefault(item => string.Equals(item.SessionId, message.SessionId, StringComparison.Ordinal));
            if (missingConversation is not null)
                _ = StartSessionAfterActivationMissAsync(missingConversation);
            return;
        }

        if (message.SessionId is not null && message.Type is ("manager_session_deleted" or "manager_error") && _pendingSessionDeletions.TryGetValue(message.SessionId, out var deletion))
        {
            if (message.Type == "manager_session_deleted") deletion.TrySetResult(true);
            else deletion.TrySetException(new InvalidOperationException($"{message.Code ?? "MANAGER_ERROR"}: {message.Message ?? "Manager Host 删除会话失败。"}"));
            return;
        }

        var conversation = message.SessionId is null
            ? SelectedConversation
            : Conversations.FirstOrDefault(item => string.Equals(item.SessionId, message.SessionId, StringComparison.Ordinal));
        if (conversation is null) return;
        if (!ReferenceEquals(conversation, SelectedConversation) && !_replayingBackgroundEvents)
        {
            RecordBackgroundEvent(conversation, message);
            Save();
            return;
        }
        switch (message.Type)
        {
            case "manager_session_ready":
                if (conversation.MainAgent is not null)
                {
                    if (!string.IsNullOrWhiteSpace(message.MainSessionRef)) conversation.MainAgent.SessionRef = message.MainSessionRef;
                    if (conversation.MainAgent.Provider == "antigravity")
                    {
                        conversation.MainAgent.Transport = "cli";
                        conversation.MainAgent.LegacyCliConversationId = message.AgyConversationId ?? message.MainSessionRef ?? conversation.MainAgent.LegacyCliConversationId;
                    }
                }
                if (conversation.CodingAgent is not null && !string.IsNullOrWhiteSpace(message.CodingSessionRef)) conversation.CodingAgent.SessionRef = message.CodingSessionRef;
                conversation.AntigravityConversationId = conversation.MainAgent?.Provider == "antigravity"
                    ? conversation.MainAgent.LegacyCliConversationId
                    : null;
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
                ReplaceAgentModels("antigravity", message.Models);
                break;
            case "agent_models":
                ReplaceAgentModels(message.Provider ?? "", message.Models);
                if (message.Provider == "codex")
                    CodingProviderStatusDetail = message.Authenticated == true
                        ? $"Codex 已登录 · {message.Models?.Count ?? 0} 个可用模型"
                        : message.Message ?? "Codex CLI 可用，但尚未登录";
                break;
            case "manager_state":
                ManagerStatus = StateLabel(message.State);
                if (message.State is "responding" or "routing" or "waiting_approval") IsBusy = true;
                else if (message.State is "idle" or "cancelled" or "error") IsBusy = false;
                ApplyManagerTaskTiming(message);
                if (message.State is "cancelled" or "error")
                {
                    StopManagerTypewriter();
                    _streamingManager?.CompleteThinking();
                }
                break;
            case "manager_delta":
                var managerSource = message.Source ?? "antigravity";
                QueueManagerText(CurrentMainDisplayName, managerSource, message.Text ?? "");
                break;
            case "manager_thinking_delta":
                var thinkingSource = message.Source ?? "antigravity";
                QueueManagerThinking(CurrentMainDisplayName, thinkingSource, message.Text ?? "");
                break;
            case "manager_tool_status":
                QueueManagerToolStatus(CurrentMainDisplayName, message.Source ?? "antigravity", message.Text ?? "");
                break;
            case "manager_completed":
                ApplyManagerTaskTiming(message);
                QueueManagerCompletion(message);
                break;
            case "manager_title":
                if (!string.IsNullOrWhiteSpace(message.Title)) conversation.Title = message.Title.Trim();
                break;
            case "manager_metrics":
                ManagerCacheReadTokens = message.CacheReadTokens ?? message.AntigravityCacheReadTokens;
                ManagerContextTokens = message.ContextTokens; ManagerContextWindow = message.ContextWindow;
                break;
            case "codex_prompt_draft":
                if (string.IsNullOrWhiteSpace(message.DraftId) || string.IsNullOrWhiteSpace(message.Text)) break;
                _pendingCodexDraftId = message.DraftId;
                InputText = $"@codex {message.Text}".TrimEnd();
                // ManagerHost has already validated the draft against the
                // active session/workspace and will validate draftId again on
                // submission. Do not block prefill on a second UI-side string
                // comparison: persisted paths can differ in casing, trailing
                // separators, or be stale while a conversation is switching.
                var draftWorkspace = message.WorkspacePath?.Trim();
                var conversationWorkspace = conversation.WorkspacePath?.Trim();
                var sameWorkspace = !string.IsNullOrWhiteSpace(draftWorkspace) && !string.IsNullOrWhiteSpace(conversationWorkspace) &&
                    AreEquivalentWorkspacePaths(draftWorkspace, conversationWorkspace);
                ManagerStatus = sameWorkspace || string.IsNullOrWhiteSpace(conversationWorkspace)
                    ? "Codex 草稿已载入输入框，请检查后发送"
                    : "Codex 草稿已载入输入框，请检查工作区后发送";
                break;
            case "codex_account_status":
                CodingProviderStatusDetail = message.Authenticated == true
                    ? $"Codex 已登录 · {message.Models?.Count ?? 0} 个可用模型"
                    : "Codex CLI 可用，但尚未登录";
                ReplaceAgentModels("codex", message.Models);
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
                _activeCodingTaskId = message.TaskId;
                GetSessionUiState(conversation).CodexBusy = true;
                OnPropertyChanged(nameof(CanCancel));
                var executorName = ExecutorDisplayName(message.Provider ?? conversation.CodingAgent?.Provider ?? "pi");
                var delegationActivity = new ManagerActivity("delegate", $"委派给 {executorName} Coding Agent", "进行中");
                _streamingCoding = AddEntry($"{executorName} Coding Agent", message.Provider ?? conversation.CodingAgent?.Provider ?? "pi", "");
                _streamingCoding.TaskId = message.TaskId;
                ApplyCodingTaskTiming(message, _streamingCoding);
                delegationActivity.Runtime = _streamingCoding.Runtime;
                Activities.Add(delegationActivity);
                break;
            case "coding_delta":
                GetSessionUiState(conversation).CodexBusy = true;
                _activeCodingTaskId ??= message.TaskId;
                _streamingCoding ??= AddEntry(CodingBubbleRole(message), CodingBubbleSource(message), "");
                _streamingCoding.TaskId ??= message.TaskId;
                if (_streamingCoding.Runtime is null && message.StartedAt is not null) ApplyCodingTaskTiming(message, _streamingCoding);
                // Match the Pi workbench: once ordinary assistant text starts,
                // the inline thinking expander is completed and collapsed.
                _streamingCoding.CompleteThinking();
                QueueCodingBubbleText(message, message.Text ?? "");
                break;
            case "coding_thinking_delta":
                GetSessionUiState(conversation).CodexBusy = true;
                _activeCodingTaskId ??= message.TaskId;
                // Manager conversations use the same inline thinking surface as
                // the original Pi workbench. Keep the right-side activity row
                // as the durable process history, while the current coding
                // reply shows its live reasoning directly above the text.
                _streamingCoding ??= AddEntry(CodingBubbleRole(message), CodingBubbleSource(message), "");
                _streamingCoding.TaskId ??= message.TaskId;
                _streamingCoding.AppendThinking(message.Text ?? "");
                var thinking = Activities.LastOrDefault(item => item.Kind == "thinking" && item.Status == "进行中");
                if (thinking is null) { thinking = new ManagerActivity("thinking", $"{CodingBubbleName(message)} 正在思考", "进行中"); Activities.Add(thinking); }
                thinking.Details += message.Text; break;
            case "coding_tool_approval_request":
                var legacyApprovalDetails = ComposeApprovalDetails(message.Details, message.Command, message.WorkingDirectory);
                EnsureCodingOperation(message.Tool ?? "工具", "等待确认", message.CallId, message.Command, legacyApprovalDetails, message.Diff, message.Source ?? message.Provider);
                AddApproval(new ApprovalRequest(conversation.SessionId, message.CallId ?? "", message.Tool ?? "", message.Summary ?? "等待确认", legacyApprovalDetails, message.Diff));
                break;
            case "coding_interaction_request":
                var interactionDetails = ComposeApprovalDetails(message.Details, message.Command, message.WorkingDirectory);
                EnsureCodingOperation(message.Kind ?? "工具", "等待确认", message.RequestId, message.Command, interactionDetails, message.Diff, message.Provider);
                AddApproval(new ApprovalRequest(conversation.SessionId, message.RequestId ?? "", message.Kind ?? "codex", message.Title ?? "Codex 等待确认", interactionDetails, message.Diff, message.Provider ?? "codex", message.Kind ?? "command_approval", JsonElementText(message.Fields), message.Url));
                break;
            case "coding_interaction_completed":
                RemoveApproval(message.RequestId); break;
            case "coding_tool_started":
                GetSessionUiState(conversation).CodexBusy = true;
                _activeCodingTaskId ??= message.TaskId;
                EnsureCodingOperation(message.Tool ?? "工具", "进行中", message.CallId, message.Command, message.Details, null, message.Source ?? message.Provider);
                Activities.Add(new ManagerActivity("tool", message.Tool ?? "工具", "进行中", message.CallId) { Details = message.Command ?? "" });
                break;
            case "coding_tool_output":
                GetSessionUiState(conversation).CodexBusy = true;
                _activeCodingTaskId ??= message.TaskId;
                var outputActivity = Activities.LastOrDefault(item => item.CallId == message.CallId);
                if (outputActivity is not null) outputActivity.Details += message.Text;
                var outputOperation = _streamingCoding?.FindOperation(message.CallId) ?? EnsureCodingOperation(message.Tool ?? "工具", "进行中", message.CallId, null, null, null, message.Source ?? message.Provider);
                if (outputOperation is not null)
                {
                    outputOperation.Details = AppendBounded(outputOperation.Details, message.Text ?? "");
                    // Match the Pi workbench: live shell output is expanded
                    // while it runs; file changes and other operations remain
                    // collapsed until the user opens them.
                    outputOperation.IsExpanded = string.Equals(outputOperation.Title, "run_command", StringComparison.OrdinalIgnoreCase);
                    var outputGroup = _streamingCoding?.Segments.LastOrDefault(segment => segment.Operations.Contains(outputOperation));
                    if (outputGroup is not null && outputOperation.IsExpanded) outputGroup.IsExpanded = true;
                }
                break;
            case "coding_tool_completed":
                GetSessionUiState(conversation).CodexBusy = true;
                _activeCodingTaskId ??= message.TaskId;
                foreach (var activity in Activities.Where(item => item.CallId == message.CallId)) activity.Status = message.Ok == true ? (message.AutoApproved == true ? "自动完成" : "完成") : "失败";
                var completedOperation = _streamingCoding?.FindOperation(message.CallId);
                completedOperation ??= EnsureCodingOperation(message.Tool ?? "工具", message.Ok == true ? "完成" : "失败", message.CallId, message.Command, message.Summary, message.Diff, message.Source ?? message.Provider);
                if (completedOperation is not null)
                {
                    completedOperation.Status = message.Ok == true ? (message.AutoApproved == true ? "自动完成" : "完成") : "失败";
                    if (!string.IsNullOrWhiteSpace(message.Command)) completedOperation.CommandLine = message.Command;
                    if (!string.IsNullOrWhiteSpace(message.Output)) completedOperation.Details = AppendBounded(completedOperation.Details, message.Output);
                    if (!string.IsNullOrWhiteSpace(message.Diff)) completedOperation.Details = AppendBounded(completedOperation.Details, message.Diff);
                    if (!string.IsNullOrWhiteSpace(message.Summary) && string.IsNullOrWhiteSpace(completedOperation.Details)) completedOperation.Details = message.Summary;
                    completedOperation.IsExpanded = false;
                    var completedGroup = _streamingCoding?.Segments.LastOrDefault(segment => segment.Operations.Contains(completedOperation));
                    if (completedGroup is not null && completedGroup.LatestOperation == completedOperation) completedGroup.IsExpanded = false;
                }
                RemoveApproval(message.CallId); break;
            case "coding_completed":
                // Codex observation mode returns ordinary text rather than a
                // CodeResult. Finish the same single bubble used for deltas,
                // append only a non-duplicated terminal suffix, and close any
                // still-running activity rows.
                _streamingCoding ??= AddEntry(CodingBubbleRole(message), CodingBubbleSource(message), "");
                _streamingCoding.TaskId ??= message.TaskId;
                ApplyCodingTaskTiming(message, _streamingCoding);
                _streamingCoding.CompleteThinking();
                FlushCodingText();
                // A completed observation normally carries the report text for
                // persistence, while assistant deltas have already rendered
                // that text in this bubble. The host sends only a proven-new
                // suffix; retain a small UI-side guard for older hosts or a
                // stale ManagerHost binary that might still send the full text.
                if (!string.IsNullOrWhiteSpace(message.Text) &&
                    !IsDuplicateCodexCompletion(_streamingCoding.Text, message.Text))
                    QueueCodingBubbleText(message, message.Text);
                CompleteStreamingCodingText();
                foreach (var item in Activities.Where(item => item.Status == "进行中"))
                    item.Status = message.Status is "failed" ? "失败" : message.Status is "cancelled" ? "已取消" : message.Status is "partial" ? "部分完成" : "完成";
                Activities.Add(new ManagerActivity("result", $"{CodingBubbleName(message)} 任务结束", message.Status is "completed" ? "完成" : message.Status is "cancelled" ? "已取消" : message.Status is "partial" ? "部分完成" : "失败") { Details = message.Text ?? "", Runtime = _streamingCoding.Runtime });
                _streamingCoding = null; _activeCodingTaskId = null; GetSessionUiState(conversation).CodexBusy = false; PendingApprovals.Clear(); NotifyApprovalChanged(); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanVerifyLastCodexTask)); VerifyLastCodexTaskCommand.NotifyCanExecuteChanged(); break;
            case "code_result":
                _streamingCoding ??= AddEntry($"{CurrentCodingDisplayName} Coding Agent", conversation.CodingAgent?.Provider ?? "pi", "");
                _streamingCoding.TaskId = message.TaskId ?? _activeCodingTaskId;
                _streamingCoding.CompleteThinking();
                FlushCodingText();
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
                _streamingCoding.Runtime?.Mark("completed", DateTimeOffset.UtcNow);
                CompleteStreamingCodingText();
                _streamingCoding = null; _activeCodingTaskId = null; GetSessionUiState(conversation).CodexBusy = false; PendingApprovals.Clear(); NotifyApprovalChanged(); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanVerifyLastCodexTask)); VerifyLastCodexTaskCommand.NotifyCanExecuteChanged(); break;
            case "verification_started":
                Activities.Add(new ManagerActivity("verification", "Antigravity 正在独立验证 Codex 结果", "进行中"));
                _streamingCoding = AddEntry("Antigravity 验证", "antigravity", "");
                _streamingCoding.TaskId = message.TaskId;
                break;
            case "verification_result":
                _streamingCoding ??= AddEntry("Antigravity 验证", "antigravity", "");
                _streamingCoding.TaskId = message.TaskId;
                FlushCodingText();
                _streamingCoding.CodeResult = message.Result;
                _streamingCoding.ReplaceText(message.Result?.SummaryForUser ?? "验证已结束。");
                foreach (var item in Activities.Where(item => item.Kind == "verification" && item.Status == "进行中")) item.Status = message.Result?.Status == "completed" ? "完成" : "失败";
                if (message.Result is { } verification)
                    foreach (var validation in verification.Validation) Activities.Add(new ManagerActivity("validation", validation.Command, validation.Status == "passed" ? "通过" : validation.Status == "failed" ? "失败" : "跳过") { Details = validation.Summary });
                _streamingCoding.Runtime?.Mark("completed", DateTimeOffset.UtcNow);
                CompleteStreamingCodingText();
                _streamingCoding = null; OnPropertyChanged(nameof(CanVerifyLastCodexTask)); VerifyLastCodexTaskCommand.NotifyCanExecuteChanged(); break;
            case "task_trace":
                if (message.TraceEvent is not null) TaskTraceStore.Append(conversation.SessionId, message.TraceEvent);
                break;
            case "manager_error":
                if (!string.Equals(message.Provider, "codex", StringComparison.Ordinal))
                {
                    StopManagerTypewriter();
                }
                if (string.Equals(message.Provider, "codex", StringComparison.Ordinal) && _streamingCoding is not null)
                {
                    FlushCodingText();
                    CompleteStreamingCodingText();
                    _streamingCoding.CompleteThinking();
                    _streamingCoding = null;
                }
                if (string.Equals(message.Provider, "codex", StringComparison.Ordinal))
                {
                    GetSessionUiState(conversation).CodexBusy = false;
                    _activeCodingTaskId = null;
                    OnPropertyChanged(nameof(CanCancel));
                }
                if (message.Code?.StartsWith("CODEX_", StringComparison.Ordinal) == true)
                {
                    var exception = new InvalidOperationException($"{message.Code}: {message.Message}");
                    _codexProbeCompletion?.TrySetException(exception); _codexProbeCompletion = null;
                    _codexLoginCompletion?.TrySetException(exception); _codexLoginCompletion = null;
                }
                AddSystemMessage($"{message.Code}: {message.Message}");
                if (!string.Equals(message.Provider, "codex", StringComparison.Ordinal))
                {
                    IsBusy = false;
                    ManagerStatus = "错误";
                }
                break;
        }
        // Persist completed turns and state changes, but do not synchronously
        // rewrite the full transcript for every streamed text delta. The
        // in-memory entry remains live so the UI can resize on each delta.
        if (message.Type is not ("manager_delta" or "manager_thinking_delta" or "manager_tool_status" or "coding_delta" or "coding_thinking_delta" or "coding_tool_output")) Save();
        SyncSessionUiState(conversation);
        NotifyCurrentProviderChanged();
    }

    private async Task StartSessionAfterActivationMissAsync(ManagerConversationItem conversation)
    {
        try { await StartOrSyncSessionAsync(conversation); }
        catch (Exception exception) { AddSystemMessage(conversation, $"Manager Host 会话恢复失败：{exception.Message}"); }
    }

    private ProcessItem? EnsureCodingOperation(string title, string status, string? callId, string? command, string? details, string? diff, string? source = null)
    {
        _streamingCoding ??= AddEntry(CodingBubbleRole(source), CodingBubbleSource(source), "");
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
        operation.IsExpanded = status == "等待确认" ||
            (status == "进行中" && string.Equals(title, "run_command", StringComparison.OrdinalIgnoreCase));
        var operationGroup = _streamingCoding.Segments.LastOrDefault(segment => segment.Operations.Contains(operation));
        if (operationGroup is not null) operationGroup.IsExpanded = operation.IsExpanded;
        return operation;
    }

    private string CodingBubbleName(ManagerHostEvent message) => CodingBubbleName(message.Source ?? message.Provider);

    private string CodingBubbleName(string? source) => string.Equals(source, "codex", StringComparison.OrdinalIgnoreCase)
        ? "Codex"
        : CurrentCodingDisplayName;

    private string CodingBubbleRole(ManagerHostEvent message) => $"{CodingBubbleName(message)} Coding Agent";

    private string CodingBubbleRole(string? source) => $"{CodingBubbleName(source)} Coding Agent";

    private string CodingBubbleSource(ManagerHostEvent message) => CodingBubbleSource(message.Source ?? message.Provider);

    private string CodingBubbleSource(string? source) => string.IsNullOrWhiteSpace(source)
        ? SelectedConversation?.CodingAgent?.Provider ?? "pi"
        : source;

    /// <summary>
    /// Coalesce Coding-agent deltas on a short UI timer.  A FlowDocument is
    /// intentionally not rebuilt here: the entry stays in its plain-text
    /// stream mode until the terminal event flushes this queue.
    /// </summary>
    private void QueueCodingBubbleText(ManagerHostEvent message, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _streamingCoding ??= AddEntry(CodingBubbleRole(message), CodingBubbleSource(message), "");
        _streamingCoding.TaskId ??= message.TaskId;
        _streamingCoding.CompleteThinking();
        const int maxBubbleLength = 48_000;
        var remaining = maxBubbleLength - _streamingCoding.Text.Length - _pendingCodingText.Length;
        if (remaining <= 0) return;
        const string truncationMarker = "\n…（Codex 输出已截断）";
        if (text.Length > remaining)
            text = remaining <= truncationMarker.Length ? truncationMarker[..remaining] : text[..(remaining - truncationMarker.Length)] + truncationMarker;
        _streamingCoding.IsStreamingText = true;
        _pendingCodingText.Append(text);
        EnsureCodingTextTimerStarted();
    }

    private void EnsureCodingTextTimerStarted()
    {
        if (_pendingCodingText.Length == 0 || _streamingCoding is null) return;
        _codingTextTimer ??= CreateCodingTextTimer();
        if (!_codingTextTimer.IsEnabled) _codingTextTimer.Start();
    }

    private DispatcherTimer CreateCodingTextTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            // Thirty frames per second remains responsive while collapsing a
            // burst of token-sized deltas into a single UI/Markdown update.
            Interval = TimeSpan.FromMilliseconds(33)
        };
        timer.Tick += (_, _) => RenderCodingTextTick();
        return timer;
    }

    private void RenderCodingTextTick()
    {
        if (_streamingCoding is null || _pendingCodingText.Length == 0)
        {
            _codingTextTimer?.Stop();
            return;
        }

        var length = NextStreamingBatchLength(_pendingCodingText);
        var value = _pendingCodingText.ToString(0, length);
        _pendingCodingText.Remove(0, length);
        _streamingCoding.Append(value);
        if (_pendingCodingText.Length == 0) _codingTextTimer?.Stop();
    }

    private static int NextStreamingBatchLength(StringBuilder pending)
    {
        var target = pending.Length switch
        {
            > 8_192 => 2_048,
            > 2_048 => 1_024,
            _ => 512,
        };
        target = Math.Min(target, pending.Length);
        if (target < pending.Length && char.IsHighSurrogate(pending[target - 1]) && char.IsLowSurrogate(pending[target])) target--;
        return Math.Max(1, target);
    }

    private void FlushCodingText()
    {
        if (_streamingCoding is null || _pendingCodingText.Length == 0) return;
        _streamingCoding.Append(_pendingCodingText.ToString());
        _pendingCodingText.Clear();
        _codingTextTimer?.Stop();
    }

    private void CompleteStreamingCodingText()
    {
        FlushCodingText();
        if (_streamingCoding is not null) _streamingCoding.IsStreamingText = false;
    }

    private static bool IsDuplicateCodexCompletion(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(incoming)) return false;
        var current = CompactCodexText(existing);
        var candidate = CompactCodexText(incoming);
        if (current.Length == 0 || candidate.Length == 0) return false;
        if (string.Equals(current, candidate, StringComparison.Ordinal)) return true;
        // A stale host can send the whole final answer after it was streamed.
        // Avoid suppressing short, legitimate suffixes (for example a one-word
        // final answer) by requiring a meaningful payload before Contains().
        return candidate.Length >= 48 && (current.Contains(candidate, StringComparison.Ordinal) || candidate.Contains(current, StringComparison.Ordinal));
    }

    private static string CompactCodexText(string value) => Regex.Replace(value, @"\s+", "").Trim();

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

    private static string ComposeApprovalDetails(string? details, string? command, string? workingDirectory)
    {
        var lines = new List<string>();
        var detailText = details?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(detailText) && !IsPlaceholderApprovalDetails(detailText)) lines.Add(detailText);
        var commandText = command?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(commandText) && !lines.Any(line => line.Contains(commandText, StringComparison.Ordinal))) lines.Add(commandText);
        var cwdText = workingDirectory?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(cwdText) && !lines.Any(line => line.Contains(cwdText, StringComparison.Ordinal))) lines.Add($"工作目录：{cwdText}");
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "请确认 Codex 请求的具体操作。";
    }

    private static bool IsPlaceholderApprovalDetails(string value) =>
        value.Equals("等待确认", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("等待批准", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("等待用户输入", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("等待文件变更确认", StringComparison.OrdinalIgnoreCase);

    private static string? JsonElementText(JsonElement? value)
    {
        if (!value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.Value.GetRawText();
    }

    private ManagerMainAgentConfig BuildMainConfig(ManagerConversationItem conversation, bool includeSecret)
    {
        var binding = conversation.MainAgent ?? throw new InvalidOperationException("对话缺少主 Agent 配置。");
        var isAntigravity = string.Equals(binding.Provider, "antigravity", StringComparison.OrdinalIgnoreCase);
        return new ManagerMainAgentConfig
        {
            Provider = binding.Provider,
            Transport = isAntigravity ? "cli" : null,
            Executable = isAntigravity ? AntigravityCliPath : binding.CliPath,
            ConversationId = isAntigravity
                ? binding.LegacyCliConversationId ?? binding.SessionRef : null,
            // Retain the field in the wire model for old JSON compatibility,
            // but never activate the removed SDK transport.
            SdkSessionRef = null,
            LegacyCliConversationId = binding.Provider == "antigravity" ? binding.LegacyCliConversationId : null,
            // Antigravity execution choices are global settings. The fields
            // retained on the conversation binding are legacy snapshot data
            // and are intentionally not used for new Manager requests.
            Model = isAntigravity ? AntigravityModel : binding.Model,
            Effort = isAntigravity ? AntigravityEffort : binding.Effort,
            TimeoutSeconds = isAntigravity ? 0 : binding.TimeoutSeconds,
            ToolPermission = isAntigravity ? NormalizeAntigravityToolPermission(AntigravityToolPermission) : null,
            TerminalSandbox = isAntigravity && AntigravityTerminalSandbox,
            BaseUrl = binding.BaseUrl, ModelId = binding.ModelId,
            ApiKey = includeSecret && binding.Provider == "openai_compatible" ? CredentialStore.Read(binding.CredentialId) : null,
            SessionFile = binding.Provider == "openai_compatible" ? binding.SessionRef : null
        };
    }

    private ManagerCodingAgentConfig BuildCodingConfig(ManagerConversationItem conversation, bool includeSecret)
    {
        var binding = conversation.CodingAgent ?? throw new InvalidOperationException("对话缺少 Coding Agent 配置。");
        var isAntigravity = string.Equals(binding.Provider, "antigravity", StringComparison.OrdinalIgnoreCase);
        var isCodex = string.Equals(binding.Provider, "codex", StringComparison.OrdinalIgnoreCase);
        return new ManagerCodingAgentConfig
        {
            Provider = binding.Provider,
            Executable = isAntigravity ? AntigravityCliPath : isCodex ? CodexCliPath : binding.CliPath,
            ThreadId = isCodex ? binding.SessionRef : null,
            Model = isAntigravity ? AntigravityModel : isCodex ? CodexModel : binding.Model,
            Effort = isAntigravity ? AntigravityEffort : isCodex ? CodexEffort : binding.Effort,
            BaseUrl = binding.BaseUrl, ModelId = binding.ModelId,
            ApprovalPolicy = isCodex ? NormalizeCodexApprovalPolicy(CodexApprovalPolicy) : null,
            SandboxMode = isCodex ? NormalizeCodexSandboxMode(CodexSandboxMode) : null,
            ApiKey = includeSecret && binding.Provider == "pi" ? CredentialStore.Read(binding.CredentialId) ?? CredentialStore.Read(LegacyPiCredentialTarget) : null,
            SessionFile = binding.Provider == "pi" ? binding.SessionRef : null,
            ConversationId = binding.Provider == "antigravity" ? binding.SessionRef : null,
            ExecutionPolicy = isAntigravity ? AntigravityExecutionPolicy : null,
            AutoApproveSafeCommands = binding.AutoApproveSafeCommands, AutoApproveGitOperations = binding.AutoApproveGitOperations
        };
    }

    private Dictionary<string, ManagerCodingAgentConfig> BuildExecutorProfiles(ManagerConversationItem conversation, bool includeSecret)
    {
        var profiles = new Dictionary<string, ManagerCodingAgentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["pi"] = new ManagerCodingAgentConfig
            {
                Provider = "pi", BaseUrl = BaseUrl, ModelId = ModelId,
                ApiKey = includeSecret ? CredentialStore.Read(PiCredentialId) ?? CredentialStore.Read(LegacyPiCredentialTarget) : null,
                AutoApproveSafeCommands = AutoApproveSafeCommands, AutoApproveGitOperations = AutoApproveGitOperations,
            },
            ["codex"] = new ManagerCodingAgentConfig { Provider = "codex", Executable = CodexCliPath, Model = CodexModel, Effort = CodexEffort, ApprovalPolicy = NormalizeCodexApprovalPolicy(CodexApprovalPolicy), SandboxMode = NormalizeCodexSandboxMode(CodexSandboxMode) },
            ["antigravity"] = new ManagerCodingAgentConfig
            {
                Provider = "antigravity", Executable = AntigravityCliPath, Model = AntigravityModel,
                Effort = AntigravityEffort, ExecutionPolicy = AntigravityExecutionPolicy,
                // Kept in the legacy profile map for older hosts; the unified
                // MainAgent payload is the one used by current ManagerHost.
            },
        };
        profiles[conversation.CodingAgent!.Provider] = BuildCodingConfig(conversation, includeSecret);
        return profiles;
    }

    private ManagerChatEntry AddEntry(string role, string source, string text)
    {
        var entry = new ManagerChatEntry(role, source, text);
        if (SelectedConversation is not null) AppendMessage(SelectedConversation, entry);
        return entry;
    }

    private void AppendMessage(ManagerConversationItem conversation, ManagerChatEntry entry)
    {
        conversation.Messages.Add(entry);
        RefreshMessageTimeMetadata(conversation);
    }

    private void RemoveMessage(ManagerConversationItem conversation, ManagerChatEntry entry)
    {
        conversation.Messages.Remove(entry);
        RefreshMessageTimeMetadata(conversation);
    }

    private void ReserveManagerBubble(ManagerConversationItem conversation)
    {
        var source = conversation.MainAgent?.Provider == "openai_compatible" ? "api_manager" : "antigravity";
        var bubble = GetOrCreateManagerBubble(CurrentMainDisplayName, source);
        if (bubble.Entry is not null) return;
        bubble.Entry = AddEntry(bubble.Role, bubble.Source, "");
        bubble.Entry.IsStreamingText = true;
        _streamingManager = bubble.Entry;
    }

    private PendingManagerBubble GetOrCreateManagerBubble(string role, string source)
    {
        if (_managerInputBubble is null || _managerInputBubble.IsComplete)
        {
            _managerInputBubble = new PendingManagerBubble(role, source);
            _managerBubbles.Enqueue(_managerInputBubble);
        }

        return _managerInputBubble;
    }

    private void QueueManagerText(string role, string source, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ClearTransientManagerStatus();
        var bubble = GetOrCreateManagerBubble(role, source);
        bubble.PendingText.Append(text);
        EnsureManagerTypewriterStarted();
    }

    private ManagerChatEntry GetOrCreateTransientManagerStatus(string role, string source)
    {
        if (_streamingManagerStatus is not null && ReferenceEquals(_streamingManagerStatusConversation, SelectedConversation))
            return _streamingManagerStatus;

        ClearTransientManagerStatus();
        var entry = new ManagerChatEntry(role, source, "", isTransientStatus: true);
        AppendMessage(SelectedConversation!, entry);
        _streamingManagerStatus = entry;
        _streamingManagerStatusConversation = SelectedConversation;
        return entry;
    }

    private void SuspendManagerBubbleForStatus()
    {
        if (_managerInputBubble is null) return;
        // Keep the empty placeholder reserved at turn start. A reasoning/tool
        // status can arrive before the manager's first visible text; removing
        // that placeholder here would let an asynchronous Codex bubble claim
        // its position and put the later manager reply below it again.
        if (_managerInputBubble.Entry is not null && !_managerInputBubble.HasText && !_managerInputBubble.IsComplete)
            return;
        _managerInputBubble.IsComplete = true;
        _managerInputBubble = null;
        EnsureManagerTypewriterStarted();
    }

    private void ClearTransientManagerStatus()
    {
        if (_streamingManagerStatus is not null && _streamingManagerStatusConversation is not null)
            RemoveMessage(_streamingManagerStatusConversation, _streamingManagerStatus);
        _streamingManagerStatus = null;
        _streamingManagerStatusConversation = null;
    }

    private static string AppendTransientStatus(string current, string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return current;
        if (string.IsNullOrWhiteSpace(current)) return chunk.Trim();
        if (current.EndsWith(chunk, StringComparison.Ordinal)) return current;
        const int maxLength = 720;
        var separator = current.EndsWith('\n') ? "" : "\n";
        var value = current + separator + chunk.Trim();
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "…";
    }

    private void QueueManagerThinking(string role, string source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SuspendManagerBubbleForStatus();
        var status = GetOrCreateTransientManagerStatus(role, source);
        if (status.Text is "正在分析…" or "我来陪你处理这项任务…") status.SetTransientText(text);
        else status.SetTransientText(AppendTransientStatus(status.Text, text));
    }

    private void QueueManagerToolStatus(string role, string source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SuspendManagerBubbleForStatus();
        var status = GetOrCreateTransientManagerStatus(role, source);
        status.SetTransientText(text);
    }

    private void QueueManagerCompletion(ManagerHostEvent message)
    {
        ClearTransientManagerStatus();
        var source = message.Source ?? "antigravity";
        var response = message.Text ?? "";
        var bubble = GetOrCreateManagerBubble(CurrentMainDisplayName, source);
        if (!bubble.HasText && !string.IsNullOrWhiteSpace(response)) bubble.PendingText.Append(response);
        bubble.CompletionAction = message.Action ?? "respond";
        bubble.IsFinal = message.Final ?? true;
        bubble.IsComplete = true;
        _managerInputBubble = null;
        EnsureManagerTypewriterStarted();
    }

    private void EnsureManagerTypewriterStarted()
    {
        if (_managerBubbles.Count == 0) return;
        _managerTypewriterTimer ??= CreateManagerTypewriterTimer();
        RenderManagerTypewriterTick();
        if (HasPendingManagerText) _managerTypewriterTimer.Start();
    }

    private bool HasPendingManagerText => _managerBubbles.Any(bubble => bubble.PendingText.Length > 0);

    private DispatcherTimer CreateManagerTypewriterTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            // Batch text at roughly 30fps.  The chat stays responsive without
            // performing a layout pass for every token-sized CLI delta.
            Interval = TimeSpan.FromMilliseconds(33)
        };
        timer.Tick += (_, _) => RenderManagerTypewriterTick();
        return timer;
    }

    private void RenderManagerTypewriterTick()
    {
        while (_managerBubbles.Count > 0)
        {
            var bubble = _managerBubbles.Peek();
            if (bubble.PendingText.Length > 0)
            {
                if (bubble.Entry is null)
                {
                    bubble.Entry = AddEntry(bubble.Role, bubble.Source, "");
                    bubble.Entry.IsStreamingText = true;
                    _streamingManager = bubble.Entry;
                }

                var length = NextTypewriterBatchLength(bubble.PendingText);
                var value = bubble.PendingText.ToString(0, length);
                bubble.PendingText.Remove(0, length);
                bubble.Entry.Append(value);
                bubble.Entry.CompleteThinking();
                if (bubble.PendingText.Length > 0) return;
            }

            if (!bubble.IsComplete)
            {
                _managerTypewriterTimer?.Stop();
                return;
            }

            _managerBubbles.Dequeue();
            CompleteManagerBubble(bubble);
            // Let the next summary begin on the next timer tick. This keeps
            // adjacent progress bubbles visually distinct even when their
            // responses arrive back-to-back.
            if (_managerBubbles.Count > 0) return;
        }

        _managerTypewriterTimer?.Stop();
    }

    private static int NextTypewriterBatchLength(StringBuilder pending)
    {
        var target = pending.Length switch
        {
            > 2_048 => 512,
            > 512 => 256,
            _ => 96,
        };
        target = Math.Min(target, pending.Length);
        if (target < pending.Length && char.IsHighSurrogate(pending[target - 1]) && char.IsLowSurrogate(pending[target])) target--;
        return Math.Max(1, target);
    }

    private void CompleteManagerBubble(PendingManagerBubble bubble)
    {
        if (bubble.Entry is not null)
        {
            bubble.Entry.CompleteThinking();
            bubble.Entry.IsStreamingText = false;
            if (string.IsNullOrWhiteSpace(bubble.Entry.Text) && !bubble.Entry.HasThinking && bubble.Entry.Runtime is null)
                if (SelectedConversation is not null) RemoveMessage(SelectedConversation, bubble.Entry);
        }

        if (ReferenceEquals(_streamingManager, bubble.Entry)) _streamingManager = null;
        if (bubble.IsFinal && !string.Equals(bubble.CompletionAction, "delegate_code", StringComparison.Ordinal)) IsBusy = false;
    }

    private void StopManagerTypewriter()
    {
        _managerTypewriterTimer?.Stop();
        _managerBubbles.Clear();
        _managerInputBubble = null;
        ClearTransientManagerStatus();
        if (_streamingManager is not null)
        {
            // Keep a task placeholder when it already has timing metadata so
            // a later cancelled/error manager_state can attach the
            // authoritative duration instead of losing the task bubble.
            var preserveTaskEntry = _streamingManager.Runtime is not null;
            _streamingManager.CompleteThinking();
            _streamingManager.IsStreamingText = false;
            if (string.IsNullOrWhiteSpace(_streamingManager.Text) && !_streamingManager.HasThinking && !preserveTaskEntry)
                if (SelectedConversation is not null) RemoveMessage(SelectedConversation, _streamingManager);
            if (!preserveTaskEntry) _streamingManager = null;
        }
    }

    private void ResetManagerTypewriter() => StopManagerTypewriter();

    private void AddSystemMessage(string text) => AddSystemMessage(SelectedConversation, text);

    private void AddSystemMessage(ManagerConversationItem? conversation, string text)
    {
        if (conversation is not null)
        {
            AppendMessage(conversation, new ManagerChatEntry("IlMatto", "system", text));
        }
        Save();
    }

    private static void RefreshMessageTimeMetadata(ManagerConversationItem conversation)
    {
        DateTime? previousDate = null;
        foreach (var message in conversation.Messages)
        {
            message.ShowDateSeparator = previousDate is null || message.CreatedAt.Date != previousDate.Value;
            previousDate = message.CreatedAt.Date;
        }
    }

    private void SortConversationsByLastUserMessage()
    {
        var ordered = Conversations
            .OrderByDescending(item => item.ListTimestamp)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();
        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var currentIndex = Conversations.IndexOf(ordered[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
                Conversations.Move(currentIndex, targetIndex);
        }
    }
    private static bool LooksLikeCodeResultJson(string? value)
    {
        var text = value?.TrimStart() ?? "";
        return text.StartsWith("{", StringComparison.Ordinal) &&
               (text.Contains("\"summaryForUser\"", StringComparison.Ordinal) || text.Contains("\"status\"", StringComparison.Ordinal));
    }
    private static bool HasCodexDirective(string text) => Regex.IsMatch(text, @"^\s*@codex(?:\s+|:)", RegexOptions.IgnoreCase);
    private static bool AreEquivalentWorkspacePaths(string left, string right)
    {
        try
        {
            var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (leftFull.Length == 2 && leftFull[1] == ':') leftFull += Path.DirectorySeparatorChar;
            if (rightFull.Length == 2 && rightFull[1] == ':') rightFull += Path.DirectorySeparatorChar;
            return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private void RemoveApproval(string? callId) { var request = PendingApprovals.FirstOrDefault(item => item.CallId == callId); if (request is not null) PendingApprovals.Remove(request); NotifyApprovalChanged(); }
    private void NotifyApprovalChanged() { OnPropertyChanged(nameof(CurrentApproval)); OnPropertyChanged(nameof(HasApproval)); OnPropertyChanged(nameof(PendingApprovalCountLabel)); }
    private void NotifyCurrentProviderChanged()
    {
        OnPropertyChanged(nameof(WorkspaceLabel)); OnPropertyChanged(nameof(CurrentCompanionName)); OnPropertyChanged(nameof(CurrentCompanionTitle)); OnPropertyChanged(nameof(CurrentMainDisplayName)); OnPropertyChanged(nameof(CurrentCodingDisplayName));
        OnPropertyChanged(nameof(IsCodexCodingAgent));
        OnPropertyChanged(nameof(CanVerifyLastCodexTask));
        VerifyLastCodexTaskCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentProviderLabel)); OnPropertyChanged(nameof(MainConnectionLabel)); OnPropertyChanged(nameof(ManagerCacheLabel)); OnPropertyChanged(nameof(ManagerContextLabel));
    }

    private void ReplaceAgentModels(string provider, IReadOnlyList<CodexModelInfo>? models)
    {
        var group = AgentModelGroups.FirstOrDefault(item => item.Provider == provider);
        if (group is null) return;
        var choices = new List<AgentModelChoice>
        {
            new(provider, "", "Auto", provider == "codex" ? "使用 Codex 默认模型。" : "使用 Antigravity 默认模型。"),
        };

        if (provider == "antigravity")
        {
            // AGY exposes reasoning tiers as separate slugs. Group them by
            // their base model so the picker does not duplicate every Gemini
            // entry. The exact slug remains available through EffortVariants.
            var builders = new Dictionary<string, AntigravityModelBuilder>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models ?? [])
            {
                var id = model.Id?.Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var effortTier = ParseAntigravityEffortVariant(id);
                var familyId = effortTier is null ? id : StripAntigravityEffortVariant(id);
                var displayName = CleanAntigravityModelDisplayName(model.DisplayName, id, effortTier);
                if (!builders.TryGetValue(familyId, out var builder))
                {
                    builder = new AntigravityModelBuilder(familyId, displayName);
                    builders[familyId] = builder;
                }
                else if (string.IsNullOrWhiteSpace(builder.DisplayName) || builder.DisplayName.Equals(builder.FamilyId, StringComparison.OrdinalIgnoreCase))
                {
                    builder.DisplayName = displayName;
                }

                if (effortTier is null) builder.BaseModelId = id;
                else builder.Variants[effortTier] = id;
            }

            var selectedEffort = NormalizeAntigravityEffort(AntigravityEffort);
            foreach (var builder in builders.Values)
            {
                var modelId = builder.Resolve(selectedEffort);
                var variantMap = builder.Variants.Count == 0
                    ? null
                    : new Dictionary<string, string>(builder.Variants, StringComparer.OrdinalIgnoreCase);
                var description = variantMap is null
                    ? $"Model ID: {modelId}"
                    : "推理强度由下方选项控制。";
                choices.Add(new AgentModelChoice(provider, modelId, builder.DisplayName, description, variantMap, builder.FamilyId));
            }
        }
        else
        {
            foreach (var model in models ?? [])
            {
                var id = model.Id?.Trim();
                if (string.IsNullOrWhiteSpace(id) || choices.Any(item => string.Equals(item.ModelId, id, StringComparison.OrdinalIgnoreCase))) continue;
                choices.Add(new AgentModelChoice(provider, id, CleanModelDisplayName(provider, model.DisplayName, id), $"Model ID: {id}"));
            }
        }
        group.Models.Clear();
        foreach (var choice in choices) group.Models.Add(choice);
        if (provider == "antigravity" && !string.IsNullOrWhiteSpace(AntigravityModel))
        {
            var currentChoice = choices.FirstOrDefault(item => item.MatchesModelId(AntigravityModel));
            var resolvedModelId = currentChoice?.ResolveModelId(NormalizeAntigravityEffort(AntigravityEffort));
            if (!string.IsNullOrWhiteSpace(resolvedModelId) && !string.Equals(resolvedModelId, AntigravityModel, StringComparison.OrdinalIgnoreCase))
                AntigravityModel = resolvedModelId;
        }
        OnPropertyChanged(nameof(SelectedModelLabel));
    }

    private static string? ParseAntigravityEffortVariant(string modelId)
    {
        var match = Regex.Match(modelId.Trim(), @"(?:^|[-_])(low|medium|med|high)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups[1].Value.Equals("med", StringComparison.OrdinalIgnoreCase) ? "medium" : match.Groups[1].Value.ToLowerInvariant();
    }

    private static string StripAntigravityEffortVariant(string modelId)
    {
        return Regex.Replace(modelId.Trim(), @"[-_](?:low|medium|med|high)$", "", RegexOptions.IgnoreCase);
    }

    private static string CleanAntigravityModelDisplayName(string? displayName, string modelId, string? effortTier)
    {
        var value = CleanModelDisplayName("antigravity", displayName, modelId);
        if (effortTier is not null)
        {
            value = Regex.Replace(value, @"\s*[\(\[]?(?:low|medium|med|high)[\)\]]?\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(value)) value = StripAntigravityEffortVariant(modelId);
        }
        return value;
    }

    private static string CleanModelDisplayName(string provider, string? displayName, string modelId)
    {
        var value = string.IsNullOrWhiteSpace(displayName) ? modelId : displayName.Trim();
        if (value.Equals(provider, StringComparison.OrdinalIgnoreCase)) return modelId;
        var prefix = Regex.Match(value, $"^{Regex.Escape(provider)}(?:\\s*[\\u00b7:：/\\-]\\s*|\\s+)(.+)$", RegexOptions.IgnoreCase);
        return prefix.Success ? prefix.Groups[1].Value.Trim() : value;
    }

    private bool _modelRefreshInFlight;
    private async Task RefreshAgentModelsAsync()
    {
        if (_modelRefreshInFlight || SelectedConversation is null) return;
        _modelRefreshInFlight = true;
        try
        {
            await EnsureSessionReadyAsync(SelectedConversation);
            if (_pipe is null) return;
            await _pipe.SendAsync(new ListAgentModelsMessage(SelectedConversation.SessionId, "antigravity"));
            await _pipe.SendAsync(new ListAgentModelsMessage(SelectedConversation.SessionId, "codex"));
        }
        catch (Exception exception)
        {
            // A missing optional Codex installation must not make the normal
            // Antigravity conversation unavailable.
            CodingProviderStatusDetail = $"Codex 模型列表不可用：{exception.Message}";
        }
        finally { _modelRefreshInFlight = false; }
    }

    [RelayCommand]
    private void SelectModel(AgentModelChoice? choice)
    {
        if (choice is null || (choice.Provider != "antigravity" && choice.Provider != "codex")) return;
        SelectedModelProvider = choice.Provider;
        if (choice.Provider == "codex")
        {
            SelectedModelId = choice.ModelId;
            CodexModel = choice.ModelId;
            SelectedTaskExecutor = "codex";
        }
        else
        {
            // A grouped AGY entry may represent several concrete CLI slugs.
            // Resolve the one matching the currently selected effort before
            // persisting or starting a session.
            var resolvedModelId = choice.ResolveModelId(AntigravityEffort);
            SelectedModelId = resolvedModelId;
            AntigravityModel = resolvedModelId;
            SelectedTaskExecutor = "default";
        }
        IsModelMenuOpen = false;
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    private async Task RefreshSelectedAgentConfigurationAsync()
    {
        try
        {
            if (SelectedConversation is not null)
            {
                await StartOrSyncSessionAsync(SelectedConversation);
            }
        }
        catch (Exception exception) { AddSystemMessage($"无法更新 Agent 配置：{exception.Message}"); }
    }

    private static string ExecutorDisplayName(string? provider) => provider switch { "codex" => "Codex", "antigravity" => "Antigravity", _ => "Pi" };
    private void OnTransportError(object? sender, string error) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
    {
        _hostStartedSessionIds.Clear();
        _activatingSessionIds.Clear();
        foreach (var deletion in _pendingSessionDeletions.Values)
            deletion.TrySetException(new InvalidOperationException($"Manager Host 连接异常：{error}"));
        AddSystemMessage($"Manager Host 连接异常：{error}");
        IsBusy = false;
    });
    private void NotifyPendingImageAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasPendingImageAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void ReindexPendingAttachments()
    {
        for (var index = 0; index < PendingImageAttachments.Count; index++)
            PendingImageAttachments[index].Order = index;
    }
    private void Save()
    {
        ManagerConversationStore.Save(Conversations);
        _draftDirty = false;
    }

    private void SaveDraftIfDirty()
    {
        if (!_draftDirty) return;
        CaptureCurrentDraft();
        Save();
    }

    private void CaptureCurrentDraft()
    {
        if (_isRestoringDraft || SelectedConversation is null) return;
        if (string.Equals(SelectedConversation.DraftText, InputText, StringComparison.Ordinal)) return;
        SelectedConversation.DraftText = InputText;
        _draftDirty = true;
    }

    private void RestoreDraft(ManagerConversationItem? conversation)
    {
        _isRestoringDraft = true;
        try
        {
            _pendingCodexDraftId = null;
            InputText = conversation?.DraftText ?? "";
        }
        finally
        {
            _isRestoringDraft = false;
        }
    }

    private void ResetDraftSaveTimer()
    {
        if (_draftSaveTimer is null) return;
        _draftSaveTimer.Stop();
        _draftSaveTimer.Start();
    }

    private ManagerConversationItem CreateConversation()
    {
        var item = new ManagerConversationItem(Guid.NewGuid().ToString("N"), "新对话")
        {
            WorkspacePath = WorkspacePath,
            CompanionProfile = new ManagerCompanionProfile
            {
                CharacterName = RoleCardComposer.NormalizeName(CompanionCharacterName),
                CharacterPrompt = RoleCardComposer.Compose(CompanionCharacterName, CompanionCharacterPrompt),
                UserProfile = CompanionUserProfile.Trim(),
            },
        };
        // Manager now has one Antigravity context for both conversation and
        // local coding. Keep the legacy provider settings visible/persisted for
        // UI compatibility, but do not bind a new Manager session to API, Pi,
        // or Codex.
        item.MainAgent = new ManagerMainAgentBinding { Provider = "antigravity", Transport = "cli", CliPath = AntigravityCliPath, Model = AntigravityModel, Effort = AntigravityEffort, ToolPermission = AntigravityToolPermission, TerminalSandbox = AntigravityTerminalSandbox, TimeoutSeconds = 0 };
        item.CodingAgent = new ManagerCodingAgentBinding { Provider = "antigravity", CliPath = AntigravityCliPath, Model = AntigravityModel, Effort = AntigravityEffort, AntigravityExecutionPolicy = "autonomous" };
        return item;
    }

    private void NormalizeLegacyBinding(ManagerConversationItem conversation)
    {
        var legacyMain = conversation.MainAgent;
        var legacyCoding = conversation.CodingAgent;
        var persistedConversationId = conversation.AntigravityConversationId;
        // Discard old API/Pi/Codex bindings while keeping the transcript and
        // CompanionProfile. The selected Antigravity CLI/model are retained
        // only when an old Antigravity binding already supplied them.
        if (legacyMain is null || !string.Equals(legacyMain.Provider, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            var source = legacyCoding is { Provider: "antigravity" } ? legacyCoding : null;
            conversation.MainAgent = new ManagerMainAgentBinding
            {
                Provider = "antigravity", Transport = "cli", CliPath = source?.CliPath ?? AntigravityCliPath,
                Model = source?.Model ?? AntigravityModel, Effort = source?.Effort ?? AntigravityEffort, ToolPermission = AntigravityToolPermission, TerminalSandbox = AntigravityTerminalSandbox, TimeoutSeconds = 0,
            };
            legacyMain = conversation.MainAgent;
        }
        if (legacyCoding is null || !string.Equals(legacyCoding.Provider, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            conversation.CodingAgent = new ManagerCodingAgentBinding
            {
                Provider = "antigravity", CliPath = legacyMain?.CliPath ?? AntigravityCliPath,
                Model = legacyMain?.Model ?? AntigravityModel, Effort = legacyMain?.Effort ?? AntigravityEffort,
                AntigravityExecutionPolicy = "autonomous",
            };
        }
        conversation.PiSessionFile = null;
        conversation.MainAgent ??= new ManagerMainAgentBinding { Provider = "antigravity", Transport = "cli", CliPath = AntigravityCliPath, Model = AntigravityModel, Effort = AntigravityEffort };
        conversation.CodingAgent ??= new ManagerCodingAgentBinding { Provider = "antigravity", CliPath = AntigravityCliPath, Model = AntigravityModel, Effort = AntigravityEffort, AntigravityExecutionPolicy = "autonomous" };
        if (conversation.MainAgent.Provider == "antigravity")
        {
            var wasSdk = string.Equals(conversation.MainAgent.Transport, "sdk", StringComparison.OrdinalIgnoreCase);
            if (wasSdk) conversation.MainAgent.SdkSessionRef ??= conversation.MainAgent.SessionRef;
            if (wasSdk || string.Equals(conversation.MainAgent.Transport, "cli_interactive", StringComparison.OrdinalIgnoreCase))
            {
                // The old SDK/ConPTY session itself is not reusable by the
                // headless CLI. Prefer an explicitly retained CLI id; if it
                // is absent, start a fresh CLI conversation.
                conversation.MainAgent.LegacyCliConversationId ??= wasSdk
                    ? null
                    : conversation.MainAgent.SessionRef ?? persistedConversationId;
            }
            else
            {
                // A previously loaded SDK snapshot has its old SDK id kept
                // only in SdkSessionRef. Do not reinterpret that id as a CLI
                // conversation when no legacy CLI id was persisted.
                if (!(conversation.MainAgent.SdkSessionRef is not null && conversation.MainAgent.SessionRef is null && conversation.MainAgent.LegacyCliConversationId is null))
                    conversation.MainAgent.LegacyCliConversationId ??= conversation.MainAgent.SessionRef ?? persistedConversationId;
            }
            conversation.MainAgent.Transport = "cli";
            conversation.MainAgent.SessionRef = conversation.MainAgent.LegacyCliConversationId;
            conversation.AntigravityConversationId = conversation.MainAgent.LegacyCliConversationId;
            if (string.IsNullOrWhiteSpace(conversation.MainAgent.CliPath) || conversation.MainAgent.CliPath == "agy") conversation.MainAgent.CliPath = AntigravityCliPath;
            if (string.IsNullOrWhiteSpace(conversation.MainAgent.Model)) conversation.MainAgent.Model = AntigravityModel;
            conversation.MainAgent.Effort = string.IsNullOrWhiteSpace(conversation.MainAgent.Effort) ? AntigravityEffort : conversation.MainAgent.Effort;
            conversation.MainAgent.ToolPermission = NormalizeAntigravityToolPermission(conversation.MainAgent.ToolPermission);
            conversation.MainAgent.TimeoutSeconds = 0;
        }
        if (conversation.CodingAgent.Provider == "pi" && string.IsNullOrWhiteSpace(conversation.CodingAgent.BaseUrl))
        {
            conversation.CodingAgent.BaseUrl = BaseUrl; conversation.CodingAgent.ModelId = ModelId; conversation.CodingAgent.CredentialId = PiCredentialId;
            conversation.CodingAgent.AutoApproveSafeCommands = AutoApproveSafeCommands; conversation.CodingAgent.AutoApproveGitOperations = AutoApproveGitOperations;
        }
        if (conversation.CodingAgent.Provider == "antigravity")
        {
            if (string.IsNullOrWhiteSpace(conversation.CodingAgent.CliPath) || conversation.CodingAgent.CliPath == "agy") conversation.CodingAgent.CliPath = AntigravityCliPath;
            if (string.IsNullOrWhiteSpace(conversation.CodingAgent.Model)) conversation.CodingAgent.Model = AntigravityModel;
            conversation.CodingAgent.Effort = string.IsNullOrWhiteSpace(conversation.CodingAgent.Effort) ? AntigravityEffort : conversation.CodingAgent.Effort;
            conversation.CodingAgent.AntigravityExecutionPolicy = conversation.CodingAgent.AntigravityExecutionPolicy is "safe_tests" or "autonomous" ? conversation.CodingAgent.AntigravityExecutionPolicy : AntigravityExecutionPolicy;
        }
    }

    private static List<ManagerImageAttachment> CopyAttachmentsToConversation(ManagerConversationItem conversation, IReadOnlyList<ManagerImageAttachment> attachments)
    {
        if (attachments.Count > MaxImageAttachmentsPerMessage) throw new InvalidOperationException("单条消息最多添加 8 张图片。");
        var total = 0L;
        if (string.IsNullOrWhiteSpace(conversation.SessionId) ||
            !string.Equals(Path.GetFileName(conversation.SessionId), conversation.SessionId, StringComparison.Ordinal) ||
            conversation.SessionId.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidOperationException("会话标识无效，无法保存图片附件。");
        var destinationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IlMatto", "manager-sessions", "attachments", conversation.SessionId);
        Directory.CreateDirectory(destinationDirectory);
        var copied = new List<ManagerImageAttachment>(attachments.Count);
        foreach (var (attachment, index) in attachments.Select((item, index) => (item, index)))
        {
            var source = Path.GetFullPath(attachment.Path);
            var info = new FileInfo(source);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) throw new InvalidOperationException($"文件不存在或不是普通文件：{attachment.DisplayName}");
            if (info.Length > MaxImageAttachmentBytes) throw new InvalidOperationException($"单张图片不能超过 20 MiB：{attachment.DisplayName}");
            total += info.Length;
            if (total > MaxImageAttachmentsBytes) throw new InvalidOperationException("单条消息的图片总大小不能超过 64 MiB。");

            var extension = Path.GetExtension(source);
            var destination = Path.Combine(destinationDirectory, $"{Guid.NewGuid():N}{extension}");
            File.Copy(source, destination, false);
            copied.Add(new ManagerImageAttachment
            {
                Type = "image",
                AttachmentId = string.IsNullOrWhiteSpace(attachment.AttachmentId) ? Guid.NewGuid().ToString("N") : attachment.AttachmentId,
                Path = destination,
                DisplayName = string.IsNullOrWhiteSpace(attachment.DisplayName) ? Path.GetFileName(source) : attachment.DisplayName,
                MimeType = attachment.MimeType,
                Order = index,
                IsStaged = false,
            });
        }
        return copied;
    }

    private static string GetAttachmentStagingDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IlMatto", "manager-sessions", "staging");

    private static void DeleteStagedAttachment(ManagerImageAttachment attachment)
    {
        if (!attachment.IsStaged || string.IsNullOrWhiteSpace(attachment.Path)) return;
        try
        {
            var staging = Path.GetFullPath(GetAttachmentStagingDirectory()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(attachment.Path);
            if (path.StartsWith(staging, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
        catch { }
    }

    private string StateLabel(string? state) => state switch { "routing" => $"{CurrentMainDisplayName} 正在协调", "responding" => $"{CurrentMainDisplayName} 正在回答", "coding" => $"{CurrentCodingDisplayName} Coding Agent 正在工作", "waiting_approval" => "等待你的确认", "cancelled" => "已取消", "error" => "错误", _ => "准备就绪" };
    private static string FirstAvatarCharacter(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..1];
    private void ReloadAvatarImages()
    {
        UserAvatarImage = LoadAvatarImage(UserAvatarPath);
        AgentAvatarImage = LoadAvatarImage(AgentAvatarPath);
        OnPropertyChanged(nameof(HasUserAvatar));
        OnPropertyChanged(nameof(HasAgentAvatar));
    }

    private static BitmapImage? LoadAvatarImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void LoadSettings(AppSettings settings)
    {
        DefaultMainAgentProvider = settings.DefaultMainAgentProvider; DefaultCodingAgentProvider = settings.DefaultCodingAgentProvider;
        BaseUrl = settings.BaseUrl; ModelId = settings.ModelId; PiCredentialId = settings.PiCredentialId;
        MainApiBaseUrl = settings.MainApiBaseUrl; MainApiModelId = settings.MainApiModelId; MainApiCredentialId = settings.MainApiCredentialId; MainApiTimeoutSeconds = settings.MainApiTimeoutSeconds;
        WorkspacePath = settings.WorkspacePath; AutoApproveSafeCommands = settings.AutoApproveSafeCommands; AutoApproveGitOperations = settings.AutoApproveGitOperations;
        AntigravityCliPath = settings.AntigravityCliPath; AntigravityModel = settings.AntigravityModel; AntigravityEffort = settings.AntigravityEffort; AntigravityToolPermission = NormalizeAntigravityToolPermission(settings.AntigravityToolPermission); AntigravityTerminalSandbox = settings.AntigravityTerminalSandbox; AntigravityTimeoutSeconds = 0;
        AntigravityExecutionPolicy = settings.AntigravityExecutionPolicy is "safe_tests" or "autonomous" ? settings.AntigravityExecutionPolicy : "approval";
        CodexCliPath = settings.CodexCliPath; CodexModel = settings.CodexModel; CodexEffort = settings.CodexEffort;
        CodexApprovalPolicy = NormalizeCodexApprovalPolicy(settings.CodexApprovalPolicy);
        CodexSandboxMode = NormalizeCodexSandboxMode(settings.CodexSandboxMode);
        UserId = string.IsNullOrWhiteSpace(settings.UserId) ? "用户" : settings.UserId.Trim();
        UserAvatarPath = settings.UserAvatarPath?.Trim() ?? "";
        AgentAvatarPath = settings.AgentAvatarPath?.Trim() ?? "";
        ReloadAvatarImages();
        var companion = settings.DefaultCompanionProfile ?? new ManagerCompanionProfile();
        CompanionCharacterName = RoleCardComposer.NormalizeName(companion.CharacterName);
        CompanionCharacterPrompt = RoleCardComposer.RemoveGeneratedPrefix(companion.CharacterPrompt);
        CompanionUserProfile = companion.UserProfile;
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

    partial void OnSelectedConversationChanged(ManagerConversationItem? value)
    {
        if (value is not null)
        {
            RefreshMessageTimeMetadata(value);
        }
        RestoreDraft(value);
        ResetDraftSaveTimer();
        NotifyCurrentProviderChanged();
        if (_initialized) _ = ActivateSelectedConversationAsync();
    }
    partial void OnUserIdChanged(string value) => OnPropertyChanged(nameof(UserAvatarText));
    partial void OnUserAvatarPathChanged(string value)
    {
        UserAvatarImage = LoadAvatarImage(value);
        OnPropertyChanged(nameof(HasUserAvatar));
    }
    partial void OnAgentAvatarPathChanged(string value)
    {
        AgentAvatarImage = LoadAvatarImage(value);
        OnPropertyChanged(nameof(HasAgentAvatar));
    }
    partial void OnSelectedConversationChanging(ManagerConversationItem? oldValue, ManagerConversationItem? newValue)
    {
        if (oldValue is null || ReferenceEquals(oldValue, newValue)) return;
        CaptureCurrentDraft();
        Save();
        CaptureSessionUiState(oldValue);
        _managerTypewriterTimer?.Stop();
        _codingTextTimer?.Stop();
    }
    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (!_isRestoringDraft && SelectedConversation is not null)
        {
            if (!string.Equals(SelectedConversation.DraftText, value, StringComparison.Ordinal))
            {
                SelectedConversation.DraftText = value;
                _draftDirty = true;
            }
        }
        // Keep the visible policy selectors aligned with the explicit
        // @codex routing directive, while still allowing the model menu to
        // switch back to Antigravity for subsequent messages.
        if (HasCodexDirective(value) && SelectedModelProvider != "codex")
        {
            SelectedModelProvider = "codex";
            SelectedModelId = CodexModel;
            SelectedTaskExecutor = "codex";
        }
    }
    partial void OnIsBusyChanged(bool value) { SendCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanVerifyLastCodexTask)); VerifyLastCodexTaskCommand.NotifyCanExecuteChanged(); }
    partial void OnMainProviderAvailableChanged(bool value) => OnPropertyChanged(nameof(MainConnectionLabel));
    partial void OnMainProviderAuthenticatedChanged(bool value) => OnPropertyChanged(nameof(MainConnectionLabel));
    partial void OnMainProviderVersionChanged(string value) => OnPropertyChanged(nameof(MainConnectionLabel));
    partial void OnManagerCacheReadTokensChanged(long? value) => OnPropertyChanged(nameof(ManagerCacheLabel));
    partial void OnManagerContextTokensChanged(long? value) => OnPropertyChanged(nameof(ManagerContextLabel));
    partial void OnManagerContextWindowChanged(long? value) => OnPropertyChanged(nameof(ManagerContextLabel));

    partial void OnSelectedModelProviderChanged(string value)
    {
        if (value != "codex" && value != "antigravity") SelectedModelProvider = "antigravity";
        OnPropertyChanged(nameof(IsCodexModelSelected));
        OnPropertyChanged(nameof(IsAntigravityModelSelected));
        OnPropertyChanged(nameof(AntigravitySandboxMode));
        OnPropertyChanged(nameof(AntigravityApprovalPolicy));
        OnPropertyChanged(nameof(SelectedModelLabel));
        OnPropertyChanged(nameof(SelectedModelProviderLabel));
    }

    partial void OnSelectedModelIdChanged(string value) => OnPropertyChanged(nameof(SelectedModelLabel));

    partial void OnIsModelMenuOpenChanged(bool value)
    {
        if (value) _ = RefreshAgentModelsAsync();
    }

    partial void OnSelectedTaskExecutorChanged(string value)
    {
        if (string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase))
        {
            if (SelectedModelProvider != "codex") SelectedModelProvider = "codex";
            SelectedModelId = CodexModel;
        }
        else if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            if (SelectedModelProvider != "antigravity") SelectedModelProvider = "antigravity";
            SelectedModelId = AntigravityModel;
        }
        OnPropertyChanged(nameof(SelectedModelLabel));
    }

    partial void OnAntigravityModelChanged(string value)
    {
        if (SelectedModelProvider == "antigravity") { SelectedModelId = value; OnPropertyChanged(nameof(SelectedModelLabel)); }
        try { var settings = SettingsStore.Load(); settings.AntigravityModel = value.Trim(); SettingsStore.Save(settings); } catch { }
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    partial void OnCodexModelChanged(string value)
    {
        if (SelectedModelProvider == "codex") { SelectedModelId = value; OnPropertyChanged(nameof(SelectedModelLabel)); }
        try { var settings = SettingsStore.Load(); settings.CodexModel = value.Trim(); SettingsStore.Save(settings); } catch { }
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    partial void OnAntigravityEffortChanged(string value)
    {
        var normalized = NormalizeAntigravityEffort(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal)) { AntigravityEffort = normalized; return; }
        // Keep the concrete AGY model slug synchronized with the selected
        // effort when the CLI advertised -low/-medium/-high variants.
        var selectedAgyModel = AgentModelGroups.FirstOrDefault(item => item.Provider == "antigravity")?.Models
            .FirstOrDefault(item => item.MatchesModelId(AntigravityModel));
        var resolvedModelId = selectedAgyModel?.ResolveModelId(normalized);
        if (!string.IsNullOrWhiteSpace(resolvedModelId) && !string.Equals(resolvedModelId, AntigravityModel, StringComparison.OrdinalIgnoreCase))
            AntigravityModel = resolvedModelId;
        try { var settings = SettingsStore.Load(); settings.AntigravityEffort = normalized; SettingsStore.Save(settings); } catch { }
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    partial void OnCodexEffortChanged(string value)
    {
        var normalized = NormalizeCodexEffort(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal)) { CodexEffort = normalized; return; }
        try { var settings = SettingsStore.Load(); settings.CodexEffort = normalized; SettingsStore.Save(settings); } catch { }
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    partial void OnAntigravityToolPermissionChanged(string value)
    {
        var normalized = NormalizeAntigravityToolPermission(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal)) { AntigravityToolPermission = normalized; return; }
        OnPropertyChanged(nameof(AntigravityApprovalPolicy));
        try { var settings = SettingsStore.Load(); settings.AntigravityToolPermission = normalized; SettingsStore.Save(settings); } catch { }
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    partial void OnAntigravityTerminalSandboxChanged(bool value)
    {
        OnPropertyChanged(nameof(AntigravitySandboxMode));
        try { var settings = SettingsStore.Load(); settings.AntigravityTerminalSandbox = value; SettingsStore.Save(settings); } catch { }
        if (_initialized && SelectedConversation is not null) _ = RefreshSelectedAgentConfigurationAsync();
    }

    partial void OnCodexApprovalPolicyChanged(string value)
    {
        var normalized = NormalizeCodexApprovalPolicy(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            CodexApprovalPolicy = normalized;
            return;
        }
        try
        {
            var settings = SettingsStore.Load();
            settings.CodexApprovalPolicy = normalized;
            SettingsStore.Save(settings);
        }
        catch { /* Settings persistence must not block sending a message. */ }
        // Refresh the active ManagerHost session so a running process uses the
        // newly selected native policy on its next Codex turn.
        if (_initialized && SelectedConversation is not null) _ = RefreshCodexConfigurationAsync();
    }

    partial void OnCodexSandboxModeChanged(string value)
    {
        var normalized = NormalizeCodexSandboxMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            CodexSandboxMode = normalized;
            return;
        }
        try
        {
            var settings = SettingsStore.Load();
            settings.CodexSandboxMode = normalized;
            SettingsStore.Save(settings);
        }
        catch { /* Settings persistence must not block sending a message. */ }
        if (_initialized && SelectedConversation is not null) _ = RefreshCodexConfigurationAsync();
    }

    private async Task RefreshCodexConfigurationAsync()
    {
        try
        {
            if (SelectedConversation is not null)
            {
                await StartOrSyncSessionAsync(SelectedConversation);
            }
        }
        catch (Exception exception) { AddSystemMessage($"无法更新 Codex 审批策略：{exception.Message}"); }
    }

    private static string NormalizeCodexApprovalPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "manual" => "on-request",
        "always" => "always",
        "on-request" => "on-request",
        "never" => "never",
        "untrusted" => "untrusted",
        // Older IlMatto builds persisted this alias. Map it to the current
        // native enum instead of sending a custom value to Codex.
        "unlesstrusted" => "untrusted",
        _ => "on-request",
    };

    private static string NormalizeAntigravityToolPermission(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "request-review" or "request_review" or "review" => "request-review",
        "proceed-in-sandbox" or "proceed_in_sandbox" or "sandbox" => "proceed-in-sandbox",
        "always-proceed" or "always_proceed" or "always" => "always-proceed",
        "strict" => "strict",
        _ => "always-proceed",
    };

    private static string NormalizeAntigravityEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "high" => "high",
        _ => "medium",
    };

    private static string NormalizeCodexEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "minimal" => "minimal",
        "low" => "low",
        "high" => "high",
        "xhigh" => "xhigh",
        _ => "medium",
    };

    private static string NormalizeCodexSandboxMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "read-only" or "readonly" or "read_only" => "read-only",
        "danger-full-access" or "dangerfullaccess" or "danger_full_access" => "danger-full-access",
        "workspace-write" or "workspacewrite" or "workspace_write" => "workspace-write",
        _ => "workspace-write",
    };

    public async ValueTask DisposeAsync()
    {
        CaptureCurrentDraft();
        _draftSaveTimer?.Stop();
        StopManagerTypewriter();
        var shutdownAt = DateTimeOffset.UtcNow;
        foreach (var conversation in Conversations)
        {
            foreach (var message in conversation.Messages)
                if (message.Runtime?.IsActive == true) message.Runtime.Mark("cancelled", shutdownAt);
            if (_sessionUiStates.TryGetValue(conversation.SessionId, out var state))
            {
                if (state.StreamingManager?.Runtime?.IsActive == true) state.StreamingManager.Runtime.Mark("cancelled", shutdownAt);
                if (state.StreamingCoding?.Runtime?.IsActive == true) state.StreamingCoding.Runtime.Mark("cancelled", shutdownAt);
                if (state.StreamingManagerStatus?.Runtime?.IsActive == true) state.StreamingManagerStatus.Runtime.Mark("cancelled", shutdownAt);
                foreach (var activity in state.Activities)
                    if (activity.Runtime?.IsActive == true) activity.Runtime.Mark("cancelled", shutdownAt);
                state.IsBusy = false;
                state.CodexBusy = false;
            }
        }
        if (IsBusy && SelectedConversation is not null)
        {
            AppendMessage(SelectedConversation, new ManagerChatEntry("IlMatto", "system", "程序退出时任务仍在运行，已标记为会话中断；下次启动不会自动重放。"));
            RefreshMessageTimeMetadata(SelectedConversation);
        }
        IsBusy = false;
        _taskDurationTimer?.Stop();
        Save();
        if (_pipe is not null) { _pipe.EventReceived -= OnHostEvent; _pipe.TransportError -= OnTransportError; }
        if (_host is not null) await _host.DisposeAsync();
    }
}
