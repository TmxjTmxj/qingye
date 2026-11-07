$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'paths.ps1')
function Assert-ProjectPath([string]$path) {
    return (Assert-ContainedPath $projectRoot $path)
}
$releaseRoot = Assert-ProjectPath (Join-Path $projectRoot 'release')
$archive = Assert-ProjectPath (Join-Path $releaseRoot 'archive')
New-Item -ItemType Directory -Path $archive -Force | Out-Null
$running = @(Get-Process -Name '轻页' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.MainModule.FileName } catch {} })
foreach ($item in Get-ChildItem -LiteralPath $releaseRoot) {
    if ($item.Name -notmatch '^轻页-0\.[123]\.0-win-x64(?:\.zip)?$') { continue }
    $source = Assert-ProjectPath $item.FullName
    if ($item.PSIsContainer) { $source = Assert-ContainedTree $projectRoot $source }
    $destination = Assert-ProjectPath (Join-Path $archive $item.Name)
    if ($running | Where-Object { $_.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }) { Write-Output "In-use version preserved: $source"; continue }
    if (Test-Path -LiteralPath $destination) { throw "Archive already exists; preserving both: $destination" }
    Move-Item -LiteralPath $source -Destination $destination
}
$artifactRoot = Assert-ProjectPath (Join-Path $projectRoot 'artifacts')
foreach ($group in @(@('*.png','ui'),@('*.txt','metrics'))) {
    $destinationRoot = Assert-ProjectPath (Join-Path $artifactRoot $group[1])
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $artifactRoot -File -Filter $group[0]) {
        $source = Assert-ProjectPath $item.FullName
        $destination = Assert-ProjectPath (Join-Path $destinationRoot $item.Name)
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) { Remove-Item -LiteralPath $source; continue }
            $destination = Assert-ProjectPath (Join-Path $destinationRoot ($item.BaseName + '-previous-' + (Get-Date -Format 'yyyyMMddHHmmssfff') + $item.Extension))
        }
        Move-Item -LiteralPath $source -Destination $destination
    }
}
$packageRoot = Assert-ProjectPath (Join-Path $projectRoot 'build/packages')
Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.nupkg' | ForEach-Object { $target=Assert-ProjectPath $_.FullName; Remove-Item -LiteralPath $target }
$binRoot = Assert-ProjectPath (Join-Path $projectRoot 'build/bin')
foreach ($item in Get-ChildItem -LiteralPath $binRoot -File | Where-Object { $_.Name.StartsWith('轻页') }) { $target=Assert-ProjectPath $item.FullName; Remove-Item -LiteralPath $target }
foreach ($item in Get-ChildItem -LiteralPath $binRoot -Directory | Where-Object { $_.Name -match '^(picker|ui|test)-data-[0-9a-f]{32}$' }) { $target=Assert-ContainedTree $projectRoot $item.FullName; Remove-Item -LiteralPath $target -Recurse -Force }
$launch = Assert-ProjectPath (Join-Path $projectRoot '启动轻页.cmd')
[IO.File]::WriteAllText($launch, "@echo off`r`nchcp 65001 >nul`r`nstart `"`" `"%~dp0app\轻页.exe`"`r`n", (New-Object Text.UTF8Encoding($false)))
$shortcut = Assert-ProjectPath (Join-Path $projectRoot '轻页.lnk')
$shell = New-Object -ComObject WScript.Shell
try { $link=$shell.CreateShortcut($shortcut); $link.TargetPath=Join-Path $projectRoot 'app/轻页.exe'; $link.WorkingDirectory=Join-Path $projectRoot 'app'; $link.IconLocation=$link.TargetPath+',0'; $link.Description='轻页 · 本地文件阅读与简单编辑'; $link.Save() } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
Write-Output 'Organized: app, release/archive, artifacts/ui, artifacts/metrics; sources preserved.'
