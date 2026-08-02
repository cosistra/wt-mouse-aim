# R39 — the STOL batch: the card never delivered 90 m/s, and the one segment that did is unflyable at 2500 m, v0.98.1

> **Renamed 2026-08-02 (was `R40-stol.md`).** These captures carry run tag **R39**, not R40 — R40 is
> the later v0.99.1 batch (`place-noop` / `place-deflect` / `alpha-pullup`). A redirect stub stood at
> the old path for one working session; its inbound references in `.py` and `SESSION-2026-08-02.md`
> were repointed in **v1.0.0** and the stub is **gone**. Body text was already correct and is
> unchanged.

Run tag `R39`, plugin **v0.98.1**, session `20260802-083849`, cards **`stol-steps`** (40 captures,
`rec` 311–350, lanes d57–d66) and **`stol-sweep`** (13 captures, `rec` 351–363, lanes d67–d76).
Fleet = 10 fixed-wing keys, `ScenarioRepeat` 4, card `startAlt` 2500 m, card `startSpeed` 90 m/s.
Standing ONE-LAW case 3 ("a low-limit STOL trainer").

Sources: the 53 CSVs + sidecars in `<game>/BepInEx/`, `mouseaim-anomalies-v0.98.1-R39-20260802-083849.log`,
`LogOutput.log`, and a private snapshot of `captures.db` with these 53 captures indexed (the shared DB
was **not** written — another agent owns it this session). Every claim states its n.

---

## Verdict

1. **`stol-sweep` produced 13 not 40 because nine of ten lanes hit the 500 m MSL altitude-floor abort
   19–26 s into replicate 1, and an abort discards the lane's remaining replicates.** Measured, not
   inferred: all nine `# stop` lines read `reason=abort: altitude floor (500 m MSL)`, and
   `ScenarioPlayer.Finish` (`ScenarioPlayer.cs:1576-1596`) sets `_card = null; _queue = null` — the
   queue **is** the replicate expansion (`:351`). **Not** an operator stop: the run continued straight
   into `rotor-hover` (`rec` 364–369). §1
2. **The batch contains no low-speed data on eight of the ten airframes.** `startSpeed 90` is applied
   once, at the placement, with throttle **1.00** and no `Scenario/ScenarioThrottle` override. By the
   end of the 6 s `arm` the jets are already at **144–147 m/s**; by the last scored segment they are at
   **340–381 m/s (2.1–2.4× corner)** — *faster than anything else in R39*. Mean speed over all scored
   `stol-steps` samples: Multirole1 296, Fighter1 281, SmallFighter1 277 m/s. Only **COIN (104 m/s
   mean, 0.85–0.95 V/Vc)** and **CAS1 (158 m/s, 0.81–1.06)** flew anything a STOL card was meant to
   test. §2
3. **`stol-sweep` is unflyable at 2500 m by every airframe in the fleet, including the one that
   "completed".** All 13 captures lose **1846–1963 m** in 19–30 s (62–189 m/s sink at the end). COIN's
   four completions end at **580/641/621/617 m** with **−67 to −75 m/s** vertical — 1.1 to 2.1 s of
   margin. The card needs ≥ 2500 m of *usable* air above the floor and is given exactly 2000. §1.3
4. **The ranking is airframe-driven and it survives.** Spearman(azimuth-step `terminalOffDeg`,
   R39-A `t040 fixedWindowOffDeg`) = **+0.794**; vs `t100` = **+0.903** — as strong as R39-A's own
   `t040`↔`t100` consistency (+0.867), across a different card, a different demand size (30° az step
   vs 6° oblique), and a different altitude. Over the 20 (airframe × {az30R, az30L}) cells,
   **R² airframe = 0.461, R² V/Vcorner = 0.000**. **No reshuffle.** But see the caveat: the intended
   contrast was never delivered (§2), so this is a second operating point, not a low-q one. §3
5. **One metric in the same batch *is* state-driven, and it is the interesting one.** The nose-down
   step's standing error `elDn40 terminalOffDeg` spans **0.091° (COIN) → 8.771° (Multirole1), ×96**,
   with **R² of V/Vcorner alone = 0.609** and Spearman(error, V/Vc) = **+0.806**. Mechanism measured
   below: it is a V-scaled bank demand, so it is a q effect wearing an airframe's clothes. §4
6. **The nose-down hemisphere is still broken, and this is the first re-measurement since v0.85
   claimed it fixed.** Fleet mean `elDn40 terminalOffDeg` = **2.87°** (n=39) against its mirror
   `elUp40` = **0.047°** (n=39) — a **×61 asymmetry on two equal 40° steps from the same energy
   state**. R19 (v0.77, pre-fix) measured 6.89° on a 50° down step. Reduced, not removed. §4
7. **Four ONE-LAW violations, three of them measured biting in this batch.** `aoaFade` runs the
   proportional form on **0 of 10** airframes; the `0.3` floor on `omegaMax *= Max(0.3, aoaGateUp)`
   is active on **24.3%** of Darkreach's and **12.0%** of trainer's sustained-turn samples; the
   harness's own `RateMaxDegS = 30` clips the "same fraction of structural g" demand on **4 of 10**
   lanes, so `stol-sweep` is not the normalized card its note claims. §5
8. **Harness defects, separately: three.** The queue-kill-on-abort (§1.1), the unpinned throttle
   (§2), and `bankClampActivePct` — which in this batch does *not* read 0.0% but **over-reads the
   bank rail by up to 66 points** (Darkreach 70.7% claimed vs 4.6% measured on `bankTR`). §6

---

## 1. Why 13 and not 40

### 1.1 The mechanism, from the `# stop` lines and the source

```
d67-Fighter1-351       dur=31.6  reason=abort: altitude floor (500 m MSL)
d68-Multirole1-352     dur=25.2  reason=abort: altitude floor (500 m MSL)
d69-SmallFighter1-353  dur=26.5  reason=abort: altitude floor (500 m MSL)
d70-trainer-354        dur=28.3  reason=abort: altitude floor (500 m MSL)
d71-VTOLTrainer1-355   dur=26.6  reason=abort: altitude floor (500 m MSL)
d72-CAS1-356           dur=26.6  reason=abort: altitude floor (500 m MSL)
d73-COIN-357           dur=36.0  reason=card 'stol-sweep' complete
d74-EW1-358            dur=26.9  reason=abort: altitude floor (500 m MSL)
d75-FastBomber1-359    dur=27.5  reason=abort: altitude floor (500 m MSL)
d76-Darkreach-360      dur=27.5  reason=abort: altitude floor (500 m MSL)
d73-COIN-361/362/363   dur=36.0  reason=card 'stol-sweep' complete   (replicates 2,3,4)
```

`Tick` fires the floor at `ScenarioPlayer.cs:1637`, calls `Abort`, which calls `Finish`, which nulls
`_queue`. Since `_queue` is the replicate expansion (`RunIndex => _qi + 1`, `:351`), **one abort ends
the lane, not the replicate.** Replicate counts actually obtained:

| lane | airframe | `stol-steps` | `stol-sweep` |
|---|---|---|---|
| d57/d67 | Fighter1 | 4 | **1** |
| d58/d68 | Multirole1 | 4 | **1** |
| d59/d69 | SmallFighter1 | 4 | **1** |
| d60/d70 | trainer | 4 | **1** |
| d61/d71 | VTOLTrainer1 | 4 | **1** |
| d62/d72 | CAS1 | 4 | **1** |
| d63/d73 | COIN | 4 | **4** |
| d64/d74 | EW1 | 4 | **1** |
| d65/d75 | FastBomber1 | 4 | **1** |
| d66/d76 | Darkreach | 4 (rep 4 = 4 rows, damage abort) | **1** |

**No airframe is missing.** 9 lanes × 1 + COIN × 4 = 13. `stol-steps` is complete at 40/40 (39
scorable).

**Ruled out, each against its own signal.** Not an operator stop — `rotor-hover` `rec` 364–369 ran
afterwards at 09:40:39–09:45:39. Not damage — `dmgFrac` is 0.000 on every row of all 13 sweep
captures, and R39-F §2 already proved that column is structurally incapable of being nonzero on an
aborted capture, so the `# stop` reason is the only usable evidence and it names the floor. Not
despawn/card-length — the card is 36 s and COIN flew it four times back to back at 36.0 s each.
Not a stall at 90 m/s: nothing stalls, everything *dives*.

### 1.2 Why the descent

`deriveAzRate` → `SustainableTurnRate` (`ScenarioPlayer.cs:1726`) computes
ω = 0.6 · g·√(n²−1)/V from the **structural** g limit and the live 90 m/s entry, with **no lift
limit**. Measured derived rates: Fighter1/Multirole1/SmallFighter1/trainer **30.0** (the
`RateMaxDegS` clamp), VTOLTrainer1 29.7, CAS1 27.9, COIN 24.0, EW1 22.2, FastBomber1/Darkreach 18.4.

At 90 m/s a Fighter1 (corner 160, gLimit 9) can pull ≈ 9·(90/160)² ≈ 2.85 g of lift, i.e. a level
turn at ≈ 69° of bank giving ≈ 17 °/s — against a 30 °/s demand. The law banks to the wall, the nose
falls, and the aircraft buys the turn rate with altitude. `deltaEnergyHeightM` is only **−498 m**
fleet-mean while altitude loss is **1932 m**: this is a trade, not a bleed — the dive is the control
input.

### 1.3 Every lane, including COIN

| airframe | seg dur | V start→end | alt loss | sink at end | turn rate | demand ratio | end alt |
|---|---|---|---|---|---|---|---|
| Fighter1 | 25.6 | 147→208 | 1942 | −63 | 23.4 | 0.99 | 501 |
| Multirole1 | 19.2 | 148→252 | 1952 | −157 | 25.4 | 0.99 | 501 |
| SmallFighter1 | 20.4 | 143→238 | 1946 | −84 | 26.1 | 0.99 | 501 |
| trainer | 22.3 | 118→210 | 1963 | −188 | 11.6 | 0.97 | 503 |
| VTOLTrainer1 | 20.6 | 120→211 | 1960 | −138 | 14.4 | 0.98 | 501 |
| CAS1 | 20.6 | 106→213 | 1926 | −126 | 12.7 | 0.98 | 507 |
| **COIN** | **29.9** | 81→141 | 1891 | **−75** | 20.9 | 1.00 | **580** |
| EW1 | 20.8 | 122→256 | 1956 | −168 | 19.3 | 0.99 | 508 |
| FastBomber1 | 21.4 | 116→227 | 1930 | −189 | 15.8 | 1.00 | 512 |
| Darkreach | 21.5 | 105→216 | 1937 | −183 | 9.9 | 0.98 | 502 |
| COIN ×3 | 30.0 | 95→140 | 1846–1879 | −67 | 20.8 | 0.99 | 617–641 |

`turnRateDemandRatio` **0.97–1.00 on 13/13**: the card is asking for exactly what the probe says the
airframe cannot exceed, so every metric on this segment is unresponsive by construction. All 13 are
`railed=1`. **COIN did not survive the maneuver — the card ended first.**

9 altitude-floor aborts is the largest such cluster in corpus history (previous: R32 `darkreach-05`,
3; R18 `fixedwing-v2`, 2).

---

## 2. The card never delivered 90 m/s — the headline harness defect

`stol-steps` mean speed **at the start → end of each scored segment**, n=4 per cell (Darkreach n=3):

| airframe | Vcorner | az30R | az30L | elDn40 | elUp40 | V/Vc at elUp40 end |
|---|---|---|---|---|---|---|
| Multirole1 | 160 | 147→243 | 244→314 | 321→367 | 367→**381** | **2.38** |
| Fighter1 | 160 | 146→238 | 239→301 | 304→348 | 348→341 | 2.13 |
| SmallFighter1 | 155 | 144→231 | 232→294 | 298→344 | 345→340 | 2.19 |
| VTOLTrainer1 | 160 | 126→186 | 186→227 | 224→265 | 266→255 | 1.59 |
| EW1 | 130 | 121→172 | 172→208 | 211→243 | 243→238 | 1.83 |
| FastBomber1 | 200 | 118→168 | 168→211 | 211→250 | 251→250 | 1.25 |
| trainer | 130 | 116→163 | 163→196 | 192→231 | 232→217 | 1.67 |
| Darkreach | 100 | 107→137 | 137→161 | 162→204 | 204→199 | 1.99 |
| CAS1 | 160 | 111→144 | 144→164 | 162→187 | 187→170 | 1.06 |
| **COIN** | 110 | 92→103 | 103→108 | 100→121 | 122→94 | **0.85** |

The entry sets speed once; throttle is **1.00** (`EntryThrottle`, no `# override` line in any of the
53 headers) and the 6 s `arm` at ≈1 g excess thrust is enough for a fighter to gain 55 m/s before
the first scored sample. **The fix is one line of card JSON** — the `oblique-6-dwell-t040/t100` pair
already pins `Scenario/ScenarioThrottle`; `stol-steps`/`stol-sweep` do not.

Consequence for the batch's stated purpose: **this is not STOL data.** It is a second high-q dataset
for eight airframes, and a genuine low-q dataset for COIN only (and CAS1 marginally). Every claim
about "the law at 90 m/s" below is therefore restricted to COIN, CAS1, and everyone's first ~6 s.

---

## 3. Ranking: airframe-driven, no reshuffle — but the test was not the one intended

`terminalOffDeg`, `stol-steps` mirrored azimuth pair, sorted by the pooled value (n=8 per airframe,
n=6 Darkreach), against R39-A's oblique legs:

| airframe | V/Vc az30R | V/Vc az30L | az30R | az30L | **az pooled** | sd | t040 fwOff | t100 fwOff |
|---|---|---|---|---|---|---|---|---|
| trainer | 1.07 | 1.37 | 0.049 | 0.072 | **0.060** | 0.013 | 0.0801 | 0.0884 |
| COIN | 0.88 | 0.95 | 0.135 | 0.192 | **0.163** | 0.039 | 0.1285 | 0.0909 |
| EW1 | 1.13 | 1.46 | 0.480 | 0.014 | **0.247** | 0.234 | 0.1710 | 0.1896 |
| VTOLTrainer1 | 0.97 | 1.28 | 0.269 | 0.245 | **0.257** | 0.014 | 0.2956 | 0.2709 |
| Fighter1 | 1.20 | 1.68 | 0.183 | 0.381 | **0.282** | 0.099 | 0.1105 | 0.1822 |
| CAS1 | 0.81 | 0.96 | 0.265 | 0.299 | **0.282** | 0.024 | 0.2479 | 0.2274 |
| Darkreach | 1.22 | 1.47 | 0.601 | 0.055 | **0.328** | 0.281 | 0.1672 | 0.3378 |
| Multirole1 | 1.22 | 1.74 | 0.355 | 0.346 | **0.351** | 0.009 | 0.3223 | 0.5088 |
| FastBomber1 | 0.71 | 0.94 | 0.223 | 0.825 | **0.524** | 0.764 | 0.2974 | 0.2893 |
| SmallFighter1 | 1.21 | 1.69 | 0.586 | 0.520 | **0.553** | 0.033 | 0.3786 | 0.5801 |

```
Spearman(az pooled , t040 fixedWindowOffDeg) = +0.794   n=10
Spearman(az pooled , t100 fixedWindowOffDeg) = +0.903
Spearman(t040       , t100                 ) = +0.867   (R39-A's own internal consistency)
Spearman(az pooled  , V/Vc during az30R    ) = +0.261
R^2 over 20 (airframe x {az30R,az30L}) cells: airframe 0.461,  V/Vcorner 0.000
Spread: az pooled x9.2 (0.060..0.553);  t040 x4.7
```

**The R39-A ordering survives.** trainer and COIN are the two best on both cards; SmallFighter1 and
Multirole1 are in the bottom three on both. The rank agreement with `t100` (+0.903) is *higher* than
R39-A's own arm-to-arm agreement, on a completely different maneuver.

`az pooled` vs `t040 rmsPointingErrorDeg` is **+0.091** — i.e. the two whole-segment averages do not
agree, matching R39-A §8's finding that `rmsPointingErrorDeg` and `fixedWindowOffDeg` measure
different things (convergence *speed* vs convergence *point*). Nothing new; noted so the +0.794 is
not over-read as "everything agrees".

**Two caveats that limit this.**
- The intended manipulation did not happen (§2). V/Vc during `az30R` spans 0.71–1.22, which sits
  *inside* R39-A's t040 band (0.87–1.17). This is a replication at a similar operating point on a
  different maneuver, not a low-q test. **The capture that would answer the question as asked:
  `stol-steps` re-flown with `Scenario/ScenarioThrottle` pinned to ≈0.15–0.25** (enough to hold
  90–110 m/s on a jet), two arms, same 10 lanes. Without that pin no card in the corpus can put a
  Fighter1 below its corner speed for 12 consecutive seconds.
- FastBomber1's `sd` is **0.764 on n=8** — one `az30L` replicate dominates its mean. Its rank is
  unsafe; excluding it does not change the sign or the qualitative order.

---

## 4. The failure mode this batch actually found: the below-horizon false bank equilibrium

Not present in the 250 m/s oblique corpus because no card there steps 40° below the horizon.

`terminalOffDeg` on the two mirrored elevation steps (both 40°, same energy state, 12 s each):

| airframe | V/Vc during elDn40 | **elDn40** | elUp40 | ratio |
|---|---|---|---|---|
| Multirole1 | 2.10 | **8.771** | 0.002 | ×4400 |
| SmallFighter1 | 2.04 | **7.508** | 0.007 | ×1070 |
| Fighter1 | 2.00 | **4.546** | 0.001 | ×4500 |
| VTOLTrainer1 | 1.48 | **3.366** | 0.007 | ×480 |
| EW1 | 1.69 | **2.263** | 0.016 | ×140 |
| FastBomber1 | 1.10 | **0.755** | 0.041 | ×18 |
| Darkreach | 1.75 | **0.386** | 0.447 | ×0.9 |
| CAS1 | 1.04 | **0.270** | 0.021 | ×13 |
| trainer | 1.55 | **0.095** | 0.006 | ×16 |
| COIN | 0.94 | **0.091** | 0.022 | ×4 |

Fleet mean 2.867 vs 0.047 (n=39 each). R19 (v0.77, before the v0.85 `BelowAlignSuppress` rework)
measured **6.889°** on a 50° down step; R11 (v0.71) measured 0.307° on a 60° one. So the defect is
smaller than at its v0.77 worst and still an order of magnitude above its mirror.

### The mechanism, measured on the last 4 s of each segment

| airframe | \|bank\| late | `bankTR` late | `tBankE` late | `azErr` late | `bWt` late | `elevErr` late | `outR` late |
|---|---|---|---|---|---|---|---|
| SmallFighter1 | **45.6** | −42.7 | −42.7 | −1.97 | 0.003 | −9.17 | +0.025 |
| Multirole1 | **42.0** | −39.7 | −39.7 | −1.77 | 0.003 | −10.56 | +0.016 |
| Fighter1 | **27.7** | −25.1 | −25.1 | −1.44 | 0.001 | −6.63 | +0.030 |
| VTOLTrainer1 | 21.9 | — | — | — | — | −6.14 | — |
| EW1 | 21.1 | — | — | — | — | −5.06 | — |
| FastBomber1 | 20.5 | — | — | — | — | −2.45 | — |
| Darkreach | 3.0 | — | — | — | — | −0.44 | — |
| COIN | 2.3 | +0.0 | −1.3 | −0.18 | 0.000 | 0.00 | +0.014 |
| CAS1 | 1.9 | — | — | — | — | −0.57 | — |
| trainer | **1.1** | +0.0 | −1.1 | −0.14 | 0.000 | 0.00 | +0.001 |

The same three columns on `elUp40`: **\|bank\| late = 0.0–4.7° for all ten**, `off` late 0.00–0.72°.

**Read of the trace (Multirole1 rec 312, representative of all five failures):**

1. **t 0–2.5 s — the law pulls UP on a nose-DOWN step.** `bigTurn = 1.0` at 40° of error, so the
   suppressor's own taper `Clamp01((1−bigTurn)/0.3)` is **zero** and `bSup = 0.000`: full
   roll-and-pull is handed back by design. Measured: `outP` peaks at **−0.78** (nose-up), **6.2 g**,
   and the aircraft **climbs 191 m** in the first 3 s of a descent command. Across the five failing
   airframes: 135–191 m gained at 3.8–7.9 g.
2. **t ≈ 2.5–4 s — the suppressor arms while the aircraft is already banked.** `bSup` rises to
   0.62 → 0.98, `bWt` collapses to 0.002. The align channel is now off — including the channel that
   would roll *out* of the bank the previous 2.5 s put in.
3. **t 4–12 s — a stable false equilibrium.** `tBankE == bankTR == −39.7°` matches the actual bank
   −42.0°: **the standing bank is commanded, not residual.** It is commanded by **1.77° of azimuth
   error**, because the turn-rate bank law is φ = atan(ω·V/g) and V is **367 m/s**. `outR` is
   +0.016 — the stick is doing nothing; the loop is *holding* 42°. With the lift vector 42° off
   vertical the 10.6° of elevation error is left to a pushover that closes at ≈1.3 °/s, and 12 s is
   not enough.

This is the same false equilibrium `ChaseController.cs:2029` describes ("rolls to 90° then yaws the
nose down"), at 25–46° instead of ~85°. v0.85 removed the two positive-feedback paths that let it
reach 85°; it did not remove the equilibrium.

**Why this is a q finding, not an airframe finding.** The identical 0.14–1.97° of azimuth error
commands 1.1° of bank on trainer at 231 m/s and 42° on Multirole1 at 367 m/s, because the demand
scales with V. R² of V/Vcorner alone over the ten cells = **0.609**, slope **+5.9° of terminal error
per unit V/Vc**. That is the opposite of §3's result on the azimuth steps in the same captures
(R² of V/Vcorner = 0.000) — **one batch, two segments, one metric airframe-locked and one
speed-locked.**

**Confound, stated plainly:** V/Vc during `elDn40` is itself set by the airframe (a jet accelerates
harder off the same 90 m/s entry), so airframe and q are collinear here and the 0.609 cannot be
attributed cleanly. Four replicates per airframe all sit at the same speed, so there is no
within-airframe lever. **The capture that separates them: `stol-steps` with a throttle A/B**
(`Scenario/ScenarioThrottle` 0.25 vs 1.00, `arm_toggle`) — each airframe then flies `elDn40` at two
speeds and the within-airframe Δ answers it directly, exactly as R39-A used throttle on the oblique
legs.

---

## 5. ONE-LAW violations

### 5.1 `aoaFade` — confirmed, with the fleet numbers (`ChaseController.cs:1222`)

`aoaFade = Max(4f, Min(6f, 0.25f * lim))`, `aoaMargin = Min(4f, 0.15f * lim)`:

| airframe | `alphaLimiter` | `0.25·lim` | **aoaFade used** | which clamp |
|---|---|---|---|---|
| COIN, Darkreach, EW1, trainer | 10 | 2.50 | **4.00** | floor |
| CAS1 | 14 | 3.50 | **4.00** | floor |
| FastBomber1, VTOLTrainer1 | 15 | 3.75 | **4.00** | floor |
| SmallFighter1 | 25 | 6.25 | **6.00** | cap |
| Fighter1, Multirole1 | 27 | 6.75 | **6.00** | cap |

**The proportional form runs on exactly 0 of 10 airframes.** Seven are on the floor, three on the
cap. The comment at `:1223` says "for lim ≥ 16 the floor is INACTIVE, so FS-12 … and every jet with
lim ≥ 16 are byte-identical" — true, but no airframe in the roster is in `16 ≤ lim ≤ 24`, so the
statement describes an empty set and the schedule is a two-valued lookup on airframe class.

Concretely for a `lim = 10` airframe: `aoaCeil = 8.5°` and the fade opens at **4.5° AoA** — the gate
is partially closed across 47% of the airframe's usable AoA range. Measured: `aoaGU < 1` on
**12.9% (COIN), 21.8% (trainer), 52.5% (Darkreach)** of `stol-steps` samples, against **0.0%** for
Fighter1/Multirole1/SmallFighter1/VTOLTrainer1. `aoaLimiterActivePct` on `az30R`: Darkreach 100%,
CAS1 44.8%, trainer 35.2%, COIN 15.4%, EW1 14.1% — and **0.000% on all four high-limiter jets.**

`aoaMargin`'s `Min(4f, …)` clips only Fighter1/Multirole1 and only by 0.05°. Harmless; leave it.

### 5.2 `omegaMax *= Mathf.Max(0.3f, aoaGateUp)` (`ChaseController.cs:1296`) — measured biting

An absolute 0.3 floor on the achievability cap. Fraction of `turn360stol` samples sitting on it:
**Darkreach 24.3%**, **trainer 12.0%**, 0.0% for the other eight. Both are `alphaLimiter = 10`
airframes — i.e. the floor engages on precisely the low-limit class the ONE-LAW rule names, and it
does so as a constant rather than as a function of how much authority is actually left. Same
signature as 5.1, in a different clamp.

### 5.3 `qSched = Mathf.Clamp(qRatio, 0.3f, 1f)` (`ChaseController.cs:1152`) — armed, barely fired

At the card's *declared* entry (90 m/s, ρ ≈ 0.986 kg/m³ at 2500 m), qRatio = V²ρ/(Vc²·1.225) is
**below the 0.3 floor on 6 of 10 airframes** (Fighter1/Multirole1/VTOLTrainer1/CAS1 0.255,
SmallFighter1 0.271, FastBomber1 0.163). On the floor, a Fighter1 at 0.56 Vc and a FastBomber1 at
0.45 Vc receive an **identical** demand schedule — the floor erases the distinction the schedule
exists to make, in exactly the regime it exists for.

Measured occupancy is small only because §2 happened: `qSched ≤ 0.3005` on **1.9%** of FastBomber1's
`stol-steps` samples and **6.7%** of its sweep samples, 0.0% elsewhere. **This one is armed and
un-fired: a card that actually holds 90 m/s will land the whole fleet on it.** Fix 5.3 before
re-flying with a pinned throttle, or the re-fly measures the floor rather than the law.

### 5.4 `RateMaxDegS = 30` (`ScenarioPlayer.cs:1724`) — harness, and it invalidates the card's premise

`stol-sweep`'s note says it "asks for the same FRACTION of structural g on a trainer as on a jet, so
this is directly comparable to fixedwing-sweep in normalized terms". Measured derived rates: **4 of
10 lanes clip at exactly 30.0 °/s** (Fighter1 g=9 wants 33.5). Those four are **not** normalized to
their structural g, so the card's stated comparability does not hold for them. `SustainableTurnRate`
also ignores the lift limit entirely (§1.2), which is what makes the demand unachievable at 90 m/s
in the first place.

---

## 6. Harness defects (separate from law findings)

| # | defect | evidence | suggested shape |
|---|---|---|---|
| H1 | **An abort discards the lane's remaining replicates.** One bad replicate costs the other three. | 9 lanes × 3 lost replicates in this batch alone. `Finish` nulls `_queue`. | Advance `_qi` and re-place instead of nulling the queue on a *floor* abort (damage should still end the lane — a shed airframe is not comparable). |
| H2 | **`startSpeed` is not held.** No throttle authority in the card schema means a speed-conditioned card measures a different speed. | §2: 90 m/s commanded, 144–381 m/s flown. | Either pin `Scenario/ScenarioThrottle` in `stol-*` (one JSON line, cards own the mechanism already) or add a `holdSpeed` entry field. |
| H3 | **`bankClampActivePct` is unreliable, differently than reported.** It does **not** read 0.0% here — `targetBank` is being written — but it tracks a signal that has diverged from `bankTR`. | Darkreach sweep: `targetBank` pinned at ±72.0 on **70.7%** of samples while `bankTR` mean is **57.9** and reaches 71.5 on only **4.6%**. `targetBank` cannot be `clamp(bankTR)`. COIN: 97.3% vs 55.8%. | Confirm the brief's instruction: **use `bankTR` for every bank-rail question.** Measured `\|bankTR\| ≥ 71.5`: Fighter1 99.5%, Multirole1 99.4%, SmallFighter1 99.4%, VTOLTrainer1 80.9%, EW1 78.7%, COIN 55.8–73.5%, CAS1/FastBomber1 56.4%, trainer 49.4%, **Darkreach 4.6%**. |
| H4 | **`stol-sweep`'s `startAlt` is below what the maneuver costs.** | 1846–1963 m consumed against 2000 m of usable air. R27's `sweep-lowq` (150 m/s, **6000 m**) had 0/32 aborts. | `startAlt` ≥ 5000 m, or shorten the turn to 20 s. |
| H5 | **A fourth Darkreach damage abort, and the first that localizes the detach.** `rec 350` (`stol-steps` rep 4) aborts at **t=0.2 s, 4 rows, detached 0.029**, with `sc_detachedRatioAtStart = 0`. Rep 3 completed at t=3277.634; rep 4 aborted at t=3277.833. | Ledger #51 / `R39-F-darkreach-damage.md`, which reports the other three as airborne at 13.9–15.3 s. This one is not. | Hand to whoever owns #51: the detach window here is the **rep-3 → rep-4 boundary**, i.e. the end of a 204 m/s `elUp40` pull or the re-placement itself — a different window than R39-F's three. |

Metrics deliberately not used anywhere above, per the brief: `wobbleEpisodes*`, `wobbleFreqHz*`,
`authorityUsedFrac`/SLACK, and `bankClampActivePct`. For the record, `authorityUsedFrac` on the
sweep behaves exactly as warned — `authBank` = mean\|bank\|/72 with mean\|bank\| 32.7–56.5° gives
0.45–0.78, which reads as "plenty of authority left" on segments that are 99% pinned on the bank
demand. It carries no information; do not resurrect it.

---

## 7. Flight tests that would discriminate

1. **`stol-steps` with `Scenario/ScenarioThrottle` A/B (0.25 vs 1.00), same 10 lanes, repeat 4.**
   The one capture the whole report is missing. Pass = the low arm holds 90–120 m/s through
   `elUp40`; that alone makes it the first real STOL data. Then: does `elDn40 terminalOffDeg` fall
   with speed *within* each airframe? A within-airframe Δ of the same sign as §4's between-airframe
   slope (+5.9°/unit V/Vc) confirms the false-bank equilibrium is a q effect; a null says it is
   airframe identity after all and §4's R²=0.609 is the collinearity talking. Watch `tBankE` and
   `bank` in the last 4 s — the discriminating signal is the *commanded* bank, not the pointing error.
2. **Fix 5.3 (`qSched` floor) before running test 1,** or the low arm sits on the 0.3 floor for the
   entire card and measures the clamp. Suggested shape: keep the floor but make it a function of
   `_pitchEff` / measured achieved-vs-commanded rate rather than a literal — the estimator already
   exists (`ChaseController.cs:1163`).
3. **`stol-sweep` at `startAlt` 6000 m, 20 s turn.** Pass = 40/40 captures with 0 aborts, and
   `turnRateDemandRatio` still ≈1 (which will say the demand is still unachievable and the card
   needs a lift-limited `SustainableTurnRate`, not just more air). Compare directly against R27
   `sweep-lowq` (`turn360loq`, 32 segments, 0 aborts, terminalOff 56.5) — same shape, 150 m/s entry.
4. **`oblique-below` on the three worst airframes (Multirole1, SmallFighter1, Fighter1).** The card
   exists and has never been flown against this. It puts a 6° step 20° below the horizon, which
   separates *belowness* from *step size*: if the false bank equilibrium appears at a 6° step it is
   the suppressor's arming order; if it needs a 40° step it is the `bigTurn` taper handing back
   roll-and-pull at large error. Pass for the taper hypothesis = `\|bank\|` in the last 4 s stays
   under 5° on `oblique-below` while `elDn40` still shows 27–46°.
