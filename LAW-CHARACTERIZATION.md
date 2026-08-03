# Law characterization — the suite, and why it is this one

The **instrument** was validated by gates A–D (R22–R25, all passed 2026-07-29); that record now lives
in [`LAW-LEDGER.md`](LAW-LEDGER.md) I1–I3. This file is what to point the instrument at.

**Read the first section before the plan.** The plan only makes sense once you accept how little we
actually know.

---

<!-- SECTION-INDEX:BEGIN -->
## Index — read a slice, not the file

**~58 KB.** Read the row you need, not the file.

| you want | section | line |
|---|---|---|
| how little we actually know | [1. Where we actually are](#1-where-we-actually-are) | 30 |
| the questions, in dependency order | [2. The four questions](#2-the-four-questions-in-dependency-order) | 152 |
| which airframe can fly what | [3. The airframe roster](#3-the-airframe-roster-and-the-constraint-nobody-has-hit-yet) | 181 |
| **what to fly next, batch by batch** | [4. The suite](#4-the-suite) | 214 |
| the rules every batch obeys | [6. Standing rules for every batch](#6-standing-rules-for-every-batch) | 377 |
| **what a `#n` means / the backlog** | [7. The numbered backlog](#7-the-numbered-backlog) → its own item index | 398 |

Airframe envelopes are in [`AIRFRAMES.md`](AIRFRAMES.md); findings are in
[`LAW-LEDGER.md`](LAW-LEDGER.md); this file is the **plan**.

<!-- SECTION-INDEX:END -->

## 1. Where we actually are

**REWRITTEN 2026-07-31 against `debugtests/captures.db`. The paragraph that stood here — *"19 cards,
one ever flown, on one airframe, and it is saturated"* — described the project as of R21 and was
still being read as current fifteen batches later. Every number below is `index-captures.py --stats`
or a query named in place; re-derive rather than trusting this table's age.**

| | |
|---|---|
| cards in `cards/` + built-ins | **39** (**36** on disk + `fixedwing-v2`, `rotorcraft-v2`, `fixedwing-sweep`) — was 34 |
| cards ever flown | **38** distinct ids (was 24) |
| disk cards never flown | **4** (was 12) — `e1-below-control`, `e1-below-suppress`, `e1b-align-lead`, `oblique-above-c`. **The whole `e1*` belowness axis is what is left unflown**, and it is §4 Batch 4's E1. Both `alpha-*`, both `rotor-*`, both `stol-*`, `e2` and `e3` have now flown (R39) — but see the ONE-LAW row: flying is not measuring. |
| captures / scored segments / recorder rows | ~~1 681 / 5 903 / 999 942 across 26 tagged batches R1–R33~~ → **2 576 / 11 015 / 2 117 598** across **31** tagged batches **R1–R40** (2026-08-02). Re-derive with `--stats`; do not trust this cell's age. |
| airframes ever flown *on a card* | **13** (was 10) — all ten fixed-wing keys plus `AttackHelo1`, `UtilityHelo1`, `QuadVTOL1` since R39 |
| airframes the ONE-LAW rule names | **4** — **2.5 of 4 covered as of R41 (v1.0.0).** The **hovering helo** now flies the real v0.58 branch (`heloOk` true on 108,987/108,987 rows, ledger H1) and the law is excellent there — but only **`QuadVTOL1` actually hovered**; the other two rotorcraft never did, so hover rests on one airframe (ledger H5). The **STOL trainer** case is still unmet for the fast jets: the R39 card flew 340–381 m/s (ledger X25) and R41's fixed card holds 85–178, which is the intended condition for `COIN`/`CAS1` but puts `Fighter1`/`Multirole1` at 128 and `FastBomber1` at 160. The **loaded jet** has never been flown at all — a card cannot set stores (backlog #19). See `LAW-LEDGER.md` O9. |
| segments RAILED corpus-wide | ~~285 of 5 903 = 4.8%~~ → **406 of 8 294 = 4.9%** (non-excluded, 2026-08-02) |

**Saturation is no longer the project's defining constraint — it is a property of five cards.**
Per-card mean over every scored segment (`GROUP BY card`, `excluded = 0`):

| card | segs | railed | `blendRailPct` | `bankClampActivePct` |
|---|---|---|---|---|
| `fixedwing-sweep` | 99 | **86** | 68.2 | 86.7 |
| `sweep-lowq` | 32 | **32** | 96.3 | 88.6 |
| `darkreach-05` | 250 | **74** | 30.3 | 26.7 |
| `sweep-slow` / `sweep-creep` / `sweep-step` | 160 | **0** | **0.0** | **0.0** |
| the whole `oblique-*-c` family (R29+R33) | 1 772 | **6** | ≤ 2.7 | ≤ 1.3 |

§5's prediction — *"`sweep-slow` is the workhorse, `fixedwing-sweep` was saturated"* — is confirmed:
`blendRailPct` is exactly 0.0 on all 160 `sweep-slow`/`-creep`/`-step` segments. **The unsaturated
data Q3 and Q4 were waiting on exists.** What is now scarce is not clean captures, it is *attribution*
— five of the twelve never-flown cards are the `e*` A/B set.

**The three "inert mechanism" claims, re-checked:**

| mechanism | the old claim | status today |
|---|---|---|
| bank pipeline weight | `blendRailPct` 93–100% | **true only of `fixedwing-sweep`/`sweep-lowq`.** Corpus-wide it is 0.0 on the modern oblique/sweep families — #21's rail is *dormant*, not permanent. R28: 0 of 1 344 healthy segments; R29: 0 of 1 740 |
| fine integrator | `iPitch` ±0.001 vs a 0.12 cap | **unchanged** (R21, 30 s turn) — but v0.83's `IntegralStallGate` was the fix for it and has never been A/B-ed unsaturated (`e*` set, never flown) |
| AoA path | *"`aoaLimiterActivePct` **0 in every capture ever taken**"* | **FALSE — see below.** This is the correction that reopens Batch 3 |

Only the fourth row of the old table survives intact, and only where it applies: on a **railed**
segment a gain change physically cannot move the metrics, so read `RAILED` before any number.

### The AoA path has fired, repeatedly, and once cleanly

`aoaLimiterActivePct` is non-zero on **66** (run, airframe, tag) cells. Of those, **23 contain no
railed segment at all**; **32** have *some* unrailed segment — see the note on the two counts below.
Highest-occupancy unrailed cells:

| run | airframe · tag | `aoaLimiterActivePct` | n | cell fully unrailed |
|---|---|---|---|---|
| **R33** | **`Darkreach` · `obDR6`** | **100.0%** | 4 | **yes** |
| R26 | `FastBomber1` · `turn360` | 82.4% | 4 of 8 | no — other 4 railed |
| R33 | `Darkreach` · `obDL6` | 76.8% | 4 | **yes** |
| R11 / R18 | `trainer` / `Multirole1` · `az150` | 55.8% / 55.4% | 1 / 2 | yes |
| R33 | `Darkreach` · `obUL6` | 46.5% | 4 | **yes** |
| R29 | `Darkreach` · `obDL12` | 37.1% | 2 | yes |
| R29 | `trainer` · `obUL12` | 11.9% on **8 of 8** replicates | 8 | yes |

**The 23-vs-32 distinction, because both numbers are correct and they answer different questions.**
`WHERE railed = 0 GROUP BY (run, airframe, tag)` keeps the *unrailed segments of a partly-railed
cell* and returns **34** — **32** excluding the two legacy no-sidecar `unsegmented` cells (R1, R2),
which is where the 32 came from. `GROUP BY … HAVING max(railed) = 0` demands the whole cell be clean
and returns **23**. Prefer **23** when the question is "is there a cell an A/B could run in" — a cell
whose sibling replicates railed is not a usable comparison group — and 32 when the question is "how
much unrailed evidence exists at all". The R26/R27 `turn360` family (79–99%) is in the 32 and not in
the 23, and its loud numbers are *no signal*: bank clamp 79–97%, `authorityUsedFrac` 0.95–1.08.

**R33 produced the first clean, high-occupancy, unsaturated live-AoA capture in the project's
history.** `Darkreach` on `oblique-6-c`, 4 complete replicates, **0 railed segments in the entire
77-capture batch**, and a monotone gradient across the diamond that repeats to within 3%:

```
obDR6  100.0%      aoaPeak 7.38-7.59   authUsed 0.717-0.748   terminalOff 0.205-0.315
obDL6   72.7-84.4%                     authUsed 0.543-0.554
obUL6   44.5-51.6%                     authUsed 0.538-0.549
obUR6   25.0-26.6%                     authUsed 0.476-0.484
```

(The lane's 5th replicate aborted on `airframe damage (detached ratio 0.029)`; it is excluded above.
`R29 trainer·obUL12` at 11.9% was the first unrailed activation of any size — R29 §4.3 — but it is
too small to A/B against. 100% on four replicates is not.)

**Why it appeared now, mechanistically — this is not luck and it is repeatable.** v0.96 closed #41:
`startSpeedCorner` now resolves against the FBW's `cornerSpeed` instead of the AI's, which dropped
`Darkreach`'s entry on this card from **171 m/s (R29) to 95 m/s (R33)** — 0.556×, the largest move on
the roster — at the same 6° demand. Low q, same demand, so the wing reaches its 10° alpha ceiling
before anything else binds. `trainer` moved 152 → 123.5 m/s the same way.

**What this unblocks: §4 Batch 3 is no longer gated on "can we provoke the regime at all".** It can
be. Batch 3's stated worry — *"if AoA still never approaches the limiter, that is the finding"* — is
**answered in the negative**: it does approach it, on a fixed-wing airframe, unrailed, reproducibly.
The cheap first move is not `alpha-steps` (never flown, 8 000 m, unvalidated) but **re-flying
`oblique-6-c` on `Darkreach` with more replicates**, because it already lands the regime unrailed at
a known entry condition.

**One caveat on reading those metrics.** `alpha_metrics` — `aoaAboveCeilingPct`, `qSchedMin`,
`gateMinUp/Dn`, `commandIntoCeilingPct`, `aoaRecoverActivePct` — is computed **only for
`alpha_step` / `alpha_hold` segments** (`scorecard.py:1143`). `obDR6` is an `oblique_step`, so none
of them exist on the very capture that provoked the regime. `aoaLimiterActivePct` and `aoaPeakDeg`
(from `aoa_g_metrics`, which runs on every segment) are all the corpus currently has there. Either
give the re-fly an `alpha*` tag, or widen the gate — but do not read "no `aoaAboveCeilingPct`" as
"the ceiling was not crossed".

**One thing NOT corrected, because it is true.** CLAUDE.md's Conventions section states that the
**game FBW's** alpha limiter is gated `if (num2 < 1f)` (decompile `:65033`) and is therefore inactive
above corner q. That is verified and stands. It is a different object: `aoaLimiterActivePct` is a
**mod-side** metric reading the mod's OWN ceiling gates (`aoaGU`/`aoaGD` below 0.999,
`scorecard.py:534-537`). The two are compatible and were being conflated — the mod's AoA block firing
at 100% while the game's is inactive is exactly CLAUDE.md's point that *"the mod's own AoA block is
the ONLY alpha protection in the loop at card speeds"*, now with a capture behind it.

### What the honest state is now

A validated instrument, a 31-card grid of which 19 have flown, ~1 700 captures, 10 airframes, and the
unsaturated baseline Q1/Q2 asked for. **The bottleneck moved from "we cannot fly the grid" to "we
have not run the attribution set."** All five `e*` cards exist, declare their own arm and fleet, and
have never been launched.

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
not `alpha-sweep`, is the discriminating card.

**UPDATED 2026-07-31 — this batch is no longer gated on "can the regime be reached".** §1 shows it
can: R33 `Darkreach·obDR6`, `aoaLimiterActivePct` **100.0%** on 4 unrailed replicates, `aoaPeakDeg`
7.4–7.6° against a 10° limiter, at a 95 m/s entry. The old worry below — *"if AoA still never
approaches the limiter, that is the finding"* — is **answered in the negative** and should no longer
be carried as the batch's headline risk. What remains open is what the law *does* there, which is
what `commandIntoCeilingPct` / `qSchedMin` / `gateMinUp`/`Dn` measure.

Two consequences for how to run it:
- **Cheapest first move is not `alpha-steps`.** Re-fly `oblique-6-c` on `Darkreach` with more
  replicates — it already lands the regime unrailed, at an entry condition that has flown.
- **`alpha_metrics` is gated to `alpha_step`/`alpha_hold` tags** (`scorecard.py:1143`), so the R33
  evidence carries none of those metrics. Any re-fly meant to answer *this* batch's question needs an
  `alpha*` segment tag, or the gate needs widening — decide before flying, not after.

The original stake, retained because it is still the shape of the risk: were the limiter unreachable,
the finding would be that the law caps turn rate so far below the airframe's capability that a whole
region of the envelope is unreachable by construction, reframing the next month of work from "tune
the loop" to "raise the ceiling". On the `Darkreach` at least, it is reachable.

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
no comparability — neither has ever been flown. Launch/preflight procedure: `cards/README.md`.

| # | knob | card | why that card |
|---|---|---|---|
| E1 | `Control/BelowAlignSuppress` | `oblique-below-c` + `oblique-6-c` + **`oblique-above-c`** | **v0.96: a 3-POINT AXIS, not a mirror pair.** `alignFracH` at −20 / 0 / +20 makes the belowness response a line rather than a difference; `oblique-6-c` is the control and **must not move**. All three arms want the SAME session to be readable as an axis. |
| E1b | `Control/AlignRateLead` | same | separate arm — it is also a 64% roll-damping change, unattributable if armed together |
| ~~E2~~ | ~~`Control/RelativeTurnLead`~~ | ~~`sweep-slow`~~ | **RETIRED — UNFLYABLE 2026-08-02.** The knob and its branch were **deleted in v0.99.1**, and its card `e2-rel-turn-lead.json` was deleted with them. R39-D swept it and it is spent: the term stays relative, only the lever is gone. Do not schedule this arm; `Scenario/ScenarioArmToggle = RelativeTurnLead` now names nothing and will fail-soft to sweeping nothing. |
| E3 | `Control/MarkerRateFeedForward` | `sweep-slow`, `sweep-creep` | ~~above the rail its roll contribution is identically 0.0000~~ **PREMISE REFUTED 2026-08-02 (R39-D) — but FLY IT ANYWAY, for the opposite reason.** Roll stick is the wrong observable for a term that moves a *target*: the feed-forward is worth **55–58% of the standing azimuth error**, and with it OFF the aircraft skids (`\|outY\|` 2–4× higher). What is still worth measuring is the **rail contamination**: the 57% was measured with three airframes on the 72° `MaxBankAngle` wall. Re-fly at `startSpeedCorner: 0.75`, throttle pinned. See `GENERALITY-REVIEW.md` finding 16. |
| E4 | **#21** roll-to-align channel (`lateralHold` ⇒ `blendWeight`) as the arm — the **precursor** | `darkreach-05` geometry | R32 §4: 34–56° of `targetBank` at \|`azErr`\| < 5° on a card whose largest step is 0.35°, on **0 of recs 01–31** and **12 of recs 32–63**. Those 31 clean replicates are the baseline, on the same airframe and card. **Fly this before E5** — see #45. **⚠ AS WRITTEN THIS CANNOT RETURN A RESULT — see the note below.** |
| E5 | **#45** `schedFloor`, expressed relative to a probed quantity | same | Only after E4. Fixing the stand-down first makes *some* departures survivable, which is worse than a departure that is legible. |

> **E4 is broken as specified (verified 2026-07-31, `captures.db`). Do not fly it expecting a
> result; it needs a redesign that is deliberately NOT attempted here.**
>
> E4 arms the roll-to-align channel on `darkreach-05`, using recs 01–31 as the clean baseline. But
> that channel is **already at zero weight** across the whole of that baseline:
>
> | `darkreach-05` | segs | RAILED | `blendRailPct` mean | max | `bankClampActivePct` |
> |---|---|---|---|---|---|
> | recs 01–31 (the "clean baseline") | 124 | **0** | **0.000** | **0.000** | **0.0** |
> | recs 32–63 (post-onset) | 126 | **74** | 60.1 | 100.0 | 52.9 |
>
> `blendRailPct` is exactly 0.000 on **all 124** baseline segments — the arm would suppress a channel
> contributing nothing, so arm A and arm B are the same flight and the A/B returns a null that reads
> as "#21 is not the precursor". Meanwhile the half where the channel *is* live is the departed half:
> **20 of its 32 captures** carry at least one railed segment (74 of 126 segments), and a railed
> segment is no signal by the rule at the top of §6. There is no window in this card where the arm
> both matters and is measurable.
>
> The measurement E4 actually wants is *the onset* — the transition between those two halves — and a
> before/after A/B on a card that only departs after ~35 replicates is not the instrument for it.
> Redesigning it is out of scope for this note; it is flagged so nobody spends a batch on it first.

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
   *different* item in every document that already cites it. ~~Highest in use is **#46**.~~
   **STALE — highest in use is #80, in a scheme this table does not carry. See RECONCILIATION below
   before allocating or citing anything.**
2. **`GENERALITY-REVIEW.md` findings are a SEPARATE namespace.** The R32 batch doc cited
   "`GENERALITY-REVIEW.md` #16" and "#18"; those are that file's finding numbers, not backlog
   numbers, and backlog #16/#18 do not exist. Write the filename with the number or don't write it.
   The backlog items that *correspond* to those findings are **#21** (finding 16) and **#45**
   (finding 18).
3. **The #45 collision is resolved as follows: #45 is the AoA-schedule authority failure, and
   nothing else.** The "belowness axis" work that was also being called #45 is **not a backlog item
   and needs no number** — it is experiment **E1** in §4 Batch 4, its card (`cards/oblique-above-c.json`)
   is already written, and its setup is in `cards/README.md`. A shipped card with a runbook entry
   is not backlog. Do not re-number E1 into this table.

---

### Item index — resolve a `#n` without reading §7

**§7 is long and three numbering schemes are in play. Read RECONCILIATION below before citing any
`#n`.** This table says where each number resolves; grep the `#n` if the line offset has drifted.
Always cite as `LAW-CHARACTERIZATION.md §7 #45` — the bare number is ambiguous across documents.

<!-- BACKLOG-INDEX:BEGIN — regenerate when you add an item; #n and status are what matter -->

| `#n` | status | item | line |
|---|---|---|---|
| `#14` | OPEN — law | `predFloor` hard 0.30 step → wants a continuous lead-confidence blend | 721 |
| `#19` | OPEN — instrument | Drone **loadout** matrix — the `Spawn` parameter is a `Loadout` object, not a name | 733 |
| `#20` | OPEN — law | `PEffRevThresh` floor branch: the `>=` makes it unreachable **from the v0.67 self-probe path** | 719 |
| `#21` | OPEN — law | `lateralHold` rails at 7.5° and zeroes the whole bank pipeline | 720 |
| `#23` | OPEN — law | Placement-tick reset: `ChaseController.Forget(ac)` does not take effect on the placement tick — `… | 722 |
| `#25` | OPEN — instrument | `RecordKey` silently fails to stop a capture when the local aircraft is gone | 732 |
| `#27` | CLOSED | concurrent A/B. The swept arm moved off `Cfg` and onto the aircraft (`ChaseController.Arm()` | 750 |
| `#29` | CLOSED | no disk card had loaded *at all* from v0.71 to v0.90. `JsonUtility` silently dropped | 740 |
| `#30` | CLOSED | two-seat airframes double-stepped the card clock **and** the control law. `Aircraft.pilots` | 742 |
| `#31` | CLOSED | a card now owns its own fleet (`airframe` comma list + `count`). v0.91 | 747 |
| `#33` | OPEN — instrument | Pillar 1 | 728 |
| `#34` | CLOSED | a lane whose airframe cannot fly the card's entry speed is refused **pre-spawn**, off | 748 |
| `#36` | OPEN — instrument | Pillar 2 rework | 729 |
| `#37` | CLOSED | `frameMs` measured nothing: `Time.unscaledDeltaTime` read inside `FixedUpdate` returns | 744 |
| `#38` | OPEN — instrument | Card **altitude budget** unchecked at preflight. Measured on R27: `oblique-below` loses 4323 m wo… | 730 |
| `#39` | OPEN — instrument | `startSpeed: 0` means **both** "hover" and "not specified" | 731 |
| `#41` | CLOSED | `startSpeedCorner` resolved against **the AI's** `cornerSpeed`, not the flight model's, so a | 754 |
| `#44` | CLOSED | damaged replicates are now **self-identifying and auto-aborted**: CSV column `dmgFrac` | 775 |
| `#45` | OPEN — law | AoA-schedule AUTHORITY FAILURE — the first genuine LAW defect in a while; everything else found l… | 718 |
| `#46` | CLOSED | the **`SplitSpec` one-slash divergence**. `ScenarioPlayer.SplitSpec` split on the FIRST | 765 |

<!-- BACKLOG-INDEX:END -->

### RECONCILIATION 2026-08-02 — this table is BEHIND, and three schemes are in play

**Read this before citing any `#n`.** Rule 1 above still says "highest in use is #46". That is false as
of 2026-08-02. Reconciled by reading every `#n` occurrence in the repo:

| scheme | range | where it lives | status |
|---|---|---|---|
| **A — this table** | #14 … #46, gaps 15–18, 22, 24, 26, 28, 32, 35, 40, 42, 43 retired | `LAW-CHARACTERIZATION.md` §7 | The documented authority. **Behind by ~34 numbers.** |
| **B — the working task list** | #1 … **#80** | the session task list; cited by `CHANGELOG.md` | Live and in active use. **Not on disk anywhere** except through its citations. |
| **C — per-document follow-ups** | `#53a–c`, `#54a–e`, `#55a–g` | the retired R36 / R37 / R39 batch docs | Document-local, and **those documents no longer exist** (consolidated 2026-08-02). The number was the document, the letter the item. Never backlog numbers; treat a bare `#53`/`#54`/`#55` as unresolvable. |

**Where A and B disagree — the part that will cause a real misreading:**

1. **`#46` means two different things.** In scheme A, #46 is the `SplitSpec` one-slash divergence,
   **CLOSED in v0.96** — re-verified 2026-08-02, `python debugtests/test-spec-grammar.py` passes both
   halves. In scheme B, #46 is **open**. They are not the same item. Never write a bare `#46`.
2. **`ledger #51` and `ledger #12` do not exist in this table**, yet both are cited *as ledger numbers*
   in `ARCHITECTURE.md`, `CLAUDE.md`, `CHANGELOG.md`, `cards/ALPHA-CARD-REDESIGN.md`,
   and `CHANGELOG.md`. They are **scheme B** numbers
   mislabelled "ledger". Their meanings are unambiguous from context and worth recording here:
   - **#51 — the placement part-shed bug.** OPEN and *instrumented, not fixed*; two attempted fixes are
     in the `MoveAssembly` graveyard (v0.96.1 tautological audit; v0.97.0 `Repair`, which killed 32/32
     placements). The bar for a third attempt is `R39-F-darkreach-damage.md` §9. See `CLAUDE.md` →
     `ScenarioPlayer.cs`.
   - **#12 — the per-replicate reset shape**: every field `Finish` resets must also be reset by
     `NextCard`. Now enforced as a `check-architecture.py` invariant (v0.99.1).
3. **Scheme-B numbers cited as shipped in v0.99.1 — #61, #63, #70, #71 — are CLOSED**, and are
   correctly absent from the open list. This is a consistency check that scheme B is being maintained.
4. **`GENERALITY-REVIEW.md` findings remain a fourth, separate namespace** (rule 2 above). Finding 16 ≈
   backlog #21; finding 18 ≈ backlog #45. Write the filename or write nothing.

**The open set as of 2026-08-02 (scheme B):**
`#45 #46 #47 #51 #53 #54 #55 #59 #62 #64 #66 #72 #73 #74 #75 #76 #77 #78 #79 #80` — 20 items.

**UNVERIFIED, and deliberately not guessed:** only **#45**, **#51** and **#64** (the wrong R35
`alpha-steps` figure — *fixed* 2026-08-02 in `plans/next-card-grid.md` and `LAW-WEAKNESS-MAP.md`; the
correct result is 7 of 8 airframes on the limiter, 2 of 8 past the ceiling) can be mapped to content
from disk evidence. **#53/#54/#55 are probably the R36/R37/R39 document follow-up bundles** (scheme C),
but that is an inference from the number matching, not a verified mapping. The remaining twelve —
#47, #59, #62, #66, #72–#80 — **have no disk evidence of their content at all.** They are listed so a
reset agent knows the count is 20 and that the ledger cannot yet resolve them.

**The substantive open work IS recoverable from disk** even where the numbers are not — it is
enumerated below, which is the authority on ordering.
**To repair this properly: the next agent holding the live task list should write its 20 open items
into the table below with their scheme-B numbers, then delete scheme A or renumber it into B.**
Two schemes with one overlapping, contradictory number is the worst of the three states.

---

### Open work, from disk — content, ordered. UPDATED 2026-08-02 after R41.

The dependency argument for the ordering is preserved inline below. Evidence for every closed item is
in `LAW-LEDGER.md` (the finding IDs are given); `debugtests/CAPTURES-DB.md` → *The batch index* maps
run tags to where their conclusions live.

**Tier 0 — before anything flies.** ~~Deploy v0.99.1, re-copy `cards/*.json`, delete
`e2-rel-turn-lead.json`, archive R39.~~ **DONE** — R41 flew on v1.0.0 with the cards re-copied.
Standing items from it, still true every batch: **re-run `--with-rows` per batch** (a `--rebuild` drops
every materialized row, so per-row queries silently have nothing to read), and **archive out of
`<game>` before the next game start** overwrites `LogOutput.log`.

**Tier 1 — code fixes that gate a re-fly.**
- ~~(a) the **helo probe call order**.~~ **CLOSED** — `heloOk = 1` on 108,987/108,987 rotorcraft rows
  in R41, against `false` on 48/48 in R39 (ledger **H1**). The `heloOk` column landed with it, so what
  cost R39 a row-by-row reconstruction is now one `select`.
- ~~(b) `bankClampActivePct` must not fire on a rotorcraft.~~ **CLOSED by the exposure mechanism** —
  v1.0.0 withdraws the metric on those segments instead of publishing it. Zero railed rotorcraft
  segments in R41, against R39's spurious `RAILED at 100.0%`.
- ~~(c) `wobble_scan` on the `hover_hold` / `bobup` arms.~~ **CLOSED** — `wobbleCoherenceAzErr` is
  populated on every R41 hover segment, where R39 had none.
- **(d) the `qSched` 0.3 floor, before any STOL re-fly. STILL OPEN.** At a real 90 m/s entry the floor
  catches **6 of 10** airframes and the re-fly measures the clamp rather than the law. Suggested shape:
  keep the floor, derive it from `_pitchEff` / measured achieved-vs-commanded rate rather than a
  literal — the estimator already exists. (This is the same constant as **#45** below.)
- **(e) `dmgFrac` — write the row BEFORE the abort check. STILL OPEN.** One row before `Abort` makes
  the column real; today it is a guaranteed constant that has misled four analyses (ledger **X24**).
  Prefer the fix to deleting the column. Either way, **index the abort's detached ratio as a capture
  column** — it is currently the only reliable damage signal and it lives in a string.
- ~~(f) `ArmOf(0) = 0`.~~ **CLOSED BOTH SIDES IN CODE — but NOT YET VERIFIED IN FLIGHT.** R41 caught it
  converting a null into an apparent 30% win (ledger **X27**). Analysis side:
  `compare-runs.py._anchor_replicate_filter`. Law side, v1.0.1: replicate 0 is a **warm-up armed as
  neither**, so `repeat − 1` must divide by 4 — cards want 4k+1 and `repeat: 8` warns. **R42 could not
  test it**: no rotor card declares an `armToggle`, so no `arm=` appears on any R42 `# config` line and
  `arm=-1` had no opportunity to be written; `compare-runs.py`'s silence on the anchor filter is
  because `_anchor_replicate_filter` is reached only from `_arm_comparisons`, which requires both arms.
  Ledger **O14**. **The `snapBackM = 0` stratum itself is undiminished** — 6 of 24 captures on each
  R42 rotor card, one per lane — because the fix re-labels replicate 0, it does not remove it, and on
  an *unarmed* card it is still pooled with its three siblings.

- **(g) NEW (R43) — the O11 re-fly needs a HUMAN, and that is the only piece left.** R43 flew the test
  ledger **O11** prescribed and it came back clean: 12 captures, `Fighter1` (= the field airframe
  FS-12), `Multirole1`, `SmallFighter1`, settled tails at **407–505 m/s / q 71.6–112.3 kPa**, `outR`
  sd **0.0007–0.0045** against a 0.05 fail threshold, `wobbleEpisodesOutR` 0 on **48 of 48** tails.
  The one thing a card cannot script is the pilot: the harness marker feeds `azErr` a smooth ramp, a
  hand on a mouse feeds it continuous micro-motion. **Do this before any roll-normalization code:**
  sandbox key → `Fighter1` → level ≥350 m/s at 4000 m → `ShowDebugHud` on → hold the marker ~1° off
  boresight for 30 s. Fail = `outR` sd > 0.05 with sustained sign flipping while `tBankE` ≈ 0. Then
  repeat with pylons (a card cannot set stores; R43's FS-12 was clean at 13.57 t). If both are clean,
  O11 moves to REFUTED for v1.0.3 and the roll work is justified on structure alone, not on a field
  report. Ledger **O11**, `GENERALITY-REVIEW.md` finding 5.
- **(h) NEW (R43) — `DroneAltDeckM` does not do what the harness says it does.** The deck sets the
  drone's **spawn** altitude; the card's placement then teleports every lane to its declared
  `startAlt`, so `entry_alt_to` has exactly one distinct value per card across R41/R42/R43 and the
  `[drone] … the decks are at 2500 and 5500 m and no lane flies 4000` log line describes an intent
  the data contradicts. Ledger **X32**. Blast radius is one card (`hs-hold` is the only one that ever
  declared a non-zero deck), but the claim is repeated in `TestDrone.cs`'s own design comment
  ("altitude becomes a BALANCED experimental factor crossed with airframe"), which is how it got into
  a card. **Either** make placement offset by the lane deck **or** delete the claim and keep the deck
  as the lane-packing device `RingRadius` needs. Until then a card's only q lever is speed, and speed
  is confounded with airframe. Related: the Tier 2 `DroneAltDeckM` default contradiction below.

**Tier 2 — docs the checker cannot see.** The v0.94 fleet-ABBA safety argument in `CLAUDE.md` (replace
with "`frameMs` is a per-row column, so covary or drop"); the `DroneAltDeckM` default contradiction
(source says 0, it is 3000); two load-bearing corrections to the `MoveAssembly` graveyard — *"always a
POSITION bug, never a load one"* is wrong (load produces position through joint-solver residual), and
the sweep-latency reading of 0.114 is wrong (it is subtree size, ledger X24); `alpha-sweep`'s note
miscalling FastBomber1 and Darkreach "the 5.0 gLimit airframes" (their sidecars read 8.0 and 4.0); the
`[card] … start` line's derived sweep rate, wrong by 2.7× and the operator's only pre-flight read of the
demand; **`aoaPeakDeg` / `aoaLimiterActivePct` printed unqualified below ~20 m/s** — R41 measured `aoa`
swinging −29.7…+77.1° at 3–22 m/s, so on a rotorcraft segment they should be read as *absent*, not as a
measurement (**R42 reproduces it and worse: `aoaPeakDeg` averages 68–177° across all 28 rotorcraft
cells, i.e. it is junk on every rotorcraft segment in the corpus, not merely the slowest**). **New in
R41 and unchanged in R42:** rotorcraft capture headers print `# fbw <unavailable>` while `heloOk = 1`
on every row — cosmetic, but it contradicts the columns of record and will mislead anyone who greps it.

**Tier 3 — design content, not yet decided.**
- **The roll twin of `_pitchEff`** — nothing in the roll/bank/settle path is scheduled against anything
  the law can measure. This is `LAW-WEAKNESS-MAP.md` W2 and the largest structural ONE-LAW gap.
  **R41 and the Discord field bundle both promoted it:** the high-q roll limit cycle (ledger **O11**,
  `GENERALITY-REVIEW.md` finding 5) is exactly this defect, now user-reported on a second airframe.
  **R43 (2026-08-02) demoted the URGENCY, not the gap.** The scripted-marker path does not limit-cycle
  at any q this fleet reaches — 48 tails at 71.6–112.3 kPa, `outR` sd 0.0007–0.0045. What R43 *did*
  measure is the q-scaling the finding predicts, in the right direction and far below the amplitude
  that matters: within `Multirole1`, Spearman(q, `outR` sd) = **+0.891**, sd rising 0.0012 → 0.0045
  over 87.6 → 112.3 kPa (steeper than linear, the shape of an unnormalized derivative gain). So the
  roll twin is still the largest structural ONE-LAW gap and is still worth building — but it is no
  longer justified as "fixing a user-visible defect" until Tier 1 (g) is flown.
- `aoaFade`'s floor should key off the lead overshoot the mod already computes (**do not ship without
  `alpha-pullup` flown twice** — narrowing the lim-10 fade from 40% to 25% of the limiter risks
  reintroducing the trainer AoA pump the floor was added to stop). The `Min(6f, …)` cap on `aoaFade`
  and `Min(4f, …)` on `aoaMargin` have **no stated justification at all** and bind on three of ten
  lanes while the comment asserts they do not — either the comment or the cap is wrong.
- **The rotorcraft outer loop.** `HeliYawScale = 2.0` is an absolute stick gain with no reference to the
  probed `heloMaxAngularVel` — **though note (2026-08-02) it is BYPASSED, along with `yawWeakFade`,
  whenever `_collective && _heloOk` (`ChaseController.cs:1994-1996`), which is every rotorcraft row in
  R41 and R42; on a resolved-probe helo this violation is currently unreachable**; `kHelo = 2.0`
  assumes a 0.3 s plant lag against a measured **0.59–1.39 s
  (2.3× across the roster)**, which by its own comment's bound gives 1.13 s⁻¹, *below* the constant; and
  the hover-blend speeds are absolute m/s, so a heavy compound heli and a light scout blend identically.
  **CORRECTED 2026-08-02 by R42 — do not re-cite the fourth claim that used to be here.** This bullet
  read *"`AttackHelo1` can never leave the hover regime at any speed it is capable of flying (Vmax
  100 m/s ⇒ lowest reachable `heliBlend` 0.455)"* and cited R41's divergence as its consequence. The
  0.455 is `(150 − 100)/110`, i.e. arithmetic on the **stale live config**; at the shipped 60/20 it
  clamps to **0** and the divergence does not occur (ledger **X29**, **X31**, **H6**). The three
  violations above are unaffected. **What replaced it as the consequential one:** on the only tiltwing
  in the game the principled tilt-driven blend is broken — the mod reads the game's tilt **command**
  back out of the joint angle and treats it as a hover fraction, missing the `1f −` the nozzle
  archetype carries, so it adds **0.18** of hover blend in settled wing-borne cruise (ledger **X30**,
  mechanism and the unshipped one-line fix in **O13**; the "0.620 at 108 m/s" figure that used to sit
  here is a **spawn transient**, not the cruise value). And there is a scoreable residual to attack:
  **ledger H7**, a deterministic 1.5–2.4° standing error (replicate CV ±1–3%) on every `AttackHelo1`
  segment above **40 m/s** — the mechanism is the game's `yawWeathervane`, closed-form on 5 of 5 tags,
  **not** ~~L15's "both turn channels de-rated"~~, which is retracted. A fourth violation belongs on
  the list above and is the cheapest of them: **`HeliForwardSpeed = 60` is anchored to
  `yawWeathervaneMaxSpeed`, the end of the ramp where nothing changes; the threshold that decides the
  behaviour is `yawWeathervaneMinSpeed = 40`, and all three weathervane fields are probeable**
  (`GENERALITY-REVIEW.md` finding 6).

**Rotorcraft re-fly — UPDATED 2026-08-02 after R42 flew items (1) and (3).**
~~(1) Reset `HeliForwardSpeed` to 60 and `HeliHoverSpeed` to 20.~~ **DONE, and it was the whole
R41 rotor verdict** — R42's `# config` reads `heliFwd=60 heliHover=20` on 56/56 captures against R41's
`150`/`40`, `AttackHelo1` converges (ledger **H6**), and R41's divergence is retracted as a config
artifact (**X29**). ~~`rotor-transition` becomes a direct `tiltFrac` readout (**O12**).~~ **DONE and
ANSWERED — and the answer is a defect** (**X30**), whose mechanism is now pinned down too: not a sign
error and not the limits, but a **missing `1f −`** against a hover reference of **0.18** (ledger
**O13**; the fix is written and deliberately unshipped). Remaining, re-ordered by
what each unblocks:

- **(a) REWRITTEN 2026-08-02 — the sign-vs-limits question is ANSWERED and this item is now a BEFORE
  capture, not a diagnosis.** Ledger **O13**: `GetAngleLimits()` is innocent, the sign is not the bug,
  and the tiltwing branch is simply missing the `1f −` its nozzle twin carries — the game pins the
  tilt command's hover end at **0.18** (`:70352`) with 1.0 = wing-borne (`:70344`), which is what R42's
  settled `heliBlend` **0.181–0.184** at 68–78 m/s is measuring. The one-line fix is written and is
  **deliberately unshipped**. What is still needed, and it is cheap: **a capture that records the
  PRE-FIX cruise-end `tiltFrac`/`heliBlend` on the condition the fix will be re-flown at**, because the
  confirming test is a before/after on the same condition and R42's captures are a decelerating sweep
  at `MinThrottle`, not a held condition. **That card now exists — `rotor-tilt-hold` / `-lo`
  (`QuadVTOL1`, `repeat: 5`): fly it BEFORE the fix ships, or the before is unrecoverable.** Two traps
  for whoever writes it: the "monotone fall" in X30 is a **spawn transient** (0.179 @ 139.9 → 0.603 @
  101 → 0.181 @ 70 m/s), so score the settled plateau and not the chase; and the game's tilt command is
  a function of **throttle** as well as speed (`:70342-70343`) while `rotor-transition` pins
  `ScenarioThrottle = 0.25`, so speed and throttle are confounded on that card as it stands. Reaching
  `heliBlend` ∈ [0.75, 1.0] is still worth having — the band has **zero samples in the corpus** — but it
  is no longer what gates the code change. ~10 min unattended.
- **(b) Give the hover cards a collective** — unchanged and still the blocker on ONE-LAW case 4.
  R42 reproduced **16/16** `UtilityHelo1` altitude-floor aborts, `velY` −24…−29 m/s, `thr` pinned at
  0.600 (ledger **H5**). Two of three rotorcraft still cannot hold altitude, so "hover" still rests on
  `QuadVTOL1` alone — and R42 shows `AttackHelo1`'s hover-card numbers are a *translating-flight*
  result (4.5 → 38 m/s across the card, never returning), not a hover one.
- **(c) `rotor-bistab` still has not measured the disturbance threshold R39 asked for**, and now for a
  second, different reason. In R41 it measured a speed divergence. In R42 it measures a clean
  convergence to a **standing 1.5–2.4° residual** (ledger **H7**) — real, above the resolution floor,
  replicate CV ±1–3% — but the bistability question needs the airframe to actually *hover*, i.e. (b).
- **(d) REWRITTEN 2026-08-02 — H7's mechanism is IDENTIFIED, so this is no longer "arm the de-raters",
  it is "choose between two fixes the corpus cannot separate".** ~~L15's `tBankE *= (1 − heliBlend)` +
  `yawWeakFade` reading~~ is **retracted**: `yawWeakFade`/`HeliYawScale` are bypassed whenever
  `_collective && _heloOk` (`ChaseController.cs:1994-1996`, and `heloOk = 1.00` on every R42 row), and
  the most bank-de-rated tag in the batch (`hoverstep5`, `heliBlend = 0.985`) is the one that
  **converges**. The residual is the game's `yawWeathervane` biasing the yaw rate error above
  `yawWeathervaneMinSpeed = 40` m/s, closed-form to 0.4–5.7% on 5 of 5 tags (ledger **H7**).
  **Two candidate ONE-LAW fixes, same probe, opposite ends:** (i) probe
  `yawWeathervaneStrength/MinSpeed/MaxSpeed` off the `heloFlyByWire` object `ResolveHelo` already
  Traverses (`ChaseController.cs:673-682`) and cancel the known bias in the helo yaw branch; (ii) re-key
  `heliBlend` off that probed band instead of the absolute 60/20. **R42 cannot pick one: every failing
  sample sits in a 0.3 m/s window (41.50–41.80 m/s) at the very bottom of a 20 m/s ramp**, where the two
  are numerically indistinguishable. **The discriminating card now exists as a matched pair,
  `rotor-weathervane-35` / `rotor-weathervane-60`** (`AttackHelo1`, `UtilityHelo1`, `repeat: 5`, both
  declaring an `armToggle`): same demand, one entry below the 40 m/s threshold and one at 60, where the
  two readings predict **opposite** outcomes — the weathervane reading says the residual grows toward
  its saturated value or rails the pedal, the `heliBlend`-fade reading says it vanishes because
  `heliBlend` is exactly 0 at 60 and the bank channel takes the whole turn. **Read the capture's own
  `spd` column, never the declared entry speed** — nothing holds entry speed on a rotorcraft (collective
  buys climb, not forward speed) and R42 watched `AttackHelo1` drift 4.5 → 38 m/s and park just above
  the threshold at 41.5–41.8 under `HoldThrottle = 0.60`. The pair's `armToggle` also closes ledger
  **O14(c)** — R42 could not exercise the v1.0.1 `arm=-1` warm-up because no rotor card declared an arm.
  Cards want `repeat: 4k+1`.

**Fixed-wing cards, from R41.** Do **not** re-fly `e1-below-suppress` or `e1-below-control` as they
stand — `bWt` ≈ 0 on the scored window, so neither can see the term regardless of arm (ledger **A2**);
either score a window where the roll channel is open (t < 3 s) or give `BelowAlignSuppress` a true off
before sweeping it. **`e1b-align-lead` is answered — retire the card or the knob** (the term fires and
does nothing; precedent is `RelativeTurnLead`, ledger X22). **`e3-marker-ff` is the one to build on**,
and the open question is its energy price — it wants a card that scores `deltaEnergyHeightM` *against*
pointing rather than treating it as a nuisance (ledger **A1**). **Redesign or retire `stol-sweep`** —
36 of 36 railed on three limits at once, `terminalOffDeg` 61–95°; it saturates the roll/turn channel
before it measures anything, and no throttle or altitude change reaches that.

**Standing holes.** The **loaded case (ONE-LAW case 2) is still unflown** and a card cannot set stores —
it needs backlog **#19** or a hand-flown capture with heavy stores at `alpha-pullup`'s entry condition.
A genuine **STOL** condition is still unmet for the fast jets (corner-relative entry puts them at
128–160 m/s). Nothing in the corpus scores **gun-solution dispersion**, so "1° of nose wander at
0.43 Hz" has no cost attached. **`oblique-above-c` is the only never-flown card left in `cards/`** —
the belowness axis, experiment **E1** in §4 Batch 4.

**And the hole that outranks them all: the corpus has ZERO rows between 250 and 400 m/s.** Every real
airframe tops out at 221 m/s because cards enter at `startSpeedCorner 1.0×` and never accelerate past
it. The one field-confirmed law defect in the ledger (**O11**, the ~2 Hz roll limit cycle at 350+ m/s)
lives in that gap. Two hand-flown captures close it; see O11 for the pass/fail signature.

---

Ordered by when it unblocks something, not by severity.

### Law defects — diagnosed, unfixed

These are Q4. All four are invisible in a railed regime, which is why the baseline comes first.

| # | Defect | Where | Why it waits |
|---|---|---|---|
| **#45** | **AoA-schedule AUTHORITY FAILURE — the first genuine LAW defect in a while; everything else found lately was instrument.** `schedFloor = 0.3f` (`ChaseController.cs:1255`) terminates the AoA-utilization schedule's range at the same **absolute** place for a 27° ceiling on an 8.7 t `Fighter1` and a 10° ceiling on a 105 t `Darkreach` — a hardcoded constant deciding an outcome, i.e. the ONE-LAW smell. Measured in R32 (63 captures, 37,868 rows, 18 departures, 3 pilots killed): `qSched` is exactly **0.300 on 100.0%** of the 2,314 rows past \|AoA\| 20°, against **0.0%** on all 31 clean pre-onset replicates of the same card and airframe. At the floor the law still commits 30% of its P demand into a plant delivering **7.7×** the commanded rate in the **opposite** direction (median on departed captures; p90 13.0×, max 28.2×; clean captures 1.56×). Sibling constants: `:1296` `Max(0.3f, aoaGateUp)` is the same shape; `:1152` is defensible because it mirrors the game's own `:65034` clamp. **The deeper statement:** the law's entire response to a non-responding plant is **five terms that each REDUCE authority** — the two `qSched` floors, the `omegaMax` floor, `pErrTerm *= _pitchEff` below `PEffRevThresh` (`:1927`), and `aoaRecover *= _pitchEff` (`:1557`), which scales the one term documented at `:1543` as "the term that flies the nose back INSIDE the envelope" by an estimator reading 0.036–0.144 for the whole departure. **Nothing in `Apply` increases authority or changes strategy.** On nine of ten airframes the airframe's own stability covers the gap; on the Darkreach it does not and the aircraft descends 3,000 m. | `ChaseController.Apply` (AoA-utilization schedule) | **HIGH, but SEQUENCED — do not fix the floor first.** R32 §4 shows the railing is downstream of a precursor: 34–56° of `targetBank` at \|`azErr`\| < 5° on a card whose largest step is 0.35°, on 0 of recs 01–31 and 12 of recs 32–63, appearing 1–2 replicates before the departure in every lane. Finding 16 (`lateralHold` rails ⇒ `blendWeight` = 1) is the standing candidate — i.e. **#21**. Fixing the stand-down first would make *some* departures survivable, which is worse than a departure that is legible. Fly in this order — **§4 Batch 4 rows E4 then E5**: (1) a precursor-isolation card on the `darkreach-05` geometry with the roll channel as the arm — recs 01–31 give it a 31-replicate clean baseline on the same airframe and card; (2) then an A/B on `schedFloor` expressed **relative to a probed quantity** (`omegaMax`, `_fbwMaxPitchVel`, or the alpha ceiling `aoaUtil` already normalises by); (3) cheap side-check: `EW1` shares `assist=0`, `maxPitchAngVel` 0.3 and `alphaLimiter` 10 at a quarter of the mass and has never flown this card. **Explicitly NOT the fix: a mod-side G-limiter.** It protects nothing (the airframe cannot be over-G'd — see below), it masks the defect (the high-G row is the readout, not the cause), and it would be a **sixth** de-authorizing term on a law whose problem is that it already has five. `GENERALITY-REVIEW.md` finding 18; `LAW-LEDGER.md` K3 / L3 (the R32 batch). |
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
| **#19** | Drone **loadout** matrix — the `Spawn` parameter is a `Loadout` object, not a name. | Blocked on one in-game dump. The sidecar already records resulting stations/masses/drag, so nothing on the analysis side changes when it lands. **Written up in `plans/drone-loadout-seam.md`** (every claim graded VERIFIED / INFERRED / UNKNOWN) — read it first: the slot already exists as the argument `TestDrone.Spawn` passes `null` for, so the work is *choosing what a config string names*, not plumbing. **This gates ONE-LAW standing case 2, the loaded jet, which has never been flown at all** — a card cannot set stores. |

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
