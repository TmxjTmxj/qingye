$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $projectRoot 'scripts/paths.ps1')
. (Join-Path $projectRoot 'scripts/version.ps1')
$projectVersion = Get-ProjectVersion $projectRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem
$passed=0; $failed=0
function Check-Test([string]$name,[scriptblock]$action) { try { & $action; $script:passed++; Write-Output "PASS $name" } catch { $script:failed++; Write-Output "FAIL $name : $($_.Exception.Message)" } }
$fixture = Assert-ContainedPath $projectRoot (Join-Path $projectRoot ('build/path-data-'+[Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$fakeProject = Join-Path $fixture 'project'
$outside = Join-Path $fixture 'outside'
New-Item -ItemType Directory -Path $fakeProject,$outside -Force | Out-Null
$junction = Join-Path $fakeProject 'cache'
$syntheticName = 'personal-test-'+[Guid]::NewGuid().ToString('N')+'.txt'
$synthetic = Assert-ContainedPath $projectRoot (Join-Path (Join-Path $projectRoot 'app') $syntheticName)
New-Item -ItemType Directory -Path (Split-Path $synthetic -Parent) -Force | Out-Null
try {
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    Check-Test 'Cleanup rejects reparse ancestors' { $blocked=$false; try { Assert-ContainedPath $fakeProject (Join-Path $junction 'payload.txt') | Out-Null } catch { $blocked=$true }; if(-not $blocked){throw 'Ancestor junction allowed external path'} }
    Check-Test 'Recursive cleanup rejects nested reparse points' {
        if (-not (Get-Command Assert-ContainedTree -ErrorAction SilentlyContinue)) { throw 'Recursive tree guard is missing' }
        $blocked=$false
        try { Assert-ContainedTree $projectRoot $fakeProject | Out-Null } catch { $blocked=$true }
        if(-not $blocked){throw 'Nested junction allowed recursive cleanup'}
    }
    Check-Test 'Portable ZIP excludes runtime personal files' {
        [IO.File]::WriteAllText($synthetic,'synthetic-private-file')
        & (Join-Path $projectRoot 'scripts/package.ps1') | Out-Null
        $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $projectRoot "release/轻页-$projectVersion-win-x64.zip"))
        try { if($zip.Entries.FullName | Where-Object { $_.EndsWith($syntheticName) }) { throw 'Runtime personal file entered portable ZIP' } } finally { $zip.Dispose() }
        if(-not(Test-Path -LiteralPath $synthetic)){throw 'Runtime personal file was removed'}
        if([IO.File]::ReadAllText($synthetic) -ne 'synthetic-private-file'){throw 'Runtime personal file was altered'}
    }
    Check-Test 'Harness package rejects a manifest path outside its runtime' {
        $manifestPath = Join-Path $projectRoot 'build/harness-runtime/runtime-files.json'
        $originalManifest = [IO.File]::ReadAllText($manifestPath)
        try {
            [IO.File]::WriteAllText($manifestPath, '[{"path":"../../outside.txt","sha256":"invalid"}]', (New-Object Text.UTF8Encoding($false)))
            $blocked = $false
            try { & (Join-Path $projectRoot 'scripts/package.ps1') | Out-Null } catch { $blocked = $_.Exception.Message -match 'Outside' }
            if (-not $blocked) { throw 'Runtime manifest allowed parent traversal' }
        } finally { [IO.File]::WriteAllText($manifestPath, $originalManifest, (New-Object Text.UTF8Encoding($false))) }
    }
    Check-Test 'Harness package refuses a nested runtime junction' {
        $runtimeLink = Join-Path $projectRoot 'build/harness-runtime/package-guard-junction'
        try {
            New-Item -ItemType Junction -Path $runtimeLink -Target $outside | Out-Null
            $blocked = $false
            try { & (Join-Path $projectRoot 'scripts/package.ps1') | Out-Null } catch { $blocked = $_.Exception.Message -match 'Reparse point refused' }
            if (-not $blocked) { throw 'Runtime package traversed a nested junction' }
        } finally { if (Test-Path -LiteralPath $runtimeLink) { Remove-Item -LiteralPath $runtimeLink -Force } }
    }
    Check-Test 'One version source drives executable and package' {
        $versionFile=Join-Path $projectRoot 'VERSION'
        if(-not(Test-Path -LiteralPath $versionFile)){throw 'Single VERSION source missing'}
        $version=[IO.File]::ReadAllText($versionFile).Trim()
        $assembly=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $projectRoot 'build/bin/轻页.exe'))
        if($assembly.Version.ToString() -ne "$version.0"){throw 'Executable version differs from VERSION'}
        if(-not(Test-Path -LiteralPath (Join-Path $projectRoot "release/轻页-$version-win-x64.zip"))){throw 'Package version differs from VERSION'}
        if([IO.File]::ReadAllText((Join-Path $projectRoot 'src/AssemblyInfo.cs')) -match 'Assembly(File)?Version\('){throw 'AssemblyInfo retains a second version literal'}
    }
    Check-Test 'Portable Harness contains a fixed runtime and excludes personal history' {
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $projectRoot "release/轻页-$projectVersion-win-x64.zip"))
        try {
            foreach ($required in @('app/Harness/node.exe','app/Harness/launcher.mjs','app/Harness/node_modules/@deepseek-ai/dsh/lib/bin.js','app/Harness/runtime-files.json','app/Harness/Node-LICENSE.txt')) {
                if (-not $zip.GetEntry($required)) { throw "Missing embedded Harness file: $required" }
            }
            if ($zip.Entries.FullName | Where-Object { $_ -match '^app/(harness-data|agent-history)/|^app/模型.txt$' }) { throw 'Agent private data entered release' }
        } finally { $zip.Dispose() }
    }
    Check-Test 'SHA256 covers ZIP and every packaged file' {
        $version=[IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
        if(-not(Test-Path -LiteralPath (Join-Path $projectRoot 'docs/release-notes-template.md'))){throw 'Release notes template missing'}
        $manifest=@{}
        foreach($line in Get-Content -LiteralPath (Join-Path $projectRoot 'release/SHA256.txt')){
            if($line -match '^([0-9A-Fa-f]{64})  (.+)$'){$manifest[$Matches[2]]=$Matches[1]}
        }
        $zipName="轻页-$version-win-x64.zip"
        $zipPath=Join-Path $projectRoot "release/$zipName"
        if($manifest[$zipName] -ne (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash){throw 'ZIP checksum absent or incorrect'}
        $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $badName=$zip.Entries | Where-Object { $_.FullName.Contains('\') -or $_.FullName.StartsWith('/') } | Select-Object -First 1
            if($badName){throw "ZIP entry uses a non-portable path: $($badName.FullName)"}
            $count=0
            foreach($entry in $zip.Entries){
                if($entry.FullName.EndsWith('/')){continue}
                $count++
                $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try {$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')} finally {$stream.Dispose();$sha.Dispose()}
                if($manifest[$entry.FullName] -ne $hash){throw "Packaged checksum absent or incorrect: $($entry.FullName)"}
            }
            if($manifest.Count -ne $count+1){throw 'Manifest contains stale entries'}
        } finally {$zip.Dispose()}
    }
} finally {
    if(Test-Path -LiteralPath $synthetic){Remove-Item -LiteralPath (Assert-ContainedPath $projectRoot $synthetic)}
    if(Test-Path -LiteralPath $junction){Remove-Item -LiteralPath $junction -Force}
    Remove-Item -LiteralPath (Assert-ContainedTree $projectRoot $fixture) -Recurse -Force
}
Write-Output "Scripts: $passed passed, $failed failed"
if($failed){exit 1}
