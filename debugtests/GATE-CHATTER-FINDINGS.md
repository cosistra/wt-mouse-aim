# Gate chatter — INSTRUCTOR-LOOP.md §5, tested

**Verdict: KILLED where it was proposed. One narrow survivor.**

The hypothesis was that `ChaseController.Apply`'s independent, hysteresis-free gates chatter at
their boundaries, and that the chatter is what the maintainer feels as "cross-fighting signals in
a lot of small movements". Measured over the existing captures, `REGRESSING` tick density does
**not** rise at gate-boundary crossings in the sub-degree region, in the elevation-down defect, or
in the sustained turn once the entry transient is excluded. In the two segments that most resemble
the complaint it goes the *wrong way*:

| segment | gate crossings/s | REGRESSING | risk ratio near a crossing | perm p |
|---|---:|---:|---:|---:|
| `fine` (20 s bounded ≤0.3° walk) | **5.66** — the highest in the corpus | 22.9% | **0.82** | 0.98 |
| `micro1..10` (0.2–1.0° steps, 110 blocks) | 3.03 | 28.5% | **0.88** | 1.00 |
| `elDn` (the strongest defect on file) | 3.11 | 10.3% | **1.01** | 0.50 |

A clean negative. What the data points at instead is in §5.

Tool: `debugtests/gatechatter.py` (stdlib, `--json`, `--bytag`, `--skip`, `--cone`, `--win`,
`--selftest`). It imports `flightscore.py` read-only for the tick classifier, so the two tools
cannot disagree about what `REGRESSING` means.

---

## 1. What was tested, and against what

**Corpus.** The 11 captures that ran the *complete* `fixedwing-v2` card (21 segments): R12-02,
R13-01..04, R18-02, R18-03, R19-01..04 — v0.72.0 through v0.77.0, all KR-67 Ifrit
(`Multirole1`). 231 tagged blocks, ~30 min of flight. The 10 R21 `fixedwing-sweep` runs only
carry `arm` + `turn360`, so they add replicates for one segment, not coverage.

```
python debugtests/gatechatter.py --cone 0.05 <the 11 fixedwing-v2 captures>
python debugtests/gatechatter.py --cone 0.05 --skip 2.0 <same>      # transient control
python debugtests/gatechatter.py --bytag <same>                     # micro1..10 separately
```

**Gates.** 15 real ones. Read from the recorder: `bigTurn`, `bankBlend`, `assist`, `qSched`,
`aoaGU`, `aoaGD`, `settleOn`. Recomputed from the recorded inputs and the `# config` line, using
the *same* expressions as `ApplyEvolvedLegacy`: `lateralHold`, `fineBlend`, `blendWeight`
(including `belowSuppress`, with `alignFrac == cos(phi)`), `azRamp`, `predFloor`, `eAlignLat`,
`phiWrap`, `pullTaper`. Each is reduced to a **rail state** (pinned low / in transit / pinned
high); a *crossing* is a change of rail, which covers both a binary flip and a blend entering or
leaving saturation.

**Three controls, and they are the reason this returns a negative rather than a confirmation:**

1. **Stratification.** Risk ratios are Mantel-Haenszel over (run × segment block) strata. R21
   showed replicates drift with run index (r = −0.82 on `terminalOffDeg`), so a pooled 2×2 would
   fold a between-run baseline shift into the effect.
2. **Circular-shift null.** Each stratum's outcome series is circularly shifted; that preserves
   both series' autocorrelation and marginals and destroys only their alignment. At 15 Hz
   neighbouring ticks are not independent, and a tick-level chi-square on 50 k rows calls
   everything significant.
3. **Sham gates.** `shamAz0.35`, `shamAz1.1`, `shamAz3.3`, `shamOff2.7` — same functional form,
   thresholds that appear nowhere in `ChaseController`. Every real gate is a threshold on `|azErr|`
   or `off`, and both cross a small value exactly when the nose passes through the target, which
   is also exactly when a P-loop overshoot reads as `REGRESSING`. Without a placebo arm, that
   common cause would have been reported as a confirmation.

Plus `--skip`, which drops the first N seconds of every block: a card step makes the gates cross
*and* makes the nose regress, both because of the step.

---

## 2. Primary test — REGRESSING density at gate crossings

`--cone 0.05` (the sub-degree segments are unscoreable at the 1.0° default and still mostly
`ON_TARGET` at 0.2°), win = ±0.20 s, 399 shifts.

| segment | x/s | near% | p(reg\|near) | p(reg\|far) | **RR** | perm p |
|---|---:|---:|---:|---:|---:|---:|
| `fine` | 5.66 | 82.6 | 0.220 | 0.277 | **0.82** | 0.980 |
| `micro` (107 strata) | 3.03 | 49.4 | 0.267 | 0.300 | **0.88** | 1.000 |
| `elDn` | 3.11 | 65.4 | 0.119 | 0.072 | **1.01** | 0.497 |
| `elUp` | 3.15 | 55.5 | 0.061 | 0.032 | 1.51 | 0.270 |
| `arm` | 2.42 | 45.7 | 0.040 | 0.039 | 1.31 | 0.388 |
| `reversal` | 1.80 | 40.7 | 0.031 | 0.017 | 1.86 | 0.133 |
| `az90` | 1.49 | 34.4 | 0.058 | 0.017 | 2.51 | 0.122 |
| `az10` | 1.16 | 25.8 | 0.084 | 0.024 | 3.25 | 0.003 |
| `az30` | 1.25 | 27.4 | 0.072 | 0.013 | 5.69 | 0.003 |
| `astern` | 0.89 | 24.4 | 0.088 | 0.011 | 4.96 | 0.003 |
| `az150` | 1.30 | 28.3 | 0.044 | 0.002 | 30.06 | 0.003 |
| `turn360` | 0.31 | 4.9 | 0.510 | 0.054 | 7.29 | 0.003 |

Read the first column before the last two. **The rank order of crossing rate is almost the
reverse of the rank order of RR.** The segments with 3–6 gate crossings per second are the ones
with RR ≈ 1; the ones that survive have 0.3–1.3 crossings/s. If gate chatter drove regression, the
chattery segments would be the affected ones. They are the unaffected ones.

### The survivors do not survive their controls

- **`turn360`** RR 7.29 → **1.28, p = 0.31** with `--skip 2.0`. The whole effect is the roll-in
  transient. And its shams score as high as its real gates (`shamAz3.3` RR 7.20 p = 0.003,
  `shamOff2.7` RR 7.14 p = 0.003, against real gates 6.6–10.6).
- **`az10/az30/az90/az150/astern`** do survive `--skip 2.0`. But per-gate, the effect is carried by
  **one** gate, and every other gate in those segments returns RR = 0.00 (i.e. *no* regressing tick
  anywhere near their crossings):

| segment | `predFloor` RR | best sham RR | every other real gate |
|---|---:|---:|---:|
| `az10` | 7.24 (p 0.003) | 1.53 | 0.00 except `azRamp` 4.33, `pullTaper` 7.88 |
| `az30` | 9.46 (p 0.003) | 3.16 | 0.00 |
| `az90` | 6.45 (p 0.010) | 4.95 (p 0.028) | 0.00 except `azRamp` 2.11 |
| `az150` | 36.10 (p 0.003) | 2.25 | 0.00 |

`azRamp` and `pullTaper` are plain `|azErr|` thresholds at 0.5 / 2.0°, and their shams are the
right comparison — they are within noise of a placebo. `predFloor` is not: it is a *ratio*
condition (the lead term against the raw error), it beats every sham by 2–16×, it replicates across
four independent step sizes, and it survives both skip controls. It is the one real result.

### Pooled real-vs-sham

Median RR over all testable (segment × gate) cells: **real 3.65 (n = 47) vs sham 3.16 (n = 15)** at
skip 0; **3.41 (n = 37) vs 2.10 (n = 11)** at skip 1.0 s. Overlapping distributions. Being a gate in
`ChaseController` buys almost nothing over being an arbitrary threshold on the same signal.

---

## 3. Secondary test — dwell, crossing frequency, and disagreement

**The predicted dwell signature is real but does not carry the defect.** `turn360` is exactly as
R21 described — `lateralHold` and `blendWeight` pinned high 97.6% of the time, `azRamp` 99.4%,
`pullTaper` 99.4%, at 0.03–0.08 crossings/s. `micro`/`fine` are 30–90× more active. So "pinned in
the easy case, moving in the hard case" is confirmed as an observation. It just does not predict
where the nose goes backwards.

**Two gates disagreeing.** `fineVsAlign` (fine cone by `off` while the roll loop is handed entirely
to `eAlign` by `azErr`) **never occurs** — 0.0% of every segment. `alignVsAzRamp` occurs on 2–7% of
`astern`/`elDn`/`elUp`/`reversal` and carries **zero** regressing ticks (RR 0.00). Both proposed
contradictions are dead.

**The one that is not dead: roll and yaw commanding opposite azimuth corrections** (`|outR|` and
`|outY|` both outside the 0.02 deadband, opposite signs) — the literal form of "should it roll or
should it yaw":

| segment | occupancy | p(reg\|anti) | p(reg\|not) | RR | perm p |
|---|---:|---:|---:|---:|---:|
| `elDn` | **42.6%** | 0.164 | 0.057 | 2.39 | 0.003 |
| `az10` | 20.6% | 0.068 | 0.032 | 2.10 | 0.263 |
| `az90` | 12.0% | 0.127 | 0.019 | 8.07 | 0.003 |
| `az30` | 11.6% | 0.150 | 0.013 | 11.57 | 0.003 |
| `astern` | 9.4% | 0.241 | 0.020 | 13.78 | 0.003 |
| `az150` | 8.0% | 0.110 | 0.007 | 11.80 | 0.003 |
| `reversal` | 2.6% | 0.044 | 0.032 | 1.60 | 0.125 |
| **`micro`** | **0.2%** | 0.000 | 0.364 | 0.00 | 1.000 |
| **`turn360`** | **0.0%** | — | 0.057 | — | 0.070 |

Roll–yaw cross-fighting is real, it is strongly associated with regression, and it lives in the
**large oblique and below-nose reorientations** — not in the small movements the complaint names.
In `micro` it is 0.2% of ticks and carries no regression at all.

---

## 4. Lead/lag — does the gate move first?

No. Peak reversal-rate lag against gate crossings: `micro` −0.40 s, `elDn` −0.26 s, `turn360`
−0.53 s, `elUp` −0.53 s, `az30` −0.33 s; zero or near-zero for `az10`/`az90`/`reversal`/`astern`;
only `fine` (+0.20 s) and `az150` (+0.07 s) lean positive. Where it points anywhere, it points at
the stick moving *before* the gate.

**But do not lean on this either.** Most gates here are algebraic functions of `|azErr|`, and the
yaw command is proportional to `local.x`, which shares its sign with `azErr`. A "gate crossing" and
a "yaw sign flip" are then partly the *same event by construction*, not a causal sequence. The
honest reading is that this test cannot separate the two directions on recorded data alone, and it
certainly does not support the gates leading.

---

## 5. What the data says instead

### (a) `elDn` is a sustained roll limit cycle in the below-nose hemisphere — not chatter

The strongest defect in the corpus, and the closest thing on file to the maintainer's complaint.
Measured over the **late 60%** of the block (after the reorientation is over), 11 runs, against its
own mirror `elUp`:

| | `elDn` (20° down) | `elUp` (30° up) |
|---|---:|---:|
| mean `off` | **6.92° ± 2.40** | **0.03° ± 0.05** |
| sd of `off` within the window | 2.67 | 0.03 |
| bank half-amplitude | **43.3° ± 9.2** | 0.11° |
| `outR` sign flips /s | 0.58 (≈ 0.29 Hz) | **0.00** |
| `outR` rms / `outY` rms | 0.41 / 0.13 | ~0 / ~0 |
| `blendWeight` mean | 0.43, **81% of ticks in the MID band** | 0.00 |
| corr(\|`azErr`\|, `blendWeight`) | **+0.918 ± 0.045** | — |

A *larger* step in the up hemisphere converges to 0.03° and never touches the stick again. The
down step never converges: after 4 s the nose sits ~7° off, rolling ±43° at ~0.3 Hz, for the rest of
the segment. `flightscore` scores it 0% `AIRFRAME_LIMITED`, 24% `REGRESSING`, jerk rms 1.61 (3× any
other segment), S = 0.535. The plant is unloaded there (g falls to ~0.7, AoA goes negative). This
is a law defect with full authority available.

The loop is visible in the numbers: `blendWeight` correlates **+0.92** with the `|azErr|` that the
roll-to-align is itself generating. Roll-to-align banks the aircraft → bank plus pull swings the
nose in azimuth → `azErr` rises → `lateralHold` rises → `blendWeight` rises → more roll-to-align.
Meanwhile `phi` sits in and wraps through the ±180 region where `eAlignTgt` saturates at ±1.5 and
flips sign.

`belowSuppress` (v0.67) exists to prevent exactly this and is disarmed by the symptom: its
`(1 - lateralHold)` factor removes **51%** of the intended suppression in this window, because
`lateralHold > 0` on **88%** of ticks — and it is above zero precisely because the roll-and-pull
created the azimuth error. The source comment says the factor "limits it to the `azErr ≈ 0` hang (a
genuine down-LATERAL with large `azErr` keeps its roll-and-pull)"; in a pure elevation-down step
there is no genuine lateral, and the gate still opens.

Note what this is **not**: `blendWeight` spends 81% of the window in the MID band. Nothing is
railing, nothing is chattering, and hysteresis would not touch it. It is a continuous positive
feedback gain, and the fix is a loop-shaping fix.

### (b) Sub-degree REGRESSING is step-size overshoot, not gate activity

Per-tag across the ten micro steps (deltas 0.2 … 1.0°, `--bytag`, 11 runs each):

```
r(REGRESSING%, |step|)                      = +0.785
r(REGRESSING%, max off reached)             = +0.888
r(REGRESSING%, gate crossings/s)            = +0.460
r(gate crossings/s, |step|)                 = +0.792
partial r(REGRESSING%, crossings/s | off)   = -0.632
```

Gate activity correlates with regression only because both scale with step size. Hold step size
fixed and **more gate crossings predict less regression**. `micro1` and `micro6` (both 0.2°) never
regress at the 0.2° cone; `micro5`/`micro9`/`micro10` (0.8–1.0°) regress 12–17%. `micro2` runs 2.96
crossings/s at 0% regression while `micro7` runs 1.83 crossings/s at 9.1%.

The fine-aim complaint should be chased as **overshoot and phase lag in the fine cone** — the
damping term, the output slew, the 15 Hz recorder aliasing an `OutputSlew = 6.0/s` limiter — not as
gate hysteresis.

### (c) `predFloor` is the one gate the data does implicate

`const float predFloor = 0.30f`. Its bind/unbind crossings carry a gate-specific excess in every
azimuth step segment (RR 6.5–36.1, p ≤ 0.01), 2–16× above the matched shams, surviving both skip
controls. Mechanically it is the only gate that is a *ratio* condition rather than a magnitude
threshold, and crossing it swings the effective azimuth P gain by ~3× (`0.30 × trGain` vs
`trGain`) with no hysteresis and no ramp. It crosses 0.47–0.62 times/s during the step-and-hold
segments and **1.36 times/s** during the micro steps.

Caveat, stated because it is the weakest claim here: the absolute effect is small. In `az150` the
segment regresses on 1.4% of ticks overall, and the contrast is 4.4% near a `predFloor` crossing
against 0.16% away. The RR is large because the baseline is tiny.

R21 flagged this same constant from the opposite direction (it binds on 100% of the settled turn
window and silently sets the loop gain to 0.28 against a configured 0.92). Two independent analyses
now point at it.

---

## 6. Ranked fix list

The hypothesis's own prescription — "fewer gates, with hysteresis" — is **not** supported and
should not be spent on. Ranked by measured leverage:

1. **`elDn` / below-hemisphere roll-to-align (§5a).** By far the largest effect in the corpus:
   6.9° of permanent error and a ±43° 0.3 Hz roll cycle where the mirror case converges to 0.03°.
   Two candidate changes, both ONE-LAW clean (live geometry only):
   - Break the `blendWeight` ← `azErr` ← `blendWeight` loop. `lateralHold` should not be raised by
     azimuth error the roll loop is itself producing; key `belowSuppress` on the *commanded*
     below-ness (`alignFrac`) and the marker's own rate, and drop the `(1 - lateralHold)` factor
     that is currently disarming it 51% of the time.
   - Damp the `eAlign` channel. It is a pure proportional map `phi/90` with no rate term; the roll
     rate damping downstream is on `rollRateF`, not on `phi`.
2. **`predFloor` (§5c).** Replace the hard 0.30 with a continuous blend keyed to the lead's own
   confidence (`headingRateFilt` vs its own noise floor), so crossing it is a ramp instead of a 3×
   gain step. Cheap, well-localized, independently implicated by R21.
3. **Fine-cone overshoot (§5b).** Not a gate problem. The tests to run are damping/slew-rate
   sweeps against `r(REGRESSING%, step size)` — the fix works when that correlation flattens.
4. **Nothing.** No hysteresis on `settleOn`, `lateralHold`, `bigTurn`, `qSched`, `aoaGU/GD`,
   `bankBlend`, `azRamp`, `pullTaper`, `eAlignLat` or `phiWrap`. Every one of them returns RR ≤ 1
   or a value a sham gate matches. `settleOn` in particular — the gate §5 singled out as "a mode
   switch inside the sub-0.5° cone" — crosses 0.29 times/s in `micro`, sits at its high rail 95% of
   the time, and carries **RR 0.00**: not one regressing tick near any of its crossings.

## 7. What would falsify this analysis

- A capture on a **different airframe**. Everything here is one plane (KR-67 Ifrit) at one entry
  condition. A low-q STOL trainer, where `qSched` and `omegaMax` actually bind, could put real
  crossings on gates that never move here.
- `elDn` flown at a **different speed**. If the ±43° cycle is speed-dependent it is a gain problem;
  if it is speed-invariant it is the geometric loop in §5a. The card only ever flies it at ~250 m/s
  after the same energy history.
- A **hysteresis A/B** on `lateralHold` would still be worth one run as a null test, the same way
  R21 proposed raising `MaxBankAngle` as a null test. Prediction from this analysis: no measurable
  change in `elDn` `terminalOffDeg` or in `micro` REGRESSING%. If it improves either by more than
  the noise floor, this analysis is wrong.

## 8. Reproducing

```
python debugtests/gatechatter.py --selftest                 # asserts on every estimator
python debugtests/gatechatter.py --cone 0.05 <11 captures>  # tables 2, 3, 4 above
python debugtests/gatechatter.py --cone 0.05 --skip 2.0 <same>
python debugtests/gatechatter.py --bytag --json <same>      # per-micro-step, for §5b
```

Captures: `<game>/BepInEx/mouseaim-rec-v0.7{2,6,7}*-fixedwing-v2-*.csv` for R12-02, R13-01..04,
R18-02, R18-03, R19-01..04.
