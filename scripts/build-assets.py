#!/usr/bin/env python3
"""Build the desktop-pet sprite atlas from the reference sheet.

The reference image is a 3x4 contact sheet of the same yellow creature on a
uniform light-blue background.  This script chroma-keys that background,
trims each cell, and writes transparent PNGs plus a contact sheet for review.

Usage:
  python scripts/build-assets.py [reference.jpg] [output-dir]

The script is deterministic and only uses Pillow + numpy.  The generated files
are checked in so the plugin runs without Python.
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

# 3 columns x 4 rows.  The bottom strip carries the source watermark and is
# intentionally excluded by the crop.
COLS = 3
ROWS = 4
# The grid is not perfectly even: measured sprite starts are x=110/521/927 and
# y=83/464/901/1308.  Cell boundaries below leave a generous margin.
X_EDGES = [0, 426, 853, 1280]
# The bottom strip of the source carries a watermark; stop above it.
Y_EDGES = [0, 427, 854, 1281, 1615]

NAMES = [
    ["cheer", "idle", "open"],
    ["peace", "hold", "heart"],
    ["wave", "chew", "think"],
    ["sleepy", "shy", "walk"],
]

# The reference background is a single flat colour.  Distances are compared in
# sRGB; a soft ramp keeps antialiased edges from becoming hard cut-outs.
BG = np.array([165, 209, 254], dtype=np.float32)
ALPHA_LOW = 14.0
ALPHA_HIGH = 48.0
PAD = 10
TARGET_HEIGHT = 260


def key_background(cell: Image.Image) -> Image.Image:
    rgb = np.asarray(cell.convert("RGB")).astype(np.float32)
    dist = np.sqrt(((rgb - BG) ** 2).sum(axis=2))
    # 0 at/below ALPHA_LOW, 255 at/above ALPHA_HIGH, linear in between.
    alpha = np.clip((dist - ALPHA_LOW) / (ALPHA_HIGH - ALPHA_LOW), 0.0, 1.0)
    alpha = (alpha * 255.0).round().astype(np.uint8)

    # Pull the matte in by one pixel and feather it: this removes the pale
    # blue fringe that a naive key leaves around yellow edges.
    matte = Image.fromarray(alpha, mode="L")
    matte = matte.filter(ImageFilter.MinFilter(3))
    matte = matte.filter(ImageFilter.GaussianBlur(0.7))

    rgba = np.dstack([np.asarray(cell.convert("RGB")), np.asarray(matte)])
    out = Image.fromarray(rgba, mode="RGBA")

    # Trim to the visible body, then restore a small transparent margin so
    # rotation and squash animations never clip the silhouette.
    bbox = out.getchannel("A").getbbox()
    if bbox is None:
        raise RuntimeError("cell became fully transparent; check the key")
    left, top, right, bottom = bbox
    left = max(0, left - PAD)
    top = max(0, top - PAD)
    right = min(out.width, right + PAD)
    bottom = min(out.height, bottom + PAD)
    out = out.crop((left, top, right, bottom))

    # Normalize height while preserving aspect.
    if out.height != TARGET_HEIGHT:
        width = max(1, round(out.width * (TARGET_HEIGHT / out.height)))
        out = out.resize((width, TARGET_HEIGHT), Image.LANCZOS)
    return out


def main() -> int:
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    source = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(here), "..", "naiwa.jpg"
    )
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "pet", "assets", "sprites")
    os.makedirs(out_dir, exist_ok=True)

    sheet = Image.open(source).convert("RGB")
    if sheet.size != (1280, 1708):
        print(f"warning: expected 1280x1708, got {sheet.size}; scaling grid", file=sys.stderr)
        x_scale = sheet.width / 1280.0
        y_scale = sheet.height / 1708.0
        x_edges = [round(v * x_scale) for v in X_EDGES]
        y_edges = [round(v * y_scale) for v in Y_EDGES]
    else:
        x_edges, y_edges = X_EDGES, Y_EDGES

    manifest = {"version": 1, "spriteHeight": TARGET_HEIGHT, "sprites": {}}
    thumbs = []
    for row in range(ROWS):
        for col in range(COLS):
            name = NAMES[row][col]
            cell = sheet.crop((x_edges[col], y_edges[row], x_edges[col + 1], y_edges[row + 1]))
            sprite = key_background(cell)
            path = os.path.join(out_dir, f"{name}.png")
            sprite.save(path, "PNG", optimize=True)
            manifest["sprites"][name] = {
                "file": f"{name}.png",
                "width": sprite.width,
                "height": sprite.height,
            }
            thumbs.append((name, sprite.copy()))
            print(f"{name:8s} {sprite.width:3d}x{sprite.height:3d}  {os.path.getsize(path):6d} bytes")

    with open(os.path.join(out_dir, "sprites.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    # Contact sheet on a checkerboard so transparency is obvious.
    tile_w = max(s.width for _, s in thumbs) + 24
    tile_h = max(s.height for _, s in thumbs) + 24
    contact = Image.new("RGBA", (tile_w * COLS, tile_h * ROWS), (32, 32, 36, 255))
    checker = Image.new("RGBA", contact.size, (32, 32, 36, 255))
    px = checker.load()
    for y in range(checker.height):
        for x in range(checker.width):
            if ((x // 16) + (y // 16)) % 2 == 0:
                px[x, y] = (44, 44, 50, 255)
    contact = checker
    for index, (name, sprite) in enumerate(thumbs):
        col = index % COLS
        row = index // COLS
        x = col * tile_w + (tile_w - sprite.width) // 2
        y = row * tile_h + (tile_h - sprite.height) // 2
        contact.alpha_composite(sprite, (x, y))
    contact.save(os.path.join(out_dir, "contact-sheet.png"), "PNG", optimize=True)
    print(f"wrote {out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
