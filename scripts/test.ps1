param([switch]$CoreOnly)
$ErrorActionPreference='Stop'
& "$PSScriptRoot/build.ps1" -Stage $(if($CoreOnly){'Core'}else{'All'})
$bin=Join-Path (Split-Path $PSScriptRoot -Parent) 'build/bin'
& (Join-Path $bin 'CoreTests.exe');if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
if(-not $CoreOnly){foreach($test in @('UiTests.exe','PickerTests.exe','ExperienceHarness.exe','UsabilityTests.exe')){& (Join-Path $bin $test);if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}}}
