$ErrorActionPreference='Stop'
& "$PSScriptRoot/build.ps1" -Stage All
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'build/bin/ExperienceHarness.exe') measure
if($LASTEXITCODE -ne 0){throw 'Desktop measurements failed.'}
