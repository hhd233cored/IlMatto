# IlMatto：向 Antigravity CLI 与 Codex CLI 传入图片等内容的调研

> 调研日期：2026-09-01  
> 范围：IlMatto 当前的 ManagerHost/桌面端调用方式，以及 Antigravity CLI、Antigravity SDK、Codex CLI 和 Codex App Server 的公开输入接口。  
> 结论仅针对本机和官方文档所对应的版本；CLI 与服务端可能随版本逐步 rollout。

## 1. 结论摘要

当前实现中，桌面端会先把图片复制到 IlMatto 的会话附件目录，再按路由传给 Codex App Server 的 `localImage` 或 Antigravity CLI 的交互式剪贴板通道。新建 Antigravity 会话默认使用 CLI；旧 CLI 会话无图片时继续使用 Headless CLI，首次发送图片时切换为 `cli_interactive`，不会迁移到 SDK。

两套底层接口的能力不对称：

| 入口 | 图片输入 | 其他媒体/文件 | IlMatto 当前能否直接使用 |
| --- | --- | --- | --- |
| Antigravity 交互式 TUI | 支持从剪贴板粘贴图片 | 官方文档还列出视频；SDK 另支持音频、视频、文档 | 是。IlMatto 使用隐藏 Windows ConPTY 和临时剪贴板 |
| Antigravity CLI headless `stream-json` | 官方协议未定义图片输入 | 只定义字符串或 `text` block | 否，不能把路径、URL 或 Base64 当作图片附件 |
| Antigravity SDK | 支持图片、PDF 等多模态附件 | 可扩展到音频、视频、文档 | 作为兼容会话保留 |
| Codex CLI | `-i`/`--image` 支持一个或多个图片文件 | 当前公开 CLI 参数明确的是图片 | 当前未直接使用该 CLI 入口 |
| Codex App Server | `image` URL、`localImage` 本地路径 | `turn/start` 的公开输入示例还包括文本 | 已在本次改动中接入 `localImage` |

因此，v1 建议把“图片附件”作为 Manager 协议中的独立输入项，而不是拼接到用户文本中：

```ts
type ManagerInputPart =
  | { type: "text"; text: string }
  | { type: "image"; path: string; mimeType?: string; displayName?: string };
```

路由后按 Provider 能力转换：

- Codex App Server：转换为 `{ type: "localImage", path: absolutePath }`。
- Antigravity headless CLI：当前只接受文本，不能发送图片 part；IlMatto 对带图普通聊天切换到交互式 TUI，而不是把路径、URL 或 Base64 拼进文本。
- 不建议把图片路径、URL 或 Base64 塞进文本，除非只是让 Agent 知道一个路径，而不是让它获得图片内容。

## 2. Antigravity CLI

### 2.1 交互式 TUI 支持媒体粘贴

Antigravity 的 Prompting & Interaction 文档说明，交互式 prompt panel 可以使用 `Ctrl+V`（或终端的原生粘贴）附加剪贴板中的富媒体。文档列出的图片类型包括 PNG、JPEG、GIF、WebP、BMP、TIFF 和 SVG，并同时列出了视频类型。

这条路径的概念流程是：

```text
用户复制图片
  -> 交互式 agy prompt panel Ctrl+V
  -> TUI 将媒体作为当前消息附件提交
  -> Agent 读取视觉上下文
```

官方最佳实践也明确建议：遇到 UI 截图、渲染问题或布局问题时，可以复制截图或视频并在 prompt box 中按 `Ctrl+V`，Agent 会使用媒体进行诊断。

参考：

- [Prompting & Interaction：Attaching media](https://antigravity.google/docs/cli-prompting)
- [Best Practices：Attaching visual evidence](https://antigravity.google/docs/cli/best-practices/)

### 2.2 IlMatto 使用的是 headless `stream-json`

IlMatto 当前在 [antigravity.ts](../src/IlMatto.ManagerHost/src/antigravity.ts) 中启动：

```text
agy --input-format stream-json --output-format stream-json
```

发送的用户事件目前是：

```json
{
  "event": "user",
  "message": {
    "content": "用户文本"
  }
}
```

Antigravity headless 文档定义的输入形式是：

1. 每行一个 NDJSON 用户事件。
2. `message.content` 可以是字符串。
3. `message.content` 也可以是文本块数组，例如：

   ```json
   {
     "event": "user",
     "message": {
       "content": [
         { "type": "text", "text": "请分析这段内容" }
       ]
     }
   }
   ```

4. 官方明确规定 `text` 是唯一支持的 block 类型；提交其他 block 类型会返回错误并结束当前 session。

所以以下写法都不能视为图片输入：

```json
{ "type": "image", "path": "C:\\tmp\\screen.png" }
{ "type": "image_url", "url": "https://example.com/screen.png" }
{ "type": "inline_data", "data": "...base64..." }
```

这些字段不是当前 headless `stream-json` 公开协议的一部分。把路径、URL 或 Base64 放入普通字符串，只会让模型看到文字，不会自动产生视觉输入。

参考：[Antigravity CLI Headless mode](https://www.agy.dev/docs/cli/headless/)，其中的 [Stream prompts from stdin](https://www.agy.dev/docs/cli/headless/#stream-prompts-from-stdin) 小节定义了上述协议。

### 2.3 本机 CLI 检查

本机只读检查结果：

```text
agy --version
1.1.22
```

本机 `agy --help` 的输入相关参数只有：

```text
--input-format   text, stream-json
--output-format  text, json, stream-json
```

没有 `--image` 参数，也没有公开的 headless 图片文件参数。这个结果与 headless 文档的 text-only content block 约束一致。

需要区分：交互式 TUI 的媒体粘贴能力和 headless `stream-json` 的机器输入协议不是同一条接口。不能因为 TUI 支持粘贴图片，就推断 IlMatto 当前写入 stdin 的 JSON 也能发送图片。

### 2.4 Antigravity SDK 是兼容路径

Antigravity SDK 文档明确展示了通过 `Image.from_file()` 以及 `from_file()` 将图片和 PDF 放入一次 `agent.chat()` 的多模态 prompt：

```python
from google.antigravity import Agent, LocalAgentConfig
from google.antigravity.types import Image, from_file

chart_image = Image.from_file("chart.png")
pdf_spec = from_file("spec.pdf")
prompt = ["Analyze this chart against the specification:", chart_image, pdf_spec]
```

IlMatto 保留长生命周期 Python Bridge 作为 SDK 兼容路径。Bridge 将图片作为 SDK 的真实 prompt part 传入，返回流式增量并校验结构化 `ManagerAction`；当前默认 CLI 路径负责纯文本会话，带图片的普通聊天改走隐藏交互式 CLI。

参考：[Antigravity SDK：Structured output / Multimodal attachments](https://www.agy.dev/docs/sdk/structured-output/)。

## 3. Codex CLI 与 App Server

### 3.1 Codex CLI 的图片参数

官方 Codex 文档给出的 CLI 形式是：

```text
codex -i screenshot.png "Explain this error and suggest the smallest fix"
codex --image before.png,after.png "Compare these states and list the regressions"
```

多个图片可以使用逗号分隔，也可以重复 `--image`；文档明确举例支持 PNG 和 JPEG。

本机 `codex --help` 同样显示：

```text
-i, --image <FILE>...   Optional image(s) to attach to the initial prompt
```

本机版本为：

```text
codex-cli 0.151.0-alpha.7.2
```

这条 CLI 入口适合一次性启动命令行任务，但 IlMatto 当前的 Codex 集成不是拼接命令行参数，而是启动 `codex app-server` 后通过 JSON-RPC 驱动会话，因此更适合使用 App Server 的输入模型。

参考：[Codex Image inputs](https://learn.chatgpt.com/docs/image-inputs)。

### 3.2 Codex App Server 的图片输入

官方 App Server 文档定义 `turn/start.input` 是一个输入 item 数组，其中包括：

```json
{ "type": "text", "text": "Explain this diff" }
{ "type": "image", "url": "https://.../design.png" }
{ "type": "localImage", "path": "/tmp/screenshot.png" }
```

对 Windows 的 IlMatto，建议使用规范化后的绝对本地路径：

```json
{
  "method": "turn/start",
  "params": {
    "threadId": "thr_123",
    "input": [
      { "type": "text", "text": "请根据截图修复布局问题" },
      { "type": "localImage", "path": "C:\\Users\\33612\\Pictures\\layout.png" }
    ]
  }
}
```

当前 [codex-worker.ts](../src/IlMatto.ManagerHost/src/codex-worker.ts) 的 `sendCodeTask()` 只发送一个 `type: "text"` item，因此只需要把输入构造从单一字符串扩展为 text item + image item，即可对 Codex App Server 传递图片。现有 Coding Agent 的审批、工具事件和结果协议可以保持不变。

App Server 还提供 `model/list` 的 `inputModalities` 字段，客户端可以据此判断当前模型是否声明支持 `text`、`image` 等输入类型。建议未来在发送前检查这个能力，避免对不支持视觉输入的模型直接发图。

参考：

- [Codex App Server：turn input items](https://learn.chatgpt.com/docs/app-server)
- [Codex App Server：model/list 与 inputModalities](https://learn.chatgpt.com/docs/app-server)

## 4. IlMatto 当前链路的缺口

### 4.1 桌面端现在可以携带图片

当前 [ManagerProtocol.cs](../src/IlMatto.Desktop/Infrastructure/ManagerProtocol.cs) 的 `SendManagerMessage` 支持文本和可选图片附件；图片也可以由输入框 `Ctrl+V` 粘贴后生成受管 PNG：

```csharp
SendManagerMessage(string SessionId, string Text, IReadOnlyList<ManagerImageAttachmentMessage>? Attachments = null)
```

当前 [ManagerViewModel.cs](../src/IlMatto.Desktop/ManagerViewModel.cs) 的图片按钮会打开系统文件选择器，支持多选图片，并在输入区显示待发送附件。发送前图片会复制到 `%LocalAppData%\\IlMatto\\manager-sessions\\attachments\\<sessionId>`，已发送的用户消息保存 IlMatto 管理后的绝对路径、原始显示名和 MIME 类型。限制为每条消息最多 8 张、单张 20 MiB、总计 64 MiB。

### 4.2 ManagerHost 验证并转发图片附件

当前 [protocol.ts](../src/IlMatto.ManagerHost/src/protocol.ts) 的客户端消息定义为：

```ts
{ type: "send_manager_message"; sessionId: string; text: string; attachments?: ManagerImageAttachment[] }
```

[index.ts](../src/IlMatto.ManagerHost/src/index.ts) 只在明确编码请求时跳过 Companion 路由，将原始文本和附件交给 Codex；普通图片消息进入 Antigravity CLI 的 `cli_interactive` 通道。这样不会把图片路径伪装成普通文本，也不会让 Antigravity headless CLI 收到未定义的图片 block。

### 4.3 路由策略需要先决定图片归属

图片输入进入不同路由时，含义不完全相同：

- “看看这张截图陪我聊聊”是 Companion/视觉理解需求；当前 Antigravity headless 不能接收。
- “根据这张 UI 截图修改项目”是 Coding 任务；Codex App Server 可以接收 `localImage`，且图片应和原始用户文本一起传给 Coding Agent。
- “把这张图保存到项目”是文件操作任务；图片作为附件只解决模型理解，实际写入仍然要走 Coding Agent 的文件审批流程。

因此不要让 Companion Agent 先把图片“翻译成技术建议”再交给 Codex；如果程序已经判定为明确 Coding 任务，应将用户原文和附件一起交给 Coding Agent。

## 5. 实现说明

以下内容描述当前已经落地的边界，不再只是后续建议。

### 阶段 A：协议与安全边界

在桌面端和 ManagerHost 之间新增附件输入，但保持文字兼容：

```ts
type ManagerAttachment = {
  type: "image";
  path: string;
  mimeType?: string;
  displayName?: string;
};

type SendManagerMessage = {
  type: "send_manager_message";
  sessionId: string;
  text: string;
  attachments?: ManagerAttachment[];
};
```

建议约束：

- 路径必须由用户通过文件选择器产生，并转换为 canonical absolute path。
- 只允许图片扩展名与检测到的 MIME 类型匹配，限制文件大小和附件数量。
- 不把任意路径放入 Antigravity 文本 prompt 中冒充附件。
- 发送前在 UI 中显示附件预览和文件名，允许移除。
- 旧客户端不传 `attachments` 时，行为完全保持现状。

### 阶段 B：先只支持 Codex Coding Agent

在 `CodexAppServerBridge.sendCodeTask()` 中接收附件并构造：

```ts
const input = [
  { type: "text", text: `${workerInstruction}\n\n<user_request>\n${userRequest}\n</user_request>` },
  ...attachments.map(file => ({ type: "localImage", path: file.path })),
];
```

随后继续使用同一个 `turn/start`、同一 `threadId`、同一审批/流式事件链路。图片不需要改变 `CodeResult`、工具审批或 Coding 结果卡片协议。

### 阶段 C：Companion 图片输入

IlMatto 保留了 Antigravity SDK Bridge，同时为 CLI 增加隐藏交互式通道。CLI 图片请求由 WPF `ClipboardBroker` 和 `ConPtySession` 执行临时 `Ctrl+V`，ManagerHost 只接收清理后的终端输出并校验 `ManagerAction`。明确编码请求仍绕过 Companion Agent，将用户原文和图片附件交给 Codex。

## 6. 最终判断

对于“Agent 如何给两种 CLI 传入图片”，当前可以直接落地的答案是：

- 给交互式 Antigravity CLI：通过 WPF 保存的受管图片设置临时剪贴板，再向隐藏 ConPTY 发送 `Ctrl+V`；请求结束后按指纹条件恢复原剪贴板。
- 给 Antigravity CLI headless：当前公开 `stream-json` 协议不能传图片；不能使用未经文档化的 `image`、URL、Base64 或文件路径字段。
- 给 Codex CLI：一次性 CLI 使用 `-i/--image`，多个图片可以重复参数或逗号分隔。
- 给 Codex App Server：在 `turn/start.input` 中使用 `{ type: "localImage", path: "..." }`，这是 IlMatto 当前最适合实现的路径。

本次实现增加了受管图片层、`cli_interactive` 传输、WPF ClipboardBroker/ConPTY 控制器和输出解析，同时保持 Headless CLI、SDK、Codex 图片输入、审批和结果卡片兼容。普通文本仍使用 `stream-json`，只有普通聊天图片请求使用交互式 CLI。
