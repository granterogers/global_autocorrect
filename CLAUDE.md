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
- **Prefer the brain over a rebuild (D102).** If a fix can be made in `brain/brain.json` (a `tuning` value from
  `Brain.Knobs`, a per-program `write` rule of "retype" or "arrows"), make it there: bump `updated` (UTC ISO 8601), run
  `pwsh ./check.ps1 fast` (it checks the file loads and every entry is known), commit and plain `git push`. The hook
  doesn't tag pushes that only touch paths in `.versionignore`, so no release or deploy is needed. Every running copy
  downloads the brain within 15 minutes. Code changes still follow the four steps above. If a fix needs a new kind of
  value, add it to the brain in code once (a release), then tune it from the brain afterwards.
- Requirement IDs (e.g. `4.5.a`) go in test names (`R4_5_a_...`), commit messages and PROGRESS.md.
- Never log or persist typed text (8.6). Counts and error types only.
- Models and llama.cpp zips are cached outside the repo in `%LOCALAPPDATA%\GlobalAutocorrect\cache` (11.4).
- Pinned versions are in PLAN.md section "Pinned versions". Don't upgrade during the build (2.4.c).
- Push with plain `git push` (the user's version hook reads the first word after `push` as the remote).
- Don't create version tags by hand; the push hook does it. v1.0.0 was the one approved exception (D86).
- The version a build carries is the tag on its commit (`scripts/version.ps1`), never edited in the project file.
