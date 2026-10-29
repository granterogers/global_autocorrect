# BRIEF: Global Autocorrect

A Windows app that cleans up what the user types, in real time, in any app, using a local AI model, and offers word predictions accepted with Tab.

Every requirement has a unique ID (for example `3.4.b`). Refer to requirements by ID in `PLAN.md`, `PROGRESS.md`, commit messages, test names and the README so nothing is lost.

---

## 0. The goal (everything else serves this)

0.1 **The problem.** The user types fast in any Windows app and makes lots of mistakes: wrong, doubled, missing or swapped keys; spaces missing or in the wrong place; bad grammar and punctuation. Today they paste the text into an AI chat, ask it to "clean it up", and paste back a version where every mistake is fixed and the intended words are inferred from context.

0.2 **Feature A, real-time clean-up (primary).** Do that clean-up automatically while the user types, in any app, with a local model. The user presses nothing.
- 0.2.a Obvious mistakes (for example "teh", "bookk") are fixed instantly as each word is finished.
- 0.2.b Everything else is fixed by the local AI, which uses the surrounding sentences to infer what the user meant.
- 0.2.c Default behaviour: fix everything it is confident about.
- 0.2.d It fixes mistakes. It never rewrites style, tone or word choice.

0.3 **Feature B, word prediction (secondary).** A faint suggestion after the caret completes the current word or predicts the next one. Tab accepts it. Ignoring it costs nothing.

0.4 **Quality bar for the interface:** beautiful, modern and polished, in a blue colour scheme (section 9). Not a developer's default settings window.

0.5 **Build order:** Feature A end to end, then Feature B, then interface polish.

0.6 If a requirement doesn't help A, B or the interface work reliably, fast and privately, raise it rather than spend time on it.

---

## 1. Decisions already made (do not reopen without the user)

1.1 Language: C# on .NET 10.
1.2 Interface: WPF with WPF-UI 4.x (Fluent design), plus WPF-UI.Tray.
1.3 AI runtime: llama-server from the official llama.cpp Windows CUDA 13.x release, run as a child process on 127.0.0.1.
1.4 Model: chosen by Spike A from the candidates in 5.3. No other models unless all candidates fail.
1.5 Spelling: WeCantSpell.Hunspell with an en_GB-ise dictionary, plus SymSpell.
1.6 Prediction: n-gram model plus the user's own typing history. No LLM.
1.7 Reading text: UI Automation. Writing text: tagged SendInput batches.
1.8 Tests: xUnit for Core, FlaUI v5 for desktop, Playwright only to host browser test pages.
1.9 Everything local and free. UK English only. UK keyboard layout.
1.10 Built and tested on the user's own laptop. No CI service.

---

## 2. How to work (unattended)

2.1 You are running in Claude Code natively on the user's Windows 11 laptop, in the local clone of the project's GitHub repository. The user pastes this brief and then leaves you to work. Work through the whole project without waiting for the user, except at the stop points in 2.6.

2.2 **Save this brief.**
- 2.2.a Save this brief unchanged as `BRIEF.md` in the repository root and commit and push it as the first commit.
- 2.2.b Never edit, shorten or reword `BRIEF.md`. Record your own decisions and assumptions in `DECISIONS.md` with the requirement ID.
- 2.2.c Re-read `BRIEF.md` at the start of every session and before closing every milestone.

2.3 **Planning (no production code yet).**
- 2.3.a Check the prerequisites in 3.5 first.
- 2.3.b Produce `PLAN.md`: architecture, project layout, milestones, risks, how each spike retires them, and test strategy.
- 2.3.c `PLAN.md` includes a traceability table listing every requirement ID, the milestone that delivers it, and the test or check that proves it. No ID may be missing.
- 2.3.d Look up the current stable version of every dependency rather than trusting versions named here. Record them in `PLAN.md`.
- 2.3.e Don't ask the user questions. Where something is unclear, choose the option that best serves section 0, record it in `DECISIONS.md`, and carry on.
- 2.3.f Commit and push `PLAN.md`, then go straight on to the spikes.

2.4 **Two spikes before features.** Each is time-boxed to half a day.
- 2.4.a **Spike A, model quality and speed.** Run llama-server on the GPU with each candidate model (5.3) on 50 messages from the tuning set (6.1). Measure p50/p95 latency, VRAM use, and the metrics in 6.3. **Pass:** at least one model has a median under 1.5 seconds and fixes at least 90% of errors with under 2% false edits.
- 2.4.b **Spike B, text access.** Read the caret context with UI Automation, write one correction with a tagged SendInput batch, then re-read to verify. Test in Notepad, Word, Outlook, an Edge textarea, a contenteditable, ProseMirror and Lexical. **Pass:** no desyncs in at least 6 of the 7.
- 2.4.c Record the measured results in `PLAN.md`. If both spikes pass, pin exact versions of llama.cpp, the model file and every library, and continue to the milestones without waiting. Don't upgrade pinned versions during the build.

2.5 **Milestones.**
- 2.5.a Each milestone ends green on `check.ps1 fast` (11.1.a).
- 2.5.b Each milestone adds a status note to `PROGRESS.md` listing the requirement IDs completed and the next task.
- 2.5.c Commit and push at the end of each milestone, with the requirement IDs in the message.

2.6 **Stop points.** Stop and report to the user only when:
- 2.6.a a prerequisite in 3.5 is missing (list the exact install commands);
- 2.6.b a spike fails its pass bar (report the numbers and propose options);
- 2.6.c the same failure recurs three times despite different fixes;
- 2.6.d something would require contradicting this brief;
- 2.6.e the release candidate is ready for the user to try (13.3).

When stopping, end the message with a line starting `🛑 NEEDS YOU:` that says exactly what the user must do or decide.

2.7 **Resuming.** Keep `PROGRESS.md` up to date so work can resume in a new session. If the user says only "continue", re-read `BRIEF.md`, `PLAN.md`, `DECISIONS.md` and `PROGRESS.md` and carry on from the next task.

2.8 **Keyboard and screen.** The user has agreed that tests may type into windows and take over the screen while they are away. Prefer Windows Sandbox where it works. If a real keystroke from the user is detected during such a test, stop the test and retry later.

2.9 **Don't repeat finished work.** A check that has passed is not rerun unless code it covers has changed. The `full` tier runs only before the release candidate.

2.10 Don't fix individual typos with word-specific rules. Fix the mechanism.

2.11 Every requirement has a test or recorded check that proves it. Every known gap is written in the README.

2.12 **Automatic model routing.** You (the main session) run on the highest-capability model and act as lead engineer. Routine work is delegated to a cheaper subagent, so the user never has to switch models.
- 2.12.a As the first step after saving the brief, create `.claude/agents/implementer.md`: a subagent with `model: sonnet`, whose instructions are to implement one task from `PLAN.md` exactly as specified, run its done-check, commit, and report back; never redesign; report failure instead of improvising.
- 2.12.b You do the planning, both spikes, all high-risk tasks and all reviews yourself. High-risk tasks include: the diff and edit rules (4.5), timing and caret checks (4.6), undo (4.8), the keyboard hook (10.3.a), UI Automation reading (10.6), SendInput writing (10.7), and anything that failed once in the implementer.
- 2.12.c Delegate every other task to the implementer, one task at a time, with the task text, the file list and the done-check command from `PLAN.md`.
- 2.12.d `PLAN.md` must be detailed enough for the implementer to work without seeing this conversation. For each task: the files to create or change, the classes and method signatures, the requirement IDs, and the exact command that proves it is done. Write the logic for the high-risk areas in pseudocode.
- 2.12.e After each delegated task, review the diff against the requirement IDs. If it's wrong, send it back once with specific feedback; if it fails again, do the task yourself.

## 3. Machine and setup

3.1 Windows 11 laptop; NVIDIA RTX PRO 2000 Blackwell laptop GPU (8 GB VRAM, sm_120); Intel Core Ultra 7 265HX; 32 GB RAM; hybrid graphics.
3.2 For the user's own PC only. No installer for others, no code signing, no AMD/Intel GPU support.
3.3 No text ever leaves the machine.
3.4 UK English, -ise spellings (colour, organise, travelling, realise). British spellings are never changed to American ones.
3.5 **Before the first milestone, check and report** that these are present: .NET 10 SDK, Git, Node.js, a current NVIDIA driver, Windows Sandbox enabled. If anything is missing, stop under 2.6.a. Don't work around it.
3.6 Create `CLAUDE.md` with the build and test commands, and `.claude/settings.json` allowing `dotnet`, `pwsh`, `git` and `node` without prompts.

---

## 4. Feature A: real-time clean-up

4.1 **Where it works.** Any text field that exposes its text and caret through UI Automation, including Notepad, Word, Outlook, Chrome, Edge, WPF and WinForms apps, Electron apps, and web editors such as ProseMirror and Lexical.
- 4.1.a Supporting an app means it works, not that it is special-cased.
- 4.1.b Where a field doesn't expose its text and caret, do nothing rather than guess.

4.2 **Only the user's own typing.** Act only on physical keystrokes. Ignore text inserted by other tools (dictation apps, paste, text expanders): don't correct it, and treat it as a break in the typing session.

4.3 **Instant layer (no AI).**
- 4.3.a When the user finishes a word with a space or punctuation, fix obvious typos within 100 ms.
- 4.3.b Never waits for the AI.
- 4.3.c Fix only when the word is invalid in Hunspell en_GB, isn't in the personal dictionary, isn't capitalised mid-sentence, contains no digits, isn't a URL, email or code-like token, and the top SymSpell candidate is edit distance 1 (or 2 with at least 10× the frequency of the runner-up).
- 4.3.d Anything less certain is left for the AI layer.

4.4 **AI layer.**
- 4.4.a Triggered at a sentence end, or when the user pauses for 700 ms (setting, range 300–2000 ms).
- 4.4.b Sends the current sentence plus up to two preceding sentences.
- 4.4.c Fixes wrong, doubled, missing and swapped letters; missing and extra spaces (run-together and split words); spelling; grammar; apostrophes; capitalisation; punctuation. It infers the intended word from context.
- 4.4.d Keeps the user's words, tone and sentence structure. No rephrasing, no synonyms, no added content.
- 4.4.e The model returns the full cleaned text. Code computes a word-level diff and applies only accepted edits as small, separate replacements. Never replace the whole span.

4.5 **What counts as an acceptable edit (the "no rewrite" rule).**
- 4.5.a Accept an edited word if it is character-similar to what the user typed (normalised edit distance ≤ 0.5), or is a spacing, punctuation, apostrophe or capitalisation change.
- 4.5.b Accept insertion or deletion of short function words (a, an, the, is, are, to, of, and similar) when grammar requires it.
- 4.5.c Reject any other edit, keeping the rest of the correction.
- 4.5.d Reject the whole correction if more than 50% of words would change, or if numbers, URLs or email addresses would change.
- 4.5.e The thresholds are settings, tuned only on the tuning set, never the holdout.

4.6 **Timing and caret rules.**
- 4.6.a While the user is actively typing, never touch the word being typed or the word before it.
- 4.6.b Once the user has paused for at least 1.5 seconds, or ended the sentence, those words may be fixed too.
- 4.6.c Write only if the text and caret are unchanged since the model read them, and no key arrived in the last 150 ms.
- 4.6.d After writing, re-read the field and compare it with the expected text. On a mismatch, log a desync and stop correcting that field for the rest of the session.
- 4.6.e Typing is never blocked or delayed.

4.7 **Visible but quiet.** Each corrected word gets a brief, subtle accent-coloured underline that fades within about a second (setting, on by default). No list or history of corrections is kept or shown.

4.8 **Undo.**
- 4.8.a Ctrl+Z straight after a correction, with the caret unchanged, reverts that correction as one step.
- 4.8.b A global shortcut (default Ctrl+Alt+Z, configurable) reverts the most recent correction from anywhere, if that text is still unchanged.
- 4.8.c A reverted word is added to the personal dictionary.

---

## 5. Model and runtime

5.1 **Runtime.**
- 5.1.a llama-server from the official llama.cpp Windows release, CUDA 13.x build, with the matching `cudart` zip in the same folder (Blackwell needs CUDA 12.8 or newer).
- 5.1.b Child process bound to 127.0.0.1 only.
- 5.1.c CPU build as a fallback.
- 5.1.d Not LLamaSharp: its CUDA 12 packages silently fall back to CPU on Blackwell.

5.2 Model, quantisation and prompt are settings, not hard-coded.

5.3 **Candidates,** GGUF at Q5_K_M (Q4_K_M if latency needs it):
- 5.3.a Gemma 4 E4B.
- 5.3.b Qwen3.5-4B with thinking disabled.
- 5.3.c Gemma 4 E2B (also the CPU fallback).
- 5.3.d The default is whichever scores best in Spike A within the latency budget.

5.4 **Inference settings:** thinking off; temperature 0; a fixed system prompt so the prompt cache is reused; capped `n_predict`. The prompt says: fix typing and grammar mistakes, infer intended words from context, keep the user's wording and British spelling, return only the cleaned text.

5.5 **Latency:** median AI pass under 2 seconds on GPU.

5.6 **Lifecycle.**
- 5.6.a A watchdog restarts llama-server if it crashes or stops answering.
- 5.6.b The model unloads after 15 minutes without typing (setting) and reloads on the next keystroke, without blocking typing.
- 5.6.c Only one instance of the app can run.

5.7 **Setup script:** downloads the pinned GGUF and the llama.cpp and cudart zips, with checksums.

5.8 **Verify script.** Fails with a plain-language fix if:
- 5.8.a the server log doesn't show the CUDA device and offloaded layers;
- 5.8.b `CUDA_VISIBLE_DEVICES=-1` is set;
- 5.8.c a conflicting `cudart64_12.dll` is on PATH;
- 5.8.d `llama-server.exe` isn't set to the high-performance GPU (`GpuPreference=2`);
- 5.8.e the model doesn't load or clean a test sentence;
- 5.8.f median latency is 2 seconds or more;
- 5.8.g Windows Defender has quarantined or blocked the app (keyboard hook plus SendInput can look like a keylogger). Explain how to allow it.

---

## 6. Measuring accuracy

6.1 **Evaluation data (no user data needed).**
- 6.1.a Write at least 400 clean, natural UK English messages in the style of everyday chat, email and work notes, with British spelling.
- 6.1.b Add realistic typing noise using a UK keyboard model: dropped, doubled, swapped and neighbouring-key letters; missing, extra and misplaced spaces; missing apostrophes and capitals; light grammar slips. Vary the noise from light to heavy.
- 6.1.c Add 50 hand-written heavily garbled messages where the intended text must be inferred from context.
- 6.1.d At least 20% of messages contain no errors, so false edits are measurable.
- 6.1.e Split into a tuning set and a holdout of at least 200 messages. The holdout is never used for tuning.

6.2 Score the whole pipeline (instant layer plus AI layer plus edit rules), not each part alone.

6.3 **Metrics:**
- 6.3.a Errors fixed (share of words that needed fixing and were fixed correctly).
- 6.3.b False edits (share of correct words that were changed).
- 6.3.c Exact-match rate against the clean original.
- 6.3.d Latency p50 and p95.
- 6.3.e The worst 20 failures, listed with error category.

6.4 **Targets:** errors fixed at least 95%; false edits under 1%.

6.5 **Real-world signal.** The app counts corrections and undos (counts only) so the false-edit rate in real use can be estimated without storing any text.

6.6 Publish the numbers with each build, including where it fails.

---

## 7. Feature B: word prediction

7.1 A faint suggestion just after the caret completes the current partial word or, after a space, predicts the next word.
7.2 Predictor: n-gram model from a freely licensed UK English text source, plus the user's own typing history, answering in under 10 ms. Name the source and licence in `PLAN.md`.
7.3 The typing history used for prediction is stored only as word counts, never as sentences.
7.4 Tab accepts the suggestion only when it is visible and the caret hasn't moved since it was shown. Otherwise Tab passes through untouched.
7.5 The suggestion disappears on the next keystroke that doesn't match it.
7.6 The overlay is a transparent, click-through, no-activate window positioned from the caret rectangle, sized from the caret height.
7.7 Setting to turn prediction off.

---

## 8. Essentials

8.1 Runs in the system tray and starts automatically with Windows (setting, on by default).
8.2 **Pause:** one click in the tray, or a global shortcut (default Ctrl+Alt+P, configurable). The tray icon shows the paused state.
8.3 **Never touch:** password fields (UIA IsPassword) and elevated windows.
8.4 **Excluded by default (editable list):** terminals, code editors, password managers, remote desktop clients, games, Excel cells.
8.5 **Personal dictionary:** words added by undo (4.8.c), by the settings window, or automatically after the user types an unrecognised word 3 times (setting). Visible and editable only in settings.
8.6 **Privacy:** logs and settings never contain typed text, only counts and error types. Settings and dictionary are plain UTF-8 text in `%APPDATA%`.

---

## 9. Interface

9.1 **Toolkit:** WPF-UI 4.x: FluentWindow with Mica backdrop, NavigationView on the left, cards, WPF-UI.Tray. No hand-built default-looking controls.

9.2 **Colour and style.**
- 9.2.a A blue palette defined once as design tokens: one primary accent blue, a lighter and darker shade, plus neutral surfaces and text colours for light and dark modes.
- 9.2.b The accent is used consistently for toggles, buttons, highlights, the correction underline and the tray icon.
- 9.2.c Segoe UI Variable, a clear type scale, generous spacing, rounded cards with soft borders, and Fluent System Icons throughout.
- 9.2.d Light and dark modes that follow Windows, both with text contrast of at least 4.5:1.
- 9.2.e A custom app icon and tray icon in blue, with active and paused states, readable on light and dark taskbars.

9.3 **Settings pages.**
- 9.3.a **Home:** status card (on/paused, model loaded, GPU in use, typical latency) and counts of corrections today.
- 9.3.b **Clean-up:** pause length, how bold corrections are, underline on/off, model.
- 9.3.c **Prediction:** on/off.
- 9.3.d **Shortcuts:** pause and undo shortcuts.
- 9.3.e **Apps:** excluded apps.
- 9.3.f **Dictionary:** view, add, remove words.
- 9.3.g **About:** version and the measured accuracy from the last build.

9.4 **First-run window.** Three short steps: what the app does, permission to download the model (with a progress bar), and a ready screen with the shortcuts.

9.5 **Self-review.** Before showing any window to the user, render screenshots of it in light and dark at 100% and 150% scale, view them yourself, and fix anything misaligned, clipped, low-contrast or unpolished.

---

## 10. Architecture

10.1 **Core** (`net10.0`, no Windows references): typing session, instant layer, AI trigger, diff and edit rules, predictor, personal dictionary, settings, and an `ITextTarget` abstraction (`ReadWindow`, `ApplyEdit`, `Verify`).
10.2 **Simulated editor** implementing `ITextTarget`, modelling typing, races, caret moves and host-app autocorrect. Most tests use it.
10.3 **Windows layer** (`net10.0-windows`):
- 10.3.a Low-level keyboard hook whose callback only timestamps and enqueues, returning immediately.
- 10.3.b UI Automation on an isolated worker with timeouts, so a hung app can't freeze the assistant.
- 10.3.c SendInput writer.
- 10.3.d Overlay for suggestions and the correction underline.
- 10.3.e Global shortcuts.
10.4 **Host:** tray, settings window, first-run window, model management.
10.5 **LLM client:** HTTP to llama-server on 127.0.0.1 only.

### Text access rules

10.6 **Reading.**
- 10.6.a Use `TextPattern2.GetCaretRange`, falling back to `TextPattern.GetSelection` for a collapsed selection.
- 10.6.b Expand by characters to get a window around the caret. Never read the whole document.
- 10.6.c If the caret is unknown, skip. Never guess it from ValuePattern.
- 10.6.d Chromium and Electron expose full text only after an accessibility client connects, so warm up the UIA connection on focus change.

10.7 **Writing.**
- 10.7.a One SendInput batch: move, delete, type with Unicode keystrokes, move back.
- 10.7.b Every injected event is tagged via `dwExtraInfo`; the hook ignores tagged events.
- 10.7.c Never use `ValuePattern.SetValue`.

10.8 **Elevated windows.** UIPI blocks injection into them. Detect the target's integrity level and do nothing. Never run the app elevated.

---

## 11. Testing and workflow

11.1 **One script, `check.ps1`:**
- 11.1.a `fast`: incremental build plus Core tests, under 30 seconds. Run after every change.
- 11.1.b `model`: llama-server tests plus latency benchmark.
- 11.1.c `eval`: scores the holdout; writes `metrics.json` and a short Markdown report.
- 11.1.d `e2e`: desktop and browser E2E, under the rules in 2.8. Prefer Windows Sandbox with a mock LLM server.
- 11.1.e `full`: everything, then packaging. Before a release only.

11.2 Every requirement ID has at least one passing test or recorded check. The test name includes the ID.

11.3 **E2E:**
- 11.3.a FlaUI v5 (UIA3) for Notepad and a WPF TextBox.
- 11.3.b Playwright only opens local test pages in headed Edge (textarea, contenteditable, password box, ProseMirror, Lexical) and reads results back from the DOM or editor state.
- 11.3.c Typing is always real OS-level SendInput, because Playwright's keyboard never reaches the low-level hook.
- 11.3.d Check for desyncs before checking text.

11.4 Cache models and llama.cpp zips outside the repo. Push to GitHub for backup only.

---

## 12. Known pitfalls

12.1 Windows Notepad fixes some typos itself. Account for it in tests, or use input Notepad doesn't change.
12.2 Never write from a caret position that moved before the keys were sent.
12.3 Don't pause the AI pass or the instant layer while the user types; only the write waits.
12.4 A theme override can make text invisible against its background. Check contrast in screenshots.
12.5 Browser test pages must not read `process.env`. Load Lexical's production build and register its rich-text plugin, or it ignores keystrokes.
12.6 Windows silently removes a low-level hook that is slow to return.
12.7 Silent CPU fallback on Blackwell (wrong CUDA build, LLamaSharp, `CUDA_VISIBLE_DEVICES`, iGPU routing) must be caught by the verify script.
12.8 Thinking-mode models blow the latency budget. Thinking must be off.
12.9 Monaco/VS Code exposes only about a page of text.
12.10 Windows Defender may flag the app as a keylogger.

---

## 13. Delivery

13.1 `check.ps1 full` produces a zip with the app, the setup script and the verify script.
13.2 **Plain-language README:** what it does, what it doesn't, the shortcuts, how to pause it, which apps were tested with what result, measured accuracy and latency, known limits, how to allow it in Windows Defender, and a requirement coverage table (ID, status, proving test).
13.3 The first build is a release candidate, labelled as such. Don't tag v1.0.0 until the user has used it on their own PC and confirmed it works.
13.4 Install and verify take minutes, not hours.

---

## 14. Out of scope for v1

14.1 Installer, code signing, other users' PCs.
14.2 AMD or Intel GPU support.
14.3 Languages other than UK English; keyboard layouts other than UK.
14.4 Correcting dictated or pasted text.
14.5 A history or list of corrections.
14.6 Pausing the AI on battery.
14.7 CI pipelines and self-hosted runners.
14.8 Text Services Framework (IME-style) integration.
14.9 Style rewriting, tone changes or summarising.
