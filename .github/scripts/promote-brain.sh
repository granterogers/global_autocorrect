#!/usr/bin/env bash
# D138, D179: merges a lesson that passed the Brain gate (and, for shared ones, the owner's approval) into main.
set -euo pipefail
git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git fetch -q origin main
# Only brain/ and data/learning/ may change: anything else is not a lesson.
if git diff --name-only origin/main...HEAD | grep -v -E '^(brain/|data/learning/)'; then echo "not a brain-only change"; exit 1; fi
# A lesson that no longer fits on main (another one landed meanwhile) is dropped: the next one is built on main again.
git rebase origin/main
git push origin HEAD:main
echo "Lesson $(git rev-parse --short HEAD) passed the stress runs and the real-model test: merged into main." >> "$GITHUB_STEP_SUMMARY"
