# Antigravity Web Search 与权限核查

核查日期：2026-09-01

## 结论

Antigravity CLI 的工具目录包含 `search_web`（Google Search）和 `read_url_content`（读取公开 URL 内容）。官方 CLI 文档的 `stream-json` 事件也会暴露工具步骤，因此理论上可以在 ManagerHost 中观测到这类工具调用。

但当前 IlMatto 的活动配置没有开放它们：

- 生成的 Manager Agent 只声明 `view_file`。
- ManagerHost 的运行时策略只允许受管图片路径的 `view_file`/`read_file`/`readfile`。
- 隔离设置使用 `toolPermission: "strict"`、`allowNonWorkspaceAccess: false`，且 `permissions.allow` 没有 URL 或浏览器规则。
- `companion.ts` 明确禁止浏览器、MCP、子 Agent 和其他工具。
- `codex-worker.ts` 中的 `webSearch` 只是 Codex 结果卡片映射，不是 Antigravity 的工具注册。

因此，当前日志中出现“联网搜索”或类似表述，不能单独证明发生了实际搜索。实际工具调用应表现为 `stream-json` 的 `step_update`，其中 `step_type` 为 `tool` 且带有对应 `tool_name`；本次检查的活动源码和现有日志没有找到 `search_web`、`read_url_content` 或浏览器工具调用。日志中的浏览器初始化/Playwright 依赖错误也不等于模型执行了网页搜索。

## 官方能力与权限边界

官方工具文档列出 `search_web` 和 `read_url_content`，并说明 SDK 中的内置 Web 工具默认启用；CLI 的工具和权限仍受其 Agent 配置及权限策略约束。官方权限文档将网页读取权限建模为 `read_url(domain)`，浏览器交互则另外使用 `execute_url(domain)`。

Headless 模式没有交互式授权提示。需要授权的工具如果没有预先配置，会被 soft-deny；全局 `--dangerously-skip-permissions` 会批准全部工具，不适合 IlMatto 的陪伴 Agent。

## 是否应该开放

技术上可以开放，但不建议直接使用全局通配规则。对陪伴 Agent 开放 Web Search 会产生外部网络访问、把用户问题或图片识别上下文发送到搜索服务、以及把搜索结果混入 RP 回复的额外边界。

若后续确认需求，建议单独增加“允许联网搜索”的显式设置，并同时修改四处：Agent 工具声明、隔离权限规则、ManagerHost 工具事件白名单/审计、陪伴提示词与测试。默认仍关闭；`read_url` 也应单独决定是否开放，不能因为允许搜索而自动开放浏览器交互或命令执行。

## 官方参考

- [Antigravity CLI Headless mode](https://www.agy.dev/docs/cli/headless/)
- [Antigravity Permissions](https://www.agy.dev/docs/permissions/)
- [Antigravity Tools & skills](https://www.agy.dev/docs/sdk/tools/)
- [Antigravity Hooks（工具名称参考）](https://www.agy.dev/docs/hooks/)
