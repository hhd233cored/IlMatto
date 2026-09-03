# IlMatto 项目上下文

本文档只记录项目中的核心术语、角色边界和不变量，不描述具体代码实现。

## 核心术语

### Manager

Manager 是 IlMatto 的双层 Agent 入口。它接收用户请求，在陪伴回复与 Coding Agent 委派之间进行路由。Manager 本身不负责读取或修改项目代码。

### 主 Agent / Coordinator

主 Agent 是 Manager 的陪伴与协调角色，可由 Antigravity 或 OpenAI-compatible API 提供。它负责角色化的普通对话、情绪回应、需求澄清和任务路由；它不应访问工作区、生成实现方案或执行工具。

### CompanionProfile

CompanionProfile 是一次 Manager 对话使用的陪伴上下文，由角色设定、用户资料和关系摘要组成。它是手动维护的上下文，不包含自动提取的记忆、关系数值或主动行为规则。全局默认资料只影响新建对话；对话创建后固定自己的资料快照。

### Coding Agent / Coding Worker

Coding Agent 是实际的技术执行角色，可由 Pi 或 Codex 提供。它负责检查项目、做技术判断、修改文件、运行验证以及执行受控的本地 Git 操作。

### 对话 / Session

对话是用户在 IlMatto 中看到的持续交互单元。一个对话固定一套主 Agent 和 Coding Agent 配置，并可关联各 Provider 自己的会话引用。全局设置的改变只影响新建对话。

### 工作区 / Workspace

工作区是 Agent 获得文件和命令操作权限的目录边界。文件读取、搜索、补丁和命令都必须在工作区内完成。工作区可以是 Git 仓库根目录，也可以是上级 Git 仓库中的子目录。

### Git 仓库根目录 / Repository Root

Git 仓库根目录是 Git 的实际管理边界。查看状态和 Diff 可以按工作区范围限制；创建或切换分支等仓库级操作只有在工作区正好是仓库根目录时才允许。

### 工具审批 / Coding 交互

工具审批是 Pi 工具调用在执行前等待用户批准的流程。Coding 交互是 Coding Agent 需要用户批准命令、文件变更、权限，或回答问题/MCP 授权时的统一交互模型。两者在 Manager UI 中共用审批队列，但底层协议不同。

### CodeTask / CodeResult

CodeTask 是提交给 Coding Agent 的原始用户任务。CodeResult 是 Coding Agent 的结构化结束结果，包含状态、用户摘要、技术决策、文件变更、验证结果和待用户决定的问题。

## 领域不变量

- 主 Agent 的输出只能形成普通回答、澄清请求或路由动作，不能替代 Coding Agent 的技术判断。
- 明确的本地文件、代码、编译、测试、命令或 Git 操作可以在进入主 Agent 前直接委派给 Coding Agent；一般技术知识问题仍可由主 Agent 回答。
- 委派给 Coding Agent 时，必须保留用户原始请求；协调层生成的技术文字不能改写任务输入。
- Coding Agent 要么完成任务，要么以明确的失败、取消或阻塞状态结束。
- 需要用户决定时，下一轮用户输入应继续当前 Coding 对话，而不是被当作新的无关任务。
- 任何涉及文件写入、命令执行或 Git 写操作的行为都必须受审批策略控制；自动批准只扩大明确列出的安全范围。
- Provider 会话引用属于对应对话，不应被不同对话复用。
