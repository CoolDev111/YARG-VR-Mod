# YARG-VR

**Play** [**YARG**](https://yarg.in) **(Yet Another Rhythm Game) in VR — with SteamVR (OpenVR) + MelonLoader,
by touching nothing but the game's cameras.**

YARG-VR is a [MelonLoader](https://melonloader.co/) mod. It does not modify YARG's assets, code, or
render pipeline. It takes over the game's **venue camera** 1:1 with your headset (6-DOF, look around
the stage mid-song), turns the gameplay UI (HUD, highways, pause menu) into a **world-space stereo
screen** floating in your play space, and submits **per-eye stereo textures** to the **SteamVR
compositor**.

> Works against **YARG v0.15.0** (latest release at build time) — and is built to degrade
> gracefully on newer versions. See [`RESEARCH.md`](./RESEARCH.md) for the full behind-the-scenes
> research on how the game renders everything and why this mod is possible at all.

## Requirements

|||
|-|-|
|Game|YARG **v0.15.0** (Windows x64) — https://github.com/YARC-Official/YARG/releases|
|Loader|**MelonLoader 0.6.x / 0.7.x** (x64)|
|VR runtime|**SteamVR** installed (headset: Index, Quest/Pico via Link/Virtual Desktop/Steam Link, Vive, WMR, … anything SteamVR supports)|
|OS|Windows 10/11|

## Install

1. **Install MelonLoader into YARG** (one time):

   * Download `MelonLoader.x64.zip` from https://github.com/LavaGang/MelonLoader/releases/latest
   * Extract it into your YARG install folder (next to `YARG.exe`) so you get `version.dll`,
`MelonLoader/`, `dobby.dll`, etc.
2. **Install the mod**:

   * Copy **`YARG-VR.dll`** and **`openvr\_api.dll`** into `YARG/Mods/`.
3. Launch:

   * Start YARG.
   * Connect an instrument to YARG (or steam will mute the controllers inputs and replace it with your VR controllers).
   * Start SteamVR and it should automatically switch over to the game in your headset.
   * Start a song and enjoy!

## Hotkeys (defaults)

|Key|Action|
|-|-|
|`F8`|Toggle VR mode on/off (restores the desktop window instantly)|
|`F9`|Recenter — **resets the stage view's rotation** to your current facing **and re-places the screen** (position + rotation) in front of you at eye height, level|

Both are rebindable in the config file (any `UnityEngine.InputSystem.Key` name, e.g. `F10`, `Home`).

## Settings — `YARG/UserData/MelonPreferences.cfg`

```toml
[YARG-VR]
Enabled = true            # master switch
SubmissionFlip = true     # vertical flip of the submitted image; required on D3D11 (fixes upside-down)
StereoVenue = true        # render the stage twice (once per eye) - turn OFF on weaker PCs
StereoHighways = true     # per-eye note highway reprojection - turn OFF on weaker PCs
KeyToggle = "F8"
KeyRecenter = "F9"
HudScale = 1.0            # multiplies the screen size (1.0 ≈ 72% of your eye FOV; 1.4 fills it)
HudDistance = 2.0         # meters from your head (applied when the screen is (re)placed - press F9)
HudFov = 0                # screen FOV; 0 = use your HMD's eye FOV
ScreenStereo = 1.0        # screen stereo depth 0-1; lower if content looks doubled/blurry
ScreenFollowsView = false # true = screen yaws to face you; false = locked where placed
ShowWorld = true          # render YARG's 3-D environment (menu bg, stage, highway room) around the screen
DesktopMirror = true      # monitor shows the headset view (letterboxed)
HudPopOut = true          # HUD floats on its own plane closer than the screen
HudPopDistance = 1.2      # HUD plane distance (m); smaller than HudDistance
Visualizer = true         # ring of 48 audio-reactive bars around the play space
VisualizerGain = 1.0      # bar reaction strength (0.1 - 5); raise if bars barely move
Supersample = 1.0         # 0.5 - 2.5 x compositor resolution (per eye)
VenueFovOverride = 0      # wider/narrower stage view in VR; 0 = YARG's own FOV
AutoRecenterOnCut = true  # re-anchor when the song cuts to another stage camera
HeightLock = false        # keep YARG's authored eye height
HeightOffset = 0          # meters of extra height for the stage camera
PoseDebug = false         # logs head/root/screen poses every 5 s (diagnostics only)
MenuEnvSurround = true    # 360° menu background sphere in the menus (v1.3.3)
VisualizerOcclusion = true # bars hide when seen through the menu/HUD screens (v1.3.3)
```

## What you'll see

* **In the headset, everywhere:** VR is no longer gameplay-only — menus, song select and the game
screen are all shown on the floating stereo screen at all times. The screen is **locked in the
room** at eye height, level, `HudDistance` meters in front of where you last recentered (`F9`);
leaning and walking produce real parallax. YARG's **3-D environment** (menu background hues,
stage environment) fills the space around the screen, and the **visualizer ring** bounces to the
music. With `HudPopOut` on, the HUD floats on a second plane closer to you.
* **During a song:** the **stage is true stereo** (two renders, real IPD — look around it freely)
and the **note highways re-project per-eye**. HUD, rock meter, score, lyrics and the pause menu
float on **their own plane closer to you** (parallax pop-out). **Chart video backgrounds**
(`video.webm`) play behind the highways on the screen. The view re-anchors whenever the song's
camera direction cuts.
* **On the monitor:** with `DesktopMirror = true` (default) the monitor shows **exactly the headset
view** (letterboxed) — perfect for spectators. With it off, YARG's normal 2-D view keeps working
(the floating screen may appear mirrored/at an angle in it, since the game camera now looks at a
real object in the room). SteamVR's **Display Mirror** shows the stereo HMD image either way.
* **Performance:** the stage is rendered twice (once per eye) at your desktop resolution and the
game screen once per eye at compositor resolution. If it's heavy, set `StereoVenue = false`,
lower YARG's *Venue Render Scale*, or lower `Supersample` to `0.9`–`1.0`.

## Building from source

```bash
# 1. fetch reference DLLs (MelonLoader + YARG Managed + openvr_api.dll)
./setup-libs.sh            # optional arg: YARG version, default 0.15.0

# 2. build (needs .NET SDK 8; works on Linux/Windows — no Windows targeting pack needed)
./build.sh
# -> bin/Release/YARG-VR.dll
```
CI (`.github/workflows/build.yml`) runs the same pipeline on every push and attaches a
ready-to-install zip to GitHub Releases on tags.

## Repository layout

```
YARG-VR-Mod/
├── YARG.VR.csproj                  # net472 class library → YARG-VR.dll
├── src/
│   ├── AssemblyInfo.cs             # MelonInfo / MelonGame("YARC","YARG")
│   ├── VrMod.cs                    # MelonMod entry: prefs, hotkeys, scene lifecycle
│   ├── VrSceneRig.cs               # stereo eye cameras, world-space screen, venue/highway stereo
│   ├── OpenVrRuntime.cs            # openvr_api preload/init/poses/per-eye submit
│   └── YargBridge.cs               # reflection bridge to YARG's CameraManager.CurrentCamera
├── vendor/OpenVR/openvr_api.cs     # Valve's official C# binding (BSD-3-Clause)
├── .github/workflows/build.yml     # CI: download real refs → build → artifact/release
├── setup-libs.sh / build.sh        # local build helpers
├── LICENSE (MIT) · NOTICE · .gitignore · README.md
```

## License \& credits

* Mod code: **MIT** — see `LICENSE`.
* `vendor/OpenVR/openvr_api.cs` + `openvr_api.dll`: Valve Corporation, **BSD-3-Clause** — see `NOTICE`.
* YARG is LGPL-3.0 © YARC; this mod is an external MelonLoader plugin and ships no game content.
* MelonLoader by LavaGang (Apache-2.0).

*Researched \& built against YARG v0.15.0 / Unity 6000.3.5f2 / MelonLoader 0.7.3 / OpenVR (IVRSystem\_026, IVRCompositor\_029). See* [*`RESEARCH.md`*](./RESEARCH.md) *for every file path and finding.*

