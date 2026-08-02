# R40 `alpha-pullup` — the vertical pull works, the guard holds on 9 of 9, nothing crossed the ceiling, and the law never once backed off, v0.99.1

**The card that replaces `alpha-sweep` did what it was built to do.** 73 captures, card
`alpha-pullup`, run tag **R40**, plugin **v0.99.1**, session `20260802-141344`, flown 2026-08-02
14:19–14:35. **144 scored `alpha_hold` segments** across 9 lanes × 8 replicates × 2 arms. Zero
railed, zero excluded, zero wobble episodes, zero stick rails.

`run_tag` R40 covers three cards; everything below filters `c.card = 'alpha-pullup'` and nothing is
pooled across cards.

**Roster is NINE, not ten.** `FastBomber1` produced one corrupt capture and stopped. See §0.

---

## Verdict

1. **The three roll/turn gates read EXACTLY zero. There is no law bug here.** `bankClampActivePct`,
   `turnRateCapActivePct` and `turnRateDemandRatio` are `0.0000` on all 144 scored segments, and the
   raw rows confirm the mechanism rather than just the metric: `max|targetBank| = 0.000` and
   `max|bankTR| = 0.000` on **every row of every lane**, `max|azErr|` = 0.03–0.51°, `max|outR|` ≤
   0.048. The roll channel is not generating bank against a demand that never asked for any. §1.
2. **The fourth gate, `blendRailPct`, is NOT ≈ 0 — it reads 34–66% on the fast arm — and both the
   card note and the SESSION doc are wrong about why.** `bWt = MAX(bigTurn, lateralHold)`
   (`ChaseController.cs:2062`), and a 72° vertical pull maximises `bigTurn` (total off-angle). It is
   elevation-driven, not azimuth-driven; `lateralHold` ≤ 0.10 and never wins the MAX.
   **`blendRailPct` is not a usable az-gate on this card.** It is not a law defect. §1.
3. **C1 PASSES on 9 of 9 flown lanes for `alphaHoldFast`.** `aoaLimiterActivePct` 34.4% (Multirole1)
   → 100% (Darkreach). The card's stated "10 of 10" is unreachable only because FastBomber1 never
   flew. On `alphaHoldSlow` it is 6 of 9 — Fighter1 / Multirole1 / SmallFighter1 are **NOT
   EXPOSED**, not failures. §3.
4. **The card did NOT reach the AoA ceiling. `aoaAboveCeilingPct` = 0.00 on 144 of 144.** Peak is
   `aoaPeakOverCeiling` **0.9882** (Darkreach, fast arm) — 98.8% of the guard's ceiling and no
   crossing. Nine lanes reach 0.354–0.988. §3.
5. **…and that is the guard working, not the card missing.** Darkreach sat at 0.986–0.988 with the
   up-gate 97% shut (`gateMinUp` 0.023–0.028) for **100% of samples** and still did not cross.
   **C5 and C1/C2 are mutually exclusive by construction**: `aoaRecover` is identically zero below
   the ceiling (`:1280`), so it can only fire if the guard fails. **C5 = NOT EXPOSED, 18 of 18, and
   this is the expected outcome of a sound guard.** §5.
6. **THE HEADLINE, and it is a law finding: the law never backs off. Not once.** Conditional on the
   up-gate being at least half shut, the raw pre-gate command `tgtPRaw` is still commanding INTO the
   ceiling on **100.0% of samples on 12 of 13 exposed lane-arms** and 97.1% on the thirteenth —
   **5,113 of 5,128 gate-biting samples**. Median `tgtPRaw` under a half-shut gate is **−0.79 to −0.97** (near
   full nose-up) on the fast arm. `qSched` had already cut the demand to 0.30–0.59 when this was
   measured (`tgtPRaw` is captured at `:1582`, *after* the schedule at `:1974`, *before* the gate).
   **C3 FAILS on 6 of 9 lanes on the fast arm and 4 of 9 on the slow arm.** §4.
7. **C6 passes on intent, 17 of 18. Dynamic pressure FELL on every lane-arm but one** — −0.9% to
   −30.2%. That is the opposite sign to `alpha-sweep` (q *rising* 3–32% on 7 of 8) and it is the
   single cleanest confirmation that the redesign's premise was right. The one exception is
   **Darkreach · `alphaHoldFast`, q +10.0%, −70 m** — 0.9% of altitude, against `alpha-sweep`'s
   525–2428 m. §6.
8. **`qSched` sits ON its `Clamp(qRatio, 0.3f, 1f)` floor for 19.9% of COIN's slow-arm samples** at
   85.0–102.6 m/s against a 110 m/s corner. §1.10 of the SESSION doc called that constant "armed and
   un-fired"; on this card it fires, on exactly one lane. §7.
9. **This IS a usable baseline for ticket #66, on nine lanes.** Replicate σ on `gateMinUp` is ≤
   0.011 and usually ≤ 0.006. The named discriminator **`EW1 · alphaHoldSlow` is 0.4835 ± 0.0093
   (n=8)** with a predicted post-fix ≈ **0.774**, a **+0.29 shift = 31 σ**. And the A/B is
   **two-sided**: the fix should raise `gateMinUp` on the six floor-bound lanes and *lower* it on the
   three cap-bound fighters. §8.

---

## 0. Cell census, and the one lane that died

### The "146" was a file count, not a capture count

Every capture writes two files (`.csv` + `.airframe.json`). `mouseaim-rec-v0.99*` matches both.

| card | captures (`.csv`) | declared | verdict |
|---|---|---|---|
| `alpha-pullup` | **73** | 10 lanes × `repeat: 8` = 80 | short by 7 |
| `place-noop` | 24 | 2 airframes × 12 | exact |
| `place-deflect` | 12 | 12 | exact |
| **R40 total** | **109** | — | matches `index-captures.py --summary` |

**There is no doubling mechanism in this batch.** 73 × 2 = 146.

### Per-lane cells

`rec` 37–109, contiguous, no gaps.

| lane | drone | captures | `alphaHoldFast` cells | `alphaHoldSlow` cells |
|---|---|---|---|---|
| Fighter1 | d4 | 8 | 8 | 8 |
| Multirole1 | d5 | 8 | 8 | 8 |
| SmallFighter1 | d6 | 8 | 8 | 8 |
| trainer | d7 | 8 | 8 | 8 |
| VTOLTrainer1 | d8 | 8 | 8 | 8 |
| CAS1 | d9 | 8 | 8 | 8 |
| COIN | d10 | 8 | 8 | 8 |
| EW1 | d11 | 8 | 8 | 8 |
| **FastBomber1** | **d12** | **1** | **0** | **0** |
| Darkreach | d13 | 8 | 8 | 8 |
| | | **73** | **72** | **72** |

**Bias assessment: none, for the nine surviving lanes.** Every one is 8 of 8, every segment is full
length (`alphaHoldFast` 64 samples / 3.94 s; `alphaHoldSlow` 192–193 / 11.95 s), 0 railed, 0
excluded, 0 `unknown_tag`. No lane is over-represented and no lane terminated early *mid-flight* —
the failure mode the brief warned about ("a lane that aborts early is a biased sample, not a smaller
one") **did not occur**. The shortfall is one lane lost whole, which is a smaller-roster problem, not
a biased-sample problem.

### FastBomber1: harness, unambiguously, and not any of the expected abort paths

`rec 45`, `d12`, one row, `# stop … reason=abort: aircraft gone`.

```
14:20:09  [drone] #12 'FastBomber1' spawned at (-6684, 2468, 4501) local / 6500 m MSL, 230 m/s, 2 crew
          [card] entry condition set: 230 -> 230 m/s (1.15x corner), 6500 -> 8000 m, snapped back 0 m
          [card] SWEEP RATE CLIPPED: ... 0.2 deg/s at 7877 m/s and 5.0 g ...     <-- already broken here
          [anomaly:trail] 364.27: ... spd=230      364.28: ... spd=7877
          [card] ABORT (aircraft gone) — 'alpha-pullup' segment arm at 0.0s — the suite ends here.
          [drone] #12 despawned (pilot killed). 8 live.
```

The single recorded row: `spd 7876.6`, `vel = (594, −7842, 433)`, `aoa 46.28`, `alt 7641.7`,
`segTag arm`, `tSeg 0.000`. **230 → 7877 m/s across one 16.7 ms frame ≈ 4.6 × 10⁵ m/s² ≈ 47,000 g.**
That is not aerodynamics. The pilot was killed by it and the aircraft despawned.

Three hypotheses, all testable, all resolved:

- **Law result — "a heavy airframe cannot hold the pull": REFUTED.** The abort landed at
  `tSeg = 0.000` of segment 1 (`arm`), 6 s *before* `alphaHoldFast` begins. The 72° zoom never ran.
  There is no `[card] entry audit` line for this capture — it died before the audit executed.
- **Altitude-deck stall margin: REFUTED.** FastBomber1 was on the **lower** deck (6500 m) at
  **230 m/s**. Density-corrected stall at 6500 m ≈ 63.9/√0.51 ≈ 89 m/s. It entered at **2.6× stall**,
  the largest margin on the card.
- **Placement blow-up (the R36 signature): CONFIRMED.** ~1 lane in 10 under v0.99.1. Not a
  detachment, so `dmgFrac` will never show it (`dmgFrac` = 0.000 on the row).

**FastBomber1 is excluded from every per-lane comparison and from the #66 baseline.** n = 1 and that
one sample is a physically corrupt trajectory. Its historical record is clean — 40 prior card-batches
across R26–R39 with two aborts, neither this one — so this is not an airframe property. **A future
A/B against this baseline must know the roster is 9.**

### The altitude deck is NOT a confound on this card

`Drone/DroneAltDeckM` was **3000** and active:

> `[drone] launching 10 x '…' at 8000 m / 1.15x corner (per airframe), … 2 alt decks 6500/9500 m (spread 3000 m)`

Deck assignment (from the spawn lines): **6500 m** — Fighter1, SmallFighter1, VTOLTrainer1, COIN,
FastBomber1. **9500 m** — Multirole1, trainer, CAS1, EW1, Darkreach.

**It sets only the spawn instant.** `entry_alt_to = 8000` on **all 73 captures**, no exceptions, and
the card re-places on every replicate. Measured altitude at the *first sample of `alphaHoldFast`*:

| lane | spawn deck | alt at `alphaHoldFast[0]` |
|---|---|---|
| Fighter1 | 6500 | 7926–7927 |
| SmallFighter1 | 6500 | 7928 |
| VTOLTrainer1 | 6500 | 7944–7945 |
| COIN | 6500 | 7962–7983 |
| Multirole1 | 9500 | 7934–7935 |
| trainer | 9500 | 7944 |
| CAS1 | 9500 | 7938–7940 |
| EW1 | 9500 | 7954–7955 |
| Darkreach | 9500 | 7917–7918 |

Lower-deck and upper-deck lanes are **indistinguishable** — 7917–7983 m, a 66 m spread with no deck
structure in it. **72 of 72 `[card] entry audit` lines read "clean".** The density-corrected
stall-margin arithmetic for a 9500 m spawn never touched a scored sample. Deck is recovered and
reported for every lane above; it is a spawn transient of < 3 s, not a flight condition.

---

## 1. GATE FIRST — the three roll/turn rails, and the fourth that is not what it was said to be

`az ≡ 0` on every sample of every scored segment, so the roll/turn rails should be unreachable by
construction. **They are, and the raw rows prove the mechanism, not just the metric.**

| lane | `bankClampActivePct` | `turnRateCapActivePct` | `turnRateDemandRatio` | max\|`targetBank`\| | max\|`bankTR`\| | max\|`azErr`\| | max\|`outR`\| | `bankDemandExcessDeg` |
|---|---|---|---|---|---|---|---|---|
| all 9 lanes, both arms, 144 segs | **0.0000** | **0.0000** | **0.0000** | **0.000** | **0.000** | 0.03–0.51° | ≤ 0.048 | **0.0000** |

**NO LAW BUG.** `targetBank` is computed every tick and reads a literal 0.000 on all **18,436 scored
rows**; `max|bank|` (actual attitude) is 0.1–2.8°, which is placement residual, not commanded. This is
an *exposed-and-zero* reading, not a not-exposed one — the demand path ran and produced nothing.

### `blendRailPct` is NOT ≈ 0, and the card note's stated mechanism is wrong

| arm | `blendRailPct` range across lanes |
|---|---|
| `alphaHoldFast` | 34.4% (Multirole1) → 65.6% (Darkreach) |
| `alphaHoldSlow` | 0.0% (5 lanes) → 28.6% (COIN) |

`cards/alpha-pullup.json` and SESSION §F2 both state that `blendRailPct` "reads `bWt`, which rails on
`|azAl|` past `EvolvedAlignHoldDeg`", hence ≈ 0 under `az ≡ 0`. That is **half the expression**:

```csharp
float lateralHold = Clamp01(azAl / max(0.01, Cfg.EvolvedAlignHoldDeg));   // :2061
float blendWeight = Max(bigTurn, lateralHold) * (1f - _heliBlend);         // :2062
```

`bigTurn` keys off the **total** nose-off-marker angle, and a 72° vertical pull drives `off` to
33.6–70.3°. Measured: `bWt == bigTurn` exactly, row for row; `lateralHold` ≤ 0.51/5 = 0.10 and never
wins the MAX. **`blendRailPct` is exposed by construction on this card and is elevation-driven.** No
segment reaches the 90% `rail_warning` threshold (max 65.6%), so nothing is flagged NO SIGNAL, and
with `outR` ≤ 0.048 the railed roll blend costs nothing. **Correction needed in the card note and in
SESSION §F2's gate list** — do not carry the "four rails ≈ 0" claim forward.

---

## 2. Entry state — read before any gate

Card entry is `1.15 ×` each lane's own FBW corner speed, throttle pinned 0.40, altitude 8000 m. The
scored arms start 6 s (`alphaHoldFast`) and 28 s (`alphaHoldSlow`) after placement, and nothing holds
the speed.

| lane | commanded entry | `spd` at `alphaHoldFast[0]` | drift | `spd` at `alphaHoldSlow[0]` | drift |
|---|---|---|---|---|---|
| Fighter1 | 184 | 196.9–199.8 | +7% | 207.4–208.6 | +13% |
| **Multirole1** | 184 | **201.9–206.0** | **+10%** | 213.1–214.7 | +16% |
| SmallFighter1 | 178.3 | 193.1–196.1 | +9% | 211.2–212.3 | +19% |
| trainer | 149.5 | 154.8–157.1 | +4% | 160.1–161.3 | +7% |
| VTOLTrainer1 | 184 | 187.2–187.3 | +2% | 174.5–175.0 | −5% |
| CAS1 | 184 | 180.7 | −2% | 158.6–158.7 | −14% |
| COIN | 126.5 | 111.1–122.0 | −4…−12% | 95.7–102.6 | −19…−24% |
| EW1 | 149.5 | 156.1–158.0 | +5% | 160.4–162.3 | +8% |
| Darkreach | 115 | 124.4–125.0 | +9% | 152.4–152.8 | +33% |

**Multirole1 · `alphaHoldFast` — the read the brief singled out.** Entry was **201.9–206.0 m/s**, not
the 230 m/s the brief flagged as the exposure-killing drift. The lane **is exposed**:
`aoaPeakOverCeiling` 0.8165–0.8196 against a predicted 0.81 — the redesign's per-lane prediction is
accurate to three decimals — with `aoaLimiterActivePct` 34.38% and `gateMinUp` 0.622–0.636.
**Multirole1 is the lowest-exposure lane, as designed, and it cleared onset.**

**`alphaHoldSlow` entry — the 18 s recovery from a 72° zoom, bounded not designed.** Every lane
except CAS1, COIN and VTOLTrainer1 arrives *faster* than its commanded entry, up to **+33%**
(Darkreach). **This is what costs three lanes their slow-arm exposure**: Fighter1, Multirole1 and
SmallFighter1 arrive at 207–215 m/s carrying the roster's three highest limiters (27, 27, 25 → onsets
17.0, 17.0, 15.25°) and peak at 8.3–9.8° AoA. They are **NOT EXPOSED** on that arm. Treat
`alphaHoldSlow` entry speed as an *uncontrolled* variable in any A/B — it moves ±33% and it decides
exposure.

---

## 3. C1 and C2 — did the guard engage, and did it stay graded

Guard constants per lane, `aoaMargin = min(4, 0.15·lim)`, `aoaFade = max(4, min(6, 0.25·lim))`,
`ceil = lim − margin`, `onset = ceil − fade`:

| lane | `alphaLimiter` | `ceil` | `fade` | `onset`° | `onset/ceil` | clamp binding |
|---|---|---|---|---|---|---|
| trainer / EW1 / COIN / Darkreach | 10 | 8.50 | 4.00 | 4.50 | **0.529** | fade FLOOR |
| CAS1 | 14 | 11.90 | 4.00 | 7.90 | 0.664 | fade FLOOR |
| VTOLTrainer1 | 15 | 12.75 | 4.00 | 8.75 | 0.686 | fade FLOOR |
| SmallFighter1 | 25 | 21.25 | 6.00 | 15.25 | 0.718 | fade CAP |
| Fighter1 / Multirole1 | 27 | 23.00 | 6.00 | 17.00 | **0.739** | margin + fade CAP |

### C1 — `aoaLimiterActivePct` > 0

| lane | `alphaHoldFast` | verdict | `alphaHoldSlow` | verdict |
|---|---|---|---|---|
| Darkreach | 100.00 | **PASS** | 100.00 | **PASS** |
| trainer | 95.31 | **PASS** | 95.83–96.35 | **PASS** |
| EW1 | 84.38–85.94 | **PASS** | 86.46–87.56 | **PASS** |
| COIN | 59.38–90.62 | **PASS** | 95.83–100.00 | **PASS** |
| VTOLTrainer1 | 62.50–64.06 | **PASS** | 58.33–58.85 | **PASS** |
| CAS1 | 62.50 | **PASS** | 75.52–76.04 | **PASS** |
| SmallFighter1 | 50.00–51.56 | **PASS** | 0.00 | **NOT EXPOSED** |
| Fighter1 | 43.75–45.31 | **PASS** | 0.00 | **NOT EXPOSED** |
| Multirole1 | 34.38 | **PASS** | 0.00 | **NOT EXPOSED** |
| FastBomber1 | — | **NOT FLOWN** | — | **NOT FLOWN** |

**C1: 9 of 9 flown lanes PASS on `alphaHoldFast`.** The card's "10 of 10" is unmet only through the
FastBomber1 placement defect.

The three slow-arm zeros are **NOT EXPOSED**, not failures, and the NOT-EXPOSED list is what says so:
`aoaLimiterActivePct = 0` *with* `aoaPeakOverCeiling` **below onset** (0.354–0.438 against onsets of
0.663–0.739) is "the card missed that lane". Their peak AoA (8.3 / 9.7 / 9.3°) is 7–8° short of the
onset. **`aoaLimiterActivePct` is the one metric the scorer leaves raw** — its precondition is not
expressible from the columns — so this is my judgement, made against the recorded AoA and stated as
such.

### C2 — `wobbleEpisodesAoa` and `aoaPeakOverCeiling`

`aoaPeakOverCeiling` ≤ **0.9882** on all 144 segments; the fail band 1.3–2.5 (the v0.57 reactive-relay
signature) is nowhere near. `wobbleEpisodesAoa` = **0** on the 15 exposed lane-arms and **NOT
EXPOSED** on the 3 with `gateMinUp` = 1.000. Every other wobble family is 0 too
(`Bank`/`AzErr`/`OutP`/`OutR`/`OutY`), and `stickRailPct` P/R/Y = 0.

**C2: PASS on 15 of 15 exposed lane-arms; NOT EXPOSED on 3.**

Carry SESSION §1.15's caveat: `wobbleEpisodesAoa` is now near-vacuous — 5 episodes in 7,837 corpus
segments after the transient exclusion — so the weight here is on `aoaPeakOverCeiling` ≤ 1.1, which is
a real, exposed measurement on all 18.

---

## 4. C3 — THE LAW FINDING. The law never backs off; the gate does 100% of the work

C3 is a **fail** criterion: `commandIntoCeilingPct` > 25% on a segment whose `gateMinUp` < 0.5.

| lane | arm | `gateMinUp` | `commandIntoCeilingPct` | C3 |
|---|---|---|---|---|
| Darkreach | fast | 0.023–0.028 | **96.88–98.44** | **FAIL 8/8** |
| trainer | fast | 0.163–0.169 | **71.88** | **FAIL 8/8** |
| EW1 | fast | 0.261–0.265 | **56.25–57.81** | **FAIL 8/8** |
| VTOLTrainer1 | fast | 0.302–0.309 | **39.06–40.62** | **FAIL 8/8** |
| CAS1 | fast | 0.304–0.307 | **34.38–37.50** | **FAIL 8/8** |
| COIN | fast | 0.332–0.367 | **28.12–39.06** | **FAIL 8/8** |
| SmallFighter1 | fast | 0.455–0.471 | 7.81–9.38 | pass |
| Fighter1 | fast | 0.473–0.493 | 1.56–3.12 | pass |
| Multirole1 | fast | 0.622–0.636 | **NOT EXPOSED** | **NOT EXPOSED** |
| Darkreach | slow | 0.129–0.134 | **94.79–95.31** | **FAIL 8/8** |
| trainer | slow | 0.280–0.295 | **51.04–54.17** | **FAIL 8/8** |
| COIN | slow | 0.255–0.290 | **30.73–42.19** | **FAIL 8/8** |
| CAS1 | slow | 0.339–0.344 | **31.77–33.16** | **FAIL 8/8** |
| EW1 | slow | 0.470–0.501 | 2.08–9.90 `[7/8 published]` | pass |
| VTOLTrainer1 | slow | 0.505–0.513 | **NOT EXPOSED** | **NOT EXPOSED** |
| Fighter1 / Multirole1 / SmallFighter1 | slow | **NOT EXPOSED** | **NOT EXPOSED** | **NOT EXPOSED** |

**C3: FAIL on 6 of 9 lanes (fast arm) and 4 of 9 (slow arm).**

### It is not a metric artefact — the conditional is a flat 100%

`commandIntoCeilingPct` only counts samples where the gate is already below 0.5, so a deeper gate
mechanically raises it. Normalising that out — *among the samples where the up-gate was at least half
shut, on what fraction was the raw law still commanding nose-up?*

| lane | arm | samples with `aoaGU` < 0.5 | of those, `tgtPRaw` < −0.05 | median `tgtPRaw` there |
|---|---|---|---|---|
| CAS1 | fast | 184 | **100.0%** | −0.962 |
| COIN | fast | 151 | **100.0%** | −0.926 |
| Darkreach | fast | 512 | 97.1% | −0.565 |
| EW1 | fast | 292 | **100.0%** | −0.931 |
| Fighter1 | fast | 10 | **100.0%** | −0.940 |
| SmallFighter1 | fast | 46 | **100.0%** | −0.968 |
| VTOLTrainer1 | fast | 205 | **100.0%** | −0.957 |
| trainer | fast | 368 | **100.0%** | −0.792 |
| CAS1 | slow | 498 | **100.0%** | −0.417 |
| COIN | slow | 504 | **100.0%** | −0.342 |
| Darkreach | slow | 1460 | **100.0%** | −0.408 |
| EW1 | slow | 76 | **100.0%** | −0.363 |
| trainer | slow | 822 | **100.0%** | −0.345 |

**5,113 of 5,128 gate-biting samples**, and the 15 exceptions are all in Darkreach's fast arm. The
median command is not a marginal residual — on the fast arm it is **79–97% of full nose-up stick**
while the gate is more than half shut.

### And `qSched` had already run

`tgtPRaw` is captured at `ChaseController.cs:1582`, which is **after** the demand schedule
(`:1974`, `tgtP = Clamp(((pErrTerm − coordPull) * qSched + _iPitch + pitchRate*pitchDamp) * PitchGain, −1, 1)`)
and **before** the gate (`:1583`, `tgtP *= tgtP < 0 ? aoaGateUp : aoaGateDn`). The schedule *did*
cut — `qSchedMin` 0.300–0.594 — and the command is still at the rail. **The schedule is not enough,
and the ceiling gate is the only thing standing between this law and the AoA excursion.**

Read it with its mitigation: the gate **held**, on all 9 lanes, with no crossing and no relay. The
architecture works. But the protection is single-point, and this is the first card in the corpus able
to say so with the alpha channel actually loaded.

---

## 5. C5 — did it fire? No, and it structurally could not

`aoaAboveCeilingPct` = **0.00 on 144 of 144 segments**. The scorer withdraws
`aoaRecoverActivePct` / `aoaRecoverPeak` accordingly: **NOT EXPOSED, 18 of 18 lane-arms.**

**C5 has still never fired anywhere in the corpus.** Not a first.

Two independent confirmations that this is real and not an indexing artefact:

- The raw `aoaRec` column is **exactly 0.000 on all 18,436 scored rows**. The only non-zero value
  anywhere in the batch (29.211) is the FastBomber1 blow-up sample — `aoa 46.28` on a 12.75 ceiling —
  which sits in an `arm` segment of an aborted capture and is not a real fire.
- The card's own prediction that "Darkreach is PAST its ceiling from the first sample by design" is
  **false**. Darkreach reaches 0.9859–0.9882 of ceiling and stops there, with the up-gate 97% shut for
  100% of samples. Predicted second-firer trainer reaches 0.9153–0.9176.

**The structural point the next agent needs:** `aoaRecover` = `(max(0, aoaPredSym − aoaCeil) − …)/aoaFade`
(`:1280`) is identically zero below the ceiling, and the up-gate exists to stop the crossing. **C5 can
only fire when C1/C2 fail.** Writing C5 as a *pass* criterion alongside a working guard is a
contradiction in the card, and it should be reworded — "C5 fires" is a guard-failure alarm, not a
success condition.

---

## 6. C4 and C6

### C4 — `qSchedMin` < 1 wherever the limiter was active

**PASS on 15 of 15 exposed lane-arms.** Range 0.300–0.616, never 1.000. The 3 slow-arm lanes with
`aoaLimiterActivePct` = 0 have `qSchedMin` **NOT EXPOSED** (withdrawn by the scorer's `gate_moved`
precondition) — that is the NOT-EXPOSED list's "`qSchedMin` = 1.000 with `aoaLimiterActivePct` = 0",
correctly caught.

### C6 — altitude and energy height

As written: end-altitude ≥ start-altitude **and** `deltaEnergyHeightM` < 0.

| lane | `alphaHoldFast` Δalt (m) | `dEh` (m) | strict | `alphaHoldSlow` Δalt (m) | `dEh` (m) | strict |
|---|---|---|---|---|---|---|
| CAS1 | −18.2 | −108.8 | FAIL (alt) | +16.7 | −218.0 | PASS |
| COIN | −1.2 | −72.9 | FAIL (alt) | +32.8 (min −7.9) | −117.7 | PASS 7/8 |
| Darkreach | **−70.0** | +1.2 | FAIL (both) | +33.2 | −68.3 | PASS |
| EW1 | +12.7 | +1.9 | FAIL (dEh) | +293.9 | +27.4 | FAIL (dEh) |
| Fighter1 | +27.6 | −101.3 | PASS | +491.8 | −26.0 | PASS |
| Multirole1 | +57.3 | −23.8 | PASS | +512.1 | **+204.5** | FAIL (dEh) |
| SmallFighter1 | +30.5 | −42.6 | PASS | +546.1 | +28.0 | FAIL (dEh) |
| VTOLTrainer1 | +1.8 | −143.3 | PASS | +168.4 | −256.0 | PASS |
| trainer | −12.8 | −62.1 | FAIL (alt) | +167.5 | −151.1 | PASS |

Strict: 4 of 9 fast, 6 of 9 slow.

**On intent, C6 passes 17 of 18.** The criterion exists to catch `alpha-sweep`'s signature — descent
into denser air, q rising, more demand buying less AoA. Dynamic pressure change across each scored
segment:

| lane | `alphaHoldFast` Δq | `alphaHoldSlow` Δq |
|---|---|---|
| CAS1 | −5.2% | −18.5% |
| COIN | −9.7% | −28.8% |
| **Darkreach** | **+10.0%** | −9.0% |
| EW1 | −1.0% | −23.2% |
| Fighter1 | −6.9% | −28.5% |
| Multirole1 | −4.6% | −19.1% |
| SmallFighter1 | −4.2% | −28.2% |
| VTOLTrainer1 | −8.2% | −28.9% |
| trainer | −3.9% | −26.0% |

**q falls on 17 of 18 lane-arms**, exactly the sign the redesign argued for. The single exception,
Darkreach · `alphaHoldFast`, loses 70 m out of 7,918 (0.9%) against `alpha-sweep`'s 525–2428 m, and it
is the lane already pinned at 0.988 of ceiling with the gate 97% shut.

The positive `dEh` readings (EW1, Multirole1, SmallFighter1) are the aircraft *gaining* total energy at
throttle 0.40 while climbing — thrust exceeding drag, not the pull failing. Multirole1's +204.5 m over
the slow arm coincides with its +16% entry drift and its 0.354 exposure: that lane is barely loaded on
that arm and is simply accelerating. **The `dEh < 0` half of C6 is measuring propulsion, not the pull,
and should be dropped or rewritten as "q must not rise".**

### One thing the card promised and did not deliver

The design argued the vertical pull is "the only stimulus that can exceed n = 3.24 under this law",
and that "the fighters need n = 4.5–6.4". Measured `gPeak` on `alphaHoldFast`: Multirole1 3.30–3.41,
Fighter1 2.41–2.52, SmallFighter1 2.45–2.55, and 0.33–1.53 on the rest. **Only Multirole1 exceeds 3.24,
and nothing comes near 4.5.** (`gPeak` is the game's `Aircraft.gForce`, a kinematic-acceleration
magnitude, not a load factor — do not read it as one.) The fighters reached 0.82–0.86 of their AoA
ceiling **from falling q and deceleration, not from load factor.** The card works; the stated
mechanism is only partly the one that produced the result.

---

## 7. `qSched` on its absolute floor — COIN, 19.9% of the slow arm

SESSION §1.10 named `qSched = Mathf.Clamp(qRatio, 0.3f, 1f)` (`:1174`) as "armed and un-fired", having
read 1.9% of samples on the STOL card only because that card never delivered its declared 90 m/s.

**It fires here.** COIN · `alphaHoldSlow`: `qSchedMin` = **0.3000 exactly on all 8 replicates**, and
the floor is occupied on **19.9%** of samples. Speed falls to **85.0 m/s** against a 110 m/s corner;
at ρ = 0.531 that is `qRatio` = (85/110)² × (0.531/1.225) = **0.256**, below the 0.30 clamp. Zero
occupancy on all other 17 lane-arms.

This is a ONE-LAW constant biting on exactly one airframe, which is the shape §1.10 predicted. It also
means COIN's slow arm is the one cell in this batch whose demand schedule is set by a constant rather
than by live state — **flag it if COIN is used in any demand-schedule A/B.**

---

## 8. Baseline verdict for ticket #66

**YES — this is a usable baseline, on nine lanes.**

Grounds:

- 144 scored segments, **0 railed, 0 excluded, 0 `unknown_tag`**, all full length.
- Every lane 8 of 8. Replicate σ on `gateMinUp` ≤ **0.0108**, and ≤ 0.0063 on 15 of 18 cells.
- All lanes flew from 8000 ± 33 m regardless of spawn deck; deck is recovered and is not a confound.
- All 72 entry audits clean.
- The three roll/turn rails are exactly zero, so the alpha channel is the only loaded channel — which
  is precisely what `alpha-sweep` could not deliver.

Qualifications a future A/B must carry:

1. **Roster is 9.** FastBomber1 has no baseline value at all.
2. **Three cells will stay NOT EXPOSED post-fix.** Fighter1 / Multirole1 / SmallFighter1 ·
   `alphaHoldSlow` peak at 8.3–9.8° AoA against proportional onsets of 16.2 / 16.2 / 15.0° — the fix
   moves their onset *further away*. They can only contribute on `alphaHoldFast`.
3. **`alphaHoldSlow` entry speed is uncontrolled** (−24% to +33%, §2). Covary it or accept it.
4. **COIN · `alphaHoldSlow` has its demand schedule pinned by the `0.3` q clamp** on 19.9% of samples
   (§7). It is a contaminated cell for anything schedule-related.

### The numbers to diff against

`gateMinUp`, mean ± σ over n = 8. "Predicted post-#66" is the counterfactual under the unclamped
proportional form (`ceil = 0.85·lim`, `fade = 0.25·lim`), computed by inverting the shipped gate to
recover the predicted-AoA peak each replicate actually saw — so it needs no assumption about the AoA
lead. Ticket #66's actual form floors the fade at `_aoaRateFilt × aoaLead` (`aoaLead = 0.30 s`,
`aoaRateTau = 0.15 s`), which on the **slow** arm has a near-zero rate term and therefore collapses to
the proportional value; on the **fast** arm the rate term is large and the shift will be smaller than
tabulated. **This is why `alphaHoldSlow` is the diagnostic arm.**

| lane | arm | **baseline `gateMinUp`** | σ | predicted post-#66 | Δ | Δ/σ |
|---|---|---|---|---|---|---|
| **EW1** | **`alphaHoldSlow`** | **0.4835** | **0.0093** | **0.7736** | **+0.290** | **31** |
| trainer | `alphaHoldSlow` | 0.2845 | 0.0048 | 0.4552 | +0.171 | 36 |
| COIN | `alphaHoldSlow` | 0.2799 | 0.0106 | 0.4478 | +0.168 | 16 |
| Darkreach | `alphaHoldSlow` | 0.1314 | 0.0015 | 0.2102 | +0.079 | 53 |
| CAS1 | `alphaHoldSlow` | 0.3420 | 0.0015 | 0.3909 | +0.049 | 33 |
| VTOLTrainer1 | `alphaHoldSlow` | 0.5091 | 0.0022 | 0.5431 | +0.034 | 15 |
| Fighter1 | `alphaHoldSlow` | NOT EXPOSED | — | NOT EXPOSED | — | — |
| Multirole1 | `alphaHoldSlow` | NOT EXPOSED | — | NOT EXPOSED | — | — |
| SmallFighter1 | `alphaHoldSlow` | NOT EXPOSED | — | NOT EXPOSED | — | — |
| COIN | `alphaHoldFast` | 0.3601 | 0.0108 | 0.5762 | +0.216 | 20 |
| EW1 | `alphaHoldFast` | 0.2628 | 0.0016 | 0.4204 | +0.158 | 99 |
| trainer | `alphaHoldFast` | 0.1644 | 0.0019 | 0.2630 | +0.099 | 52 |
| CAS1 | `alphaHoldFast` | 0.3051 | 0.0013 | 0.3487 | +0.044 | 34 |
| VTOLTrainer1 | `alphaHoldFast` | 0.3060 | 0.0022 | 0.3264 | +0.020 | 9 |
| Darkreach | `alphaHoldFast` | 0.0256 | 0.0014 | 0.0410 | +0.015 | 11 |
| **SmallFighter1** | `alphaHoldFast` | 0.4604 | 0.0046 | **0.4420** | **−0.018** | −4 |
| **Fighter1** | `alphaHoldFast` | 0.4842 | 0.0063 | **0.4230** | **−0.061** | −10 |
| **Multirole1** | `alphaHoldFast` | 0.6271 | 0.0043 | **0.5500** | **−0.077** | −18 |

**The A/B is two-sided, and that is the strongest property of this baseline.** The six floor-bound
lanes (`lim` 10–15) should see `gateMinUp` **rise** — the fade narrows from 4° to 2.5–3.75°, the onset
moves up, the guard engages later. The three cap-bound fighters (`lim` 25/27) should see it **fall** —
their fade *widens* from 6° to 6.25–6.75° and the onset moves down. **A one-sided result is the fix
not being the mechanism.** Every one of these shifts is ≥ 4 σ and most are > 15 σ.

Secondary signal, per SESSION §F2: **`wobbleEpisodesAoa` on trainer / EW1 / Darkreach ·
`alphaHoldFast`. Baseline is 0, exposed, on all three. Any non-zero reading post-fix is the fix
failing** — that is the whole reason the v0.61 floor was put in.

Diff commands:

```
python debugtests/index-captures.py --diff R40 <B> --tag alphaHoldSlow --metric gateMinUp
python debugtests/index-captures.py --diff R40 <B> --tag alphaHoldFast --metric gateMinUp
python debugtests/index-captures.py --diff R40 <B> --tag alphaHoldFast --metric wobbleEpisodesAoa
```

---

## 9. Ambiguous zeros — every one, resolved

The DB was mid-`--rebuild` while this was written. **Everything below was recomputed from the raw
`rows` table** applying `scorecard.py`'s own formulas and exposure preconditions, then cross-checked
against the rebuilt DB after it landed. **The two agree on all 144 segments.** Where they disagreed
during the rebuild window, the rebuilt DB is cited.

| reading | count | resolution |
|---|---|---|
| `bankClampActivePct` / `turnRateCapActivePct` / `turnRateDemandRatio` = 0.0000 | 144 × 3 | **REAL ZERO.** `targetBank` and `bankTR` are literally 0.000 on all 18,436 scored rows — the demand path ran and produced nothing. Exposed-and-zero. |
| `bankDemandExcessDeg` = 0.0000 | 144 | **REAL ZERO**, same mechanism. |
| `aoaAboveCeilingPct` = 0.00 | 144 | **REAL ZERO.** Peak is 0.9882 of ceiling. Published deliberately by the scorer as the exposure signal, never withdrawn. |
| `aoaRecoverActivePct` / `aoaRecoverPeak` = NULL | 144 | **NOT EXPOSED.** Two independent reasons: nothing crossed the ceiling (exposure precondition), and `aoaRec` is a constant-zero column so the indexer drops it ("missing column: aoaRec"). Raw check: 0.000 on all 18,436 scored rows. |
| `gateMinDn` = NULL | 144 | **NOT EXPOSED BY DESIGN.** No mirror push on this card. Raw `aoaGD` = 1.000 on every row. Exactly the reading `ALPHA-CARD-REDESIGN` §5 names. |
| `aoaLimiterActivePct` = 0.00, Fighter1/Multirole1/SmallFighter1 · slow | 24 | **NOT EXPOSED** (my judgement — the scorer leaves this metric raw). Peak AoA 8.3–9.8° against onsets 15.25–17.0°. |
| `commandIntoCeilingPct` = NULL, Multirole1 · fast | 8 | **NOT EXPOSED.** `gateMinUp` 0.622–0.636, never below `GATE_BITING` = 0.5. **This is the exact false pass `alpha-sweep` published on five of eight lanes** and the new scorer now blocks it. |
| `commandIntoCeilingPct` = NULL, VTOLTrainer1 · slow | 8 | **NOT EXPOSED.** `gateMinUp` 0.505–0.513, above 0.5. |
| `commandIntoCeilingPct` = NULL, Fighter1/Multirole1/SmallFighter1 · slow | 24 | **NOT EXPOSED.** No gate at all. |
| `commandIntoCeilingPct` = 0.00 published, EW1 · slow, 1 of 8 replicates | 1 | **REAL ZERO** on that replicate — the gate did dip below 0.5 there and no sample commanded in. The other 7 read 2.08–9.90. The scorer publishes 7 of 8; the 8th is withdrawn. |
| `qSchedMin` = NULL, Fighter1/Multirole1/SmallFighter1 · slow | 24 | **NOT EXPOSED.** No limiter activity to schedule against. |
| `wobbleEpisodesAoa` = 0 | 120 | **REAL ZERO** where exposed (`gateMinUp` < 1.0), but see SESSION §1.15 — the metric is near-vacuous corpus-wide. |
| `wobbleEpisodesAoa` = NULL | 24 | **NOT EXPOSED**, the 3 unexposed slow-arm lanes. |
| `qSchedMin` = 0.3000 exactly, COIN · slow | 8 | **REAL, AND IT IS THE CLAMP.** Not a floor artefact of the metric — 19.9% floor occupancy at 85 m/s / `qRatio` 0.256. §7. |
| `stickRailPct` P/R/Y = 0 | 144 × 3 | **REAL ZERO.** `tgtPRaw` reaches −0.97 but the gate cuts it before `outP`; the stick never rails. |
| `dmgFrac` = 0.000, FastBomber1 | 1 | **REAL ZERO, and meaningless.** The blow-up is not a detachment. Do not write a criterion on it (SESSION §1.9). |

---

## 10. Actions this batch generates

1. **`cards/alpha-pullup.json` and SESSION §F2 both mis-state the `blendRailPct` mechanism.** It is
   `MAX(bigTurn, lateralHold)`, elevation drives it, and it reads 34–66% on this card. Correct the
   note; do not carry "all four rails ≈ 0" forward. §1.
2. **C5 is written as a pass criterion but is a guard-failure alarm.** It can only fire when C1/C2
   fail. Reword it in the card. §5.
3. **C6's `deltaEnergyHeightM < 0` clause measures propulsion, not the pull.** Replace it with "q must
   not rise across the segment", which is the actual `alpha-sweep` discriminator and which this batch
   passes 17 of 18. §6.
4. **C3 is a real, reproducible law finding and it is the most valuable thing in this batch.** The law
   commands into the ceiling on 100% of gate-biting samples at 79–97% of full stick, with the demand
   schedule already applied. It deserves its own ticket, separate from #66. §4.
5. **The placement blow-up cost a whole lane** and it is the same defect as R36. `dmgFrac` cannot see
   it; the signature is a one-frame `spd` discontinuity at `tSeg = 0.000`. A guard on
   `|Δspd| > 10× spd` in one frame would abort-and-respawn instead of abort-and-end-the-suite. §0.
6. **`alphaHoldSlow` entry speed drifts −24% to +33%** and decides exposure on three lanes. If that arm
   is to stay a between-airframe instrument it needs a speed hold, not just a throttle pin. §2.

---

## 11. Provenance

- Captures: `E:\SlowGames\steamapps\common\Nuclear Option\BepInEx\mouseaim-rec-v0.99.1-R40-d{4..13}-*-alpha-pullup-*.csv`
- Logs: `…\BepInEx\LogOutput.log` (fleet launch line 1182; FastBomber1 abort 1322–1341),
  `…\BepInEx\mouseaim-anomalies-v0.99.1-R40-20260802-141344.log`
- DB: `debugtests/captures.db`, R40 = 109 captures / 10 airframes / 3 cards / 1 aborted, `rec` 1–109
- Source read: `ChaseController.cs:1155–1300` (q schedule, AoA guard, predictive lead, recovery bias),
  `:1582–1584` (`tgtPRaw` capture point), `:1974` (`qSched` application), `:2061–2062` (`bWt`)
- Scorer: `debugtests/scorecard.py` — `GATE_BITING` 0.5, `CMD_DEADBAND` 0.05, `BLEND_RAILED` 0.999,
  `aoa_ceiling()`, `aoa_fade()`, `require_exposure()`
- Reproduce the gate check:
  `python debugtests/index-captures.py --query "SELECT c.airframe, s.tag, MAX(s.bankClampActivePct), MAX(s.turnRateCapActivePct), MAX(s.turnRateDemandRatio), MAX(s.blendRailPct) FROM segments s JOIN captures c ON c.id=s.capture_id WHERE c.run_tag='R40' AND c.card='alpha-pullup' AND s.excluded=0 GROUP BY 1,2"`
