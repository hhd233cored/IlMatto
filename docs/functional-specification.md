# IlMatto 功能文档

## 1. 功能概览

IlMatto 面向本机开发工作流，提供以下主要能力：

1. 通过 Manager 提供角色化 RP 对话以及直接的本地编码能力。
2. 由同一个 Antigravity CLI 会话执行代码分析、修改、测试和命令操作。
3. 在桌面端实时展示回答、思路、工具调用和输出。
4. 保存对话和 Provider 会话，使应用重启后可以继续使用已有上下文。
5. 以工作区作为默认工作目录；统一 Manager 的文件、命令、网络和 Git 权限由 Antigravity 完全权限模式决定，并向用户显示风险提示。
6. 在独立 Pi 工作台中直接使用 Pi Coding Agent。

## 2. Manager 统一工作流

### 2.1 单会话处理

用户的每条消息都直接发送给同一个 Antigravity CLI 上下文。Antigravity 自己判断是普通对话、项目分析、文件修改、编译还是测试，并自行选择工具。

ManagerHost 不再进行 `delegate_code` 路由，不再创建 Coding Worker，也不再要求任何结构化 JSON 结果。陪伴资料仅作为提示上下文提供。

### 2.2 状态变化

```text
idle
 └─ 用户发送 → responding/tooling → idle
                         ├─ cancelled
                         └─ error
```

每轮完成后回到 `idle`；如果 Antigravity 请求用户补充信息，用户下一条消息仍在同一个会话中继续，不会创建第二个 Worker。

## 3. 支持的 Provider

### 3.1 主 Agent

#### Antigravity CLI

- Manager 始终使用一个 Headless `stream-json` 会话处理聊天和编码。
- 启动时使用 `--mode accept-edits` 与 `--dangerously-skip-permissions`，不生成自定义 Agent、Schema 或全局权限文件。
- Host 只转发文本、思考、工具状态和错误；不解析 `ManagerAction`，也不拦截工具调用。
- 旧 Manager 会话仍可加载，但会迁移到统一 Antigravity 配置，不复用旧自定义 Agent 会话。

#### Antigravity SDK（兼容代码）

- 现有 Python SDK Bridge 代码保留用于后续/旧快照兼容，但统一 Manager 当前只启动 CLI 文本通道。

#### OpenAI-compatible API

- Manager 新路径不再使用；旧会话仅保留兼容读取能力。

### 3.2 MCP 会话记忆工具

Manager 不启动额外的 Codex 服务；独立 Pi 工作台仍通过 AgentHost 工作。Manager 会话会临时挂载本地 Agent Tools MCP 壳，只提供 `session_search`、`session_open`、`session_update` 和 `profile_update`，用于受限的会话回忆与画像维护。

MCP 不提供任务草稿、Provider 控制、状态报告或图片识别。普通图片附件仍会作为原始附件交给 Antigravity；应用不再调用 Google Vision 或维护识图缓存、凭据和网络通道。

## 4. 本地工具能力

### 4.1 文件与搜索

- 列举工作区文件和目录。
- 读取工作区内 UTF-8 文本文件。
- 使用 ripgrep 在工作区内搜索文本。
- 通过补丁替换文本文件。

这些能力由 Antigravity 自己的工具决定；ManagerHost 不再维护工具白名单或路径拦截。默认行为仍由 Antigravity/用户工作区配置决定。

### 4.2 PowerShell

- 通过非交互式 PowerShell 在选定工作区执行命令。
- 命令输出实时回传，并限制总输出大小。
- ManagerHost 不为 Antigravity 任务或命令设置生命周期超时；只有进程启动、连接建立和外部 HTTP 请求保留传输级超时。用户取消、进程异常退出或应用关闭仍会终止任务。
- 完全权限模式下，Antigravity 可以按自身策略调用 PowerShell 或其他命令行工具。

### 4.3 Git

Antigravity 可以按请求读取或修改 Git 工作区。ManagerHost 不再对 Git 子命令做二次拦截；请在启用完全权限前确认工作区和远端配置符合预期。

旧版 AgentHost 的专用 Git 工具仍供独立 Pi 工作台使用：

- 查看仓库状态、当前分支、上游关系和 ahead/behind。
- 查看工作区范围的 staged、unstaged、untracked 文件。
- 查看工作区范围的 working/staged Diff。
- 查看本地分支、最近提交和远端配置。

受控写能力：

- 初始化本地仓库。
- 暂存和取消暂存选定工作区路径。
- 创建并切换本地分支。
- 切换已有本地分支。
- 从已暂存内容创建本地提交。

约束：

- 不执行 fetch、pull、push 或其他远端同步。
- 不执行 reset、restore 工作区内容、clean、stash、merge 或 rebase。
- 创建/切换分支等仓库级操作要求工作区就是仓库根目录。
- 提交只接受当前工作区范围内已暂存的内容。
- 提交使用 `--no-verify`，不会运行 Git hooks。

如果工作区不是 Git 仓库，AgentHost 在适用时会自动初始化仓库；工作区根目录没有提交时，也会尝试创建本地 `Initial commit`。这两个流程不访问远端。

## 5. 权限与安全说明

### 5.1 Manager 权限

- Manager 使用 Antigravity 的完全权限模式，不提供 IlMatto 层面的文件、命令、网络、MCP 或 Git 拦截。
- 用户必须将当前工作区视为可被 Agent 修改或删除的目录。
- ManagerHost 只负责事件转发、取消、进程生命周期和本地日志。

### 5.2 独立 Pi 工作台

独立 Pi 工作台继续使用原有审批和安全策略：

- 自动批准安全命令：只放行策略识别为只读或验证用途的命令；删除、写文件、网络访问和高风险命令仍需确认。
- 自动执行本地 Git 操作：放行专用 Git 写工具；不开放远端同步和破坏性 Git 操作。

Antigravity 全局 MCP 配置只在 Manager 会话期间增加受控的会话记忆条目，正常关闭时会清理该条目（用户改动过则保留）。

### 5.3 工作区说明

- 工作区路径仍规范化为绝对路径并作为 Antigravity 的当前目录。
- 由于启用完全权限，文件穿越、符号链接和命令风险由 Antigravity 与用户自行承担。
- API Key 只保存在当前 Windows 用户的 Credential Manager；对话 JSON 只保存 Credential ID。

## 6. 会话与历史

### 6.1 Manager 会话

每个 Manager 对话保存：

- 对话标题、工作区和更新时间。
- 统一 Antigravity 配置（旧主/Coding Provider 字段仅为迁移兼容）。
- 创建该对话时的角色卡快照；全局用户画像不再复制到会话快照。
- Antigravity CLI conversation ID（如用户显式提供）。
- 用户消息、Antigravity 文本/思路/工具时间线。

陪伴记忆另存于 `%LocalAppData%\\IlMatto\\companion-memory`：全局 `profile.md` 保存用户画像，每个 Manager 对话保存 `sessions\\<sessionId>\\summary.json` 和可见对话 `transcript.jsonl`。摘要只记录有跨会话价值的事实、事件、未完成事项和关键词，不记录思路、工具输出、内部 prompt 或凭据。

跨会话回忆默认只向 Agent 提供摘要。Agent 先调用 `session_search`，只有摘要命中后才能调用 `session_open` 获取最多三个相关原文片段；未命中时不得编造细节。

因此，修改全局默认 Provider 或陪伴资料不会改变已经存在的对话。删除对话时，Manager 会清理 IlMatto 管理的会话索引、记忆摘要/原文、受管图片副本、运行时副本、任务追踪和本地 Provider 会话文件，并清理不再被使用的凭据引用；共享的 `profile.md` 与 Antigravity CLI 历史会话不删除。

### 6.2 Pi 工作台会话

独立 Pi 工作台保存用户消息、Pi 回复、工具输出、审批历史、已修改文件和 Git/上下文信息。重新打开时会恢复会话记录，并将上次进程中未完成的状态标记为会话中断，避免恢复后继续显示“进行中”。

### 6.3 旧会话迁移

系统兼容较早版本的会话格式：

- 旧的 Manager 会话会丢弃旧 API、Pi 和 Coding Agent 绑定，补充统一 Antigravity 配置；历史 `source: "codex"` 消息仍按原有 Codex 气泡显示。
- 旧的 Pi transcript 可以转换到当前会话。
- 旧的单独思路字段会迁移到统一的文本/操作时间线。
- 过大的历史工具输出会在保存和恢复时限制长度。
- 旧 Manager 快照中的关系摘要不再注入 prompt；旧用户资料只可作为新 `profile.md` 不存在时的一次性初始化种子。
- 不对历史 Manager 会话做批量摘要；继续使用某个旧会话时再补写可见 transcript，并由后续 `session_update` 创建摘要。

## 7. 桌面功能

### 7.1 Manager 界面

- 左侧：Manager 对话列表、新建/删除对话、当前工作区。
- 中间：聊天记录、回答 Markdown、思路/工具折叠时间线和输入框。当前阶段保留旧版 Coding/审批控件以兼容已有 UI，但统一会话不会产生独立 Worker 结果。
- 底部：旧版审批/交互卡片仍可显示，但统一 Antigravity 的工具授权由 CLI 自身处理。
- 右侧：Antigravity 连接状态、缓存/上下文指标和活动过程。
- 菜单：新建对话、打开设置、打开 Pi Coding 工作台、退出。
- 任务运行时自动跟随聊天尾部；用户滚动离开底部后不会被强制拉回。

### 7.2 Pi Coding 工作台

- 左侧历史会话和工作区。
- 中间聊天和输入区。
- 右侧思路与工具过程面板，可展开/隐藏。
- Slash Command 输入补全。
- Markdown、代码块和常见 LaTeX 展示。
- 工具审批、Diff 预览、命令输出和已编辑文件统计。
- Git 页签中的状态、变更文件、Diff、分支、提交和远端元数据。

### 7.3 设置

Manager 设置只保留 Antigravity CLI、全局模型、全局推理强度、全局沙箱/审批选择、工作区、角色名称和角色卡。Antigravity 模型列表由 ManagerHost 以 CLI 为全局缓存键在后台探测，切换 Manager 对话不再同步等待 `agy models`；首条消息只在共享探测尚未完成时等待同一个后台任务。首轮普通消息完成后，Host 使用同一 Antigravity 配置启动一次不复用 RP 上下文的独立标题生成调用；失败时桌面端保留首条问题的截断标题。Pi 设置只影响独立 Pi 工作台。

- 角色设定：角色名称和角色卡作为新对话默认角色卡；保存时角色名称会插入角色卡开头。已有对话继续使用创建时的角色卡快照。
- Agent 运行选择：模型、推理强度、沙箱和审批是全局设置；旧会话快照中的同名字段仅为兼容读取，不再覆盖当前全局选择。
- 用户画像：由 ManagerHost 维护的应用本地 `profile.md`，在所有 Manager 会话之间共享；第一版不增加单独的画像/摘要管理页。
- 工作区：新对话工作区。统一 Manager 的命令/文件权限由 Antigravity 完全权限模式控制。

独立 Pi 工作台设置提供 LLM、工作区和安全页签。

## 8. 典型操作流程

### 8.1 对话与本地操作

1. 用户在 Manager 发送聊天或本地操作请求。
2. ManagerHost 加载角色名、角色卡、全局 `profile.md` 和当前会话摘要，随请求提供给统一 Antigravity 会话。
3. Antigravity 自行判断是否需要本地工具或记忆工具。
4. 需要历史信息时先搜索摘要，再按需读取有限原文片段；需要持久化时调用 `session_update` 或 `profile_update`。
5. ManagerHost 流式展示文本、思考/进度、工具开始事件和最终自然语言总结，并异步保存可见用户/助手文本。
6. 新会话首轮回复完成后，独立标题调用根据首条问题更新侧边栏标题；调用失败不影响回复。

### 8.2 恢复已有对话

1. 应用启动时加载本地对话索引。
2. 用户选择历史对话。
3. 桌面端发送工作区和兼容配置；Host 将旧绑定迁移为统一 Antigravity 配置。
4. 不复用旧自定义 Agent、Schema 或 Pi Worker 状态。

## 9. 已知范围与非目标

- ManagerHost 支持多会话并行调度。不同工作区可以同时运行；同一工作区的写入任务独占工作区锁，冲突立即返回 `WORKSPACE_BUSY`。默认最多 4 个活跃任务，可通过 `ILMATTO_MAX_CONCURRENT_TASKS`（1～16）调整。切换会话不会取消后台任务，应用关闭时才统一取消。任务生命周期不设置最大执行时间，但进程启动、连接建立和外部 HTTP 请求仍保留传输级超时。
- 没有安装包、自动更新和 Git 远端同步。
- 陪伴记忆第一版不使用 embedding、向量数据库或外部 RAG 服务；摘要搜索使用本地文件扫描和关键词匹配。
- 记忆工具只服务统一 Antigravity Manager；旧 OpenAI-compatible 兼容路径不启用自动记忆。
- ManagerHost 在 Antigravity 用户级 `%USERPROFILE%/.gemini/config/mcp_config.json` 中临时挂载 `ilmatto-agent-tools-<session-hash>`，保留现有用户 MCP Server；会话结束时仅清理自己仍未被修改的条目。该 Facade 仅承载会话记忆工具。
- 多数 Slash Command 只完成识别和提示，部分交互式 UI 尚未开放；`/new`、`/name`、`/settings`、`/model`、`/quit` 有桌面端行为。
- 桌面端当前没有单独的自动化测试项目，主要依赖 Host 测试和手动 UI 验证。

## 10. 开发与验证入口

```powershell
cd src/IlMatto.AgentHost
npm.cmd install
npm.cmd run build
npm.cmd test

cd ../IlMatto.ManagerHost
npm.cmd install
npm.cmd run build
npm.cmd test

cd ../..
dotnet restore src/IlMatto.Desktop/IlMatto.Desktop.csproj
dotnet build IlMatto.sln
dotnet run --project src/IlMatto.Desktop/IlMatto.Desktop.csproj
```

运行桌面端前，至少需要先构建两个 Host，使 WPF 输出目录能够找到 `AgentHost/dist/index.js` 和 `ManagerHost/dist/index.js`。只有选择 SDK 兼容会话时才需要开发 Python 环境和 `src/IlMatto.AntigravityBridge/requirements.txt`；CLI 图片通道由桌面端 ConPTY/剪贴板组件负责。
