# 0.11.1 快速、轻量化与显示优化

- 文件切换复用标签控件，仅在文件增删、标题或修改标记变化时重建；主题继续通过动态资源更新。当前标签以表面色和强调色下边线标记，高度从 42 收紧到 36 DIP。
- 标签右键指定第一/第二栏传递正确的 pane 名称，避免落到当前栏。
- 快速打开的大列表采用 70 ms 输入合并，小列表立即筛选；Enter 会先完成最新筛选，关闭时停止定时器和扫描。最多 2000 文件、6 层目录，无磁盘索引。
- 快速打开提供标题、输入提示、路径次级文字和主题化选中态，支持列表内 Esc 关闭；窄窗文字截断，路径通过工具提示查看。
- 沿用 WPF 和既有依赖，没有新增常驻进程或服务。

验证：`build/bin/UiTests.exe` 96 项通过；包含标签控件引用复用、第二栏菜单目标、未保存文档和空结果检查。截图 `artifacts/ui/quick-open-light.png`、`quick-open-dark-narrow.png`、`lightweight-tabs.png` 已目视检查。启动/内存测量见 `artifacts/metrics/lightweight-performance.txt`；这些是本机观察值，没有同条件旧版本对照，不表示量化加速比例。

构建：`scripts/build.ps1`；性能采样：`build/bin/ExperienceHarness.exe measure`；便携包：`scripts/package.ps1`。界面测试原始输出见 `artifacts/metrics/lightweight-ui.txt`。

2026-10-31 本机采样：首个观察启动 454 ms（未清理系统缓存），后续五次暖启动中位数 449 ms；六次关闭中位数 44 ms。空白窗口工作集 92.7 MiB、私有内存 90.1 MiB；稳定空闲三次一秒采样 CPU 均为 0.00%（测量精度范围内）。1/10 MiB 文本启动到标题就绪分别为 484/571 ms；标题就绪不代表完整可视排版耗时。没有同条件旧版对照，不能据此宣称启动提速或内存下降。
