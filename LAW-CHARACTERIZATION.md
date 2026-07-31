# Law characterization — the suite, and why it is this one

[`FLIGHT-PROTOCOL.md`](FLIGHT-PROTOCOL.md) validated the **instrument** (gates A–D, all passed
2026-07-29). This file is what to point the instrument at. It supersedes that document's
"Experiments" section, which was written when only one card could be flown and assumed a human in
the seat.

**Read the first section before the plan.** The plan only makes sense once you accept how little we
actually know.

---

## 1. Where we actually are

> We have 19 test cards. **One** has ever been flown. It was flown on **one** airframe. And that
> card is **saturated**, so it measures the airframe, not the law.

| | |
|---|---|
| cards in `cards/` + built-ins | **19** |
| cards ever flown | **1** (`fixedwing-sweep`) |
| airframes ever flown *on a card* | **1** (`Multirole1` = KR-67 Ifrit) |
| airframes the ONE-LAW rule names | **4** (light jet at high q, loaded jet near α, STOL trainer, hovering helo) |

And on that one card, in that one regime, three of the law's own mechanisms are measurably **inert**:

| mechanism | measured | meaning |
|---|---|---|
| bank pipeline weight | `blendRailPct` **93–100%** | `lateralHold` rails, `blendWeight = 1`, so `eFine` weight is exactly zero |
| fine integrator | `iPitch` **±0.001** vs a 0.12 cap, for a whole 30 s turn | the anti-residual term never winds |
| AoA path | `aoaLimiterActivePct` **0 in every capture ever taken** | the turn-rate cap binds first; the α machinery has never fired |
| actuator | `bankClampActivePct` **96.9%**, `turnRateCapActivePct` **96.9%** | at the stop; a gain change *physically cannot* move the metrics |

That last row is the one that matters most. **A saturated segment cannot show a gain change.** Every
A/B we have run — v0.83's `RelativeTurnLead`, crewed and on the drone — was run against an actuator
at its stop. Those results are real but they measure the *clamp*, not the loop.

So the honest state is: we have a validated instrument, a nearly complete card grid, and almost no
data. What was missing was the ability to run the grid unattended. **That landed in v0.90.** The
suite below is not new work to invent — it is the grid that already exists, finally runnable.

---

## 2. The four questions, in dependency order

Each answers the one below it. Do not reorder.

**Q1 — Which parts of the law are even live?**
Not "how good is it." A law whose mechanisms are railed or dormant cannot be tuned, because you
would be tuning nothing. The first product is not a score, it is a **liveness map**: per card ×
regime, which mechanisms are active, which are railed, which never fire at all. `scorecard.py` now
emits a `RAILED` warning naming the rail and its value, so this falls out of the batch for free.

**Q2 — Is it one law, or one law that happens to suit the Ifrit?**
This is the largest open risk in the project and it sits directly under the core design rule. It is
also now *cheap*: `DroneAirframe` is a comma list, drones fly cards, and **every lane is concurrent —
baseline and A/B alike**, since #27 closed in v0.94 (the swept arm is per-aircraft state, so the
scheduler no longer stands down under a fleet). Ten airframes in one batch, same wall clock as one.

**Q3 — Where is it worst?**
Ranked, per regime, against a noise floor. `compare-runs.py --summary` and `flightscore.py`'s
per-airframe spread produce this once Q1 and Q2 have supplied unsaturated data to rank.

**Q4 — Do the known defects cost anything measurable?**
#45, #21, #14, #23 are diagnosed and unfixed; **#20 is diagnosed and re-scoped to hygiene** — it is
no longer an experiment (§7). Each of the rest needs its own A/B, but **no longer serially**: #27
closed in v0.94. Deliberately last for #21/#14/#23 — they are invisible in a railed regime, so
running them now would produce confident nulls. **#45 is the exception and is already measured**
(R32, on a card that exists), which is why the roadmap puts it first among the law defects.

---

## 3. The airframe roster, and the constraint nobody has hit yet

13 spawnable aircraft. `Encyclopedia.TryGetPrefab` is an exact, **case-sensitive** dictionary lookup
(note `trainer` is lowercase). A typo costs the whole launch if `DroneAirframe` holds one key, and
only that lane if it holds a comma list — **so always use a comma list.**

**The constraint: a card's `startSpeed` is written into the aircraft as a hard velocity.** Give an
airframe an entry condition above its own maximum and the card measures deceleration, not tracking.
The 250 m/s cards are simply invalid for a third of the roster.

| jsonKey | aircraft | mass kg | max m/s | corner | 250 m/s cards? | role in this suite |
|---|---|---|---|---|---|---|
| `Fighter1` | FS-12 Revoker | 8 680 | 401 | 180 | **yes** | **light/agile fighter** — the high-q case |
| `Multirole1` | KR-67 Ifrit | 16 040 | 417 | 180 | **yes** | incumbent; canard remap; every prior capture |
| `trainer` | T/A-30 Compass | 6 111 | 294 | 160 | **yes** | light, conventional — the control against the canard |
| `FastBomber1` | Alkyon AB-4 | 34 100 | 479 | 180 | **yes** | **heavy** — 5.6× the trainer's mass at the same corner |
| `Darkreach` | SFB-81 | 54 311 | 279 | 180 | marginal | heaviest; gLim 5.0 |
| `SmallFighter1` | FS-20 Vortex | — | 415 | 180 | yes | compact multirole |
| `EW1` | EW-25 Medusa | — | 286 | 120 | yes | STOVL |
| `VTOLTrainer1` | VT-7 Vagrant | 5 890 | 294 | 160 | yes | tiltwing, wing-borne |
| `CAS1` | A-19 Brawler | 10 620 | **206** | 200 | **NO** | heavy strike — needs its own entry |
| `COIN` | CI-22 Cricket | 3 100 | **142** | **90** | **NO** | **the STOL trainer case** (`stol-*` cards, 90 m/s) |
| `QuadVTOL1` | VL-49 Tarantula | 28 900 | **149** | 120 | **NO** | quad tiltrotor; gLim 3.0 |
| `AttackHelo1` | SAH-46 Chicane | 7 550 | 100 | — | **NO** | **the helo case** |
| `UtilityHelo1` | UH-90 Ibis | 7 300 | 134 | — | **NO** | helo |

`alphaLimiter` per airframe is **not statically readable** — the FBW blocks span 10…27° but cannot
be attributed to airframes from the assembly. Every capture writes it into its own
`.airframe.json` sidecar, so **Batch 1 produces this table as a side effect.** That is a reason to
run Batch 1 first, not a reason to wait.

---

## 4. The suite

### Batch 1 — LIVENESS + BASELINE, fast set — *run this first*

Four airframes spanning **6 111 → 34 100 kg at the same corner speed**, across the whole fixed-wing
grid. Answers Q1 and Q2 together, and it is the single highest-information run available.

```
Drone/DroneEnabled     true
Drone/DroneCount       4
Drone/DroneAirframe    Fighter1, Multirole1, trainer, FastBomber1
Scenario/ScenarioRepeat        8
Scenario/ScenarioArmToggle     (empty — no A/B, which is what makes 4 drones legal)
Scenario/ScenarioCardSet
  oblique-05, oblique-2, oblique-dz, oblique-6, oblique-12, oblique-below,
  sweep-creep, sweep-slow, sweep-step, sweep-lowq, fixedwing-sweep
```
Press the spawn key. Nothing else. **≈ 70 min unattended**, 352 captures.

`ScenarioCardSet` overrides the checkboxes and fixes the order, so the queue interleaves cards
rather than blocking them — a session drift then spreads across every card instead of loading onto
the last one. Leave the alpha cards out: they are 8 000 m and belong to Batch 3.

**Read it with:**
```
python debugtests/compare-runs.py --summary <dir>/mouseaim-rec-v0.90.0-*.csv
python debugtests/flightscore.py <dir>/mouseaim-rec-v0.90.0-*.csv
```

**What each outcome means:**
- `RAILED` on a card **that is not `fixedwing-sweep`** → the unsaturated cards did not do their job;
  the lag bands were sized from `K ≈ 1.28 /s` measured on the Ifrit alone, and a different airframe
  lands somewhere else. Re-size, do not conclude anything about the law.
- `blendRailPct ≈ 0` on `sweep-slow` / `sweep-creep` → **the first unsaturated sustained data this
  project has ever had.** Everything in Q3 and Q4 unlocks here.
- Metrics that agree across the four airframes → the ONE-LAW rule is holding.
- Metrics that scale with **mass** → the law is missing an inertia normalization, which is
  `GENERALITY-REVIEW.md` finding 5 (roll is the least normalized axis, no roll-authority probe) with
  numbers attached at last.

### Batch 2 — the STOL / low-q set

```
DroneAirframe   COIN, trainer, COIN, trainer      (2 lanes each; COIN is the low-limit case)
ScenarioRepeat  8
ScenarioCardSet stol-steps, stol-sweep
```
≈ 25 min. `trainer` is in there as the control: same cards, an airframe with 2× the mass and a
higher limiter. If the law needs different gains for `COIN`, this is where it shows.

### Batch 3 — ENVELOPE: can the law use the airframe at all?

```
DroneAirframe   Fighter1, Multirole1, FastBomber1, trainer
ScenarioRepeat  8
ScenarioCardSet alpha-steps, alpha-sweep
```
≈ 20 min. **Gate on `aoaAboveCeilingPct > 0` at all.**

`alpha-steps` uses ±45° pitch *steps* — a demand the turn-rate cap does not bound — which is why it,
not `alpha-sweep`, is the discriminating card. If AoA still never approaches the limiter, that is
**not a card failure, it is the finding**: the law caps turn rate so far below the airframe's
capability that a whole region of the envelope is unreachable by construction. On a 9 g airframe
peaking at 7.7° of AoA against a 27° limiter, that is the most consequential thing this suite could
turn up, and it would reframe the next month of work from "tune the loop" to "raise the ceiling".

### Batch 4 — ATTRIBUTION (A/B, **fleet-wide since v0.94/v0.96**)

Only after Batch 1 says which cards are unsaturated. **The one-drone rule is gone**: #27 closed in
v0.94 (the swept arm is per-aircraft state read through the controller), so every lane runs its own
independent ABBA off its own queue index. In v0.96 all five `e*` cards and both `alpha-*` cards
therefore name the **eight fixed-wing keys that clear the v0.92 gate at 250 m/s** — Fighter1,
Multirole1, SmallFighter1, trainer, VTOLTrainer1, EW1, FastBomber1, Darkreach — and `"count": 1` was
removed. That is **8 lanes × repeat 8 = 64 captures per experiment at ONE lane's wall clock**, read as
eight independent A/Bs, since `compare-runs.py` groups by (airframe, card, arm) and refuses to pool.
Lanes fly concurrently, so lane count does not cost wall clock; replicates per lane does.

**Two hand-set globals remain and both are foot-guns.** `alpha-steps`/`alpha-sweep` declare no
`repeat`, so they fall back to `Scenario/ScenarioRepeat` — set it to **8** for those batches. And
neither they nor `oblique-above-c` declares an `armToggle`, so a value left in
`Scenario/ScenarioArmToggle` from a preceding `e*` batch **would be used**, sweeping a knob nobody
asked to sweep: clear it. Adding `"repeat": 8` to both `alpha-*` cards is the correct fix and breaks
no comparability — neither has ever been flown. `cards/TOMORROW.md` is the ordered runbook.

| # | knob | card | why that card |
|---|---|---|---|
| E1 | `Control/BelowAlignSuppress` | `oblique-below-c` + `oblique-6-c` + **`oblique-above-c`** | **v0.96: a 3-POINT AXIS, not a mirror pair.** `alignFracH` at −20 / 0 / +20 makes the belowness response a line rather than a difference; `oblique-6-c` is the control and **must not move**. All three arms want the SAME session to be readable as an axis. |
| E1b | `Control/AlignRateLead` | same | separate arm — it is also a 64% roll-damping change, unattributable if armed together |
| E2 | `Control/RelativeTurnLead` | `sweep-slow` | the v0.83 A/B re-run *unsaturated*; the existing result measures the clamp |
| E3 | `Control/MarkerRateFeedForward` | `sweep-slow`, `sweep-creep` | above the rail its roll contribution is identically 0.0000 |
| E4 | **#21** roll-to-align channel (`lateralHold` ⇒ `blendWeight`) as the arm — the **precursor** | `darkreach-05` geometry | R32 §4: 34–56° of `targetBank` at \|`azErr`\| < 5° on a card whose largest step is 0.35°, on **0 of recs 01–31** and **12 of recs 32–63**. Those 31 clean replicates are the baseline, on the same airframe and card. **Fly this before E5** — see #45. |
| E5 | **#45** `schedFloor`, expressed relative to a probed quantity | same | Only after E4. Fixing the stand-down first makes *some* departures survivable, which is worse than a departure that is legible. |

**#20 no longer has a row here, deliberately.** Its A/B was scoped as "unlock a dormant branch" and
that scoping is retired (§7): the fix moves 0.45% of corpus rows, all at the boundary, so an A/B
would report a null and the null would read as "the diagnosis was wrong". Ship it as hygiene, do not
schedule an experiment for it.

Each card can now carry its own `armToggle` and `repeat`, so **each row above should become a card
file** — then a run is one checkbox and the artifact says what it was. That is the v0.90 payoff and
it is the next thing to build.

### Batch 5 — ROTORCRAFT — **blocked, and here is the unblock**

`rotor-hover` and `rotor-bob` are `startSpeed: 0` = ungated: no placement, therefore **no reset
between replicates**, therefore not exchangeable, therefore not runnable by the harness. A drone
also spawns at `DroneSpawnSpeed` with wing-borne velocity, which is not a hover.

The unblock is a **hover entry condition**: `PlaceOnCondition` needs a branch that writes near-zero
velocity at a low `startAlt` and lets the helo settle through the `arm` segment. The pieces exist —
the placement already moves the whole part assembly correctly and zeroes `velocityPrev` — so this is
a new entry *mode*, not new physics. It is genuinely the riskiest item here (writing physics state
to a rotorcraft), so it wants someone watching the first one. **Do not run it unattended first.**

---

## 5. What changes about the previous plan

| previous | now | why |
|---|---|---|
| E1/E2/E3 as the next step | **Batch 1 first** | all three A/B a law whose bank pipeline is 93% dead. Baseline before attribution. |
| "8 replicates, multiple of 4 for ABBA" | only for **Batch 4** | a baseline has no arm, so ABBA is irrelevant and 4 drones are legal |
| one airframe implied | four per batch | the cheapest coverage available, and the rule's own requirement |
| `fixedwing-sweep` as the workhorse | **`sweep-slow` is the workhorse** | the workhorse was saturated; R22/R24/R25 all confirm 96.9% |
| E3 = "test the AoA path" | E3 = "prove the envelope is reachable" | if `aoaAboveCeilingPct` is 0 again, the law is the finding, not the card |

## 6. Standing rules for every batch

- **A refusal is always a log line.** After any batch: `grep '\[drone\]\|\[card\]' LogOutput.log`.
- **Never pool across airframes.** `compare-runs.py` refuses, and now also refuses across cards.
  Heed it rather than working around it.
- **Read `RAILED` warnings before any metric.** A railed segment is *no signal*, not a bad score.
- **#23 is NOT a fixed signature, and it is NOT harmless.** The old claim — "`rollRate ≈ −59`,
  `leadDeg` 7–14° at `tSeg=0.000` in every placed capture" — was wrong, and R28 measured it: across
  384 captures the median `|rollRate|` at `tSeg=0` is **0.725**, not 59, and **0 of 384** have
  `leadDeg` in 7–14°. **R32 then showed that distribution is bimodal and R28 saw only its lower
  mode**: over 58 placed captures, median 0.753 but **19 of 58 above 5** (max 54.2), `|leadDeg|` to
  **314°**, and **`|outP|` railing at 1.000 on 15 of 58 placement ticks**. The magnitude is set by
  the attitude the *previous* replicate ended in, so a departed replicate hands the next one a
  full-authority spurious command on tick zero. Do not treat the absence of a big `rollRate` as
  evidence the reset works, and **do not carry forward "it decays before the scored segment" as a
  general claim** — it does not on a heavy, low-authority airframe. Full entry: §7 #23.
- One card = one test. Tags unique per card. Adding or renaming a segment tag means updating
  `ScenarioPlayer.cs` **and** `scorecard.py` in the same change.

---

## 7. The numbered backlog

Sections 2 and 6 cite these by number. This is where the numbers resolve. It is the **durable**
list — the session task list holds only what is in flight right now, because a backlog rendered
every turn is a backlog nobody reads.

**THIS TABLE IS THE ONLY AUTHORITY ON WHAT A `#n` MEANS.** Reconciled 2026-07-31 after two agents
assigned numbers concurrently. Three rules, because all three were broken at once:

1. **A number is allocated here first, and it is `max(existing) + 1`.** Never reuse a gap — the gaps
   (15–18, 22, 24, 26, 28, 32, 35, 40, 42, 43) are retired numbers, and a reused one reads as a
   *different* item in every document that already cites it. Highest in use is **#46**.
2. **`GENERALITY-REVIEW.md` findings are a SEPARATE namespace.** `R32-FINDINGS.md` cites
   "`GENERALITY-REVIEW.md` #16" and "#18"; those are that file's finding numbers, not backlog
   numbers, and backlog #16/#18 do not exist. Write the filename with the number or don't write it.
   The backlog items that *correspond* to those findings are **#21** (finding 16) and **#45**
   (finding 18).
3. **The #45 collision is resolved as follows: #45 is the AoA-schedule authority failure, and
   nothing else.** The "belowness axis" work that was also being called #45 is **not a backlog item
   and needs no number** — it is experiment **E1** in §4 Batch 4, its card (`cards/oblique-above-c.json`)
   is already written, and its runbook is `cards/TOMORROW.md` §8. A shipped card with a runbook entry
   is not backlog. Do not re-number E1 into this table.

Ordered by when it unblocks something, not by severity.

### Law defects — diagnosed, unfixed

These are Q4. All four are invisible in a railed regime, which is why the baseline comes first.

| # | Defect | Where | Why it waits |
|---|---|---|---|
| **#45** | **AoA-schedule AUTHORITY FAILURE — the first genuine LAW defect in a while; everything else found lately was instrument.** `schedFloor = 0.3f` (`ChaseController.cs:1255`) terminates the AoA-utilization schedule's range at the same **absolute** place for a 27° ceiling on an 8.7 t `Fighter1` and a 10° ceiling on a 105 t `Darkreach` — a hardcoded constant deciding an outcome, i.e. the ONE-LAW smell. Measured in R32 (63 captures, 37,868 rows, 18 departures, 3 pilots killed): `qSched` is exactly **0.300 on 100.0%** of the 2,314 rows past \|AoA\| 20°, against **0.0%** on all 31 clean pre-onset replicates of the same card and airframe. At the floor the law still commits 30% of its P demand into a plant delivering **7.7×** the commanded rate in the **opposite** direction (median on departed captures; p90 13.0×, max 28.2×; clean captures 1.56×). Sibling constants: `:1296` `Max(0.3f, aoaGateUp)` is the same shape; `:1152` is defensible because it mirrors the game's own `:64861` clamp. **The deeper statement:** the law's entire response to a non-responding plant is **five terms that each REDUCE authority** — the two `qSched` floors, the `omegaMax` floor, `pErrTerm *= _pitchEff` below `PEffRevThresh` (`:1927`), and `aoaRecover *= _pitchEff` (`:1557`), which scales the one term documented at `:1543` as "the term that flies the nose back INSIDE the envelope" by an estimator reading 0.036–0.144 for the whole departure. **Nothing in `Apply` increases authority or changes strategy.** On nine of ten airframes the airframe's own stability covers the gap; on the Darkreach it does not and the aircraft descends 3,000 m. | `ChaseController.Apply` (AoA-utilization schedule) | **HIGH, but SEQUENCED — do not fix the floor first.** R32 §4 shows the railing is downstream of a precursor: 34–56° of `targetBank` at \|`azErr`\| < 5° on a card whose largest step is 0.35°, on 0 of recs 01–31 and 12 of recs 32–63, appearing 1–2 replicates before the departure in every lane. Finding 16 (`lateralHold` rails ⇒ `blendWeight` = 1) is the standing candidate — i.e. **#21**. Fixing the stand-down first would make *some* departures survivable, which is worse than a departure that is legible. Fly in this order — **§4 Batch 4 rows E4 then E5**: (1) a precursor-isolation card on the `darkreach-05` geometry with the roll channel as the arm — recs 01–31 give it a 31-replicate clean baseline on the same airframe and card; (2) then an A/B on `schedFloor` expressed **relative to a probed quantity** (`omegaMax`, `_fbwMaxPitchVel`, or the alpha ceiling `aoaUtil` already normalises by); (3) cheap side-check: `EW1` shares `assist=0`, `maxPitchAngVel` 0.3 and `alphaLimiter` 10 at a quarter of the mass and has never flown this card. **Explicitly NOT the fix: a mod-side G-limiter.** It protects nothing (the airframe cannot be over-G'd — see below), it masks the defect (the high-G row is the readout, not the cause), and it would be a **sixth** de-authorizing term on a law whose problem is that it already has five. `GENERALITY-REVIEW.md` finding 18; `debugtests/R32-FINDINGS.md` §5–§6. |
| **#20** | `PEffRevThresh` floor branch: the `>=` makes it unreachable **from the v0.67 self-probe path** | `ChaseController.Apply` | **PREMISE CORRECTED (v0.96); re-scoped from experiment to hygiene.** "Unreachable" is true only of the self-probe path — the latch-breaker LPFs toward 0.15 from below and asymptotes, so `>=` never trips *from there*. It must NOT be read as "`_pitchEff` never goes below 0.15": measured over all **1,032** archived captures (627,110 rows, R28–R32), **28,209 rows = 4.50%** sit below the threshold, min **0.000**, across **89** captures on **two** fixed-wing airframes (`Darkreach` 27,622, `FastBomber1` 587). Those are genuine reversed-plant measurements where the no-floor branch is the CORRECT behaviour. (This also retires the "5.2% / 8 captures / three airframes" figure, which reproduces against no batch.) The defect's real signature is stronger than the occupancy number: **2,811 rows read exactly `0.150` and only 8 read anything above it up to 0.152** — the LPF parked on its own target, exactly as the closed form predicts. So `>=` → `>` is worth doing and cannot regress anything, but it moves **0.45%** of corpus rows, all at the boundary. **Ship it as hygiene behind its own checkbox — do not plan an experiment around it**; an A/B written as "unlock a dormant branch" will report a null, and that null will read as "the diagnosis was wrong" when it is not. |
| **#21** | `lateralHold` rails at 7.5° and zeroes the whole bank pipeline | `ChaseController.Apply` | Bank pipeline is ~93% dead in the sweep family, so an A/B there measures nothing. |
| **#14** | `predFloor` hard 0.30 step → wants a continuous lead-confidence blend | `ChaseController.Apply` | ONE-LAW smell (a constant, not a probed quantity), but not yet shown to cost anything. |
| **#23** | Placement-tick reset: `ChaseController.Forget(ac)` does not take effect on the placement tick — `rollRate` −59, `leadDeg` 7–14° against a 0.04° error | `ScenarioPlayer.PlaceOnCondition` | **SCOPE CORRECTED AGAIN (R32); "harmless to results so far" is RETIRED.** R28's "median \|rollRate\| 0.725, 0 of 384 in the leadDeg band" measured only the **lower mode of a bimodal distribution**. R32's 58 placed captures: median **0.753** (R28 reproduced) but **19 of 58 above 5**, max **54.2**; \|leadDeg\| max **314°**; \|headingRateFilt\| max **483 °/s**; and **\|outP\| rails at 1.000 on 15 of 58 placement ticks**. Magnitude is set by the attitude the PREVIOUS replicate ended in, so a departed replicate hands the next one a full-authority spurious command on tick zero — which on a 105 t airframe with `maxPitchAngularVel = 0.3` departs it *inside* the 6 s `arm`. It is what makes the Darkreach cascade self-sustaining. **Still deliberately unfixed, for the unchanged reason: do NOT guard the finite difference against a discontinuity** — that would clean `rollRate` and leave `headingRateFilt`/`leadDeg` alone, making the symptom look fixed while hiding the cause. Untraced lead: `PlaceOnCondition` has two call sites and only one was followed. |

### Instrument — the feedback loop itself

| # | Item | State |
|---|---|---|
| **#33** | **Pillar 1**: retreat integral — `retreatDeg`, `retreatEpisodes`, monotonicity index. Does the nose ever move *away* from the commanded direction, or approach and then recede? | Not started. No new CSV column needed — it is derivable from `off`. |
| **#36** | **Pillar 2 rework**: authority *used* vs authority *needed*. v0.92's `authorityUsedFrac` answers "used", which is why its SLACK branch had to be gated to two card types — a 0.5° step legitimately uses 4% of authority. The normalizer wanted is `omega_target = min(omega_avail, off/tau)`. | Gated stopgap shipped; the real version blocked on nothing but time. |
| **#38** | Card **altitude budget** unchecked at preflight. Measured on R27: `oblique-below` loses 4323 m worst-replicate, `sweep-lowq` 3069 m — both would finish **below sea level** from a 1500 m start. `FastBomber1` is the heaviest sinker on every card. | Same shape as v0.92's speed gate; no per-airframe bound exists, so the check is card-vs-floor, not card-vs-airframe. |
| **#39** | `startSpeed: 0` means **both** "hover" and "not specified". | **Blocks the rotorcraft phase.** Fix with a nullable `float?` — Newtonsoft distinguishes absent from explicit 0, which `JsonUtility` could not. Do it together with the hover entry condition. |
| **#25** | `RecordKey` silently fails to stop a capture when the local aircraft is gone. | Minor; operator-facing only. |
| **#19** | Drone **loadout** matrix — the `Spawn` parameter is a `Loadout` object, not a name. | Blocked on one in-game dump. The sidecar already records resulting stations/masses/drag, so nothing on the analysis side changes when it lands. |

### Closed by the last three releases

Kept because they explain why there had been no law progress: every one was an **instrument**
defect, and all three were found inside ~24 h.

- **#29** — no disk card had loaded *at all* from v0.71 to v0.90. `JsonUtility` silently dropped
  `Seg[]` in both directions. Nothing caught it because the built-in cards never touch a serializer.
- **#30** — two-seat airframes double-stepped the card clock **and** the control law. `Aircraft.pilots`
  is an array and every `Pilot` registers with `JobManager` independently. Fixed v0.90.1.
- **#37** — `frameMs` measured nothing: `Time.unscaledDeltaTime` read inside `FixedUpdate` returns
  `fixedUnscaledDeltaTime`, a constant. All 223,899 rows of R27 read exactly 16.70 ms and the column
  missed a logged 119 ms hitch. Fixed v0.92.1 by sampling in `Update()`.
- **#31** — a card now owns its own fleet (`airframe` comma list + `count`). v0.91.
- **#34(b)** — a lane whose airframe cannot fly the card's entry speed is refused **pre-spawn**, off
  `Encyclopedia.Lookup`, with no aircraft instance created. v0.92. Part (a) is `startSpeedCorner`.
- **#27** — concurrent A/B. The swept arm moved off `Cfg` and onto the aircraft (`ChaseController.Arm()`
  reading a per-aircraft assignment), so N lanes fly N arms in the same instant and the scheduler no
  longer stands down. v0.94, verified by `debugtests/test-arm-schedule.py`. Attribution is now one
  fleet launch instead of ten serial ones.
- **#41** — `startSpeedCorner` resolved against **the AI's** `cornerSpeed`, not the flight model's, so a
  corner-relative card entered the roster across a 2.2× spread of true FBW corner while claiming a
  uniform aerodynamic state. `TestDrone.TryEnvelope`'s `Corner` now reads
  `ControlsFilter.FlyByWire.cornerSpeed` off the prefab pre-spawn, fail-soft to the old value with a
  once-per-airframe warning. v0.96. `AIRFRAMES.md` trap 6 stands as the record of why the two fields
  are different quantities. **Consequence for planning: corner-relative captures from R29 and earlier
  are NOT poolable with later ones** — do not re-baseline a batch across that line.
  `cards/darkreach-05.json` deliberately keeps its ABSOLUTE 171 m/s; at `0.95x` Darkreach is now
  95 m/s (1.42× Vstall, the only lane under 1.9×), so converting it would break the R29 departure
  reproduction it exists for. Its `note` explaining why it is absolute now describes a **fixed**
  defect and should be reworded, not acted on.
- **#46** — the **`SplitSpec` one-slash divergence**. `ScenarioPlayer.SplitSpec` split on the FIRST
  slash and only tested that both halves were non-empty, so it read `"A/B/C"` as section `A` / key
  `B/C`, while `scorecard.py`'s `split_spec` refused it — two definitions of one grammar, disagreeing,
  with the *offline* half being the one that runs before a batch flies. Neither side was dangerous
  (the mod's lookup then found no such entry and warned by name, fail-soft), which is why it was
  pinned as a third expected column in `debugtests/test-spec-grammar.py` rather than silently
  reconciled. Closed in v0.96 by a **one-line** refusal in the C# (`slash != spec.LastIndexOf('/')`),
  so the test collapsed to ONE shared case table. **Verified here, not assumed:**
  `python debugtests/test-spec-grammar.py` → `ok spec grammar (scorecard.py copy)` /
  `ok spec grammar (C#)`. Re-run it after touching either half.
- **#44** — damaged replicates are now **self-identifying and auto-aborted**: CSV column `dmgFrac`
  (65), the sidecar's `detachedRatioAtStart`, an any-detachment abort in `ScenarioPlayer.Tick`, and a
  DAMAGED warning from `scorecard.py`. v0.96. **Standing rule for reading a batch: DROP damaged runs,
  do not covary them out.** Unlike session age, damage is not a continuous nuisance variable — it
  changes the airframe, so a damaged replicate is not a noisy sample of the same thing.

### Also on the list, non-blocking

- **The never-flown grid cells no longer need a hand-maintained list.** `index-captures.py --cards`
  loads `cards/*.json` as dimension tables, so "which cells have we never flown?" is a `LEFT JOIN`
  (Q10 in `debugtests/CAPTURES-DB.md`). Today that enumerates the `alpha-*`, `rotor-*` and `e*` cards.
- **Read a batch's completeness before scoring it.** `index-captures.py --check` flags a dead lane;
  R29's Darkreach flew **9** captures against 48 for every other lane, which is invisible in every
  aggregate view and would have been read as a real per-airframe effect.
