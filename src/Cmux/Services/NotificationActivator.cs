using System.Runtime.InteropServices;

namespace Cmux.Services;

/// <summary>
/// COM interface that Windows calls into when the user clicks a toast
/// notification produced by an unpackaged Win32 app. Defined by Windows
/// (IID 53E31837-6600-4A81-9395-75CFFE746F94, propsys / shellapi).
/// </summary>
[ComImport]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback
{
    void Activate(
        [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NotificationUserInputData[] data,
        [In, MarshalAs(UnmanagedType.U4)] uint dataCount);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NotificationUserInputData
{
    [MarshalAs(UnmanagedType.LPWStr)] public string Key;
    [MarshalAs(UnmanagedType.LPWStr)] public string Value;
}

/// <summary>
/// Concrete COM-visible activator. Windows instantiates this via
/// CoCreateInstance using the CLSID we register against our AumId; our
/// Activate() method then routes the click to the running cmux process.
/// </summary>
[ClassInterface(ClassInterfaceType.None)]
[ComVisible(true)]
[Guid("CC5DC52C-0B27-4E3C-9F07-9E58E1B7B8A1")]
[ComSourceInterfaces(typeof(INotificationActivationCallback))]
public class NotificationActivator : INotificationActivationCallback
{
    /// <summary>
    /// Fired on the COM RPC thread. Subscribers must marshal to the UI
    /// dispatcher themselves.
    /// </summary>
    public static event Action<string>? Activated;

    public void Activate(string appUserModelId, string invokedArgs,
        NotificationUserInputData[] data, uint dataCount)
    {
        try
        {
            // Append a marker the moment Windows actually invokes us — if
            // this line never appears in cmuxw-toast.log after a click, the
            // problem is upstream (AumId/COM registration), not in our
            // dispatch. PID is included so we can tell which process
            // (running cmux vs a transient COM-launched one) received it.
            try
            {
                var p = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cmuxw-toast.log");
                System.IO.File.AppendAllText(p,
                    $"[{DateTime.Now:HH:mm:ss.fff}] Activate(aumid='{appUserModelId}', args='{invokedArgs}', pid={Environment.ProcessId})\n");
            }
            catch { }

            Activated?.Invoke(invokedArgs ?? string.Empty);
        }
        catch { /* never throw out of a COM callback */ }
    }
}
