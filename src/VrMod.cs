using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace YargVr
{
    /// <summary>MelonPreferences-backed settings for YARG-VR (MelonLoader/Preferences/YARG-VR.cfg).</summary>
    public class VrSettings
    {
        public MelonLoader.MelonPreferences_Entry<bool> Enabled;
        public MelonLoader.MelonPreferences_Entry<bool> SubmissionFlip;
        public MelonLoader.MelonPreferences_Entry<bool> StereoVenue;
        public MelonLoader.MelonPreferences_Entry<bool> StereoHighways;
        public MelonLoader.MelonPreferences_Entry<string> KeyToggle;
        public MelonLoader.MelonPreferences_Entry<string> KeyRecenter;
        public MelonLoader.MelonPreferences_Entry<string> KeyProbe;
        public MelonLoader.MelonPreferences_Entry<string> KeyReconnect;
        public MelonLoader.MelonPreferences_Entry<string> AutoBindProfileName;
        public MelonLoader.MelonPreferences_Entry<float> HudScale;
        public MelonLoader.MelonPreferences_Entry<float> HudDistance;
        public MelonLoader.MelonPreferences_Entry<float> HudFov;
        public MelonLoader.MelonPreferences_Entry<float> ScreenStereo;
        public MelonLoader.MelonPreferences_Entry<bool> ScreenFollowsView;
        public MelonLoader.MelonPreferences_Entry<bool> ShowWorld;
        public MelonLoader.MelonPreferences_Entry<bool> DesktopMirror;
        public MelonLoader.MelonPreferences_Entry<bool> HudPopOut;
        public MelonLoader.MelonPreferences_Entry<float> HudPopDistance;
        public MelonLoader.MelonPreferences_Entry<bool> Visualizer;
        public MelonLoader.MelonPreferences_Entry<float> VisualizerGain;
        public MelonLoader.MelonPreferences_Entry<float> Supersample;
        public MelonLoader.MelonPreferences_Entry<float> VenueFovOverride;
        public MelonLoader.MelonPreferences_Entry<bool> AutoRecenterOnCut;
        public MelonLoader.MelonPreferences_Entry<bool> HeightLock;
        public MelonLoader.MelonPreferences_Entry<float> HeightOffset;
        public MelonLoader.MelonPreferences_Entry<bool> PoseDebug;
        public MelonLoader.MelonPreferences_Entry<bool> MenuEnvSurround;
        public MelonLoader.MelonPreferences_Entry<bool> VisualizerOcclusion;
        public MelonLoader.MelonPreferences_Entry<bool> VisualizerInMenu;
        public MelonLoader.MelonPreferences_Entry<bool> OpenVrProjection;
        public MelonLoader.MelonPreferences_Entry<string> KeyScreenCloser;
        public MelonLoader.MelonPreferences_Entry<string> KeyScreenFarther;
    }

    /// <summary>
    /// YARG-VR: SteamVR (OpenVR) + MelonLoader camera takeover for YARG.
    ///
    /// Hotkeys (defaults):
    ///   F8 - toggle VR mode on/off
    ///   F9 - recenter (re-anchor the stage view + re-place the game screen in front of you)
    ///   F7 - input device probe (log-only dump: XInput slots, InputSystem devices, YARG
    ///        profile matching, passive 5 s input-activity window)
    ///   F6 - reconnect instruments (re-runs YARG's own profile-claim for every visible
    ///        instrument; non-destructive). v1.3.13: instruments are ALSO auto-claimed
    ///        the moment they appear (device watcher) - F6 is the manual fallback.
    ///        v1.3.14: with AutoBindProfileName set, a device that matches NO saved
    ///        profile is re-bound to that profile via YARG's own AddDevice + saved,
    ///        defeating the Riff Master's boot-order hash drift.
    ///   [ / ] - nudge the two eye images of the UI toward each other / apart at runtime
    ///        (ScreenStereo; 0 = flat, 1.0 = true depth, up to 50 = wider-than-eye
    ///        separation for fusing layers closer than the screen; saves to the config)
    ///
    /// VR is active in EVERY scene (menus included): the world-space game screen shows the
    /// game at all times; the stage camera takeover engages when a venue camera exists.
    /// </summary>
    public class VrMod : MelonLoader.MelonMod
    {
        private VrSettings _settings;
        private VrSceneRig _rig;

        private UnityEngine.InputSystem.Key _keyToggle = UnityEngine.InputSystem.Key.F8;
        private UnityEngine.InputSystem.Key _keyRecenter = UnityEngine.InputSystem.Key.F9;
        private UnityEngine.InputSystem.Key _keyProbe = UnityEngine.InputSystem.Key.F7;
        private UnityEngine.InputSystem.Key _keyReconnect = UnityEngine.InputSystem.Key.F6;
        private UnityEngine.InputSystem.Key _keyScreenCloser = UnityEngine.InputSystem.Key.LeftBracket;
        private UnityEngine.InputSystem.Key _keyScreenFarther = UnityEngine.InputSystem.Key.RightBracket;
        private string _parsedToggle, _parsedRecenter, _parsedProbe, _parsedReconnect, _parsedScreenCloser, _parsedScreenFarther;

        private float _nextInitAttempt;

        // v1.3.9: the startup device probe no longer runs at OnInitializeMelon time (that
        // was the boot-crash trigger - see DeviceProbe class header). It runs once, from
        // OnUpdate, on the first frame where DeviceProbe.YargStaticStateIsSettled() is true.
        private bool _startupDumpPending;

        private string _lastThrottleKey;
        private int _lastThrottleFrame = -1;

        /// <summary>Logs a message, but at most once per ~30 s for an identical message (no spam).</summary>
        private void LogThrottled(string message, Action<string> log)
        {
            if (string.Equals(_lastThrottleKey, message, StringComparison.Ordinal) &&
                Time.frameCount - _lastThrottleFrame < 1800)
            {
                return;
            }

            _lastThrottleKey = message;
            _lastThrottleFrame = Time.frameCount;
            log(message);
        }

        private void LogErrorThrottled(string message)
        {
            LogThrottled(message, LoggerInstance.Error);
        }

        public override void OnInitializeMelon()
        {
            BuildPreferences();
            MelonLoader.MelonPreferences.Save();

            HookSceneEvents(true);

            LoggerInstance.Msg("YARG-VR 1.3.16 initialized (true stereo).");
            LoggerInstance.Msg("  Hotkeys: " + _settings.KeyToggle.Value + " = toggle VR, " +
                _settings.KeyRecenter.Value + " = recenter / re-place screen, " +
                _settings.KeyProbe.Value + " = input device probe (log-only), " +
                _settings.KeyReconnect.Value + " = reconnect instruments, " +
                _settings.KeyScreenCloser.Value + "/" + _settings.KeyScreenFarther.Value +
                " = move the two UI images toward each other / apart (fixes doubling).");

            // v1.3.9: NEVER probe YARG at melon-init time. MelonLoader can run this method
            // before YARG's boot initialized its data paths; executing YARG statics there
            // poisons their static constructors for the whole session (boot crash). The
            // dump now happens from OnUpdate once the game is provably settled.
            _startupDumpPending = true;
            DeviceProbe.AutoBindProfileName = _settings.AutoBindProfileName.Value ?? "";
            LoggerInstance.Msg("  Startup device probe deferred until the game finishes loading.");
            LoggerInstance.Msg("  Instrument watcher active: a guitar/drums appearing is logged and auto-claimed (" +
                _settings.KeyReconnect.Value + " = manual retry)." +
                (string.IsNullOrWhiteSpace(_settings.AutoBindProfileName.Value)
                    ? " AutoBindProfileName is NOT set - set it in the cfg to survive guitar hash changes."
                    : " Auto-binding unmatched instruments to profile '" + _settings.AutoBindProfileName.Value + "'."));
            // v1.3.15: sustained absence is no longer silent - the watchdog posts status
            // lines and can self-heal a stuck dongle via the targeted Windows re-scan.
            // v1.3.16: while absent it also reads the dongle's HID interface directly
            // (5 s windows, [hid] lines) - the decisive "is the guitar sending data" test.
            LoggerInstance.Msg("  Guitar absence watchdog: while no instrument is present it posts a status line " +
                "every ~30 s, reads the dongle's HID interface directly in 5 s windows ([hid] lines - press guitar " +
                "buttons when prompted), and runs a one-time targeted Windows re-scan (dongle-replug equivalent, " +
                "needs YARG started as administrator) after 60 s of absence; " +
                _settings.KeyReconnect.Value + " runs both on demand when no candidate devices exist.");
            LoggerInstance.Msg("  Requires SteamVR. VR is active in every scene (menus included).");
            LoggerInstance.Msg("  Screen mode: " + (_settings.ScreenFollowsView.Value
                ? "FOLLOWS VIEW (set ScreenFollowsView = false for a room-locked screen)"
                : "room-locked (F9 re-places it in front of you)") +
                ", world visible: " + (_settings.ShowWorld.Value ? "yes" : "no") +
                ", desktop mirror: " + (_settings.DesktopMirror.Value ? "on" : "off") + ".");
        }

        public override void OnDeinitializeMelon()
        {
            HookSceneEvents(false);
            LeaveScene();
            OpenVrRuntime.Shutdown();
        }

        private void BuildPreferences()
        {
            var cat = MelonLoader.MelonPreferences.CreateCategory("YARG-VR", "YARG-VR (SteamVR)");

            _settings = new VrSettings();
            _settings.Enabled = cat.CreateEntry<bool>("Enabled", true, "VR enabled",
                "Master switch. VR engages in the Gameplay scene when SteamVR is running.");
            _settings.SubmissionFlip = cat.CreateEntry<bool>("SubmissionFlip", true, "Flip submitted image",
                "Vertically flips the image sent to SteamVR. Required on D3D11/D3D12/Vulkan (fixes the " +
                "upside-down view). Only turn this off if the VR image appears upside down.");
            OpenVrRuntime.FlipTextureBounds = _settings.SubmissionFlip.Value;
            _settings.KeyToggle = cat.CreateEntry<string>("KeyToggle", "F8", "Toggle VR key",
                "Key used to enable/disable VR mode at runtime (UnityEngine.InputSystem.Key name).");
            _settings.KeyRecenter = cat.CreateEntry<string>("KeyRecenter", "F9", "Recenter key",
                "Re-anchors the stage view and re-places the game screen in front of you (at eye height, level).");
            _settings.KeyProbe = cat.CreateEntry<string>("KeyProbe", "F7", "Input probe key",
                "Log-only diagnostic: dumps XInput slots, Unity InputSystem devices, YARG profile " +
                "matching, and captures 5 s of raw input events (press while an instrument controller " +
                "is dead to see which layer swallowed it).");
            _settings.KeyReconnect = cat.CreateEntry<string>("KeyReconnect", "F6", "Reconnect instruments key",
                "Re-runs YARG's own profile-claim (TryConnectProfile) for every visible instrument " +
                "device - the same non-destructive call YARG makes on first button press. Use when a " +
                "connected instrument stays dead; the log reports exactly why each device connects or not.");
            _settings.AutoBindProfileName = cat.CreateEntry<string>("AutoBindProfileName", "", "Auto-bind instruments to profile",
                "The Riff Master's device hash can differ between boots (it is a SHA1 of the device " +
                "description, which varies with enumeration order), so YARG's saved profile stops " +
                "matching and the guitar dies. When set: any instrument that matches NO saved profile " +
                "is bound to the profile with this exact name (YARG's own AddDevice path), " +
                "bindings.json is saved, and the device is connected - automatically, every boot. " +
                "Example: Mason. Leave empty to disable.");
            _settings.HudScale = cat.CreateEntry<float>("HudScale", 1.0f, "HUD scale",
                "Scales the head-locked game view / HUD. 1.0 matches the desktop layout.");
            _settings.HudDistance = cat.CreateEntry<float>("HudDistance", 2.0f, "Screen distance (m)",
                "Distance of the game screen from your head (re-applied on recenter / scene change).");
            _settings.HudFov = cat.CreateEntry<float>("HudFov", 0f, "Screen FOV (0 = auto)",
                "FOV used to size the game screen. 0 = use the HMD's eye FOV reported by OpenVR.");
            _settings.ScreenStereo = cat.CreateEntry<float>("ScreenStereo", 8.4f, "Screen stereo depth",
                "How far apart the two screen renders are (0 = flat screen, zero double vision; " +
                "1 = full IPD, physically true depth; above 1 = wider-than-eye separation - " +
                "fuses screen content at a closer distance). Default 8.4 = the tuned fusion " +
                "point where the UI merges cleanly on the reference rig; press [ / ] live to " +
                "find your own (each press saves). Lower it if the screen content looks " +
                "slightly doubled or blurry.");
            // NOTE: renamed from "ScreenBillboard" in v1.2.2 so stale configs that had it
            // enabled fall back to the intended default (room-locked).
            _settings.ScreenFollowsView = cat.CreateEntry<bool>("ScreenFollowsView", false, "Screen follows view",
                "ON: the screen yaws to always face you (v1.1 behavior). OFF (default): the screen " +
                "stays locked where it was last placed (F9) - like a real screen in the room, and " +
                "immune to slow playspace tracking drift.");
            _settings.ShowWorld = cat.CreateEntry<bool>("ShowWorld", true, "Show game world",
                "Renders YARG's 3-D environment (menu background, stage environment, highway room) " +
                "around the floating screen in the headset. OFF = the plain black void of v1.2.0.");
            _settings.DesktopMirror = cat.CreateEntry<bool>("DesktopMirror", true, "Desktop mirror",
                "Shows the headset view on the monitor (letterboxed), so the game window always " +
                "mirrors what you see in VR. OFF = the monitor keeps showing YARG's own camera.");
            _settings.HudPopOut = cat.CreateEntry<bool>("HudPopOut", true, "Pop out HUD",
                "Moves the HUD (score, lyrics, practice HUD, song info, pause menu) onto its own " +
                "floating plane closer to you for a parallax 3-D effect in front of the game screen.");
            _settings.HudPopDistance = cat.CreateEntry<float>("HudPopDistance", 1.2f, "HUD plane distance (m)",
                "Distance of the popped-out HUD plane from your head. Must be smaller than the " +
                "screen distance. Applied on (re)place - press F9 after changing.");
            _settings.Visualizer = cat.CreateEntry<bool>("Visualizer", true, "Audio visualizer ring",
                "A ring of audio-reactive bars around your play space, bouncing to the song. " +
                "Turn OFF for a plain black void.");
            _settings.VisualizerGain = cat.CreateEntry<float>("VisualizerGain", 1.0f, "Visualizer gain",
                "Multiplier on how strongly the visualizer bars react to the music (0.1 - 5). " +
                "Raise it if the bars barely move, lower it if they are pinned at full height.");
            _settings.Supersample = cat.CreateEntry<float>("Supersample", 1.0f, "Supersampling",
                "Multiplier on the compositor's recommended render target size (0.5 - 2.5).");
            _settings.VenueFovOverride = cat.CreateEntry<float>("VenueFovOverride", 0f, "Stage FOV override (0 = off)",
                "Overrides the venue camera FOV while in VR. 0 keeps YARG's own FOV.");
            _settings.AutoRecenterOnCut = cat.CreateEntry<bool>("AutoRecenterOnCut", true, "Auto recenter on camera cuts",
                "Re-anchor the HMD whenever YARG cuts to a different stage camera.");
            _settings.HeightLock = cat.CreateEntry<bool>("HeightLock", false, "Lock eye height",
                "Keep the stage camera at YARG's authored height (ignore real-world height changes).");
            _settings.HeightOffset = cat.CreateEntry<float>("HeightOffset", 0f, "Height offset (m)",
                "Extra vertical offset added to the stage camera position while in VR.");
            _settings.StereoVenue = cat.CreateEntry<bool>("StereoVenue", true, "Stereo stage",
                "Render the stage twice (once per eye) for real 3-D depth. Turn OFF on weaker PCs " +
                "(e.g. Quest over Virtual Desktop) to halve the stage rendering cost.");
            _settings.StereoHighways = cat.CreateEntry<bool>("StereoHighways", true, "Stereo note highways",
                "Re-project the note highways per eye for depth. Turn OFF on weaker PCs to save a little GPU.");
            _settings.PoseDebug = cat.CreateEntry<bool>("PoseDebug", false, "Pose debug log",
                "Logs head / room-root / screen world poses every 5 s. Turn ON only when diagnosing " +
                "whether placed objects really stay put when you turn your head.");
            _settings.MenuEnvSurround = cat.CreateEntry<bool>("MenuEnvSurround", true, "Menu background surrounds you",
                "ON (default): in the menus a 360-degree background sphere built from YARG's own animated menu " +
                "gradient material surrounds you in every direction (v1.3.3). OFF: no surround sphere. " +
                "Never shown while a song is playing - the stage owns the world there. Applied on scene change.");
            _settings.VisualizerOcclusion = cat.CreateEntry<bool>("VisualizerOcclusion", true, "Visualizer hides behind screens",
                "ON (default): bars seen through a screen are hidden (v1.3.3), and in the menus " +
                "the ring is arranged so it cannot block the menu (see VisualizerInMenu). " +
                "OFF: bars always draw.");
            _settings.VisualizerInMenu = cat.CreateEntry<bool>("VisualizerInMenu", true, "Visualizer ring in the menus",
                "ON (default): in the main menus the ring stays VISIBLE but low-profile - short " +
                "bars pushed out to 3.2 m so they can never reach the menu panels (v1.3.12). " +
                "OFF: the ring is hidden entirely while the menus are up (v1.3.6 behavior). " +
                "Needs VisualizerOcclusion ON.");
            _settings.OpenVrProjection = cat.CreateEntry<bool>("OpenVrProjection", true, "OpenVR per-eye projections",
                "ON (default): the eye cameras render with OpenVR's actual per-eye frusta (asymmetric, matching " +
                "the lenses exactly). OFF = the legacy symmetric eye FOV. Turn OFF only if the VR view suddenly " +
                "looks distorted or shifted after updating to 1.3.6.");
            _settings.KeyScreenCloser = cat.CreateEntry<string>("KeyScreenCloser", "LeftBracket", "UI images closer key",
                "Moves the two eye images of the UI toward each other (reduces ScreenStereo by 0.1). " +
                "UnityEngine.InputSystem.Key name. Default [.");
            _settings.KeyScreenFarther = cat.CreateEntry<string>("KeyScreenFarther", "RightBracket", "UI images apart key",
                "Moves the two eye images of the UI apart (raises ScreenStereo by 0.1, max 50). " +
                "1.0 = true depth; above 1.0 = wider-than-eye separation. " +
                "UnityEngine.InputSystem.Key name. Default ].");

            ParseHotkeys(force: true);
        }

        public override void OnUpdate()
        {
            try
            {
                ParseHotkeys(force: false);
                HandleHotkeys();
                TryInitOpenVr();
                DeviceProbe.InstallDeviceWatcher(LoggerInstance); // v1.3.13: no-op once installed
                DeviceProbe.Tick(LoggerInstance);

                // v1.3.9: the startup probe runs here - OnUpdate only executes once real
                // frames are ticking, which by Unity's boot order is strictly after
                // RuntimeInitializeOnLoadMethod(BeforeSplashScreen) and the first scene's
                // Awakes (YARG's SingletonAwake included). YargStaticStateIsSettled is the
                // explicit second layer.
                if (_startupDumpPending && DeviceProbe.YargStaticStateIsSettled())
                {
                    _startupDumpPending = false;
                    DeviceProbe.Dump(LoggerInstance, "startup (deferred until the game finished loading)");
                    // v1.3.13: devices that enumerated before the watcher was installed
                    // produce no Added event - sweep the table once and auto-claim if any
                    // instrument is already present.
                    DeviceProbe.AutoReconnectIfInstrumentsPresent(LoggerInstance);
                }
            }
            catch (Exception e)
            {
                LoggerInstance.Error("OnUpdate error: " + e);
            }
        }

        public override void OnLateUpdate()
        {
            if (_settings == null || !_settings.Enabled.Value || !OpenVrRuntime.IsInitialized)
            {
                return;
            }

            // Only drive the rig while the Gameplay scene is active.
            if (_rig == null || !_rig.IsActive)
            {
                return;
            }

            try
            {
                bool gotPose = false;
                Vector3 pos = Vector3.zero;
                Quaternion rot = Quaternion.identity;

                try
                {
                    gotPose = OpenVrRuntime.TryGetHmdPose(out pos, out rot);
                }
                catch (Exception e)
                {
                    LogErrorThrottled("[YARG-VR] Pose fetch error (throttled): " + e);
                }

                if (gotPose)
                {
                    try
                    {
                        _rig.LateTick(pos, rot);
                    }
                    catch (Exception e)
                    {
                        LogErrorThrottled("[YARG-VR] Rig tick error (throttled): " + e);
                    }
                }

                // MUST run even when pose fetching failed - this is the guaranteed path that
                // keeps frames flowing to the SteamVR compositor.
                try
                {
                    _rig.EnsureSubmitting();
                }
                catch (Exception e)
                {
                    LogErrorThrottled("[YARG-VR] Submission watchdog error (throttled): " + e);
                }
            }
            catch (Exception e)
            {
                LogErrorThrottled("[YARG-VR] LateUpdate error (throttled): " + e);
            }
        }

        #region Hotkeys

        private void ParseHotkeys(bool force)
        {
            if (_settings == null)
            {
                return;
            }

            if (force || !string.Equals(_parsedToggle, _settings.KeyToggle.Value, StringComparison.OrdinalIgnoreCase))
            {
                _parsedToggle = _settings.KeyToggle.Value;
                if (!Enum.TryParse(_parsedToggle, true, out _keyToggle))
                {
                    _keyToggle = UnityEngine.InputSystem.Key.F8;
                }
            }

            if (force || !string.Equals(_parsedRecenter, _settings.KeyRecenter.Value, StringComparison.OrdinalIgnoreCase))
            {
                _parsedRecenter = _settings.KeyRecenter.Value;
                if (!Enum.TryParse(_parsedRecenter, true, out _keyRecenter))
                {
                    _keyRecenter = UnityEngine.InputSystem.Key.F9;
                }
            }

            if (force || !string.Equals(_parsedProbe, _settings.KeyProbe.Value, StringComparison.OrdinalIgnoreCase))
            {
                _parsedProbe = _settings.KeyProbe.Value;
                if (!Enum.TryParse(_parsedProbe, true, out _keyProbe))
                {
                    _keyProbe = UnityEngine.InputSystem.Key.F7;
                }
            }

            if (force || !string.Equals(_parsedReconnect, _settings.KeyReconnect.Value, StringComparison.OrdinalIgnoreCase))
            {
                _parsedReconnect = _settings.KeyReconnect.Value;
                if (!Enum.TryParse(_parsedReconnect, true, out _keyReconnect))
                {
                    _keyReconnect = UnityEngine.InputSystem.Key.F6;
                }
            }

            if (force || !string.Equals(_parsedScreenCloser, _settings.KeyScreenCloser.Value, StringComparison.OrdinalIgnoreCase))
            {
                _parsedScreenCloser = _settings.KeyScreenCloser.Value;
                if (!Enum.TryParse(_parsedScreenCloser, true, out _keyScreenCloser))
                {
                    _keyScreenCloser = UnityEngine.InputSystem.Key.LeftBracket;
                }
            }

            if (force || !string.Equals(_parsedScreenFarther, _settings.KeyScreenFarther.Value, StringComparison.OrdinalIgnoreCase))
            {
                _parsedScreenFarther = _settings.KeyScreenFarther.Value;
                if (!Enum.TryParse(_parsedScreenFarther, true, out _keyScreenFarther))
                {
                    _keyScreenFarther = UnityEngine.InputSystem.Key.RightBracket;
                }
            }
        }

        private void HandleHotkeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || _settings == null)
            {
                return;
            }

            if (keyboard[_keyToggle] != null && keyboard[_keyToggle].wasPressedThisFrame)
            {
                _settings.Enabled.Value = !_settings.Enabled.Value;
                MelonLoader.MelonPreferences.Save();
                LoggerInstance.Msg("VR mode " + (_settings.Enabled.Value ? "ENABLED" : "DISABLED") + " (hotkey).");

                if (_settings.Enabled.Value)
                {
                    _nextInitAttempt = 0f; // retry init immediately
                }
                else
                {
                    LeaveScene();
                    OpenVrRuntime.Shutdown();
                }
            }

            if (_settings.Enabled.Value && keyboard[_keyRecenter] != null && keyboard[_keyRecenter].wasPressedThisFrame)
            {
                if (_rig != null && _rig.IsActive)
                {
                    _rig.Recenter();
                    LoggerInstance.Msg("Recentered VR view.");
                }
            }

            if (keyboard[_keyProbe] != null && keyboard[_keyProbe].wasPressedThisFrame)
            {
                DeviceProbe.Dump(LoggerInstance, "manual " + _parsedProbe);
            }

            if (keyboard[_keyReconnect] != null && keyboard[_keyReconnect].wasPressedThisFrame)
            {
                // v1.3.15: ReconnectInstruments returns the candidate count (-1 = pass did
                // not run). Zero candidates means the device table holds no instrument at
                // all - the claim pass cannot conjure one, so run the targeted Windows
                // device re-scan (dongle-replug equivalent) on demand.
                int scanned = DeviceProbe.ReconnectInstruments(LoggerInstance, "manual F6");
                if (scanned == 0)
                {
                    DeviceProbe.RunDeviceNudge(LoggerInstance, "manual F6 found no candidate devices");
                }
            }

            // [ / ]: live stereo-convergence tuning for the UI. The eye-camera separation is
            // recomputed every frame from ScreenStereo, so this takes effect immediately.
            if (keyboard[_keyScreenCloser] != null && keyboard[_keyScreenCloser].wasPressedThisFrame)
            {
                float v = Mathf.Clamp(_settings.ScreenStereo.Value - 0.1f, 0f, 50f);
                if (v != _settings.ScreenStereo.Value)
                {
                    _settings.ScreenStereo.Value = v;
                    MelonLoader.MelonPreferences.Save();
                    LoggerInstance.Msg("Screen stereo depth = " + v.ToString("F1") +
                        " (the two UI images moved toward each other; 0 = flat screen).");
                }
            }

            if (keyboard[_keyScreenFarther] != null && keyboard[_keyScreenFarther].wasPressedThisFrame)
            {
                float v = Mathf.Clamp(_settings.ScreenStereo.Value + 0.1f, 0f, 50f);
                if (v != _settings.ScreenStereo.Value)
                {
                    _settings.ScreenStereo.Value = v;
                    MelonLoader.MelonPreferences.Save();
                    LoggerInstance.Msg("Screen stereo depth = " + v.ToString("F1") +
                        " (the two UI images moved apart; 1.0 = true depth, above 1.0 = wider-than-eye).");
                }
            }
        }

        #endregion

        #region OpenVR lifecycle

        private void TryInitOpenVr()
        {
            if (_settings == null || !_settings.Enabled.Value || OpenVrRuntime.IsInitialized)
            {
                return;
            }

            if (Time.unscaledTime < _nextInitAttempt)
            {
                return;
            }

            _nextInitAttempt = Time.unscaledTime + 3f;

            if (OpenVrRuntime.TryInitialize())
            {
                OpenVrRuntime.FlipTextureBounds = _settings.SubmissionFlip.Value;
                LoggerInstance.Msg("SteamVR compositor ready - VR view engages in every scene.");
                DeviceProbe.Dump(LoggerInstance, "steamvr-connected");

                // Engage immediately, whatever scene we are in (menus included).
                EnterScene();
            }
            else if (!string.IsNullOrEmpty(OpenVrRuntime.InitError))
            {
                // InitError carries a state-specific explanation (SteamVR not installed / not
                // running / headset not visible - including the Meta Virtual Display case).
                LogThrottled("[YARG-VR] " + OpenVrRuntime.InitError + " (retrying every 3 s while VR is enabled)",
                    LoggerInstance.Warning);
            }
        }

        #endregion

        #region Scene management

        private void HookSceneEvents(bool hook)
        {
            // NOTE: the rig intentionally persists across scene changes (the screen stays up in
            // menus too); no scene-unloaded teardown anymore. Stale canvases/cameras are pruned
            // by the rig's periodic rescan.
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (_settings == null || !_settings.Enabled.Value || !OpenVrRuntime.IsInitialized)
            {
                return;
            }

            // Engage in EVERY scene (menus, song select, gameplay, ...). Give the scene one
            // frame to finish Awake/OnEnable before we take over.
            MelonLoader.MelonCoroutines.Start(EnterNextFrame());
        }

        private System.Collections.IEnumerator EnterNextFrame()
        {
            yield return null;
            EnterScene();
        }

        private void EnterScene()
        {
            if (_rig == null)
            {
                _rig = new VrSceneRig(_settings);
            }

            if (_rig.IsActive)
            {
                // Already active (rig persists across scenes) - just rescan for new canvases.
                _rig.OnSceneChanged();
                return;
            }

            _rig.Enter();
        }

        private void LeaveScene()
        {
            if (_rig != null)
            {
                _rig.Leave();
            }
        }

        #endregion
    }
}
