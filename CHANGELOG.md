# Changelog

All notable changes to WT Mouse Aim. Versions are the `PluginVersion` in `WTMouseAimPlugin.cs`
(the single source of truth); each release is published via `release.ps1`.

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
