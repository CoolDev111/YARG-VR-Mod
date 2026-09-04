using System;
using System.Reflection;
using UnityEngine;

namespace YargVr
{
    /// <summary>
    /// The only place where YARG-VR touches YARG's own code - and it does so purely via
    /// reflection, so the mod keeps working across YARG updates as long as these two
    /// members exist:
    ///
    ///   type:  YARG.Venue.VenueCamera.CameraManager  (Assembly-CSharp, v0.15.0)
    ///   prop:  public Camera CurrentCamera { get; }   <- the venue/stage camera that is
    ///                                                    currently active (camera cuts)
    ///
    /// Everything else the mod manipulates is plain Unity API (Cameras, Canvases, RenderTextures).
    /// If YARG renames these, the mod logs a warning and degrades gracefully: the VR view then
    /// shows the composite canvas (HUD, highways, venue RawImage) but the stage camera can no
    /// longer be driven 1:1 by the HMD.
    /// </summary>
    internal static class YargBridge
    {
        private const string ManagerTypeName = "YARG.Venue.VenueCamera.CameraManager, Assembly-CSharp";
        private const string CurrentCameraProperty = "CurrentCamera";

        private static bool _resolved;
        private static Type _managerType;
        private static PropertyInfo _currentCameraProp;
        private static bool _warned;

        /// <summary>Returns the venue camera that YARG currently renders the stage with, or null.</summary>
        public static Camera GetCurrentVenueCamera()
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    _managerType = Type.GetType(ManagerTypeName);
                    if (_managerType != null)
                    {
                        _currentCameraProp = _managerType.GetProperty(CurrentCameraProperty,
                            BindingFlags.Public | BindingFlags.Instance);
                    }
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Warning("[YARG-VR] Could not reflect YARG camera manager: " + e.Message);
                }

                if (_managerType == null || _currentCameraProp == null)
                {
                    MelonLoader.MelonLogger.Warning(
                        "[YARG-VR] YARG type '" + ManagerTypeName + "' or its '" + CurrentCameraProperty +
                        "' property was not found. Venue camera takeover is disabled (YARG version changed?).");
                }
            }

            if (_managerType == null || _currentCameraProp == null)
            {
                return null;
            }

            try
            {
                object manager = UnityEngine.Object.FindFirstObjectByType(_managerType);
                if (manager == null)
                {
                    return null;
                }

                return _currentCameraProp.GetValue(manager, null) as Camera;
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    MelonLoader.MelonLogger.Warning("[YARG-VR] Reading CurrentCamera failed: " + e.Message);
                }
                return null;
            }
        }
    }
}
