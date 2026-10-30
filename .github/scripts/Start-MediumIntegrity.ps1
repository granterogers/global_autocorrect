#Requires -Version 7
# Runs a command line at medium integrity and waits for it (CI only).
#
# The hosted runner's account is an administrator with UAC off, so every step runs at high integrity. The app refuses
# to run elevated (10.8), and Windows keeps a medium app from reading or typing into high-integrity windows (UIPI), so
# the whole E2E run (test host, app, Notepad, Edge) has to start at medium integrity, as it does on the user's PC.
# The child inherits this process's environment and desktop; it writes its own output to a file (no shared pipes).
param(
    [Parameter(Mandatory)][string]$CommandLine,
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class MediumIntegrity
{
    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string sid, out IntPtr psid);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, ref TOKEN_MANDATORY_LABEL info, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    /// <summary>Starts the command line with a copy of this process's token lowered to medium integrity; returns the pid.</summary>
    public static int Start(string commandLine, string cwd)
    {
        IntPtr token, medium, sid;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0008 | 0x0080 | 0x0001, out token)) throw new Win32Exception();   // DUPLICATE|QUERY|ADJUST_DEFAULT|ASSIGN_PRIMARY
        if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out medium)) throw new Win32Exception();                   // MAXIMUM_ALLOWED, impersonation, primary
        if (!ConvertStringSidToSid("S-1-16-8192", out sid)) throw new Win32Exception();                                         // Medium Mandatory Level
        var label = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = 0x20 } };              // SE_GROUP_INTEGRITY
        if (!SetTokenInformation(medium, 25, ref label, Marshal.SizeOf(label) + GetLengthSid(sid))) throw new Win32Exception(); // TokenIntegrityLevel
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = @"winsta0\default" };
        PROCESS_INFORMATION pi;
        if (!CreateProcessAsUser(medium, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, cwd, ref si, out pi))   // CREATE_NO_WINDOW
            throw new Win32Exception();
        return pi.dwProcessId;
    }
}
'@

$childPid = [MediumIntegrity]::Start($CommandLine, (Get-Location).Path)
$child = Get-Process -Id $childPid
$null = $child.Handle   # keep a handle so the exit code survives the process
Write-Host "started at medium integrity (pid $childPid): $CommandLine"
if (-not $child.WaitForExit([int]([TimeSpan]::FromMinutes($TimeoutMinutes).TotalMilliseconds))) {
    Write-Host "timed out after $TimeoutMinutes min; ending the process tree" -ForegroundColor Red
    taskkill /PID $childPid /T /F | Out-Null
    exit 124
}
exit $child.ExitCode
