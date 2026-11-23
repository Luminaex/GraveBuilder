"""
Generates a simple filled hex-disk structure at a given radius, purely to empirically find
the practical size ceiling for a Graveyard Orbit contract before building a real seeded map
generator around it.

Why this matters: TileField's constructor (TileField.cs:549-551) allocates its NativeArray<Tile>
sized off the BOUNDING BOX radius, not the actual painted cell count —
    R = max(|qMin|, |qMax|, |rMin|, |rMax|)
    width = 2*R + 1
    N = width * width
— so a map's real cost is quadratic in how far out its authored cells reach, regardless of
how sparse it is. This script fills a solid disk (the worst case for a given radius: dense,
not sparse) so the size tested here is a fair worst-case stand-in for "a map with bounding
radius R."

Usage:
    python gen_test_structure.py <structure_id> <radius> [--starting-money N]

Outputs <structure_id>.json + <structure_id>_cells.json into
mods/GraveBuilder/content/structures/images/<structure_id>/ (same convention as
image_to_structure.py), for manual copying into the live content/structures/ folder.
"""
import argparse
import json
import os
import random


def hex_distance(q0, r0, q1, r1):
    dq, dr = q0 - q1, r0 - r1
    return (abs(dq) + abs(dq + dr) + abs(dr)) // 2


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("structure_id")
    ap.add_argument("radius", type=int)
    ap.add_argument("--density", type=float, default=1.0,
                     help="fraction of cells within radius actually painted (1.0 = solid disk, the worst-case density test; lower = sparser, closer to a real cave-like generated map)")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--starting-money", type=int, default=500)
    args = ap.parse_args()

    rng = random.Random(args.seed)
    radius = args.radius
    q_list, r_list, alt_list, layer_list = [], [], [], []
    for r in range(-radius, radius + 1):
        for q in range(-radius, radius + 1):
            d = hex_distance(0, 0, q, r)
            if d <= radius and (args.density >= 1.0 or rng.random() < args.density):
                layer = max(1, min(5, 5 - (d * 5 // max(1, radius))))
                q_list.append(q)
                r_list.append(r)
                alt_list.append(0)
                layer_list.append(layer)

    cells = {
        "source": f"gen_test_structure:r{radius}",
        "count": len(q_list),
        "bounds": {"q": [-radius, radius], "r": [-radius, radius]},
        "q": q_list,
        "r": r_list,
        "alt": alt_list,
        "layer": layer_list,
    }
    struct_def = {
        "id": args.structure_id,
        "display_name": f"Size Test R{radius}",
        "description": f"Stress-test structure, bounding radius {radius}, {len(q_list)} cells.",
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

    width = 2 * radius + 1
    n_alloc = width * width
    print(f"radius={radius}  cells={len(q_list)}  TileField N (width^2)={n_alloc:,}")
    print(f"Wrote to {out_dir}")


if __name__ == "__main__":
    main()
