# 本地发布准备

当前版本仅维护根目录 [VERSION](../VERSION) 的 `major.minor.patch`。构建脚本生成程序集版本 `major.minor.patch.0`，打包脚本读取同一来源；若程序尚未重新构建，打包会拒绝版本不一致的产物。README 与说明文档链接该文件；历史里程碑的版本数字保留为历史记录。

在 Windows PowerShell 中执行：

```powershell
& scripts/prepare-harness.ps1 # 首次准备；后续可用 -Offline
& scripts/test.ps1
& scripts/package.ps1
```

脚本使用系统 Framework 编译器，不需要 .NET SDK。完整测试包含核心、WPF 实际窗口、系统选择器、固定 Harness 插件测试、实际 Harness 与本机合成模型的集成验收，以及包清单和路径边界检查。首次准备运行时需要开发机 Node/npm；最终便携包已内置 Node，用户无须安装。junction 与本机 HttpListener 验收需要允许对应 Windows API 的执行环境。本轮未添加 GitHub Actions。

打包结果位于 `release/`，文件名由 VERSION 生成；`package-info.txt` 记录版本和本次实测大小。程序目录为 `app/`。ZIP 使用全新的白名单暂存树，不收录 app 中额外保存的个人文件或偏好。打包不提交、不推送，也不发布到远程。

复制 [说明模板](release-notes-template.md) 并填入已验证的变更、测试输出和限制即可准备发布说明。模板中的版本从 VERSION 读取，不另行维护版本常量；未测的性能项填写“未测量”。

## SHA256 清单

`release/SHA256.txt` 每行格式为 `64 位十六进制摘要`、两个空格、相对路径。清单包含 ZIP 本身，以及 ZIP 内全部运行文件、说明、许可和示例，包内路径以 `app/` 开头。它描述白名单暂存内容；app 中保留的旧个人文件和用户修改过的示例不属于该清单。

在包含 ZIP 与 SHA256.txt 的发布目录中执行下列命令，并核对相应行的摘要：

```powershell
$version = (Get-Content ../VERSION -Raw).Trim() # 源码目录；独立下载时使用解压后的 app/VERSION
Get-FileHash -LiteralPath "轻页-$version-win-x64.zip" -Algorithm SHA256
```

解压后可逐项核对（将当前目录设为 SHA256.txt 所在目录）：

```powershell
foreach ($line in Get-Content ./SHA256.txt) {
    if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw '清单格式错误' }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ((Get-FileHash -LiteralPath $relative -Algorithm SHA256).Hash -ne $expected) {
        throw "摘要不一致：$relative"
    }
}
```

脚本回归会从 ZIP 直接读取每个文件，验证全部摘要及清单条数，防止缺项和旧记录混入。
