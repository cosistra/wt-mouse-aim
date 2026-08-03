# `captures.db` — the flight-capture index, for an agent who has never seen this repo

`debugtests/captures.db` is a SQLite index over every recorder CSV the mod has ever written
(**2576 captures / 11015 segments / ~2.12M recorder rows / 31 batches as of R40, 2026-08-02** — the
older "1604 / 7081 / ~954k as of R32" figures are retired; re-derive with `--stats` rather than
trusting any number written here). It exists so a question spanning
batches — *"does this effect hold in R28, R29 AND R30?"* — is one `GROUP BY`, not three tool runs
stitched into prose.

It is **derived and gitignored**. The CSVs under `<game>/BepInEx` are the source of truth; the `.db`
is rebuilt in ~30 s and re-indexed warm in ~0.3 s over the whole corpus.

```bash
python debugtests/index-captures.py "<game>/BepInEx"   # build / refresh (idempotent, ~0.3 s warm)
python debugtests/index-captures.py --stats            # WHAT IS IN HERE — run this first
python debugtests/index-captures.py --check R29        # is that batch complete and intact?
python debugtests/index-captures.py --query "SELECT …" # read-only by default
```

## The two rules

1. **Every metric in here comes from `scorecard.py`.** `index-captures.py` imports it and stores what
   `score_run()` returns. Nothing is re-derived — not a metric, not the RAILED threshold, not the
   tag→type rule. If a metric changes there, re-index and the database follows. Do **not** "fix" a
   number with SQL; fix it in `scorecard.py`.
2. **Metrics are SPARSE by segment type, and columns are DYNAMIC.** A corpus-wide `avg(metric)`
   silently averages whatever handful of rows happen to have it. See
   [the matrix](#the-metric--segment-type-matrix) and [the NULL idioms](#the-three-null-idioms) —
   both traps return a plausible number rather than an error.
3. **A non-NULL pointing metric can still be float grain.** `off` resolves no finer than 0.0198°, and
   `terminalOffDeg` is anchored at the segment END so it means different things on an 8 s and a 30 s
   leg. Both produce a confident four-decimal number that orders nothing —
   [the resolution floor](#the-resolution-floor--the-trap-that-survives-every-null-check).

---

## Start here: the three built-in commands

| Command | Answers |
|---|---|
| `--stats` | Totals, one row per batch (mod version, captures, airframes, cards, aborts, `n_cols` era, whether raw rows are materialized), an `n_cols` histogram, per-airframe counts, parse-warning count. |
| `--check [RUNTAG]` | Is a batch what you think it is: per-(airframe) capture counts with an **outlier flag**, `rec`-number gaps, aborted captures **with their stop reasons**, parse warnings, unknown segment tags. With no RUNTAG it scans every batch and prints only flagged lanes. |
| `--diff RUNA RUNB [--metric M] [--tag T]` | Per `(airframe, card, tag)`: `mean ± stdev%` in both runs and the ratio B/A. Railed and `arm` segments excluded. This is the question the index was built for. |

`--check` exists because a dead lane is invisible in every aggregate view. Real example — R29 flew ten
lanes; nine flew 48 captures each and one did not:

```
=== R29 ===  captures 441  airframes 10  cards 6  aborted 1  rec 1..441  v0.93.0  2026-07-30 20:00
airframe       caps  cells  min/cell  max/cell  firstRec  lastRec  abort  flag
-------------  ----  -----  --------  --------  --------  -------  -----  --------------------------------
Darkreach      9     6      1         2         10        90       1      ** 9 vs median 48 (19%)  ** STOPPED EARLY: last rec 90 of 441
CAS1           48    6      8         8         6         438      0
…
  aborted captures:
Darkreach  oblique-2-c  90   abort: aircraft gone
```

Any query that groups R29 by airframe will happily report a `Darkreach` mean over 30 segments
next to nine 192-segment lanes. Run `--check` before trusting a batch.

---

## The batch index — what each run tag flew, and where its conclusions live

**There are no per-batch findings documents any more.** They were consolidated 2026-08-02 (see
[the doc convention](#the-doc-convention--batch-analyses-update-standing-docs) below); the
conclusions live in the standing docs, and the raw evidence lives in `captures.db` plus
`debugtests/archive/`. This table is how a citation of the form `R39-rotor 1d` or
`R28-FINDINGS.md §3.2` — of which ~60 survive in `.cs` / `.py` / `cards/*.json` comments — still
resolves.

**Read a batch citation as "the finding, in the standing doc named here".** The letters/numbers in
those citations (`H7`, `§5a`, `1d`, `F2`) were document-local and are gone; the finding is not.

| run tag | mod | what flew | where its conclusions live now |
|---|---|---|---|
| **R21** | v0.83 | `fixedwing-v2`, sustained `turn360`, 10 replicates | `LAW-LEDGER.md` **S1–S3** (bank clamp is a bystander; `lateralHold` rails the bank pipeline; `_iPitch` dead outside the fine cone), **X8** (the clamp is NOT what holds the 9.4° lag), **L7** (`predFloor` binds 100%) |
| **R22–R25** | v0.87–0.89 | Gates A–D, the instrument-validation ladder | `LAW-LEDGER.md` **§1.1 I1–I3** (now carrying the gate evidence inline), **X3** (aoaTrim theory disproved), **X4** (the retracted `ctrlReset` claim), **X11** (#23 "harmless" retired) |
| **R26 / R27** | v0.90–0.92 | first drone batches | `LAW-LEDGER.md` **I4** (#29/#30/#37 instrument defects), **X7** (`aoaLimiterActivePct` is non-zero at corpus scale) |
| **R28** | v0.93 | `oblique-12-*`, 8 airframes, 384 caps | `LAW-LEDGER.md` **D1, D10, I6, I7, L10, X10, X13, X14**. The down-step penalty's first measurement. Cited by `cards/oblique-12-fwd.json` / `-rev.json` as "F1" |
| **R29** | v0.93 | `oblique-*`, 10 airframes, 441 caps | `LAW-LEDGER.md` **D1, D5, G1–G7, L1, L9, L11, X13, X15, O2** |
| **R30** | v0.94 | `oblique-12-fwd`/`-rev` crossed design, 48 caps | `LAW-LEDGER.md` **D2, D3, D4, L8, X10** — direction, not position; the crossed control |
| **R31** | v0.94 | `BelowAlignSuppress` sweep, 2 cards, 96 caps | `LAW-LEDGER.md` **D8–D12, I5, X12**, and the **BATCH SUSPECT** block at the head of the ledger (multi-card ABBA confound) |
| **R32** | v0.95 | `darkreach-05`, 63 caps, 18 departures | `LAW-LEDGER.md` **K1–K5, P1–P3, L3–L5, X1, X2, X17, X18**; `LAW-CHARACTERIZATION.md` §7 **#45**, **#23**; `GENERALITY-REVIEW.md` finding 18 |
| **R33** | v0.96.0 | `oblique-6-c`, 10 airframes, 77 caps | `LAW-LEDGER.md` **G2, G4, K6, L6, O4**; gotcha 12 below (the `gJitterG` r = 0.886 figure) |
| **R35** | v0.96.2 | `oblique-6-dwell` + `alpha-steps`, 186 caps | `LAW-LEDGER.md` **I8** (float-grain distance law); the corrected `alpha-steps` figure is in `LAW-WEAKNESS-MAP.md` W4 |
| **R36** | v0.97.1 | `oblique-6-dwell` ×2 launches, 64 caps (32 usable) | `LAW-LEDGER.md` **I8, I9** — the distance law without the airframe confound, and `fixedWindowOffDeg` as the metric `terminalOffDeg` was pretending to be |
| **R37** | v0.97.2 | `oblique-6-dwell`, 125 caps, the clean batch | `LAW-LEDGER.md` **I9, I10** — the placement kill fixed 109/109; the ranking reproduces at ρ +1.000; 74% of legs at the resolution floor |
| **R39** | v0.98.1 | five cards, 411 caps — the big batch | See the R39 sub-rows below. Six analyses (`A`–`F`) plus rotor and STOL |
| R39 · `A` ranking | | `oblique-6-dwell` throttle contrast | `LAW-LEDGER.md` **G8** — the airframe spread survives at matched speed |
| R39 · `B` card validity | | same, criterion B/D | `LAW-LEDGER.md` **X19** (`oblique-6-dwell` retired as a ranking instrument), **X20** (thrust-to-weight attribution refuted); `LAW-WEAKNESS-MAP.md` W1 |
| R39 · `C` settle mode | | Darkreach azimuth mode | `LAW-LEDGER.md` **K7** (V-dependent, Darkreach-only, f ∝ V^0.305), **X21** (the wobble detector measured entry transients) |
| R39 · `D` sustained A/B | | `e3-marker-ff`, `e2-rel-turn-lead`, 121 caps | `LAW-LEDGER.md` **D13** (the first above-floor steady-state pointing measurement), **A1** (`MarkerRateFeedForward`), **X22** (`RelativeTurnLead` spent); `GENERALITY-REVIEW.md` finding 16 |
| R39 · `E` alpha | | `alpha-sweep`, 61 caps | `LAW-LEDGER.md` **X23** (the card cannot reach the alpha regime), **N1** (the AoA guard's 40% onset spread from two absolute constants); `cards/ALPHA-CARD-REDESIGN.md` |
| R39 · `F` Darkreach damage | | ledger #51, third reproduction | `LAW-CHARACTERIZATION.md` §7 **#51**; `LAW-LEDGER.md` **X24** (`dmgFrac` structurally zero; `0.114` is subtree size, not four events) |
| R39 · rotor | | `rotor-hover` + `rotor-bob`, 48 caps | **Superseded by R41** for the shipped law. `LAW-LEDGER.md` **H1** (the v0.58 branch never executed), **H2** (the hover bistability). Cited from `scorecard.py` (13×), `check-card.py`, `ChaseController.cs`, `Recording.cs`, `ScenarioPlayer.cs`, `cards/rotor-*.json` |
| R39 · STOL | | `stol-steps` + `stol-sweep`, 53 caps | `LAW-LEDGER.md` **X25** — the card declared 90 m/s and flew 340–381; it is a second high-q dataset, not STOL data |
| **R40** | v0.99.1 | `alpha-pullup`, `place-noop`, `place-deflect`, 109 caps | `LAW-LEDGER.md` **N2** (the law never backs off — commanding into the ceiling on 100% of gate-biting samples), **X26** (the #51 phenomenon did not reproduce; 32 clean placements) |
| **R40** · metric repair | v0.99.1 | corpus-wide re-score, no flying | The **two corpus-wide invalidations** at the head of `LAW-LEDGER.md`, and the metric definitions in this file. Cited from `ScenarioPlayer.cs` |
| **R41** | v1.0.0 | seven fixed-wing cards + three rotor, 451 caps | `LAW-LEDGER.md` **A1** (feed-forward off the rail), **A2** (the `e1*` nulls), **H3–H5** (rotorcraft), **I11** (the ring geometry), **X27** (replicate 1 is a different flight condition). Cited from `compare-runs.py` |
| **Discord v0.68 field bundle** | v0.68.0 | two users, six recordings, not a batch | `LAW-LEDGER.md` **X28** (the locale formatting bug, fixed v1.0.1) and **O11** (the high-q roll limit cycle); `GENERALITY-REVIEW.md` finding 5. Cited from `WTMouseAimPlugin.cs`, `Recording.cs` |

**Raw evidence** for R28–R37 is in `debugtests/archive/R<n>-<date>/` (CSVs, sidecars, logs). Later
batches are archived out of `<game>/BepInEx` with
`python debugtests/index-captures.py "<game>/BepInEx" --archive debugtests/archive --run R<n>`.

### The doc convention — batch analyses UPDATE standing docs

**Do not create a new `R##-*.md`, `SESSION-*.md` or `*-FINDINGS.md`.** That habit produced 25 files
that disagreed with each other and with the code, and cost more to keep straight than they were
worth. A batch analysis lands as edits to the standing docs:

| what you found | where it goes |
|---|---|
| a claim you can now believe, or one you must stop believing | `LAW-LEDGER.md` — ESTABLISHED / PLAUSIBLE / **REFUTED** / OPEN, one line, with batch + n + effect size |
| an open action item | `LAW-CHARACTERIZATION.md` §7 — the durable backlog |
| a ONE-LAW violation (a constant that should be a probe) | `GENERALITY-REVIEW.md` findings |
| a ranked weakness, or a hypothesis to stop re-proposing | `LAW-WEAKNESS-MAP.md` (W-items, and its REFUTED / DO-NOT-RE-PROPOSE list) |
| a schema/metric/SQL trap | this file |
| a card's validity verdict | the card's own `note` field, plus `cards/README.md` |
| what shipped | `CHANGELOG.md` (append-only) |

Then **add a row to the batch index above** naming the run tag and where its conclusions went, and
archive the raw captures. The ledger line is the finding; the CSVs are the evidence; nothing in
between needs to exist. If a batch produced nothing that changes a standing doc, that is a result —
record it as one line in the index and move on.

---

## Tables

### `captures` — one row per CSV (86 columns today; the `sc_`/`entry_`/`ov_` ones are dynamic)

Idempotency key is `file` (the basename). Re-indexing a capture keeps its `id`, so a `rows` foreign
key stays meaningful.

| column | type | provenance |
|---|---|---|
| `id` | INTEGER PK | synthetic |
| `file` | TEXT UNIQUE | CSV basename — **the idempotency key** |
| `path` | TEXT | absolute path at index time (may be stale if `<game>` moved) |
| `mtime`, `size` | REAL, INTEGER | `os.stat` — the (mtime, size) pair that makes a warm re-index free |
| `run_tag` | TEXT | CSV header `run=` (normalised to `R29`), else `-(R\d+)-` in the filename. **NULL on 63 legacy captures.** |
| `mod_version` | TEXT | CSV header line (`# mouseaim recording  v0.94.0 …`) |
| `session` | TEXT | CSV header `session=` — the process; `rec` restarts per session |
| `rec` | INTEGER | CSV header `rec=` — per-process file counter, orders captures **in time** |
| `drone` | INTEGER | `# drone N` header line — the lane. **NULL = hand-flown** (175 captures) |
| `replicate` | INTEGER | **COMPUTED** — ordinal within `(session, drone, card)` by `rec`. Not in any artifact: `ScenarioPlayer.RunIndex` is never written to the CSV. |
| `airframe` | TEXT | **sidecar** `jsonKey` — what `compare-runs.py` groups on and refuses to pool across. NULL without a sidecar (119 captures) |
| `aircraft` | TEXT | `# aircraft 'FS-12'` — the unit NAME, not the key. Do not group on this. |
| `card` | TEXT | `# card` header. NULL on 122 hand-flown/ad-hoc captures |
| `arm` | INTEGER | regex `arm=(\d+)` off the `# config` line. **NULL when the capture is not part of an A/B** (only 107 captures have one) |
| `arm_knob` | TEXT | regex `armKnob=` off `# config` — which lever the ABBA schedule swept |
| `started` | TEXT | `# started` wall clock, LOCAL. The only reliable **time ordering** across batches |
| `utc` | TEXT | sidecar `utc` |
| `n_rows` | INTEGER | **COMPUTED** — sum of segment `samples` |
| `n_cols` | INTEGER | **COMPUTED** — fields in the CSV header line. **This is the recorder-era key** (see below) |
| `stop` | TEXT | the `# stop` footer, verbatim (carries the abort reason) |
| `aborted` | INTEGER | scorecard's `provenance()` |
| `config` | TEXT | the `# config` line verbatim — the law's knobs **as flown** |
| `entry_note` | TEXT | the `# entry` line verbatim — the per-replicate reset provenance |
| `ov_note` | TEXT | the `# override` line verbatim — knobs **the card** pinned for itself. **0 real captures today** (no shipped card uses `config`); covered by the selftest only |
| `parse_warn` | TEXT | scorecard's own stderr for this file. **NULL on every row today** — a non-NULL here means dropped rows |
| `sc_*` (45) | mixed | **sidecar** `<capture>.airframe.json` scalars, key-for-key. Absent → NULL |
| `entry_*` (9) | mixed | parsed out of `# entry`. `a->b` becomes `_from`/`_to` |
| `ov_*` | mixed | parsed out of `# override`; `/` in the key becomes `_` (`Control/BelowAlignSuppress` → `ov_Control_BelowAlignSuppress`) |

Dynamic columns are declared `NUMERIC`, so SQLite's affinity keeps `0.35` a REAL and `true` a TEXT.
They are added **on demand**: a new sidecar or entry field appears as a column on the next index run
instead of silently vanishing. The flip side is that **a column only exists if some indexed capture
produced it** — `no such column` from a query is usually "nothing in the corpus has that yet".

Notable `sc_*` (all fail-soft on the mod side, so a NULL is "could not read it", never zero):
`sc_massKg`, `sc_cornerSpeed`, `sc_turningRadius`, `sc_aircraftGLimit`, `sc_maxThrustN`,
`sc_fuelKg`, `sc_wingAreaTotal`, `sc_dragAreaTotal`, `sc_alphaLimiter`, `sc_gLimitPositive`,
`sc_maxPitchAngularVel`, `sc_infoStallSpeed`, `sc_infoMaxSpeed`, `sc_loadoutCount`,
`sc_loadoutMassKg` (both **computed** from the loadout array), `sc_loadout` / `sc_fbwParameters`
(JSON text — use `json_extract`). The Cl/Cd curves (`airfoils`, `airfoilAlphaDeg`) are dropped.

**`sc_maxSpeed` is `aircraftParameters.maxSpeed`, a NORMALIZER that reads a flat 600 for every fast
jet.** For a real Vmax use `sc_infoMaxSpeed`. See `AIRFRAMES.md` trap 5.

### `segments` — one row per (capture, segment) (62 columns: 12 fixed + 50 dynamic metrics)

> **Three metric columns are newer than this database.** `fixedWindowOffDeg`, `settleTime95` and
> `offFloorPct` (2026-08-01, see [the resolution floor](#the-resolution-floor--the-trap-that-survives-every-null-check))
> exist in `scorecard.py` but **appear in SQLite only after a re-index** — `no such column` here means
> "re-index", not "never flown". It must be `--rebuild`: the warm path skips any capture whose
> `(mtime, size)` is unchanged, and a metric change moves neither, so a plain re-run picks up nothing.
> ```bash
> python debugtests/index-captures.py "<game>/BepInEx" debugtests/archive --rebuild   # ~30 s
> ```

| column | type | provenance |
|---|---|---|
| `capture_id` | INTEGER FK → `captures.id` | `ON DELETE CASCADE` |
| `seg_index` | INTEGER | order within the capture (PK is the pair) |
| `tag` | TEXT | the `segTag` column — the card's own tag (53 distinct) |
| `type` | TEXT | `scorecard.infer_type(tag)` via `TAG_TYPE_RULES`. **Decides which metrics exist** |
| `samples` | INTEGER | rows in the segment |
| `duration_s` | REAL | last `t` − first `t` |
| `excluded` | INTEGER | `type = 'arm'` — the settling window, **no metrics at all** (1482 of 7081 rows) |
| `railed` | INTEGER | `scorecard.is_railed()` — sat on a limit ≥90% of samples. **Its metrics are no signal, not a score** (285 rows) |
| ~~`slack`~~ | INTEGER | **DEAD — no longer written (v0.99.1).** The SLACK flag and the `authorityUsedFrac` it thresholded were deleted: that quantity was `mean\|bank\|/maxBank`, not a fraction of authority, and it exceeded 1.0 in practice. The column survives in databases built before the change and holds its 8 historical rows; a fresh index leaves it NULL. **Do not filter on it.** See `R40-metric-repair.md`. |
| `unknown_tag` | INTEGER | the tag matched no `TAG_TYPE_RULES` entry → scored with the generic set only (2 rows) |
| `warnings` | TEXT | scorecard's RAILED/SLACK/unknown-tag prose, newline-joined. NULL = clean. **Match on the flags above, never on this prose** |
| `skipped` | TEXT (JSON) | `{metric: reason}` for metrics that could not be computed. See [NULL idiom 3](#3-not-applicable-vs-not-measured--segmentsskipped) |
| 50 metric columns | REAL | **`scorecard.score_run()`**, named exactly as scorecard names them |

### `rows` — raw recorder rows, opt-in

`--with-rows RUNTAG` materializes ONE batch. Columns: `capture_id`, `i` (row ordinal), then one
column per CSV column (66 today). Everything else stays in CSV: all ~1.1M rows would be ~500 MB of
mostly-unread steady state. **Today only R30 is materialized** (48 captures / 29,199 rows) — check
`--stats`'s `--with-rows` column before writing a `rows` query, and materialize what you need.

### `cards`, `card_airframes` — the card grid, opt-in

`--cards cards/` loads `cards/*.json` as dimension tables so *"which grid cells have we NEVER
flown?"* is a `LEFT JOIN`. `cards.card` is the **file basename** (the id the mod binds and the
`# card` header carries), so it joins straight to `captures.card`. `card_airframes` expands the
comma-list `airframe` field, one row per lane — and **only for cards whose list is real jsonKeys**:
an empty field means "whatever `Cfg.DroneAirframe` says" and prose means the card predates v0.90.
`cards.problems` carries `scorecard.card_setup_problems()`; NULL is clean.

---

## The metric × segment-type matrix

Counts are live (`--stats` totals). `—` means **the metric does not exist for that type at all**;
a number below the type's `n` means it exists but was skipped or is conditional.

| metric (group) | oblique_step<br>4894 | sustained_turn<br>241 | micro/az/el_step<br>299 | fine_track<br>16 | reversal/astern<br>25 | unknown<br>124 | arm<br>1482 |
|---|---|---|---|---|---|---|---|
| `aoaPeakDeg` `gPeak` `gSustained` `gJitterG` `aoaLimiterActivePct` | all | all | all | all | all | 124 / 61¹ | — |
| `bankClampActivePct` `bankDemandExcessDeg` `turnRateCapActivePct` `turnRateDemandRatio` (~~`authBank` `authAoa` `authStick` `authorityUsedFrac` — **all four DELETED v0.99.1**~~) | all | all | all | all | all | 120–124¹ | — |
| `blendRailPct` | all | 217¹ | 64¹ | — ¹ | — ¹ | 1¹ | — |
| `rmsPointingErrorDeg` `minOffDeg` `terminalOffDeg` `entryAzSign` `offFloorPct` | all | all | all | all | all | all | — |
| `fixedWindowOffDeg` | ≥8 s legs only³ | all | **—**³ | all | all | partial³ | — |
| `settleTime95` | settled legs only⁴ | partial⁴ | partial⁴ | partial⁴ | partial⁴ | partial⁴ | — |
| `overshootAzDeg` `overshootElDeg` | partial² | partial² | partial² | all | partial² | partial² | — |
| `settleBandDeg` `demandDeg` `riseTime90` `settleTime` `overshootDeg` | partial² | **—** | partial² | **—** | settle/overshoot only | **—** | — |
| `meanTurnRateDegS` `deltaTAS` `deltaEnergyHeightM` | **—** | all | **—** | **—** | **—** | **—** | — |
| `stickFlipRate{P,R,Y}` `wobbleEpisodes*` | all | **—** | **—** | all | all | **—** | — |
| `wobbleFreqHz*` | rare² | **—** | **—** | rare² | rare² | **—** | — |
| `rollCmdMedian` `yawCmdMedian` `bothActivePct` `rollYawOpposedPct` `rollYawAllocFrac` `rollBlendMean` | all | **—** | **—** | **—** | **—** | **—** | — |
| `pitchAuthorityMedian` `pitchAuthorityAntiPhaseFrac` | **—** | **—** | **—** | **—** | 24 of 25 | **—** | — |

¹ era / missing column — see NULL idiom 2 and 3.  ² conditional on the segment's own shape (no
overshoot happened; the step was too short; `wobble_scan` only emits a frequency when it finds an
episode). Both are legitimate NULLs; `count()` them.
³ `fixedWindowOffDeg` is the mean `off` over a window **anchored at segment start** (7–8 s,
`scorecard.FIXED_WINDOW_START_S`), so it is NULL — with a reason in `skipped` — on any segment
shorter than 8 s. That is most `micro_step`/`az_step` segments and **every `oblique_step` before
R35**, whose legs are 8 s exactly (measurable) or shorter (not). It is also NULL when the window mean
lands under the resolution floor. ⁴ `settleTime95` is NULL when the segment never settles, which is
not rare and **not random**: over R35's 384 scorable 30 s legs it is NULL on 43%, and the censoring
tracks distance to the world origin (near lanes 192/192 settled, far lanes 26/192). `count()` it
beside `avg()` or you are averaging the survivors.

**Segment types with metric columns that do not exist yet**, because nothing has flown them:
`alpha_step` / `alpha_hold` (`aoaAboveCeilingPct`, `aoaCeilDeg`, `aoaPeakOverCeiling`,
`aoaRecoverActivePct`, `commandIntoCeilingPct`, `qSchedMin`, `gateMinUp`, `gateMinDn`),
`hover_hold` (`positionRMSM`, `driftRateMS`), `bobup` / `translate` (`demandM`, `overshootM`),
`transition` (`altExcursionM`). Those cards exist in `cards/` and have never been run — see
[cookbook Q10](#q10-which-grid-cells-have-we-never-flown-needs---cards). `aoaAboveCeilingPct` is one
of scorecard's four RAIL_METRICS, so `segments.railed` already accounts for it; you just cannot
`SELECT` it today.

### The idiom this matrix exists to force

```sql
-- ALWAYS: count(metric) beside avg(metric), never avg() alone.
SELECT s.type, count(*) segs, count(s.meanTurnRateDegS) scored, avg(s.meanTurnRateDegS) mean
  FROM segments s GROUP BY 1;
```

`avg()` ignores NULLs. `count(*)` counts rows. If the two disagree, the mean is over a subset —
and a corpus-wide `avg(meanTurnRateDegS)` is 241 sustained turns hiding inside 7081 segments. The
`scored` column is the difference between an answer and a coincidence.

---

## The three NULL idioms

### 1. Sparse by segment type
Covered above. Filter `s.type = …` or `s.tag = …` **explicitly**; never let the GROUP BY decide for
you which types happened to have the column.

### 2. Recorder era — filter on `captures.n_cols`
The CSV grew from 38 to 64 columns across the corpus, and a metric needing a column that did not
exist yet is simply absent:

| `n_cols` | captures | mod versions | runs |
|---|---|---|---|
| 38 | 63 | (pre-run-tag) | — |
| 44 | 20 | 0.64.0 | R1 |
| 45 | 36 | 0.65.0–0.67.0 | R2, R3 |
| 54 | 2 | 0.69.0 | R10 |
| 56 | 25 | 0.71.0–0.76.0 | R11–R18 |
| 57 | 5 | 0.77.0 | R19 |
| 58 | 11 | 0.79.0 | R20, R21 |
| **64** | **1442** | 0.87.0–0.94.0 | R22–R32 |

```sql
-- The trap and the fix in one query: 423 old segments contribute nothing to this mean.
SELECT c.n_cols >= 64 modern, count(*) segs, count(s.blendRailPct) scored,
       round(avg(s.blendRailPct), 2) mean
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE s.excluded = 0 GROUP BY 1;
--  modern  segs  scored  mean
--  0       423   0       None      <- pre-v0.85: no bWt column, so no blendRailPct at all
--  1       5176  5176    5.57
```

`n_cols >= 64` is the practical "modern capture" filter. One caveat: `frameMs` exists from v0.86 but
**means the fixed step, not the frame, until v0.92.1** — captures from R22–R27 read a constant
16.70 ms. Filter `mod_version >= '0.92.1'` for anything about frame hitches.

### 3. Not-applicable vs not-measured — `segments.skipped`
When scorecard *could not* compute a metric it records why, as JSON, per segment. 423 segments carry
one today:

```sql
SELECT json_extract(s.skipped, '$.blendRailPct') why, count(*) n,
       group_concat(DISTINCT c.run_tag) runs
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE json_extract(s.skipped, '$.blendRailPct') IS NOT NULL GROUP BY 1;
--  missing column: bWt (pre-v0.85 capture)  423  R1,R2,R3,R10,…,R21
```

Reasons seen in the corpus: `missing column: bWt (pre-v0.85 capture)` (423),
`missing column(s): aoaGU/aoaGD` (63), `no cornerSpeed/gLimit on the '# fbw' header (pre-v0.55
capture)` (8), `segment too short (<2 samples)` (1). **A NULL metric with an entry in `skipped` is a
measurement that could not be taken; a NULL with no entry is a metric that does not apply.** Only the
first is worth chasing.

---

## The resolution floor — the trap that survives every NULL check

Every trap above is about a **missing** number. This one is about a number that is *present*,
*non-NULL*, printed to four decimals, and **is not a measurement**.

`off` is `Vector3.Angle(t.forward, aimDir)` — `acos(dot)` in float32, written `{off:0.00}`. Float32
spacing below `dot = 1.0` is `5.96e-8`, so the smallest non-zero angle it can return is
`sqrt(2·5.96e-8)` rad = **0.0198°**, and the first printed rung above zero is `0.02`. Proof rather
than inference: across 279k R35 oblique rows the value `0.01` **never occurs**, while `0.00` (43,285)
and `0.02` (15,122) both occur tens of thousands of times. `scorecard.OFF_FLOOR_DEG` (= 2 × that
quantum, 0.0396°) is the threshold below which an `off`-derived number carries no orderable signal.

**What it does to a ranking.** R35 (`oblique-6-dwell`, 30 s legs, six airframes flown on both a
near lane group and a far one — same batch, same card, only the distance to the world origin
differs) ranked by `terminalOffDeg`: near vs far Spearman **+0.03**. The same six cells ranked by
`rmsPointingErrorDeg`: **+1.00**. By the new `fixedWindowOffDeg`: **+1.00**. Terminal error was
ranking float grain — **94 of the 192 near-lane terminal windows read exactly 0.0000**, and three
airframes tied there.

```sql
-- The floor, in one query -- NEEDS A --rebuild first (three of these columns are newer than the db).
-- offFloorPct is the % of samples on the 0.00/0.02 rungs.
SELECT c.airframe, count(*) legs,
       sum(s.terminalOffDeg < 0.0396) at_floor, round(avg(s.offFloorPct),1) floor_pct,
       round(avg(s.terminalOffDeg),4) term, round(avg(s.fixedWindowOffDeg),4) fixedwin
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE c.run_tag = 'R35' AND s.type = 'oblique_step' AND s.railed = 0
 GROUP BY 1 ORDER BY fixedwin;
```

Reading rules, in order:

1. **`terminalOffDeg` is not deleted and not redefined** — 5,692 archived segments and every existing
   analysis key to it — but it is only a score when `terminalOffDeg >= 0.0396`. Below that, scorecard
   emits an `AT THE RESOLUTION FLOOR` warning into `segments.warnings`; **filter on the metric, never
   on that prose** (gotcha 9). On R35 it fires on 309 of 384 legs.
2. **`terminalOffDeg` is anchored at the segment END, so it is not one quantity across leg lengths.**
   An 8 s leg's terminal window scores a *mid-transient* and a 30 s leg's scores a settled residual.
   R35 settled no earlier than **9.0 s** on any of the 384 legs (median 15.5 s), i.e. every 8 s
   `oblique_step` in this corpus — 5,194 segments across 15 cards — ends before the response does.
   Use `fixedWindowOffDeg` to compare an 8 s batch with a 30 s one: R33-terminal vs R35-terminal
   correlates +0.10, R33-terminal vs R35-`fixedWindowOffDeg` +0.78.
3. **`settleTime95` is the metric `terminalOffDeg` was being used as a proxy for** — first `t` after
   which `off` stays inside `max(0.05°, 1.05 × terminalOffDeg)` for the rest of the segment, held at
   least 1 s. Its virtue is that on a leg that is still decaying it returns **NULL**, not a plausible
   wrong number. See matrix note ⁴ before averaging it.
4. `minOffDeg` is at the floor almost everywhere on modern captures (R35: five of six airframes
   average exactly 0.0000 on both lane groups). It answers "did it ever touch", not "how well".

---

## The six `sc_` twins — which one to join on

Six sidecar scalars duplicate a fixed column. **Always use the fixed one:**

| fixed (use this) | `sc_` twin | why the fixed one |
|---|---|---|
| `run_tag` | `sc_run` | `run_tag` is normalised `'R29'`; `sc_run` is the integer `29`. `WHERE sc_run = 'R29'` matches nothing |
| `mod_version` | `sc_modVersion` | comes from the CSV header, so it survives a **missing sidecar** |
| `session` | `sc_session` | same |
| `rec` | `sc_rec` | same, and it is typed INTEGER |
| `airframe` | `sc_jsonKey` | identical values (`airframe` is copied from it), but `airframe` is what's indexed and what `compare-runs.py` groups on |
| `utc` | `sc_utc` | same value; `utc` is the documented one |

**119 captures have no sidecar at all** (R1–R3 and the untagged legacy set): every `sc_*` is NULL
there, and so is `airframe`. `session` still works on all 119 and `mod_version`/`rec` on 56 of them,
because those come from the CSV header — the 63 oldest (`n_cols = 38`) predate the header carrying
them. `sc_csv` is the sidecar's own record of its CSV name — not a join key, use `file`.

---

## Cookbook

Every query below was **run against the live DB and works today** unless marked otherwise. Paste
them into `--query "…"` (read-only) or `sqlite3 debugtests/captures.db`.

`stdev(x)` and `median(x)` are registered by `index-captures.py` — SQLite ships neither. `stdev` is
the **SAMPLE** standard deviation (n−1), matching `compare-runs.py`'s `statistics.stdev` exactly, and
returns NULL below n=2. They are **not available in a bare `sqlite3` shell**; use `--query` for
anything using them.

#### Q1. Orientation — *works today*
```bash
python debugtests/index-captures.py --stats
python debugtests/index-captures.py --check          # every batch, flagged lanes only
```

#### Q2. Rank airframes by a metric within one batch — *works today*
```sql
SELECT c.airframe, count(s.terminalOffDeg) n, round(avg(s.terminalOffDeg),4) mean,
       round(stdev(s.terminalOffDeg),4) sd
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE c.run_tag = 'R29' AND s.type = 'oblique_step' AND s.railed = 0
 GROUP BY 1 ORDER BY mean;
--  trainer 192 0.0622 … SmallFighter1 192 0.2926 … Darkreach 30 2.605  <- n=30: the dead lane
```
**Swap the metric for `fixedWindowOffDeg` (or `rmsPointingErrorDeg`) before you believe an ordering
like that.** Several of those means are under the `off` column's 0.0396° resolution floor, where the
ranking is float grain — that is
[the trap this exact query walked into](#the-resolution-floor--the-trap-that-survives-every-null-check).

#### Q3. Does the effect hold across batches? — *works today*
```sql
SELECT c.run_tag, c.mod_version, count(s.terminalOffDeg) n,
       round(avg(s.terminalOffDeg),4) mean, round(median(s.terminalOffDeg),4) med
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE s.tag = 'obUL12' AND s.railed = 0
 GROUP BY 1,2 ORDER BY min(c.started);        -- ORDER BY started, NOT run_tag: 'R10' < 'R2'
```
`min(c.started)` is the only correct chronological order — run tags sort lexicographically.

#### Q4. A/B one lever inside a batch, by arm — *works today*
```sql
SELECT c.arm_knob, c.arm, s.tag, count(*) n, round(avg(s.terminalOffDeg),4) mean,
       round(stdev(s.terminalOffDeg),4) sd
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE c.run_tag = 'R31' AND c.airframe = 'Fighter1'
   AND c.arm IS NOT NULL AND s.excluded = 0 AND s.railed = 0
 GROUP BY 1,2,3 ORDER BY s.tag, c.arm;
```
`s.excluded = 0` is not optional — without it every `arm` window shows up as a row with `n=0` and a
NULL mean. Never pool across `airframe`: `compare-runs.py` refuses to, and so should you.

#### Q5. Noise floor per cell — *works today*
```sql
SELECT c.airframe, c.card, s.tag, count(*) n, round(avg(s.terminalOffDeg),4) mean,
       round(100.0*stdev(s.terminalOffDeg)/abs(avg(s.terminalOffDeg)),1) sd_pct
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE c.run_tag = 'R30' AND s.excluded = 0 AND s.railed = 0
 GROUP BY 1,2,3 HAVING n >= 3 ORDER BY sd_pct DESC;
```
This is the number an A/B has to beat. `--diff RUNA RUNB` is the two-run form.

#### Q6. Railed cells — where a gain change physically cannot move anything — *works today*
```sql
SELECT c.run_tag, c.airframe, s.tag, count(*) n,
       round(avg(s.bankClampActivePct),1) bank, round(avg(s.turnRateCapActivePct),1) turn
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE s.railed = 1 GROUP BY 1,2,3 ORDER BY n DESC LIMIT 20;
```

#### Q7. ~~Slack segments — the law, not the airframe, is the limit~~ — **REMOVED (v0.99.1). Does not work; do not restore.**
Both the `slack` flag and `authorityUsedFrac` were **deleted** from `scorecard.py`. The query below
is kept only so nobody re-derives it from scratch:
```sql
-- DEAD. authorityUsedFrac and slack are no longer written. Returns nothing on a fresh index.
-- SELECT c.run_tag, c.airframe, s.tag, count(*) n, round(avg(s.authorityUsedFrac),3) used
--   FROM segments s JOIN captures c ON c.id = s.capture_id  WHERE s.slack = 1 GROUP BY 1,2,3;
```
**Why it was deleted rather than re-thresholded:** `authorityUsedFrac` was `mean|bank| / maxBank` —
a bank-angle ratio, not a fraction of authority. It read **0.977–1.084** on R39 `alpha_hold`, i.e.
a "fraction used" above 1.0, which is the tell that the apparatus was never connected to the
quantity. It fired 8 times in 9,137 segments in its whole life, all 8 in one cell.
**The gap is still real: nothing in the corpus detects "the law is leaving authority unused."**
A replacement needs a normalizer of the form `omega_target = min(omega_avail, off/tau)` —
`LAW-CHARACTERIZATION.md` §7 #36. See `R40-metric-repair.md` and `LAW-WEAKNESS-MAP.md` W5.

#### Q8. Why is this metric NULL? — *works today*
```sql
SELECT json_extract(s.skipped, '$.blendRailPct') why, count(*) n,
       group_concat(DISTINCT c.run_tag) runs
  FROM segments s JOIN captures c ON c.id = s.capture_id
 WHERE json_extract(s.skipped, '$.blendRailPct') IS NOT NULL GROUP BY 1;
```

#### Q9. Entry-condition provenance across batches — *works today*
```sql
SELECT run_tag, count(*) n, round(avg(entry_snapBackM),1) snapback,
       round(max(entry_snapBackM),1) worst
  FROM captures WHERE entry_snapBackM IS NOT NULL GROUP BY 1 ORDER BY worst DESC;
```
`entry_*` is what the per-replicate reset had to undo. A batch whose `snapBackM` is climbing is a
batch whose replicates were drifting further apart before each reset.

> **`entry_snapBackM = 0` is a stratum, not a reading — filter it out of any A/B.** The FIRST placement
> of a lane is the one that *captures* the run anchor, so it cannot snap back to it: it writes back the
> speed and altitude the aircraft already had and the replicate flies from the spawn state, while every
> later replicate arrives teleported and decelerated. `ArmOf` is `((i+1)>>1)&1`, so index 0 is **arm 0
> on every ABBA card ever flown** — the stratum is 12.5% of one arm and 0% of the other. On R41
> `e1-below-suppress`/`FastBomber1` that single capture turned a 0.2% null into an apparent **30% knob
> effect** (`LAW-LEDGER.md` X27). **Add `AND entry_snapBackM <> 0` to any
> query that groups by `arm`.** `compare-runs.py` does this for you; raw SQL does not.
> `NULL` is different again — no `# entry` line at all, i.e. an ungated card — and means *unknown*,
> not zero.

#### Q10. Which grid cells have we NEVER flown? — *needs `--cards cards/` first*
```bash
python debugtests/index-captures.py --cards cards/
```
```sql
SELECT ca.card, group_concat(ca.airframe) never_flown
  FROM card_airframes ca
  LEFT JOIN captures c ON c.card = ca.card AND c.airframe = ca.airframe
 WHERE c.id IS NULL GROUP BY 1 ORDER BY 1;
--  alpha-steps   Fighter1,Multirole1,SmallFighter1,trainer,VTOLTrainer1,EW1,FastBomber1,Darkreach
--  oblique-05    CAS1,COIN
```

#### Q11. Frame hitches — *needs `--with-rows`; only R30 is materialized today*
```bash
python debugtests/index-captures.py --with-rows R30
```
```sql
SELECT c.run_tag, c.airframe, r.segTag, count(*) rows_over_25ms, round(max(r.frameMs),1) worst
  FROM rows r JOIN captures c ON c.id = r.capture_id
 WHERE r.frameMs > 25 GROUP BY 1,2,3 ORDER BY rows_over_25ms DESC;
```
Only meaningful for `mod_version >= '0.92.1'` — before that `frameMs` recorded the fixed step, a
constant. The drone launch stagger exists precisely so a hitch does not land on the same segment in
every lane; this is how you check it did not.

#### Q12. What did a card pin for itself? — *works, but 0 rows today*
```sql
SELECT run_tag, card, count(*) n, ov_note FROM captures
 WHERE ov_note IS NOT NULL GROUP BY 1,2,4;
```
Empty because no shipped card uses `config`. The parsing path is covered by
`index-captures.py --selftest` only — if you write a card with `config` overrides, re-index and this
becomes the record of which knobs the *card* (not the operator) chose.

#### Q13. Which columns can I even select? — *works today*
```bash
python debugtests/index-captures.py --query "SELECT * FROM segments LIMIT 0" --format csv
python debugtests/index-captures.py --query "SELECT * FROM captures LIMIT 0" --format csv
```

---

## `--query` behaviour

- **Read-only by default** (`file:…?mode=ro`). A write is refused with a line naming `--write`, not a
  traceback. The db costs ~30 s over 344 MB to rebuild; a mistyped query should not be able to cost
  that.
- **`--format table|csv|json`** — `csv` and `json` are the machine-readable forms.
- **`--limit N`** — default 1000, `0` for no cap. Truncation prints a loud `*** TRUNCATED` line on
  stderr; a silently truncated result set is a wrong answer that looks right.

## Gotchas, condensed

1. `avg()` without `count()` beside it — the whole of rule 2.
2. `ORDER BY run_tag` — lexicographic, so `R10 < R2`. Use `min(started)`.
3. Forgetting `s.excluded = 0` — 1482 `arm` windows with no metrics, silently in your GROUP BY.
4. Forgetting `s.railed = 0` — 285 segments whose numbers are limits, not scores.
5. Pooling across `airframe` — `compare-runs.py` refuses to; the grouping is `(airframe, card, tag)`.
6. `s.tag` alone as a key — tags are unique per card **by convention only**, and it already leaks
   (`hover`/`bobup` are shared by the rotor disk cards and the built-in `rotorcraft-v2`). Group by
   `(card, tag)`.
7. `sc_maxSpeed` is a normalizer (flat 600). Use `sc_infoMaxSpeed`.
8. `aircraft` is the unit name, `airframe` is the jsonKey. Group on `airframe`.
9. Matching on `segments.warnings` prose instead of the `railed`/`unknown_tag` flags — a
   reword away from silently marking the corpus clean. (`slack` is **dead**, see Q7 — a
   `WHERE slack = 1` now marks the corpus clean by construction.)
9b. **`WHERE dmgFrac = 0` as "undamaged" — it selects EVERYTHING and is the sharpest live instance
   of the zero-vs-never-measured trap.** The column is a guaranteed constant: **641,555 rows, 0
   non-zero, against 8 known damage aborts.** The recorder writes the row *after* the abort check,
   so a damaged replicate's damage is never written. The real damage signal is **the abort itself
   and the truncated capture** (`# stop` reason), plus the sidecar's `detachedRatioAtStart`. Four
   analyses have already been misled by this constant. Fix is `LAW-CHARACTERIZATION.md` §7 (Tier 1e:
   write the row before the abort check).
10. Assuming a metric column exists. It is created only when some capture produced it; `no such
    column` means "never flown", not "typo".
11. Ranking anything on `terminalOffDeg` without checking it is above `0.0396` — the `off` column's
    resolution floor. It is non-NULL, four decimals wide, and orders nothing;
    [see above](#the-resolution-floor--the-trap-that-survives-every-null-check). Same query, same
    trap, pooling an 8 s leg with a 30 s one: that column is anchored at the segment END, so it is
    two different quantities. Use `fixedWindowOffDeg` / `settleTime95`.
12. Comparing replicate spread across batches without checking `gJitterG` first. The game's world
    origin follows the OPERATOR'S CAMERA (`OriginShift`, decompile `:19365`), and a lane's physics
    jitter — which is the dominant term in `terminalOffDeg` scatter (r = 0.886 over 9 lanes,
    the R33 batch; `LAW-LEDGER.md` I8) — moves with it, **mid-batch, without warning and in opposite directions on
    different lanes**. Check the per-*replicate* series, not the mean: R33's flip is invisible in a
    lane average. A noise floor quoted without its `gJitterG` is one session's camera position.
