# 0.12 原生轻量版

> 此页记录 0.12 的历史实现。终端已在 [0.13](terminal-0.13.md) 换用 xterm.js；本页的终端能力与资源测量不代表当前版本。

本版移除 Agent、模型设置、SDK、Node 和网络模型接入。保留文件阅读、编辑、大纲、查找、格式导出和按需网页预览。

## CMD 面板

- Ctrl+反引号展开/收起，默认停靠底部，可在终端菜单改为右侧；小于 820 DIP 自动回到底部。
- 分隔线可拖动调整。只在首次展开时启动一个系统 CMD，会话从当前工作区或当前文件目录开始；无文件时用用户主目录。
- 切换文件不会自动切换现有会话目录，收起保留运行中的命令。菜单“结束会话”会结束 CMD 和其子进程，+ 可重新启动。
- 支持常见行式命令、中文、stdin、方向键、历史、复制粘贴和 Ctrl+C；有选区时 Ctrl+C 复制，无选区时中断。
- 输出仅在内存保留，历史最多 1000 行，最大 400 列。当前原生显示器为纯文本；不支持完整 ANSI 颜色、复杂全屏 TUI、鼠标跟踪与终端超链接。需要这些能力时应使用独立 Windows Terminal。
- 终端具有当前 Windows 用户的普通权限，命令可实际写文件、启动程序或访问网络；它不继承旧 Agent 的文件沙箱。
- 窗口退出时 Windows Job Object 结束其 CMD 及子进程，没有终端后台服务。

## 独立多窗口

取消单实例锁和文件转发。每次启动、双击程序或 Ctrl+Shift+N 都创建独立窗口，窗口之间不共享文档和终端；关闭一个窗口不关闭其他窗口。Ctrl+N 或顶部“新建”在当前窗口添加文件。外观偏好仍为可选的程序级设置。

## 视觉与图标

新图标将纸页、折角与叶片组合，SVG 和生成器均可编辑，ICO 包含 16/24/32/48/64/128/256 像素。主界面采用更安静的雾白、青绿强调和深蓝灰深色配色，原生细滚动条跟随主题，无透明模糊或网页外壳。

参考：[VS Code 布局](https://code.visualstudio.com/docs/configure/custom-layout)、[Zed 终端](https://zed.dev/docs/terminal)、[Windows 应用图标](https://learn.microsoft.com/en-us/windows/apps/design/style/iconography/app-icon-design)、[微软 ConPTY](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)。

验证使用 `scripts/test.ps1`；窗口与 CMD 验收及性能测量在 `tests/DesktopExperience.cs`，原始证据位于 `artifacts/metrics/native-0.12-*`，截图在 `artifacts/ui/native-0.12-*`。终端需要 Windows 10 1809 或更高版本；普通文件阅读继续使用 .NET Framework 4.8。

最终原生回归：61 项核心、62 项 UI、4 项系统选择器及 10 项桌面交互检查通过。当前本机暖启动中位数 451.8 ms、关闭 39.8 ms、空白工作集 90.5 MiB，稳定后多次空闲 CPU 样本中位数 0.00%。与旧版 449 ms/92.7 MiB 的采样条件并非严格配对，不宣称启动显著加速。

便携 ZIP 约 1.16 MiB、清单目录约 2.92 MiB；与 0.11.1 的 69.68 MiB ZIP 相比约减少 98.3%。旧本机会话与网页缓存移入项目内被忽略的 private-backup/agent-0.11，真实模型配置保留但不再读取。
