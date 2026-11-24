"""Writes brain/README.md from the code: every Tunable, engine knob, rule knob and word list (D155, D175).
Run from the repo root after adding one: python scripts/brain-readme.py"""
import os, re, sys
os.chdir(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))

QUOTED = r'"((?:[^"\\]|\\.)+)"'   # a C# string, with \" inside it
def unescape(s): return s.replace('\\"', '"')

tun = open('src/GlobalAutocorrect.Core/Learning/Tunables.cs', encoding='utf-8').read()
brain = open('src/GlobalAutocorrect.Core/Learning/Brain.cs', encoding='utf-8').read()
opts = open('src/GlobalAutocorrect.Core/Engine/EngineOptions.cs', encoding='utf-8').read()

sections = []
current = None
for line in tun.splitlines():
    m = re.match(r'\s*// ---- (.+?) ----', line)
    if m:
        current = (m.group(1), [])
        sections.append(current)
        continue
    m = re.search(r'Define\("([^"]+)", ([\d._]+), ([\d._]+), ([\d._]+), ' + QUOTED + r'\)', line)
    if m and current:
        g = m.groups()
        current[1].append(g[:4] + (unescape(g[4]),))

def num(s): return s.replace('_', ',') if len(s.replace('_', '')) > 4 else s.replace('_', '')

defaults = dict(re.findall(r'public (?:int|bool) (\w+) \{ get; init; \} = ([\w_]+);', opts))
knobs = re.findall(r'\["(\w+)"\] = \(([\d_]+), ([\d_]+), \(o, v\)', brain)
rules = re.findall(r'\["(Rules\.\w+)"\] = \(([\d.]+), ([\d.]+), \(r, v\)', brain)

out = []
out.append("# The brain\n")
out.append("`brain.json` changes how every running copy behaves without a new build: each copy downloads it within 15 minutes (D102).\n")
out.append("**To change a value:** add or edit it under `\"tuning\"` (the names below), bump `\"updated\"` (UTC ISO 8601), run "
           "`pwsh ./check.ps1 fast` (it checks every name is known and every value is in range), commit, and plain `git push`. "
           "A push that only touches `brain/` makes no new version and no release. A value outside its range is held to the range; "
           "a value left out goes back to its default.\n")
out.append("**Before pushing:** a change to how text is corrected (`Rules.*`, `Instant.*`, `Tidy.*`, `Lang.*`, the `words` lists or the `prompt`) must pass `pwsh ./scripts/brain-eval.ps1` (D111, D175). Timings and "
           "switches change how fixes are typed: push them to the `brain-candidate` branch instead of main, and the Brain gate "
           "workflow runs the stress and real-use tests on them before merging to main (D138).\n")
out.append("Switches are 1 (on) or 0 (off).\n")

for title, rows in sections:
    out.append(f"\n## {title}\n")
    out.append("| Name | Default | Range | What it does |\n|---|---|---|---|")
    for name, d, lo, hi, why in rows:
        out.append(f"| `{name}` | {num(d)} | {num(lo)}–{num(hi)} | {why} |")

out.append("\n## Engine timings (a new engine is built when one changes)\n")
out.append("| Name | Default | Range |\n|---|---|---|")
for name, lo, hi in knobs:
    out.append(f"| `{name}` | {num(defaults.get(name, '?'))} | {num(lo)}–{num(hi)} |")

out.append("\n## Correction rules (D111: check with brain-eval first)\n")
out.append("| Name | Range |\n|---|---|")
for name, lo, hi in rules:
    out.append(f"| `{name}` | {lo}–{hi} |")

lists_src = open('src/GlobalAutocorrect.Core/Learning/WordLists.cs', encoding='utf-8').read()
lists = re.findall(r'Define\("([^"]+)", ' + QUOTED + r', \[(.*?)\]([^;]*)\);', lists_src, re.S)
out.append("\n## Word lists (D175: check with brain-eval first)\n")
out.append("Under `\"words\"`, by list name: `{ \"add\": [...], \"remove\": [...] }`. Added entries join the built-in ones; "
           "`remove` takes a built-in one out (for a pair list, by its typed side). Entries are 1–64 characters; a pair is "
           "`typed=meant`. A list left out is the built-in one. Example: `\"words\": { \"Instant.Fixes\": { \"add\": [\"recieve=receive\"] } }`.\n")
out.append("| Name | Kind | Built in | What it is for |\n|---|---|---|---|")
for name, why, entries, flags in lists:
    kind = ('pairs' if 'pairs: true' in flags else 'words') + (', exact case' if 'caseSensitive: true' in flags else '')
    out.append(f"| `{name}` | {kind} | {len(re.findall(r'\"[^\"]*\"', entries))} | {unescape(why)} |")

out.append("\n## Other sections\n")
out.append("- `write`: per program, `\"retype\"` or `\"arrows\"` (D102): how fixes are typed there.")
out.append("- `prompt`: the AI's instructions and examples (D111: check with brain-eval first).")
out.append("- `apps`, `mistakes`, `pcs`: counts learnt on PCs (D103, D138), written by `scripts/publish-learning.ps1`, and from installs that share (D179, `pcs` entries starting `c-`), written by the Community learning job after the owner approves. Not edited by hand.")
out.append("- `share`: `{ \"url\": \"https://.../v1/learning\" }`, where installs whose owner switched on \"Share what it learns\" send it (D179). Written by `relay/deploy.ps1`; without it nothing is ever sent.")

open('brain/README.md', 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')
print(sum(len(r) for _, r in sections), 'tunables,', len(knobs), 'engine knobs,', len(rules), 'rule knobs,', len(lists), 'word lists')
