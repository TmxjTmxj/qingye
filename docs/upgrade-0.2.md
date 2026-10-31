# 轻页 0.2 升级记录

范围：在现有 Windows 原生架构上改善阅读、资源占用和文件切换，继续执行用户明确要求的轻量、单文件默认、临时导入和退出不恢复规则。

1. 原生 Markdown：使用现有 Markdig 解析，WPF 显示标题、段落、列表、引用、代码、表格和限定范围本地图片；不启动浏览器进程。保留内容选择与普通链接；复杂嵌入内容不执行。预览限制 512K 字符、1500 个块和 20000 个行内元素，超出时显示提示，源码保持完整。
2. 当前编辑栏切换：标签作用于当前栏，可从右键菜单明确选择第一/第二栏。布局或切换不丢修改。
3. 临时目录分批读取：后台枚举，每批最多 120 文件和 120 目录，用户点击“加载更多”继续；不会只把超过首批的文件永久隐藏。切换工作区后旧任务不回填，目录句柄每批结束释放。没有项目扫描器或索引服务。
4. 大文件：超过 32 MiB 分段只读，通常 1 MiB 一段；首段发现超长行且文件超过 256 KiB 时采用 64 KiB 小段，减少整条长行排版。UTF8/UTF16/GB18030 的跨段字符仍完整。较大的简单文本可交给专业软件编辑，这是为降低内存和排版峰值作的取舍。
5. 预览：文档内容版本未变化时保留现有页面/阅读控件，避免布局调整触发重复加载；CSS/JS 等打开文件的修改仍进入预览。每个 WebView 控件独立持有资源流，旧控件的事件不干扰新控件。编辑区文本按缓冲区版本同步，避免每次预览重复复制未变内容。

验证：新增测试分别观察到升级前失败、实现后通过。最终 37 项真实文件测试与 21 项实际 WPF/WebView2 测试通过（58/58）。网页未保存内容、CSS 修改、连续530次本地请求、原生 Markdown、PDF、隐藏/重开/初始化期间关闭均实际验证。程序目录约2.46 MiB，ZIP约0.87 MiB；发布程序启动、关闭、单实例已实际运行验证。

最终审查：新上下文只读 reviewer 完成，两个 Important 问题与一个目录重试问题进入同一次修复轮。实体字符/自动链接渲染遗漏已通过失败后成功的真实 FlowDocument 测试修复；资源请求额度耗尽已通过连续530次实际fetch测试修复，响应读到EOF即释放，只限制尚未完成的响应。完整打包了组件清单中原有的 System.Numerics.Vectors 依赖，避免特定Markdown字符解析失败。

Final: Ruling: Re-grade directory retry from Minor to Important — a transient missing directory left later files inaccessible, and the UI's advertised retry did not work. Fixed via real directory move/restore regression RED→GREEN, preserving earlier loaded items.

补充验证：普通短行文件头之后出现超长行时也转为小段，避免仅检查开头样本而把后续长行交给UI；必要时在后台进行一次限量探测，再持续使用64 KiB段。原始编码与跨段Unicode测试仍通过。

当前实测：Markdown首次阅读布局约187ms，整个窗口工作集92.7 MiB（包括测试宿主），不创建网页进程。千文件目录首批约48ms，每批最多120文件/120目录；不是把全部1000项一次性显示，与0.1首批500项的工作量不同。完整数据与采样条件见performance.md。

Ruling: Continue inline in the existing clean feature branch — the user explicitly asked to keep upgrading an already approved product; no new product direction or publication is introduced. Cost if wrong: the next release remains a local branch rather than remote deployment.

Ruling: Native Markdown replaces browser-based Markdown — removes the entire browser process group for common reading. Cost if wrong: complex embedded HTML and advanced web-only formatting require a professional renderer; supported basic text/table/code formatting remains.

Ruling: Tighten large-file reading thresholds — the user prioritizes fluency and resource use over professional editing. Cost if wrong: files over the threshold are view-only and need a specialist editor to modify.

Ruling: Offset-based directory pages re-enumerate only on explicit load-more — avoids retaining directory handles or building an index. Cost if wrong: listings can change between batches during external directory edits, and very deep later pages require more scanning; refresh the workspace to get a new listing.
