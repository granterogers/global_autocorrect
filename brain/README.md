# The brain

`brain.json` changes how every running copy behaves without a new build: each copy downloads it within 15 minutes (D102).

**To change a value:** add or edit it under `"tuning"` (the names below), bump `"updated"` (UTC ISO 8601), run `pwsh ./check.ps1 fast` (it checks every name is known and every value is in range), commit, and plain `git push`. A push that only touches `brain/` makes no new version and no release. A value outside its range is held to the range; a value left out goes back to its default.

**Before pushing:** a change to `Rules.*` or the `prompt` must pass `pwsh ./scripts/brain-eval.ps1` (D111). Timings and switches change how fixes are typed: push them to the `brain-candidate` branch instead of main, and the Brain gate workflow runs the stress and real-use tests on them before merging to main (D138).

Switches are 1 (on) or 0 (off).


## Enter held for a pending fix (D154)

| Name | Default | Range | What it does |
|---|---|---|---|
| `Enter.Hold` | 1 | 0–1 | D154: hold Enter in a field that sends on Enter while a fix is on its way (0 = off) |
| `Enter.MaxHoldMs` | 1500 | 300–3000 | D154: the longest Enter is held |
| `Enter.HardCapMs` | 4000 | 1000–8000 | D154: the longest, even while a write is in progress |
| `Enter.SendsAfter` | 1.5 | 1–5 | D154: sends seen (fading) before a field counts as sending on Enter |
| `Enter.InstantHoldMs` | 400 | 0–1500 | D156: in any field, Enter waits this long at most for the instant fix of the word just finished (0 = off) |
| `Enter.GapMs` | 60 | 0–300 | D161: after a held Enter is let go, the keys typed after it follow this much later (an editor handles Enter a moment late) |
| `Enter.SendCheckMs` | 300 | 100–1500 | D154: how long after Enter the field is looked at |

## Fixes placed through UI Automation (D122–D152)

| Name | Default | Range | What it does |
|---|---|---|---|
| `Place.Enabled` | 1 | 0–1 | D122/D125/D143: place fixes in browsers (0 = arrows and retyping, as before) |
| `Place.Retries` | 3 | 1–6 | D144: tries for a refused placement |
| `Place.SelectWaitMs` | 400 | 100–2000 | D122: wait for the field to show the fix selected |
| `Place.SettleMs` | 30 | 0–300 | D122: pause for the page's editor to take the selection |
| `Place.CkEditorSettleMs` | 120 | 0–1000 | D151: extra pause in CKEditor (Teams) |
| `Place.TypedWaitMs` | 1000 | 200–3000 | D122: wait for the replacement to show |
| `Place.ReturnWaitMs` | 500 | 100–2000 | D122: wait for the caret to be back |
| `Place.StrayCopyRepair` | 1 | 0–1 | D151: take out a fix typed beside the word (0 = off) |
| `Hold.MaxMs` | 3000 | 500–10,000 | D122: the longest the user's keys are held during a write |

## What the app learns per program (D81–D137)

| Name | Default | Range | What it does |
|---|---|---|---|
| `Health.RetypeAfter` | 0.25 | 0.05–3 | D89/D95/D121: failures (fading) before a program changes how it's written to |
| `Health.PlaceFailuresBeforeUnsafe` | 2 | 1–20 | D137: placed-fix failures before placing stops in a program |
| `Health.PlaceFailureShare` | 20 | 2–200 | D137: and more than one in this many of its fixes |

## Writing and checking (D81, D65, D124)

| Name | Default | Range | What it does |
|---|---|---|---|
| `Write.StuckLimit` | 8 | 2–50 | D81: field disagreements in a row before a write is given up |
| `Write.StuckMs` | 1500 | 300–10,000 | D81: within this long |
| `Ai.RetryAfterMs` | 40 | 10–500 | D65: retry a read that lagged the keys |
| `Ai.MaxReadRetries` | 20 | 2–100 | D65/D124: retries before the AI gives up on a field |
| `Ai.MaxRevisitsPerSession` | 20 | 0–200 | D49: looks back at earlier text per session |
| `Session.UnshownAfterMs` | 1200 | 300–5000 | D124: keys the field never showed are dropped from the record after this long |
| `Learn.AfterMs` | 5000 | 1000–60,000 | 8.5: a word is learnt once it has stood this long |

## Suggestions and phrases (D59, D94, D97, D142)

| Name | Default | Range | What it does |
|---|---|---|---|
| `Predict.ReadRetries` | 6 | 0–30 | D59/D150: re-reads while the field catches up |
| `Predict.ReadRetryMs` | 25 | 5–200 | D59: between them |
| `Predict.WaitForPauseMs` | 90 | 20–1000 | D94: typing pause before a suggestion is looked for |
| `Predict.LightWaitForPauseMs` | 300 | 50–2000 | D97: in programs whose reads are slow |
| `Predict.UseModel` | 1 | 0–1 | D159: rank word completions by the AI model's reading of the sentence (0 = the word list only) |
| `Predict.ModelTimeoutMs` | 250 | 50–1500 | D159: how long a suggestion waits for the model before using the word list |
| `Predict.ModelContextChars` | 300 | 50–1500 | D159: how much text before the caret the model reads |
| `Predict.ModelMinChance` | 0.15 | 0–1 | D159: the model's pick needs at least this chance |
| `Predict.ModelLead` | 2 | 1–10 | D159: and this many times the next candidate's |
| `Phrases.MinUses` | 3 | 2–20 | D142: uses before a phrase is offered |
| `Phrases.MinDays` | 2 | 1–30 | D142: on at least this many days |
| `Phrases.AgreeShare` | 0.7 | 0.5–1 | D142: share of times the same words followed |
| `Phrases.ForgetAfterDays` | 90 | 7–730 | D142: unused phrases are forgotten after this long |

## Correction judgements (D140, D145)

| Name | Default | Range | What it does |
|---|---|---|---|
| `Instant.ShortPickMaxLength` | 2 | 0–5 | D140: an instant fix that shortens a word to this many letters or fewer is left to the AI |
| `Lang.ShortLineWords` | 5 | 0–20 | D145: a line this short with one foreign word and no English one reads as foreign |
| `Lang.ForeignRatio` | 1.5 | 1–5 | D116: foreign words needed per English word |

## Engine timings (a new engine is built when one changes)

| Name | Default | Range |
|---|---|---|
| `ActiveTypingMs` | 1500 | 500–5000 |
| `WriteGapMs` | 150 | 50–1000 |
| `WriteDeadlineMs` | 60,000 | 5000–120,000 |
| `InstantQuietMs` | 60 | 20–500 |
| `RevisitAfterMs` | 1500 | 0–10,000 |
| `MaxLineBreaksCrossed` | 8 | 0–50 |
| `ContextSentences` | 3 | 1–6 |
| `ShakyAppGapMs` | 600 | 0–3000 |
| `ParagraphsLookedBack` | 3 | 0–10 |
| `RetypeMaxDistance` | 300 | 0–1000 |
| `LongFixDistance` | 40 | 10–1000 |
| `LongFixQuietMs` | 1500 | 0–5000 |
| `WriteSettleMs` | 200 | 0–3000 |

## Correction rules (D111: check with brain-eval first)

| Name | Range |
|---|---|
| `Rules.MaxNormalisedDistance` | 0.2–0.8 |
| `Rules.MaxChangedWordRatio` | 0.2–0.9 |
| `Rules.MaxNonWordDistance` | 0.3–0.9 |
| `Rules.AcceptLikelyKeyboardSlips` | 0–1 |
| `Rules.LenientWithNonWords` | 0–1 |
| `Rules.AddQuestionMarks` | 0–1 |

## Other sections

- `write`: per program, `"retype"` or `"arrows"` (D102): how fixes are typed there.
- `prompt`: the AI's instructions and examples (D111: check with brain-eval first).
- `apps`, `mistakes`, `pcs`: counts learnt on PCs (D103, D138), written by `scripts/publish-learning.ps1`. Not edited by hand.
