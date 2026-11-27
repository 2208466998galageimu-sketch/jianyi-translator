#Requires -Version 5.1
<#
.SYNOPSIS
    构建简译发布包并编译 NSIS 安装包。

.DESCRIPTION
    1. 跑测试（可 -SkipTests 跳过）；
    2. 清理并发布到 dist\M5-folder（self-contained win-x64）；
    3. 复制 LocalMtHost；
    4. 去掉 PDB、createdump.exe、非中文运行时资源；
    5. 解除 MOTW（Unblock-File）；
    6. 用 NSIS 生成 dist\Jianyi-Setup-<版本>.exe，并打印 SHA-256。

    默认复用 Tauri 下载的 NSIS（%LOCALAPPDATA%\tauri\NSIS），不需要另外安装。

.PARAMETER SkipTests
    跳过 dotnet test（沙箱里 testhost 可能无法启动时使用）。

.PARAMETER Makensis
    makensis.exe 的路径；默认自动查找。

.PARAMETER OcrComponentDirectory
    已构建的 OCR 组件目录；提供后加入默认不勾选的可选安装项，不自动启用 OCR。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [switch] $SkipTests,
    [string] $Makensis = '',
    [string] $OcrComponentDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

$appProject = Join-Path $repoRoot 'src\Thanslator.App\Thanslator.App.csproj'
$publishDir = Join-Path $repoRoot 'dist\M5-folder'
$hostSrc = Join-Path $repoRoot 'build\LocalMtHostMultilingual\Release'

if (-not $Makensis) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'tauri\NSIS\Bin\makensis.exe'),
        (Join-Path $env:LOCALAPPDATA 'tauri\NSIS\makensis.exe'),
        'C:\Program Files (x86)\NSIS\makensis.exe',
        'C:\Program Files\NSIS\makensis.exe'
    )
    $Makensis = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

Write-Host '== 1/6 测试 ==' -ForegroundColor Cyan
if (-not $SkipTests) {
    & $dotnet test (Join-Path $repoRoot 'Thanslator.slnx') -c Debug
    if ($LASTEXITCODE -ne 0) { throw '测试失败，终止发布' }
} else {
    Write-Host '已跳过测试'
}

Write-Host '== 2/6 结束旧进程并清理发布目录 ==' -ForegroundColor Cyan
Get-Process -Name 'Thanslator.App' -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host '== 3/6 还原（win-x64）==' -ForegroundColor Cyan
& $dotnet restore $appProject -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore 失败' }

Write-Host '== 4/6 发布 ==' -ForegroundColor Cyan
& $dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publishDir --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host '== 5/6 整理发布目录 ==' -ForegroundColor Cyan
$hostExe = Join-Path $hostSrc 'Thanslator.LocalMtHost.exe'
if (Test-Path -LiteralPath $hostExe) {
    $hostDst = Join-Path $publishDir 'LocalMtHost'
    New-Item -ItemType Directory -Force -Path $hostDst | Out-Null
    Copy-Item -LiteralPath $hostExe -Destination $hostDst -Force
    Copy-Item -LiteralPath (Join-Path $hostSrc 'ctranslate2.dll') -Destination $hostDst -Force
} else {
    Write-Warning "未找到 $hostExe，发布包将不含本地 MT Host"
}

Get-ChildItem -LiteralPath $publishDir -File -Filter '*.pdb' | Remove-Item -Force
$createdump = Join-Path $publishDir 'createdump.exe'
if (Test-Path -LiteralPath $createdump) { Remove-Item -LiteralPath $createdump -Force }

# 只保留中文运行时资源；其余语言可删（约 15 MB）
$nonChinese = 'cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr'
foreach ($lang in $nonChinese) {
    $dir = Join-Path $publishDir $lang
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

# 去掉 MOTW，避免自己拷给别人的副本被 SmartScreen 拦截
Get-ChildItem -LiteralPath $publishDir -Recurse -File | Unblock-File

# AI 沙箱会给工作区打 Low 完整性标签，新生成的文件会继承它。带 Low 标签的 exe
# 启动后会被压到低完整性，写不了 %LOCALAPPDATA%（安装时表现为「无法打开要写入的文件」）。
# 发布目录统一改回 Medium。
& icacls.exe $publishDir /setintegritylevel Medium /T /C | Out-Null

Write-Host '== 6/6 编译安装包 ==' -ForegroundColor Cyan
if ($Makensis) {
    $exe = Join-Path $publishDir 'Thanslator.App.exe'
    $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersionRaw
    if ($null -eq $version) { $version = [version]'0.5.0.0' }
    $revision = if ($version.Revision -ge 0) { $version.Revision } else { 0 }
    $v4 = '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, $version.Build, $revision
    $outFile = Join-Path $repoRoot "dist\Jianyi-Setup-$v4.exe"

    $compactArguments = @{ ReleaseDirectory=$publishDir; ReportPath=(Join-Path $repoRoot "build\release-optimization-$v4.json") }
    $ocrDirectory = $null
    if ($OcrComponentDirectory) {
        $ocrSource = (Resolve-Path -LiteralPath $OcrComponentDirectory).Path
        if (-not (Test-Path -LiteralPath (Join-Path $ocrSource 'Jianyi.OcrHost.exe'))) { throw 'OCR 组件目录不完整' }
        $ocrDirectory = Join-Path $repoRoot ("build\ocr-release-" + [Guid]::NewGuid().ToString('N') + "\Jianyi.OcrHost")
        $compactArguments.OcrSourceDirectory = $ocrSource
        $compactArguments.OcrOutputDirectory = $ocrDirectory
    }
    & (Join-Path $repoRoot 'tools\Optimize-Release.ps1') @compactArguments
    $installerSources = Join-Path $repoRoot 'installer'
    $legacyManifests = Join-Path $installerSources 'legacy'
    $nsisArguments = @('/INPUTCHARSET', 'UTF8', "/DAppVersion=$v4", "/DSourceDir=$publishDir", "/DOutputFile=$outFile",
        "/DInstallScriptDir=$installerSources", "/DLegacyManifestDir=$legacyManifests")
    if ($ocrDirectory) { $nsisArguments += "/DOcrSourceDir=$ocrDirectory" }
    & $Makensis @nsisArguments (Join-Path $repoRoot 'installer\Jianyi.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'NSIS 编译失败' }

    # 安装包本身也不能带 Low 标签，否则双击后同样写不了目标目录
    & icacls.exe $outFile /setintegritylevel Medium | Out-Null
} else {
    Write-Warning '未找到 makensis.exe。先运行一次 Tauri 构建（会下载 NSIS），或手动传 -Makensis "路径\makensis.exe"'
}

Write-Host '== SHA-256 ==' -ForegroundColor Cyan
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') -Filter 'Jianyi-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 | Format-List Algorithm, Hash, Path }
