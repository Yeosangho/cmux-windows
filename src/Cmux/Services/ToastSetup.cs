using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Cmux.Services;

/// <summary>
/// Owns the lifecycle of cmux's Windows toast notification surface:
///   - Registers a stable AumId in HKCU so Windows treats cmux as a normal
///     notification-emitting app (proper sound, persistence in Action
///     Center, click routing).
///   - Wires the COM class object for <see cref="NotificationActivator"/>
///     so toast clicks actually call back into this process.
///   - Materializes the Start Menu .lnk that Windows requires to bind the
///     AumId to the activator CLSID.
/// All operations are idempotent — running cmux twice doesn't churn the
/// registry, and a missing/corrupted .lnk gets repaired on startup.
/// </summary>
public static class ToastSetup
{
    /// <summary>Stable AumId — independent of install path. Must be a
    /// well-formed identifier (no path characters) or Windows silently
    /// degrades the toast to "ghost" mode (no sound, no click routing).</summary>
    public const string AumId = "Ten1010.Cmux";
    public const string DisplayName = "Cmux";
    public static readonly Guid ActivatorClsid = new("CC5DC52C-0B27-4E3C-9F07-9E58E1B7B8A1");

    private static int _comRegisterCookie;
    private static bool _initialized;
    // Pin the activator so the GC doesn't collect it after CoRegisterClassObject
    // returns. Without a strong reference here, the .NET RCW gets cleaned up,
    // Windows can no longer resolve the running class object, and toast clicks
    // silently fail (Activate never fires in our process even though
    // CoRegisterClassObject reported success).
    private static NotificationActivator? _activatorInstance;

    public static void Initialize(Action<string> onActivated)
    {
        if (_initialized) return;
        _initialized = true;

        // Mirror diagnostics into the same log file App.OnStartup uses,
        // so the user can see whether each setup step succeeded after a
        // failed toast click.
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cmuxw-toast.log");
        void Log(string msg)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] ToastSetup: {msg}\n", System.Text.Encoding.UTF8); }
            catch { }
        }

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exePath))
        {
            Log("cannot resolve own exe path — abort");
            return;
        }
        Log($"exe='{exePath}' aumid='{AumId}' clsid={{{ActivatorClsid}}}");

        try { EnsureRegistry(exePath); Log("EnsureRegistry OK"); }
        catch (Exception ex) { Log($"EnsureRegistry FAILED: {ex.Message}"); }

        try { EnsureShortcut(exePath); Log("EnsureShortcut OK"); }
        catch (Exception ex) { Log($"EnsureShortcut FAILED: {ex.Message}"); }

        NotificationActivator.Activated -= onActivated;
        NotificationActivator.Activated += onActivated;

        try
        {
            _comRegisterCookie = RegisterClassObject();
            Log($"CoRegisterClassObject OK; cookie={_comRegisterCookie}");
        }
        catch (Exception ex)
        {
            Log($"CoRegisterClassObject FAILED: {ex.Message}");
        }
    }

    // ── Registry ────────────────────────────────────────────────────────

    private static void EnsureRegistry(string exePath)
    {
        using (var aumidKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AumId}"))
        {
            aumidKey.SetValue("DisplayName", DisplayName);
            aumidKey.SetValue("CustomActivator", $"{{{ActivatorClsid}}}");
        }

        var clsidStr = $"{{{ActivatorClsid}}}";
        using (var clsid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsidStr}"))
        {
            clsid.SetValue(null, "Cmux Toast Activator");
        }
        using (var ls = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsidStr}\LocalServer32"))
        {
            ls.SetValue(null, $"\"{exePath}\" -ToastActivated");
        }
    }

    // ── Start Menu shortcut ─────────────────────────────────────────────

    private static void EnsureShortcut(string exePath)
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(startMenu);
        var lnkPath = Path.Combine(startMenu, $"{DisplayName}.lnk");

        if (File.Exists(lnkPath) && IsShortcutCurrent(lnkPath, exePath))
            return;

        if (File.Exists(lnkPath))
        {
            try { File.Delete(lnkPath); } catch { }
        }

        // Build the .lnk via WshShell so we don't need the IShellLink P/Invoke
        // shim. Then attach the AumId via IPropertyStore (PKEY_AppUserModel_ID).
        var wshType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell not available");
        dynamic? wsh = Activator.CreateInstance(wshType);
        if (wsh == null) throw new InvalidOperationException("Cannot create WScript.Shell");
        try
        {
            dynamic shortcut = wsh.CreateShortcut(lnkPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = DisplayName;
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(wsh);
        }

        SetShortcutAumid(lnkPath, AumId);
    }

    private static bool IsShortcutCurrent(string lnkPath, string exePath)
    {
        try
        {
            var wshType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException();
            dynamic? wsh = Activator.CreateInstance(wshType);
            if (wsh == null) return false;
            try
            {
                dynamic shortcut = wsh.CreateShortcut(lnkPath);
                string target = shortcut.TargetPath;
                if (!string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            finally { Marshal.FinalReleaseComObject(wsh); }
            return string.Equals(ReadShortcutAumid(lnkPath), AumId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // ── PKEY_AppUserModel_ID via IPropertyStore (P/Invoke) ──────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt; public ushort r1; public ushort r2; public ushort r3;
        public IntPtr p1; public IntPtr p2;
    }

    private const ushort VT_LPWSTR = 31;
    private const uint GPS_READWRITE = 2;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, uint flags,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }

    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static void SetShortcutAumid(string lnkPath, string aumid)
    {
        var iid = IID_IPropertyStore;
        SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, GPS_READWRITE, ref iid, out var ps);
        IntPtr strPtr = IntPtr.Zero;
        try
        {
            strPtr = Marshal.StringToCoTaskMemUni(aumid);
            var pv = new PROPVARIANT { vt = VT_LPWSTR, p1 = strPtr };
            var key = PKEY_AppUserModel_ID;
            ps.SetValue(ref key, ref pv);
            ps.Commit();
        }
        finally
        {
            if (strPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(strPtr);
            Marshal.FinalReleaseComObject(ps);
        }
    }

    private static string ReadShortcutAumid(string lnkPath)
    {
        var iid = IID_IPropertyStore;
        SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0u, ref iid, out var ps);
        try
        {
            var key = PKEY_AppUserModel_ID;
            ps.GetValue(ref key, out var pv);
            try
            {
                if (pv.vt == VT_LPWSTR && pv.p1 != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pv.p1) ?? string.Empty;
                return string.Empty;
            }
            finally
            {
                if (pv.p1 != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.p1);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(ps);
        }
    }

    // ── Class object registration so Windows can call into us ───────────

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 1;

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoRegisterClassObject(
        [In] ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext, uint flags, out int lpdwRegister);

    private static int RegisterClassObject()
    {
        var clsid = ActivatorClsid;
        _activatorInstance = new NotificationActivator();
        CoRegisterClassObject(ref clsid, _activatorInstance, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out var cookie);
        return cookie;
    }
}
