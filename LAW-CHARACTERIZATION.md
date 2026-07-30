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
also now *cheap*: `DroneAirframe` is a comma list, drones fly cards, and a **baseline needs no A/B
arm — so concurrency is legal** (the `Cfg`-global arm limitation of #27 only bites A/B runs). Four
airframes in one batch, same wall clock as one.

**Q3 — Where is it worst?**
Ranked, per regime, against a noise floor. `compare-runs.py --summary` and `flightscore.py`'s
per-airframe spread produce this once Q1 and Q2 have supplied unsaturated data to rank.

**Q4 — Do the known defects cost anything measurable?**
#20, #21, #14, #23 are diagnosed and unfixed. Each needs its own A/B, serial, on a card where it can
actually bite. Deliberately last: three of the four are invisible in a railed regime, so running
them now would produce four confident nulls.

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

### Batch 4 — ATTRIBUTION (A/B, one drone, serial)

Only after Batch 1 says which cards are unsaturated. `DroneCount 1` is mandatory — with more, the
arm scheduler stands down and the batch flies one arm (#27, measured in R25). 8 replicates × ~40 s
is under 6 minutes, so serial costs nothing here.

| # | knob | card | why that card |
|---|---|---|---|
| E1 | `Control/BelowAlignSuppress` | `oblique-below` + `oblique-6` | mirror pair; `oblique-6` is the control and **must not move** |
| E1b | `Control/AlignRateLead` | same | separate arm — it is also a 64% roll-damping change, unattributable if armed together |
| E2 | `Control/RelativeTurnLead` | `sweep-slow` | the v0.83 A/B re-run *unsaturated*; the existing result measures the clamp |
| E3 | `Control/MarkerRateFeedForward` | `sweep-slow`, `sweep-creep` | above the rail its roll contribution is identically 0.0000 |
| E4 | #20 `PEffRevThresh` fix | `oblique-12`, `reversal` | needs the fix shipped behind its own checkbox first |
| E5 | #21 `lateralHold` rail fix | `sweep-slow` | same |

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
- **#23 will appear in every placed capture** — `rollRate ≈ −59`, `leadDeg` 7–14° at `tSeg=0.000`.
  Known, deterministic to 0.02, decays inside the `arm` segment. Not a batch failure.
- One card = one test. Tags unique per card. Adding or renaming a segment tag means updating
  `ScenarioPlayer.cs` **and** `scorecard.py` in the same change.
