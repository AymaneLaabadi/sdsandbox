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

`gen_track` builds a seeded closed circuit in **generated_road** (the only supported scene) and
replies with `track_info`. The same seed always gives the same track (all randomness comes from a
SplitMix64 generator seeded by `seed`; `UnityEngine.Random` is not used). This is the way to make
training data; the legacy `regen_road` styles above are not used for it.

```json
{"msg_type": "gen_track", "seed": "123"}
{"msg_type": "gen_track", "seed": "123", "surface": "carpet", "surface_variant": "carpet_blue", "centre_mode": "none"}
```

Optional overrides (strings; omit or leave empty = from the seed): `road_width`, `surface`,
`surface_variant`, `edge_color`, `centre_mode`, `centre_color`, `ground`, `return_nodes`
(`"1"` adds the node list to the reply). Every value is always drawn from the seed and then
replaced by its override, so forcing one value never changes the others.

### Layout (`TrackLayouts.cs`, Python twin `tools/trackgen_ref.py`)

Built like a real circuit, so every seed has straights, corners of different radii, hairpins and
chicanes:

1. 6–16 seeded points in a box (30–130 m, at least 10 road widths, aspect 0.45–1) → convex hull.
2. Each hull edge longer than 3 road widths gets a **notch** with a per-track probability of
   0.45–1: a vertex pushed inwards (75 %) or outwards by 20–60 % of the edge length. Notches make the
   hairpins, chicanes and bus stops. Every track has at least one.
3. Every corner of that polygon is **rounded with its own radius** (log-uniform 4–40 m, so tight
   corners are common; never tighter than the min-radius rule; corners may use at most 92 % of an
   edge). Result: true straights joined by constant-radius arcs.
4. Random rotation and direction; resampled to nodes every ~2 m; node 0 (start line, car) is placed
   on the **longest straight**.

Road width 3.5–13.5 m (measured real tracks: waveshare 3.65 m … circuit_launch 13.3 m; car width
1.55 m), length 80–500 m. Hard rules, a failing candidate is replaced by the next attempt of the
same seed (median 7 attempts, ≤ 62 over 300 seeds):
* min curve radius ≥ 1.5 × half width (the road mesh never folds)
* centreline distance ≥ road width + 3 m between parts that are not neighbours along the track
  (neighbours = closer than π/2 · (width + 3 m) along the centreline)
* the two road edges never touch or cross (checked on the exact mesh boundary)

The path is the road centre (`cte` = distance from the centre). The car is put on node 0,
aligned with the road, velocity zero, `activeNode` 0; `reset_car` returns there. A start line
(class 1, lap trigger) is placed at node 0.

### Look (`TrackMarkings.cs`, shader `Custom/ProceduralRoad`)

Every choice is independent and **uniform** (equal probability), in this order:

| step | choice | values |
|---|---|---|
| 1 | `surface` | `asphalt`, `concrete`, `dirt`, `sand`, `carpet` |
| 1b | `surface_variant` (of that surface) | asphalt: `asphalt_grey`, `asphalt_coarse`, `asphalt_dark` · concrete: `concrete_cast`, `concrete_slab`, `concrete_panels`, `concrete_polished` · dirt: `dirt_track`, `dirt_rocky` · sand: `sand` · carpet: `carpet_office`, `carpet_weave`, `carpet_grey`, `carpet_blue`, `carpet_green`, `carpet_red` |
| 2 | `edge_color` (both edge lines) | `white`, `yellow`, `orange`, `red`, `blue`, `green` |
| 3 | centre line present? | yes / no |
| 3b | `centre_mode` if present | `solid`, `dashed` (dash 1–4 m, gap 1–4 m) |
| 3c | `centre_color` | same six colours, independent of the edge colour |

Not random-by-category but varied per track: edge line width 3–7 % of the road width (≥ 0.15 m),
centre line width 1.5–4 % (≥ 0.10 m), line wear 0–0.35, surface brightness ×0.85–1.1, small colour
jitter.

**Edge lines always exist and sit flush with the road edge**: left line V [0, w], right line
[1 − w, 1]. The outermost line pixel is the outermost road pixel of the mask.

**Ground:**
* `carpet` tracks: the **whole floor is the same carpet** (shader `Custom/WorldGround`, mapped in
  world space) and the road only draws its tape lines (`_LinesOnly`, every other road pixel is
  discarded). The track exists only through its edge lines, like a taped indoor track; floor and
  track surface are seamless by construction. The lines-only road casts no shadow.
* other surfaces: `grass`, `mud`, `sand` (scene default), `concrete`, uniform among those that differ
  from the road (no sand on sand, concrete on concrete or mud next to dirt).

**Labelling rule:** class 1 = the whole road mesh = from the outer edge of the left edge line to
the outer edge of the right edge line, lines included (V 0–1). Centre lines are inside class 1.
The segmentation shader never clips, so on carpet tracks the mask still covers the whole track
between the tapes.

**track_info:** `seed, shape ("circuit"), corners, road_width, length, min_radius, totalNodes,
surface, surface_variant, edge_width, edge_color, centre_mode, centre_width, centre_color, dash,
gap, ground, drivable_band, hash` (`hash` = FNV-1a over node positions and all parameters).

**Adding a surface variant:** make a material with the texture in `Assets/Resources/TrackSurfaces/`
and add a `Variant` line to `TrackMarkings.Variants` (family, line-free V window or 0–1 for a whole
seamless texture, metres per repeat, smoothness, linear tint). A new family also goes into
`TrackMarkings.Families`. To add an outdoor ground: put a material in `Assets/Resources/Ground/`
and add its name to `TrackMarkings.OutdoorGrounds`.

Tests: `python tools/check_trackgen.py --seeds 200` (determinism, same layout as the Python
reference, overlap rules; pass/fail); `python tools/check_seg.py --gen-seeds 1 2 3 [--surface carpet ...]`
(masks and overlays). `python tools/trackgen_ref.py --check 200 / --sheet 0 200` runs the same
layout algorithm offline (Python reference, same RNG; the C# port gives the same nodes to 1e-12 m).

## Existing scenes

These scenes are for **evaluating** a model trained on `gen_track` data, not for training. Same
labelling rule: road = between the outer edges of the boundary lines, lines included; everything
else (walls, cones, carpet outside the tape, …) is background.

Roads built by `RoadBuilder` and start lines (`startingLine`) label themselves. Hand-made scenes are
labelled at load time by `Scripts/segmentation/SceneSegRules.cs` (rules by object or material name;
no scene file is edited).

**Tracks painted into a floor texture** (circuit_launch, roboracingleague_1) use a **label texture**:
a black/white image in the UV space of the floor texture (`Assets/Resources/SegMasks/<scene>.png`,
R8, point-filtered, no mipmaps). `SegLabel.labelMask` hands it to `Hidden/SegReplacement`, which
samples it at exactly the UV the RGB shader samples the floor with, so the mask edge is the painted
tape edge. The textures are built by `python tools/make_seg_masks.py` (previews in
`seg_masks_preview/`):
* **circuit_launch:** the track is painted into `CircuitLaunchDiffuse.png`. `CircuitLaunch.fbx` has a
  road ribbon (`CircuitLaunchRoad`) that covers exactly the track; its UV polygons are rasterised and
  grown into the white tape pixels connected to them (outer tape edge). The ribbon and the carpet
  (`CircuitLaunchCarpet`) share the texture and overlap in depth, so **both** get the label texture:
  whichever wins the depth test, the label is the same. (Labelling the ribbon alone failed for that
  reason: the unlabelled carpet won near the camera.)
* **roboracingleague_1:** two closed white tape loops on an alpha-cutout overlay quad
  (`track_lines3`). The quad is drawn whole in the mask (`ignoreAlpha`) and the label texture keeps
  the ring between the loops plus both tapes.

| scene | how the road is labelled | status |
|-------|------|--------|
| generated_road | RoadBuilder / gen_track | ✅ verified |
| generated_track | RoadBuilder + start line | ✅ verified |
| mini_monaco | object `miniMonacoRoad` | ✅ verified (road in 100 % of on-track frames, overlays checked) |
| warren | RoadBuilder (`TransparentRoad`, `ignoreAlpha`) | ✅ verified (100 %, mask spans the white edge lines) |
| circuit_launch | label texture on `CircuitLaunchRoad` + `CircuitLaunchCarpet` | ✅ verified (100 %; mask edge = outermost tape pixel, measured per row) |
| roboracingleague_1 | label texture on the `track_lines3` overlay | ✅ verified |
| mountain_track | material `MountainTrackMat` | 🟡 overlays look right; not part of the evaluation set |
| warehouse, waveshare, sparkfun_avc | – | not labelled |

To label another scene: list its renderers with **Tools ▸ Segmentation ▸ Report Scene Renderers**
(or in batch mode `-executeMethod SegmentationTools.BatchReport -segScenes <a,b> -segOut <file>`), add a
rule (and a label texture if the track is only painted), run `tools/check_seg.py --env <env-id>` and
inspect the overlays.

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
