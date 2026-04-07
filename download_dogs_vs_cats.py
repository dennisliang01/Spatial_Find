r"""
Download the Kaggle Dogs vs. Cats dataset and organize it into the directory
layout expected by server.py:

    Assets/StreamingAssets/dogs_vs_cats/{cat,dog}/{filename}.jpg

Prerequisites:
    pip install kaggle

    You also need a Kaggle API token:
    1. Go to https://www.kaggle.com/settings  -> "Create New Token"
    2. Save the downloaded kaggle.json to:
       - Windows:  %USERPROFILE%\.kaggle\kaggle.json
       - Linux/Mac: ~/.kaggle/kaggle.json
    3. Accept the competition rules once at:
       https://www.kaggle.com/competitions/dogs-vs-cats/rules

Usage:
    python download_dogs_vs_cats.py
    python download_dogs_vs_cats.py --output ./Assets/StreamingAssets/dogs_vs_cats
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import zipfile
from pathlib import Path

_SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_OUTPUT = _SCRIPT_DIR / "Assets" / "StreamingAssets" / "dogs_vs_cats"
COMPETITION = "dogs-vs-cats"


def download_competition_data(dest: Path) -> Path:
    """Download the competition zip via the Kaggle API. Returns path to the zip."""
    try:
        from kaggle.api.kaggle_api_extended import KaggleApi
    except ImportError:
        raise SystemExit(
            "kaggle package not installed. Run:  pip install kaggle"
        )

    api = KaggleApi()
    try:
        api.authenticate()
    except Exception:
        raise SystemExit(
            "Kaggle authentication failed.\n"
            "1. Go to https://www.kaggle.com/settings -> 'Create New Token'\n"
            "2. Place kaggle.json in  ~/.kaggle/kaggle.json  (Linux/Mac)\n"
            "   or  %USERPROFILE%\\.kaggle\\kaggle.json  (Windows)\n"
            "3. Accept competition rules at:\n"
            "   https://www.kaggle.com/competitions/dogs-vs-cats/rules"
        )

    dest.mkdir(parents=True, exist_ok=True)
    print(f"Downloading {COMPETITION} data to {dest} ...")
    api.competition_download_files(COMPETITION, path=str(dest), quiet=False)

    zip_path = dest / f"{COMPETITION}.zip"
    if not zip_path.exists():
        zips = list(dest.glob("*.zip"))
        if zips:
            zip_path = zips[0]
        else:
            raise FileNotFoundError(f"No zip found in {dest} after download")
    return zip_path


def extract_and_organize(zip_path: Path, out_root: Path) -> None:
    """Extract the nested zips and sort images into cat/ and dog/ folders."""
    tmp_dir = out_root / "_tmp_extract"
    tmp_dir.mkdir(parents=True, exist_ok=True)

    print(f"Extracting {zip_path.name} ...")
    with zipfile.ZipFile(zip_path, "r") as zf:
        zf.extractall(tmp_dir)

    train_zip = tmp_dir / "train.zip"
    if train_zip.exists():
        print("Extracting train.zip ...")
        with zipfile.ZipFile(train_zip, "r") as zf:
            zf.extractall(tmp_dir)

    train_dir = tmp_dir / "train"
    if not train_dir.is_dir():
        candidates = [d for d in tmp_dir.iterdir() if d.is_dir() and d.name != "_tmp_extract"]
        if candidates:
            train_dir = candidates[0]

    cat_dir = out_root / "cat"
    dog_dir = out_root / "dog"
    cat_dir.mkdir(parents=True, exist_ok=True)
    dog_dir.mkdir(parents=True, exist_ok=True)

    moved = 0
    for img in sorted(train_dir.iterdir()):
        if not img.is_file():
            continue
        name_lower = img.name.lower()
        if name_lower.startswith("cat"):
            shutil.move(str(img), str(cat_dir / img.name))
            moved += 1
        elif name_lower.startswith("dog"):
            shutil.move(str(img), str(dog_dir / img.name))
            moved += 1

    print(f"Organized {moved} images into cat/ and dog/")

    shutil.rmtree(tmp_dir, ignore_errors=True)
    if zip_path.exists():
        zip_path.unlink()
    print("Cleaned up temporary files.")


def write_metadata(out_root: Path) -> None:
    meta = {"cat": "cat", "dog": "dog"}
    meta_path = out_root / "metadata.json"
    meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    print(f"Metadata written to {meta_path}")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Download Kaggle Dogs vs. Cats and organize for Spatial Find"
    )
    parser.add_argument(
        "--output", "-o",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"Output directory (default: {DEFAULT_OUTPUT})",
    )
    args = parser.parse_args()
    out_root: Path = args.output.resolve()

    cat_dir = out_root / "cat"
    dog_dir = out_root / "dog"
    if cat_dir.exists() and dog_dir.exists():
        n_cat = sum(1 for f in cat_dir.iterdir() if f.is_file())
        n_dog = sum(1 for f in dog_dir.iterdir() if f.is_file())
        if n_cat > 0 and n_dog > 0:
            print(f"Dataset already present: {n_cat} cat, {n_dog} dog images in {out_root}")
            write_metadata(out_root)
            return

    zip_path = download_competition_data(out_root)
    extract_and_organize(zip_path, out_root)
    write_metadata(out_root)

    n_cat = sum(1 for f in cat_dir.iterdir() if f.is_file())
    n_dog = sum(1 for f in dog_dir.iterdir() if f.is_file())
    print(f"\nDone. {n_cat} cat + {n_dog} dog images saved to {out_root}")


if __name__ == "__main__":
    main()
