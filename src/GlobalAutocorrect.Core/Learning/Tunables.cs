namespace GlobalAutocorrect.Core.Learning;

/// <summary>One value the brain may set, held to its range; back to its default when the brain leaves it out.</summary>
public sealed class Tunable
{
    private double _value;

    internal Tunable(string name, double @default, double min, double max, string why)
    {
        Name = name; Default = @default; Min = min; Max = max; Why = why;
        _value = @default;
    }

    public string Name { get; }
    public double Default { get; }
    public double Min { get; }
    public double Max { get; }

    /// <summary>The decision it came from, for the knob list (never shown to users).</summary>
    public string Why { get; }

    public double Value => Volatile.Read(ref _value);
    public int Int => (int)Math.Round(Value);
    public bool On => Value >= 0.5;

    internal void Set(double v) => Volatile.Write(ref _value, double.IsFinite(v) ? Math.Clamp(v, Min, Max) : Default);
}

/// <summary>
/// D155 (user: "move as much as possible into the brain so that we don't need to recompile"). Every timing, retry count,
/// threshold and on/off switch that is a judgement rather than a fact lives here, settable from brain.json's "tuning"
/// (each held to its range; a value the brain leaves out goes back to its default). The values on <see cref="Engine.EngineOptions"/>
/// and the rules' thresholds stay in <see cref="Brain.Knobs"/> and <see cref="Brain.RuleKnobs"/>, which build a new
/// engine; these are read where they are used, so a new brain takes effect at once. Facts (Windows IDs, file formats,
/// memory caps) are not here.
/// </summary>
public static class Tunables
{
    private static readonly Dictionary<string, Tunable> ByName = new(StringComparer.OrdinalIgnoreCase);

    private static Tunable Define(string name, double @default, double min, double max, string why)
    {
        var t = new Tunable(name, @default, min, max, why);
        ByName.Add(name, t);
        return t;
    }

    // ---- Enter held for a pending fix (D154) ----
    public static readonly Tunable EnterHold = Define("Enter.Hold", 1, 0, 1, "D154: hold Enter in a field that sends on Enter while a fix is on its way (0 = off)");
    public static readonly Tunable EnterMaxHoldMs = Define("Enter.MaxHoldMs", 1500, 300, 3000, "D154: the longest Enter is held");
    public static readonly Tunable EnterHardCapMs = Define("Enter.HardCapMs", 4000, 1000, 8000, "D154: the longest, even while a write is in progress");
    public static readonly Tunable EnterSendsAfter = Define("Enter.SendsAfter", 1.5, 1, 5, "D154: sends seen (fading) before a field counts as sending on Enter");
    public static readonly Tunable EnterInstantHoldMs = Define("Enter.InstantHoldMs", 400, 0, 1500, "D156: in any field, Enter waits this long at most for the instant fix of the word just finished (0 = off)");
    public static readonly Tunable EnterGapMs = Define("Enter.GapMs", 60, 0, 300, "D161: after a held Enter is let go, the keys typed after it follow this much later (an editor handles Enter a moment late)");
    public static readonly Tunable EnterSendCheckMs = Define("Enter.SendCheckMs", 300, 100, 1500, "D154: how long after Enter the field is looked at");

    // ---- Fixes placed through UI Automation (D122–D152) ----
    public static readonly Tunable PlaceEnabled = Define("Place.Enabled", 1, 0, 1, "D122/D125/D143: place fixes in browsers (0 = arrows and retyping, as before)");
    public static readonly Tunable PlaceRetries = Define("Place.Retries", 3, 1, 6, "D144: tries for a refused placement");
    public static readonly Tunable PlaceRetryGapMs = Define("Place.RetryGapMs", 150, 0, 2000, "D167: a refused placement's next try waits this long (the editor settling)");
    public static readonly Tunable PlaceSelectWaitMs = Define("Place.SelectWaitMs", 400, 100, 2000, "D122: wait for the field to show the fix selected");
    public static readonly Tunable PlaceSettleMs = Define("Place.SettleMs", 30, 0, 300, "D122: pause for the page's editor to take the selection");
    public static readonly Tunable PlaceCkEditorSettleMs = Define("Place.CkEditorSettleMs", 120, 0, 1000, "D151: extra pause in CKEditor (Teams)");
    public static readonly Tunable PlaceTypedWaitMs = Define("Place.TypedWaitMs", 1000, 200, 3000, "D122: wait for the replacement to show");
    public static readonly Tunable PlaceReturnWaitMs = Define("Place.ReturnWaitMs", 500, 100, 2000, "D122: wait for the caret to be back");
    public static readonly Tunable PlaceStrayCopyRepair = Define("Place.StrayCopyRepair", 1, 0, 1, "D151: take out a fix typed beside the word (0 = off)");
    public static readonly Tunable KeyHoldMaxMs = Define("Hold.MaxMs", 3000, 500, 10_000, "D122: the longest the user's keys are held during a write");

    // ---- What the app learns per program (D81–D137) ----
    public static readonly Tunable HealthRetypeAfter = Define("Health.RetypeAfter", 0.25, 0.05, 3, "D89/D95/D121: failures (fading) before a program changes how it's written to");
    public static readonly Tunable HealthPlaceFailuresBeforeUnsafe = Define("Health.PlaceFailuresBeforeUnsafe", 2, 1, 20, "D137: placed-fix failures before placing stops in a program");
    public static readonly Tunable HealthPlaceRefusalsBeforeAvoid = Define("Health.PlaceRefusalsBeforeAvoid", 4, 1, 50, "D162: refused placements (fading) before placing stops in a program");
    public static readonly Tunable HealthPlaceRefusalShare = Define("Health.PlaceRefusalShare", 5, 1, 100, "D162: and more than one in this many of its fixes");
    public static readonly Tunable HealthPlaceFailureShare = Define("Health.PlaceFailureShare", 20, 2, 200, "D137: and more than one in this many of its fixes");

    // ---- Writing and checking (D81, D65, D124) ----
    public static readonly Tunable WriteStuckLimit = Define("Write.StuckLimit", 8, 2, 50, "D81: field disagreements in a row before a write is given up");
    public static readonly Tunable WriteStuckMs = Define("Write.StuckMs", 1500, 300, 10_000, "D81: within this long");
    public static readonly Tunable AiRetryAfterMs = Define("Ai.RetryAfterMs", 40, 10, 500, "D65: retry a read that lagged the keys");
    public static readonly Tunable AiMaxReadRetries = Define("Ai.MaxReadRetries", 20, 2, 100, "D65/D124: retries before the AI gives up on a field");
    public static readonly Tunable AiMaxRevisits = Define("Ai.MaxRevisitsPerSession", 20, 0, 200, "D49: looks back at earlier text per session");
    public static readonly Tunable SessionUnshownAfterMs = Define("Session.UnshownAfterMs", 1200, 300, 5000, "D124: keys the field never showed are dropped from the record after this long");
    public static readonly Tunable LearnAfterMs = Define("Learn.AfterMs", 5000, 1000, 60_000, "8.5: a word is learnt once it has stood this long");

    // ---- Suggestions and phrases (D59, D94, D97, D142) ----
    public static readonly Tunable PredictReadRetries = Define("Predict.ReadRetries", 6, 0, 30, "D59/D150: re-reads while the field catches up");
    public static readonly Tunable PredictReadRetryMs = Define("Predict.ReadRetryMs", 25, 5, 200, "D59: between them");
    public static readonly Tunable PredictWaitForPauseMs = Define("Predict.WaitForPauseMs", 90, 20, 1000, "D94: typing pause before a suggestion is looked for");
    public static readonly Tunable PredictLightWaitForPauseMs = Define("Predict.LightWaitForPauseMs", 300, 50, 2000, "D97: in programs whose reads are slow");
    public static readonly Tunable PredictUseModel = Define("Predict.UseModel", 1, 0, 1, "D159: rank word completions by the AI model's reading of the sentence (0 = the word list only)");
    public static readonly Tunable PredictModelTimeoutMs = Define("Predict.ModelTimeoutMs", 250, 50, 1500, "D159: how long a suggestion waits for the model before using the word list");
    public static readonly Tunable PredictModelContextChars = Define("Predict.ModelContextChars", 300, 50, 1500, "D159: how much text before the caret the model reads");
    public static readonly Tunable PredictModelMinChance = Define("Predict.ModelMinChance", 0.15, 0, 1, "D159: the model's pick needs at least this chance");
    public static readonly Tunable PredictModelNextMinChance = Define("Predict.ModelNextMinChance", 0.7, 0, 1, "D163: a next word (nothing typed yet) from the model needs this share of its word choices");
    public static readonly Tunable PredictModelLead = Define("Predict.ModelLead", 2, 1, 10, "D159: and this many times the next candidate's");
    public static readonly Tunable PhrasesMinUses = Define("Phrases.MinUses", 3, 2, 20, "D142: uses before a phrase is offered");
    public static readonly Tunable PhrasesMinDays = Define("Phrases.MinDays", 2, 1, 30, "D142: on at least this many days");
    public static readonly Tunable PhrasesAgreeShare = Define("Phrases.AgreeShare", 0.7, 0.5, 1, "D142: share of times the same words followed");
    public static readonly Tunable PhrasesForgetAfterDays = Define("Phrases.ForgetAfterDays", 90, 7, 730, "D142: unused phrases are forgotten after this long");

    // ---- Correction judgements (D140, D145) ----
    public static readonly Tunable InstantShortPickMaxLength = Define("Instant.ShortPickMaxLength", 2, 0, 5, "D140: an instant fix that shortens a word to this many letters or fewer is left to the AI");
    public static readonly Tunable LangShortLineWords = Define("Lang.ShortLineWords", 5, 0, 20, "D145: a line this short with one foreign word and no English one reads as foreign");
    public static readonly Tunable LangForeignRatio = Define("Lang.ForeignRatio", 1.5, 1, 5, "D116: foreign words needed per English word");

    // ---- Updates (D131, D174) ----
    public static readonly Tunable UpdateIdleMinutes = Define("Update.IdleMinutes", 10, 2, 60, "D131/D174: a found update installs by itself after this long without keyboard or mouse (or at once when the PC is locked)");

    /// <summary>Every value, by name.</summary>
    public static IReadOnlyCollection<Tunable> All => ByName.Values;

    public static bool TryGet(string name, out Tunable t) => ByName.TryGetValue(name, out t!);

    /// <summary>Use a brain's tuning: each value it names, held to its range; every other back to its default.</summary>
    public static void Apply(IReadOnlyDictionary<string, double>? tuning)
    {
        foreach (var t in ByName.Values)
            t.Set(tuning is not null && tuning.TryGetValue(t.Name, out double v) ? v : t.Default);
    }
}
