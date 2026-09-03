import net from "node:net";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { Type } from "@mariozechner/pi-ai";
import { AuthStorage, createAgentSession, DefaultResourceLoader, defineTool, ModelRegistry, SessionManager } from "@mariozechner/pi-coding-agent";
import { applyPatch, listFiles, readFile, runCommand, searchText } from "./tools.js";
import { commitGitChanges, createGitBranch, createInitialGitCommit, getGitBranches, getGitDiff, getGitLog, getGitOverview, getGitRemotes, getStagedSummary, initGitRepository, showGitCommit, stageGitFiles, switchGitBranch, unstageGitFiles } from "./git-service.js";
import { isClientMessage } from "./protocol.js";
import { normalizeWorkspace } from "./security.js";
import { isGitWriteTool, isSafeCommand } from "./approval-policy.js";
const inlinePipe = process.argv.find((arg) => arg.startsWith("--pipe="));
const pipeFlagIndex = process.argv.indexOf("--pipe");
const pipeArgument = inlinePipe?.slice("--pipe=".length) ?? (pipeFlagIndex >= 0 ? process.argv[pipeFlagIndex + 1] : undefined);
if (!pipeArgument) {
    console.error("Missing --pipe argument");
    process.exit(2);
}
const pipeName = pipeArgument.startsWith("\\\\.\\pipe\\") ? pipeArgument : `\\\\.\\pipe\\${pipeArgument}`;
const server = net.createServer((socket) => {
    socket.setEncoding("utf8");
    let buffer = "";
    const send = (message) => socket.write(`${JSON.stringify(message)}\n`);
    send({ type: "host_ready", version: "0.1.0" });
    socket.on("data", (chunk) => {
        buffer += chunk;
        let newline = buffer.indexOf("\n");
        while (newline >= 0) {
            const line = buffer.slice(0, newline).trim();
            buffer = buffer.slice(newline + 1);
            newline = buffer.indexOf("\n");
            if (!line)
                continue;
            try {
                const parsed = JSON.parse(line);
                if (!isClientMessage(parsed))
                    throw new Error("Invalid message");
                void handle(parsed, send).catch((error) => send({ type: "error", sessionId: "sessionId" in parsed ? parsed.sessionId : undefined, message: error instanceof Error ? error.message : "Request failed", code: "REQUEST_ERROR" }));
            }
            catch (error) {
                send({ type: "error", message: error instanceof Error ? error.message : "Invalid request", code: "PROTOCOL_ERROR" });
            }
        }
    });
});
const sessions = new Map();
async function handle(message, send) {
    if (message.type === "shutdown") {
        send({ type: "session_state", sessionId: message.sessionId ?? "", state: "cancelled" });
        server.close();
        process.exit(0);
    }
    if (message.type === "approve_tool_call") {
        const session = sessions.get(message.sessionId);
        if (!session)
            throw new Error("Session not found");
        if (!session.resolveApproval(message.callId, message.approved))
            send({ type: "error", sessionId: message.sessionId, message: `Approval request not found: ${message.callId}`, code: "APPROVAL_NOT_FOUND" });
        return;
    }
    if (message.type === "start_session" || message.type === "new_session") {
        const workspacePath = normalizeWorkspace(message.workspacePath);
        const existing = sessions.get(message.sessionId);
        if (existing && message.type === "start_session" && existing.matches(workspacePath, message.baseUrl, message.modelId, message.apiKey, message.autoApproveSafeCommands ?? false, message.autoApproveGitOperations ?? false)) {
            existing.emitCommands();
            await existing.emitGitOverview();
            return;
        }
        existing?.cancel();
        const session = new AgentSession(message.sessionId, workspacePath, message.baseUrl, message.modelId, message.apiKey, message.autoApproveSafeCommands ?? false, message.autoApproveGitOperations ?? false, send);
        sessions.set(message.sessionId, session);
        await session.start();
        return;
    }
    const session = sessions.get(message.sessionId);
    if (!session)
        throw new Error("Session not found");
    if (message.type === "send_message")
        await session.prompt(message.text);
    if (message.type === "cancel")
        session.cancel();
    if (message.type === "get_commands")
        session.emitCommands();
    if (message.type === "get_git_overview")
        await session.emitGitOverview();
    if (message.type === "get_git_diff")
        await session.emitGitDiff(message.scope, message.path);
}
const HOST_COMMANDS = [
    ["settings", "Open IlMatto settings"], ["model", "Select the configured model"],
    ["scoped-models", "Enable or disable models for cycling"], ["export", "Export the current session"],
    ["import", "Import a session"], ["share", "Share the session"], ["copy", "Copy the last assistant message"],
    ["name", "Set the session display name"], ["session", "Show session information"], ["changelog", "Show changelog entries"],
    ["hotkeys", "Show keyboard shortcuts"], ["fork", "Create a new fork"], ["clone", "Duplicate the current session"],
    ["tree", "Navigate the session tree"], ["login", "Configure provider authentication"], ["logout", "Remove provider authentication"],
    ["new", "Start a new session"], ["compact", "Compact the session context"], ["resume", "Resume another session"],
    ["reload", "Reload extensions and prompts"], ["quit", "Quit IlMatto"], ["help", "Show available commands"],
    ["commands", "Show available commands"],
];
class AgentSession {
    id;
    workspace;
    baseUrl;
    modelId;
    apiKey;
    autoApproveSafeCommands;
    autoApproveGitOperations;
    send;
    cancelled = false;
    promptRunning = false;
    piSession;
    pendingApprovals = new Map();
    autoApprovedCalls = new Set();
    commands = HOST_COMMANDS.map(([name, description]) => ({ name, description, source: "extension" }));
    constructor(id, workspace, baseUrl, modelId, apiKey, autoApproveSafeCommands, autoApproveGitOperations, send) {
        this.id = id;
        this.workspace = workspace;
        this.baseUrl = baseUrl;
        this.modelId = modelId;
        this.apiKey = apiKey;
        this.autoApproveSafeCommands = autoApproveSafeCommands;
        this.autoApproveGitOperations = autoApproveGitOperations;
        this.send = send;
    }
    async start() {
        const authStorage = AuthStorage.inMemory();
        if (this.apiKey)
            authStorage.setRuntimeApiKey("ilmatto", this.apiKey);
        const modelRegistry = ModelRegistry.inMemory(authStorage);
        modelRegistry.registerProvider("ilmatto", {
            name: "IlMatto OpenAI-compatible",
            baseUrl: this.baseUrl,
            api: "openai-completions",
            apiKey: "runtime",
            models: [{ id: this.modelId, name: this.modelId, reasoning: false, input: ["text"], contextWindow: 128000, maxTokens: 16384, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } }],
        });
        const model = modelRegistry.find("ilmatto", this.modelId);
        if (!model)
            throw new Error(`Model not found: ${this.modelId}`);
        const resourceLoader = new DefaultResourceLoader({
            cwd: this.workspace,
            agentDir: path.join(os.tmpdir(), "ilmatto-agent-resources"),
            systemPrompt: [
                "You are IlMatto, a cautious coding agent.",
                "Use only the provided workspace tools. Inspect before changing files.",
                "File changes and PowerShell commands require explicit user approval.",
                "Use the dedicated Git tools for repository inspection and local Git changes; never invoke Git through PowerShell.",
                "Git file status, diffs, staging, and commits are scoped to the selected workspace. Creating or switching a branch is repository-wide and requires the selected workspace to be the repository root.",
                "If Git status reports that the workspace is not a repository and version control would help, call git_init to initialize the selected workspace. git_init is intentionally auto-approved, stages the current workspace files, and creates a local Initial commit when there are files to commit; it never contacts a remote. If the workspace is inside an ancestor repository, use that repository instead of initializing a nested one.",
                "Before a Git change, inspect status and the relevant diff. Before committing, inspect staged files and summarize the commit. Do not include unstaged files in a commit.",
                "Never attempt remote Git operations, destructive Git commands, or Git configuration changes. Explain branch-switch conflicts and missing Git identity instead of working around them.",
                "After an approved change, run an appropriate read-only check or test when useful, then summarize.",
            ].join("\n"),
            extensionFactories: [
                (pi) => {
                    for (const [name, description] of HOST_COMMANDS) {
                        pi.registerCommand(name, {
                            description,
                            handler: async (args, ctx) => this.handleCommand(name, args, ctx),
                        });
                    }
                },
            ],
        });
        await resourceLoader.reload();
        const customTools = createPiTools(this);
        const created = await createAgentSession({
            cwd: this.workspace,
            authStorage,
            modelRegistry,
            model,
            resourceLoader,
            sessionManager: SessionManager.inMemory(this.workspace),
            tools: customTools.map((tool) => tool.name),
            customTools,
        });
        this.piSession = created.session;
        const runtimeCommands = created.extensionsResult.runtime.getCommands?.() ?? [];
        this.commands = [...this.commands, ...runtimeCommands
                .filter((command) => !this.commands.some((known) => known.name === command.name))
                .map((command) => ({ name: command.name, description: command.description, source: command.source ?? "extension" }))];
        const activeToolNames = this.piSession.getActiveToolNames?.() ?? [];
        if (activeToolNames.length !== customTools.length)
            throw new Error(`Tool configuration mismatch: expected ${customTools.length}, got ${activeToolNames.length}`);
        this.piSession.subscribe((event) => {
            if (event?.type === "message_update" && event.assistantMessageEvent?.type === "text_delta")
                this.emitDelta(event.assistantMessageEvent.delta);
            if (event?.type === "message_update" && event.assistantMessageEvent?.type === "thinking_delta")
                this.emitThinking(event.assistantMessageEvent.delta);
        });
        await this.autoInitializeGitIfNeeded();
        this.emitCommands();
        this.send({ type: "session_state", sessionId: this.id, state: "idle" });
        await this.emitGitOverview();
    }
    async prompt(text) {
        if (this.promptRunning) {
            this.send({ type: "error", sessionId: this.id, message: "A prompt is already running", code: "BUSY" });
            return;
        }
        this.promptRunning = true;
        this.cancelled = false;
        this.send({ type: "session_state", sessionId: this.id, state: "thinking" });
        try {
            if (!this.piSession)
                throw new Error("Agent session is not initialized");
            await this.piSession.prompt(text);
            if (!this.cancelled)
                this.send({ type: "assistant_completed", sessionId: this.id, text: "" });
            this.send({ type: "session_state", sessionId: this.id, state: this.cancelled ? "cancelled" : "idle" });
        }
        catch (error) {
            this.send({ type: "error", sessionId: this.id, message: error instanceof Error ? error.message : "Agent failed", code: "AGENT_ERROR" });
            this.send({ type: "session_state", sessionId: this.id, state: "error" });
        }
        finally {
            this.promptRunning = false;
        }
    }
    cancel() {
        this.cancelled = true;
        for (const resolve of this.pendingApprovals.values())
            resolve(false);
        this.pendingApprovals.clear();
    }
    get metadata() { return { id: this.id, workspace: this.workspace, baseUrl: this.baseUrl, modelId: this.modelId, apiKey: this.apiKey }; }
    matches(workspace, baseUrl, modelId, apiKey, autoApproveSafeCommands = false, autoApproveGitOperations = false) { return this.workspace === workspace && this.baseUrl === baseUrl && this.modelId === modelId && this.apiKey === apiKey && this.autoApproveSafeCommands === autoApproveSafeCommands && this.autoApproveGitOperations === autoApproveGitOperations; }
    emitDelta(text) { if (!this.cancelled)
        this.send({ type: "assistant_delta", sessionId: this.id, text }); }
    emitThinking(text) { if (!this.cancelled && text)
        this.send({ type: "thinking_delta", sessionId: this.id, text }); }
    async autoInitializeGitIfNeeded() {
        const overview = await getGitOverview(this.workspace);
        const workspaceIsRepositoryRoot = Boolean(overview.root && path.resolve(overview.root).toLowerCase() === path.resolve(this.workspace).toLowerCase());
        const needsInitialization = !overview.isRepository && Boolean(overview.message?.toLowerCase().includes("not inside a git repository"));
        const needsInitialCommit = overview.isRepository && workspaceIsRepositoryRoot && overview.commits.length === 0;
        if (!needsInitialization && !needsInitialCommit)
            return;
        const callId = `auto-git-init-${this.id}`;
        this.autoApprovedCalls.add(callId);
        this.toolStarted(callId, "git_init", `git init ${this.workspace}`);
        try {
            const output = needsInitialization ? await initGitRepository(this.workspace) : await createInitialGitCommit(this.workspace);
            this.toolCompleted(callId, "git_init", true, needsInitialization ? "Git 已自动初始化并创建初始提交" : "Git 初始提交已自动完成", output);
        }
        catch (error) {
            const message = error instanceof Error ? error.message : "Git 初始化失败";
            this.toolCompleted(callId, "git_init", false, message, message);
        }
    }
    emitCommands() { this.send({ type: "slash_commands", sessionId: this.id, commands: this.commands }); }
    async handleCommand(name, args, ctx) {
        if (name === "compact") {
            ctx.compact();
            this.emitCommandResult(name, "已请求压缩当前会话上下文。");
            return;
        }
        if (name === "reload") {
            await ctx.reload();
            this.emitCommands();
            this.emitCommandResult(name, "已重新加载扩展、技能和提示模板。");
            return;
        }
        if (name === "help" || name === "commands") {
            this.emitCommandResult(name, this.commands.map((command) => `/${command.name} — ${command.description ?? ""}`).join("\n"));
            return;
        }
        if (name === "session") {
            this.emitCommandResult(name, `会话 ${this.id}\n工作区：${this.workspace}`);
            return;
        }
        if (name === "name") {
            this.emitCommandResult(name, args.trim() ? `会话名称参数：${args.trim()}` : "请在 /name 后提供名称；桌面端会话标题会根据首条消息生成。");
            return;
        }
        this.emitCommandResult(name, `/${name} 已识别。该命令的交互式 UI 尚未在 IlMatto 中开放，请使用设置、历史会话和侧栏操作。`);
    }
    emitCommandResult(command, message) { this.send({ type: "command_result", sessionId: this.id, command, message }); }
    toolStarted(callId, tool, command) { this.send({ type: "tool_started", sessionId: this.id, callId, tool, command }); }
    toolCompleted(callId, tool, ok, summary, output) {
        const autoApproved = this.autoApprovedCalls.delete(callId);
        this.send({ type: "tool_completed", sessionId: this.id, callId, tool, ok, summary, output, autoApproved: autoApproved || undefined });
    }
    resolveApproval(callId, approved) {
        const resolve = this.pendingApprovals.get(callId);
        if (!resolve)
            return false;
        this.pendingApprovals.delete(callId);
        resolve(approved);
        return true;
    }
    async approve(callId, request) {
        if (this.autoApproveSafeCommands && request.tool === "run_command" && isSafeCommand(request.command ?? ""))
            return true;
        if (request.tool === "git_init") {
            this.autoApprovedCalls.add(callId);
            return true;
        }
        if (this.autoApproveGitOperations && isGitWriteTool(request.tool)) {
            this.autoApprovedCalls.add(callId);
            return true;
        }
        return new Promise((resolve) => {
            // Register before notifying the desktop so an immediate response can
            // never arrive before the approval waiter exists.
            this.pendingApprovals.set(callId, resolve);
            this.send({ type: "tool_approval_request", sessionId: this.id, callId, tool: request.tool, summary: request.summary, details: request.details, diff: request.diff });
            this.send({ type: "session_state", sessionId: this.id, state: "waiting_approval" });
        });
    }
    async emitGitOverview() { this.send({ type: "git_overview", sessionId: this.id, overview: await getGitOverview(this.workspace) }); }
    async emitGitDiff(scope, relativePath) {
        try {
            const result = await getGitDiff(this.workspace, scope, relativePath);
            this.send({ type: "git_diff", sessionId: this.id, scope: result.scope, path: result.path, content: result.content, truncated: result.truncated });
        }
        catch (error) {
            this.send({ type: "git_diff", sessionId: this.id, scope, path: relativePath, content: error instanceof Error ? error.message : "Unable to read Git diff", truncated: false });
        }
    }
}
function createPiTools(session) {
    const executeReadOnly = async (callId, tool, command, action) => {
        session.toolStarted(callId, tool, command);
        try {
            const output = await action();
            session.toolCompleted(callId, tool, true, "只读操作完成", output);
            return { content: [{ type: "text", text: output }], details: { output } };
        }
        catch (error) {
            const message = error instanceof Error ? error.message : "Tool failed";
            session.toolCompleted(callId, tool, false, message);
            return { content: [{ type: "text", text: message }], details: { error: message }, isError: true };
        }
    };
    const executeApproval = async (callId, tool, command, action) => {
        session.toolStarted(callId, tool, command);
        try {
            const output = await action();
            session.toolCompleted(callId, tool, !output.startsWith("User denied"), output, output);
            return { content: [{ type: "text", text: output }], details: { output } };
        }
        catch (error) {
            const message = error instanceof Error ? error.message : "Tool failed";
            session.toolCompleted(callId, tool, false, message);
            return { content: [{ type: "text", text: message }], details: { error: message }, isError: true };
        }
    };
    const executeGitWrite = async (callId, tool, command, summary, details, action) => executeApproval(callId, tool, command, async () => {
        const approved = await session.approve(callId, { tool, summary, details, command });
        if (!approved)
            return "User denied the Git operation.";
        const output = await action();
        await session.emitGitOverview();
        return output;
    });
    return [
        defineTool({ name: "list_files", label: "List files", description: "List files and directories in the selected workspace.", parameters: Type.Object({ relativePath: Type.Optional(Type.String()) }), async execute(callId, params) { const relativePath = params.relativePath ?? "."; return executeReadOnly(callId, "list_files", `list_files ${relativePath}`, () => listFiles(session.metadata.workspace, relativePath)); } }),
        defineTool({ name: "read_file", label: "Read file", description: "Read a UTF-8 text file inside the selected workspace.", parameters: Type.Object({ relativePath: Type.String() }), async execute(callId, params) { return executeReadOnly(callId, "read_file", `read_file ${params.relativePath}`, () => readFile(session.metadata.workspace, params.relativePath)); } }),
        defineTool({ name: "search_text", label: "Search text", description: "Search text with ripgrep inside the selected workspace.", parameters: Type.Object({ query: Type.String() }), async execute(callId, params) { return executeReadOnly(callId, "search_text", `search_text ${params.query}`, () => searchText(session.metadata.workspace, params.query)); } }),
        defineTool({ name: "git_init", label: "Initialize Git repository", description: "Initialize a local Git repository, stage the current workspace, and create an Initial commit when files exist. This never contacts a remote.", parameters: Type.Object({}), async execute(callId) { return executeApproval(callId, "git_init", `git init ${session.metadata.workspace}`, async () => { const approved = await session.approve(callId, { tool: "git_init", summary: "Initialize Git repository and create initial commit", details: `This will create a .git directory and include all current files in the selected workspace in an Initial commit:\n${session.metadata.workspace}\n\nNo remote will be contacted.`, command: `git init ${session.metadata.workspace}` }); if (!approved)
                return "User denied Git initialization."; const output = await initGitRepository(session.metadata.workspace); await session.emitGitOverview(); return output; }); } }),
        defineTool({ name: "git_status", label: "Git status", description: "Show the selected repository's branch, changes, tracking state, and recent commits.", parameters: Type.Object({}), async execute(callId) { return executeReadOnly(callId, "git_status", "git status --porcelain=v1 --branch --untracked-files=all", async () => formatGitOverview(await getGitOverview(session.metadata.workspace))); } }),
        defineTool({ name: "git_diff", label: "Git diff", description: "Show a working-tree or staged Git diff, optionally for one path relative to the selected workspace.", parameters: Type.Object({ scope: Type.Optional(Type.Union([Type.Literal("working"), Type.Literal("staged")])), relativePath: Type.Optional(Type.String()) }), async execute(callId, params) { const scope = params.scope === "staged" ? "staged" : "working"; return executeReadOnly(callId, "git_diff", `git diff ${scope}${params.relativePath ? ` -- ${params.relativePath}` : ""}`, async () => (await getGitDiff(session.metadata.workspace, scope, params.relativePath)).content || "No changes in this diff."); } }),
        defineTool({ name: "git_log", label: "Git log", description: "Show up to 25 recent local commits. This never contacts remotes.", parameters: Type.Object({}), async execute(callId) { return executeReadOnly(callId, "git_log", "git log -25 --oneline", () => getGitLog(session.metadata.workspace)); } }),
        defineTool({ name: "git_branches", label: "Git branches", description: "Show local branches and their tracking configuration.", parameters: Type.Object({}), async execute(callId) { return executeReadOnly(callId, "git_branches", "git branch --verbose", () => getGitBranches(session.metadata.workspace)); } }),
        defineTool({ name: "git_show", label: "Git show", description: "Show one local commit by revision, including its summary and diff statistics.", parameters: Type.Object({ revision: Type.String() }), async execute(callId, params) { return executeReadOnly(callId, "git_show", `git show ${params.revision}`, () => showGitCommit(session.metadata.workspace, params.revision)); } }),
        defineTool({ name: "git_remotes", label: "Git remotes", description: "Show locally configured remote names and URLs without making network requests.", parameters: Type.Object({}), async execute(callId) { return executeReadOnly(callId, "git_remotes", "git remote -v", () => getGitRemotes(session.metadata.workspace)); } }),
        defineTool({ name: "git_stage", label: "Git stage", description: "Stage specified paths relative to the selected workspace after approval.", parameters: Type.Object({ paths: Type.Array(Type.String()) }), async execute(callId, params) { const paths = params.paths; return executeGitWrite(callId, "git_stage", `git add -- ${paths.join(" ")}`, "Stage Git changes", `Files to stage:\n${paths.join("\n")}`, () => stageGitFiles(session.metadata.workspace, paths)); } }),
        defineTool({ name: "git_unstage", label: "Git unstage", description: "Remove selected-workspace paths from the Git staging area after approval; working files are not changed.", parameters: Type.Object({ paths: Type.Array(Type.String()) }), async execute(callId, params) { const paths = params.paths; return executeGitWrite(callId, "git_unstage", `git restore --staged -- ${paths.join(" ")}`, "Unstage Git changes", `Files to unstage (working files stay unchanged):\n${paths.join("\n")}`, () => unstageGitFiles(session.metadata.workspace, paths)); } }),
        defineTool({ name: "git_create_branch", label: "Create Git branch", description: "Create and switch to a new local branch after approval. No remote branch is created.", parameters: Type.Object({ name: Type.String() }), async execute(callId, params) { return executeGitWrite(callId, "git_create_branch", `git switch -c ${params.name}`, "Create and switch Git branch", `New local branch: ${params.name}`, () => createGitBranch(session.metadata.workspace, params.name)); } }),
        defineTool({ name: "git_switch_branch", label: "Switch Git branch", description: "Switch to an existing local branch after approval. It will not stash or overwrite worktree changes.", parameters: Type.Object({ name: Type.String() }), async execute(callId, params) { return executeGitWrite(callId, "git_switch_branch", `git switch ${params.name}`, "Switch Git branch", `Target local branch: ${params.name}\nGit will refuse this if local changes would be overwritten.`, () => switchGitBranch(session.metadata.workspace, params.name)); } }),
        defineTool({ name: "git_commit", label: "Commit staged Git changes", description: "Create a local commit from already staged changes after approval. Hooks are not run and no remote is contacted.", parameters: Type.Object({ message: Type.String() }), async execute(callId, params) { const staged = await getStagedSummary(session.metadata.workspace); return executeGitWrite(callId, "git_commit", "git commit --no-verify", "Create local Git commit", `Commit message:\n${params.message}\n\nStaged files:\n${staged}`, () => commitGitChanges(session.metadata.workspace, params.message)); } }),
        defineTool({ name: "apply_patch", label: "Apply patch", description: "Replace a text file after displaying a diff and receiving user approval.", parameters: Type.Object({ relativePath: Type.String(), content: Type.String() }), async execute(callId, params) { return executeApproval(callId, "apply_patch", `apply_patch ${params.relativePath}`, () => applyPatch(session.metadata.workspace, params.relativePath, params.content, (request) => session.approve(callId, request))); } }),
        defineTool({ name: "run_command", label: "Run PowerShell", description: "Run a non-interactive PowerShell command in the selected workspace after user approval.", parameters: Type.Object({ command: Type.String() }), async execute(callId, params) { return executeApproval(callId, "run_command", params.command, () => runCommand(session.metadata.workspace, params.command, (request) => session.approve(callId, request))); } }),
    ];
}
function formatGitOverview(overview) {
    if (!overview.isRepository)
        return overview.message ?? "当前工作区不在 Git 仓库中";
    const lines = [
        `Repository: ${overview.root}`,
        `Branch: ${overview.branch}${overview.upstream ? ` -> ${overview.upstream}` : ""}`,
        `Tracking: ahead ${overview.ahead}, behind ${overview.behind}`,
        `Staged (${overview.staged.length}): ${overview.staged.map((item) => `${item.status} ${item.path}`).join(", ") || "none"}`,
        `Unstaged (${overview.unstaged.length}): ${overview.unstaged.map((item) => `${item.status} ${item.path}`).join(", ") || "none"}`,
        `Untracked (${overview.untracked.length}): ${overview.untracked.map((item) => item.path).join(", ") || "none"}`,
    ];
    if (overview.remotes.length)
        lines.push(`Configured remotes (local metadata only): ${overview.remotes.map((remote) => `${remote.name} ${remote.fetchUrl ?? remote.pushUrl ?? ""}`).join(", ")}`);
    return lines.join("\n");
}
server.listen(pipeName);
//# sourceMappingURL=index.js.map