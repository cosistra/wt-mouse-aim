# Generality review — one control law for all airframes

Audit of the control law (`ChaseController.cs`, v0.59) against the core design requirement in
[CLAUDE.md](CLAUDE.md#conventions): **one algorithm that works for every airframe at every load
and speed**, parameterized only by (a) per-airframe values probed from the game's own components
and (b) live physical state — never per-plane tuning constants. First written 2026-07-18 alongside
the v0.59 loaded-jet fix; **update this file when a finding is fixed or a new one is found.**

**Status (2026-07-18, v0.60):** findings **1, 2, 8 FIXED** by the new `Unified` law (rate-normalized
pitch + the measured `_pitchEff` estimator + the shared `BankSpeedFloor`); **9 RESOLVED by
construction** (the old Legacy law is deleted); **5 and 7 DEFERRED** with reasons noted inline.

**Status (2026-07-19, v0.61):** finding **5 PROGRESS** — `Unified` now normalizes the roll loop by a
measured roll-effectiveness estimator (`_rollEffFilt`, twin of `_pitchEff`/`_yawEffFilt`), fail-soft
to fixed gain; it stays OPEN for EvolvedLegacy (still fixed-gain, the fallback). Two new findings
recorded and **FIXED in v0.61**: **11 (eAlign-slew stale-sign counter-roll, S1)** and **12 (azErr
noise-gate / predFloor interaction, S2)**.

**Status (2026-07-19, v0.65):** **Unified was REMOVED** (the v64 A/B evidence showed it needed EL's
fine stage ported back in to close near-boresight lateral errors — see `plans` / CHANGELOG). This is
the honest cost: several findings were "fixed by Unified" and now revert to the EvolvedLegacy state.
- Finding **5 → OPEN again** — roll normalization lived only in Unified's `_rollEffFilt` servo (now
  deleted); EL is back to fixed `RollGain`/`RollDamping`.
- Findings **1 & 2 → PARTIAL** (were FIXED-by-Unified). EL keeps the v0.64 `_pitchEff` *scaling* of
  its raw pitch term (reversal-gated in v0.65 C1) but NOT the full desired-rate `/ωmax` normalization
  Unified carried. The schedules (`qSched`, AoA fold-in) remain the low-q/loaded protection.
- Finding **12 (S2)** — the "fully cured on Unified" branch is gone; only EL's Track-A.2 partial
  relief (gate on raw `azErr`, size on `azErrPred`) survives. v0.65 B2 adds a bounded sub-0.5° micro-
  bank so the residual EL leaves at high q actually settles (azimuth-only, marker-stationary gated).
- The "future EL pitch mode" (Unified's one genuine idea — pitch as `k·err·pAuth/ωmax`) is documented
  in the v0.65 plan for revival as an EL pitch-mode flag if EL's direct pitch ever proves too hot.

**Status (2026-07-19, v0.67):** four flight-assessment fixes, all keyed to live state / probed params:
- **C1 estimator latch fixed** (finding 2 note). The `_pitchEff` estimator held its value on a dead
  command, so C1 could latch a transient low-q mush at ~0 and freeze pitch (rec14). It now floors a
  dead command at `Max(_pitchEff, revThresh=0.15)` — a self-probe that keys off live `cmd` magnitude,
  no per-plane constant. Reversal still collapses (replay-confirmed on v64 rec04/rec05).
- **Achievability cap now folds the LIVE alpha margin** (strengthens finding 1's achievability story).
  `omegaMax` folded gLimit but not the live alpha-limiter, so at low q the law demanded a turn the
  limiter chopped and the roll hunted (rec16). It now also scales by `Max(0.3, aoaGateUp)` — probed
  ceiling + live AoA, the generality pattern exactly. See finding 10's watch note (this retires one).
- **Finding 12 (S2) / B2 seam** — B2's sub-0.5° micro-bank handed off to the V-scaled `bankTR` with a
  hard gain step at 0.5°, which re-armed a high-q relay on settle exit (rec30). The presence gate is now
  a proportional ramp over `[0.5°, 2°]` at both lockstep sites — still 0 below 0.5° (B2 owns it), still
  predFloor-floored. Geometry-driven regime constant, no per-plane. The deeper truth stands: B2's
  micro-bank can't close azimuth at high q, so B2 and `bankTR` ultimately want to be one continuous
  speed-aware curve (the ramp removes the *dangerous* seam; unifying them is the follow-up).
- **Down-hemisphere pushover** (new behaviour, symmetry fix). Roll-to-align saturated for below-targets
  and found a false ~85° bank equilibrium (rec24); it is now suppressed in the below-moderate band and a
  bounded pushover (bounded by `pullGate`/`aoaGateDn`) closes the target. Keyed to `alignFrac`/`bigTurn`/
  `lateralHold` — live geometry only. Softens the "no-bunt" tenet to a *bounded* negative-g for moderate
  below-targets only (maintainer-blessed); large below-reorientations still roll-and-pull.

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

### 1. HIGH — the fixed-wing pitch error term is not achievability-normalized — PARTIAL (v0.65; was FIXED-by-Unified v0.60)
**Was fixed in `Unified` (removed v0.65).** Unified pitched as `stick = -clamp(kPitch·asin(local.y)·
max(effFloor,_pitchEff) / ωmax)·pullGate` — a desired rate normalized by the probed `ωmax`. With
Unified gone, the only law (EvolvedLegacy) keeps the RAW `-local.y·sens·fineGain` pitch term *scaled*
by `_pitchEff` (v0.64, reversal-gated v0.65 C1) — so load/mush/reversal shrink the demand, but the
structural `/ωmax` rate-normalization is NOT present. The `qSched`/AoA schedules remain the low-q
protection. Marked PARTIAL: the measured-effectiveness half shipped, the rate-normalization half did
not. (Revival path: the "future EL pitch mode" flag documented in the v0.65 plan.)

`pErrTerm = -local.y·sens·fineGain·pullGate` is a raw stick command tuned by feel against the
high-q plant. The bank/yaw turn-rate demand got its `ωmax` cap in v0.55, and **helos got exactly
the right structure in v0.58** (`stick = k·err/ωmax` — a rate demand normalized by probed
authority), but plane pitch still flies the unnormalized term with schedules patching it
(`qSched` v0.56 for low q, the AoA-utilization schedule v0.59 for load/mush). Those schedules
treat symptoms of the same missing normalization. The structural endpoint: command a desired
pitch rate and normalize by the probed `ωmax`, exactly like the helo path — the schedules then
shrink to safety nets. (The v58 discord FS-12 relay is the canonical failure this would prevent.)

### 2. HIGH — no measured pitch-effectiveness estimator (yaw has one, pitch doesn't) — PARTIAL (v0.65; the estimator itself is FIXED)
**Estimator shipped and survives Unified's removal:** `_pitchEff` (computed in `Apply`) is the pitch
twin of `_yawWeak` — a low-passed achieved/commanded ratio of the FBW rate pair, SIGNED (v0.64, so a
reversed plant reads ~0), fast-attack (0.10 s) / slow-release (1.0 s). `ApplyEvolvedLegacy` scales its
pitch term by it, with a v0.65 C1 reversal-gated floor. Fail-soft (holds last value on any miss).
PARTIAL only because it scales a raw term rather than driving the `/ωmax` normalization of finding 1.

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

### 5. MEDIUM — the roll axis is the least normalized axis — OPEN again (v0.65 — Unified removed)
**Regressed to OPEN (v0.65).** The measured roll-effectiveness normalization lived ONLY in Unified's
geodesic roll servo (`_rollEffFilt`, the roll twin of `_pitchEff`/`_yawEffFilt`), which was deleted
with the law. EvolvedLegacy — now the only law — is back to fixed `RollGain`/`RollDamping`. The design
finding stands and is proven feasible: `GetFlyByWireParameters()` carries no roll-authority field, so
the fix is the measured-estimator route (low-passed spike-guarded `|rollRate|/|outR|`), not a probe —
which also satisfies the generality rule. If EL's roll is to be normalized, port that estimator + servo
back in (it is preserved in git history at v0.61–v0.64). (Roll direction in EL still comes from the
`azErr → bankTR` chain, not Unified's body-frame `phi` — see finding 12.)

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

### 7. LOW — fine boost is binary-off for collective airframes, not regime-blended — DEFERRED (v0.60)
**Deferred:** the `if (_collective) fineGain = 1` line is in shared pre-compute that EvolvedLegacy
relies on for rotorcraft; changing it alters helo behaviour, which is out of scope for a fixed-wing-only
v0.60 (`Unified` never runs on a collective airframe, so it is unaffected either way).

`if (_collective) fineGain = 1` keys off the airframe *class*. A tilt-wing VTOL in full plane
mode (`heliBlend = 0`, wing-borne, FBW-like behaviour) gets no fine-capture boost and may park
short exactly like pre-v0.24 planes. Should key off the live regime (`heliBlend`), not the class.

### 8. LOW — the two lockstep bank-target sites use different speed floors — FIXED (v0.60)
**Fixed:** both sites (and `Unified`) now use `Cfg.BankSpeedFloor` (renamed from `BankToTurnVmin`).
Apply's shared `bankTR` previously hardcoded `max(50f, vMag)` while `ApplyEvolvedLegacy` used the bind;
they are now the same knob (default 50 = the old hardcoded value, so no behaviour change at defaults).

### 9. LOW — pitch demand schedules only exist inside EvolvedLegacy — RESOLVED by construction (v0.60)
**Resolved:** the Legacy/BankToTurn laws were deleted (v0.60), and as of v0.65 `Unified` is gone too —
there is now exactly ONE fixed-wing law (EvolvedLegacy), which carries the low-q/loaded protection
(`qSched` + AoA schedules + `_pitchEff` scaling). With a single law the "A/B'd into an unprotected law"
trap cannot exist by construction.

`qSched` (and the v0.59 AoA fold-in) scale demand only in `ApplyEvolvedLegacy`; the (now removed)
Legacy/BankToTurn laws got the post-law AoA gates but not the demand scaling — a fixed-wing user who
A/B'd to Legacy silently lost the low-q/loaded protection, the same trap the v0.58 rotorcraft
law-override closed.

### 10. WATCH — fixed "ponytail" time constants
`aoaLead` 0.30 s, `aoaRateTau` 0.15 s, `hrTau` 0.35 s, `predFloor` 0.30, `eAlign` slew 3/s,
v0.59 `utilStart`/`schedFloor`/attack/release taus. These are *regime* constants (loop-shaping),
not per-plane constants, so they don't violate the rule today — but any that encodes a plant
timescale (the slews, the lead times) is a candidate for probe/measurement-driven scaling if a
future airframe misbehaves. Listed here so nobody mistakes them for arbitrary.

### 11. eAlign-slew stale-sign counter-roll (S1) — FIXED (v0.61, Track A.1)
**Root cause:** `_eAlignSlew` was a persistent static, never reset on engage or on a demand step,
that `MoveTowards`-ramped toward `clamp(phi/90, ±1.5)` at a fixed 3/s. Two failure modes compounded:
(a) on a demand **sign reversal** the rate limiter ramped the setpoint *through zero*, holding the
previous sign for the crossing (~0.3 s) → wrong-way roll at maneuver onset; (b) the seed was often
**ill-conditioned** — a near-boresight HOLD frame, where `phi = atan2(local.x, local.y)` is meaningless
(both components ~0), still saturated `eAlign` and injected that junk into the next real maneuver.
**Fix:** gate the slew to the dead-astern **wrap region** (`|phi| > 135°`, the only place `phi` is
frame-to-frame discontinuous), pass through elsewhere (no lag), and zero `eAlign` below the atan2
conditioning floor; reset on engage. **Cautionary pattern:** rate-limiting an error/setpoint signal
creates a wrong-sign output across every reversal — only rate-limit where the signal is *genuinely
discontinuous* (here the ±180 wrap), never as a blanket anti-relay. Shared code, so it fixed the
onset counter-roll on both laws.

### 12. azErr noise-gate / predFloor interaction (S2) — PARTIAL (v0.61 Track A.2; the Unified "deeper cure" was removed v0.65)
**Root cause:** the turn-rate bank zeroed on `|azErrPred| ≤ 0.5°`, but `azErrPred` is floored at
`0.30·azErr` (the v0.54 proportional-floor brake), so a genuine 1–2° error was shrunk *below* the
0.5° presence gate → `bankTR` chattered 0↔14° and the speed-independent fine-boosted yaw carried the
capture (a rudder-slew instead of bank+pull; worse at high speed). The 0.5° subtraction was doing
double duty as both a presence gate and a magnitude offset. **Fix (A.2, shipped, still live):**
decouple them — gate on the **raw** `azErr`, size on the predicted `azErrPred`. The Unified "deeper
cure" (roll direction from body-frame `phi` instead of the `azErr` chain) was **removed with Unified in
v0.65**, so EL keeps the `azErr → bankTR` direction chain and A.2's partial relief. **v0.65 B2** closes
the remaining high-q sub-0.5° residual a different way: a bounded, V-independent micro-bank in the
sub-gate cone (marker-stationary gated), so EL now settles the last half-degree instead of parking.

## Suggested order of attack

1. Get an **Ifrit recording** (blocks finding 3; the fix is cheap once confirmed).
2. **Measured pitch effectiveness** (finding 2) — the FBW rate pair is already being read; this
   is the highest-leverage generic signal and feeds findings 1 and 4.
3. **Pitch rate-normalization** (finding 1) — the structural fix; retire the schedules to
   safety nets. Big change; do it behind the A/B law switch.
4. **Roll authority probe + normalization** (finding 5) — also fixes the two unreported
   Multirole1 defects.
5. Findings 7/8/9 are small hygiene fixes; batch them with whichever of the above ships first.
