# Tuning log (T6.2)

Every change below was made by looking at the **tuning** set only (260 messages). The holdout (200) is scored once tuning is finished (4.5.e, 6.1.e). Full-pipeline runs: `dotnet run --project tools/GlobalAutocorrect.Eval -c Release -- run --set tuning`.

| Run | Change | Errors fixed | False edits | Exact | p50 / p95 |
|---|---|---|---|---|---|
| T1 | Baseline: prompt v1, rules as planned (4.5), instant layer exactly per 4.3.c | 86.1% | 0.67% | 77% | 334 / 496 ms |
| T2 | D23 noisy-channel instant layer + split check; D24 keep user's whitespace, no closing punctuation on unfinished sentences; D25 capitalisation rule; D26 split/merge must be near-exact | _running_ | | | |

## What T1's worst 20 showed

- The trailing space after a sentence was deleted, because the model output is trimmed (`policy.Please`). That glued text onto URLs, so the protected-URL check then rejected every later fix in the message. → D24.
- The instant layer took the most frequent distance-1 word when another was nearly as likely (`ofr` → `of`, meant `for`; `weh` → `we`). → D23.
- The instant layer deleted letters from run-together words (`ifyou` → `you`). → D23 split check.
- A word swap hidden inside a split (`stationfor` → `station at`) passed the normalised-distance check. → D26.
- Job titles were capitalised mid-sentence (`senior analyst` → `Senior Analyst`), giving false edits on clean messages. → D25.
