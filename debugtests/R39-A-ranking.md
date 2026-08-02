# R39 Criterion A — the throttle contrast: the airframe spread survives at matched speed, and the part that moves is a settling transient, v0.98.1

**The batch R37 §9 backlog item #54a asked for.** `oblique-6-dwell-t040` and `oblique-6-dwell-t100`,
byte-identical in geometry, entry condition, altitude, lane list and lane count; the only differences
are a pinned `Scenario/ScenarioThrottle` (0.40 vs 1.00) and a `t04`/`t10` tag suffix. 128 captures,
64 per arm, 16 lanes over 10 fixed-wing keys, `repeat: 4`, entry 0.95× each airframe's own FBW corner
speed at 4000 m. One session (`20260802-083849`), one config line, **0 aborts, 0 parse warnings,
0 railed, 0 unknown tags, 0 A/B arms, 0 DAMAGED**. Entry speed and altitude are identical per airframe
across the two arms; only the throttle differs.

| | R37 (anchor) | **t040** | **t100** |
|---|---|---|---|
| mod | v0.97.2 | **v0.98.1** | **v0.98.1** |
| throttle | 0.70 | **0.40** | **1.00** |
| captures | 125 | **64** | **64** |
| lanes | 1–16 | **1–16** | **17–32** |
| `rec` | 1..125 | **1..64** | **65..128** |
| wall clock | 22:17–22:33 | **08:40:40–08:47:43** | **08:49:57–08:57:00** |
| scorable segs | 496 | **256**, 0 railed | **256**, 0 railed |
| leg-1 `fixedWindowOffDeg` | — | **64/64** | **64/64** |

Everything below is **leg 1 only** (`obDR6t04` / `obDR6t10`), so leg-order speed drift cannot
contribute. Every claim states its n.

---

## Verdict

1. **THE GATE DOES NOT PASS AS PRE-REGISTERED.** Throttle is a real q lever on 15 of 16 lanes, but
   it reaches the pre-registered ≥1.25× speed ratio on only **3 airframes of 10** (Fighter1 1.40×,
   Multirole1 1.38×, SmallFighter1 1.36×). Six sit in 1.10–1.25×; **COIN fails outright at 1.094×**
   and its Criterion A result is uninterpretable. Nothing is compressed — the highest t100 `spd /
   sc_infoMaxSpeed` in the batch is CAS1 at 0.784. §1.
2. **The manipulation is nevertheless large compared with the confound it exists to break.** Between
   airframes, leg-1 V/Vcorner spans 0.87→1.17 inside t040 (range 0.30). Within an airframe, the
   throttle swing is +0.08 to +0.45 in the same units, and the two arms together tile V/Vcorner from
   0.87 to 1.61 with **overlapping** airframe coverage. That overlap is what makes §4 possible and is
   the thing no previous batch had.
3. **Noise unit derived from R39: σ = 0.0247° (20 cells, 108 df), 3σ = 0.0740°.** The plan's
   pre-registered 0.021° is close but 16% optimistic; using it would move nothing (Fighter1 at 2.90σ
   would become 3.4σ, i.e. 4 movers instead of 3). §2.
4. **|Δ| < 3σ on 7 of 10 airframes.** The three that move — Multirole1 +0.1865 (+7.6σ),
   SmallFighter1 +0.2015 (+8.2σ), Darkreach +0.1706 (+6.9σ) — all move in the R37 direction
   (more q → worse). Fighter1 is +0.0717 (+2.90σ), the one case the threshold decides. §3.
5. **Both pre-registered branches fire, so the branch test as written cannot decide.** AIRFRAME
   IDENTITY requires |Δ| < 3σ on ≥7 of 10 **and** a between-airframe range near R37's ×6.1: both are
   met (t040 ×4.7, t100 ×6.6, R37 ×6.1). MIXED requires "a subset moves": also met, at 7–8σ. **The
   honest verdict is MIXED**, and the identity branch's criteria should have carried a "and no
   airframe exceeds Nσ" clause. §3.
6. **THE DECIDING TEST IS NOT IN THE PRE-REGISTRATION: at matched V/Vcorner the between-airframe
   spread survives essentially intact.** Nine airframes place a leg-1 cell inside the band V/Vcorner
   **1.01–1.18**, where `fixedWindowOffDeg` spans **0.0801 (trainer) → 0.3786 (SmallFighter1) = ×4.7,
   a 12.1σ spread at held speed.** The two bands that are *purely* t100 — 4 airframes at 1.28–1.33
   (×3.8) and 3 at 1.56–1.61 (×3.2) — say the same thing at a completely different absolute speed.
   And the cross-arm pair is blunt: **Fighter1 at 1.57 scores 0.1822 while Multirole1 at 1.16 scores
   0.3223** — the faster aircraft is 1.8× better. **Live speed cannot be the driver of a spread that
   survives at matched live speed, in four separate speed bands, in both directions.** §4.
7. **Variance decomposition agrees: airframe R² = 0.838, V/Vcorner R² = 0.273** over the 20
   (airframe, arm) cells. Airframe plus a common within-airframe q slope reaches 0.926. §4.
8. **The part that does move is a settling TRANSIENT, not a quality difference.** Sweeping the
   start-anchored window across leg 1: Δ is 0 movers at 2–3 s, 3 at 7–8 s, 3 at 11–12 s, 1 at 15–16 s,
   1 at 20–21 s and **0 at 25–26 s**. `rmsPointingErrorDeg` — the whole-leg average, an independent
   window — shows **0 movers of 10**, max |z| = 1.29, and *disagrees in sign with `fixedWindowOffDeg`
   on Fighter1*. `fixedWindowOffDeg` is defined as a deliberately pre-settlement sample
   (`scorecard.py:92-104`); it is reporting a real change in how fast the law converges, not in where
   it converges. §5.
9. **The mover/non-mover split is separated by exactly one candidate of sixteen, and the separation is
   fragile.** `V/Vcorner` at t040 separates the 4-mover set (movers 1.095–1.164, non-movers
   0.867–1.093, zero overlap; Spearman(Δ, V/Vc@t040) = **+0.939**) — i.e. the airframes that respond
   to throttle are the ones already above ~1.09× corner at low throttle. **It does NOT separate the
   3-mover set**: drop Fighter1 (1.114) to the non-mover side and it lands inside the mover band. With
   16 candidates tried on 10 points, one clean separator is roughly what chance provides. §6.
10. **`omegaMax` does not out-perform mass here, and neither is worth anything.** Spearman vs the
    airframe mean: `omegaMax` −0.467, mass +0.564, `alphaLimiter` +0.585, `gLimitPositive` +0.405.
    The best single correlate is `authStick` at **+0.964**, which is a same-tick co-symptom of the
    error, not a cause. **No probed parameter explains the ranking.** §6.
11. **The arms are NOT time-interleaved** — t040 is `rec` 1..64, t100 is `rec` 65..128, nine minutes
    later. A null contrast (replicates 1+2 vs 3+4, inside each arm, same lane, same throttle) produces
    **0 of 20 cells over 3σ**, against 3 of 10 for the real contrast, so the movers are not replicate
    drift. A *step* at the fleet boundary is not excluded by that control. §7.

---

## 1. The gate — did throttle actually move q?

Mean `spd` over `tSeg` 7–8 s of leg 1, per lane, from the raw CSVs (`rows` is not materialized for
R39). 4 replicates per lane. `q ratio` is the dynamic-pressure ratio, `(V100/V40)²`.

| airframe | lanes 40→100 | V40 | V100 | V/Vc 40 | V/Vc 100 | **V ratio** | q ratio | t100 V/Vmax | origDist 40 / 100 (km) |
|---|---|---|---|---|---|---|---|---|---|
| Fighter1 | 1→17, 11→27 | 178.3 | 250.4 | 1.11 | 1.57 | **1.404** | 1.97 | 0.624 | 8.4/8.6, 68.1/68.1 |
| Multirole1 | 2→18, 12→28 | 186.4 | 257.2 | 1.16 | 1.61 | **1.380** | 1.90 | 0.617 | 14.3/14.4, 74.1/74.1 |
| SmallFighter1 | 3→19, 13→29 | 178.0 | 241.9 | 1.15 | 1.56 | **1.359** | 1.85 | 0.583 | 20.2/20.2, 80.1/80.1 |
| FastBomber1 | 9→25 | 190.6 | 235.8 | 0.95 | 1.18 | 1.237 | 1.53 | 0.492 | 56.1/56.1 |
| trainer | 4→20, 14→30 | 138.3 | 170.5 | 1.06 | 1.31 | 1.233 | 1.52 | 0.579 | 26.1/26.1, 86.1/86.1 |
| VTOLTrainer1 | 5→21, 15→31 | 166.6 | 204.7 | 1.04 | 1.28 | 1.229 | 1.51 | 0.695 | 32.1/32.2, 92.1/92.1 |
| EW1 | 8→24 | 142.1 | 172.8 | 1.09 | 1.33 | 1.216 | 1.48 | 0.604 | 50.1/50.1 |
| Darkreach | 10→26 | 109.5 | 131.7 | 1.10 | 1.32 | 1.203 | 1.45 | 0.472 | 62.0/62.1 |
| CAS1 | 6→22, 16→32 | 143.3 | 161.2 | 0.90 | 1.01 | 1.124 | 1.26 | 0.784 | 38.1/38.1, 98.1/98.1 |
| **COIN** | 7→23 | 95.3 | 104.3 | 0.87 | 0.95 | **1.094** | 1.20 | 0.736 | 44.1/44.1 |

- **PASS (≥1.25×): 3 airframes / 6 lanes.** Fighter1, Multirole1, SmallFighter1.
- **MARGINAL (1.10–1.25×): 6 airframes / 9 lanes.** trainer, VTOLTrainer1, CAS1, EW1, FastBomber1,
  Darkreach.
- **FAIL (<1.10×): COIN, lane 7→23, 1.094×.** Throttle is not a q lever on COIN at this entry
  condition. **COIN's Criterion A result below is uninterpretable and is reported, not dropped.**
- **Compression: none.** Highest t100 `spd / sc_infoMaxSpeed` is CAS1 0.784. No lane rides its Vmax.

The gate does not fail broadly — 15 of 16 lanes have a real manipulation, and it exceeds the
between-airframe speed range that made R37 unreadable — but it fails its own PASS criterion on 7 of
10 airframes, and **the size of the manipulation is itself airframe-collinear** (T/W again; Spearman
of ΔV/Vc against `maxThrustN/massKg` = +0.675). That is the R37 confound one level down, and it is
the reason §4's matched-speed test, not §3's Δ table, is what this batch actually proves.

**One thing the gate found that the plan did not anticipate: `origDist` is matched pairwise between
the arms.** t100 flew lanes 17–32 as a second `ScenarioBatchQueue` fleet, and the lane ladder was
re-laid from the same base, so every arm pair sits within 0.2 km of the same distance to the world
origin (8.4/8.6 … 98.1/98.1). Per R37 §2 that removes the distance confound outright — it does not
have to be argued away.

## 2. The noise unit

Pooled within-cell SD of `fixedWindowOffDeg`, leg 1, all 128 legs (none NULL, none railed):

| grouping | σ | cells | df |
|---|---|---|---|
| within (airframe, arm, **lane**) | 0.0222 | 32 | 96 |
| **within (airframe, arm) — pre-registered form** | **0.0247** | 20 | 108 |
| within (airframe, arm, lane), reps 2–4 only | 0.0210 | 32 | 64 |
| within lane, **near** lanes (8–62 km) | 0.0183 | 20 | — |
| within lane, **far** lanes (68–98 km) | 0.0274 | 12 | — |
| within (airframe, arm, lane), all four legs | 0.0343 | 126 | 374 |

**σ = 0.0247°, 3σ = 0.0740°.** The plan's 0.021° from the older batch is 16% low; the near-lane figure
(0.0183) reproduces it almost exactly, which is where it presumably came from. Both are reported;
neither changes the verdict — at 0.021° the mover count goes 3 → 4 (Fighter1 joins) and §4 is
untouched.

**Replicate 1 is not a separate stratum here.** It flies from the spawn (`entry_snapBackM = 0`, against
24.5 km on t040 and 37.0 km on t100 for replicates 2–4), but its leg-1 mean is 0.2402 against
0.2201/0.2260/0.2223 for t040 and 0.2846 against 0.2943/0.2857/0.2911 for t100 — inside 1σ in both
arms. Pooling it costs 6% on σ (0.0210 → 0.0222). It is pooled.

Also note the far-lane σ is **1.50×** the near-lane σ. R37 §2 established that `fixedWindowOffDeg`'s
*mean* is distance-invariant; its *replicate scatter* is not. That is new and belongs with the R37 §2
consequence list.

## 3. The per-airframe Δ

`fixedWindowOffDeg`, leg 1, t100 − t040. n = 8 per cell for the six two-lane airframes, 4 for the four
single-lane ones. `count(fw)` equals `count(*)` in every cell — no censoring on leg 1.

| airframe | n40 | n100 | fw t040 | fw t100 | **Δ** | **Δ/σ** | sd40 | sd100 | V/Vc 40 | V/Vc 100 | ΔV/Vc |
|---|---|---|---|---|---|---|---|---|---|---|---|
| SmallFighter1 | 8 | 8 | 0.3786 | 0.5801 | **+0.2015** | **+8.16** | 0.0447 | 0.0064 | 1.15 | 1.56 | +0.41 |
| Multirole1 | 8 | 8 | 0.3223 | 0.5088 | **+0.1865** | **+7.56** | 0.0241 | 0.0220 | 1.16 | 1.61 | +0.44 |
| Darkreach | 4 | 4 | 0.1672 | 0.3378 | **+0.1706** | **+6.91** | 0.0497 | 0.0182 | 1.10 | 1.32 | +0.22 |
| Fighter1 | 8 | 8 | 0.1105 | 0.1822 | +0.0717 | +2.90 | 0.0202 | 0.0184 | 1.11 | 1.57 | +0.45 |
| COIN ⚠ | 4 | 4 | 0.1285 | 0.0909 | −0.0376 | −1.52 | 0.0453 | 0.0175 | 0.87 | 0.95 | +0.08 |
| VTOLTrainer1 | 8 | 8 | 0.2956 | 0.2709 | −0.0248 | −1.00 | 0.0240 | 0.0355 | 1.04 | 1.28 | +0.24 |
| CAS1 | 8 | 8 | 0.2479 | 0.2274 | −0.0205 | −0.83 | 0.0262 | 0.0117 | 0.90 | 1.01 | +0.11 |
| EW1 | 4 | 4 | 0.1710 | 0.1896 | +0.0185 | +0.75 | 0.0152 | 0.0035 | 1.09 | 1.33 | +0.24 |
| FastBomber1 | 4 | 4 | 0.2974 | 0.2893 | −0.0082 | −0.33 | 0.0146 | 0.0102 | 0.95 | 1.18 | +0.23 |
| trainer | 8 | 8 | 0.0801 | 0.0884 | +0.0082 | +0.33 | 0.0125 | 0.0164 | 1.06 | 1.31 | +0.25 |

⚠ COIN failed the gate (1.094×); its row is not interpretable as a q contrast.

- **|Δ| > 3σ on 3 of 10**, all three positive — the R37 direction (leg-1 Spearman(V/Vcorner,
  `fixedWindowOffDeg`) was +0.44 there; here it is **+0.285 inside t040, +0.394 inside t100, +0.385**
  pooled over the 20 cells). No airframe moves in the wrong direction at any significance.
- **|Δ| < 3σ on 7 of 10.**
- On the correct standard-error form (SE of a difference of means, `σ·√(1/n₁+1/n₂)`, i.e. 0.5σ or
  0.707σ rather than σ) the count is **4 of 10**: Fighter1 5.8, Multirole1 15.1, SmallFighter1 16.3,
  Darkreach 9.8. The pre-registered 3σ-of-raw-σ threshold is conservative by 4–6×; using the
  statistically correct one does not move the verdict, because the other six sit at |t| ≤ 2.2.
- **Between-airframe range:** t040 0.0801 (trainer) → 0.3786 (SmallFighter1) = **×4.7**;
  t100 0.0884 (trainer) → 0.5801 (SmallFighter1) = **×6.6**. R37's leg 1 was 0.086 → 0.527 = ×6.1.
  Neither arm collapses the spread.
- **Ranking stability, Spearman over 10 airframes:** t040 ↔ t100 **+0.867** (only Darkreach reranks,
  4 → 8). R37 ↔ t040 +0.612; R37 ↔ t100 +0.903.

**Against the three pre-registered outcomes:**

| outcome | requires | observed | fires? |
|---|---|---|---|
| LIVE STATE | \|Δ\| > 3σ on ≥7/10, sign matching | 3/10 (4/10 on the SE form), all signs match | **no** |
| AIRFRAME IDENTITY | \|Δ\| < 3σ on ≥7/10 **and** range near ×6.1 | 7/10; ×4.7 / ×6.6 vs ×6.1 | **yes, literally** |
| MIXED | a subset moves | 3 airframes at 6.9–8.2σ | **yes** |

Two branches fire. **Declared verdict: MIXED**, with the identity branch's criteria met only because
they contain no upper bound on how hard the movers may move.

## 4. What actually decides it — the matched-speed test

The two arms together tile V/Vcorner from 0.87 to 1.61 with overlapping airframe coverage. Sorting all
twenty (airframe, arm) leg-1 cells by V/Vcorner and reading the bands:

| V/Vcorner band | cells | airframes | best → worst | ratio | spread/σ |
|---|---|---|---|---|---|
| 0.87–0.95 | 4 | 3 | COIN 0.0909 → FastBomber1 0.2974 | ×3.3 | 8.4 |
| **1.01–1.18** | **9** | **9** | **trainer 0.0801 → SmallFighter1 0.3786** | **×4.7** | **12.1** |
| 1.28–1.33 | 4 | 4 | trainer 0.0884 → Darkreach 0.3378 | ×3.8 | 10.1 |
| 1.56–1.61 | 3 | 3 | Fighter1 0.1822 → SmallFighter1 0.5801 | ×3.2 | 16.1 |

**Read the bands honestly.** The 1.01–1.18 band is nine of the ten airframes inside a 0.17-wide speed
window, but seven of its nine cells are t040 — so that band is largely restating that the t040 arm
*already* holds live speed nearly constant (V/Vcorner 0.87–1.17 across all ten airframes) and still
spreads ×4.7. That is a real and under-appreciated fact about the anchor card, not an artifact, but it
is not by itself a manipulation result.

**The manipulation result is the other three rows.** The 1.28–1.33 and 1.56–1.61 bands are *purely
t100*: four and three airframes matched to within ±0.03 and ±0.03 of V/Vcorner, at absolute speeds
20–40% above anything the t040 arm reaches — and they spread ×3.8 and ×3.2. Whatever the ranking is
made of, it reproduces at 1.6× corner as well as at 1.1× corner.

And the cross-arm pair is blunt: **Fighter1 at V/Vcorner 1.57 scores 0.1822, while Multirole1 at 1.16
scores 0.3223.** The faster aircraft is 1.8× *better*, and Fighter1 at 1.57 also beats
SmallFighter1 at 1.15 (0.3786) by 2.1×. Any "the ranking is live speed" account has to explain that,
and none does.

Variance decomposition over the 20 cells (SST = 0.34799):

| model | R² |
|---|---|
| V/Vcorner alone (slope +0.323 °/unit) | **0.273** |
| airframe alone (10 fixed effects) | **0.838** |
| airframe + one common within-airframe q slope (+0.266) | 0.926 |

Residual SD after airframe alone is 0.0750° against σ = 0.0247 — so the arm effect within airframe is
real and is the 3-mover story, but airframe carries 3× more of the between-cell variance than live
speed does.

## 5. The refutation test — where the Δ lives in time

**Conclusion to refute:** *the between-airframe spread is airframe-borne, and the throttle Δ on the
three movers is a real but separate effect.*

**What would refute it:** if the movers' Δ were the same quantity as the between-airframe spread
— i.e. a persistent, settled pointing-quality difference driven by q — then it should appear in every
window of the leg, and in the whole-leg `rmsPointingErrorDeg`, exactly as the between-airframe spread
does.

Mean `off` on leg 1 by start-anchored window, t100 − t040, with the per-window pooled σ:

| window | σ | 3σ | movers | which |
|---|---|---|---|---|
| 2–3 s | 0.2713 | 0.8138 | 0 | — |
| 4–5 s | 0.0301 | 0.0902 | 1 | Multirole1 |
| **7–8 s** | 0.0250 | 0.0749 | **3** | Multirole1, SmallFighter1, Darkreach |
| 11–12 s | 0.0206 | 0.0619 | 3 | Fighter1, Multirole1, SmallFighter1 |
| 15–16 s | 0.0198 | 0.0595 | 1 | SmallFighter1 |
| 20–21 s | 0.0217 | 0.0650 | 1 | Darkreach |
| 25–26 s | 0.0204 | 0.0612 | **0** | — |

The Δ has a shape: absent at 2–5 s, maximal at 7–12 s, gone by 25 s. SmallFighter1 runs
+0.020 → +0.036 → **+0.200** → **+0.239** → +0.122 → +0.038 → +0.027. That is a slower approach to
the same endpoint, not a worse endpoint.

`rmsPointingErrorDeg` (whole-leg, σ = 0.0611, 3σ = 0.1833) agrees: **0 movers of 10, max |z| = 1.29**,
and it *reverses sign* against `fixedWindowOffDeg` on Fighter1 (rms −1.29σ vs fw +2.90σ) and on trainer
and EW1. The between-airframe rms spread is 0.748 → 1.047, only ×1.4, exactly as R37 §3 reported.

So the refutation test **fails to refute**: the movers' Δ is confined to the settling window and does
not reach the whole-leg score. `fixedWindowOffDeg` is behaving as documented — a deliberately
pre-settlement sample of convergence rate — and what throttle moves is convergence rate.

**The honest caveat on this test:** at 25–26 s six of the ten airframes read at or under
`OFF_FLOOR_DEG` (0.0396°) in at least one arm, so "no Δ at settlement" is partly "no measurement at
settlement". The 11–16 s windows, which are above the floor for eight of ten, carry the decay claim.

## 6. What separates the movers, and what does not

Spearman over the ten airframes, against (a) the airframe's mean `fixedWindowOffDeg` across both arms
and (b) the signed Δ:

| candidate | ρ vs mean fw | ρ vs Δ | note |
|---|---|---|---|
| `authStick` @t040 (live) | **+0.964** | +0.321 | **co-symptom, not a cause** — same-tick output of the same error |
| **V/Vcorner @t040 (live)** | +0.442 | **+0.939** | the only mover/non-mover separator, and it is fragile |
| `alphaLimiter` (probed) | +0.585 | +0.422 | |
| `massKg` (probed) | +0.564 | +0.491 | |
| `infoMaxSpeed` (probed) | +0.602 | +0.498 | |
| `turningRadius` (probed) | +0.525 | +0.407 | |
| `maxThrustN/massKg` (T/W) | +0.365 | +0.675 | this is what sets ΔV/Vc, i.e. the manipulation size |
| `gLimitPositive` (probed) | +0.405 | +0.436 | |
| `fbwCornerSpeed` (probed) | +0.439 | −0.082 | |
| `maxPitchAngularVel` (probed) | −0.414 | −0.615 | |
| **`omegaMax` @t040** | **−0.467** | −0.297 | **does not out-perform mass here** |
| `omegaMax` @t100 | −0.479 | −0.539 | |
| `aoaPeakDeg` @t040 (live) | +0.018 | +0.370 | |

Two things worth saying plainly:

- **`omegaMax` = `gLimitPositive·9.81/max(V, 0.75·Vcorner)` loses to mass on this ranking** (−0.467
  against +0.564), reversing the prior result the plan cited. Neither is an explanation; both are
  weak.
- **`V/Vcorner @t040` separates the movers perfectly and should not be believed on that basis.**
  Movers 1.095–1.164, non-movers 0.867–1.093, no overlap, ρ(Δ) = +0.939 — i.e. "the airframes that
  respond to throttle are the ones already past ~1.09× corner at low throttle", a live-state threshold
  and a legal thing for the law to key off. But it separates only the **4**-mover set. On the
  pre-registered 3-mover set Fighter1 (1.114) sits *inside* the mover band and the separation is gone.
  Sixteen candidates on ten points; one clean split is roughly the chance rate. **Do not build on
  this. It is a hypothesis for a card that pins V/Vcorner directly, not a finding.**

## 7. Ruled out

| candidate | evidence |
|---|---|
| distance / `origDist` between the arms | matched pairwise to <0.2 km on all 16 pairs (§1); R37 §2 already showed `fixedWindowOffDeg`'s mean is distance-invariant |
| the entry condition differing between arms | `entry_v_to` and `entry_alt_to` identical per airframe across arms; the only differing override is `ScenarioThrottle` |
| replicate drift inside an arm | null contrast (reps 1+2 vs 3+4, same lane, same throttle): **0 of 20 cells over 3σ**, against 3 of 10 for the real contrast |
| replicate 1's spawn-entry stratum | its leg-1 mean is inside 1σ of reps 2–4 in both arms (§2) |
| the movers being one lane | Fighter1 +0.076/+0.067, Multirole1 +0.176/+0.197, SmallFighter1 +0.243/+0.160 — both lanes, both directions consistent; the non-movers move on neither lane |
| rails / clamps | 0 railed segments; `bankClampActivePct` = 0.0 on every leg-1 cell; `turnRateCapActivePct` ≤ 1.4 (Darkreach t100) |
| damage | 0 aborts, 0 DAMAGED warnings (but per R37 §5 `dmgFrac` cannot prove this negative) |
| config drift mid-batch | one distinct `config` string over all 128 captures; 0 `# cfg` lines |
| A/B arm contamination | `arm` / `arm_knob` NULL on all 128 |
| censoring of the metric | leg-1 `fixedWindowOffDeg` is 64/64 in both arms; `count()` equals `count(*)` in all 20 cells |
| leg-order speed drift | leg 1 only, throughout |

## 8. Confounds NOT ruled out — named

1. **The arms are not time-interleaved.** t040 is `rec` 1..64 (08:40:40–08:47:43) and t100 is `rec`
   65..128 (08:49:57–08:57:00), two consecutive `ScenarioBatchQueue` fleets. Any step change at the
   fleet boundary — a background process, an origin shift, the operator's camera — is fully aliased
   onto the arm contrast. The null contrast in §7 controls for *drift within* an arm, not for a *step
   between* them. **A three-arm interleaved design (t040/t100/t040) would close this and nothing else
   will.**
2. **The manipulation size is airframe-collinear.** ΔV/Vcorner runs +0.08 (COIN) to +0.45 (Fighter1)
   and is set by thrust-to-weight (ρ = +0.675). This is the R37 §4 confound one level down: the
   *strength* of the q lever is still an airframe property. It is why §4's matched-speed test, which
   does not depend on the per-airframe Δ at all, is the result this batch actually earns.
3. **"Not live speed" is not "airframe identity".** This batch manipulates ONE live-state axis. AoA
   (`aoaPeakDeg` spans 2.7–7.9° across airframes at t040 and falls 15–27% at t100), air density
   (matched, 4000 m), control effectiveness and roll inertia are untouched. A spread carried by a
   *probed parameter* or by *another live-state axis* is **ONE-LAW compliant**, not a violation. The
   defensible claim is "the spread is not live speed", and no stronger.
4. **COIN's lane failed the gate** (1.094×, ΔV/Vc +0.08). Its Δ of −0.0376 (−1.52σ) is a
   near-null-manipulation result and cannot be counted as evidence for the non-mover side. Its t100
   cell also carries `offFloorPct` **57.3%** against a 0.0909 mean — 2.3× the floor, the thinnest
   measurement in the batch.
5. **trainer's cells are close to the floor in both arms** (0.0801 / 0.0884, i.e. 2.0–2.2×
   `OFF_FLOOR_DEG`, `offFloorPct` 41.6 / 41.8%). trainer anchors the low end of every matched-q band
   in §4. The ×4.7 band ratio would shrink if trainer's floor-limited value is biased upward; the
   band still spans ×3.4 with trainer dropped (Fighter1 0.1105 → SmallFighter1 0.3786).
6. **Four airframes fly one lane, not two** (COIN, EW1, FastBomber1, Darkreach): n = 4 per arm against
   8. Darkreach is one of the three movers and is on n = 4.
7. **`origDist` still moves the replicate SD** — far-lane σ 0.0274 vs near-lane 0.0183. Since the six
   two-lane airframes carry one near and one far lane, their cell σ is a mixture; the four one-lane
   airframes are all on near lanes. The pooled σ used as the threshold is therefore slightly
   conservative for the one-lane airframes and slightly permissive for the two-lane ones.
8. **One geometry, one leg, one altitude, one mod version, ten fixed-wing keys.** 6° oblique diamond,
   `obDR` only, 4000 m, v0.98.1. Nothing here speaks to rotorcraft, STOL, large demand, or loadout.

## 9. Backlog

- **#55a — the throttle axis needs a third, interleaved arm.** `t040 / t100 / t040` in one launch, or
  three fleets alternating, kills confound 1 for the cost of one more batch. Nothing else in the card
  set can.
- **#55b — a card that pins V/Vcorner directly, not throttle.** §6's threshold hypothesis (the law
  degrades with q only above ~1.09× corner) is the one testable thing this batch produced, and
  throttle is too blunt to test it: it moved V/Vcorner by +0.08 on COIN and +0.45 on Fighter1. A card
  that trims to a commanded multiple of corner would put every airframe on the same q grid.
- **#55c — the R37 §2 consequence list needs a line about replicate SCATTER.** `fixedWindowOffDeg`'s
  *mean* is distance-invariant (R37) but its replicate σ is 1.50× larger on far lanes (§2). A noise
  floor quoted without its lane group is one lane group's noise floor.
- **#55d — pre-registration branches must be disjoint.** §3: AIRFRAME IDENTITY and MIXED both fired
  because the identity branch bounded only the count of non-movers, not the size of the movers. Any
  future pre-registration should state the exclusion.
- **#54a is CLOSED.** Both throttle arms flew. The answer is §4, not §3.
