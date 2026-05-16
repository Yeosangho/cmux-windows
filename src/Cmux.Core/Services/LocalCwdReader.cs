using System.Runtime.InteropServices;

namespace Cmux.Core.Services;

/// <summary>
/// Reads the Win32 current working directory of a running local process by
/// walking its PEB (Process Environment Block) over <c>ReadProcessMemory</c>.
///
/// Why this exists: classifying / labeling a pane's "where am I" needs to
/// follow <c>cd</c>, <c>D:</c>, <c>pushd</c> etc. inside whatever shell the
/// user is running (cmd, powershell, pwsh, nu, fish, wsl). All of those
/// eventually call <c>SetCurrentDirectoryW</c>, which updates
/// <c>PEB.ProcessParameters.CurrentDirectory.DosPath</c>. By reading that
/// directly we get cwd without any shell-side cooperation — no OSC 7,
/// no PROMPT_COMMAND, no shell integration scripts. The same code path
/// works whether daemon is on or off.
///
/// Limitations:
/// - 32-bit (WoW64) target processes have a separate 32-bit PEB at a
///   different address. We only support 64-bit targets; for WoW64 child
///   shells (rare in cmuxw's typical setup) this returns null and the
///   caller can fall back to OSC 7 if any. Detecting WoW64 cheaply via
///   IsWow64Process2 is left as a future hook.
/// - Same-user-context only. Cmuxw and its shell children share the same
///   user, so PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ
///   succeeds without elevation. Reading a process owned by another user
///   (or System) requires SeDebugPrivilege and is intentionally not
///   attempted.
/// </summary>
public static class LocalCwdReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    // Layout offsets in 64-bit RTL_USER_PROCESS_PARAMETERS for
    // CurrentDirectory.DosPath UNICODE_STRING. These have been stable since
    // at least Windows 7 x64 — they are part of the documented PEB layout
    // and undocumented but practically frozen (used by Process Hacker,
    // Sysinternals, etc.). Re-verify after a Windows major upgrade if
    // results suddenly go null.
    private const int PEB_OFFSET_PROCESS_PARAMETERS_X64 = 0x20;
    private const int RUPP_OFFSET_CURRENTDIR_LENGTH_X64 = 0x38;
    private const int RUPP_OFFSET_CURRENTDIR_BUFFER_X64 = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        ref PROCESS_BASIC_INFORMATION ProcessInformation,
        int ProcessInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    /// <summary>
    /// Returns the local Win32 cwd of <paramref name="pid"/>, or null if it
    /// can't be read (process gone, 32-bit target on x64 host, access denied,
    /// non-Windows runtime).
    /// </summary>
    public static string? TryRead(int pid)
    {
        if (pid <= 0) return null;
        if (!OperatingSystem.IsWindows()) return null;
        if (!Environment.Is64BitProcess) return null;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(handle, 0 /*ProcessBasicInformation*/, ref pbi, Marshal.SizeOf(pbi), out _);
            if (status != 0) return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            // Read PEB.ProcessParameters pointer (8 bytes at PEB+0x20).
            var ppPtrBytes = new byte[8];
            if (!ReadProcessMemory(handle, pbi.PebBaseAddress + PEB_OFFSET_PROCESS_PARAMETERS_X64,
                ppPtrBytes, 8, out _))
                return null;
            var ppAddr = (IntPtr)BitConverter.ToInt64(ppPtrBytes, 0);
            if (ppAddr == IntPtr.Zero) return null;

            // Read UNICODE_STRING { USHORT Length; USHORT MaximumLength; ...; PWSTR Buffer; }
            // at ProcessParameters+0x38 (length) and +0x40 (buffer).
            var lenBytes = new byte[2];
            if (!ReadProcessMemory(handle, ppAddr + RUPP_OFFSET_CURRENTDIR_LENGTH_X64,
                lenBytes, 2, out _))
                return null;
            int byteLen = BitConverter.ToUInt16(lenBytes, 0);
            if (byteLen == 0 || byteLen > 32_768) return null; // sanity cap

            var bufPtrBytes = new byte[8];
            if (!ReadProcessMemory(handle, ppAddr + RUPP_OFFSET_CURRENTDIR_BUFFER_X64,
                bufPtrBytes, 8, out _))
                return null;
            var bufAddr = (IntPtr)BitConverter.ToInt64(bufPtrBytes, 0);
            if (bufAddr == IntPtr.Zero) return null;

            var strBytes = new byte[byteLen];
            if (!ReadProcessMemory(handle, bufAddr, strBytes, byteLen, out _))
                return null;

            var s = System.Text.Encoding.Unicode.GetString(strBytes);
            // Strip trailing backslash(es) the kernel leaves on cwd ("C:\Users\u\").
            return s.TrimEnd('\\', '/');
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads cwd of the deepest descendant in <paramref name="rootPid"/>'s
    /// tree (the typical "foreground shell leaf" in a ConPTY pane). Walks
    /// children breadth-first via WMI, picks the deepest reachable
    /// descendant that still has a readable cwd. Falls back to the root.
    /// </summary>
    public static string? TryReadLeafCwd(int rootPid)
    {
        if (rootPid <= 0) return null;

        try
        {
            // BFS over the process tree, depth-cap 6 (typical pane: cmd -> claude.cmd -> node)
            var visited = new HashSet<int> { rootPid };
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            int leaf = rootPid;
            int budget = 32;

            while (queue.Count > 0 && budget-- > 0)
            {
                int pid = queue.Dequeue();
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {pid}");
                bool hadChild = false;
                foreach (var obj in searcher.Get())
                {
                    int child;
                    try { child = Convert.ToInt32(obj["ProcessId"]); }
                    catch { continue; }
                    if (visited.Add(child))
                    {
                        queue.Enqueue(child);
                        leaf = child;
                        hadChild = true;
                    }
                }
                // No children => current `pid` is a leaf candidate; loop continues
                // through other queued siblings — we still prefer the deepest leaf.
                if (!hadChild && pid != rootPid) leaf = pid;
            }

            var cwd = TryRead(leaf);
            if (!string.IsNullOrEmpty(cwd)) return cwd;
            // Leaf might be a non-Win32 process (rare) — fall back to root.
            return TryRead(rootPid);
        }
        catch
        {
            return TryRead(rootPid);
        }
    }
}
