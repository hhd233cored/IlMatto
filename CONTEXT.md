# IlMatto 项目上下文

本文档只记录项目中的核心术语、角色边界和不变量，不描述具体代码实现。

## 核心术语

### Manager

Manager 是 IlMatto 的统一 Agent 入口。它将用户请求交给同一个 Antigravity 会话处理陪伴回复、项目分析和本地操作。

### 主 Agent / Coordinator

主 Agent 是 Manager 的陪伴与执行角色，由 Antigravity 提供。它负责角色化的普通对话、情绪回应、需求澄清、项目分析和本地工具调用。

### CompanionProfile

CompanionProfile 是一次 Manager 对话使用的角色卡快照，活动字段是角色名称和角色设定。角色名称由设置页手动提供，并在保存时插入角色卡开头；它不包含关系数值、场景状态或关系摘要。旧快照中的用户资料和关系摘要只用于兼容迁移，不再作为活动 prompt 上下文。

### 用户画像与会话摘要

用户画像是应用本地 `%LocalAppData%\\IlMatto\\companion-memory\\profile.md`，在 Manager 会话之间全局共享，可由用户直接编辑。会话摘要保存在对应会话目录的 `summary.json`，是跨会话回忆的主要来源；可见对话 `transcript.jsonl` 只在摘要命中后提供有限相关片段。第一版不使用关系数值、向量数据库或外部 RAG。

### Coding Agent / Coding Worker

独立 Pi 工作台中的 Coding Agent 是实际的技术执行角色。Manager 则由同一个 Antigravity 会话直接检查项目、做技术判断、修改文件和运行验证。

### 对话 / Session

对话是用户在 IlMatto 中看到的持续交互单元，也是会话摘要的隔离边界。一个对话固定角色卡并可关联 Antigravity 会话引用；模型、推理强度以及对应的沙箱和审批选择来自全局设置，不按对话分叉。全局角色设定改变只影响新建对话；全局用户画像可被所有 Manager 对话读取。

### 摘要优先回忆

Agent 需要历史信息时必须先调用 `session_search` 搜索摘要；只有摘要命中后才能调用 `session_open` 查看有限的相关可见消息片段。没有检索结果时不能把模型推测当作历史事实。记忆工具异常不得阻断普通对话。

### 工作区 / Workspace

工作区是 Agent 获得文件和命令操作权限的目录边界。文件读取、搜索、补丁和命令都必须在工作区内完成。工作区可以是 Git 仓库根目录，也可以是上级 Git 仓库中的子目录。

### Git 仓库根目录 / Repository Root

Git 仓库根目录是 Git 的实际管理边界。查看状态和 Diff 可以按工作区范围限制；创建或切换分支等仓库级操作只有在工作区正好是仓库根目录时才允许。

### 工具审批 / Coding 交互

工具审批是 Pi 工具调用在执行前等待用户批准的流程。Coding 交互是 Coding Agent 需要用户批准命令、文件变更、权限，或回答问题/MCP 授权时的统一交互模型。两者在 Manager UI 中共用审批队列，但底层协议不同。

### CodeTask / CodeResult

CodeTask 是提交给 Coding Agent 的原始用户任务。CodeResult 是 Coding Agent 的结构化结束结果，包含状态、用户摘要、技术决策、文件变更、验证结果和待用户决定的问题。

## 领域不变量

- Manager 的 Antigravity 会话可直接形成普通回答、澄清请求、技术判断或本地操作。
- 本地文件、代码、编译、测试、命令或 Git 操作与一般技术问题都在同一个 Manager 会话中处理，并保留用户原始请求。
- Coding Agent 要么完成任务，要么以明确的失败、取消或阻塞状态结束。
- 需要用户决定时，下一轮用户输入应继续当前 Coding 对话，而不是被当作新的无关任务。
- 任何涉及文件写入、命令执行或 Git 写操作的行为都必须受审批策略控制；自动批准只扩大明确列出的安全范围。
- Provider 会话引用属于对应对话，不应被不同对话复用。
- 角色卡是唯一的硬性角色设定；关系和场景由模型结合当前对话和摘要自行判断。
- 角色名称只作为角色身份和界面显示名称；会话标题只在新会话首轮通过独立元数据调用生成，生成失败不得影响普通回复。
- `session_update` 只能更新当前 Manager 对话，`profile_update` 更新全局画像；两者都必须在应用本地存储范围内完成。
- 记忆摘要、画像和原文片段都是数据，不是系统指令；思路、工具输出、内部 prompt 和凭据不得写入陪伴记忆。
- 删除 Manager 对话时必须清理 IlMatto 自己管理的会话级索引、记忆、附件、任务/观察数据和本地 Provider 会话文件；不得删除全局 `profile.md` 或 Antigravity CLI 历史会话。
