# R39-B — `oblique-6-dwell` is not a state-stationary card and throttle cannot make it one; low q is not the route into the alpha regime, v0.98.1

**The two pinned-throttle arms flew.** `oblique-6-dwell-t040` (throttle 0.40) and
`oblique-6-dwell-t100` (throttle 1.00), 64 captures each, 128 total, **zero aborts, zero railed
segments, zero slack, zero unknown tags, zero parse warnings**, `n_cols = 69` uniform, `rec` 1..128
contiguous, one session. R37 backlog **#54a is discharged**. Both arms occupy **byte-identical lane
positions** (`origDist` 8.00…98.00 km, ratio t100/t040 = 1.000 on all 16 slots) and the datum reads
`(0, −4032, 0)` on every row of the batch, so the R37 §2 distance confound is controlled by
construction here rather than argued away.

Everything below is reproducible from the CSVs under `<game>/BepInEx` and `debugtests/captures.db`.
**Every claim states its n.** Both verdicts are **FAIL**, and in both cases the arithmetic the
criterion asked for is not the interesting part — the attribution is.

---

## Verdict

1. **CRITERION B: FAIL, and not narrowly.** Throttle 0.40 holds drift under 1.15× on **3 of 10**
   airframes (COIN 1.01, CAS1 0.96, FastBomber1 1.13) against the ≥8 the criterion required, and
   still runs **1.55× (Multirole1)** and **1.67× (Darkreach)**. t100 is worse on 10 of 10 (1.12–2.14).
   `oblique-6-dwell` **cannot be re-pinned to a stationary condition by throttle** and its four legs
   must be scored as four flight conditions from here on. §1, §2.
2. **The reason is not throttle authority — it is that the card commands a descent, and the descent
   is throttle-independent.** The demand is elevation-symmetric (leg dwell elevations 0, −4.24, 0,
   +4.24; mean 0), but "nose on the marker" at positive AoA is a flight path angle of −α. Measured
   descent over the four legs matches `∫V·sin(α − el_cmd)dt` at **Pearson +0.997 over all 128
   captures** (e.g. Fighter1 t040: −1016 m measured, −1034 m predicted). Every lane loses 221–1194 m,
   on **both** arms. At t040 that altitude supplies **41 % (Multirole1) to 107 % (Darkreach)** of the
   entire kinetic-energy gain. No throttle removes that term. §2.
3. **R37 §4's attribution is REFUTED: the drift is not ordered by thrust-to-weight.** Spearman(T/W,
   drift) = **+0.14** (t040), **−0.36** (t100), **−0.02** (R37 @ 0.70), n=8. Darkreach has the
   *lowest* T/W in the fleet (0.348) and the *largest* drift on all three throttles. The predictor
   that does hold, and holds on all three arms, is **Vmax/Vcorner** — the airframe's speed headroom
   above the entry the card pins — at **+0.84 / +0.83 / +0.68** (n=10). §3.
4. **The corpus's down/up asymmetry on this card is, in part, a slow/fast asymmetry — confirmed and
   quantified.** Up-step legs run at **1.01–1.42×** the speed of down-step legs at t040 and
   **1.09–1.61×** at t100. Every "down leg vs up leg" statement built on `oblique-6-dwell`,
   `oblique-6-c` or any sibling carries that confound. §4.
5. **CRITERION D: FAIL. Throttle 0.40 puts ONE airframe above 0 % limiter-active, not three.**
   Darkreach, 27.4 % mean (33.1 % over throttle-clean legs), 100 % on leg 1, `aoaPeakDeg` 5.61 mean /
   7.88 leg-1 against an 8.5° ceiling. The other nine read **0.00 %** and `aoaPeakOverCeiling`
   0.12–0.46. `aoaAboveCeilingPct` is **0.0 on all 512 legs of both arms**. §5.
6. **The eight `alpha_*` metrics are NULL on this card by construction, not by absence of signal.**
   `scorecard.alpha_metrics` is gated to `alpha_step`/`alpha_hold` (`scorecard.py:1011`, whose
   docstring says exactly this and warns against the misreading). The numbers in §5 were produced by
   calling `scorecard.alpha_metrics` directly on these `oblique_step` legs — one definition, not a
   re-spelling. §5.
7. **The Criterion D recommendation's cited evidence is WRONG, and the correct evidence points the
   other way.** R35's `alpha-steps` did **not** return `aoaAboveCeilingPct` = 0.0 on all 8: it
   returned **>0 on 2 of 8** and put **7 of 8 airframes on the limiter**, mean 33.2 %, max 96.3 % —
   at exactly the 8000 m / 250 m/s framing the criterion called "the expensive wrong one". The two
   ceiling crossings are trainer `alphaPush` **2.27 %** (n=8, **0 railed**, `aoaPeakDeg` 8.56 vs an
   8.5° ceiling, `aoaPeakOverCeiling` 1.024) and Darkreach `alphaPush` **3.00 %** (n=5, **1 railed**,
   `aoaPeakOverCeiling` 1.029 — read the trainer cell as the clean one). §6.
8. **Low q is a multiplier on demand, not a substitute for it.** The clean within-shape contrast is
   `fixedwing-sweep` (4000 m / 250 m/s) **2 of 4** airframes on the limiter → `sweep-lowq`
   (6000 m / 150 m/s, `deriveAzRate` identical) **4 of 4** at 78.7–97.7 %. Same stimulus, lower q,
   two more airframes recruited — including both 27°-ceiling jets. Against that, a 6° diamond gets
   1 of 10 at throttle 0.40, 0.70 **and** 1.00 alike. §6.
9. **`alpha-sweep` should be re-issued — but by lowering its SPEED, not its altitude, and the fix is
   `startSpeedCorner`.** It already flies at 8000 m; its q is ~2.2× `sweep-lowq`'s because it enters
   at 250 m/s against sweep-lowq's 150. Its absolute 250 m/s also refuses CAS1 and COIN pre-spawn. §6.
10. **NEW DEFECT, batch hygiene: a card's `config` override is a process-global pin, and a lane that
    finishes un-pins it under the 15 lanes still flying.** 61 of 512 legs carry rows at the wrong
    commanded throttle, in a signature that is a perfect echo of the 3 s launch stagger. It reaches
    the 7–8 s scoring window on exactly 2 legs (both FastBomber1) and changes no verdict here, but it
    will corrupt any future concurrent-fleet card whose override matters. §7.
11. **`terminalOffDeg` was not used anywhere in this document**, and neither was `off`. Nothing here
    is ranked on a pointing metric.

---

## 1. The V/Vcorner table — the durable artifact

Mean `spd` / that airframe's probed `fbwCornerSpeed`, over `tSeg` ∈ [7, 8) s of each leg, pooled over
that airframe's lanes and its 4 replicates. Legs in flight order: **`obDR6*` is leg 1, `obUR6*` is
leg 4.** Ordered by leg-1 t040 speed. Two windows are excluded for throttle contamination (§7), both
FastBomber1 leg 4 — so FastBomber1's UR cells are n=3, not n=4.

| airframe | Vcorner | **t040** L1 `DR` | L2 `DL` | L3 `UL` | L4 `UR` | **drift** | **t100** L1 `DR` | L2 `DL` | L3 `UL` | L4 `UR` | **drift** | n/cell |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| COIN | 110 | 0.87 | 0.92 | 0.98 | 0.88 | **1.01** | 0.95 | 1.08 | 1.16 | 1.06 | **1.12** | 4 |
| CAS1 | 160 | 0.90 | 0.90 | 0.94 | 0.86 | **0.96** | 1.01 | 1.16 | 1.27 | 1.22 | **1.21** | 8 |
| FastBomber1 | 200 | 0.95 | 1.03 | 1.12 | 1.08 | **1.13** | 1.18 | 1.68 | 2.09 | 2.25 | **1.91** | 4 / 3 |
| VTOLTrainer1 | 160 | 1.04 | 1.25 | 1.38 | 1.35 | **1.30** | 1.28 | 1.80 | 2.00 | 1.97 | **1.54** | 8 |
| trainer | 130 | 1.06 | 1.33 | 1.50 | 1.46 | **1.37** | 1.31 | 1.90 | 2.18 | 2.16 | **1.65** | 8 |
| EW1 | 130 | 1.09 | 1.38 | 1.58 | 1.55 | **1.42** | 1.33 | 1.93 | 2.22 | 2.21 | **1.66** | 4 |
| Fighter1 | 160 | 1.11 | 1.41 | 1.56 | 1.52 | **1.36** | 1.57 | 2.35 | 2.61 | 2.57 | **1.64** | 8 |
| SmallFighter1 | 155 | 1.15 | 1.51 | 1.71 | 1.70 | **1.48** | 1.56 | 2.41 | 2.75 | 2.74 | **1.75** | 8 |
| Multirole1 | 160 | 1.16 | 1.56 | 1.80 | 1.81 | **1.55** | 1.61 | 2.59 | 3.00 | 3.02 | **1.88** | 8 |
| Darkreach | 100 | 1.10 | 1.43 | 1.76 | 1.83 | **1.67** | 1.32 | 2.14 | 2.75 | 2.82 | **2.14** | 4 |

Fleet span at the window: t040 **0.87–1.16** (leg 1) → **0.86–1.83** (leg 4); t100 **0.95–1.61** →
**1.06–3.02**. The R37 anchor at 0.70 read 0.94–1.35 → 1.03–2.49.

**A lane-position table adds nothing and that is itself a result.** The six two-lane airframes agree
between their near lane (8–38 km) and far lane (68–98 km) to within **±0.01 V/Vcorner and ±0.01
drift** on every cell. Speed drift is not a lane-distance effect.

**Replicate 1 is not a distinct stratum for this measurement.** Flown from the spawn with
`snapBackM = 0` against 12 098–52 269 m for replicates 2–4, it nonetheless returns the same drift:
per-airframe |Δ| ≤ 0.06 on both arms (largest, COIN t040: 1.12 vs 0.98 at n=1 vs n=3). Pooled
throughout, deliberately, and checked before pooling.

---

## 2. Criterion B — FAIL, and why throttle is not the lever

```
PASS required: drift < 1.15x on >= 8 of 10 airframes at t040
MEASURED:      drift < 1.15x on    3 of 10   (COIN 1.01, CAS1 0.96, FastBomber1 1.13)
               drift > 1.50x on    2 of 10   (Multirole1 1.55, Darkreach 1.67)
t100:          drift < 1.15x on    1 of 10   (COIN 1.12);  > 1.50x on 8 of 10;  range 1.12-2.14
```

Three independent reasons this is structural, not a matter of picking a better throttle.

**(a) At 0.40 the fleet already straddles.** CAS1 **decelerates** (0.96) and Darkreach still gains
**1.67×**, in the same batch, at the same pinned throttle. A scalar throttle has one degree of
freedom against ten constraints that are already in conflict at the value under test. Lowering it
further pushes CAS1/COIN deeper into deceleration without bringing Darkreach down.

**(b) The three-throttle curve is monotone on 10 of 10 and extrapolates to nothing usable.** With
R37's 0.70 arm as the middle point:

| airframe | 0.40 | 0.70 | 1.00 | linear throttle for drift = 1 |
|---|---|---|---|---|
| COIN | 1.01 | 1.09 | 1.12 | 0.32 |
| CAS1 | 0.96 | 1.09 | 1.21 | 0.49 |
| FastBomber1 | 1.13 | 1.42 | 1.91 | 0.30 |
| VTOLTrainer1 | 1.30 | 1.50 | 1.54 | **−0.36 — unreachable** |
| trainer | 1.37 | 1.57 | 1.65 | **−0.42 — unreachable** |
| Fighter1 | 1.36 | 1.48 | 1.64 | **−0.38 — unreachable** |
| Darkreach | 1.67 | 2.03 | 2.14 | **−0.47 — unreachable** |
| Multirole1 | 1.55 | 1.75 | 1.88 | **−0.61 — unreachable** |
| SmallFighter1 | 1.48 | 1.55 | 1.75 | **−0.63 — unreachable** |
| EW1 | 1.42 | 1.60 | 1.66 | **−0.64 — unreachable** |

Monotone on all ten is the strong part — throttle *is* the right lever direction, and the two
batches agree well enough across a mod-version boundary to sit on one curve. **Seven of ten would
need negative throttle.** The linear form is not trusted below 0.40 (drag goes as V², so the curve
flattens); it is quoted as the bound it is, and (c) is why the true answer is not merely "lower than
0.40" but "not available at any throttle".

**(c) The card commands a descent, and the descent is throttle-independent.** The four legs dwell at
elevations 0, −4.24, 0, +4.24 — mean **zero**, so the demand is elevation-symmetric and the card
looks altitude-neutral. It is not: chasing a marker with the nose means a flight path angle of
`el_cmd − α`, so the mean flight path over the card is **−α**. Integrating `V·sin(α − el_cmd)` over
the four legs reproduces the measured altitude loss at **Pearson +0.997 across all 128 captures**:

| airframe | arm | measured Δalt | predicted | mean α | arm | measured Δalt | predicted | mean α |
|---|---|---|---|---|---|---|---|---|
| COIN | t040 | −568 | −581 | 2.69° | t100 | −482 | −496 | 1.95° |
| CAS1 | t040 | −1194 | −1208 | 3.97° | t100 | −802 | −823 | 2.12° |
| FastBomber1 | t040 | −1019 | −1054 | 2.40° | t100 | −530 | −519 | 1.01° |
| VTOLTrainer1 | t040 | −616 | −636 | 1.56° | t100 | −221 | −249 | 0.50° |
| trainer | t040 | −684 | −694 | 1.97° | t100 | −467 | −463 | 1.02° |
| EW1 | t040 | −477 | −488 | 1.36° | t100 | −316 | −316 | 0.71° |
| Fighter1 | t040 | −1016 | −1034 | 2.24° | t100 | −581 | −614 | 0.89° |
| SmallFighter1 | t040 | −876 | −906 | 1.94° | t100 | −488 | −544 | 0.84° |
| Multirole1 | t040 | −712 | −738 | 1.53° | t100 | −349 | −388 | 0.62° |
| Darkreach | t040 | −1156 | −1161 | 3.93° | t100 | −861 | −818 | 2.16° |

The share of the leg-1→leg-4 kinetic-energy gain that the altitude loss alone could have paid for,
at t040: Darkreach **1.07**, trainer 0.96, VTOLTrainer1 0.85, Fighter1 0.84, EW1 0.66, SmallFighter1
0.57, Multirole1 0.41. CAS1's KE gain is *negative* despite losing 1194 m — it is drag-limited, which
is why it is one of the three "passing" lanes and why its pass is not evidence the card works.

Cutting throttle raises α (less thrust ⇒ more lift needed from incidence), which raises the sink
rate, which feeds speed back. That is the loop that makes the low-throttle arm fail to be slower in
proportion, and it closes off the fix.

**What would have refuted this** and was tested: if the drift were a pure approach to a
throttle-set trim speed, then (i) some throttle would null it for everyone, and (ii) the descent
would scale with throttle. Neither holds — (i) is (a)+(b), and for (ii) the altitude loss is within
a factor of ~2 across a 2.5× throttle range while drift spans 0.96–2.14. A second refutation test:
if the fleet were still accelerating without bound at leg 4, the drift would be an artifact of leg
count rather than a property of the condition. It is not — **leg4/leg3 is 0.90–1.04 on t040 and
0.92–1.08 on t100**, i.e. the fleet has essentially reached its throttle asymptote by leg 3. The
drift is the transit from the pinned entry speed to a per-airframe trim speed, and it is complete
within the capture.

**Consequence.** The card's four legs are four flight conditions. Score them as four, or state the
speed at the window beside any number taken from them. R37 backlog **#54b is now mandatory, not
advisory.**

---

## 3. What orders the drift — R37 §4's thrust-to-weight claim is refuted

Spearman against per-airframe drift, on all three throttle arms:

| predictor | t040 | t100 | R37 @ 0.70 | n |
|---|---|---|---|---|
| **Vmax / Vcorner** | **+0.842** | **+0.830** | **+0.681** | 10 |
| mass | +0.455 | +0.818 | +0.462 | 10 |
| Vmax | +0.298 | +0.565 | +0.207 | 10 |
| −Vcorner | +0.383 | −0.006 | +0.387 | 10 |
| **thrust-to-weight** | **+0.143** | **−0.357** | **−0.024** | 8 |

R37 §4 item 2 stated "the size of the drift is set by thrust-to-weight, which is an airframe
property". It does not reproduce on either new arm and does not reproduce on R37's own data when
tested directly. The counterexample is visible without statistics: **Darkreach has the lowest T/W in
the fleet (0.348) and the largest drift on all three throttles (1.67 / 2.03 / 2.14).**

`Vmax/Vcorner` is the speed headroom between where the card pins the entry (0.95× corner) and where
the airframe's drag polar wants to sit. That is the right shape for the mechanism in §2(b): the
drift is a transit to trim, and its size is how far the entry is from trim.

**Not identified, stated rather than omitted.** With n=10, `Vmax/Vcorner` cannot be separated from
mass (+0.82 on t100) or from any other collinear airframe property. `maxThrustN` is NULL in the
sidecar for CAS1 and COIN, so the T/W row is n=8 and its two most-decelerating lanes are missing —
the refutation of T/W is therefore "does not reproduce on the 8 airframes that have the number",
not "is proven absent on 10". What is safe is the negative: **no one may now cite thrust-to-weight
as the driver of this card's drift without re-deriving it.**

---

## 4. The down/up confound, quantified

Legs 1–2 step *down* (`obDR6`, `obDL6`), legs 3–4 step *up* (`obUL6`, `obUR6`). Mean V/Vcorner at
the window, down pair vs up pair:

| airframe | t040 down | t040 up | ratio | t100 down | t100 up | ratio |
|---|---|---|---|---|---|---|
| COIN | 0.89 | 0.93 | 1.04 | 1.02 | 1.11 | 1.09 |
| CAS1 | 0.90 | 0.90 | 1.01 | 1.08 | 1.24 | 1.15 |
| FastBomber1 | 0.99 | 1.10 | 1.11 | 1.43 | 2.17 | 1.52 |
| VTOLTrainer1 | 1.14 | 1.37 | 1.20 | 1.54 | 1.98 | 1.29 |
| Fighter1 | 1.26 | 1.54 | 1.22 | 1.96 | 2.59 | 1.32 |
| trainer | 1.19 | 1.48 | 1.24 | 1.61 | 2.17 | 1.35 |
| EW1 | 1.24 | 1.57 | 1.26 | 1.63 | 2.22 | 1.36 |
| SmallFighter1 | 1.33 | 1.70 | 1.28 | 1.99 | 2.74 | 1.38 |
| Multirole1 | 1.36 | 1.80 | 1.32 | 2.10 | 3.01 | 1.44 |
| Darkreach | 1.26 | 1.80 | 1.42 | 1.73 | 2.79 | 1.61 |

Throttle 0.40 shrinks the confound (1.01–1.42×) relative to 0.70 and 1.00 but does not remove it,
and it remains **ordered by the same airframe axis as the drift itself** — so the down/up contrast
and the between-airframe contrast are confounded with each other as well as with speed. The corpus's
down/up asymmetry on this card family is **partly a slow/fast asymmetry**; how much cannot be
decided from any batch flown so far, because no arm of this card holds speed.

---

## 5. Criterion D — FAIL

```
TRIGGER required: >= 3 airframes above 0% aoaLimiterActivePct on t040
MEASURED:            1 airframe   (Darkreach).  Same 1 on t100, and same 1 on R37's 0.70 anchor.
```

`aoaCeilDeg` = `alphaLimiter − min(4, 0.15·alphaLimiter)` (`scorecard.aoa_ceiling`), computed here
because the column is NULL on `oblique_step` — see verdict 6. All eight `alpha_*` metrics below were
produced by calling `scorecard.alpha_metrics` on these legs directly.

**t040** (n = legs; 32 for two-lane airframes, 16 for one-lane):

| airframe | n | alphaLimiter | ceiling | limiter-active % | max | `aoaPeakDeg` | above-ceiling % | peak/ceiling | cmdIntoCeiling % | `qSchedMin` | recover % |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Darkreach** | 16 | 10 | **8.50** | **27.43** | **100.0** | **5.61** | 0.00 | **0.929** | 0.00 | 0.411 | 0.00 |
| CAS1 | 32 | 14 | 11.90 | 0.00 | 0.0 | 4.77 | 0.00 | 0.463 | 0.00 | 0.470 | 0.00 |
| Fighter1 | 32 | 27 | 23.00 | 0.00 | 0.0 | 3.81 | 0.00 | 0.187 | 0.00 | 0.715 | 0.00 |
| FastBomber1 | 16 | 15 | 12.75 | 0.00 | 0.0 | 3.44 | 0.00 | 0.332 | 0.00 | 0.613 | 0.00 |
| SmallFighter1 | 32 | 25 | 21.25 | 0.00 | 0.0 | 3.40 | 0.00 | 0.195 | 0.00 | 0.735 | 0.00 |
| COIN | 16 | 10 | 8.50 | 0.00 | 0.0 | 3.24 | 0.00 | 0.444 | 0.00 | 0.411 | 0.00 |
| trainer | 32 | 10 | 8.50 | 0.00 | 0.0 | 3.17 | 0.00 | 0.454 | 0.00 | 0.675 | 0.00 |
| VTOLTrainer1 | 32 | 15 | 12.75 | 0.00 | 0.0 | 2.88 | 0.00 | 0.261 | 0.00 | 0.660 | 0.00 |
| Multirole1 | 32 | 27 | 23.00 | 0.00 | 0.0 | 2.77 | 0.00 | 0.172 | 0.00 | 0.750 | 0.00 |
| EW1 | 16 | 10 | 8.50 | 0.00 | 0.0 | 2.19 | 0.00 | 0.325 | 0.00 | 0.698 | 0.00 |

**t100**, same shape: Darkreach 10.17 % (max 43.1), `aoaPeakDeg` 3.86, peak/ceiling 0.867; **all nine
others 0.00 %**, `aoaPeakDeg` 1.35–3.44, peak/ceiling 0.123–0.415, `qSchedMin` 0.444–1.000 (Fighter1,
SmallFighter1 and Multirole1 read exactly **1.000** — the demand schedule is completely inert there).

**Throttle is a real but weak lever on AoA, and it is monotone.** Darkreach's limiter occupancy runs
**10.17 % (1.00) → 15.02 % (0.70, R37) → 27.43 % (0.40)** and every airframe's `aoaPeakDeg` rises
from t100 to t040 (Multirole1 1.47 → 2.77, Fighter1 2.24 → 3.81, CAS1 3.44 → 4.77). The full 1.00 →
0.40 throttle range buys roughly **×1.4 in peak AoA**. The second-placed airframe (CAS1) sits at
**0.463 of its ceiling** and needs ×2.2. The lever is real and an order of magnitude short.

**The R37 baseline figure was a leg-1 number, not an airframe number** — worth pinning, because it
is the sort of mismatch that produces a fake regression. R37's Darkreach reads 60.09 % on `obDR6`
alone and **15.02 %** averaged over its four legs. R39 t040's Darkreach reads **100.00 %** on leg 1
and 27.43 % over four. Compared like for like, throttle 0.40 roughly **doubled** it on both
denominators.

**`commandIntoCeilingPct` = 0.00 on all 512 legs of both arms — and that is NOT evidence about the
suspected law defect.** The metric counts samples where a ceiling gate is at least half shut *and*
the raw pre-gate command is still pushing into it. Nine of ten airframes never closed a gate at all,
so the metric is structurally zero by non-exposure. Reporting it as "the defect did not reproduce"
would be exactly the failure this document is written against. **Where the regime was actually
provoked — R35's `alpha-steps` — the pattern does appear**, and it survives normalising by exposure
(`cmdInto` / `limiterActive`):

| airframe | ceiling | tag | limiter % | cmdInto % | cmdInto / limiter |
|---|---|---|---|---|---|
| trainer | 8.5 | alphaPull | 83.75 | **29.14** | **0.348** |
| EW1 | 8.5 | alphaPush | 56.07 | 19.38 | **0.346** |
| Darkreach | 8.5 | alphaPush | 51.23 | 14.38 | **0.281** |
| trainer | 8.5 | alphaPush | 56.33 | 15.47 | **0.275** |
| FastBomber1 | 12.75 | alphaPush | 14.38 | 0.94 | 0.065 |
| Darkreach | 8.5 | alphaPull | 96.00 | 3.38 | 0.035 |
| FastBomber1 | 12.75 | alphaPull | 67.73 | 0.78 | 0.012 |
| VTOLTrainer1 | 12.75 | alphaPull | 54.45 | 0.00 | **0.000** |
| SmallFighter1 | 21.25 | alphaPush | 16.41 | 0.00 | **0.000** |
| Fighter1 | 23.0 | alphaPush | 8.20 | 0.00 | **0.000** |
| Fighter1 | 23.0 | alphaPull | 3.75 | 0.00 | **0.000** |
| Multirole1 | 23.0 | both | 0.00 | 0.00 | n/a |

The 8.5°-ceiling group runs 0.28–0.35 of its gated samples commanding into the ceiling; VTOLTrainer1
and FastBomber1 had **substantial** gate activity (54 %, 68 %) and still read ~0. So the signal is
not merely "the low-ceiling airframes got more exposure" — it survives that control. The prompt's
">25 %" threshold is met by one cell (trainer alphaPull, 29.14 %), not by the group. **Not
identified:** the 8.5° group is exactly {trainer, EW1, Darkreach}, n=3, and `alphaLimiter` is also
what the law's own demand schedule keys off, so "low ceiling" and "the schedule is deep in its range"
are the same three aircraft. This is a lead, on R35 evidence, and R39 says nothing about it either way.

---

## 6. Where the alpha regime actually lives — corpus-wide

Per card, how many of its airframes ever register non-zero `aoaLimiterActivePct`:

| card | entry | stimulus | airframes on limiter | mean % | max % |
|---|---|---|---|---|---|
| `alpha-steps` | 8000 m / 250 m/s (V/Vc **1.25–2.50**) | ±45° el steps | **7 of 8** | 33.17 | 96.3 |
| `sweep-lowq` | 6000 m / 150 m/s (V/Vc 0.94–1.15) | sustained 360, `deriveAzRate` | **4 of 4** | 89.22 | 97.9 |
| `fixedwing-sweep` | 4000 m / 250 m/s | sustained 360, `deriveAzRate` | 2 of 4 | 22.43 | 100.0 |
| `oblique-6-c` | 4000 m / 0.95× corner, thr 0.70 | 6° diamond, 8 s legs | 2 of 10 | 1.71 | 100.0 |
| **`oblique-6-dwell-t040`** | 4000 m / 0.95× corner, **thr 0.40** | 6° diamond, 30 s legs | **1 of 10** | 1.71 | 100.0 |
| `oblique-6-dwell` | same, thr 0.70 | same | 1 of 10 | 0.54 | 63.0 |
| `oblique-6-dwell-t100` | same, thr 1.00 | same | 1 of 10 | 0.64 | 43.1 |

Two contrasts do the work, and they point in opposite directions from the one the criterion assumed.

**Demand dominates.** `alpha-steps` runs at the *highest* V/Vcorner of anything in the table
(1.25–2.50) and recruits the most airframes, because its demand is a ±45° elevation step. The three
`oblique-6-dwell` arms run at the *lowest* V/Vcorner and recruit one airframe between them, because
their demand is a 6° diamond. **Throttle cannot buy what the stimulus does not ask for.**

**Low q is nonetheless a genuine multiplier, on a card that asks.** `fixedwing-sweep` → `sweep-lowq`
is the same `deriveAzRate` stimulus at 4000 m / 250 m/s vs 6000 m / 150 m/s, and it goes **2 of 4 →
4 of 4**, recruiting both 27°-ceiling jets (Fighter1 91.1 %, Multirole1 89.3 %, `aoaPeakDeg` 19.58 /
18.84 against a 23° ceiling). **Caveat that must travel with that number: all 8 of 8 `sweep-lowq`
segments are `railed = 1`.** They prove exposure to the regime; they are *no signal* for scoring the
law in it.

**`alpha-sweep` recommendation, revised.** The criterion proposed re-issuing it "as a low-throttle or
lower-`startSpeedCorner` card" on the strength of a claim about `alpha-steps` that is false (verdict
7). The recommendation survives anyway, but for a different reason and with a different fix:

- `alpha-sweep` is `sweep-lowq`'s stimulus (`deriveAzRate: true`, 35 s) at **8000 m / 250 m/s**.
  Its altitude is already the lowest-density condition in the card set; **its q is ~2.2× `sweep-lowq`'s
  because of its speed**, not its altitude. Lowering altitude further is the wrong knob.
- Give it **`startSpeedCorner`** rather than a lower absolute speed. At 250 m/s absolute it also
  refuses CAS1 (0.95× Vmax = 195.3) and COIN (134.6) pre-spawn, so the roster it can fly is
  arbitrarily 8 rather than 10; a corner-relative entry fixes the regime and the roster in one field.
  `sweep-lowq`'s proven 150 m/s is 0.94–1.15× corner across its four lanes.
- **Expect it to rail.** Its sibling railed 8 of 8. Budget a demand fraction below the bank clamp, or
  accept that the card measures exposure and not quality.

---

## 7. NEW: a card `config` override does not survive a concurrent fleet

`ScenarioPlayer.ApplyOverrides` pins a **process-global** `ConfigEntry`; `RestoreOverrides` writes it
back after `_rec.Stop`. With 16 lanes flying one card on a 3 s launch stagger, lane 1 reaches its card
boundary first and restores the global **under the 15 lanes still flying it**. The recorded `thr`
column shows it exactly:

```
t040 arm: 61 legs carry rows at thr=0.70 (the un-pinned default);  t100: rows at thr=0.40.
Signature: one WALL-CLOCK event per replicate boundary, landing at each lane's own tSeg,
           stepping down by exactly 3.00 s per lane index == DroneStaggerSec.
  lane 11 obUR6t04  tSeg  0.05-2.98      lane 14 obUL6t04  tSeg 21.05-23.98
  lane 12 obUL6t04  tSeg 27.05-29.98     lane 15 obUL6t04  tSeg 18.07-21.00
  lane 13 obUL6t04  tSeg 24.05-26.98     lane 16 obUL6t04  tSeg 15.07-18.00
Lane 1 / lane 17 are never contaminated -- they are the lanes doing the restoring.
Non-final replicates leak exactly 1 row (NextCard restores then immediately re-pins);
the FINAL replicate leaks 48 rows = 3.00 s (Finish restores for good).
```

Totals: **1469 rows over 61 of 512 legs**, all in legs 3 and 4, never in `arm`, legs 1 or 2.
**32 rows reach the 7–8 s scoring window, on 2 legs — both FastBomber1 leg 4, one per arm.** Both
were excluded from §1; doing so moves FastBomber1's drift by 0.00 (t040 1.13 → 1.13) and 0.00
(t100 1.91 → 1.91), so **no verdict in this document depends on the leak**. It is reported because
whole-leg metrics on those 61 legs carry 10 % of their samples at the wrong throttle, and because the
next card that pins something the law reads will not be so lucky.

Not tested here: whether `ApplyOverrides`' save/restore also nests wrongly (lanes 2–16 save the
*already-pinned* value, so their restores are no-ops that happen to write the right number). The
observed 3 s alternation is consistent with that reading.

---

## 8. Refutation tests run

| conclusion | what would refute it | result |
|---|---|---|
| B fails: throttle cannot make the card stationary | some throttle nulls the drift fleet-wide | at 0.40 the fleet **straddles** (0.96…1.67); 3-point curve extrapolates to negative throttle on 7 of 10 |
| the drift is a transit to trim, not unbounded acceleration | leg4/leg3 ≫ 1 | leg4/leg3 = **0.90–1.04** (t040), 0.92–1.08 (t100) — asymptote reached by leg 3 |
| the descent is geometric, not throttle-related | descent scales with throttle | descent is −221…−1194 m on **both** arms; `∫V·sin(α−el)` predicts it at **r = +0.997** over 128 captures |
| the drift is not thrust-to-weight | Spearman(T/W, drift) high and stable | **+0.14 / −0.36 / −0.02** on the three arms; lowest-T/W airframe has the highest drift |
| the drift is not a lane-distance artifact | near and far lanes of one airframe disagree | six two-lane airframes agree to **±0.01** on every cell; `origDist` identical across arms |
| replicate 1 is not its own stratum here | rep-1 drift differs from reps 2–4 | per-airframe abs delta ≤ 0.06 on both arms |
| the throttle leak does not carry the result | excluding contaminated windows moves a verdict | moves FastBomber1's drift by 0.00 on both arms; 2 legs of 512 affected |
| D fails: low q is not the route | ≥3 airframes recruited at t040 | **1** — the same one at 0.40, 0.70 and 1.00 |
| low q is still a real lever | AoA flat or non-monotone across throttle | **monotone on 10 of 10**; Darkreach limiter 10.2 → 15.0 → 27.4 % |
| demand, not q, is the dominant term | a low-q low-demand card recruits as well as a high-q high-demand one | `alpha-steps` (V/Vc 1.25–2.50) **7 of 8** vs `oblique-6-dwell-t040` (V/Vc 0.87–1.16) **1 of 10** |
| `cmdIntoCeiling` = 0 here is not a clean bill | the metric could have been non-zero | 9 of 10 airframes never closed a gate — structurally zero, reported as non-exposure |
| the cmdInto pattern is not just exposure | it vanishes when normalised by limiter occupancy | survives: 0.28–0.35 for ceiling-8.5 vs 0.00 for VTOLTrainer1/FastBomber1/Fighter1 at 8–68 % occupancy |

---

## 9. Confounds not ruled out

- **`Vmax/Vcorner` vs mass vs every other airframe property.** n=10. `Vmax/Vcorner` is the only
  predictor stable across all three throttle arms, but mass reaches +0.82 on t100 and cannot be
  separated. The *negative* result (T/W) is the safe one.
- **T/W is n=8.** `sc_maxThrustN` is NULL for CAS1 and COIN — the two lanes that decelerate. The
  T/W refutation covers the eight airframes that have the number.
- **R37's 0.70 arm is a different batch and a different mod version** (v0.97.2 vs v0.98.1) and the
  anchor card, `repeat` 8 vs 4. It is used as the middle point of the throttle curve. Monotonicity
  on 10 of 10 is the evidence that it belongs on the same curve; it is not proof.
- **Ten fixed-wing keys.** Nothing here says anything about rotorcraft or STOL, and two of the four
  airframe cases the ONE-LAW rule names still have no data on this law.
- **One geometry.** A 6° oblique diamond. The §6 conclusion that demand dominates rests on
  cross-*card* comparison, which is not a controlled contrast — `alpha-steps` differs from
  `oblique-6-dwell-t040` in altitude, speed, leg length and demand at once. The controlled contrast
  is `fixedwing-sweep` → `sweep-lowq` (q only), and it supports q as a *multiplier*, not as sufficient.
- **`sweep-lowq`'s 4-of-4 is 8-of-8 railed.** Exposure, not quality.
- **`dmgFrac` is not a damage exclusion.** Per R37 §5 it read 0.0 on a capture the harness aborted
  for a detached ratio of 0.114. No claim here excludes damage; the batch simply has zero aborts.
- **The alpha ceiling is the MOD's ceiling** (`alphaLimiter − min(4, 0.15·alphaLimiter)`), not the
  game's. Per CLAUDE.md the game FBW's own alpha limiter is gated inactive above corner q, which is
  where most of this batch flies.

---

## 10. Backlog

- **#55a — `oblique-6-dwell` is retired as a between-airframe ranking instrument.** It may still be
  used as a within-lane, within-airframe A/B (the arms are byte-identical and the drift is
  reproducible to ±0.01 across lanes), but no score from it is a property of the airframe alone.
  Supersedes #54b, which asked only for a caveat.
- **#55b — a state-stationary oblique card needs a speed HOLD, not a pinned throttle.** The card
  system has no closed-loop speed control; `ScenarioThrottle` is open loop. Either add one, or accept
  four flight conditions and record V/Vcorner per leg as a first-class scored quantity.
- **#55c — cheaper interim fix: shorten the legs.** `oblique-6-c` (8 s legs, same geometry, same
  entry) recruits 2 of 10 airframes into the alpha regime against `oblique-6-dwell`'s 1, precisely
  because there is less time to accelerate away. Leg length trades settling room against state
  stationarity, and R37 §2's `settleTime95` median of 15.5 s says 8 s is too short. There is no
  free point on that axis without a speed hold.
- **#55d — `ApplyOverrides` must be per-aircraft, or `RestoreOverrides` must be refcounted.** §7. A
  process-global pin restored by the first lane to finish is a silent corruption of every concurrent
  lane. Cheapest correct fix: refcount the pin and restore only when the last holder releases.
- **#55e — re-issue `alpha-sweep` with `startSpeedCorner`, not a lower altitude.** §6. Its q problem
  is its 250 m/s, not its 8000 m; and the absolute entry arbitrarily excludes CAS1 and COIN.
- **#55f — widen `alpha_metrics`' gate beyond `alpha_step`/`alpha_hold`, or accept the workaround.**
  `scorecard.py:1011` already documents that the regime is provoked on `oblique_step` while the eight
  metrics are computed only for `alpha_*`. This document had to call the function by hand. Widening
  the gate to every segment (as `aoa_g_metrics` already is) costs one line and a `--rebuild`.
- **#55g — the cmdIntoCeiling / ceiling-height lead is R35's, and needs a card that provokes the
  regime on BOTH ceiling groups to test.** §5. `sweep-lowq` is the only card in the corpus that gets
  a 23°-ceiling airframe onto its limiter, and it rails.
