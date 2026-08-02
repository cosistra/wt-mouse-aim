# DOC-AUDIT — staleness audit of every markdown file, 2026-08-02

Written before a hard context reset, so the repo has to be good enough to cold-start from. Every
`.md` was read and judged **against the code and the newest findings**, not against vibes. Companion
to [`ORIENTATION.md`](ORIENTATION.md), which is the cold-start router this audit exists to support.

**Out of scope** (owned elsewhere this session): `CHANGELOG.md`, `ARCHITECTURE.md`, `CLAUDE.md`,
`debugtests/SESSION-2026-08-02.md`, `debugtests/R40-place.md`, `debugtests/R40-alpha.md`.

## Verdict counts

| verdict | n | meaning |
|---|---|---|
| **CURRENT** | 20 | accurate as it stands; no edit |
| **UPDATE** | 13 | had live-but-wrong content; **corrected in place, dated** |
| **SUPERSEDED** | 3 | plan/runbook whose work has been executed; banner added, kept as the record of *why* |
| **DELETE** | **0** | see "Why nothing was deleted" |
| **RENAMED** | 2 | `R40-stol.md` → `R39-stol.md`, `R40-rotor.md` → `R39-rotor.md` |
| **NEW** | 2 | `ORIENTATION.md`, this file |

## The method, and the one bias worth stating

Staleness was tested three ways, all mechanical: (1) every internal markdown link resolved against
disk — **one** file had dead links; (2) every backticked `*.py|cs|json|md` path resolved — same file,
plus deliberate references to two deleted cards; (3) a term scan for the things known to have been
removed (`RelativeTurnLead`, `authorityUsedFrac`/`authBank`/`authAoa`/`authStick`, `SLACK`,
`BankToTurn`/`Legacy`/`Unified`), each verified **against the code** before any doc was edited.

**The bias to correct for:** a findings document that quotes a now-deleted metric is *not* stale — it
is a historical record, and the metric existed when it was written. What is stale is a document that
tells you to **do** something you can no longer do, or that states a number now known to be wrong.
Edits were confined to the second kind. Historical batches (`R21`…`R37`) were left alone except where
`SESSION-2026-08-02.md` §3.2 names a specific line as refuted.

## Why nothing was deleted

The brief allowed deletion and noted it is recoverable from `ce3f97f`. **Nothing qualified**, and that
is a finding rather than timidity. The two candidates were examined and both kept:

- **`FLIGHT-PROTOCOL.md`** — its "Experiments" half is explicitly superseded, and its A/B arm no longer
  exists. But it is the **only** record of the Gate A–D instrument validation (R22–R25): that a drone
  flies the real law bit-for-bit, that ABBA arming works, that the entry reset only partly works. That
  evidence is load-bearing for every drone capture since. Banner added instead.
- **`debugtests/GATE-CHATTER-FINDINGS.md`** — a closed, negative investigation. Kept because a clean
  negative is expensive to re-derive, its §5a is the origin of the v0.85 fix, and
  `GENERALITY-REVIEW.md` finding 16 cites it.

Every other document is either live or is evidence. The corrections below are what make the historical
ones safe to read; deleting them would remove the evidence and keep the conclusions, which is the
exact failure mode this audit exists to counter.

---

## Per-file verdicts

### Root

| file | verdict | reason / evidence |
|---|---|---|
| `ORIENTATION.md` | **NEW** | Cold-start router. Did not exist. |
| `DOC-AUDIT.md` | **NEW** | This file. |
| `README.md` | **UPDATE** | User-facing and accurate, but claimed every airframe flies off the reticle with no caveat, while R39 measured a rail-to-rail yaw limit cycle on 2 of 3 rotorcraft in hover. Added a short, non-alarming **Known limitations** section. |
| `AIRFRAMES.md` | **CURRENT** | 14 jsonKeys, six traps, sourced from `Encyclopedia.aircraft` with a cross-validated sidecar. Nothing in it has moved; still the only record of this data. |
| `LAW-CHARACTERIZATION.md` | **UPDATE** | §1's corpus table was 15 batches out of date (1 681 captures / R1–R33 → **2 576 / R1–R40**); "12 disk cards never flown" → **4**; "10 airframes" → **13**; RAILED 285/5 903 → **406/8 294**. **§7 rewritten**: reconciliation block for the three numbering schemes, the open work recovered from disk, `#19` wired to its research note. Its "ONE-LAW 3 of 4 covered" claim was **wrong in the dangerous direction** — corrected to **2 of 4**, because the rotor and STOL batches produced captures but not measurements. |
| `LAW-LEDGER.md` | **UPDATE** | Corpus header stale (R33-era). Added a dated block carrying the two corpus-wide invalidations: the v0.99.1 metric repair (every `authorityUsedFrac` claim **withdrawn, not re-scaled**) and the widened multi-card ABBA confound — its "BATCH SUSPECT — R31" note understated the blast radius; **every** multi-card A/B batch must be re-flown. |
| `LAW-WEAKNESS-MAP.md` | **UPDATE** | Four of its eight "fly this" instructions were overtaken by R39, which flew *after* it was written. W1 done (and `oblique-6-dwell` retired for ranking); W4's card retired for `alpha-pullup`; **W5 CLOSED WONTFIX** — the SLACK detector and its metric were deleted, not repaired; the standing-holes paragraph claiming zero rotor/STOL captures corrected. Also carries the **TASK #64** figure fix. |
| `GENERALITY-REVIEW.md` | **UPDATE** | Header said "v0.59" and its status blocks stop at v0.65 while the content runs to v0.99.1. Finding 16's heading still read **OPEN** although its body carries a "RESOLVED, HALF-CONFIRMED" reversal — a heading-scanner would never have seen it. Heading changed to **SPLIT VERDICT**, and a top-of-file block now surfaces the reversal, the one-law-only fact, and the `RelativeTurnLead` deletion at first encounter. |
| `ROADMAP.md` | **UPDATE** | Rested on the premise *"The instrument is now believed sound"*, which the 2026-08-02 session **refuted** — it was lying in three independent places, R28 included. Dated refutation added at that sentence, noting the fix order was right but declared finished ~15 batches early. |
| `INSTRUCTOR-LOOP.md` | **UPDATE** | Standing design doc, still the right framing (measure → bound → collect → climb). One row described `RelativeTurnLead` as a live arm; knob deleted v0.99.1. |
| `FLIGHT-PROTOCOL.md` | **UPDATE** | Historical-record banner: do not fly from it, its Experiments half is superseded, and all seven `armKnob=RelativeTurnLead` lines are records of R23–R25, not instructions. |
| `WOBBLE-FINDINGS.md` | **CURRENT** | v0.51 investigation, dated and self-labelled, and the basis of `analyze-wobble.py`. Historical by construction; the two `Legacy` mentions are contemporaneous. |

### `cards/`

| file | verdict | reason / evidence |
|---|---|---|
| `cards/README.md` | **CURRENT** | Grid table, field table, the `sel[0]` rule. Already states `e2-rel-turn-lead` and the `RelativeTurnLead` knob were deleted — it was correct before this audit. |
| `cards/ALPHA-CARD-REDESIGN.md` | **CURRENT** | Written against `R39-E-alpha.md`; defines `alpha-pullup`, which flew in R40. The newest design doc in the repo. Correctly overturns R39-E's own `startSpeedCorner: 1.0` recommendation in favour of 1.15. |
| `cards/TOMORROW.md` | **SUPERSEDED** | Campaign runbook, mostly executed in R39/R40. Banner names what flew, what is retired, and the one live group — the `e1*` belowness axis + `oblique-above-c`, still the only never-flown cards. §0 install steps remain current. |

### `debugtests/`

| file | verdict | reason / evidence |
|---|---|---|
| `CAPTURES-DB.md` | **UPDATE** | Counts were R32-era (1604/7081 → **2576/11015/31 batches**). The `slack` column and query **Q7 are dead** — flag and `authorityUsedFrac` deleted; Q7 rewritten as a tombstone explaining *why* (a "fraction used" that read 0.977–1.084) so nobody re-derives it. Added gotcha **9b**: `WHERE dmgFrac = 0` selects everything — 641,555 rows, 0 non-zero, 8 known damage aborts. |
| `R21`, `R28`–`R33`, `R36`, `R37-FINDINGS.md` | **CURRENT** (historical) | All eight are dated, version-stamped batch records. Left as written; `SESSION-2026-08-02.md` §3.2 is the index of which specific lines are refuted, and duplicating it here would create a second authority. |
| `R39-A` … `R39-F` | **CURRENT** | 2026-08-02, the newest evidence base. `R39-E-alpha.md` §3 is the **source of the TASK #64 correction** and its table was independently reproduced against `captures.db` during this audit — it is right. |
| `R39-stol.md` | **RENAMED** (was `R40-stol.md`) | Title said R40; body already said "Run tag `R39`". See below. |
| `R39-rotor.md` | **RENAMED** (was `R40-rotor.md`) | Same. |
| ~~`R40-stol.md`, `R40-rotor.md`~~ | **stubs, now REMOVED (v1.0.0)** | They were redirects for one working session. The follow-up below was done: **44** inbound references across `ChaseController.cs`, `Recording.cs`, `scorecard.py`, `check-card.py`, `test-helo-probe-order.py`, `R40-metric-repair.md` and `SESSION-2026-08-02.md` were repointed to the `R39-*` names and both stubs deleted. No `cards/*.json` referenced them — the card agent had already written `R39-rotor.md`. |
| `R40-metric-repair.md` | **CURRENT** | Genuinely v0.99.1/R40-era work and not a batch document at all (a corpus-wide re-score). **Deliberately not renamed** — see below. |
| `GATE-CHATTER-FINDINGS.md` | **CURRENT** (closed) | Clean negative, kept for reproduction; §5a is the origin of the v0.85 fix. |
| `LOOP-AUDIT-FINDINGS.md` | **CURRENT** (historical) | R21-era corpus, and it **states its own coverage holes up front** — the model the rest of the repo should copy. |

### `harness/`, `plans/`

| file | verdict | reason / evidence |
|---|---|---|
| `harness/WTM-Range/README.md` | **UPDATE** | **The only file in the repo with dead links** — cited `plans/instructor-feedback-loop.md` §5.1 and `plans/research/research-D-batch.md` §8, neither of which exists. Repointed to the executable authority (`debugtests/check-mission.py`) plus `CLAUDE.md`, and the surviving counter-intuitive invariant spelled out inline: **isolation is not an empty faction list** (`EnsureFactionExists` inserts a default faction with `AIAircraftLimit = 6`). |
| `plans/drone-loadout-seam.md` | **CURRENT** | Research note, grades every claim VERIFIED/INFERRED/UNKNOWN. Had **zero inbound references** and was at risk of being lost — now cross-referenced from `LAW-CHARACTERIZATION.md` §7 `#19`, which it is the research for. |
| `plans/multi-card-queue.md` | **CURRENT** | Self-maintaining: its header table already records P1/P3/P4 shipped in v0.99.1, P2 in v0.98.1, P5 open, *with the differences from plan*. |
| `plans/next-card-grid.md` | **SUPERSEDED** | Batches **1–8 all flew** in R39; only Batch 9 remains. Per-batch outcome table added, several premises having been refuted by their own results. Also carries half the **TASK #64** fix. |

> `plans/` is git-ignored. It **does** exist in this worktree, so it was audited and edited; a fresh
> checkout will not have it, and nothing outside `plans/` depends on it.

---

## TASK #64 — the wrong R35 `alpha-steps` figure

**The claim, in two places:** *"R35's `alpha-steps` returned `aoaAboveCeilingPct` = 0.0 on all 8
airframes, peaks 5.7–16.6° against ceilings 8.5–23°"* — `plans/next-card-grid.md` (§0 and Batch 3) and
`LAW-WEAKNESS-MAP.md` (W4).

**It is false.** Corrected figure, from `debugtests/R39-E-alpha.md` §3 and **independently reproduced
during this audit** against `captures.db` (R35, card `alpha-steps`, `tag <> 'arm'`, `GROUP BY
airframe`) — the table matched to the digit:

| airframe | `aoaLimiterActivePct` mean / max | `aoaAboveCeilingPct` max | `aoaPeakOverCeiling` max |
|---|---|---|---|
| Darkreach | 73.6 / 96.3 | **5.000** | **1.029** |
| trainer | 70.0 / 84.4 | **4.375** | **1.024** |
| EW1 | 54.3 / 57.5 | 0.0 | 0.794 |
| FastBomber1 | 41.1 / 71.3 | 0.0 | 0.930 |
| VTOLTrainer1 | 27.2 / 56.3 | 0.0 | 0.820 |
| SmallFighter1 | 10.9 / 17.5 | 0.0 | 0.726 |
| Fighter1 | 6.0 / 8.8 | 0.0 | 0.733 |
| Multirole1 | 0.0 / 0.0 | 0.0 | 0.601 |

**7 of 8 on the limiter, 2 of 8 past the ceiling.** The "8 airframes" part was right (R35 flew
`alpha-steps` on 8 of its 10 keys); the **0.0** was wrong.

**Why it mattered more than a number.** It carried an inference — *"8000 m / 250 m/s cannot reach the
alpha regime, so the fix is lower q"* — and that inference is **refuted**. Both cards flew the same
entry condition and only `alpha-sweep` missed the ceiling, so the difference is **demand shape, not
q**: an azimuth demand loads the wing only through bank, clamped at 72° ⇒ n = 3.24. Fixed in both
files as dated correction blocks with the withdrawn inference struck through, not silently edited.

## The R40 naming collision

**Evidence, from the capture index — not from the documents:**

```
python debugtests/index-captures.py --query \
  "SELECT run_tag, mod_version, card, COUNT(*) n, MIN(started) FROM captures
    WHERE run_tag IN ('R39','R40') GROUP BY run_tag, card"
```

| run_tag | mod_version | card | n | first capture |
|---|---|---|---|---|
| R39 | 0.98.1 | `rotor-bob` | 24 | 2026-08-02 10:30 |
| R39 | 0.98.1 | `rotor-hover` | 24 | 2026-08-02 09:40 |
| R39 | 0.98.1 | `stol-steps` | 40 | 2026-08-02 09:30 |
| R39 | 0.98.1 | `stol-sweep` | 13 | 2026-08-02 09:34 |
| **R40** | **0.99.1** | `alpha-pullup` / `place-deflect` / `place-noop` | 109 | 2026-08-02 14:16 |

Corroborated by the capture filenames in `<game>/BepInEx/`, which carry the runtag inline —
`mouseaim-rec-v0.98.1-**R39**-d57-Fighter1-311-stol-steps-…csv` — and by **both documents' own opening
lines**, which already read "Run tag `R39`" and "48 captures, R39 run tag". Only the titles were wrong.

**Renamed** `R40-stol.md` → `R39-stol.md`, `R40-rotor.md` → `R39-rotor.md` (via `git mv`, history
preserved), each with a dated note at the top.

**Redirect stubs left at the old paths, deliberately.** ~30 inbound references live in files that were
out of scope for this pass: `ChaseController.cs`, `Recording.cs`, `ScenarioPlayer.cs`, `scorecard.py`,
`check-card.py`, `test-card-declared.py`, `test-helo-probe-order.py`, `cards/rotor-hover.json`,
`cards/rotor-bob.json`, and ~20 in `SESSION-2026-08-02.md` — which two agents were reading live. A bare
rename would have stranded every one of them, which is worse than the original problem: a title that
disagrees with its own first line is self-correcting, a reference to a missing file is not.
**Follow-up for whoever owns the code:** repoint those references and delete the two stubs.

> **DONE in v1.0.0.** 44 references repointed across seven files — `ChaseController.cs`,
> `Recording.cs`, `debugtests/scorecard.py` (13), `debugtests/check-card.py` (5),
> `debugtests/test-helo-probe-order.py`, `debugtests/R40-metric-repair.md` and
> `debugtests/SESSION-2026-08-02.md` (22) — and both stubs deleted. Two of the files listed above
> turned out to carry **no** reference by the time this ran: `ScenarioPlayer.cs`,
> `test-card-declared.py` and both `cards/rotor-*.json` had already been written against the `R39-*`
> names. Note that the citations were not all *links*: most are prose section references of the form
> `R40-rotor 1d`, which a link checker would never have caught.

**`R40-metric-repair.md` deliberately NOT renamed.** It is not a batch document — it is a corpus-wide
re-score of 2,366 captures that shipped *in* v0.99.1, the R40 plugin version. Its "R40" prefix denotes
the session, not a capture set, so unlike stol/rotor it is not making a false claim about which
captures it describes. Renaming it would have carried the same ~15-reference blast radius for no
correctness gain.

## Numbering — where the two schemes disagree

Full detail is now in `LAW-CHARACTERIZATION.md` §7's RECONCILIATION block, which is where a reset agent
will look. In short: §7 tops out at **#46** and declares itself the sole authority; the working task
list reached **#80**; `CHANGELOG.md` and the session doc cite `#51`, `#61`, `#63`, `#70`, `#71` and
"ledger #12", none of which exist in §7; and the findings docs carry a third, document-local scheme
(`#53a–c`, `#54a–e`, `#55a–g`). **The sharp edge: §7's `#46` is closed and verified passing, while the
task list's `#46` is open — one number, two items.**

## Left explicitly UNVERIFIED

- **The number → content mapping for twelve open tasks** — `#47`, `#59`, `#62`, `#66`, `#72`–`#80`.
  No disk evidence of their content exists. Recorded as a count, not guessed at.
- **`#53` / `#54` / `#55`** are *probably* the R36 / R37 / R39 document follow-up bundles, on the
  strength of the number matching the scheme-C prefixes. Marked as an inference, not a mapping.
- **`SESSION-2026-08-02.md` §3.2 says `CAPTURES-DB.md` "should stop offering `dmgFrac` as a filter".**
  It never offered one — the string `dmgFrac` did not appear in that file. A warning was **added**
  instead, since the column is real and a reader could reach for it unprompted.
