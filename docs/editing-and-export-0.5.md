# 轻页 0.5：左栏、原位编辑与导出

0.5 在现有阅读/编辑模型上增加三项能力：统一的左侧栏、Markdown 原位编辑，以及跨格式导出。三项都遵守原有约束：不安装、不登录、不写会话缓存；只读源文件不被改写；纯文本与 Markdown 阅读不启动网页进程。

## 左侧栏

左侧栏（`Ctrl+Shift+B`）由“文件”“大纲”两个页签组成：

- “文件”列出本次打开的文件；导入工作区后显示按需展开的目录树。工作区不存在时只显示本次文件，不保留最近列表。
- “大纲”按文件类型生成：Markdown 标题（ATX 与 Setext）、JSON 键、XML 元素、YAML/INI/TOML/ENV 键、CSV/TSV 列名、纯文本与日志中的章节标记。点击条目跳到源码行；阅读视图会滚动到对应标题块；原位编辑会滚动到对应标题。

大纲只扫描最多 512 KiB 文本，最多 500 条，不建立持久索引。重建在编辑停止约 400 ms 后发生。

## Markdown 原位编辑

“编辑”按钮或 `Ctrl+Shift+E` 打开 Markdown 原位编辑：

- 支持的子集：标题、段落、粗体、斜体、删除线、行内代码、链接、图片、有序/无序列表、引用、围栏代码块、表格、水平线。
- 使用只含 PipeTables、ListExtras、EmphasisExtras 的解析管线，不启用 SmartyPants、脚注、任务列表、数学公式等会改变原文的扩展。
- 文档超过 512 KiB，或包含无法表示的块（如表格内嵌块、未解析的引用式链接）时，拒绝进入原位编辑并给出原因，源码与阅读视图保持可用。
- 保存或切回源码时，富文本按上述子集序列化为 Markdown；这是有意的规范化，不保证与原始字节逐字一致。原编码、BOM 与换行约定仍按文档设置写回。
- 原位编辑与源码共用同一个 AvalonEdit 缓冲区；切回源码或保存时的整段替换是一个撤销步骤。未打开原位编辑时，源码编辑行为完全不变。

## 导出与另存为

导出只写新文件，不修改当前文档，也不把当前文档切换成目标格式。可用的目标由源类型决定：

| 源 | 目标 |
| --- | --- |
| 文本、Markdown | TXT、Markdown、HTML、DOCX、PDF |
| DOCX、PPTX、RTF | TXT、Markdown、HTML、DOCX、PDF |
| XLSX | CSV、HTML、TXT、PDF |
| CSV、TSV | CSV、XLSX、HTML、TXT、PDF |

- DOCX 生成基础 OOXML：段落、标题、粗体、列表前缀、代码字体；不写图片、样式表、页眉页脚或复杂版式。
- XLSX 生成单工作表：CSV/TSV 或 Markdown 管道表格的行列；文本按 inlineStr 写入，可被常见表格软件打开。
- HTML 使用自包含样式与 CSP；Markdown 复用阅读视图的 HTML 生成器。
- PDF 通过系统 WebView2 的 `PrintToPdf` 写入，只对可预览内容可用；未安装运行时会明确提示。
- 只读的 DOCX/PPTX/RTF/XLSX 也能通过“另存为”进入导出；导出不能覆盖当前文件，需要选择其他文件名。

## 验证

完整回归 122 项：56 核心、57 原生 UI、4 系统选择器、5 脚本。新增检查覆盖：

- 大纲：Markdown/Setext、JSON、XML、配置键、纯文章节；侧栏点击定位源码行。
- 原位编辑：真实窗口打开、追加段落、切回源码后缓冲区包含新内容；Markdown 标题、强调、列表、代码块、表格经序列化后保留。
- 导出：Markdown → HTML/DOCX、CSV → XLSX 并回读行列、导出不改变源文件；WebView2 实际写出以 `%PDF-` 开头的 PDF。
- 既有检查全部保留，包括 ZIP 条目使用正斜杠的发布校验。

命令与 0.4 相同：`scripts/build.ps1`、`scripts/test.ps1`、`scripts/package.ps1`。原始输出在 `artifacts/metrics/verification-0.5.0.txt`。

## 已知限制

- 原位编辑只覆盖 Markdown；RTF、DOCX 等富文本仍为只读阅读。
- 原位编辑是有损的 Markdown 子集，不承诺与 Typora 或其他编辑器完全互换；复杂语法请继续用源码视图。
- 导出不还原 Office 完整版式、图片、公式、图表或分页；需要保真时使用系统软件。
- PDF 导出会复用预览的 WebView2 环境，属于可选功能，不计入纯文本模式资源。
