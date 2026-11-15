#!/usr/bin/env python
"""
Reference implementation of the gen_track layout generator (closed circuits).
sdsim/Assets/Scripts/trackgen/TrackLayouts.cs is a line-by-line port (same SplitMix64 RNG and
draw order, double precision).

    python tools/trackgen_ref.py --check 200            # rule check over 200 seeds (pass/fail)
    python tools/trackgen_ref.py --sheet 0 200 --out x  # contact sheet incl. the tightest tracks

Layout (like a real circuit):
  1. seeded points in a box -> convex hull (counter-clockwise)
  2. some hull edges get a notch (a vertex pushed inwards or outwards): hairpins, chicanes,
     bus stops; at least one notch per track
  3. every corner of that polygon is rounded with its own radius (log-uniform 4..40 m, never
     tighter than the min-radius rule): true straights joined by constant-radius corners
  4. random rotation and direction, resampled to nodes every ~2 m, start on the longest straight

Hard rules for every track:
  * min curve radius            >= 1.5 * half width              (no folding)
  * centreline self-distance    >= road width + 3 m   for parts that are not neighbours
    (neighbours = closer than pi/2 * (width + 3 m) along the track)
  * the two road edges (left/right boundary polylines) never touch or cross
  * length in [80, 500] m
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
    "len_max": 500.0,
    "max_attempts": 500,
    # control polygon
    "box_min": 30.0,             # m, size of the area the circuit is laid out in
    "box_max": 130.0,
    "box_width_factor": 10.0,    # box >= 10 road widths
    "aspect_min": 0.45,
    "points_min": 6,
    "points_max": 16,
    "notch_p_min": 0.45,         # per-track probability that a hull edge gets a notch
    "notch_p_max": 1.0,
    "notch_depth_min": 0.2,      # notch depth as a fraction of the edge length
    "notch_depth_max": 0.6,
    "notch_inward_p": 0.75,
    "notch_min_edge": 3.0,       # only edges longer than 3 road widths are notched
    # corners
    "corner_r_min": 4.0,         # m (and >= 1.5 * half width)
    "corner_r_max": 40.0,
    "max_turn": math.radians(165.0),
    "edge_use": 0.92,            # corners may use at most 92 % of an edge
    "arc_step": 0.5,             # m, arc sampling before resampling
    "straight_curv": 1.0 / 200.0,
}


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


# ---------------------------------------------------------------- circuit layouts
# A track is built like a real circuit: a control polygon (convex hull of seeded points, with
# some edges notched inwards/outwards for hairpins and chicanes) whose corners are rounded with
# individual radii. Result: true straights joined by constant-radius corners.
def convex_hull(pts):
    """Andrew's monotone chain, counter-clockwise, no collinear points."""
    pts = sorted(pts)
    if len(pts) < 3:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def turn_angle(a, b, c):
    """Signed heading change at b (radians, + = left turn)."""
    d1x, d1z = b[0] - a[0], b[1] - a[1]
    d2x, d2z = c[0] - b[0], c[1] - b[1]
    return math.atan2(d1x * d2z - d1z * d2x, d1x * d2x + d1z * d2z)


def control_polygon(rng, width, P):
    box_w = rng.range(max(P["box_min"], P["box_width_factor"] * width), P["box_max"])
    box_h = box_w * rng.range(P["aspect_min"], 1.0)
    n_pts = rng.int_incl(P["points_min"], P["points_max"])
    pts = [(rng.range(0.0, box_w), rng.range(0.0, box_h)) for _ in range(n_pts)]
    hull = convex_hull(pts)
    p_notch = rng.range(P["notch_p_min"], P["notch_p_max"])
    m = len(hull)
    # every edge draws the same 4 numbers, used or not (keeps the draw order simple to port)
    edges = []
    for i in range(m):
        a, b = hull[i], hull[(i + 1) % m]
        L = dist(a, b)
        do_notch = rng.uniform() < p_notch
        t = rng.range(0.35, 0.65)
        depth = rng.range(P["notch_depth_min"], P["notch_depth_max"])
        inward = rng.uniform() < P["notch_inward_p"]
        eligible = L >= P["notch_min_edge"] * width
        edges.append([do_notch and eligible, t, depth, inward, L, eligible])
    if not any(e[0] for e in edges):   # at least one notch per track: the longest eligible edge
        best = -1
        for i in range(m):
            if edges[i][5] and (best < 0 or edges[i][4] > edges[best][4]):
                best = i
        if best >= 0:
            edges[best][0] = True
    poly = []
    for i in range(m):
        a, b = hull[i], hull[(i + 1) % m]
        poly.append(a)
        do_notch, t, depth, inward, L, _ = edges[i]
        if do_notch:
            nx, nz = -(b[1] - a[1]) / L, (b[0] - a[0]) / L    # left normal = inside of a CCW hull
            s = depth * L * (1.0 if inward else -1.0)
            poly.append((a[0] + (b[0] - a[0]) * t + nx * s, a[1] + (b[1] - a[1]) * t + nz * s))
    return poly


def fillet(poly, width, rng, P):
    """Rounds every corner with its own radius. Returns dense points (or None) and the radii."""
    n = len(poly)
    hw = width / 2.0
    r_lo = max(P["min_radius_factor"] * hw * 1.02, P["corner_r_min"])
    r_hi = max(P["corner_r_max"], r_lo)
    th, t, r = [], [], []
    for i in range(n):
        a = turn_angle(poly[i - 1], poly[i], poly[(i + 1) % n])
        th.append(a)
        rr = math.exp(rng.range(math.log(r_lo), math.log(r_hi)))   # log-uniform: more tight corners
        r.append(rr)
        t.append(rr * math.tan(abs(a) / 2.0))
    for i in range(n):
        if abs(th[i]) > P["max_turn"]:
            return None, None
    for _ in range(2):   # tangent lengths of one edge must fit on it (leave a short straight)
        for i in range(n):
            j = (i + 1) % n
            L = dist(poly[i], poly[j])
            s = t[i] + t[j]
            if s > P["edge_use"] * L:
                k = P["edge_use"] * L / s
                t[i] *= k
                t[j] *= k
    for i in range(n):
        r[i] = t[i] / math.tan(abs(th[i]) / 2.0) if abs(th[i]) > 1e-9 else float("inf")
        if r[i] < r_lo:
            return None, None
    out = []
    for i in range(n):
        p, a = poly[i], poly[i - 1]
        la = dist(a, p)
        din = ((p[0] - a[0]) / la, (p[1] - a[1]) / la)
        e = (p[0] - din[0] * t[i], p[1] - din[1] * t[i])
        if abs(th[i]) <= 1e-9:
            out.append(e)
            continue
        sgn = 1.0 if th[i] > 0 else -1.0
        c = (e[0] - din[1] * r[i] * sgn, e[1] + din[0] * r[i] * sgn)   # centre on the inside
        a0 = math.atan2(e[1] - c[1], e[0] - c[0])
        k = max(int(math.ceil(abs(th[i]) * r[i] / P["arc_step"])), 2)
        for s in range(k + 1):
            ang = a0 + th[i] * s / k
            out.append((c[0] + r[i] * math.cos(ang), c[1] + r[i] * math.sin(ang)))
    return out, r


def longest_straight_start(nodes, step, P):
    """Node index on the longest (near) straight, a little after its beginning."""
    n = len(nodes)
    straight = [curvature(nodes, i) < P["straight_curv"] for i in range(n)]
    if all(straight) or not any(straight):
        return 0
    first = next(i for i in range(n) if not straight[i])   # rotate so a run never wraps
    best_i, best_len, i = 0, -1, 0
    while i < n:
        idx = (first + i) % n
        if straight[idx]:
            j = i
            while j < n and straight[(first + j) % n]:
                j += 1
            if j - i > best_len:
                best_len, best_i = j - i, (first + i + int((j - i) * 0.2)) % n
            i = j
        else:
            i += 1
    return best_i


def generate(seed, road_width=None, P=SETTINGS):
    width = sub_rng(seed, "width").range(P["width_min"], P["width_max"])
    width = road_width or width
    rng = sub_rng(seed, "layout")
    for attempt in range(1, P["max_attempts"] + 1):
        poly = control_polygon(rng, width, P)
        rot = rng.range(0.0, 2 * math.pi)
        flip = rng.uniform() < 0.5
        dense, radii = fillet(poly, width, rng, P)
        if dense is None:
            continue
        c, sn = math.cos(rot), math.sin(rot)
        pts = [(x * c - z * sn, x * sn + z * c) for x, z in dense]
        if flip:
            pts.reverse()
        nodes, step, L = resample_closed(pts, P["node_spacing"])
        if check_rules(nodes, step, width, P):
            continue
        st = longest_straight_start(nodes, step, P)
        nodes = nodes[st:] + nodes[:st]
        corners = sum(1 for x in radii if x != float("inf"))
        return {"seed": seed, "shape": "circuit", "corners": corners, "width": width, "nodes": nodes,
                "step": step, "length": L, "min_radius": min_radius(nodes), "attempts": attempt}
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
    d.text((4, 2), "s%d c%d w%.1f R%.1f L%.0f a%d" % (
        r["seed"], r["corners"], w, r["min_radius"], r["length"], r["attempts"]), fill=(255, 255, 255))
    return im


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", type=int, default=0, help="check the rules for seeds 0..N-1")
    ap.add_argument("--sheet", type=int, nargs=2, metavar=("FIRST", "COUNT"))
    ap.add_argument("--out", default="layout_sheet")
    a = ap.parse_args()
    if a.check:
        fails, rs = [], []
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
            rs.append(r)
        print("%d seeds: %s" % (a.check, "PASS" if not fails else "FAIL %r" % fails[:10]))
        if rs:
            q = lambda k: sorted(x[k] for x in rs)
            for k in ("corners", "length", "width", "attempts"):
                v = q(k)
                print("%-8s min %.1f  median %.1f  max %.1f" % (k, v[0], v[len(v) // 2], v[-1]))
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
