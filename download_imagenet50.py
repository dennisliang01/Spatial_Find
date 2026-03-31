"""
Download the Elriggs/imagenet-50-subset from HuggingFace and organize it into
the directory layout expected by server.py:

    assets/StreamingAssets/collague_images/{classname}/{filename}.JPEG

Prerequisites:
    pip install datasets Pillow tqdm

Usage:
    python download_imagenet50.py            # default output path
    python download_imagenet50.py --output ./assets/StreamingAssets/collague_images
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from datasets import load_dataset
from PIL import Image
from tqdm import tqdm

DATASET_ID = "Elriggs/imagenet-50-subset"

WNID_TO_CLASS: dict[str, str] = {
    "n01440764": "tench",
    "n01443537": "goldfish",
    "n01484850": "great_white_shark",
    "n01491361": "tiger_shark",
    "n01494475": "hammerhead",
    "n01496331": "electric_ray",
    "n01498041": "stingray",
    "n01514668": "cock",
    "n01514859": "hen",
    "n01518878": "ostrich",
    "n01530575": "brambling",
    "n01531178": "goldfinch",
    "n01532829": "house_finch",
    "n01534433": "junco",
    "n01537544": "indigo_bunting",
    "n01558993": "robin",
    "n01560419": "bulbul",
    "n01580077": "jay",
    "n01582220": "magpie",
    "n01592084": "chickadee",
    "n01601694": "water_ouzel",
    "n01608432": "kite",
    "n01614925": "bald_eagle",
    "n01616318": "vulture",
    "n01622779": "great_grey_owl",
    "n01629819": "european_fire_salamander",
    "n01630670": "common_newt",
    "n01631663": "eft",
    "n01632458": "spotted_salamander",
    "n01632777": "axolotl",
    "n01641577": "bullfrog",
    "n01644373": "tree_frog",
    "n01644900": "tailed_frog",
    "n01664065": "loggerhead",
    "n01665541": "leatherback_turtle",
    "n01667114": "mud_turtle",
    "n01667778": "terrapin",
    "n01669191": "box_turtle",
    "n01675722": "banded_gecko",
    "n01677366": "common_iguana",
    "n01682714": "american_chameleon",
    "n01685808": "whiptail",
    "n01687978": "agama",
    "n01688243": "frilled_lizard",
    "n01689811": "alligator_lizard",
    "n01692333": "gila_monster",
    "n01693334": "green_lizard",
    "n01694178": "african_chameleon",
    "n01695060": "komodo_dragon",
    "n01697457": "african_crocodile",
}

_SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_OUTPUT = _SCRIPT_DIR / "assets" / "StreamingAssets" / "collague_images"


def main() -> None:
    parser = argparse.ArgumentParser(description="Download Elriggs/imagenet-50-subset")
    parser.add_argument(
        "--output", "-o",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"Output directory (default: {DEFAULT_OUTPUT})",
    )
    args = parser.parse_args()
    out_root: Path = args.output.resolve()

    wnids = sorted(WNID_TO_CLASS.keys())
    label_to_wnid = {i: wnid for i, wnid in enumerate(wnids)}

    print(f"Loading dataset {DATASET_ID} (this downloads ~6 GB on first run)...")
    ds = load_dataset(DATASET_ID)

    for split_name in ds:
        split = ds[split_name]
        print(f"\nProcessing split '{split_name}' ({len(split)} images)...")

        for idx in tqdm(range(len(split)), desc=split_name):
            sample = split[idx]
            image: Image.Image = sample["image"]
            label: int = sample["label"]

            wnid = label_to_wnid.get(label)
            if wnid is None:
                continue
            classname = WNID_TO_CLASS[wnid]

            class_dir = out_root / classname
            class_dir.mkdir(parents=True, exist_ok=True)

            filename = f"{wnid}_{idx:06d}.JPEG"
            dest = class_dir / filename
            if dest.exists():
                continue

            if image.mode != "RGB":
                image = image.convert("RGB")
            image.save(dest, "JPEG", quality=95)

    total = sum(1 for _ in out_root.rglob("*.JPEG"))
    print(f"\nDone. {total} images saved to {out_root}")

    meta_path = out_root / "metadata.json"
    meta_path.write_text(json.dumps(WNID_TO_CLASS, indent=2), encoding="utf-8")
    print(f"Class mapping written to {meta_path}")


if __name__ == "__main__":
    main()
