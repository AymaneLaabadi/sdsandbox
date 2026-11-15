#!/usr/bin/env python
"""
Acceptance test for the segmentation camera (image_seg) of the DonkeyCar simulator.

What it does
  * connects with gym-donkeycar, loads generated_road (or --env)
  * sends {"msg_type": "seg_config", "enabled": "1"}
  * drives slowly (steering towards the road pixels of the mask, low throttle)
  * for N frames saves  rgb_XXXX.png, mask_XXXX.png (0/255 for viewing),
    overlay_XXXX.png (50% blend, road edges drawn in yellow) and an x4 nearest-neighbour
    enlarged overlay for pixel-level inspection
  * asserts
      - np.unique(mask) is a subset of {0, 1} for every frame (G and B channels must be 0)
      - mask HxW == RGB HxW for every frame
      - road pixels exist in > 95% of the frames where the car is on the track
        (on track = |cte| <= --on-track-cte and hit == "none", from telemetry, i.e.
        independent of the mask)

Usage (simulator already running, scene menu visible):
    python tools/check_seg.py
    python tools/check_seg.py --exe path/to/donkey_sim.exe     # let gym start it
    python tools/check_seg.py --env donkey-generated-track-v0 --frames 300
    python tools/check_seg.py --styles 0,1,2,3,4,5,6 --frames 100     # legacy regen_road styles
    python tools/check_seg.py --gen-seeds 1 2 3 --frames 100           # gen_track tracks
    python tools/check_seg.py --gen-seeds 7 --surface carpet --centre-mode dashed
    python tools/check_seg.py --env donkey-circuit-launch-track-v0     # evaluation scenes

gen_track overrides (see docs/SEGMENTATION.md): --surface asphalt|concrete|dirt|sand|carpet,
--surface-variant, --edge-color, --centre-mode none|solid|dashed, --centre-color, --ground,
--road-width.

Note on --step-mode synchronous: in this fork the simulator's "step_mode synchronous"
handler only sets Time.timeScale = 0 (from the socket thread) and nothing ever advances
time again, so telemetry stops. RGB and mask are rendered back to back in the same
SendTelemetry() call, so they are aligned in asynchronous mode too. The default is
therefore "asynchronous" (nothing is sent). See docs/SEGMENTATION.md.
"""

import argparse
import base64
import csv
import json
import os
import sys
import threading
import time
from io import BytesIO

import numpy as np
from PIL import Image

try:
    import gymnasium as gym
except ImportError:  # older gym-donkeycar
    import gym

import gym_donkeycar  # noqa: F401  (registers the donkey-* envs)


GEN_OVERRIDES = ("road_width", "surface", "surface_variant", "edge_color", "centre_mode", "centre_color", "ground")


class SegGrabber:
    """Wraps the handler's telemetry callback so RGB + mask come from the SAME message."""

    def __init__(self, handler):
        self.handler = handler
        self.lock = threading.Lock()
        self.seq = 0
        self.latest = None
        self.n_with_seg = 0
        self._orig = handler.fns["telemetry"]
        handler.fns["telemetry"] = self._on_telemetry

    def _on_telemetry(self, msg):
        try:
            rgb = np.asarray(Image.open(BytesIO(base64.b64decode(msg["image"]))).convert("RGB"))
            mask_rgb = None
            if "image_seg" in msg:
                mask_rgb = np.asarray(Image.open(BytesIO(base64.b64decode(msg["image_seg"]))))
            meta = {
                "cte": float(msg.get("cte", float("nan"))),
                "hit": msg.get("hit", "none"),
                "speed": float(msg.get("speed", 0.0)),
            }
            with self.lock:
                self.seq += 1
                if mask_rgb is not None:
                    self.n_with_seg += 1
                self.latest = (self.seq, rgb, mask_rgb, meta)
        except Exception as e:  # never break the gym handler thread
            print("SegGrabber error:", e, file=sys.stderr)
        self._orig(msg)

    def get(self):
        with self.lock:
            return self.latest


def mask_from_png(mask_rgb):
    """Return (class_map HxW uint8, ok_channels bool)."""
    if mask_rgb.ndim == 2:
        return mask_rgb.astype(np.uint8), True
    cls = mask_rgb[:, :, 0].astype(np.uint8)
    ok = not np.any(mask_rgb[:, :, 1:3])
    return cls, ok


def make_overlay(rgb, cls):
    road = cls == 1
    out = rgb.astype(np.float32).copy()
    tint = np.array([0, 255, 0], dtype=np.float32)
    out[road] = 0.5 * out[road] + 0.5 * tint
    # 4-neighbour boundary of the road region, drawn fully opaque yellow
    # (road pixels that touch a background pixel, i.e. the outermost road pixels)
    edge = np.zeros_like(road)
    dv = road[:-1, :] != road[1:, :]
    dh = road[:, :-1] != road[:, 1:]
    edge[:-1, :] |= dv
    edge[1:, :] |= dv
    edge[:, :-1] |= dh
    edge[:, 1:] |= dh
    out[edge & road] = (255, 255, 0)
    return out.clip(0, 255).astype(np.uint8)


def steer_from_mask(cls, gain):
    h, w = cls.shape
    roi = cls[int(h * 0.55):, :] == 1
    ys, xs = np.nonzero(roi)
    if xs.size < 10:
        return 0.0
    x = (xs.mean() / (w - 1)) * 2.0 - 1.0  # -1 left .. +1 right
    return float(np.clip(gain * x, -1.0, 1.0))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--env", default="donkey-generated-roads-v0")
    ap.add_argument("--exe", default=None, help="path to the sim executable (omit if already running)")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9091)
    ap.add_argument("--frames", type=int, default=200)
    ap.add_argument("--throttle", type=float, default=0.15)
    ap.add_argument("--steer-gain", type=float, default=1.2)
    ap.add_argument("--on-track-cte", type=float, default=2.0,
                    help="|cte| threshold used to decide 'car is on the track'")
    ap.add_argument("--img-enc", default="JPG", choices=["JPG", "PNG"],
                    help="RGB encoding requested via cam_config (mask is always PNG)")
    ap.add_argument("--step-mode", default="asynchronous", choices=["asynchronous", "synchronous"])
    ap.add_argument("--time-step", type=float, default=0.05)
    ap.add_argument("--out", default="seg_check_out")
    ap.add_argument("--styles", default=None,
                    help="comma list of road_style indices, e.g. 0,2,3,4,5,6: sends regen_road for each "
                         "and records --frames frames into <out>/style_<n>/")
    ap.add_argument("--seed", type=int, default=42, help="rand_seed sent with regen_road")
    ap.add_argument("--regen-wait", type=float, default=3.0, help="seconds to wait after regen_road/gen_track")
    ap.add_argument("--gen-seeds", type=int, nargs="*", default=None,
                    help="gen_track seeds (scene generated_road); records into <out>/gen_<seed>/")
    for k in GEN_OVERRIDES:
        ap.add_argument("--" + k.replace("_", "-"), default=None, help="gen_track override")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)

    conf = {
        "host": args.host,
        "port": args.port,
        "body_style": "donkey",
        "body_rgb": (128, 128, 128),
        "car_name": "segcheck",
        "font_size": 30,
        "racer_name": "segcheck",
        "country": "-",
        "bio": "seg check",
        "guid": "segcheck",
        "max_cte": 1e6,  # never end the episode on cte; we judge on-track ourselves
        "cam_config": {"img_enc": args.img_enc},
        "log_level": 30,
    }
    if args.exe:
        conf["exe_path"] = args.exe

    env = gym.make(args.env, conf=conf)
    raw = env.unwrapped
    handler = raw.viewer.handler
    grab = SegGrabber(handler)

    env.reset()

    handler.blocking_send({"msg_type": "seg_config", "enabled": "1"})
    if args.step_mode == "synchronous":
        print("WARNING: step_mode synchronous freezes this fork of the sim (see docstring).")
        handler.blocking_send({"msg_type": "step_mode", "step_mode": "synchronous",
                               "time_step": str(args.time_step)})

    # wait for the first message that carries image_seg
    t0 = time.time()
    while grab.n_with_seg == 0:
        env.step(np.array([0.0, 0.0], dtype=np.float32))
        if time.time() - t0 > 15:
            env.close()
            sys.exit("FAIL: no 'image_seg' in telemetry after 15 s "
                     "(new build? seg_config handler registered? segSensor wired?)")

    # runs: (name, message to send first or None)
    runs = [("default road", None)]
    if args.styles:
        runs = [(f"style_{st}", {"msg_type": "regen_road", "road_style": str(st), "rand_seed": str(args.seed),
                                 "turn_increment": "0"}) for st in args.styles.split(",")]
    if args.gen_seeds:
        over = {k: getattr(args, k) for k in GEN_OVERRIDES if getattr(args, k) is not None}
        runs = [(f"gen_{sd}", dict({"msg_type": "gen_track", "seed": str(sd)}, **over)) for sd in args.gen_seeds]
    track_infos = {}
    handler.fns["track_info"] = lambda m: track_infos.__setitem__(m.get("seed"), m)

    results = []
    for name, msg in runs:
        out_dir = args.out if msg is None else os.path.join(args.out, name)
        os.makedirs(out_dir, exist_ok=True)
        if msg is not None:
            print(f"\n=== {msg} ===")
            handler.blocking_send(msg)
            # let the sim rebuild the road and place the car
            t0 = time.time()
            while time.time() - t0 < args.regen_wait:
                env.step(np.array([0.0, 0.0], dtype=np.float32))
            if msg["msg_type"] == "gen_track":
                info = track_infos.get(msg["seed"])
                if not info or not info.get("ok"):
                    results.append((name, out_dir, ["gen_track failed: %r" % (info,)]))
                    continue
                with open(os.path.join(out_dir, "track_info.json"), "w") as f:
                    json.dump(info, f, indent=1)
                print("  " + ", ".join(f"{k}={info.get(k)}" for k in (
                    "corners", "road_width", "length", "surface", "surface_variant", "edge_color", "centre_mode",
                    "centre_color", "ground")))
        failures = capture(env, grab, args, out_dir)
        results.append((name, out_dir, failures))

    env.step(np.array([0.0, 0.0], dtype=np.float32))
    try:
        handler.blocking_send({"msg_type": "seg_config", "enabled": "0"})
    except Exception:
        pass
    env.close()

    print("\n================ SUMMARY ================")
    any_fail = False
    for name, out_dir, failures in results:
        print(f"{name:16s} {'FAIL' if failures else 'PASS'}  -> {os.path.abspath(out_dir)}")
        for msg in failures[:20]:
            print("    -", msg)
        if len(failures) > 20:
            print(f"    ... and {len(failures) - 20} more")
        any_fail = any_fail or bool(failures)

    if any_fail:
        sys.exit(1)
    print("\nPASS: values in {0,1}, HxW match, road coverage OK.")
    print("Now inspect overlay_x4_*.png: yellow edge pixels must sit exactly on the road border.")


def capture(env, grab, args, out_dir):
    failures = []
    rows = []
    item = grab.get()
    last_seq = item[0] if item is not None else -1  # only frames rendered from now on
    steer = 0.0
    saved = 0
    on_track_frames = 0
    on_track_with_road = 0

    while saved < args.frames:
        env.step(np.array([steer, args.throttle], dtype=np.float32))
        item = grab.get()
        if item is None or item[0] == last_seq or item[2] is None:
            continue
        seq, rgb, mask_rgb, meta = item
        last_seq = seq

        cls, ch_ok = mask_from_png(mask_rgb)
        uniq = set(np.unique(cls).tolist())

        if not uniq.issubset({0, 1}):
            failures.append(f"frame {saved}: mask values {sorted(uniq)} not subset of {{0,1}}")
        if not ch_ok:
            failures.append(f"frame {saved}: mask G/B channels are not all 0")
        if cls.shape != rgb.shape[:2]:
            failures.append(f"frame {saved}: mask HxW {cls.shape} != rgb HxW {rgb.shape[:2]}")

        road_px = int((cls == 1).sum())
        on_track = (meta["hit"] in ("none", None)) and abs(meta["cte"]) <= args.on_track_cte
        if on_track:
            on_track_frames += 1
            if road_px > 0:
                on_track_with_road += 1

        stem = f"{saved:04d}"
        Image.fromarray(rgb).save(os.path.join(out_dir, f"rgb_{stem}.png"))
        Image.fromarray((cls == 1).astype(np.uint8) * 255).save(os.path.join(out_dir, f"mask_{stem}.png"))
        if cls.shape == rgb.shape[:2]:
            ov = make_overlay(rgb, cls)
            Image.fromarray(ov).save(os.path.join(out_dir, f"overlay_{stem}.png"))
            big = Image.fromarray(ov).resize((ov.shape[1] * 4, ov.shape[0] * 4), Image.NEAREST)
            big.save(os.path.join(out_dir, f"overlay_x4_{stem}.png"))

        rows.append({"frame": saved, "seq": seq, "cte": meta["cte"], "hit": meta["hit"],
                     "speed": meta["speed"], "on_track": int(on_track), "road_px": road_px,
                     "road_frac": road_px / cls.size, "values": " ".join(map(str, sorted(uniq))),
                     "steer": steer})
        saved += 1

        steer = steer_from_mask(cls, args.steer_gain) if cls.shape == rgb.shape[:2] else 0.0

    with open(os.path.join(out_dir, "frames.csv"), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)

    ratio = on_track_with_road / on_track_frames if on_track_frames else 0.0
    print(f"frames saved            : {saved}  -> {os.path.abspath(out_dir)}")
    print(f"frames on track         : {on_track_frames}")
    print(f"on-track with road px   : {on_track_with_road} ({ratio:.1%})")

    if on_track_frames == 0:
        failures.append("car was never on the track (cte/hit); cannot evaluate road coverage")
    elif ratio <= 0.95:
        failures.append(f"road pixels present in only {ratio:.1%} of on-track frames (need > 95%)")
    return failures


if __name__ == "__main__":
    main()
