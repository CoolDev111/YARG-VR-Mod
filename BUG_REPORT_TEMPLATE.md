# YARG-VR bug report template

Copy this into a GitHub issue (or a Discord message) and fill it in.
Bugs without the log lines usually cannot be diagnosed.

```text
YARG-VR bug report

- YARG version:            (e.g. 0.15.0)
- Mod version:             (the number in the log line "YARG-VR 1.3.x initialized")
- Headset + connection:    (Index / Quest 2-3 via Link / Virtual Desktop / Pico ...)
- GPU + driver version:
- SteamVR version:
- What happened:
- What you expected:
- Happens every time?:
- F8/F9 behavior:          (does toggling VR / recenter change anything?)

Log: paste ALL lines starting with [YARG-VR] from
     <YARG install folder>/MelonLoader/Latest.log
     (or attach the whole Latest.log file)
```

## Healthy startup lines to compare against

A working install logs approximately:

```text
[YARG-VR] YARG-VR 1.3.3 initialized (true stereo).
[YARG-VR] Screen mode: room-locked
[YARG-VR] world visible: True
[YARG-VR] desktop mirror: True
[YARG-VR] HMD IPD: 63 mm
[YARG-VR] Eye RT: 1724x1844  vFOV 98.0 deg
[YARG-VR] Compositor frame <increasing counter>
```

Missing or different lines = broken install or a changed path; include them in the report either way.

## Environment notes for triage

* Windows + D3D11 only; the mod talks to SteamVR/OpenVR (Oculus native runtime will not work).
* MelonLoader 0.7.3 installed into the YARG folder; both YARG-VR.dll and openvr_api.dll in Mods/.
* Mod config: \<YARG folder>/UserData/MelonPreferences.cfg (delete to regenerate defaults).

