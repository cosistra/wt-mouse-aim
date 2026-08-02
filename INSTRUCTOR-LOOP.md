# The instructor loop — how we measure "flies well" and climb it

Standing design doc for the automated flight-quality effort. Companion to
[`GENERALITY-REVIEW.md`](GENERALITY-REVIEW.md) (the ONE-LAW audit) and
[`WOBBLE-FINDINGS.md`](WOBBLE-FINDINGS.md) (the v0.51 oscillation investigation).

**The goal, in the maintainer's words:** War Thunder's aim feels smooth and like the airplane is at
your will. Ours feels confused — cross-fighting signals in small movements, unsure whether to roll
or yaw. We want a system that continuously and rigorously tests itself and learns, with no human
flying cards by hand.

That splits into four problems, and they have to be solved in this order:

1. **Measure** — a number that says how well a maneuver was flown. Without it "smoother" is an
   opinion and no change is falsifiable.
2. **Bound** — what was *achievable*, so we can tell a law defect from an airframe limit.
3. **Collect** — fly the demand space unattended, so the data is a sweep and not "whatever someone
   felt like flying."
4. **Climb** — accept a change only when it beats the noise floor, and use disagreement across
   airframes to find constants that should have been probes.

---

## 1. The measurement: pointing efficiency

The question "was there a better way to get the nose pointed where it was asked?" has an exact
answer, because the airframe's reorientation ceiling is computable from parameters we already probe.

**The achievable rate**, per tick, from probed params + live state only — no hand-tuned constants,
so it is ONE-LAW clean and comparable across a light jet, a loaded jet, a trainer and a helo:

```
q_ratio     = (rho/RHO0) * (V/cornerSpeed)^2          # cornerSpeed is a SEA-LEVEL number
n_avail     = max(1.05, aircraftGLimit * min(1, q_ratio))
omega_turn  = deg(9.81 * sqrt(n_avail^2 - 1) / V)     # steady turn rate
omega_avail = min(omega_turn, omega_pitch_cap)
```

Two corrections found while implementing this, both worth keeping in mind:

- **The density term is not optional.** Omitting it says n=9.0 at 180 m/s at 4 km when the truth is
  6.4 — 40% high, which silently misclassifies slow-at-altitude ticks as *airframe-limited* and hides
  real law defects. With `rho` included, the design-point shortcut agrees with a full
  `n = q·S·Cl(alphaLimiter)/(m·g)` computation off the sidecar's Cl curve **to within 0.5%** — the
  game defines `cornerSpeed` consistently with its own aero, so the shortcut is exact, not a fit.
- **`maxPitchAngularVel` is the assist-OFF cap.** With assist on — `assist=1` in 100% of 50,062 rows
  on file — the game uses `gLimit*9.81/max(V, 0.75*fbwCornerSpeed)` (note `fbwCornerSpeed`, not
  `cornerSpeed`). At R21's sustained point that branch independently yields **19.26 °/s against
  `omega_turn`'s 19.14** — a useful cross-check that the turn-rate formula is right. The flat 43 °/s
  cap never binds on any capture on file.

**The demand**: `omega_target = min(omega_avail, off / tau_feel)`, where `tau_feel = 0.25 s` is the
first-order time constant that reads as "instant" under a mouse. That is deliberately the *one*
human-anchored number in the whole metric — everything else is physics. It is a CLI flag so it can
be recalibrated rather than argued about.

**The score**: `e = edot / omega_target`, where `edot = -d(off)/dt`.

> `e >= 1` — closing at least as fast as a 0.25 s response, or at the airframe's limit. Acceptable.
> `e < 0` — **the nose is moving away from the mouse.** This bin is the "confused" feeling, counted.

### The classification that makes it useful

Scoring every tick equally would be wrong: an aircraft already at its limit cannot do better, and
punishing it would make every A/B read "no difference." So each tick is one of:

| class | test | meaning |
|---|---|---|
| `ON_TARGET` | `off <= 1°` | not scored |
| `AIRFRAME_LIMITED` | `turn_rate >= 0.85 * omega_avail` | **there was no better way** — law work here is wasted |
| `SCORED` | otherwise | headroom existed; low `e` is a real defect |

`turn_rate` is the rotation rate of the *velocity vector* (from the recorded `velX/velY/velZ`), which
is the quantity `omega_avail` bounds.

This split is the whole value of the metric. It converts "the mod feels bad here" into either *"the
plane can't"* (stop) or *"the law won't"* (fix, and here is how much was left on the table).

### The smoothness axis

Agility alone would reward a twitchy bang-bang controller. So a second, model-free axis:

- **reversal rate** — sign flips per axis per second, deadbanded so noise around zero doesn't count
- **jerk RMS** — `rms(d(out)/dt)` per axis
- **churn ratio** — control activity during `REGRESSING`+`STALLED` ticks over activity during
  `WORKING`+`NEAR_OPTIMAL`. Working hard and getting nowhere. This is the direct numeric form of
  "confused," and it needs no plant model.

A change ships when it improves `A` without paying for it in `S`.

**Implementation:** `debugtests/flightscore.py`, stdlib-only, `--json` for the loop to consume,
`--selftest` for the physics anchors. It reads any recorder CSV — **no segment tags required** —
which is what lets it score unattended flight and not just scripted cards.

### The lever axis — did the fix FIRE, separately from did it HELP

`A` and `S` say how well the nose was flown. They cannot say *why*, and specifically they cannot
tell "the fix worked" from "the fix never ran" — both make an error smaller. That is the whole
reason the mod records `iGate`/`leadDeg` (v0.83) and `bSup`/`bWt`/`phiLead` (v0.85) on **both**
sides of their config toggles. `flightscore` prints a per-segment lever table under the segment
scores whenever a capture carries any of them (`--levers` forces it on older captures, for the
cross-fighting pair, which needs no new column). A capture with none of them prints **nothing**
new and scores byte-identically to before the columns existed; an absent column reads `-`, which
means NOT MEASURED and is never rendered as `0.0`.

| column | reads | fired when |
|---|---|---|
| `iGate~` / `iStal%` | the wind gate the fine integrator used; % of time it was open **outside the fine cone** | `iStal%` is 0.0 **by construction** with `IntegralStallGate` off (`iGate == fineBlend == 0` there), so any value > 0 is the v0.83 gate firing |
| `lead` / `floor%` | median `\|leadDeg\|/\|azErr\|`; % of time `predFloor` bound | R21 baseline **0.84 / 100%**. ~~`RelativeTurnLead` working in a matched turn drives `lead` toward 0~~ — knob **deleted v0.99.1**; the lead term is now unconditionally relative, so this row has no arm behind it |
| `bSup~` / `r(bSup)` | below-nose suppression applied; its correlation with `\|azErr\|` | `r(bSup)` is the **disarm signature**: the deleted `(1 − lateralHold)` factor made the suppressor shrink as the error it creates grew. Clearly negative ⇒ that factor is back |
| `bWt~` / `r(bWt)` / `sham` | the roll blend weight after suppression — the loop gain — its correlation with `\|azErr\|`, and the **definitional twin's** correlation | see below |
| `phiL%` | % of time the `AlignRateLead` bearing lead was non-zero | 0 with the lever off or inside the `phiWrapGate` stand-down |
| `xf%` / `xfSus%` / `xfWt` | cross-fighting — see below | — |

**The v0.85 loop-gain check** is the headline, printed as one verdict line: pre-fix `elDn` measured
`corr(bWt, |azErr|) = +0.918`, and the fix's one falsifiable prediction is that it collapses. FAIL
needs three things at once — a **below-nose** segment (`bSup~ ≥ 0.01`; in the upper hemisphere
`bWt == lateralHold` by design and is *supposed* to track the error), a **live** channel
(`bWt~ ≥ 0.20`; a big correlation on a switched-off channel is not a loop gain), and `r ≥ +0.50`
with a **gap under +0.20** against `sham`. `sham` is `lateralHold` itself, the bare algebraic
function of `|azErr|` that `bWt` is built from: `bWt` correlates with `|azErr|` *by definition*, so
`r` at or above `sham` is definitional and is **not** evidence of feedback. Only the gap is. This
is the §5 sham-gate discipline applied to the number that replaced the hypothesis §5 killed.

**Cross-fighting** — the maintainer's actual complaint, "whether it should roll, whether it should
yaw" — is `|outR|` and `|outY|` both outside the stick deadband with **opposite signs** (the same
definition `gatechatter.py` uses, so the two tools cannot disagree about what a fight is).
`xf%` is raw occupancy; `xfSus%` counts only disagreements lasting ≥ 0.30 s, and **that is the
control**: a P-loop flips one axis a tick before the other at every zero crossing, so a nonzero
`xf%` is expected by construction and only a persistent one is an allocation fight. Baseline over
the 18 `fixedwing-v2` captures at `--cone 0.2` (medians):

| seg | `elDn` | `az10` | `az30` | `az90` | `az150` | `reversal` | `elUp` / `micro*` / `fine` / `turn360` |
|---|---:|---:|---:|---:|---:|---:|---:|
| `xf%` | **46.5** | 20.9 | 12.0 | 10.8 | 6.5 | 0.6 | 0.0 |
| `xfSus%` | **46.5** | 20.8 | 11.6 | 9.6 | 6.5 | 0.0 | 0.0 |

`elDn` is fully sustained — a fight, not crossings — and independently reproduces
`GATE-CHATTER-FINDINGS.md` §3 (42.6% there) on a different tick population. `reversal` is the
opposite: occupancy that the sustain control removes entirely. Note again that the complaint's
*small* movements (`micro*`, `fine`) score **0.0**; cross-fighting lives in the large oblique and
below-nose reorientations.

`xfWt` = mean `bWt` while fighting minus mean `bWt` while not — is the roll channel *claiming* the
azimuth error while opposing yaw? It ships without a sham because its confound runs the other way:
disagreements cluster at crossings, where `|azErr|` and therefore `bWt` are small, so common cause
pushes it **negative**. A positive value is the direction the confound cannot produce; a negative
one is just crossings.

### The metric's own acceptance test

It must reproduce a defect we already understand. In the R21 sweep captures the marker leads the nose
by a standing ~9.4° that never closes, because commanded bank hits the hard `MaxBankAngle = 72`
clamp while the airframe still had headroom (`aoa` 7.3° against a 27° limiter; `g` 5.73 against a
9 G limit). A correct metric shows those ticks **`SCORED` + `STALLED`**, never `AIRFRAME_LIMITED`.
If it says airframe-limited, the metric is wrong — not the recording.

---

## 2. What "acceptable" means

Three reference levels, in increasing order of honesty:

- **Absolute** — `e = 1` is the feel bar; `e` at the envelope is the physical bar. A segment whose
  scored ticks average `A >= 0.7` with no `REGRESSING` mass is flying well.
- **Noise floor** — no A/B claim is admissible below the minimum detectable effect,
  ≈ `2.8 * sd / sqrt(n)` for a two-arm comparison. This is why replicates are staggered: identical
  segments flown at the same wall-clock instant share a frame hitch and fake a *tighter* floor.
- **Ratchet** — the champion run per (airframe × card) cell. A change must beat it by more than the
  floor to land. Cheap to maintain, and it prevents slow regression.

The absolute bar is a hypothesis until calibrated. First real calibration job: score a capture the
maintainer judges as feeling good, and see what `A` and `S` it actually gets.

---

## 3. What cards to run

With drones flying unattended the card set stops being "what a human can be bothered to fly" and
becomes a **sweep of the demand space**. The axes:

| axis | levels |
|---|---|
| step size | micro (<2°), small (2–10°), medium (10–45°), large (45–135°), reversal (>135°) |
| demand rate | step (0), slow sweep, at-capability sweep, beyond-capability sweep |
| demand axis | pure pitch, pure azimuth, **oblique** |
| entry state | from level, from an established turn, from a roll |
| speed band | below corner, at corner, well above corner |
| airframe | light jet at high q, loaded jet near its alpha ceiling, STOL trainer, hovering helo |

**Prediction worth testing first: "confused" lives in the oblique small step.** A pure-vertical or
pure-horizontal demand has an unambiguous axis allocation — roll or pull, obviously. An oblique 5°
step is exactly where roll-vs-yaw allocation is ambiguous, which is what the maintainer described.
The current card set under-samples it.

### What the current card set gets wrong — measured, not guessed

Scoring the existing cards exposed four defects in the *cards themselves*, independent of the law:

- **`micro1..10` and `fine` are entirely sub-degree**, so at the default 1° cone they read 100%
  `ON_TARGET` and score nothing at all. The fine-aim case — the maintainer's actual complaint — was
  invisible to measurement by construction. They only score under `--cone 0.2`, where they turn in
  0.58–0.92 with **11–29% REGRESSING** and 1.1–1.7 command reversals/sec (against 0.1 in a sustained
  turn). Either size the segment to the cone or state the cone as part of the card.
- **`turn360` has zero `WORKING` ticks**, so the churn ratio — and therefore `S` — is *undefined* on
  the whole R21 corpus. A segment that only ever stalls cannot exercise the smoothness axis. Cards
  need segments that **mix closing and stalling** for `S` to mean anything.
- **`arm` scores nothing** — a 6 s wings-level hold with no demand. Pure overhead per replicate.
- **`reversal` / `astern` / `az150` are 48–55% `AIRFRAME_LIMITED`.** They largely measure the
  airframe, not the law. Fine as capability references, near-useless as A/B discriminators — a law
  change cannot move a tick that was already at the envelope.

Ranked by law headroom, the segments worth tuning against today are `turn360` (0.493, 95% stalled,
0% airframe-limited), `az10` (0.580), and `elDn` (0.621 — and **24% REGRESSING with ~3× the jerk of
any other segment**, the clearest cross-fighting case in 162 captures).

**Two coverage holes the ONE-LAW rule already forbids** and no capture has ever closed:

- ~~`aoaLimiterActivePct` is **0 in every segment of every card ever run**. The "loaded jet mushing
  near its alpha limit above corner speed" case the rule explicitly demands is untested.~~
  **CORRECTED 2026-07-31 — FALSE, and this line is the origin `scorecard.py` cited.** The metric is
  non-zero on **66** (run, airframe, tag) cells across R1–R33, **23** of them with no railed segment
  anywhere, topped by **R33 `Darkreach·obDR6` at 100.0%** (n = 4, `railed = 0`, `aoaPeakDeg`
  7.38–7.59° vs a 10° limiter). The *loaded* jet case is still untested — a card cannot set a loadout
  — but "the α machinery has never fired" is not true. `LAW-CHARACTERIZATION.md` §1.
- ~~Only two airframes have ever been flown (Ifrit, Multirole1).~~ **Stale: 10 airframes have flown a
  card** (every fixed-wing key in `AIRFRAMES.md`), across 1 681 captures. 3 of the rule's 4 cases are
  covered; the hovering helo is not.

---

## 4. How it learns

The loop, once the drone harness carries the cards:

```
drones fly the grid  ->  flightscore per cell  ->  worst cells localize the defect
      ^                                                      |
      |                                                      v
   ratchet: accept only if delta-A > noise floor  <-  propose a law change, A/B with N replicates
```

Everything except *proposing the change* automates. That is the correct division: the harness
adjudicates, the human (or an agent) hypothesizes.

**The part that makes this more than a tuner.** The ONE-LAW rule bans hand-tuned per-plane
constants, and a config sweep looks like exactly that — so point it at a different question. Run the
sweep across all four airframes and look for **knobs whose optimum disagrees between airframes**.
Every such knob is a ONE-LAW violation hiding in plain sight: a constant that is secretly standing in
for a physical quantity we should be probing.

**Worked example (and a caution about acting on arithmetic alone).** `MaxBankAngle = 72` *looked*
like the textbook case: the clamp fires on 97% of samples in the R21 sweep and discards ~10° of bank
demand, and `phi_max = acos(1/n_avail)` is its obvious principled replacement. The forensics then
showed the clamp is **very nearly inert**. `tBankE` reaches the roll servo only through `eFine`, and
`rollErr = Lerp(eFine, eAlign, blendWeight)` with `blendWeight = max(bigTurn, lateralHold)` — where
`lateralHold = clamp01((|azErr| − 2.5)/5)` **rails to exactly 1.0** at the observed `|azErr| ≈ 10°`.
So `eAlign` outweighs the clamped term **34:1**; raising the limit to 85 is predicted to move flown
bank by ~0.1°. The clamp is real, the discarded demand is real, and it changes almost nothing.

Two constants found in the same pass matter far more, and both are genuine instances of the pattern:

- **`_iPitch` is gated by `fineBlend`** (`off < FineAngle = 6°`), so at the observed ~10.2° standing
  error the integrator is *identically zero* (±0.001 against a 0.12 cap). The term whose entire
  purpose is killing steady-state residual is switched off exactly when a steady-state residual
  exists. It gates on error **magnitude**; it should gate on error **persistence**.
- **`predFloor = 0.30`**, a hard `const`, binds on **100%** of the settled window. The
  `TurnLeadTime = 0.65` lead would otherwise cancel 84% of raw `azErr`; the floor catches it — but
  the net effect is an **effective P gain of 0.28 against a configured 0.92**. A hand-picked constant
  silently setting the loop gain is precisely what the ONE-LAW rule exists to forbid.

The lesson for the loop: arithmetic that shows a clamp is *active* does not show it is *load-bearing*.
Trace the signal to the actuator before spending a change on it.

So the sweep's output is not "the best gains." It is **a list of constants to delete and replace
with probes** — which is the project's actual goal.

---

## 5. Where the cross-fighting comes from — tested, and it was not what I thought

**Status: the gate-chatter hypothesis below is FALSIFIED.** Kept in place because the reasoning that
killed it is the reusable part. Full detail in [`GATE-CHATTER-FINDINGS.md`](debugtests/GATE-CHATTER-FINDINGS.md).

The `Apply` pipeline allocates azimuth error across roll and yaw through several independent gates
and blends: `bankBlend`, `assist`, `yawEff`/`yawWeak`, `qSched`, and the v0.65 `settleOn` gate. Each
one independently decides "am I active." Independent gates chatter at their boundaries, and
`settleOn` in particular is a **mode switch inside the sub-0.5° azimuth cone** — precisely the
small-movement region where the confusion is reported.

**Falsifiable test:** does `REGRESSING` tick density spike at gate-boundary crossings? The recorder
already logs `settleOn`, `bankBlend`, `assist`, `qSched` per tick, so this is a correlation over data
we have. If it holds, the fix direction is fewer gates with hysteresis, not more gain tuning.

### The answer: no

REGRESSING density does not spike at gate crossings in the regions the hypothesis was built to
explain — and the effect runs **backwards**. `fine` has the highest crossing rate in the corpus
(5.66/s) and a regression ratio of **0.82**; `micro1..10` 3.03/s and **0.88**. The chattiest
segments are the unaffected ones. `settleOn`, the gate singled out above, crosses 0.29/s in `micro`
and carries **RR 0.00** — not one regressing tick near any crossing. The `fineVsAlign` contradiction
(two gates disagreeing about who owns the error) occurs in **0.0%** of every segment. It never
happens.

**What killed it was the controls, and they are the reusable part.** Alongside the real gates the
analysis ran **sham gates** — same functional form, thresholds appearing nowhere in
`ChaseController` — plus a circular-shift null and stratification by (run × block). Pooled median RR:
real 3.65, sham 3.16. `turn360`'s headline RR of 7.29 collapses to **1.28 (p=0.31)** once the first
2 s is skipped, and its shams score as high as its real gates. The apparent correlation is **common
cause**: every real gate thresholds on `|azErr|` or `off`, and those cross a small value exactly when
the nose passes through the target — which is exactly when a P-loop overshoot reads as REGRESSING.
Without the sham arm this would have read as clean confirmation.

*Any* future claim of this shape needs a sham control. A threshold that fires when the error is small
will always look correlated with events that happen when the error is small.

### What is actually wrong

- **`elDn` is a sustained roll limit cycle with measured positive feedback** — not chatter. Against
  its mirror segment `elUp` (a *larger* step, upper hemisphere): `off` **6.92° ± 2.40 vs 0.03°**,
  bank half-amplitude **43.3° vs 0.11°**, `outR` sign flips **0.58/s vs 0.00**. `blendWeight`
  correlates **+0.918** with the very `|azErr|` that roll-to-align is itself generating.
  `belowSuppress` exists precisely to break this, and its `(1 − lateralHold)` factor removes **51%**
  of the intended suppression — because `lateralHold > 0` on 88% of ticks *as a consequence of the
  symptom*. **The suppressor is disarmed by the thing it is meant to suppress.** And `blendWeight`
  sits **81% in the mid band**: nothing rails, so hysteresis would have done nothing.
- **Sub-degree REGRESSING is step-size overshoot**, not confusion. r(reg%, max off) = **+0.888**,
  while partial r(reg%, crossings/s | off) = **−0.632** — negative once step size is controlled for.
- **`predFloor` is the one gate that survives.** RR 6.5–36.1 (p ≤ 0.01) across all four azimuth
  steps, 2–16× its matched shams, robust to both skip controls. It is the only *ratio* condition
  rather than a magnitude threshold, and crossing it swings azimuth P gain ~3× with no ramp. R21
  flagged it independently from the opposite direction. Fix is a continuous blend on lead
  confidence, not a hard 0.30 step.

**Caveat that bounds all of this:** 11 captures, **one airframe** (KR-67 Ifrit), one entry condition.
A low-q STOL trainer, where `qSched` and `omegaMax` actually bind, could put real crossings on gates
that never move here.
