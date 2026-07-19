# Generality review — one control law for all airframes

Audit of the control law (`ChaseController.cs`, v0.59) against the core design requirement in
[CLAUDE.md](CLAUDE.md#conventions): **one algorithm that works for every airframe at every load
and speed**, parameterized only by (a) per-airframe values probed from the game's own components
and (b) live physical state — never per-plane tuning constants. First written 2026-07-18 alongside
the v0.59 loaded-jet fix; **update this file when a finding is fixed or a new one is found.**

## What the law already does right (the pattern to copy)

- **Fail-soft probes** supply the airframe truth: FBW pitch-rate params + gLimit/alphaLimiter
  (v0.55), the canard remap range (v0.57), the helo FBW authority + tilt/nozzle archetypes
  (v0.58). Every consumer degrades to pre-probe behaviour on a miss.
- **Physics does the scaling**: `bank = atan(ω·V/g)` makes one azimuth gain correct at every
  speed; `ωmax = gLimit·9.81/V` (the game's own law, reconstructed) caps demand at what the
  plane can fly; `hdgConf = cos(pitch)` deprojects heading errors exactly, no threshold.
- **Measured, not assumed**: `_yawWeak` estimates rudder effectiveness from observed heading
  closure — loadout, speed, and airframe all show up in the measurement for free.
- **Relative, not absolute**: AoA margins are fractions of the probed limiter (v0.56); the v0.59
  AoA-utilization schedule is relative to the same probed ceiling.

## Findings (ranked)

### 1. HIGH — the fixed-wing pitch error term is not achievability-normalized
`pErrTerm = -local.y·sens·fineGain·pullGate` is a raw stick command tuned by feel against the
high-q plant. The bank/yaw turn-rate demand got its `ωmax` cap in v0.55, and **helos got exactly
the right structure in v0.58** (`stick = k·err/ωmax` — a rate demand normalized by probed
authority), but plane pitch still flies the unnormalized term with schedules patching it
(`qSched` v0.56 for low q, the AoA-utilization schedule v0.59 for load/mush). Those schedules
treat symptoms of the same missing normalization. The structural endpoint: command a desired
pitch rate and normalize by the probed `ωmax`, exactly like the helo path — the schedules then
shrink to safety nets. (The v58 discord FS-12 relay is the canonical failure this would prevent.)

### 2. HIGH — no measured pitch-effectiveness estimator (yaw has one, pitch doesn't)
Loadout/mass never appears in any signal except indirectly through AoA. Yaw has `_yawWeak`
(measured closure rate); pitch has nothing measured. The recorder *already reads*
`_fbwFbw.GetTargetPitchAngVel()` / `GetPitchAngVel()` every frame — the achieved-vs-commanded
pitch-rate ratio is sitting there, unused by the law. A low-passed effectiveness estimate (same
attack/release asymmetry as `_yawWeak`) would capture load, damage, density, and mushing
generically, and could drive the demand schedule instead of (or alongside) the AoA proxy.

### 3. MEDIUM — helo yaw carries the fixed-wing reactive damping term
`tgtY = (yErrTerm + _iYaw − yawRate·damp)·YawGain` applies `ChaseDamping` (a fixed-wing outer-
loop constant) on top of the helo's own rate-command PID. Reactive rate feedback around an inner
loop with ~0.3 s lag adds phase lag, not damping — the prime suspect for the reported **Ifrit
post-turn rudder oscillation** (no recording exists yet in the v58 batch; get one before fixing).
Also stacked: the mod's `_iYaw` on top of the helo PID's own integral compensator.

### 4. MEDIUM — `kHelo = 2.0` assumes a universal ~0.3 s helo inner-loop lag
The v0.58 normalization makes the *authority* per-airframe (probed `maxAngularVel`), but the
phase-margin arithmetic hardcodes the fitted UH-90/RAH-72 lag. A modded helo with a slower PID
shifts the margin. Measuring the lag online (achieved vs commanded rate — the same machinery as
finding 2, helo edition) would close it.

### 5. MEDIUM — the roll axis is the least normalized axis
`RollGain`/`RollDamping`/`RollRateSmoothing`/`BankSlewRate` and the fixed 3/s `eAlign` slew are
global constants; no probe reads the airframe's roll authority (likely available in the same
`GetFlyByWireParameters()` array the pitch probe uses). The two **unreported Multirole1 defects**
in the v58 batch — a 0.31–0.34 Hz bank limit cycle at low speed (rec 014529) and 1.28 Hz
roll-stick chatter at ~450 m/s (rec 014141) — are both roll-axis, i.e. the same disease pitch
had before v0.55/v0.59: one gain serving plants whose response varies by an order of magnitude
across speed/airframe. A probed roll-rate normalization is the principled fix.

### 6. LOW — regime thresholds for plain helis are global speed constants
`HeliForwardSpeed`/`HeliHoverSpeed` (60/20 m/s) drive `heliBlend` for any rotorcraft without a
tilt/nozzle gauge. Semi-principled (the 60 matches the game's own weathervane fade, which is
game-wide), but a heavy compound heli and a light scout blend identically.
`CompoundHeloController` is detected but log-only.

### 7. LOW — fine boost is binary-off for collective airframes, not regime-blended
`if (_collective) fineGain = 1` keys off the airframe *class*. A tilt-wing VTOL in full plane
mode (`heliBlend = 0`, wing-borne, FBW-like behaviour) gets no fine-capture boost and may park
short exactly like pre-v0.24 planes. Should key off the live regime (`heliBlend`), not the class.

### 8. LOW — the two lockstep bank-target sites use different speed floors
Apply's shared `bankTR` uses hardcoded `max(50f, vMag)`; `ApplyEvolvedLegacy` uses
`max(Cfg.BankToTurnVmin, vMag)`. The comments demand the sites stay in lockstep; the floors
already disagree.

### 9. LOW — pitch demand schedules only exist inside EvolvedLegacy
`qSched` (and the v0.59 AoA fold-in) scale demand only in `ApplyEvolvedLegacy`. Legacy/
BankToTurn get the post-law AoA gates but not the demand scaling. Acceptable while EvolvedLegacy
is the default and forced for rotorcraft, but a fixed-wing user who A/Bs to Legacy silently
loses the low-q/loaded protection — the same trap the v0.58 rotorcraft law-override closed.

### 10. WATCH — fixed "ponytail" time constants
`aoaLead` 0.30 s, `aoaRateTau` 0.15 s, `hrTau` 0.35 s, `predFloor` 0.30, `eAlign` slew 3/s,
v0.59 `utilStart`/`schedFloor`/attack/release taus. These are *regime* constants (loop-shaping),
not per-plane constants, so they don't violate the rule today — but any that encodes a plant
timescale (the slews, the lead times) is a candidate for probe/measurement-driven scaling if a
future airframe misbehaves. Listed here so nobody mistakes them for arbitrary.

## Suggested order of attack

1. Get an **Ifrit recording** (blocks finding 3; the fix is cheap once confirmed).
2. **Measured pitch effectiveness** (finding 2) — the FBW rate pair is already being read; this
   is the highest-leverage generic signal and feeds findings 1 and 4.
3. **Pitch rate-normalization** (finding 1) — the structural fix; retire the schedules to
   safety nets. Big change; do it behind the A/B law switch.
4. **Roll authority probe + normalization** (finding 5) — also fixes the two unreported
   Multirole1 defects.
5. Findings 7/8/9 are small hygiene fixes; batch them with whichever of the above ships first.
