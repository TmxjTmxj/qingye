# 轻页维护验收

依次执行 P0-1、P0-2、P0-3、P1-1、P1-2、PPTX、发布准备。每项新增失败测试后再实现，完成后运行完整 `scripts/test.ps1`，输出保存在 `artifacts/metrics/`。保留现有断言，不升级依赖、不新增依赖、不改名、不提交或远程发布。

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| P0-1 内容主题令牌 | 已通过 | p0-1-red.txt 复现代码背景固定；p0-1-green.txt 完整通过 |
| P0-2 分页解码对齐 | 已通过 | p0-2-red.txt 为 66.23 倍；p0-2-green.txt 为 0.98 倍，首次索引 246.15 ms；完整测试通过 |
| P0-3 有界打开队列 | 已通过 | p0-3-red.txt 复现连续请求丢失；p0-3-green.txt 验证顺序、上限四项、提示与异常恢复，完整通过 |
| P1-1 区域 UserControl / 测试钩子 | 已通过 | p1-1-red.txt 复现；p1-1-green.txt 完整通过；MainWindow 247 行，4 张截图逐像素一致 |
| P1-2 惰性预览文本 | 已通过 | p1-2-red.txt / p1-2-green.txt；10 MiB × 20 次编辑：924.68 → 165.01 MiB，最新输入与完整保存通过 |
| PPTX 只读内容 | 已通过 | pptx-red.txt / pptx-green.txt；13 项针对性检查，完整 110 项通过；仅页标题、正文和表格 |
| 单一版本源 / 发布模板 / 哈希说明 | 已通过 | release-red.txt / release-green.txt；VERSION 驱动构建和包名，ZIP 及包内文件完整核验；ZIP 条目显式使用正斜杠，113 项全绿 |

## 0.5 功能扩展（2026-10-29）

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 左侧栏与类型大纲 | 已通过 | 文件/大纲页签、六类大纲提供者；点击定位源码、阅读标题或原位标题 |
| Markdown 原位编辑 | 已通过 | 受限子集渲染与序列化；真实窗口追加段落后切回源码保留内容；超限或不支持的块拒绝进入 |
| 导出与另存为 | 已通过 | TXT/Markdown/HTML/DOCX/CSV/XLSX/PDF；CSV→XLSX 回读；导出不改源文件；WebView2 写出 PDF |
| 发布校验 | 已通过 | ZIP 条目全部使用正斜杠，SHA256 覆盖 ZIP 与包内文件；完整 122 项通过（56 核心、57 UI、4 选择器、5 脚本） |

范围与限制见 [编辑与导出](editing-and-export-0.5.md)。

## 0.6 Agent（2026-10-29）

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 模型配置 | 已通过 | `模型.txt` 中文标签解析、保存、脱敏；地址/模型/key 校验；实际端点 HTTP OK |
| 工作区沙箱 | 已通过 | 工作区越界、`..`、单文件模式、禁止新建、受保护文件均有测试 |
| 只读/写入工具 | 已通过 | list/read/search/write 提案；读取/写入上限；无 shell/删除/重命名 |
| diff 确认 | 已通过 | 应用/拒绝两条路径；拒绝不修改文件；写入后重新载入打开文件 |
| 接入方式 | 已通过 | 直连 OpenAI 兼容 `/v1/chat/completions`，不启动本地 harness，不占端口；显式 TLS 1.2；真实端点完成 read/list/write 循环并审批 1 次 |
| 回归 | 已通过 | 128 项（61 核心、58 UI、4 选择器、5 脚本）；原始输出 verification-0.6.0.txt |

范围与限制见 [Agent 0.6](agent-0.6.md)。

## 0.7 阅读与交互（2026-10-29）

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| EPUB 原生阅读 | 已通过 | 合成 EPUB 读取元数据/章节/图片；加密 EPUB 拒绝；UI 阅读不启浏览器 |
| 工作区搜索 | 已通过 | 核心搜索命中/限制/跳过二进制；UI 导入工作区后出现勾选并返回结果 |
| 深色主题 | 已通过 | 四套主题下代码块背景、引用/表格 token 均变化 |
| 快速打开 / 打印 | 已实现 | Ctrl+P / Ctrl+Alt+P；打印使用系统 PrintDialog |
| 原位编辑增强 | 已实现 | 剪贴板图片入 assets、表格 Tab/Shift+Tab 单元格移动 |
| 顶栏与 Agent 交互 | 已通过 | 分组/Agent 固定在右侧/保存下拉；Agent 附加上下文、改动列表、计时、重试、可调宽 |
| 回归 | 已通过 | 133 项（64 核心、60 UI、4 选择器、5 脚本）；原始输出 verification-0.7.0.txt |

范围与剩余项见 [阅读与交互 0.7](reading-0.7.md)。0.8 未完成：ZIP 浏览、日志跟随、大表格分页、可选文件关联、阅读视图内搜索。

格式探测：现有 `ContentReaders` 已使用 Framework 自带 ZIP/XML，`OfficeReader` 有共享渲染预算。PPTX 还需按 presentation 关系恢复页序及解析 DrawingML 文本/表格；估计 3–4 个相关文件、10–14 项测试。EPUB 还需 container/manifest/spine、章节导航、XHTML 语义渲染、受限图片及加密包识别，估计 5–7 个文件、16–22 项测试，约为前者两倍。以上是实现范围估算，不是实测工时。

参考：[PresentationML 结构](https://learn.microsoft.com/en-us/office/open-xml/presentation/structure-of-a-presentationml-document)、[EPUB 3.3](https://www.w3.org/TR/epub-33/)。用户已确认本轮先做 PPTX；EPUB 不实现。
