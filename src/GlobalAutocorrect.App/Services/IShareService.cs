namespace GlobalAutocorrect.App.Services;

/// <summary>D179: "Share what it learns" on the About page.</summary>
public interface IShareService
{
    /// <summary>The brain names somewhere to send to. Without it nothing is ever sent, whatever the switch says.</summary>
    bool Available { get; }

    /// <summary>When this install last sent, and what ("5 programs and 12 undone changes"); null if never.</summary>
    (DateTimeOffset At, string What)? LastSent { get; }

    /// <summary>Why the last try didn't go, in plain words; null when it went or nothing was tried.</summary>
    string? Problem { get; }

    /// <summary>Writes exactly what would be sent now to a file and returns its path (opened for the user to read).</summary>
    string WritePreview();

    /// <summary>The switch was just turned on: send within the next few minutes rather than waiting for the daily one.</summary>
    void SendSoon();

    /// <summary>LastSent, Problem or Available changed. Can fire on any thread.</summary>
    event Action? Changed;
}
