param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'paths.ps1')
$source = Assert-ContainedPath $projectRoot (Join-Path $projectRoot 'src/Harness')
$runtime = Assert-ContainedPath $projectRoot (Join-Path $projectRoot 'build/harness-runtime')
$nodeVersion = '24.18.0'
$archiveName = "node-v$nodeVersion-win-x64.zip"
$download = Assert-ContainedPath $projectRoot (Join-Path $projectRoot "build/$archiveName")
$checksums = Assert-ContainedPath $projectRoot (Join-Path $projectRoot "build/node-$nodeVersion-SHASUMS256.txt")
if (-not $Offline) {
    & npm.cmd ci --prefix $source --cache (Join-Path $projectRoot 'build/npm-cache') --ignore-scripts --no-audit --no-fund --fetch-retries=0
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Harness dependency restore failed.' }
    Invoke-WebRequest "https://nodejs.org/dist/v$nodeVersion/SHASUMS256.txt" -OutFile $checksums
    if (-not (Test-Path -LiteralPath $download)) { Invoke-WebRequest "https://nodejs.org/dist/v$nodeVersion/$archiveName" -OutFile $download }
}
if (-not (Test-Path -LiteralPath $checksums)) { throw 'Missing Node checksums; prepare once with network access.' }
$expected = Get-Content -LiteralPath $checksums | Where-Object { $_ -match "  $([Regex]::Escape($archiveName))`$" } | Select-Object -First 1
if (-not $expected -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expected.Split(' ')[0]) { throw 'Node distribution checksum mismatch.' }
$unpacked = Assert-ContainedPath $projectRoot (Join-Path $projectRoot "build/node-v$nodeVersion-win-x64")
if (-not (Test-Path -LiteralPath (Join-Path $unpacked 'node.exe'))) { Expand-Archive -LiteralPath $download -DestinationPath (Join-Path $projectRoot 'build') }
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
foreach ($file in @((Get-ChildItem -LiteralPath $source -Filter '*.mjs' | Select-Object -ExpandProperty Name) + @('package.json','package-lock.json'))) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $runtime $file) -Force
}
# No install scripts, package managers, credentials or user profile files ship.
Assert-ContainedTree $projectRoot $runtime | Out-Null
& node (Join-Path $projectRoot 'scripts/copy-harness-runtime.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Harness runtime copy failed.' }
Copy-Item -LiteralPath (Join-Path $unpacked 'node.exe') -Destination $runtime -Force
Copy-Item -LiteralPath (Join-Path $unpacked 'LICENSE') -Destination (Join-Path $runtime 'Node-LICENSE.txt') -Force
@{
    harness = '0.2.1-rc.5'
    node = $nodeVersion
    source = 'https://github.com/deepseek-ai/deepseek-harness'
    nodeArchiveSHA256 = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    lockSHA256 = (Get-FileHash -LiteralPath (Join-Path $source 'package-lock.json') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtime 'provenance.json') -Encoding UTF8
& node (Join-Path $projectRoot 'scripts/harness-manifest.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Harness runtime manifest failed.' }
Write-Output "Prepared fixed Harness runtime: $runtime"
