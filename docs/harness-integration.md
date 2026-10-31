# 内置 Harness SDK 集成（0.11）

轻页的 Agent 是内置的官方 DeepSeek Harness 运行时：固定 `@deepseek-ai/dsh@0.2.1-rc.5` 与 Node 24.18.0，以 **`sdk-minimal` profile + Cordis 补丁 overlay** 启动一个常驻本机子进程，通过 **stdio 换行 JSON-RPC 2.0** 与 WPF 原生面板通信。0.10 起不再用 WebView2 承载官方 Web GUI；0.11 进一步换成纯内存临时会话、当前轮取消、精简运行时依赖闭包，见 [0.11 Agent 稳定性与轻量化](agent-0.11.md)。

0.8–0.9 的 WebView2 Web 方案（`web` profile + 页面内注入皮肤 + InPrivate 浏览器）**已退役**，代码仅作历史回归保留，见文末「历史方案」。

## 集成方式

固定官方运行时与 Node 版本，使用官方 `sdk-minimal` 组合和定向补丁。上游 CLI、会话循环、模型流、工具执行管线、任务与事件实际运行；轻页没有重写 Agent 循环，也没有修改下载的上游包。

运行时进程由 C# 按需启动并按需常驻：

- `src/Harness/sdk-launcher.mjs`：清洗继承环境（去掉 `DSH_*`、`*_API_KEY` 等）、把 stdout 留给 JSON-RPC、诊断写 stderr 并脱敏密钥/桥令牌；生成 `agent-data/qingye-sdk.patch.json` 后以 `--profile sdk-minimal --patch <overlay>` 导入官方 CLI。
- `src/Harness/sdk-composition.mjs`：定义 profile 名、禁用的 16 组默认行（含官方 `sdk-jsonrpc-server`、`tool-fs`、`tool-working-directory`、终端/持久 shell、`compaction-basic`、`image-offload`、`mcp-resources`、`jobs`、未使用的 DeepSeek 产品适配器等），插入 `llm-pi-ai` 模型适配器（provider `qingye-model`，密钥只读环境变量 `QINGYE_MODEL_KEY`；裁剪后以 `file:` URL 直接指向适配器入口，避免依赖被裁掉的安装闭包）、persona 与 `native-tools` 插件；生产会话由 `session-memory.mjs` 提供纯内存后端，`QINGYE_SESSION_STORAGE=jsonl` 仅用于旧版兼容夹具。
- `src/Harness/sdk-native-server.mjs`：在官方 `HarnessSdkJsonRpcServer` 与 `JsonRpcLineTransport` 之上增加轻页命令：`qingye/session/cancel`（只取消当前轮、保留进程）、`qingye/session/delete`、`qingye/sessions/clear`；公共文本增量按 40 ms / 2048 字符合并转发，不转发推理内容。
- `src/Harness/session-memory.mjs`：会话与 KV 的纯内存实现（每份 8 MiB、最多 32 个临时会话），不创建 JSONL 或 SDK 会话目录。
- `src/Harness/native-tools.mjs`：6 个文件工具（`list_files`、`read_file`、`search_files`、`write_file`、`edit_file`、`create_file`）；实现与 0.9 一致，传输改为通用 native bridge。
- `src/Harness/native-bridge.mjs`：安装进程级 bridge；SDK 路径是 TCP 客户端（首行 `{"token":...}`，一行一请求、按 id 匹配，可取消、按操作设超时）。退役 WebView 使用的行长适配器 `createLineBridge` 仍保留。
- `src/Harness/sdk-approval-bridge.mjs`：备用插件，把 SDK 的 `approval/request` waterfall 转成 stdout 请求；0.10 **不挂载**（审批走 TCP 桥），挂载会造成重复审批。

C# 侧：`src/Core/SdkRuntime.cs`（JSON-RPC 通道、进程生命周期、共享常驻实例、TCP 桥 server）、`src/Core/SdkAgentController.cs`（事件映射、新会话/续聊、审批、停止）、`src/UI/AgentPanel.cs`（原生面板）、`src/UI/DocumentSync.cs`（写入后的编辑器缓冲同步）。运行时定位仍由 `src/Core/HarnessRuntime.cs` 与 `ProcessLifetime.cs` 负责。

参考：[官方仓库与许可证](https://github.com/deepseek-ai/deepseek-harness)、[架构与扩展接口](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/architecture.md)。源码主分支仍在快速迭代，实际交付以 npm 锁文件的发布版为准，未直接跟随 master。

## 进程协议

启动环境变量：`QINGYE_MODEL_BASE_URL`、`QINGYE_MODEL_NAME`、`QINGYE_MODEL_KEY`、`QINGYE_APP_HOME`、`QINGYE_BRIDGE_PORT`、`QINGYE_BRIDGE_TOKEN`，以及 `DSH_HOME`、`DSH_TELEMETRY_DISABLED=1`、`DSH_PERMISSION_MODE=workspace-write`。

JSON-RPC（stdin/stdout，一行一帧）：

- C# → Node：`initialize`、`session/prompt`、`session/resume`、`session/working-directory/set`、`shutdown`。
- Node → C#：`session.event`（`user/message`、`assistant/message`、`tool/call`、`tool/result`、`approval/asked|decided`、`todo/write`、`turn/start|end`、`step/start`、`session/title` 等）、`session.status`、错误帧。

`src/Core/SdkRuntime.cs` 的单行帧上限 8 MiB，默认请求超时 60 s；`initialize` 每个进程只做一次，后续控制器复用常驻实例。事件在 `SdkAgentController` 中映射为原生 UI 已有的 `AgentActivity`/`AgentSession` 结构。

## 文件沙盒与权限

- 默认单文件；主动导入的目录才允许新建/修改文件，仍要求父目录已存在。模型不能通过相对路径、绝对路径、目录链接或切换 UI 扩大范围；切换范围结束旧运行并重新绑定当前文件/工作区。
- 只读 / 每次确认 / 自动编辑工作区三档。默认每次确认；单文件始终确认；退出不记住自动授权。原生层再次检查权限，撤回只读或自动→确认都会使旧自动提案失效。
- 读取 256 KiB 字符、写入 512 KiB、列表 200 项、搜索 200 文件与 50 结果预算不变；读取支持按行翻页并使用当前编辑缓冲区。写入复核未保存编辑、存在状态与 SHA256，原子替换后刷新编辑器与阅读内容。`edit_file`（唯一精确匹配、CRLF 归一化、`replace_all`）、`create_file`（仅工作区、拒绝已存在目标）、`list_files`（`depth` 1–6）、`search_files`（正则与上下文行）沿用 0.9；审批差异是有界 `+/-` 行式（最多 60 行 / 8000 字符，超出截取显示，完整内容仍在工具参数中）。
- `模型.txt`、`.env`、私钥、`.git`、`agent-history`、`agent-data`、运行时私有数据和实际执行中的 Harness 代码目录受保护。工具不包含 shell、删除、重命名、任意网络或插件安装；guard 在官方工具执行管线单调拒绝未开放工具。这是应用级文件能力隔离，不声称提供 Windows AppContainer 或任意程序执行沙盒。

## 审批与编辑器同步

审批不依赖 SDK 线协议（固定的 `sdk-jsonrpc-server@0.2.1-rc.5` 没有 `approvalRequests` 桥）：native-tools 的写工具先通过 TCP 桥 `tool` 拿到包含 diff 的提案，再发 `approval`；编辑器弹原生 diff 卡，批准才 `apply`，拒绝则 `discard` 并返回中文「用户未批准」。桥超时（默认 10 分钟）、断连、提案缺失或归属不符一律按拒绝处理（fail-closed）。

写入落盘后 `src/UI/DocumentSync.cs` 更新已打开文件的编辑器缓冲：干净缓冲区通过一次可撤销的替换接收磁盘文本，光标/选区/滚动保持；脏缓冲区不被覆盖；正在原位编辑的 Markdown 只提示核对。

## 会话、历史与清理

- DSH 侧会话以未压缩 JSONL 存于程序目录 `agent-data/sessions`；轻页侧用 `AgentSessionStore` 把显示条目、标题、范围、草稿原子写入 `agent-history/`（每份 8 MiB、最多 4096 条）。历史面板可列表、搜索、删除、清空与续聊。
- 新任务在同一个常驻进程内创建新会话，不重启运行时；续聊先 `session/resume`，再按当前文件/工作区重绑工作目录。
- 关闭面板不停止运行时；退出轻页时停止 Node 子进程（实测 35 ms），不刷新对话。停止任务会结束进程并标记 stopped，下次发送重启并按需 resume。
- `agent-data/` 与 `agent-history/` 不进入便携包；`preferences.xml` 例外地由外观设置显式选择是否写入。应用不写最近文件、文档备份、文本缓存或工作区配置，不递归删除用户工作区。
- 聊天记录是本机文件，删除历史或退出不会撤销已经批准的写入。

## 构建与分发

原生构建链和 AvalonEdit/Markdig/WebView2 版本保持不变，不需要 .NET SDK。开发首次执行 `scripts/prepare-harness.ps1`，通过 npm lock 恢复依赖且禁止生命周期脚本，并校验 Node 官方 SHA256；后续可以 `-Offline`。`build.ps1` 会把 `src/Harness/*.mjs` 全部同步到 `build/harness-runtime`，之后必须重新执行 `scripts/prepare-harness.ps1 -Offline` 生成 `runtime-files.json`，否则新增的 `sdk-*.mjs`/`native-bridge.mjs` 不在清单里，`package.ps1` 会失败。用户拿到包后无需 npm 或 Node 安装，也不联网安装依赖。

运行时只包含 SDK 实际依赖闭包和生产文件，不包含类型声明、source map、Web/客户端 UI/Desktop 包与未启用的 LibreOffice 转换组件；原生 Office 阅读/导出不受此裁剪影响。`scripts/harness-sdk-packages.mjs` 计算闭包，`runtime-files.json` 是固定运行文件清单（0.11 为 10,659 个文件），打包校验来源和哈希，不把 app 中个人文件、模型配置或任何对话数据收进去。读写器本体仍有 15 MiB 上限；完整包另有 512 MiB 上限。0.11.0 实测便携 ZIP 约 69.6 MiB、解压目录 约 212.4 MiB；相比 0.10.1 的 119.58 / 382.90 MiB 约减少 42% / 45%。准确字节数与 SHA256 见 `release/package-info.txt` 和 `release/SHA256.txt`。

## 验证

基础回归：`scripts/test.ps1`；插件/运行时测试：`build/harness-runtime/node.exe --test tests/harness.test.mjs`；真实 WPF + 官方 `sdk-minimal` 运行时 + 本机合成模型：`build/bin/ExperienceHarness.exe harness flow restart large`（旧版兼容）与 `harness-memory`（生产临时会话、取消、清空、切换范围、820/390 布局、退出清理）。

0.11 完整回归 `scripts/test.ps1`：**Core 117 + UI 94 + 系统选择器 4 + 脚本 8 = 223 项通过，0 失败**；`node --test tests/harness.test.mjs` **20 项通过**；旧版兼容 E2E **16 条 PASS**，生产内存 E2E **7 条 PASS**（冷启动就绪 1016.8 ms、空闲工作集 115.9 MB、审批中停止 19.1 ms、热发送到审批 187.1 ms、新任务 3.0 ms 同 PID、无 WebView2/node 残留）。输出 `artifacts/metrics/verification-0.11.0.txt`，计时见 `artifacts/metrics/optimization-native-measured.txt`；截图在 `artifacts/ui/agent-optimized-*.png` 与 `artifacts/audit-0.10.1/`。

真实外部端点的兼容性、延迟、启动时间、内存和 CPU **未测量**。本机合成模型验证真实官方 SDK、JSON-RPC、工具与审批链路，不代表 DeepSeek 线上性能。最终完整回归数量和交付大小以本轮验收输出为准。

## 历史方案：0.8–0.9 WebView2 Web GUI（已退役）

0.8 首次内置官方 Harness `web` profile：Node 启动本地 HTTP 服务，WebView2 在右侧承载官方 Web 页面，原生文件工具经匿名管道/自定义桥接回编辑器；0.9 为把页面收敛成 Cline 式侧栏，新增 `src/UI/AgentSkin.cs` 在 document-start 注入 CSS/JS（折叠导航、隐藏工作区/模型选择、映射四套主题），并禁用 57 项无关插件。该方案的会话与 KV 是内存后端，对话不落盘。

0.10 因启动速度（面板约 5 s、新任务/换范围 4–6 s）、DOM 皮肤维护成本与历史缺失而改为 SDK/stdio 原生方案。以下代码保留但**不在生产路径**：`src/Harness/launcher.mjs`、`src/Harness/composition.mjs`、`src/UI/HarnessPanel.cs`、`src/UI/AgentSkin.cs`、`src/UI/HarnessBrowserCleanup.cs`、`src/Core/HarnessRuntime.cs` 的 Web 启动分支、`native-bridge.mjs` 的 `createLineBridge`。它们只用于历史回归或兼容入口；生产面板不会构造 WebView2、不启动本地服务、不注入页面样式、不创建浏览器临时目录。0.9 的历史设计记录在 [0.9 Agent 面板与工具增强](agent-0.9.md)。
