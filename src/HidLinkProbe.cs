using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace YargVr
{
    /// <summary>
    /// v1.3.16: reads the Riff Master dongle's HID interfaces DIRECTLY (read-only,
    /// non-exclusive) to answer the one question the rest of the stack cannot: is the
    /// guitar actually sending data to Windows?
    ///
    /// What the v1.3.15 log proved: the dongle is present and HEALTHY at the PnP level
    /// (HID\VID_0E6F&PID_0248&IG_00 + USB\VID_0E6F&PID_0248&IG_00, status 0x180200A =
    /// started + driver loaded, problem code 0), the GameInput runtime loads fine
    /// ("present"), XInput never shows this guitar (it is a GameInput-only device), and
    /// Unity's InputSystem table holds nothing. The layer between "dongle alive" and
    /// "guitar linked" is only visible in the dongle's own HID reports:
    ///   - reports flow while the user presses frets/strum  ->  the guitar IS linked and
    ///     delivering data; the drop is in the GameInput/Unity layer and the mod can
    ///     bridge the reports into YARG directly (next round).
    ///   - zero reports  ->  the guitar is NOT linked to the dongle at all (power /
    ///     batteries / re-pairing - user-side fix, now provable instead of guessed).
    ///   - interfaces unopenable  ->  another component holds them exclusively.
    ///
    /// Safety: only VID_0E6F interfaces are ever read; handles are opened with
    /// GENERIC_READ shared read/write; NO output reports are ever sent; windows run only
    /// while the guitar is absent from Unity's device table, so a working input path is
    /// never touched.
    /// </summary>
    internal static class HidLinkProbe
    {
        private static readonly Guid HidInterfaceGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceinterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;
        private const int MaxInterfaces = 16;

        // 0 = idle, 1 = reading window open, 2 = draining (cancel issued, results pending)
        private static int _phase;
        private static float _phaseAt;
        private static volatile bool _stop;
        private static volatile int _reports;
        private static volatile string _firstReportHex = "";
        private static MelonLoader.MelonLogger.Instance _log;
        private static readonly object StateLock = new object();
        private static readonly List<IntPtr> _openHandles = new List<IntPtr>();
        private static readonly List<string> _interfaceResults = new List<string>();
        private static string _lastSummary = "n/a yet";

        /// <summary>One-line result of the last liveness window (for the watchdog status lines).</summary>
        public static string LastSummary
        {
            get { return _lastSummary; }
        }

        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        /// <summary>
        /// Starts a 5 s read-only liveness window on every present PDP (VID_0E6F) HID
        /// interface. No-op when one is already running or not on Windows. Safe at any
        /// time: read-only, shared, and only while the guitar is absent from Unity.
        /// </summary>
        public static void StartWindow(MelonLoader.MelonLogger.Instance log, string reason)
        {
            if (!IsWindows || _phase != 0)
            {
                return;
            }

            try
            {
                List<string> paths = EnumerateHidInterfaces(true, MaxInterfaces);
                if (paths.Count == 0)
                {
                    _lastSummary = "dongle HID interface absent";
                    log.Msg("[YARG-VR][hid] no PDP (VID_0E6F) HID interface is present to read - the dongle itself is gone from Windows (replug it).");
                    return;
                }

                _phase = 1;
                _stop = false;
                _reports = 0;
                _firstReportHex = "";
                _log = log;
                lock (StateLock)
                {
                    _openHandles.Clear();
                    _interfaceResults.Clear();
                }
                _phaseAt = Time.unscaledTime + 5f;

                log.Msg("[YARG-VR][hid] reading the dongle's HID interface(s) directly for 5 s (" + reason +
                    ") - PRESS BUTTONS ON THE GUITAR NOW.");

                foreach (string p in paths)
                {
                    string path = p; // capture per iteration
                    var t = new Thread(delegate() { ReadLoop(path); });
                    t.IsBackground = true;
                    t.Start();
                }
            }
            catch (Exception e)
            {
                _phase = 0;
                log.Msg("[YARG-VR][hid] liveness probe failed to start: " + e.Message);
            }
        }

        /// <summary>Drives the window phases. Called every frame from DeviceProbe.Tick.</summary>
        public static void Tick()
        {
            try
            {
                if (_phase == 1 && Time.unscaledTime >= _phaseAt)
                {
                    // Window over: stop the threads. A blocking ReadFile only wakes via
                    // CancelIoEx (cancels outstanding I/O for the handle from any thread);
                    // the threads then exit and close their own handles.
                    _stop = true;
                    IntPtr[] handles;
                    lock (StateLock)
                    {
                        handles = _openHandles.ToArray();
                    }
                    foreach (IntPtr h in handles)
                    {
                        CancelIoEx(h, IntPtr.Zero);
                    }
                    _phase = 2;
                    _phaseAt = Time.unscaledTime + 0.5f;
                }
                else if (_phase == 2 && Time.unscaledTime >= _phaseAt)
                {
                    Finish();
                }
            }
            catch
            {
                _phase = 0;
            }
        }

        private static void ReadLoop(string path)
        {
            IntPtr h = IntPtr.Zero;
            int reports = 0;
            string openErr = null;
            string product = "";
            try
            {
                h = CreateFileW(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h == IntPtr.Zero || h.ToInt64() == -1)
                {
                    openErr = "error " + Marshal.GetLastWin32Error();
                }
                else
                {
                    lock (StateLock)
                    {
                        _openHandles.Add(h);
                    }
                    product = GetProductString(h);

                    var buf = new byte[64];
                    uint got;
                    while (!_stop)
                    {
                        if (!ReadFile(h, buf, (uint)buf.Length, out got, IntPtr.Zero))
                        {
                            break; // cancelled (window over) or device gone
                        }

                        reports++;
                        int total = Interlocked.Increment(ref _reports);
                        if (total == 1)
                        {
                            var hex = new StringBuilder();
                            for (int i = 0; i < got && i < 16; i++)
                            {
                                hex.Append(buf[i].ToString("X2")).Append(' ');
                            }
                            _firstReportHex = hex.ToString();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                openErr = openErr ?? e.Message;
            }
            finally
            {
                lock (StateLock)
                {
                    _openHandles.Remove(h);
                    _interfaceResults.Add("    " + PathTail(path) +
                        (product.Length > 0 ? " product='" + product + "'" : "") +
                        (openErr != null ? " : open FAILED (" + openErr + ")" : " : " + reports + " reports"));
                }
                if (h != IntPtr.Zero && h.ToInt64() != -1)
                {
                    CloseHandle(h);
                }
            }
        }

        private static void Finish()
        {
            _phase = 0;
            var log = _log;
            _log = null;
            if (log == null)
            {
                return;
            }

            string[] results;
            lock (StateLock)
            {
                results = _interfaceResults.ToArray();
            }

            int opened = 0;
            foreach (string r in results)
            {
                if (r.IndexOf("open FAILED", StringComparison.Ordinal) < 0)
                {
                    opened++;
                }
            }

            if (_reports > 0)
            {
                _lastSummary = _reports + " reports/5 s";
                log.Msg("[YARG-VR][hid] liveness result: " + _reports + " input reports in 5 s (first report bytes: " +
                    _firstReportHex + ") - THE GUITAR IS DELIVERING DATA TO WINDOWS. The drop is in the GameInput/Unity " +
                    "layer; send this log - the mod can bridge the reports into YARG directly.");
            }
            else if (opened > 0)
            {
                _lastSummary = "no reports/5 s";
                log.Msg("[YARG-VR][hid] liveness result: the dongle's HID interface(s) opened but produced ZERO reports in 5 s " +
                    "while you pressed buttons - the guitar is NOT sending data to Windows. Power it on (Xbox button until it " +
                    "lights), check/charge the batteries, and re-pair (hold sync on the guitar AND on the dongle).");
            }
            else
            {
                _lastSummary = "interfaces not openable";
                log.Msg("[YARG-VR][hid] liveness result: the dongle's HID interface(s) could not be opened - they are held " +
                    "exclusively by another component (likely the GameInput runtime). Send this log.");
            }

            foreach (string r in results)
            {
                log.Msg("[YARG-VR][hid]" + r);
            }
        }

        /// <summary>
        /// F7/probe dump: lists EVERY present HID interface with its product string
        /// (zero-access handles, nothing is read). A linked guitar often exposes extra or
        /// renamed collections - this makes that directly visible.
        /// </summary>
        public static void DumpAllInterfaces(MelonLoader.MelonLogger.Instance log)
        {
            if (!IsWindows)
            {
                return;
            }

            try
            {
                List<string> all = EnumerateHidInterfaces(false, MaxInterfaces);
                log.Msg("[YARG-VR][hid] " + all.Count + " HID interface(s) present system-wide (PDP ones carry the liveness windows):");
                foreach (string p in all)
                {
                    log.Msg("[YARG-VR][hid]   " + PathTail(p) + " product='" + ProductStringOf(p) + "'");
                }
            }
            catch (Exception e)
            {
                log.Msg("[YARG-VR][hid] interface listing failed: " + e.Message);
            }
        }

        private static string ProductStringOf(string path)
        {
            IntPtr h = CreateFileW(path, 0 /* query access - no data I/O */, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h.ToInt64() == -1)
            {
                return "<denied>";
            }
            try
            {
                return GetProductString(h);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static string GetProductString(IntPtr h)
        {
            try
            {
                var sb = new StringBuilder(256);
                if (HidD_GetProductString(h, sb, (uint)(sb.Capacity * 2)) && sb.Length > 0)
                {
                    return sb.ToString();
                }
            }
            catch
            {
            }
            return "";
        }

        private static List<string> EnumerateHidInterfaces(bool onlyPdp, int cap)
        {
            var paths = new List<string>();
            Guid hidIface = HidInterfaceGuid; // static readonly cannot be passed by ref
            IntPtr set = SetupDiGetClassDevsW(ref hidIface, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceinterface);
            if (set == IntPtr.Zero || set.ToInt64() == -1)
            {
                return paths;
            }

            try
            {
                for (uint i = 0; i < 256; i++)
                {
                    var ifd = new SP_DEVICE_INTERFACE_DATA();
                    ifd.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    Guid ifaceRef = HidInterfaceGuid;
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref ifaceRef, i, ref ifd))
                    {
                        break; // end of list
                    }

                    uint need;
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifd, IntPtr.Zero, 0, out need, IntPtr.Zero) && need == 0)
                    {
                        continue;
                    }

                    IntPtr buf = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize must be 8 (DWORD cbSize +
                        // the first WCHAR, struct-aligned) on both x86 and x64.
                        Marshal.WriteInt32(buf, 0, 8);
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref ifd, buf, need, out need, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                            if (!string.IsNullOrEmpty(p) &&
                                (!onlyPdp || p.IndexOf("vid_0e6f", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                paths.Add(p);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }

                    if (paths.Count >= cap)
                    {
                        break;
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return paths;
        }

        private static string PathTail(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "?";
            }
            int idx = path.LastIndexOf('\\');
            string tail = idx >= 0 ? path.Substring(idx + 1) : path;
            if (tail.Length > 60)
            {
                tail = tail.Substring(0, 60) + "...";
            }
            return tail;
        }

        // ---- Win32 plumbing ----

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, uint bufferLength);
    }
}
