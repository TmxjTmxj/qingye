param([switch]$CoreOnly)
$ErrorActionPreference = 'Stop'
if ($CoreOnly) { & "$PSScriptRoot/build.ps1" -Stage Core } else { & "$PSScriptRoot/build.ps1" -Stage All }
$outputRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'build/bin'
& (Join-Path $outputRoot 'CoreTests.exe')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $CoreOnly) {
    & (Join-Path $outputRoot 'UiTests.exe')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $outputRoot 'PickerTests.exe')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $harnessNode = Join-Path (Split-Path $PSScriptRoot -Parent) 'build/harness-runtime/node.exe'
    if (-not (Test-Path -LiteralPath $harnessNode)) { throw 'Run scripts/prepare-harness.ps1 before the complete test suite.' }
    & $harnessNode --test (Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/harness.test.mjs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $outputRoot 'ExperienceHarness.exe') harness flow restart large
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # Production memory-mode acceptance: temporary sessions, current-turn cancel,
    # clear-while-approval, scope switching, 820/390 layout and owned-process exit.
    & (Join-Path $outputRoot 'ExperienceHarness.exe') harness-memory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # Packaging reads runtime-files.json, so refresh the manifest after build.ps1
    # has copied the current src/Harness/*.mjs files into the runtime.
    & $harnessNode (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts/harness-manifest.mjs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/ScriptTests.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
