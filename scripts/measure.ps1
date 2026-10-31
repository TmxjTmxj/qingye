$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/build.ps1" -Stage All
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $projectRoot 'build/bin/ExperienceHarness.exe') measure
if ($LASTEXITCODE -ne 0) { throw 'Process measurements failed.' }
& (Join-Path $projectRoot 'build/bin/ExperienceHarness.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native viewer verification failed.' }
& (Join-Path $projectRoot 'build/bin/ExperienceHarness.exe') reading
if ($LASTEXITCODE -ne 0) { throw 'Native reading measurement failed.' }
