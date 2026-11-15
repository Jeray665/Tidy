using System.Runtime.InteropServices;

namespace Tidy.Core;

static class Native
{
    // ---------- memory / cpu ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    // SystemMemoryListInformation = 80. Query returns SYSTEM_MEMORY_LIST_INFORMATION (22 ULONG_PTR on x64):
    // [0]Zero [1]Free [2]Modified [3]ModifiedNoWrite [4]Bad [5..12]StandbyByPriority [13..20]Repurposed [21]ModifiedPageFile
    public const int SystemMemoryListInformation = 80;
    public const int MemoryPurgeStandbyList = 4;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, [Out] ulong[] buffer, int length, out int returned);

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int cls, ref int command, int length);

    // ---------- privileges ----------
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    public static bool EnablePrivilege(string name)
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            // AdjustTokenPrivileges returns true even when not all were assigned; ERROR_NOT_ALL_ASSIGNED = 1300
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1300;
        }
        finally { CloseHandle(token); }
    }

    // ---------- power plans ----------
    [DllImport("powrprof.dll")]
    public static extern uint PowerEnumerate(IntPtr root, IntPtr schemeGuid, IntPtr subGroup, uint accessFlags, uint index, ref Guid buffer, ref uint size);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint size);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr p);

    // ---------- NVIDIA NVML (read-only telemetry, ships with the driver in System32) ----------
    [StructLayout(LayoutKind.Sequential)] public struct NvUtil { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] public struct NvMem { public ulong Total, Free, Used; }

    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvUtil util);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvMem mem);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint pct);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    // ---------- Authenticode ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// <summary>True only if the file carries an embedded Authenticode signature whose chain validates.</summary>
    public static bool HasValidSignature(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (chain + expiry still checked)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
            };
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    // ---------- recycle bin ----------
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    /// <summary>Windows caches icons by file path, so an exe swapped in place keeps showing its old icon (Start, desktop)
    /// until told that these files changed.</summary>
    public static void IconsChanged(params string[] paths)
    {
        foreach (var p in paths) SHChangeNotify(0x2000 /* SHCNE_UPDATEITEM */, 0x5 /* SHCNF_PATHW */, p, IntPtr.Zero);
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, null, IntPtr.Zero);
    }

    // ---------- Recycle Bin (reversible delete) ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Sends files/folders to the Recycle Bin. FOF_WANTNUKEWARNING: if something doesn't fit in the bin, Windows asks instead of deleting it for good.</summary>
    public static bool Recycle(IEnumerable<string> paths)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    // ---------- processes ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SET_QUOTA = 0x100;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
    static extern bool EmptyWorkingSet(IntPtr process);

    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool TrimProcess(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return EmptyWorkingSet(h); } finally { CloseHandle(h); }
    }

    // ---------- run as the signed-in user, without admin ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string app, System.Text.StringBuilder cmd, uint flags,
        IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, int ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out int code);

    /// <summary>Starts exe with Explorer's token: the signed-in user without admin rights, as if opened from the desktop.
    /// Only Windows' own explorer.exe counts as that shell. Returns the exit code, or null (with the reason) if it couldn't start.</summary>
    /// <param name="visible">Normal window and the exe's own folder as working directory (an interactive uninstaller), instead of hidden in System32.
    /// timeoutMs = Timeout.Infinite waits for as long as it takes.</param>
    public static (int? Exit, string Error) RunAsShellUser(string exe, string args, int timeoutMs, bool visible = false)
    {
        GetWindowThreadProcessId(GetShellWindow(), out var pid);
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (pid == 0 || !string.Equals(ProcessPath(pid), explorer, StringComparison.OrdinalIgnoreCase)) return (null, "el Explorador de Windows no está abierto");
        IntPtr proc = IntPtr.Zero, token = IntPtr.Zero, user = IntPtr.Zero;
        try
        {
            proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero || !OpenProcessToken(proc, 0x2 /* TOKEN_DUPLICATE */, out token)) return (null, $"error {Marshal.GetLastWin32Error()}");
            // TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, primary token
            if (!DuplicateTokenEx(token, 0x8 | 0x2 | 0x1 | 0x80 | 0x100, IntPtr.Zero, 2, 1, out user)) return (null, $"error {Marshal.GetLastWin32Error()}");
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessWithTokenW(user, 0, exe, new System.Text.StringBuilder($"\"{exe}\" {args}"), visible ? 0u : 0x08000000u /* CREATE_NO_WINDOW */,
                    IntPtr.Zero, visible ? System.IO.Path.GetDirectoryName(exe) : Environment.SystemDirectory, ref si, out var pi))
                return (null, $"error {Marshal.GetLastWin32Error()}");
            CloseHandle(pi.hThread);
            try
            {
                if (WaitForSingleObject(pi.hProcess, timeoutMs) != 0)
                {
                    try { System.Diagnostics.Process.GetProcessById(pi.dwProcessId).Kill(true); } catch { }
                    return (-1, "timeout");
                }
                GetExitCodeProcess(pi.hProcess, out var code);
                return (code, "");
            }
            finally { CloseHandle(pi.hProcess); }
        }
        finally
        {
            if (user != IntPtr.Zero) CloseHandle(user);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (proc != IntPtr.Zero) CloseHandle(proc);
        }
    }

    // ---------- files by handle (a delete can't be redirected by a folder swapped for a junction) ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, System.Text.StringBuilder path, uint len, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int cls, out FILE_BASIC_INFO info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int cls, ref FILE_BASIC_INFO info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int cls, ref uint info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_BASIC_INFO { public long Created, Accessed, Written, Changed; public uint Attributes; }

    /// <summary>Opens path itself (a link at the last component is not followed when noFollow). Invalid handle: in use, denied or gone.</summary>
    public static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path, uint access, bool noFollow)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!path.StartsWith(@"\\")) path = @"\\?\" + path;
        // share read|write|delete, OPEN_EXISTING, BACKUP_SEMANTICS (directories) [+ OPEN_REPARSE_POINT]
        return CreateFileW(path, access, 7, IntPtr.Zero, 3, 0x02000000 | (noFollow ? 0x00200000u : 0), IntPtr.Zero);
    }

    /// <summary>Where the opened object really is (after resolving every folder on the way), or null (also for network paths).</summary>
    public static string? FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var sb = new System.Text.StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return null;
        var p = sb.ToString();
        return p.StartsWith(@"\\?\UNC\") ? null : p.StartsWith(@"\\?\") ? p[4..] : p;
    }

    /// <summary>FILE_ATTRIBUTE_* of the opened object itself, or null.</summary>
    public static System.IO.FileAttributes? Attributes(Microsoft.Win32.SafeHandles.SafeFileHandle h) =>
        GetFileInformationByHandleEx(h, 0 /* FileBasicInfo */, out var i, (uint)Marshal.SizeOf<FILE_BASIC_INFO>()) ? (System.IO.FileAttributes)i.Attributes : null;

    /// <summary>Deletes the file this handle points to (needs DELETE | FILE_WRITE_ATTRIBUTES access).</summary>
    public static bool DeleteByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        // FileDispositionInfoEx: DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
        var flags = 0x1u | 0x2 | 0x10;
        if (SetFileInformationByHandle(h, 21, ref flags, 4)) return true;
        // older Windows or FAT/exFAT: clear read-only through the handle, then the classic disposition
        if (Attributes(h) is { } a && a.HasFlag(System.IO.FileAttributes.ReadOnly))
        {
            var bi = new FILE_BASIC_INFO { Attributes = (uint)(a & ~System.IO.FileAttributes.ReadOnly) | (a == System.IO.FileAttributes.ReadOnly ? (uint)System.IO.FileAttributes.Normal : 0) }; // times 0 = unchanged
            SetFileInformationByHandle(h, 0, ref bi, (uint)Marshal.SizeOf<FILE_BASIC_INFO>());
        }
        var del = 1u; // FILE_DISPOSITION_INFO { BOOLEAN DeleteFile } (class 4)
        return SetFileInformationByHandle(h, 4, ref del, 1);
    }

    // ---------- Restart Manager: who has a file open ----------
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType, AppStatus, TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, string key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint reasons);

    public static List<(int Pid, string App)> WhoLocks(string path)
    {
        var result = new List<(int, string)>();
        if (RmStartSession(out var h, 0, Guid.NewGuid().ToString()) != 0) return result;
        try
        {
            if (RmRegisterResources(h, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return result;
            uint count = 0, reasons = 0;
            var rc = RmGetList(h, out var needed, ref count, null, ref reasons);
            if (rc == 234 /* ERROR_MORE_DATA */ && needed > 0)
            {
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(h, out _, ref count, info, ref reasons) == 0)
                    for (var i = 0; i < count; i++) result.Add((info[i].Process.dwProcessId, info[i].strAppName));
            }
        }
        finally { RmEndSession(h); }
        return result;
    }
}
