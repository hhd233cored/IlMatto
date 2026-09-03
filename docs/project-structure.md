# IlMatto 项目结构文档

## 1. 项目定位

IlMatto 是运行在 Windows 开发机上的 WPF Agent。桌面端提供交互界面和本地状态管理；ManagerHost 维护一个统一的 Antigravity CLI 会话，同时处理陪伴对话、项目分析、文件修改、命令执行和测试。

当前代码是单工作区、单活动 Antigravity 会话的 MVP。系统支持两种入口：

- Manager 界面：默认启动，使用一个同时承担聊天和编码的 Antigravity Agent。
- Pi Coding 工作台：保留原有的独立 Pi 编码界面，可从 Manager 的“视图”菜单打开。

## 2. 顶层目录

```text
IlMatto/
├── IlMatto.sln                         .NET 解决方案
├── README.md                           构建、运行和使用说明
├── NuGet.config                        NuGet 配置
├── src/
│   ├── IlMatto.Desktop/                WPF 桌面应用
│   ├── IlMatto.ManagerHost/            Manager Node.js/TypeScript Host
│   ├── IlMatto.AgentHost/              Pi Agent Node.js/TypeScript Host
│   └── IlMatto.AntigravityBridge/      Antigravity Python SDK NDJSON Bridge
└── artifacts/                          构建或验证产物
```

`dist/`、`bin/` 和 `obj/` 是构建输出或中间文件，不是主要源码边界。源码阅读和修改应优先集中在各项目的 `src/`、WPF XAML/C# 文件和配置文件上。

## 3. 运行时架构

```text
┌─────────────────────────────────────────────────────────────┐
│ IlMatto.Desktop                                             │
│  ManagerWindow / ManagerViewModel                           │
│  MainWindow / MainViewModel（独立 Pi 工作台）               │
└───────────────┬───────────────────────────┬─────────────────┘
                │ Windows Named Pipe        │ Windows Named Pipe
                ▼                           ▼
┌───────────────────────────┐   ┌─────────────────────────────┐
│ IlMatto.ManagerHost        │   │ IlMatto.AgentHost             │
│ ManagerSession             │   │ AgentSession                  │
│ Unified AGY text session   │   │ Pi Coding Agent + 本地工具    │
└───────────────┬───────────┘   └──────────────┬──────────────┘
                │                              │
                └─ Antigravity CLI（同一进程，完整权限）
                   stream-json ↔ 工作区文件/命令/MCP
```

可选 Codex 观察路径：

```text
Antigravity ──只读 MCP Facade──> ManagerHost ──(用户确认后)──> Codex App Server
```

MCP Facade 只允许生成待确认草稿和读取状态/报告；Codex 的控制接口仅存在于
ManagerHost 内部。ManagerHost 会把带有当前管道名和会话 ID 的 Facade 条目临时写入
Antigravity 用户级全局 `%USERPROFILE%/.gemini/config/mcp_config.json`，保留已有
Server；会话结束时清理自身且未被修改的条目。Codex 事件和线程 ID 不发送给
Antigravity。

### 3.1 桌面端与 Host 的边界

- 桌面端负责窗口、绑定、会话列表、聊天时间线、审批卡片和结果展示。
- Host 负责启动 Provider、维护 Provider 会话、执行工具以及将事件转成桌面协议。
- 桌面端不直接执行 Agent 的文件工具、PowerShell 或 Git 操作。
- 桌面端与 Host 之间使用“一行一个 JSON 对象”的 NDJSON 通信格式；传输通道是本机 Windows Named Pipe。

## 4. `src/IlMatto.Desktop`

### 4.1 入口与窗口

| 文件 | 职责 |
| --- | --- |
| `App.xaml` / `App.xaml.cs` | WPF 应用入口、全局资源、未处理异常捕获和启动日志。默认创建 `ManagerWindow`。 |
| `ManagerWindow.xaml` / `.xaml.cs` | Manager 主界面：对话历史、聊天区、审批区、输入区、Agent 过程和设置入口。 |
| `ManagerViewModel.cs` | Manager 对话生命周期、Named Pipe 事件分发、统一 Antigravity 状态迁移和兼容旧版时间线展示。 |
| `ManagerSettingsWindow.xaml` / `.xaml.cs` | 兼容旧配置字段的设置窗口；统一 Manager 当前只使用 Antigravity 和工作区/陪伴资料。 |
| `MainWindow.xaml` / `.xaml.cs` | 独立 Pi Coding 工作台。 |
| `MainViewModel.cs` | Pi 交互会话、工具审批、Slash Command、过程记录、上下文指标和 Git 面板。 |
| `SettingsWindow.xaml` / `.xaml.cs` | 独立 Pi 工作台设置。 |
| `SettingsViewModel.cs` | 独立 Pi 工作台设置的编辑和保存。 |

### 4.2 `Models/`

UI 使用的状态模型和展示模型：

- `ManagerConversationItem` / `ManagerChatEntry`：Manager 对话、消息、Provider 绑定和 Coding Agent 结果。
- `ConversationItem` / `ChatEntry`：独立 Pi 工作台的对话和消息。
- `ChatSegment` / `ProcessItem`：将文本、思路和工具操作保存在同一条时间线中，并支持折叠。
- `ApprovalRequest`：旧版命令/文件审批和交互请求模型；统一 Antigravity 的工具授权由 CLI 自身处理。
- `ActivityItem` / `ManagerActivity`：右侧过程面板中的活动记录。
- `EditedFileItem`：独立 Pi 工作台中已修改文件及增删行统计。
- `GitModels.cs`：工作区范围内的变更、分支、提交、远端和 Git 总览。
- `SlashCommandItem`：内置及 Provider 扩展指令的补全项。

### 4.3 `Infrastructure/`

| 文件 | 职责 |
| --- | --- |
| `PipeClient.cs` | 独立 AgentHost 的 Named Pipe 客户端和 Node 子进程生命周期。 |
| `ManagerPipeClient.cs` | ManagerHost 的 Named Pipe 客户端和 Node 子进程生命周期。 |
| `ClipboardBroker.cs` | 临时保存、设置和条件恢复系统剪贴板中的受管图片。 |
| `ConPtySession.cs` | 创建隐藏 Windows ConPTY，并向交互式 Antigravity CLI 发送文本和控制键。 |
| `InteractiveCliController.cs` | 处理 ManagerHost 的交互式 CLI 请求，将 ConPTY 与剪贴板操作串行化。 |
| `AgentProtocol.cs` | 独立 Pi 工作台与 AgentHost 的消息模型。 |
| `ManagerProtocol.cs` | Manager 与 ManagerHost 的消息模型。 |
| `SettingsStore.cs` | 全局配置的 JSON 读写。 |
| `ConversationStore.cs` | 独立 Pi 对话、过程记录和会话快照的读写。 |
| `ManagerConversationStore.cs` | Manager 对话、Provider 绑定和结构化结果的读写及旧格式迁移。 |
| `CredentialStore.cs` | 使用 Windows Credential Manager 保存、读取和删除 API Key。 |

### 4.4 `Controls/`

`MarkdownViewer.cs` 是聊天内容的自定义 WPF 展示控件，负责渲染常用 Markdown 和数学表达式，供 Manager 与 Pi 工作台共用。

## 5. `src/IlMatto.ManagerHost`

ManagerHost 是独立的 TypeScript/Node.js 进程。它只维护一个活动的 `ManagerSession` 和一个统一的 Antigravity CLI 文本会话。

| 文件 | 职责 |
| --- | --- |
| `src/index.ts` | Named Pipe Server、统一 ManagerSession 生命周期、文本/工具事件转发和删除/关闭处理。 |
| `src/companion.ts` | CompanionProfile 默认值、角色系统提示词和陪伴上下文规范化。 |
| `src/protocol.ts` | ManagerHost 与桌面端的兼容协议和图片附件校验。 |
| `src/runtime.ts` | 统一会话的日志/附件运行目录；不再生成 Agent、Schema 或全局权限文件。 |
| `src/antigravity.ts` | Antigravity CLI 探测、Headless 文本流和工具事件解析。 |
| `src/antigravity-sdk.ts` | 启动 Python SDK Bridge、转发 NDJSON、接收流式文本/状态/结构化结果，并管理 SDK 会话目录。 |
| `src/antigravity-interactive.ts` | 通过桌面反向协议驱动隐藏交互式 CLI，解析终端输出并校验 ManagerAction。 |
| `src/coordinator.ts` | 旧 API Coordinator 的兼容实现，仅供旧会话迁移测试使用，不是统一 Manager 的运行路径。 |
| `src/codex-worker.ts` | Codex App Server 适配代码；结构化 Coding Worker 仅保留兼容测试，观察模式使用普通文本 Turn。 |
| `src/codex-observation.ts` | Codex 草稿确认、单会话隐藏 thread、事件持久化、报告生成和工作区指纹。 |
| `src/codex-mcp.ts` | 面向 Antigravity 的本地 MCP Facade，只暴露草稿和只读观察工具/资源。 |
| `src/*.test.ts` | Antigravity 文本流、单进程生命周期、协议兼容和 Host 集成测试。 |

### 5.1.1 Antigravity CLI 通道

统一 Manager 使用 `src/antigravity.ts` 的单一 `stream-json` 进程。图片会先复制到受管附件目录，再把路径作为上下文交给同一会话；当前不切换 ConPTY/交互式 CLI。

### 5.1.2 Antigravity SDK Bridge

`src/IlMatto.AntigravityBridge/bridge.py` 是旧版 SDK 兼容代码。统一 Manager 当前不启动 Python Bridge，而是直接使用 CLI 文本通道；该 Bridge 仅为后续或旧快照恢复保留。

SDK 会话仍可由旧快照恢复；旧快照缺少 `transport` 时按 CLI 处理。当前新 Manager 会话不会迁移到 SDK。

### 5.1 ManagerHost 的运行方式

1. 所有消息直接进入同一个 Antigravity CLI 上下文，不再经过主 Agent/Coding Worker 委派。
2. Antigravity 使用 `--mode accept-edits` 与 `--dangerously-skip-permissions`，自行决定工具和命令。
3. Host 只转发文本、思考、工具状态和错误，不解析强制 JSON，也不拦截工具。
4. Codex 观察通道默认不启动；用户确认 Antigravity 生成的草稿后，ManagerHost 才启动普通文本 Codex App Server Turn。
5. Antigravity 只能按需读取 Codex 摘要报告，不能提交、继续、steer 或中断 Codex。

## 6. `src/IlMatto.AgentHost`

AgentHost 是独立的 TypeScript/Node.js 进程，底层使用 `@mariozechner/pi-coding-agent`，继续作为独立 Pi 工作台的后端。统一 Manager 不再启动其 `coding_worker` 模式。

| 文件 | 职责 |
| --- | --- |
| `src/index.ts` | Named Pipe Server、Pi AgentSession、事件转发、工具注册、会话恢复、上下文压缩和 CodeResult 提交。 |
| `src/protocol.ts` | AgentHost 客户端/服务端消息、CodeResult、Git 总览和会话模式定义。 |
| `src/tools.ts` | 文件列举、文件读取、文本搜索、补丁写入和非交互 PowerShell 执行。 |
| `src/git-service.ts` | Git 仓库发现、状态/Diff/历史读取，以及暂存、分支和本地提交。 |
| `src/security.ts` | 工作区路径规范化、目录穿越、`.git` 元数据和符号链接逃逸防护。 |
| `src/approval-policy.ts` | 安全命令和 Git 写工具的审批分类。 |
| `src/provider-config.ts` | OpenAI-compatible Base URL、Gemini 兼容端点和 Provider 错误脱敏。 |
| `src/*.test.ts` | 文件/命令安全、Git 边界、Provider 配置、协议和 Host 集成测试。 |

### 6.1 AgentHost 的两种模式

- `interactive`：接收 `send_message`，用于独立 Pi 工作台。
- `coding_worker`：只接收 `code_task`，要求每个任务最终调用一次 `submit_code_result`。

两种模式共用工具和安全边界，但 Manager 委派路径只使用 `coding_worker`。

## 7. 配置、会话和运行目录

| 数据 | 默认位置 | 内容 |
| --- | --- | --- |
| 全局设置 | `%LocalAppData%\\IlMatto\\settings.json` | Provider 默认值、模型、工作区、超时和自动批准开关。 |
| 独立 Pi 对话索引 | `%LocalAppData%\\IlMatto\\conversations.json` | Pi 工作台的历史消息、过程记录和关联会话文件。 |
| Pi Provider 会话 | `%LocalAppData%\\IlMatto\\sessions\\*.jsonl` | Pi 的持久会话记录。AgentHost 会校验路径和工作区匹配关系。 |
| Manager 对话索引 | `%LocalAppData%\\IlMatto\\manager-sessions\\conversations.json` | Manager 对话、完整 Provider 绑定快照和结构化 Coding 结果。 |
| API Manager 会话（旧兼容） | `%LocalAppData%\\IlMatto\\manager-sessions\\coordinator\\*.jsonl` | 仅供旧快照/兼容测试读取；统一 Manager 不写入。 |
| Antigravity SDK 会话 | `%LocalAppData%\\IlMatto\\manager-sessions\\antigravity-sdk\\<sessionRef>` | SDK 会话持久化目录；只保存 SDK 所需的会话状态。 |
| Manager 图片附件 | `%LocalAppData%\\IlMatto\\manager-runtime\\attachments\\<sessionId>` | 发送前复制的图片，供统一 Antigravity 会话读取；每条消息最多 8 张、单张 20 MiB、总计 64 MiB。 |
| Manager 运行时 | `%LocalAppData%\\IlMatto\\manager-runtime` | 临时附件目录和配置；统一路径不生成 Agent、Schema 或全局权限文件。 |
| 全局 MCP 配置 | `%USERPROFILE%\\.gemini\\config\\mcp_config.json` | Manager 会话临时写入 `ilmatto-codex-observation`，保留用户已有 Server；正常关闭后只清理自身且未被修改的条目。 |
| Manager 日志 | `%LocalAppData%\\IlMatto\\logs\\antigravity-manager-<workspace>.log` | Antigravity Manager 运行日志。 |
| API Key | Windows Credential Manager | 只在 JSON 中保存 Credential ID，不保存 API Key 本身。 |

## 8. 构建关系

1. `IlMatto.AgentHost` 和 `IlMatto.ManagerHost` 先由 TypeScript 编译到各自的 `dist/`。
2. WPF 项目把两个 Node Host 的 `dist/`、`package.json` 和 SDK Bridge 文件复制到桌面应用输出目录；正式版还应把锁定版本的 Python 运行时放入 `AntigravityBridge/python/`。
3. 桌面端运行时按需启动对应的 Node Host；ManagerHost 在 SDK 会话中再启动 Python Bridge。
4. 关闭窗口时，桌面端先发送关闭消息，再在超时后终止残留子进程。

## 9. 测试边界

- AgentHost：协议、路径安全、工具审批、Provider 配置、Git 服务和 Named Pipe 集成。
- ManagerHost：统一 Antigravity 文本流、图片暂存、兼容协议、工作区 MCP 挂载和 Named Pipe 集成；旧 API/Codex 适配代码不在新会话运行路径。
- Desktop：当前没有独立的测试项目；行为主要由 ViewModel、协议模型和 Host 集成覆盖。

## 10. 当前结构限制

- 当前只允许一个 Manager 活动会话和一个 Antigravity CLI 进程。
- 工作区是本地目录，系统没有 Git 远端同步、安装包或自动更新流程。
- IlMatto 不提供独立的全局 MCP 配置管理界面；Codex 观察 Facade 由 ManagerHost 在会话期间挂载到 Antigravity 全局 MCP 配置，失败时保持可选通道停用。旧工作区插件挂载仅作为兼容 fallback。
