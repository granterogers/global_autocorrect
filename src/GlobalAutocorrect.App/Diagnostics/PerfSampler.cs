using System.Diagnostics;
using System.Runtime.InteropServices;
using GlobalAutocorrect.Core.Telemetry;

namespace GlobalAutocorrect.App.Diagnostics;

/// <summary>
/// D94, user request "keep track of performance, like memory usage": every 10 minutes, or within a minute of typing
/// being slowed, one diagnostics line with memory, CPU, GC pauses, the model's memory and how often and how slowly
/// fields were read. Numbers only, never text (8.6).
/// </summary>
internal sealed class PerfSampler(Timings timings, Action<string> log)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10), SlowEvery = TimeSpan.FromMinutes(1);
    private PerfSample? _last;
    private DateTime _lastLine = DateTime.UtcNow;
    private int _lines;

    /// <summary>Called by the maintenance timer (every few seconds).</summary>
    public void Tick()
    {
        var now = Take();
        _last ??= now;
        var since = DateTime.UtcNow - _lastLine;
        if (since < SlowEvery) return;
        var snap = timings.Snapshot();
        string line = PerfReport.Line(_last, now, snap.GetValueOrDefault("read"), snap.GetValueOrDefault("key.hookDelay"));
        bool slow = line.Contains(" SLOW ", StringComparison.Ordinal);
        if (!slow && since < Every) return;
        if (++_lines <= 300) log(line);
        _last = now;
        _lastLine = DateTime.UtcNow;
    }

    private PerfSample Take()
    {
        using var me = Process.GetCurrentProcess();
        double model = 0;
        foreach (var p in Process.GetProcessesByName("llama-server").Concat(Process.GetProcessesByName(Models.ModelDownloader.ServerName)))   // D106
        {
            try { model += p.WorkingSet64; } catch (InvalidOperationException) { }   // D112: as Task Manager shows it; "private bytes" counted the mapped model file (~5 GB)
            p.Dispose();
        }
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        double free = GlobalMemoryStatusEx(ref mem) ? mem.AvailPhys / 1048576.0 : 0;
        return new PerfSample(Environment.TickCount64, me.PrivateMemorySize64 / 1048576.0, me.WorkingSet64 / 1048576.0,
            me.TotalProcessorTime.TotalMilliseconds, GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(2),
            model / 1048576.0, free, timings.Total("read"), timings.Total("key.hookDelay"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
