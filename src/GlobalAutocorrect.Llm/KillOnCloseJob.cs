using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlobalAutocorrect.Llm;

/// <summary>
/// Ties llama-server to this app's lifetime (5.6): processes in the job are killed when the app exits, even after a crash,
/// so about 5 GB of GPU memory is never left behind by an orphaned server.
/// </summary>
public static class KillOnCloseJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong R, W, O, RB, WB, OB; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, nint job, out bool result);
#pragma warning restore SYSLIB1054

    // One job for the app's whole life; the handle is closed by Windows when the process ends, which kills the children.
    private static readonly Lazy<nint> Job = new(() =>
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) ? job : 0;
    });

    /// <summary>Adds the process to the app's job. False if Windows refused (the server then simply isn't tied).</summary>
    public static bool Assign(Process p)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == 0) return false;
        try { return AssignProcessToJobObject(Job.Value, p.Handle); }
        catch (InvalidOperationException) { return false; }   // already exited
    }

    public static bool Contains(Process p) =>
        OperatingSystem.IsWindows() && Job.Value != 0 && IsProcessInJob(p.Handle, Job.Value, out bool inJob) && inJob;
}
