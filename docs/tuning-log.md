# Tuning log (T6.2)

Every change below was made by looking at the **tuning** set only (260 messages). The holdout (200) is scored once tuning is finished (4.5.e, 6.1.e). Full-pipeline runs: `dotnet run --project tools/GlobalAutocorrect.Eval -c Release -- run --set tuning`.

| Run | Change | Errors fixed | False edits | Exact | p50 / p95 |
|---|---|---|---|---|---|
| T1 | Baseline: prompt v1, rules as planned (4.5), instant layer exactly per 4.3.c | 86.1% | 0.67% | 77% | 334 / 496 ms |
| T2 | D23 noisy-channel instant layer + split check; D24 keep user's whitespace, no closing punctuation on unfinished sentences; D25 capitalisation rule; D26 split/merge must be near-exact | 88.6% | 0.26% | 80% | 334 / 497 ms |
| T3 | D27: 3-way split/merge in the aligner; one likely keyboard slip accepted regardless of length; instant layer leaves half of a split word | 89.2% | 0.23% | 81% | 337 / 494 ms |
| T4 | A stretch whose letters match exactly is one spacing edit (`ev eninginstead`); prompt v2: put back clearly dropped small words, plus two examples | 90.5% | 0.29% | 83% | 342 / 511 ms |
| T5 | Alignment cost uses the keyboard model, so `s` ↔ `a` pairs word for word (trace showed `I hae s flight` lost to a split + deletion) | 90.5% | 0.23% | 84% | 346 / 520 ms |

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
