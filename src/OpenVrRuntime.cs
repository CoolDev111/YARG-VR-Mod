using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace YargVr
{
    /// <summary>
    /// Thin, self-contained wrapper around Valve's official OpenVR C# binding (openvr_api.cs).
    ///
    /// Responsibilities:
    ///  - Preload the native openvr_api.dll from a set of candidate locations (the loader must
    ///    find it before the first P/Invoke, so we LoadLibrary it explicitly by full path).
    ///  - Initialize OpenVR as a Scene application and grab the compositor.
    ///  - Convert HMD poses from OpenVR's coordinate system (-Z forward) into Unity's (+Z forward).
    ///  - Expose per-eye geometry (eye-to-head offsets) so the rig can render true stereo.
    ///  - Submit per-eye textures to the SteamVR compositor. Since v1.1.0 the left and right
    ///    eyes receive DIFFERENT textures (true stereo), and D3D-style render targets are
    ///    submitted with vertically flipped texture bounds (Unity RTs store rows top-down,
    ///    the compositor expects bottom-up - without the flip everything appears upside down).
    /// </summary>
    internal static class OpenVrRuntime
    {
        private static bool _nativePreloaded;
        private static bool _initialized;
        private static string _initError;
        private static CVRSystem _system;
        private static CVRCompositor _compositor;
        private static TrackedDevicePose_t[] _renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        // NOTE: the binding's WaitGetPoses wrapper dereferences BOTH arrays' .Length - passing
        // null for the game-pose array (legal in the C API) throws NullReferenceException every
        // call. Always pass a real array here.
        private static TrackedDevicePose_t[] _gamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static string _lastPoseProblem;
        private static int _lastPoseProblemFrame = -1;
        private static bool _loggedFirstPose;
        private static uint _recWidth, _recHeight;
        private static float _eyeVFovDeg = 100f;
        private static float _eyeAspect = 1f;

        public static bool IsInitialized { get { return _initialized; } }
        public static string InitError { get { return _initError; } }
        public static uint RecommendedWidth { get { return _recWidth; } }
        public static uint RecommendedHeight { get { return _recHeight; } }
        public static float EyeVFovDeg { get { return _eyeVFovDeg; } }
        public static float EyeAspect { get { return _eyeAspect; } }

        #region Native library preloading

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>
        /// Tries to LoadLibrary() openvr_api.dll by absolute path so that the DllImports inside
        /// openvr_api.cs (which reference the bare name "openvr_api.dll") resolve against the
        /// already-loaded module. Order:
        ///   1. next to this mod DLL (MelonLoader/Mods/YARG-VR/openvr_api.dll)
        ///   2. the YARG game root (YARG.exe folder)
        ///   3. the SteamVR runtime (parsed from %LOCALAPPDATA%/openvr/openvrpaths.vrpathreg)
        ///   4. the YARG root /MelonLoader folder
        /// If nothing matched, the standard Windows search order applies (PATH etc.).
        /// </summary>
        public static string PreloadNativeLibrary()
        {
            if (_nativePreloaded)
            {
                return "already loaded";
            }

            IntPtr handle = GetModuleHandle("openvr_api.dll");
            if (handle != IntPtr.Zero)
            {
                _nativePreloaded = true;
                return "already loaded by the process";
            }

            foreach (string candidate in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    handle = LoadLibrary(candidate);
                    if (handle != IntPtr.Zero)
                    {
                        _nativePreloaded = true;
                        MelonLoader.MelonLogger.Msg("[YARG-VR] Loaded native OpenVR API: " + candidate);
                        return candidate;
                    }
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Warning("[YARG-VR] Failed to load '" + candidate + "': " + e.Message);
                }
            }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> CandidatePaths()
        {
            // 1. Next to the mod assembly itself (works no matter where MelonLoader placed us)
            string modDir = null;
            try
            {
                string codeBase = typeof(OpenVrRuntime).Assembly.Location;
                if (!string.IsNullOrEmpty(codeBase))
                {
                    modDir = Path.GetDirectoryName(Path.GetFullPath(codeBase));
                }
            }
            catch
            {
                // Ignore - assembly location may be unavailable.
            }

            if (!string.IsNullOrEmpty(modDir))
            {
                yield return Path.Combine(modDir, "openvr_api.dll");
            }

            // 2./4. Game root and its MelonLoader folder
            string gameRoot = null;
            try
            {
                gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch
            {
                // Ignore
            }

            if (!string.IsNullOrEmpty(gameRoot))
            {
                yield return Path.Combine(gameRoot, "openvr_api.dll");
                yield return Path.Combine(gameRoot, "MelonLoader", "openvr_api.dll");
            }

            // 3. SteamVR runtime, via the OpenVR path registry file
            string vrPathReg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "openvr", "openvrpaths.vrpathreg");

            foreach (string runtime in ParseVrPathRegistry(vrPathReg))
            {
                yield return Path.Combine(runtime, "bin", "win64", "openvr_api.dll");
                yield return Path.Combine(runtime, "bin", "win32", "openvr_api.dll");
            }
        }

        private static System.Collections.Generic.IEnumerable<string> ParseVrPathRegistry(string path)
        {
            List<string> runtimes = new List<string>();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    // Minimal, dependency-free extraction of the "runtime" array entries.
                    int idx = json.IndexOf("\"runtime\"", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int arrStart = json.IndexOf('[', idx);
                        int arrEnd = json.IndexOf(']', arrStart);
                        if (arrStart >= 0 && arrEnd > arrStart)
                        {
                            string arrayBody = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                            foreach (string raw in arrayBody.Split(','))
                            {
                                string entry = raw.Trim().Trim('"').Replace("\\\\", "\\");
                                if (!string.IsNullOrEmpty(entry) && Directory.Exists(entry))
                                {
                                    runtimes.Add(entry);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Registry parsing is best-effort.
            }

            foreach (string runtime in runtimes)
            {
                yield return runtime;
            }
        }

        #endregion

        #region Diagnostics

        private static float _nextProcProbe;
        private static bool _steamVrRunningCached;

        /// <summary>
        /// True when the SteamVR status process (vrmonitor) is running. Cached for ~10 s
        /// because enumerating processes every frame would be wasteful.
        /// </summary>
        public static bool IsSteamVrRunning
        {
            get
            {
                if (Time.unscaledTime >= _nextProcProbe)
                {
                    _nextProcProbe = Time.unscaledTime + 10f;
                    try
                    {
                        _steamVrRunningCached =
                            System.Diagnostics.Process.GetProcessesByName("vrmonitor").Length > 0;
                    }
                    catch
                    {
                        _steamVrRunningCached = false;
                    }
                }

                return _steamVrRunningCached;
            }
        }

        /// <summary>
        /// Human-readable explanation of the current OpenVR environment, pinpointing WHY no
        /// usable headset is visible. Notably: Meta "Virtual Display" (the Quest remote-monitor
        /// mode) is a productivity feature - it streams a Windows desktop to the headset and
        /// NEVER registers an HMD with SteamVR, so OpenVR apps simply cannot see the headset
        /// while it is connected that way.
        /// </summary>
        public static string DescribeEnvironment()
        {
            bool runtimeInstalled;
            bool hmdPresent;
            try { runtimeInstalled = OpenVR.IsRuntimeInstalled(); }
            catch { runtimeInstalled = false; }
            try { hmdPresent = OpenVR.IsHmdPresent(); }
            catch { hmdPresent = false; }

            if (!runtimeInstalled)
            {
                return "SteamVR is not installed on this PC. The mod drives the headset through " +
                    "SteamVR (OpenVR) only - install SteamVR from Steam and connect the headset in VR mode.";
            }

            if (!hmdPresent)
            {
                if (!IsSteamVrRunning)
                {
                    return "SteamVR is installed but NOT running. Launch SteamVR, or connect the " +
                        "headset through a PCVR app that starts it for you (Quest Link, Steam Link, " +
                        "Virtual Desktop, ALVR). Note that monitor-style connections such as Meta " +
                        "Virtual Display never start SteamVR.";
                }

                return "SteamVR is running but reports NO headset. If your Quest is connected via " +
                    "Meta 'Virtual Display' (the remote-monitor mode), SteamVR can never see it - " +
                    "that mode only mirrors a flat monitor and does not create a VR headset. " +
                    "Connect in VR mode instead: Quest Link (Air Link or cable), the Steam Link app, " +
                    "Virtual Desktop (with SteamVR), or ALVR.";
            }

            return "SteamVR reports a headset - initializing...";
        }

        #endregion

        #region Initialization

        /// <summary>Attempts OpenVR init. Returns true when the compositor is ready.</summary>
        public static bool TryInitialize()
        {
            if (_initialized)
            {
                return true;
            }

            // Load the native binding BEFORE touching any OpenVR entry points (the P/Invokes
            // would throw DllNotFoundException otherwise if the dll is not on the probe paths).
            string preload = PreloadNativeLibrary();
            if (preload == null)
            {
                _initError = "Could not load openvr_api.dll (looked next to the mod, in the game root, and in the SteamVR runtime).";
                return false;
            }

            if (!OpenVR.IsRuntimeInstalled() || !OpenVR.IsHmdPresent())
            {
                _initError = DescribeEnvironment();
                return false;
            }

            try
            {
                EVRInitError err = EVRInitError.None;
                _system = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
                if (err != EVRInitError.None || _system == null)
                {
                    _initError = "OpenVR init failed: " + err + " (" + (int)err + ")";
                    if (err == EVRInitError.Init_HmdNotFound ||
                        err == EVRInitError.Init_HmdNotFoundPresenceFailed)
                    {
                        _initError += " - the headset dropped out while initializing. Make sure it is " +
                            "connected in VR mode (Quest Link / Steam Link / Virtual Desktop / ALVR), " +
                            "not in a monitor mode such as Meta Virtual Display.";
                    }
                    return false;
                }

                _compositor = OpenVR.Compositor;
                if (_compositor == null)
                {
                    _initError = "OpenVR compositor interface is unavailable.";
                    OpenVR.Shutdown();
                    _system = null;
                    return false;
                }

                _compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseStanding);

                _system.GetRecommendedRenderTargetSize(ref _recWidth, ref _recHeight);

                // Vertical FOV of the left eye, derived from the OpenVR projection matrix.
                // OpenVR's P[1][1] = 1/tan(fovY/2), i.e. HmdMatrix44_t field m5.
                HmdMatrix44_t proj = _system.GetProjectionMatrix(EVREye.Eye_Left, 0.1f, 100f);
                if (proj.m5 > 0.0001f)
                {
                    _eyeVFovDeg = 2f * Mathf.Atan(1f / proj.m5) * Mathf.Rad2Deg;
                    _eyeAspect = (proj.m5 > 0.0001f) ? (proj.m0 / proj.m5) : 1f;
                }

                ResolveEyeGeometry();

                _initialized = true;
                _initError = null;

                MelonLoader.MelonLogger.Msg(string.Format(
                    "[YARG-VR] OpenVR initialized. Recommended RT size: {0}x{1}, eye vFOV: {2:F1} deg, aspect {3:F2}",
                    _recWidth, _recHeight, _eyeVFovDeg, _eyeAspect));
                return true;
            }
            catch (Exception e)
            {
                _initError = "OpenVR init threw: " + e.Message;
                return false;
            }
        }

        public static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            _initialized = false;
            _compositor = null;
            _system = null;
            try
            {
                OpenVR.Shutdown();
            }
            catch
            {
                // Ignore shutdown races with SteamVR quitting.
            }
            MelonLoader.MelonLogger.Msg("[YARG-VR] OpenVR shut down.");
        }

        #endregion

        #region Tracking

        // Per-eye geometry (eye-to-head transform, converted to Unity space). Used to place the
        // two UI eye cameras and to offset the venue camera / highway reprojection per eye.
        private static Vector3 _eyeOffsetL = Vector3.zero;
        private static Vector3 _eyeOffsetR = Vector3.zero;
        private static Quaternion _eyeRotL = Quaternion.identity;
        private static Quaternion _eyeRotR = Quaternion.identity;
        private static bool _eyeGeometryResolved;

        public static Vector3 EyeOffsetLeft { get { return _eyeOffsetL; } }
        public static Vector3 EyeOffsetRight { get { return _eyeOffsetR; } }
        public static Quaternion EyeRotationLeft { get { return _eyeRotL; } }
        public static Quaternion EyeRotationRight { get { return _eyeRotR; } }
        public static bool HasEyeGeometry { get { return _eyeGeometryResolved; } }

        private static void ResolveEyeGeometry()
        {
            if (_eyeGeometryResolved)
            {
                return;
            }

            _eyeGeometryResolved = true;
            try
            {
                Matrix4x4 l = OpenVrToUnity(_system.GetEyeToHeadTransform(EVREye.Eye_Left));
                Matrix4x4 r = OpenVrToUnity(_system.GetEyeToHeadTransform(EVREye.Eye_Right));

                _eyeOffsetL = new Vector3(l.m03, l.m13, l.m23);
                _eyeRotL = Quaternion.LookRotation(l.GetColumn(2), l.GetColumn(1));
                _eyeOffsetR = new Vector3(r.m03, r.m13, r.m23);
                _eyeRotR = Quaternion.LookRotation(r.GetColumn(2), r.GetColumn(1));

                float ipd = Vector3.Distance(_eyeOffsetL, _eyeOffsetR) * 1000f;
                MelonLoader.MelonLogger.Msg(string.Format(
                    "[YARG-VR] Eye offsets: L({0:F3},{1:F3},{2:F3}) R({3:F3},{4:F3},{5:F3}) m, IPD {6:F0} mm - true stereo enabled.",
                    _eyeOffsetL.x, _eyeOffsetL.y, _eyeOffsetL.z,
                    _eyeOffsetR.x, _eyeOffsetR.y, _eyeOffsetR.z, ipd));
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Warning("[YARG-VR] GetEyeToHeadTransform failed (" + e.Message +
                    ") - eye offsets fall back to zero (stereo degrades to mono).");
            }
        }

        /// <summary>Logs a pose problem, but at most once per ~30 s for an identical message.</summary>
        private static void LogPoseProblem(string message)
        {
            if (string.Equals(_lastPoseProblem, message, StringComparison.Ordinal) &&
                Time.frameCount - _lastPoseProblemFrame < 1800)
            {
                return;
            }

            _lastPoseProblem = message;
            _lastPoseProblemFrame = Time.frameCount;
            MelonLoader.MelonLogger.Error("[YARG-VR] " + message);
        }

        /// <summary>
        /// Fetches fresh poses from the compositor and returns the HMD pose in Unity space
        /// (standing origin, +Z forward). Returns false when tracking is unavailable this frame.
        /// Never throws - a broken compositor degrades to "no head tracking", not log spam.
        /// </summary>
        public static bool TryGetHmdPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (!_initialized || _compositor == null)
            {
                return false;
            }

            EVRCompositorError err;
            try
            {
                // Real arrays for BOTH parameters - see the note on _gamePoses above.
                err = _compositor.WaitGetPoses(_renderPoses, _gamePoses);
            }
            catch (Exception e)
            {
                LogPoseProblem("WaitGetPoses threw " + e.GetType().Name + ": " + e.Message);
                return false;
            }

            if (err != EVRCompositorError.None)
            {
                LogPoseProblem("WaitGetPoses failed: " + err + " (" + (int)err + ")");
                return false;
            }

            TrackedDevicePose_t pose = _renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!pose.bDeviceIsConnected || !pose.bPoseIsValid)
            {
                return false;
            }

            if (!_loggedFirstPose)
            {
                _loggedFirstPose = true;
                MelonLoader.MelonLogger.Msg("[YARG-VR] *** HMD tracking is live - head pose received. ***");
            }

            Matrix4x4 m = OpenVrToUnity(pose.mDeviceToAbsoluteTracking);
            position = new Vector3(m.m03, m.m13, m.m23);
            rotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
            return true;
        }

        /// <summary>
        /// Change-of-basis from OpenVR space (X right, Y up, -Z forward) into Unity space
        /// (X right, Y up, +Z forward): M_unity = B * M_openvr * B, with B = diag(1, 1, -1).
        /// HmdMatrix34_t is row-major 3x4 (m0..m11), translation in column 3 (m3, m7, m11).
        ///
        /// v1.3.1 CRITICAL FIX - the rotation block must be copied ROW-BY-ROW (m01 = e.m1,
        /// m10 = e.m4, ...). v1.3.0 and earlier accidentally built the TRANSPOSE of the
        /// rotation (m01 = e.m4, m10 = e.m1, ...), and for pure rotations transpose ==
        /// INVERSE, so every HMD pose handed to the rig carried the head rotation flipped
        /// on ALL axes: looking around ran reversed (left=right, up=down) and the world
        /// appeared glued to the view. At the neutral pose the matrix is identity, which is
        /// why the screen still looked correctly centered right after a recenter - the bug
        /// only showed up once the head turned. Verified against the derivation
        /// M_u = B * M_o * B (column j of M_u = B * column j of M_o, column 2 also negated)
        /// and the SteamVR Unity plugin's reference conversion.
        /// </summary>
        private static Matrix4x4 OpenVrToUnity(HmdMatrix34_t e)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = e.m0;  m.m01 = e.m1;  m.m02 = -e.m2;   m.m03 = e.m3;
            m.m10 = e.m4;  m.m11 = e.m5;  m.m12 = -e.m6;   m.m13 = e.m7;
            m.m20 = -e.m8; m.m21 = -e.m9; m.m22 = e.m10;   m.m23 = -e.m11;
            return m;
        }

        #endregion

        #region Submission

        private static VRTextureBounds_t _flipBounds;
        private static VRTextureBounds_t _fullBounds;
        private static bool _boundsReady;
        private static Texture_t _tex;
        private static ETextureType _textureType = ETextureType.Invalid;
        private static string _lastSubmitProblem;      // last logged problem message (log on change only)
        private static bool _loggedFirstL;
        private static bool _loggedFirstR;
        private static long _successfulL;
        private static long _successfulR;
        private static int _lastSuccessfulSubmitFrame = -1;   // Time.frameCount of last accepted frame
        private static int _lastAttemptFrameL = -1;           // per-eye once-per-frame guards
        private static int _lastAttemptFrameR = -1;
        private static int _lastHandoffFrame = -1;
        private static int _lastHeartbeatFrame;
        private static int _submitAttempts;

        /// <summary>
        /// Vertically flips the texture submitted to the compositor (via VRTextureBounds_t).
        /// D3D/D3D12/Vulkan render targets store their rows top-down, but the OpenVR compositor
        /// treats row 0 as the BOTTOM scanline - without the flip the entire VR view appears
        /// upside down (YARG renders with D3D11). Defaults to true; the SubmissionFlip
        /// preference can turn it off for exotic setups.
        /// </summary>
        public static bool FlipTextureBounds { get; set; }

        /// <summary>How many frames the compositor has actually accepted (both eyes combined).</summary>
        public static long SuccessfulSubmits { get { return _successfulL + _successfulR; } }

        /// <summary>True when the given eye was already submitted for the current Time.frameCount.</summary>
        public static bool SubmittedThisFrame(EVREye eye)
        {
            return eye == EVREye.Eye_Left
                ? _lastAttemptFrameL == Time.frameCount
                : _lastAttemptFrameR == Time.frameCount;
        }

        /// <summary>
        /// Frames elapsed since the compositor last accepted a frame (int.MaxValue if never).
        /// The scene rig uses this as a watchdog trigger.
        /// </summary>
        public static int FramesSinceSuccessfulSubmit
        {
            get { return _lastSuccessfulSubmitFrame < 0 ? int.MaxValue : Time.frameCount - _lastSuccessfulSubmitFrame; }
        }

        /// <summary>Logs a submission problem, but only when the message changes (no per-frame spam).</summary>
        private static void LogSubmitProblem(string message)
        {
            if (string.Equals(_lastSubmitProblem, message, StringComparison.Ordinal))
            {
                return;
            }

            _lastSubmitProblem = message;
            MelonLoader.MelonLogger.Error("[YARG-VR] " + message);
        }

        private static bool ResolveTextureType()
        {
            if (_textureType != ETextureType.Invalid)
            {
                return true;
            }

            switch (SystemInfo.graphicsDeviceType)
            {
                case GraphicsDeviceType.Direct3D11:
                    _textureType = ETextureType.DirectX;
                    return true;
                case GraphicsDeviceType.Direct3D12:
                    _textureType = ETextureType.DirectX12;
                    return true;
                case GraphicsDeviceType.Vulkan:
                    _textureType = ETextureType.Vulkan;
                    return true;
                case GraphicsDeviceType.OpenGLCore:
                case GraphicsDeviceType.OpenGLES3:
                    _textureType = ETextureType.OpenGL;
                    return true;
                default:
                    LogSubmitProblem("Unsupported graphics API for OpenVR submission: " +
                        SystemInfo.graphicsDeviceType);
                    return false;
            }
        }

        private static void EnsureBounds()
        {
            if (_boundsReady)
            {
                return;
            }

            _boundsReady = true;

            _fullBounds.uMin = 0f; _fullBounds.vMin = 0f;
            _fullBounds.uMax = 1f; _fullBounds.vMax = 1f;

            // D3D-style and Vulkan RTs need the vertical flip; GL RTs are already bottom-up.
            bool flip = FlipTextureBounds &&
                (_textureType == ETextureType.DirectX ||
                 _textureType == ETextureType.DirectX12 ||
                 _textureType == ETextureType.Vulkan);

            _flipBounds.uMin = 0f; _flipBounds.uMax = 1f;
            _flipBounds.vMin = flip ? 1f : 0f;
            _flipBounds.vMax = flip ? 0f : 1f;

            if (flip)
            {
                MelonLoader.MelonLogger.Msg("[YARG-VR] Submitting with vertically flipped texture bounds " +
                    "(" + _textureType + " row-order correction - fixes the upside-down image).");
            }
        }

        /// <summary>
        /// Submits one eye's RenderTexture to the compositor. Safe to call from multiple paths
        /// per frame - the per-eye once-per-frame guard makes duplicate calls no-ops.
        /// </summary>
        public static void SubmitEye(EVREye eye, RenderTexture rt)
        {
            if (!_initialized || _compositor == null || rt == null)
            {
                return;
            }

            bool isLeft = eye == EVREye.Eye_Left;
            if (isLeft ? _lastAttemptFrameL == Time.frameCount : _lastAttemptFrameR == Time.frameCount)
            {
                return; // another submission path already handled this eye this frame
            }
            if (isLeft) _lastAttemptFrameL = Time.frameCount;
            else _lastAttemptFrameR = Time.frameCount;

            try
            {
                if (!ResolveTextureType())
                {
                    return;
                }
                EnsureBounds();

                IntPtr nativeTex = rt.GetNativeTexturePtr();
                if (nativeTex == IntPtr.Zero)
                {
                    // Never silent: a zero pointer means the compositor can never accept anything.
                    LogSubmitProblem("Eye RenderTexture native pointer is NULL (eye=" + eye +
                        ", gfx=" + SystemInfo.graphicsDeviceType + ", IsCreated=" + rt.IsCreated() +
                        ") - cannot submit.");
                    return;
                }

                _tex.handle = nativeTex;
                _tex.eType = _textureType;
                _tex.eColorSpace = EColorSpace.Auto;

                _submitAttempts++;

                VRTextureBounds_t bounds = FlipTextureBounds ? _flipBounds : _fullBounds;
                EVRCompositorError e = _compositor.Submit(eye, ref _tex, ref bounds, EVRSubmitFlags.Submit_Default);

                if (e != EVRCompositorError.None)
                {
                    LogSubmitProblem(string.Format(
                        "Compositor.Submit({0}) FAILED: {1} ({2}) (gfx={3}, native=0x{4:X}, type={5})",
                        eye, e, (int)e, SystemInfo.graphicsDeviceType, nativeTex.ToInt64(), _textureType));
                    return;
                }

                if (isLeft)
                {
                    _successfulL++;
                    if (!_loggedFirstL)
                    {
                        _loggedFirstL = true;
                        MelonLoader.MelonLogger.Msg("[YARG-VR] *** Compositor accepted the first LEFT eye frame. ***");
                    }
                }
                else
                {
                    _successfulR++;
                    if (!_loggedFirstR)
                    {
                        _loggedFirstR = true;
                        MelonLoader.MelonLogger.Msg("[YARG-VR] *** Compositor accepted the first RIGHT eye frame - stereo is live. ***");
                    }
                }
                _lastSuccessfulSubmitFrame = Time.frameCount;
                _lastSubmitProblem = null;

                if (_loggedFirstL && _loggedFirstR && Time.frameCount - _lastHeartbeatFrame > 1800)
                {
                    // Roughly every 30 seconds, confirm the pipeline is still alive.
                    _lastHeartbeatFrame = Time.frameCount;
                    MelonLoader.MelonLogger.Msg("[YARG-VR] Submit heartbeat: " + _successfulL + " left / " +
                        _successfulR + " right frames accepted by the compositor so far.");
                }

                // Hand the presented frame to the compositor ASAP once BOTH eyes are in
                // (lower latency on D3D11; calling it once per frame is the intended usage).
                if (_lastAttemptFrameL == Time.frameCount && _lastAttemptFrameR == Time.frameCount &&
                    _lastHandoffFrame != Time.frameCount)
                {
                    _lastHandoffFrame = Time.frameCount;
                    _compositor.PostPresentHandoff();
                }
            }
            catch (Exception e)
            {
                LogSubmitProblem("Submit(" + eye + ") threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// One-line-per-field dump used by the scene rig's watchdog when no frame has reached
        /// the compositor. Tells us exactly which stage of render->submit is broken.
        /// </summary>
        public static string GetSubmitDiagnostics()
        {
            return "initialized=" + _initialized +
                ", compositor=" + (_compositor != null) +
                ", gfxApi=" + SystemInfo.graphicsDeviceType +
                ", textureType=" + _textureType +
                ", flip=" + FlipTextureBounds +
                ", submitAttempts=" + _submitAttempts +
                ", successfulL=" + _successfulL +
                ", successfulR=" + _successfulR +
                ", framesSinceSuccess=" + (FramesSinceSuccessfulSubmit == int.MaxValue ? "never" : FramesSinceSuccessfulSubmit.ToString()) +
                ", lastProblem=" + (_lastSubmitProblem ?? "none");
        }

        #endregion
    }
}
