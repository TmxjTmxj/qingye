# 构建、发布与校验

Windows x64、.NET Framework 4.8；CMD 面板需要 Windows 10 1809 或更新版本，集成终端与网页类预览需要系统 WebView2。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ScriptTests.ps1
```

不需要 Node、npm 或 .NET SDK。核心测试覆盖格式读写，窗口测试覆盖主题/布局/导出，桌面验收覆盖独立多窗口、真实 CMD、中文、stdin、Ctrl+C 和子进程清理；脚本测试核对打包与文件边界。测试使用项目内临时夹具。

单独生成包：`scripts/package.ps1`。`VERSION` 是唯一版本源；构建程序集与包名必须一致。ZIP 仅含清单中的程序、依赖、示例与说明，不含模型配置、个人偏好、会话、Node、Harness 或本机测试输出。

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath 'release/轻页-0.13.0-win-x64.zip'
```

将结果与 `release/SHA256.txt` 第一行比较；其余行覆盖包内每个文件。源码修改不自动上传或发布；需要时按明确授权推送到 GitHub。

## GitHub 发布

公开下载使用英文附件名 `qingye-0.13.0-win-x64.zip`，内部文件名仍为中文。下载后可在 PowerShell 中执行 `Get-FileHash -Algorithm SHA256 -LiteralPath 'qingye-0.13.0-win-x64.zip'`，与同一发布页的 `SHA256.txt` 对照；公开清单的首行必须使用该英文文件名。

上传前从白名单重新打包，扫描待提交源码与 ZIP 的敏感模式，核对 ZIP 内每个文件及构建/本机程序一致性。模型配置、个人偏好、旧会话、私有备份和构建缓存保持忽略，不使用 `git add -f`，不上传本机旧私有分支。标签必须指向已验证且已推送的源码提交；发布完成后下载附件重新核对 SHA256。
