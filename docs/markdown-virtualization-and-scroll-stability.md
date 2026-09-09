# 长会话场景下 Markdown 消息的虚拟化与滚动稳定性

本文说明 IlMatto Manager 当前对长会话 Markdown 消息的处理方式，以及“为什么这样设计”。目标是在保留完整聊天记录的同时，减少 WPF 控件树、Markdown 排版和图片加载对首屏打开及滚动的影响，并尽量避免滚动条 Thumb 因消息高度变化而跳动。

## 1. 要解决的问题

聊天记录中的消息高度不是固定值。高度会受到以下因素影响：

- 普通文本换行和段落间距；
- Markdown 标题、列表、引用、代码块和内联格式；
- 图片和附件；
- 工具过程、操作记录和结果卡片；
- 窗口内容宽度、字体和主题。

如果每条历史消息都立即创建完整的 `MarkdownViewer`、图片控件和操作控件，WPF 需要同时完成大量控件创建、Measure、Arrange 和 Markdown 排版。消息数量较多时，打开会话和拖动滚动条都会变慢。

另一个问题是：如果尚未渲染的消息在布局中高度为零，随后 Markdown 完成渲染后才突然变高，ScrollViewer 的 `ExtentHeight` 会反复变化，滚动条比例和当前位置就会发生跳动。

当前方案的核心原则是：

> 消息数据可以完整保留，但昂贵的视觉控件只在视口附近创建；不确定的真实高度先用稳定占位高度参与滚动范围计算，实际测量完成后再小幅修正。

## 2. 总体结构

```text
ManagerConversationItem.Messages
        │
        │ 完整消息模型，继续保存在内存和 conversations.json
        ▼
ChatTimelineLayoutIndex
        │
        │ 每条消息的缓存高度或估算高度
        │ Fenwick Tree 计算前缀高度和偏移定位
        ▼
ChatTimelineController
        │
        │ 只把视口附近消息放入 ListBox
        │ 其他区域由上下 Spacer 占位
        ▼
┌─────────────────────────────────────────┐
│ 上方逻辑占位                             │
│ 视口附近的 ChatTimelineMessageRow        │
│ 下方逻辑占位                             │
└─────────────────────────────────────────┘
        │
        ├─ RenderContent = false：空气泡/占位模板
        └─ RenderContent = true ：头像、Markdown、图片和操作记录
```

这里的“虚拟化”是 `ChatTimelineController` 在普通 WPF `ListBox` 上实现的逻辑虚拟化。当前没有直接依赖 WPF 原生不定高 `VirtualizingStackPanel` 来计算完整历史的高度，因此能自行控制占位高度和锚点修正。

## 3. 消息数据与视觉对象分离

`ManagerViewModel.ChatEntries` 仍然代表当前会话的完整消息集合。不会为了滚动优化而删除、分页截断或修改 `conversations.json` 中的消息。

时间线显示层另外维护三类运行时对象：

- `ChatTimelineSpacer`：代表尚未放入 ListBox 的上方或下方空间；
- `ChatTimelineMessageRow`：轻量消息行，保存消息索引、占位高度、气泡尺寸和是否渲染正文；
- `ReservedMessagePresenter`：根据 `RenderContent` 选择空气泡模板或完整内容模板。

因此，离开视口的消息不会从会话记录消失，只会释放昂贵的 Markdown、图片和操作控件；再次进入视口时，根据缓存尺寸重新创建。

## 4. 高度和气泡尺寸缓存

### 4.1 内存缓存

`ChatMessageLayoutCache` 按以下维度保存本次程序运行中的实测高度：

```text
消息对象 + 内容宽度档位（32px） → 行高度
```

未测量的消息使用稳定估算值。估算会考虑：

- 角色和日期分隔行；
- 文本换行数；
- 附件数量；
- 可见操作记录；
- 结构化结果卡片。

估算值会稍微偏高，目的是避免滚动过程中从很小的高度突然扩张。真实内容完成布局后，再用实测高度替换估算值。

### 4.2 本地缓存

已测量的布局提示会写入：

```text
%LocalAppData%\IlMatto\manager-sessions\layout-cache\<session-token>.json
```

每项包含：

```json
{
  "messageIndex": 12,
  "rowHeight": 286,
  "bubbleHeight": 244,
  "bubbleWidth": 680
}
```

其中：

- `rowHeight` 用于计算完整滚动范围；
- `bubbleHeight` 和 `bubbleWidth` 用于生成与历史真实气泡接近的占位；
- `WidthBucket` 区分不同内容宽度档位。

缓存只是性能提示，不是消息数据的事实来源。缓存损坏、缺失或过期时会回退到估算值，不应阻断聊天。

写入由 `ManagerLayoutCacheWriter` 延迟合并：

1. 真实测量结果进入内存队列；
2. 约 500ms 内合并同一会话、同一宽度档位的更新；
3. 保存时保留旧文件中没有被本次更新覆盖的条目；
4. 使用临时文件写入后原子替换；
5. 与旧值相差小于约 0.5px 时不重复写入。

这样可以避免每次流式变化或每次布局事件都重写整个缓存文件。

## 5. 逻辑高度索引

`ChatTimelineLayoutIndex` 为每条消息保存当前逻辑高度，并使用 Fenwick Tree 维护前缀和：

```text
消息 0     120px
消息 1     380px
消息 2     210px
消息 3     640px
...
```

它可以快速计算：

- 整个会话的逻辑总高度；
- 某条消息的顶部位置；
- 某个滚动偏移对应的消息索引；
- 某条消息高度变化对总高度的影响。

因此滚动条的初始范围不依赖“当前已经创建了多少个气泡”，而是依赖完整消息集合的缓存高度或估算高度。

## 6. 视口附近才渲染完整 Markdown

每次滚动或布局变化时，`ChatTimelineController` 会根据当前 `VerticalOffset` 和 `ViewportHeight` 计算一个材料化区：

```text
当前视口上下各保留一段预取范围
```

材料化区内的消息行进入 ListBox。离开材料化区的普通消息执行：

```text
RenderContent = false
```

此时只显示固定尺寸的占位模板，不创建：

- `MarkdownViewer` 或 `AdaptiveMarkdownPresenter`；
- 图片控件和图片解码；
- 工具过程的嵌套控件；
- 结果卡片的完整内容。

占位模板仍保留头像、用户/角色名称、时间、运行时长和气泡外形，因此布局不会因为正文暂时没有创建而塌陷。

## 7. Markdown 渲染队列

从占位恢复为完整内容不会一次性创建所有气泡，而是进入优先队列：

1. 视口内消息优先；
2. 距离视口较近的预取消息其次；
3. 每个 UI 渲染周期最多恢复 2 条；
4. 新一轮滚动、会话切换或销毁窗口时取消旧队列；
5. 流式输出中的消息保持实时渲染，不被普通历史回收规则打断。

这把昂贵的 WPF 控件创建分摊到多个 UI 周期中，避免滚动停止时所有 Markdown 同时重建造成长时间卡顿。

对于用户刚发送的消息和 Agent 正在输出的消息，优先采用完整内容模板。输出结束后进行一次自然高度测量；当这些消息之后离开视口，才会按照普通历史消息回收到占位状态。

## 8. 实测高度与滚动锚点

完整气泡完成布局后，`ReservedMessagePresenter` 把实际高度通知控制器。控制器会：

1. 将真实高度写入内存高度索引；
2. 更新当前消息的行高、气泡宽高；
3. 必要时异步写入本地布局缓存；
4. 更新上下 Spacer 的高度；
5. 根据当前阅读位置决定是否补偿滚动偏移。

如果变化发生在当前锚点消息之前，后续消息的绝对位置会整体移动。此时控制器把高度差加入待补偿值，并通过：

```text
新的 VerticalOffset = 原 VerticalOffset + 上方高度变化量
```

保持第一条可见消息及其相对位置不变。

以下情况不会强行补偿：

- 用户正在底部，应该继续跟随新消息；
- 用户正在拖动滚动条 Thumb；
- 用户刚刚滚动，仍处于短暂稳定窗口；
- 高度变化发生在当前锚点之内或下方。

这样可以区分“程序修正布局”与“用户主动滚动”，避免修正逻辑抢夺用户位置。

## 9. 快速滚动、Thumb 拖动和正常滚动

当前实现采用轻量状态处理：

- 普通滚动：按滚动事件在 `DispatcherPriority.Render` 刷新材料化区；
- 快速移动或大范围跳转：优先重建新的小材料化区，旧行释放正文内容；
- Thumb 拖动：取消待处理的 Markdown 恢复任务，并保留已知高度的占位；
- Thumb 松开：先恢复目标视口，再按队列逐步创建完整 Markdown；
- 会话切换：清空旧会话的时间线行、队列和高度索引，只保留持久化消息模型。

关键点是：快速滚动时不让尚未完成的 Markdown 测量参与实时滚动范围；滚动范围始终由缓存/估算高度构成。真实测量只负责渐进修正，而不是重新决定整个滚动条。

## 10. `full` 与 `air` 调试模式

设置页中的完整聊天渲染开关用于性能对比。还可以使用环境变量临时覆盖：

```text
ILMATTO_CHAT_RENDER_MODE=full
```

强制所有消息使用完整渲染路径，适合测量 Markdown 和 WPF 控件本身的成本。

```text
ILMATTO_CHAT_RENDER_MODE=air
```

强制使用空气泡/占位路径，适合验证高度索引、滚动条范围和锚点补偿。

未设置环境变量时，由设置项 `UseFullChatRendering` 决定：关闭时使用当前默认的轻量时间线，开启时使用完整列表作为基线和调试路径。

## 11. 设计取舍与当前边界

当前方案优先解决消息级虚拟化问题，但仍有以下边界：

- 完整会话消息仍会读入内存，没有做磁盘级分页；
- 单条超长 Markdown 消息进入视口时，仍可能需要较长时间创建和排版；
- 高度缓存按宽度档位保存，字体、主题或布局结构变化后可能需要一次自然测量修正；
- 缓存目前是优化数据，不承担消息恢复和一致性职责；
- 图片在完整模板中使用固定的 `180 × 140` 展示尺寸，空气泡不进行图片解码；
- 本方案不尝试在后台线程创建 WPF 控件。可放到后台的只有纯文本预处理，控件创建和布局仍在 UI 线程完成。

因此，该设计的稳定性来源不是“提前精确猜中所有 Markdown 高度”，而是三层保护：

```text
稳定估算/历史缓存
        ↓
完整逻辑滚动范围
        ↓
真实测量后的锚点补偿
```

即使某条消息的初始估算不准确，也只会产生一次受控修正，不会因为大量未渲染消息反复从零高度扩张而导致滚动条持续乱跳。

## 12. 相关实现位置

| 文件 | 作用 |
| --- | --- |
| `src/IlMatto.Desktop/Controls/ChatTimelineController.cs` | 材料化范围、空气泡回收、渲染队列、滚动刷新和锚点补偿 |
| `src/IlMatto.Desktop/Controls/ChatTimelineItems.cs` | Spacer、消息行、占位/完整模板切换和自然高度事件 |
| `src/IlMatto.Desktop/Controls/ChatTimelineLayoutIndex.cs` | 宽度档位、消息高度索引和 Fenwick Tree 前缀计算 |
| `src/IlMatto.Desktop/Infrastructure/ManagerLayoutCacheStore.cs` | 本地布局缓存的读取、合并、去重和原子保存 |
| `src/IlMatto.Desktop/Controls/ManagerLayoutCacheWriter.cs` | 测量结果的防抖、批量和后台写入 |
| `src/IlMatto.Desktop/ManagerWindow.xaml` | 空气泡、完整消息、骨架模板和聊天 ListBox |
| `src/IlMatto.Desktop/ManagerWindow.xaml.cs` | ScrollViewer 接入、自然高度回调和窗口生命周期 |
| `src/IlMatto.Desktop/ManagerViewModel.cs` | 完整消息集合、流式消息和会话切换 |

