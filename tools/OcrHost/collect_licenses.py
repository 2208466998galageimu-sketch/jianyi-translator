"""Collect local package notices without changing the source environment."""
import hashlib
import importlib.metadata as metadata
import json
from pathlib import Path
import shutil
import sys

destination = Path(sys.argv[1]).resolve()
notices = destination / "ThirdPartyNotices"
versions = {}
for name in ["numpy", "opencv-python", "onnxruntime", "shapely", "pyclipper", "loguru", "tqdm", "pillow", "colorama", "typing_extensions"]:
    distribution = metadata.distribution(name)
    versions[name] = distribution.version
    for file in distribution.files or []:
        if not any(word in file.name.lower() for word in ["license", "copying", "notice"]):
            continue
        source = Path(distribution.locate_file(file))
        target = (notices / name / str(file)).resolve()
        if destination not in target.parents or not source.is_file():
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
python_license = Path(sys.base_prefix) / "LICENSE.txt"
if python_license.exists():
    shutil.copy2(python_license, destination / "LICENSE-Python.txt")
manifest = {"python": sys.version.split()[0], "dependencies": versions,
    "models": {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in (destination / "models").iterdir() if file.is_file()}}
(destination / "component-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
