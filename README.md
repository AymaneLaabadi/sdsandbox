# DonkeyCar Simulator: Road Segmentation Fork

[![Unity 2020.3.49f1](https://img.shields.io/badge/Unity-2020.3.49f1-black?logo=unity)](https://unity.com/releases/editor/archive)
[![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-3776AB?logo=python&logoColor=white)](tools/requirements.txt)
[![Fork of tawnkramer/sdsandbox](https://img.shields.io/badge/fork%20of-tawnkramer%2Fsdsandbox-lightgrey?logo=github)](https://github.com/tawnkramer/sdsandbox)

A fork of the [DonkeyCar Unity simulator](https://github.com/tawnkramer/sdsandbox). It sends a
**pixel-perfect drivable-road mask** along with every camera frame, and it can generate an
**endless supply of seeded, randomised tracks**. Together these produce labelled data for
training road-segmentation models.

<p align="center">
  <img src="docs/assets/segmentation/rgb_mask_overlay.png" width="760"
       alt="Camera image, segmentation mask and overlay for a carpet track, a concrete track, circuit_launch and roboracingleague">
  <br><sub>Rows: generated carpet track (tape only), generated concrete track, and the evaluation scenes circuit_launch and roboracingleague_1.</sub>
</p>

---

## What this fork adds

| Feature | Message | What you get |
|---|---|---|
| **Segmentation camera** | `seg_config` | An extra `image_seg` field in every `telemetry` message: a lossless PNG mask of the *same* frame, with the same size as `image`. **0 = background, 1 = drivable road.** |
| **Procedural tracks** | `gen_track` | A realistic circuit built from a seed (straights, hairpins, chicanes) with a random surface (asphalt, concrete, dirt, sand or carpet), edge-line colour and centre line. The same seed always gives the same track. The sim replies with `track_info`. |
| **More road styles** | `regen_road` | `road_style` 0–6: asphalt, dirt, cobblestone, sand and more, up from the original 2. |
| **Evaluation scenes** | – | `mini_monaco`, `warren`, `circuit_launch` and `roboracingleague_1` are labelled at load time, including tracks that only exist as tape painted into a floor texture. No scene file is edited. |
| **Test tools** | – | `tools/check_seg.py` and `tools/check_trackgen.py`: automatic pass/fail checks plus overlays for a visual check. |

The original RGB pipeline is **unchanged**: `image` is rendered and encoded exactly as before. A
second, hidden camera renders the mask at the same moment, using a replacement shader.

<p align="center">
  <img src="docs/assets/segmentation/track_variety.png" width="760"
       alt="Camera images of four generated tracks with different surfaces and two evaluation scenes">
  <br><sub>Camera images: four <code>gen_track</code> tracks (surface, edge colour and centre line drawn independently per seed; on carpet only the tape marks the track) and two evaluation scenes.</sub>
</p>

## How it fits together

```mermaid
flowchart LR
    A["<b>sdsandbox</b> (this repo)<br/>Unity project"] -- build --> B["donkey_sim.exe"]
    B -- "TCP :9091<br/>image + image_seg" --> C["<b>DonkeyCar</b> project<br/>donkey_seg_datagen"]
    C -- "RGB + mask PNGs" --> D["donkey_seg_train<br/>segmentation model"]
    C -- "seg_config / gen_track" --> B
```

* **This repo:** the simulator source. Change it here when you need new labels, scenes or track
  variety, then rebuild the `.exe`.
* **[DonkeyCar project](https://github.com/AymaneLaabadi/donkeycar):** the Python side. It drives
  the car, saves RGB + mask pairs (`donkey_seg_datagen`) and trains the model (`donkey_seg_train`).
  It expects the build at `../Builds/Segmentation/donkey_sim.exe`.

Recommended folder layout:

```
DonkeyCar Project/
├── sdsandbox/                         ← this repo
├── Builds/Segmentation/donkey_sim.exe ← your build of this repo
└── DonkeyCar/                         ← data generation + training
```

## Quick start

### 1. Build the simulator

1. Install **Unity 2020.3.49f1** with Unity Hub. Use this exact version to avoid re-importing
   the project and getting noisy diffs.
2. *Add project from disk* → `sdsandbox/sdsim`. The first import takes a few minutes.
3. **File ▸ Build Settings ▸ Build**, and choose the output folder `DonkeyCar Project/Builds/Segmentation/`.
   Name the executable `donkey_sim.exe`.

To try changes without building, open `Assets/Scenes/menu` and press **Play**. The sim then
listens on `127.0.0.1:9091`, just like the `.exe`.

### 2. Collect data

Use the [DonkeyCar project](https://github.com/AymaneLaabadi/donkeycar). Its README covers the
install, the collector keys and the training. In short:

```bash
cd DonkeyCar
uv sync --extra torch
uv run python -m donkey_seg_datagen.collect --split train
```

### 3. Or talk to the sim directly

Any [gym-donkeycar](https://github.com/tawnkramer/gym-donkeycar) client works. Send one extra message:

```python
import base64, io
import gymnasium as gym, gym_donkeycar, numpy as np
from PIL import Image

env = gym.make("donkey-generated-roads-v0",
               conf={"exe_path": "../Builds/Segmentation/donkey_sim.exe"})
handler = env.unwrapped.viewer.handler
env.reset()

handler.blocking_send({"msg_type": "gen_track", "seed": "42"})        # optional: random track #42
handler.blocking_send({"msg_type": "seg_config", "enabled": "1"})     # turn the mask on

def decode_mask(msg):
    """Every telemetry message from now on carries image_seg -> HxW uint8, values {0, 1}."""
    png = base64.b64decode(msg["image_seg"])
    return np.asarray(Image.open(io.BytesIO(png)))[:, :, 0]
```

## Message reference

**`seg_config`**: turns the mask on or off for this connection. It is off by default.

```json
{"msg_type": "seg_config", "enabled": "1"}
```

`image_seg` is a base64 PNG with the same width and height as `image`. **Red channel = class id**;
G and B are 0. Always resize masks with **nearest-neighbour** interpolation.

**`gen_track`**: builds a seeded circuit in the `generated_road` scene (`donkey-generated-roads-v0`)
and puts the car on its longest straight.

```json
{"msg_type": "gen_track", "seed": "123"}
{"msg_type": "gen_track", "seed": "123", "surface": "carpet", "centre_mode": "none"}
```

**Layout:** like a real circuit. A seeded polygon (convex hull plus notches) has every corner rounded
with its own radius. That gives straights, hairpins, chicanes and sweepers; every seed is different.

<p align="center">
  <img src="docs/assets/segmentation/track_layouts.png" width="900"
       alt="Top view of twelve generated circuits; the red mark is the start line">
  <br><sub>Twelve <code>gen_track</code> seeds, top view (red = start line, always on the longest straight).</sub>
</p>

**Look:** every choice is independent and uniform, in this order:

| Step | Override | Values (leave it out to draw the value from the seed) |
|---|---|---|
| 1 | `surface` | `asphalt`, `concrete`, `dirt`, `sand`, `carpet` |
| 1b | `surface_variant` | a variant of that surface, e.g. `asphalt_dark`, `concrete_panels`, `carpet_blue` ([full list](docs/SEGMENTATION.md#look-trackmarkingscs-shader-customproceduralroad)) |
| 2 | `edge_color` | `white`, `yellow`, `orange`, `red`, `blue`, `green`. Edge lines always exist, flush with the road edge |
| 3 | `centre_mode` | none or present; if present `solid` or `dashed` |
| 3b | `centre_color` | same six colours |
| – | `ground` | outdoor floor for non-carpet tracks: `grass`, `mud`, `sand`, `concrete` |
| – | `road_width` | 3.5 – 13.5 m |
| – | `return_nodes` | `"1"` adds the centreline nodes to the reply |

On **carpet** tracks, the whole floor is that carpet and the track exists only through its tape lines,
like a real indoor track.

The `track_info` reply contains the seed, every parameter that was drawn, the corner count, length,
minimum radius and a `hash`. Store it next to your data so each sample can be traced back to its track.

**`regen_road`** (legacy roads, not used for training data): `{"msg_type": "regen_road", "road_style": "4", "rand_seed": "42"}`,
with `road_style` 0–6.

**Labelling rule:** class 1 covers the road surface from the outer edge of the left edge line to the
outer edge of the right edge line, **lines included**. The start line also counts as road. Grass, kerbs and
sidewalks painted into a road texture are class 0.

## Scene support

| Scene | Road mask | Status |
|---|---|---|
| `generated_road` (+ `gen_track`, `regen_road`) | built by the sim | ✅ verified (`check_seg`, `check_trackgen`) |
| `generated_track` | built by the sim + start line | ✅ verified |
| `mini_monaco` | rule in `SceneSegRules.cs` | ✅ verified (road present in 100 % of on-track frames) |
| `warren` | built by the sim | ✅ verified |
| `circuit_launch` | label texture (track painted into the carpet) | ✅ verified, mask edge = outermost tape pixel |
| `roboracingleague_1` | label texture (tape loops on an overlay) | ✅ verified |
| `mountain_track` | rule in `SceneSegRules.cs` | 🟡 overlays look right; not in the evaluation set |
| `warehouse`, `waveshare`, `sparkfun_avc` | – | ❌ not labelled |

`mini_monaco`, `warren`, `circuit_launch` and `roboracingleague_1` are the **evaluation scenes**:
use them to test a model trained on `gen_track` data, not to train it.

## Verifying changes

Install the tool dependencies (`pip install -r tools/requirements.txt`). Start the sim (or pass
`--exe`), then run:

```bash
# masks valid, aligned, road visible → PASS/FAIL
python tools/check_seg.py --gen-seeds 1 2 3 --frames 100

# same check on an existing scene
python tools/check_seg.py --env donkey-minimonaco-track-v0

# determinism + no overlapping or folding roads
python tools/check_trackgen.py --seeds 200

# offline preview of track layouts (no sim needed)
python tools/trackgen_ref.py --sheet 0 50
```

`check_seg.py` writes `rgb_*`, `mask_*`, `overlay_*` and `overlay_x4_*` images to `seg_check_out/`.
Check the `overlay_x4` images: the yellow border must sit exactly on the road edge.

> Extended telemetry (`cte`, `pos_*`) must be **on**. It is on by default; you can toggle it in the sim's main menu settings.

## Working on the segmentation

| You want to… | Look at |
|---|---|
| Understand the rendering | [`docs/SEGMENTATION.md`](docs/SEGMENTATION.md) (full design notes) |
| Change how the mask is rendered | `Assets/Scripts/segmentation/SegmentationSensor.cs`, `Assets/Resources/SegReplacement.shader` |
| Mark an object as road | add a `SegLabel` component (`classId = 1`) |
| Label a new scene | **Tools ▸ Segmentation ▸ Report Scene Renderers**, then add a rule in `SceneSegRules.cs` |
| Add a road texture | material → `RoadBuilder.roadMaterials`; if its sides aren't road, add its drivable V-band in `SegRoadStyles.cs` |
| Add a track surface or ground | `Assets/Scripts/trackgen/TrackMarkings.cs` (`Variants`, `OutdoorGrounds`), surface materials in `Assets/Resources/TrackSurfaces/`, ground materials in `Assets/Resources/Ground/` |
| Label a track that is painted into a floor texture | `tools/make_seg_masks.py` (label texture in the floor's UV space) + a rule in `SceneSegRules.cs` |
| Change the track layouts | `TrackLayouts.cs` **and** `tools/trackgen_ref.py`. They are line-by-line twins with the same RNG, so keep them in sync. Then run `check_trackgen.py` |
| Handle a new TCP message | `Assets/Scripts/tcp/TcpCarHandler.cs` |
| Add the mask camera to a car prefab | **Tools ▸ Segmentation ▸ Add SegCam To Car Prefabs**. Without it, the camera is created at runtime with a warning |

**Workflow:** change the code → press Play in the editor or rebuild the `.exe` → run `check_seg.py` → check the overlays → open a PR.

## Known limitations

* **Fisheye** (`cam_config` `fish_eye_x/y`) only distorts the RGB image. Keep it at 0, or warp the
  mask in Python with nearest-neighbour sampling.
* **`step_mode synchronous` does not work** in this sim version. Use the default asynchronous mode.
  RGB and mask are still rendered back to back for the same frame.
* The second camera (`imageb`) has no mask.
* Two classes only (road / background) for now.

More details are in [docs/SEGMENTATION.md § Known limitations](docs/SEGMENTATION.md#known-limitations).

## Credits

Based on [tawnkramer/sdsandbox](https://github.com/tawnkramer/sdsandbox) (the DonkeyCar simulator)
and [gym-donkeycar](https://github.com/tawnkramer/gym-donkeycar). For the original simulator
documentation (creating tracks, the path manager, the private API), see [`docs/`](docs/README.md) and the
[DonkeyCar docs](https://docs.donkeycar.com/guide/deep_learning/simulator/).
Licensed under the original project's [LICENSE](LICENSE).
