# Tuning log (T6.2)

Every change below was made by looking at the **tuning** set only (260 messages). The holdout (200) is scored once tuning is finished (4.5.e, 6.1.e). Full-pipeline runs: `dotnet run --project tools/GlobalAutocorrect.Eval -c Release -- run --set tuning`.

| Run | Change | Errors fixed | False edits | Exact | p50 / p95 |
|---|---|---|---|---|---|
| T1 | Baseline: prompt v1, rules as planned (4.5), instant layer exactly per 4.3.c | 86.1% | 0.67% | 77% | 334 / 496 ms |
| T2 | D23 noisy-channel instant layer + split check; D24 keep user's whitespace, no closing punctuation on unfinished sentences; D25 capitalisation rule; D26 split/merge must be near-exact | 88.6% | 0.26% | 80% | 334 / 497 ms |
| T3 | D27: 3-way split/merge in the aligner; one likely keyboard slip accepted regardless of length; instant layer leaves half of a split word | 89.2% | 0.23% | 81% | 337 / 494 ms |
| T4 | A stretch whose letters match exactly is one spacing edit (`ev eninginstead`); prompt v2: put back clearly dropped small words, plus two examples | 90.5% | 0.29% | 83% | 342 / 511 ms |
| T5 | Alignment cost uses the keyboard model, so `s` ↔ `a` pairs word for word (trace showed `I hae s flight` lost to a split + deletion) | 90.5% | 0.23% | 84% | 346 / 520 ms |

| T6 | Prompt v3: one more example with dropped "the", plus a clean sentence that must stay unchanged (guards against over-inserting) | **90.8%** | **0.20%** | 83% | 344 / 523 ms |

**Accuracy push after RC1 (2026-10-30), still tuning set only.** Each run dumped every message with the raw whole-message model reply, so "the model missed it" could be told apart from "the app lost it" (`run --set tuning --dump`, tuning only).

| Run | Change | Errors fixed | False edits | Exact | p50 / p95 |
|---|---|---|---|---|---|
| T6 again | Same code, re-run as the baseline | 90.8% | 0.20% | 83% | 515 / 786 ms |
| (2 passes) | The model re-reads its own reply once. Not adopted | 90.5% | 0.20% | 83% | 860 / 1533 ms |
| T7 | D30: two likely keyboard slips accepted (`by` → `but`, `to` → `got`); lone `i` always capitalised | 92.7% | 0.20% | 87% | — |
| T8 | Prompt v4 (file): dropped small words put back "where any native writer would clearly use them", two more dropped-"the" examples, one heavy-typo example, one more clean example | 93.5% | 0.20% | 88% | 369 / — ms |

| T9 | D31: a fix next to a dropped function word (`si` → `is the`); a capital after a "?" the reply adds; possessives of valid words | 94.4% | 0.23% | 88% | 375 / 569 ms |
| T10 | Same, with the built-in prompt now v4; padding guard for `Spare room` → `The spare room` | **94.4%** | **0.20%** | 89% | 371 / 566 ms |

| (8-bit) | Same code with `gemma-4-E4B-it-Q8_0.gguf` (same model, same pinned revision, 7.63 GB). Not adopted: no gain, slower, about 5.4 GB of graphics memory instead of about 3.5 GB | 94.4% | 0.20% | 89% | 420 / 659 ms |

**Long garbled paragraphs (2026-10-30, user report).** New tuning material: `data/eval/stress.jsonl`, 16 new multi-sentence messages at heavy and severe noise (heavy noise applied twice, about half the words slipped), each also clean, plus the user's own paragraph. Tuning only; the holdout is untouched by it.

| Run | Change | Stress fixed | Stress false | Tuning fixed | Tuning false |
|---|---|---|---|---|---|
| S0 | T10 code | 83.5% | 0.32% | 94.4% | 0.20% |
| S1 / T11 | D36: non-words fixed leniently and not counted towards 4.5.d | 89.2% | 0.32% | 94.4% | 0.20% |
| S2 / T12 | D38 sweep, first version (no limits). Not adopted: corrupted re-spaced words | 86.6% | 0.32% | 92.9% | 0.23% |
| S3 / T13 | Sweep only where the model tried; plausible slips only; whole-word spans | 88.7% | 0.32% | 94.4% | 0.20% |
| S4 / T14 | D37: a real word split only when the pair is 3× as common (`anew` → `a new`) | **89.1%** | **0.32%** | **94.4%** | **0.20%** |

**Tuning frozen again after T10** (prompt v4, rules D19–D31). The holdout is scored once more on this code; the earlier holdout score (92.3%) was seen before this round, which is disclosed with the new number.

**Tuning frozen after T6** (prompt v3, rules D19–D27, instant layer D23). The holdout is scored once, next.

T5 plateaus at about 90.5%. The scorer shares the alignment cost, so the error count moved from 733 to 728 (more accurate alignment). Most of what remains is dropped articles the model doesn't restore, and heavy multi-error messages.

## What later rounds showed

- **T3 → T4:** the biggest remaining block was dropped articles (`for pantomime`, `in main hall`). The prompt now allows putting back a small word "only when it was clearly dropped". That recovered some, with false edits still at 0.29%.
- **T4 trace** (`run --set tuning --trace a080,a071,b116`): the model's reply for `I hae s flight` was right, but the alignment paired `hae` ↔ `have a` (split) and deleted `s`, so both edits were rejected. → T5.
- **Accepted residue:** a valid word replaced by another valid word (`round` → `around`, `adaptor` → `adapter`) is a model word choice the rules can't tell from a real-word fix (`form` → `from`) without context. It counts as false edits, which stay well under 1%. An article the model guesses differently (`spare room` → "a" instead of "the") is genuinely ambiguous.

## What T1's worst 20 showed

- The trailing space after a sentence was deleted, because the model output is trimmed (`policy.Please`). That glued text onto URLs, so the protected-URL check then rejected every later fix in the message. → D24.
- The instant layer took the most frequent distance-1 word when another was nearly as likely (`ofr` → `of`, meant `for`; `weh` → `we`). → D23.
- The instant layer deleted letters from run-together words (`ifyou` → `you`). → D23 split check.
- A word swap hidden inside a split (`stationfor` → `station at`) passed the normalised-distance check. → D26.
- Job titles were capitalised mid-sentence (`senior analyst` → `Senior Analyst`), giving false edits on clean messages. → D25.
