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
n_avail     = max(1.05, aircraftGLimit * min(1, (V/cornerSpeed)^2))   # lift-limited below corner
omega_turn  = deg(9.81 * sqrt(n_avail^2 - 1) / V)                     # steady turn rate
omega_pitch = deg(maxPitchAngularVel)                                 # FBW body-rate cap
omega_avail = min(omega_turn, omega_pitch)
```

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

**Two coverage holes the ONE-LAW rule already forbids** and no capture has ever closed:

- `aoaLimiterActivePct` is **0 in every segment of every card ever run**. The "loaded jet mushing
  near its alpha limit above corner speed" case the rule explicitly demands is untested.
- Only two airframes have ever been flown (Ifrit, Multirole1). The rule names four cases.

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

## 5. Open hypothesis: where the cross-fighting comes from

Not yet tested — the first thing to run once `flightscore.py` lands, and it needs **no new flying**,
only the 162 existing captures.

The `Apply` pipeline allocates azimuth error across roll and yaw through several independent gates
and blends: `bankBlend`, `assist`, `yawEff`/`yawWeak`, `qSched`, and the v0.65 `settleOn` gate. Each
one independently decides "am I active." Independent gates chatter at their boundaries, and
`settleOn` in particular is a **mode switch inside the sub-0.5° azimuth cone** — precisely the
small-movement region where the confusion is reported.

**Falsifiable test:** does `REGRESSING` tick density spike at gate-boundary crossings? The recorder
already logs `settleOn`, `bankBlend`, `assist`, `qSched` per tick, so this is a correlation over data
we have. If it holds, the fix direction is fewer gates with hysteresis, not more gain tuning.

**The R21 forensics already support the mechanism class, in a regime where it wasn't even suspected.**
In a *steady sustained turn* — no small movements, no mode switching by design — three separate
gates were found pinned at a rail for essentially the whole window: `lateralHold` at 1.0 (97%),
`predFloor` binding (100%), `_iPitch` held at zero by a magnitude gate (100%). That is the same
disease presenting statically: independent thresholds, each locally reasonable, composing into a
loop whose actual gain and actual integrator bear little resemblance to the configured ones. If the
gates rail in the *easy* case, expect them to chatter in the hard one.

This reframes the likely fix. "Confused in small movements" and "9.4° of standing lag in a sweep"
may not be two bugs — they may be one architecture in which too many independent gates decide, per
tick and without hysteresis, which term owns the error.
