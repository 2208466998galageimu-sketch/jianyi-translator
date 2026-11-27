[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$OcrSourceDirectory,
    [string]$OcrOutputDirectory,
    [Parameter(Mandatory)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$source = if ($OcrSourceDirectory) { (Resolve-Path -LiteralPath $OcrSourceDirectory).Path } else { $null }
$output = if ($source) { [IO.Path]::GetFullPath($OcrOutputDirectory) } else { $null }
if (-not $release.StartsWith($repository + '\dist\', [StringComparison]::OrdinalIgnoreCase) -and
    -not $release.StartsWith($repository + '\build\', [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录必须位于仓库 dist 或 build 内' }
if ($source -and -not $output.StartsWith($repository + '\build\', [StringComparison]::OrdinalIgnoreCase)) { throw '精简组件必须写入仓库 build 内' }
if ($source -and (Test-Path -LiteralPath $output)) { throw '目标组件已存在，不覆盖旧构建' }
$beforeApp = (Get-ChildItem -LiteralPath $release -Recurse -File | Measure-Object Length -Sum).Sum
$beforeOcr = if ($source) { (Get-ChildItem -LiteralPath $source -Recurse -File | Measure-Object Length -Sum).Sum } else { 0 }
$removed = [Collections.Generic.List[object]]::new()
function Remove-ReleaseFile([string]$file, [string]$reason) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return }
    $item = Get-Item -LiteralPath $file
    $removed.Add([pscustomobject]@{ path=$item.FullName; bytes=$item.Length; reason=$reason })
    Remove-Item -LiteralPath $item.FullName -Force
}
# These are design-time assemblies and XML IntelliSense documentation, not the WPF/WinForms runtime.
foreach ($file in @('System.Windows.Forms.Design.dll','System.Windows.Forms.Design.Editors.dll',
    'Microsoft.Web.WebView2.WinForms.dll','Microsoft.Web.WebView2.Core.xml',
    'Microsoft.Web.WebView2.Wpf.xml','Microsoft.Web.WebView2.WinForms.xml')) {
    Remove-ReleaseFile (Join-Path $release $file) 'unused designer or developer documentation'
}
if ($source) {
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $output -Recurse
# Screenshot OCR receives PNG pixel arrays; AVIF decoding and NumPy's test extension are not used.
foreach ($file in @('_internal/PIL/_avif.cp312-win_amd64.pyd',
    '_internal/numpy/_core/_multiarray_tests.cp312-win_amd64.pyd')) {
    Remove-ReleaseFile (Join-Path $output $file) 'unused image codec or test extension'
}
# Keep package-local DLLs. Remove a flattened duplicate only when its bytes are identical.
foreach ($pair in @(
    @('_internal/libscipy_openblas64_-ed4f167a5330424524f45258e7ca2c8d.dll','_internal/numpy.libs/libscipy_openblas64_-ed4f167a5330424524f45258e7ca2c8d.dll'),
    @('_internal/geos-ae6efa0782962b98e358f10ea539ae5f.dll','_internal/Shapely.libs/geos-ae6efa0782962b98e358f10ea539ae5f.dll'),
    @('_internal/msvcp140-a4c2229bdc2a2a630acdc095b4d86008.dll','_internal/numpy.libs/msvcp140-a4c2229bdc2a2a630acdc095b4d86008.dll')
)) {
    $flat = Join-Path $output $pair[0]
    $package = Join-Path $output $pair[1]
    if ((Test-Path -LiteralPath $flat) -and (Test-Path -LiteralPath $package) -and
        (Get-FileHash -LiteralPath $flat).Hash -eq (Get-FileHash -LiteralPath $package).Hash) {
        Remove-ReleaseFile $flat 'byte-identical duplicate; package-local original retained'
    }
}
}
$afterApp = (Get-ChildItem -LiteralPath $release -Recurse -File | Measure-Object Length -Sum).Sum
$afterOcr = if ($source) { (Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object Length -Sum).Sum } else { 0 }
$report = [pscustomobject]@{
    beforeApp=$beforeApp; afterApp=$afterApp; beforeOcr=$beforeOcr; afterOcr=$afterOcr
    bytesSaved=($beforeApp+$beforeOcr-$afterApp-$afterOcr); removed=$removed
}
New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))) -Force | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$report | Select-Object beforeApp,afterApp,beforeOcr,afterOcr,bytesSaved | ConvertTo-Json

