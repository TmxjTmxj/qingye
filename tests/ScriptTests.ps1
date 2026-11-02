$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'scripts/paths.ps1')
$version=(Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$terminalAssets=Join-Path $root 'src/Assets/Terminal'
$vendor=Get-Content -LiteralPath (Join-Path $terminalAssets 'vendor-lock.json') -Raw | ConvertFrom-Json
foreach($package in $vendor){foreach($file in $package.files.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $terminalAssets $file.Name) -Algorithm SHA256).Hash -ne $file.Value){throw "Terminal vendor hash mismatch: $($file.Name)"}}}
Write-Output 'PASS pinned terminal vendor files match SHA256 lock'
$terminalAssembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $root 'build/bin/轻页.exe')))
foreach($asset in (Get-ChildItem -LiteralPath $terminalAssets -File)){
  $stream=$terminalAssembly.GetManifestResourceStream('Qingye.Terminal.'+$asset.Name)
  if($null -eq $stream){throw "Missing embedded terminal asset: $($asset.Name)"}
  try{$sha=[Security.Cryptography.SHA256]::Create();try{$embedded=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}
  if($embedded -ne (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash){throw "Embedded terminal asset mismatch: $($asset.Name)"}
}
Write-Output 'PASS executable embeds the exact verified terminal assets'

$fixture=Assert-ContainedPath $root (Join-Path $root ('build/package-test-'+[Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$link=Join-Path $fixture 'link'
$personal=Join-Path $root ('app/private-test-'+[Guid]::NewGuid().ToString('N')+'.txt')
try {
  New-Item -ItemType Junction -Path $link -Target (Join-Path $root 'examples') | Out-Null
  $blocked=$false;try{Assert-ContainedTree $root $fixture | Out-Null}catch{$blocked=$true};if(-not $blocked){throw 'Junction was not refused'}
  [IO.Directory]::Delete($link)
  Write-Output 'PASS packaging guard refuses linked directories'
  [IO.File]::WriteAllText($personal,'synthetic test only')
  & (Join-Path $root 'scripts/package.ps1')
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zipPath=Join-Path $root "release/轻页-$version-win-x64.zip"
  $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
  try {
    if($zip.Entries.FullName | Where-Object {$_ -match '(^|/)(Harness|node_modules|agent-data|agent-history|模型.txt|preferences.xml)(/|$)' -or $_.EndsWith([IO.Path]::GetFileName($personal))}){throw 'Private or retired runtime file in package'}
    if(-not(Test-Path -LiteralPath $personal)){throw 'Personal app file was removed'}
    Write-Output 'PASS package excludes private files and retired Agent/Node'
    $manifest=Get-Content -LiteralPath (Join-Path $root 'release/SHA256.txt')
    foreach($entry in $zip.Entries){if($entry.FullName.Contains('\')){throw 'Nonportable ZIP path'};$stream=$entry.Open();try{$sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()};if(-not($manifest | Where-Object {$_ -eq "$hash  $($entry.FullName)"})){throw "Missing hash: $($entry.FullName)"}}
    Write-Output 'PASS every packaged file matches SHA256 manifest'
  }finally{$zip.Dispose()}
  $hash=(Get-FileHash -LiteralPath $zipPath).Hash;if(-not($manifest | Where-Object {$_ -eq "$hash  轻页-$version-win-x64.zip"})){throw 'ZIP hash mismatch'}
  Write-Output 'PASS ZIP SHA256'
  $binaryVersion=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $root 'app/轻页.exe')).Version.ToString();if($binaryVersion -ne "$version.0"){throw 'Binary version mismatch'}
  Write-Output 'PASS version source matches executable and portable ZIP'
} finally {
  if(Test-Path -LiteralPath $link){if((Get-Item -LiteralPath $link).Attributes -band [IO.FileAttributes]::ReparsePoint){[IO.Directory]::Delete($link)}}
  if(Test-Path -LiteralPath $personal){Remove-Item -LiteralPath (Assert-ContainedPath $root $personal) -Force}
  if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath (Assert-ContainedTree $root $fixture) -Recurse -Force}
}
