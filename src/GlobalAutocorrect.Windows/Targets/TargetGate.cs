using System.Runtime.InteropServices;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Windows.Uia;

namespace GlobalAutocorrect.Windows.Targets;

/// <summary>Decides whether the focused target may be touched at all (8.3, 8.4, 10.8).</summary>
public static class TargetGate
{
    public static Func<UiaSnapshot, TargetStatus?> Create(
        AppExclusions exclusions,
        Func<int, bool>? isElevated = null,
        Func<bool>? foregroundIsFullScreen = null)
    {
        isElevated ??= IntegrityLevel.IsElevatedAboveUs;
        foregroundIsFullScreen ??= () => AppExclusions.IsFullScreen(Native.GetForegroundWindow());

        return s =>
        {
            if (exclusions.IsExcludedProcess(s.ProcessName)
                || AppExclusions.IsExcelCell(s)
                || foregroundIsFullScreen()) return TargetStatus.Excluded;
            if (isElevated(s.ProcessId)) return TargetStatus.Elevated;
            return null;
        };
    }

#pragma warning disable SYSLIB1054 // DllImport is fine for this one call.
    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();
    }
#pragma warning restore SYSLIB1054
}
