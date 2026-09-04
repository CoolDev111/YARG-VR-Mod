using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace YargVr
{
    /// <summary>
    /// v1.3.6 input diagnostics + instrument reconnect. Log-only except for the explicit
    /// F6 reconnect action, which calls YARG's own PUBLIC claim API (PlayerContainer.
    /// TryConnectProfile) - the same non-destructive call YARG makes when a controller's
    /// first button press arrives.
    ///
    /// v1.3.6 CRITICAL CHANGE: the v1.3.5 raw input-event capture (InputSystem.onEvent
    /// hook) is GONE. The user's v1.3.5 session crashed natively right after the guitar
    /// produced its first events, and the onEvent hook was the only new code touching the
    /// input pipeline at event frequency. Device activity is now measured PASSIVELY by
    /// comparing InputDevice.lastUpdateTime across a 5 s window - a plain field read that
    /// cannot interfere with the game's input processing. Same diagnostic answer, zero
    /// pipeline footprint.
    ///
    /// Background (from YARG's input sources, verified against the 0.15.0 assemblies):
    /// a controller only starts working once YARG's InputManager.OnEvent sees a
    /// ButtonControl change from it and calls PlayerContainer.TryConnectProfile(device),
    /// which matches the device against saved profiles by layout + SHA1(description JSON)
    /// (XInput user index excluded). If the dongle re-enumerates the device with a
    /// slightly different description depending on boot order, the hash changes, no
    /// profile matches, and the instrument dies silently - device present, inputs dead.
    ///
    /// v1.3.7 CRITICAL FIX (root cause of the v1.3.5/v1.3.6 boot crash): the startup
    /// dump ran the YARG introspection at OnInitializeMelon - BEFORE the game's first
    /// scene Awake. Reading PlayerContainer.ProfilesDirectory there forced YARG's
    /// PlayerContainer static constructor to run before YARG had initialized its own
    /// paths, so Path.Combine(null, ...) threw and Mono permanently poisoned the type
    /// (type-init failures are cached for the whole session). YARG's own
    /// GlobalVariables.SingletonAwake then hit the cached TypeInitializationException
    /// and the whole boot cascade collapsed (Sacn/Discord/LoadingScreen errors in
    /// Player.log, game never reaches the menu). The introspection now DEFERS until
    /// Unity's first scene has fully loaded; update-loop callers (F6/F7, SteamVR
    /// connect) are always past that point, so nothing is lost in practice.
    ///
    /// v1.3.9 CRITICAL FIX (the v1.3.7 gate was not enough - crash survived it): the
    /// v1.3.7 gate used SceneManager.GetActiveScene().isLoaded alone, and on Unity 6000
    /// + MelonLoader 0.7.3 that check returned TRUE while OnInitializeMelon still ran
    /// before YARG's boot had initialized its data paths (RuntimeInitializeOnLoadMethod
    /// callbacks had not run yet - PathHelper.PersistentDataPath was still null). The
    /// startup introspection therefore still executed PlayerContainer's static ctor
    /// early: Path.Combine(null, "profiles") threw, Mono cached the failure, and YARG's
    /// own SingletonAwake hit the cached TypeInitializationException. The user's
    /// mod-free boot test proved the mod was the sole trigger. v1.3.9 makes early
    /// YARG access physically impossible: (1) the gate now requires Time.frameCount >
    /// 0 - at least one real player-loop frame has started, which Unity guarantees can
    /// only happen AFTER RuntimeInitializeOnLoadMethod(BeforeSplashScreen) and every
    /// first-scene Awake (including YARG's SingletonAwake) have completed - plus a real
    /// scene (buildIndex >= 0, rejecting the pre-scene placeholder) and isLoaded;
    /// (2) OnInitializeMelon no longer calls Dump at all - the startup probe is
    /// deferred to the first settled frame in OnUpdate. Defense in depth: even if a
    /// future gate check misfires again, no YARG type is reachable from melon-init
    /// time anymore.
    ///
    /// v1.3.13 DEVICE WATCHER + AUTO RECONNECT (the "0/0 devices claimed" round): the
    /// user's failing session showed reconnect "0/0" AND all four OS XInput slots empty
    /// AND no instrument-class InputSystem device - the guitar was never enumerated by
    /// Windows during that window, so there was nothing to claim (a claim pass can only
    /// re-bind devices that exist in the device table; it cannot conjure one). Two gaps
    /// remained: (a) no event-level record of whether the guitar EVER enumerates and
    /// when, (b) the claim only ran when the user pressed F6 at the right moment. Fix:
    /// InputSystem.onDeviceChange is now watched (metadata logging only - the v1.3.5
    /// lesson stands: never touch the per-event input pipeline) and every instrument-
    /// class Added/Reconnected schedules one automatic ReconnectInstruments pass ~1.5 s
    /// later (retried once per second while the game is still loading). The startup dump
    /// additionally sweeps the device table once - devices that enumerated before the
    /// watcher installed produce no Added event. F6 remains the manual fallback; every
    /// reconnect log line now names its trigger.
    ///
    /// v1.3.14 AUTO-BIND (the "just creates a new profile" round): the watcher worked -
    /// the log finally showed the Riff Master enumerating at boot (iface=GameInput, NOT
    /// XInput - that is why the XInput slots were always empty for this guitar) and the
    /// auto pass firing. The real blocker became visible: YARG matches profiles by
    /// SHA1(BindingSerialization.Serialize(device)) - "yargHash" - and the user's saved
    /// profile did NOT match the live device ("NO matching profile - YARG can never
    /// auto-connect it"). The hash is derived from the device description, which varies
    /// with enumeration order/timing on GameInput devices - so a profile saved in one
    /// boot can never match the device in another ("NO matching profile" every boot;
    /// answering YARG's new-device dialog just spawns another profile that goes stale
    /// next boot - exactly "that just creates a new profile, it doesnt solve issue").
    /// Fix: AutoBindProfileName pref - when an instrument matches NO saved profile, the
    /// mod binds it to the named profile using YARG's OWN ProfileBindings.AddDevice
    /// (the same call YARG's profile UI makes), persists via BindingsContainer.
    /// SaveBindings(BindingsPath), then TryConnectProfile. Verified against the 0.15.0
    /// assemblies: ProfileBindings.AddDevice(InputDevice): bool,
    /// BindingsContainer.SaveBindings(string): int, BindingsContainer.BindingsPath.
    /// F7 additionally dumps every profile's stored SerializedInputDevice (Layout+Hash)
    /// so hash drift is directly visible next to the live yargHash line.
    ///
    /// v1.3.15 ABSENCE WATCHDOG + WINDOWS DEVICE RE-SCAN (the "it just ignores the
    /// guitar" round): the follow-up boot showed a blocker UPSTREAM of profiles - for the
    /// whole session Unity InputSystem held only Keyboard/Mouse/Touchscreen (zero [watch]
    /// lines, F6 = "0/0 devices claimed"), i.e. the guitar never reached the device table
    /// at all, so no profile logic could even run. Previously that state was silent. Now:
    /// while no instrument is present, a watchdog logs one compact status line (InputSystem
    /// device count, XInput slots, GameInput runtime presence) on a slow cadence, so the
    /// next log proves WHEN (or whether) the guitar ever appears; after 60 s of continuous
    /// absence it runs a ONE-TIME targeted Windows re-scan (WindowsDeviceNudge: cfgmgr32
    /// CM_Reenumerate_DevNode on the PDP VID_0E6F dongle node - the programmatic
    /// equivalent of Device Manager's "Scan for hardware changes" / a dongle replug),
    /// which can resurrect a dongle whose HID/GameInput children failed to start. F6 with
    /// zero candidate devices runs the same re-scan on demand. GameInput runtime presence
    /// is probed via LoadLibrary("gameinput.dll") + the GameInputCreate export - the
    /// historical "GameInput 0x8007007F" (ERROR_PROC_NOT_FOUND) in the user's logs means
    /// a broken/old redistributable, which Unity's GameInput backend needs for this
    /// guitar. Probe hygiene: the "PRESS BUTTONS" sampling banner only prints when an
    /// instrument is actually in the table.
    ///
    /// v1.3.16 HID LIVENESS (the "dongle healthy but guitar invisible" round): the first
    /// watchdog run answered half the question - the dongle IS present and healthy at the
    /// PnP level (HID\VID_0E6F&PID_0248&IG_00 + USB\VID_0E6F&PID_0248&IG_00, started,
    /// problem code 0), the GameInput runtime loads, and the PnP re-scan was denied
    /// (rc=51 - that call needs elevation; logged as such now). The remaining unknown is
    /// a single binary question: is the guitar itself sending ANY data to Windows? Only
    /// the dongle's HID reports can answer it, so HidLinkProbe (read-only, shared,
    /// VID_0E6F interfaces only, never while an instrument is present) runs 5 s windows
    /// on a slow cadence during absence and on F6/F7: reports > 0 = the guitar IS linked
    /// and the drop is in GameInput/Unity (bridge becomes possible); zero reports = the
    /// guitar is not linked (power/pair - user-side); unopenable = another component
    /// holds the interfaces exclusively. The probe dump also lists every present HID
    /// interface with its product string (a linked guitar often adds collections).
    ///
    /// F7 dump answers: (1) does the OS XInput layer see the guitar and is its packet
    /// counter advancing, (2) does Unity's InputSystem device table contain it and what
    /// is its YARG profile hash, (3) does any saved profile MATCH it, (4) is the device
    /// actually producing input (passive lastUpdateTime window), (5) what has YARG
    /// currently claimed. F6 retries the claim on demand and reports exactly why it fails.
    /// </summary>
    internal static class DeviceProbe
    {
        // ---- passive activity window state (v1.3.6 - replaces the onEvent capture) ----
        private static bool _activityPending;
        private static float _activityEnd;
        private static MelonLoader.MelonLogger.Instance _activityLog;
        private static readonly Dictionary<int, double> _activityFirstUpdate = new Dictionary<int, double>();

        // ---- XInput second sample state ----
        private static bool _xinputPending;
        private static float _xinputSecondAt;
        private static MelonLoader.MelonLogger.Instance _xinputLog;
        private static readonly uint[] _xinputFirstPackets = new uint[4];
        private static readonly bool[] _xinputFirstConnected = new bool[4];

        // ---- v1.3.13 device watcher + auto reconnect ----
        private static bool _watcherInstalled;
        private static MelonLoader.MelonLogger.Instance _watchLog;
        private static bool _autoReconnectPending;
        private static float _autoReconnectAt;
        private static string _autoReconnectTrigger;
        private static MelonLoader.MelonLogger.Instance _autoLog;

        // ---- v1.3.15 absence watchdog + Windows device re-scan ----
        private static bool _absenceActive;
        private static float _absenceSince;
        private static float _absenceNextLog;
        private static int _absenceLogs;
        private static bool _absenceNudgeRan;      // auto re-scan: at most one per session
        private static MelonLoader.MelonLogger.Instance _absenceLog;
        private static bool _gameInputProbed;
        private static string _gameInputState = "<not probed>";
        private static float _hidNextProbeAt;      // v1.3.16 HID liveness windows

        /// <summary>v1.3.14: when non-empty, an instrument that matches NO saved profile is
        /// bound to the profile with this name (YARG's own ProfileBindings.AddDevice) and
        /// connected. Set by VrMod from the AutoBindProfileName preference at melon init.</summary>
        public static string AutoBindProfileName = "";

        // ---- cached reflection handles (resolved lazily, verified against 0.15.0) ----
        private static bool _yargTried;
        private static bool _yargOk;
        private static MethodInfo _miGetProfileForDevice;   // PlayerContainer.GetProfileForDevice(InputDevice) : object
        private static MethodInfo _miIsDeviceTaken;         // PlayerContainer.IsDeviceTaken(InputDevice) : bool
        private static MethodInfo _miTryConnectProfile;     // PlayerContainer.TryConnectProfile(InputDevice) : bool
        private static MethodInfo _miIsProfileTaken;        // PlayerContainer.IsProfileTaken(profile) : bool
        private static PropertyInfo _piProfiles;            // PlayerContainer.Profiles : IEnumerable
        private static PropertyInfo _piPlayers;             // PlayerContainer.Players : IEnumerable
        private static PropertyInfo _piProfilesDirectory;   // PlayerContainer.ProfilesDirectory : string
        private static FieldInfo _fiRegisteredDevices;      // InputManager._registeredDevices : HashSet<InputDevice>
        private static MethodInfo _miGetBindingsForProfile; // BindingsContainer.GetBindingsForProfile(profile) : ProfileBindings
        private static MethodInfo _miMatchesDevice;         // ProfileBindings.MatchesDevice(InputDevice) : bool
        private static MethodInfo _miGetHash;               // BindingSerialization.GetHash(InputDevice) : string
        private static MethodInfo _miAddDevice;             // ProfileBindings.AddDevice(InputDevice) : bool
        private static PropertyInfo _piBindingsPath;        // BindingsContainer.BindingsPath : string (static)
        private static MethodInfo _miSaveBindings;          // BindingsContainer.SaveBindings(string) : int
        private static FieldInfo _fiUnresolvedDevices;      // ProfileBindings._unresolvedDevices : List<SerializedInputDevice>
        private static FieldInfo[] _profileFields;          // YargProfile: Name, Id, IsBot, GameMode, AutoConnectOrder, LastUsed

        public static void Dump(MelonLoader.MelonLogger.Instance log, string reason)
        {
            try
            {
                var sb = new StringBuilder(4096);

                ProbeXInput(sb);
                ProbeUnityDevices(sb);
                ProbeYargClaims(sb);

                log.Msg("[YARG-VR][probe] ---------- device probe (" + reason + ") ----------");
                FlushLines(log, sb);

                // v1.3.15 hygiene: the sampling banners only make sense when an instrument
                // is actually in the table - previously they printed (and the 5 s activity
                // window ran) even on guitar-less sessions, producing pure noise.
                if (AnyInstrumentPresent())
                {
                    StartActivityWindow(log);

                    _xinputPending = true;
                    _xinputSecondAt = Time.unscaledTime + 0.3f;
                    _xinputLog = log;
                    log.Msg("[YARG-VR][probe] packet-delta sample in 0.3 s; input activity window runs 5 s - PRESS BUTTONS ON THE CONTROLLER NOW.");
                }
                else
                {
                    log.Msg("[YARG-VR][probe] no instrument in the device table - nothing to sample; the absence watchdog is monitoring for it.");
                    // v1.3.16: with nothing in Unity's table, the dongle's own HID interface
                    // is the only place the guitar can still show life - read it for 5 s
                    // and list every present HID interface (a linked guitar often adds
                    // collections that Unity never sees).
                    HidLinkProbe.StartWindow(log, "probe with no instrument - press buttons on the guitar");
                    HidLinkProbe.DumpAllInterfaces(log);
                }

                log.Msg("[YARG-VR][probe] ---------- end probe ----------");
            }
            catch (Exception e)
            {
                log.Msg("[YARG-VR][probe] probe failed: " + e);
            }
        }

        /// <summary>Call every frame from VrMod.OnUpdate. Cheap when idle.</summary>
        public static void Tick(MelonLoader.MelonLogger.Instance log)
        {
            try
            {
                if (_xinputPending && Time.unscaledTime >= _xinputSecondAt)
                {
                    _xinputPending = false;
                    SecondXInputSample();
                }

                if (_activityPending && Time.unscaledTime >= _activityEnd)
                {
                    _activityPending = false;
                    FinishActivityWindow();
                }

                // v1.3.13: fire a scheduled auto-reconnect pass once due.
                if (_autoReconnectPending && Time.unscaledTime >= _autoReconnectAt)
                {
                    if (YargStaticStateIsSettled())
                    {
                        _autoReconnectPending = false;
                        var lg = _autoLog != null ? _autoLog : log;
                        _autoLog = null;
                        ReconnectInstruments(lg, _autoReconnectTrigger);
                        _autoReconnectTrigger = null;
                    }
                    else
                    {
                        // Device appeared while the game was still loading - retry every
                        // second until YARG's statics are settled (silent, no log spam).
                        _autoReconnectAt = Time.unscaledTime + 1f;
                    }
                }

                // v1.3.15: log sustained instrument absence and self-heal via the
                // targeted Windows re-scan (once per session, after 60 s).
                WatchdogTick(log);

                // v1.3.16: drive the HID liveness window phases (no-op when idle).
                HidLinkProbe.Tick();
            }
            catch (Exception e)
            {
                log.Msg("[YARG-VR][probe] tick error: " + e);
            }
        }

        // ---- v1.3.13 device watcher + auto reconnect ----

        /// <summary>
        /// Call from OnUpdate (no-op once installed). Subscribes InputSystem.onDeviceChange:
        /// logs every non-text device add/remove/disconnect/reconnect (the definitive
        /// "did the guitar EVER enumerate, and when" timeline) and schedules one automatic
        /// reconnect pass when an instrument appears. Metadata logging only - the handler
        /// never touches the per-event input pipeline (the v1.3.5 lesson).
        /// </summary>
        public static void InstallDeviceWatcher(MelonLoader.MelonLogger.Instance log)
        {
            if (_watcherInstalled)
            {
                return;
            }

            try
            {
                _watchLog = log;
                InputSystem.onDeviceChange += OnDeviceChange;
                _watcherInstalled = true;
                log.Msg("[YARG-VR][watch] device watcher installed - an instrument appearing will be logged and auto-claimed (F6 stays as the manual fallback).");
            }
            catch
            {
                // InputSystem not initialized yet - retried next frame from OnUpdate.
            }
        }

        private static void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            try
            {
                if (device == null || _watchLog == null ||
                    device is Keyboard || device is Mouse || device is Touchscreen || device is Pen)
                {
                    return;
                }

                // v1.3.15: include the backend interface (iface=GameInput proves the guitar
                // arrived via Microsoft's GameInput stack - the only path this Riff Master uses).
                string devName = device.displayName + " (" + device.layout + ") id=" + device.deviceId +
                    " iface=" + device.description.interfaceName;

                switch (change)
                {
                    case InputDeviceChange.Added:
                        _watchLog.Msg("[YARG-VR][watch] device added: " + devName);
                        ScheduleAutoReconnect("auto (device added: " + devName + ")");
                        break;

                    case InputDeviceChange.Reconnected:
                        _watchLog.Msg("[YARG-VR][watch] device reconnected (e.g. woke from sleep): " + devName);
                        ScheduleAutoReconnect("auto (device reconnected: " + devName + ")");
                        break;

                    case InputDeviceChange.Removed:
                        _watchLog.Msg("[YARG-VR][watch] device removed: " + devName);
                        break;

                    case InputDeviceChange.Disconnected:
                        _watchLog.Msg("[YARG-VR][watch] device disconnected: " + devName);
                        break;

                    // Enabled/Disabled/SoftReset/ConfigurationChanged etc. - not actionable.
                }
            }
            catch
            {
                // Never let watcher noise leak into the game's input pipeline.
            }
        }

        private static void ScheduleAutoReconnect(string trigger)
        {
            _autoReconnectPending = true;
            _autoReconnectTrigger = trigger;
            _autoReconnectAt = Time.unscaledTime + 1.5f; // debounce bursts: last event wins
        }

        // ---- v1.3.15 absence watchdog + Windows device re-scan ----

        private static bool AnyInstrumentPresent()
        {
            try
            {
                var devices = InputSystem.devices;
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d != null && !(d is Keyboard) && !(d is Mouse) &&
                        !(d is Touchscreen) && !(d is Pen))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Device table not readable yet.
            }
            return false;
        }

        /// <summary>
        /// Called every frame from Tick. While NO instrument-class device exists, logs one
        /// compact status line on a slow cadence (the next log then proves whether the
        /// guitar ever appears and when), and after 60 s of continuous absence runs a
        /// ONE-TIME targeted Windows re-scan of the PDP (VID_0E6F) nodes - the programmatic
        /// equivalent of Device Manager's "Scan for hardware changes", which can resurrect
        /// a dongle whose HID/GameInput children failed to start. Resets the moment any
        /// instrument appears (the watcher announces arrivals separately).
        /// </summary>
        private static void WatchdogTick(MelonLoader.MelonLogger.Instance log)
        {
            if (AnyInstrumentPresent())
            {
                if (_absenceActive)
                {
                    _absenceActive = false;
                    log.Msg("[YARG-VR][watch] instrument is back in the device table - absence watchdog reset.");
                }
                return;
            }

            float now = Time.unscaledTime;
            if (!_absenceActive)
            {
                _absenceActive = true;
                _absenceSince = now;
                _absenceNextLog = now + 20f;
                _absenceLogs = 0;
                _absenceLog = log;
                _hidNextProbeAt = now + 25f; // first HID liveness window ~25 s into the absence
            }

            if (!_absenceNudgeRan && now - _absenceSince >= 60f)
            {
                _absenceNudgeRan = true;
                log.Msg("[YARG-VR][watch] no instrument for " + (int)(now - _absenceSince) +
                    " s - running the one-time targeted Windows device re-scan (Device Manager 'Scan for hardware changes' equivalent)...");
                WindowsDeviceNudge.Nudge(_absenceLog != null ? _absenceLog : log);
            }

            // v1.3.16: HID liveness - read the dongle's HID interface directly to decide
            // between "guitar not linked to the dongle" (zero reports) and "data flows but
            // GameInput/Unity drops it" (reports > 0 -> the next build can bridge it).
            if (now >= _hidNextProbeAt)
            {
                _hidNextProbeAt = now + 120f;
                HidLinkProbe.StartWindow(_absenceLog != null ? _absenceLog : log,
                    "guitar absent - press buttons on the guitar");
            }

            if (now >= _absenceNextLog)
            {
                _absenceNextLog = now + (_absenceLogs < 5 ? 30f : 120f);
                _absenceLogs++;

                int devCount = 0;
                try { devCount = InputSystem.devices.Count; } catch { }
                int xinput = CountXInputSlots();
                log.Msg("[YARG-VR][watch] guitar ABSENT for " + (int)(now - _absenceSince) + " s - InputSystem: " +
                    devCount + " devices (text only), XInput: " + (xinput < 0 ? "n/a" : xinput + "/4 slots") +
                    ", GameInput runtime: " + GameInputRuntimeState() +
                    ", dongle HID: " + HidLinkProbe.LastSummary +
                    ". The auto-bind can only act once the guitar reaches Unity's device table - a [watch] line will announce it.");
            }
        }

        /// <summary>v1.3.15: manual F6 with zero candidate devices - the claim pass cannot
        /// conjure a device, so run the targeted Windows re-scan on demand. v1.3.16: also
        /// opens a 5 s HID liveness window - pressing guitar buttons during it decides
        /// between "not linked to the dongle" and "data flows but GameInput/Unity drops it".</summary>
        public static void RunDeviceNudge(MelonLoader.MelonLogger.Instance log, string reason)
        {
            log.Msg("[YARG-VR][watch] running the targeted Windows device re-scan (" + reason + ")...");
            WindowsDeviceNudge.Nudge(log);
            HidLinkProbe.StartWindow(log, "manual F6 - press buttons on the guitar");
        }

        private static int CountXInputSlots()
        {
            if (!TryGetXInputDelegate(out var getState, out _))
            {
                return -1;
            }

            int connected = 0;
            for (int slot = 0; slot < 4; slot++)
            {
                var state = new XInputState();
                if (getState(slot, ref state) == 0)
                {
                    connected++;
                }
            }
            return connected;
        }

        /// <summary>
        /// v1.3.15: Unity's GameInput backend is the ONLY path this Riff Master uses
        /// (iface=GameInput; XInput is never involved). That backend needs Microsoft's
        /// GameInput runtime (gameinput.dll). The user's older logs contained
        /// "GameInput 0x8007007F" (ERROR_PROC_NOT_FOUND) - a broken/old redistributable.
        /// Probe presence + the GameInputCreate export once and cache the verdict.
        /// </summary>
        private static string GameInputRuntimeState()
        {
            if (_gameInputProbed)
            {
                return _gameInputState;
            }

            _gameInputProbed = true;
            try
            {
                IntPtr h = LoadLibrary("gameinput.dll");
                if (h == IntPtr.Zero)
                {
                    _gameInputState = "MISSING (install the Microsoft GameInput Redistributable x64)";
                    return _gameInputState;
                }

                _gameInputState = GetProcAddress(h, "GameInputCreate") != IntPtr.Zero
                    ? "present"
                    : "present but GameInputCreate export MISSING (update the Microsoft GameInput Redistributable)";
                return _gameInputState;
            }
            catch
            {
                _gameInputState = "<probe failed>";
                return _gameInputState;
            }
        }

        /// <summary>
        /// v1.3.13: devices that enumerated BEFORE the watcher installed produce no Added
        /// event. Called once right after the startup dump: if any instrument-class device
        /// is already in the table, schedule one auto reconnect pass (one pass covers
        /// every device in the table anyway).
        /// </summary>
        public static void AutoReconnectIfInstrumentsPresent(MelonLoader.MelonLogger.Instance log)
        {
            try
            {
                var devices = InputSystem.devices;
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d == null || d is Keyboard || d is Mouse || d is Touchscreen || d is Pen)
                    {
                        continue;
                    }

                    _autoLog = log;
                    ScheduleAutoReconnect("auto (instrument already present at load: " +
                        d.displayName + " (" + d.layout + "))");
                    return;
                }
            }
            catch
            {
                // Device table not readable - the watcher covers later additions.
            }
        }

        /// <summary>
        /// Re-run YARG's own profile-claim for every visible instrument device.
        /// Non-destructive: uses the same public TryConnectProfile path YARG uses on
        /// first button press; reports exactly why a device stays dead.
        /// v1.3.13: takes a trigger name for the log ("manual F6" / "auto (device
        /// added: ...)") so a pass can always be traced back to its cause.
        /// v1.3.15: returns the number of candidate devices scanned (-1 = the pass did
        /// not run: game still loading or introspection unavailable) so the manual F6
        /// caller can detect "zero candidates" and run the Windows device re-scan.
        /// </summary>
        public static int ReconnectInstruments(MelonLoader.MelonLogger.Instance log, string trigger)
        {
            if (string.IsNullOrEmpty(trigger))
            {
                trigger = "manual";
            }

            log.Msg("[YARG-VR][reconnect] attempting to connect instrument profiles (trigger: " + trigger + ")...");

            if (!YargStaticStateIsSettled())
            {
                // v1.3.7 guard (same reasoning as the introspection gate): never execute
                // YARG statics before the game's first scene has finished loading.
                if (trigger.StartsWith("auto", StringComparison.Ordinal))
                {
                    log.Msg("[YARG-VR][reconnect] deferred - game has not finished loading yet; the pass will retry automatically.");
                }
                else
                {
                    log.Msg("[YARG-VR][reconnect] deferred - game has not finished loading yet. Wait for the main menu, then press F6 again.");
                }
                return -1;
            }

            if (!EnsureYargRefs(log) || !TryGetDevices(log, out var devices))
            {
                return -1;
            }

            int attempts = 0, connected = 0;
            foreach (var device in devices)
            {
                if (device == null || device is Keyboard || device is Mouse || device is Touchscreen || device is Pen)
                {
                    continue;
                }

                attempts++;
                string devName = device.displayName + " (" + device.layout + ")";

                bool taken = false;
                try { taken = (bool)_miIsDeviceTaken.Invoke(null, new object[] { device }); }
                catch (Exception e) { log.Msg("[YARG-VR][reconnect] IsDeviceTaken failed: " + e.Message); }

                if (taken)
                {
                    log.Msg("[YARG-VR][reconnect] " + devName + ": already claimed by a player - OK.");
                    connected++;
                    continue;
                }

                object profile = null;
                try { profile = _miGetProfileForDevice.Invoke(null, new object[] { device }); }
                catch (Exception e) { log.Msg("[YARG-VR][reconnect] GetProfileForDevice failed: " + e.Message); }

                if (profile == null)
                {
                    // v1.3.14: the Riff Master's hash can change between boots (SHA1 of the
                    // device description, which varies with enumeration order on GameInput
                    // devices) - a profile saved in one boot never matches the next. When
                    // AutoBindProfileName is set, re-bind the live device to that profile
                    // via YARG's OWN AddDevice + save, then connect. Self-healing.
                    if (!string.IsNullOrWhiteSpace(AutoBindProfileName) &&
                        _miAddDevice != null && _piBindingsPath != null && _miSaveBindings != null)
                    {
                        log.Msg("[YARG-VR][reconnect] " + devName + ": no matching profile - auto-binding to profile '" +
                            AutoBindProfileName + "' (YARG's own AddDevice)...");

                        if (AutoBindDevice(log, device, AutoBindProfileName))
                        {
                            object p2 = null;
                            try { p2 = _miGetProfileForDevice.Invoke(null, new object[] { device }); }
                            catch (Exception e) { log.Msg("[YARG-VR][reconnect] re-match failed: " + e.Message); }

                            if (p2 != null)
                            {
                                bool ok2 = false;
                                try { ok2 = (bool)_miTryConnectProfile.Invoke(null, new object[] { device }); }
                                catch (Exception e) { log.Msg("[YARG-VR][reconnect] TryConnectProfile failed: " + e.Message); }

                                if (ok2)
                                {
                                    connected++;
                                    log.Msg("[YARG-VR][reconnect] " + devName + ": AUTO-BOUND to '" + AutoBindProfileName + "' and CONNECTED.");
                                    continue;
                                }

                                log.Msg("[YARG-VR][reconnect] " + devName + ": auto-bound but connect failed (profile already in use?).");
                            }
                            else
                            {
                                log.Msg("[YARG-VR][reconnect] " + devName + ": auto-bind ran but the profile still does not match - press F7 and send the log.");
                            }
                        }

                        continue;
                    }

                    log.Msg("[YARG-VR][reconnect] " + devName + ": NO matching profile - the saved profile's device " +
                        "layout/hash does not match this connection (the hash can change between boots on " +
                        "GameInput instruments). One-time fix: set AutoBindProfileName = <your profile name> " +
                        "in MelonLoader/Preferences/YARG-VR.cfg and press F6 - the mod then re-binds and " +
                        "connects automatically every boot. (Or re-bind the profile in YARG's player settings.)");
                    continue;
                }

                bool ok = false;
                try { ok = (bool)_miTryConnectProfile.Invoke(null, new object[] { device }); }
                catch (Exception e) { log.Msg("[YARG-VR][reconnect] TryConnectProfile failed: " + e.Message); }

                if (ok)
                {
                    connected++;
                    log.Msg("[YARG-VR][reconnect] " + devName + ": profile CONNECTED.");
                }
                else
                {
                    log.Msg("[YARG-VR][reconnect] " + devName + ": profile found but connect failed (profile already in use?).");
                }
            }

            log.Msg("[YARG-VR][reconnect] done: " + connected + "/" + attempts +
                " devices claimed. If a device says 'already claimed' but is dead in-game, press F7 and send the log.");
            return attempts;
        }

        /// <summary>
        /// v1.3.14: bind a live instrument device to the named saved profile using YARG's
        /// own ProfileBindings.AddDevice (the same call YARG's profile UI makes - it
        /// serializes the device description + hash into the profile), then persist via
        /// BindingsContainer.SaveBindings(BindingsPath). Defeats the boot-order hash
        /// drift that makes YARG's saved profile stop matching the Riff Master. The
        /// caller re-matches and calls TryConnectProfile afterwards.
        /// </summary>
        private static bool AutoBindDevice(MelonLoader.MelonLogger.Instance log, InputDevice device, string profileName)
        {
            try
            {
                IList profiles = _piProfiles != null ? _piProfiles.GetValue(null) as IList : null;
                object target = null;
                string names = "";
                if (profiles != null)
                {
                    var available = new StringBuilder();
                    foreach (var pr in profiles)
                    {
                        if (pr == null) continue;
                        string n = ReadProfileField(pr, "Name") ?? "?";
                        available.Append(n).Append("; ");
                        if (target == null && string.Equals(n, profileName, StringComparison.OrdinalIgnoreCase))
                        {
                            target = pr;
                        }
                    }
                    names = available.ToString();
                }

                if (target == null)
                {
                    log.Msg("[YARG-VR][reconnect] auto-bind: no profile named '" + profileName +
                        "'. Profiles available: " + (names.Length > 0 ? names : "<none>") +
                        " - fix AutoBindProfileName in MelonLoader/Preferences/YARG-VR.cfg.");
                    return false;
                }

                object bindings = null;
                try { bindings = _miGetBindingsForProfile.Invoke(null, new object[] { target }); }
                catch (Exception e) { log.Msg("[YARG-VR][reconnect] auto-bind: GetBindingsForProfile failed: " + e.Message); }

                if (bindings == null)
                {
                    log.Msg("[YARG-VR][reconnect] auto-bind: could not obtain the profile's bindings.");
                    return false;
                }

                bool added = (bool)_miAddDevice.Invoke(bindings, new object[] { device });
                log.Msg("[YARG-VR][reconnect] auto-bind: AddDevice -> " +
                    (added ? "device bound to profile '" + profileName + "'" : "returned false (device may already be bound)"));

                try
                {
                    string bpath = _piBindingsPath.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(bpath))
                    {
                        _miSaveBindings.Invoke(null, new object[] { bpath });
                        log.Msg("[YARG-VR][reconnect] auto-bind: bindings saved to " + bpath);
                    }
                }
                catch (Exception e)
                {
                    log.Msg("[YARG-VR][reconnect] auto-bind: SaveBindings failed (bind still active for this session): " + e.Message);
                }

                return true;
            }
            catch (Exception e)
            {
                var tie = e as TargetInvocationException;
                log.Msg("[YARG-VR][reconnect] auto-bind failed: " +
                    (tie != null && tie.InnerException != null
                        ? tie.InnerException.GetType().Name + ": " + tie.InnerException.Message
                        : e.Message));
                return false;
            }
        }

        // ---- dump sections ----

        private static void ProbeXInput(StringBuilder sb)
        {
            if (!TryGetXInputDelegate(out var getState, out string loadError))
            {
                sb.AppendLine("XInput: unavailable (" + loadError + ")");
                return;
            }

            for (int slot = 0; slot < 4; slot++)
            {
                var state = new XInputState();
                int rc = getState(slot, ref state);
                if (rc == 0)
                {
                    _xinputFirstConnected[slot] = true;
                    _xinputFirstPackets[slot] = state.dwPacketNumber;
                    sb.Append("XInput slot ").Append(slot).Append(": CONNECTED packet=").Append(state.dwPacketNumber)
                      .Append(" buttons=0x").Append(state.wButtons.ToString("X4"))
                      .Append(" LT=").Append(state.bLeftTrigger).Append(" RT=").Append(state.bRightTrigger)
                      .AppendLine();
                }
                else if (rc == 1167) // ERROR_DEVICE_NOT_CONNECTED
                {
                    _xinputFirstConnected[slot] = false;
                    _xinputFirstPackets[slot] = 0;
                    sb.Append("XInput slot ").Append(slot).AppendLine(": empty");
                }
                else
                {
                    _xinputFirstConnected[slot] = false;
                    sb.Append("XInput slot ").Append(slot).Append(": rc=").AppendLine(rc.ToString());
                }
            }
        }

        private static void SecondXInputSample()
        {
            if (_xinputLog == null || !TryGetXInputDelegate(out var getState, out _))
            {
                return;
            }

            for (int slot = 0; slot < 4; slot++)
            {
                if (!_xinputFirstConnected[slot])
                {
                    continue;
                }

                var state = new XInputState();
                int rc = getState(slot, ref state);
                if (rc == 0)
                {
                    uint delta = state.dwPacketNumber - _xinputFirstPackets[slot];
                    _xinputLog.Msg("[YARG-VR][probe] XInput slot " + slot + " packet " + _xinputFirstPackets[slot] +
                        " -> " + state.dwPacketNumber + " (delta " + delta + (delta > 0 ? " - stream LIVE" : " - stream IDLE (press buttons)") + ")");
                }
                else
                {
                    _xinputLog.Msg("[YARG-VR][probe] XInput slot " + slot + " dropped from the OS between samples (rc=" + rc + ")");
                }
            }
        }

        private static void ProbeUnityDevices(StringBuilder sb)
        {
            try
            {
                var devices = InputSystem.devices;
                sb.AppendLine("Unity InputSystem devices: " + devices.Count);
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    sb.Append("  [").Append(d.deviceId).Append("] ")
                      .Append(d.layout)
                      .Append(" | display='").Append(d.displayName)
                      .Append("' | name=").Append(d.name)
                      .Append(" | enabled=").Append(d.enabled);

                    if (!(d is Keyboard) && !(d is Mouse) && !(d is Touchscreen) && !(d is Pen))
                    {
                        var desc = d.description;
                        sb.Append(" | iface=").Append(desc.interfaceName)
                          .Append(" class=").Append(desc.deviceClass)
                          .Append(" mfr='").Append(desc.manufacturer)
                          .Append("' product='").Append(desc.product)
                          .Append("' serial='").Append(desc.serial)
                          .Append("' ver='").Append(desc.version)
                          .Append("'");
                        string caps = desc.capabilities;
                        if (!string.IsNullOrEmpty(caps))
                        {
                            if (caps.Length > 220) caps = caps.Substring(0, 220) + "...";
                            sb.Append(" caps=").Append(caps);
                        }

                        string hash = TryGetHash(d);
                        sb.Append(" | yargHash=").Append(hash ?? "<unavailable>");
                    }

                    sb.AppendLine();
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("Unity InputSystem unavailable: " + e.Message);
            }
        }

        /// <summary>
        /// v1.3.9: true only once the game's own boot has fully completed. Requires ALL of:
        ///   - Time.frameCount > 0: at least one player-loop frame has started. Unity runs
        ///     RuntimeInitializeOnLoadMethod(BeforeSplashScreen) callbacks and every
        ///     first-scene Awake (including YARG's SingletonAwake) BEFORE the first frame,
        ///     so this alone proves YARG's static state is safe to touch.
        ///   - active scene buildIndex >= 0: rejects the placeholder "active scene" Unity
        ///     reports before any real scene has loaded (it reports buildIndex -1).
        ///   - isLoaded: that scene finished its Awake/OnEnable phase.
        /// v1.3.7/v1.3.8 used isLoaded ALONE - and on Unity 6000 + MelonLoader 0.7.3 that
        /// returned true while OnInitializeMelon still ran before YARG's boot, so the
        /// startup introspection still executed PlayerContainer's static ctor early (see
        /// class header). Code driven from OnUpdate (F6/F7, SteamVR connect dump, the
        /// deferred startup probe) only runs once frames are ticking, so this never
        /// blocks real use.
        /// </summary>
        public static bool YargStaticStateIsSettled()
        {
            try
            {
                if (Time.frameCount <= 0) return false;
                var scene = SceneManager.GetActiveScene();
                return scene.buildIndex >= 0 && scene.isLoaded;
            }
            catch { return false; }
        }

        private static void ProbeYargClaims(StringBuilder sb)
        {
            if (!YargStaticStateIsSettled())
            {
                sb.AppendLine("YARG introspection deferred - game has not finished loading yet (press F7 once the main menu is up).");
                return;
            }

            if (!EnsureYargRefs(null))
            {
                sb.AppendLine("YARG input introspection: reflection handles unavailable (YARG version changed?).");
                return;
            }

            try
            {
                string dir = _piProfilesDirectory.GetValue(null) as string;
                sb.AppendLine("YARG profiles directory: " + dir + "  (bindings.json lives here - attach it to bug reports)");
            }
            catch (Exception e)
            {
                // Never swallow silently again - a hidden failure here is exactly what
                // masked the v1.3.5/v1.3.6 static-ctor poisoning during diagnosis.
                sb.AppendLine("YARG profiles directory read failed: " + e.Message);
            }

            // What has YARG registered/claimed so far?
            try
            {
                var registered = _fiRegisteredDevices.GetValue(null) as IEnumerable;
                int n = 0;
                var names = new StringBuilder();
                foreach (var dev in registered)
                {
                    var d = dev as InputDevice;
                    if (d == null) continue;
                    n++;
                    names.Append(d.layout).Append("; ");
                }
                sb.AppendLine("YARG registered devices (claimed by players): " + n + (n > 0 ? " [" + names + "]" : ""));
            }
            catch (Exception e)
            {
                sb.AppendLine("YARG registered-device read failed: " + e.Message);
            }

            if (!TryGetDevices(null, out var devices) || _piProfiles == null)
            {
                return;
            }

            IList profiles;
            try { profiles = _piProfiles.GetValue(null) as IList; }
            catch (Exception e)
            {
                // TargetInvocationException hides the real reason in InnerException.
                var tie = e as TargetInvocationException;
                sb.AppendLine("YARG profile list read failed: " +
                    (tie != null && tie.InnerException != null ? tie.InnerException.Message : e.Message));
                return;
            }

            if (profiles == null || profiles.Count == 0)
            {
                sb.AppendLine("YARG profiles: NONE saved - instruments can never auto-connect. Create a player profile in YARG.");
                return;
            }

            sb.AppendLine("YARG profiles: " + profiles.Count + " saved");

            // Match matrix: instrument devices x profiles
            foreach (var device in devices)
            {
                if (device == null || device is Keyboard || device is Mouse || device is Touchscreen || device is Pen)
                {
                    continue;
                }

                string devName = device.displayName + " (" + device.layout + ")";
                var matches = new StringBuilder();
                int matchCount = 0;

                foreach (var profileObj in profiles)
                {
                    if (profileObj == null) continue;

                    string pName = ReadProfileField(profileObj, "Name") ?? "?";
                    bool taken = false;
                    try { taken = (bool)_miIsProfileTaken.Invoke(null, new object[] { profileObj }); }
                    catch { }

                    object bindings = null;
                    try { bindings = _miGetBindingsForProfile.Invoke(null, new object[] { profileObj }); }
                    catch { }

                    bool m = false;
                    if (bindings != null && _miMatchesDevice != null)
                    {
                        try { m = (bool)_miMatchesDevice.Invoke(bindings, new object[] { device }); }
                        catch { }
                    }

                    if (m)
                    {
                        matchCount++;
                        matches.Append("'").Append(pName).Append("'").Append(taken ? " [in use]" : " [free]").Append("; ");
                    }
                }

                sb.Append("  device '").Append(devName).Append("': ");
                if (matchCount > 0)
                {
                    sb.Append(matchCount).Append(" matching profile(s): ").Append(matches);
                    bool takenNow = false;
                    try { takenNow = (bool)_miIsDeviceTaken.Invoke(null, new object[] { device }); }
                    catch { }
                    sb.Append(takenNow ? "- claimed already" : "- NOT claimed (F6 can connect it)");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("NO matching profile - YARG can never auto-connect it (hash mismatch or profile for another instrument).");
                }
            }

            // Current players and their devices
            try
            {
                var players = _piPlayers.GetValue(null) as IEnumerable;
                int count = 0;
                if (players != null)
                {
                    foreach (var player in players)
                    {
                        count++;
                        string pName = "?";
                        try
                        {
                            var profile = player.GetType().GetProperty("Profile")?.GetValue(player, null);
                            pName = ReadProfileField(profile, "Name") ?? "?";
                        }
                        catch { }

                        var boundDevices = new StringBuilder();
                        try
                        {
                            var binds = player.GetType().GetProperty("Bindings")?.GetValue(player, null);
                            var list = binds?.GetType().GetProperty("InputDevices")?.GetValue(binds, null) as IEnumerable;
                            if (list != null)
                            {
                                foreach (var dev in list)
                                {
                                    var d = dev as InputDevice;
                                    boundDevices.Append(d != null ? d.layout : "?").Append("; ");
                                }
                            }
                        }
                        catch { }

                        sb.AppendLine("  player '" + pName + "' devices: " + boundDevices);
                    }
                }
                sb.AppendLine("YARG active players: " + count);
                if (count == 0)
                {
                    sb.AppendLine("  NOTE: saved profiles exist but NO player is active - a profile only plays if its player is spawned (a connected device spawns it).");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("YARG player list read failed: " + e.Message);
            }

            // v1.3.14: dump every profile's stored serialized devices (Layout + Hash) -
            // put these next to the live "yargHash=" line above and hash drift between
            // boots becomes directly visible (this is what kills profile matching).
            try
            {
                foreach (var profileObj in profiles)
                {
                    if (profileObj == null) continue;
                    string pName = ReadProfileField(profileObj, "Name") ?? "?";

                    object bindings = null;
                    try { bindings = _miGetBindingsForProfile.Invoke(null, new object[] { profileObj }); }
                    catch { }
                    if (bindings == null) continue;

                    var stored = new StringBuilder();
                    var entries = _fiUnresolvedDevices != null
                        ? _fiUnresolvedDevices.GetValue(bindings) as IEnumerable
                        : null;
                    if (entries != null)
                    {
                        foreach (var entry in entries)
                        {
                            if (entry == null) continue;
                            string lay = entry.GetType().GetField("Layout")?.GetValue(entry) as string;
                            string hash = entry.GetType().GetField("Hash")?.GetValue(entry) as string;
                            stored.Append(lay ?? "?").Append(" hash=").Append(hash ?? "?").Append("; ");
                        }
                    }

                    sb.AppendLine("  profile '" + pName + "' stored devices: " +
                        (stored.Length > 0 ? stored.ToString() : "<none stored>"));
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("stored-device dump failed: " + e.Message);
            }
        }

        // ---- passive input-activity window (v1.3.6) ----

        /// <summary>
        /// Records each device's current lastUpdateTime, then compares again 5 s later.
        /// A device whose lastUpdateTime advanced is producing input. Pure field reads -
        /// unlike the v1.3.5 onEvent capture, nothing here can affect the game's input
        /// pipeline (the v1.3.5 hook was removed after it correlated with a native crash).
        /// </summary>
        private static void StartActivityWindow(MelonLoader.MelonLogger.Instance log)
        {
            if (_activityPending)
            {
                // A previous window was still open - finish it first so results never interleave.
                FinishActivityWindow();
            }

            _activityFirstUpdate.Clear();
            try
            {
                var devices = InputSystem.devices;
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d == null)
                    {
                        continue;
                    }

                    try { _activityFirstUpdate[d.deviceId] = d.lastUpdateTime; }
                    catch { }
                }
            }
            catch
            {
                // InputSystem not ready yet (startup probe) - the window will report that.
            }

            _activityPending = true;
            _activityEnd = Time.unscaledTime + 5f;
            _activityLog = log;
        }

        private static void FinishActivityWindow()
        {
            if (_activityLog == null)
            {
                return;
            }

            try
            {
                _activityLog.Msg("[YARG-VR][probe] input activity (5 s window) results:");
                int reported = 0;
                var devices = InputSystem.devices;
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d == null || d is Keyboard || d is Mouse || d is Touchscreen || d is Pen)
                    {
                        continue;
                    }

                    double first;
                    bool hadFirst = _activityFirstUpdate.TryGetValue(d.deviceId, out first);
                    bool active = false;
                    if (hadFirst)
                    {
                        try { active = d.lastUpdateTime > first + 0.0001; }
                        catch { }
                    }
                    else
                    {
                        // Device appeared during the window - it is alive by definition.
                        active = true;
                    }

                    _activityLog.Msg("[YARG-VR][probe]   " + d.name + ": " +
                        (active ? "ACTIVE (input flowing)" : "idle (no state updates)"));
                    reported++;
                }

                if (reported == 0)
                {
                    _activityLog.Msg("[YARG-VR][probe]   no instrument-class InputSystem devices at probe time.");
                }
            }
            catch (Exception e)
            {
                _activityLog.Msg("[YARG-VR][probe] activity window failed: " + e.Message);
            }

            _activityLog = null;
            _activityFirstUpdate.Clear();
        }

        // ---- reflection plumbing ----

        private static void FlushLines(MelonLoader.MelonLogger.Instance log, StringBuilder sb)
        {
            string[] lines = sb.ToString().Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                {
                    log.Msg("[YARG-VR][probe] " + lines[i]);
                }
            }
        }

        private static bool TryGetDevices(MelonLoader.MelonLogger.Instance log, out IReadOnlyList<InputDevice> devices)
        {
            try
            {
                devices = InputSystem.devices;
                return true;
            }
            catch (Exception e)
            {
                devices = null;
                if (log != null)
                {
                    log.Msg("[YARG-VR][probe] InputSystem.devices read failed: " + e.Message);
                }
                return false;
            }
        }

        private static string ReadProfileField(object profile, string field)
        {
            if (profile == null)
            {
                return null;
            }

            foreach (var fi in _profileFields)
            {
                if (fi.Name == field)
                {
                    var v = fi.GetValue(profile);
                    return v == null ? null : v.ToString();
                }
            }

            return null;
        }

        private static string TryGetHash(InputDevice device)
        {
            // Same gate as the introspection: BindingSerialization is YARG's static too.
            if (!YargStaticStateIsSettled() || !EnsureYargRefs(null) || _miGetHash == null)
            {
                return null;
            }

            try
            {
                return _miGetHash.Invoke(null, new object[] { device }) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool EnsureYargRefs(MelonLoader.MelonLogger.Instance log)
        {
            if (_yargTried)
            {
                return _yargOk;
            }

            _yargTried = true;
            try
            {
                var playerContainer = Type.GetType("YARG.Player.PlayerContainer, Assembly-CSharp", false);
                var inputManager = Type.GetType("YARG.Input.InputManager, Assembly-CSharp", false);
                var bindingsContainer = Type.GetType("YARG.Input.Bindings.BindingsContainer, Assembly-CSharp", false);
                var profileBindings = Type.GetType("YARG.Input.ProfileBindings, Assembly-CSharp", false);
                var bindingSerialization = Type.GetType("YARG.Input.Serialization.BindingSerialization, Assembly-CSharp", false);
                var yargProfile = Type.GetType("YARG.Core.Game.YargProfile, YARG.Core.Package", false);

                if (playerContainer == null || inputManager == null || bindingsContainer == null ||
                    profileBindings == null || bindingSerialization == null || yargProfile == null)
                {
                    if (log != null)
                    {
                        log.Msg("[YARG-VR][probe] YARG type resolution failed:" +
                            " PlayerContainer=" + (playerContainer != null) +
                            " InputManager=" + (inputManager != null) +
                            " BindingsContainer=" + (bindingsContainer != null) +
                            " ProfileBindings=" + (profileBindings != null) +
                            " BindingSerialization=" + (bindingSerialization != null) +
                            " YargProfile=" + (yargProfile != null));
                    }
                    _yargOk = false;
                    return false;
                }

                const BindingFlags STAT = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags INST = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                _piProfilesDirectory = playerContainer.GetProperty("ProfilesDirectory", STAT | BindingFlags.GetProperty);
                _piProfiles = playerContainer.GetProperty("Profiles", STAT | BindingFlags.GetProperty);
                _piPlayers = playerContainer.GetProperty("Players", STAT | BindingFlags.GetProperty);
                _miGetProfileForDevice = playerContainer.GetMethod("GetProfileForDevice", STAT);
                _miIsDeviceTaken = playerContainer.GetMethod("IsDeviceTaken", STAT);
                _miTryConnectProfile = playerContainer.GetMethod("TryConnectProfile", STAT);
                _miIsProfileTaken = playerContainer.GetMethod("IsProfileTaken", STAT);
                _fiRegisteredDevices = inputManager.GetField("_registeredDevices", STAT);
                _miGetBindingsForProfile = bindingsContainer.GetMethod("GetBindingsForProfile", STAT);
                _miMatchesDevice = profileBindings.GetMethod("MatchesDevice", INST);
                _miGetHash = bindingSerialization.GetMethod("GetHash", STAT);
                // v1.3.14 auto-bind (verified against the 0.15.0 assemblies)
                _miAddDevice = profileBindings.GetMethod("AddDevice", INST);
                _piBindingsPath = bindingsContainer.GetProperty("BindingsPath", STAT | BindingFlags.GetProperty);
                _miSaveBindings = bindingsContainer.GetMethod("SaveBindings", STAT);
                _fiUnresolvedDevices = profileBindings.GetField("_unresolvedDevices", INST);

                var profileFields = new List<FieldInfo>();
                foreach (var name in new[] { "Name", "Id", "IsBot", "GameMode", "AutoConnectOrder", "LastUsed" })
                {
                    var fi = yargProfile.GetField(name, INST | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi != null)
                    {
                        profileFields.Add(fi);
                    }
                }
                _profileFields = profileFields.ToArray();

                _yargOk = _piProfiles != null && _miGetProfileForDevice != null && _miIsDeviceTaken != null &&
                          _miTryConnectProfile != null && _miGetBindingsForProfile != null &&
                          _miMatchesDevice != null && _miGetHash != null && _profileFields.Length >= 5;

                if (!_yargOk && log != null)
                {
                    log.Msg("[YARG-VR][probe] YARG member resolution incomplete - introspection will be partial.");
                }
            }
            catch (Exception e)
            {
                if (log != null)
                {
                    log.Msg("[YARG-VR][probe] YARG reflection setup failed: " + e);
                }
                _yargOk = false;
            }

            return _yargOk;
        }

        // ---- XInput via kernel32 (Mono-safe: DllImport on xinput*.dll failed on the user's machine) ----

        private delegate int XInputGetStateDelegate(int dwUserIndex, ref XInputState pState);

        private static bool _xinputTried;
        private static XInputGetStateDelegate _xinputGetState;

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint dwPacketNumber;
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        private static bool TryGetXInputDelegate(out XInputGetStateDelegate state, out string error)
        {
            if (_xinputTried)
            {
                state = _xinputGetState;
                error = _xinputGetState == null ? "no XInput DLL loadable" : null;
                return _xinputGetState != null;
            }

            _xinputTried = true;
            state = null;
            error = null;

            string[] candidates = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
            foreach (var dll in candidates)
            {
                IntPtr h = LoadLibrary(dll);
                if (h == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr proc = GetProcAddress(h, "XInputGetState");
                if (proc == IntPtr.Zero)
                {
                    continue;
                }

                _xinputGetState = (XInputGetStateDelegate)Marshal.GetDelegateForFunctionPointer(
                    proc, typeof(XInputGetStateDelegate));
                state = _xinputGetState;
                return true;
            }

            error = "LoadLibrary failed for xinput1_4/1_3/9_1_0 (last error " + Marshal.GetLastWin32Error() + ")";
            return false;
        }
    }
}
