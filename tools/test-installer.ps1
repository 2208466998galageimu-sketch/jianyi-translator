#Requires -Version 5.1
[CmdletBinding()]
param([string] $Makensis = '')

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $Makensis) { $Makensis = Join-Path $env:LOCALAPPDATA 'tauri\NSIS\Bin\makensis.exe' }
$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repoRoot "artifacts\installer-qa\$runId"
$registryPath = "Software\JianyiUpgradeQA\$runId"
$appKey = "$registryPath\App"
$uninstallKey = "$registryPath\Uninstall"
$exeName = 'Jianyi.UpgradeQA.exe'
$utf8 = New-Object System.Text.UTF8Encoding($true)
$results = New-Object System.Collections.Generic.List[string]
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Run-Setup([string] $File, [string] $Arguments) {
    if ($Arguments -notmatch '^/S (?:/D=|_\?=)(.+)$') { throw 'Unsupported fixture arguments' }
    $target = [IO.Path]::GetFullPath($Matches[1])
    Assert-That ($target.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture target escaped its workspace'
    Assert-That ([IO.Path]::GetFullPath($File).StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture executable escaped its workspace'
    Write-Host "Running fixture: $([IO.Path]::GetFileName($File))"
    $launch = New-Object Diagnostics.ProcessStartInfo
    $launch.FileName = $File
    $launch.Arguments = $Arguments
    $launch.UseShellExecute = $false
    $launch.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($launch)
    try {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw "Fixture timed out: $File"
        }
        Write-Host "Fixture exit: $($process.ExitCode)"
        return $process.ExitCode
    } finally { $process.Dispose() }
}

function Compile-Setup([string] $Script, [string[]] $Options) {
    & $Makensis /V2 @Options $Script
    if ($LASTEXITCODE -ne 0) { throw "NSIS fixture compile failed: $Script" }
    $output = ($Options | Where-Object { $_.StartsWith('/DOutputFile=') }).Substring(13)
    & icacls.exe $output /setintegritylevel Medium | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not explicitly set fixture integrity; using inherited label.' }
}

function Make-Legacy([string] $Case, [string] $Mode = '') {
    $registry.DeleteSubKeyTree($registryPath, $false)
    $directory = Join-Path $runRoot "$Case space 旧版"
    $installer = Join-Path $runRoot "$Case-legacy.exe"
    $options = @("/DOutputFile=$installer", "/DFixtureRoot=$directory", "/DSourceDir=$oldSource",
        "/DAppKey=$appKey", "/DUninstallKey=$uninstallKey")
    if ($Mode) { $options += "/D$Mode" }
    Compile-Setup (Join-Path $repoRoot 'tests\Installer\Legacy.nsi') $options
    Assert-That ((Run-Setup $installer "/S /D=$directory") -eq 0) 'Legacy setup failed'
    New-Item -ItemType Directory -Path (Join-Path $directory 'data\models') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'data\thanslator.db'), 'synthetic-settings', $utf8)
    [IO.File]::WriteAllText((Join-Path $directory 'data\models\keep.bin'), 'synthetic-model', $utf8)
    return $directory
}

function Assert-Data([string] $Directory) {
    Assert-That ([IO.File]::ReadAllText((Join-Path $Directory 'data\thanslator.db')) -eq 'synthetic-settings') 'Settings changed'
    Assert-That ([IO.File]::ReadAllText((Join-Path $Directory 'data\models\keep.bin')) -eq 'synthetic-model') 'Model changed'
}

$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,
    [Microsoft.Win32.RegistryView]::Registry32)
try {
    $oldSource = Join-Path $runRoot 'old-payload'
    $newSource = Join-Path $runRoot 'new-payload'
    $ocrSource = Join-Path $runRoot 'ocr-payload'
    foreach ($directory in @($oldSource, $newSource, $ocrSource)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $oldSource $exeName), 'old-program', $utf8)
    [IO.File]::WriteAllText((Join-Path $oldSource 'obsolete.dll'), 'obsolete', $utf8)
    [IO.File]::WriteAllText((Join-Path $newSource $exeName), 'new-program', $utf8)
    [IO.File]::WriteAllText((Join-Path $newSource 'runtime.dll'), 'new-runtime', $utf8)
    [IO.File]::WriteAllText((Join-Path $ocrSource 'Jianyi.OcrHost.exe'), 'new-ocr', $utf8)
    New-Item -ItemType Directory -Path (Join-Path $oldSource 'OcrHost') | Out-Null
    [IO.File]::WriteAllText((Join-Path $oldSource 'OcrHost\Jianyi.OcrHost.exe'), 'old-ocr', $utf8)
    New-Item -ItemType Directory -Path (Join-Path $oldSource 'assets\dictionary') | Out-Null
    [IO.File]::WriteAllText((Join-Path $oldSource 'assets\dictionary\dict-en-zh.sqlite'), 'old-dictionary', $utf8)

    $installerSource = Join-Path $repoRoot 'installer'
    $legacyManifests = Join-Path $runRoot 'legacy-manifests'
    New-Item -ItemType Directory -Path $legacyManifests | Out-Null
    $ps51 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $installerSource 'InstallFiles.ps1') -Mode Build -Source $oldSource -Output (Join-Path $legacyManifests '0.6.7.0.lst')
    Assert-That ($LASTEXITCODE -eq 0) 'Fixture manifest generation failed'

    # Rebrand only side effects; execute the real installer control flow.
    $script = [IO.File]::ReadAllText((Join-Path $installerSource 'Jianyi.nsi'))
    $script = $script.Replace('"Software\Jianyi"', '"' + $appKey + '"').Replace('Thanslator.App.exe', $exeName)
    $script = $script.Replace('Software\Microsoft\Windows\CurrentVersion\Uninstall\Jianyi', $uninstallKey)
    $script = $script.Replace('简译', "Jianyi Upgrade QA $runId")
    $script = $script.Replace('..\src\Thanslator.App\Assets\app.ico', (Join-Path $repoRoot 'src\Thanslator.App\Assets\app.ico'))
    $script = $script.Replace('$LOCALAPPDATA\Programs\Jianyi', (Join-Path $runRoot 'default'))
    $script = [regex]::Replace($script, '(?m)^\s*(CreateShortcut|CreateDirectory|Delete|RMDir)\s+[^\r\n]*\$(DESKTOP|SMPROGRAMS)[^\r\n]*', '')
    $script = $script.Replace(' "CleanInstallData.ps1"', ' "' + (Join-Path $installerSource 'CleanInstallData.ps1') + '"')
    $diagnosticLog = Join-Path $runRoot 'installer-errors.txt'
    $script = $script.Replace('    DetailPrint "$2"', ('    FileOpen $5 "' + $diagnosticLog + '" a' + [Environment]::NewLine +
        '    FileWrite $5 "$0 $2$\r$\n"' + [Environment]::NewLine + '    FileClose $5'))
    Write-Host "Fixture directory: $runRoot"
    $testScript = Join-Path $runRoot 'Jianyi.nsi'
    [IO.File]::WriteAllText($testScript, $script, $utf8)
    $newInstaller = Join-Path $runRoot 'new-setup.exe'
    $options = @('/DAppVersion=0.8.1.0', "/DSourceDir=$newSource", "/DInstallScriptDir=$installerSource", "/DLegacyManifestDir=$legacyManifests")
    Compile-Setup $testScript ($options + @("/DOcrSourceDir=$ocrSource", "/DOutputFile=$newInstaller"))

    $mixed = Join-Path $runRoot 'fresh-with-user-file'
    New-Item -ItemType Directory -Path $mixed | Out-Null
    $mixedFile = Join-Path $mixed 'private.txt'
    [IO.File]::WriteAllText($mixedFile, 'user-file')
    Assert-That ((Run-Setup $newInstaller "/S /D=$mixed") -ne 0) 'Fresh setup accepted a shared directory'
    Assert-That ([IO.File]::ReadAllText($mixedFile) -eq 'user-file') 'Fresh setup changed an unrelated file'
    $results.Add('fresh shared directory rejected without changing user files')

    $fresh = Join-Path $runRoot 'fresh space 安装'
    Assert-That ((Run-Setup $newInstaller "/S /D=$fresh") -eq 0) 'Fresh install failed'
    Assert-That ([IO.File]::ReadAllText((Join-Path $fresh $exeName)) -eq 'new-program') 'Fresh payload missing'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $fresh 'OcrHost'))) 'Fresh OCR unexpectedly selected'
    Assert-That (Test-Path -LiteralPath (Join-Path $fresh 'jianyi-installed-files.lst')) 'Installed manifest missing'
    $results.Add('fresh setup records exact files and leaves OCR optional')

    $previous = Make-Legacy 'verified-mixed'
    $rootFile = Join-Path $previous 'notes.dll'
    $childFile = Join-Path $previous 'OcrHost\user-keep.txt'
    [IO.File]::WriteAllText($rootFile, 'personal root file')
    [IO.File]::WriteAllText($childFile, 'personal OCR folder file')
    $rootHash = (Get-FileHash -LiteralPath $rootFile).Hash
    $childHash = (Get-FileHash -LiteralPath $childFile).Hash
    $oldUninstallerHash = (Get-FileHash -LiteralPath (Join-Path $previous 'uninstall.exe')).Hash
    $different = Join-Path $runRoot 'must-not-relocate'
    Assert-That ((Run-Setup $newInstaller "/S /D=$different") -eq 0) 'Known previous release did not upgrade'
    Assert-That (-not (Test-Path -LiteralPath $different)) 'Upgrade moved away from retained data'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $previous 'obsolete.dll'))) 'Known obsolete program file was not cleaned'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $previous 'data\uninstalled.txt'))) 'Legacy uninstaller was launched'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $previous 'uninstall.exe'))) 'Legacy uninstaller remained executable'
    Assert-That ((Get-FileHash -LiteralPath (Join-Path $previous 'Jianyi-previous-uninstall.disabled')).Hash -eq $oldUninstallerHash) 'Legacy uninstaller bytes were changed'
    Assert-That ([IO.File]::ReadAllText((Join-Path $previous 'OcrHost\Jianyi.OcrHost.exe')) -eq 'new-ocr') 'Installed OCR was not retained'
    Assert-Data $previous
    $results.Add('known legacy upgrade in place without running old uninstaller; data and OCR retained')

    Assert-That ((Run-Setup $newInstaller "/S /D=$previous") -eq 0) 'Manifest-based reinstall failed'
    Assert-Data $previous
    $results.Add('manifest-based reinstall retains data')
    $uninstaller = Join-Path $runRoot 'uninstall-fixture.exe'
    Copy-Item -LiteralPath (Join-Path $previous 'Jianyi-uninstall.exe') -Destination $uninstaller
    Assert-That ((Run-Setup $uninstaller "/S _?=$previous") -eq 0) 'Manifest-based uninstall failed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $previous $exeName))) 'Installed executable was not removed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $previous 'OcrHost\Jianyi.OcrHost.exe'))) 'Installed OCR executable was not removed'
    Assert-That ((Get-FileHash -LiteralPath $rootFile).Hash -eq $rootHash) 'Root user file changed'
    Assert-That ((Get-FileHash -LiteralPath $childFile).Hash -eq $childHash) 'Nested user file changed'
    Assert-Data $previous
    $results.Add('shared root and OCR subdirectory user files stay byte-identical after upgrade/reinstall/uninstall')

    $previous = Make-Legacy 'unknown-version'
    $key = $registry.OpenSubKey($uninstallKey, $true)
    try { $key.SetValue('DisplayVersion', '99.99.99.0') } finally { $key.Dispose() }
    Assert-That ((Run-Setup $newInstaller "/S /D=$previous") -ne 0) 'Unknown legacy release was not blocked'
    Assert-That ([IO.File]::ReadAllText((Join-Path $previous $exeName)) -eq 'old-program') 'Unknown program changed'
    Assert-Data $previous
    $results.Add('unknown version stopped before edits')

    $previous = Make-Legacy 'shared-name'
    $collidingFile = Join-Path $previous 'runtime.dll'
    [IO.File]::WriteAllText($collidingFile, 'user-owned same name')
    Assert-That ((Run-Setup $newInstaller "/S /D=$previous") -ne 0) 'Same-name unrelated file was not detected'
    Assert-That ([IO.File]::ReadAllText($collidingFile) -eq 'user-owned same name') 'Same-name file changed'
    Assert-That ([IO.File]::ReadAllText((Join-Path $previous $exeName)) -eq 'old-program') 'Upgrade partially removed files before checking collisions'
    $results.Add('unrecognized name collision rejected before deleting program files')

    $baseInstaller = Join-Path $runRoot 'base-setup.exe'
    Compile-Setup $testScript ($options + @("/DOutputFile=$baseInstaller"))
    $previous = Make-Legacy 'keep-optional-components'
    $existingOcr = Join-Path $previous 'OcrHost\Jianyi.OcrHost.exe'
    $existingDictionary = Join-Path $previous 'assets\dictionary\dict-en-zh.sqlite'
    $personalOcrFile = Join-Path $previous 'OcrHost\custom.txt'
    [IO.File]::WriteAllText($personalOcrFile, 'personal OCR file', $utf8)
    foreach ($attempt in 1..2) {
        Assert-That ((Run-Setup $baseInstaller "/S /D=$previous") -eq 0) 'Base setup failed to upgrade a previous OCR installation'
        Assert-That ([IO.File]::ReadAllText((Join-Path $previous $exeName)) -eq 'new-program') 'Main program was not upgraded'
        Assert-That ([IO.File]::ReadAllText($existingOcr) -eq 'old-ocr') 'Installed OCR changed'
        Assert-That ([IO.File]::ReadAllText($existingDictionary) -eq 'old-dictionary') 'Installed dictionary changed'
        Assert-Data $previous
    }
    $baseUninstaller = Join-Path $runRoot 'uninstall-retained-fixture.exe'
    Copy-Item -LiteralPath (Join-Path $previous 'Jianyi-uninstall.exe') -Destination $baseUninstaller
    Assert-That ((Run-Setup $baseUninstaller "/S _?=$previous") -eq 0) 'Uninstall after retaining optional components failed'
    Assert-That (-not (Test-Path -LiteralPath $existingOcr)) 'Verified retained OCR was not uninstalled'
    Assert-That (-not (Test-Path -LiteralPath $existingDictionary)) 'Verified retained dictionary was not uninstalled'
    Assert-That ([IO.File]::ReadAllText($personalOcrFile) -eq 'personal OCR file') 'Uninstall changed an unrelated OCR file'
    Assert-Data $previous
    $results.Add('base setup upgrades legacy and registered OCR installations while retaining verified optional components')

    $cleanScript = Join-Path $runRoot 'Jianyi-clean.nsi'
    $clean = $script.Replace('  Call ReadOcrSelection', "  StrCpy " + '$CleanInstall 1' + [Environment]::NewLine + '  Call ReadOcrSelection')
    [IO.File]::WriteAllText($cleanScript, $clean, $utf8)
    $cleanInstaller = Join-Path $runRoot 'clean-setup.exe'
    Compile-Setup $cleanScript ($options + @("/DOcrSourceDir=$ocrSource", "/DOutputFile=$cleanInstaller"))
    $previous = Make-Legacy 'clean'
    Assert-That ((Run-Setup $cleanInstaller "/S /D=$previous") -eq 0) 'Confirmed clean install failed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $previous 'data'))) 'Confirmed clean install kept data'
    $results.Add('explicit clean install removes only approved data and known program files')

    [ordered]@{ runRoot=$runRoot; results=$results.ToArray() } | ConvertTo-Json -Depth 4 |
        Set-Content -Encoding UTF8 -LiteralPath (Join-Path $runRoot 'results.json')
    $results
} finally {
    if ($diagnosticLog -and (Test-Path -LiteralPath $diagnosticLog)) { Get-Content -LiteralPath $diagnosticLog }
    $registry.DeleteSubKeyTree($registryPath, $false)
    $registry.Dispose()
}
