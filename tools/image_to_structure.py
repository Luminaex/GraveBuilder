"""
Converts an image into a Graveyard Orbit hex-grid "structure" (map/contract) that can be
loaded via GraveBuilder's StructuresInjectPatch.cs — the same proven new-contract
injection pattern already confirmed working in-game, no new C# code needed.

Usage:
    python image_to_structure.py <image_path> <structure_id> [--size N] [--invert]

Outputs two files into out/<structure_id>/:
    <structure_id>.json        (StructureDef metadata)
    <structure_id>_cells.json  (MegastructureData-format cell data)

Hex coordinate math matches the game's own HexLayout.OffsetToAxial(col, row) =
(col - floor(row/2), row) exactly, so the image comes out as a clean rectangle on the hex
grid instead of a sheared parallelogram (the naive mistake with hex grids).

Pixel -> tile mapping:
    - Alpha channel (if present): pixels with alpha below a threshold become holes (no
      tile) instead of substrate, so a cutout/logo image renders as its actual silhouette
      on the hex grid rather than always being a filled rectangle.
    - --color-mode palette (default): the cell "layer" field (1-5) is not just a density
      value in the real game — GraveyardOrbit.ColourMath.ColourOfLayer maps it directly to
      one of 5 real ore colours (1=Black, 2=Blue, 3=Pink, 4=Gold, 5=Purple), each with its
      own real in-game weakness rules (a laser only does full damage to its matching colour,
      or to Black which is colour-neutral; anything else takes 25% damage). This mode does a
      true nearest-colour match in RGB space against all 5 real ore colours (Black included
      as a genuine palette entry, not a separate hue/saturation threshold), with
      Floyd-Steinberg error-diffusion dithering so gradients/photos approximate far more than
      5 flat colours instead of banding (disable with --no-dither).
    - --color-mode luminance: the old behaviour — darker pixels become a higher layer
      number, lighter pixels lower, ignoring hue entirely. --invert flips the direction.

Use --preview to also write a PNG of the quantized/dithered result next to the source image,
so colour-accuracy changes can be checked by eye without booting the game at all.
"""
import argparse
import colorsys
import json
import math
import os

from PIL import Image, ImageOps

from go_palette import BLACK_RGB, HUE_PALETTE_RGB, PALETTE_RGB


def offset_to_axial(col, row):
    return col - math.floor(row / 2), row


def _redmean_dist_sq(c1, c2):
    # "redmean" weighted RGB distance — a cheap, well-known approximation of perceptual
    # colour difference that's much closer to how human vision weights colour than plain
    # Euclidean RGB distance (which over-values blue differences and under-values red ones).
    r1, g1, b1 = c1
    r2, g2, b2 = c2
    rmean = (r1 + r2) / 2.0
    dr, dg, db = r1 - r2, g1 - g2, b1 - b2
    return (2 + rmean / 256.0) * dr * dr + 4 * dg * dg + (2 + (255 - rmean) / 256.0) * db * db


def nearest_hue_layer(rgb):
    best_layer, best_dist = 2, None
    for layer, target in HUE_PALETTE_RGB.items():
        d = _redmean_dist_sq(rgb, target)
        if best_dist is None or d < best_dist:
            best_dist, best_layer = d, layer
    return best_layer


def classify_pixel(r, g, b):
    """Returns (layer, is_black). Black routing stays an explicit saturation/value gate, not
    part of the nearest-colour search — tried folding Black into the same redmean search as
    the 4 hues and it broke grayscale/desaturated images outright (confirmed on the smiley
    test image: a solid dark face turned into scattered coloured dithering noise, since a
    truly gray pixel has no real hue information and can read as "nearest" to any of the 4
    hue colours almost arbitrarily). Saturated pixels still get the improved, value-aware
    redmean search instead of the old hue-only match."""
    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    if v < 0.18 or s < 0.15:
        return 1, True
    return nearest_hue_layer((r, g, b)), False


def classify_palette_image(img, new_w, new_h, alpha_threshold, dither):
    """Returns (layer_grid, preview_img) — layer_grid[row][col] is None for holes."""
    pixels = img.load()
    # Float RGB working buffer so dithering error can accumulate without quantizing early.
    rgb_buf = [[list(pixels[c, r][:3]) for c in range(new_w)] for r in range(new_h)]
    layer_grid = [[None] * new_w for _ in range(new_h)]
    preview = Image.new("RGBA", (new_w, new_h), (0, 0, 0, 0))
    prev_px = preview.load()

    for row in range(new_h):
        for col in range(new_w):
            if pixels[col, row][3] < alpha_threshold:
                continue  # hole — no tile, no colour, no error to diffuse
            old = rgb_buf[row][col]
            layer, is_black = classify_pixel(*(max(0, min(255, round(c))) for c in old))
            layer_grid[row][col] = layer
            target = PALETTE_RGB[layer]
            prev_px[col, row] = (int(target[0]), int(target[1]), int(target[2]), 255)
            if not dither or is_black:
                continue  # no meaningful hue error to spread for a Black-gated pixel
            err = [old[i] - target[i] for i in range(3)]

            def diffuse(c, r, weight):
                if 0 <= c < new_w and 0 <= r < new_h and pixels[c, r][3] >= alpha_threshold:
                    for i in range(3):
                        # Clamping to the representable 0-255 range (standard practice for
                        # error-diffusion dithering) matters a lot more than usual here: the
                        # palette only has 5 colours with real gaps between them (no green/
                        # cyan/true-red target at all), so a run of source pixels that are
                        # genuinely far from every target can otherwise accumulate unbounded
                        # error and eventually tip the whole run over to a worse-matching
                        # colour than plain nearest-match would've picked — confirmed via a
                        # synthetic hue-wheel test image where a cyan/green stretch that
                        # nearest-match correctly reads as Blue got dragged into Pink instead
                        # once dithering was added without this clamp.
                        rgb_buf[r][c][i] = max(0.0, min(255.0, rgb_buf[r][c][i] + err[i] * weight))

            diffuse(col + 1, row, 7 / 16)
            diffuse(col - 1, row + 1, 3 / 16)
            diffuse(col, row + 1, 5 / 16)
            diffuse(col + 1, row + 1, 1 / 16)

    return layer_grid, preview


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("image_path")
    ap.add_argument("structure_id")
    ap.add_argument("--size", type=int, default=140, help="max grid dimension in hex cells (default 140)")
    ap.add_argument("--color-mode", choices=["palette", "luminance"], default="palette",
                     help="palette (default): match the 5 real ore colours by hue. luminance: old brightness-only mode.")
    ap.add_argument("--invert", action="store_true", help="invert brightness->layer mapping (luminance mode only)")
    ap.add_argument("--no-dither", action="store_true", help="disable Floyd-Steinberg dithering (palette mode only)")
    ap.add_argument("--preview", action="store_true", help="also write a PNG preview of the quantized result (palette mode only)")
    ap.add_argument("--alpha-threshold", type=int, default=16, help="alpha below this = hole (0-255)")
    ap.add_argument("--name", default=None, help="display name (default: derived from filename)")
    ap.add_argument("--starting-money", type=int, default=500)
    args = ap.parse_args()

    img = Image.open(args.image_path)
    img = img.convert("RGBA")

    w, h = img.size
    # A hex column step is ~1.71875 world units wide, a hex row step is ~1.5 units tall
    # (HexLayout constants) — columns cover MORE physical distance per cell than rows do,
    # so to keep the source image's true aspect ratio on screen we need relatively MORE
    # rows than a naive 1:1 pixel mapping, not fewer. Solve for (new_w, new_h) such that
    # new_w*1.71875 / (new_h*1.5) == w/h, capping the longer hex-unit axis at --size.
    col_unit, row_unit = 1.71875, 1.5
    scale = args.size / max(w / col_unit, h / row_unit)
    new_w = max(1, round(scale * w / col_unit))
    new_h = max(1, round(scale * h / row_unit))
    img = img.resize((new_w, new_h), Image.LANCZOS)

    alpha = img.getchannel("A") if "A" in img.getbands() else None
    mask = alpha.point(lambda a: 255 if a >= args.alpha_threshold else 0) if alpha else None

    gray = None
    layer_grid = None
    preview_img = None
    if args.color_mode == "luminance":
        # Stretch the tonal range before quantizing to 5 layer buckets — a flat/low-contrast
        # source (common in portraits/line art) otherwise collapses into 1-2 buckets and
        # loses detail. Mask out hole pixels so a transparent background doesn't skew it.
        gray = ImageOps.autocontrast(ImageOps.grayscale(img), cutoff=1, mask=mask)
        gray_pixels = gray.load()
    else:
        layer_grid, preview_img = classify_palette_image(
            img, new_w, new_h, args.alpha_threshold, dither=not args.no_dither)

    q_list, r_list, alt_list, layer_list = [], [], [], []
    q_vals, r_vals = [], []

    pixels = img.load()
    for row in range(new_h):
        for col in range(new_w):
            a_px = pixels[col, row][3]
            if a_px < args.alpha_threshold:
                continue  # hole — no tile here
            if args.color_mode == "palette":
                layer = layer_grid[row][col]
            else:
                lum = gray_pixels[col, row] / 255.0
                if args.invert:
                    lum = 1.0 - lum
                layer = max(1, min(5, round(1 + (1.0 - lum) * 4)))
            q, r = offset_to_axial(col, row)
            q_list.append(q)
            r_list.append(r)
            alt_list.append(0)
            layer_list.append(layer)
            q_vals.append(q)
            r_vals.append(r)

    if not q_list:
        raise SystemExit("No tiles produced — image is fully transparent, lower --alpha-threshold.")

    # Centre the structure near the origin, matching how real structures look.
    q_mid = round((min(q_vals) + max(q_vals)) / 2)
    r_mid = round((min(r_vals) + max(r_vals)) / 2)
    q_list = [q - q_mid for q in q_list]
    r_list = [r - r_mid for r in r_list]

    cells = {
        "source": f"image_to_structure:{os.path.basename(args.image_path)}",
        "count": len(q_list),
        "bounds": {"q": [min(q_list), max(q_list)], "r": [min(r_list), max(r_list)]},
        "q": q_list,
        "r": r_list,
        "alt": alt_list,
        "layer": layer_list,
    }

    display_name = args.name or os.path.splitext(os.path.basename(args.image_path))[0].replace("_", " ").title()
    struct_def = {
        "id": args.structure_id,
        "display_name": display_name,
        "description": f"Imported from {os.path.basename(args.image_path)} — clean the picture.",
        "cells_file": f"{args.structure_id}_cells.json",
        "starting_money": args.starting_money,
        "sealed_layers": True,
        "bands": [{"layer": 1, "hp_sealed": 75}],
        "glyph_count": 0,
        "green_count": -1,
    }

    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "mods", "GraveBuilder", "content", "structures", "images", args.structure_id)
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)

    with open(os.path.join(out_dir, f"{args.structure_id}.json"), "w") as f:
        json.dump(struct_def, f, indent=2)
    with open(os.path.join(out_dir, f"{args.structure_id}_cells.json"), "w") as f:
        json.dump(cells, f)

    print(f"Wrote {len(q_list)} cells ({new_w}x{new_h} grid) to {out_dir}")
    print(f"  bounds q[{cells['bounds']['q']}] r[{cells['bounds']['r']}]")

    if args.preview:
        if preview_img is None:
            print("  --preview only applies to --color-mode palette, skipped.")
        else:
            # Upscale with nearest-neighbour so individual quantized pixels/dither pattern
            # stay crisp (sharp blocks) instead of blurring into a smooth gradient, which
            # would misrepresent how a dithered flat-colour palette actually looks.
            preview_scale = max(1, 512 // max(new_w, new_h))
            preview_out = preview_img.resize(
                (new_w * preview_scale, new_h * preview_scale), Image.NEAREST)
            preview_path = os.path.join(out_dir, f"{args.structure_id}_preview.png")
            preview_out.save(preview_path)
            print(f"  Preview written to {preview_path}")


if __name__ == "__main__":
    main()
