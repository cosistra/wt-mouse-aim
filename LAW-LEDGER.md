# Law ledger — what we actually know, 2026-07-31

The backlog has grown faster than the conclusions. This file separates **what has been measured and
reproduced** from **what we merely believe**, with a citation on every line. It is a *state of
knowledge* document, not a plan: [`ROADMAP.md`](ROADMAP.md) says what to do next,
[`LAW-CHARACTERIZATION.md`](LAW-CHARACTERIZATION.md) §7 is the numbered backlog, and this says what
those two are entitled to rest on.

**Corpus** — ~~1 681 captures, 7 462 segments, 999 942 recorder rows, 27 batches (R1…R33), 11
airframes, 24 cards flown of 31 shipped~~ → **updated 2026-08-02: 2 576 captures, 11 015 segments,
2 117 598 recorder rows, 31 batches (R1…R40), 13 airframes, 38 cards flown of 36 shipped + 3
built-ins.** Every SQL figure below is reproducible against `debugtests/captures.db`; the query is
given where it is not obvious. **Re-derive with `--stats` rather than trusting any count in this file
— they were written at R33 and the corpus has since grown ~53%.**

> **TWO CORPUS-WIDE INVALIDATIONS LANDED 2026-08-02. Apply them before reading any line below.**
> Both are in `debugtests/SESSION-2026-08-02.md` §3, which is authoritative.
> 1. **The metric repair (v0.99.1).** `bankClampActivePct` read a column written by a law deleted in
>    v0.60 (27.5% of segments move > 5 pp; 17 verdicts flip to RAILED); the wobble detector was
>    measuring entry transients (318 "episodes" → **5**); and `authorityUsedFrac` / `authBank` /
>    `authAoa` / `authStick` **and the SLACK flag are DELETED**. **Every `authorityUsedFrac` claim in
>    this file is withdrawn, not re-scaled** — the quantity was `mean|bank|/maxBank`, so it never
>    measured effort. See `debugtests/R40-metric-repair.md`.
> 2. **The multi-card ABBA confound is wider than the R31 note below says.** v0.99.1 found `ArmOf`
>    keyed the **queue** index, and a multi-card selection blocks the queue — so **every multi-card
>    A/B batch on disk carries it and must be RE-FLOWN, not re-scored.** R31 is the worst case, not
>    the only one. Single-card batches are unaffected (`_block == 1`).

**Bucket rules, applied without mercy:**

| bucket | admission test |
|---|---|
| **ESTABLISHED** | measured, **reproduced in a second batch or by a crossed design**, and the measurement was neither RAILED nor confounded. Batch, n and effect size on every line. |
| **PLAUSIBLE** | measured once, or measured but confounded, or consistent with the data and never isolated by an A/B. Includes everything resting on n=1 or on a single airframe. |
| **REFUTED / RETRACTED** | believed, then disproved. Cheapest section in the file — every line here is a mistake nobody has to make twice. |
| **OPEN** | the real question, plus the measurement that would close it. |

A RAILED segment is **no signal**. Nothing scored from one appears in ESTABLISHED.

**BATCH SUSPECT — R31 (`20260730-215053`, 96 captures) — every ARM CONTRAST in it must be re-flown,
not re-scored.** R31 is the corpus's only **multi-card armed** batch: 3 airframes × **2 cards**
(`oblique-12-fwd`, `oblique-12-rev`) × 8 replicates, sweeping `BelowAlignSuppress`. The pre-v0.99.1
ABBA index keyed the **queue** position, and a multi-card selection blocks the queue, so within *each
card* the arm is confounded with position — roughly **12 `rec` positions of systematic separation**,
with the two cards leaning in **opposite** directions. That is why nothing warned: the balance tally
ran over the whole queue and cancelled, while `compare-runs.py` groups by (airframe, **card**, arm)
and slices along exactly the confounded axis. **A position confound is not recoverable by
re-analysis** — there is no unconfounded contrast in the data to recover. Consequences, precisely:

- **Suspect, do not cite:** **I5** (it certified the concurrent A/B *using the schedule that was
  broken*), **D11** and **D12** — all three are arm-vs-arm contrasts.
- **Unaffected, still citable:** **D8**, **D9**, **D10** (within-segment observations),
  **D1**'s R31 down/up ratio (geometry, pooled across arms), **X12** (a reading of the source),
  **X15**, **X16**.
- **No other batch is affected**: every other armed batch is one card per lane, where `_block == 1`
  makes the old and new index identical.

---

## 1. ESTABLISHED

### 1.1 The instrument

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| I1 | **The rig does not drift enough to invent an effect.** | Gate A / R22, n=8, `fixedwing-sweep` ([`FLIGHT-PROTOCOL.md`](FLIGHT-PROTOCOL.md) §Gate A) | first-sample `spd` spread **0.10 m/s**; null split worst metric **1.37 sd** vs a 1.40 threshold; `terminalOffDeg` sd **0.046° (0.5 %)** |
| I2 | **Captures are labelled correctly and ABBA arms alternate.** | Gate B / R23, n=4 (FLIGHT-PROTOCOL §Gate B) | `arm=` 0,1,1,0; 64 columns; no `d<n>` leak on a crewed capture |
| I3 | **A drone flies the same law as the player, bit-for-bit, and does not touch the player's aircraft.** | Gates C/D — R24 (n=5), R25 (n=9) | drone `terminalOffDeg` in the crewed band; no marker or stick cross-talk |
| I4 | **Three instrument defects were real and are fixed.** #29 no disk card had loaded at all v0.71→v0.90 (`JsonUtility` dropped `Seg[]`); #30 two-seat airframes double-stepped the control law; #37 `frameMs` read a constant. | [`ROADMAP.md`](ROADMAP.md) "Where we actually are"; R26 (trainer/FastBomber1 flew a 30 s segment in 14.95 s); R27 (**223 899 rows all exactly 16.70 ms**) | all three would have corrupted any law A/B run against them |
| I5 | **The concurrent per-aircraft A/B (v0.94) works in flight.** | R31, 96 captures, 6 lanes | 48 `arm=0` / 48 `arm=1`, ABBA exact on every lane, 136 overlapping pairs on opposite arms, `# config` cannot lie about the arm (R31 §7.3–7.4) |
| I6 | **Frame-time cost of extra lanes is superlinear.** | R28 (8 lanes) vs R29 (10 lanes), comparable ~30 min sessions | rows > 20 ms **16 → 243 (13×)**; distinct stall events **2 → 23 (11×)** for a 25 % lane increase (R29 §5.3) |
| I7 | **The oblique family is UNSATURATED and is the only regime whose metrics can respond to a gain change.** | R27/R28/R29/R30/R31/R33, 4 894 oblique segments | `authorityUsedFrac` median **0.10–0.20** per batch, max 0.78; **0 railed segments in R30, R31 and R33** |

### 1.2 The down-step penalty — the largest measured law effect in the corpus

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| D1 | **At matched step magnitude and mirrored geometry, moving the nose DOWN leaves more terminal error than moving it up.** | R28 (384 caps, 7 of 8 airframes), R29 (441 caps, 9 of 10), R30 (48, order-controlled), R31 (96, arm-controlled), R33 (77) | geomean down/up ratio at 12°: R28 3.49, R29 2.37, R30 2.84 pooled, R31 2.8–5.5 |
| D2 | **It is DIRECTION, not position in the card.** Crossed design: identical geometry, reversed traversal. | R30, 3 airframes × 2 cards × 8 replicates, `oblique-12-fwd`/`-rev` | direction **×3.07 / ×5.39 / ×1.39**; position **×0.98 / 0.71 / 0.79** — 4–7× smaller **and the wrong sign**, so every shipped card *understates* it. Interaction ×1.005–1.150. |
| D3 | **It is not energy, dynamic pressure or airspeed.** Down is worse when it is the slow leg *and* when it is the fast leg. | R30 §5.1 — the crossing is free from the design | fwd: down at 252–276 m/s, up at 273–319; rev: reversed. Ratio > 1 both ways |
| D4 | **It is not terminal elevation.** The DR↔UL mirror terminates at the same commanded elevation in both cards. | R30 §5.2; R28 §3.2 (`oblique-below` at −20° shows a *larger* ratio) | ×2.646 (Fighter1), ×4.607 (Multirole1) on the matched pair |
| D5 | **It is magnitude-gated, essentially absent below the `FineAngle = 6` knee.** | R29 §3.3, 9 airframes | geomean ratio **1.04 (0.5°), 1.18 (2°), 1.06 (2.5°), 1.39 (6°), 3.33 (12°)**; ρ(log ratio, step magnitude) ≥ +0.8 on 8 of 9 airframes |
| D6 | **It is speed-insensitive.** R29→R33 changed the resolved entry speed by −44 % … +22 % per lane (the #41 AI-corner → FBW-corner fix) and the ratios barely moved on 7 of 10. | R29 vs R33, `oblique-6-c`, same card, same tags | COIN 1.54→1.60, VTOLTrainer1 1.35→1.37, EW1 1.17→1.29, CAS1 1.50→1.37, Multirole1 1.80→1.54, trainer 1.33→1.16, SmallFighter1 1.20→1.09 |
| D7 | **`Fighter1` INVERTS it — up is worse — in both batches.** Not noise; it is the airframe with the best score overall. | R29 0.74 → R33 0.61 (geomean of both mirror pairs, `oblique-6-c`) | R33 DR/UL **0.492**, DL/UR 0.921 |
| D8 | **`bSup` / `BelowAlignSuppress` is NOT the transmission path.** `bWt` — the loop gain `bSup` multiplies — is **identically 0.000 over the terminal 1.0 s of all 384 scored R31 segments**, and over the whole late 60 % of 379 of them. The metric is read 5–7 s after the gate shut. | R31 §4.3, 96 captures, both arms, 3 airframes | roll channel closes at t = **0.83–3.10 s** of an 8 s segment; `terminalOffDeg` is averaged over 7.0–8.0 s |
| D9 | **The penalty is created downstream of the roll handover, in the fine regime.** Both hemispheres hand over at the *same* azimuth error; the up leg then closes 93–95 % of it and the down leg 58–80 %. | R31 §4.3 | handover \|azErr\| 1.87–2.69° both directions; converged-to ratio **0.051–0.073× (up)** vs **0.202–0.418× (down)** |
| D10 | **The residual is almost pure azimuth.** | R28 §3.2, R29 §3.4, R31 §4.3 (light jets) | `Fighter1 obDR12` terminal `off` 0.608°, `azErr` +0.608°, `elevErr` **−0.015°** |
| D11 | **Reverting to the v0.67 suppressor moves it 5 %/29 %/2 % and leaves ×2.8–3.9 standing.** Up legs do not regress on either form. | R31 §4.2, paired within (lane, card), n=4 cells | arm0/arm1 **0.948 / 0.709 / 0.980**; up terminal 0.980 / 0.981 / 0.919, every CI touching 1 |
| D12 | **The v0.67 form rails the roll stick and the v0.85 form does not** — the cost v0.85 was shipped to buy is real and in the predicted channel. | R31 §5.2 | `\|outR\| ≥ 0.999` on **1.17–1.49 %** of down-leg ticks (59 of 96 segments) on arm 0, **0.000 %** on arm 1; 17 vs 4 `outR` oscillation episodes |

### 1.3 Cross-airframe generality

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| G1 | **The law is NOT tuned to the Ifrit.** The airframe every pre-R26 capture was taken on is mid-band. | R28 (`Multirole1` rank 4 of 8, ΔA to VTOLTrainer1 = 0.9× noise), R29 (rank 5 of 10, ΔA to CAS1 = **0.1× noise**), R33 (rank 9 of 10 by terminal error) | the long-standing worry is dead |
| G2 | **The airframe ranking is stable across two independent changes of entry condition.** | R28→R29 Spearman **ρ = +0.929, permutation p = 0.0022** (n=8). R29→R33 on `oblique-6-c` **ρ = +0.903 (n=10), +0.967 (n=9 ex-Darkreach)** across the #41 corner-definition change (verified here by SQL) | no airframe moves more than one rank; every swap is inside or near the noise floor |
| G3 | **Entry speed does not explain the spread.** | R29 §Q2, n=10, speeds 86–190 m/s; independently R33 | ρ(A, entry speed) = **+0.188, p = 0.61**; ρ(A, entry ÷ Vstall) = −0.049. In R33, `terminalOffDeg` reproduces R29 to **±17 % on 9 of 10 airframes** while entry speed moved −44 % … +22 % |
| G4 | **The residual spread is real but bounded, and it is not at the incumbent.** | R33, 77 caps, 10 airframes, `oblique-6-c`, **zero railed segments** | per-airframe mean `terminalOffDeg` **0.0646 (trainer) … 0.3819 (SmallFighter1)** = **5.91×**, i.e. **29× the replicate noise floor** (median cell sd 0.0109°) — but on a 6.0° leg, even the worst removes **93.6 %** of the step and the best **98.9 %** |
| G5 | **The R28 spread was ~40 % entry condition and ~60 % law–airframe interaction.** | R28 vs R29, 8 common airframes | `flightscore` A spread 0.237 → **0.1455** (70× → 29× noise); ex-Darkreach 0.146 → **0.082** (16× noise) |
| G6 | **Two-seat crew, FBW `assist=0` and distance-above-corner are all EXCLUDED as causes of the spread.** | R28 §2.3, R29, R32 §7 | both twin-seaters mid-band; `EW1` has `assist=0` and scores mid-band (ρ +0.048); `EW1` flies furthest above corner and outranks `FastBomber1` |
| G7 | **`CAS1` and `COIN` — the two airframes the flat-250 grid could never fly — are ordinary members of the band.** | R29 §4.2, 48/48 captures each, **0 of 192 railed segments each** | rank 6 and 9 of 10; no clamp, cap, rail or AoA gate fires on either |

### 1.4 The game — three corrections verified against the 181 878-line 0.34 decompile

| # | Claim | Evidence |
|---|---|---|
| P1 | **The game has NO G governor.** `ControlsFilter.GLimiter` is dead code — the identifier occurs **exactly once** (`:65242`, its own `protected class` declaration), no field of that type exists, nothing instantiates it, and `LimitG(...)` (`:65277`) has **zero call sites**. | R32 §1.1 |
| P2 | **Over-G damages the PILOT, never the airframe.** `Pilot.TakeGForceDamage` (`:85989`) fires above 20 g and applies `(sqrG − 400)·0.007` to **one part index — the pilot's own**. No structural-G path exists anywhere in the decompile. | R32 §2; confirmed in flight — 3 R32 lanes ended `despawned (pilot killed)` with `aeroPartCount` **35 on all 63 captures** and `massKg` constant to 5 kg |
| P3 | **The game's alpha limiter is gated `if (num2 < 1f)` (`:65033`) and is therefore INACTIVE above corner q — which is where every shipped card flies.** The mod's own AoA block is the only alpha protection in the loop at card speeds. | R32 §1.3 — `num2 < 1` on **2.3 %** of 37 868 R32 rows; **86.3 %** of the 5 541 rows past the airframe's own 10° `alphaLimiter` had the limiter structurally inactive |
| P4 | **`aeroPartCount` cannot see damage.** Nothing on the detach path calls `RemoveFromUnit()`, the only caller of `DeregisterAeroPart` (`AeroPart:74749-74755`), so it never decreases. | CLAUDE.md `Recording.cs` bullet; v0.96 replaced it with `dmgFrac` off `partDamageTracker.GetDetachedRatio()` |

### 1.5 The Darkreach failure

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| K1 | **The precursor reproduces**: the roll-to-align channel commands large bank against a sub-degree azimuth error on a card whose largest demanded step is 0.35°. | R29 (n=1, 55–63° at 1.3° `azErr`) **reproduced in R32** (63 caps, 5 lanes, fresh session, different mod version) | recs 01–31: **0.0° of `targetBank` at \|azErr\| < 5° on every capture**. Recs 32–63: up to **55.5°**, 12 captures over 30°. Precursor appears **1–2 replicates before the first departure in every lane** |
| K2 | **The departure is an AoA/authority failure, not a G failure.** The mod commands \|`outP`\| ≤ 0.24 *against* the excursion the whole way; the plant delivers pitch rate in the opposite direction. | R32 §5, 18 departed captures | `\|fbwPR/fbwTgtPR\|` median **7.73**, p90 13.0, max 28.2 on departed captures vs **1.56** on clean |
| K3 | **The law's entire response to a non-responding plant is a graded stand-down, and it runs out.** Five terms reduce authority (`qSched`'s two 0.3 floors, `Max(0.3f, aoaGateUp)`, `pErrTerm *= _pitchEff`, `aoaRecover *= _pitchEff`); **nothing in `Apply` increases authority or changes strategy.** | R32 §6; [`GENERALITY-REVIEW.md`](GENERALITY-REVIEW.md) finding 18 | `qSched` **exactly 0.300 on 100.0 %** of the 2 314 rows past \|AoA\| 20°, against **0.0 %** on all 31 clean pre-onset replicates of the same card and airframe |
| K4 | **The placement-tick transient (#23) is BIMODAL, and the upper mode is not benign.** | R32 §8, 58 placed `Darkreach` captures | median \|`rollRate`\| **0.753** (reproduces R28's 0.725) but **19 of 58 above 5**, max 54.2; \|`leadDeg`\| max **314°**; **\|`outP`\| rails at 1.000 on 15 of 58 placement ticks** |
| K5 | **The airframe-side half is a specific combination, and `flightAssist = 0` is not it.** | R32 §7, FBW headers of all 10 airframes | unique to Darkreach: `gLimitPositive = 4` (lowest; next is 6), `maxPitchAngularVel = 0.3` and `alphaLimiter = 10` **on 105 409 kg as flown**, `fbwCornerSpeed = 100` against a published 180 |
| K6 | **At a genuine 0.95× FBW corner (95 m/s) the Darkreach flies the card.** | R33, 4 replicates before the damage abort | `terminalOffDeg` **0.2178** (R29 at 171 m/s: 0.5366 — a 2.5× improvement); **zero railed segments**; `authorityUsedFrac` 0.48–0.73 |

### 1.6 R21 / gate-chatter — what the sustained turn actually showed

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| S1 | **The `MaxBankAngle` clamp is a bystander, not the cause of the sustained-turn lag.** The roll servo gives the clamped bank target 2 % weight and flies +8.1° *past* it. | R21, 10 replicates, 4 802 pooled `turn360` samples | `eFine` weight = 1 − `blendWeight` = **0.020**; `eAlign` outweighs the bank path **34:1**; unclamping moves bank by ~0.1° |
| S2 | **`lateralHold` rails at 7.5° and drops the entire bank pipeline to exactly zero weight in a sustained turn.** | R21 (`blendWeight` **1.0000 on 100.0 %** of the settled `turn360`, n=1601); LOOP-AUDIT F2; GENERALITY-REVIEW finding 16 | `blendWeight` = 1 on 97.6 % of the whole segment, 83.6 % of `astern`, 63.4 % of `reversal` |
| S3 | **`_iPitch` is dead outside the 6° fine cone** — it is gated on error *magnitude*, so it is identically ~0 in a large standing error. | R21 (±0.001 against a 0.12 cap for a whole 30 s turn); R32 §5 reproduces it during a departure | v0.83's `IntegralStallGate` exists because of this |
| S4 | **Gate chatter is NOT the cause of the fine-aim complaint.** In the three segments that most resemble the complaint the risk ratio goes the *wrong way*, and real gates score no better than sham gates. | [`debugtests/GATE-CHATTER-FINDINGS.md`](debugtests/GATE-CHATTER-FINDINGS.md), 11 complete `fixedwing-v2` captures, 231 blocks, Mantel-Haenszel + circular-shift null + 4 sham gates | `fine` RR **0.82** at the corpus's highest crossing rate (5.66/s); `micro` RR 0.88; `elDn` RR 1.01. Pooled median RR real **3.65** vs sham **3.16** |
| S5 | **`elDn` is a sustained roll limit cycle in the below-nose hemisphere, and the mirror step in the upper hemisphere converges.** | GATE-CHATTER §5a, 11 runs, late 60 % of the block | `elDn` mean `off` **6.92 ± 2.40°**, bank half-amplitude **43.3 ± 9.2°**, corr(\|azErr\|, `blendWeight`) **+0.918 ± 0.045**; `elUp` (a *larger* step) mean `off` **0.03°**, bank 0.11° |
| S6 | **The fine-cone regression scales with step size, not with gate activity.** | GATE-CHATTER §5b, 10 micro steps × 11 runs | r(REGRESSING %, \|step\|) **+0.785**; **partial** r(REGRESSING %, crossings/s \| off) **−0.632** |

---

## 2. PLAUSIBLE — measured once, confounded, or never isolated

| # | Claim | Why it is not established | What it currently rests on |
|---|---|---|---|
| L1 | **`aircraftGLimit` is the property that tracks the per-airframe spread.** | Collinear with mass / wing area / drag area at pairwise ρ 0.72–0.90; n=10 cannot separate them. "gLimit" is a label on a cluster. | R28 ρ +0.810 (n=8) → R29 **ρ +0.872, p = 0.0023** (n=10), strengthening while its confounders weakened (R29 §2.3). Still ordinal, not mechanistic |
| L2 | **`pEff` is the mechanism of the down-step penalty.** | Its within-card correlation with the residual **flips sign with step size** (+0.52…+0.84 at 2–2.5°, −0.76…−0.99 at ≥6°) — a correlate of demand magnitude. It is also in the **pitch** channel while the residual (D10) is azimuth. | R28 §3.2: 1.7–2.6× less peak pitch stick on the down leg than its mirror; R30 §6.2 rank corr −0.67…−0.93 |
| L3 | **#45 `schedFloor = 0.3f` is a genuine ONE-LAW violation that costs an airframe.** | Measured on **one airframe, one card, one batch**, and R32 itself says the railing is *downstream* of the precursor. `qSched` railing may be a symptom of the departure, not its cause. | R32 §6; GENERALITY-REVIEW finding 18. The *structural* argument (a hardcoded absolute floor on a schedule whose input is correctly relative) is strong independently of the flight data |
| L4 | **#21 (`lateralHold` rail) is what initiates the Darkreach precursor.** | Named as "the standing candidate" and never tested. `darkreach-05` carries no arm. **And the test scheduled for it (§4 Batch 4 row E4) CANNOT RETURN A RESULT as written — verified 2026-07-31.** | R32 §10 — an inference from "34–56° of `targetBank` is the roll-to-align channel", nothing more. **E4 blocker:** on the recs 01–31 "clean baseline" `blendRailPct` is **0.000 on all 124 segments** and `bankClampActivePct` 0.0, so the arm would suppress a channel already at zero weight — arm A ≡ arm B, and the null would read as "#21 is not the precursor". The half where the channel *is* live is the departed half: **20 of 32 captures / 74 of 126 segments RAILED**. No window in this card has the arm both mattering and measurable. Annotated in `LAW-CHARACTERIZATION.md` §4; **not redesigned** |
| L5 | **#23's placement transient is what makes the Darkreach cascade self-sustaining.** | Ordering only: a bad replicate precedes a bad placement precedes a bad replicate. No intervention has been run. | R32 §8; rec 51's first row `outP −0.800` against `off 0.42°`, departed by tSeg 5.967 inside the `arm` |
| L6 | **The per-replicate reset teleport can damage an airframe.** | **n = 1.** `detachedRatioAtStart = 0` on all 77 R33 captures including the one that aborted, so the part came off *during* rec 50; the four preceding replicates were gentle (max AoA 7.6°, max g 2.1) so in-flight loads are not a credible cause — but "during rec 50" is not "at the placement tick". | R33: `Darkreach` recs 10/20/30/40 complete, rec 50 `abort: airframe damage (detached ratio 0.029)`, all five with identical `# entry` (`v=165.8->95.0 snapBackM≈5071`) |
| L7 | **`predFloor = 0.30` is a real, distinct gate defect.** | The relative risk is huge because the baseline is tiny (in `az150`, 4.4 % near a crossing vs 0.16 % away, on a segment that regresses on 1.4 % of ticks overall). | Two independent analyses: GATE-CHATTER §5c (RR 6.5–36.1, p ≤ 0.01, beating every sham by 2–16×, surviving both skip controls) and R21 (binds on **100.0 %** of the settled window, holding `azErrPred/azErr` at exactly 0.300) |
| L8 | **The position effect in a card is energy accumulation.** | n=3 airframes. R30 deliberately lumps order + energy + `arm` attitude because they are all properties of card order. | R30 §4: `Multirole1` has both the largest early→late speed walk (+37.6 m/s) and the largest position effect (0.710); `Fighter1` the smallest of both (0.983) |
| L9 | **`FastBomber1`'s variable-geometry wing explains its 5–10× replicate sd.** | Plausible and unproven; within-card ρ(wingArea, A) = +0.02. | R29 §2.4(b): `wingAreaTotal` **100.2–135.1 m²** with `aeroPartCount` constant at 35 |
| L10 | **The law's problem at the heavy end is pitch authority running out**, not a gain. | `FastBomber1` was "the airframe where the law runs out of pitch" in R28 and then joined the band in R29 at a lower entry speed. | R28 §3.3 (`pEff` median 0.472, standing `elevErr` growing 0.61→3.47° through the card) vs R29 (`pEff` median 1.000, floor-branch occupancy 5.87 % → **0.00 %**) |
| L11 | **`trainer · oblique-12-c` is a card/airframe pair on which an AoA-gate A/B could return non-null.** | One cell, one batch, small activation. | R29 §4.3: `aoaLimiterActivePct` 11.9 % mean on **8 of 8 replicates**, every other healthy cell in the batch 0.0 % |
| L12 | **`_yawWeak` measures "the error did not close", not "the rudder is weak"** — and both its consumers move what it measures. | STRUCTURAL + closed-form; never A/B-ed. | LOOP-AUDIT F3 / GENERALITY-REVIEW 15: closed form on R21's settled turn gives `weakInst` 0.9945 against a recorded max **0.996**, on ticks where the FBW delivers **99.4 %** of commanded rate. `yawWeakFade` removes 57 % of the yaw command; `coordPull *= assist` gates the pitch term on rudder health |
| L13 | **v0.85 `AlignRateLead` makes the roll DERIVATIVE gain a function of `blendWeight`** — 1.00× at 0, **1.64× at 1** — i.e. of the `azErr` the roll loop itself produces. | STRUCTURAL, arithmetic only; the batch that would show it has not been flown, and D8 says `bWt` is 0 over the scored window of every card that has. | LOOP-AUDIT F4 / GENERALITY-REVIEW 17. Measured mean multiplier `turn360` 1.63, `elDn` 1.39. Always stabilising in sign, but it breaks the change's own premise that `RollDamping` is preserved |
| L14 | **`_pitchEff` × `_alphaSchedFilt` are two de-raters of ONE physical event, multiplied to 0.09** where each is documented as flooring at 0.30. | Called "unfalsifiable on a corpus where `aoaLimiterActivePct` is 0" — **which X7 shows is wrong.** It is falsifiable today on R27's `turn360loq` (railed, so read with care) and on R33's Darkreach legs (unrailed). | LOOP-AUDIT F6 / GENERALITY-REVIEW 17 |

---

## 3. REFUTED / RETRACTED — believed, then disproved

**This is the most valuable section in the file.** Each line is a claim that was written down, acted
on, and turned out to be wrong.

| # | The claim that was believed | What killed it | Where it still lives (fix or annotate) |
|---|---|---|---|
| X1 | *"No mod-side G-limiter — the game's stability control governs."* | `GLimiter` is dead code: one occurrence in 181 878 lines, `LimitG` zero call sites. **THE GAME HAS NO G GOVERNOR.** | Corrected in CLAUDE.md Conventions (v0.96), R32 §1.1 |
| X2 | *"The law is bending airframes."* Stated to the maintainer. | Over-G damages the pilot only (`Pilot.TakeGForceDamage :85989`, one part index). No structural-G path exists. `aeroPartCount` 35 on all 63 R32 captures. | Retracted explicitly in R32 §2. **"The law bent an airframe" is not a possible diagnosis.** |
| X3 | v0.88's **aoaTrim theory** — that writing the placement velocity at AoA = 0 caused the entry thump. | Gate B / R23: run 01 is the run's *first* placement, so it was written **untrimmed** — the exact condition v0.88 blamed — and it has the **cleanest entry of the four** (AoA 0.07→1.46° with no overshoot, `off` peak 0.59° vs 1.72–2.87° on the three trimmed ones). | Reverted in v0.89. FLIGHT-PROTOCOL §Gate B finding 1 |
| X4 | Gate A: *"`iPitch`/`iYaw` read 0.0000 on every first row, so `ctrlReset` does what it claims."* | R21 measured `_iPitch` at ±0.001 for an entire 30 s turn — it is ~0 coming out of a turn **whether or not anything reset it**. The observation stands; the inference does not. | Retracted in FLIGHT-PROTOCOL §"This retracts one Gate A claim" |
| X5 | #20: *"the `PEffRevThresh` floor branch is unreachable, so `_pitchEff` never goes below 0.15."* | True only of the **self-probe path**. Corpus-wide, 28 209 rows (4.50 % of 627 110) sit below the threshold, min 0.000, on two fixed-wing airframes — genuine reversed-plant measurements where the no-floor branch is *correct*. | Premise corrected v0.96; re-scoped from experiment to hygiene (LAW-CHARACTERIZATION §7 #20). The old "5.2 % / 8 captures / three airframes" figure **reproduces against no batch** |
| X6 | *"The oblique family is where #20 and #21 get A/B-ed"* (LAW-CHARACTERIZATION §4 Batch 4). | R28: #20's floor branch runs on **0.00 %** of rows on 5 of 8 airframes; #21's rail on **0 of 1344** healthy segments. R29: #20 on **0.00 % of all 10 airframes**, #21 on **0 of 1740**. | Both deprioritized in R28/R29 ranked fix lists; still listed as E4/E5-adjacent in the plan |
| X7 | *"`aoaLimiterActivePct` is 0 in every capture ever taken."* | **FALSE at corpus scale, and this is a new finding.** `SELECT run_tag, airframe, tag, avg(aoaLimiterActivePct) … WHERE aoaLimiterActivePct > 0` returns R26 `trainer·turn360` **99.2 %** and `FastBomber1·turn360` 86.7 %; R27 `turn360loq` **78.7–97.7 % on four airframes**; R11/R13/R18 azimuth steps 20–56 %. **66 (run, airframe, tag) cells in total.** **The unrailed count is 23 or 32 depending on the question, and both are right** (resolved 2026-07-31): `WHERE railed = 0 GROUP BY (run, airframe, tag)` keeps the unrailed segments *of a partly-railed cell* → **34**, or **32** excluding the two legacy no-sidecar `unsegmented` cells (R1, R2) — that is where the 32 came from. `HAVING max(railed) = 0` demands the whole cell be clean → **23**. **Cite 23 for "can an A/B run here"** (a cell whose sibling replicates railed is not a comparison group), 32 for "how much unrailed evidence exists". Partial rescue: the loudest cells (the R26/R27 `turn360` family, 79–99 %) are all RAILED — bank clamp 79–97 %, `authorityUsedFrac` 0.95–1.08 — so they are *no signal*, and they are in the 32 but not the 23. The weaker form, "…never fired in an UNSATURATED capture", is **also false**: R29 `trainer·obUL12` 11.9 % on 8/8 replicates (L11) and R33 `Darkreach·obDR6` **100 % on 4 unrailed segments** (O4). Mechanism, so it reproduces: v0.96's #41 fix dropped that lane's entry 171 → **95 m/s**, so low q — not load — reached the ceiling. | **FIXED 2026-07-31 in all five named sites plus four more the audit missed**: `LAW-CHARACTERIZATION.md` §1 (rewritten wholesale) + §4 Batch 3, `GENERALITY-REVIEW.md` finding 17, `debugtests/LOOP-AUDIT-FINDINGS.md` (4 sites), `debugtests/R28-FINDINGS.md` §1.2, `debugtests/scorecard.py` `alpha_metrics` docstring — **and** `INSTRUCTOR-LOOP.md` §3 (the ORIGIN the scorecard comment cited), `FLIGHT-PROTOCOL.md` §E3, `cards/README.md`, `cards/TOMORROW.md` §6–7. `debugtests/R21-FINDINGS.md:360` still says "currently exactly 0" — left as an in-era pass/fail criterion in a closed doc |
| X8 | R21/LAW-CHARACTERIZATION: *"the bank clamp is what holds the 9.4° sustained-turn lag."* | The clamp is active on 97 % of the turn and discards ~10° of demand — and the roll servo gives that target **2 % weight** and flies **+8.1° past it**. Raising `MaxBankAngle` would move bank by ~0.1°. | R21 §Q1 "The causal half: REFUTED" |
| X9 | INSTRUCTOR-LOOP §5: *"independent hysteresis-free gates chatter and that is the cross-fighting the maintainer feels."* | Killed where proposed: RR 0.82 / 0.88 / 1.01 in the three most relevant segments, real gates indistinguishable from sham gates. The prescription "fewer gates, with hysteresis" is **not supported and should not be spent on**. | GATE-CHATTER verdict; `gatechatter.py` kept for reproduction only |
| X10 | R28 §3.2: *"`bSup` reads 0.000–0.06, so belowness is excluded as the mechanism."* | **That was a median.** The mean is 6× asymmetric (0.240/0.293 down vs 0.045/0.041 up on R28's own captures). | R30 §6 re-measured R28's data and reinstated `bSup` as a lead — which R31 then killed on different grounds (D8) |
| X11 | R28 §4.3 / FLIGHT-PROTOCOL: *"#23 does not reproduce and is confirmed harmless."* | R28 measured only the **lower mode of a bimodal distribution**. R32's upper mode: \|`outP`\| rails at 1.000 on 15 of 58 placement ticks. | Scope corrected in LAW-CHARACTERIZATION §6/§7 #23, FLIGHT-PROTOCOL §Gate B, R32 §8. "Harmless to results so far" is **retired** |
| X12 | *"`arm=0` on `BelowAlignSuppress` disables the suppression."* A whole batch was commissioned on it. | `ChaseController.cs:2048–2050` is a ternary between two **forms**, not on/off. `arm=0` is the v0.67 body-frame form. Mean `bSup` on a down leg is 0.145–0.404 on arm 0, not 0. **The true "off" arm has never been flown.** | R31 §1. Action item still open: rename the knob or make the `false` branch zero |
| X13 | R28's headline *"1.2–17.9× the terminal error"* as a property of the law. | The **sign** is robust; the **size** is not. Geomean over 26 matched cells R28 3.49 → R29 2.37, with individual cells moving up to 7× in either direction and two airframes flipping sign. | R29 §3.2 |
| X14 | R28's *"treat any non-zero count of 33.3 ms rows as the stop signal for going wider."* | **Zero rows at 33.3 ms, and zero rows anywhere in [30, 40] ms** — the frame time does not quantise to 2× vsync on this machine. A rule that provably cannot fire is not evidence when it does not fire. | Retired in R29 §5.3; replace with a rate |
| X15 | *"`FastBomber1` is a failure airframe."* | R29: 0.559 → 0.662, joined the band, `pEff` median 0.472 → 1.000. But R30/R31 then showed its **replicate CV is 30–43 %** and its two lanes disagree by 3.8×, so it is not a *failure* — it is **unusable as a measurement**. R33 confirms: mean cell CV **74 %**, against 4–17 % on every other airframe. | Keep it as a stressor; **do not quote its ratios** (R30 §7.5, R31 §8) |
| X16 | LAW-CHARACTERIZATION §1: *"19 cards, ONE has ever been flown, on ONE airframe, and it is saturated."* | Badly stale. **24 cards flown, 11 airframes, 1 681 captures, 27 batches**; R30/R31/R33 have **zero railed segments** between them. | §1 of the standing plan reads as if R26–R33 never happened; it is the first thing a new agent reads |
| X17 | *"The Darkreach is the only airframe with `flightAssist = 0`."* | `EW1` has it too and scores mid-band. | Corrected inside R32 §7 before it could propagate |
| X18 | *"R29's 26.9 g means the airframe was overstressed."* | It is a **readout** of a departed airframe at 80 m/s and −87° AoA, falling. It damaged the pilot, nothing else. | R32 §2/§9. Consequence: **do NOT add a mod-side G-limiter** — it protects nothing, deletes the most legible failure signal, and would be a sixth de-authorizing term on a law whose defect is that it already has five |

---

## 4. OPEN — the real questions, and what would close each

| # | Question | Why it is still open | The measurement that closes it |
|---|---|---|---|
| O1 | **What arrests the down leg in the fine regime?** (D8/D9) | `bSup` is out of the loop by t = 3.1 s and the metric is read at 7–8 s. The yaw channel is *equally dead* in both hemispheres (achieved yaw rate 0.004–0.009 °/s in every cell) while the command differs 3× — so it is *where the aircraft got arrested*, not how hard the loop pushes. `iGate` reads 0.87–0.97 both directions, so it is not an integrator gate. R31 §6 names the untested alternative: **plant asymmetry the law does not model** (g falls to 0.14–0.55 in the terminal window on both directions). | A **long-dwell** oblique: the same 12° diamond with 30 s legs instead of 8 s, 8 lanes. If the down leg eventually converges, it is bandwidth; if it parks at 0.55–0.81°, it is a standing equilibrium and the next instrument is a per-term decomposition of `outY`/`outP` in the fine cone |
| O2 | **Is the residual spread a law problem or an airframe-capability difference?** | The four candidate properties (gLimit / mass / wing area / drag area) are collinear at ρ 0.72–0.90 and no fixed-wing key in the game breaks the cluster (R29 §Q2.3 says this may be *unresolvable with current game content*). | A card that varies **loading on ONE airframe** — the only way to move mass without moving gLimit. Blocked on backlog **#19** (the `Loadout` object), which is blocked on one in-game dump |
| O3 | **Does #21 (`lateralHold` rail) cost anything?** | **It is currently unmeasurable anywhere in the corpus.** `SELECT count(*) FROM segments WHERE blendRailPct>=90 AND railed=0 AND excluded=0` returns **0** of 7 462. Every time the bank pipeline rails, so does everything else. The unsaturated sweep cards (`sweep-slow`/`-creep`/`-step`) have `blendRailPct` = **0.0**; the ones that rail it (`sweep-lowq` 93–98 %, `fixedwing-sweep`/`turn360` 27–97 %) are railed 8/8. | A card that holds \|azErr\| between 7.5° and the bank clamp for 20+ s. Nothing shipped does this. **This invalidates the current shape of Batch 4 row E4**: on `darkreach-05`, recs 01–31 (the clean baseline) have `blendRailPct` **0.0** and `authorityUsedFrac` 0.24–0.34, so the arm would suppress a channel that is already at zero weight; recs 32–63 are railed on 18–19 of 32 |
| O4 | **Does the mod's AoA path work?** | Never scored in an unsaturated capture until now. The α-cards (`alpha-steps`, `alpha-sweep`) have **never been flown**. | R33 just produced the first clean data: `Darkreach obDR6` at **100 % `aoaLimiterActivePct`, `railed = 0`, `authorityUsedFrac` 0.725, terminal 0.257°** (n=4, one lane, aborted on damage). Re-fly it, plus `alpha-steps` on the 8-key roster |
| O5 | **What sets the R32 onset at replicate ~32?** | Ruled out by measurement: frame hitches, mass, fuel, damage, config edits, entry state. Wall clock and replicate index are confounded because the lanes launched together. | A card with a deliberately staggered *start* (not just a staggered launch), so wall clock and replicate index separate |
| O6 | **Does removing `belowSuppress` entirely remove the down-step penalty?** | No arm has ever flown with `belowSuppress == 0` (X12). | A **code change** — make the `false` branch zero, or add a third form — then re-fly `oblique-12-fwd`/`-rev`. Not a card question |
| O7 | **Does the precursor CAUSE the Darkreach departure, or share a cause?** | R32 establishes only ordering (precursor 1–2 replicates earlier, in every lane). | A card that suppresses the roll channel and changes nothing else — but see O3: the arm has no effect during the clean period |
| O8 | **Is `EW1` doing the same thing more slowly?** | Same `assist = 0`, same `maxPitchAngVel` 0.3, same `alphaLimiter` 10, at a quarter of the mass. Never flown on this card. | One lane of `darkreach-05` with `EW1` in the airframe list. Cheap |
| O9 | **Rotorcraft, STOL, and the whole attribution set are UNFLOWN.** | 12 of 31 shipped cards have zero captures: `alpha-steps` `alpha-sweep` `oblique-above-c` `e1-below-control` `e1-below-suppress` `e1b-align-lead` `e2-rel-turn-lead` `e3-marker-ff` `rotor-bob` `rotor-hover` `stol-steps` `stol-sweep`. Two of the ONE-LAW rule's four named cases (STOL trainer, hovering helo) have **never been measured on a card**. | `stol-*` is runnable today. `rotor-*` is blocked on backlog **#39** (`startSpeed: 0` means both "hover" and "not specified") plus a hover entry mode |
| O10 | **Does the law ever move the nose AWAY from the demand?** (Pillar 1, backlog #33) | Never measured. Every "is the law converging or fighting itself?" question so far was answered by proxy. | **Zero flying.** Derivable from the `off` column on all 1 681 captures already on disk |

---

## 5. Is it one law?

**Yes in form; no in outcome — and the residual is small enough that the honest answer is
"unproven either way, and probably not worth calling a violation."**

Three numbers, in descending order of how much they should move your opinion:

**(a) The spread is real and it is not at the incumbent.** R33, 77 captures, 10 airframes, one card,
**zero railed segments**: per-airframe mean `terminalOffDeg` runs **0.0646° (trainer) → 0.3819°
(SmallFighter1)**, a **5.91× ratio** and **29× the replicate noise floor** (median cell sd 0.0109°).
That is far outside noise. It is not measurement scatter and it is not the Ifrit (G1).

**(b) But every airframe removes 94–99 % of the demanded step.** On the 6.0° leg the worst airframe
ends **0.382°** off and the best **0.065°** — i.e. **93.6 %** and **98.9 %** of the step closed. A
5.9× ratio between two small numbers is not the same finding as a 5.9× ratio between two large ones,
and nothing in the repo currently says this out loud. The R28 headline ("the two heaviest airframes
fail outright") was true of the flat-250 entry condition and is **no longer true**: at
`0.95× fbwCornerSpeed` the heaviest airframe in the game flies the card at 0.218° (K6), and the two
airframes that could not fly it at all are ordinary members of the band (G7).

**(c) The spread does not track the flight condition, so it is a property of the law–airframe
interaction.** ρ(A, entry speed) = +0.188, p = 0.61 at n=10 (G3), and in R33 the terminal error
reproduced R29 to **±17 % on 9 of 10 airframes** while the resolved entry speed moved by −44 % to
+22 % (G2, G6). Rank order survives at ρ = +0.903 (n=10) / +0.967 (n=9).

**So: law problem or airframe-capability difference?** The evidence supports *neither* cleanly, and
that is the honest state:

- The best correlate is `aircraftGLimit` (ρ +0.872, p 0.0023) — an **airframe capability**. If that
  is the truth, the spread is not a ONE-LAW violation at all; it is the law correctly getting less
  out of a less capable airframe, and `flightscore`'s `A` normalizer (which divides by
  `omega_avail`, itself derived from `maxPitchAngularVel` and `gLimitPositive`) is supposed to have
  removed exactly that.
- But gLimit is collinear with mass/wing/drag at ρ 0.72–0.90 and **n = 10 cannot separate them**
  (L1, O2). Calling it "airframe capability" is currently a label, not an identification.

**The two places where the ONE-LAW rule is genuinely violated are structural, not statistical**, and
both are visible in the source rather than in the spread:

| violation | why it is a violation regardless of the flight data |
|---|---|
| `schedFloor = 0.3f` (`ChaseController.cs:1255`) and its sibling `Max(0.3f, aoaGateUp)` (`:1296`) | a hardcoded absolute terminates a schedule whose input (`aoaUtil`) is correctly *relative* to a probed ceiling — same floor for a 27° ceiling on 8.7 t and a 10° ceiling on 105 t (R32 §6, GENERALITY-REVIEW 18) |
| `_yawWeak`'s normaliser `Clamp01(closeRate / 6f)` | an absolute deg/s constant — "a per-airframe constant in disguise", which the v0.83 `_stallFilt` comment two blocks away explicitly forbids. The right denominator (`omegaMax`, probed + live) is computed nearby (GENERALITY-REVIEW 15) |

And the deeper structural statement, which is the single most important thing in this file:
**every one of the five terms that responds to a non-responding plant REDUCES authority, and there is
no sixth term that does anything else** (K3). That is a design property, not a per-airframe one — it
just happens to be survivable on nine airframes out of ten because their own stability covers the gap.

---

## 6. The next three measurements, and why those three

Ordered by **information gained per minute of flying.**

### 1. The retreat integral (#33) — **0 minutes of flying**

Re-score all 1 681 archived captures for `retreatDeg` / `retreatEpisodes` / a monotonicity index off
the existing `off` column, then re-index (`index-captures.py`, ~30 s).

**Why first:** it is free, it applies retroactively to every batch already flown, and it answers the
one question the whole corpus has been answering by proxy — *does the nose ever move away from the
demand?* O1 (the largest open law question) is a convergence question and there is currently no
convergence metric. `terminalOffDeg` cannot distinguish "converged slowly to 0.6°" from "reached
0.2°, backed off, and settled at 0.6°", and those two have different fixes.

**~~Fold in for free while re-scoring: fix X7 in the same pass.~~ DONE 2026-07-31** — corrected in
**nine** files, not five (the audit missed `INSTRUCTOR-LOOP.md` §3, which is the origin the
`scorecard.py` comment cited, plus `FLIGHT-PROTOCOL.md`, `cards/README.md`, `cards/TOMORROW.md`).
The index says non-zero on **66** (run, airframe, tag) cells — **23 fully unrailed / 32 with some
unrailed segment**, see X7 for which to cite. `scorecard.py`'s comment block was the one justifying
the `alpha_metrics` design: **the design was re-checked and stands** (nothing there consumes the
false premise; only the justification was wrong), so the comment was fixed and the code was not
touched. What the check *did* surface: `alpha_metrics` runs only on `alpha_step`/`alpha_hold`
(`scorecard.py:1143`), so on the one clean capture that reached the ceiling — an `oblique_step` —
none of its eight metrics exist. Tag the re-fly `alpha*` or widen the gate; recorded in
`LAW-CHARACTERIZATION.md` §1 and §4 Batch 3.

### 2. The long-dwell oblique — **~20 min unattended, 8 lanes**

`oblique-12-fwd` / `-rev` geometry with **30 s legs instead of 8 s**, on the eight fixed-wing keys
that clear the pre-spawn gate, 8 replicates, no arm.

**Why second:** the down-step penalty is the **largest measured law effect in the corpus** (×2.8–5.5,
60–240× the replicate noise), it is universal across 7–10 airframes, it survives a crossed order
control, and after four batches (R28, R29, R30, R31) it is localised to a 5-second window nobody has
looked inside. R31 §4.3 is precise about the gap: both hemispheres hand over at the same error, and
the down leg then stops closing. **This is the cheapest test that can distinguish the two remaining
hypotheses** — bandwidth (it converges eventually) versus standing equilibrium (it parks) — and it
needs no code change and no new lever.

**Not** another lever sweep. R31 spent a whole batch proving `bSup` is out of the loop before the
metric is even defined; a second sweep of a gate that closes at t = 3 s would repeat that.

### 3. The AoA path, on the one airframe that just produced clean data — **~15 min, 2 lanes**

Re-fly `oblique-6-c` on `Darkreach` + `EW1` at `0.95× fbwCornerSpeed`, 8 replicates each, plus
`alpha-steps` (never flown) on the 8-key roster with `repeat: 8`.

**Why third and why now:** R33 produced the **first unsaturated capture in the corpus where the mod's
AoA machinery is live on a healthy fixed-wing airframe** — `Darkreach obDR6`, gate active on 100 % of
samples, `railed = 0`, `authorityUsedFrac` 0.725, terminal error 0.257°. The α-path has been the
blocked item in `LAW-CHARACTERIZATION.md` Batch 3 since it was written, on the grounds that nothing
ever provoked the regime. **That is no longer true.** It also settles O8 (`EW1` shares Darkreach's
`assist=0` / `maxPitchAngVel 0.3` / `alphaLimiter 10` at a quarter of the mass) in the same launch,
and it is the only route to L3/#45 that does not require a departure.

**Bring the L6 caveat:** R33's Darkreach lane shed a part on its fifth placement. Expect to lose the
lane, watch `dmgFrac`, and treat 4–8 replicates as the realistic yield rather than 8.

---

### What is deliberately NOT in the next three

| item | why not |
|---|---|
| **E4 — the Darkreach precursor with the roll channel as the arm** | O3: on `darkreach-05`'s clean baseline (recs 01–31) `blendRailPct` is **0.0** and `bWt` median is 0.000, so the arm suppresses a channel already at zero weight; the departed half is railed on 18–19 of 32. The card would produce a null in the control period and no signal in the treatment period. Fix the card before flying the experiment. |
| **E5 — the `schedFloor` A/B (#45)** | R32 is explicit that fixing the stand-down before the precursor is understood makes *some* departures survivable, which is worse than a departure that is legible. Also L3: n=1 airframe. |
| **#20 as an experiment** | X5/X6: the branch executes on 0.00 % of rows on all 10 airframes at the current entry conditions. Ship the `>=` → `>` as hygiene behind a checkbox; do not commission a batch. |
| **The belowness axis (E1, `oblique-above-c`)** | X12: `arm=0` is a form selector, not "off". Flying the axis before the knob semantics are fixed repeats R31. |
| **Rotorcraft** | O9: blocked on #39 and a hover entry mode. Genuinely the riskiest item on the list (writing physics state to a rotorcraft) and it must not be run unattended first. |
