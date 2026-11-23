"""
Procedurally generates a wrecked-ship-shaped hex-grid structure from an integer seed —
same output format as image_to_structure.py (MegastructureData-compatible cells file +
StructureDef metadata), so it loads through the exact same proven injection pattern.

Design, in order of how the shape comes together:
1. An elongated capsule silhouette along the q axis (q = "length", r = "width") — ships read
   as ship-like mainly through aspect ratio and a tapered nose, not through fine detail.
2. The taper profile differs front (narrow, pointed bow) from back (blunter, blockier stern)
   — a symmetric ellipse reads as a blob/asteroid, not a ship.
3. The silhouette edge is perturbed by value noise (a small dependency-free Perlin-alike:
   hash the integer lattice, smoothstep-interpolate) so the hull outline is organic/irregular
   instead of a mathematically clean curve.
4. Random circular "breach" holes are carved out of the solid hull afterward — both for the
   damaged/wrecked look the theme calls for, AND because carving holes directly reduces
   actually-painted cell density, which this session's size testing confirmed is the real
   performance lever (TileField's allocation cost depends only on bounding radius, but visible
   lag at a given radius tracked painted-cell count, not the box itself — see
   gen_test_structure.py's docstring for the measurement this is built on).
5. Layer (= ore colour) assignment reads structural role, not noise: a central "spine" strip
   along the long axis gets a distinct colour (thicker/tougher reading), the outer rim
   (nearest the hull edge) gets another, and a handful of noise-seeded "deposit" blobs
   scattered through the body get a third — so there's real colour variety to mine through,
   not a flat single-colour fill.

Usage:
    python gen_wreck_structure.py <structure_id> <seed> [--length N] [--width N] [--preview]
"""
import argparse
import json
import math
import os
import random

from PIL import Image

from go_palette import PALETTE_RGB


class ValueNoise2D:
    """Minimal dependency-free smooth noise: hash the integer lattice to pseudo-random
    values, smoothstep-interpolate between them. Not true Perlin/Simplex, but organic enough
    for a hull edge/deposit mask at hex-cell resolution, with zero extra dependencies."""

    def __init__(self, seed):
        self.seed = seed

    def _lattice(self, ix, iy):
        h = (ix * 374761393 + iy * 668265263 + self.seed * 2147483647) & 0xFFFFFFFF
        h = (h ^ (h >> 13)) * 1274126177 & 0xFFFFFFFF
        h = h ^ (h >> 16)
        return (h & 0xFFFF) / 65535.0

    @staticmethod
    def _smooth(t):
        return t * t * (3 - 2 * t)

    def sample(self, x, y):
        ix, iy = math.floor(x), math.floor(y)
        fx, fy = self._smooth(x - ix), self._smooth(y - iy)
        a, b = self._lattice(ix, iy), self._lattice(ix + 1, iy)
        c, d = self._lattice(ix, iy + 1), self._lattice(ix + 1, iy + 1)
        top = a + (b - a) * fx
        bot = c + (d - c) * fx
        return top + (bot - top) * fy

    def fractal(self, x, y, octaves=3, lacunarity=2.0, gain=0.5):
        amp, freq, total, norm = 1.0, 1.0, 0.0, 0.0
        for _ in range(octaves):
            total += self.sample(x * freq, y * freq) * amp
            norm += amp
            amp *= gain
            freq *= lacunarity
        return total / norm


def hex_distance(q0, r0, q1, r1):
    dq, dr = q0 - q1, r0 - r1
    return (abs(dq) + abs(dq + dr) + abs(dr)) // 2


def width_profile(t, bow_break, nose_sharpness, stern_break, stern_bluntness):
    """t in [-1,1] along the length axis, -1=bow, +1=stern. Returns a 0-1 width multiplier.

    First prototype used a symmetric cosine taper on both ends — it read as a lens/eye/
    football shape, not a ship, because BOTH ends still ran a long, gradual taper all the
    way to a point; only the curve's steepness differed. What actually reads as "ship" needs
    the two ends to be structurally different, not just differently-curved versions of the
    same taper: stay near full width for most of the hull, pointed wedge bow only over the
    last stretch of the front, and a short, blunt, almost-flat cutoff at the back (an engine-
    block silhouette) instead of a second point. Confirmed by eye against the first version.
    """
    if t < 0:
        if t > -bow_break:
            return 1.0  # full width — the taper only starts near the very front
        local = (-t - bow_break) / (1.0 - bow_break)
        return max(0.0, math.cos(local * math.pi / 2)) ** nose_sharpness
    if t < stern_break:
        return 1.0 + 0.08 * (t / max(0.01, stern_break))  # slight widen toward the engines
    local = (t - stern_break) / max(0.01, 1.0 - stern_break)
    floor = 0.15 * stern_bluntness  # blunter seeds cut off to a flatter stub, not a point
    return max(floor, 1.0 - (1.0 - floor) * local)


def generate(seed, length, width, hole_count, hole_radius_frac, edge_noise_amp):
    rng = random.Random(seed)
    noise = ValueNoise2D(seed)
    bow_break = rng.uniform(0.35, 0.65)    # how much of the front stays full-width before tapering
    nose_sharpness = rng.uniform(0.6, 1.4)
    stern_break = rng.uniform(0.55, 0.8)   # how much of the back stays full-width before cutoff
    stern_bluntness = rng.uniform(0.3, 1.0)

    half_len = length / 2.0
    cells = []  # (q, r)
    for q in range(-math.ceil(half_len), math.ceil(half_len) + 1):
        t = q / half_len
        if abs(t) > 1.05:
            continue
        base_w = width_profile(t, bow_break, nose_sharpness, stern_break, stern_bluntness) * (width / 2.0)
        edge_wobble = 1.0 + edge_noise_amp * (noise.fractal(q * 0.15, 0, octaves=3) * 2 - 1)
        w = max(0.0, base_w * edge_wobble)
        r_range = math.ceil(w)
        for r in range(-r_range, r_range + 1):
            if abs(r) <= w:
                cells.append((q, r))

    cell_set = set(cells)

    # Carve damage holes: random centres roughly within the hull bounding box, each a
    # noise-perturbed circular breach (not a clean circle, so it still reads as damage
    # rather than a drilled hole).
    if cell_set:
        qs = [c[0] for c in cell_set]
        for _ in range(hole_count):
            cq = rng.randint(min(qs), max(qs))
            cr = rng.randint(-int(width), int(width))
            hole_r = max(1, round(hole_radius_frac * width * rng.uniform(0.5, 1.3)))
            for dq in range(-hole_r - 1, hole_r + 2):
                for dr in range(-hole_r - 1, hole_r + 2):
                    q, r = cq + dq, cr + dr
                    if (q, r) not in cell_set:
                        continue
                    d = hex_distance(cq, cr, q, r)
                    wobble = 1.0 + 0.4 * (noise.fractal(q * 0.3 + 50, r * 0.3 + 50, octaves=2) * 2 - 1)
                    if d <= hole_r * wobble:
                        cell_set.discard((q, r))

    if not cell_set:
        raise SystemExit("Generated an empty hull — try a larger --length/--width or different seed.")

    # Layer assignment — realigned against the real official wrecks (Hive, Mothership,
    # Cruise Liner, Leviathan, Labyrinth) and Lumi's own read of what each ore colour means
    # thematically in this game: Pink is hull ARMOUR and is the dominant body colour in every
    # official wreck, not a minor accent — the first version of this generator never used
    # Pink at all and made Black (meant to be plain structural fill) the dominant colour
    # instead, which is backwards. Blue reads as energy/conduit lines (so a branching network,
    # not a solid rim), Purple is energy infrastructure (the core/spine), Gold is shields and
    # can appear in real clusters rather than single-cell specks, Black is just low-importance
    # filler used sparingly.
    spine_r = max(1, round(width * 0.09))
    conduit_spacing = max(6, round(length / 14))
    result = {}
    for (q, r) in cell_set:
        t = q / half_len
        w_here = max(1.0, width_profile(t, bow_break, nose_sharpness, stern_break, stern_bluntness) * (width / 2.0))
        on_edge = any((q + dq, r + dr) not in cell_set for dq, dr in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1)))
        shield = noise.fractal(q * 0.12 + 200, r * 0.12 + 200, octaves=2) > 0.62
        on_spine = abs(r) <= spine_r
        # Conduits: thin branches running perpendicular off the spine at regular intervals
        # along the hull length, reaching about halfway to the current edge — reads as a
        # circuit/power-line network instead of a uniform ring.
        near_conduit = (not on_spine) and (q % conduit_spacing < 2) and abs(r) <= spine_r + w_here * 0.5
        if shield:
            layer = 4  # Gold — shield generator cluster
        elif on_spine:
            layer = 5  # Purple — energy infrastructure spine
        elif near_conduit:
            layer = 2  # Blue — conduit branch off the spine
        elif on_edge:
            layer = 3  # Pink — outer hull armour (dominant body colour)
        else:
            layer = 3 if noise.fractal(q * 0.2 + 400, r * 0.2 + 400, octaves=2) > 0.3 else 1  # mostly Pink body, Black filler pockets
        result[(q, r)] = layer

    q_list = [c[0] for c in result]
    r_list = [c[1] for c in result]
    layer_list = [result[c] for c in result]
    alt_list = [0] * len(q_list)
    return q_list, r_list, alt_list, layer_list


def render_preview(q_list, r_list, layer_list, scale=6):
    qmin, qmax = min(q_list), max(q_list)
    rmin, rmax = min(r_list), max(r_list)
    w, h = qmax - qmin + 1, rmax - rmin + 1
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    px = img.load()
    for q, r, layer in zip(q_list, r_list, layer_list):
        c = PALETTE_RGB[layer]
        px[q - qmin, r - rmin] = (int(c[0]), int(c[1]), int(c[2]), 255)
    return img.resize((w * scale, h * scale), Image.NEAREST)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("structure_id")
    ap.add_argument("seed", type=int)
    ap.add_argument("--length", type=int, default=120, help="hull length in cells (q-axis span)")
    ap.add_argument("--width", type=int, default=50, help="max hull width in cells (r-axis span)")
    ap.add_argument("--hole-count", type=int, default=None, help="default scales with hull size")
    ap.add_argument("--hole-radius-frac", type=float, default=0.22)
    ap.add_argument("--edge-noise", type=float, default=0.18)
    ap.add_argument("--starting-money", type=int, default=500)
    ap.add_argument("--preview", action="store_true")
    args = ap.parse_args()

    hole_count = args.hole_count if args.hole_count is not None else max(3, (args.length * args.width) // 1500)
    q_list, r_list, alt_list, layer_list = generate(
        args.seed, args.length, args.width, hole_count, args.hole_radius_frac, args.edge_noise)

    q_mid = round((min(q_list) + max(q_list)) / 2)
    r_mid = round((min(r_list) + max(r_list)) / 2)
    q_list = [q - q_mid for q in q_list]
    r_list = [r - r_mid for r in r_list]

    cells = {
        "source": f"gen_wreck_structure:seed{args.seed}",
        "count": len(q_list),
        "bounds": {"q": [min(q_list), max(q_list)], "r": [min(r_list), max(r_list)]},
        "q": q_list,
        "r": r_list,
        "alt": alt_list,
        "layer": layer_list,
    }
    struct_def = {
        "id": args.structure_id,
        "display_name": f"Wreck Seed {args.seed}",
        "description": f"Procedurally generated wreck — seed {args.seed}.",
        "cells_file": f"{args.structure_id}_cells.json",
        "starting_money": args.starting_money,
        "sealed_layers": True,
        "bands": [{"layer": 1, "hp_sealed": 75}],
        "glyph_count": 0,
        "green_count": -1,
    }

    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "content", "structures", "images", args.structure_id)
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, f"{args.structure_id}.json"), "w") as f:
        json.dump(struct_def, f, indent=2)
    with open(os.path.join(out_dir, f"{args.structure_id}_cells.json"), "w") as f:
        json.dump(cells, f)

    print(f"seed={args.seed} cells={len(q_list)} bounds q{cells['bounds']['q']} r{cells['bounds']['r']}")
    print(f"Wrote to {out_dir}")

    if args.preview:
        preview = render_preview(q_list, r_list, layer_list)
        path = os.path.join(out_dir, f"{args.structure_id}_preview.png")
        preview.save(path)
        print(f"Preview written to {path}")


if __name__ == "__main__":
    main()
