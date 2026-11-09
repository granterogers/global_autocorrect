# D105: runs inside Windows Sandbox, whose user is an administrator at high integrity. Starts a command at medium
# integrity, as a normal user's programs run: the app refuses to run elevated (10.8), and the programs the tests start
# (Edge, Notepad) inherit the level, so the app can type into them as it does on the user's PC (UIPI).
param([Parameter(Mandatory)][string]$CommandLine)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Medium {
    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle; public int x, y, xs, ys, xc, yc, fill, flags;
        public short show, r2; public IntPtr r3, hin, hout, herr; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr t, uint access, IntPtr sa, int level, int type, out IntPtr nt);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ConvertStringSidToSid(string s, out IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr t, int cls, ref TOKEN_MANDATORY_LABEL l, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessWithTokenW(IntPtr t, int logon, string app, string cmd, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    public static int Start(string cmd) {
        IntPtr tok, dup, sid;
        if (!OpenProcessToken(GetCurrentProcess(), 0x000B /* DUPLICATE|QUERY|ASSIGN_PRIMARY */ | 0x0080 /* ADJUST_DEFAULT */, out tok)) return -Marshal.GetLastWin32Error();
        if (!DuplicateTokenEx(tok, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* impersonation */, 1 /* primary */, out dup)) return -Marshal.GetLastWin32Error();
        ConvertStringSidToSid("S-1-16-8192", out sid);   // Medium Mandatory Level
        var l = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = 0x20 /* SE_GROUP_INTEGRITY */ } };
        if (!SetTokenInformation(dup, 25 /* TokenIntegrityLevel */, ref l, Marshal.SizeOf(l) + 12)) return -Marshal.GetLastWin32Error();
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.lpDesktop = "winsta0\\default"; PROCESS_INFORMATION pi;
        if (!CreateProcessWithTokenW(dup, 0, null, cmd, 0, IntPtr.Zero, "C:\\ga", ref si, out pi)) return -Marshal.GetLastWin32Error();
        return pi.pid;
    }
}
'@
"started at medium integrity: pid $([Medium]::Start($CommandLine))"
