# High-speed bank-loop wobble — diagnosis (branch `wobble-fix`, 2026-06-21)

> **2026-07-15 UPDATE 8 — the vertical-zoom azErr singularity, the rotorcraft ~1 Hz limit
> cycles (hot outer gain around the game's helo rate FBW), and the canard-probe mystery.
> Fixes SHIPPED in v0.58.0.** Inputs: the 8-recording v0.57 Ifrit round
> (`debugtests/v57/`, session 022335) + the three Discord heli recordings (UH-90 ×2, RAH-72).
>
> - **The round's one FAIL (023018, growing 0.46 Hz roll/az cycle at 244–279 m/s) was a
>   GEOMETRY bug, not a gain bug.** `off` sat at 0.3–1.7° while azErr swung ±9.5° — only
>   possible near the vertical. Reconstructing cos(pitch)=√(off²−elevErr²)/|azErr| per row:
>   023018 h≈0.132 (nose ~82° up, a zoom climb; independently re-derived, median 0.132
>   p10–p90 0.129–0.134), sibling 022946 h≈0.014 (~89°!), every clean file 0.99–1.00.
>   Horizontal-plane azErr inflates by 1/cos(pitch); the V-scaled atan bank map chased the
>   phantom (commanded bank anti-phase with achieved, corr −0.90) into a growing cycle that
>   railed the roll stick. **A static fine-cone/high-V gain cap was built first and rejected
>   by replay**: it cut 023018 ×3.9 but squashed the healthy high-V standing turns ×2.1–5.2
>   (023328/023918/023931 legitimately hold 30–60° bank from 1–2° standing error). The shipped
>   fix multiplies the bank-path errors (azBank, azErrPred post-clamp) by hdgConf=cos(pitch)
>   (=|horizontal projection of t.forward|, already computed): exact, threshold-free, replay
>   ×7–12 cut on the two pathological files, ≤0.5° change on every clean/big-turn file.
>   Pitch/yaw fly body-frame errors, so near-vertical captures still close.
> - **Rotorcraft: both reported wobbles are pre-v0.55 fixed-wing disease on a rotor.** The
>   full rotorcraft decompile (was missing — now at `<decompiled>/HeloControlsFilter.cs` etc.)
>   shows `HeloControlsFilter` overrides `Filter` (no 25 m/s early-return) and runs
>   `HeloFlyByWire`, a 3-axis **rate-command** PID; fitted from the recordings the plant tracks
>   the mod's stick as a rate at corr 0.88–0.97 with ~0.3 s lag. The mod's error→stick gain
>   (sens×FineGainBoost×HeliYawScale ≈ 0.37 stick/deg) made the outer loop 10–15 s⁻¹ around
>   that lag ⇒ UH-90 0.88–1.08 Hz pitch buzz (law-independent — Legacy A/B identical, rec
>   224117), RAH-72 1.16 Hz hover yaw relay railing at ~3° azErr. Fix: helo probe (private
>   nested `heloFlyByWire` via Traverse) + normalized rate commands `stick=kHelo·err/ωmax`,
>   kHelo=2.0 s⁻¹ (~55° phase margin vs instability at ~5 s⁻¹); FineGainBoost forced 1 on
>   collective; replay cuts the wobble-window stick ×3.5–8. Regime: heliBlend now driven by
>   max(speed-blend, tilt-fraction) — `TiltWingController.GetWingAngle()` /
>   `SwivelDuctSystem.GetNozzleAngle()/90` (orientation of the tiltwing fraction UNCONFIRMED,
>   needs one transition flight); `HeliForwardSpeed` 150→60, `HeliHoverSpeed` 40→20 (the
>   game's yaw weathervane fades in at 40–60 m/s — its own statement of where yaw stops
>   turning the aircraft); rotorcraft always fly EvolvedLegacy (Legacy had zero heli logic).
> - **The v0.57 canard fix is still UNEXERCISED — and that's correct behavior.** This session's
>   'Multirole1' read `Aircraft.relaxedStabilityController` = raw null (no `[canard]` line, no
>   `canardRange` in the fbw header) while showing NO buzz (flip 0.22–0.30 vs 0.58–0.61 in v56;
>   pitch fit r=+1.00): the game guards its remap on the same field, so null field = the game
>   wasn't remapping either. Same binary/plugins/config as the buzzing v56 session 1 h earlier
>   ⇒ a different Multirole1 prefab/variant was flown (Blueprinter clone with the SerializeField
>   unwired is the best-supported theory; the field is never assigned at runtime). Side find:
>   `RelaxedStabilityController.effectiveness` latches 0 on engine-off and NEVER resets — buzz
>   can vanish for a whole airframe instance mid-session on the stock jet too. `[canard]` now
>   logs unconditionally with a log-only `childScan=` (binding stays field-only — inverting a
>   remap the game isn't applying would CREATE the warp).
> - **v0.57 fixes verified in the field:** eAlign slew turned the astern chatter into a
>   committed capture (022946's railing converges); the predictive AoA gate held outP=0 through
>   a 4.7 g/124 m/s break with only +1.6° momentum overshoot (022658, benign WARN). The 63
>   overshoot + 47 over-roll anomaly lines were the 023018 loop at benign amplitude (all
>   off<8°, median 315–347 m/s) — expected to collapse with the deprojection. All 41
>   overstress lines were detector garbage (one unloaded 1 g departure, |AoA| up to 176° at
>   21–66 m/s): alpha branch now gated on g>2.5 ∧ |AoA|≤90° ∧ V>100 (retro-suppresses 41/41,
>   keeps the genuine v56 9.7 g events). New `az-limit-cycle` detector (two 3 s half-windows:
>   flips≥2 both, envelope ×1.25, outR span ≥0.5, fine-cone only) fires at t=380 on 023018 —
>   4 s before the rail — and on nothing else in 55 sample files.**

> **2026-07-13 UPDATE 7 — the KR-67 Ifrit ~5.3 Hz straight-line pitch buzz (the game's canard
> input remap) + the assist-off AoA pump + the big-turn eAlign relay. Fixes SHIPPED in v0.57.0.**
> The 47-recording v0.56 fleet round (`debugtests/v56/`, 8 airframes: FS-12, Trainer, CI-22,
> CAS1, Aryx light fighter, KR-67 'Multirole1', EW1, P_Trisurface1) — three agent batches plus a
> dedicated micro-oscillation pass on the user-reported Ifrit buzz the analyzer couldn't see:
>
> - **The buzz is real, Ifrit-exclusive and old.** High-passed outP sign-flips on 57–70% of
>   samples at ~5.3 Hz (period-3-sample correlogram at the 15 Hz recording rate), hf pp up to
>   0.74 stick, g wiggling ±0.4, on up to **82% of a file** (012502), in dead-straight flight
>   (elevErr ±0.2°, iPitch flat, AoA benign), BOTH assist states. Every other airframe is clean
>   under the same detector — including Aryx/Trisurface with the same maxPitchAngVel=0.75 — and
>   the v0.53-era KR-67 recordings show it too (weaker, 43% flips at 430–450 m/s).
> - **Root cause is the game, mechanism confirmed in decompiled source.** The Ifrit is the one
>   airframe with a `RelaxedStabilityController`. `Aircraft.FilterInputs()` runs it BEFORE the
>   FBW: `inputs.pitch = Lerp(a, rawPitch, |rawPitch|)` with `a = AoA/canardRange` (V > 30 m/s,
>   effectiveness 1 unless engine-off). Consequences: small stick acts **quadratically**
>   (x·|x| — 0.05 stick delivers 0.0025, half stick delivers a quarter), and e(x) is locally
>   **non-monotone**: for `0 < x < a/2` more stick = LESS effective pitch (at cruise AoA ≈1.3°
>   the reversed zone is |x| < ~0.03 — exactly where fine-aim corrections live). The mod pushed,
>   nothing (or the opposite) happened, pushed harder, the quadratic bit on a very agile plant,
>   overshot, flipped sign: a describing-function deadzone limit cycle. Phase data agrees: outP
>   tracks pitchRate at lag 0 (corr +0.84, the damping term reacting) and vs fbwPR −0.86 — the
>   loop closes stick↔plant with ~1.5 samples of transport.
> - **Fix: invert the remap.** e(x)=a(1−|x|)+x·|x| solves closed-form per branch
>   (p≥a: x=(a+√(a²+4(p−a)))/2; p<a: x=(a−√(a²+4(a−p)))/2 — e is monotone each side of the
>   O(a²) dip), so the mod commands the stick that makes the game deliver the law's intended
>   pitch. Probe-driven (`relaxedStabilityController`/`canardRange` via AccessTools, fail-soft),
>   identity on all other airframes. Also carries the trim offset (cancelling `a` takes ~−0.22
>   stick at cruise AoA — more than the whole iCap; the fine integrator used to grind at it).
> - **Assist-off AoA pump (Trainer/CAS1): the reactive gate is a relay.** Hard pull → AoA blows
>   1.3–2.5× past the ceiling before the fade bites → gate slams outP→0 → AoA falls → gate
>   reopens into the same full pull, ~0.7 Hz, GROWING (Trainer 010051: peaks 8.8°→20.4° over
>   8 s on an 8.5° ceiling; CAS1 010930/010938: 9.7 g on a 6 g frame, 22.2° on a 14° limiter,
>   16 s — with ZERO anomaly lines). v0.57: gate on predicted AoA (+0.30 s of rate, only INTO
>   the ceiling; reopen on real AoA — the asymmetry is hysteresis). Same lesson as UPDATE 6's
>   peak-clipper: a boundary needs phase, not just a clamp. New `overstress` anomaly line.
> - **eAlign relay (FS-12 005641/005706, CI-22 010621, KR-67 012442): third member of the v0.53
>   raw-error→roll family.** Target crosses dead-astern → phi flips sign in ONE tick (+162°→
>   −147°) → eAlign=clamp(phi/90,±1.5) reverses rail-to-rail at 0.86–0.98 Hz. tBankE stayed
>   smooth throughout — the v0.54 slew never sees this path, and the v0.53 deadzone only guards
>   the fine cone. v0.57: eAlign slew-limited at 3/s (chatter needs ~5.5/s to sustain).
> - **v0.56 itself verified:** q-schedule tracked correctly everywhere, no schedule-induced
>   sluggishness (Aryx converges in the same 1–2 s at sched 0.6 as at 1.0); Trainer relative
>   margins confirmed (gating only >6° AoA, assist-on); 005417's FAIL is a genuine energy-bleed
>   departure, not a regression. The 60+ s assist-ON takeoff regression test was NOT flown —
>   still owed. Game-side oddity: P_Trisurface1 records 15–19 g against gLimit=9 (game's own
>   gForce; its limiter strength is 0.03 — likely WIP airframe).

> **2026-07-12 UPDATE 6 — the 0.55 Hz low-q pitch limit cycle (the takeoff oscillation).
> Fix SHIPPED in v0.56.0.**
> The maintainer's 31-recording v0.55 round (FS-12 + Trainer, `debugtests/v55/`) plus three
> agent analyses and a phase-forensics pass on the cleanest capture (225034: 15.5 s of a very
> regular **1.77 s period** at 112–115 m/s, assist ON, `engP/R/Y = 0` on every row — 100%
> mod-flown):
>
> - **The cycle is pitch-dominant and the plant supplies the phase.** Cross-correlation lags
>   around the loop: elevErr→outP **+0.13 s** (corr −0.97 — the mod is an essentially instant
>   P-responder; no mod-side filter lag), but the ACHIEVED pitch rate lags the command by >1 s
>   at 113 m/s, and AoA/flight-path integration adds the rest of the 360°. iPitch stayed at
>   ±0.003 (integrators irrelevant); azErr↔elevErr corr only −0.25 (roll rides along, doesn't
>   drive). Classic saturating P-loop around a slow low-q plant: outP railed −1.00 each
>   half-cycle while AoA was still negative, then swung to +0.4..0.7 as AoA hit 14–17.7°.
> - **It ratchets, slowly.** One continuous flight showed the amplitude building over ~55 s of
>   uninterrupted tracking (bank 18°→85°, g 0.7→7.5, AoA −29..+37°) into stall/departure —
>   which is why deliberate short low-speed tests never reproduced it and takeoff climb-out
>   (a long window of full mod authority) reliably did.
> - **Why v0.55 didn't cover it**: the pitch normalization was ≡ 1 with assist ON by design;
>   the ωdes cap is atan-saturated near corner speed (capped bankTR still ~83° at 156 m/s);
>   and the AoA gate is a peak-clipper — it trims the driving side but leaves the
>   recovery-direction command at full ±1, so it bounds peaks without breaking the cycle.
> - **Sluggishness attribution** (4,219 demanding low-speed samples): the airframe tracks the
>   game's own commanded rate at r 0.85–0.99 — the plane was never the bottleneck. Most
>   holdback was the AoA fade in genuinely deep-AoA flight (by design), EXCEPT: (a) on the
>   Trainer (alphaLimiter 10°) the fixed −4°/6° margins put the fade start at **0° AoA** —
>   60–90% of pitch authority cut at ordinary 3–5° turning AoA; (b) the assist-OFF
>   normalization (×0.32–0.5 below corner) was the single biggest deliberate restriction.
>
> **v0.56.0 fix**: the pitch demand terms (error P + coordPull) are scaled by the game's own
> q clamp (`clamp(q_ratio, 0.3, 1)`, ≡ 1 at/above corner speed) so the loop asks at a constant
> fraction of achievable at every speed, while the UNSCALED rate-damping term becomes
> relatively ~3× stronger exactly where the plant is slow — one mechanism, both assist
> regimes. AoA margins went relative (`min(4°, 0.15·lim)` / `min(6°, 0.25·lim)`: Trainer
> freed, FS-12 identical). The assist-off normalization is DELETED (assist-OFF = performance
> mode, guarded by the AoA ceiling + q schedule). Analyzer verdicts split FAIL/WARN
> (rail-only evidence is a benign WARN — the v55 captures proved plain roll-railing is
> usually a max-performance reversal) and the digest derives per-segment `sched`/`gate`
> attribution from existing columns. No new knobs, no new CSV columns.**

> **2026-07-12 UPDATE 5 — the LOW-SPEED regime (Draken round 2). Fix SHIPPED in v0.55.0.**
> With the high-speed wing-rock fixed (all high-speed v54 captures PASS the scorer), 11 new
> tester recordings exposed a different failure below ~450 km/h: a compounding ~0.25 Hz
> azErr/pitch oscillation (AoA swinging −18..+37°, g 0.3–4.3, tgtBank slamming ±72°, can crash)
> plus "stability assist OFF turns ~3× slower".
>
> - **The decompiled law** (`ControlsFilter.FlyByWire.Filter`, fresh ilspycmd dump): pitch stick
>   is a g-scaled RATE command, `targetPitchRate = pitch·gLimit·9.81/max(V, 0.75·Vc)`; below
>   corner speed it is further scaled by `clamp(q_ratio, 0.3, 1)` and a weak alpha limiter
>   (0.05/deg above ~25° — AoA can still blow through to 35°+). The in-game "stability assist"
>   toggle selects that protected law vs a flat, UNPROTECTED `pitch·maxPitchAngularVel`; above
>   q_ratio ≈ 1.2 the two are IDENTICAL.
> - **Offline model fit** (`pitchRate ~ −outP·G·9.81/V`, now productized in analyze-wobble.py):
>   at 300–414 m/s the implied full-stick G is 7.8–9.5 with corr +0.92..+0.99 — law confirmed on
>   real data. At 52–145 m/s the corr goes NEGATIVE (−0.35..−0.51): the aircraft no longer
>   follows pitch commands (stall dynamics dominate) while the mod keeps commanding full scale —
>   the outer loop winds up, coordPull loads into the stall, and the ±72° bank targets compound
>   it. No anti-windup existed anywhere against inner-loop saturation.
> - **The "3× slower" assist-off report**: v0.51's flat `AssistOffPitchScale = 0.5` was wrong per
>   the decompiled law — assist-off changes nothing at high q, so the mod was just halving its
>   own turn rate. No flat scale can be right; the real difference is speed- and airframe-shaped.
>
> **v0.55.0 fix (one concept: command only what the plane can do)**: an FBW probe reads the
> per-airframe params from the game each tick (public `GetFlyByWireParameters()` + AccessTools
> for the private `gLimitPositive`/`alphaLimiter`, fail-soft everywhere); the pitch command is
> normalized by the protected/achievable rate ratio (≡ 1 assist-on/high-q — zero change to the
> verified high-speed feel — and the exact per-airframe cut assist-off at low q; the
> `AssistOffPitchScale` knob is DELETED); the turn-rate demand ωdes is capped at the achievable
> pitch rate in BOTH bank-target sites so low-speed bank targets shrink physically; the fine
> integrators freeze while capped; and a mod-side AoA ceiling (game `alphaLimiter` − 4°,
> sign-aware, assist-independent) gives assist-OFF the protection the game withholds.
> Recordings now carry `assist`/`fbwTgtPR`/`fbwPR` columns + a `# fbw` params header; the scorer
> gained the model-fit line and a named `low-speed stall oscillation` verdict — all four
> low-speed v54 captures FAIL it, all high-speed captures still PASS.**

> **2026-07-10 UPDATE 4 — v0.53 verified; the remaining oscillation is the v0.52 brake-clamp
> RECTIFYING the lead into a 0↔azErr sawtooth. Fix plan below SHIPPED in v0.54.0 (2026-07-11):
> predFloor 0.30·azErr + hrTau 0.35 + new `BankSlewRate` knob (60°/s) + `tBankE` CSV column.**
> Nineteen v0.53 recordings (KR67 EFRET 450–536 m/s, AB4 Alcyon 'FastBomber1' 226–518 m/s), three
> parallel analyses + inline cross-checks:
>
> - **v0.53 confirmed working**: `outR` vs `sign(azErr)` correlation ≈ 0 in every chatter window;
>   `lateralHold` never exceeds 0.25. The eAlign relay is dead.
> - **Fast ~1.5 Hz chatter (KR67, 4/11 FAIL)**: a closed loop through the lead term itself. Bank
>   oscillation → `headingRateFilt` ripples ±3.5°/s at the same cadence → `azErrPred =
>   clamp(azErr − hRF·0.65, [0, azErr])` **rectifies** between 0 and full azErr every half-cycle
>   (the brake-clamp turned the v0.51 sign-flip relay into a one-sided bang-bang, not a fix) → the
>   ~44°-bank-per-degree atan slope at 500 m/s amplifies that into tBankE banging 0↔48–65° → the
>   eFine servo faithfully chases it (corr `outR` vs `bankTR−bank` = 0.79–0.96; vs everything else
>   ≤ 0.5) → wings rock ±14–30° from a 1–3° error that never changes sign. leadT=0.65 s sits at
>   the loop's own period. Steady vs decaying is amplitude, not mechanism (worst: 233131).
> - **Slow 0.5–0.55 Hz cycle (KR67 233258 FAIL, Alcyon 233509 GROW)**: same rectification at
>   larger scale. `azErrPred` pins to exactly 0.00 while raw azErr is still 1.5–5.7° →
>   targetBank/tBankE collapses to ~0 in <1 s → the wings-leveler executes a *commanded* rollout
>   mid-correction → nose drifts → error regrows. Sustained by roll-servo overshoot: bank
>   overshoots the fast-moving target by 15–20° (peaks ±88°), which sweeps the marker past
>   boresight and re-triggers the cycle opposite-signed (eAlign saturates via phi sweep, eFine via
>   extreme bank; RollDamping 0.10 too weak to check it).
> - **User's "self-leveling causes it" suspicion: CONFIRMED in precise form** — the leveler is not
>   misbehaving; it is *commanded* to level early by the pinned-to-zero prediction. His "yaws
>   instead of banking on diagonals": refuted for large snaps (outR rails in ~100–120 ms while
>   outY never exceeds 0.46 anywhere in 19 files; coordPull engages properly) — but confirmed in
>   effect for sub-2°: there tBankE flaps 0↔−27° (Alcyon Phase A), so the wings rock without ever
>   *holding* a bank, the velocity vector never turns, and closure falls to the weak rudder.
> - **Beware the recorded `targetBank` column**: EvolvedLegacy flies its local tBankE (≈ the
>   `bankTR` column), not `targetBank` (the yawWeak-gated shared blend). Reading targetBank
>   produced two red herrings this session (233612's fake "1.72 s bank lag" — actually bigTurn
>   gating rollErr to eAlign — and a misread of 233509 Phase A as pure leveler instability).
>
> **v0.54 fix plan (agreed direction, in order)**: (1) hrTau 0.18→0.35 (cuts the ripple feeding
> the rectifier ~2.3× at 1.5 Hz); (2) slew-rate-limit tBankE (a bank target that can't bang
> 0↔48° at 1.5 Hz can't relay, and a slower-moving target also shrinks the roll-servo overshoot
> that sustains the slow cycle); (3) proportional floor on the brake clamp — clamp azErrPred to
> [k·azErr, azErr] (k≈0.25–0.3) instead of [0, azErr], so early rollout still happens but full
> wings-level is never commanded while real error remains (floor self-releases as azErr→0);
> (4) record tBankE in the recorder so validation runs show the target the servo actually flew.
>
> **2026-07-10 UPDATE 3 — v0.52 exposed a SECOND az→roll path; v0.53.0 deadzones it.**
> Twelve v0.52 recordings (KR67 EFRET 480–588 m/s + Trainer): the v0.52 clamp verified working —
> `targetBank` stays inside ±3° while station-keeping — yet the wings still rocked ±20–33° at
> ~1.2 Hz with `outR` flipping sign (worst: 14 s sustained, 3/12 files FAIL, more just under
> threshold). Raw rows show the roll command tracking **sign(azErr) at full scale**, not the bank
> error: near boresight `phi` snaps ±90° with the sign of a sub-degree error, so `eAlign = phi/90`
> is a directional relay, and the v0.42 align-hold weight `|azErr|/EvolvedAlignHoldDeg` fed it RAW
> error — ±0.2 roll stick per degree, no lead, no deadzone, bypassing the entire atan/lead/clamp
> bank pipeline. Only visible once v0.52 quieted that pipeline; only violent at 570+ m/s roll
> authority. **v0.53 fix: subtract `FineBankDeadzone` from |azErr| before the weight** (the same
> guard `azBank` has had since v0.29) — inside the fine cone roll is purely eFine (wings-level +
> braked/clamped tBankE). Open-loop replay: roll-command sign-flips 15→3 / 8→3 in the two chatter
> segments, big-turn segment byte-identical. Remaining fallbacks unchanged: hrTau 0.18→~0.35,
> then motion-profile shaping.
>
> **2026-07-10 UPDATE 2 — v0.51's lead closed its own FAST loop; v0.52.0 clamps it.**
> Sixteen v0.51 flight recordings (Ifrit + Compass, 108–508 m/s): the slow 0.3–0.85 Hz cycle is
> gone (big turns are clean single-overshoot responses), but a NEW ~1.1–1.35 Hz chatter appeared
> during small-error station-keeping (HOLD phase), onset ~280 m/s. Mechanism (phase-confirmed):
> with azErr small, `azErrPred` is dominated by `−headingRateFilt·0.65` (2.1–2.7× the real error);
> the V-scaled atan slope (~44°/° at 470 m/s) turns that rate ripple into a ±65° bank relay;
> hrTau LPF lag (56° at 1.3 Hz) + the 0.4–0.6 s actuation lag supply the phase; headingRateFilt
> measurably LEADS bank (causal inversion = the lead drives the loop). **v0.52 fix: clamp
> `azErrPred` to [0, azErr]** — brake-only lead: early rollout preserved, but the command is
> bounded by the real error, so the relay can't self-sustain. If residual chatter survives flight
> testing, next levers (in order): raise the hardcoded `hrTau` 0.18→~0.35 (cuts 1.3 Hz content
> ~2×, costs 0.2 s of rollout timing), then motion-profile shaping (parked item 3 below).
>
> **2026-07-10 UPDATE — root cause CONFIRMED, fix SHIPPED in v0.51.0.** The v0.38 damping fix
> planned below was never implemented, and EvolvedLegacy (v0.42) then made the undamped
> `atan(ω·V/g)` bank law universal at all speeds — users kept hitting the wobble. Ten user
> recordings (Kryrins KR67; Draken Multirole1/FS-12/Trainer; two helis) were analyzed:
>
> - **Confirmed**: the azimuth→bank command is pure-proportional (corr(azErr, bankTR) =
>   +0.83…+0.94 at zero lag in all 8 fixed-wing files) while achieved bank lags the command by a
>   **constant 0.68–0.71 s** and overshoots it 1.2–3.2× — ~90–180° phase at the observed
>   0.3–0.85 Hz → self-sustained limit cycle. Worst case: ±88° bank from ±6° azErr, `outR`
>   railed ±1 for 33% of a 47 s cycle, targetBank pinned at the ±72° clamp 23–33% of frames.
> - **Unstable at EVERY tested speed (70–390 m/s)** — even the Trainer at 72 m/s shows a growing
>   0.5 Hz cycle; speed only scales the amplitude. Frequency is set by airframe time constants,
>   so gain cuts alone can never fix it — it needed phase (lead), as planned below.
> - **Ruled out numerically**: inner 3–7 Hz roll PIO, yaw hunt, integrator windup, yawWeak
>   chatter (symptom, not driver). Pitch oscillates at the same frequency as a coupled passenger.
> - **Second mode**: with the game's flight-assist (AoA limiter) OFF, the FBW's stick→pitch-rate
>   gain doubles-triples at low-mid q (decompiled `ControlsFilter.FlyByWire`:
>   `targetPitchAngVel = stick·maxPitchAngularVel` instead of the G-limited formula) → recorded
>   pitch divergence (FS-12: outP railed, AoA −29°→+52°).
>
> **v0.51.0 fix**: `azErrPred = azErr − noseHeadingRate·TurnLeadTime` (default 0.65 s, just under
> the measured lag) feeds both copies of the turn-rate bank math; raw azErr kept for the linear
> servo + coordPull release taper. Plus `AssistOffPitchScale` (0.5) while flight-assist is off.
> Score recordings with `debugtests/analyze-wobble.py` (codifies these metrics; the two violent
> baseline recordings FAIL, the mild one PASSes).
>
> **Parked follow-ups**, in priority order: (1) heli sideways wobble — a DISTINCT ~1.15 Hz
> yaw-loop limit cycle at heliBlend≈1 (HeliYawScale 2.0 P-gain, negligible yaw-rate damping) —
> same lead/damping recipe on the yaw axis; (2) full per-airframe FBW pitch-gain inversion
> (needs reflected `gLimitPositive` + reconstructing `limitFactorSmoothed`'s dynamic-pressure
> blend — the assist-off gain jump only exists below ~1.1·cornerSpeed); (3) no-overshoot
> motion-profile shaping (ω_des = min(ω_max, √(2·err·decel))) if lead alone isn't enough.

Checkpoint of the v0.35→v0.37.1 control-law work plus the diagnosis of a **high-speed wobble**.
The v0.38 fix below was the plan; **v0.51.0 shipped it** (see update above) — kept for history.

## Symptom

A jet whose **rudder folds away after ~Mach** (yaw goes truly dead, `yawEff` ≈ 0.01) feels
**wobbly** at 290–560 m/s. The nose rocks / wallows instead of settling on the marker.

## Data (5 recordings, one analysis agent each)

| CSV (mouseaim-rec-2026-0621-) | spd m/s | off peak | What it shows |
|---|---|---|---|
| `185205` | ~290 | 4.5° | ~0.5 Hz roll hunt, ~2 s period, decaying; small DC residual |
| `185422` | 490–508 | 7.4° | roll limit cycle, period 3.6→1.7 s; targetBank to −72° cap |
| `185455` | 490–510 | 13° | one **144° bank sweep** — `phi` wrapped ±180 (marker behind). Separate bug |
| `185506` | 503–520 | **94°** | gross pursuit; targetBank +72°↔−58° over ~6 s, `yawWeak` osc 0.1↔0.95 |
| `185821` | 518–559 | 5.6° | targetBank square-waves 0→60→0, ~3 s period; coordPull-vs-elevation fight |

All 5 runs had **zero human stick input** — the wobble is 100% controller-generated.

## Root cause (4 of 5 runs): the bank outer-loop is undamped and over-commands at speed

`ChaseController.Apply`, bank command (`WTMouseAimPlugin.cs` ~1416–1421):

```
omegaDes  = (|azErr|−0.5) · k · Deg2Rad         // k = AssistTurnRateGain = 1.5  — PURE PROPORTIONAL
bankTR    = atan(omegaDes · V / g)              // V amplifies the bank
targetBank= lerp(linBank, bankTR, yawWeak·(1−bigTurn))
```

- **Linear in error AND speed:** a **3° azimuth error at 520 m/s commands 72° of bank** (hits
  `MaxBankAngle`). `atan(2.5·0.01745·1.5·520/9.81) = 74° → cap`. Far more bank than 3° needs.
- **No derivative term.** Pitch has the anti-overshoot brake (`pitchRate·pitchDamp`, ~1429); roll has
  rate damping (`rollRateF·RollDamping`, ~1520); the azimuth→bank loop has **none**.
- The limit cycle: 3° error → 72° loaded bank → nose slews fast → azErr overshoots zero → `bankTR`→0
  → targetBank collapses → roll-out lags (momentum) → overshoot → re-bank. ~0.3–0.5 Hz, decaying only
  as speed bleeds off.
- This airframe exposes it because dead yaw pins `yawWeak` high (0.5–0.9) → `bankBlend` ≈ full → the
  turn-rate law runs at full authority with nothing gentler diluting it. The 385–417 m/s test planes
  had some residual yaw + slower slew, so the overshoot stayed sub-critical.
- DIFFERENT from the v0.31 RollRateSmoothing fix (that was a 6–7 Hz *inner* rate cycle; this is the
  slow *outer* command loop).

## Secondary contributors

- **coordPull vs. elevation fight** (~1467–1472): the nose-up coordinating pull fires while the nose
  is already high (`elevErr` < 0), keeping `off` elevated → feeds the bank demand each cycle.
- **phi ±180 wrap** (185455 only): `eAlign = phi/90` (~1503) is singular at ±180; a large acquisition
  error with the marker behind rolls through it → 144° sweep.

## Planned fix (v0.38) — add rate damping to the bank command (PD loop)

Reduce `omegaDes` by the nose's **low-passed** azimuth slew rate so the loaded turn decelerates onto
the marker instead of overshooting:

```
omegaDes = azTR·Deg2Rad·AssistTurnRateGain − _azRateFilt·AssistTurnRateDamp
```

- Source rate: `yawRate` (~1305, +: nose right, ≈rad/s). **Must low-pass** it (reuse the
  `_rollRateFilt` LPF pattern — raw high-Q rate feedback flipped to driving in v0.31).
- New knob `AssistTurnRateDamp` (Control, ~0–2, default TBD); add to `Cfg.LogSnapshot()` (~577).
- Self-scaling: full authority on a large error with the nose still → keeps the v0.37 bulk-turn win;
  backs off when the nose slews fast → kills the overshoot. Should also calm the `yawWeak` swing.
- Optional secondaries: gate `coordPull` down when `elevErr` shows nose-high; add `phi`-wrap hysteresis.
- Bump `PluginVersion` 0.37.1 → 0.38.0; refresh the `Awake` load-line.

Verify on a 500+ m/s dead-yaw jet: `targetBank` no longer square-waves 0↔cap; `bank`/`azErr` settle
without the ~3 s limit cycle; bulk turn still reaches steep loaded bank. Re-fly the slow plane as the
stock control case. Ship via `release.ps1` (user runs it).
