# 轻页 0.11：Agent 稳定性、临时会话与精简安装包

0.11 处理 0.10.1 审查发现的三个 P0（运行时/数据保护、范围切换、临时会话与清理），并完成安装包裁剪。固定版本仍是 `@deepseek-ai/dsh@0.2.1-rc.5`（`sdk-minimal` 树）与 Node 24.18.0；没有修改上游包、升级依赖或提交发布。

## 为什么做

0.10.1 的审查（`artifacts/audit-0.10.1/review.md`）确认了可用性问题之外的策略缺口：

- 导入软件所在目录后，文件工具能为 `app/Harness/native-tools.mjs` 生成修改提案；运行时代码与数据目录没有进入保护范围。
- 切换文件/目录后，旧会话范围仍然生效，自动编辑权限也没有复位。
- 面板历史与未压缩 JSONL 会话默认写入程序目录，与“临时会话、退出即清空”的定位不一致；清空只删显示条目，不删 SDK 上下文。
- 停止会结束整个常驻进程；没有“只取消当前轮”的能力。

## P0：运行时与数据保护

- `AgentWorkspace` 新增 `Protect(...)`；生产控制器创建范围时统一追加受保护根：实际执行的 Harness 目录、运行时数据目录、`agent-data`、`agent-history`、模型配置目录等。
- 读、列举、搜索、写提案、恢复会话都走同一策略；受保护路径返回中文拒绝，不会进入审批。
- 路径检查同时覆盖单文件模式的重解析点祖先，目录链接无法绕过。

## P0：切换范围立即撤销旧授权

- `AgentPanel.Scope.cs` 在编辑器当前文件/工作区变化时：取消旧任务、撤销未执行提案、丢弃旧控制器与权限（自动编辑回到每次确认）、清空旧上下文，再按当前范围重新绑定。
- 同一目录内切换文件保留同一会话范围；切换到另一个目录必须重新授权。单文件模式仍逐次确认。
- 生产面板的会话范围不再因历史恢复而静默扩大；恢复失败给中文提示，不伪造上下文。

## P0：临时会话与完整清理

- `session-memory.mjs` 提供纯内存的会话与 KV 后端：默认不写 JSONL、不写 SDK 会话目录；`QINGYE_SESSION_STORAGE=jsonl` 只保留给旧版兼容性验收夹具。
- 面板历史使用内存模式的 `AgentSessionStore`（32 条、每份 8 MiB 上限），退出或清空即失效；输入草稿也只留在内存。
- 清空/删除/切换范围会先取消待处理审批，再调用 SDK 侧的 `qingye/sessions/clear`、`qingye/session/delete`（仅内存会话），并释放每次运行随机生成的运行目录；不会重启常驻进程。
- 旧版存档（`agent-history`、`agent-data`）不自动读取、不自动删除；面板提供显式“清理旧版存档…”入口，只删这两类目录且拒绝目录链接。
- 每次运行使用 GUID 临时运行目录，正常退出时经既有守卫清理；`app/agent-data` 等用户已有内容保持原样。

## 停止、审批与并发

- 新增当前轮取消：SDK 侧 `qingye/session/cancel`，C# `CancelCurrentTurnAsync` 只取消当前轮并等待其结算，常驻进程 PID 不变；窗口退出/`ShutdownAsync` 才结束进程。
- 审批中的“停止”取消 C# 审批等待与 Node 侧等待，不落盘、不重启；审批中的“清空/删除/切换文件”也先取消审批再清理上下文。
- 迟到的事件/回调按未绑定或已撤销的会话丢弃，并在 Dispatcher 执行时再次核对会话身份；自动写提案的最终发布在策略锁内完成。

## 轻量化安装包

- 新增 `scripts/harness-sdk-packages.mjs`：从固定 CLI 与 SDK 根（`sdk-minimal`、`llm-pi-ai`、`sdk-jsonrpc-server`、`sdk-protocol`、`session-persistence`、`storage`、`tools`、`cordis` 等）计算最小依赖闭包；`copy-harness-runtime.mjs` 只复制闭包内文件，排除 Web/客户端 UI/Desktop/Office 转换包与类型声明、source map。
- 运行时清单从 **24,124** 文件降到 **10,659** 文件；`node_modules` 从源码树 690.9 MiB 降到 119.0 MiB；`app/Harness`（含 node.exe）约 209.4 MiB。
- **模型适配器修复**：裁剪后固定 CLI 的依赖图不再暴露 `dsh-llm-pi-ai` 的安装范围，启动时出现 `no adapter registered for provider "qingye-model"`。`sdk-composition.mjs` 改为用 `file:` URL 直接插入 `@deepseek-ai/dsh-llm-pi-ai/lib/index.js`（测试固定断言），适配器不再依赖可选安装闭包；裁剪树与便携包内均可正常启动。
- 打包前由 `scripts/test.ps1` 在构建后重新生成 `runtime-files.json`；`prepare-harness.ps1` 复制全部自有 `src/Harness/*.mjs` 再生成清单。

## 界面与操作

- 820 DIP 保持文档与 Agent 并排（不再强制专注）；390 DIP 进入专注模式仍可输入；分隔线可拖动。
- 输入区压缩：权限下拉与状态同一行，改动摘要默认折叠；审批区保留宽差异与明确动作。
- “本次改动”成为可核对结果：按文件合并展示、可查看本轮差异、可带冲突检查撤销（新建文件撤销为删除；文件后续被修改时拒绝）。
- 源码选区可引用为本次上下文（最多 16 KiB），标签可移除；历史标签改为“本次会话/临时会话”，与不留档策略一致。
- 诊断计时默认关闭，显式开启时只写应用内目录（环形 64 条、单文件 256 KiB 上限），不再从程序目录推导源码工程路径。

## 验证（本机实测，合成模型）

`scripts/test.ps1` 一次运行覆盖：构建 → CoreTests → UiTests → Picker → `node --test` → 旧版兼容 E2E → 生产内存 E2E → 脚本/打包检查。

| 检查 | 结果 |
| --- | --- |
| 核心 | **117 通过，0 失败** |
| 原生 UI | **94 通过，0 失败** |
| 系统选择器 | **4 通过，0 失败** |
| `node --test tests/harness.test.mjs` | **20 通过，0 失败** |
| 旧版兼容 E2E（`harness flow restart large`） | 16 条 PASS |
| 生产内存 E2E（`harness-memory`） | 7 条 PASS |
| 打包/版本/守卫脚本 | **8 通过，0 失败** |

生产内存 E2E 覆盖：停止审批中的当前轮并保持 PID、会话不落盘、删除 SDK 上下文不重启、审批中切换文件撤销旧范围、审批中清空并保留干净运行时、820 并排与 390 可用、退出释放运行目录与进程、无 WebView2。

实测（本机合成模型）：冷启动就绪 **1016.8 ms**；空闲工作集 **115.9 MB**；审批中停止 **19.1 ms**；热发送到审批约 **187.1 ms**；新任务 **3.0 ms**（同 PID）。

## 打包与限制

- 0.11.0 便携 ZIP **约 69.6 MiB**，解压目录 **约 212.4 MiB**；相比 0.10.1（119.58 / 382.90 MiB）分别约 **-41.8%** 与 **-44.5%**。准确字节数与 SHA256 见 `release/package-info.txt`、`release/SHA256.txt`。
- ZIP 内含 `app/Harness/sdk-launcher.mjs`、`sdk-composition.mjs`、`sdk-native-server.mjs`、`session-memory.mjs`、`native-bridge.mjs`、`native-tools.mjs`；不含 Web/客户端 UI/Office 转换包、测试程序、偏好、`agent-data`、`agent-history` 或 `模型.txt`。解压后的运行时已用独立启动探针验证 `initialize` 成功、模型适配器可加载。
- 真实外部模型端点的延迟、流式兼容性与长期内存占用**未测量**；本机合成模型只验证协议、工具、审批、取消与清理链路。
- 仍固定 `@deepseek-ai/dsh@0.2.1-rc.5`；升级需复核裁剪根、overlay 行 id、事件名与内存后端接口。包内仍包含 Node 24 可执行文件（原生 Harness 运行时所需）。
- 0.8–0.9 的 WebView 方案代码保留为历史回归入口，不在生产路径；网页/PDF/SVG 预览仍使用系统 WebView2。
