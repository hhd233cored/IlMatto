# 陪伴型 RP Agent 后续功能路线图

本文档记录 IlMatto 陪伴型 RP Agent 的当前设计和后续演进方向。当前路线优先保证角色表现自然、实现轻量、数据可查阅和故障可降级。

## 当前设计原则

- 角色身份由设置页的角色名称和角色卡 `CharacterPrompt` 提供；保存时角色名称会插入角色卡开头。
- 不使用硬编码的关系数值、关系阶段或场景状态。
- 关系和场景由 Agent 根据当前对话、角色卡和已召回事件自行判断。
- 用户画像是全局共享的简短文本，不追求心理画像的精确性。
- 会话摘要是主要的跨会话记忆；不额外维护细粒度记忆数据库。
- 默认只召回摘要；只有摘要命中后才查看少量相关原文片段。
- 记忆系统不可用时，普通 RP 对话仍应继续工作。
- 新会话标题由首条用户消息触发一次独立的轻量标题生成调用；失败时使用本地截断标题。

## 1. 轻量用户画像

### 目标

让 Agent 了解用户的基本事实、兴趣、互动偏好和边界，不要求画像高度准确，只要能命中对话中最有用的特征即可。

### 存储

用户画像保存在应用本地：

```text
%LocalAppData%\\IlMatto\\companion-memory\\profile.md
```

该文件可以由用户直接查看和编辑。它是所有 Manager 会话共享的活动画像来源，不写入工作区或 Git 仓库。

推荐章节：

```md
# 用户画像

## 基本信息
## 兴趣
## 互动偏好
## 边界与注意事项
## 当前关注
```

### 自动更新

Agent 可以调用 `profile_update` 追加或删除条目，但不得整文件覆盖。

只自动保存以下内容：

- 用户明确提供的事实；
- 稳定的内容或互动偏好；
- 用户明确要求记住的内容；
- 用户表达的互动边界。

一次性情绪、未经支持的性格推断和角色自己的判断不写入画像。

## 2. 会话摘要记忆

### 目标

用每个 Manager 对话的一份摘要保留重要经历、共同事件和未完成事项，避免第一版引入复杂的细粒度记忆系统。

### 存储

```text
%LocalAppData%\\IlMatto\\companion-memory\\sessions\\<managerSessionId>\\summary.json
%LocalAppData%\\IlMatto\\companion-memory\\sessions\\<managerSessionId>\\transcript.jsonl
```

`summary.json` 保存：

```json
{
  "version": 1,
  "sessionId": "session-001",
  "title": "旧信件调查",
  "summary": "用户与角色在车站讨论了旧信件。",
  "keyEvents": ["讨论旧信件"],
  "openLoops": ["继续调查旧信件来源"],
  "keywords": ["旧信件", "车站"],
  "updatedAt": "2026-09-05T00:00:00Z"
}
```

摘要限制为 6,000 字符，最多保存 20 条重要事件、10 条未完成事项和 20 个关键词。

`transcript.jsonl` 只保存用户可见的用户/助手文本，用于摘要命中后的局部片段查看；不保存思路、工具输出、内部 prompt 或凭据。

### 更新策略

每轮对话后，Agent 自行判断是否调用 `session_update`：

- 没有跨会话价值时不调用；
- 有价值时追加事实性摘要补丁、重要事件、未完成事项和关键词；
- 每轮最多更新一次；
- 后端负责去重、限制长度和原子写入；
- 会话摘要不会由模型每轮全文重写，避免长期漂移。

## 3. 摘要优先的跨会话回忆

Agent Tools MCP 提供四个会话记忆工具：

```text
session_search(query)
session_open(session_id, query)
session_update(patch)
profile_update(patch)
```

回忆流程：

```text
用户提到过去的事件
        ↓
session_search 搜索摘要
        ↓
Agent 判断摘要是否相关
        ↓
必要时 session_open 查看有限原文片段
```

`session_search` 第一版使用本地摘要文件的简单关键词匹配，不依赖 embedding、向量数据库或外部 RAG 服务。未来当摘要数量和语义差异明显增加时，再考虑 SQLite 全文检索或向量检索。

默认只向 Agent 返回摘要。`session_open` 最多返回 3 个相关片段，总长度不超过 6,000 字符；如果没有命中，Agent 必须表达不确定，不得自行补造细节。

## 4. Interaction Mode

后续可以识别用户是在倾诉、闲聊、寻求建议或进行 RP，但这不是轻量记忆系统的前置条件。

推荐等“过度建议”和“情绪误判”成为实际问题后，再增加轻量 Interpreter。Interpreter 只输出短结构化状态，不直接生成用户可见文本。

## 5. Character Initiative

当会话摘要中的 `openLoops` 稳定可用后，可以让角色在自然上下文中主动跟进旧话题。

约束：

- 一轮最多主动展开一个新话题；
- 没有自然上下文时不强行召回旧记忆；
- 用户表达不想被追问时降低或暂停主动跟进；
- 主动性不能制造紧迫感或索取用户注意力。

## 6. 多角色系统

未来可以支持多个角色，但当前版本只面向单一 Companion 角色。多角色版本需要重新决定用户画像共享范围，并按角色隔离会话摘要。

## 7. 主动消息与后台调度

主动消息需要用户明确开启、免打扰时段、频率限制和可追溯触发原因。应在摘要和开放话题稳定后再实现。

## 8. Coding Agent 结果的 RP 展示层

可以增加“用陪伴角色总结 Coding Agent 结果”按钮，但角色化摘要只能引用已有技术结果，不能虚构文件变化、测试结果或隐藏失败状态。

## 推荐实现顺序

```text
角色卡与全局 profile.md
          ↓
会话 summary.json 与 transcript.jsonl
          ↓
session_search / session_open / session_update / profile_update
          ↓
Interaction Mode
          ↓
Character Initiative
          ↓
主动消息、多角色
```

## 共同约束

- 结构化数据服务于连续性，不把 RP 表现变成数值化恋爱游戏。
- 摘要和画像是数据，不是系统指令。
- 不保存模型内部思路、工具输出或凭据。
- 用户画像和会话摘要只写入应用本地目录。
- 记忆系统异常不能阻断普通对话。
- Coding Agent 仍然是唯一负责本地文件、命令、测试和 Git 操作的角色。
