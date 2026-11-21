using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using GlobalAutocorrect.App.Theme;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Write;
using GlobalAutocorrect.Windows.Overlay;

namespace GlobalAutocorrect.App.Overlay;

/// <summary>
/// Feature B on screen (7.1–7.6) and the correction underline (4.7). After each key it predicts in the background (never
/// blocking typing), shows the suggestion at the caret, and arms Tab at exactly that key sequence: Tab is only taken if
/// nothing happened since the suggestion appeared (7.4). A key that doesn't match the suggestion hides it (7.5).
/// </summary>
public sealed class OverlayController
{
    private readonly AppHost _host;
    private readonly Dispatcher _ui;
    private readonly SuggestionOverlay _suggestion;
    private readonly UnderlineOverlay _underline;
    private volatile string _shown = "";
    private long _generation;

    public OverlayController(AppHost host, Dispatcher ui)
    {
        _host = host;
        _ui = ui;
        var accent = new SolidColorBrush(Palette.ToColor(Palette.AccentPrimary));
        accent.Freeze();
        _suggestion = new SuggestionOverlay(new SolidColorBrush(Palette.ToColor(Palette.PausedGrey)));
        _underline = new UnderlineOverlay(accent);
        host.KeyProcessed += OnKey;
        host.Corrected += OnCorrected;
        host.SuggestionAcceptedByTab += OnAccepted;
    }

    private void OnKey(KeyInput k)
    {
        if (_host.Paused || _host.Predictor is not { Enabled: true } || k.Kind is not (KeyKind.Char or KeyKind.Backspace))
        {
            Hide();
            return;
        }
        // 7.5: every key hides the suggestion; a fresh one is predicted from the new caret position, so it is never
        // drawn at a stale place.
        Hide();
        // D158 (user: "if I backspace whilst typing a word … no more suggestions are given"): after a Backspace the
        // suggestion comes back for what is left, once the field shows the deletion (it ends as the typed record does).
        char key = k.Kind == KeyKind.Backspace ? _host.TypedTail ?? '\0' : k.Char;
        if (key == '\0') return;   // the field is empty now: nothing to complete
        StartPredict(k.Seq, key);
    }

    private void StartPredict(long seq, char key)
    {
        long gen = Interlocked.Increment(ref _generation);
        long keyAt = System.Diagnostics.Stopwatch.GetTimestamp();   // D161: TickCount64 moves in ~15.6 ms steps
        _ = Task.Run(async () =>
        {
            // D94: read the field only once typing pauses, never once per key of a burst.
            // D97: longer in a program whose reads are slow.
            bool light = _host.Target?.LastSnapshot?.ProcessName is { } app && _host.ReadCost.IsLight(app);
            await Task.Delay(FieldCatchUp.PauseFor(light));
            if (_host.Hook is not { } hook || !FieldCatchUp.Paused(seq, hook.LatestKeySeq, (long)System.Diagnostics.Stopwatch.GetElapsedTime(keyAt).TotalMilliseconds, light)
                || gen != Interlocked.Read(ref _generation))
            {
                // D147: which of the two, under test only (numbers, never text): a later key, or a later check.
                Why(UnderTest ? $"typing seq={seq} latest={_host.Hook?.LatestKeySeq} gen={gen}/{Interlocked.Read(ref _generation)}" : "typing", null);
                return;
            }
            try { Predict(seq, gen, key); }
            catch (Exception e) { Why("failed " + e.GetType().Name, null); throw; }
        });
    }

    private void Predict(long seq, long gen, char key)
    {
        var target = _host.Target;
        var hook = _host.Hook;
        if (target is null || hook is null) return;
        bool light = target.LastSnapshot?.ProcessName is { } app && _host.ReadCost.IsLight(app);   // D97
        // D59: a browser may not have drawn this key yet; predicting from that read suggested for the previous letter.
        // D150 (E2E, CKEditor, twice): one read said the field had no text just after the last key, and no suggestion
        // came at all. A read that can't find the caret gets the same few retries as one that's behind.
        bool known = target.ReadForSuggestion(light).CaretKnown;
        for (int i = 0; i < FieldCatchUp.Retries && (!known || !FieldCatchUp.ShowsKey(target.LastSnapshot?.Before ?? "", key)); i++)
        {
            Thread.Sleep(FieldCatchUp.RetryDelayMs);
            if (hook.LatestKeySeq != seq || gen != Interlocked.Read(ref _generation)) { Why("stale", null); return; }
            known = target.ReadForSuggestion(light).CaretKnown;
        }
        if (!known) { Why("caret-unknown", target.LastSnapshot?.Before); return; }
        if (!FieldCatchUp.ShowsKey(target.LastSnapshot?.Before ?? "", key)) { Why("field-behind", target.LastSnapshot?.Before); return; }
        if (target.LastSnapshot is not { CaretRect: { } caret } snap) { Why("no-caret-rect", target.LastSnapshot?.Before); return; }
        // Stale if the user typed on while we read (7.4: the caret must not have moved since).
        if (hook.LatestKeySeq != seq || gen != Interlocked.Read(ref _generation)) { Why("stale", null); return; }
        var s = _host.Predictor?.Suggest(snap.Before, snap.ProcessName);   // D57: this app's own history counts for more
        if (s is null || s.Completion.Length == 0) { Why("no-suggestion", snap.Before); return; }
        Why("shown", snap.Before);
        Show(s.Completion, caret, seq, snap.FontName, snap.CaretIsFontHeight, snap.FontSizePt, snap.ProcessName, s.IsNextWord);
    }

    private static readonly bool UnderTest = Environment.GetEnvironmentVariable("GA_E2E") == "1";

    /// <summary>
    /// Screen tests only (GA_E2E=1): why a suggestion was or wasn't shown, with the code points of the last few
    /// characters before the caret, so a CI failure can be diagnosed. Never the text itself, and never in normal use (8.6).
    /// </summary>
    private static void Why(string reason, string? before)
    {
        if (!UnderTest) return;
        string tail = before is null ? "" : string.Join(' ', before[Math.Max(0, before.Length - 4)..].Select(c => $"U+{(int)c:X4}"));
        AppHost.Diagnose($"predict: {reason} beforeLength={before?.Length ?? -1} last=[{tail}]");
    }

    private string _shownApp = "";
    private bool _shownIsNext;

    private void Show(string completion, (double X, double Y, double W, double H) caret, long seq, string? fontName, bool caretIsFontHeight = false, double? fontSizePt = null, string? app = null, bool nextWord = false)
    {
        var hook = _host.Hook;
        if (completion.Length == 0 || hook is null) { Hide(); return; }
        _ui.BeginInvoke(() =>
        {
            if (hook.LatestKeySeq != seq) { Why("stale-at-show", null); return; }   // something happened in between
            _suggestion.ShowAt(completion, caret, fontName, caretIsFontHeight, fontSizePt);
            Why($"drawn h={caret.H:F0}", null);
            _shown = completion;
            hook.SuggestionArm.ArmAt(seq);   // Tab now means "accept", until anything else happens
            // D165: counted per program, numbers only, so the brain can learn how often a suggestion is worth showing.
            _shownApp = app ?? ""; _shownIsNext = nextWord;
            _host.Health.SuggestionShown(_shownApp, nextWord);
        });
    }

    private void Hide()
    {
        _host.Hook?.SuggestionArm.Disarm();
        if (_shown.Length == 0) return;
        _shown = "";
        _ui.BeginInvoke(_suggestion.HideSuggestion);
    }

    private void OnAccepted()
    {
        string text = _shown;
        Hide();
        if (text.Length == 0) return;
        _host.Health.SuggestionTaken(_shownApp, _shownIsNext);   // D165
        // D80: the word and the space after it, as phone keyboards do, then straight away a guess at the next word, so
        // Tab can be pressed again and again through an obvious sentence. A full stop typed next takes the space back
        // (D55). No key reaches the hook while we type, so the guess starts from the Tab's place in the key order.
        _host.AcceptSuggestion(text + " ", () =>
        {
            if (_host.Hook is { } hook && _host.Predictor is { Enabled: true } && !_host.Paused) StartPredict(hook.LatestKeySeq, ' ');
        });
    }

    private void OnCorrected(CorrectionNotice n)
    {
        if (!_host.Settings.UnderlineEnabled || _host.Uia is not { } uia) return;   // 4.7 setting
        _ = Task.Run(() =>
        {
            var rect = uia.Invoke(r => r.RangeRect(n.CaretDistance, n.Length));
            if (rect is { } r) _ui.BeginInvoke(() => _underline.Flash(r));
        });
    }
}
