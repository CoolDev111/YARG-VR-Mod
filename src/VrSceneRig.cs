using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.Video;
using Valve.VR;

namespace YargVr
{
    /// <summary>
    /// Per-scene VR rig. Created when the "Gameplay" scene loads (and OpenVR is ready),
    /// destroyed when the scene unloads or VR is toggled off.
    ///
    /// How the takeover works (see RESEARCH.md for the full story):
    ///
    ///  1. YARG's entire gameplay view - venue background, note highways, HUD, pause menu -
    ///     is composed on Screen Space Overlay uGUI canvases. "Venue Output" and the
    ///     "Highways Output" RawImages display offscreen RenderTextures rendered by YARG's
    ///     own cameras.
    ///
    ///  2. This rig converts those root canvases into a WORLD-SPACE "floating screen" that
    ///     is anchored at a fixed point in the play space (placed on enter / recenter) and
    ///     then LOCKED in the room like a real screen - it does not follow your view. F9
    ///     re-places it in front of you. Because the screen now exists as a real object in
    ///     space, it has genuine depth and parallax instead of being glued to your face.
    ///
    ///  3. Two mod-created "eye cameras" (OpenVR-sized render targets) view the scene -
    ///     YARG's 3-D world AND that screen - from the actual left/right eye positions
    ///     (real per-eye offsets from OpenVR) and submit their textures to the compositor
    ///     per eye - TRUE stereo. The monitor gets the same image via a desktop-mirror
    ///     camera, so the flat screen always shows exactly what the headset sees.
    ///
    ///  4. Stereo inside the screen content:
    ///       - Venue (stage): YARG renders the stage with its own camera inside a begin-
    ///         cameraRendering hook (which also sets YARG's venue shader state and enqueues
    ///         its alpha-fix pass). Right AFTER YARG's hook runs, we re-render the SAME
    ///         camera for the right eye (offset by IPD) via URP's RenderSingleCamera, into
    ///         our own texture. While each eye camera draws the canvas, we swap the
    ///         "Venue Output" RawImage texture between YARG's (left) and ours (right).
    ///       - Note highways: YARG's highway reprojection shader reads the GLOBAL matrices
    ///         _YargCamViewMatrices/_YargCamInvViewMatrices (set once per frame by YARG's
    ///         highway camera hook). We swap those globals per eye with IPD-offset copies
    ///         while each eye camera draws, then restore the originals.
    ///
    ///  5. YARG's venue camera is driven 1:1 by the HMD (anchored at the pose YARG chose;
    ///     camera cuts re-anchor). The desktop window keeps showing the normal game view;
    ///     SteamVR's desktop mirror shows the HMD image.
    /// </summary>
    internal sealed class VrSceneRig : IDisposable
    {
        private readonly VrSettings _settings;

        #region Eye cameras (stereo render of the world-space screen)

        private Camera _eyeCamL;
        private Camera _eyeCamR;
        private RenderTexture _rtL;
        private RenderTexture _rtR;
        private bool _hooked;

        // v1.3.0 ROOM ROOT ("the invisible cube"): one persistent object that defines the
        // play space. The screen stack, pop-out HUD and visualizer ring are all positioned
        // RELATIVE TO IT (root-local constants), and the root moves ONLY on placement (first
        // valid pose / F9) - never per frame - so "the world follows my head" is structurally
        // impossible: head motion cannot move an object that nothing derives from the head.
        private Transform _roomRoot;
        private bool _screenAnchorPlaced;
        private Vector3 _screenAnchorLocal = new Vector3(0f, 0f, 2f); // screen center, root space
        private Vector3 _hudAnchorLocal = new Vector3(0f, 0f, 1.2f);  // pop-out HUD, root space

        // Pop-out HUD plane: HUD/pause menu live on their own floating plane closer to the
        // player than the game screen, which gives the screen real layered depth.
        private GameObject _hudPlane;
        private Canvas _hudPlaneCanvas;
        private readonly List<ReparentRecord> _hudReparented = new List<ReparentRecord>();
        private bool _hudPopAttempted;

        private struct ReparentRecord
        {
            public RectTransform Rect;
            public Transform Parent;
            public int SiblingIndex;
        }

        // Audio visualizer ring (bars around the play space, driven by the song's spectrum).
        private GameObject _visualizer;
        private Transform[] _visBars;
        private Material[] _visMats;
        private Color[] _visBase;
        private float[] _visAmp;
        private float[] _visSpectrum; // AudioListener fallback layout (256 raw bins)
        private float[] _visBass;     // BASS FFT magnitudes (BassSpectrum.BinCount)
        private int[] _visBinLo;
        private int[] _visBinHi;
        private const int VisBarCount = 48;
        private const float VisRadius = 2.7f;
        private const float VisMaxHeight = 1.7f;
        private bool _visBassOkLogged;
        private bool _visBassMissingLogged;
        private MeshRenderer[] _visRenderers; // for per-bar occlusion (hide bars behind screens)

        // Desktop mirror: renders the left-eye view on the monitor (letterboxed), so the flat
        // screen always shows exactly what the headset sees. Rendered LAST via camera depth.
        private Camera _mirrorCam;
        private Transform _mirrorQuad;
        private Material _mirrorMat;

        // World anchoring: maps the player's head onto YARG's own camera pose, so the 3-D
        // environment (menu background room, highway room) lines up exactly the way the game
        // authored it - instead of floating around the raw tracking origin at an arbitrary
        // facing (which read as "the background is a few cm from the menu" and "left/right
        // feel reversed"). The screen/HUD/visualizer are placed inside this anchored frame.
        private Camera _worldCam;
        private Camera _worldCamCandidate;
        private Vector3 _worldCamAuthoredPos;
        private Quaternion _worldCamAuthoredRot = Quaternion.identity;
        private Vector3 _worldAnchorPos;
        private Quaternion _worldAnchorRot = Quaternion.identity;
        private bool _worldAnchorActive;
        private static Type _menuBgType;
        private static FieldInfo _menuBgCameraField;
        private static FieldInfo _menuBgContainerField;
        private static bool _menuBgResolved;
        private static bool _menuBgDiagLogged;
        private static UnityEngine.Object _menuBgComponent;
        private static Camera _menuBgCamera;   // last menu-background camera found (menu-surround mode)

        // v1.3.3 "menu surround": YARG's menu background is NOT a room - it is one camera
        // (skybox clear) plus a single 2 x 1 m quad ("Wall") carrying the animated
        // "Unlit/MenuBackground" gradient material, and MainMenuBackground.Update() lerps the
        // container back to (0, 0.5, 0) EVERY FRAME (so v1.3.2's one-shot re-anchor was undone
        // immediately, and there was no environment geometry to stand inside anyway). The eye
        // cameras also clear solid black (no skybox), leaving a void around the menu screen.
        // Fix: build our own inward-facing sphere around the player that uses YARG's OWN menu
        // gradient material (it is UV-based, so it wraps any mesh), shown in menu scenes only.
        private GameObject _menuSurroundGo;
        private Mesh _menuSurroundMesh;
        private Material _menuSurroundMat;
        private bool _menuSurroundMatOwned;
        private static bool _menuSurroundFailedLogged;

        // Optional pose debugging (PoseDebug pref): throttled pose logging to diagnose
        // "attached to the player" reports with hard numbers.
        private float _nextPoseDebug;

        // Watchdog observability.
        private int _beginEvents;
        private int _eyeEndEventsL;
        private int _eyeEndEventsR;
        private int _backstopSubmits;
        private float _watchdogGraceUntil;
        private bool _watchdogDiagnosticsLogged;

        #endregion

        #region Canvas conversion (world-space floating screens)

        private struct CanvasSnapshot
        {
            public Canvas Canvas;
            public RenderMode Mode;
            public Camera WorldCamera;
            public float PlaneDistance;
            public Vector3 LocalScale;
            public float ScaleFactor;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public Vector2 Pivot;
        }

        private readonly List<CanvasSnapshot> _converted = new List<CanvasSnapshot>();

        private struct Billboard
        {
            public Canvas Canvas;
            public float Offset; // meters closer to the head than the anchor (stacking order)
        }

        private readonly List<Billboard> _billboards = new List<Billboard>();
        private int _nextCanvasScanFrame;

        #endregion

        #region Venue takeover + per-eye venue rendering

        private struct VenueSnapshot
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov;
        }

        private readonly Dictionary<Camera, VenueSnapshot> _venueAuthored =
            new Dictionary<Camera, VenueSnapshot>();

        private Camera _venueCam;
        private Vector3 _anchorPos;
        private Quaternion _anchorRot = Quaternion.identity;
        private bool _anchored;

        // Per-eye venue poses, recomputed every LateTick; the LEFT pose is applied to the
        // venue camera directly, the RIGHT pose is used inside the render hook.
        private Vector3 _venueLeftPos;
        private Quaternion _venueLeftRot = Quaternion.identity;
        private Vector3 _venueRightPos;
        private Quaternion _venueRightRot = Quaternion.identity;

        // "Venue Output" RawImage texture swap (left = YARG's texture, right = ours).
        private RawImage _venueOutput;
        private RenderTexture _yargVenueTexture;
        private RenderTexture _venueRightRt;
        private bool _venueStereoBroken;
        private static bool _venueStereoWarned;
        private static bool _venueOutputWarned;

        // Dedicated RIGHT-EYE venue camera. YARG's VenueCameraRenderer enables/disables its own
        // camera and resets its shader state inside per-render hooks, so mutating that camera
        // mid-hook (or re-rendering it) would desynchronize YARG's own state. Instead we keep a
        // clone that YARG's hooks ignore entirely: it is rendered once per frame via URP's
        // RenderSingleCamera while YARG's venue shader globals are live, and never by the
        // normal pipeline (enabled = false).
        private GameObject _venueCloneGo;
        private Camera _venueClone;
        private Component _venueCloneUrpData;
        private static Type _urpCameraDataType;
        private static bool _urpCameraDataTypeResolved;
        private static readonly string[] _urpDataCopyProps =
        {
            "renderPostProcessing", "renderShadows", "stopNaN", "dithering",
            "antialiasing", "antialiasingQuality",
        };

        // Highway reprojection stereo (global matrix swap per eye).
        private const string HwViewName = "_YargCamViewMatrices";
        private const string HwInvViewName = "_YargCamInvViewMatrices";
        private bool _highwayStereoBroken;
        private static bool _highwayStereoWarned;
        private bool _hwGlobalsCaptured;
        private Matrix4x4[] _hwViewOrig, _hwInvViewOrig;
        private Matrix4x4[] _hwViewL, _hwInvViewL, _hwViewR, _hwInvViewR;

        // URP's RenderSingleCamera, resolved once via reflection (keeps the csproj lean -
        // the type lives in Unity.RenderPipelines.Universal.Runtime which we don't reference).
        private static MethodInfo _renderSingleCameraMethod;
        private static bool _renderSingleCameraResolved;

        #endregion

        public bool IsActive { get { return _eyeCamL != null; } }

        public VrSceneRig(VrSettings settings)
        {
            _settings = settings;
        }

        #region Scene enter / leave

        public void Enter()
        {
            if (IsActive)
            {
                return;
            }

            try
            {
                CreateEyeCameras();
                EnsureMirrorCamera();
                HookPipeline();
                ConvertCanvases();
                FindVenueOutput();
                CreateVenueClone();
                RefreshCameras();

                _venueCam = null;
                _anchored = false;
                _venueAuthored.Clear();
                _screenAnchorPlaced = false;

                MelonLoader.MelonLogger.Msg("[YARG-VR] VR rig active in Gameplay scene " +
                    "(stereo per-eye " + _rtL.width + "x" + _rtL.height +
                    ", gfx=" + SystemInfo.graphicsDeviceType +
                    ", pipeline=" + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                        ? "SRP/URP"
                        : "Built-in") + ").");
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Error("[YARG-VR] Failed to enter VR mode: " + e);
                Leave();
            }
        }

        public void Leave()
        {
            DestroyMenuSurround();
            DestroyMirror();
            ReleaseVideoHooks(restorePlayers: true);
            RestoreHudPlane();
            DestroyVisualizer();
            UnhookPipeline();
            RestoreCanvases();
            RestoreVenueCamera();
            _venueCam = null;
            _anchored = false;
            _venueAuthored.Clear();
            _screenAnchorPlaced = false;
            _worldCam = null;
            _worldCamCandidate = null;
            _worldAnchorActive = false;

            if (_roomRoot != null)
            {
                UnityEngine.Object.Destroy(_roomRoot.gameObject);
                _roomRoot = null;
            }

            if (_venueOutput != null && _yargVenueTexture != null &&
                ReferenceEquals(_venueOutput.texture, _venueRightRt))
            {
                _venueOutput.texture = _yargVenueTexture;
            }
            _venueOutput = null;
            _yargVenueTexture = null;

            if (_venueRightRt != null)
            {
                _venueRightRt.Release();
                UnityEngine.Object.Destroy(_venueRightRt);
                _venueRightRt = null;
            }

            if (_venueCloneGo != null)
            {
                _venueClone = null;
                _venueCloneUrpData = null;
                UnityEngine.Object.Destroy(_venueCloneGo);
                _venueCloneGo = null;
            }

            if (_eyeCamL != null)
            {
                UnityEngine.Object.Destroy(_eyeCamL.gameObject);
                _eyeCamL = null;
            }
            if (_eyeCamR != null)
            {
                UnityEngine.Object.Destroy(_eyeCamR.gameObject);
                _eyeCamR = null;
            }

            if (_rtL != null)
            {
                _rtL.Release();
                UnityEngine.Object.Destroy(_rtL);
                _rtL = null;
            }
            if (_rtR != null)
            {
                _rtR.Release();
                UnityEngine.Object.Destroy(_rtR);
                _rtR = null;
            }
        }

        public void Dispose()
        {
            Leave();
        }

        /// <summary>
        /// Called when YARG loads another scene while the rig is alive (the rig now persists
        /// across scenes so the screen is always up). Forces a canvas rescan so the new scene's
        /// UI is picked up immediately.
        /// </summary>
        public void OnSceneChanged()
        {
            _nextCanvasScanFrame = 0;

            // Scene-local HUD plane objects died with the old scene - allow a rebuild.
            _hudPopAttempted = false;
            _hudReparented.Clear();
            _hudPlane = null;
            _hudPlaneCanvas = null;

            // The old scene's cameras died - re-bind the world anchor to the new scene's
            // camera and give the screen a fresh placement (scene-granular, so no drift).
            _worldCam = null;
            _worldCamCandidate = null;
            _worldAnchorActive = false;
            _screenAnchorPlaced = false;
            _menuBgCamera = null;
            _menuBgComponent = null;
        }

        private void RestoreVenueCamera()
        {
            foreach (KeyValuePair<Camera, VenueSnapshot> kv in _venueAuthored)
            {
                if (kv.Key == null)
                {
                    continue;
                }

                kv.Key.transform.SetPositionAndRotation(kv.Value.Position, kv.Value.Rotation);
                kv.Key.fieldOfView = kv.Value.Fov;
            }
        }

        #endregion

        #region Setup helpers

        private void CreateEyeCameras()
        {
            float ss = Mathf.Clamp(_settings.Supersample.Value, 0.5f, 2.5f);
            int w = Mathf.Max(64, Mathf.RoundToInt(OpenVrRuntime.RecommendedWidth * ss));
            int h = Mathf.Max(64, Mathf.RoundToInt(OpenVrRuntime.RecommendedHeight * ss));

            _rtL = CreateEyeTexture("YARG-VR Eye L", w, h);
            _rtR = CreateEyeTexture("YARG-VR Eye R", w, h);

            float fov = _settings.HudFov.Value > 1f
                ? Mathf.Clamp(_settings.HudFov.Value, 30f, 170f)
                : OpenVrRuntime.EyeVFovDeg;

            _eyeCamL = MakeEyeCamera("YARG-VR Eye Camera L", _rtL, fov, 1000f);
            _eyeCamR = MakeEyeCamera("YARG-VR Eye Camera R", _rtR, fov, 1001f);

            // The rig persists across scene changes so the screen is always up in the headset.
            UnityEngine.Object.DontDestroyOnLoad(_eyeCamL.gameObject);
            UnityEngine.Object.DontDestroyOnLoad(_eyeCamR.gameObject);
        }

        private static RenderTexture CreateEyeTexture(string name, int w, int h)
        {
            RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.name = name;
            rt.Create();
            return rt;
        }

        private static Camera MakeEyeCamera(string name, RenderTexture target, float fov, float depth)
        {
            GameObject go = new GameObject(name);
            Camera cam = go.AddComponent<Camera>();

            cam.cullingMask = 1 << 5; // placeholder - RefreshEyeCullingMasks widens this
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 50f;
            cam.depth = depth;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.aspect = (float)target.width / target.height;
            cam.fieldOfView = fov;
            cam.targetTexture = target;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.enabled = true; // URP renders it automatically (base camera with targetTexture)
            return cam;
        }

        /// <summary>
        /// Per-scene camera refresh, run on enter, on scene change and every ~2 s:
        ///
        /// 1. Widens the eye cameras' culling mask so YARG's 3-D world renders around the
        ///    floating screen (copied from YARG's own world camera, plus the UI layer, minus
        ///    the desktop-mirror layer).
        /// 2. Picks the world-anchor camera: "Camera (No Venue)" (gameplay highway room),
        ///    else the main menu's environment camera (reflected from MainMenuBackground),
        ///    else Camera.main.
        /// </summary>
        private void RefreshCameras()
        {
            if (_eyeCamL == null || _eyeCamR == null)
            {
                return;
            }

            Camera named = null;
            Camera main = null;
            int union = 0;
            Camera[] cams = UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null || c == _eyeCamL || c == _eyeCamR || c == _mirrorCam || c == _venueClone)
                {
                    continue;
                }

                if (c.name == "Camera (No Venue)")
                {
                    named = c;
                    break;
                }
                if (main == null && c.CompareTag("MainCamera"))
                {
                    main = c;
                }
                union |= c.cullingMask;
            }

            // 1) Eye culling mask.
            int mask = 1 << 5;
            if (_settings.ShowWorld.Value)
            {
                if (named != null) mask = named.cullingMask;
                else if (main != null) mask = main.cullingMask;
                else if (union != 0) mask = union;
            }
            mask |= (1 << 5);   // "UI" layer - world-space canvases + visualizer bars
            mask &= ~(1 << 2);  // "Ignore Raycast" - reserved for the desktop-mirror quad
            _eyeCamL.cullingMask = mask;
            _eyeCamR.cullingMask = mask;

            // 2) World-anchor camera candidate (bound by LateTick).
            _worldCamCandidate = named;
            if (_worldCamCandidate == null)
            {
                _worldCamCandidate = FindMenuBackgroundCamera();
            }
            if (_worldCamCandidate == null)
            {
                _worldCamCandidate = main;
            }
        }

        /// <summary>
        /// The main menu's environment camera (the one that renders the background room).
        /// It is not tagged MainCamera, so it is reached through MainMenuBackground's
        /// serialized fields. Verified against YARG 0.15's Assembly-CSharp metadata:
        ///   YARG.Menu.Main.MainMenuBackground { Transform _cameraContainer; Camera _camera; }
        ///
        /// v1.3.2 hardening: the v1.2.2 lookup silently returned null at runtime (the world
        /// anchor log fell back to 'Main Camera' and never mentioned why). Now it searches
        /// inactive objects too, falls back to the container's camera when _camera is unset,
        /// and prints ONE diagnostic line stating exactly what was resolved and what wasn't,
        /// so a failed bind is visible in the log instead of silent.
        /// </summary>
        private static Camera FindMenuBackgroundCamera()
        {
            if (!_menuBgResolved)
            {
                _menuBgResolved = true;
                try
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length; i++)
                    {
                        _menuBgType = assemblies[i].GetType("YARG.Menu.Main.MainMenuBackground");
                        if (_menuBgType != null)
                        {
                            break;
                        }
                    }
                    if (_menuBgType != null)
                    {
                        _menuBgCameraField = _menuBgType.GetField("_camera",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        _menuBgContainerField = _menuBgType.GetField("_cameraContainer",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }
                catch
                {
                    // Reflection is best-effort.
                }

                MelonLoader.MelonLogger.Msg("[YARG-VR] Menu background reflection: type " +
                    (_menuBgType != null ? "found" : "NOT FOUND") + ", _camera field " +
                    (_menuBgCameraField != null ? "found" : "NOT FOUND") + ", _cameraContainer field " +
                    (_menuBgContainerField != null ? "found" : "NOT FOUND") + ".");
            }

            _menuBgCamera = null;
            _menuBgComponent = null;
            if (_menuBgType == null || _menuBgCameraField == null)
            {
                return null;
            }

            try
            {
                // Include inactive: the background object can be disabled for a frame during
                // scene transitions, and we only READ its pose (never render from it).
                UnityEngine.Object[] comps = UnityEngine.Object.FindObjectsByType(_menuBgType,
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (comps == null || comps.Length == 0)
                {
                    if (!_menuBgDiagLogged)
                    {
                        _menuBgDiagLogged = true;
                        MelonLoader.MelonLogger.Msg("[YARG-VR] No MainMenuBackground component in this scene " +
                            "(normal outside the main menu) - world anchor falls back to YARG's main camera.");
                    }
                    return null;
                }

                _menuBgComponent = comps[0];
                _menuBgCamera = _menuBgCameraField.GetValue(comps[0]) as Camera;
                if (_menuBgCamera == null)
                {
                    // _camera can be unset until the component initializes - try the container.
                    Transform container = _menuBgContainerField != null
                        ? _menuBgContainerField.GetValue(comps[0]) as Transform
                        : null;
                    if (container != null)
                    {
                        _menuBgCamera = container.GetComponentInChildren<Camera>(true);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_menuBgDiagLogged)
                {
                    _menuBgDiagLogged = true;
                    MelonLoader.MelonLogger.Warning("[YARG-VR] Menu background camera lookup failed: " + e.Message);
                }
            }
            return _menuBgCamera;
        }

        /// <summary>Binds (or re-binds) the world anchor when YARG's world camera changes.</summary>
        private void BindWorldCamera(Vector3 hmdPos, Quaternion hmdFlat)
        {
            if (_worldCamCandidate == _worldCam)
            {
                return; // includes both-null
            }

            _worldCam = _worldCamCandidate;
            if (_worldCam == null)
            {
                _worldAnchorActive = false;
                return;
            }

            _worldCamAuthoredPos = _worldCam.transform.position;
            _worldCamAuthoredRot = FlattenRoll(_worldCam.transform.rotation);

            // v1.3.3 NOTE: v1.3.2 anchored the player to the MainMenuBackground _cameraContainer
            // position here. That was a no-op in practice: the container's authored spot IS the
            // menu camera's spot (the camera is a child of the container), and the game's own
            // MainMenuBackground.Update() lerps the container back to (0, 0.5, 0) every frame.
            // YARG's menu "environment" is additionally just one small glowing quad + skybox, so
            // there was never any geometry to stand inside. The surround is now delivered by the
            // v1.3.3 background sphere (UpdateMenuSurround) built from YARG's own menu gradient
            // material, and the anchor uses the camera's authored pose exactly like pre-1.3.2.
            if (_menuBgCamera != null && _worldCam == _menuBgCamera)
            {
                MelonLoader.MelonLogger.Msg("[YARG-VR] Menu background camera bound - the animated menu " +
                    "gradient will surround you via a 360-degree background sphere" +
                    (_settings.MenuEnvSurround.Value ? "" : " (currently disabled: MenuEnvSurround=false)") + ".");
            }
            else
            {
                MelonLoader.MelonLogger.Msg("[YARG-VR] World anchored to YARG's camera '" + _worldCam.name +
                    "' - the environment now lines up with the game's own view (F9 re-anchors it).");
            }

            ReanchorWorld(hmdPos, hmdFlat);
        }

        /// <summary>Maps the player's current head pose onto the world camera's authored pose.</summary>
        private void ReanchorWorld(Vector3 hmdPos, Quaternion hmdFlat)
        {
            if (_worldCam == null)
            {
                _worldAnchorActive = false;
                return;
            }

            _worldAnchorRot = _worldCamAuthoredRot * Quaternion.Inverse(hmdFlat);
            _worldAnchorPos = _worldCamAuthoredPos - _worldAnchorRot * hmdPos;
            _worldAnchorActive = true;
        }

        private Vector3 AnchorPoint(Vector3 raw)
        {
            return _worldAnchorActive ? _worldAnchorPos + _worldAnchorRot * raw : raw;
        }

        private Quaternion AnchorRotation(Quaternion raw)
        {
            return _worldAnchorActive ? _worldAnchorRot * raw : raw;
        }

        private Vector3 AnchorDirection(Vector3 raw)
        {
            return _worldAnchorActive ? _worldAnchorRot * raw : raw;
        }

        /// <summary>
        /// Wires up the render-pipeline hooks. beginCameraRendering performs the per-eye venue
        /// / highway swaps BEFORE each eye camera draws; endCameraRendering submits each eye's
        /// finished texture; endContextRendering restores YARG's global state and acts as a
        /// submission backstop.
        /// </summary>
        private void HookPipeline()
        {
            if (_hooked)
            {
                return;
            }

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;

            _hooked = true;
            _watchdogGraceUntil = Time.unscaledTime + 2f; // let the first frames render before judging
            _watchdogDiagnosticsLogged = false;
        }

        private void UnhookPipeline()
        {
            if (!_hooked)
            {
                return;
            }

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
            _hooked = false;
        }

        private void FindVenueOutput()
        {
            _venueOutput = null;
            _yargVenueTexture = null;
            TryFindVenueOutput(warnOnce: false);
        }

        /// <summary>
        /// Looks for YARG's "Venue Output" RawImage. Retried on every canvas rescan because the
        /// gameplay UI hierarchy can finish loading after the rig engages.
        /// </summary>
        private void TryFindVenueOutput(bool warnOnce)
        {
            RawImage[] rawImages = UnityEngine.Object.FindObjectsByType<RawImage>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < rawImages.Length; i++)
            {
                if (rawImages[i] != null && rawImages[i].gameObject.name == "Venue Output")
                {
                    _venueOutput = rawImages[i];
                    _yargVenueTexture = _venueOutput.texture as RenderTexture;
                    if (YargBridge.GetCurrentVenueCamera() != null)
                    {
                        MelonLoader.MelonLogger.Msg("[YARG-VR] Found 'Venue Output' - stereo stage enabled.");
                    }
                    else
                    {
                        MelonLoader.MelonLogger.Msg(
                            "[YARG-VR] Found 'Venue Output' (no venue camera yet - stage stereo engages " +
                            "when the song has a 3-D venue; video/image-background songs have none).");
                    }
                    return;
                }
            }

            if (warnOnce)
            {
                WarnOnce(ref _venueOutputWarned,
                    "'Venue Output' RawImage not found yet - the stage renders mono until it appears " +
                    "(HUD, highways and screen depth remain stereo).");
            }
        }

        #endregion

        #region Canvas conversion (Screen Space Overlay -> world-space floating screens)

        private void ConvertCanvases()
        {
            ScanForCanvases(initial: true);
            MelonLoader.MelonLogger.Msg("[YARG-VR] Converted " + _converted.Count +
                " root canvas(es) into a world-space screen (locked in the room - F9 re-places it).");
        }

        /// <summary>
        /// The screen occupies this fraction of the eye's field of view by default. Filling 100%
        /// of a ~98° HMD frustum makes the edges impossible to see without head turning and
        /// exaggerates perspective (the "bent screen" feeling); ~72% keeps the whole screen in
        /// view comfortably. HudScale multiplies on top of this.
        /// </summary>
        private const float ScreenFillFactor = 0.72f;

        /// <summary>
        /// Converts every root Screen Space Overlay canvas that we have not converted yet.
        /// Runs once on scene enter and then every ~2 seconds, so canvases that spawn later
        /// (menus, popups, lyric panels) are picked up too.
        /// </summary>
        private void ScanForCanvases(bool initial)
        {
            // Prune destroyed canvases / venue cameras from previous scenes first.
            for (int i = _converted.Count - 1; i >= 0; i--)
            {
                if (_converted[i].Canvas == null)
                {
                    _converted.RemoveAt(i);
                }
            }
            for (int i = _billboards.Count - 1; i >= 0; i--)
            {
                if (_billboards[i].Canvas == null)
                {
                    _billboards.RemoveAt(i);
                }
            }
            List<Camera> deadVenueCams = null;
            foreach (KeyValuePair<Camera, VenueSnapshot> kv in _venueAuthored)
            {
                if (kv.Key == null)
                {
                    if (deadVenueCams == null)
                    {
                        deadVenueCams = new List<Camera>();
                    }
                    deadVenueCams.Add(kv.Key);
                }
            }
            if (deadVenueCams != null)
            {
                for (int i = 0; i < deadVenueCams.Count; i++)
                {
                    _venueAuthored.Remove(deadVenueCams[i]);
                }
            }

            // The venue RawImage may appear after the scene finishes loading - keep retrying.
            if (_venueOutput == null)
            {
                TryFindVenueOutput(warnOnce: !initial);
            }

            // Chart-provided video backgrounds render outside the canvas - keep them hooked.
            try
            {
                ScanForVideoBackgrounds();
            }
            catch
            {
                // Video hooking is best-effort; never break the canvas scan over it.
            }

            // Pick up scene/camera changes (e.g. the menu environment camera) so the eye
            // cameras keep rendering YARG's world around the screen.
            RefreshCameras();

            Canvas[] canvases = UnityEngine.Object.FindObjectsByType<Canvas>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            List<Canvas> pending = null;
            foreach (Canvas canvas in canvases)
            {
                if (canvas == null || !canvas.isRootCanvas)
                {
                    continue;
                }

                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    continue;
                }

                bool already = false;
                foreach (CanvasSnapshot s in _converted)
                {
                    if (s.Canvas == canvas)
                    {
                        already = true;
                        break;
                    }
                }
                if (already)
                {
                    continue;
                }

                if (pending == null)
                {
                    pending = new List<Canvas>();
                }
                pending.Add(canvas);
            }

            if (pending == null)
            {
                return;
            }

            // Stack canvases in their sorting order: higher sortingOrder ends up closer to the
            // head, preserving YARG's own layering.
            pending.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));

            // Size the screen to a comfortable fraction of the eye's field of view at the
            // configured distance (HudScale multiplies on top).
            float hudFov = _settings.HudFov.Value > 1f
                ? Mathf.Clamp(_settings.HudFov.Value, 30f, 170f)
                : OpenVrRuntime.EyeVFovDeg;
            float dist = Mathf.Max(0.2f, _settings.HudDistance.Value);
            float worldH = 2f * dist * Mathf.Tan(hudFov * 0.5f * Mathf.Deg2Rad) * ScreenFillFactor;
            float worldW = worldH * Mathf.Max(0.2f, OpenVrRuntime.EyeAspect);
            float hudScale = Mathf.Clamp(_settings.HudScale.Value, 0.1f, 4f);

            foreach (Canvas canvas in pending)
            {
                var snap = new CanvasSnapshot
                {
                    Canvas = canvas,
                    Mode = canvas.renderMode,
                    WorldCamera = canvas.worldCamera,
                    PlaneDistance = canvas.planeDistance,
                    LocalScale = canvas.transform.localScale,
                    ScaleFactor = canvas.scaleFactor,
                    LocalPos = canvas.transform.localPosition,
                    LocalRot = canvas.transform.localRotation,
                    Pivot = (canvas.transform as RectTransform) != null
                        ? (canvas.transform as RectTransform).pivot
                        : new Vector2(0.5f, 0.5f),
                };

                canvas.renderMode = RenderMode.WorldSpace;
                canvas.scaleFactor = 1f;

                // World Space canvases still use worldCamera for EventSystem raycasting
                // (rendering is unaffected - every camera renders them). Point it at the game's
                // main camera so mouse/gamepad UI input keeps working as best it can.
                canvas.worldCamera = Camera.main;

                // Center the pivot so the billboard position is the screen's exact center.
                RectTransform rt = canvas.transform as RectTransform;
                if (rt != null)
                {
                    rt.pivot = new Vector2(0.5f, 0.5f);
                }

                // World width maps the canvas' pixel rect onto a comfortable fraction of the eye FOV.
                float pxW = rt != null ? rt.rect.width : 0f;
                if (pxW < 1f)
                {
                    pxW = 1000f; // defensive fallback for odd canvas setups
                }
                canvas.transform.localScale = Vector3.one * (worldW / pxW * hudScale);

                _billboards.Add(new Billboard
                {
                    Canvas = canvas,
                    Offset = _billboards.Count * 0.004f,
                });
                _converted.Add(snap);

                if (!initial)
                {
                    MelonLoader.MelonLogger.Msg("[YARG-VR] Late-converted a new root canvas to a VR screen.");
                }
            }
        }

        private void RestoreCanvases()
        {
            foreach (CanvasSnapshot snap in _converted)
            {
                if (snap.Canvas == null)
                {
                    continue; // scene object already destroyed
                }

                snap.Canvas.transform.localScale = snap.LocalScale;
                snap.Canvas.transform.localPosition = snap.LocalPos;
                snap.Canvas.transform.localRotation = snap.LocalRot;
                snap.Canvas.scaleFactor = snap.ScaleFactor;
                snap.Canvas.planeDistance = snap.PlaneDistance;
                snap.Canvas.worldCamera = snap.WorldCamera;
                RectTransform restoreRt = snap.Canvas.transform as RectTransform;
                if (restoreRt != null)
                {
                    restoreRt.pivot = snap.Pivot;
                }
                snap.Canvas.renderMode = snap.Mode;
            }

            _converted.Clear();
            _billboards.Clear();
        }

        #endregion

        #region Chart video backgrounds (VideoPlayer -> canvas slot)

        private sealed class VideoBgHook
        {
            public VideoPlayer Player;
            public RenderTexture Texture;
            public RawImage Slot;
            public bool SlotOriginallyActive;
            public VideoRenderMode OriginalMode;
            public Camera OriginalTargetCamera;
            public bool AspectMatched;
        }

        private readonly List<VideoBgHook> _videoHooks = new List<VideoBgHook>();
        private static bool _videoSlotWarned;

        /// <summary>
        /// Chart-provided video backgrounds (community "video.webm" files) are NOT part of
        /// YARG's UI: the Gameplay scene's VideoPlayer plays in CameraFarPlane mode onto the
        /// scene's "Camera (No Venue)" - a plain base camera at depth -1 that draws the video
        /// plane straight to the desktop window (YARG's BackgroundManager._backgroundImage
        /// RawImage is only activated for IMAGE backgrounds). None of that is visible to the
        /// headset's eye cameras, so the video showed on the monitor while the headset saw a
        /// black background behind the highways/HUD.
        ///
        /// Fix: retarget the VideoPlayer to a mod-owned RenderTexture and display it through
        /// YARG's own "Background" RawImage (the backmost slot on the gameplay canvas). The
        /// video becomes part of the canvas composition, so the monitor AND both eyes see it.
        /// Restored on Leave(); released automatically when the player dies (scene change).
        /// </summary>
        private void ScanForVideoBackgrounds()
        {
            // Prune hooks whose player or slot died (song ended / scene changed).
            for (int i = _videoHooks.Count - 1; i >= 0; i--)
            {
                VideoBgHook dead = _videoHooks[i];
                if (dead.Player == null || dead.Slot == null)
                {
                    if (dead.Texture != null)
                    {
                        dead.Texture.Release();
                        UnityEngine.Object.Destroy(dead.Texture);
                    }
                    _videoHooks.RemoveAt(i);
                }
            }

            VideoPlayer[] players = UnityEngine.Object.FindObjectsByType<VideoPlayer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < players.Length; i++)
            {
                VideoPlayer vp = players[i];
                if (vp == null)
                {
                    continue;
                }

                VideoBgHook existing = FindVideoHook(vp);
                if (existing != null)
                {
                    UpdateVideoHook(existing);
                    continue;
                }

                // Only take over camera-plane playback (YARG's video-background mode).
                // Yarground venues manage their own RenderTexture video - leave those alone.
                if (vp.renderMode != VideoRenderMode.CameraFarPlane || vp.targetCamera == null)
                {
                    continue;
                }

                HookVideoBackground(vp);
            }
        }

        private VideoBgHook FindVideoHook(VideoPlayer vp)
        {
            for (int i = 0; i < _videoHooks.Count; i++)
            {
                if (_videoHooks[i].Player == vp)
                {
                    return _videoHooks[i];
                }
            }
            return null;
        }

        private void HookVideoBackground(VideoPlayer vp)
        {
            RawImage slot = FindBackgroundSlot();
            if (slot == null)
            {
                WarnOnce(ref _videoSlotWarned,
                    "Found a chart video background but not YARG's 'Background' RawImage slot - " +
                    "the video stays monitor-only for this song.");
                return;
            }

            // Size the RT like the game window (the background is authored fullscreen); the
            // VideoPlayer scales frames to fill whatever texture it targets. Aspect gets
            // corrected to the video's real size once it is prepared (UpdateVideoHook).
            int w = Mathf.Clamp(Screen.width, 640, 2560);
            int h = Mathf.Clamp(Screen.height, 360, 1440);
            RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.Default)
            {
                name = "YARG-VR Video Background"
            };

            VideoBgHook hook = new VideoBgHook
            {
                Player = vp,
                Texture = rt,
                Slot = slot,
                SlotOriginallyActive = slot.gameObject.activeSelf,
                OriginalMode = vp.renderMode,
                OriginalTargetCamera = vp.targetCamera,
            };
            _videoHooks.Add(hook);

            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;

            slot.texture = rt;
            // VideoPlayer writes RenderTextures upright for standard UVs (YARG's own image
            // path pre-flips images "to match video" before sampling the video texture the
            // same way), so the default UV rect is correct here.
            slot.uvRect = new Rect(0f, 0f, 1f, 1f);
            slot.gameObject.SetActive(true);

            MelonLoader.MelonLogger.Msg(
                "[YARG-VR] Chart video background hooked - it now renders on the VR screen too.");
        }

        private void UpdateVideoHook(VideoBgHook hook)
        {
            if (hook.Player == null || hook.Texture == null)
            {
                return;
            }

            // If YARG switched the player away from us (yarground songs manage their own video
            // texture for venue shaders), stop displaying our copy so it cannot cover the
            // real venue output.
            if (hook.Player.renderMode != VideoRenderMode.RenderTexture ||
                !ReferenceEquals(hook.Player.targetTexture, hook.Texture))
            {
                if (hook.Slot != null && ReferenceEquals(hook.Slot.texture, hook.Texture))
                {
                    hook.Slot.gameObject.SetActive(false);
                    hook.Slot.texture = null;
                }
                return;
            }

            // Once the video is prepared we know its real size - match the RT aspect to the
            // video so non-16:9 videos are not stretched (RenderTexture mode always fills).
            if (!hook.AspectMatched && hook.Player.width > 0 && hook.Player.height > 0)
            {
                float videoAspect = (float)hook.Player.width / hook.Player.height;
                float rtAspect = (float)hook.Texture.width / hook.Texture.height;
                if (Mathf.Abs(videoAspect - rtAspect) > 0.02f)
                {
                    int h = Mathf.Clamp(Screen.height, 360, 1440);
                    int w = Mathf.Clamp(Mathf.RoundToInt(h * videoAspect), 320, 2560);
                    hook.Texture.Release();
                    hook.Texture.width = w;
                    hook.Texture.height = h;
                    hook.Texture.Create();
                }
                hook.AspectMatched = true;
            }
        }

        /// <summary>
        /// YARG's "Background" RawImage (backmost element of the gameplay canvas, sibling of
        /// the Dimmer inside "Background Container"). Resolved via reflection on
        /// BackgroundManager._backgroundImage so a YARG object rename cannot break us;
        /// falls back to the authored object name.
        /// </summary>
        private RawImage FindBackgroundSlot()
        {
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour mb = behaviours[i];
                if (mb == null || mb.GetType().Name != "BackgroundManager")
                {
                    continue;
                }

                FieldInfo field = mb.GetType().GetField("_backgroundImage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                RawImage img = field != null ? field.GetValue(mb) as RawImage : null;
                if (img != null)
                {
                    return img;
                }
            }

            // Fallback: the authored hierarchy path (Background Container -> Background).
            RawImage[] rawImages = UnityEngine.Object.FindObjectsByType<RawImage>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < rawImages.Length; i++)
            {
                if (rawImages[i] != null && rawImages[i].gameObject.name == "Background")
                {
                    Transform parent = rawImages[i].transform.parent;
                    if (parent != null && parent.name == "Background Container")
                    {
                        return rawImages[i];
                    }
                }
            }

            return null;
        }

        private void ReleaseVideoHooks(bool restorePlayers)
        {
            for (int i = 0; i < _videoHooks.Count; i++)
            {
                VideoBgHook hook = _videoHooks[i];

                if (hook.Player != null && restorePlayers &&
                    hook.Player.renderMode == VideoRenderMode.RenderTexture &&
                    ReferenceEquals(hook.Player.targetTexture, hook.Texture))
                {
                    hook.Player.renderMode = hook.OriginalMode;
                    hook.Player.targetTexture = null;
                }

                if (hook.Slot != null)
                {
                    if (ReferenceEquals(hook.Slot.texture, hook.Texture))
                    {
                        hook.Slot.texture = null;
                    }
                    hook.Slot.gameObject.SetActive(hook.SlotOriginallyActive);
                }

                if (hook.Texture != null)
                {
                    hook.Texture.Release();
                    UnityEngine.Object.Destroy(hook.Texture);
                }
            }
            _videoHooks.Clear();
        }

        #endregion

        #region Per-frame pose application (called from MelonMod.OnLateUpdate)

        public void LateTick(Vector3 hmdPos, Quaternion hmdRot)
        {
            if (!IsActive)
            {
                return;
            }

            // Pick up canvases that spawned after scene load (throttled to every ~2 s).
            if (Time.frameCount >= _nextCanvasScanFrame)
            {
                _nextCanvasScanFrame = Time.frameCount + 120;
                try
                {
                    ScanForCanvases(initial: false);
                }
                catch
                {
                    // Rescanning is best-effort.
                }
            }

            Quaternion hmdFlat = FlattenRoll(hmdRot);

            // 1) World anchor: map the player's head onto YARG's own camera pose, so the
            //    environment lines up with the game's authored view (menus included).
            BindWorldCamera(hmdPos, hmdFlat);

            // v1.3.3: menu background surround sphere - visible exactly while the menu
            // background camera is the bound world camera (never during a song).
            UpdateMenuSurround();

            Vector3 headPosA = AnchorPoint(hmdPos);
            Quaternion headRotA = AnchorRotation(hmdRot);

            // 2) Eye cameras. The UI screen stereo uses PURELY HORIZONTAL symmetric offsets
            // (half the user's IPD along the head's right axis) instead of OpenVR's raw
            // eye-to-head transforms: those carry tiny Y/Z components that became a small
            // vertical misalignment between the two screen images - perceived as a slight
            // blur/double vision. ScreenStereo scales the separation (0 = flat screen).
            float halfIpd = OpenVrRuntime.HasEyeGeometry
                ? (Mathf.Abs(OpenVrRuntime.EyeOffsetLeft.x) + Mathf.Abs(OpenVrRuntime.EyeOffsetRight.x)) * 0.5f
                : 0.0315f;
            halfIpd *= Mathf.Clamp(_settings.ScreenStereo.Value, 0f, 1f);
            Vector3 headRightA = headRotA * Vector3.right;
            _eyeCamL.transform.SetPositionAndRotation(headPosA - headRightA * halfIpd, headRotA);
            _eyeCamR.transform.SetPositionAndRotation(headPosA + headRightA * halfIpd, headRotA);

            // 3) World-space screen: anchor on first valid pose, then place (or billboard).
            if (!_screenAnchorPlaced)
            {
                PlaceScreenAnchor(hmdPos, hmdFlat);
            }
            TryBuildHudPlane();
            UpdateScreenPose(headPosA);
            UpdateVisualizer(Time.deltaTime);
            UpdateVisualizerOcclusion(headPosA);
            EnsureMirrorCamera();
            UpdateMirrorQuad();

            // Optional hard-number diagnostics (PoseDebug pref): if a "everything follows my
            // head" report persists, this settles whether placed objects really move or the
            // perception comes from elsewhere (compositor, tracking origin, game camera).
            if (_settings.PoseDebug.Value && _roomRoot != null && Time.unscaledTime >= _nextPoseDebug)
            {
                _nextPoseDebug = Time.unscaledTime + 5f;
                Vector3 screenWorld = _roomRoot.TransformPoint(_screenAnchorLocal);
                MelonLoader.MelonLogger.Msg("[YARG-VR][pose] head=(" +
                    hmdPos.x.ToString("F2") + ", " + hmdPos.y.ToString("F2") + ", " + hmdPos.z.ToString("F2") +
                    ") yaw=" + hmdRot.eulerAngles.y.ToString("F0") + "deg  root=(" +
                    _roomRoot.position.x.ToString("F2") + ", " + _roomRoot.position.y.ToString("F2") + ", " +
                    _roomRoot.position.z.ToString("F2") + ")  screenWorld=(" +
                    screenWorld.x.ToString("F2") + ", " + screenWorld.y.ToString("F2") + ", " +
                    screenWorld.z.ToString("F2") + ")");
            }

            // 3) Venue camera takeover (anchor math stays on the HEAD pose, like v1.0.2).
            Camera venue = YargBridge.GetCurrentVenueCamera();
            if (venue == null)
            {
                _venueCam = null;
                _anchored = false;
                return;
            }

            if (venue != _venueCam)
            {
                BindVenueCamera(venue, hmdPos, hmdFlat, recenter: _settings.AutoRecenterOnCut.Value);
            }

            if (!_anchored)
            {
                return;
            }

            // Per-eye venue poses = anchor * (flat head pose + eye offset).
            Vector3 headL = hmdPos + hmdFlat * OpenVrRuntime.EyeOffsetLeft;
            Vector3 headR = hmdPos + hmdFlat * OpenVrRuntime.EyeOffsetRight;
            _venueLeftPos = _anchorPos + _anchorRot * headL;
            _venueRightPos = _anchorPos + _anchorRot * headR;
            _venueLeftRot = _anchorRot * hmdFlat * OpenVrRuntime.EyeRotationLeft;
            _venueRightRot = _anchorRot * hmdFlat * OpenVrRuntime.EyeRotationRight;

            if (_settings.HeightLock.Value && _venueAuthored.ContainsKey(_venueCam))
            {
                float lockedY = _venueAuthored[_venueCam].Position.y + _settings.HeightOffset.Value;
                _venueLeftPos.y = lockedY;
                _venueRightPos.y = lockedY;
            }
            else
            {
                _venueLeftPos.y += _settings.HeightOffset.Value;
                _venueRightPos.y += _settings.HeightOffset.Value;
            }

            // Drive the venue camera at the LEFT eye pose; the render hook renders the
            // dedicated right-eye clone before the normal pass (see RenderVenueRightEye).
            _venueCam.transform.SetPositionAndRotation(_venueLeftPos, _venueLeftRot);

            // Keep the right-eye clone synchronized with YARG's camera and park it at the
            // right-eye pose.
            if (_venueClone != null)
            {
                SyncVenueClone(_venueCam);
                _venueClone.transform.SetPositionAndRotation(_venueRightPos, _venueRightRot);
            }
        }

        /// <summary>
        /// Creates the room root - the "invisible cube" every placed object (screen stack,
        /// pop-out HUD, visualizer ring) is positioned relative to. It is created once and
        /// moves only in PlaceScreenAnchor (first valid pose / F9).
        /// </summary>
        private void EnsureRoomRoot()
        {
            if (_roomRoot != null)
            {
                return;
            }

            GameObject go = new GameObject("YARG-VR RoomRoot");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _roomRoot = go.transform;
        }

        private void PlaceScreenAnchor(Vector3 hmdPos, Quaternion hmdFlat)
        {
            float dist = Mathf.Max(0.2f, _settings.HudDistance.Value);

            // IMPORTANT: place the screen on the HORIZONTAL plane at eye height, ignoring head
            // pitch. Placing it along the pitched gaze direction used to plant the screen near
            // the floor whenever the user happened to look down when the rig engaged (e.g. at
            // the keyboard while starting a song), forcing them to stare down at it.
            Vector3 fwd = hmdFlat * Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.000001f ? fwd.normalized : Vector3.forward;

            // v1.3.0: MOVE THE ROOM ROOT to the current head pose (yaw only, no pitch/roll).
            // This is the ONLY code in the mod that moves the root, and it runs just twice:
            // on the first valid pose after entering a scene and on F9. Everything the mod
            // places is expressed in the root's local space, so room-lock is inherited from
            // the root instead of being recomputed from the head.
            //
            // The root is placed at the ANCHORED head pose (see BindWorldCamera) - the same
            // frame the eye cameras render from - so the screen is centered in view exactly
            // like v1.2.2 placed it, but now every later frame derives from the root instead
            // of a frozen vector. In scenes without a world camera the anchor is identity and
            // the root sits at the raw head pose.
            //
            // uGUI is readable from the canvas' -Z side, so the canvas' +Z must point AWAY
            // from the viewer - i.e. along the gaze (head -> screen). A canvas at root-local
            // +Z with the root's yaw-aligned rotation satisfies exactly that (the v1.2.2
            // convention that reads correctly).
            EnsureRoomRoot();
            Vector3 rootPos = AnchorPoint(hmdPos);
            Quaternion rootRot = AnchorRotation(Quaternion.LookRotation(fwd, Vector3.up));
            _roomRoot.SetPositionAndRotation(rootPos, rootRot);

            // Screen / HUD poses become ROOT-LOCAL constants. World poses are derived from
            // the root every frame in UpdateScreenPose - so they are exactly as room-locked
            // as the root itself.
            _screenAnchorLocal = new Vector3(0f, 0f, dist);

            // Pop-out HUD plane sits between the player and the screen on the same ray.
            float popDist = Mathf.Clamp(_settings.HudPopDistance.Value, 0.4f, dist - 0.1f);
            _hudAnchorLocal = new Vector3(0f, 0f, popDist);

            _screenAnchorPlaced = true;

            MelonLoader.MelonLogger.Msg("[YARG-VR] Room anchor set at (" +
                rootPos.x.ToString("F2") + ", " + rootPos.y.ToString("F2") + ", " + rootPos.z.ToString("F2") +
                ") - screen, HUD and visualizer are locked to it (F9 re-places).");

            EnsureVisualizer();
            PositionVisualizer();
        }

        /// <summary>
        /// Positions every converted canvas and the pop-out HUD plane.
        ///
        /// ScreenFollowsView = OFF (default): every pose is derived from the ROOM ROOT, which
        /// moves only on placement (first pose / F9). The screen is therefore locked to the
        /// room exactly as firmly as the root itself - head motion cannot move it, by
        /// construction. This replaces v1.2.2's "frozen world-space vector" approach, which
        /// was correct per the math but left the screen's relationship to the re-anchored
        /// 3-D environment undefined whenever the world anchor re-fired.
        ///
        /// ScreenFollowsView = ON: the old v1.1 behavior - the screen stays at its root-locked
        /// position and only yaws to face the head.
        ///
        /// <paramref name="headPos"/> must be the ANCHORED head position (world space).
        /// </summary>
        private void UpdateScreenPose(Vector3 headPos)
        {
            if (!_screenAnchorPlaced || _roomRoot == null)
            {
                return;
            }

            Vector3 anchorWorld = _roomRoot.TransformPoint(_screenAnchorLocal);
            Quaternion rot;
            Vector3 stackDir;
            if (_settings.ScreenFollowsView.Value)
            {
                Vector3 toHead = headPos - anchorWorld;
                toHead.y = 0f;
                stackDir = toHead.sqrMagnitude > 0.000001f
                    ? toHead.normalized
                    : -_roomRoot.forward; // head exactly above the anchor - degenerate
                // uGUI reads from the canvas' -Z side, so +Z must point AWAY from the head
                // (along head -> screen). This is the v1.1.x convention that read correctly.
                rot = Quaternion.LookRotation(-stackDir, Vector3.up);
            }
            else
            {
                rot = _roomRoot.rotation;
                stackDir = -_roomRoot.forward;
            }

            for (int i = 0; i < _billboards.Count; i++)
            {
                Billboard b = _billboards[i];
                if (b.Canvas == null)
                {
                    continue;
                }

                // Stack: later canvases (higher sortingOrder, drawn on top) sit closer to the
                // head so YARG's own layering is preserved in depth. Kept small: a large
                // depth spread shears the layers apart when pitching (the "bending window").
                b.Canvas.transform.SetPositionAndRotation(
                    anchorWorld + stackDir * b.Offset, rot);
            }

            if (_hudPlane != null)
            {
                _hudPlane.transform.SetPositionAndRotation(
                    _roomRoot.TransformPoint(_hudAnchorLocal), rot);
            }
        }

        private void BindVenueCamera(Camera venue, Vector3 hmdPos, Quaternion hmdFlat, bool recenter)
        {
            _venueCam = venue;

            if (!_venueAuthored.ContainsKey(venue))
            {
                // First time we touch this camera: record the pose YARG gave it, plus its FOV,
                // so we can restore everything exactly on teardown.
                _venueAuthored[venue] = new VenueSnapshot
                {
                    Position = venue.transform.position,
                    Rotation = venue.transform.rotation,
                    Fov = venue.fieldOfView,
                };

                float fovOverride = _settings.VenueFovOverride.Value;
                if (fovOverride > 1f)
                {
                    venue.fieldOfView = fovOverride;
                }
            }

            // The very first bind always recenters (otherwise the takeover would never start).
            // Later rebinds (YARG camera cuts) recenter fully when AutoRecenterOnCut is on;
            // otherwise only the anchor POSITION is rebased onto the new camera so the user's
            // facing direction persists across cuts (F9 still recenters fully at any time).
            if (recenter || !_anchored)
            {
                Recenter(hmdPos, hmdFlat);
            }
            else
            {
                VenueSnapshot authored = _venueAuthored[venue];
                _anchorPos = authored.Position - _anchorRot * hmdPos;
            }

            RefreshCameras();
        }

        /// <summary>
        /// Manual recenter (F9): MOVES THE ROOM ROOT (screen stack, pop-out HUD and visualizer
        /// ring travel with it) to the current head pose, re-anchors the 3-D environment
        /// mapping so the stage view's rotation matches your current facing, and re-places
        /// the floating screen straight ahead of you. Without a venue camera (menus) it just
        /// moves the root (which re-places the screen).
        /// </summary>
        public void Recenter()
        {
            if (!OpenVrRuntime.TryGetHmdPose(out Vector3 hmdPos, out Quaternion hmdRot))
            {
                return;
            }

            Quaternion hmdFlat = FlattenRoll(hmdRot);

            // Re-anchor the world to the current facing FIRST, so the screen is re-placed
            // inside the fresh anchor frame.
            ReanchorWorld(hmdPos, hmdFlat);

            // Manual recenter ALWAYS re-places the screen (position + rotation) in front of
            // the user - that is the whole point of F9.
            PlaceScreenAnchor(hmdPos, hmdFlat);

            if (_venueCam != null && _venueAuthored.ContainsKey(_venueCam))
            {
                Recenter(hmdPos, hmdFlat);
            }
        }

        private void Recenter(Vector3 hmdPos, Quaternion hmdFlat)
        {
            VenueSnapshot authored = _venueAuthored[_venueCam];
            Quaternion authoredFlat = FlattenRoll(authored.Rotation);

            _anchorRot = authoredFlat * Quaternion.Inverse(hmdFlat);
            _anchorPos = authored.Position - _anchorRot * hmdPos;
            _anchored = true;

            // NOTE: the screen anchor is deliberately NOT re-placed here. This overload also
            // runs for AUTO-recenters (YARG camera cuts) when AutoRecenterOnCut is on, and
            // re-placing the screen on each cut turned playspace tracking creep into the
            // slow stepwise upward drift reported by users. The screen stays locked; only
            // the manual F9 recenter re-places it.
        }

        #endregion

        #region Pop-out HUD plane (parallax depth)

        /// <summary>
        /// Builds a second world-space canvas between the player and the game screen and moves
        /// YARG's HUD roots ("Main HUD Container" - score, lyrics, practice HUD, song info,
        /// BRE box - and "Pause Menu Manager") onto it. The HUD then floats closer to the player
        /// than the screen: looking around produces real parallax between HUD and game - the
        /// "popped out 3-D" effect. The plane's pixel rect matches the game screen's exactly and
        /// its scale is multiplied by (popDistance / screenDistance), so the HUD keeps the same
        /// angular size it had - it just detaches in depth.
        /// </summary>
        private void TryBuildHudPlane()
        {
            if (!_settings.HudPopOut.Value || _hudPlane != null || _hudPopAttempted)
            {
                return;
            }

            Canvas main = null;
            for (int i = 0; i < _converted.Count; i++)
            {
                CanvasSnapshot s = _converted[i];
                if (s.Canvas != null && s.Canvas.transform.Find("Main HUD Container") != null)
                {
                    main = s.Canvas;
                    break;
                }
            }
            if (main == null)
            {
                return; // gameplay canvas not converted yet (or not the gameplay scene)
            }

            _hudPopAttempted = true;

            RectTransform mainRt = main.transform as RectTransform;
            if (mainRt == null)
            {
                return;
            }

            float dist = Mathf.Max(0.2f, _settings.HudDistance.Value);
            float popDist = Mathf.Clamp(_settings.HudPopDistance.Value, 0.4f, dist - 0.1f);

            GameObject go = new GameObject("YARG-VR HUD Plane");
            Canvas cv = go.AddComponent<Canvas>();
            cv.renderMode = RenderMode.WorldSpace;
            cv.sortingOrder = 30000;
            cv.scaleFactor = 1f;
            cv.worldCamera = Camera.main;

            RectTransform rt = go.transform as RectTransform;
            rt.sizeDelta = mainRt.sizeDelta;
            rt.pivot = new Vector2(0.5f, 0.5f);
            // Same angular size as on the screen, but at the pop-out distance.
            rt.localScale = mainRt.localScale * (popDist / dist);

            _hudPlane = go;
            _hudPlaneCanvas = cv;

            PopOut(main.transform, "Main HUD Container");
            PopOut(main.transform, "Pause Menu Manager");

            MelonLoader.MelonLogger.Msg(
                "[YARG-VR] HUD popped out onto its own floating plane (parallax depth in front of the screen).");
        }

        private void PopOut(Transform main, string childName)
        {
            Transform t = main.Find(childName);
            if (t == null)
            {
                return;
            }

            RectTransform rect = t as RectTransform;
            if (rect == null)
            {
                return;
            }

            _hudReparented.Add(new ReparentRecord
            {
                Rect = rect,
                Parent = t.parent,
                SiblingIndex = t.GetSiblingIndex(),
            });

            // worldPositionStays = false keeps the child's LOCAL values (anchors, positions,
            // scales in pixel space) - and the plane's pixel rect matches the screen's, so the
            // layout is preserved exactly; only the uniform world scale differs.
            rect.SetParent(_hudPlaneCanvas.transform, false);
        }

        private void RestoreHudPlane()
        {
            for (int i = 0; i < _hudReparented.Count; i++)
            {
                ReparentRecord r = _hudReparented[i];
                if (r.Rect == null)
                {
                    continue; // died with its scene
                }

                r.Rect.SetParent(r.Parent, false);
                r.Rect.SetSiblingIndex(r.SiblingIndex);
            }
            _hudReparented.Clear();

            if (_hudPlane != null)
            {
                UnityEngine.Object.Destroy(_hudPlane);
            }
            _hudPlane = null;
            _hudPlaneCanvas = null;
            _hudPopAttempted = false;
        }

        #endregion

        #region Desktop mirror (monitor shows the headset view)

        private const float MirrorFov = 60f;

        /// <summary>
        /// A dedicated camera rendered LAST (depth 2000, higher than any YARG camera or eye
        /// camera) that draws a full-screen quad textured with the LEFT EYE's render target
        /// onto the game window - letterboxed to the eye's aspect ratio. The monitor then
        /// always shows exactly what the headset sees (screen, visualizer, world), which also
        /// fixes the old "game window is mirrored / disappears on the monitor" confusion: the
        /// game's own camera now looks at the floating screen from wherever IT is, not from
        /// where the player is.
        /// The quad lives on layer 2 ("Ignore Raycast") - a layer the eye cameras never
        /// render (feedback loop) and physics raycasts ignore by name.
        /// </summary>
        private void EnsureMirrorCamera()
        {
            if (!_settings.DesktopMirror.Value)
            {
                DestroyMirror();
                return;
            }

            if (_mirrorCam != null || _rtL == null)
            {
                return;
            }

            Shader shader = Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            if (shader == null)
            {
                return; // extremely unlikely - skip silently
            }

            GameObject go = new GameObject("YARG-VR Desktop Mirror");
            _mirrorCam = go.AddComponent<Camera>();
            _mirrorCam.enabled = true; // URP renders it automatically - depth 2000 = last
            _mirrorCam.depth = 2000f;
            _mirrorCam.clearFlags = CameraClearFlags.SolidColor;
            _mirrorCam.backgroundColor = Color.black;
            _mirrorCam.cullingMask = 1 << 2; // "Ignore Raycast" - the mirror quad only
            _mirrorCam.nearClipPlane = 0.01f;
            _mirrorCam.farClipPlane = 5f;
            _mirrorCam.fieldOfView = MirrorFov;
            _mirrorCam.allowMSAA = false;
            _mirrorCam.useOcclusionCulling = false;
            _mirrorCam.stereoTargetEye = StereoTargetEyeMask.None;
            UnityEngine.Object.DontDestroyOnLoad(go);

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Collider col = quad.GetComponent<Collider>();
            if (col != null)
            {
                UnityEngine.Object.Destroy(col); // no physics / raycast interference
            }
            quad.name = "Mirror Quad";
            quad.layer = 2;
            _mirrorMat = new Material(shader);
            _mirrorMat.mainTexture = _rtL;
            quad.GetComponent<MeshRenderer>().sharedMaterial = _mirrorMat;
            quad.transform.SetParent(go.transform, false);
            quad.transform.localPosition = new Vector3(0f, 0f, 1f);
            _mirrorQuad = quad.transform;

            UpdateMirrorQuad();
            MelonLoader.MelonLogger.Msg("[YARG-VR] Desktop mirror enabled - the monitor now shows the headset view.");
        }

        /// <summary>Sizes the mirror quad so the eye texture fits (letterboxed) the window.</summary>
        private void UpdateMirrorQuad()
        {
            if (_mirrorQuad == null || _rtL == null || _mirrorCam == null)
            {
                return;
            }

            float dist = 1f;
            float frustumH = 2f * dist * Mathf.Tan(MirrorFov * 0.5f * Mathf.Deg2Rad);
            float frustumW = frustumH * _mirrorCam.aspect;
            float rtAspect = (float)_rtL.width / _rtL.height;

            float w = frustumH * rtAspect;
            float h = frustumH;
            if (w > frustumW)
            {
                w = frustumW;
                h = w / rtAspect;
            }

            _mirrorQuad.localScale = new Vector3(w, h, 1f);
        }

        private void DestroyMirror()
        {
            if (_mirrorMat != null)
            {
                UnityEngine.Object.Destroy(_mirrorMat);
                _mirrorMat = null;
            }
            if (_mirrorCam != null)
            {
                UnityEngine.Object.Destroy(_mirrorCam.gameObject);
                _mirrorCam = null;
            }
            _mirrorQuad = null;
        }

        #endregion

        #region Audio visualizer ring

        /// <summary>
        /// A ring of audio-reactive bars around the play space (the VR "room" around the game
        /// screen). Bars sit on the UI layer so the eye cameras render them; the spectrum comes
        /// from the master output (AudioListener.GetSpectrumData), so anything YARG plays drives
        /// them. Centered on the player's position at placement time and re-centered by F9.
        /// </summary>
        private void EnsureVisualizer()
        {
            if (!_settings.Visualizer.Value || _visualizer != null)
            {
                return;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            if (shader == null)
            {
                return; // extremely unlikely - skip silently
            }

            _visualizer = new GameObject("YARG-VR Visualizer");

            // v1.3.0: the ring is a CHILD of the room root - it surrounds the PLAYER (where
            // they stood at the last recenter) and inherits every root move automatically.
            // (The root is DontDestroyOnLoad, so the child needs no flag of its own.)
            EnsureRoomRoot();
            _visualizer.transform.SetParent(_roomRoot, false);

            _visBars = new Transform[VisBarCount];
            _visMats = new Material[VisBarCount];
            _visRenderers = new MeshRenderer[VisBarCount];
            _visBase = new Color[VisBarCount];
            _visAmp = new float[VisBarCount];
            _visBinLo = new int[VisBarCount];
            _visBinHi = new int[VisBarCount];
            _visSpectrum = new float[256];
            _visBass = new float[BassSpectrum.BinCount];

            for (int i = 0; i < VisBarCount; i++)
            {
                GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Collider col = bar.GetComponent<Collider>();
                if (col != null)
                {
                    UnityEngine.Object.Destroy(col); // no physics / raycast interference
                }
                bar.name = "Vis Bar " + i;
                bar.layer = 5; // "UI" - the layer the eye cameras always render
                bar.transform.parent = _visualizer.transform;
                _visBars[i] = bar.transform;
                _visRenderers[i] = bar.GetComponent<MeshRenderer>();

                Color c = Color.HSVToRGB((float)i / VisBarCount, 0.85f, 1f);
                _visBase[i] = c;
                Material m = new Material(shader);
                m.color = c * 0.25f;
                _visMats[i] = m;
                bar.GetComponent<MeshRenderer>().sharedMaterial = m;

                // Log-spaced FFT bands over YARG's BASS FFT2048 (1024 bins x ~21.5 Hz):
                // bar 0 starts at bin 1 (~21 Hz, sub-bass/kick) and bar 47 ends at bin 512
                // (~11 kHz) - the musically meaningful range.
                int lo = (int)Mathf.Floor(Mathf.Pow(512f, (float)i / VisBarCount));
                int hi = (int)Mathf.Floor(Mathf.Pow(512f, (float)(i + 1) / VisBarCount));
                _visBinLo[i] = Mathf.Clamp(lo, 1, 511);
                _visBinHi[i] = Mathf.Clamp(Mathf.Max(hi, lo + 1), 2, 512);
            }

            MelonLoader.MelonLogger.Msg(
                "[YARG-VR] Audio visualizer ring created (" + VisBarCount + " bars around the play space).");
        }

        /// <summary>
        /// v1.3.0: the ring SURROUNDS THE PLAYER again - centered on the room root, i.e. where
        /// the player stood at the last (re)center, and it rotates with the root's yaw so its
        /// "front" matches the screen direction. At radius 2.7 m the front arc passes BEHIND
        /// the ~2 m screen plane, so the screen occludes the bars dead ahead while the ring
        /// wraps around the player on the sides and behind (v1.2.2 pushed the whole ring 5+
        /// m out as a backdrop, which read as "the bars are not around me").
        /// Bar bases sit ~1.35 m below the head-height root (estimated floor level).
        /// </summary>
        private void PositionVisualizer()
        {
            if (_visualizer == null || _roomRoot == null)
            {
                return;
            }

            _visualizer.transform.localPosition = new Vector3(0f, -1.35f, 0f);
            _visualizer.transform.localRotation = Quaternion.identity;

            for (int i = 0; i < VisBarCount; i++)
            {
                if (_visBars[i] == null)
                {
                    continue;
                }
                float a = (float)i / VisBarCount * Mathf.PI * 2f;
                _visBars[i].localPosition = new Vector3(
                    Mathf.Cos(a) * VisRadius, _visBars[i].localScale.y * 0.5f, Mathf.Sin(a) * VisRadius);
            }
        }

        private void UpdateVisualizer(float dt)
        {
            if (_visualizer == null || _visBars == null)
            {
                return;
            }

            // 1) Preferred source: YARG's own audio engine. YARG plays everything through the
            //    native BASS library (WASAPI/ASIO) - Unity's AudioListener hears NONE of it
            //    - so we tap BASS directly for the FFT (the same call YARG itself uses for
            //    its whammy-pitch detector). This is why the bars were frozen before 1.2.1.
            bool gotSpectrum = false;
            float bassAvg = 0f;
            if (BassSpectrum.TryGetMagnitudes(_visBass))
            {
                gotSpectrum = true;
                if (!_visBassOkLogged)
                {
                    _visBassOkLogged = true;
                    MelonLoader.MelonLogger.Msg(
                        "[YARG-VR] Visualizer tapped YARG's audio mixer (BASS FFT) - bars follow the song.");
                }
            }
            else
            {
                // 2) Fallback: Unity's master mix (unused by YARG today, but harmless to try).
                try
                {
                    AudioListener.GetSpectrumData(_visSpectrum, 0, FFTWindow.BlackmanHarris);
                    gotSpectrum = true;
                }
                catch
                {
                    gotSpectrum = false; // no active AudioListener - bars idle
                }

                if (!_visBassMissingLogged && Time.frameCount > 300)
                {
                    _visBassMissingLogged = true;
                    MelonLoader.MelonLogger.Msg(
                        "[YARG-VR] Visualizer: no BASS mixer data yet - bars idle until a song or menu track is playing.");
                }
            }

            // Slow spin for a bit of life; bar heights react with fast attack / slow decay.
            // Local space: the ring is a child of the room root since v1.3.0.
            _visualizer.transform.Rotate(Vector3.up, 3f * dt, Space.Self);

            float gain = Mathf.Clamp(_settings.VisualizerGain.Value, 0.1f, 5f);

            for (int i = 0; i < VisBarCount; i++)
            {
                if (_visBars[i] == null)
                {
                    continue;
                }

                float target = 0f;
                if (gotSpectrum)
                {
                    if (_visBassOkLogged)
                    {
                        // BASS layout: average the band's FFT magnitudes (1024 raw bins).
                        float sum = 0f;
                        for (int b = _visBinLo[i]; b < _visBinHi[i]; b++)
                        {
                            sum += _visBass[b];
                        }
                        bassAvg = sum / Mathf.Max(1, _visBinHi[i] - _visBinLo[i]);
                    }
                    else
                    {
                        // AudioListener layout: 256 raw bins; scale band indexes by 1/4.
                        float sum = 0f;
                        int lo = _visBinLo[i] >> 2;
                        int hi = Mathf.Max(_visBinHi[i] >> 2, lo + 1);
                        for (int b = lo; b < hi; b++)
                        {
                            sum += _visSpectrum[b];
                        }
                        bassAvg = sum / Mathf.Max(1, hi - lo);
                    }

                    target = Mathf.Clamp01(Mathf.Sqrt(bassAvg) * 9f * gain);
                }

                _visAmp[i] = target > _visAmp[i] ? target : Mathf.Max(target, _visAmp[i] - dt * 2.2f);

                float h = 0.06f + _visAmp[i] * VisMaxHeight;
                float a = (float)i / VisBarCount * Mathf.PI * 2f;
                _visBars[i].localScale = new Vector3(0.16f, h, 0.16f);
                _visBars[i].localPosition = new Vector3(
                    Mathf.Cos(a) * VisRadius, h * 0.5f, Mathf.Sin(a) * VisRadius);
                _visMats[i].color = _visBase[i] * (0.25f + 0.85f * _visAmp[i]);
            }
        }

        private void DestroyVisualizer()
        {
            _visRenderers = null;
            if (_visMats != null)
            {
                for (int i = 0; i < _visMats.Length; i++)
                {
                    if (_visMats[i] != null)
                    {
                        UnityEngine.Object.Destroy(_visMats[i]);
                    }
                }
                _visMats = null;
            }
            if (_visualizer != null)
            {
                UnityEngine.Object.Destroy(_visualizer);
            }
            _visualizer = null;
            _visBars = null;
            _visBase = null;
            _visAmp = null;
            _visSpectrum = null;
            _visBass = null;
            _visBinLo = null;
            _visBinHi = null;
        }

        #endregion

        #region Visualizer occlusion (bars never visible behind the screens)

        /// <summary>
        /// v1.3.3: uGUI never writes depth, so the visualizer bars (drawn at ring radius 2.7 m,
        /// BEHIND the ~2 m screen) used to paint right over the menu - "bars poking through the
        /// menus". We cannot inject a depth-writing occluder without a custom shader, so instead
        /// every bar is ray-tested against every world-space screen each frame: if the ray from
        /// the head to the bar passes through a screen's rectangle, that bar is hidden
        /// (renderer disabled) for the frame. Bars in front of / beside / behind the PLAYER stay
        /// visible; only the arc genuinely behind a screen disappears.
        /// </summary>
        private void UpdateVisualizerOcclusion(Vector3 headPos)
        {
            if (_visualizer == null || _visBars == null || _visRenderers == null)
            {
                return;
            }

            bool occlusionOn = _settings.VisualizerOcclusion.Value;
            for (int i = 0; i < VisBarCount; i++)
            {
                MeshRenderer rend = _visRenderers[i];
                Transform bar = _visBars[i];
                if (rend == null || bar == null)
                {
                    continue;
                }

                bool visible = true;
                if (occlusionOn)
                {
                    visible = !IsPointHiddenBehindScreens(headPos, bar.position);
                }

                if (rend.enabled != visible)
                {
                    rend.enabled = visible;
                }
            }
        }

        /// <summary>
        /// True when the ray head -> point crosses any active world-space screen rectangle
        /// before reaching the point (i.e. the point is seen THROUGH that screen).
        /// Canvas +Z faces away from the viewer, so "behind the screen" = positive local Z side.
        /// The pop-out HUD plane (if built) is tested too - it is a world-space canvas as well.
        /// </summary>
        private bool IsPointHiddenBehindScreens(Vector3 headPos, Vector3 point)
        {
            for (int i = 0; i < _billboards.Count; i++)
            {
                Canvas c = _billboards[i].Canvas;
                if (c != null && c.isActiveAndEnabled &&
                    IsPointHiddenBehindRect(headPos, point, c.transform))
                {
                    return true;
                }
            }

            if (_hudPlaneCanvas != null && _hudPlaneCanvas.isActiveAndEnabled &&
                IsPointHiddenBehindRect(headPos, point, _hudPlaneCanvas.transform))
            {
                return true;
            }

            return false;
        }

        private static bool IsPointHiddenBehindRect(Vector3 headPos, Vector3 point, Transform t)
        {
            RectTransform rt = t as RectTransform;
            if (rt == null)
            {
                return false;
            }

            Vector3 n = t.forward; // canvas +Z points AWAY from the viewer
            Vector3 planePos = t.position;

            // Signed distances along the canvas normal (head should be on the -Z/front side,
            // the bar behind means it is on the +Z side).
            float dHead = Vector3.Dot(n, headPos - planePos);
            float dPoint = Vector3.Dot(n, point - planePos);
            if (dHead > -0.001f || dPoint <= 0.001f)
            {
                return false; // head not in front of this screen, or the point is not behind it
            }

            // Segment parameter where head->point crosses the screen plane.
            float s = -dHead / (dPoint - dHead); // (0, 1) by construction here
            Vector3 hit = headPos + (point - headPos) * s;

            // Rect test in canvas-local units (pixels); pivot was centered on conversion.
            Vector3 lp = t.InverseTransformPoint(hit);
            float halfW = rt.rect.width * 0.5f;
            float halfH = rt.rect.height * 0.5f;

            // Margin in canvas units: keeps the 0.16 m wide bar cubes from peeking at the edges.
            float scale = Mathf.Max(0.0001f, t.lossyScale.x);
            float margin = 0.25f / scale;

            return Mathf.Abs(lp.x) <= halfW + margin && Mathf.Abs(lp.y) <= halfH + margin;
        }

        #endregion

        #region Menu background surround (v1.3.3)

        private const float MenuSurroundRadius = 6f;
        private const int MenuSurroundSegments = 48;
        private const int MenuSurroundRings = 20;

        /// <summary>
        /// Shows/hides the surround sphere. It belongs to the MENU only: it is visible exactly
        /// while YARG's menu background camera is the bound world camera (menus, not songs -
        /// during a song the venue/stage takes the world over, per the original request), and
        /// MenuEnvSurround gates the whole feature. The sphere is a child of the room root, so
        /// F9 (re-place) carries it along and it stays locked to the room.
        /// </summary>
        private void UpdateMenuSurround()
        {
            bool want = _settings.MenuEnvSurround.Value && _worldCam != null &&
                        _menuBgCamera != null && _worldCam == _menuBgCamera;

            if (want && _menuSurroundGo == null)
            {
                EnsureMenuSurround();
            }

            if (_menuSurroundGo == null)
            {
                return;
            }

            if (_menuSurroundGo.activeSelf != want)
            {
                _menuSurroundGo.SetActive(want);
            }
        }

        private void EnsureMenuSurround()
        {
            Material mat = ResolveMenuBackgroundMaterial();
            if (mat == null)
            {
                if (!_menuSurroundFailedLogged)
                {
                    _menuSurroundFailedLogged = true;
                    MelonLoader.MelonLogger.Warning(
                        "[YARG-VR] Menu surround skipped: the menu background material was not found " +
                        "(expected 'Unlit/MenuBackground' on the MenuBackground prefab's Wall renderer).");
                }
                return;
            }

            Mesh mesh = BuildMenuSurroundMesh(MenuSurroundRadius);
            _menuSurroundMesh = mesh;

            EnsureRoomRoot();
            GameObject go = new GameObject("YARG-VR Menu Surround");
            go.layer = 5; // "UI" - the layer the eye cameras always render
            go.transform.SetParent(_roomRoot, false);
            go.transform.localPosition = Vector3.zero; // centered on the anchored head position

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _menuSurroundGo = go;

            MelonLoader.MelonLogger.Msg("[YARG-VR] Menu background sphere created (r=" +
                MenuSurroundRadius.ToString("F1") + " m, YARG's own menu gradient material) - the menu " +
                "background now surrounds you in every direction (menus only; F9 re-places it).");
        }

        /// <summary>
        /// Finds YARG's animated menu background material. Ground truth from YARG's repo:
        /// MenuBackground.prefab = MenuBackground root with children [Directional Light,
        /// Global Volume, Camera Container, Wall]; the Wall MeshRenderer carries
        /// Assets/Art/Materials/Menu/MenuBackground.mat (shader "Unlit/MenuBackground").
        /// Prefer that exact shader; fall back to Shader.Find for resilience.
        /// </summary>
        private Material ResolveMenuBackgroundMaterial()
        {
            Component comp = _menuBgComponent as Component;
            if (comp != null)
            {
                MeshRenderer[] rends = comp.GetComponentsInChildren<MeshRenderer>(true);
                MeshRenderer fallback = null;
                for (int i = 0; i < rends.Length; i++)
                {
                    Material m = rends[i] != null && rends[i].sharedMaterial != null
                        ? rends[i].sharedMaterial
                        : null;
                    if (m == null || m.shader == null)
                    {
                        continue;
                    }

                    if (m.shader.name == "Unlit/MenuBackground")
                    {
                        return m;
                    }

                    if (fallback == null)
                    {
                        fallback = rends[i];
                    }
                }

                if (fallback != null)
                {
                    return fallback.sharedMaterial;
                }
            }

            Shader shader = Shader.Find("Unlit/MenuBackground");
            if (shader != null)
            {
                _menuSurroundMatOwned = true;
                return new Material(shader);
            }

            return null;
        }

        /// <summary>
        /// Builds an inward-facing sphere (elevation -80 deg .. +90 deg) so the player is fully
        /// enclosed, including overhead. The menu gradient shader works in UV space (animated
        /// color points orbiting around UV center), so u wraps the full 360 degrees around the
        /// player and v runs floor-to-zenith. Triangles are wound to face the INSIDE (Unity
        /// front faces are clockwise as seen by the viewer; verified against a viewer at the
        /// sphere center looking +X with screen-right = -Z).
        /// </summary>
        private static Mesh BuildMenuSurroundMesh(float radius)
        {
            const float minElevDeg = -80f;
            const float maxElevDeg = 90f;

            int columns = MenuSurroundSegments + 1; // duplicated seam column for clean UVs
            int rows = MenuSurroundRings + 1;
            Vector3[] verts = new Vector3[columns * rows];
            Vector2[] uvs = new Vector2[verts.Length];
            int[] tris = new int[MenuSurroundSegments * MenuSurroundRings * 6];

            int v = 0;
            for (int j = 0; j < rows; j++)
            {
                float tv = (float)j / MenuSurroundRings;
                float elev = Mathf.Lerp(minElevDeg, maxElevDeg, tv) * Mathf.Deg2Rad;
                float y = Mathf.Sin(elev);
                float r = Mathf.Cos(elev);
                for (int i = 0; i < columns; i++)
                {
                    float tu = (float)i / MenuSurroundSegments;
                    float a = tu * Mathf.PI * 2f;
                    verts[v] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r) * radius;
                    uvs[v] = new Vector2(tu, tv);
                    v++;
                }
            }

            int t = 0;
            for (int j = 0; j < MenuSurroundRings; j++)
            {
                for (int i = 0; i < MenuSurroundSegments; i++)
                {
                    int a0 = j * columns + i;
                    int a1 = a0 + 1;
                    int b0 = a0 + columns;
                    int b1 = b0 + 1;
                    tris[t++] = a0; tris[t++] = a1; tris[t++] = b1;
                    tris[t++] = a0; tris[t++] = b1; tris[t++] = b0;
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "YARG-VR Menu Surround";
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        private void DestroyMenuSurround()
        {
            if (_menuSurroundGo != null)
            {
                UnityEngine.Object.Destroy(_menuSurroundGo);
            }
            _menuSurroundGo = null;

            if (_menuSurroundMesh != null)
            {
                UnityEngine.Object.Destroy(_menuSurroundMesh);
            }
            _menuSurroundMesh = null;

            if (_menuSurroundMatOwned && _menuSurroundMat != null)
            {
                UnityEngine.Object.Destroy(_menuSurroundMat);
            }
            _menuSurroundMat = null;
            _menuSurroundMatOwned = false;
        }

        #endregion

        #region Render hooks (per-eye venue/highway state + submission)

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            _beginEvents++;

            if (camera == _venueCam)
            {
                // YARG's own begin hook has already run for this camera (subscription order):
                // venue shader globals are set and the alpha-fix pass is enqueued for YARG's
                // left-eye render. Render the right-eye CLONE now, while that state is live;
                // the normal pipeline pass afterwards produces the left eye untouched.
                if (_anchored && !_venueStereoBroken && _venueOutput != null && _venueClone != null &&
                    _settings.StereoVenue.Value)
                {
                    RenderVenueRightEye(context, camera);
                }
                return;
            }

            if (camera == _eyeCamL)
            {
                // Left eye draws the canvas with YARG's own venue texture. Track it here so we
                // notice when YARG recreates it (window resize).
                if (_venueOutput != null)
                {
                    Texture t = _venueOutput.texture;
                    if (!ReferenceEquals(t, _venueRightRt))
                    {
                        _yargVenueTexture = t as RenderTexture;
                    }
                }

                CaptureHighwayGlobals();
                ApplyHighwayStereo(eyeLeft: true);
                return;
            }

            if (camera == _eyeCamR)
            {
                // Right eye draws with our right-eye venue texture.
                if (_venueOutput != null && _venueRightRt != null)
                {
                    _venueOutput.texture = _venueRightRt;
                }
                ApplyHighwayStereo(eyeLeft: false);
                return;
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == _eyeCamL)
            {
                _eyeEndEventsL++;
                OpenVrRuntime.SubmitEye(EVREye.Eye_Left, _rtL);
            }
            else if (camera == _eyeCamR)
            {
                _eyeEndEventsR++;
                OpenVrRuntime.SubmitEye(EVREye.Eye_Right, _rtR);
            }
        }

        private void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            // Restore YARG's own state for the desktop view and the next frame.
            if (_venueOutput != null && _yargVenueTexture != null &&
                ReferenceEquals(_venueOutput.texture, _venueRightRt))
            {
                _venueOutput.texture = _yargVenueTexture;
            }
            RestoreHighwayGlobals();

            // Backstop: guarantee both eyes reach the compositor even if the per-camera hooks
            // misfire (the per-eye once-per-frame guard makes these no-ops when they did not).
            if (!OpenVrRuntime.SubmittedThisFrame(EVREye.Eye_Left))
            {
                _backstopSubmits++;
                OpenVrRuntime.SubmitEye(EVREye.Eye_Left, _rtL);
            }
            if (!OpenVrRuntime.SubmittedThisFrame(EVREye.Eye_Right))
            {
                _backstopSubmits++;
                OpenVrRuntime.SubmitEye(EVREye.Eye_Right, _rtR);
            }
        }

        #endregion

        #region Venue right-eye rendering

        private void RenderVenueRightEye(ScriptableRenderContext context, Camera venue)
        {
            MethodInfo mi = ResolveRenderSingleCamera();
            if (mi == null)
            {
                _venueStereoBroken = true;
                WarnOnce(ref _venueStereoWarned,
                    "URP RenderSingleCamera not found via reflection - the stage will render mono.");
                return;
            }

            try
            {
                if (!EnsureVenueRightTexture(venue.targetTexture))
                {
                    _venueStereoBroken = true;
                    WarnOnce(ref _venueStereoWarned,
                        "Venue camera has no render target yet - the stage will render mono this session.");
                    return;
                }

                // The clone carries its own transform (right-eye pose, set in LateTick) and its
                // own render target. YARG's hooks ignore it, and it is never rendered by the
                // normal pipeline, so YARG's left-eye state is completely untouched.
                _venueClone.targetTexture = _venueRightRt;
                mi.Invoke(null, new object[] { context, _venueClone });
            }
            catch (Exception e)
            {
                _venueStereoBroken = true;
                WarnOnce(ref _venueStereoWarned,
                    "Venue right-eye render failed, falling back to mono stage: " + e.Message);
            }
        }

        /// <summary>Copies YARG's venue camera configuration onto the right-eye clone.</summary>
        private void SyncVenueClone(Camera venue)
        {
            // Bit 2 ("Ignore Raycast") is reserved for the desktop-mirror quad - never
            // let it leak into the stage render (or anywhere else).
            _venueClone.cullingMask = venue.cullingMask & ~(1 << 2);
            _venueClone.clearFlags = venue.clearFlags;
            _venueClone.backgroundColor = venue.backgroundColor;
            _venueClone.nearClipPlane = venue.nearClipPlane;
            _venueClone.farClipPlane = venue.farClipPlane;
            _venueClone.fieldOfView = venue.fieldOfView;
            _venueClone.aspect = venue.aspect;
            _venueClone.allowMSAA = venue.allowMSAA;
            _venueClone.useOcclusionCulling = false;
            _venueClone.stereoTargetEye = StereoTargetEyeMask.None;

            // Mirror URP-specific camera settings (post-processing, AA mode) so both eyes look
            // identical. The URP data component is resolved via reflection to keep the build
            // free of a hard URP dependency.
            if (!_urpCameraDataTypeResolved)
            {
                _urpCameraDataTypeResolved = true;
                try
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length; i++)
                    {
                        _urpCameraDataType = assemblies[i].GetType(
                            "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                        if (_urpCameraDataType != null)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // Best effort.
                }
            }

            if (_urpCameraDataType == null)
            {
                return;
            }

            try
            {
                Component origData = venue.GetComponent(_urpCameraDataType);
                if (origData == null)
                {
                    return;
                }

                if (_venueCloneUrpData == null)
                {
                    _venueCloneUrpData = _venueCloneGo.AddComponent(_urpCameraDataType);
                }

                for (int i = 0; i < _urpDataCopyProps.Length; i++)
                {
                    PropertyInfo prop = _urpCameraDataType.GetProperty(_urpDataCopyProps[i]);
                    if (prop != null && prop.CanRead && prop.CanWrite)
                    {
                        prop.SetValue(_venueCloneUrpData, prop.GetValue(origData, null), null);
                    }
                }
            }
            catch
            {
                // URP data copying is best-effort; defaults are usually correct.
            }
        }

        private bool EnsureVenueRightTexture(RenderTexture template)
        {
            if (template == null)
            {
                return false;
            }

            if (_venueRightRt != null &&
                _venueRightRt.width == template.width &&
                _venueRightRt.height == template.height &&
                _venueRightRt.format == template.format)
            {
                return true;
            }

            RenderTexture old = _venueRightRt;
            RenderTextureDescriptor desc = template.descriptor;
            _venueRightRt = new RenderTexture(desc)
            {
                name = "YARG-VR Venue Right Eye",
            };
            _venueRightRt.Create();

            // If the RawImage still points at our old texture, put YARG's back so the swap
            // logic re-learns the correct left texture on the next frame.
            if (_venueOutput != null && old != null && ReferenceEquals(_venueOutput.texture, old))
            {
                _venueOutput.texture = _yargVenueTexture;
            }

            if (old != null)
            {
                old.Release();
                UnityEngine.Object.Destroy(old);
            }
            return true;
        }

        private void CreateVenueClone()
        {
            if (_venueClone != null)
            {
                return;
            }

            _venueCloneGo = new GameObject("YARG-VR Venue Eye R");
            _venueClone = _venueCloneGo.AddComponent<Camera>();
            _venueClone.enabled = false; // rendered only via RenderSingleCamera
            UnityEngine.Object.DontDestroyOnLoad(_venueCloneGo);
            _venueCloneGo.hideFlags = HideFlags.HideAndDontSave;
        }

        private static MethodInfo ResolveRenderSingleCamera()
        {
            if (_renderSingleCameraResolved)
            {
                return _renderSingleCameraMethod;
            }

            _renderSingleCameraResolved = true;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type t = assemblies[i].GetType("UnityEngine.Rendering.Universal.UniversalRenderPipeline");
                    if (t == null)
                    {
                        continue;
                    }

                    _renderSingleCameraMethod = t.GetMethod("RenderSingleCamera",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(ScriptableRenderContext), typeof(Camera) }, null);
                    if (_renderSingleCameraMethod != null)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Reflection resolution is best-effort; venue stereo degrades to mono.
            }

            return _renderSingleCameraMethod;
        }

        #endregion

        #region Highway per-eye reprojection (global shader matrix swap)

        private void CaptureHighwayGlobals()
        {
            _hwGlobalsCaptured = false;
            if (_highwayStereoBroken || !_settings.StereoHighways.Value)
            {
                return;
            }

            try
            {
                Matrix4x4[] v = Shader.GetGlobalMatrixArray(HwViewName);
                if (v == null || v.Length == 0)
                {
                    return; // highway system has not initialized its globals yet
                }

                Matrix4x4[] iv = Shader.GetGlobalMatrixArray(HwInvViewName);
                if (iv == null || iv.Length != v.Length)
                {
                    return;
                }

                if (_hwViewOrig == null || _hwViewOrig.Length != v.Length)
                {
                    _hwViewOrig = new Matrix4x4[v.Length];
                    _hwInvViewOrig = new Matrix4x4[v.Length];
                    _hwViewL = new Matrix4x4[v.Length];
                    _hwInvViewL = new Matrix4x4[v.Length];
                    _hwViewR = new Matrix4x4[v.Length];
                    _hwInvViewR = new Matrix4x4[v.Length];
                }

                Array.Copy(v, _hwViewOrig, v.Length);
                Array.Copy(iv, _hwInvViewOrig, v.Length);
                _hwGlobalsCaptured = true;
            }
            catch (Exception e)
            {
                _highwayStereoBroken = true;
                WarnOnce(ref _highwayStereoWarned,
                    "Highway stereo disabled (global matrix read failed): " + e.Message);
            }
        }

        /// <summary>
        /// Offsets YARG's highway reprojection view matrices by the current eye's IPD vector
        /// (in world space, along each highway view's own right axis). The reprojection
        /// material reads these globals while the RawImage is drawn, so swapping them between
        /// the two eye-camera renders yields true stereo note highways.
        /// </summary>
        private void ApplyHighwayStereo(bool eyeLeft)
        {
            if (_highwayStereoBroken || !_hwGlobalsCaptured)
            {
                return;
            }

            try
            {
                Vector3 offsetLocal = eyeLeft ? OpenVrRuntime.EyeOffsetLeft : OpenVrRuntime.EyeOffsetRight;
                Matrix4x4[] viewOut = eyeLeft ? _hwViewL : _hwViewR;
                Matrix4x4[] invOut = eyeLeft ? _hwInvViewL : _hwInvViewR;

                for (int i = 0; i < _hwViewOrig.Length; i++)
                {
                    // invView maps view->world; its rotation carries the eye offset into world space.
                    Vector3 d = _hwInvViewOrig[i].MultiplyVector(offsetLocal);
                    viewOut[i] = _hwViewOrig[i] * Matrix4x4.Translate(-d);
                    invOut[i] = Matrix4x4.Translate(d) * _hwInvViewOrig[i];
                }

                Shader.SetGlobalMatrixArray(HwViewName, viewOut);
                Shader.SetGlobalMatrixArray(HwInvViewName, invOut);
            }
            catch (Exception e)
            {
                _highwayStereoBroken = true;
                WarnOnce(ref _highwayStereoWarned,
                    "Highway stereo disabled (global matrix write failed): " + e.Message);
            }
        }

        private void RestoreHighwayGlobals()
        {
            if (_highwayStereoBroken || !_hwGlobalsCaptured)
            {
                return;
            }

            try
            {
                Shader.SetGlobalMatrixArray(HwViewName, _hwViewOrig);
                Shader.SetGlobalMatrixArray(HwInvViewName, _hwInvViewOrig);
            }
            catch
            {
                // Restore is best-effort; YARG re-sets the globals every frame anyway.
            }
        }

        #endregion

        #region Watchdog

        /// <summary>
        /// Last-resort submission path, called from MelonMod.OnLateUpdate. If nothing has
        /// reached the compositor for ~half a second, log a full diagnostic dump once and then
        /// submit both eye textures directly (one frame of latency - far better than a black HMD).
        /// </summary>
        public void EnsureSubmitting()
        {
            if (!IsActive || _rtL == null || _rtR == null || !_hooked)
            {
                return;
            }

            if (Time.unscaledTime < _watchdogGraceUntil)
            {
                return;
            }

            if (OpenVrRuntime.FramesSinceSuccessfulSubmit < 45)
            {
                return; // the render-hook paths are doing their job
            }

            if (!_watchdogDiagnosticsLogged)
            {
                _watchdogDiagnosticsLogged = true;
                MelonLoader.MelonLogger.Warning("[YARG-VR] No frame has reached the SteamVR compositor " +
                    "(" + (OpenVrRuntime.FramesSinceSuccessfulSubmit == int.MaxValue
                        ? "never"
                        : OpenVrRuntime.FramesSinceSuccessfulSubmit + " frames") + "). " +
                    "Enabling fallback submission.\n" +
                    "[YARG-VR] Diagnostics: " + OpenVrRuntime.GetSubmitDiagnostics() +
                    ", beginEvents=" + _beginEvents +
                    ", eyeEndEvents(L/R)=" + _eyeEndEventsL + "/" + _eyeEndEventsR +
                    ", backstopSubmits=" + _backstopSubmits +
                    ", pipeline=" + (GraphicsSettings.currentRenderPipeline != null ? "SRP/URP" : "Built-in"));
            }

            if (!OpenVrRuntime.SubmittedThisFrame(EVREye.Eye_Left))
            {
                _backstopSubmits++;
                OpenVrRuntime.SubmitEye(EVREye.Eye_Left, _rtL);
            }
            if (!OpenVrRuntime.SubmittedThisFrame(EVREye.Eye_Right))
            {
                _backstopSubmits++;
                OpenVrRuntime.SubmitEye(EVREye.Eye_Right, _rtR);
            }
        }

        #endregion

        #region Math

        /// <summary>Strips roll (and guards the degenerate straight-up/down case). Yaw+pitch only.</summary>
        private static Quaternion FlattenRoll(Quaternion q)
        {
            Vector3 e = q.eulerAngles;
            if (e.x > 180f) e.x -= 360f;
            if (e.y > 180f) e.y -= 360f;
            // Keep pitch within a sane range so the Euler conversion never flips.
            e.x = Mathf.Clamp(e.x > 90f ? e.x - 360f : e.x, -85f, 85f);
            e.z = 0f;
            return Quaternion.Euler(e.x, e.y, 0f);
        }

        private static void WarnOnce(ref bool warned, string message)
        {
            if (warned)
            {
                return;
            }
            warned = true;
            MelonLoader.MelonLogger.Warning("[YARG-VR] " + message);
        }

        #endregion
    }
}
