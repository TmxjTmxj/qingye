<div align="center">
  <img src="src/Assets/qingye.png" width="80" alt="轻页图标" />
  <h1>轻页 · Qingye</h1>
  <p><strong>一页，刚刚好。</strong></p>
  <p>Windows 本地文件阅读与轻度编辑工具。打开即用，按需加载。</p>
  <p>
    <img alt="Version" src="https://img.shields.io/badge/version-0.13.0-176E57?style=flat-square" />
    <img alt="Windows x64" src="https://img.shields.io/badge/platform-Windows_x64-356A8A?style=flat-square" />
    <img alt="Native WPF" src="https://img.shields.io/badge/interface-native_WPF-69569D?style=flat-square" />
  </p>
  <p><a href="https://github.com/TmxjTmxj/qingye/releases/latest">下载便携版</a> · <a href="docs/usage.md">使用说明</a> · <a href="#从源码构建">源码构建</a> · <a href="docs/README.md">文档索引</a></p>
</div>

![轻页原生 Markdown 阅读界面](docs/images/reading.png)

## 0.13 · 终端更好用，阅读依然轻快

移除内置 Agent 与 Node，保留原生文件阅读；需要命令行时，再展开终端。新的 ConPTY + xterm.js 引擎支持 ANSI 配色、全屏 CLI 和 Unicode 字素，改善复杂终端界面的字符错位。每次启动创建独立窗口。

[**下载 0.13.0 · Windows x64**](https://github.com/TmxjTmxj/qingye/releases/download/v0.13.0/qingye-0.13.0-win-x64.zip) · [版本说明与 SHA256](https://github.com/TmxjTmxj/qingye/releases/tag/v0.13.0)

## 少一点等待，多一点专注

轻页适合临时查看文件、阅读资料、修改文本，以及对照查看源码与排版结果。默认打开一份文件；需要更多内容时，再导入文件或工作区。

| 阅读 | 编辑 | 按需辅助 |
| :--- | :--- | :--- |
| Markdown 原生排版、类型化阅读、大纲导航 | 文本与代码编辑、查找替换、编码和换行保留 | 多文件对照、格式导出、按需 CMD 面板 |
| 文本和 Markdown 阅读无需网页进程 | 标签切换复用控件，快捷键贯穿主要流程 | 网页/PDF 预览及 CMD 会话按需启用 |

## 开始使用

1. 下载 [Windows x64 便携 ZIP](https://github.com/TmxjTmxj/qingye/releases/download/v0.13.0/qingye-0.13.0-win-x64.zip)，可用同一发布页的 `SHA256.txt` 校验。
2. 解压后双击 `app/轻页.exe`，保留整个 `app` 目录。
3. 拖入文件，或按 `Ctrl+O` 打开。需要多个文件时按 `Ctrl+Shift+O` 导入。

需要 Windows x64 和 .NET Framework 4.8。网页、PDF、SVG 预览及集成终端需要系统 Microsoft Edge WebView2 Runtime；普通文本和原生阅读无需启用它。基础阅读与编辑不需要登录，CMD 面板需要 Windows 10 1809 或更高版本。

## 常用操作，一步到位

| 操作 | 快捷键 |
| :--- | :--- |
| 打开 / 导入多个文件 | `Ctrl+O` / `Ctrl+Shift+O` |
| 保存 / 另存为 | `Ctrl+S` / `Ctrl+Shift+S` |
| 快速打开 | `Ctrl+P` |
| 下一份 / 上一份文件 | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| 查找 / 跳转行 | `Ctrl+F` / `Ctrl+G` |
| 阅读与源码切换 / 源码预览对照 | `Ctrl+E` / `Ctrl+Shift+V` |
| 文件与大纲 / 导入工作区 | `Ctrl+Shift+B` / `Ctrl+K` |
| CMD 面板 / 独立新窗口 | Ctrl+反引号 / `Ctrl+Shift+N` |

快速打开支持未保存文档、文件名模糊匹配和路径过滤。工作区扫描按需运行，最多 2000 个文件、6 层目录；关闭弹窗后取消，不建立磁盘索引。

<details>
<summary>查看 OpenCode 实际终端界面</summary>

![深色终端](docs/images/terminal-dark.png)

</details>

## 按文件类型，选择合适的阅读方式

| 文件 | 当前能力 |
| :--- | :--- |
| TXT、日志、源码、配置 | 阅读与编辑；按类型选择字体、换行与语法高亮 |
| Markdown | 原生排版、源码、对照；可选受限子集原位编辑 |
| JSON / XML | 可展开结构视图与源码，保留原始内容 |
| CSV / TSV、XLSX | 表格阅读；XLSX 展示已有缓存值，不重算公式 |
| DOCX / PPTX / RTF | 基础文本与结构阅读，不还原完整 Office 版式 |
| EPUB、图片、PDF | 章节阅读、图片查看或系统运行时预览 |
| HTML / SVG | 源码与按需预览；可连接已运行的本机开发服务 |

导出支持 TXT、Markdown、HTML、DOCX、CSV、XLSX 和 PDF，能力取决于当前内容。详细格式限制、大小预算与转换边界见 [完整说明](docs/usage.md#文件范围) 和 [编辑与导出](docs/editing-and-export-0.5.md)。

## 集成终端与独立多窗口

终端默认收起，首次展开才启动 Windows ConPTY、系统 CMD 和本地 xterm.js 渲染器。支持 ANSI 颜色、全屏 TUI、中文宽字符、组合字符、Emoji 字素、方向键及粘贴；尺寸随实际字符网格同步，底部和右侧均可停靠、拖动调整。收起保留会话，退出窗口结束所属进程。

`Ctrl+C` 在选中文字时复制，否则中断；`Ctrl+Shift+C` 复制，`Ctrl+V` 粘贴，`Ctrl++/-/0` 调整终端字号。菜单也可在系统 CMD 中另开会话。它拥有普通命令行权限，可真实执行命令。[0.13 实测与边界](docs/terminal-0.13.md)

每次启动都创建独立窗口；`Ctrl+Shift+N` 也能新开窗口。文档和终端相互独立，关闭一个窗口不会影响另一个。

## 轻量化

Agent、内置 Node 和模型运行时已移除。普通阅读与编辑保留原生 WPF；0.13 的终端按需启用已有系统 WebView2，以支持成熟的字符渲染与交互。终端脚本全部内置，无 CDN、Node 或本地网页服务器，关闭所属窗口清理进程与临时配置目录。

打开终端会增加浏览器进程与内存；终端保持收起时不会产生这项开销。没有常驻索引、账户、更新或终端后台服务。[改造与验证](docs/terminal-0.13.md)

## 已验证的体验与边界

| 项目 | 本机验证 |
| :--- | :--- |
| 便携包 | ZIP 约 1.38 MiB；解压约 3.73 MiB，不含系统运行时 |
| 普通空白窗口 | 热启动中位数约 463 ms；工作集约 90 MiB |
| 回归 | 60 项核心、62 项界面、4 项选择器检查，以及实际多窗口与 CMD 验收通过 |
| OpenCode 1.19.5 | 底部全屏、右侧窄面板实际界面通过 |
| Claude Code 2.1.316 | 启动文字通过；上游连接检查返回 403，完整交互尚未验收 |

测量仅代表本机条件，未清理系统缓存；打开终端的额外进程成本未计入普通空白窗口工作集。CLI 需要自行安装与配置，轻页不附带这些工具或模型凭据。[完整验证与复验方式](docs/terminal-0.13.md)

## 从源码构建

准备 Git，并在 Windows PowerShell 中执行：

```powershell
git clone https://github.com/TmxjTmxj/qingye.git
cd qingye
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

编译使用系统 .NET Framework C# 编译器，无需另装 .NET SDK。原生依赖锁定于 `packages.lock.json`；内置终端组件的版本与哈希记录在 `src/Assets/Terminal/vendor-lock.json`。首次准备需联网，之后可按文档复用离线依赖。

```text
src/        原生应用与 CMD 终端源码
tests/      核心、原生窗口与终端测试
scripts/    依赖准备、构建、测量、打包与整理
docs/       使用说明、设计记录、能力边界与验证
examples/   可选演示内容
licenses/   第三方许可与声明
```

`app/` 是本机运行目录，`release/` 保存便携包，`build/` 和 `artifacts/` 保存构建缓存与验证材料；这些生成目录不进入源码仓库。完整目录说明见 [项目布局](docs/project-layout.md)。

## 文档与许可

- [完整使用说明](docs/usage.md) · [文档索引](docs/README.md)
- [0.13 终端改造](docs/terminal-0.13.md) · [0.12 改版记录](docs/native-0.12.md) · [发布与 SHA256 校验](docs/releasing.md)
- [第三方组件及许可](licenses/THIRD-PARTY-NOTICES.md)

第三方组件分别遵循其附带许可；本仓库暂未为轻页自身代码指定开源许可证。
