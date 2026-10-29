# Global Autocorrect

**Release candidate 1 — not yet confirmed on your PC (13.3).**

Global Autocorrect tidies up what you type, as you type, in almost any Windows app. It runs entirely on your laptop, using your NVIDIA GPU. No text ever leaves the machine.

- **Obvious slips are fixed the moment you finish a word.** `teh` becomes `the`, and `bookk` becomes `book`.
- **Everything else is fixed by a small AI model that reads the surrounding sentence.** That includes run-together or split words, missing apostrophes and capitals, punctuation and grammar slips. It runs when you finish a sentence or pause for a moment.
- **It fixes mistakes, not your style.** It never swaps your words for synonyms, rephrases you, or changes British spellings to American ones.
- **A faint suggestion after the caret finishes your word or predicts the next one.** Press **Tab** to take it, or just keep typing to ignore it.

## What it doesn't do

- It never touches password fields, windows running as administrator, or the apps on its excluded list: terminals, code editors, password managers, remote desktop, games and Excel cells. You can edit that list.
- It never corrects pasted or dictated text, or text inserted by other tools. It only corrects what you type yourself.
- It keeps no history or list of corrections. Only counts are kept, such as "corrections today".
- English (UK) only, on a UK keyboard layout.

## Shortcuts

| What | Default | Change it in |
|---|---|---|
| Pause or resume | **Ctrl+Alt+P**, or one click on the tray icon | Settings › Shortcuts |
| Undo the last correction, straight after it | **Ctrl+Z** | — |
| Undo the last correction, from anywhere in that field | **Ctrl+Alt+Z** | Settings › Shortcuts |
| Take the suggestion | **Tab**, only while it's showing | Settings › Prediction (on or off) |

Undoing a correction also adds that word to your personal dictionary, so it won't be corrected again.

## Install

1. Install the .NET 10 runtime (already on this PC).
2. Unzip `GlobalAutocorrect-<version>.zip` somewhere permanent, for example `C:\Apps\GlobalAutocorrect`.
3. Run `GlobalAutocorrect.exe`. The first-run window explains what it does and downloads the model, about 5 GB, with a progress bar. You can also download it from a terminal:
   ```
   pwsh scripts/setup.ps1
   ```
4. Check it's using the GPU:
   ```
   pwsh scripts/verify.ps1
   ```
   Every check prints OK, or explains the fix in plain language.

It starts with Windows (you can turn that off in Settings) and lives in the system tray.

## Windows Defender

A program that watches the keyboard and types for you can look like a keylogger to antivirus software. If Global Autocorrect disappears after you start it:

1. Open **Windows Security › Virus & threat protection › Protection history**.
2. Choose the Global Autocorrect entry and pick **Allow on device**.
3. Optionally add the app folder under **Virus & threat protection settings › Exclusions › Add an exclusion › Folder**.

`pwsh scripts/verify.ps1` checks for this too.

## Measured accuracy and speed

Measured on 200 test messages that were never used for tuning. These are everyday UK English messages with realistic typing slips added, plus hand-mangled ones. The whole pipeline was scored: instant fixes, the AI and the safety rules together. Measured 29 October 2026 with Gemma 4 E4B on the RTX PRO 2000 (full report: `docs/metrics/holdout-2026-10-29.md`).

| | Result | Target |
|---|---|---|
| Mistakes fixed | **92.3%** | 95% ❌ (not met) |
| Correct words wrongly changed | **0.19%** (about 1 in 500) | under 1% ✅ |
| Messages left exactly as intended | 84% | — |
| Time for an AI clean-up | 344 ms typical, 507 ms slowest 5% | under 2 s ✅ |

**Where it falls short.** Light slips and heavily mangled messages are fixed 95% of the time. Messages with many different slips are fixed 86% of the time. Most misses are:
- a dropped small word the model won't guess ("ask council" instead of "ask the council");
- a slip that turns one real word into another;
- run-together words next to a capitalised name.

It prefers leaving a mistake to changing something you meant. That's why wrong changes are so rare.

## Apps tested

_Filled in from Spike B and the E2E run at release._

## Known limits

_Filled in at release._

## Requirement coverage

_Generated at release from `PLAN.md` §9 and the test results (2.11, 11.2)._

## For developers

See `CLAUDE.md` for build and test commands, `PLAN.md` for the design, `DECISIONS.md` for every judgement call, and `PROGRESS.md` for status.
