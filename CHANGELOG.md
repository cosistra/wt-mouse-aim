# Changelog

All notable changes to WT Mouse Aim. Versions are the `PluginVersion` in `WTMouseAimPlugin.cs`
(the single source of truth); each release is published via `release.ps1`.

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
