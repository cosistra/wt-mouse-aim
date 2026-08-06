# Offline tools — `debugtests/`

Every offline tool in this repo: what it answers, when to run it, and the traps. **This is the only
full tool reference** — `CLAUDE.md` carries a one-line-per-tool table and points here; the
`ARCHITECTURE.md` L0 map has a single `pytool` node that points here too.

All tools are **stdlib-only Python**, run from the repo root, and every one has `--selftest`.

**Read a slice, not the file.** Jump to the tool you are about to run:

| tool | answers | section |
|---|---|---|
| `check-card.py` | can this card fly its own experiment? **Run before every batch.** | [preflight](#check-cardpy--card-preflight) |
| `analyze-wobble.py` | what happened in this capture? (`--digest` first) | [wobble/digest](#analyze-wobblepy--wobble-scoring-and---digest) |
| `scorecard.py` | per-segment metrics for one run | [scoring](#scorecardpy--scoring-a-test-card-run) |
| `index-captures.py` | the cross-batch SQLite corpus | [index](#index-capturespy--the-cross-batch-sqlite-index) |
| `flightscore.py` | flight quality, normalized across airframes | [flightscore](#flightscorepy--cross-airframe-flight-quality) |
| `loopaudit.py` | can a term move the signal that gates it? | [loop audit](#loopauditpy--self-referential-feedback-loops) |
| `gatechatter.py` | gate rail dwell (closed investigation) | [gate chatter](#gatechatterpy--gate-chatter-closed) |
| `check-mission.py` | is the test range still isolated? | [mission](#check-missionpy--validating-the-test-range) |
| `compare-runs.py` | run-to-run spread by airframe+card+arm | [compare](#compare-runspy--comparing-runs) |
| `check-architecture.py` | does `ARCHITECTURE.md` still match the code? | `CLAUDE.md` → Keeping the diagram current |
| `index-decompiled.py` | reverse a `:NNNNN` citation into a type/member | [decompile index](#index-decompiledpy--the-decompile-index) |
| **flying a batch** | the whole unattended procedure | [harness](#uncrewed-drones--the-harness-procedure), [run board](#the-harness-run-board), [sandbox](#hand-flying-the-law-the-sandbox-key) |
| `test-card-owns.py` | does a card's declared value beat the F1 value? | [ownership](#test-card-ownspy--the-ownership-rule) |
| **source-region tests** | compile a region of the shipped C# and assert on it | [arm](#test-arm-schedulepy--concurrent-ab-arms), [respawn](#test-lane-respawnpy--dead-lane-respawn), [collective](#test-collective-holdpy--the-rotorcraft-collective-hold), [grammar](#test-spec-grammarpy--config-spec-grammar), [fleet](#test-fleet-resolvepy--fleet-resolvers-and-the-entry-speed-gate), [lane](#test-lane-framepy--lane-frame--ring), [card model](#test-card-modelpy--card-deserialisation), [ownership](#test-card-ownspy--the-ownership-rule), [board](#test-board-mathpy--the-run-boards-arithmetic) |

Related references: `debugtests/CAPTURES-DB.md` (schema + SQL traps), `cards/README.md` (the card
grid), `AIRFRAMES.md` (jsonKeys and envelopes).

---

## check-card.py — card preflight

**Card preflight — run this BEFORE flying, it is the cheapest check in the repo.**
`python debugtests/check-card.py cards/*.json` (stdlib-only; `--selftest` for the asserts) loads a
card plus the airframe table and refuses cards that cannot fly their own experiment. It exists
because three cards in two days did not fly the test they were named for, each failing on
arithmetic computable without launching the game: `alpha-sweep` demanded load through azimuth,
which reaches the wing only via bank, clamped at 72° = **3.24 g** against the 4.8–24 g its lanes
needed; `stol-*` declared 90 m/s and flew 340–381; `rotor-*` never hovered because `startSpeed: 0`
fell through to the live `DroneSpawnSpeed`. It checks segment tags against `scorecard.py`'s table
(an unknown tag is invisible to scoring), envelope + **density-corrected** stall at the card's own
altitude, the FBW authority knee, the throttle floor, alpha reachability, and total wall-clock.
Every constant is parsed from `Cfg.cs` / `ScenarioPlayer.cs` / `TestDrone.cs` / `AIRFRAMES.md` at
runtime and it hard-errors if a regex stops matching — a hardcoded copy here would be exactly the
drift the tool exists to catch. **The fallthrough table in its header is the authoritative list of
every card field that resolves silently**; read it before adding a card field.

**v1.0.3 — CHECK 6, ownership, and it FAILS rather than warns.** Every parameter that decides what a
run measures must be declared on the card: the fields (`airframe`, a resolvable lane count,
`repeat`, `armToggle` — `"none"` is the explicit "no A/B", since absent and empty are the same
string) and the `config[]` pins in `PINS_REQUIRED` (`ScenarioThrottle`, `ScenarioEntryFuel`,
`ScenarioForceEntry`, `DroneAltDeckM`, `DroneStaggerSec`) plus `PINS_ROTOR`
(`HeliForwardSpeed`/`HeliHoverSpeed`) on rotorcraft cards. A warning on a 500-capture batch is read
*after* the batch. The lane/cost model reads the card's declared deck and stagger through
`pinned_num`, so its arithmetic follows the card rather than the globals. **Adding a card-owned knob
means adding it to `PINS_REQUIRED` here and to every shipped card.** The runtime half is
[`test-card-owns.py`](#test-card-ownspy--the-ownership-rule).

## analyze-wobble.py — wobble scoring and --digest

**Offline recording tool.** `python debugtests/analyze-wobble.py <rec.csv>...` (stdlib-only) has
two modes. **Default** scores any maneuver-recorder CSV for the death-wobble signature:
oscillation episodes with frequency/amplitude/trend, roll-rail %, targetBank clamp %, and the
bank-vs-command lag (built from the v0.51 investigation, **whose conclusions are REFUTED — see
`LAW-LEDGER.md` X35**: read this tool as a per-capture readout, not as a verdict, and note that the
wobble it was chasing is a *below-nose* phenomenon, not a high-speed one).
**`--digest <rec.csv>`** collapses the 900-row-ish capture into a ~30-line phase-segmented
timeline (per segment: duration, the signals that moved, per-axis stick sign-flip counts, and any
`# cfg` change / `[anomaly]` from the sibling `mouseaim-anomalies-<session>.log`). **To read a
recording, run `--digest` first and only open raw rows for a segment it flags** — feeding raw CSV
to an LLM is expensive and mostly steady-state redundancy. `--selftest` runs the in-memory asserts.
Run this on user-reported recordings before theorizing. Past **10** captures `--digest` collapses
one level further, to one line per file; `--verbose` keeps the full timelines.
It also **exports `WOBBLE_SIGNALS`** (v0.96) — the per-signal oscillation dead-bands (bank 3.0,
azErr 0.5, outR/outP/outY 0.05, aoa 2.0) — as the one definition, consumed by `scorecard.py`, plus
(v1.0.5) **`PRINT_QUANTUM`**, the recorder's print step per column, and **`SETTLED_SIGNALS`**, the
subset that survives a *settled-window* question: `outR` is replaced there by `rollRate` and `outP`
is dropped outright, because in a settled tail those two columns hold 1–1.5 print quanta of spread
and an autocorrelation will fit the quantiser (`CAPTURES-DB.md` gotcha 22, `LAW-LEDGER.md` X34).
`WOBBLE_SIGNALS` itself is unchanged: this module's own death-wobble scan is amplitude-gated at 50
quanta and rail-to-rail, so quantisation cannot reach it. The
direction rule is the non-obvious bit and is worth stating: `scorecard.py` `exec_module()`s
analyze-wobble (the hyphenated filename means no plain `import`), so the dependency runs
**scorecard → analyze-wobble**, and anything shared between the two must be defined on the
*analyze-wobble* side. Defining it in scorecard would close the cycle.

## Batch-sized output

**Batch-sized output (v0.90).** `scorecard.py`, `flightscore.py` and `analyze-wobble.py --digest`
are all O(files) — at the 100-450 captures an unattended batch now writes, that is thousands of
lines. All three suppress the per-file detail past **10** files and print only the roll-up /
aggregate, with `--verbose` to force the old behaviour; at ≤10 files nothing changed. Read a big
batch through `compare-runs.py --summary` (one line per card+segment) and open the full table only
for what it points at.

## scorecard.py — scoring a test-card run

**Scoring a test-card run.** `python debugtests/scorecard.py <rec.csv>` segments by `segTag` and
emits per-segment metrics (`--json`, `--selftest`). A segment sitting on a limit for ≥90% of its
samples (bank clamp / turn-rate cap / blend rail / past the AoA ceiling) is flagged **RAILED** in
`warnings` — its metrics cannot respond to a gain change, so read them as *no signal*, not as a
score. A capture whose `dmgFrac` (column 65, v0.96) ever exceeds 0 is flagged **DAMAGED** in the
same `warnings` channel — whole-capture, not per-segment (detachment is permanent, so a per-segment
form would just repeat itself), naming the max ratio, the first segment and the `t` it appeared at.
An absent column (every capture on disk predates it) and the −1 "could not read it" sentinel never
warn. **`dmgFrac` cannot currently reach that branch and must not be used as a damage filter** —
the v0.96 abort truncates the capture *before* the row is written, so the column is 0 on all
641,555 indexed rows against 8 known damage aborts; damage shows up as the abort and the short
capture, never as the column (`LAW-LEDGER.md` X24).
**Every metric routes through one dead-column guard (R40).** `load_csv` withdraws, per capture,
any numeric column that is present in the header and identically 0.0 on every row, and emits one
**DEAD COLUMN** warning naming them — so a metric derived from a column nobody writes comes out
SKIPPED rather than as a confident 0.0. Zero-variance-at-*zero* only: a constant non-zero column
(`bWt` railed at 1, `assist` 0/1, `thr`) still scores, because there the value is its own
evidence. `scorecard.py --deadscan <many.csv>` is the wider report — DEAD, CONSTANT, and columns
**flat within each capture but varying between them**, which is the shape a conditionally-written
column takes.
**Two metric families were repaired in R40 and one was deleted — read
the three corpus-wide invalidations at the head of `LAW-LEDGER.md` before quoting any pre-R40 score.** `bankClampActivePct` now
reads `bankTR`, not `targetBank` (which is the removed Legacy law's azErr-proportional bank
target and errs in *both* directions — 27.5% of corpus segments move > 5 pp, 17 flip to RAILED);
`wobbleEpisodes*`/`wobbleFreqHz*` are measured over a per-segment settled window with an
amplitude-independent autocorrelation+DFT estimator, and there is a new `wobbleCoherence*` beside
each frequency (318 corpus "episodes" were entry transients; 5 survive); and
`authorityUsedFrac`/`authBank`/`authAoa`/`authStick` **and the SLACK flag are gone** — the
denominator was `maxBank`, and bank in a coordinated turn is pinned by `atan(ωV/g)` before the law
runs, so it measured the card's demand. Do not reintroduce a mean-over-a-limit as an effort metric.
**Do NOT rank airframes on `terminalOffDeg` (v0.97.1).** It is unreliable in *both* directions and
the two failures compound. (a) It is **end-anchored**: R35's 30 s legs have a median `settleTime95`
of **15.5 s** and *not one leg in the batch settled before 9.0 s*, so every 8 s `oblique_step` in the
corpus is scored mid-transient — the terminal sample is a point on a decay curve, not a steady state.
(b) Below the float32 grain it reports **zero, not small**: `off` is `Vector3.Angle` in float32, so its
quantum is `sqrt(2·5.96e-8)` rad = **0.0198°** (proof: across 279k R35 rows the value `0.01` NEVER
occurs while `0.00` and `0.02` occur tens of thousands of times), and **94 of 192** near-lane terminal
windows read exactly 0.0000. Three replacements, all in `pointing_metrics` so every non-`arm` segment
gets them and `index-captures.py` needs no change: **`fixedWindowOffDeg`** (mean `off` over a fixed
7–8 s window from segment start — the same window for every leg, so it cannot be gamed by segment
length; `None` + a `skipped` reason when the segment is under 8 s or the mean lands under the floor),
**`settleTime95`** (seconds to the LAST band exit — scanned backward from the end, so an early dip
cannot fake a settle; `None` = did not settle, which is an outcome and not a failed measurement), and
**`offFloorPct`** (% of samples under `OFF_FLOOR_DEG` = 2× the quantum). A third warning joins the
RAILED family — **AT THE RESOLUTION FLOOR** — and is not mutually exclusive with it. On the
R35 archive (96 captures, 384 unrailed 30 s legs) the near-vs-far Spearman is `terminalOffDeg`
**+0.029** — nothing — against `rmsPointingErrorDeg` **+1.000** and `fixedWindowOffDeg` **+1.000**;
the new metric is doing what RMS does, which is the point. `terminalOffDeg` keeps its raw value for
continuity with archived scores. **`settleTime95` is censored and NOT at random** — 192/192 near-lane
legs settle against 26/192 far-lane ones, so ranking on its *mean* yields −0.600 from survivorship
alone: read `count()` beside `avg()`, or score the settle *rate*. **A metric change needs
`index-captures.py --rebuild`**, not a re-run: the warm path skips captures whose `(mtime, size)` are
unchanged, and a scorer edit moves neither, so `no such column` on the three new names means
"rebuild", never "never flown".
**`gJitterG` (added by R33) is the metric to read BEFORE any cross-batch comparison of replicate
spread**: mean |Δg| between consecutive samples, per segment, deliberately orthogonal to
`gPeak`/`gSustained`. It is not an aero quantity — the game's `Aircraft.gForce` is
`|v − vPrev|/(dt·9.81)` off the COCKPIT PART's rigidbody (`:61977`), so it carries the joint
solver's noise, and that noise is the **dominant term in `terminalOffDeg` replicate scatter**
(r = 0.886, log-log slope 0.82, over 9 lanes and in both directions —
`LAW-LEDGER.md` I8, and `debugtests/CAPTURES-DB.md` gotcha 12). It is a property of the **session, not
the airframe**: the game's world origin follows the OPERATOR'S CAMERA (`OriginShift`, `:19365`),
and R33 caught it flipping mid-batch at one instant, widening six lanes 2.2–12x while narrowing
four. Read the per-*replicate* series — the flip is invisible in a lane mean — and park the camera
for a batch that has to be compared with another. **An unrecognised tag prints a WARNING** —
never ignore it: the tag vocabulary lives in `ScenarioPlayer.cs` **and in `cards/*.json`**, while the
tag→metric table lives in `scorecard.py`, with no compile-time link between them. That pair silently
drifted once already (v0.71: 19 of 21 segments scored as "unknown" with no output at all).
**Adding or renaming a card segment means updating both, in the same change.** **Both halves are now
checked (v0.96)**: disk cards by `scorecard.py --selftest` (which parses every file in `cards/` and
asserts each tag resolves), and the **built-ins** by `check-architecture.py`, which scrapes every tag
`ScenarioPlayer.cs` can emit and resolves it through `scorecard.infer_type`. That check found two
built-in tags scoring as "unknown" on its first run: `rec` (`StopRecord`'s recorded-demand track) and
`seg<i>` (`Validate`'s fallback for a disk card whose author left `tag` empty). `rec` now maps to
`fine_track`; `seg\d+` maps to `untagged`, which gets the generic metrics and **deliberately no
warning** — the *card* is what is underspecified, not the table, so telling the reader to add a rule
would point at a rule that cannot exist.

## index-captures.py — the cross-batch SQLite index

**Cross-batch index (SQLite).** **Read [`debugtests/CAPTURES-DB.md`](CAPTURES-DB.md)
BEFORE writing a query against it** — the column-by-column reference (type + provenance), the
metric × segment-type matrix, the NULL idioms and a cookbook of 13 queries each verified against the
live index. It exists because **every trap in this schema returns a plausible number rather than an
error**: metrics are SPARSE by segment type, so a corpus-wide `avg(metric)` silently averages a
handful of rows; the `n_cols` staircase (38/44/45/54/56/57/58/64/65) is how you filter by era; and
the six `sc_*` raw-sidecar twins each have a right and a wrong side to join on. Always
`select count(metric)` beside `avg(metric)`.
`python debugtests/index-captures.py <game>/BepInEx` builds
`debugtests/captures.db` — one row per capture, one per (capture, segment) — in ~30 s over the
whole corpus, and re-runs in 0.2 s because it skips captures whose (mtime, size) are unchanged. It
is where a question spanning batches lives: "does this effect hold in R28, R29 AND R30?" is one
`GROUP BY c.run_tag`, not three tool runs stitched into prose. **Every metric in it comes from
`scorecard.py`** — the module is imported and `score_run()` called, so the tag→metric table, the
RAILED threshold and the unrecognised-tag rule stay in ONE place; re-index and the database
follows. (`scorecard.is_railed(seg)` / `railed_metrics(seg)` exist for exactly this: the index
needs the railed *predicate*, and matching on the warning prose would have been one reword away
from silently marking a whole corpus un-railed.) What the index parses itself is only header text
scorecard's `provenance()` skips (`# entry`, `# override`, `# drone`, `arm=`/`armKnob=`). Sidecar
scalars land as `sc_*` columns and entry fields as `entry_*`, both added **dynamically** so a new
field appears rather than disappears. Raw rows stay in CSV unless you ask (`--with-rows R30`, one
batch at a time — all ~1.1M rows would be ~500 MB and mostly unread). `--archive <dir> --run R29`
copies that batch's CSVs, sidecars and `LogOutput-R29.log` out of `<game>`: **do that after every
batch**, because `LogOutput.log` is overwritten each session and R28's launch lines are already
gone for good. `--selftest` runs on a
synthetic capture with no game folder needed. The `.db` is gitignored — it is derived, and the
CSVs are the source of truth.
**Three orientation commands (v0.96), and `--stats` is the one to run first:**
- `--stats` — totals, a per-batch table (mod version, captures, airframes, cards, aborts, `n_cols`,
  materialized rows), the `n_cols` era histogram, per-airframe counts and the parse-failure count.
- `--check [RUNTAG]` — completeness. Per-(run, airframe) capture counts with an outlier and
  **STOPPED EARLY** flag, `rec` gaps **per session** (`rec` is a per-*process* counter, so a
  corpus-wide gap scan is meaningless), aborted captures with their stop reasons, parse warnings and
  unknown tags. With no RUNTAG it scans all 26 batches and prints only the flagged lanes. This is
  what catches a dead lane: R29's Darkreach flew **9** captures against 48 for every other lane, and
  that is invisible in every aggregate view.
- `--diff RUNA RUNB [--metric M] [--tag T]` — per (airframe, card, tag): `mean ± stdev%` in both
  batches and the B/A ratio, railed and `arm` segments excluded, grouped the way `compare-runs.py`
  groups and never pooled across airframes.

`--query "<sql>"` prints a table; four worked queries (rank airframes, compare batches, find railed
cells, A/B by arm) are in the module docstring and thirteen more in `CAPTURES-DB.md`. Since v0.96 it
is **read-only by default** (`file:…?mode=ro`; `--write` opts out) — the db costs ~30 s over 344 MB
to rebuild and a mistyped query should not be able to spend that — and takes
`--format table|csv|json` and `--limit` (default 1000, `0` = uncapped; truncation is a **loud stderr
line**, never silent). A write attempt on the read-only handle is turned into a one-line refusal
naming `--write`, not a traceback.
**`stdev()` and `median()` are registered SQL aggregates** (SQLite ships neither), available in
`--query` but **not** in a bare `sqlite3` shell. `stdev` is the **sample** (n−1) form, matched
deliberately to `compare-runs.py`'s `statistics.stdev` so a SQL noise floor and a compare-runs table
cannot disagree — the population form is 6.9% smaller at the n=8 the shipped grid flies, which would
read as "the noise floor moved".
**`--cards <dir>`** loads `cards/*.json` into `cards` / `card_airframes` dimension tables (card id =
the file basename, so it joins straight to `captures.card`), which turns "which grid cells have we
NEVER flown?" into a `LEFT JOIN`. Lanes are expanded only for cards whose `airframe` is a real
jsonKey list, and `scorecard.card_setup_problems()` is the arbiter — not a second copy of the rule.

## flightscore.py — cross-airframe flight quality

**Cross-airframe flight quality.** `python debugtests/flightscore.py <rec.csv>...` answers one
question per tick — *given what this airframe could physically do at that instant, was there a
better way to get the nose where it was asked?* Every normalizer comes from the sibling
`.airframe.json` probe plus live state (V, air density, velocity vector), **never a hand-tuned
constant**, which is what makes a light jet, a loaded jet, a STOL trainer and a helo comparable —
the offline mirror of the one-law rule. `--levers` prints the lever block (incl. `xfightPct`) even
on old captures; `--json`, `--selftest`. **It also owns `opposed(r, y)`** — the ONE definition of a
roll/yaw cross-fight (both channels clear of `STICK_DEADBAND`, opposite signs). flightscore owns it
because it owns the constant and imports nothing but stdlib, so every other tool can reach it
without a cycle; `gatechatter.rollYawAnti` and `scorecard.rollYawOpposedPct` **call** it rather than
re-spelling it. Before v0.96 that predicate existed inline in three files against two spellings of
the same 0.02 — three answers waiting to diverge on the next threshold tweak.

## loopaudit.py — self-referential feedback loops

**Self-referential feedback loops.** `python debugtests/loopaudit.py <rec.csv>...` asks
GENERALITY-REVIEW finding 13's question — *can the command this term gates move this term?* — by
recomputing `blendWeight`/`assist`/`coordPull` and inverting `bankTR` to recover `omegaDes`, so it
can report what fraction of the demand chain actually **reaches** a control output, plus the
`_pitchEff` self-probe latch, diagnosed from the recorded rate pair rather than inferred.
`--settled 20` drops entry transients; `--json`, `--selftest` (the closed forms, no data needed).
Findings: `LAW-LEDGER.md` L12–L14.

## gatechatter.py — gate chatter (CLOSED)

**Gate chatter — CLOSED INVESTIGATION.** `python debugtests/gatechatter.py <rec.csv>...
[--win 0.20] [--cone 0.2] [--json] [--perm 399] [--skip 0.0] [--bytag] [--selftest]`. Kept for
reproduction only: its hypothesis was answered in v0.85 (`LAW-LEDGER.md` S4–S6, X9 — the
below-nose roll-to-align positive feedback loop) and fixed behind
`BelowAlignSuppress`/`AlignRateLead`. Its durable half is `flightscore`'s `xfightPct`. **Do not
reach for it to score a routine batch.**

## check-mission.py — validating the test range

**Validating the test range.** `python debugtests/check-mission.py <mission.json>` checks a mission
against the WTM-Range isolation/pinning invariants: no free-standing units, every faction HQ the map
carries listed with its AI budget explicitly zeroed, at least one real airbase, weather/wind/
time-of-day pinned, wreck cleanup wired. **Isolation is NOT an empty faction list** — that was this
checker's own bug: `Mission.EnsureFactionExists` auto-inserts a default `MissionFaction` with
`AIAircraftLimit = 6`, so `"factions": []` means "both factions, six AI aircraft each, deploying
about five seconds in". An unpinned range crashes nothing; it quietly corrupts every score run
against it. `--selftest`.

## compare-runs.py — comparing runs

**Comparing runs.** `python debugtests/compare-runs.py <rec1.csv> <rec2.csv> ...` reports
per-segment spread across N runs — the noise floor, and the A/B of a law change. It **groups by
(airframe, card, arm) and refuses to pool**, and excludes truncated segments rather than blending
them; heed both warnings rather than working around them. The card is in the key because segment
tags are unique per card **by convention only** and that already leaks (`hover`/`bobup` are shared
by the rotor disk cards and the built-in `rotorcraft-v2`). **`--summary`** prints one line per
(card, segment) — n, duration, worst rail, and three headline metrics as `mean +- stdev%` — which
is the only readable form at ~40 card/tag pairs; scorecard's per-run warnings (incl. RAILED) are
carried through, deduped with a count.

## Uncrewed drones — the harness procedure

**Uncrewed drones (v0.81; flying the real law since v0.87; self-configuring since v0.90, fleet and
all since v0.91; **concurrent A/B since v0.94**).** The whole procedure is now: tick `Drone/DroneEnabled` in F1, tick **one** card in
`Scenario Cards`, press the spawn key. The card supplies the airframe(s), altitude, speed, replicate
count, A/B knob and — since v0.91 — **how many drones fly and what each one is**: `airframe` is a
comma list indexed by lane and wrapping (`"Fighter1, Multirole1, SmallFighter1"` = a three-airframe
fleet), and `count` defaults to the number of keys in it. **Keys come from
[`AIRFRAMES.md`](../AIRFRAMES.md)**, which is also where you check that every airframe in the list can
actually fly the card's `startSpeed` — there are 14 real jsonKeys and an invented one costs a
refused lane. **v0.93: a card can instead say `startSpeedCorner`**, an entry speed as a multiple of
each lane airframe's own corner speed, which is the way to make one card flyable by a roster whose
Vmax spans 141–479 m/s. So **nothing in F1 needs to match the
card** — **do not hand-match the `Drone*`/`Scenario*` globals to it**; they are the fallback for a
card that declares nothing, and hand-matching was the mismatch this removes (a mismatch does not
refuse, it writes a capture that scores fine and answers a different question). Those drones launch
`DroneStaggerSec` apart, each starts that card itself, flies it with the mod's control
law, writes its own CSV (`d<N>-<airframe>` in the filename) and **despawns itself ~5 s after its
card ends** — including if it was aborted, refused or never started. A mixed batch reads back
correctly because `compare-runs.py` groups on the sidecar's `jsonKey` and refuses to pool across
airframes: one row per airframe, which is what a fleet card is asking for. **An A/B no longer needs
one drone (v0.94)** — the swept arm is per-aircraft state read through the controller, so every lane
runs its own independent ABBA off its own queue index and a 10-airframe attribution batch is one
launch instead of ten serial ones. Nothing writes the `Cfg` knob any more, so your own aircraft keeps
flying whatever F1 says while the fleet sweeps around you. Everything it does is one grep:
`[drone]` in `LogOutput.log` covers spawn/despawn (with the reason), every refusal (no server,
unknown airframe key, **an airframe that cannot fly the card's entry speed — v0.92, checked
pre-spawn off `Encyclopedia.Lookup`; the line carries the requested speed, the bound it violated
and that bound's value**, no `Spawner`, the instructor declining to engage), a pilot killed or ejected,
a drone the game removed under us, and `[drone] frame hitch` for any rendered frame over 50 ms. The
launch line also names, item by item, whether the airframe/alt/speed/**drone count** came from the
card or from F1 — read it, it is the only confirmation that the card drove the spawn. The **spawn** line carries the
**crew count** (v0.90.1): every seat fires the pilot postfix independently, which double-stepped both
the card clock and the control law until the `Time.fixedTime` guard in `OnPilotStep`, and seat count
is prefab data with no code-side definition — that line is the only way to learn a `trainer` has two.
`[card]` lines carry the
card/segment progress for every aircraft flying one. **Five CAPS greps are worth knowing before you
read a short batch** — they exist because P1/P2-style degradations make a card contribute *no*
capture, and a short replicate count must mean "read the log", never "the recorder dropped it":
`SKIPPING` (this card's entry speed is outside this lane airframe's envelope — the `[drone] refused`
line above it names the bound), `ABORTED` (the suite-end tally: how many replicates this lane lost,
each of which still wrote its own CSV with its reason on the `# stop` line), `ENTRY CONDITION NOT
HELD` (the card was placed on condition and the `arm` segment already left it — the throttle pin
does not match the declared `startSpeed`, so every scored segment below is at the drifted state),
`SWEEP RATE CLIPPED` (a `deriveAzRate` card's derived rate hit the 3..30 deg/s clamp, so this lane
is NOT flying the same fraction of its own g limit the unclipped lanes are) and `OVERRIDE REFUSED`
(another card in the air already holds that knob at a different value, or it is the A/B knob).
**A refusal is always a log line, never a
silent no-op** — the harness runs unattended, so a key that appears to do nothing has to be
explainable after the fact. `TestDrone.FrameDt` (the fixed-step `Time.unscaledDeltaTime` sample) is
the signal the stagger exists to defend against.
**THE `sel[0]` RULE (know this before ticking two checkboxes).** Multi-card selection is supported
and each drone flies the whole queue round-robin — but `airframe`, `count`, `repeat`, `armToggle`,
`startAlt` and `startSpeed` are **all read off `sel[0]`** (`ScenarioPlayer.Preview` and `StartSuite`
both take `sel[0]`; the spawn resolves ONE `Preflight` per batch) and applied to the entire launch.
The trap: `Register` binds each card's checkbox with `builtIn` as its **default value**
(`ScenarioPlayer.cs:497`) and `LoadCards` registers the built-ins **before** scanning disk, so on a
fresh config `sel[0]` is `fixedwing-v2` — which declares no airframe/count/repeat/armToggle. The
whole batch silently becomes one `Multirole1`, one replicate, no A/B, with the card you actually
ticked flying second as a stimulus only. **Nothing refuses.** Compounding it: the spawn's `sel[0]` is
the UNFILTERED one (`Preview` applies no `cls` filter, by design — it has no aircraft in hand) while
`StartSuite` filters by class, so a ticked `rotorcraft-v2` can dictate the spawn while a `Plane` card
is what flies. **`Scenario/ScenarioCardSet`** (an ordered comma list that overrides the checkboxes
entirely) is the reliable selector.

## The harness run board

**Harness run board (v0.90).** With `DroneEnabled` on, a panel top-left shows either **PREFLIGHT**
(what the spawn key *would* fly: card, replicate count, per-drone total time, and the airframe /
altitude / speed / **drone count** each marked `[from card]` or `[from F1]` — v0.91 reads that count
through `TestDrone.CountOf` rather than quoting `Cfg.DroneCount`, which would be wrong exactly when
the card is driving, i.e. the case this panel exists for — plus the A/B knob, and an amber
**NO CARD SELECTED** line, which is the commonest setup mistake and used to surface only as a log
warning *after* N drones were airborne measuring nothing) or, once anything is flying, one line per
aircraft with card, run *x*/*y*, arm, segment and tag, seconds left in the segment and in the card,
and the recorder's sample count. It draws through `ShowOverlay` being off and through the operator
having no aircraft, because that is the state you watch a batch from. Its two pieces of arithmetic
live between the `BOARD-MATH` markers in `ScenarioPlayer.cs` and are checked by
`python debugtests/test-board-math.py`, which extracts that region **verbatim**, compiles it with
the .NET SDK and runs 23 cases — so it tests the shipped code, not a Python copy that would drift.

## Hand-flying the law: the sandbox key

**Hand-flying the law: the sandbox key (v0.95).** `Cfg.SandboxKey` (default **F4**, `Sandbox`
section, read whether or not `DroneEnabled` is on) puts **you** airborne at
`SandboxAlt`/`SandboxSpeed` — 4000 m / 250 m/s, i.e. the shipped grid's entry condition — so a
law change can be *felt* without loading a mission, taking off and climbing to it. Already in an
aircraft: it is placed there, wings level, over its current position and on its current heading,
and nothing spawns. Not in one (spectating, ejected, on the ramp): a `SandboxAirframe` is spawned
500 m ahead of the camera and the game seats you; pressing it again while alive swaps airframe (the
game ejects the old one). Needs an active server, exactly like the drone spawn — SP is a host, so SP
and hosting work and an MP client refuses. Everything it does is one grep: **`[sandbox]`** in
`LogOutput.log`, alongside `[drone]` and `[card]`, covering the placement/spawn line (airframe, alt,
speed, heading, crew count) and every refusal — no server, no `Spawner`, `Encyclopedia` not loaded,
no local player, no faction HQ yet, an unresolvable `SandboxAirframe` key, `SpawnAircraft` returning
nothing. Same doctrine as the harness: **a refusal is always a log line, never a silent no-op.**
Two things it deliberately does *not* do: it is not envelope-checked (unlike a drone lane — see
[`AIRFRAMES.md`](../AIRFRAMES.md) if the speed matters for your airframe), and it writes **no capture** —
it is a way to get to a state, not an instrument. Hit `RecordKey` afterwards if you want a CSV.

## test-arm-schedule.py — concurrent A/B arms

**Concurrent A/B arms (v0.94).** `python debugtests/test-arm-schedule.py` — same trick again, on two
regions at once: `ARM-SCHEDULE` in `ScenarioPlayer.cs` (the ABBA index) and `ARM-SEAM` in
`ChaseController.cs` (the per-aircraft arm map). Asserts the sequence `0,1,1,0,0,1,1,0`, equal mean
queue position at every multiple of 4 (and that `n=6` is equal-COUNT but unequal mean — the case that
proves counting arms cannot detect an imbalance), the arm surviving a rebuilt controller, two
aircraft on opposite arms at once, and per-aircraft clearing. Plus **five** source assertions the
compiled region cannot make about itself: `ChaseController.Forget` must NOT clear the arm (the
per-replicate reset calls it every run — clearing there silently un-sweeps the experiment), `For`
must seed it, `ApplyArm` must never write the global `ConfigEntry`, all **five** lever sites must read
through `Arm()`, and — **v0.96** — `LEVERS` must equal exactly the set of `ConfigEntry<bool>`
declarations `Cfg.cs` marks `(A/B lever)`, so adding OR removing a lever fails here until `LEVERS` /
`LEVER_SITES` are updated with it (which is exactly how v0.99.1's `RelativeTurnLead` deletion was
caught — four levers, five sites now). **Run it after touching the arm machinery or adding an A/B
lever** — none of those five fails to compile, and all five produce a batch that scores fine and
answers a different question.

## test-lane-respawn.py — dead-lane respawn

**Dead-lane respawn (v1.0.2).** `python debugtests/test-lane-respawn.py` compiles the
`LANE-CONTINUITY` **and** `ARM-SCHEDULE` regions of `ScenarioPlayer.cs` together, because the
property is compositional and neither piece can state it alone: **a lane that loses its aircraft at
any replicate and resumes where it left off flies every queue index an undamaged lane would have**
(nothing re-flown, nothing skipped) **and scores no anchor-capturing replicate** (ledger `X27`) —
the resumed one is a warm-up, and the check pins the cost at exactly that one replicate so a fix
that unscored the whole resumed tail would fail too. Plus the **hard cap**: a lane that dies on
every replicate must relaunch exactly `MaxLaneRespawns` times, not sixteen (R41's `UtilityHelo1`).
Plus five source asserts the arithmetic cannot make about itself — `TestDrone.LaneLost` still asks
the card player, **both** removal paths ask it *before* `ForgetState` (which nulls the queue that
holds the answer), the respawn reuses `LaunchLane` rather than a second spawn site, `DespawnAll`
guards with `_cancelling`, and the resume actually reaches `StartSuite`. Run it after touching the
despawn paths, the lane spawn path, or the arm schedule.

## test-collective-hold.py — the rotorcraft collective hold

**The rotorcraft collective hold (v1.0.2).** `python debugtests/test-collective-hold.py` does the
same extract-and-compile trick to the `COLLECTIVE-HOLD` region of `TestDrone.cs` — the PI altitude
hold that owns the throttle on a drone-flown rotorcraft *hover* card. Two halves. (1) **Eleven
one-step cases** pin the **sign** and the shipped gains: an inverted collective term does not
wobble, it flies the aircraft into the ground at full deflection and the capture reads as a
control-law failure. They also pin the `VsMax` cap, both clamps and the fact that `dt` scales the
integrator *and nothing else*. (2) **Six closed-loop cases** fly a first-order rotorcraft for 120 s
whose hover collective the loop is **never told**, spanning 0.35–0.90 — that is the generality rule
asserted rather than argued, and it is the check that fails if anyone replaces the integrator with a
constant (deleting it drops those cases 89–282 m off target). Run it after touching the loop or its
gains. Needs the .NET SDK, like the other extract-and-compile checks.

## test-spec-grammar.py — config-spec grammar

**Config-spec grammar (v0.96).** `python debugtests/test-spec-grammar.py` extracts `SplitSpec` from
the `SPEC-GRAMMAR` markers in `ScenarioPlayer.cs`, compiles it, and runs 16 cases against **both**
it and `scorecard.py`'s hand-written `split_spec` (which powers `card_setup_problems`) from one
shared table. Run it after touching either. **One known divergence, deliberate and pinned by the
test**: the C# splits on the *first* slash and accepts `"A/B/C"` as section `A` / key `B/C`; the
Python copy refuses more than one slash. Neither is dangerous — the mod's lookup then finds no such
entry and warns by name, fail-soft — and the stricter offline side is the more useful one, because
it says so *before* the batch flies rather than after. See `LAW-CHARACTERIZATION.md` §7 for the
one-line C# change that would collapse the two columns.

## test-fleet-resolve.py — fleet resolvers and the entry-speed gate

**Fleet resolvers and the entry-speed gate (v0.96).** `python debugtests/test-fleet-resolve.py`
compiles `ResolveCount`+`CountKeys` (`FLEET-RESOLVE` in `ScenarioPlayer.cs`) and
`AirframeList`+`AirframeForLane` (`FLEET-RESOLVE`) + `StallMargin`/`VMaxMargin` (`ENTRY-MARGINS`,
both `TestDrone.cs`) verbatim, then asserts the pair invariant `CountKeys(s) == len(AirframeList(s))`
over a token table, lane wrapping, all three `ResolveCount` sources **with their `src` strings**,
both `1..16` clamps, the card-list-beats-`Cfg`-wholesale rule, and the v0.92 margins against
`AIRFRAMES.md`'s roster — including that `StallMargin` stays **below 1.20**, because `stol-*` at
90 m/s on `SmallFighter1` is a ratio of exactly 1.200 (`270/3.6 == 75.0` exactly). Run it after
touching the fleet resolvers or the entry-speed gate.

## test-lane-frame.py — lane frame + ring

**Lane frame + ring (v0.97.1, extended v0.99).** `python debugtests/test-lane-frame.py` — three
parts. (1) **Source:** `_laneBase` is a `GlobalPosition`, every write converts and the read converts
at the launch instant; plus the v0.99 invariants — `RingRadius` still carries both its chord terms
and its `lanesPerRing >= 2` guard, `_laneFwd` exists, **no `_laneRot` field is back** (a shared spawn
rotation is what makes a ring smear), the spawn rotation is built from the lane's own ray, and
`DroneAltDeckM` defaults to 0. (2) **Frame model:** R35's launch replayed against a frozen
`LEGACY_ABEAM_M = 8000` — the pre-fix formula must reproduce the measured `origDist` medians
(24.0…7.4 then 44.0…98.5), the fixed one a uniform 6 km ladder. (3) **Ring model:** the radius at
1/2/3/8/16 lanes per ring, the in-deck chord ≥ `LaneM` and the **3-D pair separation ≥ `LaneM`** over
every N in 2..16 × five roster lengths × six deck spreads, `|pos − base|` equal across lanes at t=0
*and* after 31.5 km of card translation (with the shared-heading counterfactual asserted to smear,
so it is not vacuous), the `_slot`-overflow guard, and the **deck×airframe property** — every
airframe's deck counts within one of each other, both decks carrying the full roster at `N ≥ 2A`,
with `k % 2` and `(k / A) & 1` both asserted to fail as counterfactuals and the deck×arm 2×2
asserted balanced. Stdlib only, no
SDK. Run it after touching the lane geometry — reverting to a `Vector3` compiles fine and writes a
batch that scores fine.

## test-card-model.py — card (de)serialisation

**Card (de)serialisation (v0.90.1).** `python debugtests/test-card-model.py` does the same trick to
the `CARD-MODEL` region of `ScenarioPlayer.cs`: extracts the three model classes verbatim, compiles
them against the game's `Newtonsoft.Json.dll`, and round-trips **every file in `cards/`**. It exists
because `UnityEngine.JsonUtility` silently dropped the `Seg[] segments` field in both directions —
written cards had no `segments` key, read cards were rejected as "no segments", and **no disk card
loaded at all from v0.71 to v0.90**. Nothing caught it because the built-in cards are constructed in
C# and never touch a serializer, so every gate and batch went through the one path that could not
fail. Run it after ANY change to the card model, and read `[card] N card(s) bound (… X from disk)`
in the log as the in-game confirmation — `0 from disk` with files in the folder is the bug's shape.
It also checks one **synthetic** card carrying the fields no shipped card uses — `config`, and since
v0.91 a comma-list `airframe` and a non-zero `count` — because a field only the grid exercises is a
field the round-trip stops covering the moment the grid stops using it. The airframe string is
asserted byte-for-byte: `AirframeList` splits it per lane and `CountKeys` counts the same tokens, so
a serializer that reformatted it would change both the fleet size and which lane flies what.

## index-decompiled.py — the decompile index

`python debugtests/index-decompiled.py` builds a type/member index over the 0.34.1 decompile —
`--at NNNNN` reverses a `:NNNNN` citation back into a type and member (v0.97.1). 1655 types. The
cache lives **beside the decompile, never in the repo**; `--selftest` re-derives 16 citations.

## test-helo-probe-order.py — the helo probe fires in the drone call order

Two halves, because no compiler sees a call order. **(1) source:** the retry structure plus the
three liveness columns wired end to end. **(2) model:** the trigger reimplemented in Python over all
three real call orders, with the **pre-fix trigger as a counterfactual that MUST fail** the drone
case — without it the test would pass against the bug it exists to catch (v1.0.0).

## test-card-declared.py — the declared-zero rule

`Card.Unset` / `Card.Declared`. A **scan, not a case table**: it fails on ANY comparison against
zero of the four names, in the two files that own them, because one more `p.StartSpeed > 0f`
compiles, flies, scores, and silently restores the whole defect for that path. Stdlib, no SDK, no
game (v1.0.0).

## test-board-math.py — the run board's arithmetic

Extracts the BOARD-MATH region from `ScenarioPlayer.cs` **verbatim**, compiles it with the .NET SDK
and runs 23 cases (v0.90). It checks the shipped code, not a Python copy that would drift.

**v1.0.3 — `ROWS_CASES`, the no-truncation claim.** `BoardRows` joined the region when the board
stopped capping at 8 rows. The table asserts a full 16-lane fleet is shown whole on 720p, 1080p and
1440p; that a short viewport still truncates (rows drawn off the bottom edge are truncation you
cannot see); that it never returns 0 rows with lanes flying; and that a degenerate row height
(0, negative, NaN) shows **everything** — a progress instrument may fail long, never silently short.

## test-card-owns.py — the ownership rule

**A card's declared value beats the F1 value, and its silence does not.** That property is what
makes a card a repeatable experiment rather than a stimulus whose meaning depends on whatever the
config file happened to hold, and two field failures are why it is a test and not a convention:
`hs-hold` was designed around `Drone/DroneAltDeckM = 3000` while the live config held 0 (nothing
refused — the operator was told to hand-edit F1), and batch **R41**'s entire rotorcraft verdict was
withdrawn because `HeliForwardSpeed`/`HeliHoverSpeed` sat at stale v0.43 values no card declared.

Compiles three shipped regions verbatim: `CARD-OWNS` + `SPEC-GRAMMAR` (`ScenarioPlayer.cs`) and
`CARD-OWNS-SPAWN` (`TestDrone.cs`). The last is the half that was actually broken — a card's pins
are applied when the card *starts*, and the fleet is laid out before that, so `DeckSpreadM` and
`StaggerSec` cannot read the pin and must read the card. Cases cover both directions, bare vs
`Section/Key` spellings of one entry, a near-miss key, duplicate keys (first wins, matching
`PinShared`), whitespace, unparseable and empty literals (fail soft to the live value, never throw
— this runs on a hotkey path), and null config/entry (skipped, not dereferenced). **Runs under
`de-DE`** on purpose: a card file travels between machines and `"0.40"` must not become 40.

Plus a **source invariant** the compiled region cannot make about itself: no file in
`ScenarioPlayer.cs` / `TestDrone.cs` / `WTMouseAimPlugin.cs` may read `Cfg.DroneAltDeckM`,
`Cfg.DroneStaggerSec`, `Cfg.ScenarioForceEntry` or `Cfg.ScenarioEntryFuel` **bare** — the only legal
shape is as the fallback argument of the resolver, in the same statement. A resolver nobody calls is
exactly the state the harness was in before v1.0.3.

Its sibling is `check-card.py` CHECK 6: this one proves the harness **honours** a declaration, that
one proves each shipped card **makes** it. Neither sees the other's half, and a card that declares
nothing fails the same way as a resolver that ignores what it declares.
