using System.Runtime.InteropServices;

namespace FcDriverFixer.Core;

/// <summary>
/// Thin P/Invoke wrapper over the libwdi.dll we compile from vendor/libwdi.
/// Marshalling notes (x64):
///   - Win32 BOOL is a 4-byte int.
///   - All char* in libwdi are ANSI (CharSet.Ansi).
///   - LIBWDI_API is WINAPI/__stdcall; on x64 there is a single native calling
///     convention, but we declare Winapi to stay correct if a 32-bit build is ever added.
///   - wdi_device_info is a linked list; we walk it manually via IntPtr rather than
///     letting the marshaller auto-marshal a chained struct.
/// </summary>
public static class LibWdi
{
    private const string Dll = "libwdi.dll";

    /// <summary>
    /// Security hardening: pin libwdi.dll to the application's own directory. Because this
    /// app runs elevated, resolving the native DLL through the normal search order would let
    /// a poisoned libwdi.dll planted in the working directory (e.g. a Downloads folder) load
    /// with admin rights — a privilege-escalation path. An explicit absolute-path load from
    /// AppContext.BaseDirectory removes the search entirely. Falls back to the default
    /// resolver (returns Zero) if the file isn't there, which also covers single-file publish.
    /// </summary>
    static LibWdi()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibWdi).Assembly, (name, _, _) =>
        {
            if (string.Equals(name, Dll, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "libwdi", StringComparison.OrdinalIgnoreCase))
            {
                string full = Path.Combine(AppContext.BaseDirectory, Dll);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    // enum wdi_driver_type
    public const int WDI_WINUSB = 0;
    public const int WDI_LIBUSB0 = 1;
    public const int WDI_LIBUSBK = 2;
    public const int WDI_CDC = 3;
    public const int WDI_USER = 4;

    // enum wdi_log_level
    public const int WDI_LOG_LEVEL_DEBUG = 0;
    public const int WDI_LOG_LEVEL_WARNING = 2;
    public const int WDI_LOG_LEVEL_NONE = 4;

    public const int WDI_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiDeviceInfo
    {
        public IntPtr next;
        public ushort vid;
        public ushort pid;
        public int is_composite;   // BOOL
        public byte mi;
        public IntPtr desc;
        public IntPtr driver;
        public IntPtr device_id;
        public IntPtr hardware_id;
        public IntPtr compatible_id;
        public IntPtr upper_filter;
        public ulong driver_version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiOptionsCreateList
    {
        public int list_all;
        public int list_hubs;
        public int trim_whitespaces;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiOptionsPrepareDriver
    {
        public int driver_type;
        public IntPtr vendor_name;
        public IntPtr device_guid;
        public int disable_cat;
        public int disable_signing;
        public IntPtr cert_subject;
        public int use_wcid_driver;
        public int external_inf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiOptionsInstallDriver
    {
        public IntPtr hWnd;
        public int install_filter_driver;
        public uint pending_install_timeout;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr wdi_strerror(int errcode);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int wdi_create_list(out IntPtr list, ref WdiOptionsCreateList options);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int wdi_destroy_list(IntPtr list);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private static extern int wdi_prepare_driver(IntPtr device_info, string path, string inf_name, ref WdiOptionsPrepareDriver options);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private static extern int wdi_install_driver(IntPtr device_info, string path, string inf_name, ref WdiOptionsInstallDriver options);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int wdi_set_log_level(int level);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int wdi_is_driver_supported(int driver_type, IntPtr driver_info);

    /// <summary>Human-readable message for a libwdi return code.</summary>
    public static string StrError(int code)
    {
        IntPtr p = wdi_strerror(code);
        return p == IntPtr.Zero ? $"error {code}" : Marshal.PtrToStringAnsi(p) ?? $"error {code}";
    }

    public static void SetLogLevel(int level) => wdi_set_log_level(level);

    /// <summary>True if this libwdi build can install WinUSB (i.e. was built with WDK_DIR).</summary>
    public static bool IsWinUsbSupported() => wdi_is_driver_supported(WDI_WINUSB, IntPtr.Zero) != 0;

    /// <summary>A single USB device as reported by wdi_create_list.</summary>
    public readonly record struct UsbDevice(
        ushort Vid, ushort Pid, byte Mi, bool IsComposite,
        string? Description, string? Driver, string? DeviceId, string? HardwareId);

    /// <summary>Enumerate every USB device (list_all) with its currently bound driver.</summary>
    public static List<UsbDevice> ListDevices()
    {
        var result = new List<UsbDevice>();
        var ocl = new WdiOptionsCreateList { list_all = 1, list_hubs = 1, trim_whitespaces = 1 };
        int r = wdi_create_list(out IntPtr list, ref ocl);
        if (r != WDI_SUCCESS || list == IntPtr.Zero)
            return result; // WDI_ERROR_NO_DEVICE (-4) simply means nothing to list

        try
        {
            for (IntPtr cur = list; cur != IntPtr.Zero;)
            {
                var info = Marshal.PtrToStructure<WdiDeviceInfo>(cur);
                result.Add(new UsbDevice(
                    info.vid, info.pid, info.mi, info.is_composite != 0,
                    Marshal.PtrToStringAnsi(info.desc),
                    Marshal.PtrToStringAnsi(info.driver),
                    Marshal.PtrToStringAnsi(info.device_id),
                    Marshal.PtrToStringAnsi(info.hardware_id)));
                cur = info.next;
            }
        }
        finally
        {
            wdi_destroy_list(list);
        }
        return result;
    }

    /// <summary>Outcome of an install attempt.</summary>
    public readonly record struct InstallResult(bool Success, int Code, string Message);

    /// <summary>
    /// Install the WinUSB driver for a specific VID/PID, mirroring wdi-simple's flow:
    /// prepare inf -> find the live device to copy its hardware/device IDs -> install.
    /// <paramref name="extractDir"/> MUST be an absolute path: an elevated process
    /// inherits System32 as its cwd and a relative path fails with WDI_ERROR_RESOURCE.
    /// </summary>
    public static InstallResult InstallWinUsb(
        ushort vid, ushort pid, byte mi, bool isComposite,
        string deviceName, string vendorName, string extractDir)
    {
        if (!Path.IsPathRooted(extractDir))
            throw new ArgumentException("extractDir must be an absolute path", nameof(extractDir));
        Directory.CreateDirectory(extractDir);

        const string infName = "usb_device.inf";
        IntPtr descPtr = Marshal.StringToHGlobalAnsi(deviceName);
        IntPtr vendorPtr = Marshal.StringToHGlobalAnsi(vendorName);
        IntPtr hardwareIdPtr = IntPtr.Zero, deviceIdPtr = IntPtr.Zero;
        IntPtr devPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WdiDeviceInfo>());

        try
        {
            var dev = new WdiDeviceInfo
            {
                next = IntPtr.Zero,
                vid = vid,
                pid = pid,
                is_composite = isComposite ? 1 : 0,
                mi = mi,
                desc = descPtr,
            };
            Marshal.StructureToPtr(dev, devPtr, false);

            var opd = new WdiOptionsPrepareDriver
            {
                driver_type = WDI_WINUSB,
                vendor_name = vendorPtr,
            };
            int r = wdi_prepare_driver(devPtr, extractDir, infName, ref opd);
            if (r != WDI_SUCCESS)
                return new InstallResult(false, r, $"Preparing driver failed: {StrError(r)}");

            // Match the live device to fill in hardware_id/device_id (avoids a Device
            // Manager prompt). Copy the strings into our own memory so their lifetime
            // survives wdi_destroy_list.
            var ocl = new WdiOptionsCreateList { list_all = 1, list_hubs = 1, trim_whitespaces = 1 };
            if (wdi_create_list(out IntPtr list, ref ocl) == WDI_SUCCESS && list != IntPtr.Zero)
            {
                try
                {
                    for (IntPtr cur = list; cur != IntPtr.Zero;)
                    {
                        var info = Marshal.PtrToStructure<WdiDeviceInfo>(cur);
                        if (info.vid == vid && info.pid == pid && info.mi == mi &&
                            (info.is_composite != 0) == isComposite)
                        {
                            string? hid = Marshal.PtrToStringAnsi(info.hardware_id);
                            string? did = Marshal.PtrToStringAnsi(info.device_id);
                            if (hid != null) hardwareIdPtr = Marshal.StringToHGlobalAnsi(hid);
                            if (did != null) deviceIdPtr = Marshal.StringToHGlobalAnsi(did);
                            break;
                        }
                        cur = info.next;
                    }
                }
                finally
                {
                    wdi_destroy_list(list);
                }
            }

            dev.hardware_id = hardwareIdPtr;
            dev.device_id = deviceIdPtr;
            Marshal.StructureToPtr(dev, devPtr, false);

            var oid = new WdiOptionsInstallDriver { pending_install_timeout = 30000 };
            int ri = wdi_install_driver(devPtr, extractDir, infName, ref oid);
            return new InstallResult(ri == WDI_SUCCESS, ri, StrError(ri));
        }
        finally
        {
            Marshal.FreeHGlobal(devPtr);
            Marshal.FreeHGlobal(descPtr);
            Marshal.FreeHGlobal(vendorPtr);
            if (hardwareIdPtr != IntPtr.Zero) Marshal.FreeHGlobal(hardwareIdPtr);
            if (deviceIdPtr != IntPtr.Zero) Marshal.FreeHGlobal(deviceIdPtr);
        }
    }
}
