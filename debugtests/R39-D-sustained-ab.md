# R39-D — the sustained-turn pair: the floor is finally cleared, the feed-forward is vindicated, the lead term is not, and SLACK cannot fire here by kinematics, v0.98.1

Two cards of R39's five-card batch queue, each its own single-card fleet, 8 fixed-wing lanes,
`repeat: 8`, absolute 250 m/s at 4000 m, `sweep-slow` geometry. **121 captures, 119 scorable, 2 aborts
(both Darkreach, both `detached ratio 0.029`).** Session `20260802-083849`, one session, `n_cols = 69`
uniform, **zero parse warnings**, datum `(0, -4032, 0)` constant on every sampled row of both cards.

| | `e3-marker-ff` | `e2-rel-turn-lead` |
|---|---|---|
| lever (`armToggle`) | `Control/MarkerRateFeedForward` | `Control/RelativeTurnLead` |
| `rec` | 129–189 | 251–310 |
| wall clock | 08:59:14 → 09:04:54 | 09:11:42 → 09:17:23 |
| captures / scorable | 61 / **60** | 60 / **59** |
| segment tag → type | `turn360mff` → **`sustained_turn`** | `turn360rtl` → **`sustained_turn`** |
| railed / slack / unknown | 0 / 0 / 0 | 0 / 0 / 0 |
| arm 0 flew | `mrFF=0 relLead=1` | `mrFF=1 relLead=0` |
| arm 1 flew | `mrFF=1 relLead=1` | `mrFF=1 relLead=1` |

`arm` / `arm_knob` non-NULL on all 121. ABBA balanced 4/4 per lane on the seven full lanes.
**Card demand verified from the JSON, not assumed:** `trackAz` is 2001 samples spanning exactly
**180.00° over 40.0 s = 4.500 °/s**, `trackEl` identically 0 — a pure sustained azimuth sweep.
Measured `aimRate` 4.4520–4.4644 °/s. (The runner's own `[card] … start` line reports
*"Derived sweep rate 12.1 deg/s"* — wrong by 2.7×, cosmetic, backlog #55f.)

---

## Verdict

1. **The standing error cleared the resolution floor by 36×–115×. This is the corpus's first
   above-floor steady-state pointing measurement and it is not close.** `offFloorPct` = **0.000–0.059%**
   per lane against R37's 74% of legs at the floor; `azErr` reads exactly 0.0000 on **0.000–0.059%** of
   rows against R37's 44.6% of settle-window ticks. `fixedWindowOffDeg` non-NULL on **121/121** segments,
   zero `skipped` entries. §2.
2. **The predicted ~3.5° standing lag is confirmed, on the arm where it was predicted.** With
   `MarkerRateFeedForward` OFF the settled `|azErr|` is **3.38–4.16°** on 7 of 8 lanes against
   rate/K = 4.5/1.28 = **3.52°**. And it is genuinely *standing*: mean `|azErr|` by 10 s decade is flat
   from 10 s (Fighter1 arm 0: 3.399 / 3.437 / 3.439). §2.
3. **`MarkerRateFeedForward` is worth 55–58% of the standing azimuth error on 7 of 8 lanes** —
   `fixedWindowOffDeg` 5.08–6.38 → 1.46–2.18, `rmsPointingErrorDeg` down 8/8, effect/replicate-SD −10 to
   −960. `aimRate` is non-zero and identical on both arms (4.4596 / 4.4546), so this is the law consuming
   the marker rate, not the stimulus changing. §4.
4. **GENERALITY-REVIEW finding 16 is half true and its inference is refuted.** The feed-forward really
   does deliver **0.0068–0.0109 of roll stick on BOTH arms** — no roll-stick difference at all — and it
   does so with `bWt` at 0.000–0.040, i.e. completely OFF the `lateralHold` rail this time. It delivers
   through **target bank**: `bankTR` 53.0–64.9° → 68.4–75.1°, achieved bank 51.6–65.6° → 65.9–69.4°.
   Roll stick is the servo term that *holds* a trimmed bank; it is the wrong observable for a
   feed-forward that moves the bank target. The OFF arm makes up the deficit in **yaw** (mean `|outY|`
   0.090–0.208 vs 0.031–0.105) — it skids. §4a.
5. **`RelativeTurnLead` FAILS its own declared criterion: `leadDeg` separates 38×, the standing error
   does not move.** `|leadDeg|` 2.845–2.848° OFF → 0.075–0.262° ON; mean `|azErr|` moves −0.2% to −3.8%,
   which is **inside the harness's own null contrast** (§6a). §5.
6. **The v0.83 clamp question is answered and the answer is "it swapped rails".** The predFloor clamp
   went 95.1–99.3% (OFF) → 55.3–64.7% (ON) — off the rail v0.83 measured. It landed on
   **`MaxBankAngle`**: `bankTR ≥ 72°` on **41–100% of settled samples** on Fighter1 / Multirole1 /
   SmallFighter1 / FastBomber1 / Darkreach. A 3.33× larger error term buys **+0.6 to +2.0°** of
   commanded bank when the command is already at the wall. §5.
7. **SLACK: 0 of 121, and none of the three branches describes what happened.** The letter is PASS
   (0% is inside 0–20%), the substance is **NO INFORMATION**: `authorityUsedFrac == authBank` in all 32
   (card, lane, arm) cells, and `authBank = mean|bank|/72` is fixed by kinematics — φ = atan(ω·V/g) gives
   **0.866–0.986 before any law runs**. Across all 51 `sustained_turn` cells ever flown, the metric is a
   monotone function of the *card's* demanded turn rate. All 8 corpus fires are on `sweep-creep`
   (1.3 °/s); R39's cards are `sweep-slow` geometry, which R27 already measured at 0.893–0.952. **The
   premise that this card could wake SLACK is false.** §3.
8. **`bankClampActivePct` — one of the four `RAIL_METRICS` — is computed from a column the shipped
   fixed-wing law does not fly.** It keys on the CSV's `targetBank`, the shared `yawWeak`-gated
   pre-compute whose `ApplyEvolvedLegacy` parameter was **deleted in v0.96**
   (`ChaseController.cs:146-147`). It reads **0.0%** on e2 Fighter1 / SmallFighter1 while `bankTR` — the
   live command — is at or over 72° on **94–100%** of their settled samples, and while
   `bankDemandExcessDeg` (correctly built on `bankTR`) reads 0.56–1.71° in the same segment.
   **`segments.railed = 0` on these 121 segments is not evidence the bank axis was free.** §7c.
9. **The 87 ms hitch was three frames, not one, and the log undercounts it by design.** The rows show
   **86.8 / 86.8 / 126.7 ms — ~300 ms of wall time, 21 rows, 7 captures.** The warning is edge-triggered
   and rate-limited to one per 0.25 s (`TestDrone.cs:472,476`), so only the leading 87 was logged.
   5 scored segments touched, 15 of 37,760 scored rows (0.040%). **Dropped; no conclusion changes.** §6.
10. **Every hitch in this batch is arm-pure, and structurally must be.** All 5 hitched e2 captures are
    arm 0; all 7 hitched e3 captures are arm 1. CLAUDE.md's v0.94 note says a fleet-wide instantaneous
    confound is safe because it "would have to hit the fleet at one instant AND correlate with lane". It
    does not: the fleet runs the same ABBA index at the same wall clock, so **the arm is synchronised
    across all 8 lanes by construction**. Harmless at 0.04% of rows here; the argument that protects it
    is wrong. §6b.
11. **Replicate 1 is 100% arm 0 in both cards** — ABBA index 0 always maps to arm 0, so 8 of 30 arm-0
    captures per card are unplaced (`snapBackM = 0`) against 0 of 30 arm-1. Tested and not driving
    anything here (§6c), but it is a permanent structural imbalance, not a property of this batch.

---

## 1. Gate, per lane per card

Requirements: `blendRailPct` < 10, mean `|azErr|` in 2.5–5°. `> 90` on the blend = RAILED (conclude
nothing); `< 0.2°` = floor-limited.

**Blend rail: PASS everywhere.** Lane-mean `blendRailPct` 0.00–0.27%; worst single segment 11.8
(Darkreach, e2). `bWt` itself averages 0.000–0.040 on the six clean lanes — the roll-to-align blend is
fully **disengaged**, the opposite of R21's 100%-latched sustained corpus. **Nothing is floor-limited**:
the smallest lane mean is 1.44°, 36× `OFF_FLOOR_DEG`.

The band, though, must be read **per arm** — the two arms are not the same plant.

### `e3-marker-ff`

| lane | n | `blendRail%` | `\|azErr\|` arm0 (FF off) | `\|azErr\|` arm1 (FF on) | pooled | band |
|---|---|---|---|---|---|---|
| Fighter1 | 8 | 0.00 | 3.270 | 1.456 | 2.363 | arm0 **in** / arm1 low |
| Multirole1 | 8 | 0.00 | 3.426 | 1.538 | 2.482 | arm0 **in** / arm1 low |
| SmallFighter1 | 8 | 0.00 | 3.508 | 1.504 | 2.506 | arm0 **in** / arm1 low |
| trainer | 8 | 0.00 | 3.224 | 1.442 | 2.333 | arm0 **in** / arm1 low |
| VTOLTrainer1 | 8 | 0.00 | 3.504 | 1.498 | 2.501 | arm0 **in** / arm1 low |
| EW1 | 8 | 0.00 | 4.047 | 1.754 | 2.901 | arm0 **in** / arm1 low |
| FastBomber1 | 8 | 0.27 | 3.818 | 1.612 | 2.715 | arm0 **in** / arm1 low |
| Darkreach | **4** | 0.00 | 4.536 | 3.248 | 3.892 | both **in**, but see §7d |

### `e2-rel-turn-lead`

| lane | n | `blendRail%` | `\|azErr\|` arm0 (lead off) | `\|azErr\|` arm1 (lead on) | pooled | band |
|---|---|---|---|---|---|---|
| Fighter1 | 8 | 0.00 | 1.464 | 1.457 | 1.461 | **low** |
| Multirole1 | 8 | 0.00 | 1.524 | 1.514 | 1.519 | **low** |
| SmallFighter1 | 8 | 0.00 | 1.534 | 1.507 | 1.520 | **low** |
| trainer | 8 | 0.00 | 1.445 | 1.442 | 1.444 | **low** |
| VTOLTrainer1 | 8 | 0.00 | 1.534 | 1.516 | 1.525 | **low** |
| EW1 | 8 | 0.00 | 1.757 | 1.690 | 1.724 | **low** |
| FastBomber1 | 8 | 0.00 | 1.590 | 1.536 | 1.563 | **low** |
| Darkreach | **3** | 0.00 | 3.101 (n=1) | 4.081 (n=2) | 3.754 | in, n=1 on arm 0 |

**Gate verdict.** PASS on `e3-marker-ff`'s OFF arm — 8 of 8 lanes land in 2.5–5° and match the 3.5°
prediction. MISSES LOW everywhere `MarkerRateFeedForward` is on, which is 15 of the 16 remaining
lane-arms. The band was sized from K ≈ 1.28 /s; measured with the feed-forward on it is
**K = 4.38 / 1.47 = 2.98 /s**, 2.3× tighter. **Nothing is RAILED on the blend and nothing is
floor-limited** — the miss is a good-news miss and the whole band should be re-derived at the
shipped-default K.

**For re-sizing the track:** achieved `meanTurnRateDegS` **4.10–4.38 °/s** against 4.500 demanded, at
**245–373 m/s** and 51.6–69.4° of bank. To put the shipped default back in a 2.5–5° band the demand
needs roughly **9 °/s** — which at these speeds is past the 72° clamp, so the entry speed has to come
down with it (see §7a).

---

## 2. The floor check — the batch's most important negative result, inverted

| card | lane | `offFloorPct` | `azErr == 0.0000` % | mean `\|azErr\|` | ×`OFF_FLOOR_DEG` |
|---|---|---|---|---|---|
| e3 | Fighter1 | 0.059 | 0.059 | 2.363 | 60 |
| e3 | SmallFighter1 | 0.020 | 0.020 | 2.506 | 63 |
| e3 | Darkreach | 0.000 | 0.000 | 3.892 | 98 |
| e2 | Fighter1 | 0.020 | 0.020 | 1.461 | 37 |
| e2 | Multirole1 | 0.020 | 0.000 | 1.519 | 38 |
| e2 | trainer | 0.020 | 0.000 | 1.444 | 36 |

Full range across all 16 lane cells: `offFloorPct` **0.000–0.059%**, exact-zero `azErr`
**0.000–0.059%**, mean `|azErr|` **1.44–4.54°** = **36×–115×** the 0.0396° floor. `fixedWindowOffDeg`
is non-NULL on all 121 segments with **zero** `skipped` entries — the first card family in the corpus
where that metric never fell under the floor.

**A sustained demand leaves a standing error, exactly as designed.** Mean `|azErr|` by `tSeg` decade:

```
                   0-10s  10-20s  20-30s  30-41s
e3 Fighter1  arm0  2.805   3.399   3.437   3.439     <- flat from 10 s: STANDING
e3 Fighter1  arm1  1.411   1.466   1.469   1.478
e3 trainer   arm0  2.732   3.396   3.381   3.386
e2 Fighter1  arm0  1.419   1.473   1.477   1.487
e2 EW1       arm0  2.453   1.567   1.497   1.512     <- slow entry, then flat
e3 FastBomber1 arm0 3.272  3.630   3.774   4.592     <- NOT flat, still growing
e3 Darkreach arm0  5.130   3.702   4.721   4.592     <- non-stationary, both arms
```

Standing on 7 of 8 lanes on both arms. Exceptions: FastBomber1 arm 0 (+26% over the last 30 s) and
Darkreach (both arms). **Refutation test:** if the arm-0 error were an unsettled integration rather
than a standing lag, the decades would climb monotonically; they do not on 14 of 16 lane-arms.

---

## 3. SLACK — the verdict, and why all three branches are the wrong question

**Facts.** Type present: 121 `sustained_turn` segments, the first since R27 and only the sixth card
family ever to produce the type. `authorityUsedFrac` populated on **121/121**. `segments.slack` = **0**.
So the literal rate is **0.0% of unrailed `turn360mff` segments**, inside the declared 0–20% PASS band.

**Why that is not PASS in any useful sense.** `authorityUsedFrac` is `max(turnRateDemandRatio, authBank,
authAoa, authStick)` and **`authBank` is the max term in all 32 cells**. `authBank = mean|bank| /
maxBank`, and for a coordinated turn the bank is fixed by kinematics: φ = atan(ω·V/g).

| lane | arm | achieved ω | mean V | **predicted** φ/72 | **measured** `authBank` |
|---|---|---|---|---|---|
| Fighter1 | 1 | 4.38 | 343.3 | 0.965 | 0.956 |
| Multirole1 | 1 | 4.38 | 373.0 | 0.986 | 0.963 |
| trainer | 0 | 4.35 | 245.3 | 0.866 | 0.716 |
| EW1 | 0 | 4.30 | 264.7 | 0.891 | 0.824 |
| VTOLTrainer1 | 0 | 4.31 | 269.3 | 0.896 | 0.821 |

Measured tracks predicted, sitting a little below it only because `mean|bank|` includes the entry ramp
from wings-level. **The law cannot make this number small without failing the card.**

**The corpus proves the same thing across every sustained turn ever flown.** `authorityUsedFrac`
against the card's demanded turn rate, all 51 cells:

| card family | ω (°/s) | `authorityUsedFrac` | SLACK fires |
|---|---|---|---|
| `sweep-creep` | 1.30–1.33 | **0.462–0.554** | **8** (all 8 in the corpus) |
| `sweep-step` | 4.25 | 0.831–0.885 | 0 |
| `sweep-slow` (R27) | 4.38 | 0.893–0.952 | 0 |
| **`e3-marker-ff` / `e2-rel-turn-lead` (R39)** | **4.10–4.38** | **0.816–0.963** | **0** |
| `fixedwing-sweep` | 5.9–11.8 | 0.951–1.081 | 0 |
| `sweep-lowq` | 9.1–18.8 | 0.967–0.993 | 0 |

All 8 corpus fires are on the single slowest card, on the lane with the lowest bank of its four.
**SLACK is detecting a slow card, not a slack law.** R39's two cards are `sweep-slow` geometry — the
family R27 already measured at 0.893–0.952 — so **the premise that `e3-marker-ff` is "the first card in
ten batches that can wake it" is false.** It is the first `sustained_turn` since R27; it is also the
geometry that was already known to run at twice the threshold.

**Sensitivity, quantified — the calibration `SLACK_FRAC`'s docstring asks for.** R39 contains the
largest deliberate law degradation the corpus has ever induced: `MarkerRateFeedForward` off costs
**2.3× the standing pointing error**. It moves `authorityUsedFrac` by **0.03–0.11**
(Fighter1 0.956 → 0.849; Multirole1 0.963 → 0.910; trainer 0.916 → 0.716). Direction is correct.
Scale is not: reaching `SLACK_FRAC` = 0.5 from 0.94 would need **~4–14 such defects stacked**. The
threshold is roughly **5× too low** for this card family — or, equivalently, the metric's dynamic
range against a real law defect is 0.11 and its trigger distance is 0.44.

**Verdict, against the three branches as written:**
- **Not FAIL-HIGH.** 0%, not >50%. The detector does not invert on this card.
- **Not PASS in substance.** 0% here is a structurally guaranteed non-fire, not a discriminating one.
- **Not FAIL-ZERO in the "no sensitivity left" sense either.** It has sensitivity (0.11 against a 2.3×
  defect); it is **mis-scaled**, and its numerator is the wrong quantity.

**Do NOT ungate `SLACK_TYPES`** — 94.8% of the modern corpus sits under `SLACK_FRAC` and ungating floods
every batch. **And the deferred peak / rise-window statistic does not fix this one either**: these are
40 s steady-state segments where a windowed statistic answers the same question. The defect is the
**denominator**. `authBank` normalises by the airframe's 72° clamp; on a sustained turn the meaningful
denominator is the bank *the card's own demand requires*, `atan(ω_demand·V/g)`. Rebased that way the
metric would read "did the law fly the bank the task needed" instead of "is the task near the clamp",
and R39 supplies exactly the paired data — a good arm at φ/φ_req ≈ 1.0 and a bad arm 4–14° short — to
calibrate a threshold on it. Backlog #55a.

---

## 4. A/B — `e3-marker-ff` / `Control/MarkerRateFeedForward`: **PASS**, large

n = 4/4 per arm on seven lanes, 2/2 on Darkreach. On-arm = feed-forward ON.

| lane | `fixedWindowOffDeg` off → on | Δ% | SD off / on | `rms` off → on | mean `\|azErr\|` Δ / SD |
|---|---|---|---|---|---|
| Fighter1 | 5.881 → **1.464** | −75% | 0.007 / 0.004 | 5.902 → 1.586 | −1.814 / **−903** |
| Multirole1 | 6.128 → **1.493** | −76% | 0.008 / 0.006 | 6.027 → 1.647 | −1.888 / **−601** |
| SmallFighter1 | 6.381 → **1.777** | −72% | 0.018 / 0.011 | 6.609 → 1.860 | −2.004 / **−959** |
| trainer | 5.283 → **1.474** | −72% | 0.045 / 0.008 | 5.395 → 1.571 | −1.782 / **−200** |
| VTOLTrainer1 | 6.040 → **1.465** | −76% | 0.013 / 0.006 | 6.174 → 1.970 | −2.006 / **−548** |
| EW1 | 5.075 → **2.181** | −57% | 0.079 / 0.028 | 5.451 → 2.330 | −2.293 / **−212** |
| FastBomber1 | 5.872 → **1.511** | −74% | 0.223 / 0.319 | 5.879 → 2.198 | −2.205 / **−10** |
| Darkreach | 5.897 → 6.636 | **+13%** | 0.386 / 0.094 | 4.983 → 4.297 | −1.288 / −15 |

`rmsPointingErrorDeg` is **down 8/8**; `fixedWindowOffDeg` down 7/8. The single reversal is Darkreach
(n = 2/2, the aborted lane, 20–64% bank-clamped) and its two metrics disagree with each other.

**`aimRate` non-zero on both arms — the null-confusion guard passes.** 4.4596 (off) / 4.4546 (on),
non-zero on 99.92% / 99.96% of rows. The marker swept identically; the difference is the law.

### 4a. Where the feed-forward actually acts — and why finding 16 read it as inert

| lane | arm | `bWt` | mean `\|outR\|` | mean `\|outY\|` | `bankTR` | `tBankE` | achieved bank |
|---|---|---|---|---|---|---|---|
| Fighter1 | 0 | 0.028 | **0.0100** | 0.181 | 61.6 | 61.6 | 61.1 |
| Fighter1 | 1 | 0.000 | **0.0099** | 0.044 | 73.6 | 72.0 | 68.9 |
| Multirole1 | 0 | 0.036 | **0.0084** | 0.183 | 64.9 | 64.9 | 65.6 |
| Multirole1 | 1 | 0.001 | **0.0068** | 0.046 | 75.1 | 72.0 | 69.4 |
| trainer | 0 | 0.033 | 0.0162 | 0.169 | 53.0 | 53.1 | 51.6 |
| trainer | 1 | 0.000 | 0.0081 | 0.031 | 68.4 | 68.5 | 65.9 |

- **The "0.0000 of roll stick" claim REPRODUCES, off the rail.** `bWt` is 0.000–0.040 — `blendRailPct`
  is 0, this is nowhere near the `lateralHold` latch — and mean `|outR|` is **0.0068–0.0109 on both
  arms** of the five clean lanes. The feed-forward adds essentially no roll stick. True as stated.
- **The inference from it is refuted.** The same lever is worth 57% of the standing error. Its channel
  is **target bank**: `bankTR` +10.4 to +15.4°, achieved bank +4 to +14°. Roll stick is the servo term
  that holds a trimmed bank, not the term that sets it — so `outR` was the wrong observable, and "82.5%
  of the turn demand arriving as 0.0000 of roll stick" describes a correctly-functioning bank-to-turn
  law, not an inert one.
- **The OFF arm's failure mode is now named: it skids.** It flies 4–14° *less* bank than the geometry
  requires and makes up the turn rate with yaw — mean `|outY|` 0.090–0.208 against 0.031–0.105 on the
  ON arm, a 2–4× increase. `|iYaw|` 0.062–0.110 vs 0.002–0.014, i.e. the fine yaw integrator is
  saturating against a deficit the bank channel should have supplied.

---

## 5. A/B — `e2-rel-turn-lead` / `Control/RelativeTurnLead`: **FAIL** (correct and worthless)

n = 4/4 per arm on seven lanes; Darkreach is **1/2** and carries no SD — conclude nothing there.

**The lever does exactly what v0.83 said it does.** With `RelativeTurnLead` off,
`leadRate = _headingRateFilt` = the absolute 4.46 °/s nose rate, × `TurnLeadTime` 0.65 s = 2.90° of
phantom lead. With it on, `leadRate = _headingRateFilt − _aimAzRateFilt` ≈ −0.05 °/s in a matched turn.

| lane | `\|leadDeg\|` off → on | `azErrPred` off → on | predFloor clamp % off → on | `bankTR` off → on |
|---|---|---|---|---|
| Fighter1 | 2.847 → **0.075** | 0.444 → 1.460 | 99.2 → 64.7 | 71.5 → 73.6 |
| Multirole1 | 2.846 → 0.103 | 0.449 → 1.477 | 98.2 → 62.1 | 73.2 → 75.0 |
| SmallFighter1 | 2.846 → 0.098 | 0.447 → 1.457 | 98.7 → 60.1 | 71.5 → 73.6 |
| trainer | 2.848 → 0.093 | 0.437 → 1.438 | 99.3 → 63.5 | 66.0 → 68.4 |
| VTOLTrainer1 | 2.844 → 0.133 | 0.443 → 1.436 | 97.5 → 59.5 | 67.6 → 70.0 |
| EW1 | 2.845 → 0.144 | 0.458 → 1.471 | 95.1 → 56.7 | 66.9 → 69.4 |
| FastBomber1 | 2.846 → 0.262 | 0.448 → 1.388 | 98.4 → 56.2 | 69.1 → 71.4 |

`azErrPred` off = **exactly 0.30 × `azErr`** (the predFloor bottom rail); on = **exactly `azErr`** (the
top rail). The lever changes the P-term input by **3.33×**.

**And the error does not move.**

| lane | mean `\|azErr\|` Δ | Δ vs null contrast (§6a) | `fixedWindowOffDeg` Δ% | `rms` Δ% |
|---|---|---|---|---|
| Fighter1 | −0.0069 (−0.5%) | null +0.0011 — **same order** | −2.1% | **+4.4%** |
| Multirole1 | −0.0107 (−0.7%) | null −0.0241 — **null is bigger** | −9.5% | **+1.7%** |
| SmallFighter1 | −0.0266 (−1.7%) | null +0.0029 | −25.4% | −0.6% |
| trainer | −0.0030 (−0.2%) | null +0.0002 | **+3.4%** | **+4.7%** |
| VTOLTrainer1 | −0.0184 (−1.2%) | null +0.0182 — **same order** | −16.2% | **+23.8%** |
| EW1 | −0.0673 (−3.8%) | null −0.0641 — **same order** | −20.4% | **+6.8%** |
| FastBomber1 | −0.0535 (−3.4%) | null −0.0760 — **null is bigger** | −7.0% | **+15.6%** |

- **On the declared PASS/FAIL test this is FAIL.** `leadDeg` separates 38× while the error does not.
- **`rmsPointingErrorDeg` is UP on 7 of 8 lanes** (+1.7% to +23.8%, |Δ/SD| 3.7–40). Two error metrics
  disagreeing in sign is not a win in either direction.
- The one thing the lever does buy is a **faster settle**, not a smaller standing lag:
  `fixedWindowOffDeg` (the 7–8 s window, deliberately pre-settlement) is down 6/8, and `settleTime95`
  availability rises on 5 of 8 lanes (VTOLTrainer1 1/4 → 4/4, FastBomber1 1/4 → 3/4, Fighter1 3/4 → 4/4).
  The *standing* `|azErr|` from 10 s on is unchanged to three decimals.

**Why it cannot move the standing lag on this card — the second rail.** The lever's 3.33× lands on a
bank command that is already at `MaxBankAngle`:

| lane | arm | `tBankE ≥ 71.5°` % of settled | `bankTR ≥ 72°` % of settled |
|---|---|---|---|
| Fighter1 | 0 / 1 | 56.5 / **99.2** | 41.0 / **94.6** |
| Multirole1 | 0 / 1 | 88.4 / **100.0** | 80.6 / **100.0** |
| SmallFighter1 | 0 / 1 | 55.1 / **98.7** | 39.6 / **93.8** |
| FastBomber1 | 0 / 1 | 0.0 / 56.4 | 0.0 / 40.1 |
| Darkreach | 0 / 1 | 6.2 / 82.3 | 5.0 / 68.6 |

A 3.33× larger error term buys **+0.6 to +2.0°** of effective bank because the command is against the
wall. The original v0.83 A/B ran at 96.9% *predFloor*-clamped and measured that clamp; R39 got off it
(98% → 60%) and immediately hit `MaxBankAngle`. **So the answer to "is the lead term what holds the
standing lag?" is no — and it cannot be, on this card.** The standing lag is held by 72° of bank against
a 4.5 °/s demand at 340–373 m/s, which needs 69.4–71.0°. §7a explains how the lanes got to that speed.

---

## 6. The frame hitch, and the two structural confounds it exposed

### 6a. The null contrast — the yardstick both A/Bs should be read against

`e3-marker-ff` arm 1 and `e2-rel-turn-lead` arm 1 flew **byte-identical lever settings**
(`mrFF=1 relLead=1`, the shipped default) on the same 8 airframes at the same lane distances
(`origDist` medians 12.2/12.3, 18.0/18.1 … 51.9/51.8 km), as two separate fleet launches 12 minutes
apart with `alpha-sweep` in between. That is a free null A/B:

```
lane            e3 arm1   e2 arm1   ratio
Fighter1         1.4560    1.4571   1.0008
trainer          1.4419    1.4421   1.0001
SmallFighter1    1.5040    1.5069   1.0019
VTOLTrainer1     1.4975    1.5157   1.0122
Multirole1       1.5376    1.5135   0.9843
EW1              1.7542    1.6901   0.9635
FastBomber1      1.6122    1.5362   0.9529
Darkreach        3.2484    4.0812   1.2564   <- Darkreach again
```

**Between-launch reproducibility of an identical configuration is 0.1–4.7% (median 0.08%) on 7 lanes.**
Within-card replicate SD is 0.002–0.05, which *understates* it. `e3`'s 55–58% effect is 12–500× the
null. **`e2`'s 0.2–3.8% on mean `|azErr|` is inside the null on 6 of 7 lanes** — on EW1 the null diff
(−0.064) and the arm diff (−0.067) are the same number. That is the honest reading of e2, and it is
stronger than the within-card −3 to −19 sigma suggests.

### 6b. The hitch

| what the log says | what the rows say |
|---|---|
| `LogOutput.log:8743` — one `[drone] frame hitch: 87 ms`, inside the e2 window | **86.8 / 86.8 / 126.7 ms**, three consecutive frames, **21 rows across 7 captures** |
| `:4384` — one `152 ms`, inside the e3 window | 152.2 ms on **1 row**, rec 137, in an `arm` window (excluded) |
| `:10238`, `:10239` — 65 / 54 ms | after the last e2 capture closed; no rows |

The log undercounts because the warning is edge-triggered (`rising`) and rate-limited to one per 0.25 s
(`TestDrone.cs:472,476`) — the 126.7 ms frame arrived 0.10 s after the 87 and was suppressed.
**Do not size a hitch from the log line; the column is the record.**

**Segments touched by the e2 event:** 5 scored (`turn360rtl`, recs 277/278/279/280/281 =
SmallFighter1, trainer, VTOLTrainer1, EW1, FastBomber1) at `tSeg` 27.0–39.1 s, 3 rows each; plus 2
`arm` windows (recs 283/284, `excluded = 1`, no metrics). **15 of 37,760 scored e2 rows = 0.040%.**
e3 carries a separate 3-frame event (31.4 / 32.9 / 46.2 ms, 21 rows, recs 169–175, all in
`turn360mff`) and one 25.3 ms frame on 7 arm-0 captures. Batch-wide: **59 rows over 25 ms of 88,084
(0.067%)**.

**Handling: dropped.** Re-running both A/Bs with every capture carrying an in-segment frame > 50 ms
removed:

```
lane            Δ|azErr| all      Δ|azErr| hitch-free
SmallFighter1   -0.0266  (n0=4)   -0.0263  (n0=3)
trainer         -0.0030           -0.0036
VTOLTrainer1    -0.0184           -0.0178
EW1             -0.0673           -0.0711
FastBomber1     -0.0535           -0.0551
```

**No conclusion changes**, on either card, on either metric. e3 has zero captures over 50 ms in-segment.

**But the hitch is arm-pure and structurally must be.** All 5 hitched e2 captures are arm 0; all 7
hitched e3 captures are arm 1. That is not luck. The fleet launches on a 3 s stagger and every lane
runs the **same ABBA queue index at the same wall clock**, so at any instant all 8 lanes are on the
same arm. CLAUDE.md's v0.94 note defends the design on the grounds that a confound "would have to hit
the fleet at one instant AND correlate with lane" — the second condition is not required, because the
arm is synchronised across the fleet by construction. Here it cost 0.04% of rows and nothing; the
argument is still wrong and should be replaced with "`frameMs` is a per-row column, so covary or drop".
Backlog #55c.

### 6c. Replicate 1 is 100% arm 0

`ArmOf(i) = ((i+1)>>1)&1` → index 0 maps to arm 0, always. Replicate 1 is the only one flown from the
spawn (`snapBackM = 0`), so **8 of 30 arm-0 captures per card are unplaced against 0 of 30 arm-1**.
Refutation test — drop it:

```
                 all replicates          rep-1 dropped
e3 Fighter1      -1.8141 (n0=4)          -1.8139 (n0=3)
e3 FastBomber1   -2.2053                 -2.2935
e2 SmallFighter1 -0.0266                 -0.0280
e2 EW1           -0.0673                 -0.0686
```

e3's rep-1 arm-0 mean (3.626) is within 0.7% of its placed arm-0 mean (3.602). **Not driving anything
here** — but the imbalance is permanent, it applies to every ABBA card ever flown, and a card with a
real placement effect would be biased by it in one direction only. Backlog #55b.

---

## 7. Confounds NOT ruled out

### 7a. Throttle 1.00, absolute 250 m/s entry, nothing holds speed

Both cards enter at an absolute 250 m/s with `thr` pinned at **1.00** and no speed hold. Over the 40 s
leg the lanes reach **245–373 m/s** (`deltaTAS` +0.8 to +138 m/s; `deltaEnergyHeightM` **−55 to +5078 m**
on a nominally level turn). This is R37 §4 recurring on a different card family, and here it is worse
than a ranking nuisance: at 340–373 m/s the 4.5 °/s demand *requires* 69.4–71.0° of bank, which puts
Fighter1 / Multirole1 / SmallFighter1 on the 72° clamp for most of the leg — **which is what makes the
e2 A/B unanswerable.** Any cross-lane statement on these two cards is confounded with live V.

### 7b. The treatment moves the flight condition inside a lane

e3 arm 1 flies 4–14° more bank and finishes 300–500 m lower on `deltaEnergyHeightM` than arm 0
(Fighter1 2915 vs 3391 m; trainer −45 vs +361 m). The arms are **not** at the same energy state at the
scoring window, so the 57% is "the feed-forward-ON *regime*", not a ceteris-paribus lever delta.
Controls that do hold: `aoaPeakDeg` is flat between arms on every lane (3.8/3.9, 3.2/3.3, 2.6/2.7) and
`gJitterG` is identical to three decimals per lane (0.088/0.088, 0.163/0.165) — so this is not aero and
not a distance artifact.

### 7c. `bankClampActivePct` cannot see this card's bank rail — and it is a `RAIL_METRIC`

`scorecard.py:757` computes `bankClampActivePct` from the CSV's **`targetBank`**. That column is the
shared `yawWeak`-gated pre-compute; `ApplyEvolvedLegacy` — the only shipped fixed-wing law — does not
fly it, and **its `targetBank` parameter was deleted in v0.96** (`ChaseController.cs:146-147`:
*"the recorded targetBank is the shared yawWeak-gated blend, which EvolvedLegacy does [not use]"*). The
live command is `bankTR` → `tBankE`.

Measured contradiction inside single segments:

| lane / arm | `bankClampActivePct` | `bankDemandExcessDeg` | `bankTR ≥ 72°` (settled rows) |
|---|---|---|---|
| e2 Fighter1 arm 1 | **0.0%** | 1.66° | **94.6%** |
| e2 SmallFighter1 arm 1 | **0.0%** | 1.69° | **93.8%** |
| e2 Multirole1 arm 1 | 2.4% | 2.74° | **100.0%** |
| e3 Fighter1 arm 1 | **0.0%** | 1.67° | **94.5%** |

`bankDemandExcessDeg` is built on `bankTR` and is therefore correct; its sibling, built on `targetBank`,
reads zero on the same rows. **`segments.railed = 0` across these 121 segments is not evidence the bank
axis was free**, and the same under-report applies to every `sustained_turn` in the corpus. This is a
scorecard defect, not an R39 artifact. Backlog #55d.

### 7d. Darkreach

n = 4 (e3, 2/2 by arm) and n = 3 (e2, **1/2** by arm — no SD on arm 0). 20–64% `bankClampActivePct`
(measured on the wrong column, so a floor not a ceiling), 12–46% `turnRateCapActivePct`, non-stationary
error on both arms, and the **only sign reversal on either card**. **Conclude nothing on Darkreach.**

Its two aborts — `rec 168` (e3, replicate 5) and `rec 282` (e2, replicate 4), both
`airframe damage (detached ratio 0.029)` = 1/35 parts — each **killed the rest of the lane**: e3 lost
replicates 6–8, e2 lost 5–8. That is how 64 nominal becomes 61 and 60. R37 §5's backlog #54e, twice
more. `dmgFrac` was **not** used to exclude anything (R37 §5); the damage is known only from the
`# stop` line.

### 7e. Elevation is unexplained on e2's on-arm

The card's `trackEl` is identically zero, so `off` ≈ `|azErr|` by construction. On e2 the residual is
not zero and it moves with the arm: `off − |azErr|` is 0.047 (arm 0) vs 0.120 (arm 1) on Fighter1, i.e.
implied elevation error ≈ 0.38° vs 0.60°. That is where `rmsPointingErrorDeg` gets worse while `|azErr|`
does not move. Nothing here explains why a pure-azimuth lever costs elevation.

### 7f. Ruled out

| candidate | evidence |
|---|---|
| an origin shift during either stagger | `datumX/Y/Z` = `(0, −4032, 0)` on every sampled row of both cards |
| a distance confound between arms | `gJitterG` identical to 3 dp between arms within every lane |
| an aero confound between arms | `aoaPeakDeg` flat between arms on all 8 lanes |
| the stimulus differing between arms | `aimRate` 4.4546 vs 4.4596, non-zero on 99.9% of rows |
| frame hitching biasing a conclusion | 0.040% of scored rows; dropping them moves nothing (§6b) |
| the rep-1 stratum driving the arm effect | dropping it moves e3 by ≤0.09%, e2 by ≤0.006° (§6c) |
| mid-run config drift | 2 distinct `# config` strings per card = the two arms, nothing else |
| the blend rail (R21's 100% latch) | `bWt` 0.000–0.040 on the clean lanes; `blendRailPct` ≤ 0.27% |
| unsettled legs faking a standing error | `\|azErr\|` flat by decade from 10 s on 14 of 16 lane-arms |
| unknown-tag / era contamination | `n_cols = 69` on all 121, 0 parse warnings, 0 unknown tags |

---

## 8. What R39-D CANNOT answer

- **Whether `RelativeTurnLead` helps at all.** The card put the bank command on the 72° clamp
  (§5, §7a), so the lever's 3.33× had nowhere to go. Re-fly at a demand the clamp does not bind:
  same geometry at **0.75× corner** entry, or 2.5 °/s at 250 m/s.
- **Whether SLACK works.** §3 — `authorityUsedFrac` on any sustained turn is a kinematic restatement of
  the card's demanded turn rate. No `sustained_turn` card can test it until the denominator changes.
- **Anything about rotorcraft, STOL, or the alpha ceiling.** Eight fixed-wing keys, `aoaPeakDeg`
  1.9–5.6° against limiters of 20–27°.
- **A clean per-lever effect size.** Both arms differ in energy state at the scoring window (§7b), and
  cross-lane comparison is collinear with live V (§7a).
- **Anything about Darkreach.** §7d.

## 9. Backlog

- **#55a — `authorityUsedFrac` needs a task-relative denominator on `sustained_turn`.** `authBank`
  normalises by the 72° clamp; the sustained-turn question is bank flown vs bank *required*
  (`atan(ω_demand·V/g)`). R39 supplies the paired calibration data (§3). Until then, leave
  `SLACK_TYPES` gated and treat the flag as uncalibrated on any turn card.
- **#55b — `ArmOf(0) = 0` puts the entire unplaced `snapBackM = 0` stratum on arm 0, on every ABBA card
  ever flown.** 12.5% of one arm, 0% of the other. Not biasing R39 (§6c); one line to fix.
- **#55c — CLAUDE.md's v0.94 fleet-ABBA safety argument is wrong.** A wall-clock confound does not need
  to correlate with lane, because the fleet is arm-synchronised (§6b). Every hitch in this batch is
  arm-pure. Replace the claim with the mitigation that actually works (`frameMs` per row).
- **#55d — `bankClampActivePct` reads a dead column.** Point it at `bankTR`/`tBankE`, re-index, and
  re-check every `sustained_turn` segment's `railed` flag (§7c). This is a `RAIL_METRIC`.
- **#55e — the gate band needs re-deriving at the shipped-default K.** 1.28 /s was measured without the
  feed-forward; with it the loop closes at 2.98 /s and a 4.5 °/s card lands at 1.44–1.53°, under the
  band (§1). Either raise the demand and drop the entry speed, or accept a 1.4° target.
- **#55f — the `[drone] frame hitch` warning undercounts a multi-frame stall** (edge-triggered +
  0.25 s rate limit, `TestDrone.cs:472,476`): 87 ms logged for a 300 ms / 3-frame event (§6b). And
  `[card] … start` prints a "derived sweep rate" of 12.1 °/s for a track that is 4.500 °/s.
- **#55g — both cards should pin throttle or use `startSpeedCorner`.** Throttle 1.00 from an absolute
  250 m/s puts the fast lanes on the bank clamp by 20 s (§7a), which is what cost e2 its answer.
