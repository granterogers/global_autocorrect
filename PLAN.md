# PLAN: Global Autocorrect

Source of truth: `BRIEF.md`. Decisions: `DECISIONS.md` (D1 to D18). Status: `PROGRESS.md`.
Owner key: **L** = lead (main session, high-risk per 2.12.b), **I** = implementer subagent (2.12.c).

---

## 1. Architecture

```
 physical keys ─► [KeyboardHook]──enqueue──► [InputPump]──► TypingSession (Core)
   (LL hook thread, timestamps only)              │              │ word end           │ sentence end / pause
                                                  │              ▼                    ▼
 WinEvents (focus, caret) ─► FocusTracker ────────┤      InstantCorrector       AiTrigger ─► LlmCorrector ─► llama-server
                                                  │       (Hunspell+SymSpell)          (Core)        (HTTP 127.0.0.1)
                                                  │              │                    │
                                                  │              ▼                    ▼
                                                  │        EditCandidates ──► EditRules (4.5) ──► WriteGate (4.6)
                                                  │                                              │
                                    ITextTarget ◄─┴──────────────────────────────────────────────┘
                     ┌──────────────┴───────────────┐
             UiaTextTarget (Windows)        SimulatedEditor (tests, eval)
          UiaReader (STA worker) + SendInputWriter
```

- **Core** (`net10.0`, no Windows refs, 10.1): everything that decides *what* to change and *when*. Driven by an `IClock` so tests and eval run on virtual time.
- **Windows layer** (`net10.0-windows`, 10.3): hook, UIA, SendInput, overlay, hotkeys. Implements `ITextTarget` and feeds events into Core.
- **Llm** (`net10.0`, 10.5): llama-server process host + HTTP client. Process management is portable .NET; the GPU-preference registry write is in the setup script.
- **App** (`net10.0-windows`, WPF + WPF-UI, 10.4): tray, settings, first-run, model management, wiring.

### 1.1 Event flow for one word

1. Hook callback gets `KBDLLHOOKSTRUCT`. If `dwExtraInfo == InjectionTag` → pass through, nothing queued (10.7.b). If `LLKHF_INJECTED` and not ours → queue `ForeignInput` (4.2). Else queue `KeyEvent{Vk, Scan, Flags, IsDown, Ticks, Seq}`. Return `CallNextHookEx`. Two exceptions are decided synchronously from volatile flags: Tab (7.4) and Ctrl+Z (4.8.a). See §6.4.
2. `InputPump` (background thread) translates key-downs to characters (`ToUnicodeEx`, flag 0x4, UK layout, modifier state tracked by the pump) and calls `TypingSession.OnKey`.
3. `TypingSession` keeps the typed buffer (D5). On a word-ending character it raises `WordFinished`. On `.`/`!`/`?` + space/Enter it raises `SentenceEnded`. A 700 ms idle timer raises `Paused`.
4. `WordFinished` → `InstantCorrector.TryCorrect(word, context)` → maybe an `Edit` → `WriteGate`.
5. `SentenceEnded`/`Paused` → `AiTrigger` reads `ITextTarget.ReadWindow()`, builds the request, awaits the LLM, diffs, applies the edit rules and timing rules, then writes through `WriteGate`.
6. `WriteGate` → `ITextTarget.ApplyEdit` → `ITextTarget.Verify` (4.6.d) → underline (4.7) → undo record (4.8).

---

## 2. Project layout

```
GlobalAutocorrect.slnx
Directory.Build.props            # LangVersion latest, Nullable enable, TreatWarningsAsErrors, net10 defaults
Directory.Packages.props         # central package versions = §4 pinned versions
check.ps1                        # fast | model | eval | e2e | full (11.1)
scripts/setup.ps1                # downloads pinned GGUF, llama.cpp, cudart, dictionary; SHA-256 checks (5.7)
scripts/verify.ps1               # 5.8.a–g
scripts/trace-check.ps1          # every requirement ID in BRIEF.md appears in PLAN.md §9 table (2.3.c)
src/GlobalAutocorrect.Core/      # 10.1
  Text/        Tokeniser.cs Token.cs TextEdit.cs WordDiff.cs EditRules.cs EditRuleSettings.cs Similarity.cs TokenClassifier.cs SentenceSplitter.cs
  Session/     TypingSession.cs KeyChar.cs SessionBreakReason.cs ProtectedRange.cs
  Instant/     InstantCorrector.cs ISpellChecker.cs HunspellSpellChecker.cs SymSpellSuggester.cs
  Ai/          ILlmClient.cs PromptBuilder.cs AiCorrector.cs AiTrigger.cs
  Write/       WriteGate.cs WritePlan.cs
  Targets/     ITextTarget.cs TextWindow.cs ApplyResult.cs
  Undo/        UndoManager.cs CorrectionRecord.cs
  Prediction/  NgramModel.cs UserHistory.cs Predictor.cs Suggestion.cs
  Dictionary/  PersonalDictionary.cs
  Settings/    AppSettings.cs SettingsStore.cs
  Telemetry/   Counters.cs
  Engine/      CorrectionEngine.cs IClock.cs SystemClock.cs
src/GlobalAutocorrect.Llm/       # 10.5, 5.x
  LlamaServerClient.cs LlamaServerHost.cs ServerOptions.cs Watchdog.cs
src/GlobalAutocorrect.Windows/   # 10.3
  Native/NativeMethods.cs  Input/KeyboardHook.cs Input/InputPump.cs Input/KeyTranslator.cs Input/InjectionTag.cs
  Uia/UiaWorker.cs Uia/UiaReader.cs Uia/FocusTracker.cs
  Write/SendInputWriter.cs  Targets/UiaTextTarget.cs  Targets/WindowInfo.cs Targets/IntegrityLevel.cs Targets/AppExclusions.cs
  Overlay/OverlayWindow.cs Overlay/SuggestionOverlay.cs Overlay/UnderlineOverlay.cs  Hotkeys/HotkeyManager.cs
src/GlobalAutocorrect.App/       # 10.4, 9.x
  App.xaml(.cs) Theme/Tokens.xaml Theme/Icons/  Tray/TrayController.cs  Views/*.xaml ViewModels/*.cs  FirstRun/*  Models/ModelManager.cs SingleInstance.cs
tools/GlobalAutocorrect.DataGen/ # 6.1 noise generator, split; corpus → freq dict + n-gram build (7.2)
tools/GlobalAutocorrect.Eval/    # 6.2–6.6, Spike A bench
tools/SpikeB/                    # 2.4.b text-access spike console
tests/GlobalAutocorrect.Core.Tests/       # xUnit v3; includes Simulated/SimulatedEditor.cs (10.2)
tests/GlobalAutocorrect.Llm.Tests/        # model tier (11.1.b); trait Category=Model
tests/GlobalAutocorrect.E2E/              # FlaUI + Playwright host (11.3); trait Category=E2E
tests/pages/                              # textarea, contenteditable, password, ProseMirror, Lexical, CKEditor 5 (esbuild bundles)
data/eval/                                # messages.jsonl, garbled.jsonl, tuning.jsonl, holdout.jsonl
assets/                                   # en_GB-ise.dic/.aff, freq_en_GB.txt, ngram.bin, icons
artifacts/                                # (git-ignored) metrics.json, eval-report.md, screenshots, package zip
```

Cache (outside repo, 11.4): `%LOCALAPPDATA%\GlobalAutocorrect\cache\{llama,models,corpus}`.
User data (8.6): `%APPDATA%\GlobalAutocorrect\{settings.json, dictionary.txt, history.txt, counters.json, app.log}`. All UTF-8. No typed text in any of them except words the user chose to add (dictionary) and unigram counts (7.3).

---

## 3. Milestones

| # | Milestone | Content | Owner mix |
|---|---|---|---|
| M0 | Scaffold | solution, props, check.ps1 fast, trace-check, test projects | I |
| M1 | Eval data | 400 clean + 50 garbled messages, noise generator, split, scorer | I (data, scorer), L (review) |
| SA | Spike A | setup script (download part), bench tool, run 3 candidates | L |
| SB | Spike B | test pages, UIA reader + SendInput writer prototype, 7 targets | L |
| M2 | Diff and rules | tokeniser, WordDiff, EditRules | L |
| M3 | Instant layer | dictionary assets, Hunspell + SymSpell, personal dictionary | I, L review |
| M4 | Session and timing | TypingSession, AiTrigger, WriteGate, UndoManager, SimulatedEditor, CorrectionEngine | L |
| M5 | Model runtime | LLM client, host, watchdog, idle unload, verify script | I (client), L (host/verify) |
| M6 | Pipeline eval | full-pipeline eval on virtual clock, tune on tuning set, report | L |
| M7 | Windows layer: Feature A end to end | hook, pump, UIA, writer, exclusions, integrity, hotkeys, headless host, E2E | L (hook/UIA/write), I (exclusions, hotkeys) |
| M8 | Feature B | n-gram build, predictor, history, overlay, Tab accept | I (build, predictor), L (Tab path) |
| M9 | Interface | tokens, icons, tray, settings pages, first run, underline, screenshot review | I (views), L (review 9.5) |
| M10 | Release candidate | README, full tier, package, Defender notes, RC report | L |

Every milestone ends with `pwsh ./check.ps1 fast` green, a `PROGRESS.md` note, and a commit + `git push` carrying the IDs (2.5).

---

## 4. Pinned versions (2.3.d, 2.4.c)

Looked up 2026-10-29. **Do not upgrade during the build.**

| Item | Version | Source / checksum |
|---|---|---|
| .NET SDK | 10.0.403 | installed |
| PowerShell | 7.6.9 | installed |
| WPF-UI / WPF-UI.Tray | 4.3.0 / 4.3.0 | NuGet |
| CommunityToolkit.Mvvm | 8.4.2 | NuGet |
| WeCantSpell.Hunspell | 7.0.1 | NuGet |
| SymSpell | 6.7.3 | NuGet |
| Interop.UIAutomationClient | 10.19041.0 | NuGet |
| FlaUI.Core / FlaUI.UIA3 | 5.0.0 / 5.0.0 | NuGet (tests only) |
| xunit.v3 / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 4.0.2 / 4.0.1 / 18.11.0 | NuGet |
| Microsoft.Playwright (.NET, tests only, channel `msedge`) | 1.64.1 | NuGet |
| esbuild / prosemirror-state / -view / -model / -schema-basic / -example-setup | 0.28.2 / 1.4.4 / 1.42.8 / 1.25.12 / 1.2.5 / 1.2.3 | npm (test pages) |
| lexical / @lexical/rich-text | 0.53.0 / 0.53.0 | npm (test pages) |
| ckeditor5 (Teams' editor; GPL licence key) | 49.0.0 | npm (test pages), added 2026-10-30 for the CI E2E run |
| llama.cpp | build **b11960** (= release v0.8.0) | `llama-b11960-bin-win-cuda-13.4-x64.zip` sha256 `31c75ea62b80f0816c855ea783ef68757bc2d48028edaa57d904e4982bddea4a` |
| cudart | 13.4 | `cudart-llama-bin-win-cuda-13.4-x64.zip` sha256 `738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668` |
| llama.cpp CPU fallback (5.1.c) | b11960 | `llama-b11960-bin-win-cpu-x64.zip` sha256 `73f962f82850d244db47eaacb9d913637027b82399124324298576d58c5f4287` |
| Gemma 4 E4B Q5_K_M | unsloth/gemma-4-E4B-it-GGUF @ `bfc15c382204943c3a8fff0c750b94ae2364d7a3` | `gemma-4-E4B-it-Q5_K_M.gguf` 5,228 MB sha256 `a5d6e634db151368d2caa4270fd32ab604d96c2d1291950586989f46d8e17d36` |
| Gemma 4 E4B Q4_K_M | same repo | `gemma-4-E4B-it-Q4_K_M.gguf` sha256 `85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87` |
| Qwen3.5-4B Q5_K_M | unsloth/Qwen3.5-4B-GGUF @ `e87f176479d0855a907a41277aca2f8ee7a09523` | `Qwen3.5-4B-Q5_K_M.gguf` 2,998 MB sha256 `8814232b85594dcd46c50e5b8b29324a7efe9e746edbe8a3d1df3d3fce7aad39` |
| Qwen3.5-4B Q4_K_M | same repo | `Qwen3.5-4B-Q4_K_M.gguf` sha256 `00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4` |
| Gemma 4 E2B Q5_K_M | unsloth/gemma-4-E2B-it-GGUF @ `0314792d7f1f7e229411f620751375812bb9faf2` | `gemma-4-E2B-it-Q5_K_M.gguf` 3,201 MB sha256 `90293b8cdaf9c973012bf4df8a1e92bde7d74ad66a4fe56cf905ccd563d660c5` |
| Gemma 4 E2B Q4_K_M | same repo | `gemma-4-E2B-it-Q4_K_M.gguf` sha256 `740185b21d22ceb83a11c3aa62ad5842ef32c70f6096d756bbee85a1e4ec34b8` |
| Hunspell en_GB-ise | SCOWL rel-2026.02.25 | `hunspell-en_GB-ise-2026.02.25.zip` sha256 `d6fbb91ae7824c52fb02f74d7bc2cd9092f130faec60f42326a59437fa7247a3` (github.com/en-wl/wordlist) |
| Corpus | Leipzig `eng-uk_web-public_2018_1M` | downloads.wortschatz-leipzig.de/corpora; **CC BY 4.0**; sha256 `5973e28f0c172bac612beefc416d55da18b71a90dc5e2599ef997e6ebb478524` |

Model download URL form: `https://huggingface.co/<repo>/resolve/<commit>/<file>`.

### 4.1 Spike results

**Spike A (2.4.a), 2026-10-29: PASS.** 50 evenly spaced tuning messages (every noise level and the garbled set). Raw model output scored against the clean text (D18). llama.cpp b11960 CUDA 13.4, `-ngl 99 -c 4096 -np 1 --reasoning-budget 0`, temperature 0, same prompt (`PromptTemplate.Default`) for all. Results in `artifacts/spikeA-*.json`.

| Candidate | p50 | p95 | Errors fixed | False edits | Exact | VRAM in use (before → loaded) | GPU evidence | Bar |
|---|---|---|---|---|---|---|---|---|
| **Gemma 4 E4B Q5_K_M (5.3.a)** | **360 ms** | 552 ms | **91.8%** | **0.63%** | 84% | 4.0 → 7.6 GB of 8.15 | CUDA0, 43/43 layers | **PASS** |
| Qwen3.5-4B Q5_K_M, thinking off (5.3.b) | 317 ms | 473 ms | 89.1% | 0.94% | 76% | 4.2 → 7.5 GB | CUDA0, 33/33 layers | fail (fixed < 90%) |
| Gemma 4 E2B Q5_K_M (5.3.c) | 200 ms | 301 ms | 91.2% | 2.36% | 64% | 4.1 → 6.2 GB | CUDA0, 36/36 layers | fail (false ≥ 2%) |

- **Default model (5.3.d): Gemma 4 E4B Q5_K_M.** It is the only candidate to clear both quality bars, with p50 a quarter of the budget. Q4_K_M wasn't needed. **CPU fallback (5.1.c, 5.3.c): Gemma 4 E2B Q5_K_M.**
- The about 4 GB "in use" before loading is the Windows desktop, WebView2 apps and an unrelated llama-server from another project. 7,043 MiB was free at load.
- Most remaining misses are noise that deleted "the", leaving valid English ("Ring council about…"), and "Wi-fi" capitalised to "Wi-Fi". Tuning happens in M6 on the tuning set only.

**Spike B (2.4.b), 2026-10-29: PASS, 6/7 without desync.** Type `alpha bravo chralie delta echo` as the user (untagged SendInput), read the caret context with UIA, fix `chralie` → `charlie` with one tagged batch from 11 characters before the caret, then re-read and verify the text and the caret position (`artifacts/spikeB.json`; run 1 in `spikeB-run1.json`).

| Target | Read via | Run 1 | Run 2 (after D22) |
|---|---|---|---|
| Notepad (Win11, 11.2607) | TextPattern2 caret range | desync: `alpha bravo  delta echoeeeeeee` | ✅ |
| Word | TextPattern2 caret range | ✅ (Word auto-capitalised "Alpha"; adopted) | ✅ |
| Outlook (classic) | — | ❌ harness couldn't focus the message body | ❌ same |
| Edge textarea | TextPattern selection | ✅ | ✅ |
| Edge contenteditable | TextPattern selection | ✅ | ✅ |
| Edge ProseMirror | TextPattern selection | ✅ | ✅ |
| Edge Lexical (production build, rich-text plugin) | TextPattern selection | ✅ | ✅ |

- Run 1's Notepad desync led to D22: replacement text is typed as real key events. Notepad reorders a burst of Unicode packets.
- Outlook is a harness limitation, not an observed desync. The compose body is Word's editor, which passes. It is listed as untested in the README.
- Chromium exposed the caret through `TextPattern.GetSelection` (collapsed), not TextPattern2, which is the 10.6.a fallback path.

**Both spikes pass. Versions in §4 are pinned for the build (2.4.c).**

---

## 5. Risks and how they are retired

| Risk | Retired by |
|---|---|
| R1 Model too slow or too sloppy on 8 GB Blackwell | Spike A (pass bar 2.4.a); Q4_K_M fallback; E2B fallback |
| R2 Silent CPU fallback (12.7) | Spike A logs CUDA device and offload; verify script 5.8.a–d |
| R3 UIA can't read caret/text in some editors | Spike B across 7 targets; 4.1.b "do nothing" path |
| R4 SendInput desync (caret moves, host autocorrect, wrapped lines) | Spike B; key-relative writes (D6); verify-after-write (4.6.d); paragraph-only edits (D8) |
| R5 Hook removed by Windows when slow (12.6) | hook callback does no work (10.3.a); unit-timed in E2E; re-install on `LowLevelHooksTimeout` symptom (heartbeat) |
| R6 AI rewrites style | edit rules 4.5 measured by false-edit metric on holdout |
| R7 Defender flags keylogger behaviour (12.10) | verify 5.8.g; README section |
| R8 Disk space (D4) | one model at a time during the spike |
| R9 Notepad's own autocorrect (12.1) | E2E inputs avoid Notepad's dictionary fixes; typed-buffer mismatch = break (D5) |

---

## 6. High-risk logic (pseudocode; owner L)

### 6.1 WordDiff (4.4.e) and EditRules (4.5)

```
Tokenise(text) -> Token[]  // kinds: Word (letters/digits/'/internal - and .), Punct, Space, Newline; keeps Start/End char offsets
  URL/email/number detection marks Word tokens with Flags {Number, Url, Email, CodeLike}

WordDiff.Compute(original, cleaned) -> TextEdit[]   // edits in ORIGINAL coordinates
  A = Tokenise(original) without Space tokens; B = Tokenise(cleaned) without Space tokens
  ops = MyersLcs(A, B, eq: exact text)                     // anchors
  for each hunk (Aseg, Bseg) between anchors:
     if Concat(Aseg) ignoring spacing == Concat(Bseg) ignoring spacing (case-sensitive):
        emit Spacing edit(s): span of Aseg incl. inner spaces -> Bseg joined with B's spaces
        continue
     align = NeedlemanWunsch(Aseg, Bseg,
               sub cost = NormDist(a,b) (case-insensitive DamerauLevenshtein / max len),
               gap cost = 0.6, merge/split candidates: a ~ b1+b2 (no space) or a1+a2 ~ b)
     for each aligned pair:
        (a, b)  -> Substitution or Capitalisation (equal ignoring case) or Apostrophe (equal ignoring ') or Punctuation (both Punct)
        (a, -)  -> Deletion(a);  (-, b) -> Insertion(b at position after previous aligned A token)
        merge/split -> Spacing
  Each TextEdit: { Start, End, Replacement, Kind, OriginalText }  // Start/End include adjacent Space where needed
  Whitespace-only differences between anchors -> Spacing edits (extra/missing spaces)

EditRules.Filter(original, cleaned, edits, ctx) -> AcceptedEdits | RejectAll(reason)
  // whole-correction gates (4.5.d)
  if multiset(Number|Url|Email tokens of original) != multiset(of cleaned): RejectAll(ProtectedTokenChanged)
  changedWords = count of original Word tokens touched by any edit
  if changedWords / max(1, wordCount(original)) > settings.MaxChangedWordRatio (0.5): RejectAll(TooManyChanges)
  // per-edit (4.5.a–c)
  for e in edits:
     if e touches a token flagged Number/Url/Email/CodeLike: reject e
     if e touches a word in PersonalDictionary (exact, case-insensitive): reject e
     switch e.Kind:
       Spacing, Punctuation, Apostrophe, Capitalisation: accept
       Substitution:
          if NormDist(lower(a), lower(b)) > settings.MaxNormalisedDistance (0.5): reject
          else if !Speller.IsValid(b) && !PersonalDict.Contains(b) && !(IsCapitalised(b) && IsCapitalised(a)): reject  // D16
          else accept
       Insertion/Deletion: accept iff word ∈ settings.FunctionWords (a an the is are was were be to of and or in on at for with it that this i)  // 4.5.b
       other: reject
  return accepted (each stays a separate small edit; never merge into a span replace)
```

### 6.2 Timing and caret rules (4.6) — `WriteGate` and `AiTrigger`

```
State: lastKeyTicks, keySeq (from session), desyncedTargets: set<TargetId>

AiTrigger.OnSentenceEnded / OnPaused(pauseMs >= settings.PauseMs):
   if target in desyncedTargets or paused or excluded: return
   snap = target.ReadWindow()   // TextWindow {Before, After, CaretKnown, TargetId, KeySeqAtRead}
   if !snap.CaretKnown: return   // 10.6.c
   para = current paragraph of snap.Before (text after last newline)            // D8
   ctx  = last 3 sentences of para (SentenceSplitter)                            // 4.4.b
   editable = intersection(ctx range, session.TypedRange in Before coordinates)  // D5
   if editable empty: return
   cleaned = await llm.Clean(ctx)  (runs while user types; 12.3)
   edits = EditRules.Filter(WordDiff.Compute(ctx, cleaned))
   protected = session.IsActivelyTyping(now) ? last two words of Before : none   // 4.6.a
       where IsActivelyTyping = now - lastKeyTicks < 1500ms and !SentenceEndedSinceLastKey   // 4.6.b
   edits = edits where edit ⊂ editable and edit ∩ protected = ∅
   for e in edits ordered right-to-left: WriteGate.Submit(Plan(e, snap))

WriteGate.Submit(plan):                  // waits only for the write (12.3), never blocks typing (4.6.e)
   loop until plan.Deadline (2 s):
      wait until now - lastKeyTicks >= 150ms                                    // 4.6.c
      if Keyboard modifiers physically down: continue
      cur = target.ReadWindow()
      if !cur.CaretKnown or cur.TargetId != plan.TargetId: drop
      appended = session.CharsTypedSince(plan.KeySeqAtRead)                     // D6 rebase
      if cur.Before != plan.Before + appended: drop                              // text changed by something else
      // re-check 4.6.a against current state
      if plan.Edit intersects protected words of cur: drop
      seq = session.KeySeq
      batch = BuildBatch(plan.Edit, caretDistance = len_elements(cur.Before) - plan.Edit.End)
      if session.KeySeq != seq: continue                                          // key slipped in
      target.ApplyEdit(batch)
      ok = target.Verify(expected = Apply(cur.Before, plan.Edit) + cur.After)    // 4.6.d
      if !ok: desyncedTargets.add(target); Counters.Desync++; log("desync", targetKind); return
      Underline(plan.Edit); Undo.Record(plan.Edit, target, cur); Counters.Correction++
      session.ApplyOwnEdit(plan.Edit)   // keep typed buffer aligned with the field
      return
```

Instant layer (D6/D7): the plan is built from the typed buffer, not a UIA read. `Verify` runs afterwards on the UIA worker. A failed verify desyncs the target exactly as above.

### 6.3 Undo (4.8)

```
UndoManager holds ONE CorrectionRecord? last        // D10
CorrectionRecord { TargetId, StartFromCaret, Original, Replacement, KeySeqAfterWrite, CaretTicks, ContextBefore(≤20 chars), ContextAfter(≤0) }

Hook (sync): on physical Ctrl+Z down, no other modifiers:
   if UndoArm.IsArmed (set when last written, cleared by any later key, mouse click or caret-move WinEvent):
       swallow (return 1); queue UndoRequested(Local)
OnUndoRequested(Local):
   rec = last; if rec null: return
   plan = inverse edit at same caret-relative position; WriteGate.SubmitImmediate(plan)    // no 150ms wait: user is waiting
   on success: PersonalDictionary.Add(rec.Original word) (4.8.c); Counters.Undo++; last = null
Global hotkey Ctrl+Alt+Z (4.8.b, D9):
   cur = target.ReadWindow(); if cur.TargetId != rec.TargetId: Notify("Switch back to that field to undo"); return
   locate rec.Replacement with rec.ContextBefore immediately before it, searching back from the caret within the paragraph
   if not found exactly once: return (text changed)
   build the edit relative to the current caret; write; verify; dictionary add; counters
```

### 6.4 Keyboard hook (10.3.a, 12.6)

```
static IntPtr HookProc(int nCode, IntPtr wParam, KBDLLHOOKSTRUCT* k):
   if nCode < 0: return CallNextHookEx
   if k->dwExtraInfo == InjectionTag.Value: return CallNextHookEx          // ours, ignore (10.7.b)
   injected = (k->flags & LLKHF_INJECTED) != 0
   isDown = wParam in (WM_KEYDOWN, WM_SYSKEYDOWN)
   ev = KeyEvent(k->vkCode, k->scanCode, k->flags, isDown, Stopwatch.GetTimestamp(), Interlocked.Increment(ref seq))
   if !injected and isDown:
       if vk == VK_TAB and no Ctrl/Alt/Win and SuggestionArm.TryConsume(seqBefore = ev.Seq-1): queue(AcceptSuggestion); swallowTabUp = true; return 1
       if vk == 'Z' and Ctrl down and no Alt/Shift/Win and UndoArm.TryConsume(ev.Seq-1): queue(UndoLocal); swallowZUp = true; return 1
   if !injected and !isDown and vk matches swallowed key: clear flag; return 1
   channel.Writer.TryWrite(injected ? ForeignInput(ev) : ev)   // unbounded, never blocks
   return CallNextHookEx
// Arm.TryConsume(expectedSeq): atomic compare: armedAtSeq == expectedSeq && !caretMovedFlag → clear and return true.
// Modifier state for the arms is read with GetAsyncKeyState (cheap), not tracked.
// Hook thread: dedicated thread, SetWindowsHookEx(WH_KEYBOARD_LL, ..., hMod = GetModuleHandle(null), 0), GetMessage loop.
// Heartbeat: if no event was seen for 60 s while GetLastInputInfo shows keyboard activity → re-install the hook (12.6).
```

### 6.5 UIA reading (10.6)

```
UiaWorker: one STA thread, CUIAutomation8 as IUIAutomation2 { ConnectionTimeout=250ms, TransactionTimeout=250ms }
  Invoke<T>(Func<IUIAutomation,T>, timeout 500ms): post to thread; if timeout → mark thread poisoned, start a new worker, skip that hwnd 30 s (D11)

ReadWindow():
  el = GetFocusedElement(); if null → Unknown
  if el.CurrentIsPassword → Skip(Password)                         // 8.3
  hwnd/pid via el.CurrentNativeWindowHandle or walk to ancestor with hwnd; WindowInfo.IsElevated(pid) → Skip  // 10.8
  AppExclusions.Matches(processName, className) → Skip              // 8.4
  tp2 = el.GetCurrentPattern(UIA_TextPattern2Id) as IUIAutomationTextPattern2
  if tp2 != null: caret = tp2.GetCaretRange(out active)
  else tp = TextPattern; sels = tp.GetSelection(); if sels.Length == 1 and sels[0] collapsed → caret = sels[0]   // 10.6.a
  if caret == null → Unknown                                        // 10.6.c, never ValuePattern
  b = caret.Clone(); b.MoveEndpointByUnit(Start, Character, -BeforeChars (400)); before = b.GetText(-1)  // 10.6.b
  a = caret.Clone(); a.MoveEndpointByUnit(End, Character, +AfterChars (80)); after = a.GetText(-1)
  rect = caret bounding rect (expand by one char left if empty)
  return TextWindow(before, after, rect, TargetId(hwnd, runtimeId), keySeqAtRead)
FocusTracker: SetWinEventHook(EVENT_OBJECT_FOCUS) → UiaWorker.Warm(hwnd): GetFocusedElement + GetCurrentPattern(TextPattern) once (10.6.d)
```

### 6.6 SendInput writing (10.7)

```
BuildBatch(edit, caretDistance):            // all in text elements (D8)
  inputs = []
  repeat caretDistance: key(VK_LEFT)        // to the edit's end
  repeat len(edit.Original): key(VK_BACK)
  for each UTF-16 unit u in edit.Replacement: KEYEVENTF_UNICODE down/up with wScan=u
  repeat caretDistance: key(VK_RIGHT)       // back to where the caret was
  every INPUT has dwExtraInfo = InjectionTag.Value                     // 10.7.b
  one SendInput call; check return == inputs.Length, else treat as desync
Never ValuePattern.SetValue (10.7.c).
```

---

## 7. Test strategy

- **Core (fast tier, 11.1.a):** xUnit v3. Most behaviour runs against `SimulatedEditor` (10.2), which models typing, caret moves, races (key arrives between read and write), host autocorrect and paste. Test names start with the requirement ID, e.g. `R4_5_d_RejectsWhenUrlChanges`. Target under 30 s.
- **Model tier (11.1.b):** `Category=Model`. Starts the real llama-server with the pinned model, checks CUDA offload in its log, cleans a test sentence and runs a 50-message latency benchmark.
- **Eval tier (11.1.c):** `tools/GlobalAutocorrect.Eval --set holdout` runs the full pipeline (instant + AI + rules + timing on a virtual clock) over `SimulatedEditor` and writes `artifacts/metrics.json` and `artifacts/eval-report.md`.
- **E2E tier (11.1.d):** FlaUI v5 for Notepad and a WPF TextBox test window; Playwright (.NET, headed Edge) for local pages. Typing is real `SendInput` from the test process **without** our tag, so the hook sees it as physical. (Physical-key detection treats untagged injected events from the test runner as physical only when env `GA_E2E=1`; production never sets it.) Desyncs are checked before text (11.3.d). Mock LLM server (`tests/GlobalAutocorrect.E2E/MockLlm`) returns scripted corrections. User-keystroke guard (2.8): if `GetLastInputInfo` advances without our test sending, abort and retry later.
- **Full tier (11.1.e):** fast + model + eval + e2e + package.
- **Traceability:** `scripts/trace-check.ps1` (part of `fast`) fails if any ID in `BRIEF.md` is missing from §9. README's coverage table is generated from §9 plus test results.

---

## 8. Tasks

Conventions for every task:
- Done-check is run from the repo root in `pwsh`. Default: `pwsh ./check.ps1 fast`.
- Tests are named `R<id with _>_<Behaviour>` (e.g. `R4_3_c_SkipsWordsWithDigits`).
- No typed text in logs (8.6). Use `Counters` and `ILogger`-free plain `AppLog.Event(name, kind)` with no text arguments.
- Public types are `sealed` unless designed for extension; records for data.

### M0 Scaffold

**T0.1 (I)** — Solution and projects. IDs 1.1, 1.8, 10.1, 10.3, 10.4, 10.5.
Files: `GlobalAutocorrect.slnx`, `Directory.Build.props`, `Directory.Packages.props` (versions exactly as §4), `.gitignore` (bin, obj, artifacts, node_modules, *.gguf), all `src/*`, `tools/*`, `tests/*` `.csproj` files with empty placeholder classes. Core: `<TargetFramework>net10.0</TargetFramework>`, no package refs to Windows. Windows/App: `net10.0-windows`, `<UseWPF>true</UseWPF>`. App references WPF-UI, WPF-UI.Tray, CommunityToolkit.Mvvm. Core.Tests references xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk.
Done-check: `dotnet build GlobalAutocorrect.slnx -c Debug` exits 0.

**T0.2 (I)** — `check.ps1` and `scripts/trace-check.ps1`. IDs 11.1.a–e, 2.3.c, 11.2.
`check.ps1 [fast|model|eval|e2e|full]`. `fast` = `scripts/trace-check.ps1` + `dotnet build GlobalAutocorrect.slnx --no-restore` (restore once if obj missing) + `dotnet test --project tests/GlobalAutocorrect.Core.Tests --no-build -- --filter-not-trait "Category=Model" --filter-not-trait "Category=E2E"` (the repo uses Microsoft.Testing.Platform via `global.json`; VSTest `--filter` syntax does not apply); print elapsed time; exit non-zero on any failure. `model` = `dotnet test tests/GlobalAutocorrect.Llm.Tests`. `eval` = `dotnet run --project tools/GlobalAutocorrect.Eval -c Release -- --set holdout --out artifacts`. `e2e` = `dotnet test tests/GlobalAutocorrect.E2E` with `GA_E2E=1`. `full` = all, then `scripts/package.ps1`. `trace-check.ps1`: regex all IDs `^\s*(\d+\.\d+(\.[a-z])?)\s` and `^- (\d+\.\d+\.[a-z])` from BRIEF.md (numbered lines and bullet sub-items), plus parent IDs that have sub-items; each must appear as `| <id> |` in PLAN.md §9; print missing IDs; exit 1 if any.
Done-check: `pwsh ./check.ps1 fast` exits 0 and prints elapsed < 30 s.

### M1 Evaluation data (6.1)

**T1.1 (I)** — Clean messages. IDs 6.1.a, 6.1.d. Write `data/eval/messages.jsonl`: ≥ 400 lines `{"id":"m0001","clean":"...","style":"chat|email|notes"}`. Natural everyday UK English, British -ise spelling, 1–3 sentences each, 6–45 words, varied (work notes, family chat, booking emails, numbers, times, names, a few URLs/emails). No two messages share a first five words. Write in batches of 50.
Done-check: `dotnet test tests/GlobalAutocorrect.Core.Tests --filter "FullyQualifiedName~EvalData"` (test `R6_1_a_MessagesValid`: ≥400 lines, valid JSON, unique ids, every word valid in en_GB-ise Hunspell **or** capitalised **or** number/URL/email; written in T1.3 once Hunspell assets exist; before then the test only checks count/JSON/uniqueness).

**T1.2 (L)** — 50 hand-garbled messages. IDs 6.1.c. `data/eval/garbled.jsonl` `{"id":"g001","clean":"...","noisy":"..."}`, heavy realistic mangling where context is needed.

**T1.3 (I)** — Noise generator and split. IDs 6.1.b, 6.1.d, 6.1.e, 3.4.
`tools/GlobalAutocorrect.DataGen` command `noise --in data/eval/messages.jsonl --seed 20261029`. Class `UkKeyboardNoise(Random rng)` with `string Apply(string clean, NoiseLevel level)`; ops: drop letter, double letter, swap adjacent, neighbour key (UK QWERTY adjacency map incl. `#`, `'`, `\`), missing space (merge), extra space (split), misplaced space, drop apostrophe, lowercase sentence start / `i`, light grammar (its/it's, their/there, a/an, missing the). Levels: None 22%, Light 30%, Medium 30%, Heavy 18% (per-message). Output `data/eval/noisy.jsonl` `{id, clean, noisy, level, ops:[...]}`; then `split` merges noisy + garbled (450), seeded shuffle, holdout = first 200 → `holdout.jsonl`, rest → `tuning.jsonl`. Tests in Core.Tests `EvalData/` (`R6_1_b_*`, `R6_1_d_AtLeast20PercentClean` per split, `R6_1_e_HoldoutAtLeast200AndDisjoint`).
Done-check: `pwsh ./check.ps1 fast`.

**T1.4 (I)** — Scorer. IDs 6.3.a–c, 6.3.e. `src/GlobalAutocorrect.Core/Eval/Scorer.cs` (Core, so eval and tests share it):
`public static MessageScore Score(string clean, string noisy, string output)`; `public sealed record MessageScore(int NeedFix, int Fixed, int CorrectWords, int FalseEdits, bool Exact, IReadOnlyList<string> Categories)`; `public static Aggregate Combine(IEnumerable<MessageScore>)` → ErrorsFixedRate, FalseEditRate, ExactMatchRate. Method: align clean↔noisy and clean↔output with `WordDiff` token alignment (word tokens incl. attached punctuation, case-sensitive); a clean word "needs fixing" if its aligned noisy token differs; "fixed" if the aligned output token equals it; a clean word that equals its noisy token is "correct"; it is a false edit if the output token differs. Categories from noise ops (or `garbled`).
Depends on T2.1. Done-check: `pwsh ./check.ps1 fast` with tests `R6_3_a_*`, `R6_3_b_*`, `R6_3_c_*`.

### Spike A (2.4.a) — L

**SA.1** `scripts/setup.ps1` download part (5.7): `-Model <key>`, `-Llama`, `-Dictionary`, `-Corpus`; downloads into cache, verifies SHA-256 from a manifest `scripts/manifest.json`, extracts llama + cudart into one folder (5.1.a); writes `GpuPreference=2;` for `llama-server.exe` (5.8.d).
**SA.2** `tools/GlobalAutocorrect.Eval bench --model <key> --n 50 --set tuning`: start server (`-ngl 99 -c 4096 -np 1 --reasoning-budget 0 --host 127.0.0.1`), warm-up, send each message, record latency, `nvidia-smi` VRAM, parse log for `CUDA0` and `offloaded N/N layers`, score raw output with Scorer. Writes `artifacts/spikeA-<model>.json`.
**SA.3** Run E4B, Qwen3.5-4B, E2B (Q5_K_M; Q4_K_M only if latency fails). Record table in §4.1.

### Spike B (2.4.b) — L

**SB.1** `tests/pages`: `textarea.html`, `contenteditable.html`, `password.html`, `prosemirror.html` (bundled with esbuild), `lexical.html` (production build, `registerRichText`, 12.5). No `process.env`.
**SB.2** `tools/SpikeB`: for each target: launch, focus, type `the quick brown fox jumsp over` with untagged SendInput, ReadWindow, apply one edit (`jumsp`→`jumps`) with a tagged batch, re-read, compare. Report per target: caret known, read ok, write ok, desync. Pass: ≥ 6/7 without desync.

### M2 Diff and edit rules — L

**T2.1** `Tokeniser`, `Token`, `TokenClassifier` (Number/Url/Email/CodeLike: contains `_`, `::`, `()`, camelCase inner capital, or `/` path), `Similarity.DamerauLevenshtein`, `Similarity.NormDist`, `SentenceSplitter`. Tests `R4_3_c_*` (classifier), `R4_5_a_NormDist*`.
**T2.2** `WordDiff.Compute` per §6.1. Tests `R4_4_e_*`: substitution, run-together split, split merged, punctuation, capitalisation, apostrophe, insert/delete function word, multiple separate edits, unchanged text → no edits.
**T2.3** `EditRules.Filter` + `EditRuleSettings { MaxNormalisedDistance=0.5, MaxChangedWordRatio=0.5, FunctionWords }`. Tests `R4_5_a_*` … `R4_5_e_SettingsAreConfigurable`, `R3_4_NeverAmericanises`, `R4_4_d_RejectsSynonym`.

### M3 Instant layer

**T3.1 (I)** — Dictionary assets. IDs 1.5, 3.4. `scripts/setup.ps1 -Dictionary` copies `en_GB-ise.dic/.aff` into `assets/`. `tools/GlobalAutocorrect.DataGen freq --corpus <leipzig words file> --dic assets/en_GB-ise.dic --out assets/freq_en_GB.txt`: keep words valid in Hunspell (case-insensitive), sum counts by lowercase form, top 80,000, format `word count` per line. Commit both (small). Add attribution lines to `assets/NOTICE.md` (SCOWL licence, Leipzig CC BY).
Done-check: `pwsh ./check.ps1 fast` + `R1_5_FreqDictLoads`.

**T3.2 (I)** — `HunspellSpellChecker : ISpellChecker` (`bool IsValid(string word)`; loads `assets/en_GB-ise.*` once; thread-safe) and `SymSpellSuggester` (`IReadOnlyList<(string Word, int Distance, long Freq)> Lookup(string word, int max = 3)`; maxEditDistance 2, prefix length 7, `Verbosity.All`, top 3 by (distance, -freq)). Tests `R1_5_*`, `R3_4_ColourIsValid`, `R3_4_ColorIsInvalid`.

**T3.3 (L)** — `InstantCorrector.TryCorrect(string word, WordContext ctx) -> TextEdit?` implementing 4.3.c exactly: invalid in Hunspell, not in personal dictionary, not capitalised mid-sentence (`ctx.IsSentenceStart` false and char 0 upper), no digits, not URL/email/CodeLike, top candidate distance 1, or distance 2 with freq ≥ 10× runner-up. Preserve the original's capitalisation pattern (first letter upper, all upper). Tests `R4_3_a_*` (timing, < 100 ms incl. load-once), `R4_3_b_NeverCallsLlm`, `R4_3_c_*` one per condition, `R4_3_d_UncertainLeftForAi`, `R0_2_a_TehAndBookk`.

**T3.4 (I)** — `PersonalDictionary` (load/save `%APPDATA%\GlobalAutocorrect\dictionary.txt`, one word per line, UTF-8; `Add`, `Remove`, `Contains` (case-insensitive), `Words`; `AutoLearn(word)`: counts unrecognised words in memory, adds at 3 when setting on). Tests `R8_5_*`, `R4_8_c_*` (via UndoManager in M4).

### M4 Session, timing, undo, simulated editor — L

**T4.1** `ITextTarget` (`TextWindow ReadWindow()`, `ApplyResult ApplyEdit(WritePlan plan)`, `bool Verify(string expectedBefore, string expectedAfter)`, `string TargetId`), `TextWindow`, `SimulatedEditor` (10.2): typing API, caret moves, host-autocorrect hook, paste, scripted race injection.
**T4.2** `TypingSession`: typed buffer, key seq, breaks (4.2), `WordFinished`, `SentenceEnded`, `IsActivelyTyping` (4.6.a/b).
**T4.3** `AiTrigger` + `AiCorrector` + `PromptBuilder` (4.4.a–d, 5.4), `WriteGate` (§6.2), `UndoManager` (§6.3), `Counters` (6.5), `CorrectionEngine` wiring with `IClock`.
Tests: `R4_2_*`, `R4_4_a_*`, `R4_4_b_*`, `R4_6_a_*` … `R4_6_e_*`, `R4_8_a_*` … `R4_8_c_*`, `R12_2_*`, `R12_3_*`, `R6_5_*`, `R10_2_*`.

### M5 Model runtime

**T5.1 (I)** — `LlamaServerClient : ILlmClient`: `Task<LlmResult> CleanAsync(string text, CancellationToken)`, POST `http://127.0.0.1:{port}/v1/chat/completions` with fixed system prompt + few-shot (from `AppSettings.Prompt`), `temperature 0`, `max_tokens = min(512, 2 × tokensEstimate + 16)`, `cache_prompt: true`, `chat_template_kwargs: {enable_thinking:false}`. Rejects any non-loopback base address (`ArgumentException`). Tests with an in-process fake server: `R10_5_*`, `R5_4_*`, `R3_3_LoopbackOnly`.
**T5.2 (L)** — `LlamaServerHost`: start/stop child process (`--host 127.0.0.1`, free port, `-ngl 99`, `--reasoning-budget 0`, `-c 4096`, `-np 1`), parse log for CUDA device/offload, `Watchdog` (health every 5 s, 3 failures → restart, 5.6.a), idle unload after `IdleUnloadMinutes` (15) and lazy reload on next key without blocking (5.6.b), CPU fallback when CUDA is missing (5.1.c). Tests `R5_1_b_*`, `R5_6_a_*`, `R5_6_b_*` with a fake executable.
**T5.3 (L)** — `scripts/verify.ps1` 5.8.a–g, each failure prints a plain-language fix. Model tier tests in `tests/GlobalAutocorrect.Llm.Tests`: `R5_5_MedianUnder2s`, `R5_8_e_CleansTestSentence`.

### M6 Pipeline eval — L

**T6.1** `tools/GlobalAutocorrect.Eval run --set tuning|holdout`: types each noisy message into `SimulatedEditor` through `CorrectionEngine` on a virtual clock (realistic 60–120 ms inter-key, sentence ends, final pause), real LLM, scores with Scorer, latency p50/p95 (6.3.d), worst 20 by category (6.3.e), `metrics.json` + `eval-report.md` (6.6).
**T6.2** Tune prompt and thresholds on **tuning** only (4.5.e, 6.1.e). Record each tuning run in `artifacts/tuning-log.md`.

### M7 Windows layer — Feature A end to end

**T7.1 (L)** `NativeMethods`, `InjectionTag`, `KeyboardHook` (§6.4), `InputPump`, `KeyTranslator`.
**T7.2 (L)** `UiaWorker`, `UiaReader` (§6.5), `FocusTracker`.
**T7.3 (L)** `SendInputWriter` (§6.6), `UiaTextTarget`.
**T7.4 (I)** `WindowInfo` + `IntegrityLevel.IsElevated(int pid)` (OpenProcess + TokenIntegrityLevel; access denied ⇒ true) (8.3, 10.8); `AppExclusions` defaults (8.4): processes `WindowsTerminal, cmd, powershell, pwsh, conhost, wt, Code, devenv, rider64, idea64, KeePass, KeePassXC, 1Password, Bitwarden, mstsc, msrdc, vmconnect`, Excel when focused element's ControlType is DataItem/Table cell, any full-screen exclusive window (games). Editable list in settings. Tests `R8_3_*`, `R8_4_*`, `R10_8_*` (logic parts with fakes).
**T7.5 (I)** `HotkeyManager` (RegisterHotKey on a message-only window; parse "Ctrl+Alt+P"; `event Action<string> Pressed`) (8.2, 4.8.b, 10.3.e).
**T7.6 (L)** Headless host wiring in App (no UI yet), single instance mutex (5.6.c), pause (8.2), E2E: Notepad, WPF TextBox, Edge pages (11.3).

### M8 Feature B — prediction

**T8.1 (I)** `DataGen ngram --corpus <sentences> --dic ... --out assets/ngram.bin`: lowercased tokens valid in Hunspell, vocab top 50k, bigram + trigram counts ≥ 3, store top 5 continuations per context; prefix table for completion. Binary format documented in the file header.
**T8.2 (I)** `NgramModel.Load`, `Predictor.Suggest(string before) -> Suggestion?` (complete partial word or next word after a space; user unigram boost; ≤ 10 ms; min length / confidence threshold), `UserHistory` (unigram counts only, 7.3). Tests `R7_1_*`, `R7_2_Under10ms`, `R7_3_*`, `R7_5_*`, `R7_7_*`.
**T8.3 (L)** Overlay windows (`WS_EX_TRANSPARENT|LAYERED|NOACTIVATE|TOOLWINDOW|TOPMOST`, 7.6), suggestion arm + Tab path (7.4), underline (4.7). E2E `R7_4_*`.

### M9 Interface — I views, L review

**T9.1 (I)** `Theme/Tokens.xaml` (9.2.a): `AccentPrimary #1F6FEB`, `AccentLight #5B9BFF`, `AccentDark #134FB0`, light surfaces `#F7F9FC/#FFFFFF`, dark `#0F1622/#172131`, text light `#0B1220`, dark `#E8EEF8`; applied via `ApplicationAccentColorManager.Apply(accent)`; `SystemThemeWatcher` follows Windows (9.2.d).
**T9.2 (I)** Icons (9.2.e): `assets/icons/app.ico`, tray active/paused × light/dark taskbar, drawn from vector XAML → exported by a small tool.
**T9.3 (I)** Settings window: `FluentWindow` Mica, `NavigationView` pages 9.3.a–g, cards, Fluent System Icons (`SymbolIcon`).
**T9.4 (I)** First-run window (9.4): three steps, model download with progress (calls setup logic in-process), shortcuts screen.
**T9.5 (I)** Tray (8.1, 8.2): menu (Pause/Resume, Settings, Quit), paused icon, start-with-Windows (HKCU Run key) setting.
**T9.6 (L)** Screenshot self-review (9.5): `tools/Screenshots` renders every page light/dark × 100/150 % to `artifacts/screens/`, contrast check ≥ 4.5:1 on token pairs (9.2.d, 12.4).

### M10 Release candidate — L

README (13.2), `scripts/package.ps1` zip (13.1), `check.ps1 full`, Defender guidance (5.8.g, 12.10), PROGRESS + stop 2.6.e.

---

## 9. Traceability (2.3.c)

Proof column: test name prefix (all tests whose name starts with it), or **Check:** a recorded manual/scripted check with where it is recorded.

| ID | Milestone | Proof |
|---|---|---|
| 0.1 | all | Check: eval report shows errors fixed / false edits (M6) |
| 0.2 | M4–M7 | `R0_2_*`; E2E `R0_2_EndToEnd_*` |
| 0.2.a | M3 | `R0_2_a_TehAndBookk` |
| 0.2.b | M4–M6 | `R4_4_c_*`; eval report |
| 0.2.c | M4 | `R0_2_c_AppliesAllAcceptedEdits` |
| 0.2.d | M2 | `R4_4_d_*`, `R4_5_*` |
| 0.3 | M8 | `R7_*` |
| 0.4 | M9 | Check: screenshot review in PROGRESS M9 |
| 0.5 | all | Check: milestone order in PROGRESS |
| 0.6 | all | Check: DECISIONS.md raised items |
| 1.1 | M0 | Check: `Directory.Build.props` net10.0 |
| 1.2 | M0, M9 | Check: package refs WPF-UI 4.3.0 / Tray |
| 1.3 | SA, M5 | `R5_1_*` |
| 1.4 | SA | Check: §4.1 spike table |
| 1.5 | M3 | `R1_5_*` |
| 1.6 | M8 | `R7_2_*` |
| 1.7 | M7 | `R10_7_*`, E2E `R11_3_*`; Check: Spike B (§4.1) |
| 1.8 | M0 | Check: test project refs |
| 1.9 | all | `R3_3_*`, `R3_4_*` |
| 1.10 | all | Check: no CI files in repo |
| 2.1 | all | Check: process |
| 2.2 | M0 | Check: BRIEF.md first commit 0850a00 |
| 2.2.a | M0 | Check: commit 0850a00, pushed |
| 2.2.b | all | Check: `git log -- BRIEF.md` shows one commit |
| 2.2.c | all | Check: PROGRESS notes "BRIEF re-read" per milestone |
| 2.3 | plan | Check: this file |
| 2.3.a | plan | Check: PROGRESS prerequisites entry |
| 2.3.b | plan | Check: this file |
| 2.3.c | plan | Check: `scripts/trace-check.ps1` runs in the fast tier |
| 2.3.d | plan | Check: §4 |
| 2.3.e | plan | Check: DECISIONS.md |
| 2.3.f | plan | Check: commit of PLAN.md |
| 2.4 | SA, SB | Check: §4.1 |
| 2.4.a | SA | Check: §4.1 Spike A table + `artifacts/spikeA-*.json` |
| 2.4.b | SB | Check: §4.1 Spike B table |
| 2.4.c | SA, SB | Check: §4 pinned + §4.1 |
| 2.5 | all | Check: PROGRESS |
| 2.5.a | all | Check: PROGRESS records fast green per milestone |
| 2.5.b | all | Check: PROGRESS |
| 2.5.c | all | Check: git log messages carry IDs |
| 2.6 | all | Check: process |
| 2.6.a | prereq | Check: stop on 2026-10-29 for .NET 10 |
| 2.6.b | SA, SB | Check: process |
| 2.6.c | all | Check: process |
| 2.6.d | all | Check: D1 (tag conflict raised) |
| 2.6.e | M10 | Check: RC stop message |
| 2.7 | all | Check: PROGRESS next-task field |
| 2.8 | M7 | Check: E2E `Desk.RequireIdle` and Spike B `InputGuard` (D20); runs recorded in PROGRESS |
| 2.9 | all | Check: process |
| 2.10 | M2, M3 | Check: no word lists in correction code; `R2_10_NoWordSpecificRules` (scans Core sources for hard-coded typo pairs) |
| 2.11 | M10 | Check: README coverage table |
| 2.12 | all | Check: process |
| 2.12.a | M0 | Check: `.claude/agents/implementer.md` |
| 2.12.b | all | Check: owner column in §8 |
| 2.12.c | all | Check: PROGRESS delegation log |
| 2.12.d | plan | Check: this file §6, §8 |
| 2.12.e | all | Check: PROGRESS review notes |
| 3.1 | SA | Check: spike hardware notes |
| 3.2 | all | Check: no installer/signing |
| 3.3 | M5 | `R3_3_*` |
| 3.4 | M2, M3 | `R3_4_*` |
| 3.5 | prereq | Check: PROGRESS prerequisites entry |
| 3.6 | M0 | Check: CLAUDE.md, .claude/settings.json |
| 4.1 | M7 | E2E `R4_1_*` per app |
| 4.1.a | M7 | `R4_1_a_NoAppSpecialCases` (scans Windows layer for process-name branches outside AppExclusions) |
| 4.1.b | M4, M7 | `R4_1_b_*` |
| 4.2 | M4, M7 | `R4_2_*` |
| 4.3 | M3 | `R4_3_*` |
| 4.3.a | M3, M4 | `R4_3_a_*` |
| 4.3.b | M3 | `R4_3_b_*` |
| 4.3.c | M3 | `R4_3_c_*` |
| 4.3.d | M3 | `R4_3_d_*` |
| 4.4 | M4 | `R4_4_*` |
| 4.4.a | M4 | `R4_4_a_*` |
| 4.4.b | M4 | `R4_4_b_*` |
| 4.4.c | M4, M6 | `R4_4_c_*`; eval categories |
| 4.4.d | M2 | `R4_4_d_*` |
| 4.4.e | M2 | `R4_4_e_*` |
| 4.5 | M2 | `R4_5_*` |
| 4.5.a | M2 | `R4_5_a_*` |
| 4.5.b | M2 | `R4_5_b_*` |
| 4.5.c | M2 | `R4_5_c_*` |
| 4.5.d | M2 | `R4_5_d_*` |
| 4.5.e | M2, M6 | `R4_5_e_*`; tuning log uses tuning set only |
| 4.6 | M4 | `R4_6_*` |
| 4.6.a | M4 | `R4_6_a_*` |
| 4.6.b | M4 | `R4_6_b_*` |
| 4.6.c | M4 | `R4_6_c_*` |
| 4.6.d | M4, M7 | `R4_6_d_*` |
| 4.6.e | M4, M7 | `R4_6_e_*` |
| 4.7 | M8 | Check: underline screenshot taken during E2E (`artifacts/screens/e2e-underline.png`); setting in `R9_3_*` |
| 4.8 | M4 | `R4_8_*` |
| 4.8.a | M4, M7 | `R4_8_a_*` |
| 4.8.b | M4, M7 | `R4_8_b_*` |
| 4.8.c | M4 | `R4_8_c_*` |
| 5.1 | M5 | `R5_1_*` |
| 5.1.a | SA | Check: setup extracts cudart beside llama-server; `R5_1_a_*` |
| 5.1.b | M5 | `R5_1_b_*` |
| 5.1.c | M5 | `R5_1_c_*` |
| 5.1.d | M5 | Check: no LLamaSharp reference (`R5_1_d_NoLlamaSharp`) |
| 5.2 | M5 | `R5_2_*` |
| 5.3 | SA | Check: §4.1 |
| 5.3.a | SA | Check: §4.1 |
| 5.3.b | SA | Check: §4.1 |
| 5.3.c | SA | Check: §4.1 |
| 5.3.d | SA | Check: §4.1 default chosen |
| 5.4 | M5 | `R5_4_*` |
| 5.5 | M5 | `R5_5_*` (model tier) |
| 5.6 | M5, M7 | `R5_6_*` |
| 5.6.a | M5 | `R5_6_a_*` |
| 5.6.b | M5 | `R5_6_b_*` |
| 5.6.c | M7 | `R5_6_c_*` |
| 5.7 | SA | Check: setup.ps1 run log; `R5_7_ManifestHasChecksums` |
| 5.8 | M5 | Check: verify.ps1 run |
| 5.8.a | M5 | Check: verify.ps1 |
| 5.8.b | M5 | Check: verify.ps1 |
| 5.8.c | M5 | Check: verify.ps1 |
| 5.8.d | M5 | Check: verify.ps1 |
| 5.8.e | M5 | Check: verify.ps1 |
| 5.8.f | M5 | Check: verify.ps1 |
| 5.8.g | M5 | Check: verify.ps1 |
| 6.1 | M1 | `R6_1_*` |
| 6.1.a | M1 | `R6_1_a_*` |
| 6.1.b | M1 | `R6_1_b_*` |
| 6.1.c | M1 | `R6_1_c_*` |
| 6.1.d | M1 | `R6_1_d_*` |
| 6.1.e | M1 | `R6_1_e_*` |
| 6.2 | M6 | Check: eval runs `CorrectionEngine` |
| 6.3 | M1, M6 | `R6_3_*` |
| 6.3.a | M1 | `R6_3_a_*` |
| 6.3.b | M1 | `R6_3_b_*` |
| 6.3.c | M1 | `R6_3_c_*` |
| 6.3.d | M6 | Check: metrics.json |
| 6.3.e | M6 | Check: eval-report.md |
| 6.4 | M6 | Check: eval-report.md targets |
| 6.5 | M4 | `R6_5_*` |
| 6.6 | M6, M10 | Check: About page + README numbers |
| 7.1 | M8 | `R7_1_*` |
| 7.2 | M8 | `R7_2_*`; Check: §4 corpus licence |
| 7.3 | M8 | `R7_3_*` |
| 7.4 | M8 | `R7_4_*` |
| 7.5 | M8 | `R7_5_*` |
| 7.6 | M8 | `R7_6_*` |
| 7.7 | M8 | `R7_7_*` |
| 8.1 | M9 | `R8_1_*` |
| 8.2 | M7, M9 | `R8_2_*` |
| 8.3 | M7 | `R8_3_*` |
| 8.4 | M7 | `R8_4_*` |
| 8.5 | M3 | `R8_5_*` |
| 8.6 | M4 | `R8_6_*` (log/settings scans for typed text) |
| 9.1 | M9 | Check: screenshots |
| 9.2 | M9 | Check: screenshots |
| 9.2.a | M9 | Check: Tokens.xaml |
| 9.2.b | M9 | Check: screenshots |
| 9.2.c | M9 | Check: screenshots |
| 9.2.d | M9 | `R9_2_d_ContrastAtLeast4_5` |
| 9.2.e | M9 | Check: icon screenshots on light/dark taskbar |
| 9.3 | M9 | Check: screenshots |
| 9.3.a | M9 | Check: screenshots |
| 9.3.b | M9 | Check: screenshots |
| 9.3.c | M9 | Check: screenshots |
| 9.3.d | M9 | Check: screenshots |
| 9.3.e | M9 | Check: screenshots |
| 9.3.f | M9 | Check: screenshots |
| 9.3.g | M9 | Check: screenshots |
| 9.4 | M9 | Check: screenshots |
| 9.5 | M9 | Check: `artifacts/screens` + PROGRESS review notes |
| 10.1 | M0 | `R10_1_CoreHasNoWindowsReferences` |
| 10.2 | M4 | `R10_2_*` |
| 10.3 | M7 | Check: project layout |
| 10.3.a | M7 | `R10_3_a_*`; E2E `R11_3_*` (every E2E key goes through the hook) |
| 10.3.b | M7 | `R10_3_b_*` |
| 10.3.c | M7 | E2E `R10_3_c_*` |
| 10.3.d | M8 | E2E `R10_3_d_*` |
| 10.3.e | M7 | `R10_3_e_*` |
| 10.4 | M9 | Check: App project |
| 10.5 | M5 | `R10_5_*` |
| 10.6 | M7 | `R10_6_*`; Check: Spike B (§4.1) |
| 10.6.a | M7 | Check: Spike B (§4.1): TextPattern2 in Notepad/Word, selection fallback in Chromium |
| 10.6.b | M7 | `R10_6_b_*` |
| 10.6.c | M7 | `R10_6_c_*` |
| 10.6.d | M7 | Check: Spike B (§4.1): Chromium editors read after warm-up; E2E `R11_3_b_*` |
| 10.7 | M7 | `R10_7_*` |
| 10.7.a | M7 | `R10_7_a_*` |
| 10.7.b | M7 | `R10_7_b_*` |
| 10.7.c | M7 | `R10_7_c_NoSetValue` (source scan) |
| 10.8 | M7 | `R10_8_*` |
| 11.1 | M0 | Check: check.ps1 |
| 11.1.a | M0 | Check: fast timing in PROGRESS |
| 11.1.b | M5 | Check: check.ps1 model run |
| 11.1.c | M6 | Check: check.ps1 eval run |
| 11.1.d | M7 | Check: check.ps1 e2e run |
| 11.1.e | M10 | Check: check.ps1 full run |
| 11.2 | all | Check: `scripts/coverage.ps1` generates the README table |
| 11.3 | M7 | E2E suite |
| 11.3.a | M7 | E2E `R11_3_a_*` |
| 11.3.b | M7 | E2E `R11_3_b_*` |
| 11.3.c | M7 | Check: E2E typer uses SendInput |
| 11.3.d | M7 | Check: E2E assertion order |
| 11.4 | SA | Check: cache path; `.gitignore` |
| 12.1 | M7 | Check: E2E Notepad inputs (`teh`, `bookk`, `their`) are words Notepad leaves alone; Spike B Notepad run |
| 12.2 | M4 | `R12_2_*` |
| 12.3 | M4 | `R12_3_*` |
| 12.4 | M9 | `R9_2_d_*`; screenshots |
| 12.5 | SB | Check: lexical.html |
| 12.6 | M7 | Check: hook heartbeat `KeyboardHook.CheckAlive` every 5 s re-installs a dropped hook; callback does no I/O (`R10_3_a_*`) |
| 12.7 | M5 | Check: verify.ps1 |
| 12.8 | SA, M5 | `R5_4_ThinkingDisabled` |
| 12.9 | M7 | Check: README known limits |
| 12.10 | M5, M10 | Check: verify.ps1 5.8.g; README |
| 13.1 | M10 | Check: package zip contents |
| 13.2 | M10 | Check: README |
| 13.3 | M10 | Check: no v1.0.0 tag (D1) |
| 13.4 | M10 | Check: timed setup + verify run |
| 14.1 | — | Out of scope |
| 14.2 | — | Out of scope |
| 14.3 | — | Out of scope |
| 14.4 | M4 | `R4_2_*` (pasted/dictated text ignored) |
| 14.5 | M4 | `R4_8_*` (single record, D10) |
| 14.6 | — | Out of scope |
| 14.7 | — | Out of scope |
| 14.8 | — | Out of scope |
| 14.9 | M2 | `R4_4_d_*` |
