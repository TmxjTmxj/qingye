param([ValidateSet('Core','App','UI','All')][string]$Stage = 'All')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'version.ps1')
$projectVersion = Get-ProjectVersion $projectRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$framework = Split-Path $compiler -Parent
$outputRoot = Join-Path $projectRoot 'build/bin'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Runtime.Serialization.dll','System.Net.Http.dll','System.Web.Extensions.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/Core') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
function Compile([string[]]$arguments) {
    & $compiler /nologo /codepage:65001 /optimize+ /warn:4 @arguments
    if ($LASTEXITCODE -ne 0) { throw "Compiler failed: $LASTEXITCODE" }
}
function Copy-UnchangedSafe([string]$Source,[string]$Destination) {
    # UI Automation can retain an assembly handle after the test window closes.
    # Identical dependencies need no write; changed dependencies still fail visibly if in use.
    if ((Test-Path -LiteralPath $Destination) -and ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash)) { return }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}
Compile (@('/target:exe',('/out:' + (Join-Path $outputRoot 'CoreTests.exe'))) + $references + $coreSources + (Join-Path $projectRoot 'tests/CoreTests.cs') + (Join-Path $projectRoot 'tests/HarnessCoreTests.cs'))
if ($Stage -eq 'Core') { exit 0 }
$harnessRuntime = Join-Path $projectRoot 'build/harness-runtime'
if (Test-Path -LiteralPath $harnessRuntime) {
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/Harness') -Filter '*.mjs' | Select-Object -ExpandProperty Name)) { Copy-Item -LiteralPath (Join-Path $projectRoot "src/Harness/$file") -Destination (Join-Path $harnessRuntime $file) -Force }
}
$lock = Get-Content -LiteralPath (Join-Path $projectRoot 'packages.lock.json') -Raw | ConvertFrom-Json
foreach ($package in $lock) {
    $packagePath = Join-Path $projectRoot "build/packages/$($package.id)/$($package.version)"
    $libPath = Join-Path $packagePath 'lib/net462'
    if (-not (Test-Path -LiteralPath $libPath)) { $libPath = Join-Path $packagePath 'lib/net45' }
    if (-not (Test-Path -LiteralPath $libPath)) { throw "No compatible framework target for $($package.id)" }
    Get-ChildItem -LiteralPath $libPath -Filter '*.dll' | ForEach-Object { Copy-UnchangedSafe $_.FullName (Join-Path $outputRoot $_.Name) }
    if ($package.id -eq 'microsoft.web.webview2') {
        Copy-UnchangedSafe (Join-Path $packagePath 'runtimes/win-x64/native/WebView2Loader.dll') (Join-Path $outputRoot 'WebView2Loader.dll')
    }
}
$wpfReferences = @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $framework "WPF/$_") }
$applicationIcon = Join-Path $projectRoot 'src/Assets/qingye.ico'
if (-not (Test-Path -LiteralPath $applicationIcon)) {
    $toolRoot = Join-Path $projectRoot 'build/tools'
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    $iconTool = Join-Path $toolRoot 'IconGenerator.exe'
    Compile (@('/target:exe','/platform:x64','/main:IconGenerator',('/out:' + $iconTool),('/resource:' + (Join-Path $projectRoot 'src/Assets/Icons/leaf.svg') + ',Qingye.Icons.leaf.svg')) + $references + $wpfReferences + (Join-Path $framework 'System.Xaml.dll' | ForEach-Object { '/r:' + $_ }) + (Join-Path $projectRoot 'src/UI/IconView.cs') + (Join-Path $projectRoot 'scripts/IconGenerator.cs'))
    & $iconTool $applicationIcon
    if ($LASTEXITCODE -ne 0) { throw 'Application icon generation failed.' }
}
$appReferences = $references + $wpfReferences + @(('/r:' + (Join-Path $framework 'System.Xaml.dll')), ('/r:' + (Join-Path $framework 'System.Windows.Forms.dll')), ('/r:' + (Join-Path $framework 'System.Drawing.dll')))
$appReferences += @(Get-ChildItem -LiteralPath $outputRoot -Filter '*.dll' | Where-Object { $_.Name -ne 'WebView2Loader.dll' } | ForEach-Object { '/r:' + $_.FullName })
$appSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -Recurse | Select-Object -ExpandProperty FullName)
$versionSource = Join-Path $projectRoot 'build/AssemblyVersion.g.cs'
@("using System.Reflection;", "[assembly: AssemblyVersion(`"$projectVersion.0`")]", "[assembly: AssemblyFileVersion(`"$projectVersion.0`")]") | Set-Content -LiteralPath $versionSource -Encoding UTF8
$appSources += $versionSource
$iconResources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/Assets/Icons') -Filter '*.svg' | ForEach-Object { '/resource:' + $_.FullName + ',Qingye.Icons.' + $_.Name })
Compile (@('/target:winexe','/platform:x64','/main:Qingye.Program',('/out:' + (Join-Path $outputRoot '轻页.exe')),('/win32icon:' + $applicationIcon),('/resource:' + $applicationIcon + ',Qingye.Application.ico')) + $appReferences + $appSources + $iconResources)
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination (Join-Path $outputRoot '轻页.exe.config') -Force
if (Test-Path -LiteralPath (Join-Path $projectRoot 'tests/UiTests.cs')) {
    Compile (@('/target:exe','/platform:x64','/main:Qingye.Tests.UiTests',('/out:' + (Join-Path $outputRoot 'UiTests.exe')),('/r:' + (Join-Path $outputRoot '轻页.exe'))) + $appReferences + (Join-Path $projectRoot 'tests/UiTests.cs') + (Join-Path $projectRoot 'tests/HarnessUiTests.cs'))
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination (Join-Path $outputRoot 'UiTests.exe.config') -Force
}
Write-Output "Build ready: $outputRoot"
if (Test-Path -LiteralPath (Join-Path $projectRoot 'tests/PickerTests.cs')) {
    $automationReferences = @('UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object { '/r:' + (Join-Path $framework "WPF/$_") }
    Compile (@('/target:exe','/platform:x64','/main:Qingye.Tests.PickerTests',('/out:' + (Join-Path $outputRoot 'PickerTests.exe')),('/r:' + (Join-Path $outputRoot '轻页.exe'))) + $appReferences + $automationReferences + (Join-Path $projectRoot 'tests/PickerTests.cs'))
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination (Join-Path $outputRoot 'PickerTests.exe.config') -Force
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'tests/ExperienceHarness.cs')) {
    Compile (@('/target:exe','/platform:x64','/main:Qingye.Tests.ExperienceHarness',('/out:' + (Join-Path $outputRoot 'ExperienceHarness.exe')),('/r:' + (Join-Path $outputRoot '轻页.exe'))) + $appReferences + (Join-Path $projectRoot 'tests/ExperienceHarness.cs') + (Join-Path $projectRoot 'tests/HarnessExperience.cs') + (Join-Path $projectRoot 'tests/HarnessOptimizationExperience.cs'))
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination (Join-Path $outputRoot 'ExperienceHarness.exe.config') -Force
}
