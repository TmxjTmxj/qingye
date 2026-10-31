$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$packageRoot = Join-Path $projectRoot 'build/packages'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$lockPath = Join-Path $projectRoot 'packages.lock.json'
if (Test-Path -LiteralPath $lockPath) {
    $packages = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
} else {
    $packages = @('avalonedit', 'microsoft.web.webview2', 'markdig') | ForEach-Object {
        $versions = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$_/index.json"
        $stable = @($versions.versions | Where-Object { $_ -notmatch '-' })
        [pscustomobject]@{ id = $_; version = $stable[-1] }
    }
    $packages | ConvertTo-Json | Set-Content -LiteralPath $lockPath -Encoding UTF8
}
foreach ($package in $packages) {
    $destination = Join-Path $packageRoot "$($package.id)/$($package.version)"
    if (-not (Test-Path -LiteralPath $destination)) {
        $archivePath = Join-Path $packageRoot "$($package.id).$($package.version).nupkg"
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$($package.id)/$($package.version)/$($package.id).$($package.version).nupkg" -OutFile $archivePath
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($entry in $archive.Entries) {
                $target = [System.IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
                $allowedRoot = [System.IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
                if (-not $target.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
                if ($entry.Name) {
                    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
                }
            }
        } finally { $archive.Dispose() }
    }
    Write-Output "$($package.id) $($package.version)"
    Get-ChildItem -LiteralPath (Join-Path $destination 'lib') -Directory | Select-Object -ExpandProperty Name
}
