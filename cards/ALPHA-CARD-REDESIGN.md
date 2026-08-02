# `alpha-pullup` — the AoA-ceiling card, redesigned

**Replaces `alpha-sweep`. Keeps `alpha-steps` untouched.** Written against
[`debugtests/R39-E-alpha.md`](../debugtests/R39-E-alpha.md), which is the failure analysis this
design is derived from — read its §4 and §10 before arguing with anything below.

Deliverables: `cards/alpha-pullup.json` (repo + game load path), and — unrelated, §8 — the
placement-forensics pair `place-noop` / `place-deflect` for ledger #51.

---

## 1. Verdict, in one paragraph

`alpha-sweep` did not fail by being mistuned. It failed because **its demand is an azimuth sweep**,
and an azimuth demand loads the wing only by way of bank — which the law clamps at 72°, i.e. at
`n = 1/cos 72° = 3.24`. Every lane that needs more than 3.24 g to reach its own alpha ceiling was
structurally unable to get there no matter how large the demand grew, and the surplus demand went
into the three rails and into 525–2428 m of descent. The replacement pulls **in the vertical plane**,
where load costs no bank at all: `az` is identically `0.0` for every sample of every scored segment.
That is the whole idea; everything else is sizing.

---

## 2. Why the old card railed — mechanically, from source

`deriveAzRate` (`ScenarioPlayer.cs:1726`) sets the sweep rate to
`0.6 × 57.3 × 9.81·√(n²−1)/V`, clamped to 3–30 °/s, with `n` = **the airframe's structural g limit**.
So the card asks for a turn rate sized by the *g* limit and then measures the *alpha* channel — a
demand derived from the wrong limit. The law converts an azimuth demand into a bank demand, the bank
demand hits `Cfg.MaxBankAngle` (72°), and load pins at 3.24 g regardless of how much more is asked.

Measured (R39-E §2): bank clamp 74–97%, turn-rate cap 85–97%, blend rail 81–96%,
`aoaAboveCeilingPct` **0.0 on 60 of 60**. R39-E §4 is the decisive line: `alpha-sweep` commands
**4–50× the turn rate** of `alpha-steps` at the *identical* entry condition and reaches a **lower**
`aoaPeakOverCeiling` on **7 of 8** airframes.

Two consequences that the redesign is built on:

- **Lowering altitude cannot help.** The aircraft already descends 0.5–2.4 km on its own and *gains*
  q on seven of eight lanes. Starting lower raises q at entry and makes the rail worse. 8000 m stays.
- **Raising demand cannot help either.** Past the bank clamp, additional azimuth demand is discarded
  (`bankDemandExcessDeg` is exactly that throw-away). The binding constraint has to be changed, not
  pushed harder.

### The rails are unreachable for a vertical pull *by construction*

This is not an expectation, it is a property of how the four rail metrics are computed
(`scorecard.py` `saturation_metrics`, `RAIL_METRICS`):

| rail metric | computed from | with `az ≡ 0` |
|---|---|---|
| `bankClampActivePct` | `|targetBank|` ≥ `maxBank` | no bank demand → ~0 |
| `turnRateCapActivePct` | `des = |tan(bankTR)|·g/v` vs `omegaMax` | `bankTR ≈ 0` → `des ≈ 0` → ~0 |
| `turnRateDemandRatio` | `mean(des)/mean(omegaMax)` | ~0 |
| `blendRailPct` | `bWt ≥ 1`, and `bWt` rails on `|azAl| >` `EvolvedAlignHoldDeg` | no azimuth error → ~0 |

The only rail left is `aoaAboveCeilingPct`, and `rail_warning` **already exempts** it on
`alpha_step`/`alpha_hold` ("on an alpha_* segment this is the card doing its job — the ceiling IS the
stimulus", `scorecard.py:914`). So this card can push past the ceiling without its own metrics being
written off as NO SIGNAL — which `alpha-sweep` could not.

**A non-zero reading on any of the first three rails is therefore a bug report about the law, not a
card problem.** It would mean the roll channel is generating bank against a demand that never asked
for any.

---

## 3. The physics that sets the numbers

### 3.1 The demand → AoA chain

For a wing with roughly linear `C_L`, AoA at load factor `n` and dynamic pressure `q` is

```
AoA / AoA_ceiling  =  n / n_max_aero ,     n_max_aero(V, ρ) = the load at which the wing is AT its ceiling
n_max_aero  ∝  V²·ρ                        (so it scales with q)
```

For a **pull in the vertical plane** at commanded pitch rate `ω` and pitch attitude `θ`:

```
n = cos θ  +  V·ω / g
```

Two properties make this the right stimulus, and both are the opposite sign to a level turn:

- **No bank term.** A level turn is `n = 1/cos φ`, capped at 3.24 by the 72° clamp. The vertical pull
  has no `φ` in it at all, so it is the *only* stimulus that can exceed 3.24 g under this law — and
  the fighters need `n = 4.5–6.4` to reach their own alpha ceiling at corner speed.
- **The aircraft climbs.** `q` falls as the segment runs, so `AoA/ceiling` rises monotonically.
  `alpha-sweep` dove and `q` rose, which is precisely why more demand bought less AoA.

Within a segment the aircraft also decelerates at roughly `g·sin θ`, so `V` falls, `n_max_aero` falls
as `V²`, and `n` falls only as `V` — the ratio rises on both counts. **The pull is itself a sweep
through the AoA band**, which is what localises the guard's onset rather than just tripping it.

### 3.2 Why corner speed does NOT normalise the alpha state (and what to do about it)

The harness normalises entry speed to the FBW corner speed. Textbook corner is
`V_c = V_stall·√n_limit`, which *would* make `n_max_aero` identical across the roster. **It does not
hold in this game** — these are hand-authored values:

| | Fighter1 | Multirole1 | SmallFighter1 | trainer | VTOLTrainer1 | CAS1 | COIN | EW1 | FastBomber1 | Darkreach |
|---|---|---|---|---|---|---|---|---|---|---|
| `V_c/V_stall` | 2.22 | 2.40 | 2.07 | 2.60 | 3.20 | 2.62 | 2.83 | 3.90 | 3.13 | **1.50** |
| `√gLimit` | 3.00 | 3.00 | 3.00 | 3.00 | 2.83 | 2.74 | 2.55 | 2.45 | 2.24 | 2.24 |

`V_c/V_stall` spans **1.50 → 3.90**, a 2.6× spread, and does not track `√gLimit`. So at any single
corner-relative entry the roster's **1-g AoA fraction already spans ~0.65 (Darkreach) to ~0.11
(Multirole1) before any demand is applied**.

**Therefore no single (entry speed, demand) pair can put all ten airframes at 0.9 of their ceiling.**
That is a hard result, and it is why this card carries **two pitch rates 4.5× apart** rather than one
tuned number. It is also why the readout is *where in the sweep the guard engaged*, not *did every
lane sit at 0.9*.

### 3.3 Entry speed: `startSpeedCorner: 1.15`

Bounded on both sides, and both bounds bind:

- **Not 1.0×** — Darkreach's FBW corner is 100 m/s and its density-corrected stall at 8000 m is
  `66.7/√0.4287 ≈ 102 m/s`. `1.0×` places that lane **below stall**. The v0.92 envelope gate would
  *not* catch it: it checks `1.10 × Vstall` against the **sea-level** `aircraftInfo.stallSpeed`, with
  no density correction, so 100 > 73.4 passes while the aircraft cannot fly.
- **Not 1.2×** — the ceiling is `0.95 × Vmax`. CAS1 clears `1.2×` by 3.3 m/s (192 vs 195.3) and COIN
  by 2.6 m/s (132 vs 134.6). A 1.7% margin on a table value is not a margin.
- **1.15× restores ~6%** on both and clears all ten. `alpha-steps`/`alpha-sweep` flew an absolute
  250 m/s, which refuses CAS1 and COIN outright: **10 lanes instead of 8, at no cost in wall clock**,
  since lanes fly concurrently.

### 3.4 The two rates

Amplitude is capped at ~72° of pitch: beyond that the aircraft approaches vertical, heading becomes
ill-defined and the roll-to-align channel enters its wrap region (`phiWrapGate`) — which would inject
exactly the roll-channel behaviour this card exists to exclude. Amplitude ÷ rate fixes the duration.

| segment | rate | dur | amplitude | job |
|---|---|---|---|---|
| `alphaHoldFast` | **18 °/s** | 4 s | 72° | high load. Exposes the high-margin lanes (Multirole1, Fighter1, SmallFighter1). Also the **fast AoA transient** the v0.61 4° fade floor exists to keep graded — `_aoaRateFilt × aoaLead` is largest here. |
| `alphaHoldSlow` | **4 °/s** | 12 s | 48° | slow approach. Walks the **lim-10 group** (trainer, EW1, Darkreach) across the 0.529-vs-0.7059 onset band. Most of its AoA comes from in-segment deceleration. |

**Order is load-bearing: fast first.** `alphaHoldFast`'s load is `cos θ + Vω/g`, i.e. it is
*entry-speed-sensitive*, so it runs off the pristine placement. `alphaHoldSlow` derives most of its
AoA from deceleration within the segment and is robust to a degraded start, so it takes second place
behind the 18 s recovery arm. **Read `spd` at the first sample of `alphaHoldSlow` before reading
anything else on that segment** — that arm is a recovery from a 72° zoom and it is the one part of
this card whose end state is not designed, only bounded.

### 3.5 Does the card *hold* its entry condition, or merely set it?

**Merely set it — and that is true of every card in the grid, so it has to be designed around rather
than assumed away.** `startSpeed` / `startSpeedCorner` are written once, at the placement. Nothing
holds them afterwards; the only persistent lever is throttle.

The evidence that this is not theoretical:

- R37 measured `oblique-6-dwell` at the **0.70 default** drifting from `0.95×` corner to
  **1.03–2.49×** across one capture — an airframe-dependent 1.10× (CAS1) to 2.04× (Darkreach).
- The STOL batch is the extreme case: `stol-*` declared **90 m/s**, ran with the throttle unpinned at
  1.00, and reached **144–147 m/s by the end of the 6 s arm** and **340–381 m/s** by the last scored
  segment — on cards whose entire purpose is low q. Fixed in this pass; see §9.

**For `alpha-pullup` the risk is one-sided, and that is what sets the number.** Drift *up* raises q,
raises `n_max_aero` and **lowers** `AoA/ceiling` — it destroys exposure, which is the one thing this
card must not lose. Drift *down* reinforces the sweep the card is built on. So the throttle is
**biased low rather than set to hold**: `Scenario/ScenarioThrottle` is pinned at **0.40**, an
already-flown operating point (`oblique-6-dwell-t040`) and safely above the `MinThrottle` = 0.25 floor
below which `EntryThrottle` silently snaps back to the 0.70 default (`ScenarioPlayer.cs:1365`).

Worked, for the lane that matters: at 0.70, ~19 m/s of drift over the 6 s arm would take Fighter1 from
184 to ~203 m/s (+10%). On **Multirole1** — the lowest predicted exposure at 0.81 — a drift to 230 m/s
would raise `n_max_aero` by 1.56× and drop its `alphaHoldFast` ratio to **0.63, below the 0.739 onset,
i.e. out of exposure entirely**. That single lane is why the throttle is pinned low rather than left
at the default. **Check `spd` at the first sample of `alphaHoldFast` on Multirole1 before reading its
gate.**

Two related traps this card is structurally clear of, both worth stating because they are what broke
its predecessors:

- **`RateMaxDegS = 30` clips 4 of 10 derived sweep rates**, which makes `stol-sweep`'s "same fraction
  of structural g on every airframe" premise false on those lanes. `alpha-pullup` uses **explicit
  `trackEl` ramps, not `deriveAzRate`**, so its demand is exactly what the file says on every lane.
- **`MarkerRateFeedForward` (default ON since v0.78) adds the marker's azimuth rate to the turn
  demand**, pushing `bankTR` +10–15° and taking Fighter1 from 61.6° to 73.6° against the 72° clamp on
  94–100% of settled samples — very likely part of why `alpha-sweep` railed. `alpha-pullup` commands
  **zero azimuth**, so `_aimAzRateFilt` is zero and the feed-forward has nothing to contribute. It
  cannot reach this card's bank channel.

---

## 4. Predicted per-lane exposure

`AoA/ceiling` at segment start → end. `n_max_aero` is taken from the R39 measurement
(`gPeak / aoaPeakOverCeiling`, scaled by `(V/250)²`) where one exists; CAS1 and COIN have never flown
this condition, so they use the `V_stall`-based estimate and are marked. `n` is capped at each
airframe's g limit.

| airframe | `alphaLimiter` | V entry | shipped onset | `alphaHoldFast` | `alphaHoldSlow` |
|---|---|---|---|---|---|
| Fighter1 | 27 | 184 | 0.739 | **1.16 → 1.19** | 0.39 → 0.49 |
| Multirole1 | 27 | 184 | 0.739 | **0.81 → 0.84** | 0.27 → 0.35 |
| SmallFighter1 | 25 | 178 | 0.718 | **1.18 → 1.21** | 0.40 → 0.52 |
| trainer | 10 | 150 | 0.529 | 2.42 → 2.52 | **0.86 → 1.24** |
| VTOLTrainer1 | 15 | 184 | 0.686 | 2.14 → 2.20 | **0.72 → 0.92** |
| CAS1 *(est.)* | — | 184 | — | 1.77 → 1.82 | **0.59 → 0.76** |
| COIN *(est.)* | — | 126 | — | 1.11 → 1.18 | **0.42 → 0.68** |
| EW1 | 10 | 150 | 0.529 | 1.92 → 2.00 | **0.69 → 0.98** |
| FastBomber1 | 15 | 230 | 0.686 | **1.00 → 1.22** | 0.53 → 0.62 |
| Darkreach | 10 | 115 | 0.529 | 3.41 → 3.65 | 1.33 → 2.38 |

**Every one of the ten lanes is exposed on `alphaHoldFast`** — the minimum is Multirole1 at 0.81,
which is above both the shipped onset (0.739) and the proportional one (0.7059). That is the
criterion `alpha-sweep` could not meet: five of its eight lanes never got the gate below 0.5 and
published a passing `commandIntoCeilingPct = 0.00` purely from non-exposure.

**`EW1` on `alphaHoldSlow` is the money lane for the fix.** It enters at 0.69 — *below* the
proportional onset 0.7059 and *above* the shipped 0.529 — and sweeps to 0.98. R39-E §5c already
identified EW1 and Darkreach as the only two lanes that are gated **solely because `aoaFade` is
floored at 4°**. Under the shipped code its gate bites from the first sample; under the unclamped
proportional form it should not bite until a few seconds in. That is a **before/after difference
visible in a single signal on a single lane**, which is what "legible" has to mean.

**Darkreach is past its ceiling from the first sample, by design.** It is the lane that finally
exposes `aoaRecoverActivePct` and `aoaRecoverPeak`, which are identically zero below the ceiling and
have **never fired anywhere in the corpus**.

### Confidence

The two `n_max_aero` models (R39-measured vs `V_stall`-derived) disagree by up to 2.9× on individual
lanes — `aircraftInfo.stallSpeed` is a hand-authored display field and the R39 peaks were taken
mid-descent. **The predictions above are good to roughly a factor, not to a decimal.** The design is
built to survive that: the two rates are 4.5× apart, so a lane that misses its band on one arm is
caught by the other, and the criteria in §5 are written to distinguish "did not reach" from "reached
and behaved".

---

## 5. PASS criteria — each with its fire, fail, and not-exposed reading

The rule this table exists to satisfy: *a criterion that cannot fire is not evidence.* Two of
`alpha-sweep`'s five were structurally unreachable. Every row below names the reading that means
**not exposed**, and that reading is never scored as a pass.

**Scope note:** all six are read on `alphaHoldFast` / `alphaHoldSlow` only. `arm` segments are
excluded from scoring.

### C1 — the card reached the regime *(the gate on everything else)*
- **Metric:** `aoaLimiterActivePct` > 0, and `aoaPeakOverCeiling`.
- **Fires / passes:** `aoaLimiterActivePct` > 0 on `alphaHoldFast` for **10 of 10 lanes**.
- **Fails:** `aoaLimiterActivePct` = 0 on a lane whose `aoaPeakOverCeiling` ≥ its onset — that would
  mean the guard did not engage when the AoA says it should have, i.e. a real law defect.
- **Not exposed:** `aoaLimiterActivePct` = 0 **with** `aoaPeakOverCeiling` < onset. The card missed
  that lane. **Not a pass** — raise the rate for that lane and re-fly; do not read C2–C6 on it.

### C2 — the guard is graded, not a relay *(the one the fix could break)*
- **Metric:** `wobbleEpisodesAoa` = 0, plus `aoaPeakOverCeiling` ≤ 1.1.
- **Fires / passes:** `wobbleEpisodesAoa` = 0 on a segment where `gateMinUp` < 1.0 (the gate moved,
  and it did not oscillate).
- **Fails:** `wobbleEpisodesAoa` > 0, or `aoaPeakOverCeiling` in the 1.3–2.5 band — the v0.57 reactive
  relay signature. **This is the criterion that should catch the fix going wrong**: removing the 4°
  floor narrows the lim-10 fade from 40% to 25% of the limiter, and the v0.61 comment says that floor
  exists specifically to stop the trainer AoA pump.
- **Not exposed:** `gateMinUp` = 1.000 (the gate never moved, so it cannot have chattered).

### C3 — the law backs its own demand off
- **Metric:** `commandIntoCeilingPct`, with `gateMinUp` read beside it as its exposure.
- **Fires / passes:** `commandIntoCeilingPct` < 10% **on a segment whose `gateMinUp` < 0.5**.
- **Fails:** > 25% with `gateMinUp` < 0.5 — the raw law keeps commanding into the ceiling and leaves
  the gate to do the backing off.
- **Not exposed:** `gateMinUp` ≥ 0.5. The metric requires `aoaGU < GATE_BITING` (0.5) to count a
  sample at all, so it reads **0.00 both for "the law behaved" and "we never looked"**. R39-E §6 found
  five of eight lanes in exactly that state. **A 0.00 with `gateMinUp` ≥ 0.5 is not a pass.**
  (Standing caveat from R39-E §6, unresolved and not resolvable by a card: whether the gate crosses
  0.5 is itself partly an artefact of the 4° floor, so this metric confounds "does the law back off?"
  with "did the gate close far enough to see?". Read C3 as *supporting*, never as the headline.)

### C4 — the v0.59 demand schedule engaged
- **Metric:** `qSchedMin` < 1.
- **Fires / passes:** `qSchedMin` < 1 on any segment with `aoaLimiterActivePct` > 0.
- **Fails:** `qSchedMin` = 1.000 **while** `aoaLimiterActivePct` > 0 — AoA was in the band and the
  schedule stayed inert.
- **Not exposed:** `qSchedMin` = 1.000 with `aoaLimiterActivePct` = 0 (same lane as a C1 non-exposure).

### C5 — the recovery bias exists *(never once observed)*
- **Metric:** `aoaRecoverActivePct` > 0, `aoaRecoverPeak`.
- **Fires / passes:** > 0 on a segment with `aoaAboveCeilingPct` > 0. **Predicted to fire first on
  Darkreach and trainer**, which are past the ceiling by §4.
- **Fails:** `aoaRecoverActivePct` = 0 **while** `aoaAboveCeilingPct` > 0 — AoA crossed the ceiling
  and nothing flew the recovery. That is a real defect and it is now detectable.
- **Not exposed:** `aoaRecoverActivePct` = 0 with `aoaAboveCeilingPct` = 0. The term is *identically*
  zero below the ceiling (`ChaseController.cs:1280`), so this reading carries no information at all.
  **This is exactly how `alpha-sweep` scored 0/8 and it must never again be reported as a fail.**

### C6 — the energy is not being paid in altitude *(the specific `alpha-sweep` failure)*
- **Metric:** `deltaEnergyHeightM` and the `alt` trace, both on the sustained branch that
  `alpha_hold` gets.
- **Fires / passes:** altitude at the end of each scored segment ≥ altitude at its start (the pull
  climbs), and `deltaEnergyHeightM` < 0 (energy is spent, as it must be, but into altitude not out of
  it).
- **Fails:** altitude **falls** across a scored segment. That is `alpha-sweep`'s signature — 525–2428
  m of descent with q rising 3–32% — and it would mean the pull is not holding.
- **Not exposed:** cannot happen; `alt` and `spd` are present on every capture, so this one always
  reads.

### Dropped from `alpha-sweep`'s criterion set, deliberately
- **`aoaAboveCeilingPct` > 0 as a headline pass.** It is now a *precondition* for C5 and an exemption
  trigger in `rail_warning`, not a score. Whether a lane crosses is set by its own stall margin (§3.2),
  so scoring it would rank airframes on `V_c/V_stall`.
- **`gateMinDn`.** No mirror push on this card (§7), so it will read 1.000. That is non-exposure by
  design, not a pass. `alpha-steps` remains the mirrored card.

---

## 6. Operator instructions

Cards are already copied to
`E:\SlowGames\steamapps\common\Nuclear Option\BepInEx\config\wtmouseaim-cards\`.
**Restart the game** — cards bind at startup.

### Setup

| setting | value | why |
|---|---|---|
| `Drone/DroneEnabled` | **on** | required; the subsystem is inert otherwise |
| `Scenario/ScenarioCardSet` | **`alpha-pullup`** | **use this, not the F1 checkboxes** — see below |
| `Scenario/ScenarioBatchQueue` | empty | single fleet |
| `Drone/DroneAirframe` | *ignored* | the card names all 10 lanes and overrides the list **wholesale** |
| `Drone/DroneCount` | *ignored* | card `count` is unset → resolves to the 10 keys `airframe` names |
| `Scenario/ScenarioRepeat` | *ignored* | card `repeat: 8` wins |
| `Scenario/ScenarioThrottle` | *ignored* | card pins **`0.40`** via its own `config` block; it lands in the capture's `# override` header. See §3.5 for why 0.40 and §6.1 for the fix it waits on |
| `Scenario/ScenarioArmToggle` | operator's choice | see the A/B note below |

Then press the drone spawn key (**F2** by default).

### 6.1 One blocking dependency — fly this after the config-pin refcount fix

**`alpha-pullup` pins `Scenario/ScenarioThrottle` = 0.40, and pinned card `config` overrides are
currently process-global.** With a concurrent fleet the **first lane to finish un-pins the override
under every lane still flying**, so on a 10-lane card the later lanes would silently revert to whatever
the operator's global says. A parallel agent is refcounting the pins; **fly this card after that
lands.** The same applies to `place-noop` / `place-deflect` (§8) and to the two STOL cards (§9).

The card is correct to use the mechanism — it is the only way to stop the throttle being an
uncontrolled operator global, which is the R18 failure mode. Until the fix lands, the audit is
per-capture and cheap: **every capture carries a `# override Scenario/ScenarioThrottle=0.40` header
line**, so a lane that flew unpinned is identifiable after the fact rather than silently wrong.

> **The `sel[0]` trap — this is the one that silently ruins a batch.** `airframe`, `count`, `repeat`,
> `armToggle`, `startAlt` and `startSpeed` are all read off the **first selected card**, and
> `LoadCards` registers the built-ins before scanning disk, so on a fresh config `sel[0]` is
> `fixedwing-v2` — which declares none of them. The whole batch then becomes one `Multirole1`, one
> replicate, no A/B, with `alpha-pullup` flying second as a stimulus only, and **nothing refuses**.
> `ScenarioCardSet` overrides the checkboxes entirely. Use it.

### What it flies

- **Airframes (10 lanes, concurrent):** `Fighter1, Multirole1, SmallFighter1, trainer, VTOLTrainer1,
  CAS1, COIN, EW1, FastBomber1, Darkreach`
- **Entry:** 1.15 × each lane's own **FBW** corner speed, 8000 m, throttle **0.40** (pinned by the card)
- **Replicates:** 8 per lane → **80 captures**
- **Per replicate:** 48 s (`arm` 6 → `alphaHoldFast` 4 → `arm` 18 → `alphaHoldSlow` 12 → `arm` 8)
- **Wall clock:** 384 s per lane + 27 s of launch stagger (10 lanes × 3 s) + despawn ≈ **7½ minutes**

### Confirm before walking away

1. `[drone] ... launch` names **10 lanes** and says the airframe / alt / speed / count came **from the
   card**, not from F1.
2. **No** `[drone] ... cannot fly ... entry speed` refusal. If CAS1 or COIN refuses, its FBW corner
   differs from `AIRFRAMES.md` — drop `startSpeedCorner` to 1.10 and re-fly.
3. **No** `could not read corner speed` warning. If it fires for every airframe the prefab read
   failed and every lane silently fell back to the absolute `startSpeed: 250`, which is the old card's
   entry condition and the wrong test.
4. The run board shows 10 aircraft with card `alpha-pullup`.

### For the before/after on the `aoaFade` / `aoaMargin` fix

Fly the batch above **twice** — once on the shipped build, once with the absolute floors removed —
and diff with `python debugtests/index-captures.py --diff <RUN_A> <RUN_B> --tag alphaHoldSlow`, then
again on `alphaHoldFast`. The two signals to read first, in order:

1. **`gateMinUp` on `EW1 · alphaHoldSlow`** — the single cleanest discriminator (§4). Expect a *rise*
   toward 1.0 after the fix, because the proportional onset (0.7059) sits above where that lane flies.
2. **`wobbleEpisodesAoa` on `trainer` / `EW1` / `Darkreach`** (the lim-10 group, `alphaHoldFast`) —
   this is where a narrowed fade would reintroduce the relay the floor was added to prevent. **Any
   non-zero reading here is the fix failing**, and it is the reason to fly the fast arm at all.

Leave `ScenarioArmToggle` **empty** for this comparison: the fix is a source change, not a config
lever, so it cannot be swept ABBA within one session and the arms would only split each lane's n.

---

## 7. Design decisions worth defending

**`alpha-steps` is kept, unchanged.** R39-E §3 corrected a claim in the original brief: `alpha-steps`
at 8000 m / 250 m/s put **7 of 8 airframes on the limiter and 2 of 8 past the ceiling**, with bank
clamp **0.0%** and turn-rate cap 0–4.4% — it is the only card in the corpus that has ever produced
above-ceiling data, and its pure-pitch geometry is why. `alpha-pullup` is that insight taken further
(rate-limited ramps instead of steps, corner-relative entry, 10 lanes), so `alpha-steps` stays as
**the R35 comparison baseline and the mirrored-pair card**. Editing it would destroy the only
before-comparison available.

**`alpha-sweep` is kept on disk but marked SUPERSEDED in its own `note`.** Deleting it would strip
R39's 61 captures of their dimension row in `index-captures.py --cards`, which joins on the file
basename.

**No mirror push — a documented exception** to the mirrored-pair rule, alongside the three
`cards/README.md` already lists. The question here is the *positive* ceiling and the `aoaFade` floor
that governs `gateMinUp`; a push would double the card to answer a question `alpha-steps` already
covers. `gateMinDn` = 1.000 is therefore expected non-exposure.

**Two scored segments, not one, and not four.** One rate cannot cover the roster (§3.2). A staircase
of four would, but `cards/README.md`'s *one card = one test* rule bites: each successive pull would
start from whatever energy state the previous left, making the later segments unattributable. Two is
the minimum that exposes all ten lanes, and both are read as independent probes of the same guard.

**Throttle pinned at 0.40, biased low rather than set to hold** — the full argument is §3.5. Short
version: a card only *sets* its entry speed and nothing holds it, the drift is airframe-dependent
(R37 measured 1.10×–2.04× across one capture at the 0.70 default), and here the risk is **one-sided**
— drift up destroys exposure, drift down reinforces the sweep. Pinning it at all also removes the R18
failure mode where an operator's leftover global silently sets the energy profile, and it
self-documents in the capture's `# override` header.

**No new segment tags.** `alphaHoldFast` / `alphaHoldSlow` resolve through the existing
`alphaHold` → `alpha_hold` rule by prefix match, and `alpha_hold` is the right type: it brings the
sustained branch's `deltaEnergyHeightM` / `deltaTAS`, which is what C6 reads. Nothing in
`ScenarioPlayer.cs` or `scorecard.py` needs to change. Verified: all 39 cards in `cards/` pass the six
rules `scorecard.py --selftest` enforces (name == basename, first segment `arm`, positive durations,
no repeated scored tag, track arrays long enough, every tag resolves to a known type).

> `python debugtests/scorecard.py --selftest` currently **fails at line 1649** on a synthetic
> `authBank` assertion — that is another agent's in-flight edit to `scorecard.py` (it shows as
> modified in git), unrelated to `cards/` and upstream of the card scan. The card-scan half was run
> separately against these files and passes.

### Known risks, and the signal that catches each

| risk | signal to read |
|---|---|
| The 18 s recovery arm leaves the aircraft too fast or too slow for `alphaHoldSlow` | `spd` at the first sample of `alphaHoldSlow` vs the entry speed |
| Darkreach departs rather than mushing (it is past its ceiling at entry) | `aoaPeakOverCeiling` ≫ 2.5 with `off` diverging; if so it is a departure capture, not a guard measurement |
| CAS1 / COIN predictions are `V_stall`-derived and untested | their `aoaLimiterActivePct` on `alphaHoldFast` — if 0, C1 non-exposure, re-fly those two at a higher rate |
| A lane climbs far enough that thrust cannot hold it | `alt` trace; there is no service ceiling in the game, and the 500 m floor abort is 7.5 km away, so this degrades to a mush rather than aborting |

---

## 8. Unrelated: ledger #51 placement-forensics pair

Separate experiment, requested alongside. **Not an alpha card, and it shares nothing with §1–§7.**

**What it separates.** Under v0.96+, 7 of 7 Darkreach lanes scheduled ≥5 replicates died — 6 at
replicate 5, 1 at replicate 4, none at 1–3, no survivors past 5, across 4 mod versions, 6 cards and 2
lane distances (geometric-hazard likelihood ≈ 3e-8). The failure indexes on **placement count** and
nothing else measured: invariant to flight seconds per replicate (32 s vs 126 s in one session), to
snapback distance (5.0 vs 25.4 km), to cumulative snapback (13.8 vs 75.9 km) and to lane distance
(49.5 vs 62.0 km). But placement count is **collinear with card-run count in every batch ever flown**.
These two cards break the collinearity: 12 placements with almost no flight between them.

| card | lanes | per replicate | total |
|---|---|---|---|
| `place-noop` | `Darkreach`, `EW1` (2, concurrent) | `arm` 3 s | 36 s/lane |
| `place-deflect` | `Darkreach` (1) | `arm` 1 s + `az25R` 2 s + `az25L` 2 s | 60 s |

Both: `startSpeedCorner: 0.95`, `startAlt: 4000`, `repeat: 12`, `ScenarioThrottle` pinned `0.40` —
byte-identical entry to `oblique-6-dwell-t040`, so the historical corpus is comparable.

**Why not `oblique-6-dwell-t040` directly**, as originally suggested: it declares `repeat: 4` over a
16-lane fleet, and **both are read off the card, not off F1** — there is no way to run it Darkreach-only
at 12 replicates without editing a card another investigation is using. `place-deflect` is the matched
deflection arm instead, and it is a *better* contrast: identical placement count **and** identical
(minimal) flight time, so a difference is attributable to deflection alone rather than confounded with
126 s of flight per replicate. ±25° was chosen to exercise the **roll** channel — the one surviving
correlate for why this is Darkreach-and-EW1-specific is `maxRollAngularVel = 3` alongside
`maxPitchAngularVel = 0.3`, held by exactly those two.

### Operator instructions

| setting | value |
|---|---|
| `Drone/DroneEnabled` | on |
| `Scenario/ScenarioBatchQueue` | **`place-noop;place-deflect`** |
| `Scenario/ScenarioCardSet` | leave as-is — the queue **writes** it per fleet |
| `Drone/DroneAirframe` | *ignored* (cards name their own lanes) |
| `Drone/DroneCount` | *ignored* (`count` 2 then 1) |
| `Scenario/ScenarioRepeat` | *ignored* (`repeat: 12` on both) |

One press of the spawn key. Fleet 2 launches once the sky is empty plus a settle gap.
**Wall clock ≈ 2½ minutes total** (~45 s + ~65 s of flying, plus stagger, despawn and the inter-fleet
gap). Confirm `[drone] batch queue: 2 fleets` appears in the log within the first ten seconds.

### Reading it

**The readout is the abort, not a metric.** The single `place-noop` segment is tagged `arm` and is
excluded from scoring on purpose — what is measured is *which replicate index the run stops at*,
which lands in the CSV's `# stop` line and in `[drone]` / `[card]` in `LogOutput.log`.

- **Darkreach `place-noop` dies at placement 4–5** → placement is causal, flight exposure is
  irrelevant, and #51 is a bug in the placement path.
- **Darkreach `place-noop` runs 12 clean** → placement count is a proxy; flight exposure is required
  and the investigation goes back to the flight side. `place-deflect` then says whether *deflection*
  is the missing ingredient.
- **EW1 `place-noop`** settles whether the threshold is per-airframe at no extra cost.

> **Do not write a criterion on `dmgFrac`.** The abort check runs *before* the row is written, so an
> aborted capture reads `dmgFrac = 0.0` by construction. The column is structurally dead for this
> question.

---

## 9. Also in this pass — other `cards/` fixes, unrelated to the alpha work

### 9.1 `stol-steps` / `stol-sweep` never flew slow *(throttle)*

Both declare `startSpeed: 90` and neither held it: the speed is written once at the placement, the
throttle was unpinned so the batch ran at 1.00, and the jets were at **144–147 m/s by the end of the
6 s arm** and **340–381 m/s (2.1–2.4× corner)** by the last scored segment — faster than anything else
in the run, on the grid's two *low-q* cards. Only COIN (104 m/s mean) actually flew one. Both now pin
`Scenario/ScenarioThrottle` = **0.25**.

**How 0.25 was chosen** — from the runaway itself, not by taste:

```
90 -> 145.5 m/s in 6 s            =>  mean specific excess thrust  (T-D)/m = 9.25 m/s^2
level drag at 90 m/s  ~ g/(L/D)   =>  D/m ~ 0.98 m/s^2   (L/D ~ 10)
                                  =>  full-throttle T/m ~ 10.2 m/s^2   (T/W ~ 1.04, plausible low+slow)
throttle to hold 90 m/s           =>  0.98 / 10.2  ~  0.10        (thrust linear in throttle)
```

**The mod will not accept 0.10.** `EntryThrottle` snaps anything below `MinThrottle` = 0.25 back to the
**0.70 default** (`ScenarioPlayer.cs:1365`), so pinning 0.10 would fly at 0.70 — worse than doing
nothing. **0.25 is a floor, not an optimum.** It leaves ~+1.6 m/s² of residual excess: ~+57 m/s over
`stol-sweep`'s 36 s and ~+91 m/s over `stol-steps`' 58 s. That is a 4–6× improvement on 340–381 m/s and
it is *not* a hold.

**The check, on the first capture: `spd` at the end of the arm.** Still above ~120 m/s ⇒ the throttle
lever is exhausted and the remaining fix is mod-side (`MinThrottle`), not a card edit.

### 9.2 `stol-sweep`'s altitude did not cover the maneuver

Nine of ten lanes hit `abort: altitude floor (500 m MSL)` 19–26 s into replicate 1; all 13 captures
lost **1846–1963 m**, and COIN only survived by ending at 580 m descending 75 m/s. `startAlt: 2500`
left 2000 m of usable air. Raised to **6000 m**, sized from those 13 measurements rather than rounded:

```
worst measured mean sink   = 1963 m / 19 s = 103 m/s
every capture was TRUNCATED by the floor, so the full 36 s cost is at least 36 x 103 = 3719 m
6000 - 3719 = 2281 m, i.e. 1781 m still above the 500 m floor
```

Corroborated, not just computed: **R27's `sweep-lowq` is the identical 36 s `deriveAzRate` shape at
6000 m and took 0 aborts in 32.** Pinning the throttle down (§9.1) also removes the thrust that was
previously paying for the descent, so the altitude had to be sized for the **fixed** card, not the
flown one.

`stol-steps` went 2500 → **4000 m** for a weaker reason, stated as such: it never aborted, but it is
58 s long with a deliberate 12 s 40° nose-down leg (~900 m at these speeds) and it is about to fly with
much less thrust. 4000 m leaves 3500 m of usable air.

> **Not fixed here, and it shapes both cards:** the harness kills the whole **lane** on an abort rather
> than the replicate (`ScenarioPlayer.Finish` nulls `_queue`, which *is* the replicate expansion),
> which is why the batch produced 13 captures instead of 40. The harness agent owns that. Also
> `RateMaxDegS = 30` clips 4 of 10 derived sweep rates, so `stol-sweep`'s "same fraction of structural
> g on every airframe" premise is **false on those four lanes** — read the derived rate off the
> `[card] suite start` line before making any cross-airframe claim from it.

### 9.3 `e2-rel-turn-lead` deleted

Its `armToggle` named `Control/RelativeTurnLead`, a config entry a parallel agent removed along with
its branch after the A/B came back spent (`leadDeg` separated 38×, standing error moved 0.2–3.8%,
inside the batch's own 0.1–4.7% null contrast). **`ResolveArm` fails soft**: with the toggle
unresolvable it warns once and then flies every replicate on the same arm while each capture still
labels itself `arm=0`/`arm=1` — a complete, well-formed, entirely fictional A/B, which is the exact
silent null the arm machinery exists to prevent. Deleted from the repo **and from the game load path**
(the live path — a repo-only deletion leaves it selectable). Its rows are gone from `README.md`'s grid
table and `TOMORROW.md`'s runbook, both renumbered, and `README.md` now records that the sweepable set
is **four levers at five `Arm()` sites**. Its R39-D captures are archived and analysed; nothing is lost.

`TOMORROW.md` was also updated for the alpha redesign — batch 7 was `alpha-sweep`, which is now
superseded and must not be launched; it is `alpha-pullup` at 10 lanes instead.
