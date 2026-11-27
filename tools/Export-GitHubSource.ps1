#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$ArchiveFile = ''
)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
[xml]$project=Get-Content -LiteralPath (Join-Path $repository 'src/Thanslator.App/Thanslator.App.csproj') -Raw
$version=@($project.Project.PropertyGroup.Version | Where-Object {$_})[0]
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repository ('build/github-preparation/source-'+$stamp)}
if(-not $ArchiveFile){$ArchiveFile=Join-Path $repository ('dist/Jianyi-Source-'+$version+'-github-'+$stamp+'.zip')}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$archive=[IO.Path]::GetFullPath($ArchiveFile)
if(Test-Path -LiteralPath $output){throw '源码输出目录已存在，请使用新的目录；不会覆盖旧快照。'}
if(Test-Path -LiteralPath $archive){throw '源码 ZIP 已存在，请使用新的文件名。'}
if($output -eq $repository -or $repository.StartsWith($output.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '输出目录不能是工程本身或其父目录。'}

$info=New-Object Diagnostics.ProcessStartInfo
$info.FileName=(Get-Command git -ErrorAction Stop).Source
$info.Arguments='-c core.quotepath=false ls-files -z --cached --others --exclude-standard'
$info.WorkingDirectory=$repository
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true
$info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
$process=[Diagnostics.Process]::Start($info)
$listed=$process.StandardOutput.ReadToEnd()
$process.WaitForExit()
if($process.ExitCode -ne 0){throw '请从已初始化的源代码 Git 仓库导出。'}
$process.Dispose()

$rootFiles=@('README.md','CONTRIBUTING.md','SECURITY.md','CODE_OF_CONDUCT.md','LICENSE','NOTICE',
    'agents.md','AGENTS.md','product.md','design.md','Thanslator.slnx','global.json','NuGet.Config','Directory.Build.props',
    '.gitignore','.gitattributes','.editorconfig')
$records=@()
foreach($relative in ($listed.Split([char]0) | Where-Object {$_} | Sort-Object -Unique)){
    $normalized=$relative.Replace('\','/')
    $allowed=$rootFiles -contains $normalized -or $normalized -match '^(src|tests|tools|installer|docs|\.github)/' -or
        $normalized -match '^assets/(licenses|overrides)/' -or $normalized -match '^assets/dictionary/(README|THIRD_PARTY_NOTICES)\.md$'
    if(-not $allowed){continue}
    if($normalized -match '(^|/)(bin|obj|__pycache__|\.venv|\.git|node_modules)/' -or
       $normalized -match '^docs/(superpowers/|commercialization-roadmap\.md$|distribution-and-monetization\.md$)' -or
       $normalized -match '\.(exe|dll|lib|pdb|bin|gguf|onnx|safetensors|spm|model|db|sqlite|pem|key|pfx|p12|zip|7z|pyc|pyo)$'){continue}
    $source=[IO.Path]::GetFullPath((Join-Path $repository $relative))
    if(-not $source.StartsWith($repository.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '源文件超出工程目录。'}
    if(-not(Test-Path -LiteralPath $source -PathType Leaf)){continue}
    $item=Get-Item -LiteralPath $source -Force
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw ('不导出符号链接：'+$normalized)}
    if($item.Length -gt 10MB){throw ('待公开文件大于 10 MiB，请先核对：'+$normalized)}
    $target=Join-Path $output $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    $records += [pscustomobject]@{path=$normalized;bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
}
if(-not(Test-Path -LiteralPath (Join-Path $output 'Thanslator.slnx')) -or -not(Test-Path -LiteralPath (Join-Path $output '.github/workflows/ci.yml'))){throw '源码输出缺少解决方案或 CI。'}
$license=if(Test-Path -LiteralPath (Join-Path $output 'LICENSE')){'selected'}else{'proposal-only'}
[pscustomobject]@{version=$version;createdUtc=[DateTime]::UtcNow.ToString('o');licenseStatus=$license;files=$records} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'SOURCE_MANIFEST.json') -Encoding utf8
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($output,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
[pscustomobject]@{source=$output;archive=$archive;files=$records.Count;licenseStatus=$license;bytes=(Get-Item -LiteralPath $archive).Length;sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash} | ConvertTo-Json -Compress
