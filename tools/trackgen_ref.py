#!/usr/bin/env python
"""
Reference implementation of the gen_track layout generator (closed loops only).
sdsim/Assets/Scripts/trackgen/TrackLayouts.cs is a line-by-line port (same SplitMix64 RNG and
draw order, double precision).

    python tools/trackgen_ref.py --check 200            # rule check over 200 seeds (pass/fail)
    python tools/trackgen_ref.py --sheet 0 200 --out x  # contact sheet incl. the tightest tracks

Hard rules for every track:
  * min curve radius            >= 1.5 * half width              (no folding)
  * centreline self-distance    >= road width + 3 m   for parts that are not neighbours
    (neighbours = closer than pi/2 * (width + 3 m) along the track)
  * the two road edges (left/right boundary polylines) never touch or cross
  * length in [80, 450] m, all control-point radii in [15, 70] m
A failed candidate is replaced by the next attempt from the same seed.
"""
import argparse
import math
import os

MASK = (1 << 64) - 1

SETTINGS = {
    "node_spacing": 2.0,
    "width_min": 3.5,
    "width_max": 13.5,
    "min_radius_factor": 1.5,
    "margin": 3.0,
    "len_min": 80.0,
    "len_max": 450.0,
    "r_min": 15.0,
    "r_max": 70.0,
    "max_attempts": 500,
}
SHAPES = ["oval", "polar", "s_bends"]   # equal probability


class Rng:
    """SplitMix64 (identical to SeededRng.cs)."""

    def __init__(self, seed):
        self.s = seed & MASK

    def next_u64(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & MASK
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)

    def uniform(self):
        return (self.next_u64() >> 11) * (1.0 / 9007199254740992.0)

    def range(self, a, b):
        return a + (b - a) * self.uniform()

    def int_incl(self, a, b):
        return a + min(int(self.uniform() * (b - a + 1)), b - a)


def fnv1a64(data):
    h = 0xCBF29CE484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001B3) & MASK
    return h


def sub_rng(seed, label):
    return Rng(Rng((seed ^ fnv1a64(label.encode())) & MASK).next_u64())


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


# ---------------------------------------------------------------- geometry
def catmull_rom_closed(ctrl, samples=32, alpha=0.5):
    n = len(ctrl)
    out = []
    for i in range(n):
        p0, p1, p2, p3 = ctrl[(i - 1) % n], ctrl[i], ctrl[(i + 1) % n], ctrl[(i + 2) % n]
        t0 = 0.0
        t1 = t0 + max(dist(p0, p1), 1e-6) ** alpha
        t2 = t1 + max(dist(p1, p2), 1e-6) ** alpha
        t3 = t2 + max(dist(p2, p3), 1e-6) ** alpha
        for k in range(samples):
            t = t1 + (t2 - t1) * k / samples
            pt = []
            for c in range(2):
                a1 = (t1 - t) / (t1 - t0) * p0[c] + (t - t0) / (t1 - t0) * p1[c]
                a2 = (t2 - t) / (t2 - t1) * p1[c] + (t - t1) / (t2 - t1) * p2[c]
                a3 = (t3 - t) / (t3 - t2) * p2[c] + (t - t2) / (t3 - t2) * p3[c]
                b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2
                b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3
                pt.append((t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2)
            out.append((pt[0], pt[1]))
    return out


def resample_closed(pts, spacing):
    """Equal arc-length nodes; spacing adjusted so the loop closes evenly."""
    seg = list(zip(pts, pts[1:] + [pts[0]]))
    L = sum(dist(a, b) for a, b in seg)
    n = max(int(round(L / spacing)), 8)
    step = L / n
    out, si, acc = [], 0, 0.0
    for k in range(n):
        s = k * step
        while si < len(seg) - 1 and acc + dist(*seg[si]) < s:
            acc += dist(*seg[si])
            si += 1
        a, b = seg[si]
        d = dist(a, b)
        u = 0.0 if d < 1e-12 else min(max((s - acc) / d, 0.0), 1.0)
        out.append((a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u))
    return out, step, L


def curvature(nodes, i):
    n = len(nodes)
    a, b, c = nodes[i - 1], nodes[i], nodes[(i + 1) % n]
    cr = abs((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]))
    return 2.0 * cr / max(dist(a, b) * dist(b, c) * dist(c, a), 1e-12)


def min_radius(nodes):
    k = max(curvature(nodes, i) for i in range(len(nodes)))
    return float("inf") if k <= 0 else 1.0 / k


def self_distance(nodes, step, width, margin):
    """Min distance between centreline nodes that are not neighbours along the loop."""
    n = len(nodes)
    k0 = int(math.ceil(0.5 * math.pi * (width + margin) / step))
    best = float("inf")
    for i in range(n):
        for j in range(i + k0, n):
            if n - (j - i) >= k0:
                best = min(best, dist(nodes[i], nodes[j]))
    return best


def road_edges(nodes, half_width):
    """Left/right boundaries exactly as RoadBuilder.BuildGeneratedRoad builds the mesh."""
    n = len(nodes)
    left, right = [], []
    for i in range(n):
        p, a, b = nodes[i], nodes[i - 1], nodes[(i + 1) % n]
        tx, tz = b[0] - a[0], b[1] - a[1]
        L = math.hypot(tx, tz)
        sx, sz = -tz / L, tx / L  # Cross(tangent, up)
        left.append((p[0] + sx * half_width, p[1] + sz * half_width))
        right.append((p[0] - sx * half_width, p[1] - sz * half_width))
    return left, right


def segments_touch(a, b, c, d):
    rx, rz = b[0] - a[0], b[1] - a[1]
    qx, qz = d[0] - c[0], d[1] - c[1]
    den = rx * qz - rz * qx
    if abs(den) < 1e-12:
        return False
    t = ((c[0] - a[0]) * qz - (c[1] - a[1]) * qx) / den
    u = ((c[0] - a[0]) * rz - (c[1] - a[1]) * rx) / den
    return 0.0 <= t <= 1.0 and 0.0 <= u <= 1.0


def edges_touch(nodes, half_width):
    """True if two road-boundary segments of non-neighbouring quads touch or cross."""
    left, right = road_edges(nodes, half_width)
    n = len(nodes)
    segs = [(e[i], e[(i + 1) % n], i) for e in (left, right) for i in range(n)]
    for x in range(len(segs)):
        a, b, i = segs[x]
        for y in range(x + 1, len(segs)):
            c, d, j = segs[y]
            if min((i - j) % n, (j - i) % n) <= 1:
                continue
            if segments_touch(a, b, c, d):
                return True
    return False


def check_rules(nodes, step, width, P=SETTINGS):
    """'' if all hard rules hold, else the name of the first violated rule."""
    hw = width / 2.0
    if min_radius(nodes) < P["min_radius_factor"] * hw:
        return "min radius"
    if self_distance(nodes, step, width, P["margin"]) < width + P["margin"]:
        return "self distance"
    if not (P["len_min"] <= step * len(nodes) <= P["len_max"]):
        return "length"
    if edges_touch(nodes, hw):
        return "edges touch"
    return ""


# ---------------------------------------------------------------- shapes (unit radius)
def shape_points(shape, rng):
    if shape == "oval":
        n = rng.int_incl(12, 16)
        asp = rng.range(1.0, 3.0)
        f = lambda i, a: rng.range(0.96, 1.04) / math.sqrt((math.cos(a) / asp) ** 2 + math.sin(a) ** 2)
    elif shape == "polar":
        n = rng.int_incl(6, 14)
        q = rng.range(1.1, 3.5)
        f = lambda i, a: rng.range(1.0, q)
    else:  # s_bends: alternating inner/outer control points
        n = 2 * rng.int_incl(3, 5)
        q = rng.range(1.3, 2.0)
        f = lambda i, a: (q if i % 2 == 0 else 1.0) * rng.range(0.95, 1.05)
    step = 2 * math.pi / n
    ctrl, rs = [], []
    for i in range(n):
        a = i * step + rng.range(-0.3, 0.3) * step
        r = f(i, a)
        ctrl.append((r * math.cos(a), r * math.sin(a)))
        rs.append(r)
    return catmull_rom_closed(ctrl), min(rs), max(rs)


def straightest_start(nodes, window=5):
    n = len(nodes)
    curv = [curvature(nodes, i) for i in range(n)]
    best_i, best_v = 0, float("inf")
    for i in range(n):
        v = max(curv[(i + k) % n] for k in range(-window, window + 1))
        if v < best_v - 1e-12:
            best_i, best_v = i, v
    return best_i


def generate(seed, road_width=None, shape=None, P=SETTINGS):
    width = sub_rng(seed, "width").range(P["width_min"], P["width_max"])
    width = road_width or width
    shp = SHAPES[sub_rng(seed, "shape").int_incl(0, len(SHAPES) - 1)]
    shp = shape or shp
    hw = width / 2.0
    rng = sub_rng(seed, "layout")
    for attempt in range(1, P["max_attempts"] + 1):
        dense, rmin_u, rmax_u = shape_points(shp, rng)
        rot = rng.range(0.0, 2 * math.pi)
        flip = rng.uniform() < 0.5
        u = rng.uniform()
        nodes_u, _, L_u = resample_closed(dense, 0.05)
        minr_u = min_radius(nodes_u)
        s_lo = max(P["min_radius_factor"] * hw / minr_u, P["len_min"] / L_u, P["r_min"] / rmin_u)
        s_hi = min(P["len_max"] / L_u, P["r_max"] / rmax_u)
        if s_lo > s_hi:
            continue
        s = s_lo + (s_hi - s_lo) * u
        c, sn = math.cos(rot), math.sin(rot)
        pts = [(s * (x * c - z * sn), s * (x * sn + z * c)) for x, z in dense]
        if flip:
            pts.reverse()
        nodes, step, L = resample_closed(pts, P["node_spacing"])
        if check_rules(nodes, step, width, P):
            continue
        st = straightest_start(nodes)
        nodes = nodes[st:] + nodes[:st]
        return {"seed": seed, "shape": shp, "width": width, "nodes": nodes, "step": step,
                "length": L, "min_radius": min_radius(nodes), "attempts": attempt}
    return None


# ---------------------------------------------------------------- tools
def draw(r, size=300):
    from PIL import Image, ImageDraw
    nodes, w = r["nodes"], r["width"]
    xs, zs = [p[0] for p in nodes], [p[1] for p in nodes]
    span = max(max(xs) - min(xs), max(zs) - min(zs)) + w + 4
    S = (size - 20) / span
    im = Image.new("RGB", (size, size + 16), (40, 90, 40))
    d = ImageDraw.Draw(im)
    tp = lambda p: (10 + (p[0] - min(xs) + w / 2 + 2) * S, size + 6 - (p[1] - min(zs) + w / 2 + 2) * S)
    left, right = road_edges(nodes, w / 2)
    n = len(nodes)
    for i in range(n):  # draw the actual mesh quads
        j = (i + 1) % n
        d.polygon([tp(left[i]), tp(left[j]), tp(right[j]), tp(right[i])], fill=(85, 85, 85))
    for e in (left, right):
        d.line([tp(p) for p in e + [e[0]]], fill=(255, 255, 255), width=1)
    x, y = tp(nodes[0])
    d.ellipse((x - 3, y - 3, x + 3, y + 3), fill=(255, 0, 0))
    d.text((4, 2), "s%d %s w%.1f R%.1f R/hw%.1f L%.0f" % (
        r["seed"], r["shape"], w, r["min_radius"], r["min_radius"] / (w / 2), r["length"]), fill=(255, 255, 255))
    return im


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", type=int, default=0, help="check the rules for seeds 0..N-1")
    ap.add_argument("--sheet", type=int, nargs=2, metavar=("FIRST", "COUNT"))
    ap.add_argument("--out", default="layout_sheet")
    a = ap.parse_args()
    if a.check:
        fails, shapes = [], {}
        for seed in range(a.check):
            r = generate(seed)
            if r is None:
                fails.append((seed, "no valid layout"))
                continue
            why = check_rules(r["nodes"], r["step"], r["width"])
            if why:
                fails.append((seed, why))
            if generate(seed)["nodes"] != r["nodes"]:
                fails.append((seed, "not deterministic"))
            shapes[r["shape"]] = shapes.get(r["shape"], 0) + 1
        print("%d seeds: %s" % (a.check, "PASS" if not fails else "FAIL %r" % fails[:10]))
        print("shapes:", shapes)
    if a.sheet:
        from PIL import Image
        os.makedirs(a.out, exist_ok=True)
        rs = [r for r in (generate(s) for s in range(a.sheet[0], a.sheet[0] + a.sheet[1])) if r]
        tight = sorted(rs, key=lambda r: r["min_radius"] / (r["width"] / 2))
        first = rs[:12]
        pick = first + [r for r in tight if r not in first][:12]
        cols = 6
        sheet = Image.new("RGB", (cols * 300, ((len(pick) + cols - 1) // cols) * 316), "black")
        for i, r in enumerate(pick):
            sheet.paste(draw(r), ((i % cols) * 300, (i // cols) * 316))
        path = os.path.join(a.out, "contact_sheet.png")
        sheet.save(path)
        print("saved", path, "(row 1-2: seeds %d.., row 3-4: the 12 tightest by minR/half-width)" % a.sheet[0])


if __name__ == "__main__":
    main()
