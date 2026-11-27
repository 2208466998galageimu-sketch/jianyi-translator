param([switch] $Delete)

$ErrorActionPreference = 'Stop'
try {
    $installDir = [IO.Path]::GetFullPath($env:JianyiCleanInstallDir).TrimEnd('\')
    $volumeRoot = [IO.Path]::GetPathRoot($installDir + '\').TrimEnd('\')
    if ($installDir -eq $volumeRoot -or $installDir.StartsWith('\\')) { exit 2 }

    # Reject junctions, symlinks and mounted folders before traversing them.
    $directory = Get-Item -LiteralPath $installDir -Force
    for ($ancestor = $directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if (($ancestor -isnot [IO.DirectoryInfo]) -or ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { exit 2 }
    }

    $data = Get-Item -LiteralPath (Join-Path $installDir 'data') -Force
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    if (!$data.PSIsContainer -or ($data.Attributes -band [IO.FileAttributes]::ReparsePoint)) { exit 2 }
    $pending.Push($data)
    while ($pending.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath ($pending.Pop().FullName) -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 2 }
            if ($entry.PSIsContainer) { $pending.Push($entry) }
        }
    }

    if ($Delete) {
        Remove-Item -LiteralPath $data.FullName -Recurse -Force
        if (Test-Path -LiteralPath $data.FullName) { exit 2 }
    }
    exit 0
} catch {
    exit 2
}
