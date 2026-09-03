# 陪伴型 RP Agent 后续功能路线图

本文档记录 IlMatto v1 暂未实现的陪伴型 RP 功能，以及后续建议的技术方向。

v1 已经具备：

- 角色设定、用户资料和关系摘要
- Companion Agent 与 Coding Agent 的路由
- 明确文件/代码操作的程序级直通
- Antigravity 与 OpenAI-compatible Main Agent 支持

v1 暂不包含自动记忆、结构化关系状态、主动行为和多角色系统。

## 1. 自动记忆提取

### 目标

让 Agent 从对话中识别值得长期保存的事实、偏好、经历和共同事件，减少完全手动维护关系摘要的负担。

### 建议的记忆类型

```text
UserFact             用户的稳定事实
UserPreference       用户的偏好和沟通习惯
EmotionalEpisode     具有长期意义的情绪经历
SharedExperience     角色与用户共同经历的事件
CharacterPromise     角色曾经做出的承诺
RelationshipMilestone 关系中的重要节点
```

### 建议数据

```json
{
  "id": "memory-001",
  "kind": "user_fact",
  "content": "用户养了一只叫 Mochi 的猫",
  "sourceConversationId": "conversation-001",
  "sourceMessageId": "message-014",
  "createdAt": "2026-08-31T10:00:00Z",
  "confidence": 0.95,
  "status": "candidate"
}
```

### 实现注意事项

- 先生成候选记忆，再写入正式记忆，避免模型直接覆盖用户资料。
- 记录来源对话和原始消息，支持用户查看、修改和删除。
- 对敏感信息默认不自动保存，或要求用户确认。
- 记忆冲突时保留新旧版本，不静默覆盖。
- 初期可以使用模型抽取，不需要立即引入向量数据库。

## 2. 结构化 UserProfile

### 目标

把当前的一段用户资料文本逐步拆成可检索、可修改的用户模型。

### 建议结构

```json
{
  "preferences": {
    "food": ["拿铁"],
    "conversation": ["不喜欢未经请求的建议"]
  },
  "importantContext": [
    { "key": "pet", "value": "Mochi", "source": "memory-001" }
  ],
  "recurringConcerns": ["工作压力"]
}
```

结构化 UserProfile 负责回答“用户是什么样的人”；具体角色何时知道这些内容，仍由角色自己的记忆负责。

## 3. RelationshipState

### 目标

让角色能够保持关系发展的连续性，同时避免因为一两轮对话就突然表现得过度亲密。

### 建议模型

内部可以使用数值，但不直接展示给模型或用户：

```json
{
  "stage": "familiar",
  "familiarity": 0.72,
  "trust": 0.55,
  "closeness": 0.43,
  "lastUpdatedAt": "2026-08-31T10:00:00Z"
}
```

Context Builder 应将其转换成自然语言，例如“你们已经相当熟悉，但还没有建立很深的情感亲密关系”。

### 关系策略

- 关系状态只能缓慢变化。
- 用户表达好感不等于关系立即升级。
- 关系阶段用于软约束语气和话题，不使用“低于某数值禁止某句话”的机械规则。
- 用户可以手动重置或编辑关系摘要。

## 4. Interaction Mode

### 目标

区分用户是在倾诉、寻求安慰、请求建议、闲聊还是进行角色扮演。

### 建议输出

```json
{
  "topic": "工作压力",
  "userTone": "exhausted",
  "mode": "venting",
  "adviceRequested": false,
  "responseStyle": "warm_low_intensity"
}
```

### 推荐演进

先保持 v1 的单次生成；当“过度建议”和“情绪误判”成为主要问题后，再增加轻量 Interpreter：

```text
用户消息
  ↓
Interaction Interpreter
  ↓
Context Builder
  ↓
Companion Dialogue Model
```

Interpreter 只输出短结构化状态，不直接生成用户可见文本。

## 5. Character Initiative

### 目标

让角色能够自然地记得用户之前提到的事情，并在合适时进行一次克制的 follow-up。

### 建议数据

```json
{
  "openLoops": [
    {
      "topic": "工作面试",
      "prompt": "询问面试结果",
      "importance": 0.8,
      "availableAfter": "2026-09-01"
    }
  ],
  "initiativeBudget": 1
}
```

### 约束

- 一轮最多主动展开一个新话题。
- 没有自然上下文时不要强行召回旧记忆。
- 用户明确表示不想被追问时，暂时关闭主动 follow-up。
- 角色主动性不能替代用户输入，也不能制造紧迫感或索取用户注意力。

## 6. 多角色系统

### 目标

允许同一个用户拥有多个独立角色，每个角色有自己的设定、记忆和关系历史。

```text
User
├── Character: Luna
│   ├── CharacterProfile
│   ├── RelationshipState
│   └── CharacterMemory
└── Character: Alice
    ├── CharacterProfile
    ├── RelationshipState
    └── CharacterMemory
```

### 实现要求

- 当前会话固定一个 `characterId`。
- 记忆必须按 `characterId` 隔离。
- UserProfile 可以是共享的，但角色是否知道某条资料必须单独记录。
- 角色切换不能自动共享未授权的私密经历。
- 当前 v1 的单一 CompanionProfile 需要迁移为默认角色配置。

## 7. 主动消息与后台调度

### 目标

在用户没有发送消息时，角色可以根据开放话题或用户授权进行提醒和问候。

### 前置条件

- 用户明确开启主动消息。
- 支持免打扰时段和频率限制。
- 支持立即暂停和总开关。
- 每条主动消息都要能追溯到触发原因。

这部分涉及定时任务、应用生命周期和通知权限，不建议在记忆系统稳定前实现。

## 8. Coding Agent 结果的 RP 展示层

### 目标

在不修改技术结果的前提下，让陪伴角色可以对 Coding Agent 的结果做简短的人格化转述。

### 推荐设计

```text
Coding Agent
  ├── CodeResult：事实结果，保持原样
  └── Optional Companion Summary：可选的角色化摘要
```

角色化摘要只能引用 `CodeResult` 中已有的信息，不能重新解释技术方案、虚构文件变化或隐藏失败状态。

v2 初期可以只增加一个“用陪伴角色总结”按钮，而不是每次自动调用模型。

## 推荐实现顺序

```text
自动记忆候选
      ↓
结构化 UserProfile
      ↓
RelationshipState
      ↓
Interaction Mode
      ↓
Character Initiative
      ↓
主动消息
```

多角色系统和 Coding Agent 结果展示层可以作为相对独立的分支开发。

推荐分阶段推进：

1. v1.1：记忆候选、记忆查看/删除、基础 UserProfile。
2. v1.2：关系阶段和自然语言 Context Builder。
3. v1.3：Interaction Mode 和更稳定的情绪回应。
4. v2：主动 follow-up、多角色、主动消息和 Coding 结果角色化摘要。

## 共同约束

- 结构化状态服务于连续性，不应让对话表现得像数值化恋爱游戏。
- 角色可以有虚构背景，但不能把虚构内容伪装成现实中已经发生的事实。
- 所有长期记忆都应支持用户查看、修改和删除。
- 敏感信息默认最小化保存，并尽量保留来源和更新时间。
- Coding Agent 仍然是唯一负责本地文件、命令、测试和 Git 操作的角色。
