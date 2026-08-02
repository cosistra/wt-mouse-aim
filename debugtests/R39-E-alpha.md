# R39 `alpha-sweep` — the tag resolved, the card reached the guard but never the ceiling, and the guard's onset is set by two absolute-degree constants, v0.98.1

**The first `alpha_hold` data that has ever existed.** 61 captures on card `alpha-sweep`, 8 lanes,
`repeat: 8`, absolute entry 250 m/s at 8000 m, session 2026-08-02. One Darkreach abort (airframe
damage, another agent's topic) costs that lane 4 replicates. **60 scorable `alpha_hold` segments.**

`run_tag` R39 covers five cards; everything below filters `c.card = 'alpha-sweep'` and nothing is
pooled across cards except the deliberate `alpha-steps` contrast in §4.

---

## Verdict

1. **The tag vocabulary did NOT drift. `alphaHold` → `alpha_hold` resolved on all 61 segments,
   `unknown_tag = 0`,** and all nine alpha metric columns materialised (`aoaCeilDeg`,
   `aoaAboveCeilingPct`, `aoaPeakOverCeiling`, `gateMinUp`, `gateMinDn`, `qSchedMin`,
   `aoaRecoverActivePct`, `aoaRecoverPeak`, `commandIntoCeilingPct`). The `cards/*.json` ↔
   `scorecard.py` pair that has silently drifted before is intact here. §1.
2. **The gate PASSES: `aoaLimiterActivePct` > 0 on 5 of 8 lanes** (trainer 96.3, VTOLTrainer1 90.8,
   Darkreach 93.5, FastBomber1 71.6, EW1 25.2; SmallFighter1 / Fighter1 / Multirole1 exactly 0.0). §2.
3. **But `aoaAboveCeilingPct` = 0.0 on 60 of 60 segments.** By the card's *own* stated pass criterion
   ("Pass = aoaAboveCeilingPct > 0 on alphaHold") the card failed. It reached the **guard band** on
   five lanes and the **ceiling** on none. Peak AoA is 0.447–0.856 of ceiling. §2.
4. **ALL 60 segments are RAILED — on bank clamp (74–97%), turn-rate cap (85–97%) and blend rail
   (81–96%), never on AoA.** Per `CAPTURES-DB.md` that is NO SIGNAL. Every alpha number in this
   document is read off a turn that is pinned against three *other* limits. This is the dominant
   confound and it is not removable from this batch. §2, §7.
5. **THE ONE-LAW FINDING, and it is provable from source before any data is consulted: the AoA
   guard's switch-on point, expressed in each airframe's own ceiling, spans 0.529 → 0.739 across the
   roster — a 40% spread produced entirely by two absolute-degree clamps.** Under the unclamped
   proportional expression it would be a constant **0.7059 for every airframe**. **Not one of the
   eight airframes on this card runs the unclamped expression**: the proportional form is live only
   for `alphaLimiter` ∈ [16, 24], and the roster is 10, 10, 10, 15, 15, 25, 27, 27. §5.
6. **The predicted bite-set matches the measured one 8/8, and the quantitative model reproduces
   `gateMinUp` to ±0.024 on 7 of 8 lanes.** `clamp01((ceil − |aoa|)/fade)` against the recorded
   minimum: trainer 0.445 vs 0.439, EW1 0.947 vs 0.933, Darkreach 0.739 vs 0.735, VTOLTrainer1 0.469
   vs 0.445, and exactly 1.000 vs 1.000 on all three high-ceiling lanes. The mechanism is not
   inferred. §5.
7. **Counterfactual: remove both clamps and EW1 and Darkreach stop being gated at all.** Their
   normalised peak AoA (0.554, 0.652) sits below the proportional onset 0.7059 but above their
   floor-lowered onset 0.529. Those two lanes are gated **only** because `aoaFade` is floored at 4°. §5.
8. **The predicted low-ceiling/high-ceiling asymmetry REPRODUCES ON ONLY 2 OF THE 3 PREDICTED
   AIRFRAMES, AND THE CEILING IS NOT THE DISCRIMINATOR.** `commandIntoCeilingPct` is 55.5% on trainer
   and 42.9% on VTOLTrainer1 (both > the 25% fail threshold) against 0.00 on all three high-ceiling
   lanes — but it is also **0.00 on Darkreach and EW1, which share trainer's 8.5° ceiling**, and
   VTOLTrainer1's ceiling is 12.75°. The discriminator is gate *depth*, not ceiling. §6.
9. **The five 0.00 readings are NON-EXPOSURE, not a clean bill.** `commandIntoCeilingPct` requires
   `aoaGU < 0.5`; the gate never went below 0.5 on Darkreach (min 0.719), EW1 (0.919), SmallFighter1 /
   Fighter1 / Multirole1 (1.000). A gate that never closed reports the same 0 as a gate that behaved. §6.
10. **SLACK did not fire on any `alpha_hold` segment, and could not have — it is blocked twice
    over.** `rail_warning` only reaches the SLACK branch when `hits` is empty, and all 60 segments
    rail; independently `authorityUsedFrac` is **0.977–1.084**, nowhere near `SLACK_FRAC` 0.5. The
    corpus's first `alpha_hold` data adds **zero** SLACK observations and leaves the count at 8-in-9,137. §8.
11. **The alt-tab frame hitch did NOT touch this card.** Zero rows over 25 ms across all 61 captures;
    max in-capture `frameMs` **20.9**, and the three lanes that could have caught the 87 ms event read
    20.9 / 18.4 / 17.0. Frame hitching is not a usable confound for anything here. §9.
12. **A CITED FIGURE IN THE TASK BRIEF WAS WRONG AND IS CORRECTED HERE.** See §3 — the claim that
    R35's `alpha-steps` returned `aoaAboveCeilingPct` = 0.0 on all 8 airframes is false; it reached
    the limiter on 7 of 8 and crossed the ceiling on 2. Every conclusion below is drawn against the
    corrected table.
13. **Higher demand produced LESS AoA — the counter-intuitive result and the reason this card
    missed.** `alpha-sweep` commands 4–50× the turn rate of `alpha-steps` at the identical entry
    condition (`turnRateDemandRatio` 0.98–1.00 vs 0.02–0.23) and reaches a **lower**
    `aoaPeakOverCeiling` on 7 of 8 airframes. The bank clamp and turn-rate cap saturate upstream of
    the alpha channel and the deficit is paid in **altitude** (525–2428 m of descent, q rising
    3–32%) rather than in AoA. §4.

---

## 1. Tag resolution

```
card         type        tag        n   unknown_tag  excluded  railed  slack
alpha-sweep  alpha_hold  alphaHold  61  0            0         61      0
alpha-sweep  arm         arm        61  0            61        0       0
```

`alpha_hold` is the second member of `scorecard.SLACK_TYPES` and had **n = 0 corpus-wide** before
this batch. It now has 61. No drift between `cards/alpha-sweep.json` and `scorecard.py`'s
`TAG_TYPE_RULES`.

### n per lane after excluding the aborted capture

| lane | airframe | `alphaLimiter` | captures | aborted | **scorable n** |
|---|---|---|---|---|---|
| 41 | Fighter1 | 27 | 8 | 0 | **8** |
| 42 | Multirole1 | 27 | 8 | 0 | **8** |
| 43 | SmallFighter1 | 25 | 8 | 0 | **8** |
| 44 | trainer | 10 | 8 | 0 | **8** |
| 45 | VTOLTrainer1 | 15 | 8 | 0 | **8** |
| 46 | EW1 | 10 | 8 | 0 | **8** |
| 47 | FastBomber1 | 15 | 8 | 0 | **8** |
| 48 | Darkreach | 10 | 5 | 1 | **4** |

**60 scorable of 64 nominal.** Darkreach `rec 229` (replicate 5) aborted at 9.3 s / 149 samples on
`detached ratio 0.114` and the lane ended there — replicates 6–8 never flew, the same
one-abort-kills-the-lane shape as R37 §5. Its partial segment is excluded from every number in this
document. Darkreach's four survivors are internally consistent (`aoaPeakOverCeiling` 0.655–0.660), and
the aborted one reads 0.633 — low, but it only flew 9.3 s of a 35 s hold, so that is truncation, not a
different flight.

---

## 2. The gate, and what "reaching the regime" actually means here

Two different metrics answer two different questions and they disagree on this card:

| metric | what it asks | result |
|---|---|---|
| `aoaLimiterActivePct` | did either ceiling gate move off 1.0? (= AoA entered the **fade band**) | **> 0 on 5 of 8** — task gate PASSES |
| `aoaAboveCeilingPct` | did AoA cross `alphaLimiter − aoaMargin`? | **0.0 on 60 of 60** — card's own criterion FAILS |

Per lane (`alpha_hold`, aborted capture excluded, n as in §1):

| airframe | ceil° | n | `aoaLimiterActivePct` | `aoaPeakDeg` | `aoaPeakOverCeiling` | `aoaAboveCeilingPct` |
|---|---|---|---|---|---|---|
| trainer | 8.50 | 8 | 96.3 | 6.72 | 0.791 | 0.0 |
| EW1 | 8.50 | 8 | 25.2 | 4.71 | 0.555 | 0.0 |
| Darkreach | 8.50 | 4 | 93.5 | 5.54 | 0.657 | 0.0 |
| FastBomber1 | 12.75 | 8 | 71.6 | 10.02 | 0.786 | 0.0 |
| VTOLTrainer1 | 12.75 | 8 | 90.8 | 10.88 | 0.853 | 0.0 |
| SmallFighter1 | 21.25 | 8 | 0.0 | 12.07 | 0.568 | 0.0 |
| Fighter1 | 23.00 | 8 | 0.0 | 12.98 | 0.564 | 0.0 |
| Multirole1 | 23.00 | 8 | 0.0 | 10.27 | 0.447 | 0.0 |

### Everything is railed, and not on AoA

| airframe | `bankClampActivePct` | `turnRateCapActivePct` | `blendRailPct` | `gPeak` | `gSustained` |
|---|---|---|---|---|---|
| trainer | 95.1 | 96.4 | 96.1 | 5.29 | 3.84 |
| EW1 | 96.3 | 95.2 | 89.7 | 4.67 | 3.80 |
| Darkreach | 74.1 | 96.7 | 80.9 | 4.26 | 2.86 |
| FastBomber1 | 95.4 | 85.4 | 90.9 | 4.64 | 3.27 |
| VTOLTrainer1 | 91.5 | 96.0 | 93.4 | 5.06 | 3.79 |
| SmallFighter1 | 97.2 | 97.4 | 92.5 | 6.37 | 6.02 |
| Fighter1 | 96.9 | 96.7 | 91.2 | 6.21 | 5.94 |
| Multirole1 | 97.2 | 97.5 | 93.9 | 6.98 | 6.44 |

Median |bank| is 70.2–80.3° against the 72° clamp. `scorecard.rail_warning` carries a special case
for an alpha card railed **only** on `aoaAboveCeilingPct` ("the ceiling IS the stimulus"); it does not
apply, because the three rails that fired are the other three.

### The four PASS criteria

| criterion | threshold | result |
|---|---|---|
| `aoaPeakOverCeiling` | ≤ 1.1 | **PASS 8/8** — max over all 60 segments is **0.856** (VTOLTrainer1). The v0.57 relay signature (1.3–2.5×) does not appear anywhere. |
| `commandIntoCeilingPct` | < 10% | **FAIL 2/8** — trainer 55.5, VTOLTrainer1 42.9. FastBomber1 0.07 (one replicate of eight). Five lanes read 0.00 but see §6: that is non-exposure. |
| `qSchedMin` | < 1 | **PASS 5/8** — 0.548–0.997 on the five gated lanes; **exactly 1.000** on SmallFighter1 / Fighter1 / Multirole1, i.e. the v0.59 demand schedule was completely inert on those three. |
| `aoaRecoverActivePct` | > 0 | **FAIL 8/8 — 0.0 everywhere**, and `aoaRecoverPeak` 0.0 everywhere. |
| `wobbleEpisodesAoa` | = 0 | **PASS 8/8** — zero AoA wobble episodes in 60 segments. |

**Criteria 3 and 4 are unreachable by construction on this card.** `aoaRecover` is
`(max(0, aoaPredSym − aoaCeil) − max(0, −aoaPredSym − aoaCeil)) / aoaFade` (`ChaseController.cs:1280`)
— it is identically zero until AoA crosses the ceiling, and nothing crossed. Reading
`aoaRecoverActivePct = 0` as a law defect would be the attribution error this document exists to
avoid: it is the card not reaching the regime, restated. Same for `qSchedMin = 1.000` on the three
high-ceiling lanes.

**Overall verdict: the card FAILS on reach (criterion 2 of its own note, and criteria 3–4 of the
brief), and the law FAILS criterion 2 on the two lanes where the measurement was exposed.**

---

## 3. Correction to a cited figure

The task brief stated:

> "R35's `alpha-steps` flew this exact 8000 m / 250 m/s condition and returned `aoaAboveCeilingPct` =
> **0.0 on all 8 airframes**, with peaks of 5.7–16.6° against ceilings of 8.5–23°."

**That is false.** The actual R35 `alpha-steps` result, non-`arm` segments grouped by airframe:

| airframe | `aoaLimiterActivePct` mean / max | `aoaAboveCeilingPct` max | `aoaPeakOverCeiling` max |
|---|---|---|---|
| Darkreach | 73.6 / 96.3 | **5.000** | **1.029** |
| trainer | 70.0 / 84.4 | **4.375** | **1.024** |
| EW1 | 54.3 / 57.5 | 0.0 | 0.794 |
| FastBomber1 | 41.1 / 71.3 | 0.0 | 0.930 |
| VTOLTrainer1 | 27.2 / 56.3 | 0.0 | 0.820 |
| SmallFighter1 | 10.9 / 17.5 | 0.0 | 0.726 |
| Fighter1 | 6.0 / 8.8 | 0.0 | 0.733 |
| Multirole1 | 0.0 / 0.0 | 0.0 | 0.601 |

`alpha-steps` at 8000 m / 250 m/s put **7 of 8 airframes on the limiter and 2 of 8 past the ceiling**.

**Consequences, applied throughout this document:**
- The premise *"8000 m / 250 m/s cannot reach the alpha regime"* is **refuted**. The recommendation
  the brief pre-authorised on the strength of that premise — "the card failed, not the law; the fix is
  lower q" — is **not** issued here in that form. §10 issues a different one.
- Since both cards flew the same entry condition and only `alpha-sweep` missed the ceiling, the
  difference is **demand shape**, not q. That is §4, and it is the more interesting result.

---

## 4. Demand, not q — and more demand bought less AoA

Same 8000 m, same 250 m/s absolute entry, same eight airframes, same law family. The only difference
is what the card asks for: `alpha-steps` walks ±45° azimuth steps; `alpha-sweep` sets
`deriveAzRate: true`, a constant azimuth sweep at `SustainableTurnRate` (`ScenarioPlayer.cs:1726`) =
`0.6 × 57.3 × 9.81·√(n²−1)/V`, clamped 3–30 °/s, derived from the airframe's **g limit**.

| airframe | `turnRateDemandRatio` steps → sweep | `bankClampActivePct` | `turnRateCapActivePct` | `aoaPeakOverCeiling` steps → sweep |
|---|---|---|---|---|
| trainer | 0.04 → **0.98** | 0.0 → 95.1 | 4.4 → 96.4 | 1.024 → **0.792** |
| Darkreach | 0.12 → **0.98** | 0.0 → 74.1 | 2.8 → 96.7 | 1.029 → **0.660** |
| EW1 | 0.23 → **0.98** | 0.0 → 96.3 | 0.0 → 95.2 | 0.794 → **0.558** |
| FastBomber1 | 0.09 → **0.98** | 0.0 → 95.4 | 1.6 → 85.4 | 0.930 → **0.812** |
| VTOLTrainer1 | 0.14 → **0.98** | 0.0 → 91.5 | 0.0 → 96.0 | 0.820 → **0.856** |
| SmallFighter1 | 0.06 → **1.00** | 0.0 → 97.2 | 0.0 → 97.4 | 0.726 → **0.569** |
| Fighter1 | 0.02 → **0.99** | 0.0 → 96.9 | 0.0 → 96.7 | 0.733 → **0.565** |
| Multirole1 | 0.05 → **1.00** | 0.0 → 97.2 | 0.0 → 97.5 | 0.601 → **0.448** |

**4–50× the demand, and lower peak AoA on 7 of 8 airframes.** The mechanism, measured:

| airframe | Δaltitude over the 35 s hold | q(end)/q(start) |
|---|---|---|
| trainer | **−2428 m** | 1.319 |
| VTOLTrainer1 | −1901 m | 0.948 |
| FastBomber1 | −1127 m | 1.255 |
| Darkreach | −1065 m | 1.271 |
| Multirole1 | −825 m | 1.187 |
| Fighter1 | −716 m | 1.031 |
| SmallFighter1 | −682 m | 1.055 |
| EW1 | −525 m | 1.211 |

The card's stated mechanism is *"thin air means that fraction is no longer available aerodynamically,
so the wing rides its alpha ceiling and stays there as energy bleeds."* The first half happened —
`turnRateDemandRatio` ≈ 1.0 confirms the demanded rate is at or beyond what is available. The second
half did not. **The aircraft rolls to the 72° bank clamp, pulls what the turn-rate cap allows, and
pays the deficit in altitude** — descending into denser air, so q *rises* 3–32% on seven of eight
lanes and the AoA needed for the same load factor *falls*. Nothing in the card constrains altitude and
nothing in the law does either. Peak AoA ends up **below** what a small, unsaturated step demand
reaches.

An alpha card whose stimulus is derived from the **g limit** saturates the bank/turn-rate channel
first. To load the alpha channel the demand has to be derived from the **alpha ceiling**, or the
entry q has to be low enough that the alpha ceiling is the binding constraint. §10.

---

## 5. The constants — `aoaFade` and `aoaMargin`

`ChaseController.cs:1216` and `:1222`, inside the `if (!_collective)` block:

```csharp
float aoaMargin = Mathf.Min(4f, 0.15f * lim);                       // :1216
float aoaFade   = Mathf.Max(4f, Mathf.Min(6f, 0.25f * lim));        // :1222
float aoaCeil   = lim - aoaMargin;                                  // :1223
...
aoaGateUp = Mathf.Clamp01((aoaCeil - aoaPredUp) / aoaFade);         // :1238
aoaGateDn = Mathf.Clamp01((aoaCeil + aoaPredDn) / aoaFade);         // :1239
```

Both are **absolute degrees**. Three clamps can bind, and on this roster all three do:

| airframe | `lim` | `aoaMargin` | which clamp | `aoaFade` | which clamp | `ceil` | guard onset° | **onset / ceil** |
|---|---|---|---|---|---|---|---|---|
| trainer | 10 | 1.50 | proportional | 4.00 | **4° FLOOR** | 8.50 | 4.50 | **0.529** |
| EW1 | 10 | 1.50 | proportional | 4.00 | **4° FLOOR** | 8.50 | 4.50 | **0.529** |
| Darkreach | 10 | 1.50 | proportional | 4.00 | **4° FLOOR** | 8.50 | 4.50 | **0.529** |
| FastBomber1 | 15 | 2.25 | proportional | 4.00 | **4° FLOOR** | 12.75 | 8.75 | **0.686** |
| VTOLTrainer1 | 15 | 2.25 | proportional | 4.00 | **4° FLOOR** | 12.75 | 8.75 | **0.686** |
| SmallFighter1 | 25 | 3.75 | proportional | 6.00 | **6° CAP** | 21.25 | 15.25 | **0.718** |
| Fighter1 | 27 | 4.00 | **4° CAP** | 6.00 | **6° CAP** | 23.00 | 17.00 | **0.739** |
| Multirole1 | 27 | 4.00 | **4° CAP** | 6.00 | **6° CAP** | 23.00 | 17.00 | **0.739** |

**With both clamps removed, `onset/ceil` = (1 − 0.15 − 0.25)/(1 − 0.15) = 0.7059 for every airframe,
by construction.** The measured spread 0.529 → 0.739 is 40%, and it is entirely clamp artefact.

**The proportional expression is live only for `alphaLimiter` ∈ [16, 24].** Below 16 the fade floor
binds; above 24 the fade cap binds; at 27 the margin cap binds as well. **The eight airframes on this
card are 10, 10, 10, 15, 15, 25, 27, 27 — none of them is in the window.** Expressed as a fraction of
each airframe's own limiter, the fade the law actually uses is:

```
lim 10  ->  4.00 deg  =  40.0% of limiter      lim 25  ->  6.00 deg  =  24.0%
lim 15  ->  4.00 deg  =  26.7% of limiter      lim 27  ->  6.00 deg  =  22.2%
```

A 1.8× spread in a quantity the ONE-LAW rule requires to be constant or probe-derived.

### 5a. The v0.61 comment is a deliberate design choice — and it contains a factual error

`ChaseController.cs:1218-1221`:

> *"v0.61: floor the fade at 4 deg. The proportional fade collapses to 2.5 deg on a low limiter
> (lim 10), narrower than the one-lead-time AoA overshoot a low-q plant produces — that turns the gate
> into a relay (the Trainer AoA pump). Floor keeps it a graded fade, never bang-bang. **For lim >= 16
> (0.25\*lim >= 4) the floor is INACTIVE, so FS-12 (lim 27, fade 6) and every jet with lim >= 16 are
> byte-identical**; only low-limit STOL/trainers widen."*

Two things follow, and they cut in opposite directions.

**In the floor's favour:** its justification is a real physical quantity. The overshoot the fade must
be wider than is `_aoaRateFilt × aoaLead` (`:1233-1235`, lead 0.30 s) — a rate times a time, which is
in **degrees**, not in fractions of a ceiling. An absolute floor is the right *shape*. But it is
implemented as the constant `4f` rather than as the lead overshoot the mod already computes one line
away, which is exactly the substitution the ONE-LAW rule asks for.

**Against it:** the bolded claim is **false for `lim > 24`**. At lim 27, `0.25 × 27 = 6.75`, so
`Min(6f, ·)` binds and the fade is 6.00, not 6.75; and `0.15 × 27 = 4.05`, so `Min(4f, ·)` binds and
the margin is 4.00, not 4.05. Fighter1 and Multirole1 are **not** byte-identical to the proportional
form, and neither is SmallFighter1 at lim 25. The comment's parenthetical "(lim 27, fade 6)" quotes
what the cap *produces* as though it were what the proportion produces. Three of this card's eight
lanes are governed by a clamp the comment says is inactive.

### 5b. The model, tested against the data

Predicted bite = `peak |aoa| > onset`. Observed bite = `gateMinUp < 0.999`.

| airframe | peak/ceil | onset/ceil | predicted | observed | |
|---|---|---|---|---|---|
| trainer | 0.791 | 0.529 | bite | bite (0.439) | ✓ |
| EW1 | 0.554 | 0.529 | bite | bite (0.933) | ✓ |
| Darkreach | 0.652 | 0.529 | bite | bite (0.735) | ✓ |
| FastBomber1 | 0.786 | 0.686 | bite | bite (0.574) | ✓ |
| VTOLTrainer1 | 0.853 | 0.686 | bite | bite (0.445) | ✓ |
| SmallFighter1 | 0.568 | 0.718 | no bite | no bite (1.000) | ✓ |
| Fighter1 | 0.564 | 0.739 | no bite | no bite (1.000) | ✓ |
| Multirole1 | 0.447 | 0.739 | no bite | no bite (1.000) | ✓ |

**8/8.** And quantitatively, `clamp01((ceil − |aoa|)/fade)` minimised over each segment, against the
recorded `gateMinUp` (the residual is the 0.30 s predictive lead, which the model omits):

| airframe | measured | model | diff |
|---|---|---|---|
| trainer | 0.439 | 0.445 | −0.006 |
| EW1 | 0.933 | 0.947 | −0.014 |
| Darkreach | 0.735 | 0.739 | −0.004 |
| VTOLTrainer1 | 0.445 | 0.469 | −0.024 |
| FastBomber1 | 0.574 | 0.683 | −0.109 |
| SmallFighter1 / Fighter1 / Multirole1 | 1.000 | 1.000 | 0.000 |

Seven of eight within 0.024. FastBomber1's −0.109 is the lead firing on its rising-AoA transient,
which is the one lane where AoA is still climbing at the gate minimum.

### 5c. The counterfactual — which lanes exist only because of the floor

| airframe | peak/ceil | shipped onset | proportional onset | shipped | proportional | differs |
|---|---|---|---|---|---|---|
| trainer | 0.791 | 0.529 | 0.7059 | bite | bite | |
| **EW1** | **0.554** | **0.529** | **0.7059** | **bite** | **NO bite** | **← yes** |
| **Darkreach** | **0.652** | **0.529** | **0.7059** | **bite** | **NO bite** | **← yes** |
| FastBomber1 | 0.786 | 0.686 | 0.7059 | bite | bite | |
| VTOLTrainer1 | 0.853 | 0.686 | 0.7059 | bite | bite | |
| SmallFighter1 / Fighter1 / Multirole1 | 0.447–0.568 | 0.718–0.739 | 0.7059 | no bite | no bite | |

**EW1 and Darkreach are gated only because the 4° floor binds.** Two of the five "limiter active"
lanes in the §2 gate are floor artefacts.

### 5d. The matched pair — the cleanest single statement

| | `alphaLimiter` | ceil | peak AoA | **peak/ceil** | fade | fade as % of lim | onset/ceil | `gateMinUp` |
|---|---|---|---|---|---|---|---|---|
| **EW1** | 10 | 8.50 | 4.71° | **0.554** | 4.0° | 40.0% | 0.529 | **0.933 — gate bit** |
| **SmallFighter1** | 25 | 21.25 | 12.07° | **0.568** | 6.0° | 24.0% | 0.718 | **1.000 — never moved** |

Two airframes at effectively the same fraction of their own alpha ceiling. One has its pitch authority
gated; the other's guard never moves. The only difference is that 4/10 is larger than 6/25. **That is
a constant showing through, which is what the ONE-LAW rule forbids.**

---

## 6. The low-ceiling asymmetry — reproduces in part, and NOT on the ceiling axis

Predicted: `commandIntoCeilingPct` > 25% on trainer / Darkreach / EW1 (ceiling 8.5°), ≈ 0 on Fighter1 /
Multirole1 (23°). Measured:

| airframe | ceil° | `commandIntoCeilingPct` | `gateMinUp` mean (min–max) | `aoaLimiterActivePct` | cmdInto / limiter |
|---|---|---|---|---|---|
| trainer | 8.50 | **55.48** | 0.439 (0.436–0.442) | 96.3 | 0.576 |
| VTOLTrainer1 | 12.75 | **42.87** | 0.445 (0.433–0.451) | 90.8 | 0.472 |
| FastBomber1 | 12.75 | 0.07 | 0.574 (0.451–0.631) | 71.6 | 0.001 |
| Darkreach | 8.50 | **0.00** | 0.726 (0.719–0.731) | 93.5 | 0.000 |
| EW1 | 8.50 | **0.00** | 0.933 (0.919–0.941) | 25.2 | 0.000 |
| SmallFighter1 | 21.25 | 0.00 | 1.000 | 0.0 | — |
| Fighter1 | 23.00 | 0.00 | 1.000 | 0.0 | — |
| Multirole1 | 23.00 | 0.00 | 1.000 | 0.0 | — |

**Two of the three predicted airframes fail, and one that was not predicted fails harder than two that
were.** The prediction is **half right for the wrong reason**:

- ✅ Both failing lanes exceed 25% by a wide margin (55.5, 42.9), and all three high-ceiling lanes read
  exactly 0.00. The *shape* of the asymmetry is there.
- ❌ Darkreach and EW1 share trainer's 8.5° ceiling and read **0.00**. If ceiling were the driver they
  would fail too.
- ❌ VTOLTrainer1's ceiling is 12.75°, in the middle of the roster, and it is the second-worst lane.

**The actual discriminator is `GATE_BITING`.** `commandIntoCeilingPct` counts samples where
`aoaGU < 0.5` **and** the raw pre-gate command `tgtPRaw` is still nose-up past the deadband
(`scorecard.py:1081-1088`, `GATE_BITING = 0.5` at `:997`). Only trainer (0.436–0.442), VTOLTrainer1
(0.433–0.451) and FastBomber1 on one replicate (min 0.451) ever got the gate below 0.5. On the other
five the metric is **structurally incapable of firing**.

**So the five 0.00 readings are non-exposure, not a clean bill** — a gate that never closed reports
the same 0 as a gate that behaved correctly. Of the three lanes where the measurement was actually
exposed, **two command into the ceiling roughly half the time**. That is a real defect where it can be
seen, on a much smaller n than the raw table suggests.

**The subtlety that keeps this honest.** The 4° floor does not *cause* the law to command into the
ceiling — it causes the gate to be shut while it does. Under a proportional fade, trainer's gate would
minimise at `(8.50 − 6.72)/2.5 = 0.712`, never crossing `GATE_BITING`, and `commandIntoCeilingPct`
would read **0.00 while the raw law did exactly the same thing**. The metric's firing is partly a
constant artefact; the underlying behaviour is invariant to it. **`commandIntoCeilingPct` is therefore
not a clean instrument for this question** — it confounds "does the law back off?" with "did the gate
close far enough for us to look?".

---

## 7. Refutation tests

Each conclusion, the observation that would refute it, and the result.

| # | conclusion | would be refuted by | result |
|---|---|---|---|
| 1 | The absolute-degree clamps set which airframes' guard engages | any lane where the clamp analysis predicts a bite and none occurs, or vice versa | **NOT REFUTED** — 8/8, and `gateMinUp` reproduced to ±0.024 on 7/8 (§5b) |
| 2 | The failing lanes are the low-**ceiling** ones | a low-ceiling lane that does not fail, or a mid/high-ceiling lane that does | **REFUTED** — Darkreach and EW1 (8.5°) read 0.00; VTOLTrainer1 (12.75°) reads 42.9 (§6) |
| 3 | `commandIntoCeilingPct` = 0.00 is a pass on the other six lanes | those lanes' gates never reaching `GATE_BITING` = 0.5 | **REFUTED** — five of six never got below 0.719; it is non-exposure (§6) |
| 4 | The card reached the regime it was built for | `aoaAboveCeilingPct` = 0 | **REFUTED** — 0.0 on 60/60 (§2) |
| 5 | 8000 m / 250 m/s cannot reach the alpha regime (the brief's premise) | another card reaching it at the same entry | **REFUTED** — `alpha-steps` crossed the ceiling on 2 of 8 there (§3) |
| 6 | Therefore more demand gets you closer to alpha | a higher-demand card reaching *less* AoA | **REFUTED** — `alpha-sweep` at 4–50× the demand reaches lower `aoaPeakOverCeiling` on 7/8 (§4) |
| 7 | The 4° floor is an oversight | a source comment showing it was chosen deliberately | **REFUTED** — `:1218-1221` states the reasoning; the defect is the hardcoded `4f`, not the existence of a floor (§5a) |
| 8 | Replicate 1 biases the lane means | replicate 1 differing from 2–8 beyond replicate scatter | **NOT SUPPORTED** — `aoaPeakOverCeiling` varies in the 3rd decimal within every lane (e.g. Fighter1 0.564–0.565 across all 8). VTOLTrainer1's rep 1 `commandIntoCeilingPct` is 37.6 against a 37.6–45.3 range — the low end, but inside it |
| 9 | The 87 ms alt-tab hitch contaminated this card | any row over 25 ms | **REFUTED** — 0 rows of 34,000+ over 25 ms; max 20.9 (§9) |
| 10 | SLACK's absence says something about the law | SLACK being reachable here | **REFUTED** — blocked twice: all 60 railed, and `authorityUsedFrac` 0.977–1.084 vs `SLACK_FRAC` 0.5 (§8) |

---

## 8. SLACK on `alpha_hold` — the first opportunity, and it was structurally unavailable

`slack = 1` on **0 of 61** segments. The corpus stays at 8 SLACK firings in 9,137 segments, all R27.

Two independent blocks, either of which alone is sufficient:

1. **`rail_warning` never reaches the SLACK branch.** It returns early on `if not hits:`
   (`scorecard.py:897`) and all 60 scorable segments have three rail hits.
2. **The authority test would fail anyway.** `authorityUsedFrac` is 0.977–1.084 across the eight lanes
   — 2–2.2× `SLACK_FRAC`. `authBank` alone is 0.93–1.08, i.e. the bank clamp is the binding term.
   Values above 1.0 on four lanes (Fighter1 1.076, Multirole1 1.084, SmallFighter1 1.078, EW1 1.005)
   are worth a separate look; a fraction of *available* authority should not exceed 1.

**What this is and is not evidence for.** It is a clean demonstration that adding `alpha_hold` to
`SLACK_TYPES` was inert on the first `alpha_hold` batch ever flown — the type gate was never the
binding constraint here. It is **not** evidence that the type gate is well-calibrated, because this
card never produced a segment that could have tested it. Per the brief, no recommendation to ungate
`SLACK_TYPES` follows: 94.8% of the modern corpus sits under `SLACK_FRAC`, and this batch adds nothing
that would change that.

---

## 9. Frame health and provenance

- **Zero rows over 25 ms** across all 61 captures. Max in-capture `frameMs` **20.9** (lanes 41–46),
  18.4 (FastBomber1), 17.0 (Darkreach), against a 17 ms fixed step. The logged 87 ms hitch landed on a
  later card; nothing on `alpha-sweep` was affected. `mod_version` v0.98.1 is well past the v0.92.1
  fix, so `frameMs` here means the frame, not the fixed step.
- `rec` 190–250, contiguous within each lane at stride 8 (Darkreach 197–229 at stride 8, stopping
  early).
- Segment shape: `arm` 6 s excluded, `alphaHold` 35.0 s / 561 samples on all 60 scorable segments —
  no truncation anywhere except the aborted Darkreach capture (9.3 s / 149).
- `unknown_tag` 0, `parse_warn` NULL, no `# cfg` mid-run config changes.

---

## 10. What would actually reach the regime

Not "lower q by climbing" — the aircraft already descends 0.5–2.4 km on its own and *gains* q (§4).
Two levers, and the speed one is quantifiable.

**Lower the entry speed via `startSpeedCorner`, not the altitude.** The absolute 250 m/s entry is the
problem twice over: it is 2.50× corner for Darkreach (FBW corner 100) and 1.25× for FastBomber1
(corner 200) — a 2× spread in aerodynamic entry state across lanes that are supposed to be asking one
question — and it refuses CAS1 and COIN pre-spawn on the v0.92 envelope gate.

At the bank clamp the load factor is fixed at `1/cos 72° = 3.24`, so for a linear-CL wing AoA ∝ 1/q ∝
1/V². Required entry speed to bring peak AoA to the ceiling is `250 × √(aoaPeakOverCeiling)`:

| airframe | peak/ceil | required V | FBW corner | **required multiple** |
|---|---|---|---|---|
| Multirole1 | 0.447 | 167 m/s | 160 | **1.04×** |
| FastBomber1 | 0.786 | 222 | 200 | 1.11× |
| Fighter1 | 0.564 | 188 | 160 | 1.17× |
| SmallFighter1 | 0.568 | 188 | 155 | 1.21× |
| EW1 | 0.555 | 186 | 130 | 1.43× |
| VTOLTrainer1 | 0.853 | 231 | 160 | 1.44× |
| trainer | 0.791 | 222 | 130 | 1.71× |
| Darkreach | 0.657 | 203 | 100 | 2.03× |

**`startSpeedCorner: 1.0` clears every lane** — the binding case is Multirole1 at 1.04×, and per the
v0.96 note all ten fixed-wing keys pass the envelope gate at 1.0× corner. It would also admit CAS1 and
COIN, taking the card from 8 lanes to 10.

Two caveats on that estimate: it assumes the bank clamp still binds at corner speed (it may not, which
would *raise* the AoA reached, in the helpful direction), and it ignores the in-segment descent, which
raises q 3–32% and eats margin.

**Second lever — derive the stimulus from the alpha ceiling, not the g limit.** `SustainableTurnRate`
(`ScenarioPlayer.cs:1726`) builds the demand from `aircraftGLimit`, so an "alpha" card commands a
g-limited turn and saturates bank/turn-rate first (§4). While the demand rails those three limits, the
alpha channel is downstream of a saturated actuator and the card cannot isolate it — which is also why
all 60 segments are RAILED and, strictly, NO SIGNAL. A demand at ~0.7× the current rate would likely
come off the bank clamp and let AoA rise, but that is a card-design change and is untested.

Note also a stale claim in the card's own `note`: it calls FastBomber1 and Darkreach "the 5.0 gLimit
airframes". Their sidecar `gLimitPositive` reads **8.0 and 4.0**.

---

## 11. Confounds not ruled out

1. **All 60 segments are RAILED on three non-AoA limits.** Bank clamp 74–97%, turn-rate cap 85–97%,
   blend rail 81–96%. Every alpha metric here is read off a saturated turn, and `CAPTURES-DB.md`'s
   rule is that a railed segment's metrics are no signal. The §5 gate arithmetic survives this because
   it is derived from source and validated against `gateMinUp`, which is a direct function of measured
   AoA — but the §6 asymmetry does **not** obviously survive it, because `tgtPRaw` is the raw command
   of a law whose roll/turn channel is on the stops.
2. **AoA and altitude are confounded within the segment.** Every lane descends 525–2428 m during the
   hold and q rises on seven of eight. Peak AoA is not taken at a controlled flight condition, and the
   descent magnitude correlates with the lanes that gated (trainer −2428 m, VTOLTrainer1 −1901 m are
   the two deepest descents *and* the two `commandIntoCeilingPct` failures). Cannot separate "the gate
   bit because the constant binds" from "the gate bit because this lane bled the most energy".
3. **`alphaLimiter` is collinear with airframe class on this roster.** The 10° group is exactly the
   three slow/heavy airframes and the 27° group is exactly the two fighters. The clamp-binding set and
   the "slow airframe" set are the same set, and this card cannot separate them. The §5 finding is
   stated as source arithmetic plus a `gateMinUp` reproduction for precisely this reason — the
   *consequence* of the clamps for flight quality is **not** identified here.
4. **n is small per ceiling group** — three airframes at 8.5°, two at 12.75°, three at 21.25/23°. The
   exposed subset in §6 is n = 3 lanes.
5. **No loadout variation.** ONE-LAW standing case 2 is "a **loaded** jet mushing near its alpha limit
   above corner speed". A card cannot set a loadout (the card's own note says so), and these flew
   default stores (845–13,050 kg). **The loaded half of standing case 2 remains unflown**, and this
   batch does not close it.
6. **One session, one mod version, no A/B arm.** `arm`/`arm_knob` NULL on all 61. No lever was swept,
   so nothing here attributes an effect to a law change.
7. **The three high-ceiling lanes contribute no alpha information at all.** `aoaLimiterActivePct` 0.0,
   `gateMinUp` 1.000, `qSchedMin` 1.000 — the entire AoA block was inert on Fighter1, Multirole1 and
   SmallFighter1. They are controls, not measurements.

---

## 12. What this batch CANNOT prove

- **That the law mishandles the alpha ceiling.** Nothing crossed the ceiling. The recovery bias and
  the past-ceiling behaviour are still entirely unmeasured, corpus-wide.
- **That the 4° floor degrades flight quality.** §5 proves the guard's onset varies 40% across the
  roster because of absolute constants, and §5c proves two lanes are gated only because of it. It does
  **not** show that this made any lane fly worse — `aoaPeakOverCeiling` ≤ 0.856, zero AoA wobble
  episodes, no relay signature anywhere.
- **Anything about the loaded case, STOL, or rotorcraft.** Eight fixed-wing keys at default loadout.
- **Anything about `alpha_step`.** That type still has n = 0 in this batch; the R35 `alpha-steps`
  numbers in §3 are a different card in a different batch, cited for contrast only.
- **Anything a railed segment can prove.** See confound 1.

## 13. Backlog

- **#55a — re-issue `alpha-sweep` with `startSpeedCorner: 1.0`** and drop `startSpeed: 250`. §10 gives
  the arithmetic; it clears all eight lanes with margin and adds CAS1 + COIN. This is the single
  cheapest way to get the corpus its first above-ceiling `alpha_hold` data.
- **#55b — `aoaFade`'s floor should key off the lead overshoot the mod already computes.** The v0.61
  justification (`:1218`) is `_aoaRateFilt × aoaLead`, a live measured quantity in degrees, and it is
  one line above the constant. Replacing `Mathf.Max(4f, …)` with a floor derived from that term would
  keep the anti-relay property and remove the constant. **Diagnosis only — not implemented.**
- **#55c — the `Min(6f, …)` cap on `aoaFade` and `Min(4f, …)` on `aoaMargin` have no stated
  justification** and bind on three of this card's eight lanes. The comment at `:1220-1221` asserts
  they do not. Either the comment or the cap is wrong.
- **#55d — `commandIntoCeilingPct` should report its own exposure.** It reads 0.00 both for "the law
  backed off" and "the gate never reached `GATE_BITING`". A companion `gateBitingPct` (% of samples
  with `aoaGU < 0.5`) would make the denominator visible; without it, five of this card's eight lanes
  publish a passing number that means nothing. §6.
- **#55e — `authorityUsedFrac` exceeds 1.0 on four lanes** (up to 1.084). A fraction of available
  authority should not. §8.
- **#55f — `alpha-sweep`'s note is stale**: it calls FastBomber1 and Darkreach "the 5.0 gLimit
  airframes"; their sidecars read 8.0 and 4.0. §10.
- **#55g — the loaded case (ONE-LAW standing case 2) is still unflown.** A card cannot set stores, so
  this needs a hand-flown capture with heavy stores at the §10 entry condition.
