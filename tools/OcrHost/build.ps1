param(
    [string]$MineruRoot = $env:JIANYI_MINERU_ROOT,
    [string]$BuildPython = 'python',
    [string]$OutputName = 'ocr-dist'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($MineruRoot)) { throw '请通过 -MineruRoot 或 JIANYI_MINERU_ROOT 指定已配置的源码、依赖和模型目录；见 tools/OcrHost/README.md' }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$env:PYINSTALLER_CONFIG_DIR = Join-Path $repoRoot 'build/ocr-cache'
if ($OutputName -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid OCR build name' }
$sourceRoot = Join-Path $MineruRoot 'src'
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONPATH = Join-Path $MineruRoot '.venv\Lib\site-packages'
$arguments = @('-B', '-m', 'PyInstaller', '--noconfirm', '--onedir', '--console', '--name', 'Jianyi.OcrHost',
    '--icon', (Join-Path $repoRoot 'src/Thanslator.App/Assets/app.ico'),
    '--paths', $PSScriptRoot, '--paths', $env:PYTHONPATH, '--distpath', (Join-Path $repoRoot "build/$OutputName"),
    '--workpath', (Join-Path $repoRoot 'build/ocr-work'), '--specpath', (Join-Path $repoRoot 'build/ocr-spec'))
foreach ($module in @('torch','torchvision','transformers','gradio','fastapi','matplotlib','scipy','pandas','IPython',
    'onnxruntime.quantization','onnxruntime.transformers','onnxruntime.tools','onnx','pytest')) {
    $arguments += @('--exclude-module', $module)
}
& $BuildPython @arguments (Join-Path $PSScriptRoot 'host.py')
if ($LASTEXITCODE -ne 0) { throw 'OCR bundle failed' }
$destination = Join-Path $repoRoot "build/$OutputName/Jianyi.OcrHost"
$modelDirectory = Join-Path $destination 'models'
New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null
$models = Join-Path $MineruRoot 'models/MinerU-4_models_onnx/OCR/paddleocr'
Copy-Item -LiteralPath (Join-Path $models 'ch_PP-OCRv6_tiny_det_infer.onnx'),(Join-Path $models 'ch_PP-OCRv6_small_rec_infer.onnx') -Destination $modelDirectory -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'mineru/model/ocr/data/ppocrv6_dict.txt') -Destination $modelDirectory -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE.md') -Destination (Join-Path $destination 'LICENSE-MinerU.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE-Apache-2.0.txt') -Destination $destination -Force
& (Join-Path $MineruRoot '.venv/Scripts/python.exe') -B (Join-Path $PSScriptRoot 'collect_licenses.py') $destination
if ($LASTEXITCODE -ne 0) { throw 'Collect OCR notices failed' }
# OCR 不使用视频解码，移除不需要的 FFmpeg 动态插件。
foreach ($plugin in Get-ChildItem -LiteralPath $destination -Recurse -File -Filter 'opencv_videoio_ffmpeg*.dll') {
    if (-not $plugin.FullName.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected plugin path' }
    Remove-Item -LiteralPath $plugin.FullName -Force
}
Get-ChildItem -LiteralPath $destination -Recurse -File | Measure-Object -Property Length -Sum
