# 本地屏幕 OCR 组件

复用 MinerU 本机 4.0.5 源码快照的 PP-OCRv6 ONNX 路径（2026-11-10），只包含 OCR 推理所需子集。上游：https://github.com/opendatalab/MinerU 。`LICENSE-MinerU.md` 是原许可，附加条款保留；不能称为纯 Apache 2.0。

vendor 的复制来源为 `mineru/model/ocr/{pp_ocr_v6_onnx,db_postprocess,geometry,image,resources,seal_crop,seal_det_warp}.py`、`mineru/model/runtime/onnx.py` 和 `mineru/model/_internal/pytorchocr/data/imaug/operators.py`。保留版权头；仅调整平铺包相对导入、把 geometry 中只用于注解的 BBox 改为本地类型别名，并移除 image 中未使用的公式遮罩函数及其 DocVortex 导入，避免带入 PDF/Office 流水线。检测、裁剪、识别和解码算法未重写。

CPU ONNX Runtime，4 线程，保留低分行。stdin/stdout 为 UTF-8 JSON lines，先返回 `{"ready":true}`；请求 `{"image":"PNG base64"}`，响应含 `lines` 或固定错误码。只有启动时读取本地模型/字符表；每请求不写文件、不联网，异常不输出原文。取消由主程序终止其自建组件进程，空闲 5 分钟回收。

构建在隔离 E 盘 Python 3.12 环境安装 `pyinstaller==6.22.3`（官方 PyPI），只读引用已有 RAG venv 的依赖；不复制整个 venv、不改 RAG。PyInstaller 的打包例外允许任意许可证的生成物，但各依赖许可仍须保留：https://pyinstaller.org/en/stable/license.html 。跨环境 pathex 在 PyInstaller 6 有弃用警告，因此锁定该构建版本，未来升级前重新验证。

```powershell
& 'D:\RAG\ocr\mineru\.venv\Scripts\python.exe' -m venv artifacts/ocr-build-env
& 'artifacts/ocr-build-env/Scripts/python.exe' -m pip install pyinstaller==6.22.3
powershell -NoProfile -ExecutionPolicy Bypass -File tools/OcrHost/build.ps1
```

脚本使用 Windows PowerShell 5 时保存为 UTF-8 BOM；新脚本包含中文注释，不能按本地代码页误读。

生成 `build/ocr-dist/Jianyi.OcrHost`，包含独立运行时、两份用户已有 ONNX 模型和配套字符表。权重原模型卡：[检测](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det_onnx)、[识别](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx)，均标 Apache 2.0；本地复制件以 SHA-256 记录，未宣称与官方分发逐字节一致。公开再分发前仍需复核整包许可。

将组件放在 exe 旁 `OcrHost`，或在右键菜单选择组件目录，再显式启用「屏幕 OCR」。不自动下载模型。安装包通过可选 NSIS `OcrSourceDir` 包含此组件，用户需勾选；不改变应用默认关闭状态。

## 0.7 多模型与公开源码构建

运行组件支持 `--model-directory` 加载 `ocr.json`，四组推荐权重共用一份运行环境；OCR Profile 规范见 `docs/model-import-format-0.7.md`。不需要在用户机器安装完整 MinerU 或 Python 开发环境。

公开源码只提供 worker 和已保留原许可的适配代码，不附模型、Python venv 或可分发组件二进制。托管构建不依赖这些文件。制作 OCR 组件时，应显式传入自己的已配置环境，例如：

```powershell
./tools/OcrHost/build.ps1 -MineruRoot C:/dev/mineru -BuildPython C:/dev/ocr-build-env/Scripts/python.exe -OutputName ocr-dist
```

`MineruRoot` 需要脚本所用的 `src`、`.venv`、`models` 资源。它不是下载仓库后即可无外部资源运行的一键组件构建；首次公开二进制发布前还需建立固定上游快照、依赖与模型的可重现构建。旧段落中的本机路径只记录曾使用的验证环境，不是其他开发者需要照搬的路径。
