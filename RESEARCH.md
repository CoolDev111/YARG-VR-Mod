# YARG-VR — RESEARCH.md

*How YARG works behind the scenes, and how a camera-only VR mod is built on top of it.*

Everything below was verified directly against the **YARG source code** (open source,
`https://github.com/YARC-Official/YARG`, default branch `master`, repository snapshot of
2026-09-02) and the **YARG v0.15.0** release package. File paths are relative to the repo
root and can be opened on GitHub to verify every claim.

---

## 1. Game overview

| | |
|---|---|
| Game | **YARG** (Yet Another Rhythm Game) — Clone Hero-style band rhythm game |
| Developer | YARC (Yet Another Rhythm Company), `YARC-Official/YARG` |
| Latest release | **v0.15.0** (published 2026-06-24, commit target `master`) |
| Windows asset | `YARG_v0.15.0-Windows-x64.zip` — 127.9 MB |
| License | **LGPL-3.0** (repo `LICENSE`) |
| Engine | **Unity 6000.3.5f2** (Unity 6) — `ProjectSettings/ProjectVersion.txt` |
| Scripting backend | **Mono** (`ProjectSettings.asset` → `scriptingBackend: Android: 0`, standalone defaults to Mono; the release zip ships `YARG_Data/Managed` with 196 DLLs and `MonoBleedingEdge`) |
| API compatibility | `.NET 4.8` profile (`apiCompatibilityLevel: 3` = `NET_Unity_4_8`) |
| Color space / pipeline | **URP 17.3.0** (`com.unity.render-pipelines.universal`) with **RenderGraph** (Unity 6 default) |
| Input | **Input System package 1.17.0 only** (`activeInputHandler: 1` → legacy `UnityEngine.Input` API throws) |
| UI | **uGUI** (`com.unity.ugui 2.0`) for gameplay HUD + menus seen in-game; UI Toolkit present for some editor/menu tooling, but everything relevant to gameplay rendering is uGUI |
| Other notable packages | Cinemachine 2.10.5, Addressables 2.8, UniTask 2.5.3, UniVRM (character rendering), ProBuilder, `in.yarg.core` (local package wrapping **YARG.Core**, the C# chart/audio engine) |
| Identity | `ProjectSettings.asset` → `companyName: YARC`, `productName: YARG`; release zip `YARG_Data/app.info` = `YARC` / `YARG` |
| Code layout | Game C# under `Assets/Script/**` (compiled into `Assembly-CSharp.dll`, 1.65 MB); chart/audio engine under `YARG.Core/` (`YARG.Core.Package.dll`, 0.88 MB) |

Scenes (from `Assets/Scenes/`):

- `PersistentScene` — root scene, always loaded (global managers live here)
- `MenuScene` — main menu
- `Gameplay` — song gameplay (the only scene this mod touches)
- `ScoreScene` — post-song score screen
- `CalibrationScene` — lag calibration

## 2. How the gameplay screen is actually composed

This is the single most important discovery for a camera-only VR mod, because **YARG's
gameplay view is not rendered by one camera to the screen — it is a uGUI canvas
composition fed by offscreen RenderTextures.**

`Assets/Scenes/Gameplay.unity` contains exactly these UI roots (all verified in the scene
YAML: `Canvas` components with `m_RenderMode: 0` = **Screen Space Overlay**, on layer 5 = "UI"):

| Canvas | Contents |
|---|---|
| `Canvas` | `Venue Output` (RawImage), `Venue Fade Overlay`, `Highways Output` (RawImage), `Dimmer`, `Black Background`, `Main HUD Container`, `Player HUDs`, `Practice Hud`, `Song Info Display`, `Pause Menu Manager`, text notifications, etc. |
| `Score Canvas` | live score display |
| `Lyric Canvas` | vocals lyric bar |

### 2.1 The venue (stage) layer — `Assets/Script/Venue/VenueCamera/`

- `CameraManager.cs` (namespace `YARG.Venue.VenueCamera`): a `GameplayBehaviour` that, on
  chart load (`OnChartLoaded`), collects **all `Camera` components under the loaded venue
  prefab** (`_venue.GetComponentsInChildren<Camera>(true)`), disables all of them except the
  first `CameraLocation.Stage` camera, and drives **camera cuts** — either from the chart's
  `VenueTrack.CameraCuts` events or a beat-synced timer. Cameras are switched with plain
  `GameObject.SetActive(...)`; the active one is exposed as
  **`public Camera CurrentCamera { get; }`**. There are ~25 authored camera locations
  (Stage, Guitar, GuitarCloseup, Bass, Drums, Keys, Vocals, Behind, Crowd, …).
- `VenueCamera.cs` — small metadata component on each venue camera (`CameraLocation`,
  `CameraDistance` (Near/Far), `CameraOrientation` (Front/Behind), `CameraCutSubjects`).
  Venue cameras are **statically authored**: nothing moves their transform per-frame
  (motion during songs comes from the venue's animator channels, e.g.
  `Assets/Script/Venue/Animator/Channels/CameraChannel.cs`).
- `VenueCameraRenderer.cs` (namespace `YARG.Gameplay`, `[RequireComponent(typeof(Camera))]`,
  added to every venue camera by `CameraManager`): the stage is rendered **offscreen**:
  - `Awake()` disables the camera and sets up URP camera data;
  - `Update()` runs an accumulator-based **FPS cap** (`Settings.VenueFpsCap`);
  - when it renders: `_renderCamera.targetTexture = _venueTexture; _renderCamera.enabled = true;`
    and in its `RenderPipelineManager.endCameraRendering` callback it disables the camera
    again and clears `targetTexture`;
  - `_venueTexture` is an **HDR RenderTexture** sized `Screen.width * renderScale ×
    Screen.height * renderScale` (`renderScale` = `GraphicsManager.Instance.VenueRenderScale`,
    default 1.0 — this field is `public` on the component, so a mod can raise it for HMDs);
  - the texture is displayed by the **`Venue Output` RawImage**:
    `_venueOutput.texture = _venueTexture` where `_venueOutput` is resolved via
    `GameObject.Find("Venue Output")`;
  - venue post-processing (mirror wipes, scanlines, posterize, trails, slow-FPS — the classic
    RB3 "killer band" effects) is injected as a URP `ScriptableRenderPass` via
    `RenderPipelineManager.beginCameraRendering` and RenderGraph.
- `CameraManager.PostProcessing.cs` — the `partial` second half handling those volume-based effects.

### 2.2 The highway layer — `Assets/Script/Gameplay/Visuals/`

- `HighwayCameraRendering.cs`: the note highways are captured by an **orthographic top-down
  camera** ("Highway Renderer" GO, depth 0, clear flags SolidColor) into
  `HighwaysOutputTexture` (HDR RT, `Screen`-sized, alpha carries fades), displayed by the
  **`Highways Output` RawImage**. Perspective is faked in a **shader**: per-lane view /
  inverse-view / projection matrices (`_YargCamViewMatrices`, `_YargCamProjMatrices`, …) are
  uploaded as global shader arrays and the highway texture is re-projected per player lane
  (`WorldToViewport`, `RecalculateScaleFactors`, lane screen-width/height caps).
- `CameraPositioner.cs`: per-player camera preset component (FOV, `PositionY`,
  `PositionZ - 6`, pitch `Rotation`) that animates the **highway** camera with DOTween
  (raise-on-start, kick **Bounce**, star-power **Punch**, **Scoop**, lower-on-end). This is
  the camera that YARG's in-game "camera position" settings manipulate.
- Practical consequence: moving either of these cameras for VR makes no sense — the highway
  is a 2D re-projection of an ortho capture. The highway layer is best treated as a stable
  screen-space overlay (which is also how it behaves on desktop).

### 2.3 HUD / pause menu — `Assets/Script/Gameplay/HUD/`

Pure uGUI: `PauseMenuManager.cs` + `GenericPause.cs` / `FailPause.cs` / `PracticePause.cs` /
`SetlistPause.cs` / `ReplayPause.cs` (TextMeshPro + Image/RawImage), score boxes
(`ScoreBox/`), `LyricBar.cs`, `TextNotifications.cs`, `CountdownDisplay.cs`,
`MainHUDPaddingAdjuster.cs`, draggable HUD elements (`Dragging/DraggableHudManager.cs`), etc.
All of it lives on the overlay canvases above, so **re-targeting those canvases to a
different camera moves the entire gameplay UI** — HUD, pause menu, score, lyrics — as one
atomic unit. Backgrounds (video/etc.) are uGUI too (`BackgroundManager.cs`: `VideoPlayer`
+ `_backgroundImage` RawImage).

### 2.4 Why this architecture is a gift for VR

The final gameplay picture = **one canvas tree**. If a mod:

1. creates its own camera rendering only the "UI" layer into an OpenVR-sized
   `RenderTexture`, and
2. switches those canvases from *Screen Space Overlay* to *Screen Space Camera* pointing at
   that camera, and
3. drives **YARG's own venue camera** transform from the HMD,

…then the headset sees the *exact* desktop composition (venue texture + highway texture +
HUD + pause menu), the stage view pans/rotates **1:1 with your head** (the `Venue Output`
RawImage is literally the venue camera's frustum), and the HUD stays locked to your gaze.
Zero custom shaders, zero render-graph surgery, zero Harmony patches — **only cameras are
touched**.

## 3. MelonLoader on YARG

| | |
|---|---|
| Loader | **MelonLoader 0.7.3** (latest x64 zip; `version.dll` proxy + `MelonLoader/net6|net35|net472`) |
| Mod target | **net472** variant (game is Mono, .NET 4.8 API profile) — `MelonLoader.dll` 1.95 MB |
| Harmony | MelonLoader ships 0Harmony 2.x, but **this mod needs no Harmony patches at all** (see below) |
| Game identity | `[assembly: MelonGame("YARC", "YARG")]` (verified via `YARG_Data/app.info`) |
| Entry points used | `MelonMod.OnInitializeMelon / OnUpdate / OnLateUpdate / OnSceneWasLoaded / OnDeinitializeMelon`, `MelonPreferences`, `MelonLogger`, `MelonCoroutines` |

Two Unity-6-specific facts shape the code:

1. **`Camera.onPreRender/onPostRender` do not fire under URP.** Everything goes through
   `RenderPipelineManager.beginCameraRendering / endCameraRendering` (verified: YARG's own
   `VenueCameraRenderer` and `HighwayCameraRendering` use exactly these events).
2. **`UnityEngine.SceneManagementModule` no longer exists** in Unity 6 — `SceneManager`/`Scene`
   live in `UnityEngine.CoreModule` (confirmed by inspecting the game's `Managed` folder).

Why no Harmony is needed:

- Venue cameras are only moved by **animator channels** and by `SetActive` camera cuts.
  An animator writes transforms during the *animation phase*, which always runs **before
  any `LateUpdate`** — so re-writing the transform from `MelonMod.OnLateUpdate` deterministically
  wins every frame without patching anything.
- The canvas re-target and composite camera are purely additive; nothing in YARG resets
  `Canvas.renderMode` or `worldCamera` at runtime.
- The mod still isolates its one YARG-specific touchpoint
  (`YARG.Venue.VenueCamera.CameraManager.CurrentCamera`) in `src/YargBridge.cs` behind
  reflection, so a future YARG update that renames it degrades gracefully instead of crashing.

## 4. SteamVR / OpenVR integration

| | |
|---|---|
| API | **OpenVR** via Valve's official single-file C# binding `openvr_api.cs` (vendored under `vendor/OpenVR/`, BSD-3-Clause) + native `openvr_api.dll` (win64, shipped with the mod) |
| Interfaces in play | `IVRSystem_026`, `IVRCompositor_029` (current binding), `OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene)`, `OpenVR.Compositor` |
| Native loading | `kernel32!LoadLibrary` preloads `openvr_api.dll` from: next to the mod DLL → game root → `%LOCALAPPDATA%/openvr/openvrpaths.vrpathreg` runtime path → game `/MelonLoader` folder, so the bare-name DllImports resolve safely |
| Pose input | `IVRCompositor::WaitGetPoses` each frame (from `OnLateUpdate`, right before rendering) → `TrackedDevicePose_t` of device 0 → change-of-basis `M_unity = diag(1,1,-1) · M_openvr · diag(1,1,-1)` (OpenVR is −Z forward, Unity is +Z forward) |
| Eye FOV | `IVRSystem::GetProjectionMatrix(Eye_Left, …)`; vertical FOV = `2·atan(1/m[1][1])` (field `m5`) |
| Frame output | `IVRCompositor::Submit(Eye_Left/Right, Texture_t{handle = RenderTexture.GetNativeTexturePtr(), ETextureType.DirectX, EColorSpace.Auto})` — **the same texture is submitted to both eyes** ("mono duplicate") |
| Tracking origin | `SetTrackingSpace(TrackingUniverseStanding)` |

Mono-duplicate submission is what makes "VR by only tweaking the camera" possible at all:
a full per-eye stereo pipeline would require duplicating cameras and re-structuring
rendering, while duplicate-to-both-eyes gives 6-DOF head tracking on a single render path.
Resolution is the compositor's per-eye recommended size (`GetRecommendedRenderTargetSize`,
e.g. ~2016×2240 on Index), optionally multiplied by the `Supersample` setting.

## 5. Mod design (what YARG-VR actually does)

```
SteamVR compositor
        ▲ Submit(Left+Right, same RT, D3D11 native ptr)
        │
[YARG-VR Composite Camera]  (mod-created, cullingMask = UI only, depth 100,
        │                    targetTexture = OpenVR-size RT, FOV = eye FOV, driven by HMD)
        │ renders (URP, RenderGraph)
        ▼
Screen Space Camera canvases  ← converted from Screen Space Overlay at scene load
   ├─ Venue Output RawImage ──► RT ◄── YARG venue camera (driven 1:1 by HMD, recenterable)
   ├─ Highways Output RawImage ◄─ RT ◄─ YARG highway camera (untouched)
   └─ HUD / pause / score / lyrics           (head-following, locked to gaze)
```

Per frame (`MelonMod.OnLateUpdate`):

1. `WaitGetPoses` → HMD pose (position + yaw/pitch; **roll is stripped** — a rolled
   screen-space canvas would just rotate the whole image on the retina).
2. Composite camera = HMD pose → the converted canvas is head-locked (this *is* the
   "head-following HUD": the HUD is part of the same composition).
3. `CameraManager.CurrentCamera` (reflection) → if it changed (YARG camera cut) → re-bind:
   record the *authored* pose/FOV of that camera once, then compute the anchor
   `anchorRot = authoredFlat · inverse(hmdFlat)` / `anchorPos = authoredPos − anchorRot·hmdPos`
   so the user's current head pose maps onto where YARG put the camera
   (recenter). `F9` does the same manually; auto-recenter-on-cut is a setting.
4. Venue camera transform = `anchor ⊗ HMD` (+ optional height lock / height offset,
   + optional FOV override).
5. URP renders the composite camera (last, depth 100); in
   `RenderPipelineManager.endCameraRendering` the composite RT is submitted to both eyes.

Teardown (`sceneUnloaded` / hotkey / quit) restores every canvas (`renderMode`, `worldCamera`,
`planeDistance`, `localScale`, `scaleFactor`) and every touched venue camera
(position/rotation/FOV) exactly as YARG left them.

### Settings (`MelonLoader/Preferences/YARG-VR.cfg`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | master switch (hotkey F8 flips it live) |
| `KeyToggle` / `KeyRecenter` | `F8` / `F9` | hotkeys (`UnityEngine.InputSystem.Key` names) |
| `HudScale` | `1.0` | scale of the head-locked game view/HUD |
| `HudDistance` | `1.0` m | distance of the head-locked view plane |
| `HudFov` | `0` (auto) | composite camera FOV; 0 = HMD eye FOV |
| `Supersample` | `1.0` | multiplier on the compositor's recommended RT size |
| `VenueFovOverride` | `0` (off) | widen/narrow the stage camera FOV in VR |
| `AutoRecenterOnCut` | `true` | re-anchor on YARG camera cuts |
| `HeightLock` | `false` | keep YARG's authored eye height |
| `HeightOffset` | `0` m | extra vertical offset of the stage camera |

### Deliberate limitations (v1)

- **Mono duplicate, no stereoscopic depth.** This is inherent to the "only tweak the
  camera" scope. True stereo would need a second venue-camera rig and per-eye composites
  (feasible follow-up, sketched below).
- **Desktop window is black during VR gameplay** — the composition is rendered into the
  compositor texture. SteamVR's own desktop mirror view (SteamVR menu → Display Mirror)
  shows exactly what the HMD sees. Menus outside the Gameplay scene (song select etc.) are
  normal desktop; use the desktop for navigation, the headset for gameplay.
- **Venue FPS cap**: the venue texture refreshes at YARG's `Venue FPS Cap` setting
  (Settings → Graphics); set it to 0/unlimited for smooth head tracking in the stage view.
- Venue animator "camera channel" motion (pan/tilt during songs) is overridden while the
  HMD is in control (the anchor stays fixed at recenter time).
- Latency: SteamVR adds ~1-2 frames of compositor latency versus desktop play — noticeable
  to score-focused players, fine for "play in VR" purposes.

### Future: true stereo (design sketch)

Because the composite is just "cameras → RT → submit", stereo can be added camera-only:
create two composite cameras (L/R) with per-eye projections from `GetProjectionMatrix` and
per-eye offsets from `GetEyeToHeadTransform`, each rendering the canvas tree into its own
RT (two `worldCamera`s are not possible per canvas, so the second eye's canvas would be a
lightweight world-space mirror, or the canvas is swapped between eyes per frame), then
submit each RT to its eye. Documented here so the next iteration has a starting point.

## 6. Build & CI

- **Local**: `setup-libs.sh` downloads MelonLoader.x64.zip (`MelonLoader/net472/MelonLoader.dll`)
  and the YARG v0.15.0 Windows release (extracts only `YARG_Data/Managed/*` — the mod
  compiles against the game's *actual* `UnityEngine.CoreModule`, `UnityEngine.UI`,
  `Unity.InputSystem`, …) plus `openvr_api.dll`; `build.sh` runs
  `dotnet build -c Release -p:GameLibs=./libs`. Targets **net472** with
  `Microsoft.NETFramework.ReferenceAssemblies` so no Windows targeting pack is needed
  (builds fine on Linux).
- **CI**: `.github/workflows/build.yml` (GitHub Actions, ubuntu-latest, .NET SDK 8) does the
  same download → build → packages `YARG-VR.dll` + `openvr_api.dll` + `INSTALL.txt` into a
  zip; artifact on every run, attached to GitHub Releases on version tags.
- **This document's companion DLL** (`YARG-VR.dll`, 286 KB) was compiled in exactly this way
  — `dotnet build` succeeded with 0 errors / 0 warnings against the real YARG v0.15.0
  reference assemblies, MelonLoader 0.7.3 (net472) and the official OpenVR binding.

## 7. References

- YARG source: https://github.com/YARC-Official/YARG (v0.15.0)
  - `Assets/Script/Venue/VenueCamera/CameraManager.cs` — venue cameras & camera cuts, `CurrentCamera`
  - `Assets/Script/Gameplay/VenueCameraRenderer.cs` — offscreen venue RT → `Venue Output` RawImage
  - `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs` — ortho highway capture + shader re-projection
  - `Assets/Script/Gameplay/Visuals/CameraPositioner.cs` — highway camera presets/animations
  - `Assets/Script/Gameplay/HUD/**` — uGUI HUD & pause menu
  - `ProjectSettings/ProjectVersion.txt`, `ProjectSettings/ProjectSettings.asset`, `Packages/manifest.json`
- MelonLoader: https://github.com/LavaGang/MelonLoader (docs: https://melonwiki.xyz/)
- OpenVR C API + C# binding: https://github.com/ValveSoftware/openvr
  (`headers/openvr_api.cs`, `bin/win64/openvr_api.dll`)
- SteamVR/OpenVR documentation: https://developer.valvesoftware.com/wiki/OpenVR

## 8. v1.1.0 — true stereo & the upside-down fix (field-tested addendum)

v1.0.2 was the first version the compositor accepted, which exposed two remaining visual defects
reported from a real Index-class HMD:

1. **The whole view was upside down.** Root cause: `CVRCompositor.Submit` receives a D3D11 texture
   whose row 0 is the **top** scanline (D3D/Vulkan convention, and Unity renders its render targets
   that way), but the OpenVR compositor interprets row 0 as the **bottom** of the eye image. The fix
   costs one struct: submit with `VRTextureBounds_t { uMin=0, vMin=1, uMax=1, vMax=0 }`, which
   samples the texture vertically flipped at composite time. Zero GPU cost, and it is exposed as the
   `SubmissionFlip` preference so it can be toggled without a rebuild. (OpenGL render targets are
   bottom-up already, so the flip is only applied for DirectX/DirectX12/Vulkan texture types.)
2. **No stereo depth.** v1.0.2 submitted one texture to both eyes ("mono duplicate"), so the world
   read as a picture glued to the lenses. v1.1.0 replaces this with true stereo (below).

### 8.1 Stereo architecture (why it is possible at all)

YARG's gameplay view is composed on uGUI canvases; the 3-D world is inside RawImages that display
offscreen render textures. Two consequences drive the design:

- A Screen Space Camera canvas renders through exactly ONE assigned camera, so per-eye canvases are
  impossible without cloning the whole UI tree (which would freeze all live data). Instead, v1.1.0
  converts the root canvases to **World Space**: the canvas becomes a real object in the play space
  that *every* camera can render — including two new per-eye cameras.
- Per-eye *content* can be achieved without touching YARG's code, because both of YARG's offscreen
  pipelines have a point where per-eye state can be swapped at draw time:

| Pipeline | YARG's mechanism | v1.1.0 stereo hook |
|---|---|---|
| Stage ("Venue Output") | `VenueCameraRenderer` enables its camera with `targetTexture = venueRT` inside `Update()` (FPS-cap accumulator); its `beginCameraRendering` hook sets venue shader globals + enqueues the alpha-fix RenderGraph pass; its `endCameraRendering` hook disables the camera and resets the globals | A mod-owned **clone camera** (enabled = false, so YARG's hooks and the normal pipeline never touch it) is synchronized each frame with YARG's venue camera (culling, clear, FOV, URP post settings via reflection) and parked at the right-eye pose. Our `beginCameraRendering` handler (subscribed after YARG's, so it runs later — while YARG's venue shader globals are still live) calls `UniversalRenderPipeline.RenderSingleCamera(context, cloneCamera)` into a mod-owned RT of matching format. YARG's own camera then renders the **left eye** through its normal, completely untouched path. While each eye camera draws the canvas, the RawImage texture is swapped (`YARG's RT` for left, ours for right) and restored in `endContextRendering`. (Re-rendering YARG's own camera was rejected: the nested render would re-enter YARG's end hook, which clears the venue shader globals and disables the camera before the left-eye pass executes.) |
| Note highways ("Highways Output") | `HighwayCameraRendering` renders the highways **orthographically** (eye-independent!) and the perspective re-projection happens in the RawImage's material, which reads the GLOBAL matrices `_YargCamViewMatrices` / `_YargCamInvViewMatrices` (set once per frame by YARG's highway camera hook) | Our eye-camera begin-hooks swap those two global matrix arrays with IPD-offset copies: `viewₑ = view · T(−dₑ)`, `invViewₑ = T(dₑ) · invView`, where `dₑ = invView · eyeOffsetₑ` (the eye offset expressed along each highway view's own right axis). Originals are restored in `endContextRendering`. The ortho capture is shared; only the re-projection becomes per-eye. |
| HUD / menus / lyrics | uGUI on the same canvases | Rendered by both eye cameras from their true positions → genuine parallax for free. |

- `RenderSingleCamera` is resolved once via reflection (type
  `UnityEngine.Rendering.Universal.UniversalRenderPipeline`, in
  `Unity.RenderPipelines.Universal.Runtime.dll` — verified present in the game's URP 17.3 assembly).
  Any failure (missing method, RenderGraph incompatibility, exception) flips a one-way
  `_venueStereoBroken` flag: the stage degrades to mono and the rest keeps working. Same pattern for
  the highway matrices (`_highwayStereoBroken`).

### 8.2 The floating screen

The converted canvases form one "screen" anchored at a fixed world position (2 m-equivalent of
`HudDistance` in front of the head, re-placed on enter/recenter) that yaw-billboards to face the
head. Fixed position is deliberate: it is what produces real parallax when you lean (a fully
head-locked screen would be geometrically identical to v1.0.2's Screen Space Camera mode — zero
parallax). Each canvas is offset ~2 cm along the view axis by stacking rank so overlapping root
canvases cannot z-fight; uGUI faces the canvas transform's −Z, so the billboard makes +Z point away
from the head. A ~2 s rescan converts canvases that spawn mid-song.

### 8.3 Per-eye submission

`OpenVrRuntime.SubmitEye(EVREye, RenderTexture)` submits each eye's texture with a per-eye
once-per-frame guard, per-eye success counters, `PostPresentHandoff()` once both eyes are in, and
the vertical bounds flip. Eye geometry comes from `IVRSystem.GetEyeToHeadTransform`, converted with
the same `diag(1,1,-1)` change-of-basis as HMD poses, and logged at init
(`Eye offsets: L/R ... IPD ... mm`). The watchdog from v1.0.1/1.0.2 is unchanged and now submits
both eyes as its last-resort fallback.

## 9. v1.1.1 — screen placement, all-scene VR, Quest/streaming support (field-tested addendum)

Field feedback on v1.1.0 (Index-class HMD, Quest over Virtual Desktop):

1. **"Bent" screen — the screen appeared tilted ("/" vs the desired "\") and forced the neck down.**
   Root cause: `PlaceScreenAnchor` positioned the anchor along the FULL flat head rotation, i.e.
   including **pitch**. The rig engages the moment a song starts — when the user is typically
   looking DOWN at the keyboard/monitor — so the anchor was planted low in front of them; the
   screen then hung below eye level and head pitch was frozen into its position. Fix: the anchor is
   now placed on the horizontal plane at eye height (yaw only), so the screen is always level with
   the eyes regardless of where the user was looking when it was placed. `F9` re-places it.
2. **"Hard to see the entire screen."** The v1.1.0 screen was sized to fill 100% of the ~98° eye
   frustum at `HudDistance = 1.0 m` — edge-to-edge by construction, so the edges were outside the
   viewable area and the close distance exaggerated vertical keystone (contributing to the "bent"
   read). Fix: the screen now fills **~72% of the eye FOV** (`ScreenFillFactor`), `HudDistance`
   defaults to **2.0 m**, and the canvas RectTransform pivot is centered so the billboard position
   is the exact screen center.
3. **"Show the game screen the entire time."** The rig used to engage only in the Gameplay scene.
   It now engages in **every scene** (menus, song select, loading included): eye cameras and the
   clone camera are `DontDestroyOnLoad`, scene unload no longer tears the rig down, and the ~2 s
   canvas rescan (plus an `OnSceneChanged` rescan trigger) converts each scene's Overlay canvases
   into the floating screen. Stale canvas snapshots and destroyed venue-camera snapshots are pruned
   during rescans. In menu scenes there is no venue camera: `Recenter()` falls back to re-placing
   the screen only. World-space canvases get `worldCamera = Camera.main` so UI EventSystem
   raycasting keeps working as best effort; the monitor's menu view becomes an angled spectator
   view of the floating screen (headset / gamepad / SteamVR desktop view recommended for menus).
4. **`'Venue Output' RawImage not found`** appeared in the v1.1.0 log (stage rendered mono all
   session without the user knowing). The lookup now uses `FindObjectsByType<RawImage>` including
   inactive objects and **retries on every rescan** until found; a one-time warning is only logged
   if it stays missing, and success logs `Found 'Venue Output' - stereo stage enabled.`
5. **Quest + Virtual Desktop support.** The mod is already OpenVR-native (VD/Steam Link/ALVR all
   feed SteamVR), so no code changes were needed for compatibility — but two **performance knobs**
   were added for streaming: `StereoVenue` (disables the second stage render, halving stage cost)
   and `StereoHighways` (disables the per-eye highway matrix swap). Both default to ON. README
   gained a Quest/VD tuning section (90 Hz, SSW, Venue FPS cap guidance).
6. **"VR didn't work at all — the headset isn't seen as a VR headset" (v1.1.1 field test 2).** The
   user reconnected the Quest via Meta's **Virtual Display** (the Horizon OS remote-monitor mode),
   conflating it with the *Virtual Desktop* streaming app. Virtual Display is a productivity
   feature: it streams a Windows desktop (with keyboard/mouse passthrough) to the headset as a flat
   monitor and **never registers an HMD with SteamVR** — no driver, no tracking, no compositor, no
   OpenVR session. From OpenVR's point of view the PC has no headset: `VR_IsHmdPresent()` is false
   and `VR_InitInternal` fails (`Init_HmdNotFound` = 108). A SteamVR-only mod physically cannot
   render there; the remedy is a connection method that creates an OpenVR session (Quest Link +
   SteamVR, Steam Link, Virtual Desktop, ALVR). v1.1.1 (turn 2) therefore adds **environment
   diagnostics + a guided wait**: on every failed init the mod probes `VR_IsRuntimeInstalled()`,
   `VR_IsHmdPresent()` and the SteamVR status process (`vrmonitor`), and logs a state-specific
   explanation (SteamVR not installed / installed but not running / running but no headset — with
   the Virtual Display case called out by name); identical messages are throttled and re-logged
   whenever the state changes, and the mod connects by itself once a headset appears (no game
   restart). `openvr_api.dll` is now preloaded **before** the `IsRuntimeInstalled`/`IsHmdPresent`
   probes so the diagnostics can never die on a `DllNotFoundException`, and
   `Init_HmdNotFound` / `Init_HmdNotFoundPresenceFailed` init errors carry an explicit hint that
   monitor-mode connections cannot work.

## 10. v1.1.2 — chart video backgrounds were monitor-only (field-tested addendum)

**Symptom.** Songs with community-provided background videos (`video.webm` / `background.webm` in
the chart folder — a Clone Hero convention YARG supports) showed the video on the desktop monitor,
while the headset saw only a black backdrop behind the note highways and HUD.

**Root cause (verified against YARG's sources and the Gameplay scene YAML).** Chart video
backgrounds never touch YARG's UI canvas:

- `BackgroundManager.cs` (`Assets/Script/Gameplay/`) drives a serialized scene `VideoPlayer`.
  Its `_backgroundImage` RawImage is used **only for IMAGE backgrounds** (texture assigned +
  `SetActive(true)` + `uvRect = (0,0,1,-1)`). The VIDEO path only sets `.url` and calls
  `Prepare()`/`Play()`.
- The scene's VideoPlayer is authored with `m_RenderMode: 0`. In Unity 6's `VideoRenderMode`
  that is **`CameraFarPlane`** (0; Unity renamed the old `CameraPlane`: `CameraFarPlane=0`,
  `CameraNearPlane=1`, `RenderTexture=2`, `MaterialOverride=3`, `APIOnly=4` — reflected from
  `UnityEngine.VideoModule.dll` of the shipped game), targeting the scene object
  **"Camera (No Venue)"** (`m_TargetCamera`).
- "Camera (No Venue)" is a plain base camera (clear = solid black, culling mask = layer 9,
  `m_Depth: -1`, renders to Display 0). The VideoPlayer draws its internal plane into that
  camera's frustum, so the video is composited **directly into the desktop window**, behind the
  Overlay gameplay canvas. It is not a camera stack member and not part of any canvas.
- Consequence: the mod's eye cameras (which render only the converted world-space canvases) never
  see the video → black background in the headset. The monitor kept showing it because the
  desktop composite still runs.
- Notes: `CameraManager.CurrentCamera` is chosen exclusively from cameras under the loaded venue
  prefab, so it stays null for video-background songs — the mod's venue takeover correctly does
  nothing there, and the "Camera (No Venue)" object is untouched by YARG and mod alike.
- Orientation ground truth: YARG's yarground path blits image backgrounds into its video
  RenderTexture with `Graphics.Blit(tex, videoTex, scale=(1,-1), offset=(0,1))` —
  "render image background flipped **to match video**" — proving `VideoPlayer.targetTexture`
  content is upright under standard UVs in this engine build.

**Fix (camera-and-UI-only, in `VrSceneRig`).** A `ScanForVideoBackgrounds()` pass (runs with the
existing canvas rescan cadence) now:

1. finds `VideoPlayer`s in `CameraFarPlane` mode with a target camera (YARG's video-background
   configuration; yarground songs that manage their own `RenderTexture` video are left alone),
2. retargets the player to a mod-owned `RenderTexture` (`RenderTexture` mode) sized like the game
   window, and
3. displays that texture on YARG's own **"Background" RawImage** — the backmost element of the
   gameplay canvas inside "Background Container" (resolved via reflection on
   `BackgroundManager._backgroundImage`, with a name-based fallback) — activated for the duration.

The video becomes part of the canvas composition: the monitor spectator view, both eye cameras,
and therefore the headset all show it behind the highways/HUD, correctly dimmed by YARG's own
"Dimmer" element. Once the video is prepared, the RT aspect is matched to the real video size so
non-16:9 videos are not stretched (`RenderTexture` mode always fills the texture). Hooks are
restored on `Leave()` (original render mode + target camera + slot active state) and released
when the player dies at scene change. If YARG switches the player away from the mod's texture
(yarground interplay), the mod deactivates its slot so it cannot cover the real venue output.

## 11. v1.2.0 — stereo comfort, room-locked screen, HUD pop-out, visualizer (feature release)

1. **"Stereo isn't lined up — slight blur/double vision on the screen."** The eye cameras were
   positioned using OpenVR's raw eye-to-head transforms. Those are pure translations, but they
   carry tiny Y/Z components (a few mm of forward/vertical offset per eye). Horizontal offsets
   produce correct parallax; the small vertical component produced a small VERTICAL misalignment
   between the two screen images — exactly what human vision is most sensitive to (a fraction of a
   degree of vertical disparity reads as ghosting/strain). Fix: the UI screen stereo now uses
   **pure horizontal symmetric offsets** — half the user's measured IPD
   (`(|L.x| + |R.x|)/2` from the OpenVR eye offsets) along the head's right axis, with identical
   rotations. The venue camera / highway reprojection stereo keeps the full OpenVR offsets (real
   world-space depth). A new **`ScreenStereo`** pref (0-1, default 1) scales the screen's stereo
   separation so users can dial it down (`0` = flat screen, zero double vision) without touching
   the stage depth.
2. **"The floating game window slowly moves upward."** Two contributing mechanisms, both fixed:
   - Quest Link (inside-out SLAM) slowly re-estimates its tracking origin; the origin creep shows
     up as slow world drift in SteamVR space. With `AutoRecenterOnCut` on, every YARG camera cut
     re-tethered the STAGE anchor to the head (absorbing the creep), AND re-placed the SCREEN
     anchor to the head's current height — so the screen stepped upward with the creep while the
     user's proprioception stayed put. Fix: auto-recenters no longer touch the screen anchor at
     all (only the stage view re-anchors on cuts).
   - The screen itself now stays **locked in the room**: `UpdateScreenPose` (replacing the old
     yaw-billboard) places all converted canvases at the anchor with a FIXED orientation captured
     at placement (uGUI -Z facing the head). New **`ScreenBillboard`** pref (default OFF) restores
     the old follow-view behavior for anyone who prefers it. `F9` re-places position AND rotation.
   - Bonus fix found while re-reading the billboard math: the canvas stack offset moved HIGHER
     sortingOrder canvases AWAY from the head (the opposite of the intended layering); the sign is
     corrected so topmost UI (pause menu, notifications) sits closest.
3. **HUD pop-out ("pop all the HUD off the floating window").** A second world-space canvas
   ("YARG-VR HUD Plane") is created with the game screen's exact pixel rect and a scale of
   `mainScale × (HudPopDistance / HudDistance)` — same angular size, closer distance. YARG's
   "Main HUD Container" (score, lyrics, practice HUD, song info, BRE box) and "Pause Menu Manager"
   are reparented onto it with `SetParent(false)` (local pixel-space layout preserved exactly;
   only the uniform world scale differs). Looking around now yields real parallax between the HUD
   plane (1.2 m) and the game screen (2 m) — a layered 3-D pop-out. Restore on Leave() re-parents
   to the original siblings; scene changes rebuild the plane (it is scene-local, so it dies with
   its scene and `OnSceneChanged` re-arms the builder). `HudPopOut`/`HudPopDistance` prefs.
4. **Visualizer ring ("blocks that bounce to the song").** `EnsureVisualizer` builds 48 cubes in a
   2.7 m ring on the UI layer (the only layer the eye cameras render), parented under a
   DontDestroyOnLoad holder centered on the player at placement (base ≈ floor level, headY−1.35).
   `AudioListener.GetSpectrumData` (256 samples, Blackman-Harris, log-spaced bins 2-127) drives
   bar heights with fast-attack/slow-decay smoothing and an HSV hue wheel whose brightness tracks
   amplitude; the ring slowly rotates. Bars have no colliders, use the URP-safe
   `Sprites/Default` shader, and `Visualizer = false` restores the plain void. If YARG's audio
   bypasses the Unity AudioListener the bars simply idle.

## 12. v1.2.1 — field report: mirrored billboard, black void, frozen bars, monitor confusion (field-tested addendum)

User report on v1.2.0 (with logs + a desktop screenshot): "everything is still attached to the
player, the bars dont move, the billboard is... flipped?"; clarified: "those pretty blue and red
hues you see in the screenshot isnt on the headset, its just a black void out there, everything
else in the screenshot is what i see in the headset". Four root causes, all confirmed against
YARG's source (main repo + YARG.Core tarballs pulled for this investigation):

1. **Mirrored screen / "still attached to the player" — billboard mode with an inverted facing
   sign.** uGUI world-space canvases are readable from the **+Z side** of their transform (proved
   in production: v1.1.1's `PlaceScreenAnchor` used `LookRotation(-fwd)`, fwd = head→screen, i.e.
   +Z points screen→head, and users read the screen fine). v1.2.0's `UpdateScreenPose` billboard
   branch computed `LookRotation(-dirToHead)` — +Z pointing AWAY from the head — so the player and
   (since YARG's menu camera container sits at the player's position, `MainMenuBackground.Start`
   sets `_cameraContainer.position = (0, 2, 0)`) the monitor both saw the canvas' **back**:
   mirrored text. Yaw-follow also re-oriented the screen to the head every frame = the "attached
   to the player" feeling. Fix: `LookRotation(dirToHead)`; startup now logs the active screen mode
   (`Screen mode: room-locked|BILLBOARD ...`) so misconfiguration is visible in logs. The room-
   locked (default) branch was already correct — every report symptom points at the billboard
   branch being active in the user's config.
2. **Black void — the eye cameras only ever rendered the UI layer.** `MakeEyeCamera` hardcoded
   `cullingMask = 1 << 5`. YARG's *screen content* (highways, venue output, HUD, video background)
   lives on the canvases, so gameplay looked complete — but YARG's **3-D world** (the main menu's
   environment with its blue/red gradient + floating shapes, rendered by the menu's own camera;
   the stage environment around the highways) was never drawn in the headset. Not a v1.2.0
   regression — v1.2.0's visualizer ring merely made the void obvious. Fix: `RefreshEyeCullingMasks()`
   copies the world camera's mask (`Camera (No Venue)` by name → `Camera.main` → union of enabled
   cameras; re-run on scene change / venue bind / the 2 s rescan), ORs in layer 5 (canvases +
   bars), and strips layer 2. `ShowWorld` pref (default on) reverts to the void if ever wanted.
3. **Frozen bars — YARG's audio never touches Unity's audio engine.** YARG plays everything
   through the **native BASS library** (`Assets/Script/Audio/Bass/*`, ManagedBass P/Invoke,
   WASAPI/ASIO exclusive modes in `YARG.Core.Audio.AudioOutputMode`), so
   `AudioListener.GetSpectrumData` returned silence forever. Chain confirmed in source:
   `GlobalAudioHandler._instance` (static, YARG.Core.dll) → `AudioManager._activeMixers`
   (private `List<StemMixer>`) → `BassStemMixer._tempoStreamHandle` (private int, Assembly-CSharp)
   — the *playing* BASS stream, the very handle YARG itself feeds to `Bass.ChannelGetData(...,
   DataFlags.FFT...)` for its whammy-pitch detector (which proves FFT on that handle works while
   playing). New `BassSpectrum.cs`: P/Invokes `BASS_ChannelGetData` (`bass.dll` is already loaded
   in-process — binding by name reuses the same module), `BASS_DATA_FFT2048|FFT_REMOVEDC`
   (0x80000003|0x40) → 1024 interleaved re/im pairs → per-bin magnitudes, max across mixers,
   handle list refreshed ~1 Hz (mixers live per song / menu music). Log-arm:
   `Visualizer tapped YARG's audio mixer (BASS FFT) - bars follow the song.`
   `VisualizerGain` pref scales the response.
4. **Monitor confusion ("trying to get the game window to appear on the monitor" — the F9 spam).**
   With the canvas converted to world space, YARG's own camera renders the floating screen from
   *its* position — mirrored when it stands on the opposite side, off-screen when it looks away;
   users re-spam F9 hunting for it. Fix: a **desktop-mirror camera** (depth 2000 = rendered last,
   targetTexture = null = the game window, clear black, cullingMask = layer 2 only) draws a
   full-screen quad textured with the left-eye RT, sized per-frame to letterbox the eye aspect.
   Layer 2 ("Ignore Raycast") is reserved for the quad: stripped from the eye masks (no feedback
   loop), from the venue clone (`SyncVenueClone`), and invisible to YARG's UI raycasts. The
   monitor then always shows exactly what the headset sees. `DesktopMirror` pref (default on).

## 13. v1.2.2 — field report: mirrored text, reversed-feeling look, bars blocking the screen, off-angle environment

User report on v1.2.1: "looking left and right is reversed (right is left, left is right), the
bars are moving but they block the billboard, billboard is still mirrored, and looking up or down
rotates the window slightly on the right... it still doesnt seem not attached to the player
either, also that background i was showing you now appears but its several cm away from the
menu?" — four root causes (two of them mine, carried through v1.2.0/1.2.1):

1. **uGUI facing convention (the mirrored text).** Ground truth re-established from first
   principles: a Unity camera looking along its +Z sees world +X to its RIGHT (basic projection
   fact); a world-space uGUI canvas at rotation identity contains text along its local +X and is
   therefore READ correctly by a viewer looking along the canvas' **+Z** — i.e. uGUI reads from
   the canvas' **-Z side** and its +Z must point AWAY from the viewer. This matches the canonical
   "billboard a world-space canvas" pattern (`canvas.rotation = camera.rotation`, +Z = camera
   forward) and matches the original v1.1.x implementation notes ("+Z away from the head"). The
   v1.2.0/v1.2.1 code had it inverted in BOTH paths (`LookRotation(-fwd)` in PlaceScreenAnchor
   with a self-contradicting comment; plus v1.2.1's wrong billboard "fix"), pointing the canvas'
   back at the player = mirrored text everywhere. Fix: locked rot = `LookRotation(fwd)` (+Z along
   the gaze); follow-view rot = `LookRotation(-dirToHead)` (v1.2.0's original form).
2. **"Still attached" — the user's config had the old follow-view key enabled.** The pref was
   renamed `ScreenBillboard` → `ScreenFollowsView` (default false) so stale configs fall back to
   the intended room-locked default; the `Screen mode:` startup line prints the active mode.
   The canvas stack offset was also reduced 2 cm → 4 mm per layer (a large depth spread shears
   the stacked canvases apart when pitching = the "bending window" report).
3. **"Left/right reversed"-feeling + background at a weird angle/distance — the world was
   un-anchored.** v1.2.1 rendered YARG's world from the raw SteamVR tracking pose, so the
   environment appeared at an arbitrary position/facing relative to its authored layout (the
   menu's environment is built around its own camera — `MainMenuBackground._cameraContainer`
   sits at (0,2,0) and the camera itself pans with the desktop mouse; MenuScene has NO MainCamera-
   tagged camera). Fix: **world anchoring** — the eye cameras are now driven by
   `anchor ∘ headPose` where the anchor maps the head onto the authored pose of YARG's world
   camera ("Camera (No Venue)" by name → `MainMenuBackground._camera` via reflection →
   Camera.main; re-resolved on scene change and the 2 s scan, re-anchored on F9). The screen,
   HUD plane and visualizer ring are placed inside the anchored frame (`AnchorPoint/
   AnchorRotation/AnchorDirection`), so the environment lines up exactly like the monitor view
   while the screen stays room-locked. Scene changes re-bind and re-place (scene-granular —
   still no per-cut drift).
4. **Bars blocking the screen.** The ring was centered on the PLAYER at 2.7 m radius while the
   screen sits at 2.0 m — its front arc stood between the player and the screen. Re-centered
   behind the screen: `ringCenter = screenAnchor + screenFwd * (radius + 0.6)` → the front arc
   passes ~0.6 m behind the screen plane (backdrop arc).
