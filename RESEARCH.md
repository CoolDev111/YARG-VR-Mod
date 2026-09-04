# YARG-VR — Research Notes

Reverse-engineering and technical findings accumulated while building the YARG-VR
MelonLoader mod. Reference game version: **YARG v0.15.0** (Windows x64), Unity
6000.3.5f2, URP 17.3 (RenderGraph), D3D11, Mono. Mod loader: MelonLoader 0.7.3
(net472, C# 7.3). VR runtime: SteamVR / OpenVR.

Last updated: v1.2.2.

---

## 1. OpenVR submission

- Per-eye texture submission to the SteamVR compositor.
- D3D11 textures submit **upside-down** unless `TextureBounds` is flipped:
  `vMin = 1`, `vMax = 0`.
- Eye offsets: ±0.031 m (tuned to the tester's 63 mm IPD).
- Valve's official `openvr_api.cs` binding (BSD-3-Clause) is vendored in
  `vendor/OpenVR/` and compiled directly into the mod DLL; the native
  `openvr_api.dll` ships alongside and must be copied into `<YARG>/Mods/` with
  the mod.

## 2. uGUI world-space canvases

- World-space uGUI canvases are **readable from their +Z side only**. Any code
  that orients the screen must end with the canvas +Z pointing at the head.
- v1.1.x used `Quaternion.LookRotation(-fwd)` (sign error → mirrored text).
- v1.2.1's billboard path used `Quaternion.LookRotation(dirToHead, Vector3.up)`.
- v1.2.2 fixed the final sign error in the room-locked placement; the
  room-locked screen is the default again (`ScreenFollowsView = false`).

## 3. YARG audio (BASS, not Unity)

- YARG plays 100% of audio through **native BASS** (WASAPI/ASIO). Unity's
  `AudioListener` is always silent — do not try to read spectrum from Unity.
- Spectrum data must come from P/Invoke `BASS_ChannelGetData` with flags
  `0x80000003 | 0x40` (FFT2048 | REMOVEDC → 1024 magnitude bins,
  ≈ 21.5 Hz per bin at 44.1 kHz).
- Reflection chain to the stream handle:
  `GlobalAudioHandler._instance` (static) → `AudioManager._activeMixers`
  (`List<StemMixer>`) → `BassStemMixer._tempoStreamHandle` (int).
- Implementation: `src/BassSpectrum.cs` — `TryGetMagnitudes`,
  `RefreshHandles` every 60 frames, `DllImport("bass")` reusing the in-process
  module (no separate bass.dll load).
- This is also the same call YARG itself uses for its whammy-pitch detector,
  which is why the bars were frozen before v1.2.1 (they read Unity's mixer).

## 4. Layers

- **Layer 2 ("Ignore Raycast")** = desktop mirror quad ONLY.
  - Excluded from eye-camera culling masks (prevents feedback loop).
  - Excluded from the venue clone: `_venueClone.cullingMask = venue.cullingMask & ~(1<<2)`.
  - Ignored by UI raycasts.
- **Layer 5 (UI)** = world-space canvases + visualizer bars.

## 5. Black void (fixed v1.2.1)

- Root cause: eye cameras had hardcoded `cullingMask = 1<<5`.
- Fix: `RefreshEyeCullingMasks()` — union of masks from "Camera (No Venue)" →
  `Camera.main` → all enabled cameras, then `mask |= 1<<5; mask &= ~(1<<2)`.
- Called on Enter / ScanForCanvases / BindVenue / scene changes.
- The `ShowWorld` preference gates the world layers.

## 6. Desktop mirror

- The game window shows the headset view via a dedicated mirror camera:
  `EnsureMirrorCamera()` — depth 2000 (renders last), clear black,
  `cullingMask = 1<<2`, Unlit/Texture material, `DontDestroyOnLoad`,
  `targetTexture = null` (renders straight to the game window).
- `UpdateMirrorQuad()` letterboxes the LEFT eye render texture each frame.
- `DestroyMirror()` is called at the start of `Leave()`.
- Rationale: uGUI seen by the normal game camera from behind looks mirrored or
  misplaced, so the game window cannot simply render the world.

## 7. Visualizer ring

- 48 bars, radius 2.7 m, max height 1.7 m.
- Log-spaced FFT bands (bin 1 → 512 ≈ 21 Hz → 11 kHz).
- Fast attack / slow decay envelope per bar.
- BASS data preferred, AudioListener fallback
  (`_visBassOkLogged` / `_visBassMissingLogged` each arm once).
- v1.2.1 placed the ring around the PLAYER (front arc overlapped the screen).
- v1.2.2 pushed the whole ring 5+ m out behind the screen (backdrop) — read as
  "the bars are not around me".
- **v1.3.0 fix**: the ring is a CHILD of the room root — it surrounds the player
  again (centered where they stood at recenter, radius 2.7 m so the front arc is
  behind the 2 m screen plane), yaw-aligned with the root, slowly spinning in
  local space.

## 8. Chart video backgrounds

- Chart authors add per-song background videos (e.g. `video.webm`).
- The fix (found in-headset) was redirecting the far-plane / clear camera
  output to a render texture, not painting with a `CameraFarPlane` approach.

## 9. YARG menu background

- `MainMenuBackground` sets `_cameraContainer.position = (0, 2, 0)` — the menu
  camera sits AT the player position. Relevant to any menu alignment bug.
- **v1.2.2**: the environment is anchored to the menu's own camera pose
  (`World anchored to YARG's camera '...'` log line), so distances and facing
  match the authored view; the several-cm menu offset is gone.
- **v1.3.3 GROUND TRUTH (from YARG's repo sources, master branch)**:
  - `Assets/Prefabs/Menu/MenuBackground.prefab` (placed directly in
    `MenuScene.unity`) = `MenuBackground` root with children `[Directional
    Light, Global Volume, Camera Container, Wall]`.
  - `Camera Container` → `Main Camera` (FOV 60, ClearFlags = **Skybox**,
    depth 0, renders to display 0, culling mask bits 0,1,2,4,5,6,7,9).
  - `Wall` = a Unity built-in **quad** (2 m x 1 m), lying horizontal at y=0,
    z = 5 (5 m in front of the camera) — the ONLY geometry. Material =
    `Assets/Art/Materials/Menu/MenuBackground.mat`, shader
    `Unlit/MenuBackground` (hand-written CG, renders fine under URP as
    SRPDefaultUnlit): an animated gradient computed in **UV space** around
    UV center with three orbiting color points (teal `_Color_SideA`, red
    `_Color_SideB`, dark blue `_Color_Background`) + screen-space dither
    noise. Because it is UV-based it maps correctly onto ANY mesh.
  - `MainMenuBackground.Update()` **lerps `_cameraContainer` back to
    (0, 0.5, 0) EVERY FRAME** (and offsets the camera local position with the
    mouse) — any one-shot reposition by a mod is undone within a second.
  - Consequence for VR: the eye cameras clear SOLID BLACK (no skybox), so the
    headset showed only the menu screen + ring + a tiny quad — a void. The
    menu "background" the user asked to surround them is the gradient look,
    which the v1.3.3 sphere reproduces with YARG's own material.

## 10. World anchoring + the room root (v1.3.0)

- v1.2.1 symptom: looking left/right felt REVERSED and the world seemed
  attached to the player (yaw sign in pose math:
  `_anchorPos = authored.Position - _anchorRot * hmdPos`).
- **v1.2.2**: the 3-D environment is anchored to YARG's own camera pose
  (`World anchored to YARG's camera '...'` log line). Pitch/roll zeroed on
  billboard/look vectors (`fwd.y = 0f`, `toHead.y = 0f`).
- **v1.2.2 in-headset result (first v1.2.2 test)**: screen/HUD/ring still felt
  attached to the head, and the ring was not around the player.
- **v1.3.0 — the room root ("the invisible cube", the tester's design)**:
  one persistent DontDestroyOnLoad GameObject `YARG-VR RoomRoot`. The screen
  stack, pop-out HUD and visualizer ring are positioned in ROOT-LOCAL space
  (`_screenAnchorLocal`, `_hudAnchorLocal`); `UpdateScreenPose` derives their
  world poses via `_roomRoot.TransformPoint(...)` every frame. The root moves
  ONLY in `PlaceScreenAnchor` — i.e. on the first valid pose and on F9 (plus
  scene changes, which reset placement). Head motion cannot move anything the
  mod places, by construction. `PlaceScreenAnchor` logs
  `Room anchor set at (x, y, z) ...` every time the root moves.
- The world-anchor system (`_worldAnchorPos/Rot`, `_anchorPos/Rot`) that maps
  the head onto YARG's authored cameras is UNCHANGED — it still governs the
  3-D environment (menu background, venue stage) and the eye-camera mapping.
- New `PoseDebug` pref (default false): logs head/root/screenWorld poses every
  5 s to settle "still attached" reports with hard numbers.
- **v1.3.1 — REVERSED LOOK-AROUND ROOT CAUSE**: `OpenVrToUnity`
  (OpenVrRuntime.cs) built the rotation block TRANSPOSED. For pure rotations
  transpose = inverse, so every HMD pose reached the rig inverted on all axes
  (reversed look-around; the venue anchor product counter-rotated with the head
  = the "attached to the player" feel; identity at neutral pose = why recenters
  looked right at first). Fixed by un-transposing to `M_u = B·M_o·B`,
  B = diag(1,1,−1); verified numerically against a 90° left-turn pose.
- **v1.3.2 — MENU SURROUND (superseded by v1.3.3)**: the menu background lookup
  (`YARG.Menu.Main.MainMenuBackground`, fields verified from YARG 0.15
  Assembly-CSharp metadata: `Transform _cameraContainer`, `Camera _camera`)
  silently returned null at runtime in earlier versions — the world anchor fell
  back to 'Main Camera' and the menu room rendered from the wrong spot (read as
  a backdrop behind the menu UI). Now: inactive-inclusive component search,
  container-camera fallback, and one-shot diagnostic logging of what resolved.
  The container-override idea itself was a no-op (see section 9) and was
  removed again in v1.3.3; the lookup hardening stays.
- **v1.3.3 — VISUALIZER OCCLUSION**: uGUI never writes depth, so the ring's
  bars (r = 2.7 m, BEHIND the ~2 m screen) painted straight over the menus
  ("bars poke through the menus"). Fix: `UpdateVisualizerOcclusion(headPosA)`
  runs after `UpdateVisualizer` each tick; every bar is segment-tested against
  every active world-space screen (converted canvases + the pop-out HUD plane):
  if the head→bar segment crosses a screen's rect (canvas-local units, 0.25 m
  margin), the bar's renderer is disabled that frame. Off-axis-safe (plane
  crossing point, not bar-center-in-rect). Bars in front of/beside/behind the
  player still render. `VisualizerOcclusion` pref (default true).
- **v1.3.3 — MENU SURROUND SPHERE**: replaces v1.3.2's container anchor. A
  child of the room root: inward-facing sphere mesh (elevation −80°..+90°,
  48x20 grid, seam-duplicated UV column, triangles wound for Unity's clockwise-
  from-inside front faces), radius 6 m (outside the ring's 2.7 m and the authored
  Wall's 5 m, inside the 50 m far clip), layer 5, material = YARG's OWN
  `Unlit/MenuBackground` material (resolved from the MenuBackground prefab's
  Wall renderer; `Shader.Find` fallback creates an owned material).
  Visible exactly while `_worldCam == _menuBgCamera` (menus; never during a
  song — venue owns the world), gated by `MenuEnvSurround` (default true);
  SetActive toggled per tick; F9 carries it with the room root. Log lines:
  `Menu background camera bound ...` (bind) and
  `Menu background sphere created (r=6.0 m ...)` (build), plus a one-shot
  warning if the material cannot be resolved.

## 11. Preferences (MelonLoader config)

| Key | Default | Notes |
|---|---|---|
| `ScreenStereo` | — | stereo vs mono screen render |
| `ScreenFollowsView` | **false** | renamed from `ScreenBillboard` in v1.2.2 so stale configs that had it `true` are ignored |
| `HudPopOut` | — | HUD pop-out |
| `HudPopDistance` | 1.2 | |
| `Visualizer` | true | |
| `HudDistance` | 2.0 | |
| `ShowWorld` | true | gates world layers |
| `DesktopMirror` | true | game window shows headset view |
| `VisualizerGain` | 1.0 | |
| `PoseDebug` | false | v1.3.0: 5 s pose logging for diagnostics |
| `MenuEnvSurround` | true | v1.3.3: 360° menu background sphere in the menus (YARG's own menu gradient material) |
| `VisualizerOcclusion` | true | v1.3.3: hide visualizer bars seen through the menu/HUD screens |

Startup log prints `YARG VR <version>` plus `Screen mode:`, `world visible:`,
`desktop mirror:` lines. Diagnose from these, not guesses.

## 12. Build notes

- net472 + `LangVersion 7.3`; target 0 warnings / 0 errors.
- Reference assemblies: MelonLoader.dll, 0Harmony.dll, openvr_api.dll plus
  Unity DLLs from `YARG_Data/Managed` (UnityEngine, CoreModule, UIModule, UI,
  Unity.InputSystem, VideoModule, AudioModule, PhysicsModule).
  `setup-libs.sh` fetches all of them automatically.
- In Unity 6, `UnityEngine.SceneManagement` lives inside CoreModule — there is
  no separate SceneManagementModule.
- CI: `.github/workflows/build.yml` builds on every push/PR and attaches the
  artifact to a GitHub Release on `v*` tags. One release per version; never
  rebuild the same version.

## 13. Version history (root causes)

| Ver | Fixes |
|---|---|
| 1.0.2 | upside-down view (TextureBounds flip); per-eye stereo |
| 1.1.0 | game screen visible the whole time; zoom-out distance |
| 1.1.x | "bent screen" (`/ <` vs `\ <`) geometry; Virtual Display confusion (mod requires SteamVR/OpenVR) |
| 1.2.0 | HUD pop-out, stereo alignment, upward drift, Recenter rotation, room-locked screen attempt, visualizer environment |
| 1.2.1 | billboard mirror (LookRotation sign), black void (RefreshEyeCullingMasks), frozen bars (BASS FFT), desktop mirror camera |
| 1.2.2 | world anchored to YARG's camera pose (left/right reversal, attached-world feel), room-locked screen default + final canvas sign fix (mirrored text), visualizer ring moved behind the screen, pitch/roll zeroing (screen tilt when looking up/down), menu background alignment |
| 1.3.0 | room root ("invisible cube"): screen/HUD/ring parented to one anchor that only F9/scene changes move (structural room-lock); visualizer ring surrounds the player again (front arc behind the screen plane); PoseDebug pref |
| 1.3.1 | **reversed look-around root cause** — `OpenVrToUnity` (OpenVrRuntime.cs) built the rotation block TRANSPOSED (`m01=e.m4` instead of `e.m1`, …); for pure rotations transpose = inverse, so every HMD pose was handed to the cameras rotated the opposite way on ALL axes (left=right, up=down; world/venue counter-swing felt "attached to the player"). Invisible at the neutral (identity) pose, which is why recenters looked right at first. Un-transposed to `M_u = B·M_o·B`, B = diag(1,1,−1); verified numerically against a 90° left-turn pose and the SteamVR plugin's reference conversion. Also explains why v1.2.1's "yaw sign" and v1.2.2's pitch/roll-zeroing patches never fixed the reversal |
| 1.3.2 | menu backgrounds surround the player — menu-bg camera lookup hardened (inactive-inclusive, container fallback, one-shot diagnostics) after it silently failed and fell back to 'Main Camera'; when bound to it, the world anchor position is overridden with the `MainMenuBackground._cameraContainer` position (the environment's authored player spot) so the menu room wraps around the player 360°; `MenuEnvSurround` pref (default on); F9 re-faces; gameplay unchanged |
| 1.3.3 | **v1.3.2 surround had no visible effect (confirmed in-headset)** — ground truth from YARG's repo: the menu background is only a camera (skybox clear) + one 2x1 m glowing quad + a container that `MainMenuBackground.Update()` lerps back to (0, 0.5, 0) EVERY FRAME (our one-shot re-anchor was instantly undone, and the eye cameras' solid-black clear showed none of the skybox). Fixes: (1) **menu surround sphere** — inward-facing r=6 m sphere under the room root using YARG's own UV-based `Unlit/MenuBackground` animated gradient material, shown only while the menu bg camera is the bound world camera (never during songs), `MenuEnvSurround` gates it; v1.3.2's container-anchor override removed (anchor back to the camera's authored pose); (2) **visualizer occlusion** — uGUI writes no depth so ring bars painted over the menus; every bar is now per-frame segment-tested against every world-space screen rect (converted canvases + pop-out HUD plane) and its renderer disabled while seen through one; `VisualizerOcclusion` pref (default on). Tester also confirmed the v1.3.1 pose fix works (menus no longer follow the player, seated feel correct) |
