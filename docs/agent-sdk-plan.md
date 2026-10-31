# 0.10：原生 Agent 客户端 + DSH SDK（已实现）

**状态：已实现，随 0.10.0 发布。** 用户决定放弃嵌入官方 Web GUI（右侧 WebView2），改用「CLI 式」架构：Agent 运行时是常驻的本机子进程，经 stdio JSON-RPC 驱动；界面由 WPF 原生自绘。本文件记录最终契约、目录与限制；完整实现说明见 [0.10 原生 SDK 客户端](agent-0.10.md)。

## 为什么

- 实测：DSH web 服务就绪 ~3.7s；再加 WebView2 创建与页面加载，打开面板约 5s；每次「新任务」或切换文件范围都会重启整个运行时（4–6s）。
- DSH SDK 走 stdio JSON-RPC，无 webserver、无 WebView；实测 `--profile sdk` initialize ~2.9s；改用独立最小树 **`--profile sdk-minimal`** 后 initialize **~1.45–1.7s**（首个事件 ~1.8s）。进程常驻后新会话/新任务只发一次 `session/prompt`。
- sdk-minimal 默认 `compression: none` 的 JSONL 持久化，便于检视和测试；默认工具树里的 fs/terminal/compaction 等由 overlay 禁用，只保留我们的 6 个原生工具。
- 官方 SDK 已支持：`session/prompt`、`session/resume`、`session/wait`、`session/working-directory/*`、`session.event` / `session.status` 通知、持久化（JSONL）。固定版本没有 `approvalRequests` 桥。
- 原来的 0.7 原生对话 UI（`AgentConversationView`、`AgentHistoryView`、`DiffDialog`、`AgentSessionStore`）仍在仓库里，已复用，未从零重写。

## 架构

```
轻页 WPF
 ├─ AgentPanel（原生 UI：对话/工具卡/审批 diff/历史/输入）
 │    └─ SdkAgentController（C#）：把 SDK 事件映射成 AgentActivity + AgentSession 条目
 ├─ SdkRuntime（C#）：spawn node.exe sdk-launcher.mjs
 │    ├─ stdout/stdin：换行 JSON-RPC 2.0（initialize / session/* / shutdown）
 │    ├─ stderr：诊断（密钥脱敏）
 │    └─ TCP loopback 本机桥（一次性 token 鉴权）：文件工具与审批
 └─ HarnessNativeTools（C#，沿用）：context/pin/tool/apply/discard 与现有权限/沙盒语义
```

Node 侧（`src/Harness/`）：

```
sdk-launcher.mjs       启动 dsh --profile sdk-minimal --patch <生成的 overlay>，并把 native bridge 接到 TCP
sdk-composition.mjs    生成 sdk-minimal 的 overlay（模型、持久化、persona、禁用默认工具、插入 native-tools）
native-tools.mjs       沿用（6 个工具：list/read/search/write/edit/create），bridge 传输为通用接口
native-bridge.mjs      bridge 安装 + TCP 客户端（+ 退役 WebView 用的行长适配器）
sdk-approval-bridge.mjs 备用 SDK 原生审批插件，0.10 不挂载
```

## 最终契约

启动环境变量（C# → Node）：

| 变量 | 含义 |
| --- | --- |
| `QINGYE_MODEL_BASE_URL` / `QINGYE_MODEL_NAME` | 模型端点与模型名（`模型.txt`） |
| `QINGYE_MODEL_KEY` | 密钥，仅环境变量，不写文件/日志 |
| `QINGYE_APP_HOME` | 稳定应用数据目录（会话 JSONL、overlay、scope 文件） |
| `QINGYE_BRIDGE_PORT` / `QINGYE_BRIDGE_TOKEN` | 本机 TCP 桥端口与一次性令牌 |
| `DSH_HOME`、`DSH_TELEMETRY_DISABLED=1`、`DSH_PERMISSION_MODE=workspace-write` | 沿用 |

JSON-RPC（stdout/stdin，一行一帧）：

- C# → Node：`initialize {provider, model, cwd, approvalRequests:false, capabilities:{userQuestions:false}}`；`session/prompt {sessionId, contentBlocks:[{type:"text",text}]}`；`session/resume {sessionId}`；`session/working-directory/set`；`shutdown`。
- Node → C#：`session.event {sessionId, event:{type,data,...}}`；`session.status {sessionId,status}`；错误帧。

事件映射（`session.event`）：`user/message`→用户气泡；`assistant/message`→助手 Markdown；`tool/call`/`tool/result`→工具卡与结果折叠；`approval/asked|decided`→审批轨迹；`todo/write`→任务清单；`turn/start`/`step/start`→状态与步数；`turn/end`→完成/失败/停止；`session/title`→标题。

TCP 桥（一行一条 JSON，C# 为 server，Node 启动后连接并在首行发送 `{"token":...}`）：

- Node → C#：`{"id":N,"operation":"context"|"pin"|"tool"|"approval"|"apply"|"discard","sessionId":...,"args":{...},"path"?:...,"diff"?:...,"reason"?:...,"proposal"?:...}`
- C# → Node：`{"id":N,"value":...}` 或 `{"id":N,"error":"..."}`；`approval` 返回 `{"value":{"allowed":boolean}}`，超时/断连一律按拒绝（Node 侧 fail-closed）。

审批：固定 `sdk-jsonrpc-server` 0.2.1-rc.5 没有 `approvalRequests` 桥，因此审批不依赖 SDK 线协议，而是完全走本机桥：native-tools 的写工具先 `tool` 拿提案（含 diff），再 `approval` 询问 C#；C# 弹原生 diff 卡，允许才 `apply`，拒绝则 `discard` 并返回中文「用户未批准」。stdout/stdin 只承载 JSON-RPC，Node→C# 只有一条 TCP 通道。

## 会话与历史（最终实现）

- DSH 侧：`session-persistence-jsonl` 根指向 `QINGYE_APP_HOME/sessions`（sdk-minimal 默认 `compression: none`）；用于 `session/resume` 的模型上下文。
- 轻页侧：`AgentSessionStore`（`AgentSession` 带 `HarnessSessionId`）保存显示用条目、标题、范围、草稿到 `agent-history/`；历史面板列/搜/删/续聊。
- 新任务：同一个常驻进程内生成新 sessionId 并发 `session/prompt`，不重启运行时（实测 38 ms）。
- 恢复：先 `session/resume {sessionId}` 再允许 prompt，同时重绑当前文件/工作区（实测 93 ms）；本地条目立即渲染旧对话。
- 停止：SDK 线协议没有取消方法；「停止」= 结束进程并标记 stopped，下次发送时重启进程并按需 resume（限制见下）。

## 启动与预热（最终实现）

- 首次打开面板才启动 SDK 进程（sdk-minimal，实测 1490 ms 到可用，显示「正在启动内置运行时…」）。
- 进程常驻：关闭面板不杀，新任务不重启，退出应用时 Dispose（实测关闭 35 ms、窗口关闭总耗时 109 ms）。
- 不做应用启动预热（`AgentPanel.AllowPrewarm` 默认关闭，仅保留测试入口）。

## 验收结果

- Node 运行时：`artifacts/metrics/sdk-node-*.txt` — `request/header.tools` 恰为 6 个原生工具；冷 initialize ~1.45s；批准/拒绝/超时三条路径均不越权落盘。
- C# Core/UI：JSON-RPC 编解码、事件映射、approval 往返、resume、scope/权限、历史条目落盘、进程停止清理均有回归；完整 `scripts/test.ps1` 全绿（Core 104 + UI 85 + 系统选择器 4 + 脚本 8 = 201，0 失败），node --test 15 项通过。
- 端到端：`build/bin/ExperienceHarness.exe harness flow restart large` 在真实 WPF 窗口 + 本机合成模型（SSE）+ 固定 SDK 运行时上跑通「输入→工具→审批（拒绝/批准）→落盘→编辑器缓冲区原地更新→历史可见→新任务 38 ms→恢复旧会话 93 ms」；无 WebView2 进程、退出无 Node 残留；16 条 PASS，见 `artifacts/metrics/sdk-qa-final.txt`。
- 文档：README、docs/harness-integration.md 已改为 SDK 架构；0.8–0.9 Web 皮肤章节标注退役；版本 0.10.0。

## 目录与产物

- 源码：`src/Core/SdkRuntime.cs`、`src/Core/SdkAgentController.cs`、`src/UI/AgentPanel.cs`、`src/UI/DocumentSync.cs`、`src/Harness/sdk-*.mjs`、`src/Harness/native-bridge.mjs`。
- 运行时数据：`app/agent-data/`（SDK 会话 JSONL、overlay）与 `app/agent-history/`（显示条目）均在程序目录，不进入便携包。
- 便携包：`release/轻页-0.10.0-win-x64.zip`，包内 `app/Harness/` 含 sdk 启动器与 `runtime-files.json`；大小与哈希见 `release/package-info.txt`、`release/SHA256.txt`。

## 已知限制（先记录，后续跟进）

- SDK 线协议不转发逐 token 增量（只有 `assistant/message` 完成事件），原生面板先显示「正在回复」，完成后一次性渲染；如需流式，需要上游增加 live-chunk 或改用 HTTP follow。
- 没有取消方法：停止后下次发送需要重启运行时（含一次冷启动）。
- 原生 Markdown 渲染与工具卡样式仍在复用 0.7 组件，未做流式增量与富内容扩展。
- 真实外部模型端点的性能与兼容性未测量；0.8–0.9 WebView 代码只保留历史回归。
