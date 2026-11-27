"""Local OCR worker. JSON lines over inherited pipes; no files or network per request."""
import argparse
import base64
import json
import os
from pathlib import Path
import sys


def deny_network(event, args):
    if event in {"socket.connect", "socket.getaddrinfo"}:
        raise RuntimeError("ocr.offline")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-directory")
    args = parser.parse_args()
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
    sys.addaudithook(deny_network)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", PYTHONDONTWRITEBYTECODE="1",
                      OMP_NUM_THREADS="4", OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="4")
    from loguru import logger
    logger.remove()
    import cv2
    import numpy as np
    import onnxruntime as ort
    ort.disable_telemetry_events()
    from vendor.pp_ocr_v6_onnx import PPOCRv6ONNX

    root = Path(args.model_directory) if args.model_directory else Path(sys.executable).parent / "models"
    paths = [root / "ch_PP-OCRv6_tiny_det_infer.onnx", root / "ch_PP-OCRv6_small_rec_infer.onnx", root / "ppocrv6_dict.txt"]
    manifest = root / "ocr.json"
    if manifest.exists():
        if manifest.stat().st_size > 8192:
            raise ValueError("ocr.profile")
        profile = json.loads(manifest.read_text(encoding="utf-8"))
        if profile.get("formatVersion") != 1 or profile.get("engine") != "paddle-ctc":
            raise ValueError("ocr.profile")
        paths = []
        for key in ("detector", "recognizer", "characters"):
            relative = profile[key]
            if not isinstance(relative, str) or not relative or ":" in relative or any(p in ("", ".", "..") for p in relative.replace("\\", "/").split("/")):
                raise ValueError("ocr.path")
            path = root / relative
            if path.is_absolute() and not path.resolve().is_relative_to(root.resolve()):
                raise ValueError("ocr.path")
            if not path.is_file() or not 0 < path.stat().st_size <= 1024 ** 3:
                raise ValueError("ocr.file")
            paths.append(path)
    engine = PPOCRv6ONNX(str(paths[0]), str(paths[1]), dict_path=str(paths[2]),
        drop_score=0.0, enable_merge_det_boxes=False, intra_op_num_threads=4)
    print('{"ready":true}')
    while True:
        line = sys.stdin.readline(12 * 1024 * 1024 + 1)
        if not line:
            break
        if len(line) > 12 * 1024 * 1024:
            break
        image = None
        try:
            request = json.loads(line)
            encoded = request["image"]
            if not isinstance(encoded, str) or len(encoded) > 11 * 1024 * 1024:
                raise ValueError()
            data = base64.b64decode(encoded, validate=True)
            if not data.startswith(b"\x89PNG\r\n\x1a\n") or len(data) > 8 * 1024 * 1024:
                raise ValueError()
            # PNG IHDR dimensions are checked before allocating a decoded raster.
            width, height = int.from_bytes(data[16:20], "big"), int.from_bytes(data[20:24], "big")
            if not (0 < width <= 1600 and 0 < height <= 1000 and width * height <= 1500000):
                raise ValueError()
            image = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError()
            output = engine.ocr(image, tqdm_enable=False)[0] or []
            result = []
            for box, (text, score) in output[:256]:
                points = np.asarray(box)
                x, y = points.min(axis=0)
                right, bottom = points.max(axis=0)
                result.append(dict(text=text[:4096], x=float(x), y=float(y), width=float(right-x),
                                   height=float(bottom-y), confidence=float(score) if text.strip() else 0.0))
            print(json.dumps({"lines": result}, ensure_ascii=False, allow_nan=False))
        except Exception as exc:
            print(json.dumps({"error": "ocr.failed", "kind": type(exc).__name__, "module": getattr(exc, "name", None)}))
        finally:
            if image is not None:
                image.fill(0)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print('{"error":"ocr.unavailable"}', flush=True)
        sys.exit(1)
