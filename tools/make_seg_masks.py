#!/usr/bin/env python
"""
Builds the label textures (Assets/Resources/SegMasks/*.png) for scenes whose track is painted into
a floor texture. A label texture lives in the UV space of that floor texture: white = road (class 1),
black = background. Hidden/SegReplacement samples it at exactly the UV the RGB shader samples the
floor with, so the mask edge sits on the painted tape line. SceneSegRules.cs assigns them.

Labelling rule (as everywhere): road = from the outer edge of one boundary line to the outer edge of
the other, lines included.

    python tools/make_seg_masks.py            # writes both masks + previews in seg_masks_preview/

circuit_launch      CircuitLaunchDiffuse.png has the track painted on carpet. The CircuitLaunchRoad
                    mesh of CircuitLaunch.fbx is a ribbon that covers exactly the track; its UV
                    polygons are rasterised, then the white tape pixels touching them are added.
roboracingleague_1  track_lines3.jpg (cutout overlay) holds two closed white tape loops. The road
                    is the ring between them: non-line regions nested once inside the outer loop,
                    plus all tape pixels.
Needs numpy and pillow only.
"""
import os
import struct
import zlib

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "sdsim", "Assets")
OUT = os.path.join(ASSETS, "Resources", "SegMasks")
PREVIEW = os.path.join(ROOT, "seg_masks_preview")
Image.MAX_IMAGE_PIXELS = None


# ---------------------------------------------------------------- minimal binary FBX reader
def _read_nodes(d, off, end, v64):
    nodes = []
    while off < end:
        if v64:
            end_off, nprops, _ = struct.unpack_from("<QQQ", d, off); off += 24
        else:
            end_off, nprops, _ = struct.unpack_from("<III", d, off); off += 12
        nlen = d[off]; off += 1
        if end_off == 0:
            break
        name = d[off:off + nlen].decode("ascii", "replace"); off += nlen
        props, p = [], off
        for _ in range(nprops):
            t = chr(d[p]); p += 1
            if t in "YCIFDL":
                fmt = {"Y": "<h", "C": "<?", "I": "<i", "F": "<f", "D": "<d", "L": "<q"}[t]
                props.append(struct.unpack_from(fmt, d, p)[0]); p += struct.calcsize(fmt)
            elif t in "fdlib":
                n, enc, clen = struct.unpack_from("<III", d, p); p += 12
                raw = d[p:p + clen]; p += clen
                raw = zlib.decompress(raw) if enc == 1 else raw
                props.append(np.frombuffer(raw, dtype={"f": "<f4", "d": "<f8", "l": "<i8", "i": "<i4", "b": "<i1"}[t], count=n))
            elif t in "SR":
                n = struct.unpack_from("<I", d, p)[0]; p += 4
                props.append(d[p:p + n]); p += n
        children = _read_nodes(d, p, end_off, v64) if p < end_off else []
        nodes.append((name, props, children))
        off = end_off
    return nodes


def fbx_uv_polygons(path, model_name):
    """UV polygons (list of [(u, v), ...]) of the geometry attached to model_name."""
    d = open(path, "rb").read()
    top = _read_nodes(d, 27, len(d), struct.unpack_from("<I", d, 23)[0] >= 7500)
    get = lambda nodes, n: [x for x in nodes if x[0] == n]
    objects, conns = get(top, "Objects")[0][2], get(top, "Connections")[0][2]
    models = {p[0]: p[1].split(b"\x00")[0].decode() for n, p, _ in objects if n == "Model"}
    geoms = {p[0]: ch for n, p, ch in objects if n == "Geometry"}
    gid = next(p[1] for _, p, _ in conns if p[0] == b"OO" and p[1] in geoms and models.get(p[2]) == model_name)
    ch = geoms[gid]
    pidx = get(ch, "PolygonVertexIndex")[0][1][0]
    uvn = get(ch, "LayerElementUV")[0][2]
    uv = get(uvn, "UV")[0][1][0].reshape(-1, 2)
    assert get(uvn, "MappingInformationType")[0][1][0] == b"ByPolygonVertex"
    uvi = get(uvn, "UVIndex")[0][1][0]
    polys, cur = [], []
    for k, vi in enumerate(pidx):
        cur.append(tuple(uv[uvi[k]]))
        if vi < 0:
            polys.append(cur)
            cur = []
    return polys


# ---------------------------------------------------------------- helpers
def dilate(mask, radius):
    im = Image.fromarray(mask.astype(np.uint8) * 255)
    size = 2 * radius + 1
    while size > 1:   # MaxFilter is limited in size; repeat with smaller kernels
        k = min(size, 15)
        im = im.filter(ImageFilter.MaxFilter(k))
        size -= k - 1
    return np.asarray(im) > 127


def flood(free, seed):
    """4-connected region of `free` that contains seed (x, y)."""
    # .copy(): an image made by fromarray shares a read-only buffer and floodfill silently does nothing
    im = Image.fromarray(np.where(free, 255, 0).astype(np.uint8)).copy()
    ImageDraw.floodfill(im, seed, 128, thresh=0)
    return np.asarray(im) == 128


def grow_into(seed_mask, allowed, steps):
    """Geodesic dilation: grow seed_mask by `steps` pixels, but only through `allowed` pixels."""
    m = seed_mask.copy()
    for _ in range(steps):
        m = (dilate(m, 1) & allowed) | m
    return m


def save(mask, name, base_rgb):
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(PREVIEW, exist_ok=True)
    Image.fromarray(mask.astype(np.uint8) * 255).save(os.path.join(OUT, name + ".png"), optimize=True)
    over = base_rgb.astype(np.float32)
    over[mask] = over[mask] * 0.5 + np.array([0, 255, 0]) * 0.5
    pv = Image.fromarray(over.astype(np.uint8))
    pv.thumbnail((2048, 2048))
    pv.save(os.path.join(PREVIEW, name + "_overlay.png"))
    print("%-20s %dx%d  road %.1f %%" % (name, mask.shape[1], mask.shape[0], 100.0 * mask.mean()))


# ---------------------------------------------------------------- scenes
def circuit_launch():
    tex = np.asarray(Image.open(os.path.join(ASSETS, "Models/tracks/Materials/CircuitLaunchDiffuse.png")).convert("RGB"))
    H, W = tex.shape[:2]
    polys = fbx_uv_polygons(os.path.join(ASSETS, "Models/tracks/CircuitLaunch.fbx"), "CircuitLaunchRoad")
    im = Image.new("L", (W, H), 0)
    dr = ImageDraw.Draw(im)
    for poly in polys:
        dr.polygon([(u * W, (1.0 - v) * H) for u, v in poly], fill=255)
    road = np.asarray(im) > 127
    lum = tex.mean(axis=2)
    sat = tex.max(axis=2) - tex.min(axis=2)
    white = (lum > 200) & (sat < 40)
    white[: int(0.14 * H)] = False          # the unused blank band at the top of the texture
    # tape pixels connected to the ribbon (within a tape width) belong to the road, up to the outer
    # edge of the line; isolated light carpet flecks next to the tape do not
    mask = grow_into(road, white, 24 * W // 4096)
    inside_tape_frac = (white & road).sum() / max(1, (white & mask).sum())
    # close pin-holes left between ribbon polygons and tape
    mask = ~dilate(~dilate(mask, 2), 2)
    print("circuit_launch: %d UV polygons, tape pixels already inside the ribbon: %.0f %%" % (len(polys), 100 * inside_tape_frac))
    save(mask, "circuit_launch", tex)


def roboracingleague_1():
    lines = np.asarray(Image.open(os.path.join(ASSETS, "Textures/roboracingleague/track_lines3.jpg")).convert("L"))
    tape = lines > 128
    tape = grow_into(tape, lines > 60, 2)   # include the anti-aliased tape border
    free = ~tape
    H, W = tape.shape
    outside = flood(free, (0, 0))
    # ring = the region just inside the outer tape loop: walk along the middle row from the left
    y = H // 2
    xs = np.nonzero(tape[y])[0]
    x = xs[0]
    while tape[y, x]:
        x += 1
    ring = flood(free, (int(x), y))
    inner = free & ~outside & ~ring          # inside the inner loop (and specks between tapes)
    big_inner = inner.sum() > 0.02 * H * W
    mask = ~outside & ~inner                 # ring + both tape loops
    print("roboracingleague_1: outside %.1f %%, ring %.1f %%, inner %.1f %% (%s)" % (
        100 * outside.mean(), 100 * ring.mean(), 100 * inner.mean(), "ok" if big_inner else "CHECK: no inner region"))
    floor = np.asarray(Image.open(os.path.join(ASSETS, "Textures/roboracingleague/floor3.jpg")).convert("RGB"))
    base = np.maximum(floor, np.repeat(lines[:, :, None], 3, axis=2))
    save(mask, "roboracingleague_1", base)


if __name__ == "__main__":
    circuit_launch()
    roboracingleague_1()
