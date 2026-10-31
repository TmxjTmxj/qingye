function Assert-ContainedPath([string]$Root, [string]$Path) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside project: $resolved" }
    # Check every existing ancestor before reading or changing a child. The child
    # itself may be ordinary (or missing) while a parent is a junction/symlink.
    $ancestor = $resolved
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $ancestor" }
        }
        $parent = [IO.Directory]::GetParent($ancestor)
        if ($null -eq $parent) { break }
        $ancestor = $parent.FullName
    }
    return $resolved
}

function Assert-ContainedTree([string]$Root, [string]$Path) {
    $resolved = Assert-ContainedPath $Root $Path
    if (-not (Test-Path -LiteralPath $resolved)) { return $resolved }
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if ($item.PSIsContainer) { $pending.Push($resolved) }
    while ($pending.Count) {
        $directory = $pending.Pop()
        # Enumerate one level at a time; never traverse a reparse point.
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($child.FullName)" }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
    return $resolved
}
