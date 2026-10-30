using System.Threading;

namespace GlobalAutocorrect.App;

/// <summary>Only one copy of the app runs (5.6.c).</summary>
public static class SingleInstance
{
    public const string Name = @"Local\GlobalAutocorrect.SingleInstance";

    /// <summary>The held mutex if this is the first copy; null if another copy already holds it.</summary>
    public static Mutex? TryAcquire(string name = Name)
    {
        var m = new Mutex(initiallyOwned: true, name, out bool created);
        if (created) return m;
        m.Dispose();
        return null;
    }

    public const string ExitName = @"Local\GlobalAutocorrect.Exit";

    /// <summary>
    /// The running copy waits on this; setting it is the same as choosing Exit in the tray, so the installer can
    /// replace the files without killing the app and losing what it hasn't saved yet.
    /// </summary>
    public static EventWaitHandle CreateExitSignal(string name = ExitName) => new(false, EventResetMode.AutoReset, name);

    /// <summary>Asks the running copy to exit. False when none is running.</summary>
    public static bool SignalExit(string name = ExitName)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var h)) return false;
        using (h) h.Set();
        return true;
    }
}
