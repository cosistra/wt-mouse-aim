# Changelog

All notable changes to WT Mouse Aim. Versions are the `PluginVersion` in `WTMouseAimPlugin.cs`
(the single source of truth); each release is published via `release.ps1`.

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
