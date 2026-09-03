# IlMatto

IlMatto 是一个运行在开发机上的双层 WPF Agent。Manager 会话现在只启动一个 Antigravity CLI 进程，同一上下文同时处理陪伴对话、项目分析、文件修改、命令、编译和测试；桌面端与 Host 通过本机 Windows Named Pipe 通信。独立 Pi 工作台仍作为单独入口保留。

## 文档

- [项目结构文档](docs/project-structure.md)
- [功能文档](docs/functional-specification.md)
- [陪伴型 RP 后续路线图](docs/companion-roadmap.md)
- [项目上下文与术语](CONTEXT.md)

## 环境

- Windows 10/11
- .NET 8 SDK（当前仓库也可使用更高版本 SDK）
- Node.js 22+
- PowerShell
- `rg`、`git` 在 PATH 中
- Python 3.11+；正式发布还需要将锁定版本的 Python 运行时和 `google-antigravity` SDK 放入 `AntigravityBridge/python/`
- Antigravity CLI `agy`（Manager 的唯一运行时；首次使用前需要在可见终端完成一次交互式登录）
- Codex CLI 是可选的观察/编码依赖；Manager 启动时不会启动或探测 Codex

## 构建

```powershell
cd C:\Users\33612\Documents\GitHub\IlMatto
dotnet run --project .\src\IlMatto.Desktop\IlMatto.Desktop.csproj
```

WPF 启动时会同时寻找 `ManagerHost/dist/index.js` 和按需使用的 `AgentHost/dist/index.js`。从源码运行时会回退到仓库中对应的 `src/*Host/dist/index.js`。Python `AntigravityBridge` 仅作为旧 SDK 兼容代码保留；统一 Manager 不需要安装 Python 依赖。

## 使用

1. 默认打开 Manager 界面。现有设置页仍可显示旧的 Provider 选项，但 ManagerHost 会将会话迁移为单一 Antigravity 上下文；不会启动 Pi/Codex Coding Worker。
2. 在“设置 → 陪伴设定”编辑角色设定、用户资料和关系摘要。所有消息都进入同一个 Antigravity 会话，由 Antigravity 自行判断是否需要工具；只有用户明确要求本地操作时才执行命令或修改文件。
3. Manager 使用 `--mode accept-edits` 与 `--dangerously-skip-permissions`。它可以执行命令、修改或删除文件、访问网络以及使用本机配置的 MCP/插件；请把工作区视为可被自动操作的目录。Host 只转发文本、进度、工具状态和错误。
4. 通过“视图 → Pi Coding 工作台”可按需打开原有 Pi 界面；关闭它不会影响 Manager 会话。

如需让 Antigravity 了解用户确认后由 Codex 完成的任务，ManagerHost 会在 Antigravity 用户级全局配置 `%USERPROFILE%\.gemini\config\mcp_config.json` 中临时写入 `ilmatto-codex-observation`（保留原有 MCP Server）。该条目指向当前 ManagerHost 的本地管道，会话正常结束时只删除自己仍未被修改的条目；Codex 不存在或配置不可写时，观察通道自动停用，Antigravity 仍可正常运行。旧版本支持的工作区插件挂载仍可通过内部兼容选项使用。Facade 只暴露 `draft_codex_task`、`get_codex_status`、`get_latest_codex_report`、`get_codex_report` 和 `get_codex_diff`；生成草稿后必须在现有 Manager 审批卡中编辑/确认，ManagerHost 才会启动 Codex。Facade 不暴露提交、继续、steer 或中断接口，也不会把 Codex 实时输出发送给 Antigravity。

MCP Facade 的手动入口（主要用于诊断；正常 Manager 会话会自动挂载）：

```powershell
cd C:\Users\33612\Documents\GitHub\IlMatto\src\IlMatto.ManagerHost
npm.cmd run build
npm.cmd run mcp -- --pipe <ManagerHost管道名> --session-id <Manager会话ID>
```

原 Pi 工作台的使用方式保持不变：

1. 从顶部“设置”或左侧“⚙ 设置”打开配置窗口，在 LLM 页设置 Base URL、模型名和 API Key，在工作区页选择项目目录。
2. Base URL、模型名和工作区会保存到 `%LocalAppData%\IlMatto\settings.json`；API Key 写入当前 Windows 用户的 Credential Manager。
3. 输入编码任务并发送。
4. 只读操作会自动执行；文件修改和 PowerShell 命令会在操作记录中等待“批准一次”或“拒绝”。
5. 输入 `/` 会显示当前可用的 Pi 指令并支持点击补全；例如 `/help`、`/compact`、`/new`、`/settings`、`/session`。
6. 左侧历史列表支持切换和删除会话；右侧“思路与工具”面板可展开/收起，显示思路增量、工具审批、执行状态和截断输出。
7. Agent 回复支持常见 Markdown（标题、列表、代码块、粗体、斜体、链接、引用）以及常见 LaTeX 数学表达式（`$...$`、`$$...$$`、`\(...\)`、`\[...\]`）。右侧每条过程记录都可单独展开/折叠，折叠时只保留一行摘要。

Manager 会话保存在 `%LocalAppData%\IlMatto\manager-sessions`。旧会话仍保留聊天记录，但 Provider/Coding Worker 绑定会迁移为统一 Antigravity 配置，不复用旧的自定义 Agent 会话 ID。图片会复制到 Manager 受管附件目录后随原始请求提供给 Antigravity；运行目录只保存日志和临时附件，不生成 Agent、Schema 或全局权限文件。可选 Codex 观察数据保存在 `%LocalAppData%\IlMatto\codex-observation`，原始事件只在本地私有日志中保存，Antigravity 只能按需读取摘要报告。Host 不写入 Antigravity 的全局 Agent 或权限配置，但会在会话期间维护一个受控的全局 MCP Server 条目。

当前版本是单工作区、单活动会话的 MVP，不包含安装包、自动更新、Git 远端同步、并行编码/验证任务或进程级 Codex 隔离。Codex 观察通道为可选能力，默认不启动。

## Git

- 工作区可以是 Git 仓库根目录或其中的子目录。IlMatto 会自动发现上级 Git 仓库；右侧 Git 页签仅显示当前工作区范围内的本地改动和 Diff，同时显示仓库级分支、提交历史和远端配置；不会发起网络请求。
- 如果当前工作区既不是 Git 仓库、也不在上级仓库内，Agent 会直接执行 `git init`，并将当前工作区文件暂存后创建本地 `Initial commit`；空目录会跳过空提交，也不会访问网络。其他 Git 写操作仍遵循审批或自动执行本地 Git 设置。
- Agent 可使用受控 Git 工具查看、暂存/取消暂存和创建本地提交；提交只包含当前工作区内已暂存的内容，且使用 `--no-verify`，不会运行 Git hooks。创建或切换本地分支是仓库级操作，因此仅在工作区正好为仓库根目录时可用。
- 默认每次 Git 写操作都会请求批准。可在“设置 → 安全”启用“自动执行本地 Git 操作”；该选项默认关闭，不会开放 reset、restore、clean、stash、merge、rebase、fetch、pull 或 push。

## 测试

```powershell
cd C:\Users\33612\Documents\GitHub\IlMatto\src\IlMatto.AgentHost
npm.cmd run build
npm.cmd test

cd C:\Users\33612\Documents\GitHub\IlMatto\src\IlMatto.ManagerHost
npm.cmd run build
npm.cmd test
```
