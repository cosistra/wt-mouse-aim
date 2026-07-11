# High-speed bank-loop wobble — diagnosis (branch `wobble-fix`, 2026-06-21)

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
