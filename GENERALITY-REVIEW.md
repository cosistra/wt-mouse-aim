# Generality review — one control law for all airframes

> **READ THIS FIRST — the status blocks below stop at v0.65; the file's content runs to v0.99.1.**
> The per-finding verdicts are the authority, not the summary blocks. As of **2026-08-02 (v0.99.1)**:
> - **Finding 16 is a SPLIT verdict, not OPEN.** Its "the feed-forward is inert" inference is
>   **REFUTED** by R39-D — `MarkerRateFeedForward` is worth 55–58% of the standing azimuth error, and
>   turning it off makes the aircraft *skid*. The `blendWeight` hand-off critique in the same finding
>   still stands. The generalisable lesson is inside finding 16 and is the most reusable paragraph in
>   this file: **for a term that moves a TARGET, the servo output that HOLDS the target is the wrong
>   observable.**
> - **The law referenced throughout is `ApplyEvolvedLegacy`, and it is the ONLY law.** `Legacy`,
>   `BankToTurn` (v0.60) and `Unified` with its enum and hotkey (v0.65) are all deleted. Findings
>   that read "FIXED by Unified" describe a state that **no longer exists** — each already carries
>   its own v0.65 revert note; believe the revert note.
> - **`RelativeTurnLead` (finding-adjacent, v0.83) was DELETED in v0.99.1**, knob and branch. The
>   term stays relative; the *lever* is gone.
> - Findings 14, 15, 17, 18 are unaffected by the above and remain as their headings state.

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

**Status (2026-07-28, v0.85): new finding 13 — "live geometry" is not automatically loop-independent
geometry, and this one bit.** The v0.67 down-hemisphere suppressor above was audited as clean because
every term in it is live geometry. It was live geometry *measured through the aircraft's own
response*: `alignFrac` is body-frame, so the aircraft's bank changes the answer (at 90° of bank a
straight-down target reads exactly abeam), and `lateralHold` is azimuth error, which roll-to-align is
itself the source of. So the suppressor was keyed on two signals its own output moves — a feedback
path, not a measurement. Measured on `elDn` over 11 captures: `corr(|azErr|, blendWeight) = +0.918`,
51% of the intended suppression removed on 88% of ticks, 6.92° of standing error at ±43° of bank,
where the *larger* mirror step in the upper hemisphere converges to 0.03°.

**FIXED in v0.85** (`Cfg.BelowAlignSuppress`, default ON): keyed on `alignFracH`, the same belowness
in a horizon-referenced frame around the nose — axes built from `t.forward` alone, so roll cannot
move it — and the `(1 − lateralHold)` factor is deleted outright. Both changes are still live
geometry and still per-plane-constant-free; the difference is that neither input is now downstream of
the command it gates.

**The reusable audit question this adds:** for any gate or schedule, do not stop at "is this term
live/probed rather than a constant". Also ask **"can the command this term gates move this term?"** A
live signal inside its own feedback path is a loop gain wearing a measurement's clothes, and it passes
the existing generality test unchanged.

**Status (2026-07-29): that question was run over the whole `Apply` pipeline — see
[`debugtests/LOOP-AUDIT-FINDINGS.md`](debugtests/LOOP-AUDIT-FINDINGS.md) (tool:
`debugtests/loopaudit.py`, `--selftest`). NEITHER `_yawWeak` NOR `_pitchEff` cleared** (they are
findings 14 and 15 below); a third, larger one (16) fell out of the same sweep. Nine other terms were
cleared with reasons, including `pullGate`, which keys on the *same* body-frame `alignFrac` v0.85 had
to abandon and is nonetheless correct — which sharpens the rule:

> **Express a gate in the frame in which the GATED COMMAND'S PHYSICAL EFFECT is defined** — not the
> frame the signal is cheapest to compute in. A pull rotates the nose about body-right, so
> `pullGate`'s "would pulling help" is a body-frame question by construction. `belowSuppress` gated
> *roll* on a *horizon-frame* geometric fact (the align law's false below-nose equilibrium), so it
> had to be roll-invariant. Same signal, opposite verdicts, one criterion.

**Status (2026-07-30, R32): new finding 18 — the law has no recovery mode, only a graded stand-down,
and the constants that grade it are hardcoded.** Five separate terms de-authorize the pitch channel
when the plant stops responding (`qSched`'s two 0.3 floors, `Max(0.3f, aoaGateUp)`, `pErrTerm *=
_pitchEff` below `PEffRevThresh`, `aoaRecover *= _pitchEff`) and **nothing anywhere in `Apply`
increases authority or changes strategy**. On nine of ten airframes the airframe's own stability
covers the gap. On `Darkreach` it does not, and R32 measured the schedule sitting on its floor for
100.0% of the rows past |AoA| 20° while the aircraft descended 3 000 m and killed the pilot. Full
evidence in [`debugtests/R32-FINDINGS.md`](debugtests/R32-FINDINGS.md); finding 18 below.

**Also corrected by the R32 decompile audit, because it changes what "the game protects it" means:**
`ControlsFilter.GLimiter` is **dead code** (one occurrence in 181 878 lines, never instantiated,
`LimitG` never called), and the FBW's alpha limiter is gated `if (num2 < 1f)` (`:65033`) so it is
**inactive above corner q — where every shipped card flies** (97.7% of R32 rows). The law's
"probed ceiling + live AoA" pattern is therefore not backstopped by the game the way findings 1/2/10
assume; the mod's AoA block is the *only* alpha protection in the loop at card speeds.

- **Align-channel rate lead** (v0.85, `Cfg.AlignRateLead`, default ON) — partial, orthogonal relief for
  finding 5 (`eAlign` is fixed-gain because EL has no roll-effectiveness estimator). The `phi/90` map
  was pure proportional; `phi` is now led by its measured rate with `Cfg.RollDamping` as the lead time.
  This does **not** retire finding 5: the lead angle self-scales with the airframe (live measured rate ×
  a shared time), but the channel's *gain* is still un-normalized. The probed/measured roll-authority
  route in finding 5 remains the principled fix.

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

### 14. HIGH — `_pitchEff`'s v0.67 self-probe cannot clear its own threshold — OPEN (STRUCTURAL + MEASURED)
**Answer to finding 13's `_pitchEff` question: the RATIO clears, the GATE around it does not.**
`Clamp01(ach/cmd)` is scale-invariant, so scaling the command cannot move the measurement — under
rate saturation it even solves to the stable fixed point `_pitchEff = √(ω_plant/(K·e))`, a convergent
soft de-rater. The defect is `PEffRevThresh = 0.15f` being used as **both** the self-probe's target
(L1048) **and** C1's floor threshold, tested `>=` (L1773). The probe is a first-order LPF toward that
same number, so it approaches from below and asymptotes — in float32 it stalls ~30 ulps short — and
the floor branch is **unreachable**. The law then multiplies its pitch P term by `0.1499995` instead
of `Max(0.30, ·)`: **exactly a factor of 2 of pitch authority, lost to a `>=`.** That closes the very
latch the self-probe exists to break, because a P term at ×0.15 keeps the FBW target rate under the
0.05 rad/s re-measure gate. Measured (R11-03, `az30`): 3.07 s at `pEff` 0.143→0.150 with
`|fbwTgtPR|` pinned at 0.045–0.049 and the plant delivering **110%** of commanded; exit was
exogenous, and arrived as a 2× step in loop gain. Occupancy 6.0% of `az30`, 1.6% of `turn360`.
**Fix is one comparison and no new constant** (probe to a level strictly above the threshold, e.g.
`effFloor` — which is what pre-C1 gave for free and what the v0.67 comment says it restores). Highest
priority once the v0.82–v0.85 batch is flown.

**Scope corrected 2026-07-30 (R32 audit), and the mechanism CONFIRMED at corpus scale.** "The branch
is unreachable" is true **only of the self-probe path**, and must not be read as "`_pitchEff` never
goes below 0.15". Over all 1 032 archived captures (627 110 rows, R28–R32): **28 209 rows (4.50%)**
sit below `PEffRevThresh`, min **0.000**, in **89 captures on two fixed-wing airframes**
(`Darkreach` 27 622 rows, `FastBomber1` 587) — genuine reversed-plant measurements, where the no-floor
branch is the *correct* behaviour, not the defect. The defect's own signature is separately visible
and unambiguous: **2 811 rows read exactly `0.150` and only 8 rows read anything above it up to
0.152** — the LPF parking on its own target, exactly as the closed form predicts. So the `>=` → `>`
change is worth making and cannot regress anything, but it moves **0.45%** of corpus rows, all of them
at the boundary. Do not scope an experiment as "unlock a dormant branch" — it would be measuring a
boundary case, and the batch would read as a null.

### 15. HIGH — `_yawWeak` measures "the error did not close", not "the rudder is weak" — OPEN (STRUCTURAL + MEASURED)
**Answer to finding 13's `_yawWeak` question: it does not clear, and the reason is the v0.83 shape.**
The premise (a heading error that will not close means the rudder failed) is true only against a
**stationary** marker; against a sweeping one a standing error is what a P loop must hold. Closed
form on R21's settled turn: closure 0.033 °/s → `weakInst = 1 − 0.033/6 = 0.9945`, recorded
`yawWeak` max **0.996** — on ticks where the FBW delivers 99.4% of commanded rate and no axis is
within 20% of a rail. Corpus-wide it is highest in exactly the two segments already known to be law
defects (`turn360` 0.950, `elDn` 0.743) and lowest in the large errors that close (0.17–0.21).
Both live consumers move what it measures: `yawWeakFade` removes **57%** of the yaw command in the
settled turn, and `coordPull *= assist` gates the term `Cfg.cs` itself calls *"the REAL driver of a
high-speed correction"* on rudder health — so an airframe whose rudder *does* work gets **zero**
coordinating pull for the same commanded bank, which is a one-law violation on its own. Its
normalisation `Clamp01(closeRate / 6f)` is also the absolute-deg/s form the v0.83 `_stallFilt`
comment explicitly forbids as "a per-airframe constant in disguise"; the right denominator
(`omegaMax`, probed + live) is computed two blocks away.

### 16. HIGH — `lateralHold` rails and disconnects the ENTIRE bank pipeline — SPLIT VERDICT (2026-08-02): the `blendWeight` hand-off critique is OPEN; the "feed-forward is inert" inference is **REFUTED**
`lateralHold` rails at `FineBankDeadzone + EvolvedAlignHoldDeg` = **7.5°**, and at the rail
`blendWeight = 1`, so `eFine` — the only carrier of `tBankE` — has weight **exactly zero**. A
standing lag above 7.5° therefore disconnects the machinery whose job is to reduce it, and the
escape condition needs that machinery: a latch, closed in one step. Measured `blendWeight` = **1.0000
on 100.0%** of R21's settled `turn360` (n=1601), 97.6% of the whole segment, 83.6% of `astern`,
63.4% of `reversal`, 60.9% of `az150` — every large-error segment in the corpus. Consequence for the
last two releases: the v0.78 marker-rate feed-forward supplies **82.5%** of the turn demand and
arrives as **0.0000** of roll and **0.0425** of pitch stick (6.9% of `|outP|`), through `coordPull`
alone — which finding 15 gates on `_yawWeak`. With `YawAssistEnabled = false`, a supported config,
the v0.78 feed-forward, the v0.83 relative lead, `predFloor`, `omegaMax` and `MaxBankAngle` have
**no effect on any control output** in a sustained turn. This is R21's finding #1 and GATE-CHATTER
§5a's +0.918 seen from the loop side; the principled statement is that `blendWeight` is a **hand-off
between two alternative roll channels**, not a gate, and whatever replaces it must key on which
channel can close the error rather than on how big the error is. Also flags **dead code**:
`targetBank`/`linBank`/`azBank`/`bankGain`/`bankBlend` are passed to `ApplyEvolvedLegacy` and never
read — they reach only the recorder, the `[chase]` trace and the `over-roll` detector.

> **RESOLVED 2026-08-02, HALF-CONFIRMED — and the wrong half is the durable lesson.** R39-D
> (`debugtests/R39-D-sustained-ab.md`, 8 lanes × n=8) swept `MarkerRateFeedForward` directly. The
> **observation reproduces**, and off the `lateralHold` rail this time (`bWt` 0.000–0.040, so this is
> not the railed regime above): mean `|outR|` is **0.0068–0.0109 on BOTH arms**. The **inference is
> refuted.** The feed-forward is worth **55–58% of the standing azimuth error** (`fixedWindowOffDeg`
> 5.08–6.38° → 1.46–2.18° on 7 of 8 lanes, `rms` down 8/8, effect/replicate-SD −10 to −960), and with
> it OFF the aircraft **skids** instead — mean `|outY|` 2–4× higher, `_iYaw` saturating against the
> deficit the bank channel should have supplied. It was never inert.
>
> **Why the reasoning failed, stated generally because it will recur: for a term that moves a
> TARGET, the servo output that HOLDS the target is the wrong observable.** The feed-forward acts on
> `bankTR` (+10.4 to +15.4°, achieved bank +4 to +14°); roll stick is what trims the aircraft *to*
> a bank and returns to ~0 once there, so reading it to decide whether a bank-target term fired
> measures the settling, not the command. `bankClampActivePct` made the same class of error on a
> different column and cost the corpus its rail detector (`debugtests/R40-metric-repair.md`).
>
> What survives of this finding: the `blendWeight` hand-off critique in the paragraph above stands
> unaltered and is still OPEN — it is a separate claim from the feed-forward's reach. The "dead code"
> list also stands, with one consequence now measured: `targetBank` reaching only the recorder is
> exactly why nobody noticed it had stopped tracking the law.
>
> **New, and not clean:** ON is also what puts the fast lanes on the 72° `MaxBankAngle` wall
> (Fighter1 61.6° OFF → 73.6° ON), so on 94–100% of settled samples three airframes command 1.6–3.1°
> of bank they cannot have. The default is still right — the skid it prevents is worse — but the
> 57% figure was measured *on the rail*, and the discriminating re-fly is eight lanes entered at
> `startSpeedCorner: 0.75` with the throttle pinned. If standing `|azErr|` climbs back toward the
> 3.5° OFF-arm figure once the rail is gone, this vindication is narrower than it reads.

### 17. MEDIUM — v0.85 `AlignRateLead` makes the roll DERIVATIVE gain a function of `blendWeight` — OPEN (STRUCTURAL, unflown)
The lead itself is correct (it is the true derivative of the align channel's own error). The side
effect is not: with `phi` in degrees and `rollRate` in rad/s, against a stationary marker the lead
adds `rollRate·RollDamping·(180/π)/90` of rate feedback **weighted by `blendWeight`**, so the
effective roll-rate feedback is `RollDamping·(1 + 0.6366·blendWeight)` — 1.00× at `blendWeight` 0,
**1.64× at 1** (measured mean multiplier: `turn360` 1.63, `elDn` 1.39). `blendWeight` is the signal
GATE-CHATTER §5a measured at +0.918 with the `azErr` the roll loop produces, so in `elDn` the roll
damping is modulated at the frequency of the cycle it damps. Always stabilising in sign (cannot
diverge), but it breaks the change's own premise that the tuned `RollDamping` is preserved. **Read
the v0.85 flight test knowing `AlignRateLead` is a 64% damping change, not only a lead.**
Two smaller siblings are recorded in the findings doc: `belowSuppress`'s residual `bigTurn` loop
(inert below `off` = 19.3°, live above — so v0.85's `elDn` hang at 6.92° is safe and its *entry* is
not), and `_pitchEff × _alphaSchedFilt` compounding to **0.09** where each is documented as flooring
at 0.30.

> **CORRECTED 2026-07-31.** That last item used to read "*unfalsifiable on a corpus where
> `aoaLimiterActivePct` is 0 in every capture ever taken*". **The premise is false and the item is
> falsifiable today.** `aoaLimiterActivePct` is non-zero on **66** (run, airframe, tag) cells in
> `captures.db`, **23** of which contain no railed segment at all. The usable one is **R33
> `Darkreach·obDR6`: 100.0% occupancy, `railed = 0`, n = 4**, `aoaPeakDeg` 7.38–7.59° against a 10°
> `alphaLimiter`, `authorityUsedFrac` 0.717–0.748 — i.e. the α machinery is live and the actuator is
> *not* at its stop, which is exactly the pair this finding needs. R27's `turn360loq` family (78.7–
> 97.7% on four airframes) also qualifies on occupancy but is RAILED, so read it as no signal.
> The regime appeared because v0.96's #41 fix dropped `Darkreach`'s entry on `oblique-6-c` from
> 171 → 95 m/s. Do not carry "unfalsifiable" forward; the capture exists.

### 18. HIGH — the AoA schedule rails at a hardcoded 0.300 floor while the airframe departs — OPEN (MEASURED, R32)

**The clearest one-law violation in the corpus: a hardcoded constant, not a probed quantity, decides
whether an airframe recovers.** Evidence: [`debugtests/R32-FINDINGS.md`](debugtests/R32-FINDINGS.md)
§6 (63 captures, 37 868 rows, `Darkreach` on `darkreach-05`, 18 departures, 3 dead pilots).

**The constant.** `ChaseController.cs:1255`:

```csharp
const float utilStart = 0.6f, schedFloor = 0.3f, atkTau = 0.05f, relTau = 1.0f;
float aoaUtil  = Mathf.Max(aoaPredUp, -aoaPredDn) / Mathf.Max(1f, aoaCeil);
float schedRaw = Mathf.Lerp(1f, schedFloor, Mathf.Clamp01((aoaUtil - utilStart) / (1f - utilStart)));
...
qSched = Mathf.Min(qSched, _alphaSchedFilt);            // :1260
```

`aoaUtil` is correctly relative (live predicted AoA over the **probed** ceiling — the pattern this
file holds up as right). **`schedFloor` is not.** It is an absolute 0.3 that terminates the
schedule's range at the same place for a 27° ceiling on an 8.7 t `Fighter1` and a 10° ceiling on a
105 t `Darkreach`. `qSched` scales `pErrTerm` only (`:1928`), so at the floor the law is still
committing 30% of its proportional pitch demand into a plant delivering **7.7× the commanded rate
in the opposite direction** (R32 median on departed captures; p90 13.0×, max 28.2×).

Measured: over the 2 314 R32 rows past |AoA| 20°, `qSched` is **exactly 0.300 on 100.0%**. Over the
whole batch it is railed on 16.7% of rows and on 13–91% of every departed capture, against **0.0%**
on all 31 clean pre-onset replicates of the same card and airframe.

**Its two siblings are the same shape**, and finding 10 already lists `schedFloor` as a WATCH item —
R32 promotes it, because "not shown to cost anything" is no longer true:

- `:1152` `qSched = Mathf.Clamp(qRatio, 0.3f, 1f)` — this one is **defensible**: it deliberately
  mirrors the game's own `:65034` clamp, so it is a reconstruction of a game constant, not a mod
  constant. Leave it. Note it never bound in R32 (`qRatio` = 2.03 at the entry condition).
- `:1296` `omegaMax *= Mathf.Max(0.3f, aoaGateUp)` — the v0.67 AoA-margin turn cap, floored "so a
  wing AT the ceiling still holds a sustained turn (mirrors the qSched floor)". Same hardcoded 0.3,
  same absence of an airframe-relative basis, and it is a floor on the *achievability cap*, i.e. it
  guarantees the law keeps demanding a turn a departed wing cannot make.

**And the term that is supposed to be the exception is scaled away too.** `:1557`
`if (!_collective) aoaRecover *= _pitchEff;` — the recovery bias, documented at `:1543` as "the term
that flies the nose back INSIDE the envelope", multiplied by an estimator reading **0.036–0.144** for
the entire duration of the departure. The comment's reasoning ("If the plant is not following, adding
command is pumping") is sound against a *reversed* plant and R32's plant genuinely is reversed — so
this is not a coding error. It is the completion of the pattern: **every one of the five terms that
respond to a non-responding plant reduces authority, and there is no sixth term that does anything
else.** `pErrTerm *= _pitchEff` below `PEffRevThresh` (`:1927`) is the fifth.

**What a fix has to be, to satisfy the rule.** Not a bigger number. The floor is currently the
answer to "how much demand is still safe at the ceiling", asked with no reference to the airframe;
the probed quantities to key it off are already in hand two blocks away — `omegaMax` (probed
`gLimit`/`V`, and after `:1296` also alpha-margin-aware), `_fbwMaxPitchVel`, the measured `_pitchEff`
and the alpha ceiling that `aoaUtil` already normalises by. A floor expressed as a fraction of what
the plant can currently deliver keys off measurement; 0.3f does not.

**Do not fix this before the precursor is understood.** R32 §4 shows the schedule railing is
*downstream* of a roll-to-align event (34–56° of `targetBank` at <5° of `azErr`) that finding 16 is
the standing candidate for. Fixing the stand-down first would make a departed airframe recover
sometimes, which is worse than a departure that is legible.

**Do NOT reach for a mod-side G-limiter as the fix** — see R32 §9. The over-G is a *readout* of the
departure, it damages only the pilot (`Pilot.TakeGForceDamage`, `:85989`, 20 g threshold, one part
index), and clipping it removes the most visible failure signal while changing nothing about the
authority problem. It would also be a sixth de-authorizing term on a law whose defect is that it
already has five.

## Suggested order of attack

0. **Finding 14** — one comparison, no new constant, closed-form proof plus a measured 3.07 s
   episode, and it cannot make anything else worse because the branch it repairs is unreachable *from
   the self-probe path*. Do it first, behind its own checkbox, **after** v0.82–v0.85 have been flown.
   **But budget it as hygiene, not as an experiment**: it moves 0.45% of corpus rows (see the R32
   scope correction under finding 14), so an A/B commissioned to measure it will report a null.
0b. **Finding 18** — the largest measured law defect currently open, and the first genuine *law*
   defect since the instrument fixes. Blocked on understanding the precursor first (finding 16 is the
   candidate); see the note at the end of 18.
1. Get an **Ifrit recording** (blocks finding 3; the fix is cheap once confirmed).
2. **Measured pitch effectiveness** (finding 2) — the FBW rate pair is already being read; this
   is the highest-leverage generic signal and feeds findings 1 and 4.
3. **Pitch rate-normalization** (finding 1) — the structural fix; retire the schedules to
   safety nets. Big change; do it behind the A/B law switch.
4. **Roll authority probe + normalization** (finding 5) — also fixes the two unreported
   Multirole1 defects.
5. Findings 7/8/9 are small hygiene fixes; batch them with whichever of the above ships first.
