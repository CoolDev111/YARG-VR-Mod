using System;
using System.Runtime.InteropServices;
using System.Text;

namespace YargVr
{
    /// <summary>
    /// v1.3.15: targeted Windows Plug-and-Play re-scan, used when the guitar is missing
    /// from Unity's device table. The Riff Master's dongle (PDP, VID_0E6F) occasionally
    /// stays alive at the USB level but its HID/GameInput children fail to (re)start -
    /// Windows then never hands the guitar to Unity's InputSystem and nothing in YARG can
    /// claim it ("0/0 devices claimed"). CM_Reenumerate_DevNode on the dongle node is the
    /// programmatic equivalent of Device Manager's "Scan for hardware changes" on that
    /// node - a replug without physically touching the USB port. Harmless by construction:
    /// nothing is disabled, removed or reinstalled; the only state-changing calls are
    /// ENABLE on a disabled node and a re-scan request. Never runs while an instrument is
    /// present (checked by the caller), so it can never disturb a working session.
    /// </summary>
    internal static class WindowsDeviceNudge
    {
        private static readonly Guid UsbClassGuid = new Guid("36FC9E60-C465-11CF-8056-444553540000");
        private static readonly Guid HidClassGuid = new Guid("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

        private const uint DigcfPresent = 0x00000002;
        private const uint CmProbDisabled = 0x00000016; // 22 - CM_PROB_DISABLED

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer, uint bufferLen, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Enable_DevNode(uint devInst, uint flags);

        /// <summary>
        /// Decodes a failed re-scan return code. Observed on the user's rig: rc=51 from a
        /// non-elevated process - PnP mutation (CM_Reenumerate_DevNode) is rejected for
        /// callers without elevated rights; Device Manager does the same operation from
        /// an elevated context. If the code is ever something else, print it raw.
        /// </summary>
        private static string DescribeRc(int rc)
        {
            if (rc == 51)
            {
                return " - ACCESS DENIED (needs elevated rights): the in-game re-scan only works when YARG is " +
                       "started via right-click -> Run as administrator. Skipping it is harmless.";
            }
            return " - error code " + rc + ".";
        }

        /// <summary>Runs the re-scan and logs everything it saw and did.</summary>
        public static void Nudge(MelonLoader.MelonLogger.Instance log)
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    log.Msg("[YARG-VR][watch] device re-scan skipped - not running on Windows.");
                    return;
                }

                int usbMatches = ScanAndNudgeClass(log, UsbClassGuid, "USB");
                if (usbMatches < 0)
                {
                    return; // scan API failed - already logged
                }

                if (usbMatches == 0)
                {
                    int hidMatches = ScanAndNudgeClass(log, HidClassGuid, "HID");
                    if (hidMatches <= 0)
                    {
                        log.Msg("[YARG-VR][watch] re-scan found NO PDP (VID_0E6F) device node present in Windows at all - " +
                            "the guitar/dongle is not visible to the OS. Check: guitar powered on (press the Xbox " +
                            "button until it lights), dongle seated (try another USB port), then press F6 again.");
                    }
                }
            }
            catch (Exception e)
            {
                log.Msg("[YARG-VR][watch] device re-scan failed: " + e.Message);
            }
        }

        /// <summary>
        /// Enumerates the present devices of one PnP class and re-scans every VID_0E6F
        /// node. For USB-class nodes the re-scan targets the node itself (re-enumerates
        /// its HID/GameInput children); for HID-class nodes it targets the parent (the
        /// dongle). Returns the number of matches (-1 on API failure).
        /// </summary>
        private static int ScanAndNudgeClass(MelonLoader.MelonLogger.Instance log, Guid classGuid, string label)
        {
            IntPtr set = IntPtr.Zero;
            try
            {
                set = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
                if (set == IntPtr.Zero || set == (IntPtr)(-1))
                {
                    log.Msg("[YARG-VR][watch] device re-scan: SetupDiGetClassDevs failed (error " + Marshal.GetLastWin32Error() + ").");
                    return -1;
                }

                int matches = 0;
                for (uint index = 0; index < 256; index++)
                {
                    var data = new SP_DEVINFO_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, index, ref data))
                    {
                        break; // end of the present-device list
                    }

                    var id = new StringBuilder(512);
                    if (CM_Get_Device_ID(data.DevInst, id, (uint)id.Capacity, 0) != 0)
                    {
                        continue;
                    }

                    string deviceId = id.ToString();
                    if (deviceId.IndexOf("VID_0E6F", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matches++;

                    uint status = 0, problem = 0;
                    string statusText = "";
                    if (CM_Get_DevNode_Status(out status, out problem, data.DevInst, 0) == 0)
                    {
                        statusText = " status=0x" + status.ToString("X") +
                                     (problem == 0 ? "" : " problem=" + problem +
                                         (problem == CmProbDisabled ? " (DISABLED)" : " (has a problem code)"));
                    }

                    if (problem == CmProbDisabled)
                    {
                        int erc = CM_Enable_DevNode(data.DevInst, 0);
                        log.Msg("[YARG-VR][watch] re-scan: " + label + " node " + deviceId + statusText +
                            " -> node was DISABLED, enable request sent (rc=" + erc + ").");
                        continue;
                    }

                    uint target = data.DevInst;
                    if (label != "USB")
                    {
                        uint parent;
                        if (CM_Get_Parent(out parent, data.DevInst, 0) == 0)
                        {
                            target = parent;
                        }
                    }

                    int rrc = CM_Reenumerate_DevNode(target, 0);
                    log.Msg("[YARG-VR][watch] re-scan: " + label + " node " + deviceId + statusText +
                        " -> child re-scan requested (rc=" + rrc + ")." +
                        (rrc == 0 ? " If the guitar now enumerates, it will be logged and auto-claimed within ~2 s." : DescribeRc(rrc)));
                }

                if (matches > 0)
                {
                    log.Msg("[YARG-VR][watch] re-scan: " + matches + " PDP (VID_0E6F) node(s) in the " + label + " class processed.");
                }
                return matches;
            }
            finally
            {
                if (set != IntPtr.Zero && set != (IntPtr)(-1))
                {
                    SetupDiDestroyDeviceInfoList(set);
                }
            }
        }
    }
}
