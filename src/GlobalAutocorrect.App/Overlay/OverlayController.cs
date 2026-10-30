using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using GlobalAutocorrect.App.Theme;
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
        if (_host.Paused || _host.Predictor is not { Enabled: true } || k.Kind != KeyKind.Char)
        {
            Hide();
            return;
        }
        // 7.5: every key hides the suggestion; a fresh one is predicted from the new caret position, so it is never
        // drawn at a stale place.
        Hide();
        long gen = Interlocked.Increment(ref _generation);
        _ = Task.Run(() => Predict(k.Seq, gen));
    }

    private void Predict(long seq, long gen)
    {
        var target = _host.Target;
        var hook = _host.Hook;
        if (target is null || hook is null) return;
        if (!target.ReadWindow().CaretKnown || target.LastSnapshot is not { CaretRect: { } caret } snap) return;
        // Stale if the user typed on while we read (7.4: the caret must not have moved since).
        if (hook.LatestKeySeq != seq || gen != Interlocked.Read(ref _generation)) return;
        var s = _host.Predictor?.Suggest(snap.Before);
        if (s is null || s.Completion.Length == 0) return;
        Show(s.Completion, caret, seq, snap.FontName, snap.CaretIsFontHeight);
    }

    private void Show(string completion, (double X, double Y, double W, double H) caret, long seq, string? fontName, bool caretIsFontHeight = false)
    {
        var hook = _host.Hook;
        if (completion.Length == 0 || hook is null) { Hide(); return; }
        _ui.BeginInvoke(() =>
        {
            if (hook.LatestKeySeq != seq) return;   // something happened in between: don't show a stale suggestion
            _suggestion.ShowAt(completion, caret, fontName, caretIsFontHeight);
            _shown = completion;
            hook.SuggestionArm.ArmAt(seq);   // Tab now means "accept", until anything else happens
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
        if (text.Length > 0) _host.AcceptSuggestion(text);
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
