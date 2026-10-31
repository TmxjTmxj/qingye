# 项目目录

下表对应源码项目文件夹。该文件夹根目录双击 `轻页.lnk` 或 `启动轻页.cmd` 启动最新版；便携ZIP仅包含可运行的 `app/`，解压后直接双击其中的 `轻页.exe`。

| 位置 | 内容 |
| --- | --- |
| `app/` | 当前可运行程序及同目录DLL；保留完整文件夹 |
| `app/Harness/` | 固定官方 Harness、便携 Node、SDK 启动器（`sdk-launcher.mjs`、`sdk-composition.mjs`、`sdk-native-server.mjs`、`session-memory.mjs`、`native-tools.mjs`、`native-bridge.mjs`）、按 SDK 依赖闭包裁剪的 `node_modules`、`runtime-files.json` 及许可证；不能单独移除 |
| `app/agent-data/` | 旧版本遗留的 Agent 运行数据（如存在）；当前版本生产会话只留在内存，不写入新 JSONL，不进入便携包；可在面板“清理旧版存档…”显式删除 |
| `app/agent-history/` | 旧版本遗留的面板历史条目（如存在）；当前版本临时会话只在内存，不进入便携包；可在面板显式清理 |
| `app/harness-data/` | 0.8–0.9 WebView 方案遗留（若存在）：浏览器临时目录与旧启动配置；当前版本不读取、不进入便携包，可手动删除 |
| `release/` | 最新便携ZIP、大小和SHA256记录 |
| `release/archive/` | 历史版本，保留其文件与可能的用户修改 |
| `src/` | C#源代码、原始图标SVG和多分辨率程序图标；`src/Harness` 包含 SDK overlay 组合、原生工具/桥、纯内存会话与 npm lock；`src/UI/AgentPanel.cs` 是原生 Agent 面板（SDK 生命周期、会话清理、范围、改动与上下文分别位于同名 `.Storage/.Scope/.Changes/.Context.cs`） |
| `src/UI/AgentPanel.cs` | 原生 Agent 面板：对话、工具卡、审批、临时历史、权限下拉与输入区；快捷键命令在 `src/UI/MainWindow.AgentCommands.cs`，键位分派在 `src/UI/WindowShortcuts.cs` |
| `src/Core/SdkRuntime.cs` | stdio JSON-RPC 通道、Node 进程生命周期、共享常驻运行时、TCP 本机桥 server |
| `src/Core/SdkAgentController.cs` | DSH 事件 → 原生 `AgentActivity`/`AgentSession` 映射；新会话、续聊、当前轮取消、审批与停止 |
| `src/Core/AgentWorkspace.cs` / `AgentChangeSet.cs` | 文件范围与受保护根；本次改动的内存记录、差异与带冲突检查撤销 |
| `src/Core/LegacyAgentArchives.cs` | 只清理旧版 `agent-history`/`agent-data`，拒绝目录链接，不删除已写入文件 |
| `src/UI/DocumentSync.cs` | Agent 写入落盘后原地刷新编辑器缓冲区（保留光标/选区/滚动，可一次撤销） |
| `VERSION` | 当前版本唯一来源；构建与打包脚本共同读取 |
| `scripts/` | 构建、测试、打包、整理脚本及图标生成器；`harness-sdk-packages.mjs` 计算 SDK 依赖闭包，`prepare-harness.ps1 -Offline` 生成固定运行文件清单 |
| `tests/` | 核心文件、真实窗口、实际系统选择器、固定 Harness 插件、原生 SDK 旧版兼容与生产内存端到端及整理/打包回归测试 |
| `docs/` | 设计稿、实现取舍、修复与实测记录；0.11 稳定性/临时会话/精简包见 `agent-0.11.md`，0.10 协议见 `agent-0.10.md`，0.8–0.9 WebView 方案见 `agent-0.9.md`（已退役） |
| `examples/` | 可选演示文件 |
| `licenses/` | 第三方许可 |
| `build/` | 开发构建、`build/bin` 测试程序、`build/harness-runtime` 运行文件与已解压依赖缓存；用户运行只需要app |
| `artifacts/ui/` | 实际界面、系统图标核对截图（0.10 原生面板为 `agent-native-*.png`） |
| `artifacts/metrics/` | 性能采样、回归输出与 RED/GREEN 原始记录 |

整理仅移动旧版本和核对素材、移除可重新生成的包下载副本及乱码构建产物。保留源代码、设计稿、版本历史和已解压依赖，以便后续修改且无需重新下载。不会扫描或整理项目之外的个人文件。

`scripts/organize.ps1` 可重复执行；移动或删除前检查绝对路径、各级父目录与递归内容，拒绝目录链接。`scripts/package.ps1` 使用新的临时清单目录生成 ZIP，不把 `app/` 中额外保存的文件、`preferences.xml`、`agent-data`、`agent-history`、`模型.txt` 打进去；临时打包目录完成后删除。`build/` 是开发缓存，不随便携包分发。修改 `src/Harness/*.mjs` 后，`build.ps1` 会复制到 `build/harness-runtime`，打包前必须重新执行 `scripts/prepare-harness.ps1 -Offline` 刷新 `runtime-files.json`。
