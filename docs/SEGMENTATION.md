# Segmentation camera (`image_seg`)

Every telemetry message can carry, next to the normal RGB `image`, a **pixel-aligned
segmentation mask of the same frame**:

| class id | meaning |
|---------:|---------|
| 0 | background: everything else (sky, grass, ground, walls, cones, cars, ...) |
| 1 | drivable road surface |

The RGB image path is unchanged: `CameraSensor.cs` is not modified, `image` is produced
exactly as before (same camera, same render, same JPG/PNG encoding). The mask is rendered by
a second, separate camera.

## How it works

```
car prefab
 ├─ CameraSensor  (RGB, disabled Camera, rendered manually)  ──► "image"
 └─ SegCam        (disabled Camera + SegmentationSensor)      ──► "image_seg"
```

1. **`SegLabel`** (`Assets/Scripts/segmentation/SegLabel.cs`) sits on objects that are road.
   On enable it writes `_SegId = (classId/255, ignoreAlpha, 0, 1)` into a per-material
   `MaterialPropertyBlock` with `SetVector`. `SetColor` would apply an sRGB to linear
   conversion, because the project uses Linear colour space. Normal shaders never read `_SegId`.
2. A global default `Shader.SetGlobalVector("_SegId", Vector4.zero)` is set at startup
   (`[RuntimeInitializeOnLoadMethod]` in `SegLabel`), so every unlabelled object is class 0.
3. **`RoadBuilder.InitRoad`** adds a `SegLabel(classId = 1, ignoreAlpha = true)` to the
   generated road right after tagging it `road_mesh`. It also sets the texture's **drivable
   band** from `SegRoadStyles` (see *Road styles* below). **Start lines** (`startingLine`
   component) add a `SegLabel(classId = 1)` to themselves in `Awake`, so they are road too. `ignoreAlpha` makes the whole mesh count
   as road, even when the road material is `TransparentRoad`. That material is alpha-cutout with
   alpha taken from greyscale, so only the white tape lines survive the cutout.
4. **`SegmentationSensor`** (`Assets/Scripts/segmentation/SegmentationSensor.cs`) does the
   following at capture time:
   * copies position, rotation, FOV, near/far, culling mask, occlusion and per-layer cull
     distances, and finally the exact **projection matrix** of `CameraSensor.sensorCam`
   * uses the RGB render texture's width, height and depth-buffer bits
   * `clearFlags = SolidColor`, background `(0,0,0,0)`, no MSAA, no HDR, forward path, no image effects
   * renders into an `ARGB32` `RenderTexture` created with `RenderTextureReadWrite.Linear`,
     `antiAliasing = 1` and point filtering, using
     `SetReplacementShader(Hidden/SegReplacement, "RenderType")`
   * reads it back into a linear `RGB24` `Texture2D` and encodes PNG (lossless)
5. **`Hidden/SegReplacement`** (`Assets/Resources/SegReplacement.shader`) outputs
   `float4(_SegId.r, 0, 0, 1)`. It has no lighting, no fog and no textures, except the alpha
   test. SubShaders:
   * `Opaque`
   * `TransparentCutout`: `_MainTex.a * _Color.a` is tested against `_Cutoff`, the same test as
     Standard Cutout. The test is skipped when `_SegId.g = 1`.
   * `TreeBark`, `TreeLeaf`, `TreeOpaque`, `TreeTransparentCutout`, `Grass`, `GrassBillboard`:
     these use the same vertex animation and billboarding as Unity's own depth-normals
     replacement shader, so foliage hides the road in the mask exactly as it does in RGB.
   * Objects with any other `RenderType` (`Transparent`, `Background`, untagged custom
     shaders, ...) are **not drawn** in the mask, so they are see-through.
6. **`TcpCarHandler.SendTelemetry`** renders the RGB image and then, immediately afterwards in
   the same call (same physics state, same frame), the mask. Both are rendered on demand, so
   RGB and mask are always the same instant, also in asynchronous mode.

## Message

Enable or disable (off by default, per car / connection):

```json
{"msg_type": "seg_config", "enabled": "1"}
{"msg_type": "seg_config", "enabled": "0"}
```

(`"1"`/`"0"` strings as for all other sim messages; `true/false` and numbers are accepted too.)

When enabled, each `telemetry` message gets one extra field, placed right after `image`:

| field | content |
|-------|---------|
| `image_seg` | base64 PNG, RGB 8-bit, same W×H as `image`. **Red channel = class id**; G = B = 0. |

Python decoding:

```python
import base64, io, numpy as np
from PIL import Image
mask = np.asarray(Image.open(io.BytesIO(base64.b64decode(msg["image_seg"]))))[:, :, 0]   # HxW uint8 in {0,1}
```

If a car prefab has no `SegCam` wired, `TcpCarHandler` creates one at runtime the first time
it is needed (a warning is logged), so the message also works with old prefabs.

## Road styles of regen_road (legacy, generated_road / generated_track)

`regen_road` picks the road texture with `road_style` (index into `RoadBuilder.roadMaterials`
in `Prefabs/Scripts/_controllers.prefab`, modulo the list length):

| road_style | texture | look | drivable band (texture V) |
|---:|---|---|---|
| 0 | TexturesCom_Roads0086_1_seamless_S 1 | asphalt, yellow centre line, white edge lines | 0.055 – 1 |
| 1 | TransparentRoad | dark asphalt, dashed centre line, white edges | 0 – 1 |
| 2 | UdacityChallengeRoad | asphalt, white dashed centre line | 0.055 – 1 |
| 3 | UdacitySimRoad | asphalt, **no centre line**, kerbs and dirt at the sides | 0.224 – 0.775 |
| 4 | TexturesCom_Roads0075_1_seamless_S | **dirt road**, grass at the sides | 0.215 – 0.645 |
| 5 | UdacityRoadBridge | **cobblestone**, sidewalks at the sides | 0.233 – 0.757 |
| 6 | SandAlbedo | **sand**, no markings | 0 – 1 |

Styles 0 and 1 are the original two; 2 to 6 were added (materials `SegStyle_*.mat` in
`Textures/roads/Materials`, plus the existing `UdacitySimRoad.mat` and `UdacityRoadBridge.mat`).

```json
{"msg_type": "regen_road", "road_style": "4", "rand_seed": "42", "turn_increment": "0"}
```

* **generated_road:** handled by the car's TrainingManager as before. A new random path is made
  from `rand_seed`. Road width and offset also cycle with the style (`roadWidths` 5/12/10/10/12;
  this is existing behaviour).
* **generated_track:** the TrainingManager is disabled in this scene, so `regen_road` used to do
  nothing. `TcpCarHandler` now falls back to RoadBuilder directly. It changes only the texture
  and rebuilds the same loop, keeping the scene's width and offset, and puts the car back at
  the start.

**Drivable band.** RoadBuilder maps texture V across the road and U along it. Several textures
paint grass, kerbs or sidewalks at their sides. `SegRoadStyles.cs` stores, per texture name,
the V range that is real road surface (asphalt, dirt, cobbles or sand, including painted
lines). The replacement shader writes class 0 outside that range, using the same transformed
UV the RGB shader samples with. To add a road texture:
1. Make a material with it.
2. Append the material to `roadMaterials`.
3. If its sides are not road, add `{ "<texture name>", new Vector2(vMin, vMax) }` to
   `SegRoadStyles.Bands`, with V = 1 − (pixel row / image height).

Unknown textures label the whole mesh and log a warning once.

## Generated tracks (`gen_track`, scene generated_road)

`gen_track` builds a seeded closed-loop track in **generated_road** (the only supported scene)
and replies with `track_info`. The same seed always gives the same track (all randomness comes
from a SplitMix64 generator seeded by `seed`; `UnityEngine.Random` is not used).

```json
{"msg_type": "gen_track", "seed": "123"}
{"msg_type": "gen_track", "seed": "123", "edge_mode": "inset", "edge_inset": "0.1", "centre_mode": "dashed"}
```

Optional overrides (strings; omit or leave empty = from the seed): `shape`, `road_width`,
`base`, `edge_mode`, `edge_inset`, `centre_mode`, `ground`, `return_nodes` (`"1"` adds the
node list to the reply). Forcing one value never changes the others.

**Layout.** Shapes `oval`, `polar` (random blob), `s_bends`, equal probability. Road width
3.5–13.5 m (measured real tracks: waveshare 3.65 m … circuit_launch 13.3 m; car width 1.55 m).
Length 80–450 m, control-point radii 15–70 m, node spacing ~2 m. Hard rules, a failing
candidate is replaced by the next attempt of the same seed:
* min curve radius ≥ 1.5 × half width (the road mesh never folds)
* centreline distance ≥ road width + 3 m between parts that are not neighbours along the track
  (neighbours = closer than π/2 · (width + 3 m) along the centreline)
* the two road edges never touch or cross (checked on the exact mesh boundary)

The path is the road centre (`cte` = distance from the centre). The car is put on node 0,
aligned with the road, velocity zero, `activeNode` 0; `reset_car` returns there. A start line
(class 1, lap trigger) is placed at node 0. `regen_road` still works as before (legacy).

**Markings** (shader `Custom/ProceduralRoad`, parameters per road):

| parameter | values (default probability / range) |
|---|---|
| `base` | `asphalt_grey`, `asphalt_coarse`, `dirt`, `cobblestone`, `sand` (equal); tint ×0.7–1.15 |
| `edge_mode` | `at_edge` 0.45, `inset` 0.40, `none` 0.15 |
| `edge_inset` | 0.03–0.15 of the road width (inset mode) |
| edge line width | 0.12–0.45 m (≤ 12 % of road width), solid |
| `centre_mode` | `none` 0.35, `solid` 0.25, `dashed` 0.40; width 0.10–0.30 m; dash 1–4 m, gap 1–4 m |
| colours | white, yellow, orange, red, blue (+ small jitter), edge and centre independent |
| wear | 0–0.35 (slight noise on the lines) |
| `ground` | `sand` (scene default), `grass`, `mud`, `concrete`, `carpet` (equal; never sand on sand) |

**Labelling rule:** class 1 spans from the outer edge of the left edge line to the outer edge
of the right edge line, lines included. `at_edge` and `none`: the whole road (V 0–1);
`inset`: V in [inset, 1 − inset], the strip outside each line is class 0. Centre lines are always
class 1. The shader draws the lines in the same V coordinate the segmentation shader uses for
the band, with hard edges, so the line's outer edge and the mask edge are the same pixel.

**track_info:** `seed, shape, road_width, length, min_radius, totalNodes, base, edge_mode,
edge_inset, edge_width, edge_color, centre_mode, centre_width, centre_color, dash, gap, ground,
drivable_band, hash` (`hash` = FNV-1a over node positions and all parameters).

To add a base surface: append to `TrackMarkings.Bases` a texture that is in
`RoadBuilder.roadMaterials` and its line-free V window. To add a ground: put a material in
`Assets/Resources/Ground/` and add its name to `TrackMarkings.Grounds`.

Tests: `python tools/check_trackgen.py --seeds 200` (determinism + overlap rules, pass/fail);
`python tools/check_seg.py --gen-seeds 1 2 3 [--edge-mode inset --edge-inset 0.12 ...]`
(masks and overlays). `python tools/trackgen_ref.py --check 200 / --sheet 0 200` runs the same
layout algorithm offline (Python reference, same RNG).

## Existing scenes

Roads built by `RoadBuilder` and start lines (`startingLine`) label themselves. Plain road meshes
of hand-made scenes are labelled at load time by `Scripts/segmentation/SceneSegRules.cs`
(one rule per scene, by object or material name; no scene file is edited). To label another
scene: list its renderers with **Tools ▸ Segmentation ▸ Report Scene Renderers**, add a rule,
run `tools/check_seg.py --env <env-id>` and inspect the overlays.

| scene | road | status |
|-------|------|--------|
| generated_road | RoadBuilder / gen_track | confirmed |
| generated_track | RoadBuilder + start line | confirmed |
| warren | RoadBuilder (`TransparentRoad`, labelled with `ignoreAlpha`) | to check once (overlays) |
| mini_monaco | object `miniMonacoRoad` | to check once (overlays) |
| mountain_track | material `MountainTrackMat` | to check once (overlays) |
| circuit_launch | object `CircuitLaunchRoad` | to check once; likely fails: the lanes are painted in the carpet texture |
| warehouse, waveshare, roboracingleague_1, sparkfun_avc | – | dropped (track only exists in a texture / no road edges) |

A scene that is not clean after the one check is dropped (remove its rule).

## Known limitations

* **Fisheye is applied only to the RGB camera.** `cam_config` `fish_eye_x/y` enables the
  `Fisheye` image effect on the RGB `CameraSensor` only. The mask is undistorted and will *not*
  line up. Either keep `fish_eye_x = fish_eye_y = 0`, or warp the masks in Python with the same
  distortion using **nearest-neighbour** sampling (never bilinear, which would create invalid
  class values). `SegmentationSensor` logs a warning once if fisheye is active.
* `img_d = 1` (greyscale RGB) does not affect the mask.
* The start line is labelled as its whole quad. If the quad is wider than the road, the part
  sticking out is class 1 as well.
* On regenerated roads, the texture goes onto the road prefab material, which has a parallax
  (height) map with strength 0.02. At grazing angles, the painted road/shoulder border in RGB
  can shift by about 1–2 % of the road width relative to the band edge in the mask. This only
  matters for styles with a band (0, 2, 3, 4, 5).
* Objects whose shader uses a `RenderType` other than those listed above are invisible in the
  mask. The same applies to shaders that displace vertices in a custom way (water, wind shaders
  not based on Unity's terrain/tree engine). Road behind them is labelled as visible.
* Billboard trees rendered by the terrain at distance (`TreeBillboard`) are not drawn in the
  mask. This only matters for terrain scenes (warren, mini_monaco, mountain_track). Their
  terrain data is binary, so whether they contain trees was not checked. If they do, check the
  overlays for road showing through distant trees.
* The second camera B (`cam_config_b`, `imageb`) has no mask.
* `step_mode synchronous` is **not usable** in this fork. The handler only sets
  `Time.timeScale = 0`, and it does so from the socket thread, where Unity APIs are not allowed.
  Nothing ever advances time again, so telemetry stops. Use the default asynchronous mode. The
  mask is still exact, because RGB and mask are rendered back to back in the same
  `SendTelemetry()` call.

## Extended telemetry (`cte`, `pos_*`, `vel_*`, `set_position`)

`GlobalState.extendedTelemetry` controls:

* the telemetry fields `cte`, `pos_x/y/z` and `vel_x/y/z` (`TcpCarHandler.SendTelemetry`)
* whether the `set_position` message is honoured (`TcpCarHandler.OnSetPosition`)

How it is set:

* Default: **`true`** (`Assets/Scripts/GlobalState.cs`).
* There is **no command-line argument** for it. The only command-line arguments parsed are
  `--host` and `--port` (`SandboxServer.CheckCommandLineConnectArgs`).
* It is persisted in **PlayerPrefs** under key `extendedTelemetry` (int 0/1).
  `GlobalStateEditor.Awake()` (menu scene) loads it and immediately saves the prefs again.
* To change it: in the main menu, the settings panel (`GlobalStateEditor` GUI) has a checkbox
  **`extendedTelemetry`**. Toggle it and press **Save**. The value persists across launches.
* PlayerPrefs location on Windows: the registry at
  `HKEY_CURRENT_USER\Software\DonkeyCar\donkey_sim` for a build, or
  `HKEY_CURRENT_USER\Software\Unity\UnityEditor\DonkeyCar\donkey_sim` in the editor.
  Unity stores the value as `extendedTelemetry_h<hash>`.
* Pressing Play directly in a track scene in the editor skips the menu, so the default `true`
  applies.

`tools/check_seg.py` needs `cte` to decide "car is on the track", so extended telemetry must be on.

## Acceptance test

```
python tools/check_seg.py                         # sim already running at 127.0.0.1:9091
python tools/check_seg.py --exe <path to exe>     # let gym-donkeycar start it
python tools/check_seg.py --env donkey-generated-track-v0
python tools/check_seg.py --styles 0,1,2,3,4,5,6 --frames 100          # every road style
python tools/check_seg.py --env donkey-generated-track-v0 --styles 0,3,4,6
```

With `--styles`, the script sends `regen_road` for each style (seed `--seed`, default 42),
waits `--regen-wait` seconds and records into `seg_check_out/style_<n>/`. It then prints a
per-style PASS/FAIL summary.

The script loads the scene, sends `seg_config enabled=1` and drives slowly (it steers towards
the road pixels, throttle 0.15). It saves 200 frames to `seg_check_out/`:
`rgb_*.png`, `mask_*.png` (0/255), `overlay_*.png` (50 % green on road, outermost road pixels in
yellow), `overlay_x4_*.png` (4× nearest-neighbour, for pixel inspection) and `frames.csv`.

It asserts:

* `np.unique(mask)` ⊆ {0, 1} (and G/B are 0) for every frame
* mask H×W equals RGB H×W
* road pixels exist in > 95 % of the frames in which the car is on the track
  (`|cte| <= 2.0` and `hit == "none"`, i.e. judged independently of the mask)

Pixel alignment is checked visually in `overlay_x4_*.png`: the yellow pixels must lie exactly
on the road border visible in the RGB image.
