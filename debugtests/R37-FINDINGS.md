# R37 — the clean batch: placement survives, the lane ladder is perfect, and the card does not hold the condition it claims, v0.97.2

**First full batch on v0.97.2, the version that reverted the `AeroPart.Repair` placement pass.** One
F2 press, 16 lanes, card `oblique-6-dwell`, the same 10 fixed-wing keys wrapping into 16 lanes.
**125 captures, 124 complete, one abort.** R36's 32/32 placement kill is gone: every one of the
**109 large-displacement placements** (13.9–41.2 km snapback) flew its card to completion. The single
loss is a *different* failure — Darkreach replicate 5, killed by the v0.96 airframe-damage abort at
`detached ratio 0.114`, 0.3 s into `arm`.

| | R33 | R35 | R36 | **R37** |
|---|---|---|---|---|
| mod | v0.96.0 | v0.96.2 | v0.97.1 | **v0.97.2** |
| game | 0.34.1 | 0.34.1 | 0.34.1 | **0.34.1_18bd24b712df** |
| card | `oblique-6-c` | `oblique-6-dwell` + `alpha-steps` | `oblique-6-dwell` | **`oblique-6-dwell`** |
| lanes | 10 | 16 (one launch) | 16 x 2 launches | **16 (one launch)** |
| captures | 77 | 186 | 64 | **125** |
| replicates per lane | 8 | 8 | 1 | **8** (5 on lane 10) |
| aborted | 1 (detach) | 3 (detach) | 32 (`aircraft gone`) | **1 (detach)** |
| `n_cols` | 65 | 66 | 69 | **69** |
| scorable segments | — | — | 128 | **496**, 0 railed |

Session 2026-08-01 22:17:49 → 22:33:17, `rec` 1..125 contiguous, `n_cols = 69` uniform, zero parse
warnings, no A/B (`arm`/`arm_knob` NULL on all 125). Everything below is reproducible from
`debugtests/captures.db` and `debugtests/archive/R37-20260801/`. **Every claim states its n.**

---

## Verdict

1. **The placement kill is FIXED, and R37 is the only batch that proves it.** 109 placements at
   13.9–41.2 km snapback, **109 survived**; zero `aircraft gone`, zero pilot kills, zero velocity
   explosions. R36's separation was perfect the other way (32/32 zero-displacement survived, 32/32
   displaced died) on the identical card and roster, so the contrast is clean. §1.
2. **The `_laneBase` question is UNTOUCHED by this batch, and the datum column says so directly.**
   `datumX/Y/Z` reads **`(0, -4032, 0)` on all 250,074 rows of all 125 captures** — one value, never
   moved. Per R36 §1a the lane test only discriminates when the datum *shifts* mid-stagger; it did
   not, so R37 is a second no-regression and not a confirmation. Note also that R37's datum differs
   from R36's (`0, -4032, -1024`), so **`posX/posY/posZ` are not comparable across those two batches**
   — only `origDist` is. §1.
3. **The lane ladder is the cleanest in the corpus: a uniform 6.000 km ladder, 8.0 → 98.0 km, zero
   sign changes, no rift.** And unlike R35/R36 the *late-frame* form agrees: per-lane median
   `origDist` is monotone 18.4 → 98.9 km, steps +2.7..+6.5 km, 0 sign changes. §1.
4. **R37 contains the corpus's first purpose-shaped distance control, and it settles the metric-validity
   argument.** Six airframes fly a NEAR lane and a FAR lane ~60 km apart in the same batch, same law,
   same card. Across those six matched pairs: `gJitterG` **6/6 up, median 3.50x**; `gSustained` 6/6 up
   2.19x; `gPeak` 6/6 up 1.95x; `terminalOffDeg` **6/6 up, median 5.26x**; `overshootAzDeg` 6/6 up
   3.61x; `rollCmdMedian` 6/6 up 3.40x; `settleTime95` availability **6/6 DOWN, median 0.32x**. Against
   that: `fixedWindowOffDeg` median **1.01x (3/6 up)**, `rmsPointingErrorDeg` **1.01x**, `aoaPeakDeg`
   **0.99x**. The aircraft's flight condition is identical; only the instrument moves. §2.
5. **74% of R37's legs (367/496) are AT THE RESOLUTION FLOOR on `terminalOffDeg`.** The
   `floor_warning` now reaches `segments.warnings` (R36 backlog #53b is fixed), so this is visible
   without a manual threshold. `terminalOffDeg` is not a metric on this card. §2.
6. **The 10-airframe ranking on `fixedWindowOffDeg` reproduces R35 → R37 at Spearman +1.000** (exact,
   n=10) across two mod versions and two lane frames, ratios 0.92–1.13; R36 → R37 is +0.976. The
   ordering is COIN 0.076 < trainer 0.095 < CAS1 0.182 < EW1 0.197 < Fighter1 0.213 < VTOLTrainer1
   0.222 < FastBomber1 0.332 < Multirole1 0.342 < SmallFighter1 0.463 < Darkreach 0.574. §3.
7. **AND THAT RANKING IS NOT IDENTIFIED AS AN AIRFRAME PROPERTY — this is R37's most consequential
   finding.** The card pins entry speed at 0.95x each airframe's *probed* corner speed and then pins
   **throttle at 0.70 for everyone**, so every lane accelerates away from its entry condition at a rate
   set by thrust-to-weight. Measured at the 7–8 s scoring window: on **leg 1** V/Vcorner already spans
   **0.94 (CAS1) → 1.35 (Multirole1)**; by **leg 4** the same lanes read **1.03 → 2.49 (Darkreach)** — a
   within-capture drift of 1.09x (CAS1) to **2.03x (Darkreach)**. Live V/Vcorner at the window ranks the
   outcome at **Spearman +0.709** vs `fixedWindowOffDeg` — as strong as any probed parameter anyone
   proposed. Airframe and live speed are collinear by construction on this card, and live speed is
   exactly what the ONE-LAW rule *permits* a schedule to key off. §4.
8. **`settleTime95` availability is a joint law/distance gauge and must be read per lane.** Batch rate
   248/496 = 50.0%, but it is 6/6 down on the matched near/far pairs (100% → 9.4% for Multirole1) while
   also separating airframes at matched distance. Rank on the rate, never the mean, and never pool
   lanes. §2, §3.
9. **`dmgFrac` is NOT a valid damage exclusion, and R37 demonstrates it inside a single capture.**
   Capture rec 74 aborts on `detached ratio 0.114` (= 4/35 parts) while `dmgFrac = 0.0` on all five of
   its rows **and** `sc_detachedRatioAtStart = 0.0` in its sidecar. Ledger #51's `PartChecker` sweep
   latency, shown end-to-end. Any analysis that excluded damage using `dmgFrac = 0` excluded nothing. §5.
10. **One abort ends the whole lane, not one capture.** Lane 10 aborted on replicate 5 and the runner
    logged `#10 despawned (card finished)` ~20 s later; replicates 6–8 never flew. That is how 128
    nominal becomes 125. §5.
11. **`LogOutput-R37.log` SURVIVED** — copied out of `<game>/BepInEx` before the index ran, unlike
    R36's. Frame health is the best in the corpus: **1 row of 250,074 over 25 ms**, max in-capture
    `frameMs` 156.3, mean 16.70. §6.

---

## 1. Placement, the datum, and the lane ladder

### 1a. The placement kill is gone

```
R36 (v0.97.1)                          R37 (v0.97.2)
  snapBackM = 0.0    -> 32/32 lived      snapBackM = 0.0            -> 16/16 lived
  snapBackM 13.9-41.2 km -> 32/32 DIED   snapBackM 13.9-41.2 km  -> 109/109 lived
  stop = "aircraft gone" x32             stop = "aircraft gone" x0
```

Same card, same 10-key roster, same 16-lane layout, one mod version apart. R36's separation on
displacement was perfect; R37's is perfect the other way. `entry_ctrlReset = 1` on all 125.

### 1b. The datum never moved — so the lane-frame question is still open

`datumX/Y/Z` = **`(0, -4032, 0)`** on every one of the **250,074** rows. Per R36 §1a, the `_laneBase`
defect lives in the *frame*, not the number: a batch flown with a stationary camera produces an
identical layout whether the fix is present or not, and `test-lane-frame.py` says so
(*"parked camera: fixed and broken layouts identical (the change is a no-op)"*). **R37 proves no
regression and nothing more.** Backlog #53a is still open and still costs five minutes.

Cross-batch trap introduced here: **R36's datum was `(0, -4032, -1024)` and R37's is `(0, -4032, 0)`.**
`posX/posY/posZ` are datum-relative, so those two batches' position columns are in different frames.
`origDist` is a magnitude in the live frame and is comparable; the raw components are not.

### 1c. The ladder, in both forms

```
lane airframe        spawn origDist   step      per-lane median origDist
  1  Fighter1             8.021 km       -              18.36 km
  2  Multirole1          14.012      +5.991             23.12
  3  SmallFighter1       20.009      +5.996             26.52
  4  trainer             26.006      +5.998             29.25
  5  VTOLTrainer1        32.005      +5.999             35.67
  6  CAS1                38.004      +5.999             39.77
  7  COIN                44.004      +5.999             44.90   <- R35's 36.6 km rift was HERE
  8  EW1                 50.003      +5.999             52.04
  9  FastBomber1         56.003      +6.000             58.45
 10  Darkreach           62.003      +6.000             63.22
 11  Fighter1            68.002      +6.000             70.45
 12  Multirole1          74.002      +6.000             76.70
 13  SmallFighter1       80.002      +6.000             82.27
 14  trainer             86.002      +6.000             87.32
 15  VTOLTrainer1        92.002      +6.000             93.65
 16  CAS1                98.002      +6.000             98.91
     |step| 5.991..6.000, 0 sign changes        monotone, +2.73..+6.51, 0 sign changes
```

Both forms pass. The median form is the discriminating one (R36 §1a) and R37 is the first batch where
it comes back clean *and* tight — R36's median steps ranged 2.75–7.23 km, R37's 2.73–6.51 km, and
neither shows anything resembling R35's 36.62 km step.

---

## 2. The distance control — what R37 uniquely can prove

Six of the ten airframes fly **two lanes 60 km apart in the same batch**: Fighter1 (8.0 / 68.0 km),
Multirole1 (14.0 / 74.0), SmallFighter1 (20.0 / 80.0), trainer (26.0 / 86.0), VTOLTrainer1 (32.0 / 92.0),
CAS1 (38.0 / 98.0). Same airframe, same law, same card, same session, same datum. This is the design
R33 backlog #52a asked for; R36 got a rough version by accident and R37 gets a clean, evenly-spaced one.

Per-metric far/near ratio over the six matched pairs (`oblique_step`, `excluded=0`, `railed=0`, n=496):

| metric | CAS1 | Fighter1 | Multirole1 | SmallFighter1 | VTOL | trainer | median | direction |
|---|---|---|---|---|---|---|---|---|
| `gJitterG` | 1.91 | 4.73 | 6.22 | 3.26 | 3.33 | 3.68 | **3.50** | 6/6 up |
| `terminalOffDeg` | 2.40 | 2.60 | 16.80 | 3.67 | 7.39 | 6.85 | **5.26** | 6/6 up |
| `overshootAzDeg` | 2.67 | 1.64 | 7.18 | 4.73 | 4.55 | 1.40 | **3.61** | 6/6 up |
| `rollCmdMedian` | 1.81 | 5.19 | 3.94 | 3.09 | 2.44 | 3.71 | **3.40** | 6/6 up |
| `gSustained` | 1.54 | 2.43 | 2.80 | 1.91 | 2.24 | 2.14 | **2.19** | 6/6 up |
| `gPeak` | 1.83 | 1.59 | 2.30 | 1.37 | 2.08 | 2.14 | **1.95** | 6/6 up |
| `yawCmdMedian` | 1.48 | 1.27 | 1.47 | 1.21 | 2.08 | 1.58 | **1.47** | 6/6 up |
| `authStick` | 1.15 | 1.10 | 1.11 | 1.09 | 1.21 | 1.33 | **1.13** | 6/6 up |
| `settleTime95` rate | 0.73 | 0.29 | 0.09 | 0.12 | 0.34 | 0.50 | **0.32** | 6/6 DOWN |
| `offFloorPct` | 0.74 | 1.03 | 0.69 | 0.65 | 0.66 | 0.66 | **0.67** | 5/6 down |
| **`fixedWindowOffDeg`** | 0.99 | 1.00 | 0.98 | 1.02 | 1.04 | 1.13 | **1.01** | **3/6 — none** |
| **`rmsPointingErrorDeg`** | 1.01 | 0.99 | 1.06 | 1.02 | 1.01 | 1.01 | **1.01** | **none** |
| **`aoaPeakDeg`** | 1.00 | 1.00 | 0.97 | 1.00 | 0.99 | 0.99 | **0.99** | **none** |
| `stickFlipRateR` | 0.96 | 0.90 | 1.79 | 0.92 | 0.75 | 1.00 | **0.94** | none |

Lane-level (16 cells) Spearman vs `origDist`: `gJitterG` **+0.909**, `gSustained` **+0.903**,
`terminalOffDeg` **+0.622**, `settleTime95` rate **-0.485**, `rmsPointingErrorDeg` +0.103,
`fixedWindowOffDeg` **-0.032**. Log-log slope of `gJitterG` vs `origDist`: **+1.21** pooled (r=0.923),
**median +1.21** over the six matched pairs (0.71–1.62) — steeper than R35/R36's 0.885/0.893, which were
pooled across airframes at a coarser distance grid.

**What this establishes, and what it does not.** `aoaPeakDeg` flat at 0.99x is the load-bearing control:
the aircraft is at the same angle of attack near and far, so the 2–5x moves in the g family, terminal
error, overshoot and roll command are not aerodynamic. They are float grain in a world coordinate that
is 60 km larger. It does **not** establish that the law is unaffected — `rollCmdMedian` moving 3.40x
means the law really is commanding more roll out there, in response to a noisier aim geometry. It is a
real command on a corrupted input, which is worse than a measurement artifact and cheaper to fix (fly
inside ~50 km).

**Consequences for every future analysis:**
- `fixedWindowOffDeg`, `rmsPointingErrorDeg` and `aoaPeakDeg` may be compared across lanes.
- `gJitterG`, `gPeak`, `gSustained`, `terminalOffDeg`, `overshootAzDeg`, `rollCmdMedian`, `yawCmdMedian`,
  `authStick`, `offFloorPct` and `settleTime95` availability **may not**, ever, at different `origDist`.
- `scorecard.py` carries the distance caveat on `gJitterG` only. `gPeak`/`gSustained`/`terminalOffDeg`/
  `overshootAzDeg`/`rollCmdMedian` need the same note. (Backlog #54c.)

### 2a. The resolution floor, now visible

367 of 496 legs (**74.0%**) carry an `AT THE RESOLUTION FLOOR` warning in `segments.warnings` — the
`floor_warning` plumbing R36 §7 asked for is in. 14 legs are additionally `skipped` on
`fixedWindowOffDeg` ("under the `off` column's resolution floor (0.0396 deg)"): **13 EW1 + 1 COIN**.
EW1 therefore publishes `fixedWindowOffDeg` from 19 of its 32 legs, and its 0.1969 is the mean of its
worst 59%. Any EW1 comparison must state that n.

---

## 3. The airframe ranking, and its stability

| airframe | n | `fixedWindowOffDeg` | n(fw) | `rms` | `terminalOffDeg` | settle % | `gJitterG` | `offFloorPct` |
|---|---|---|---|---|---|---|---|---|
| COIN | 32 | 0.0757 | 31 | 0.868 | 0.0366 | 3.1 | 0.249 | 37.1 |
| trainer | 64 | 0.0949 | 64 | 0.806 | 0.0141 | 75.0 | 0.266 | 48.6 |
| CAS1 | 64 | 0.1816 | 64 | 0.896 | 0.0179 | 59.4 | 0.441 | 38.9 |
| EW1 | 32 | 0.1969 | **19** | 0.945 | 0.0109 | 81.2 | 0.165 | 56.0 |
| Fighter1 | 64 | 0.2125 | 64 | 0.754 | 0.0196 | 62.5 | 0.200 | 27.8 |
| VTOLTrainer1 | 64 | 0.2217 | 64 | 0.925 | 0.0138 | 67.2 | 0.354 | 42.6 |
| FastBomber1 | 32 | 0.3321 | 32 | 0.946 | 0.0491 | 12.5 | 0.270 | 8.9 |
| Multirole1 | 64 | 0.3417 | 64 | 1.064 | 0.0198 | 54.7 | 0.373 | 33.6 |
| SmallFighter1 | 64 | 0.4627 | 64 | 0.964 | 0.0389 | 28.1 | 0.184 | 16.2 |
| Darkreach | **16** | 0.5740 | 16 | 1.131 | 0.0805 | **0.0** | 0.270 | 11.1 |

Rank reproduction on `fixedWindowOffDeg`: **R35 ↔ R37 Spearman +1.000**, R36 ↔ R37 +0.976, R35 ↔ R36
+0.976 (n=10 each). Per-airframe R37/R35 ratios 0.917–1.125.

**That perfect reproduction is a statement about harness determinism, not about the law.** §4 explains
why. `rmsPointingErrorDeg` gives a different and much flatter picture (Fighter1 best at 0.754,
Darkreach worst at 1.131 — a 1.50x range against `fixedWindowOffDeg`'s 7.6x), and the two metrics
disagree on ordering for six of the ten airframes. Two metrics with a 5x difference in spread are not
measuring the same thing: `fixedWindowOffDeg` at 7–8 s of a 30 s leg is a **settling-rate** sample
(`scorecard.py:92-104` documents the window as deliberately pre-settlement), `rmsPointingErrorDeg` is a
whole-leg average. Report both or say which one you mean.

---

## 4. `oblique-6-dwell` does not hold the condition it claims

The card sets entry speed to **0.95x each airframe's probed FBW corner speed** — a correct,
ONE-LAW-compliant normalisation — and then pins **`ScenarioThrottle = 0.70` for every airframe**.
Nothing holds speed after entry. Measured V/Vcorner in the 7–8 s scoring window of each leg, R37:

| airframe | Vcorner | leg 1 `obDR6` | leg 2 `obDL6` | leg 3 `obUL6` | leg 4 `obUR6` | drift |
|---|---|---|---|---|---|---|
| CAS1 | 160 | 0.94 | 1.01 | 1.09 | 1.03 | 1.09x |
| COIN | 110 | 0.95 | 1.06 | 1.13 | 1.03 | 1.09x |
| FastBomber1 | 200 | 1.02 | 1.22 | 1.40 | 1.45 | 1.42x |
| VTOLTrainer1 | 160 | 1.18 | 1.57 | 1.78 | 1.77 | 1.50x |
| trainer | 130 | 1.21 | 1.66 | 1.90 | 1.89 | 1.57x |
| Darkreach | 100 | 1.22 | 1.82 | 2.37 | **2.49** | **2.03x** |
| EW1 | 130 | 1.23 | 1.70 | 1.97 | 1.96 | 1.60x |
| Fighter1 | 160 | 1.28 | 1.78 | 1.93 | 1.89 | 1.48x |
| SmallFighter1 | 155 | 1.33 | 1.92 | 2.11 | 2.06 | 1.55x |
| Multirole1 | 160 | 1.35 | 2.00 | 2.32 | 2.37 | 1.75x |

Three separate problems, all fatal to a between-airframe reading:

1. **The four legs are four flight conditions.** Comparing `obDR6` to `obUR6` inside one capture is
   comparing 1.22x corner to 2.49x corner on Darkreach. Every "down leg vs up leg" statement on this
   card is confounded with a 2x speed change (the down legs run first — `seg_index` 1,2 = obDR6/obDL6).
2. **The size of the drift is set by thrust-to-weight, which is an airframe property.** 1.09x on CAS1,
   2.03x on Darkreach.
3. **The between-airframe spread the corpus keeps ranking is collinear with live speed.** At the scoring
   window, V/Vcorner ranks `fixedWindowOffDeg` at **Spearman +0.709** (n=10) and raw V at +0.588 — as
   strong as or stronger than any *probed* parameter tested tonight. Live dynamic state is precisely
   what the standing rule allows a schedule to key off, so "the ranking is airframe identity" is not
   identified from "the ranking is speed" anywhere in this card's 314 captures.

**The fix already exists and has never been flown.** `cards/oblique-6-dwell-t040.json` and
`cards/oblique-6-dwell-t100.json` are byte-identical to the anchor except for a pinned
`Scenario/ScenarioThrottle` of 0.40 and 1.00, `repeat: 4`, and `t04`/`t10`-suffixed tags so no tool can
pool them with the anchor. Three throttle arms x the same 16 lanes is a within-lane, within-airframe
q axis — the only design in the card set that separates airframe from live speed. **Fly them.**

---

## 5. The one abort, and what it invalidates

```
rec 74  drone 10  Darkreach  replicate 5  snapBackM 25 356 m
  [card] entry audit: 95 m/s, clean (commanded 95).
  [card] ABORT (airframe damage (detached ratio 0.114)) — 'oblique-6-dwell' segment arm at 0.3s.
  [rec]  done (abort: airframe damage (detached ratio 0.114)) dur=0.3s samples=5
  ... ~20 s later ...
  [drone] #10 despawned (card finished). 15 live.
```

Two things this proves that are worth more than the lost capture:

**`dmgFrac` cannot exclude damage.** On this capture `dmgFrac = 0.0` on all five rows, and
`sc_detachedRatioAtStart = 0.0` in the sidecar, while the abort names 0.114 (4 of 35 parts) at the same
instant. `Aircraft.PartChecker` walks one part per fixed step and needs ~0.58 s to sweep 35 parts, so
both the row column and the sidecar snapshot read a partially-swept accumulator (ledger #51). Corpus-wide
`max(dmgFrac) = 0` in R37 means nothing. **Any earlier analysis that wrote "not damage, `dmgFrac = 0`"
has not excluded damage.** The only reliable damage signal is the abort line itself and
`sc_aeroPartCount`.

**An abort costs the lane, not the capture.** The runner ended lane 10 entirely; replicates 6, 7, 8 never
flew. 16 x 8 = 128 nominal, minus 3 = 125. Darkreach — already the corpus's thinnest lane, flagged short
in R29 (9/48) and R36 — is down to 16 scorable segments here against 64 for the six two-lane airframes,
so it carries 4x fewer legs than the airframes it is ranked against.

Where the damage came from is not decidable from R37: v0.97.2 reverted the `AeroPart.Repair` pass, so a
part shed on replicate 4 stays shed into replicate 5, and Darkreach's replicate 4 completed normally with
`dmgFrac = 0` on all 2017 rows — which, per the paragraph above, is not evidence of anything.

---

## 6. Archive and log health

```
debugtests/archive/R37-20260801/    252 files
  125 x mouseaim-rec-v0.97.2-R37-d<1..16>-<airframe>-<01..125>-oblique-6-dwell-*.csv
  125 x matching .airframe.json sidecars
    1 x mouseaim-anomalies-v0.97.2-R37-20260801-221709.log   (1457 lines)
    1 x LogOutput-R37.log                                    <-- SURVIVED
```

```
[drone] launching 16 x 'Fighter1,Multirole1,SmallFighter1,trainer,VTOLTrainer1,CAS1,COIN,EW1,
        FastBomber1,Darkreach' (by lane, wrapping) at 4000 m / 0.95x corner (per airframe),
        3s apart, lanes 8000 m + 6000 m abeam.
[drone] card 'oblique-6-dwell' (1 selected, 126s each, x8 from card 'oblique-6-dwell')
```

- **Frame health, best in the corpus:** in-capture `frameMs` max **156.3**, mean **16.70**, and exactly
  **1 row of 250,074 (0.0004%) over 25 ms**. 10 `[drone] frame hitch` lines in the log (1102 / 332 / 240 /
  172 / 156 / 109 / 78 / 76 / 68 / 55 ms) — the 1102 ms one is during the launch stagger, outside any
  capture. Frame hitching is not a usable confound for anything in R37.
- 0 `[place]` lines (expected — the v0.96.1 audit was a tautology and was removed).
- 0 `# cfg` mid-run config changes; the config header is constant across all 125.
- 16 `despawned (card finished)`, 1 `ABORT`, 0 `pilot killed`.
- Capture shape: 2016–2018 rows, 126.0 s; legs 29.9–30.0 s / 480–481 samples; `arm` 6 s, `excluded=1`.

---

## 7. Ruled out

| candidate | evidence |
|---|---|
| the R36 placement kill recurred | 109/109 displaced placements survived; 0 `aircraft gone` |
| an origin shift during the stagger | `datumX/Y/Z` = `(0,-4032,0)` on all 250,074 rows |
| the lane rift recurred | spawn ladder steps 5.991–6.000 km and median ladder monotone, both 0 sign changes |
| frame hitching biasing any metric | 1 row of 250,074 over 25 ms |
| mid-run config drift | 0 `# cfg` lines; header identical across 125 captures |
| railed / slack / unknown-tag contamination | 0 / 0 / 0 in 496 scorable segments |
| A/B arm contamination | `arm` and `arm_knob` NULL on all 125 |
| damage anywhere but lane 10 rep 5 | 1 abort; but see §5 — `dmgFrac` cannot prove the negative |
| the near/far contrast being an aero effect | `aoaPeakDeg` far/near 0.99x, 1/6 up |
| the near/far contrast being airframe | it is *within* airframe by construction, 6 matched pairs |

## 8. What R37 CANNOT prove

- **Anything about `_laneBase`.** Constant datum ⇒ the fixed and broken code paths are identical (§1b).
- **Anything about a control-law change.** No A/B arm, one mod version, one card. R35 ↔ R37 is not a
  null A/B either: six of sixteen lanes were physically relocated between those batches.
- **Anything about airframe generality.** §4 — airframe is collinear with live speed on this card.
- **Anything about rotorcraft or STOL.** Ten fixed-wing keys. Two of the four airframe cases the standing
  rule names have no drone data on the current law at all (§9).
- **Anything about large demand.** One geometry: a 6° oblique diamond, four mirrored legs, ~5.7° steps.
- **Anything about `terminalOffDeg`-ranked quality.** 74% of legs are at the float floor (§2a).

## 9. Backlog

- **#54a — fly `oblique-6-dwell-t040` + `-t100`.** The single highest-value flight in the card set: it is
  the only design that separates airframe identity from live speed, both cards are already written, and
  the anchor arm (0.70) already has 314 captures. ~2 x 20 min unattended. §4.
- **#54b — `oblique-6-dwell` should state its speed drift in its own note, or pin throttle per airframe.**
  Until then every between-airframe claim built on it needs the §4 caveat attached.
- **#54c — `scorecard.py` should carry the distance caveat on more than `gJitterG`.** §2 measures
  `gPeak`, `gSustained`, `terminalOffDeg`, `overshootAzDeg`, `rollCmdMedian`, `yawCmdMedian`, `authStick`
  and `settleTime95` availability all moving 1.1–5.3x on a pure distance contrast at constant AoA.
  Docstrings only; no metric changes.
- **#54d — `dmgFrac` needs a warning in `CAPTURES-DB.md`.** §5 is a single-capture proof that it reads 0
  on a capture the harness aborted for damage. Consider indexing the abort's detached ratio into a
  column so damage is queryable.
- **#54e — an abort should not kill the lane.** Lane 10 lost 3 of 8 replicates to one bad spawn. If the
  runner re-placed instead of despawning, the corpus's thinnest lane would not keep getting thinner.
- **#53a is still open.** R37 is the second consecutive no-regression on a parked camera. Deliberately
  fly the camera past the 1024 m threshold mid-stagger and score `datumX/Y/Z` first.
- **#54f — 13 of 34 cards have never been flown**, including both throttle arms, all five e1/e2/e3
  attribution A/Bs, `stol-steps`, `stol-sweep`, `rotor-hover`, `rotor-bob`, `alpha-sweep` and
  `oblique-above-c`. See `LAW-WEAKNESS-MAP.md` §"What we still cannot see".
