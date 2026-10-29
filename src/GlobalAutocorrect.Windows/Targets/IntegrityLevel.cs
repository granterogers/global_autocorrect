using System.Runtime.InteropServices;

namespace GlobalAutocorrect.Windows.Targets;

/// <summary>
/// Process integrity level (10.8). UIPI blocks input injection into windows of a higher integrity
/// level, so we do nothing there.
/// </summary>
public static class IntegrityLevel
{
    /// <summary>Integrity RID of the process, or null if it could not be read (e.g. access denied).</summary>
    public static int? Of(int pid)
    {
        nint process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out nint token)) return null;
            try
            {
                return FromToken(token);
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>Integrity RID of our own process (medium = 0x2000, high = 0x3000, system = 0x4000).</summary>
    public static int Current { get; } = Of(Environment.ProcessId) ?? 0;

    /// <summary>
    /// True when the process is above us, or unreadable (access denied, so treat it as protected).
    /// </summary>
    public static bool IsElevatedAboveUs(int pid) => Of(pid) is not { } level || level > Current;

    private static int? FromToken(nint token)
    {
        Native.GetTokenInformation(token, Native.TokenIntegrityLevel, 0, 0, out int needed);
        if (needed <= 0) return null;
        nint buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, needed, out _)) return null;
            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES: the SID pointer comes first.
            nint sid = Marshal.ReadIntPtr(buffer);
            if (sid == 0) return null;
            nint countPtr = Native.GetSidSubAuthorityCount(sid);
            if (countPtr == 0) return null;
            byte count = Marshal.ReadByte(countPtr);
            if (count == 0) return null;
            nint ridPtr = Native.GetSidSubAuthority(sid, (uint)(count - 1));
            if (ridPtr == 0) return null;
            return Marshal.ReadInt32(ridPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

#pragma warning disable SYSLIB1054 // DllImport is fine for these few blittable calls.
    private static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenIntegrityLevel = 25;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(nint process, uint access, out nint token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(nint token, int infoClass, nint info, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern nint GetSidSubAuthorityCount(nint sid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern nint GetSidSubAuthority(nint sid, uint index);
    }
#pragma warning restore SYSLIB1054
}
