param(
    [Parameter(Mandatory=$true)] [ValidateSet('Build','Check','Remove','Register','Uninstall')] [string] $Mode,
    [string] $Source = '',
    [string] $Output = '',
    [string] $Prefix = ''
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
$ledgerName = 'jianyi-installed-files.lst'
$uninstaller = 'Jianyi-uninstall.exe'
$legacyUninstaller = 'uninstall.exe'
$disabledUninstaller = 'Jianyi-previous-uninstall.disabled'

function Find-Item([string] $Path) {
    try { Get-Item -LiteralPath $Path -Force -ErrorAction Stop }
    catch [System.Management.Automation.ItemNotFoundException] { $null }
}

function Assert-Directory([string] $Path) {
    for ($directory = [IO.DirectoryInfo]::new($Path); $null -ne $directory; $directory = $directory.Parent) {
        $item = Find-Item $directory.FullName
        if ($null -ne $item -and (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
            throw "Directory or a parent is a link or file."
        }
    }
}

function Resolve-File([string] $Root, [string] $Relative) {
    if (!$Relative -or $Relative -match '[\x00-\x1f<>:"|?*/]' -or $Relative.StartsWith('\') -or
        ($Relative.Split('\') | Where-Object { !$_ -or $_.EndsWith('.') -or $_.EndsWith(' ') }) -or
        $Relative -match '^(data|jianyi-installed-files\.lst)(\\|$)') { throw "Invalid program file path." }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Program file is outside the installation directory."
    }
    Assert-Directory ([IO.Path]::GetDirectoryName($full))
    $item = Find-Item $full
    if ($null -ne $item -and ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw "A program file path is a directory or link."
    }
    $full
}

function Get-Hash([string] $Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose() }
    } finally { $algorithm.Dispose() }
}

function Read-Manifest([string] $Path, [hashtable] $Files) {
    $info = Find-Item $Path
    if ($null -eq $info -or $info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $info.Length -gt 2MB) {
        throw "Missing or invalid program file manifest."
    }
    foreach ($line in [IO.File]::ReadLines($info.FullName, $utf8)) {
        if (!$line) { continue }
        if ($line -notmatch '^([0-9A-F]{64})\|(.+)$' -or $Files.ContainsKey($Matches[2])) { throw "Invalid program file manifest entry." }
        $Files[$Matches[2]] = $Matches[1]
    }
}

try {
    if ($Mode -eq 'Build') {
        $origin = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
        Assert-Directory $origin
        $lines = foreach ($item in Get-ChildItem -LiteralPath $origin -Recurse -File -Force | Sort-Object FullName) {
            $relative = if ($Prefix) { $Prefix + '\' + $item.FullName.Substring($origin.Length + 1) } else { $item.FullName.Substring($origin.Length + 1) }
            if ($relative -in @($ledgerName,$uninstaller,$legacyUninstaller,$disabledUninstaller) -or $relative -match '^data(\\|$)') {
                throw "Reserved path in program payload."
            }
            [void](Resolve-File $origin $item.FullName.Substring($origin.Length + 1))
            "$(Get-Hash $item.FullName)|$relative"
        }
        if (!$lines) { throw "Program payload is empty." }
        [IO.File]::WriteAllLines($Output, [string[]]$lines, $utf8)
        exit 0
    }

    $root = [IO.Path]::GetFullPath($env:JianyiProgramDir).TrimEnd('\')
    if ($root -eq [IO.Path]::GetPathRoot($root + '\').TrimEnd('\') -or $root.StartsWith('\\')) {
        throw "Choose a dedicated local installation directory."
    }
    Assert-Directory $root
    $ledger = Join-Path $root $ledgerName
    $installed = @{}
    $incoming = @{}
    $knownLegacy = $false
    if ($Mode -ne 'Uninstall') {
        Read-Manifest (Join-Path $PSScriptRoot 'program-main.lst') $incoming
        if ($env:JianyiInstallOcr -eq '1') { Read-Manifest (Join-Path $PSScriptRoot 'program-ocr.lst') $incoming }
        $incoming[$uninstaller] = ''
    }

    if ($Mode -eq 'Register') {
        $lines = foreach ($entry in $incoming.GetEnumerator()) {
            $path = Resolve-File $root $entry.Key
            $hash = Get-Hash $path
            if ($entry.Value -and $entry.Value -ne $hash) { throw "A copied program file failed verification." }
            "$hash|$($entry.Key)"
        }
        $stream = [IO.File]::Open($ledger, [IO.FileMode]::CreateNew)
        try {
            $writer = [IO.StreamWriter]::new($stream, $utf8)
            try { foreach ($line in $lines) { $writer.WriteLine($line) } }
            finally { $writer.Dispose() }
        } finally { $stream.Dispose() }
        [Console]::Write((Get-Hash $ledger))
        exit 0
    }

    if ($env:JianyiManifestHash) {
        Read-Manifest $ledger $installed
        if ($env:JianyiManifestHash -ne (Get-Hash $ledger)) { throw "Installed program manifest could not be verified." }
    } elseif ($Mode -eq 'Uninstall') {
        throw "Installed program manifest is missing."
    } elseif ($env:JianyiUpgrading -eq '1') {
        if ($env:JianyiPreviousVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw "Unknown installed program version." }
        $version = [version]$env:JianyiPreviousVersion
        if ($version.Revision -lt 0) { $version = [version]($version.ToString() + '.0') }
        Read-Manifest (Join-Path (Join-Path $PSScriptRoot 'Legacy') "$version.lst") $installed
        $program = Resolve-File $root $env:JianyiProgramName
        if (!$installed.ContainsKey($env:JianyiProgramName) -or (Get-Hash $program) -ne $installed[$env:JianyiProgramName]) {
            throw "Installed program does not match the known legacy version."
        }
        if ($null -ne (Find-Item $ledger)) { throw "An unrecognized manifest exists in the installation directory." }
        $oldUninstaller = Resolve-File $root $legacyUninstaller
        if ((Find-Item $oldUninstaller) -and (Find-Item (Join-Path $root $disabledUninstaller))) {
            throw "There is already a file at the legacy uninstaller backup path."
        }
        $knownLegacy = $true
    } elseif ([IO.Directory]::Exists($root)) {
        foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
            if ($entry.Name -ne 'data' -or !$entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                ![IO.File]::Exists((Join-Path $entry.FullName 'thanslator.db'))) {
                throw "Choose an empty directory for a new installation."
            }
        }
    }

    $owned = @{}
    foreach ($entry in $installed.GetEnumerator()) {
        $path = Resolve-File $root $entry.Key
        if ([IO.File]::Exists($path) -and (Get-Hash $path) -eq $entry.Value) { $owned[$entry.Key] = $path }
    }
    foreach ($relative in $incoming.Keys) {
        $path = Resolve-File $root $relative
        if ([IO.File]::Exists($path) -and !$owned.ContainsKey($relative)) {
            throw "An existing file could not be verified as part of Jianyi."
        }
    }
    if ($Mode -eq 'Check') { exit 0 }

    # Verify a file again immediately before removing it; uninstallers and the main executable go last.
    foreach ($relative in $owned.Keys | Sort-Object { $_ -in @($uninstaller,$env:JianyiProgramName) }, { $_ }) {
        $path = Resolve-File $root $relative
        if ([IO.File]::Exists($path)) {
            if ((Get-Hash $path) -ne $installed[$relative]) { throw "A program file changed during cleanup." }
            [IO.File]::Delete($path)
        }
    }
    if ($knownLegacy -and [IO.File]::Exists($oldUninstaller)) {
        [IO.File]::Move($oldUninstaller, (Join-Path $root $disabledUninstaller))
    }
    if ($env:JianyiManifestHash) {
        if ((Get-Hash $ledger) -ne $env:JianyiManifestHash) { throw "The manifest changed during cleanup." }
        [IO.File]::Delete($ledger)
    }

    $parents = @{}
    foreach ($relative in $owned.Keys) {
        for ($folder = [IO.Path]::GetDirectoryName($owned[$relative]); $folder -ne $root; $folder = [IO.Path]::GetDirectoryName($folder)) {
            $parents[$folder] = $true
        }
    }
    foreach ($folder in $parents.Keys | Sort-Object Length -Descending) {
        Assert-Directory $folder
        $directory = Find-Item $folder
        if ($null -ne $directory -and !($directory.EnumerateFileSystemInfos() | Select-Object -First 1)) {
            [IO.Directory]::Delete($folder, $false)
        }
    }
    exit 0
} catch {
    [Console]::Write($_.Exception.Message)
    exit 2
}
