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
}
