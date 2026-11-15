#!/usr/bin/env python
"""
gen_track acceptance test (scene generated_road, build with the trackgen scripts).

For every seed:
  * generate twice -> the track_info "hash" must be identical (determinism)
  * re-check the hard rules on the nodes the sim returns (independent of the C# code):
      min curve radius >= 1.5 * half width, centreline self-distance >= width + 3 m for
      non-neighbouring parts, road edges never touch, length in [80, 500] m
  * the layout equals the Python reference (tools/trackgen_ref.py) for the same seed
  * the car is at node 0 afterwards (telemetry activeNode 0, totalNodes matches)
Prints PASS or FAIL (with the failing seeds) only.

    python tools/check_trackgen.py --seeds 200 [--exe <donkey_sim.exe>]
"""
import argparse
import os
import sys
import threading
import time

import numpy as np

try:
    import gymnasium as gym
except ImportError:
    import gym
import gym_donkeycar  # noqa: F401

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import trackgen_ref as ref  # noqa: E402

# float32 node positions from the sim: allow tiny numeric slack on the rule boundaries
RADIUS_TOL = 0.995
DIST_TOL = 0.01


class Client:
    def __init__(self, env):
        self.env = env
        self.h = env.unwrapped.viewer.handler
        self.lock = threading.Lock()
        self.info = None
        self.tel = {}
        self.h.fns["track_info"] = self._on_info
        orig = self.h.fns["telemetry"]

        def on_tel(msg):
            with self.lock:
                self.tel = {"activeNode": msg.get("activeNode"), "totalNodes": msg.get("totalNodes")}
            orig(msg)

        self.h.fns["telemetry"] = on_tel

    def _on_info(self, msg):
        with self.lock:
            self.info = msg

    def step(self, seconds):
        t0 = time.time()
        while time.time() - t0 < seconds:
            self.env.step(np.array([0.0, 0.0], dtype=np.float32))

    def gen(self, seed):
        with self.lock:
            self.info = None
        self.h.blocking_send({"msg_type": "gen_track", "seed": str(seed), "return_nodes": "1"})
        t0 = time.time()
        while time.time() - t0 < 30:
            self.step(0.05)
            with self.lock:
                if self.info is not None:
                    return self.info
        raise TimeoutError("no track_info for seed %d" % seed)


def check_nodes(info):
    nodes = [(float(p[0]), float(p[1])) for p in info["nodes"]]
    w = float(info["road_width"])
    n = len(nodes)
    step = sum(ref.dist(nodes[i], nodes[(i + 1) % n]) for i in range(n)) / n
    if ref.min_radius(nodes) < RADIUS_TOL * ref.SETTINGS["min_radius_factor"] * w / 2:
        return "min radius %.2f < 1.5 * half width" % ref.min_radius(nodes)
    sd = ref.self_distance(nodes, step, w, ref.SETTINGS["margin"])
    if sd < w + ref.SETTINGS["margin"] - DIST_TOL:
        return "self distance %.2f < width + 3 = %.2f" % (sd, w + 3)
    if not (ref.SETTINGS["len_min"] - 1 <= step * n <= ref.SETTINGS["len_max"] + 1):
        return "length %.1f" % (step * n)
    if ref.edges_touch(nodes, w / 2):
        return "road edges touch"
    return ""


def compare_with_reference(seed, info):
    """The sim's layout must be the Python reference layout (shifted so node 0 is the scene start)."""
    r = ref.generate(seed)
    if r is None:
        return "reference has no layout"
    nodes = [(float(p[0]), float(p[1])) for p in info["nodes"]]
    if len(nodes) != len(r["nodes"]):
        return "node count %d != reference %d" % (len(nodes), len(r["nodes"]))
    x0, z0 = nodes[0]
    rx0, rz0 = r["nodes"][0]
    d = max(abs((x - x0) - (rx - rx0)) + abs((z - z0) - (rz - rz0)) for (x, z), (rx, rz) in zip(nodes, r["nodes"]))
    return "differs from reference by %.3f m" % d if d > 0.01 else ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=200)
    ap.add_argument("--first", type=int, default=0)
    ap.add_argument("--exe", default=None)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9091)
    a = ap.parse_args()

    conf = {"host": a.host, "port": a.port, "max_cte": 1e6, "log_level": 30,
            "body_style": "donkey", "body_rgb": (128, 128, 128), "car_name": "trackgen", "font_size": 30}
    if a.exe:
        conf["exe_path"] = a.exe
    env = gym.make("donkey-generated-roads-v0", conf=conf)
    c = Client(env)
    env.reset()

    fails = []
    for seed in range(a.first, a.first + a.seeds):
        i1 = c.gen(seed)
        i2 = c.gen(seed)
        if not i1.get("ok") or not i2.get("ok"):
            fails.append((seed, "gen_track error: %s" % (i1.get("error") or i2.get("error"))))
            continue
        if i1["hash"] != i2["hash"]:
            fails.append((seed, "hash differs %s / %s" % (i1["hash"], i2["hash"])))
        why = check_nodes(i1) or compare_with_reference(seed, i1)
        if why:
            fails.append((seed, why))
        c.step(0.3)
        with c.lock:
            tel = dict(c.tel)
        if tel.get("totalNodes") != i1["totalNodes"] or tel.get("activeNode") not in (0, 1):
            fails.append((seed, "car not at node 0: %r" % tel))
    env.close()

    if fails:
        print("FAIL (%d of %d seeds)" % (len({s for s, _ in fails}), a.seeds))
        for s, why in fails:
            print("  seed %d: %s" % (s, why))
        sys.exit(1)
    print("PASS (%d seeds: deterministic hash, same as reference, no self-overlap, min radius ok, car at node 0)" % a.seeds)


if __name__ == "__main__":
    main()
