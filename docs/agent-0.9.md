# 轻页 0.9：Cline 式 Agent 面板与文件工具增强

> **历史文档（已退役）**：0.9 的 WebView2 承载方案已被 0.10 的「原生 WPF 面板 + DSH SDK 常驻进程」取代，当前实现见 [0.10 原生 SDK 客户端](agent-0.10.md)。本文件保留当时的设计与证据，不再代表生产路径。

0.9 把右侧内置 Harness 从“嵌进来的完整 IDE 页面”收敛为纯对话侧栏，并补齐精确编辑类工具。本轮不更换固定版本（`@deepseek-ai/dsh@0.2.1-rc.5`、Node 24.18.0），不修改下载的上游包，只调整定向组合、WebView 注入皮肤、原生文件工具与测试。集成、权限与内存边界的完整说明仍见 [Harness 集成记录](harness-integration.md)。

## 现场基线与问题

0.8 的面板承载的是官方 `web` profile：除了对话，还渲染 Harness 自己的左侧导航、会话头、工作区选择、模型/预设选择、右侧抽屉和首页品牌。轻页本身已有左侧文件栏和中间原生阅读/编辑器，因此面板里出现第二套导航与工作区管理，既挤占宽度，也让“当前文件或导入目录”这一原生范围在 UI 上产生歧义。

同时，0.8 的文件工具只有整文件 `write_file`：模型做一处小改动也要提交完整新文，审批卡片展示全文，既慢也容易覆盖无关内容。

## 面板改造

### 定向组合裁剪

`src/Harness/composition.mjs` 在官方组合上追加一份 `disabled` 清单（57 项），关掉会话管理、设置页、插件管理、终端与文件导航、工作区/模型选择、品牌、反馈、任务/计划/技能/子代理等与“纯对话 + 工具 + 审批”无关的插件。被 `ui-workspace`、`ui-settings`、`ui-sidebar-right`、`ui-trajectory` 等仍被其它客户端插件 `inject` 的 UI 不能直接禁用，它们的界面由皮肤隐藏。会话仍是内存后端（`qingye-memory-storage`），未开启磁盘 JSONL 或 spill。

裁剪只影响“显示哪些 UI 面板”，不改动会话循环、工具执行管线、审批、上下文压缩和 SSE。guard 仍然单调拒绝未开放工具。

### 皮肤注入

新增 `src/UI/AgentSkin.cs`：

- 通过 `AddScriptToExecuteOnDocumentCreatedAsync` 在 document-start 注入有序脚本，首个文档渲染前就装上 `<style>` 并写入初始调色板，避免闪一下官方深色/品牌界面。
- 注入纯本地 CSS/JS：不改导航、不放宽资源栅栏、不发网络请求。
- `MutationObserver` 在 React 重渲染后重扫；同一脚本再次执行时以 `window.__qingyeAgentSkin` 幂等。
- 原生 `Appearance` 的 `SurfaceBrush`/`PanelBrush`/`TextBrush`/`LineBrush`/`AccentBrush` 等被映射为页面实际的 `--dsw-alias-*` / `--dsw-specific-*` 设计令牌，跟随薄荷、暖白、浅紫、深色四套主题热切换；`data-ds-dark-theme` 与 `color-scheme` 同步。
- 去壳方式按“槽位/DOM 属性 + 少量 CSS 模块类名兜底”：折叠左侧栏列但保留网格占位，移除右侧抽屉、会话头拖拽条、品牌槽、工作区/模型选择按钮、token 统计与底部 shell；首页状态只保留底部输入框，并给出一条 12px 的范围提示。
- 批准卡片的多行 diff 强制 `white-space: pre-wrap`，避免长 diff 被合并成一行。

面板头部仍是原生 WPF：`轻页 Agent` / `已连接` / `新任务`，不再有历史会话按钮。`Theme.cs` 为 ComboBox/ComboBoxItem 提供自绘模板（Border + ContentPresenter + 箭头 Path + Popup；Aero2 默认模板不识别 `Background`，原先白底即由此而来），深色主题下“文件权限”下拉与面板同底色、文字使用 `TextBrush`；`UiTests` 增加“ComboBox closed state follows the dark palette”断言，校验闭合态模板取 `SurfaceBrush`/`TextBrush`。

### 面板边界

- 面板**没有**独立导航、工作区管理、模型或预设选择、账户、设置页和历史会话入口；工作区由“当前文件”或用户主动导入的目录决定。
- 渲染仍是本机 loopback 页面 + InPrivate WebView，资源只允许自身来源与内联 `data/blob`；关闭、“新任务”或退出沿用 0.8 的停止进程与临时目录清理。
- 皮肤依赖固定版本页面的 DOM 结构与少量 CSS 模块类名。升级 Harness 版本时需要重新核对选择器；这是本轮明确的维护成本，不以“兼容任意版本”表述。

## 文件工具增强

原生工具定义在 `src/Core/AgentTools.cs` / `src/Core/HarnessNativeTools.cs`，页面侧适配在 `src/Harness/native-tools.mjs`。

### `edit_file`（新增，优先于整文件写入）

- `old_text` 必须在最新文本（优先用打开中的编辑器缓冲区）中**唯一精确匹配**；多处匹配且未设 `replace_all=true` 时拒绝，并报出出现次数。
- 未找到时返回中文错误，提示先用 `read_file` 核对原文的空格、缩进与换行符。
- 读取按 LF 归一化，因此从 `read_file` 复制出的片段也能匹配 CRLF 文件：归一化匹配成功时按文件自身换行风格写回，不整文件转换。
- `replace_all=true` 替换全部匹配；`new_text` 为空表示删除这段文字。
- 仍要求普通文本、非未保存缓冲区、只读权限拒绝、写入后文件不超过 512 KiB；写入仍是提案，需批准。
- 单文件范围下 `edit_file` 仍逐次确认（与 `write_file` 一致）。

### `create_file`（新增）

- 仅工作区模式；目标已存在时拒绝并提示改用 `edit_file`/`write_file`；父目录必须已存在（与 0.8 的 `write_file` 新建规则一致）。
- 单文件模式不可用；内容不超过 512 KiB；批准后才落盘。

### `list_files` / `search_files`（增强）

- `list_files` 增加 `depth` 1–6：`1` 只列当前目录，`2` 含直接子目录文件；`depth=0` 沿用 `recursive` 语义（默认只列一层，递归时深层）。返回的 `depth` 字段是实际生效层数，最多 200 项。
- `search_files` 增加 `regex=true`（不区分大小写、1 秒匹配超时、模式长度 ≤500）与 `context` 0–10（返回匹配行前后若干行）；预算仍是最多 200 个文件、50 条结果、单文件 1 MiB。

### 审批 diff 有界

`write_file`/`edit_file`/`create_file` 的审批卡片不再显示全文，改为有界 `+/-` 行式差异：最多 60 行、8000 字符，单行 300 字符，最多各显示 20 行增删，前后各 3 行上下文；超出时追加“已截取显示，请在工具参数中核对完整内容”。新建文件只列 `+` 行。完整参数仍在工具调用里对模型可见，截断只影响读者。

## 权限与内存边界

本轮没有放宽 0.8 的边界：

- 默认单文件；主动导入目录才允许工作区；`..`、绝对路径、目录链接、切换 UI 都不能扩大范围；切换范围结束旧运行并清空旧对话。
- 只读 / 每次确认 / 自动编辑工作区三档保留；单文件始终确认；退出不记住自动授权；撤回权限使旧自动提案失效。
- `模型.txt`、`preferences.xml`、`.env`、私钥、`.git`、历史、运行时私有目录受保护；没有 shell、删除、重命名、任意网络或插件安装工具。
- 读取 256 KiB 字符、写入 512 KiB、列表 200 项、搜索 200 文件/50 结果预算不变；读取支持按行翻页并使用实时缓冲区。
- 会话、草稿、任务、附件引用只在内存；每份临时日志 8 MiB、最多 32 个临时会话，超预算要求清空；关闭 spill 与磁盘长输出。InPrivate + 每次运行随机子目录，清空/正常退出停止 Harness、释放浏览器进程并删除自己的目录。

## 验收证据

- 完整回归 `scripts/test.ps1`：**Core 94 + UI 76 + 系统选择器 4 + 脚本 8 = 182 项通过，0 失败**；另有 `node --test tests/harness.test.mjs` **8 项通过**，Harness QA **9 条 PASS**（含 820/390 DIP 布局、真实官方运行时读写与重启、内存清理、资源拒绝、大输出不落盘）。原始输出 `artifacts/metrics/verification-0.9.0.txt`。
- 文件工具 RED/GREEN：`artifacts/metrics/agent-edit-tool-red.txt` / `agent-edit-tool-green.txt`，diff 与报错样例 `agent-edit-tool-diff-sample.txt`。
- 面板 QA：`agent-panel-flow-final.txt`、`agent-panel-empty-final.txt`、`agent-panel-dep-check.txt`、`agent-panel-build-final.txt`、`harness-layout.json`（380×607 视口下网格为 `0px 380px 0px`，首列为零宽、无右侧栏）。
- 截图：`artifacts/ui/harness-agent-light.png`、`harness-agent-dark.png`（四主题皮肤与深色下拉）；既有人工核对截图未删除。
- 打包：脚本只改 `scripts/package.ps1` 的 docs 白名单一处（把 `agent-0.9.md` 加入便携包 docs），不改版本与打包逻辑；ZIP 仍只从允许清单生成，不含 UiTests/PickerTests/ExperienceHarness、`preferences.xml`、`agent-history`、`模型.txt` 之外的用户文件。本轮实测便携 ZIP 约 119.5 MiB（解压目录约 382.8 MiB），准确字节数与 SHA256 见 `release/package-info.txt` 与 `release/SHA256.txt`。

## 限制与未测量项

- 真实外部模型端点的延迟、流式兼容性与资源占用**未测量**；Harness QA 使用本机合成本地模型，验证的是官方运行时与 HTTP/SSE/工具/审批链路，不代表 DeepSeek 线上性能。
- 皮肤绑定固定 Harness 版本的 DOM 结构与部分 CSS 模块类名；上游 UI 重构会使部分隐藏/配色选择器失效，需要随版本升级复核。
- `edit_file` 是精确文本替换，不做模糊匹配、行号编辑或补丁语法；多处匹配必须提供唯一上下文或显式 `replace_all`。
- 审批 diff 有界截断；读者看到的是变化摘要，完整内容以工具参数为准。
- 面板不提供历史会话、项目索引、终端或运行/测试能力；对话仍是单次运行内存态，退出或清空后不可恢复。
- 没有测量或承诺 Windows AppContainer 级别的程序执行沙盒；文件能力隔离是应用级策略。
