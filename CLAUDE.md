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
- **After every change the user asks for, run `pwsh ./scripts/deploy.ps1`** so the installed app is the latest build.
  The user gives feedback and expects to find it fixed in the running app without doing anything. If deploy fails, say
  so and leave the previous install running.
- Requirement IDs (e.g. `4.5.a`) go in test names (`R4_5_a_...`), commit messages and PROGRESS.md.
- Never log or persist typed text (8.6). Counts and error types only.
- Models and llama.cpp zips are cached outside the repo in `%LOCALAPPDATA%\GlobalAutocorrect\cache` (11.4).
- Pinned versions are in PLAN.md section "Pinned versions". Don't upgrade during the build (2.4.c).
- Push with plain `git push` (the user's version hook reads the first word after `push` as the remote).
- Don't create version tags by hand; the push hook does it. Never tag v1.0.0 (13.3).
