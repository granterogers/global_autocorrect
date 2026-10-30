# Progress

Newest first. Each entry: date, milestone, requirement IDs completed, checks, next task (2.5.b, 2.7).

## 2026-10-30 (night) — Split words joined, prediction scored, settings switches, wider CI screen tests

- **Accepted plan (user: "Go ahead and do"):** five steps, each with tests, real-model measurement where it applies, CI screen tests, deploy and push.
- **D50:** words split into pieces ("ne arly", "the ir") are joined when the joined word is in the dictionary. Realistic stress: obvious misspellings left 6 → 3, fixed 89.5% → 90.6%, desyncs 0; tuning 94.6%.
- **D51:** word prediction scored for the first time (`eval predict`). Right suggestions on unseen text 23% → 43.5%, suggestions shown down about 40%, keystrokes saved 10–14%.
- **D52 (9.3.b):** Clean-up page switches "Fix the whole paragraph" and "Look back when you pause" (on by default; Cautious overrides) and "Free the AI's memory after" 5/15/30/60 minutes. New page-load test caught a misspelt icon (would have crashed the page) and a missing row.
- **CI screen tests (11.3.b, 7.6):** 27 tests on the Windows runner. Edge and Chrome, each with textarea, contenteditable, ProseMirror, Lexical, CKEditor 5, Quill, TinyMCE and Slate; Notepad, WPF, password box; suggestion size measured on screen in Notepad and CKEditor. No fixing bugs found in the new editors or Chrome.
- **D53 (7.6):** that size check found the suggestion 15% small in Chromium (caret is one em there, not one line); fixed, and the check tightened to one pixel.
- **Known:** the screenshot tool (`tools/GlobalAutocorrect.Screens`) fails on this PC with "URI prefix is not recognized" from WPF-UI when it isn't run as GlobalAutocorrect.exe; it was already broken before these changes. The page-load test covers what it was used for here.
- **Not automated:** real Teams and Intercom (the user's accounts), Google Docs.## 2026-10-30 (evening) — Rework: field is the truth; realistic simulator; CI screen tests

- **User:** "it works if I type slowly, not when I just type"; wants it confirmed internally, screen tests without them.
- **Root cause (D42):** the app compared its own copy of the typed keys with the field and silently stopped on any difference; real editors differ constantly (blank lines, non-breaking spaces, their own autocorrect, Edge's suggestion after the caret, lagging reads). Now: comparisons tolerant of how editors report text, re-read and carry on instead of stopping (3 failures a minute still stop), whole paragraph (D43), Edge suggestion tolerated and AI context in typed coordinates, Enter ends a line for the AI (D44), in-flight request positions move with earlier fixes (D45), CKEditor 5 (Teams) fixes retyped not arrowed and never across a line break (D46), instant fixes wait for 60 ms of quiet (D47).
- **Realistic simulator:** each real-editor quirk modelled (`SimulatedEditor`), eval `--realistic`; each has a test that fails without its fix (checked both ways).
- **Results (real model, stress set with all quirks on):** 67.2% → 88.6% fixed, desyncs 52 → 0, 96.4% of obvious misspellings fixed; the user's three real texts fixed. Tuning set unchanged (94.4%, 0.20%). Holdout not re-scored (it has no multi-line text, which is what changed).
- **CI screen tests (GitHub Actions, Windows Server, mock model):** `.github/workflows/e2e.yml` on every push to main; 14 tests (Notepad, WPF, Edge: textarea, contenteditable, ProseMirror, Lexical, CKEditor 5, password box). Found the CKEditor wrong-place bug (D46) and the in-flight key race (D47); latest runs all green.
- **Not automated:** real Teams and Intercom (need the user's accounts); a short session on the user's PC if wanted.
## 2026-10-30 (late afternoon) — Long garbled paragraphs, Ctrl+Z removed, suggestion font

- **User reports:** a long, badly typed paragraph in Notepad left obvious misspellings; "inorganic" became "in organic"; the suggestion's text size didn't match the field; Ctrl+Z interception clashes with other apps. (The first Notepad test ran while an E2E run had closed the installed app; that run was stopped and the app restarted.)
- **New stress set** `data/eval/stress.jsonl` (tuning only): 16 new multi-sentence messages at heavy and severe noise plus clean copies, and the user's paragraph.
- **Changes:** D34 (Ctrl+Z left to the app; undo is Ctrl+Alt+Z), D36 (non-words fixed leniently and not counted towards 4.5.d), D37 (a real word split only when the pair is 3× as common), D38 (sweep of non-words the model tried to fix), suggestion drawn in the field's own font at its size (UIA font name, 7.6).
- **Results:** stress set 83.5% → 89.1% fixed (severe 79.6% → 87.6%), false edits unchanged at 0.32%. Tuning set identical message by message (94.4%, 0.20%). Holdout scored once: 92.9% fixed, 0.08% false edits, p50 332 ms (unchanged: mostly single sentences).
- **Measured, not changed:** llama-server load modes. The model holds about 4 GB of RAM by design (per-layer embeddings); no flag reduces it without reserving more. Idle unload after 15 minutes stays.
- **Checks:** fast tier green (221 Core, 19 Llm, 58 Windows, 32 App). Deployed to %LOCALAPPDATA%\Programs\GlobalAutocorrect. Not run: the E2E tests for the Ctrl+Z change and the suggestion font (they need the keyboard idle).
## 2026-10-30 (afternoon) — Accuracy push after RC1 review

- **User asked to push accuracy higher before accepting RC1.** All tuning on the tuning set only (4.5.e, 6.1.e); the holdout scored once at the end.
- **Method:** the eval can now dump every tuning message with the raw whole-message model reply (`--dump`, refuses the holdout), which separated "the model missed it" from "a rule threw the model's fix away".
- **Changes:** D30 (two likely keyboard slips; lone `i` capitalised), D31 (a fix beside a dropped function word; a capital after a sentence end the reply adds; possessives), prompt v4 (dropped small words). A second clean-up pass was measured and not adopted (no gain, twice the time).
- **Tuning set:** 90.8% → 94.4% errors fixed, false edits 0.20% (unchanged), exact 83% → 89%. Log: `docs/tuning-log.md` T7–T10.
- **Holdout (once, on the frozen code; it had also been scored once before this round, disclosed in the README):** 92.3% → **92.9%** errors fixed (95% target still missed), false edits 0.19% → **0.08%**, exact 84% → 87%, p50 361 ms, p95 535 ms. `docs/metrics/holdout-2026-10-30.*`.
- **Checks:** fast tier green (195 Core, 19 Llm, 55 Windows, 31 App; 9 new rule tests). trace-check 223/223, coverage 0 gaps. RC zip rebuilt with the new metrics. E2E not re-run: the change is in the rules and prompt, which the E2E mock model doesn't exercise.
- **8-bit model (user approved the download):** tuning set 94.4% / 0.20%, identical to 5-bit, slower and about 2 GB more graphics memory. Not adopted (D32); the holdout was not re-scored.
- **Next task:** the user's RC1 review. Every lever inside the brief (rules, prompt, second pass, model precision) has now been measured.
## 2026-10-30 — Release candidate 1 (0.9.0-rc1) ready for review (2.6.e)

- **Audit:** an independent read of the brief against the code found 4 blockers, 7 majors and 12 minors; all blockers and majors and the meaningful minors are fixed with tests (pause 8.2, hotkey thread 8.1, UIA failures 10.6.c, undo with modifiers 4.8.a, auto-learn 8.5, password history 8.3, stale suggestion 7.5–7.6, About numbers 9.3.g, midnight counts 6.5, retry backoff and orphan servers 5.6).
- **End-to-end (11.3):** final full run 11/12. Both Tab tests then re-ran twice, 4/4. The one Tab failure ("Please t me") did not repeat and is unexplained; it is recorded in `e2e-results.txt`. These runs found 8 bugs, all fixed with unit tests: ToUnicodeEx crash, false desync, Ctrl+Z not seen (hook tracks modifiers), lost edit on one failed read (4.6.c), Edge read lagging the last keys (4.4.a), undo while Ctrl held (D29, 4.8.a–b), Edge arrow keys out of step (D28, 10.7.a), and orphaned model servers (5.6).
- **Accuracy (6.6, holdout, final code):** 92.3% of errors fixed (target 95%, **missed**), 0.19% false edits (target met), p50 344 ms, p95 511 ms.
- **Checks:** fast tier green (186 Core, 19 Llm, 55 Windows, 31 App). trace-check: 223/223 IDs. coverage: 0 gaps. UK spelling test over UI, messages and README. Package `artifacts/GlobalAutocorrect-0.9.0-rc1.zip` (6 MB) starts from a fresh unzip.
- **Decisions added:** D28 (retype a short tail instead of arrow keys), D29 (undo waits for modifiers to be released).
- **Needs the user (2.6.e):**
  - Accept or reject RC1 with accuracy at 92.3%.
  - Confirm D22 (key-press typing instead of Unicode keystrokes, a deviation from 10.7.a).
  - Try Intercom, Claude.ai and ChatGPT in Edge (not automatable).
  - Outlook untested.
  - Never tag v1.0.0 until RC1 is accepted (13.3).
- **Next task:** the user's RC1 review.
## 2026-10-29 (evening) — Spikes passed; Feature A wired end to end; tuning in progress

- **BRIEF re-read** (2.2.c).
- **Spike A PASS** (PLAN §4.1): Gemma 4 E4B Q5_K_M is the default (p50 360 ms, 91.8% fixed, 0.63% false edits, 43/43 layers on CUDA0). E2B is the CPU fallback. **Spike B PASS 6/7** after D22 (key-event typing fixed a Notepad reorder bug). Outlook is a harness limitation, logged as untested.
- **Done since last entry:**
  - M3 (SymSpell from Leipzig UK, I; noisy-channel instant layer D23, L).
  - M5 (host, watchdog, idle unload, CPU fallback with E2B, verify.ps1: all 7 checks pass on this PC; D21 per-launch API key).
  - M7 (hook, translator, UIA worker, target, focus tracker, engine loop, exclusions and integrity gate (I), hotkeys (I), app host, single instance, never elevated).
  - M6 runner with tracing.
  - E2E suite (FlaUI, Playwright, mock model, idle guard; not yet run).
  - Overlays.
  - Packaging script.
  - README draft.
- **Tuning (tuning set only):** errors fixed 86.1 → 88.6 → 89.2 → 90.5% (T4), false edits 0.67 → 0.29%. T5 is running. Log: `docs/tuning-log.md`. Decisions D23–D27.
- **Checks:** fast tier green (145 Core, 17 Llm, 32 Windows).
- **In flight:** T8.1/T8.2 n-grams and predictor (I). T9.1/9.2/9.5 tokens, icons and tray (I). Tuning T5.
- **Next tasks:**
  - Prediction controller and Tab path (L).
  - Settings and first-run windows (I, using `Models/ModelDownloader`).
  - Screenshot review (9.5, L).
  - E2E run when idle.
  - Holdout eval once (6.1.e).
  - README numbers, then `check.ps1 full`, then RC stop (2.6.e).
- **Noted for the user:** their global push hook double-fires and tags commits that weren't pushed (tags v0.2–v0.9 so far; never v1.0.0).

## 2026-10-29 — M0, M1, M2, M4 core; spikes in progress

- **BRIEF re-read** before closing this block (2.2.c).
- **M0 done:** T0.1 scaffold (I), T0.2 check.ps1 + trace-check (I). `check.ps1 fast` green in about 4 s (11.1.a).
- **M1 done:** 410 clean messages (I, two batches), 50 hand-garbled (L), UK keyboard noise + stratified split (I): holdout 200 (23.5% clean), tuning 260 (23.8% clean); scorer (I). IDs 6.1.a–e, 6.3.a–c.
- **M2 done (L):** tokeniser, two-stage word diff (LCS anchors, then alignment with merge/split), edit rules. IDs 4.4.e, 4.5.a–e, 3.4. D19 (what counts as a changed word).
- **M3 partial:** Hunspell en_GB-ise checker (L), personal dictionary (I, T3.4), instant corrector (L). Waiting for the corpus to build SymSpell frequencies (T3.1, T3.2 SymSpell part).
- **M4 done (L):** typing session (typed buffer, D5), write gate (4.6, D6/D7/D8), AI trigger (4.4.a/b), undo (4.8, D9/D10), counters (6.5), correction engine, simulated editor (10.2). 30 engine tests on virtual time.
- **M5 partial:** LLM client (I, T5.1) with per-launch API key (D21).
- **Spike A (in progress):** see PLAN §4.1. Qwen3.5-4B: p50 317 ms, 89.1% fixed, 0.94% false edits. Gemma 4 E2B: p50 200 ms, 91.2% fixed, 2.36% false edits. Gemma 4 E4B: still downloading. Both models are fully offloaded to CUDA0.
- **Spike B (in progress):** harness ready. The run is waiting for 2 minutes of keyboard/mouse idle (2.8, D20), because the user is active at the PC.
- **Delegation log (2.12.c/e):** T0.1, T0.2, T1.1a/b, T1.3, T1.4, T3.4, T5.1 went to the implementer. T5.1 was blocked by a file lock from my own Spike B run, not its code. I reviewed it, added the API key and committed it.
- **Next task:** Spike A on E4B, then decide the model. Spike B result. Then T3.1/T3.2 (SymSpell frequencies from the corpus), M5 host and verify.

## 2026-10-29 — Planning

- **Prerequisites (3.5, 2.3.a):** .NET SDK 10.0.403 ✅ (installed by the user after a 2.6.a stop), Git 2.53.0 ✅, Node.js 24.14.1 ✅, NVIDIA driver 596.98 on RTX PRO 2000 Blackwell (sm_120, 8 GB) ✅, Windows Sandbox ✅ (`WindowsSandbox.exe` present; feature state needs elevation to query). PowerShell 7.6.9 also installed.
- **Done:** 2.2.a (BRIEF.md first commit `0850a00`, pushed with baseline tag `v0.1.0`, D1), 2.12.a (implementer agent), 3.6 (CLAUDE.md, .claude/settings.json), 2.3.b–e (PLAN.md, DECISIONS.md, pinned versions §4, traceability §9: all 223 IDs present).
- **Spike B targets available:** Notepad 11.2607, Word, Outlook (classic and new), Edge, Chrome.
- **Next task:** T0.1 scaffold (implementer), then M1 data, then Spike A and Spike B.
