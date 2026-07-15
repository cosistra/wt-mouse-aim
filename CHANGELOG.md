# Changelog

All notable changes to WT Mouse Aim. Versions are the `PluginVersion` in `WTMouseAimPlugin.cs`
(the single source of truth); each release is published via `release.ps1`.

## 0.58.0

- **Rotorcraft stabilization — the heli wobble fix.** The UH-90's ~1 Hz forward-flight pitch
  buzz and the RAH-72's ~1.2 Hz hover "sideways wobble" (Discord reports) are one disease: the
  mod's fixed-wing error→stick gains ran an outer loop at 10–15 s⁻¹ around the game's own
  `HeloFlyByWire` — a competent 3-axis **rate-command** PID with ~0.3 s lag (fitted from the
  recordings at corr 0.88–0.97) — guaranteeing a ~1 Hz limit cycle on any heli, in *both* laws
  (a user A/B'd to Legacy and the buzz was identical). Fix is the v0.55 fixed-wing pattern,
  helo edition: a new fail-soft probe reads the private `heloFlyByWire` params (Enabled,
  gLimit, maxAngularVel) and pitch/yaw become normalized rate commands
  (`stick = 2.0·err/ωmax`, ~55° phase margin), auto-adapting to modded helis; `FineGainBoost`
  no longer applies to collective airframes (it peaked the gain exactly at boresight).
- **VTOL/heli regime from what the aircraft is, not a speed guess.** `heliBlend` is now driven
  by the live tilt/nozzle angle where the airframe exposes one (`TiltWingController` wing
  angle, `SwivelDuctSystem` nozzle angle — the higher of tilt-fraction and speed-blend wins);
  `HeliForwardSpeed` default 150→**60** m/s and `HeliHoverSpeed` 40→**20** (the game's own helo
  yaw weathervane fades in at 40–60 m/s — above that, yaw commands sideslip the game actively
  fights; 150 kept a *cruising* UH-90 40% in hover regime, the mushy/skiddy-turn complaint).
  `CompoundHeloController` (thruster heli) presence is detected and logged.
- **Rotorcraft always fly EvolvedLegacy.** Every bit of heli handling lives in that law;
  switching to Legacy silently dropped all of it (that user's A/B). `ControlLawMode` is now
  ignored for collective airframes (logged once); fixed-wing selection untouched.
- **Heading deprojection — the vertical-zoom roll oscillation fix.** The one FAIL of the v57
  round (growing 0.46 Hz roll/azimuth cycle at 250–280 m/s) was not a gain problem: the nose
  was ~82° up in a zoom climb, where horizontal-plane `azErr` inflates by 1/cos(pitch) —
  a real sub-degree offset read as ±9.5° (a sibling capture at ~89° read ±170°!) and the
  V-scaled bank map chased the phantom rail-to-rail. The bank-path errors (`azBank`,
  `azErrPred`) are now multiplied by cos(pitch): exact at level flight (clean files 0.99–1.00,
  replay ×1.00), zeroing bank authority only where heading is genuinely meaningless — pitch/yaw
  fly body-frame errors and still close the capture. Replay: phantom bank-target swing cut
  ×7–12 on the two pathological files, ≤0.5° change on every clean/big-turn file.
- **Detector fixes.** `overstress` now requires real load (g > 2.5) and valid geometry
  (|AoA| ≤ 90°, speed > 100 m/s) for the alpha branch — the v57 session's 41 lines (all from
  one unloaded 1 g departure, "AoA −176°" at 21 m/s) are all suppressed, the genuine v56
  9.7 g events all still fire. New `az-limit-cycle` anomaly: azErr sign-flipping with a rising
  envelope + active roll stick in the fine cone — the signature that logged zero lines in the
  v57 FAIL file (fires at t=380 on that recording, 4 s before the stick railed; silent on all
  54 other sample files). `[canard]` now logs unconditionally with a log-only child-scan
  (`field=`/`childScan=`), to settle why one session's KR-67 carries the canard controller and
  another's doesn't — binding stays field-only on purpose: the game guards its remap on the
  same field, so a null field means no remap exists to invert.
- Knob delta: **0 new Cfg entries**; two default changes (`HeliForwardSpeed` 150→60,
  `HeliHoverSpeed` 40→20 — F1-reset or delete the cfg line to pick them up on an existing
  install).

## 0.57.0

- **KR-67 Ifrit canard linearization — the straight-line buzz fix.** The 47-recording v0.56 fleet
  round (8 airframes) confirmed the user-reported tiny straight-line oscillation on the Ifrit: a
  ~5.3 Hz pitch buzz (stick sign-flipping on 60–70% of samples, g wiggling ±0.4) active on up to
  82% of a recording, both assist states, Ifrit-exclusive, present since at least v0.53. Root
  cause (decompiled `Aircraft.FilterInputs`): the Ifrit is the one airframe with a
  `RelaxedStabilityController`, which **replaces** the pitch stick with
  `Lerp(AoA/canardRange, stick, |stick|)` before the FBW sees it — small inputs act
  quadratically (0.05 stick delivers 0.0025) and the response is locally **reversed** for
  `0 < stick < a/2`, a textbook deadzone/relay limit cycle around boresight. The mod now probes
  the component (fail-soft, like the FBW probe) and inverts the remap closed-form, so the FBW
  receives exactly the pitch the control law intended. Identity on every other airframe; on the
  Ifrit this also un-warps the whole mid-stick response (half stick used to deliver a quarter).
- **Predictive AoA gate — the assist-off anti-pump.** The reactive v0.55/0.56 gate is a relay on
  a hard pull: AoA blows 1.3–2.5× past the ceiling before the fade bites (Trainer 20.4° on an
  8.5° ceiling, growing; CAS1 dragged to 9.7 g on a 6 g airframe for 16 s), the gate slams shut,
  AoA falls, full pull re-engages — a ~0.7 Hz buck cycle. The gate now closes on **predicted**
  AoA (`AoA + max(0, rate)·0.30 s` toward each ceiling) but reopens on the real AoA — the
  asymmetric lead is hysteresis, which is what actually breaks a relay.
- **Slewed big-turn roll alignment.** Third member of the v0.53 raw-error→roll relay family: when
  the target crosses dead-astern, `phi` flips sign in one tick and `eAlign` followed it
  rail-to-rail (0.86–0.98 Hz roll-stick chatter in the FS-12/CI-22/KR-67 scissors captures),
  bypassing both the v0.53 fine-cone deadzone and the v0.54 bank-target slew. `eAlign` is now
  slew-limited at 3/s (full reversal ~1 s) — the chatter needs ~5.5/s to sustain, a genuine
  over-the-top sweep barely notices.
- **Overstress anomaly line.** The 9.7 g / 22° AoA assist-off episodes produced **zero**
  `[anomaly]` lines — every detector watched stick patterns, none watched the airframe. New
  `overstress` anomaly fires when g/AoA stay past the airframe's own FBW limits for 0.5 s.
- **Analyzer:** high-frequency pitch-buzz detector (the buzz was invisible to the episode
  detector — the v56 Ifrit files PASSed while buzzing 82% of the time); AoA-pump FAIL verdicts
  with limiter-relative thresholds (pp 20 on a 27° limiter is honest maneuvering, on a 10°
  Trainer it's a blow-through cycle); AoA/g overstress WARN lines against the `# fbw` header
  (which now also records `canardRange`); WARN verdict text generalized.
- Knob delta: **0 new Cfg entries** (canard inversion is probe-driven; gate lead / eAlign slew
  are `ponytail:` constants).

## 0.56.0

- **Q-scheduled pitch gain — the takeoff-oscillation fix.** The 31-recording v0.55 test round
  (FS-12 + Trainer) caught a 100% mod-driven 0.55 Hz pitch limit cycle below corner speed that
  needs ~30–55 s of uninterrupted tracking to ratchet up (bank 18°→85°, g 0.7→7.5, AoA −29..+37°)
  and then departs — which is why short low-speed tests never reproduced it; takeoff climb-out
  supplied the window. Phase forensics (WOBBLE-FINDINGS.md UPDATE 6): the mod's P-response to the
  aim error is instant (+0.13 s), but the *achieved* pitch rate lags the command by >1 s at
  113 m/s — the low-q plant supplies essentially all the loop phase, and the outer P gain (tuned
  against the fast high-q plant) is too hot there. The pitch demand terms (error P + coordPull)
  are now scaled by the game's own q clamp (`clamp(q_ratio, 0.3, 1)`, ≡ 1 at/above corner speed —
  high-speed feel untouched), leaving the rate-damping term unscaled so damping is relatively ~3×
  stronger exactly where the plant is slow. One mechanism, both assist regimes; big errors still
  rail the stick, so max-performance pulls keep full authority.
- **Relative AoA ceiling margins.** The fixed −4°/6° margin+fade collapsed on low-limit airframes:
  the Trainer's 10° `alphaLimiter` gave a 6° ceiling with the fade starting at **0° AoA** —
  measured 60–90% of pitch authority cut at completely ordinary 3–5° turning AoA. Margins are now
  proportional (`min(4°, 0.15·lim)` margin, `min(6°, 0.25·lim)` fade): Trainer gets full authority
  below 6° AoA (ceiling 8.5°); the FS-12 (27°) keeps exactly the old 4°/6°.
- **Assist-OFF is now a performance mode** (v0.55's assist-off pitch normalization DELETED). The
  v55 sweep showed the normalization (×0.32–0.5 below corner speed) was the single biggest
  mod-side restriction — ~15% of demanding assist-off samples held back at safe AoA — and the
  airframe itself tracks its commanded rate at r 0.85–0.99 (the plane was never the bottleneck).
  With assist OFF the game's raw law now passes through at full command, guarded by the AoA
  ceiling + the q schedule (both assist-independent). High-speed assist parity needs no mod-side
  scaling — the game runs the identical protected law above ~1.2× corner-q itself.
- **Analyzer: FAIL/WARN verdict split + guard attribution.** Rail-only evidence (roll stick
  railed, any speed) is now a WARN — the v55 captures showed plain railing is usually a benign
  max-performance reversal; FAIL requires dynamic evidence (oscillation episode / growing azErr /
  AoA blow-through). The digest derives per-segment `sched min` / `pitch gated %` from the
  existing columns + `# fbw` header (no new CSV columns needed — the guards are pure functions of
  what's already recorded). Selftest coverage for both. Config knob delta: **0**.

## 0.55.0

- **Low-speed pitch oscillation fixed + stability-assist parity — the Draken round-2 report**
  (compounding oscillation below ~450 km/h on Ifrit/Compass/Revoker that could crash the plane,
  and stability-assist OFF turning ~3× slower). A fresh decompile of the game's
  `ControlsFilter.FlyByWire.Filter` plus an offline fit on the 11 tester recordings nailed both
  root causes (see WOBBLE-FINDINGS.md UPDATE 5):
  - **FBW probe (new)**: the mod now reads each airframe's fly-by-wire parameters from the game
    at runtime (cornerSpeed/maxPitchAngularVel via the public API; gLimitPositive/alphaLimiter
    via reflection, everything fail-soft — helicopters and FBW-less airframes keep the old
    behaviour) and reconstructs the game's own stick→pitch-rate gain per tick.
  - **`AssistOffPitchScale` REMOVED** (knob deleted): the decompiled law shows assist-off changes
    *nothing* above ~1.2× corner-speed dynamic pressure, so v0.51's flat 0.5 cut was simply
    halving high-speed assist-off turns (the "3× slower"). Replaced by an exact per-airframe,
    per-speed normalization (protected/achievable rate ratio) that is ≡ 1 with assist on or at
    speed — the verified high-speed feel is byte-identical — and the physically-correct cut with
    assist off at low q.
  - **Achievability cap**: the turn-rate demand ωdes is capped at the achievable pitch rate (in
    both bank-target sites, shared pre-compute + EvolvedLegacy), so at low speed the bank target
    shrinks physically instead of slamming ±72° into a turn the collapsed pitch rate can't fly;
    the fine integrators freeze while capped (anti-windup — the offline fit showed
    corr(command, response) going *negative* below corner speed: the plane had stopped following
    while the loop kept winding).
  - **Mod-side AoA ceiling**: the pitch command driving AoA past the game's per-airframe
    `alphaLimiter` − 4° is scaled out (sign-aware, so recovery is never blocked), active
    regardless of the assist state — assist-OFF now gets the AoA protection the game withholds.
- **G-LOC fade-to-black warning**: a gradual full-screen grey-out driven by the same
  `pilotStrength` signal as the amber OVER-G text, so third-person pilots (who get none of the
  game's cockpit-only black-out) see G-LOC coming instead of an instant control cut. New knobs
  `GLocFadeEnabled` / `GLocFadeOnset` (0.4) / `GLocFadeMaxAlpha` (0.7).
- **Instrumentation**: recordings gain `assist`/`fbwTgtPR`/`fbwPR` columns and a `# fbw`
  per-airframe params header line; `analyze-wobble.py` gains a per-file stick→rate model fit
  (high-q/low-q split at the airframe's corner speed) and a named `low-speed stall oscillation`
  FAIL verdict (stall blow-through / growing azErr / low-speed roll-rail), with `--selftest`
  coverage. Config knob delta: −1.

## 0.54.0

- **De-rectified turn lead + slew-limited bank target — the ~1.5 Hz wing rock and the
  "self-leveling fights the turn" drift.** Nineteen v0.53 recordings (KR67 EFRET 450–536 m/s, AB4
  Alcyon 226–518 m/s) verified the v0.53 deadzone (the eAlign relay is dead — `outR` no longer
  tracks `sign(azErr)` anywhere) but exposed the next loop underneath: the v0.52 brake-clamp is a
  **rectifier**. Bank oscillation ripples the filtered heading rate ±3°/s; `azErrPred =
  clamp(azErr − hRF·leadT, [0, azErr])` therefore slams between exactly 0 and full `azErr` every
  half-cycle, and the ~44°-bank-per-degree atan slope at 500 m/s amplifies that sawtooth into a
  bank target banging 0↔48–65° at ~1.5 Hz from a 1–3° error that never changes sign. The roll
  servo chased it faithfully (corr `outR` vs `bankTR−bank` = 0.79–0.96) — wings rocking ±14–30°.
  The slow 0.5 Hz big-turn cycle is the same rectification at scale: the prediction pinned to 0
  while 1.5–5.7° of real error remained, commanding full wings-level mid-correction (the
  user-reported self-leveling drift), sustained by the bank overshooting the collapsing target by
  15–20°. Three fixes in the same pipeline:
  - **Proportional floor on the brake-clamp**: `azErrPred` now floors at `0.30·azErr` instead of
    0 — early rollout (the lead's job) still happens, but level flight is never commanded while
    real error remains; the floor self-releases as `azErr → 0`.
  - **`hrTau` 0.18 → 0.35** (hardcoded): ~2× more attenuation of the 1.3–1.5 Hz ripple feeding the
    rectifier, at a cost of ~0.2 s of rollout timing.
  - **New knob `BankSlewRate` (default 60°/s, 0 = off)**: rate-limits EvolvedLegacy's bank target
    so it physically can't flap above the airframe's own roll response; also shrinks the servo
    overshoot that sustained the slow cycle. Applied before `coordPull` so the pull sizes off the
    bank actually commanded.
- **New CSV column `tBankE`** — the bank target EvolvedLegacy's roll servo *actually* flies
  (slew-limited). The existing `targetBank` column is the shared yawWeak-gated blend, which this
  law does not fly; reading it produced two red herrings in the v0.53 analysis.
- Analysis: `WOBBLE-FINDINGS.md` UPDATE 4. The "yaws instead of banking" report was refuted for
  large diagonal snaps (roll rails within ~120 ms, yaw never exceeds 0.46 across all 19 files) —
  the real small-error mechanism was the flapping bank target never *holding* a bank.

## 0.53.0

- **Fine-cone deadzone on the align-hold roll weight — kills the KR67-class 570 m/s wing-rock.**
  Twelve v0.52 recordings (KR67 EFRET at 480–588 m/s + Trainer) confirmed the v0.52 clamp works —
  `targetBank` stays inside ±3° while station-keeping — yet the wings still rocked ±20–33° with the
  roll stick flipping at ~1.2 Hz (worst file: 14 s of sustained chatter). The driver was a **second,
  unguarded azimuth→roll path**: near boresight `phi` snaps between ±90° with the *sign* of a
  sub-degree error, making `eAlign = phi/90` a full-scale directional relay, and the v0.42
  align-hold blend weight (`|azErr| / EvolvedAlignHoldDeg`) fed it with **raw** error — ±0.2 of
  roll stick per degree, no lead, no deadzone, bypassing the entire atan/lead/clamp bank pipeline.
  At 570+ m/s roll authority that loop self-sustains. Fix: the blend weight now subtracts
  `FineBankDeadzone` (2.5°) from `|azErr|` first — the exact guard the linear bank servo has had
  since v0.29 (`azBank`). Inside the fine cone the roll servo is purely the wings-leveler + the
  braked/clamped `tBankE`; big turns unchanged (`bigTurn` still dominates the blend), medium
  errors reach full align weight at ~7.5° instead of 5°.

## 0.52.0

- **Brake-only lead — fixes the fast chatter the v0.51 lead introduced.** Sixteen v0.51 recordings
  (Ifrit + Compass, 108–508 m/s) showed the old slow 0.3–0.85 Hz wobble genuinely gone, but a NEW
  ~1.1–1.35 Hz bank/roll-stick chatter appeared while station-keeping (HOLD phase, aim error under
  ~2°), from ~280 m/s up. Cause (confirmed by phase analysis): near boresight the v0.51 prediction
  `azErr − headingRate·TurnLeadTime` was **dominated by the heading-rate term** (2.1–2.7× the real
  error), and the speed-scaled bank slope (~44° of bank per degree of error at 470 m/s) turned
  ±2°/s of nose-rate ripple into a ±65° bank relay — the lead had closed its own faster loop
  through the roll actuator (heading rate measurably *led* bank, the causal smoking gun). Fix:
  `azErrPred` is now **clamped to [0, azErr]** — the lead may shrink the error toward zero (the
  early rollout that killed the slow wobble) but can never flip its sign or exceed it, so the
  commanded bank is always bounded by the *real* aim error and the chatter loop can't self-sustain.
  Genuine big turns are unchanged (the clamp only engages when the rate term outruns the error);
  offline replay of the 16 recordings shows the chatter files' mean bank command dropping ~60–90%
  with the deliberate-turn files byte-identical. Side benefit: `TurnLeadTime` is now safe to raise
  (more anticipation can only advance the rollout, not feed the relay).
- `debugtests/analyze-wobble.py`: FAIL band widened 0.3–0.9 → 0.25–2.0 Hz (it was passing the new
  fast mode) + a roll-stick chatter criterion (≥0.8 Hz rail-to-rail). Note: recordings from this
  session were 5 Hz — check `Recorder/RecordRateHz` (should be 20) for better diagnosis data.

## 0.51.0

- **THE death-wobble fix: anticipatory lead on the turn-rate bank command.** Ten user recordings
  (Kryrins, Draken — thanks!) pinned the reported fixed-wing "death wobble" to a single mechanism:
  the `atan(ω·V/g)` azimuth→bank command was **pure proportional** in the heading error while the
  achieved bank **lags that command by a constant ~0.7 s** (measured by cross-correlation,
  identical across airframes) — at the observed 0.3–0.85 Hz oscillation that lag is ~90–180° of
  phase, so the loop self-sustained a limit cycle: ±88° of bank from a ±6° aim error, roll stick
  railed for 47 s straight, **at every speed tested (70–390 m/s)** — speed only set the violence.
  The bank target is now computed from the *predicted* heading error
  `azErrPred = azErr − noseHeadingRate·TurnLeadTime` (new knob, default **0.65 s**, just under the
  measured lag; `0` = old behaviour), so the bank rolls out *early* — including a brief
  anticipatory counter-bank that brakes the turn — instead of after the overshoot. Nose-only rate
  (marker-independent), so the lead can never fight a mouse flick. Applied to both copies of the
  turn-rate bank math (shared pre-compute + EvolvedLegacy); the linear low-speed servo and the
  coordinating-pull release taper deliberately keep the *raw* error (release timing must track
  real arrival). This is the v0.38 fix `WOBBLE-FINDINGS.md` planned and never shipped.
- **Assist-off pitch guard.** With the game's own flight-assist (AoA limiter) OFF, the game FBW's
  stick→pitch-rate gain roughly doubles-triples (decompiled `ControlsFilter.FlyByWire`), and the
  mod's fixed pitch gains then diverged (recorded FS-12: elevator railed, AoA −29°→+52°). New knob
  `AssistOffPitchScale` (default **0.5**, `1` = old behaviour) flatly scales the instructor's pitch
  command while flight-assist is off. A rough compensating cut, not a per-airframe FBW inversion
  (that's deferred — see WOBBLE-FINDINGS).
- **Instrumentation:** recorder CSVs gain trailing `headingRateFilt,azErrPred` columns; the config
  snapshot gains `leadT=`/`aOffP=`; the `[chase]` trace logs `azPred=`. New
  `debugtests/analyze-wobble.py` (stdlib-only) scores any recording for the wobble signature
  (episodes, frequencies, rail %, the 0.7 s lag) — it flags the two violent baseline recordings
  FAIL and the mild one PASS.
- Known remaining (parked, in `WOBBLE-FINDINGS.md`): helicopter sideways wobble (distinct ~1.15 Hz
  yaw-loop cycle at hover), full per-airframe FBW pitch-gain inversion, motion-profile shaping.

## 0.44.0

- **Recordings are now self-describing.** Each maneuver-recorder CSV (`F8`) starts with a `#` comment
  header block carrying the plugin version, the session id, the wallclock + `Time.time` start, the
  aircraft, and the **full control-law gain set** (the same dump the startup `[config]` line emits, via
  a shared `Cfg.SnapshotString()`). Any setting changed live (F1) *during* a recording is appended as a
  `# cfg t=… Section/Key = value` row, so a feel change is inline with the data — you can debug a run
  from the CSV alone without cross-referencing the log.
- **New diagnostic CSV columns:** `rollRateF` (the filtered roll rate that feeds the damping term — the
  key signal for high-speed roll-PIO/wobble, previously only in the anomaly trail), `iPitch`/`iYaw`
  (fine-integrator state), and `bankTR`/`bankBlend` (the EvolvedLegacy `atan(ωV/g)` commanded bank and
  its blend weight).
- **Anomalies get their own file.** A dedicated, session-scoped `mouseaim-anomalies-<session>.log` (next
  to `LogOutput.log`) collects only the `[anomaly]`/`[anomaly:trail]` lines, separated from the noisy
  shared BepInEx log. Each anomaly is tagged with the active **control law** and, when a recording is
  running, the **CSV it belongs to** (`rec=…`); a session id ties the anomaly file, every recording, and
  the BepInEx config log together. The on-screen flash and the BepInEx warning still fire as before.
- **Less log spam / lower context.** Dropped the full gain snapshot that every `[anomaly]` line repeated
  (gains are already logged once at startup + on each change, and embedded in each recording header).
  Halved the verbose `DebugLogging` trace cadences (`[chase]` ~5→2.5/sec, fine-capture ~10→5/sec;
  `[aim]` and `[orbitcam]` likewise) so a debug run stays readable without losing shape. The recorder
  rate stays 20 Hz (`Recorder/RecordRateHz` is the knob if files get large).

## 0.43.0

- **Regime-aware hover handling for EvolvedLegacy.** On collective aircraft (helicopters / hover-VTOLs,
  `takeoffDistance == 0`) the `atan(ωV/g)` bank-to-turn law degenerates at low forward speed — it lays
  the aircraft over without slewing the nose. EvolvedLegacy now ramps from bank-to-turn to *yaw-to-point*
  as forward speed (`vFwd`, the nose-direction velocity component) drops between new knobs
  `Control/HeliForwardSpeed` (60 m/s, full fixed-wing) and `Control/HeliHoverSpeed` (20 m/s, full hover),
  via a per-frame blend `heliBlend`. In hover the commanded bank is suppressed (the roll axis becomes a
  wings-leveler) and yaw authority is raised by `Control/HeliYawScale` (2.0) so the tail rotor points the
  nose. Forced fully on whenever the game's AutoHover is engaged. Fixed-wing airframes are unaffected
  (`heliBlend == 0`, byte-identical to 0.42). New recorder columns `heliBlend`/`vFwd`; `[seam]` logs the
  collective/AutoHover flags.

## 0.33.0

- **Fixed the high-speed roll buzz for real — by cutting the damping, not adding more.** Restoring
  full roll authority in 0.32 brought back a violent roll PIO at high dynamic pressure (logs: roll
  stick dithering ±0.45 at ~3 Hz on-heading, bank overshooting its target to ±40° rolling out). The
  driver is the roll-*damping* term itself: with the wings level the roll command is essentially
  `−rollRate · RollDamping · RollGain`, and that delayed rate feedback flips from damping to driving
  the cycle — so raising `RollDamping` (the old "fix overshoot" advice) made it *worse*. New defaults
  **`RollDamping` 0.6 → 0.1** and **`RollGain` 1.3 → 1.0** drop the loop gain below the limit-cycle
  threshold: the buzz is gone and the wings hold steady at speed, while fast rolls stay crisp. (Both
  remain live-tunable; 0 damping is a touch jittery on-heading, hence 0.1.)
- **Controls every airframe now, not just fixed-wing.** Helicopters and hover-VTOLs (collective
  aircraft, flagged by `takeoffDistance == 0`) fly off the same chase law — they drive the same
  pitch/roll/yaw (cyclic + tail rotor); collective stays on your throttle, untouched. New
  `Control/ControlRotorcraft` (default on) opts them out if you'd rather keep rotorcraft on stock
  controls.
- **Master ON/OFF hotkey (`General/ToggleKey`, default F10).** Flip the whole mod on/off in flight
  without opening the menu; a brief on-screen toast confirms the change.
- **Clean reticle-only HUD by default.** The diagnostic text readouts (status line, live
  pitch/yaw/roll, anomaly flash, phase) are now hidden behind `HUD/ShowDebugHud` (default off). Out
  of the box you see just the reticle, the airframe marker, the FLY LEVEL banner, and the G-LOC
  warning — turn `ShowDebugHud` on for tuning.

## 0.32.0

- **Restored high-speed roll authority.** Removed the v0.30 dynamic-pressure roll gain schedule
  (`RollGainRefSpeed` / `RollGainSpeedExp` / `RollGainMinScale` and the `qScale` term) entirely. It
  never measurably reduced the high-speed roll wobble — that was a rate-feedback limit cycle, fixed
  in 0.31 — and it was silently cutting roll authority by up to ~65% at speed. The plane now keeps
  full roll authority across the envelope.
- **Anomaly logging is suspended while you're on the stick.** When any axis is manually engaged
  (and through the ease-back window after release), the `[anomaly]` detectors no longer fire on the
  attitude/rates *you* are driving. The trail ring buffer keeps filling, so a genuine anomaly right
  after hand-back still has its pre-frames.

## 0.31.0

- **Fixed the high-speed roll wobble** (the felt one). It's a derivative-feedback limit cycle: level
  on-heading the roll command is essentially `-rollRate · RollDamping`, and `rollRate` is a one-frame
  finite difference; at high dynamic pressure that delayed feedback flips from damping to driving at
  ~6–7 Hz. Added a first-order low-pass on the roll rate feeding the damping term
  (`Control/RollRateSmoothing`, default 0.06 s) — rolls off the high-frequency content so the damping
  only opposes real, low-frequency roll motion. Kills the wobble without touching steering authority.

## 0.30.0 *(superseded by 0.31/0.32 — never the active fix)*

- Experimental dynamic-pressure roll gain schedule, on the theory the wobble was a proportional-gain
  instability. Flight logs showed cutting the gain to 0.35× left the wobble amplitude unchanged,
  disproving that theory. Mechanism removed in 0.32.0.

## 0.29.0

- **Fixed the mid-speed (fine-cone) roll wobble**: a soft azimuth deadband on the bank servo
  (`Control/FineBankDeadzone`). Inside a few degrees of heading error the wings just level and yaw
  alone does the final capture, instead of the bank servo amplifying a sub-degree heading hunt into a
  continuous roll-stick dither.
