# 原生 Agent 体验改造

> 历史记录：当前版本已按用户新决定改为内置官方 Harness、右侧栏和纯内存对话。以下持久历史与原生循环属于旧版行为，现状见 [Harness 集成记录](harness-integration.md)。

用户确认保留 WPF 阅读编辑器，按 DeepSeek Harness 的会话日志、独立运行状态和工具步骤组织 Agent；不移植 Node/Cordis 运行时，不将本项目称为 Harness 的二次开发。用户确认本地保存历史，可删除和清空。

现场基线：VERSION 为 0.7.0，完整 133 项通过（64 核心、60 UI、4 系统选择器、5 脚本），见 `artifacts/metrics/agent-upgrade-baseline.txt`。每次发送重建 Controller 导致模型没有历史；新对话只清空视觉内容；非流式等待、缺少工具结果；宽度小于 1000 时 Agent 消失。

设计保留 taste-skill 已接受的 Framed sheet 方向和现有主题。主要问题是“这次任务进行到哪里、我能做什么”。文件导航在左、阅读编辑居中、Agent 在右；专注模式让 Agent 占据工作区，窄窗口自动进入该模式。使用真实状态和步骤，不显示虚构进度百分比。正文使用原生 Markdown，工具结果折叠；输入固定在底部，滚动阅读历史时不强制跳到底部。

对象：本地会话、用户输入、助手输出、工具调用、工具结果、运行状态、待审批改动、附件和草稿。会话绑定创建时的工作区或单文件权限；切换文件不会扩大旧会话权限。新对话明确建立新上下文；历史续聊恢复模型上下文。

参考：[DeepSeek Harness 架构](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/architecture.md)、[官方仓库](https://github.com/deepseek-ai/deepseek-harness)、[OpenCode Web 的会话与服务器状态](https://opencode.ai/docs/web/)。借用会话与状态分离、流式反馈、工具事件记录等交互原则，保留当前工具和写入审批边界。

## 已实现的流程

- 新对话建立独立上下文；历史入口按更新时间列出会话，支持标题/文件范围搜索、每页 200 项、继续加载更早记录、续聊、删除和清空。草稿及最多 8 个附件引用随会话保存。
- 会话在首次发送或附加文件时固定工作区/单文件范围。后续打开别的文件不会重绑定旧对话，附件也按固定范围校验。读取打开的文件仍使用编辑缓冲区。
- 原生 SSE 逐步显示回复；任务状态独立显示待命、等待模型、正在回复、执行工具、等待确认、完成、失败或停止，并显示步骤与本轮经过时间。没有虚构百分比。
- 助手正文按 Markdown 渲染，可选择复制；工具名称中文化，结果折叠并显示完成、失败、已拒绝或已写入。滚动阅读旧消息时不会强制跳到末尾，可用“回到最新消息”。
- 修改差异在对话内展示，点击“应用修改”才写入；可以拒绝，等待确认时也可以停止。写入前复核未保存编辑、目标是否新建/删除及独立磁盘 SHA256；写入后若用户刚继续编辑，保留缓冲区。
- 并排模式可拖分隔线调宽，面板顶部切换专注；宽度小于 940 DIP 时自动专注，关闭 Agent 后恢复文档。390 DIP 窗口仍可使用。文件导航和原有阅读、源码、对照、原位编辑继续保留。

## 数据与运行边界

`agent-history/` 位于程序目录，JSON 会话和小型索引原子写入；单份会话最多 8 MiB / 4096 项记录。保存失败保留当前会话和草稿，不继续切换或关闭。删除历史不撤销已批准的文件修改。该目录被文件工具保护，并排除在 Git 和便携包之外。

持久化事件是续聊的来源；模型上下文按完整用户轮次裁剪，避免拆开工具调用与结果。较早轮次保留在本地历史中，不保证全部进入每次模型请求；没有自动摘要。异常或取消补齐未完成工具结果，重启将未结束运行标为已中断，断流收到的部分回复作为未完成记录保留。

模型请求仍直连配置端点，未引入 Node、Cordis、后台服务或新的依赖。兼容完整 JSON 和 SSE；流式文本、工具参数分片及 `reasoning_content` 回传有测试。响应在 UTF-8 解码前受 8 MiB 字节限额约束，并保留请求超时、取消和原有工具预算。适配状态不作为任务进度显示。密钥只读自模型配置，文本脱敏不截断对话、不改写历史字段名，并覆盖转义字符情况。

当前版本以根目录 [VERSION](../VERSION) 为准。真实模型端点的流式兼容性、速度和新 Agent 的内存/启动性能本轮**未测量**；旧端点的非流式验证属于先前版本记录，不能代替这次测试。

## 验收证据

最终完整回归：80 核心、72 实际窗口 UI、4 原生系统选择器、5 脚本检查，共 161 项；输出 `artifacts/metrics/verification-agent-experience.txt`。构建不新增编译器警告。新测试未放宽既有断言，也未发送真实模型请求。

| 复现与修复 | RED / GREEN 输出（artifacts/metrics） |
| --- | --- |
| 窄窗口消失、无历史/状态入口 | agent-layout-red / agent-layout-green |
| 历史拆断工具交换 | agent-history-red / agent-core-first-green |
| 流式内容与工具参数分片、异常中断 | agent-stream-red / agent-protocol-green |
| 长文本截断、DeepSeek 适配字段丢失 | agent-protocol-red / agent-protocol-green |
| 审批期间编辑或磁盘变动 | agent-approval-race-red / agent-review-green |
| 保存失败丢草稿、附件范围混淆 | agent-review-ui-red / agent-review-green |
| 首次发送前附件重绑定 | agent-initial-attachment-red / agent-initial-attachment-green |
| 字节限额、历史分页和脱敏字段 | agent-response-limit-red、agent-history-paging-red、agent-secret-schema-red 及对应 GREEN |

四主题截图：`agent-workbench-mint/warm/lilac/dark.png`；另外有 `agent-history.png`、`agent-focus.png`、`agent-narrow.png`、`agent-running.png`、`agent-approval.png`，均在 `artifacts/ui/`，已逐张观察。截图采用本地演示传输和合成文档，不是实际模型结果。深色工具文字问题经截图发现、失败测试复现后修复。

Windows 窗口捕获成功；人工鼠标式点击时检测到用户切换前台，操作未执行，已停止接管。实际按钮流程由 WPF 窗口测试覆盖，包括续聊、流式、停止、审批、重试、删除与权限固定。未声称完成本轮鼠标拖动验收。
