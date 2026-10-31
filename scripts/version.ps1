function Get-ProjectVersion([string]$ProjectRoot) {
    $versionPath = Join-Path $ProjectRoot 'VERSION'
    $value = [IO.File]::ReadAllText($versionPath).Trim()
    if ($value -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain major.minor.patch.' }
    foreach ($component in $value.Split('.')) {
        if ([int]$component -gt 65534) { throw 'VERSION exceeds the AssemblyVersion component limit.' }
    }
    return $value
}
