# 轻页 0.10：原生 Agent 面板 + DSH SDK 常驻运行时（移除 WebView2 承载）

> 0.10.1 增补：统一 Agent 模式的操作逻辑与快捷键（Ctrl+Alt+F/N/H/M/S、Ctrl+L、Enter 发送/Shift+Enter 换行、Esc 分层退出、焦点自动交还），并补回文件权限三档下拉；架构与实测数据同 0.10，见「快捷键与权限」章节。
>
> 0.11 增补：临时内存会话、当前轮取消、切换范围撤销旧授权、运行时/数据目录保护与 SDK 依赖闭包裁剪；本文件的 JSONL 历史与包体数字以 [0.11 稳定性与轻量化](agent-0.11.md) 为准。

0.10 把右侧 Agent 从「用 WebView2 承载官方 Web GUI」改为 **原生 WPF 面板 + DeepSeek Harness SDK（stdio JSON-RPC）常驻子进程**：运行时使用官方 `sdk-minimal` profile，编辑器通过换行分隔的 JSON-RPC 2.0 驱动会话、事件与历史，文件工具和写审批走编辑器自己拥有的本机 TCP 回环桥。固定版本仍为 `@deepseek-ai/dsh@0.2.1-rc.5`（`sdk-minimal` 树）与 Node 24.18.0；没有修改下载的上游包。

0.8–0.9 的 WebView2 Web 方案已**退役**：`launcher.mjs`、`composition.mjs`、`src/UI/HarnessPanel.cs`、`src/UI/AgentSkin.cs`、`src/UI/HarnessBrowserCleanup.cs` 等代码仍保留在仓库并可作为历史回归入口，但不在生产路径上，生产面板不会构造 WebView2、不会启动本地 HTTP 服务、不会注入页面皮肤。本文件只描述 0.10 的实际实现；0.9 当时的设计见 [0.9 Agent 面板与工具增强](agent-0.9.md)（历史文档，已由本方案取代）。

## 为什么放弃 WebView2

0.8–0.9 的链路是：Node 启动官方 `web` profile 的本地服务 → WebView2 打开页面 → 注入皮肤隐藏 Harness 自己的导航。实测与维护成本集中在三处：

- **启动慢**：Web 服务就绪约 3.7 s，再加 WebView2 创建与页面加载，打开面板约 5 s；「新任务」或切换文件范围都要重启整个运行时，耗时 4–6 s。
- **职责错位**：界面由上游 React 页面渲染，原生面板只能靠 DOM 选择器与 CSS 模块类名去「藏」导航、工作区和模型选择；升级 Harness 就可能失效，而且被隐藏的组件仍在挂载。
- **数据与权限要跨两条链路**：审批、文件工具要经过页面 → 原生桥的额外转发；会话历史仍要靠内存后端实现。

改为 SDK 后没有 webserver、没有 WebView、没有页面注入：SDK 子进程用 stdio 说 JSON-RPC，事件直接映射成原生 UI；`sdk-minimal` 首启 initialize 实测约 **1.45–1.7 s**，进程常驻后「新任务」只发一次会话创建，实测 **38 ms** 且 PID 不变。

## 架构

```
轻页 WPF（单进程）
 ├─ AgentPanel（原生 UI：对话 / 工具卡 / 审批 diff / 历史 / 附件 / 输入）
 │    └─ SdkAgentController：DSH 事件 → AgentActivity / AgentSession
 ├─ SdkRuntime：spawn node.exe sdk-launcher.mjs
 │    ├─ stdin/stdout：换行分隔 JSON-RPC 2.0
 │    ├─ stderr：诊断（密钥与桥令牌脱敏）
 │    └─ TCP 127.0.0.1 回环桥（一次性令牌）：context / pin / tool / approval / apply / discard
 └─ HarnessNativeTools：沿用 0.6–0.9 的文件工具、权限与沙盒语义
```

Node 侧（`src/Harness/`，构建时复制到 `build/harness-runtime/` 并写入 `runtime-files.json`）：

| 文件 | 作用 |
| --- | --- |
| `sdk-launcher.mjs` | 清空继承环境、生成 overlay、启动 `--profile sdk-minimal --patch <overlay>`；stdout 留给 JSON-RPC，诊断写 stderr 并脱敏 |
| `sdk-composition.mjs` | 生成 sdk-minimal 的补丁：禁用默认 shell/fs/compaction 等 15 组行，插入模型适配器与 6 个原生工具，设置 persona 与 JSONL 会话根 |
| `native-tools.mjs` | 6 个文件工具（`list_files`、`read_file`、`search_files`、`write_file`、`edit_file`、`create_file`），经通用 native bridge 访问编辑器 |
| `native-bridge.mjs` | 安装进程级 bridge；SDK 路径使用 TCP 客户端（首行 `{"token":...}`，一行一请求，按 id 匹配），并保留退役 WebView 使用的行长桥适配器 |
| `sdk-approval-bridge.mjs` | **不挂载**的备用插件：把 SDK 的 `approval/request` waterfall 转成 stdout 请求；0.10 审批走 TCP 桥，挂载它会重复弹审批 |

C# 侧：

| 文件 | 作用 |
| --- | --- |
| `src/Core/SdkRuntime.cs` | stdio JSON-RPC 通道（单行 8 MiB 上限、按 id 关联、60 s 默认超时）、Node 进程生命周期、一次性 `initialize`、共享常驻实例、TCP 桥上令牌鉴权与请求分发、stderr 脱敏 |
| `src/Core/SdkAgentController.cs` | 驱动常驻运行时：新会话、`session/prompt`、`session/resume`、`session/working-directory/set`；把事件映射为原生 UI 的 `AgentActivity`；处理写提案与审批；停止/释放 |
| `src/Core/HarnessNativeTools.cs` | 文件工具的权限、路径边界、SHA256/脏缓冲区复核、原子写入（沿用） |
| `src/UI/AgentPanel.cs` | 原生面板：对话、工具步骤、审批卡、历史会话、附件、权限下拉、停止/重试/新任务；首次显示时显示「正在启动内置运行时…」 |
| `src/UI/DocumentSync.cs` | Agent 写入后原地刷新已打开的编辑器缓冲区（不重建窗口或会话） |

## 进程协议

启动环境变量（C# → Node）：

| 变量 | 含义 |
| --- | --- |
| `QINGYE_MODEL_BASE_URL` / `QINGYE_MODEL_NAME` | OpenAI 兼容端点与模型名（来自 `模型.txt`） |
| `QINGYE_MODEL_KEY` | 密钥；只经环境变量传入，不写文件、不进日志（stderr 也会脱敏） |
| `QINGYE_APP_HOME` | 稳定应用数据目录（默认程序目录 `agent-data`），SDK 会话 JSONL 写在其下 |
| `QINGYE_BRIDGE_PORT` / `QINGYE_BRIDGE_TOKEN` | 本机 TCP 桥端口与一次性令牌 |
| `DSH_HOME`、`DSH_TELEMETRY_DISABLED=1`、`DSH_PERMISSION_MODE=workspace-write` | 运行时数据目录、关闭遥测、文件权限模式 |

JSON-RPC（stdout/stdin，一行一帧）：

- C# → Node：`initialize {provider, model, cwd, approvalRequests:false, capabilities:{userQuestions:false}}`；`session/prompt {sessionId, contentBlocks:[{type:"text",text}]}`；`session/resume {sessionId}`；`session/working-directory/set`；`shutdown`。
- Node → C#：`session.event {sessionId, event:{type,data,...}}`；`session.status {sessionId,status}`；错误帧。

事件映射（`session.event`）：

| DSH 事件 | 原生 UI |
| --- | --- |
| `user/message`（source user） | 用户气泡（合成上下文注入不进入对话） |
| `assistant/message` | 助手 Markdown；只取 `message.content[].text` |
| `tool/call` / `tool/result` | 工具卡名称、参数摘要、结果/失败折叠 |
| `approval/asked` / `approval/decided` | 审批轨迹（真正的审批卡由 TCP 桥的 `approval` 请求驱动） |
| `todo/write` | 任务清单（可选） |
| `turn/start` / `step/start` | 状态：等待模型 / 执行中、步数 |
| `turn/end` | 完成 / 失败 / 停止及原因 |
| `session/title` | 会话标题（缺失时回退为首条用户消息） |

## TCP 本机桥与审批

C# 是 TCP server（只绑定 `127.0.0.1`，系统分配端口），Node 启动后连接并在首行发送 `{"token":...}`；之后一行一条 JSON：

- Node → C#：`{"id":N,"operation":"context"|"pin"|"tool"|"approval"|"apply"|"discard","sessionId":...,"args":{...},"path"?:...,"diff"?:...,"reason"?:...,"proposal"?:...}`
- C# → Node：`{"id":N,"value":...}` 或 `{"id":N,"error":"..."}`

写审批只走这一条通道，stdout 保持纯 JSON-RPC：

1. `write_file` / `edit_file` / `create_file` 先经 `tool` 让编辑器生成提案（含相对路径与有界 `+/-` diff）。
2. `approval` 在原生面板弹出 diff 卡；批准返回 `{"allowed":true}`，拒绝返回 `{"allowed":false}`。
3. 批准后 `apply` 落盘并触发 [DocumentSync](../src/UI/DocumentSync.cs)；拒绝则 `discard` 并给模型中文「用户未批准」。
4. 桥断连、超时（默认 10 分钟）、提案缺失或归属不符一律按拒绝处理（fail-closed）；自动编辑权限也只对当前主动导入的工作区生效，写入前仍复核脏缓冲区与磁盘 SHA256。

## 会话、历史与续聊

0.10 恢复了持久历史（这是相对 0.9「仅内存、退出即失」的有意变更）：

- **DSH 侧**：`sdk-minimal` 的会话持久化默认 JSONL，根指向 `agent-data/sessions`，`compression: none`，便于检视与测试；`session/resume` 用它恢复模型上下文。
- **轻页侧**：`AgentSessionStore` 把显示条目、标题、范围、草稿写到程序目录 `agent-history/`，每份上限 8 MiB、最多 4096 条，原子写入；历史面板支持列表、搜索、删除、清空与续聊。
- **新任务**：在同一个常驻进程里生成新 sessionId 并发 `session/prompt`，实测 **38 ms**，Node PID 不变。
- **续聊**：先 `session/resume`，再按编辑器当前文件/工作区重绑 `session/working-directory/set`；实测打开历史到可发送 **93 ms**。范围已不存在时给出提示，不静默改目录。
- **停止**：SDK 线协议没有取消方法；「停止」结束 Node 进程并标记 stopped，下次发送时重启运行时并按需 resume（限制见下文）。

## 编辑器缓冲同步

`src/UI/DocumentSync.cs` 在 Agent 写入落盘后处理已打开的文件：

- 干净缓冲区：通过一次 `StartUndoGroup` + `Replace` 把磁盘内容写回，撤销一次即可恢复写入前内容；光标、选区与滚动位置保留。
- 脏缓冲区：**不覆盖**用户未保存的修改，只提示「磁盘已写入，你的修改仍在缓冲区」。
- 正在原位编辑的 Markdown：提示退出原位编辑后核对，不强行替换。
- 写入未打开的文件：只提示结果并刷新文件树/标签，不打开新窗口。

## 快捷键与权限

0.10.1 统一了 Agent 模式的操作逻辑；所有 Agent 快捷键都可以在面板关闭时直接使用，会先打开面板再执行：

| 快捷键 | 行为 |
| --- | --- |
| **Ctrl+Alt+A** | 显示/隐藏右侧 Agent 面板；关闭时焦点回到当前文档 |
| **Ctrl+Alt+F** | 并排 / 专注模式切换 |
| **Ctrl+Alt+N** | 新任务（同一常驻进程新会话，不重启运行时） |
| **Ctrl+Alt+H** | 打开/关闭历史抽屉并聚焦搜索框 |
| **Ctrl+Alt+M** | 模型配置 |
| **Ctrl+Alt+S** | 停止当前任务（结束时停掉常驻运行时） |
| **Ctrl+L** | 把焦点移到 Agent 输入框（面板关闭时先打开） |
| **Enter** | 发送；**Shift+Enter** 换行；**Ctrl+Enter** 仍可发送（兼容旧习惯） |
| **Esc** | 在面板内先关闭历史抽屉；否则把焦点交还文档，不关闭面板 |

焦点与状态约定：打开面板自动聚焦输入框；收起面板或按 Esc 交还焦点；审批批准/拒绝后焦点回到输入框；历史打开后自动聚焦搜索框，搜索框 **Enter** 打开第一条结果、**↑/↓** 在结果间移动；任务进行中执行「新任务/切换历史」会给出中文提示而不是静默失败。头部按钮的提示文字带快捷键。

- 权限三档：**只读** / **每次确认**（默认） / **自动编辑工作区**。单文件范围始终逐次确认；自动编辑只对主动导入的目录生效，切回只读或每次确认会撤销未执行的自动提案；退出不记住自动授权。
- 工具上限沿用：读取 256 KiB 字符、写入 512 KiB、列表 200 项、搜索 200 文件/50 结果；`edit_file` 唯一精确匹配（CRLF 归一化、`replace_all`），`create_file` 仅工作区且拒绝已存在目标。
- 受保护路径：`模型.txt`、`.env`、私钥、`.git`、`agent-history`、`agent-data`、运行时目录等；没有 shell、删除、重命名、任意网络或插件安装工具。文件能力隔离是应用级策略，不声称 Windows AppContainer 级沙盒。

## 验证证据（本机实测）

本机合成模型（loopback OpenAI 兼容 SSE）+ 真实 WPF 窗口 + 真实 `sdk-minimal` 运行时，见 `artifacts/metrics/sdk-qa-timing.txt`、`sdk-qa-pass.txt`、`sdk-qa-final.txt`：

| 项目 | 实测（0.10.1 最终回归） |
| --- | --- |
| 首次打开面板（冷启动 initialize + 面板就绪） | **1396 ms**（生产顺序「先启动运行时→后打开文件」为 1343 ms；历史运行在 1.4–1.8 s 区间） |
| 热发送受理 / 整轮 | **5 ms** / **202 ms**（常驻进程，PID 不变） |
| 新任务（同 PID，创建→可发送） | **34 ms** |
| 历史恢复（打开会话→可发送） | **97 ms** |
| 关闭运行时 / 窗口关闭总耗时 | **55 ms** / 窗口关闭见 E2E 记录 |
| 退出残留 | 无 WebView2 进程；本进程 Node 子进程已退出 |
| 原生 E2E | **16 条 PASS**（读、审批拒绝不落盘、批准落盘、缓冲区原地更新、范围外拒绝、历史恢复、窄窗口 820/390 DIP、生产顺序等） |

回归：`scripts/test.ps1` 全绿，**Core 104 + UI 85 + 系统选择器 4 + 脚本 8 = 201 项通过，0 失败**；`node --test tests/harness.test.mjs` **15 项通过**；完整输出 `artifacts/metrics/verification-0.10.1.txt`。截图 `artifacts/ui/agent-native-*.png`（空状态、审批、写入、历史、四主题、820/390 DIP）。

## 打包与运行

- `build.ps1` 会把 `src/Harness/*.mjs` 全部复制到 `build/harness-runtime`；打包前必须执行 `scripts/prepare-harness.ps1 -Offline` 重新生成 `runtime-files.json`，否则新的 `sdk-*.mjs` / `native-bridge.mjs` 不在清单里，`package.ps1` 会拒绝。实测清单 24,124 个文件、便携包内包含 `app/Harness/sdk-launcher.mjs`、`sdk-composition.mjs`、`native-bridge.mjs` 等。
- 便携 ZIP 只从白名单暂存树生成：包含 `轻页.exe`、`Harness/`、文档、许可与示例；不含 UiTests/PickerTests/ExperienceHarness、`preferences.xml`、`agent-data`、`agent-history`、`模型.txt` 或构建缓存。
- 0.10.0 实测便携 ZIP 约 **119.6 MiB**、解压目录约 **382.9 MiB**；准确字节数与 SHA256 见 `release/package-info.txt` 和 `release/SHA256.txt`（不沿用旧版数字）。
- 用户运行时无需安装 Node/npm，不联网安装依赖；`Harness/` 已内置 Node、官方运行时与 `runtime-files.json`。Agent 不再需要 WebView2；网页/PDF/SVG 预览仍依赖系统 WebView2。

## 限制与未测量项

- **真实外部模型端点的延迟、流式兼容性与资源占用未测量**；本机合成模型验证的是真实 SDK/HTTP/工具/审批链路，不代表 DeepSeek 线上性能。
- SDK 线协议不转发逐 token 增量，只有 `assistant/message` 完成事件；面板在回复期间显示状态，完成后一次性渲染助手 Markdown。
- 没有 `session/cancel`；「停止」= 结束进程，下次发送重启并按需 resume，因此停止后首条消息会包含一次冷启动。
- 固定 `sdk-minimal` 树与 `@deepseek-ai/dsh@0.2.1-rc.5`；升级上游需复核 overlay 行 id、事件名与 JSONL 格式。
- `sdk-approval-bridge.mjs` 只是备用实现，未挂载；若未来改走 SDK 原生审批，必须同时移除 TCP 审批，避免双重弹窗。
- 0.8–0.9 的 WebView 代码与 `AgentSkin` 仅保留历史回归，不随新功能维护，且不在打包运行时路径上。
- 本文件记录的是 2026-10-30 本机实测；发布包内容与哈希以 `release/` 的实际产物为准。
