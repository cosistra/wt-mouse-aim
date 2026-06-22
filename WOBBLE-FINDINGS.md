# High-speed bank-loop wobble — diagnosis (branch `wobble-fix`, 2026-06-21)

Checkpoint of the v0.35→v0.37.1 control-law work plus the diagnosis of a **high-speed wobble**.
The v0.38 fix is **not yet implemented** — this doc is the resume context for it.

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
