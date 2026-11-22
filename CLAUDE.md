# Global Autocorrect

Windows tray app: real-time typo/grammar clean-up in any app via a local llama-server model, plus Tab-accepted word prediction. Spec: `BRIEF.md` (never edit). Plan: `PLAN.md`. Decisions: `DECISIONS.md`. Status: `PROGRESS.md`.

## Commands

All checks go through one script (PowerShell 7):

```
pwsh ./check.ps1 fast    # incremental build + Core tests, < 30 s. Run after every change.
pwsh ./check.ps1 model   # llama-server tests + latency benchmark (needs GPU + model cache)
pwsh ./check.ps1 eval    # score holdout -> artifacts/metrics.json + artifacts/eval-report.md
pwsh ./check.ps1 e2e     # desktop + browser E2E; takes over keyboard/screen
pwsh ./check.ps1 full    # everything + packaging. Release candidate only.
```

Install / update the app on this PC (build, fast tests, package, close the running copy cleanly, replace, restart):

```
pwsh ./scripts/deploy.ps1
```

The unzipped package is `dist\GlobalAutocorrect` (git-ignored); its `install.ps1` is the user's one-command install.
The app installs to `%LOCALAPPDATA%\Programs\GlobalAutocorrect`; user data stays in `%APPDATA%\GlobalAutocorrect`.

Plain build: `dotnet build GlobalAutocorrect.slnx -c Debug`
Core tests only: `dotnet test tests/GlobalAutocorrect.Core.Tests`

## Rules
- **After every change the user asks for, release it, without asking (D86):**
  1. `pwsh ./check.ps1 fast`
  2. commit, with the requirement IDs
  3. plain `git push`: the hook tags the next minor version (v1.N.0) and GitHub Actions `release.yml` publishes it
  4. `pwsh ./scripts/deploy.ps1 -SkipTests`, which installs that same version on this PC.

  The user gives feedback and expects to find it fixed in the running app without doing anything. If a step fails, say
  so: don't push failing code, and leave the previous install running.
- **The brain first, always (D102, D175).** The user's aim: the app improves without new builds. Before any code change,
  ask whether the fix can be made in `brain/brain.json`; most correction fixes can. It holds (every name in
  `brain/README.md`): `tuning` values (`Tunables`, `Brain.Knobs`, `Brain.RuleKnobs`), including an on/off switch for each
  correction rule (`Rules.*`, `Instant.*`, `Tidy.*`); `words`, changes to the word lists the layers use (`WordLists`:
  `Instant.Fixes` typed=meant pairs, `Instant.Leave`, `Words.Known`, `Words.NeverChange`, `Words.Function`, spelling
  pairs, abbreviations, `Lang.English`); the AI `prompt`; per-program `write` rules ("retype" or "arrows").
  To change it: edit, bump `updated` (UTC ISO 8601), run `pwsh ./check.ps1 fast` (it checks the file loads and nothing
  in it is ignored), commit and plain `git push`. Pushes that only touch `.versionignore` paths make no version, release
  or deploy; every running copy downloads the brain within 15 minutes. A change to how text is corrected (rules,
  switches, word lists, prompt) must first pass `pwsh ./scripts/brain-eval.ps1` (scores it against origin/main's brain
  on the tuning set, never the holdout; it closes and restarts the installed app after 2 minutes idle). Don't push one
  that fails. Typing timings go through the `brain-candidate` branch and the Brain gate (D138).
  Code is for what the brain can't express: a bug in reading or typing into other apps, or a new kind of value or list.
  A new kind is added in code once (a release, with the four steps above), then tuned from the brain afterwards.
  After adding a `Tunable` or `WordList`, run `python scripts/brain-readme.py` to list it in `brain/README.md` (a test checks).
- **New code brings its numbers into the brain (D155).** A timing, retry count, threshold or on/off switch that is a
  judgement, not a fact, is a `Tunable` (`src/GlobalAutocorrect.Core/Learning/Tunables.cs`: default, range, the decision
  it came from), read where it's used, and listed in `brain/README.md` (a test checks). Then it can be changed later
  without a rebuild.
- Requirement IDs (e.g. `4.5.a`) go in test names (`R4_5_a_...`), commit messages and PROGRESS.md.
- Never log or persist typed text (8.6). Counts and error types only.
- Models and llama.cpp zips are cached outside the repo in `%LOCALAPPDATA%\GlobalAutocorrect\cache` (11.4).
- Pinned versions are in PLAN.md section "Pinned versions". Don't upgrade during the build (2.4.c).
- Push with plain `git push` (the user's version hook reads the first word after `push` as the remote).
- Don't create version tags by hand; the push hook does it. v1.0.0 was the one approved exception (D86).
- The version a build carries is the tag on its commit (`scripts/version.ps1`), never edited in the project file.
