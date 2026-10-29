# Progress

Newest first. Each entry: date, milestone, requirement IDs completed, checks, next task (2.5.b, 2.7).

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
