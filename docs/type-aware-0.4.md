# 轻页 0.4：按格式阅读

后续 Agent 体验改造保留原生阅读编辑架构；新历史、任务状态、流式与并排/专注布局的现行规则及验证见 [原生 Agent 体验改造](agent-experience.md)。本页格式展示规则继续保留。

当前版本由根目录 [VERSION](../VERSION) 单一维护；本文件中的旧版本章节为阶段记录。程序集版本由 Framework 构建生成，ZIP 名称从同一来源读取。发布说明模板及完整 SHA256 校验见 [发布准备](releasing.md)；发布准备完整回归 113 项通过，输出为 `release-green.txt`。0.5 的左侧栏、Markdown 原位编辑与导出见 [编辑与导出](editing-and-export-0.5.md)。本机包目录 2.58 MiB、ZIP 0.94 MiB；未提交或远程发布。

用户选择先扩展 PPTX：按 presentation 关系给定顺序读取页标题、正文及表格，支持 Transitional / Strict OOXML，复用 OfficeReader。最多 100 页，累计 1500 块、20000 行内节点、512 Ki 字符；XML 单项 16 MiB、累计 64 MiB，DTD、外部页引用、路径穿越均拒绝。只读且不解压落盘，不读取图片、图表、动画和讲者备注；原始版式请交给系统软件。13 项针对性检查及完整回归见 `pptx-green.txt`。页序依据 [Microsoft PresentationML 文档](https://learn.microsoft.com/en-us/office/open-xml/presentation/working-with-presentation-slides)。

预览防抖改为保留 AvalonEdit 不可变快照，使用 ContentRevision 缓存并按阅读预算读取前缀；不再每次把 buffer.Text 全文复制到 Document。保存按需读取全文。10 MiB Markdown 连续编辑 20 次，本机托管分配 924.68 → 165.01 MiB，下降约 82%；最新输入及保存完整性通过，完整测试见 `p1-2-green.txt`。

维护更新：阅读代码背景、表头、分隔线、引用边框/文字及语法色统一从 Theme 资源读取，三主题可动态切换；P0-1 已经观察到失败测试再修复，完整测试输出见 `artifacts/metrics/p0-1-green.txt`。后续维护验收见 `maintenance-2026-10-29.md`。

GB18030 分页改用内存偏移索引（最多四个文件，文件元数据变化即失效）；不保存正文缓存。129 MiB 夹具实测首次建索引含打开 246.15 ms，热读首页/末页 4.32/4.24 ms；原边界检查及 UTF-32 两字节序检查通过，见 `p0-2-green.txt`。

连续打开使用一个活动项加最多四个等待项的窗口队列；溢出取消最早等待项并显示提示，失败不阻塞后续项。对应 RED/GREEN 及完整输出为 `p0-3-red.txt` / `p0-3-green.txt`。

P1-1 将 Toolbar、DocumentBar、AppearancePopup、Footer、TabStrip 及相关搜索、工作区、布局提取为区域控件。MainWindow 247 行，测试改用 internal WindowTestHooks；Markdown、CSV、代码、窄阅读窗口四张截图与拆分前逐像素一致。完整回归输出为 `p1-1-green.txt`。

本轮优化让文件内容决定展示方式，并保留轻页原生、便携和按需加载的定位。源码仍在当前 WPF/AvalonEdit 项目中，未迁移到网页应用。

## 设计决定

使用本地 codex-design、frontend-design 和 Leonxlnx/taste-skill。taste-skill 为应用界面生成的方向是 **Framed sheet**，seed `510666604`，保留项目原有配色和字体，没有重抽；完整记录在 `.taste-skill/direction.json`。

淡薄荷框架包住白色内容页，叶片标识沿用。窗口工具栏从 62 调为 56 DIP；文件标题、类型与展示方式独立成行。阅读/结构/表格、源码、对照位于文件旁，选中项用白色表面和字重区分。暖白、浅紫继续可选，未把 taste-skill 的网页审美限制当作移除既有主题的理由。

界面使用系统中文与 Segoe 字体，源码使用等宽字体。正文与元信息分级，三套主题的元信息在内容和框架表面均达到约 4.8:1 或更高的计算对比度。Markdown 内容限制在居中阅读宽度，源码不再放大标题字形；表格增加行号、36 DIP 行高和数字列右对齐。阅读界面只显示相关控件，PDF 不显示本机开发服务地址栏。

## 格式规则

| 格式 | 默认展示 | 可切换与边界 |
| --- | --- | --- |
| TXT / 未命名文本 | 自然换行、无行号 | 可选原生文本阅读 |
| 日志、源码、配置 | 等宽源码、行号、保留行序 | 常见语言高亮；JSON 与 YAML/INI/TOML/ENV 使用专用或配置规则，未知语言保留文本 |
| Markdown | 原生排版 | 源码/同一文件对照；不提供 Typora 式原位富文本编辑 |
| JSON / XML / XAML / XML 配置 | 可展开结构 | 原始源码不重写；大整数保持文字值，空数组/对象分开显示；解析失败与超限有可恢复提示 |
| CSV / TSV | 只读行列表格 | 源码编辑；最多 500 行/50 列，不把第一行擅自从数据中移除 |
| DOCX | 原生基础版式 | 标题、粗斜体、段落与表格；支持 Strict/Transitional 和内容控件；复杂版式使用系统软件 |
| XLSX | 可切换工作表 | 只读缓存值、常用内置数字/日期/百分比、布尔；不重算公式，不还原完整 Excel 格式 |
| RTF | 原生富文本 | 只读，最多 4 MiB |
| 图片 / PDF / SVG | 专用查看 | 沿用对应的缩放与查看器；GIF 首帧、图片解码尺寸等限制仍保留 |
| HTML | 源码 | 主动选择网页或对照才开启预览 |
| 未支持的格式 | 原始文本或十六进制 | 标示只读/边界，一键使用系统关联软件 |

每份文件的视图只在本次内存保留。视图切换复用编辑缓冲区，修改可进入阅读/对照，保持编码、BOM 和换行。文件旁的“对照”只展示该文件的源码与结果；外观面板仍可选择双文件布局。关闭和错误路径都会使相关视图缓存失效。

Office 渲染共享块、单元格、run、inline、字符和嵌套深度预算；超限提示可见。XML 禁止外部实体，结构树按展开请求创建，并保留节点上限。

## 在线参考及采用方式

- [Typora Markdown 说明](https://support.typora.io/Markdown-Reference/)：阅读时呈现段落、标题、列表和表格语义。采用分开的阅读/源码视图，不声称实现 Typora 编辑器。
- [VS Code Markdown](https://code.visualstudio.com/docs/languages/markdown)：明确源码与预览的切换与并排查看。
- [Windows 内容布局](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics)及 [Fluent 字体](https://fluent2.microsoft.design/typography)：通过间距、字体层级和控件分组呈现工作界面。
- [RFC 8259](https://www.rfc-editor.org/info/rfc8259/)与 [RFC 4180](https://www.rfc-editor.org/info/rfc4180/)：JSON 类型、数值文字与 CSV 引号、逗号、跨行字段的验证依据。
- [WordprocessingML 正文结构](https://learn.microsoft.com/en-us/office/open-xml/word/how-to-open-and-add-text-to-a-word-processing-document)与 [SpreadsheetML 数字格式](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/17d11129-219b-4e2c-88db-45844d21e528)：Office 只读视图的结构及数字格式依据。

这些规范不意味着所有格式存在同一套显示标准，也不代表轻页能替代完整 Office、媒体或专有软件。

## 验证与复现

构建命令仍为 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1`。完整回归使用 `scripts/test.ps1`；类型界面采集使用 `build/bin/ExperienceHarness.exe types`。

完整回归：38 项核心、40 项原生 UI、4 项真实系统选择器、3 项打包/边界检查，共 85 项。junction 测试需要允许创建项目内临时目录链接的 Windows 执行环境，受限沙箱会拒绝这一步；正常权限环境中已通过。测试原始输出在 `artifacts/metrics/verification-0.4.0.txt`。

新增检查包括：类型默认值、本次视图选择、未保存修改、同文件源码对照、JSON 数字精度/特殊键/空容器、XML 外部实体、CSV 多行、RTF、Strict DOCX、内容控件、Office 总渲染预算，以及错误预览后的正常文档恢复。独立只读审查发现的缓存、Word 兼容和预算问题均已增加对应修正与检查。

实际程序窗口通过当前 CUA API检查了 Markdown 阅读/源码切换。原生截图还由 WPF 实际布局与 WebView2 官方捕获产生并逐张目视核对：`artifacts/ui/type-*.png`，覆盖 1240×820 与最低 820×540。`before/markdown.png`、`before/integrated.png` 保存本次改动前的界面。此程序不是网页，未运行 HTML DOM 专用的 taste inspector，也未把 390px 浏览器截图当成原生窗口证据；未重新测量跨显示器 DPI 或全面性能。

发布仍通过清单打包，保留 app 内用户修改的示例和外观偏好，不发布测试工具、夹具或截图。中文版 Windows PowerShell 5.1 所执行的脚本保留 UTF-8 BOM，避免包名被误解码。

## 0.4.1：阅读宽度与自由缩放

2026-10-29 按用户反馈取消阅读器 900 DIP 最大宽度和居中限制，改为随内容区铺开；常规左右内边距 24 DIP，窄阅读区为 16 DIP，Markdown、DOCX 和 RTF 共用此布局。窗口不再设置 820×540 人工最小尺寸限制。

小于 720 DIP 时次要工具进入“更多”，源码/预览自动上下排列；小于 620 DIP 时文件信息与视图按钮分行。窄窗口折叠工作区栏和次要状态信息，放宽后恢复，不丢失文档或工作区。搜索栏可换行，隐藏工具仍可通过菜单或原快捷键使用。

新增三个真实控件检查覆盖窗口 240/360/480/640 DIP 的尺寸和按钮边界、溢出菜单/搜索换行，以及 1600→360 DIP 阅读宽度与内容保留。原生截图 `artifacts/ui/reading-wide.png`、`reading-narrow.png`、`reading-small.png` 分别为 1440×900、390×640、240×320，均已目视检查。0.4.0 的固定宽度和最低尺寸说明是历史行为，以本节为准。

最终 88 项检查通过（38 核心、43 UI、4 系统选择器、3 打包），记录为 `artifacts/metrics/verification-0.4.1.txt`。CUA 实际拖动右下角验证了 1240→640→390 DIP 窗口宽度，阅读内容随之换行，“更多工具”菜单可打开，原文保持不变。app 与 0.4.1 ZIP 已更新，清单哈希一致。

## 内置 Harness 的兼容边界

当前 Agent 已改为右侧内置官方 Harness；文件阅读、源码、对照与原位编辑仍由原生控件完成，原生依赖版本没有升级。Harness 只在打开面板后启动，文件读写通过轻页原有预算和冲突保护；当前文件为默认范围，主动导入文件夹才使用目录范围。对话仅保存在本次内存中，清空、切换范围或退出后结束。运行时、权限、体积与验收见 [Harness 集成记录](harness-integration.md)，本页早期性能数字不代表新 Agent 性能。
