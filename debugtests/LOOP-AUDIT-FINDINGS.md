# Loop audit — "can the command this term gates move this term?"

Systematic sweep of every gate, blend, schedule and estimator in `ChaseController.Apply` +
`ApplyEvolvedLegacy` against the reusable audit question the v0.85 `belowSuppress` defect added to
[GENERALITY-REVIEW.md](../GENERALITY-REVIEW.md) (finding 13). Findings only — **no `.cs` file was
changed.** v0.82–v0.85 are green-built and flown by nobody; another unflown law change makes the
test matrix worse, not better.

Reproduce: `python debugtests/loopaudit.py --selftest` (the closed forms, no data needed), then
`python debugtests/loopaudit.py [--settled 20] <rec.csv>...`.

**Corpus.** The 11 complete `fixedwing-v2` captures (R12-02, R13-01..04, R18-02/03, R19-01..04,
v0.72–v0.77) + the 10 `fixedwing-sweep` replicates (R21, v0.79) + the v0.71/v0.75 runs that carry
the `pEff` episodes. All KR-67 Ifrit (`Multirole1`), one entry condition.
**Coverage holes, stated up front — as they were at the time of the audit (R21-era corpus):**
`aoaLimiterActivePct` was 0 in every capture *this audit read*, only 2 of the 4 required airframe
classes had flown, and there was no low-q / near-alpha-ceiling / rotorcraft flight at all. Nothing
below claims MEASURED support in those regimes.

> **CORRECTED 2026-07-31 — the AoA hole is closed and the corpus is 30× larger.** *"0 in every
> capture ever taken"* was a corpus-wide claim and it is **false**: `aoaLimiterActivePct` is non-zero
> on **66** (run, airframe, tag) cells across R1–R33, **23** of them with no railed segment anywhere.
> The clean one is **R33 `Darkreach·obDR6` — 100.0% occupancy, `railed = 0`, n = 4**, `aoaPeakDeg`
> 7.38–7.59° vs a 10° `alphaLimiter`, `authorityUsedFrac` 0.717–0.748. R29 `trainer·obUL12` (11.9% on
> 8 of 8, unrailed) was the first activation of any size. The corpus is now **1 681 captures / 10
> airframes on cards**, so "only 2 of the 4 airframe classes" is also stale — 3 of 4 are covered; the
> hovering helo is not. **F1, F6 and F7 below are re-readable against real data; where the text says
> "unfalsifiable on the present corpus", read "unfalsifiable on the corpus this audit had".**

---

## Ranked findings

| # | term | grade | severity | one line |
|---|---|---|---|---|
| **F1** | `_pitchEff` self-probe | **STRUCTURAL + MEASURED** | **HIGH** | the probe's target *is* the threshold it must cross, so it provably never crosses it — 83% of pitch demand deleted, latched, on a plant delivering 110% |
| **F2** | `lateralHold` → `blendWeight` | **STRUCTURAL + MEASURED** | **HIGH** | rails at \|azErr\| ≥ 7.5° and drops the entire bank pipeline to **exactly zero** weight; v0.78/v0.83 reach the plant through one term that is itself gated on `_yawWeak` |
| **F3** | `_yawWeak` | **STRUCTURAL + MEASURED** | **HIGH** | measures "did the error close", not "is the rudder working"; reads 0.99 while the plant is at 63% of its turn rate, and its output cuts the yaw it is measuring |
| **F4** | v0.85 `AlignRateLead` | **STRUCTURAL** (unflown) | MED | makes the roll **derivative** gain a function of `blendWeight` — 1.00×→1.64× — i.e. of the `azErr` the roll loop itself produces |
| **F5** | `belowSuppress` residual | **STRUCTURAL** | LOW | v0.85 cut the `lateralHold` path; the `bigTurn` taper is the same loop through `off`, inert below 19.3° and live above |
| **F6** | `_pitchEff` × `_alphaSchedFilt` | **STRUCTURAL** (never flown) | MED | two independent de-raters of the *same* physical event multiply to 0.09, where each is documented as flooring at 0.30 |
| **F7** | v0.83 `_stallFilt` | **HYPOTHESIS** | MED | opens the **body-frame** integrators in the large-error regime, which is exactly where the law commands 43–80° of bank |

Ranked by severity × evidence. F1 outranks F2 only because it is a hard latch with an unbounded
exit condition; F2 is the larger *steady* effect.

---

## F1 — the `_pitchEff` self-probe cannot clear its own threshold

**STRUCTURAL + MEASURED. This is the single change to make first.**

Three lines, all in `ChaseController.cs`:

```csharp
private const float PEffRevThresh = 0.15f;                              // L197
pitchEffInst = Mathf.Abs(cmd) > 0.05f ? Mathf.Clamp01(ach / cmd)
                                      : Mathf.Max(_pitchEff, PEffRevThresh);   // L1048
if (!_collective) pErrTerm *= _pitchEff >= PEffRevThresh ? Mathf.Max(effFloor, _pitchEff)
                                                        : _pitchEff;            // L1773
```

`PEffRevThresh` is used **both** as the v0.67 self-probe's *target* and as C1's floor *threshold*,
and the declaration comment states that on purpose ("one const so they can't drift apart"). That
sharing is the bug. The probe is a first-order low-pass toward `Max(_pitchEff, 0.15)`, so from below
it approaches 0.15 **asymptotically** and the `>=` test is never satisfied:

- exact: `x ← x + α(0.15 − x)` has 0.15 as a limit, not a value;
- float32: the increment `α·(0.15 − x)` rounds away once `0.15 − x < ~4.5e-7`, so `_pitchEff` stalls
  ≈30 ulps short of `0.15f`. `loopaudit.py --selftest` runs 100 000 iterations (~28 min of flight)
  and asserts `x < PEffRevThresh`.

So the estimate parks on the **un-floored** branch and the law multiplies its pitch P term by
`0.1499995` instead of `Max(0.30, ·)` — **exactly a factor of 2 of pitch authority, thrown away by a
`>=`**. And that closes the latch the self-probe exists to prevent: a P term at ×0.15 produces an FBW
target rate under the 0.05 rad/s re-measure gate for any demand whose unscaled target rate is below
0.33 rad/s, so `|cmd| < 0.05` stays true, the estimator never gets to measure, and nothing internal
can raise it. With the floor applied the same demand *would* clear the gate (`0.34 × 0.30 > 0.05`).

### Measured — R11-03, `az30`, 3.07 s

`mouseaim-rec-v0.71.0-R11-03-fixedwing-v1-20260727-214306.csv`, t = 611.08 → 614.15:

| | value |
|---|---|
| `pEff` | 0.143 → 0.150, monotone, ~+0.001 per 0.1 s (the 1.0 s release tau, asymptoting) |
| `fbwTgtPR` (cmd) | **0.045 – 0.049 rad/s** — pinned just under the 0.05 gate |
| `fbwPR` / `fbwTgtPR` | **1.10** — the plant is delivering 110% of what it was asked |
| `tgtPRaw` | −0.171, flat; unscaled the P term wanted ≈ −1.15, i.e. **rail** |
| AoA / g / spd | 1.0–2.1° / 1.7–3.1 / 275 m/s — nothing loaded, nothing saturated |

Entry mechanism (t = 610.97–611.08, three samples): `cmd` = −0.050 while `ach` = +0.009 — a
**command sign reversal against a lagging plant**, which `Clamp01(ach/cmd)` reads as 0. The 0.10 s
attack tau commits in 3 frames. The v0.64 signed ratio is right for a *sustained* reversal and wrong
for every *commanded* reversal, because the achieved rate must cross zero after the commanded one.

Exit: purely exogenous. At t = 614.22 the demand rose from outside, `cmd` reached 0.080 > 0.05, the
gate opened, `pEff` jumped 0.150 → 0.178, the floor branch engaged — and the pitch command **doubled
in one tick** (`tgtPRaw` −0.171 → −0.282). The latch exit is itself a 2× step in loop gain, i.e. the
relay element the v0.61 tanh work removed from `aoaRecover`.

Occupancy (`loopaudit.py`, `pEff < 0.15` **and** `|fbwTgtPR| < 0.05`), v0.71/v0.75 runs:

| segment | %latched | plant's TRUE \|ach/cmd\| during the latch |
|---|---:|---:|
| `arm` | 6.62% | 1.09 |
| `az30` | 5.97% | 1.12 |
| `turn360` | 1.64% | 1.31 |
| `az90` | 0.42% | 0.67 |
| `az150` | 0.28% | 0.16 |

0.21% of all v0.7x ticks; three episodes ≥ 1 s, longest 2.68 s. Rare entry, **structurally
unclearable exit**. And the flown corpus is one healthy jet at high q — the two regimes that produce
lagging/opposing pitch response constantly (loaded jet near its alpha ceiling, low-q STOL trainer)
have never been flown.

**The fix is one character wide** and needs no new constant: separate the probe target from the
threshold. Either test `>` on a probe that lands *above* the threshold, or probe to a level strictly
above `PEffRevThresh` (the natural choice is `effFloor`, which is what pre-C1 behaviour gave for
free and what the v0.67 comment says it is restoring). Do **not** widen the 0.05 gate — that is the
symptom, not the cause. Recommend keeping it behind its own checkbox so the A/B is attributable.

---

## F2 — `lateralHold` rails and disconnects the entire bank pipeline

**STRUCTURAL + MEASURED (n = 10 R21 + 11 fixedwing-v2).**

```csharp
float azAl = Mathf.Max(0f, Mathf.Abs(azErr) - Cfg.FineBankDeadzone.Value);
float lateralHold = Mathf.Clamp01(azAl / Mathf.Max(0.01f, Cfg.EvolvedAlignHoldDeg.Value));
float blendWeight = Mathf.Max(bigTurn, lateralHold) * (1f - _heliBlend);
...
float rollErr = Mathf.Lerp(eFine, eAlign, blendWeight);   // eFine is the ONLY carrier of tBankE
```

`lateralHold` rails at `FineBankDeadzone + EvolvedAlignHoldDeg` = **7.5°** at stock. At the rail,
`blendWeight = 1`, so `eFine`'s weight — and with it the weight of the **whole** bank pipeline
(`azErrPred` → `azTR` → `omegaDes` → `omegaMax` cap → `bankTR` → `MaxBankAngle` → `tBankE`) — is
**exactly zero**.

**That is a latch, closed in one step.** A standing azimuth lag above 7.5° disconnects the machinery
whose job is to reduce the lag; the escape condition (|azErr| < 7.5°) requires the disconnected
machinery. R21's measured lag is 9.3–10.0°.

| window | n | `blendWeight` | eFine weight | `blendWeight` = 1 |
|---|---:|---:|---:|---:|
| R21 `turn360`, whole | 4802 | 0.980 | 0.020 | 97.0% |
| R21 `turn360`, **settled (tSeg ≥ 20 s)** | 1601 | **1.0000** | **0.0000** | **100.0%** |
| fixedwing-v2 `turn360` | 5125 | 0.983 | 0.017 | 97.6% |
| `astern` | 2640 | 0.894 | 0.106 | 83.6% |
| `reversal` | 2640 | 0.675 | 0.325 | 63.4% |
| `az150` | 2640 | 0.645 | 0.355 | 60.9% |

Not a `turn360` curiosity: it covers **every large-error segment in the corpus**, i.e. exactly the
segments the bank pipeline exists for. In `micro`/`fine`/`arm` — where the pipeline *is* connected —
`tBankE` is near zero and there is nothing to carry.

### What this costs the last two releases

`tBankE`'s only other live consumer is `coordPull`, which takes it at full weight. So in the settled
turn the entire demand chain reaches the plant through one scalar. Measured on R21 (`loopaudit.py
--settled 20`, counterfactual = the same tick with `_aimAzRateFilt` removed from `omega`):

| | value |
|---|---:|
| turn demand `omegaDes` | 14.63 °/s |
| of which the **v0.78 marker-rate feed-forward** | 12.06 °/s = **82.5%** |
| `bankTR` (before the clamp) | 81.6° |
| `tBankE` **with** the feed-forward | 72.0° (clamped) |
| `tBankE` **without** it | 49.8° |
| feed-forward delivered to **roll** | **0.0000** (weight is zero) |
| feed-forward delivered to **pitch**, via `coordPull` | **0.0425** stick |
| … against mean \|`outP`\| | 0.613 → **6.9%** |

**82.5% of the turn demand arrives as 6.9% of one axis**, and that surviving path is `coordPull`,
which is multiplied by `assist ∝ _yawWeak` (F3). Set `YawAssistEnabled = false` — a supported
config — and `coordPull` is identically zero, at which point the v0.78 feed-forward, the v0.83
relative lead, `predFloor`, `omegaMax` and `MaxBankAngle` have **literally no effect on any control
output** in a sustained turn. `loopaudit.py --selftest` asserts both halves.

This also explains R21's headline (the bank clamp is a bystander) and GATE-CHATTER §5a's +0.918
without needing a second mechanism.

**Do not "just cap `lateralHold`".** R21 already proposed that as a flight test; it is the right
experiment, but the principled statement is that `blendWeight` is a **hand-off**, not a gate: the
two roll channels are alternatives, and handing 100% to `eAlign` because the error is large discards
the only channel that carries speed-aware achievability. Whatever replaces it must key on which
channel can close the error, not on how big the error is.

---

## F3 — `_yawWeak` measures "the error did not close", not "the rudder is weak"

**STRUCTURAL + MEASURED. Same shape as the v0.83 `leadRate` defect: correct in the regime it was
validated in, self-referential outside it.**

```csharp
closeRaw = (|_prevAzErr| - |azErr|) / dt;                      // filtered, tau 0.2
weakInst = |azErr| > 1.5 ? 1f - Clamp01(_closeRateFilt / 6f) : 0f;
_yawWeak = asym-LPF(weakInst);                                  // attack 0.8 s, release 3.2 s
float assist = _yawWeak * (1f - bigTurn) * Cfg.YawAssistStrength.Value;
```

### (a) The regime error (the v0.83 shape)

The estimator's premise — a heading error that will not close means the rudder is failing — holds
against a **stationary** marker. Against a **sweeping** one, a standing error is what a
proportional loop must hold, and says nothing about the rudder.

Closed form, on R21's settled window: the azimuth error closes at 0.033 °/s, so
`weakInst = 1 − 0.033/6 = 0.9945`. **Recorded `yawWeak` max: 0.996.** R21 also established that on
those same ticks the game's FBW delivers **99.4%** of the commanded pitch rate, the aircraft holds
63% of its structural turn rate, uses 60% of its g and 27% of its AoA limit, and **no axis is
within 20% of a rail**. The estimator reports "the rudder is 99.5% ineffective" about an airframe
with authority to spare.

Corpus-wide, `_yawWeak` is highest in exactly the two segments already known to be *law* defects
with full authority available, and low in the large-error segments that close normally:

| segment | `yawWeak` | %>0.9 | what the corpus already knows about it |
|---|---:|---:|---|
| `turn360` | **0.950** | 84.9% | R21: law saturated, plant idle |
| `elDn` | **0.743** | 38.9% | GATE-CHATTER §5a: 6.92° standing error, roll limit cycle |
| `az150` / `az90` / `az30` | 0.17–0.21 | 0% | large errors that close |
| `micro` / `fine` | 0.000–0.005 | 0% | converged |

### (b) The feedback path

`_yawWeak`'s two live consumers both move the thing it measures:

- `yawWeakFade = 1 − YawWeakFade·assist` **attenuates the yaw command**. Measured yaw command
  surviving: `turn360` **43.3%** (settled), `elDn` 62.6%. So the estimator concludes the rudder is
  not closing the heading, and in response removes over half the rudder. Reinforcing.
- `coordPull *= assist`. Per `Cfg.cs`'s own description, `CoordPullGain` is *"the REAL driver of a
  high-speed correction… a level turn needs back-pressure or gravity just drops the nose and the
  bank does nothing."* That is universal physics, and it is gated on rudder health. On an airframe
  or speed where the rudder *does* close heading (`_yawWeak → 0`), the identical commanded bank gets
  **zero** coordinating pull. Under the one-law rule that is a reportable defect on its own: the
  presence of a coordination term must not depend on a per-airframe, per-speed estimator of an
  unrelated axis.

### (c) The normalisation is the per-airframe constant the codebase already forbids

`Clamp01(_closeRateFilt / 6f)` — an **absolute** closure rate in deg/s. The v0.83 `_stallFilt`
comment states the rule explicitly and follows it:

> *"A RATIO on purpose: an absolute deg/s 'is it closing' threshold would be a per-airframe constant
> in disguise (what counts as slow closure on a 19 deg/s fighter is fast on a trainer), which the
> one-law rule forbids."*

`_yawWeak` is that exact form, and predates the rule by 48 versions. `6 °/s` is 31% of the KR-67's
19.5 °/s achievable rate at R21 conditions; the mod already computes the correct denominator
(`omegaMax`, probed + live) two blocks away.

**Not correlational.** The table above is illustration; the finding is that the *definition* cannot
separate "rudder ineffective" from "the law is not closing this error for any other reason", and
that its two consumers both act on the quantity it measures. No sham control applies to a
closed-form claim about a definition.

---

## F4 — v0.85 `AlignRateLead` makes the roll **derivative** gain a function of `blendWeight`

**STRUCTURAL, unflown. Flag this before the v0.85 flight test, not after.**

```csharp
_phiLead = ... ? _phiRateFilt * Cfg.RollDamping.Value : 0f;
float eAlignTgt = Mathf.Clamp((phi + _phiLead) / 90f, -1.5f, 1.5f);
...
tgtR = (Mathf.Lerp(eFine, eAlign, blendWeight) - rollRateF * Cfg.RollDamping.Value) * RollGain;
```

The lead is the *right* term — `phi` is the align channel's own error, so its total rate is the true
derivative and the v0.83 lesson does **not** apply (this is the one v0.85 change that clears the
frame/regime test cleanly). The side effect is the problem. Against a stationary marker
`d(phi)/dt = −rollRate`, with `phi` in **degrees** and `rollRate` in rad/s, so the lead contributes
an extra `rollRate · RollDamping · (180/π)/90` of rate feedback, **weighted by `blendWeight`**, on
top of the servo's own term:

> **effective roll-rate feedback = `RollDamping · (1 + 0.6366 · blendWeight)`**

1.00× at `blendWeight = 0`, **1.64× at `blendWeight = 1`** — and `blendWeight` is the signal
GATE-CHATTER §5a measured at **+0.918** with the `azErr` the roll loop is itself producing. Measured
mean multiplier by segment: `turn360` **1.63**, `astern` 1.57, `reversal` 1.43, `elDn` 1.39,
`micro`/`fine` 1.00.

So in `elDn` — the ±43°, ~0.3 Hz roll limit cycle — the roll damping gain is now **modulated at the
frequency of the cycle it is supposed to damp**. The sign is always stabilising, so this cannot
diverge, but it makes the roll loop time-varying and it silently breaks the change's own stated
premise ("the lead TIME reuses `Cfg.RollDamping` — the roll channel's already-tuned derivative
time"): the *time* is reused, the effective *gain* is not preserved.

Cheap principled fix when the time comes: divide the lead by the same `1/90` the map applies, or
subtract the double-count, so the total stays `RollDamping` regardless of the blend. **Not urgent —
but the v0.85 flight test should be read knowing that `AlignRateLead` is a 64% roll-damping change
in the high-`blendWeight` regime, not only a lead.**

---

## F5 — `belowSuppress`'s residual `bigTurn` loop

**STRUCTURAL, inert at the measured operating point.**

v0.85 deleted the `(1 − lateralHold)` factor and moved to the roll-invariant `alignFracH`. What
remains is `Clamp01((1f - bigTurn) / downAlignTaper)`, and `bigTurn` is a function of `off`, which
**contains** the azimuth error roll-to-align creates. Same loop, one signal further out.

Closed form: the taper saturates at 1 (inert, derivative exactly 0) until
`bigTurn > 1 − 0.3`, i.e. `off > FineAngle + 0.7·(AlignAngle − FineAngle)` = **19.3°** at stock.

- `elDn`'s measured hang sits at `off` = 6.92° → **inert**. v0.85 is safe there.
- The 20° step's own entry runs at `off` ≈ 20° → **live**, and in the reinforcing direction (more
  `off` → less suppression → more roll-to-align → more `azErr` → more `off`).

So the loop survives only in the transient v0.85 was not measured on. Low severity, but it is the
reason not to call F13 "closed": record it and re-check on the v0.85 `elDn` capture.

---

## F6 — `_pitchEff` and `_alphaSchedFilt` are two de-raters of one event, multiplied

**STRUCTURAL; the regime has never been flown.**

```csharp
pErrTerm *= Max(effFloor /*0.30*/, _pitchEff);                 // in ApplyEvolvedLegacy
tgtP = ((pErrTerm - coordPull) * qSched + ...);                 // qSched = Min(qRatio, _alphaSchedFilt)
```

Both estimators respond to the same physical event — a mushing wing — from two directions
(`_pitchEff` from achieved-vs-commanded rate, `_alphaSchedFilt` from AoA against the probed
ceiling), each documented as flooring at 0.30. They **multiply**: at both floors the pitch P term is
scaled by **0.09**, i.e. 91% removed where each mechanism was designed to remove 70%. Inside the
fine cone a third scaling (`fineGain`'s boost is also gated by `_alphaSchedFilt`) stacks on top.

Corpus check *as audited* (v0.6x/v0.7x): min `pEff` is 0.0, but `aoaLimiterActivePct` read 0 in every
capture then available, so the two had never been low at the same time in any recorded flight. This
is a compounding-gates finding, not a loop.

> **CORRECTED 2026-07-31 — "unfalsifiable on the present corpus" is RETIRED.** The AoA half of the
> pair is measurable today: 66 cells non-zero, **23 fully unrailed**, headed by **R33
> `Darkreach·obDR6` at 100.0%, `railed = 0`, n = 4** (`aoaPeakDeg` 7.38–7.59 vs a 10° limiter). That
> is a cell where `_alphaSchedFilt` is demonstrably active *and* the actuator is off its stop
> (`authorityUsedFrac` 0.717–0.748), which is precisely the condition this finding needs. The
> remaining unknown is whether `_pitchEff` is simultaneously at its floor there — a `rows`-level
> question, so `index-captures.py --with-rows R33` first. **F6 is now testable, not theoretical.**

---

## F7 — v0.83 `_stallFilt` opens **body-frame** integrators in the large-bank regime

**HYPOTHESIS.**

`_iPitch` / `_iYaw` wind on `-local.y` / `local.x` — body-frame components — and are **rate biases in
body axes with no roll compensation**. Through v0.82 the only wind gate was `fineBlend`, so they
only ever accumulated inside the 6° fine cone, where bank excursions are small and the body frame is
quasi-stationary. v0.83's `iGate = Max(fineBlend, _stallFilt)` opens them in the large-error regime —
which is precisely where the law commands 43–80° of bank (R21: 80.1° sustained; GATE-CHATTER: ±43°
at 0.3 Hz in `elDn`). A bias wound at bank φ and carried to bank φ′ contributes `iPitch·sin(Δφ)` on
the wrong axis.

The gate itself audits **clean** on the loop question: `closeFrac` is a dimensionless ratio, roll-blind
by construction (`noseTurnDeg` differences the forward axis), and the 4 s attack / 0.2 s release
asymmetry is genuine anti-windup — a 50/50 duty cycle converges to ≈0.05, not to 1. Two minor notes:
the denominator floor (`1e-3 °/s`) is three decades below the mod's own stated frame-noise floor
(~0.8 °/s, per the `aimStillRate` comment), so with a near-stationary nose `closeFrac` is a noise
amplifier — absorbed by the 4 s attack, but it is luck rather than design; and `yawCapped` suppresses
the new path for `_iPitch` too, which is the correct choice and is load-bearing for the STOL case.

**What would settle it:** an `elDn` or `turn360` capture on v0.83+ with `IntegralStallGate` on vs
off, reading the new `iGate` column against `bank`. Pass = `iPitch` non-zero and `terminalOffDeg`
down by ≥ the 0.11° MDE; failure signature = `outR` sign-flip rate rising with `|bank|`, or the
integrator holding a bias through a roll reversal.

---

## Cleared, and on what basis

Not empty cells — each of these was checked against the same question and has a reason.

| term | frame | why it clears |
|---|---|---|
| `pullGate` (`alignFrac`) | **body** | body-frame is **correct here**. A pull rotates the nose about body-right, so "will pulling move me toward the target" is a body-frame question by construction. Contrast `belowSuppress`, which gated *roll* on a *horizon-frame* geometric fact and so had to be roll-invariant. **The refined rule: express a gate in the frame in which the GATED COMMAND'S PHYSICAL EFFECT is defined — not the frame the signal is cheapest to compute in.** |
| `phi`, `eAlign` | body | `phi` is the align channel's *controlled variable*, not a gate on it. Roll moving `phi` is the loop working. |
| `_phiRateFilt` as a lead | body | the true derivative of that same controlled variable, including the marker's own motion — the v0.83 lesson applied correctly. (Its effect on the D *gain* is F4.) |
| `_aimAzRateFilt`, `_aimRateFilt`, `_settleOK` | world | derived from the **marker alone**. Structurally unmovable by any mod output — the cleanest signals in the file, and the model for what a gate should look like. |
| `hdgConf` | world | an exact deprojection (`cos(pitch)`), not a threshold; multiplicative and applied after the clamp so the bounds scale coherently. |
| `qSched` (q half) | physical | airspeed. The command bleeds airspeed on a ~10 s timescale (R21: −15.4 m/s / 30 s), three decades below the control bandwidth. |
| `omegaMax`, `aoaGateUp/Dn`, `aoaRecover` | body/probed | genuine **negative** feedback, with the v0.57 predictive-lead asymmetry as documented hysteresis. **Cleared on structure, not on data** — but "never exercised in the corpus (`aoaLimiterActivePct` = 0 everywhere)" is **corrected 2026-07-31**: it is non-zero on 66 cells, 23 fully unrailed, up to **100.0% on R33 `Darkreach·obDR6`** (n=4, `railed = 0`). The gates *have* been exercised; nobody has yet scored what they did there. |
| `_pitchEff` under plant **saturation** | ratio | scale-invariant: `ach/cmd` is unchanged by scaling `cmd`, so the self-reference has loop gain ≈0 for a linear plant. Under rate saturation it solves to `_pitchEff = √(ω_plant/(K·e))` — a unique, stable, bounded fixed point, i.e. a convergent soft de-rater. **This half of `_pitchEff` is well designed; F1 is the gate around it, not the ratio.** |
| `_rollRateFilt` | body | proper derivative term on the roll axis. |
| `fineBlend`, `bigTurn`, `brakeGate`, `azRamp`, `pullTaper` | invariant | all functions of error magnitude gating commands that reduce error magnitude — ordinary negative feedback. GATE-CHATTER already tested these for chatter against sham gates and returned a clean negative; nothing here changes that verdict. |
| `predFloor` | ratio | already implicated twice (GATE-CHATTER §5c, R21) and unchanged by this audit. Its status is "known, ranked, not a loop". |

**Also noted, not a loop: dead code.** `targetBank` and its whole chain (`azBank`, `azDz`,
`bankGain`, `linBank`, `bankBlend`) is **passed to `ApplyEvolvedLegacy` and never read** — the law
computes `tBankE` locally. It reaches only the recorder column `targetBank`, the `[chase]` trace,
and the `over-roll` anomaly detector. That last one matters: a mis-reading `_yawWeak` (F3) also
corrupts a diagnostic, and any future reader who tunes `FineBankGain`/`BankAuthGain` expecting a
flight effect will get none.

---

## The one change to make first

**F1**, once the v0.82–v0.85 batch has been flown. It is one comparison, needs no new constant, has
a closed-form proof and a measured 3.07 s episode, and — unlike F2 and F3 — it cannot make anything
else worse, because the branch it repairs is currently unreachable. Ship it behind its own checkbox
(`Cfg.PitchEffProbeFix` or similar) so the A/B is attributable, per the v0.83/v0.85 pattern.

F2 is the larger effect and the right *second* change, but it is a hand-off redesign, not a fix, and
it must not be bundled with anything.

## The capture that would settle the best HYPOTHESIS-grade finding

F7 (and F6, and the un-flown half of F1) all need the **same** capture, which the corpus has never
had: **a low-q / loaded-jet run where the AoA machinery actually engages.** Concretely — a heavy
loadout at 160–180 m/s, `fixedwing-v2`, 4 replicates per arm, ABBA-interleaved per v0.84.

Pass/fail signals, in priority order:

1. ~~`aoaLimiterActivePct` > 0 **at all** — until one capture achieves that, three de-rating
   subsystems (`aoaGateUp/Dn`, `aoaRecover`, `_alphaSchedFilt`) have never been observed working.~~
   **ACHIEVED 2026-07-31, and the requested capture is not the one that did it.** R33
   `Darkreach·oblique-6-c`: `obDR6` **100.0%**, `obDL6` 76.8%, `obUL6` 46.5%, `obUR6` 25.4% — n = 4
   each, **`railed = 0`**, `authorityUsedFrac` 0.476–0.748. Not a heavy loadout at 160–180 m/s but a
   **95 m/s** entry (v0.96's #41 corner-speed fix, down from 171 m/s in R29) on a 105 t airframe with
   a 10° `alphaLimiter` — i.e. low q was the lever, not load. Signals 2–4 below are still open and
   this is the capture to run them against.
2. `pEff` and `qSched` **simultaneously** at their floors → the 0.09 product of F6 is real; if they
   never coincide, F6 downgrades to theoretical.
3. `pEffLatchPct` from `loopaudit.py` — the prediction is that it rises sharply from the 0.2%
   measured on a healthy jet, because the entry condition (achieved rate lagging or opposing
   commanded) is what a mushing plant produces continuously.
4. `iGate` vs `bank` with `IntegralStallGate` on/off → F7.

Second priority, and much cheaper: **`elDn` on v0.85**, `BelowAlignSuppress` and `AlignRateLead`
toggled **separately** (they are separate knobs on purpose). Read `bWt` and `phiLead` directly — F4
predicts `phiLead` is a non-trivial fraction of `rollRateF·RollDamping` whenever `bWt` is high, and
F5 predicts `bSup` is pinned at its `bigTurn` rail (taper = 1) for the whole hang and only moves
during the entry.
