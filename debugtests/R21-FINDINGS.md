# R21 — 10x `fixedwing-sweep`, v0.79.0, KR-67 (Multirole1), mrFF=1

Ten replicates of one card, one build, one config. Source:
`<game>/BepInEx/mouseaim-rec-v0.79.0-R21-{01..10}-fixedwing-sweep-*.csv` (+ `.airframe.json`
sidecars, shared anomaly log `mouseaim-anomalies-v0.79.0-R21-20260728-214036.log`).

**Data integrity.** The 58-column header matches the stated order **exactly**, byte for byte, in all
10 files — no mismatch to report. Each file is `arm` (96–97 samples, 6.0 s) + `turn360` (480–481
samples, 29.9 s); all 10 stopped with `reason=card 'fixedwing-sweep' complete` (none aborted, none
truncated). 4802 pooled `turn360` samples. Config on the header line: `maxBank=72 trGain=0.92
leadT=0.65 bankDz=2.5 alignHold=5.0 rollDamp=0.10 rollG=1.00 chaseDamp=0.25 sens=3.0 thr` fixed at
0.700, `mrFF=1`. (Note `trGain=0.92`, not the 0.90 in the hypothesis — it does not change any
conclusion; both give bankTR ≈ 81.8°.) The anomaly log holds 34 lines, all `overshoot` fired at
`t≈64/102/…` in the **arm** window at `off=5.0 bank≈10`, i.e. the roll-in, not the turn. Nothing
fires during `turn360`.

---

## Verdict

1. **The bank-clamp arithmetic is right and the bank-clamp *story* is wrong.** The clamp is active
   on 97% of the turn and discards ~10° of demand — exactly as stated. But the roll servo gives the
   clamped bank target **2% weight**, flies **+8.1° past it**, and would fly the same bank if
   `MaxBankAngle` were 90°. The clamp is a bystander, not the cause of the 9.4° lag.
2. **The plant is nowhere near saturated.** 63% of the airframe's structural turn rate, 60% of its
   g, 27% of its AoA limit, 39% of pitch stick in reserve, AoA gates never once active. Every A/B on
   this card is meaningful; the 9.3° lag is a control-law defect with plenty of authority to fix it.
3. **The noise floor is tiny and the replicates are not quite exchangeable.** `rmsPointingErrorDeg`
   has sd 0.077° (CV 0.75%); at 4 per arm the MDE is **0.11°, ~1% of the mean**. But entry airspeed
   drifts 2.2 m/s monotonically across run order and two metrics correlate with run index at
   |r|>0.82 — block or interleave the arms.

---

## Q1 — Is the sustained turn bank-clamp saturated?

### The mechanical half: CONFIRMED, exactly as stated

Per run, over `turn360` only (`|targetBank|` and `|tBankE|` within 0.01 of 72.0; `|bankTR|` > 72):

| run | `targetBank`@72 | `tBankE`@72 | `bankTR`>72 | mean `bankTR`−72 | max |
|----:|----------------:|------------:|------------:|-----------------:|----:|
|  1 | 96.9% | 94.0% | 99.4% | 9.96 | 11.70 |
|  2 | 96.9% | 94.0% | 99.4% | 9.95 | 11.70 |
|  3 | 97.1% | 94.0% | 99.4% | 9.96 | 11.70 |
|  4 | 96.9% | 94.0% | 99.4% | 9.97 | 11.70 |
|  5 | 97.1% | 94.0% | 99.4% | 9.96 | 11.70 |
|  6 | 97.1% | 94.0% | 99.6% | 9.94 | 11.70 |
|  7 | 97.1% | 94.0% | 99.6% | 9.94 | 11.70 |
|  8 | 96.9% | 93.5% | 99.4% | 9.93 | 11.70 |
|  9 | 97.1% | 93.8% | 99.4% | 9.95 | 11.70 |
| 10 | 96.9% | 93.6% | 99.2% | 9.95 | 11.70 |
| **pooled** | **96.98%** | **93.86%** | **99.40%** | **9.95** | **11.70** |

`|bankTR|` spans [0.00, 83.70], mean 81.75 — the demand really is ~82° for the whole sweep and the
clamp really does throw away ~10° of it, on essentially every sample. Between-run spread is
negligible (≤0.2 pp), so one run was a fair sample of ten.

### The causal half: REFUTED

The recorded `bankTR` is Apply's shared value taken **after** the v0.55 achievability cap and
**before** the `MaxBankAngle` clamp, so `omegaDes` is recoverable exactly by inverting
`bankTR = atan(ω·V/g)`. In the settled window that gives 14.63 °/s demanded against an `omegaMax` of
19.48 °/s — the achievability cap binds on only **4.8%** of samples. So the demand survives the
`omegaMax` gate, hits the 72° wall, and is truncated there. All as claimed.

**But the roll servo never flies that number.** `ApplyEvolvedLegacy` computes
`rollErr = Lerp(eFine, eAlign, blendWeight)` where `eFine = t.right.y + sin(tBankE)` is the only
term carrying the bank target, and

```
lateralHold = clamp01((|azErr| − FineBankDeadzone) / EvolvedAlignHoldDeg)   # (|azErr|−2.5)/5.0
blendWeight = max(bigTurn, lateralHold)
```

With `|azErr| ≈ 10°`, `lateralHold` rails to 1.0. Measured over the pooled turn:

| quantity | value |
|---|---|
| `bigTurn` | mean 0.226 |
| `lateralHold` | mean 0.980, **97.0% of samples at exactly 1.0** |
| `blendWeight` | mean 0.980, **97.0% at exactly 1.0** |
| **`eFine` weight = 1 − `blendWeight`** | **mean 0.020** |

The whole bank pipeline — `azErrPred` → `azTR` → `omegaDes` → `bankTR` → `MaxBankAngle` → `tBankE` —
reaches the roll command through a gain of **0.02**. Contribution check at the operating point:
`0.980 × eAlign(0.019) = +0.0186` versus `0.020 × eFine(−0.0275) = −0.00055`. `eAlign` outweighs the
bank path **34:1**. The roll loop is regulating `phi` (the body-frame roll angle to the marker) to
≈0, and the bank that results is whatever geometry that requires.

**This is why actual bank sits above the target — and the user was right to distrust that number.**

| window | `bank` | `targetBank` | `tBankE` | `bankTR` | `bank`−`tBankE` |
|---|---:|---:|---:|---:|---:|
| whole `turn360` | 77.81 | 70.98 | 69.76 | 81.75 | **+8.04** |
| settled (tSeg ≥ 20 s) | 80.14 | 72.00 | 72.00 | 81.58 | **+8.14** |

Pooled `|bank| − |tBankE|`: mean **+8.06°**, sd 4.35, positive on **96.1%** of samples. The roll
servo is not flying `targetBank` and is not flying `tBankE`. Raising `MaxBankAngle` moves `eFine` by
`sin(81.6°) − sin(72°) = 0.038`, times the 0.02 weight = **0.0008 of roll command ≈ 0.1° of bank**.

The one path where the clamp does bite is **`coordPull`**, which sizes off `|sin(tBankE)|` and feeds
pitch at full weight (not blended). Unclamping would raise it by a factor `sin(81.6)/sin(72) = 1.040`
— worth ~0.015 of pitch stick, ~0.3 °/s of turn rate. Real, but ~4% of the deficit, not the story.

### `azErr` vs `azErrPred` — the lead does eat the error, and the floor is what stops it

Settled window: raw `azErr` = 9.31°, `headingRateFilt × TurnLeadTime` = 7.85°, so the unfloored lead
prediction would be **1.46° — 84% of the real error removed**, exactly as hypothesized. It does not
land there: `predFloor = 0.30` (a `const` in `ChaseController.cs`, not a Cfg knob) clamps
`azErrPred ≥ 0.30·azErr` and holds it at **2.79°**, giving back +1.33°. The floor is binding on
**82.2% of the whole turn and 100.0% of the settled window** — `azErrPred/azErr` reads 0.300 to
three digits. So `predFloor` is a *third* stacked saturation nobody was looking at, and it is the
only reason a proportional term exists at all in a sustained sweep.

### Rate tracking — matched

`headingRateFilt` and `aimRate` agree in sign on 99.5% of samples.

| window | `aimRate` | `headingRateFilt` | signed error |
|---|---:|---:|---:|
| whole `turn360` | +11.92 | +11.58 | **−0.336 °/s** |
| settled (tSeg ≥ 20 s) | +12.06 | +12.07 | **+0.009 °/s** |

The whole-segment deficit is entirely the roll-in transient. **In steady state the v0.78 marker-rate
feed-forward tracks the commanded rate to within 0.01 °/s.** The rate loop is not the problem.

### What actually holds the 9.3° lag

`azErr` is still decaying at the end of the segment: slope over the last 10 s is −0.033 °/s
(consistent across all 10 runs, −0.026 to −0.036), which would need **286 s** to close the remaining
9.3°. It is a quasi-steady-state offset, not a converged one and not a growing one.

Two things hold it, and neither is the bank clamp:

- **`_iPitch` is dead.** The integrator is scaled by `fineBlend = clamp01(1 − off/FineAngle)`, and
  `FineAngle = 6°` while `off ≈ 10.2°` — so `fineBlend = 0` for the entire turn. Recorded `iPitch`:
  mean 0.000, range [−0.001, +0.002] against a 0.12 cap. There is nothing in the loop that
  integrates a standing pointing error outside the 6° fine cone.
- **The pitch channel is a pure P loop and it is not asking for the demand.** Achieved turn rate
  equals `|outP| × omegaMax` to three digits (11.58 vs 11.58 °/s pooled), and `outP` sits at −0.609.
  The bank pipeline commands 14.98 °/s; the pitch stick delivers 11.58.

  *Caveat, stated because it is the weakest thing here:* an offline least-squares decomposition of
  `outP` into `pErrTerm` / `coordPull` / rate-damping fits at R²=0.992 but returns coefficients
  ~0.63× the source constants and a sign-flipped damping term. Every signal in this segment is
  near-constant, so the terms are collinear and **not identifiable from this capture**. Do not trust
  a per-term pitch budget from R21; the claims above rest only on directly recorded columns.

---

## Q2 — Is the aircraft at a physical limit?

**No. Nowhere close.** `aircraftGLimit = 9`, `alphaLimiter = 27°`, `buffetOnsetAlpha = 5°`
(sidecar), all ten runs identical (mass 25563 kg ± 0.2, fuel 8200 kg, same loadout).

| run | mean g | peak g | mean AoA | peak AoA | V | ω@n=9 | ω@n used | `aimRate` | achieved | **cmd/ω(n=9)** | AoA gates |
|----:|-------:|-------:|---------:|---------:|------:|------:|---------:|----------:|---------:|---------------:|----------:|
|  1 | 5.45 | 6.25 | 6.95 | 7.54 | 266.7 | 18.85 | 11.29 | 11.92 | 11.58 | **63.2%** | 0.0% |
|  2 | 5.45 | 6.24 | 6.95 | 7.53 | 266.8 | 18.85 | 11.29 | 11.91 | 11.58 | 63.2% | 0.0% |
|  3 | 5.45 | 6.25 | 6.96 | 7.55 | 266.6 | 18.85 | 11.30 | 11.92 | 11.59 | 63.2% | 0.0% |
|  4 | 5.44 | 6.41 | 6.97 | 7.72 | 266.1 | 18.89 | 11.29 | 11.93 | 11.59 | 63.2% | 0.0% |
|  5 | 5.46 | 6.39 | 6.96 | 7.52 | 266.8 | 18.84 | 11.31 | 11.93 | 11.60 | 63.3% | 0.0% |
|  6 | 5.46 | 6.24 | 6.96 | 7.53 | 266.8 | 18.84 | 11.31 | 11.93 | 11.60 | 63.3% | 0.0% |
|  7 | 5.45 | 6.29 | 6.97 | 7.59 | 266.3 | 18.88 | 11.30 | 11.93 | 11.60 | 63.2% | 0.0% |
|  8 | 5.41 | 6.33 | 6.97 | 7.69 | 265.6 | 18.93 | 11.25 | 11.91 | 11.56 | 62.9% | 0.0% |
|  9 | 5.42 | 6.64 | 6.97 | 7.69 | 265.8 | 18.92 | 11.26 | 11.92 | 11.57 | 63.0% | 0.0% |
| 10 | 5.41 | 6.35 | 6.97 | 7.69 | 265.6 | 18.93 | 11.24 | 11.90 | 11.56 | 62.9% | 0.0% |

(ω in deg/s, `ω = g·√(n²−1)/V` from recorded `spd`.)

### Answer to the headline question

**At the commanded 12.06 °/s the card is asking for 63% of the airframe's available turn rate.**

Stacked-limit occupancy over the pooled turn (n=4802) — note which lines are law and which are plant:

| limit | occupancy | law or plant? |
|---|---:|---|
| `predFloor` (0.30) | **82.2%** | law |
| `MaxBankAngle` (72°) | **97.0%** | law |
| roll `blendWeight` = 1.0 (bank pipeline → 2% weight) | **97.0%** | law |
| `omegaMax` achievability cap | 4.8% | law (probe-derived) |
| AoA gates (`aoaGU`/`aoaGD` < 1) | **0.0%** | plant |
| `outP` railed (\|·\| ≥ 0.99) | **0.0%** | plant |
| `outR` railed | **0.0%** | plant |

Every plant limit reads zero. Every law limit reads 82–97%.

- **g**: 5.44 used of 9 available (60%); worst single sample 6.64.
- **AoA**: 6.96° mean, 7.72° worst of a 27° limiter — 27% of the ceiling. Above `buffetOnsetAlpha`
  (5°), so the airframe is buffeting, but the limiter is untouched and `aoaRec` is identically 0.
- **Pitch stick**: `outP` mean −0.609, worst −0.794 → **39% mean headroom, 21% at the worst sample.**
- **FBW tracking**: `fbwTgtPR` −0.2020 vs `fbwPR` −0.2007 rad/s — the game's inner loop delivers
  **99.4%** of the rate it is asked for. The plant is not mushing, not reversed (`pEff` 0.964).
- **The full uncapped demand is flyable.** 14.63 °/s at 259.7 m/s needs 81.6° bank, n = 6.83, versus
  a structural 9. The law's own `omegaMax` says 19.48 °/s is available. Nothing physical stops it.

### Energy — the one real caveat

The turn is **not** energetically sustained: `deltaTAS` = −15.4 ± 0.5 m/s and altitude drops
~470 m over the 30 s (`deltaEnergyHeightM` = −884 ± 24 m). At 80° bank and 5.5 g, the vertical lift
component `n·cos(bank)` = 0.955 < 1 — the aircraft is descending ~15 m/s at −3.4° flight path. This
is a **card** property, not a plant limit: `thr` is pinned at 0.700 for the whole run (sd 0.000).
Speed decaying 273 → 258 m/s during the segment is why the early and late thirds of `turn360` are
not the same operating point, and why the settled-window numbers above differ from whole-segment
means.

**Conclusion: the plant has headroom, the law is saturated, and the 9.3° lag is a control-law defect
worth fixing.** Any A/B on this card is measurable.

---

## Q3 — The noise floor

`scorecard.py --json`, `turn360` segment, 10 replicates. MDE = `2.8·sd/√n` for a two-arm comparison.

| metric | mean | sd | min | max | CV% | MDE n=4 | %mean | MDE n=10 | %mean | r(run idx) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `rmsPointingErrorDeg` | 10.3577 | 0.0773 | 10.2842 | 10.5199 | 0.75 | **0.108** | **1.04%** | 0.068 | 0.66% | +0.296 |
| `terminalOffDeg` | 9.3679 | 0.0585 | 9.2924 | 9.4859 | 0.62 | 0.082 | 0.87% | 0.052 | 0.55% | **−0.824** |
| `minOffDeg` | 0.3800 | 0.1763 | 0.0600 | 0.6300 | **46.38** | 0.247 | 64.9% | 0.156 | 41.1% | −0.171 |
| `meanTurnRateDegS` | 11.5838 | 0.0151 | 11.5599 | 11.6039 | 0.13 | 0.021 | 0.18% | 0.013 | 0.12% | −0.412 |
| `gPeak` | 6.3390 | 0.1231 | 6.2400 | 6.6400 | 1.94 | 0.172 | 2.72% | 0.109 | 1.72% | +0.535 |
| `gSustained` | 5.6595 | 0.0130 | 5.6400 | 5.6700 | 0.23 | 0.018 | 0.32% | 0.012 | 0.20% | **−0.839** |
| `aoaPeakDeg` | 7.6050 | 0.0822 | 7.5200 | 7.7200 | 1.08 | 0.115 | 1.51% | 0.073 | 0.96% | +0.623 |
| `deltaTAS` | −15.3500 | 0.4859 | −15.8000 | −14.6000 | 3.17 | 0.680 | 4.43% | 0.430 | 2.80% | +0.684 |
| `deltaEnergyHeightM` | −884.29 | 24.03 | −910.50 | −847.42 | 2.72 | 33.64 | 3.80% | 21.28 | 2.41% | +0.777 |

### The sentence

> **With 4 replicates per arm, a change in `rmsPointingErrorDeg` must be at least 0.11° — about 1%
> of the current 10.36° — before it is distinguishable from run-to-run noise. At 10 per arm that
> floor drops to 0.07°.**

This card is remarkably quiet: the entry state is scripted, so 8 of 9 metrics have CV under 3.2%.

**Do not use `minOffDeg` as an endpoint.** CV 46%, MDE at n=4 is 65% of its own mean. It is the
single best approach anywhere in the segment — for `turn360` that is a transient artifact of the
roll-in, not a property of the turn. Everything else is usable.

### Drift across run order — replicates are NOT fully exchangeable

Two metrics correlate with run index at |r| > 0.82:

| metric | r | 01 → 10 change | in sd |
|---|---:|---:|---:|
| `terminalOffDeg` | −0.824 | −0.126 (−1.34% of mean) | 2.2 sd |
| `gSustained` | −0.839 | −0.030 (−0.53% of mean) | 2.3 sd |

`deltaTAS` (+0.684) and `deltaEnergyHeightM` (+0.777) lean the same way. **The cause is not fuel** —
the sidecars show `fuelLevel = 1` and mass 25563.00 → 25563.22 kg (a 0.22 kg / 8.6 ppm accumulation
artifact, physically irrelevant). The cause is **the card's entry state**:

| run | entry `spd` | entry `alt` | entry `bank` | `posZ` |
|----:|---:|---:|---:|---:|
| 1 | 273.4 | 3964.7 | 0.00 | 2 094 |
| 5 | 273.7 | 3964.5 | 0.00 | 18 972 |
| 10 | 271.5 | 3971.6 | −1.40 | 31 915 |

Entry airspeed drifts **273.7 → 271.5 m/s (spread 2.2 m/s, r vs run index = −0.753)**, entry bank
from 0.00 to −2.1°, and position **30 km downrange** across the batch — `ForceEntryCondition` is not
resetting map position, and the 6 s `arm` window does not fully re-converge airspeed. Entry speed
correlates with `terminalOffDeg` at **r = +0.719**, which is the mechanism behind the run-index
trend.

Magnitude in practice: split-half (runs 1–5 vs 6–10) differs by 0.077° on `terminalOffDeg` against
an n=5 MDE of 0.073° — **a first-half/second-half split would register as a false positive.**
So: **interleave or block the arms (ABBA / ABABAB), never A×4 then B×4.** With interleaving the
drift becomes a nuisance variance, not a confound.

---

## Q4 — New `scorecard.py` saturation diagnostic

Added `cfg_params(meta)` + `saturation_metrics(rows, cols, cfg, fbw)`, wired into `compute_segment`
via a new optional `ctx=None` argument (so the existing 4-arg call still works — it just skips these
metrics). Four keys, added to every non-excluded segment:

| metric | meaning |
|---|---|
| `bankClampActivePct` | % samples with `\|targetBank\|` at `Cfg.MaxBankAngle` |
| `bankDemandExcessDeg` | mean `\|bankTR\| − MaxBankAngle` over clamped samples — the demand actually discarded (0.0, not None, when it never clamps) |
| `turnRateCapActivePct` | % samples with the demanded ω at the v0.55 `omegaMax` achievability cap |
| `turnRateDemandRatio` | mean(ω demanded) / mean(`omegaMax`). ≥ 1 ⇒ the card asks for something the airframe cannot fly and no A/B on it means anything |

Notes:
- AoA-gate occupancy is **not** duplicated — `aoa_g_metrics` already reports `aoaLimiterActivePct`.
- The demanded ω is recovered by inverting `bankTR`'s own definition (`ω = tan(bankTR)·g/V`), so
  there is no second copy of the demand chain to drift out of lockstep with `ChaseController`.
- `omegaMax` mirrors Apply's fixed-wing branch (`gLimit·9.81/max(V, 0.75·corner)`, q-scaled below
  corner, × `max(0.3, aoaGU)`, with the raw-law `maxPitchAngVel` branch when assist is off at low q)
  from the `# fbw` header + `spd`/`airDensity`/`assist`/`aoaGU` columns.
- Both halves degrade independently and fail-soft: no `# config` line → the bank pair is skipped;
  pre-v0.55 `# fbw` → the turn-rate pair is skipped. Stdlib only. `--json` gains keys, renames none.
  Unrecognised-`segTag` warning behaviour untouched.
- `--selftest` extended with exact-arithmetic asserts for both metric pairs, the AoA-gate shrink of
  `omegaMax`, the never-clamped 0.0-not-None case, ctx/no-ctx backward compatibility, and `arm`
  exclusion. `python scorecard.py --selftest` → OK. `check-architecture.py` → clean.

Output on all 10 R21 runs (`turn360`):

| metric | per-run range | mean |
|---|---|---:|
| `bankClampActivePct` | 96.88 – 97.08 | **96.98** |
| `bankDemandExcessDeg` | 9.93 – 9.98 | **9.95** |
| `turnRateCapActivePct` | 2.71 – 7.71 | **4.83** |
| `turnRateDemandRatio` | 0.786 – 0.791 | **0.788** |

Read together with `gSustained` 5.66 / 9 and `aoaLimiterActivePct` 0.0, the pair
"`bankClampActivePct` 97% + `turnRateDemandRatio` 0.79" is the self-reporting signature of exactly
this finding: **law saturated, plant idle.**

---

## Card defects worth fixing before more A/Bs

1. **Entry state is not reset** (see Q3 drift). Position drifts 30 km, entry airspeed 2.2 m/s.
2. **Throttle is pinned at 0.700** for the whole card, so the "sustained" turn bleeds 15 m/s and
   470 m. The segment's operating point at t=25 s is not the one at t=5 s. Either close a speed loop
   on the card or score only a settled window.
3. **`turn360` is 30 s and `azErr` has not converged** at the end (−0.033 °/s, ~286 s to close).
   `terminalOffDeg` measures a transient, albeit a very repeatable one.

## Where to look instead of `MaxBankAngle`

Ranked by measured leverage, all consistent with the "one law, no per-plane tuning" rule since each
keys off live state or a probed parameter:

1. **The roll blend gate.** `lateralHold = clamp01((|azErr| − 2.5)/5.0)` rails at |azErr| ≥ 7.5°, so
   any sustained turn with a lag larger than that hands the roll loop entirely to `eAlign` and
   discards the bank pipeline. That is the single mechanism that makes the clamp, `predFloor`,
   `omegaMax` and `bankTR` all irrelevant at once.
2. **The dead integrator.** `_iPitch` is gated by `fineBlend` (`off < FineAngle = 6°`) so nothing
   integrates a standing pointing error in a large-error sustained turn. This is what makes the
   offset permanent rather than decaying.
3. **`predFloor = 0.30`** — binding 100% of the settled window, and a hard `const`, not a Cfg knob.
   It is currently *helping* (without it the lead would eat 84% of the error), but it means the
   effective proportional gain in a sustained sweep is `0.30 × AssistTurnRateGain`, i.e. 0.28, not
   0.92.
4. `MaxBankAngle` — worth ~0.3 °/s through `coordPull` only. Last.

## Flight tests that would discriminate

Same card, same 4-per-arm blocked/interleaved design, A/B one change at a time. Baseline for all:
`rmsPointingErrorDeg` 10.36 ± 0.08, `terminalOffDeg` 9.37 ± 0.06, `meanTurnRateDegS` 11.58 ± 0.02.

- **Null test first — raise `MaxBankAngle` 72 → 85.** Prediction from this analysis: `bank`
  unchanged within ±0.3°, `terminalOffDeg` improves by **less than 0.3°** (the `coordPull` path
  only). If it improves by ≥ 1°, this whole analysis is wrong and the clamp really was the cause.
  Fastest single experiment; run it before anything else. Pass = `bankClampActivePct` drops toward 0
  while `terminalOffDeg` barely moves.
- **Un-rail the roll blend** — cap `lateralHold` (e.g. at 0.7) or make `blendWeight` fall off once
  the marker is being tracked at rate. Pass = `bank` converges toward `tBankE` (`bank − tBankE` mean
  under +2°, currently +8.1°) and `terminalOffDeg` drops by ≥ 0.11° (the n=4 MDE). Failure signature
  to watch: the v0.52 wing-rock returning — check `wobbleEpisodesBank` and `stickFlipRateR`.
- **Let the pitch integrator live outside the fine cone**, gated on a settled marker rate rather
  than on `off`. Pass = `iPitch` non-zero (currently ±0.001 against a 0.12 cap), `terminalOffDeg`
  falls, `g` rises toward 6.8 (still 25% under the 9 limit), AoA stays under 10° of the 27° limiter.
  Failure = AoA gates start firing (`aoaLimiterActivePct` > 0, currently exactly 0) or `outP` rails.
- **Cross-check on a different plant before shipping any of these**, per the one-law rule: a low-q
  STOL trainer where `omegaMax` and `qSched` do bind (`turnRateCapActivePct` should be high there —
  the opposite regime from R21's 4.8%), and a loaded jet near its alpha ceiling, where item 3's
  integrator must not wind against a mushing plant.
