# Progress

Newest first. Each entry: date, milestone, requirement IDs completed, checks, next task (2.5.b, 2.7).

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
