$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'paths.ps1')
# Used only after the complete runtime source/destination trees were checked
# for reparse points. Fresh staging is owned by this invocation.
function Assert-RuntimeChild([string]$Root, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside runtime: $resolved" }
    return $resolved
}
. (Join-Path $PSScriptRoot 'version.ps1')
$projectVersion = Get-ProjectVersion $projectRoot
$binaryRoot = Assert-ContainedPath $projectRoot (Join-Path $projectRoot 'build/bin')
$releaseRoot = Assert-ContainedPath $projectRoot (Join-Path $projectRoot 'release')
$destination = Assert-ContainedPath $projectRoot (Join-Path $projectRoot 'app')
$files = @('轻页.exe','轻页.exe.config','ICSharpCode.AvalonEdit.dll','Markdig.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','System.Buffers.dll','System.Memory.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Numerics.Vectors.dll','WebView2Loader.dll')
$binaryVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $binaryRoot '轻页.exe')).Version.ToString()
if ($binaryVersion -ne "$projectVersion.0") { throw 'Build version differs from VERSION; run scripts/build.ps1 first.' }
# A fresh allowlisted staging tree keeps runtime preferences and user documents
# out of portable releases. Existing examples in app are user-editable.
$entries = [ordered]@{}
foreach ($file in $files) { $entries[$file] = Join-Path $binaryRoot $file }
$entries['使用说明.md'] = Join-Path $projectRoot 'README.md'
$entries['VERSION'] = Join-Path $projectRoot 'VERSION'
$entries['docs/releasing.md'] = Join-Path $projectRoot 'docs/releasing.md'
$entries['docs/usage.md'] = Join-Path $projectRoot 'docs/usage.md'
$entries['docs/README.md'] = Join-Path $projectRoot 'docs/README.md'
$entries['docs/images/reading.png'] = Join-Path $projectRoot 'docs/images/reading.png'
$entries['docs/images/quick-open-dark.png'] = Join-Path $projectRoot 'docs/images/quick-open-dark.png'
$entries['模型.example.txt'] = Join-Path $projectRoot '模型.example.txt'
$entries['docs/lightweight-0.11.1.md'] = Join-Path $projectRoot 'docs/lightweight-0.11.1.md'
$entries['docs/release-notes-template.md'] = Join-Path $projectRoot 'docs/release-notes-template.md'
$entries['docs/editing-and-export-0.5.md'] = Join-Path $projectRoot 'docs/editing-and-export-0.5.md'
$entries['docs/agent-0.6.md'] = Join-Path $projectRoot 'docs/agent-0.6.md'
$entries['docs/reading-0.7.md'] = Join-Path $projectRoot 'docs/reading-0.7.md'
$entries['docs/performance.md'] = Join-Path $projectRoot 'docs/performance.md'
$entries['docs/project-layout.md'] = Join-Path $projectRoot 'docs/project-layout.md'
foreach ($document in @('harness-integration.md','agent-experience.md','agent-0.6.md','agent-0.9.md','agent-0.10.md','agent-0.11.md','reading-0.7.md','editing-and-export-0.5.md','type-aware-0.4.md')) { $entries['docs/'+$document] = Join-Path $projectRoot ('docs/'+$document) }
foreach ($file in @('AvalonEdit-LICENSE.txt','DotNet-LICENSE.txt','Lucide-LICENSE.txt','Markdig-LICENSE.txt','DeepSeek-Harness-LICENSE.txt','THIRD-PARTY-NOTICES.md','WebView2-LICENSE.txt')) { $entries['licenses/'+$file] = Join-Path (Join-Path $projectRoot 'licenses') $file }
foreach ($file in @('index.html','server.py','style.css','表格示例.csv','阅读示例.md')) { $entries['examples/'+$file] = Join-Path (Join-Path $projectRoot 'examples') $file }
$readerSize = ($entries.Values | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum
if ($readerSize -gt 15MB) { throw 'Native reader exceeded the 15 MiB target.' }
$harnessRoot = Assert-ContainedPath $projectRoot (Join-Path $projectRoot 'build/harness-runtime')
$harnessManifest = Join-Path $harnessRoot 'runtime-files.json'
if (-not (Test-Path -LiteralPath $harnessManifest)) { throw 'Embedded Harness not prepared; run scripts/prepare-harness.ps1.' }
Assert-ContainedTree $projectRoot $harnessRoot | Out-Null
$appHarness = Assert-ContainedPath $projectRoot (Join-Path $destination 'Harness')
if (Test-Path -LiteralPath $appHarness) { Assert-ContainedTree $projectRoot $appHarness | Out-Null }
foreach ($runtimeFile in (Get-Content -LiteralPath $harnessManifest -Raw | ConvertFrom-Json)) {
    if ($runtimeFile.path.Contains(':') -or $runtimeFile.path -match '(^|[/\\])\.\.([/\\]|$)') { throw "Outside runtime: $($runtimeFile.path)" }
    $source = Assert-RuntimeChild $harnessRoot (Join-Path $harnessRoot $runtimeFile.path)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $runtimeFile.sha256) { throw "Harness manifest mismatch: $($runtimeFile.path); prepare the runtime again." }
    $entries['Harness/'+$runtimeFile.path] = $source
}
$entries['Harness/runtime-files.json'] = $harnessManifest
foreach ($entry in $entries.GetEnumerator()) {
    $isRuntime = $entry.Key.StartsWith('Harness/')
    $source = if ($isRuntime) { Assert-RuntimeChild $harnessRoot $entry.Value } else { Assert-ContainedPath $projectRoot $entry.Value }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package file: $source" }
    if ($isRuntime) { Assert-RuntimeChild $appHarness (Join-Path $destination $entry.Key) | Out-Null }
    else { Assert-ContainedPath $projectRoot (Join-Path $destination $entry.Key) | Out-Null }
}
New-Item -ItemType Directory -Path $destination,$releaseRoot -Force | Out-Null
$stageRoot = Assert-ContainedPath $projectRoot (Join-Path $projectRoot ('build/package-stage-'+[Guid]::NewGuid().ToString('N')))
$stageApp = Join-Path $stageRoot 'app'
$zipName = "轻页-$projectVersion-win-x64.zip"
$zipPath = Assert-ContainedPath $projectRoot (Join-Path $releaseRoot $zipName)
try {
    New-Item -ItemType Directory -Path $stageApp -Force | Out-Null
    foreach ($entry in $entries.GetEnumerator()) {
        $isRuntime = $entry.Key.StartsWith('Harness/')
        $stageFile = if ($isRuntime) { Assert-RuntimeChild $stageApp (Join-Path $stageApp $entry.Key) } else { Assert-ContainedPath $projectRoot (Join-Path $stageApp $entry.Key) }
        $appFile = if ($isRuntime) { Assert-RuntimeChild $appHarness (Join-Path $destination $entry.Key) } else { Assert-ContainedPath $projectRoot (Join-Path $destination $entry.Key) }
        [IO.Directory]::CreateDirectory((Split-Path $stageFile -Parent)) | Out-Null
        [IO.File]::Copy($entry.Value, $stageFile, $true)
        if ($entry.Key.StartsWith('examples/') -and (Test-Path -LiteralPath $appFile)) { continue }
        [IO.Directory]::CreateDirectory((Split-Path $appFile -Parent)) | Out-Null
        [IO.File]::Copy($entry.Value, $appFile, $true)
    }
    $size = (Get-ChildItem -LiteralPath $stageApp -Recurse -File | Measure-Object Length -Sum).Sum
    if ($size -gt 512MB) { throw 'Release exceeded the 512 MiB embedded-runtime budget.' }
    $stageZip = Join-Path $stageRoot 'portable.zip'
    # Write entries explicitly: Windows PowerShell 5.1's Compress-Archive stores
    # backslash separators, which violates the ZIP specification and no longer
    # matches the forward-slash names in SHA256.txt. Entry names must be portable.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipStream = [IO.File]::Open($stageZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $stagePrefix = $stageRoot.TrimEnd('\') + '\'
            Get-ChildItem -LiteralPath $stageApp -Recurse -File | Sort-Object FullName | ForEach-Object {
                $relative = $_.FullName.Substring($stagePrefix.Length).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
    } finally { $zipStream.Dispose() }
    Move-Item -LiteralPath (Assert-ContainedPath $projectRoot $stageZip) -Destination $zipPath -Force
    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    $manifest = @("$((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash)  $zipName")
    $manifest += $entries.Keys | Sort-Object | ForEach-Object { $hash=Get-FileHash -LiteralPath (Join-Path $stageApp $_) -Algorithm SHA256; "$($hash.Hash)  app/$_" }
    $manifest | Set-Content -LiteralPath (Assert-ContainedPath $projectRoot (Join-Path $releaseRoot 'SHA256.txt')) -Encoding UTF8
    @("Version: $projectVersion", "Release directory: $size bytes ($([Math]::Round($size/1MB,2)) MiB)", "Portable ZIP: $zipSize bytes ($([Math]::Round($zipSize/1MB,2)) MiB)", "Executable: $destination\轻页.exe") | Set-Content -LiteralPath (Assert-ContainedPath $projectRoot (Join-Path $releaseRoot 'package-info.txt')) -Encoding UTF8
    Get-Content -LiteralPath (Join-Path $releaseRoot 'package-info.txt')
} finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath (Assert-ContainedTree $projectRoot $stageRoot) -Recurse -Force }
}
