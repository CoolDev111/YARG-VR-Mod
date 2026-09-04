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
- **v1.3.2 — MENU SURROUND**: the menu background lookup
  (`YARG.Menu.Main.MainMenuBackground`, fields verified from YARG 0.15
  Assembly-CSharp metadata: `Transform _cameraContainer`, `Camera _camera`)
  silently returned null at runtime in earlier versions — the world anchor fell
  back to 'Main Camera' and the menu room rendered from the wrong spot (read as
  a backdrop behind the menu UI). Now: inactive-inclusive component search,
  container-camera fallback, and one-shot diagnostic logging of what resolved.
  When the bound world camera IS the menu background camera and
  `MenuEnvSurround` is on (default), `_worldCamAuthoredPos` is overridden with
  the `_cameraContainer` position (the environment's authored player spot) —
  the player stands in the middle of the menu room (360° surround). Rotation
  still uses the camera's flattened yaw via the unchanged `ReanchorWorld` math,
  so F9 re-faces the environment. Gameplay is unaffected: "Camera (No Venue)"
  wins candidate priority during songs (venue takeover untouched).

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
| `MenuEnvSurround` | true | v1.3.2: menu background centered on the player (360°) instead of authored backdrop view |

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
