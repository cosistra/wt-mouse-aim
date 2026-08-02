# R39-F — the Darkreach damage aborts: the failure is indexed on PLACEMENT COUNT, and the placement is not the proximate trigger, v0.98.1

Ledger **#51**, third instrumented reproduction. R39 flew 310 indexed captures across five cards
(plus 10 unindexed `stol-steps` captures, lanes 57–66, not in `captures.db`). **Darkreach flew 23 —
22 indexed — where the five two-lane airframes flew 40, and it aborted three times on airframe
damage, once on each of the three cards it was on.**

```
rec 168  d40  e3-marker-ff      rep 5  detached 0.029  dur 14.0s  224 rows  snapBackM 7118.6
rec 229  d48  alpha-sweep       rep 5  detached 0.114  dur 15.3s  245 rows  snapBackM 5104.4
rec 282  d56  e2-rel-turn-lead  rep 4  detached 0.029  dur 13.9s  223 rows  snapBackM 6936.6
```

Everything below is reproducible from `debugtests/captures.db`, the R39 CSVs in
`<game>/BepInEx/`, `LogOutput.log`, and the 0.34.1 decompile. **Every claim states its n.**

---

## Verdict

1. **All three aborts are AIRBORNE, not at the placement.** 13.9–15.3 s after the placement, 7.9–9.3 s
   into the card's *second* segment, ~820–900 fixed steps and **23–26 complete `PartChecker` sweeps**
   past the entry. The geometric excursion began inside a 0.6 s window that does not contain the
   placement. §1, §2.
2. **`dmgFrac` is not "unreliable" — it is structurally incapable of ever being nonzero on a capture the
   harness aborts.** This is now proved from the decompile, not inferred from one capture. 641,555
   indexed rows across R33/R35/R36/R37 carry `dmgFrac`; **zero are nonzero, zero are the `-1` sentinel**,
   against 8 damage aborts. §3.
3. **`0.114` is ONE detach event, not four.** `UnitPart.Detach` cascades `onParentDetached` down the whole
   subtree and `PartDamageTracker` counts `detachedFromUnit`, so the ratio is **subtree size, not event
   count**. 0.029 = a leaf, 0.057 = a part with one child, 0.114 = a part with three descendants,
   0.026 = an EW1 leaf. The graveyard's "sweep latency" reading of R37's 0.114 is wrong: the tracker
   recounts within **one fixed step** of the first detach, and `PartChecker` cannot latch four parts in
   one step. §4.
4. **THE HEADLINE: the failure is indexed on the number of PLACEMENTS the drone has had, and on nothing
   else measured.** Under v0.96+ every Darkreach lane scheduled ≥5 replicates died — **6 of 7 at
   replicate 5, one at replicate 4, zero at 1–3, zero survivors past 5**, across 4 mod versions,
   6 distinct cards and 2 lane distances. It is invariant to per-replicate flight time (**32 s vs 126 s,
   3.9×**), to per-placement snapback (**5.0 km vs 25.4 km, 5.1×**), to cumulative snapback (**13.8 km vs
   75.9 km, 5.5×**) and to lane distance (**49.5 vs 62.0 km**). §5.
5. **That refutes the v0.97.2 exoneration as written, and does not restore #51's original premise
   either.** The float-grain argument ("~0.004 m, 125× under threshold, therefore the placement cannot
   produce an attach failure") is an argument about **one** placement. The data indexes on the **count**.
   But the R39 excursions happen 14 s downstream of the placement, so the placement is not the
   proximate trigger. What survives both facts is narrow and awkward: **the placement leaves the
   airframe in a state that fails a fixed number of placements later.** Neither "it's the placement" nor
   "it's flight loads" is supported as stated. §2, §5, §8.
6. **The frame hitch is refuted, hard.** The three aborted captures contain **no frame over 16.9 ms**
   (fixed step 17 ms) against a batch maximum of 152.2 ms, and they sit inside the longest hitch-free
   stretch of the session's log. Seven concurrent lanes ate a 126.7 ms hitch 16.2 s *after* abort #3 and
   shed nothing. §7.
7. **Darkreach-specificity is OBSERVED, not explained, and R39 cannot explain it.** Every single-parameter
   correlate anyone would reach for — mass, wing area, drag area, part count, `alphaLimiter`,
   `fbwCornerSpeed`, `gLimitPositive`, lane distance, `authorityUsedFrac` — is refuted by a non-shedding
   airframe. **One** survives: `maxPitchAngularVel = 0.3` **and** `maxRollAngularVel = 3`, held by exactly
   Darkreach and EW1, which are exactly the two airframes that have ever shed a part. That is n = 2 and
   it is unfalsifiable from this corpus. §6.
8. **The flight loads at the abort are the gentlest in the batch.** g 2.2–3.4 against a batch peak of
   7.15; AoA 3.1–5.4° against 13.0°; both aborts on turning cards are at ~0.85 of Darkreach's own
   `gLimit` while Fighter1 hits 0.69 of its own and never sheds. What *is* extreme is
   **sustained** attitude and stick: bank 70–77° held for seconds at `outP` −0.53…−0.68 and
   `authorityUsedFrac` 0.441, the highest in the fleet by 1.6×. §2, §6.

---

## 1. Where in the capture it happens

| | rec 168 | rec 229 | rec 282 |
|---|---|---|---|
| card | `e3-marker-ff` | `alpha-sweep` | `e2-rel-turn-lead` |
| segment at abort | `turn360mff` (2/2) | `alphaHold` (2/2) | `turn360rtl` (2/2) |
| `tSeg` at abort | **8.0 s** | **9.3 s** | **7.9 s** |
| `t` at abort | 1425.717 | 1801.333 | 2128.233 |
| last row `t` | 1425.700 | 1801.283 | 2128.200 |
| gap, last row → stop | **1 fixed step** | 3 steps | 2 steps |
| `arm` segment | 0.000 → 5.950 s, 96 rows, clean | 0.000 → 5.917 s, 96 rows, clean | 0.000 → 5.950 s, 96 rows, clean |
| `dmgFrac`, all rows | **0.0 ×224** | **0.0 ×245** | **0.0 ×223** |
| `frameMs` max | 16.8 | 16.9 | 16.7 |
| `datumX/Y/Z` | `(0,−4032,0)` const | same | same |
| `# entry` | v 260.8→250.0, alt 3888.8→4000.0, snapBackM 7118.6, ctrlReset 1 | v 279.7→250.0, alt 6656.2→8000.0, snapBackM 5104.4, ctrlReset 1 | v 278.7→250.0, alt 3123.6→4000.0, snapBackM 6936.6, ctrlReset 1 |
| entry audit | `250 m/s, clean (commanded 250)` | clean | clean |
| `sc_detachedRatioAtStart` | 0 | 0 | 0 |

State in the 0.6 s before the abort — the only window the excursion can be in (§2):

| | rec 168 | rec 229 | rec 282 |
|---|---|---|---|
| `spd` | 264.5 → 264.8 | 272.5 → 272.8 | 265.3 → 265.6 |
| `g` | 1.41–2.40 (capture peak 3.40) | 2.27–3.07 (peak 3.39) | 1.56–2.64 (peak 3.38) |
| `aoa` | 3.14–3.38 (peak 3.58) | 5.37–5.38 (peak 5.38) | 3.18–3.47 (peak 3.54) |
| `bank` | 69.4 → 72.3 | 76.8 → 76.1 | 67.9 → 70.9 |
| `outP` | −0.526…−0.547 | −0.662…−0.675 | −0.496…−0.543 |
| `thr` | 1.000 | 1.000 | 1.000 |
| `origDist` | 50 297 → 50 362 m | 50 558 → 50 652 m | 50 287 → 50 349 m |
| `alt` | 3894 → 3884 m | 7798 → 7779 m | 3890 → 3879 m |

Nothing moves. There is no transient, no spike, no discontinuity in `spd`, `pos`, `g`, `aoa`,
`rollRate` or `frameMs` in the rows before any of the three aborts. **The recorder sees a completely
ordinary sustained turn right up to the last sample.** That is itself a finding: whatever moved 0.5 m
did not move the aircraft's flight state, which is consistent with a *part* excursion and inconsistent
with any bulk-motion or teleport mechanism.

---

## 2. Placement, or flight?

**Flight, in all three — with a hard bound, not a judgement call.**

The detection chain and its latency, from the 0.34.1 decompile:

- `Aircraft.PartChecker.Check` (`:60157–60180`) calls `CheckAttachment` on **one** part per fixed step,
  round-robin over the private `partsWithAero`. 35 parts × 17 ms = **0.595 s per full sweep**.
- `AeroPart.CheckAttachment` (`:74349`) latches `attachInfo.detachedFromParentPart` and calls
  `parentUnit.DetachPart` the moment `FastMath.OutOfRange(attachInfo.localPosition, b, 0.5f)` holds.
- `PartDamageTracker.GetDetachedRatio` (`:79443`) early-returns the cache **only** while `!needsCheck`,
  and `lastCheck` is advanced **only by an actual recount**. On a clean aircraft no recount has ever
  run, so `lastCheck == 0`, `Time.timeSinceLevelLoad − 0 ≫ 1`, and the **first** call after the first
  `onPartDetached` recounts immediately.
- `ScenarioPlayer.Tick` calls it every fixed step and aborts on `dmg > 0`.

So: detach → nonzero read is **≤ 1 fixed step**. Excursion onset → detach is **≤ 0.595 s** if the
excursion persists, and if it does not persist it is caught only while in progress. Either way the
**onset is inside [abort − 0.6 s, abort]**.

| | placement at | abort at | separation | full sweeps in between |
|---|---|---|---|---|
| rec 168 | t = 1411.75 | t = 1425.72 | **13.97 s** | 23.5 |
| rec 229 | t = 1786.05 | t = 1801.33 | **15.28 s** | 25.7 |
| rec 282 | t = 2114.32 | t = 2128.23 | **13.92 s** | 23.4 |

**In R39 the placement is 23–26 complete sweeps outside the detection window.** A persistent
placement-induced displacement would have latched in the first 0.6 s of the `arm` segment; the `arm`
segment ran its full 5.95 s clean on all three.

**Historically it is mixed, and that matters.** Of the seven Darkreach damage aborts, three *are* inside
the placement window and four are not:

| run | rec | rows before abort | ≈ s after placement | in the 0.6 s window? |
|---|---|---|---|---|
| R33 | 50 | 1 | 0.07 | **yes** |
| R35 d10 | 74 | 3 | 0.19 | **yes** |
| R37 d10 | 74 | 5 | 0.31 | **yes** |
| R35 d24 | 165 | 375 | ~23.4 | no |
| R39 d40 | 168 | 224 | 13.97 | no |
| R39 d48 | 229 | 245 | 15.28 | no |
| R39 d56 | 282 | 223 | 13.92 | no |

So "the placement snaps a part off" cannot be the whole story — four of seven happen far downstream —
and "it is ordinary flight loads" cannot be either, because the flight is ordinary and the *replicate
index* is not (§5).

---

## 3. What `dmgFrac` actually does — established, not assumed

The task said to establish this empirically before leaning on it. It is worse than "not trustworthy":
**it is dead by construction.**

The recorder (`Recording.cs:524`) and the abort check (`ScenarioPlayer.cs:1658`) call the **same**
method on the **same** aircraft. Per §2 the tracker recounts within one fixed step of the first detach.
`ScenarioPlayer.Tick` runs before the row is emitted, and `Abort` → `StopRecord` closes the file
immediately. The recorder samples at ~16 Hz (every ~3rd fixed step) while the check runs every step.
Result: the last row is 1–3 fixed steps before `# stop`, and **the first sample that could carry a
nonzero `dmgFrac` is never written.**

Corollary: `dmgFrac` can only be nonzero if damage occurs while `_frameSet` is false — i.e. between
cards — and that case is already covered by `sc_detachedRatioAtStart`. The evidence agrees exactly:

- **641,555 rows** with a non-NULL `dmgFrac` (R33 46,259 / R35 280,639 / R36 64,583 / R37 250,074).
- **0 nonzero. 0 `-1` sentinels.** Every row is exactly `0.0`.
- **8 damage aborts** in the same batches.
- R39's three aborted captures: 0.0 on all 692 rows; batch-wide 0 nonzero over all 320 captures.
- `sc_detachedRatioAtStart` = 0 on **all 8** aborted captures, including the ones whose predecessor
  replicate flew a full card.

**The only working damage signal in the corpus is the `# stop` / `[card] ABORT` line.** R37 backlog
`#54d` — index the abort's detached ratio into a column — is not a nice-to-have; it is the only way to
make damage queryable at all. Consider also deleting `dmgFrac`: a column that is provably always 0 is
worse than no column, because four separate analyses in this corpus have used it to "exclude damage".

---

## 4. One event or four

**One, in every case. The ratio is subtree size.**

`UnitPart.CreateAttachInfo` (`:84151`) subscribes each child's `UnitPart_OnParentDetached` to its
**parent's** `onParentDetached` event. `UnitPart.Detach` (`:84358`) fires `onParentDetached`, whose
handler (`:84200`) sets `detachedFromUnit = true` and re-fires `onParentDetached` — recursively, all the
way down. `PartDamageTracker` counts `IsDetached()`, which returns `detachedFromUnit` (`:84113`).
Meanwhile `PartDamageTracker` subscribes only to `onPartDetached`, which fires **only** on the directly
detached part.

So one `CheckAttachment` failure produces: one `needsCheck`, one immediate recount, and a count of
**1 + descendants**.

| observed ratio | parts | reading |
|---|---|---|
| 0.026 = 1/38 | EW1 | leaf part |
| 0.029 = 1/35 | Darkreach ×4 | leaf part |
| 0.057 = 2/35 | Darkreach ×1 | part with 1 child |
| 0.114 = 4/35 | Darkreach ×3 | **part with 3 descendants** |

The timing seals it independently: `PartChecker` tests one part per fixed step, and the recount happens
within one fixed step of the first detach, so **four independent latches inside that window is
arithmetically impossible**. R37 §5's "both the row column and the sidecar snapshot read a partially
swept accumulator" is the wrong mechanism — there is no partial sweep; there is a cascade.

Practical consequence: **0.114 and 0.029 are the same failure**, hitting a part at a different depth in
the tree. There is exactly one Darkreach part whose subtree is 4 and it has now shed in R35, R37 and
R39. Naming that part is a one-line reflection dump the harness does not currently do (backlog).

---

## 5. The corpus history — it is the placement COUNT

The observable window starts at **v0.96.0**: the damage abort and `dmgFrac` were added there, so R28
(48 Darkreach captures to replicate 8) and R32 (63 to replicate 16) are silent by construction, not
clean. Within the window, every Darkreach lane:

| run | ver | card | lane | lane dist | s/replicate | snapBack/rep | reps sched | abort at |
|---|---|---|---|---|---|---|---|---|
| R33 | 0.96.0 | `oblique-6-c` | d10 | n/a¹ | ~38 | 5.0–5.1 km | 8 | **5** |
| R35 | 0.96.2 | `oblique-6-dwell` | d10 | 61.5 km | ~126 | 25.1–25.3 km | 8 | **5** |
| R35 | 0.96.2 | `alpha-steps` | d24 | 49.5 km | ~32 | 7.0 km | 8 | **5** |
| R37 | 0.97.2 | `oblique-6-dwell` | d10 | 62.0 km | ~126 | 25.2–25.4 km | 8 | **5** |
| R39 | 0.98.1 | `e3-marker-ff` | d40 | 50.0 km | ~46 | 6.9–7.1 km | 8 | **5** |
| R39 | 0.98.1 | `alpha-sweep` | d48 | 50.2 km | ~41 | 5.1 km | 8 | **5** |
| R39 | 0.98.1 | `e2-rel-turn-lead` | d56 | 50.0 km | ~46 | 6.9 km | 8 | **4** |
| R39 | 0.98.1 | `oblique-6-dwell-t040` | d10 | 62.0 km | ~126 | 19.4–19.5 km | **4** | — |
| R39 | 0.98.1 | `oblique-6-dwell-t100` | d26 | 62.0 km | ~126 | 28.8–29.0 km | **4** | — |
| R39 | 0.98.1 | `stol-steps` | d66 | 62.1 km | — | — | **1** | — |

¹ `origDist` is a v0.96.2 column.

**7 lanes scheduled ≥5 replicates; 7 died. 6 at replicate 5, 1 at replicate 4. Zero at 1–3. Zero
survivors past 5.** Three lanes scheduled ≤4 completed.

Under a constant per-replicate hazard *p*, the geometric likelihood of six failures at exactly 5 and one
at 4 peaks at *p* ≈ 0.2 and is ≈ **3 × 10⁻⁸**. This is a threshold, not a Bernoulli process.

What the same table rules **out** as the accumulator:

| candidate accumulator | range across the 7 lanes | failure replicate |
|---|---|---|
| flight seconds before the failing replicate | **128 s → 504 s (3.9×)** | unchanged |
| snapback per placement | **5.0 km → 25.4 km (5.1×)** | unchanged |
| cumulative snapback | **13.8 km → 75.9 km (5.5×)** | unchanged |
| lane distance from origin | 49.5 → 62.0 km | unchanged |
| card | 6 distinct | unchanged |
| mod version | 0.96.0, 0.96.2, 0.97.2, 0.98.1 | unchanged |
| session / batch | 4 | unchanged |

The single quantity that is constant is **the number of placements**. R35 d10 and R35 d24 are the
decisive pair: same session, same airframe, same mod version, **126 s vs 32 s per replicate**, and both
died on replicate 5.

Non-Darkreach: **EW1, R35 d22, `alpha-steps`, replicate 8** — the only other damage abort in the corpus,
also on the *last* replicate its lane reached. So the threshold is per-airframe, not a universal 5, and
EW1 is n = 1.

**One loose thread, flagged not resolved.** R32 (v0.94.0, `darkreach-05`, no detector) ran Darkreach to
replicate 16 on two lanes. Its scored segments are flat through replicate 8 (median `gPeak` 0.28–0.46,
`aoaPeakDeg` 2.6–3.2) and then break abruptly: replicate 9 lifts, replicate 10 jumps to `gPeak` 2.0–3.1
and `aoaPeakDeg` 13–18°, reaching 28–38° by replicate 12–14, with three `altitude floor` aborts at
replicates 10, 10 and 11. That is the only other place in the corpus where Darkreach degrades on a
replicate index. Different version, different card, no detachment instrumentation — it neither
confirms nor refutes, and it should not be folded into the count above.

---

## 6. Why Darkreach — observed, not explained

7 of 8 damage aborts are Darkreach, which is 46 of 762 v0.96+ captures (6.0%). The rate is
**7/46 vs 1/716**. The effect is not in doubt; the attribution is.

Full probed parameter table, R39, ordered by mass. Aborting airframes in bold.

| airframe | parts | mass kg | gLim | αLim | αStr | fbwVc | maxPitchAngVel | maxRollAngVel | wingArea | dragArea | authUsedFrac |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Darkreach** | 35 | **105 409** | **4** | 10 | 0.05 | **100** | **0.3** | **3** | **383** | **6.34** | **0.441** |
| FastBomber1 | 35 | 57 622 | 8 | 15 | 0.20 | 200 | 0.5 | 6 | 149.9 | 5.22 | 0.199 |
| Multirole1 | 34 | 25 563 | 9 | 27 | 0.05 | 160 | 0.75 | 10 | 123 | 1.86 | 0.057 |
| **EW1** | **38** | 24 580 | 6 | 10 | 0.05 | 130 | **0.3** | **3** | 97.4 | 2.70 | 0.185 |
| CAS1 | 33 | 20 280 | 7.5 | 14 | 0.10 | 160 | 1 | 8 | 96 | 2.43 | 0.274 |
| SmallFighter1 | 34 | 13 689 | 9 | 25 | 0.08 | 155 | 0.7 | 5 | 55.7 | 1.66 | 0.076 |
| Fighter1 | 28 | 13 573 | 9 | 27 | 0.10 | 160 | 0.9 | 6 | 49.9 | 2.21 | 0.086 |
| VTOLTrainer1 | 28 | 11 178 | 8 | 15 | 0.08 | 160 | 1 | 8 | 41.4 | 1.72 | 0.106 |
| trainer | 36 | 9 806 | 8 | 10 | 0.05 | 130 | 1 | 8 | 57.4 | 1.08 | 0.202 |
| COIN | 33 | 4 854 | 6 | 10 | 0.10 | 110 | 1 | 4 | 48.8 | 1.61 | 0.274 |

Every single-parameter story dies on this table:

| story | refuted by |
|---|---|
| mass | FastBomber1, 2nd heaviest at 57.6 t, 68 captures, 0 sheds |
| part count | EW1 is 1st at 38 but Darkreach is 3rd at 35, behind trainer's 36 — trainer 96 captures, 0 sheds |
| `alphaLimiter` = 10 | trainer and COIN also 10, 132 captures, 0 sheds |
| `fbwCornerSpeed` low | COIN at 110 is next-lowest, 0 sheds |
| `gLimitPositive` low | COIN and EW1 at 6; COIN 0 sheds |
| wing / drag area | CAS1 at 96 m² ≈ EW1's 97.4 m², 0 sheds |
| lane distance | Fighter1 at 68 km and CAS1 at 98 km, 0 sheds; Darkreach shed at 50 km and survived 62 km |
| flight loads | Darkreach's aborts are at g 2.2–3.4 / AoA 3.1–5.4 while Multirole1 pulls 7.15 g and Fighter1 13.0° AoA, 0 sheds |
| `authorityUsedFrac` | CAS1 and COIN at 0.274 sit above EW1's 0.185, 0 sheds |

**One survives R39: `maxPitchAngularVel = 0.3` together with `maxRollAngularVel = 3`, held by exactly
Darkreach and EW1 — exactly the two airframes that have ever shed a part.** It has a plausible
mechanism attached: the FBW reads pitch/yaw as a commanded *rate*, so the lowest rate ceilings in the
fleet mean the law must hold the largest sustained deflection to get the demanded rate, which is what
the rows show (`outP` −0.53…−0.68 held for seconds at bank 70–77°, `authorityUsedFrac` 0.441). A
sustained hinge moment on the largest control surfaces in the fleet is the largest sustained
`FixedJoint` load in the batch, and `FixedJoint` solver residual *is* a position error — which is
exactly what `CheckAttachment` measures.

**State it as unfalsifiable and leave it there.** n = 2 airframes, and those two differ from the rest in
five other ways at the same time. **What would distinguish it:** the parameter pair predicts that any
airframe forced to hold near-full sustained deflection sheds, regardless of mass or size. Two ways to
test without touching code — (a) fly `oblique-6-dwell-t040` (throttle 0.40) on a **12-replicate**
Darkreach lane: at 0.40 throttle Darkreach cannot hold 70° bank, so if the deflection story is right the
lane runs past replicate 5; (b) fly a 12-replicate **EW1** lane — if EW1's threshold is also ~5 the
parameter pair is doing no work, and if it is reliably 8 the threshold is airframe-scaled and worth
regressing against.

**Correcting the graveyard on one point.** "An `AeroPart` has exactly one way to detach:
`CheckAttachment`, a purely geometric test… **so 'the airframe shed a part' is always a POSITION bug,
never a load one**" conflates the *test* with the *cause*. The test is geometric. Under complex physics
each part is its own `Rigidbody` on a `FixedJoint`, and joint stretch under load is a position — solver
residual, not a joint break. **Load produces position.** The damage route is closed; the load route is
not.

---

## 7. The frame-hitch check

**Refuted, and cleanly enough to be worth the negative result.**

- The three aborted captures' `frameMs` maxima are **16.8, 16.9, 16.7 ms** against a 17 ms fixed step.
  Not one excursion in 692 rows.
- Batch maxima: 152.2 ms (Fighter1 d33 rec 137, at `t` = 1254.00 — **171.7 s before** abort #1) and
  126.7 ms (seven concurrent lanes, recs 275–284, at `t` = 2144.38 — **16.2 s after** abort #3, and
  those seven lanes shed nothing).
- The session's 87 ms alt-tab hitch is logged at `LogOutput.log:8743`, **after** all three aborts
  (lines 5400, 6886, 8610).
- The three aborts fall in the log's **longest hitch-free stretch**: the previous `[drone] frame hitch`
  warning is at line 4384 and the next at line 8743.

A long frame remains a plausible mechanism for a geometric excursion in general. It is not the
mechanism here.

---

## 8. My theory, what would refute it, and the result of testing that

**Theory.** The hazard is indexed on placement count, not on elapsed time, distance moved, or flight
load; and it is per-airframe, with Darkreach's threshold at 4–5 placements.

**Refutations named in advance:**

| refutation | result |
|---|---|
| R1. A Darkreach lane reaches replicate ≥6 clean under v0.96+ | **none exists** — 7 lanes, all dead by 5 |
| R2. A Darkreach lane aborts at replicate ≤3 | **none exists** — 0 of 7 |
| R3. Changing per-replicate flight time moves the failure replicate | **tested and passed** — R35 d10 (126 s/rep) vs R35 d24 (32 s/rep), same session, same version, both failed at 5 |
| R4. Changing snapback distance moves the failure replicate | **tested and passed** — 5.0 km (R33) vs 25.4 km (R37), both failed at 5 |
| R5. The failure tracks lane distance from origin | **tested and passed** — 49.5 km and 62.0 km lanes both failed at 5; the batch's 68–98 km lanes never fail |
| R6. A frame hitch precedes the abort | **tested and passed** — §7 |
| R7. The abort correlates with peak load | **tested and passed** — the aborts are the gentlest flights in the batch |

**Refutations I could not run, and that keep this from being a localisation:**

- **The placement count is perfectly collinear with the card-run count.** Every replicate is exactly one
  placement plus one flight. R3 rules out flight *duration*, but not "something discrete that happens
  once per card run". The placement is the only discrete event per run that touches physics — that is an
  argument from elimination, not a measurement.
- **Nothing in the recorder changes across replicates.** Rows 0…N of replicate 5 are ordinary and
  indistinguishable from replicate 4 (they are not byte-identical — R39 replicates diverge from row 0,
  unlike R33's — but nothing drifts monotonically). Whatever accumulates is invisible to every column
  the harness writes. That is a finding and a wall at the same time.
- **The mechanism has no measurement.** A ~0.1 m residual per placement would explain a 4–5 placement
  threshold against a 0.5 m limit, but the float-grain estimate for one placement is ~0.004–0.02 m —
  **5–25× short** — and the joint solver should *relax* a residual rather than bank it. So the
  arithmetic that would make the count causal does not currently close.

**Honest verdict: we still cannot localise this.** What R39 buys is that the *search space* has
collapsed. The failure is not intermittent — it is a threshold at a fixed placement count — and that is
a fact any candidate mechanism now has to reproduce.

---

## 9. What a third fix has to show BEFORE it is allowed

Two fixes are already in the graveyard; v0.96.1's could never fire and v0.97.0's killed 32 of 32
placements. The bar:

1. **A measurement of the accumulating quantity, taken across placements.** Not a theory — a number that
   is ~0 after placement 1 and near 0.5 m after placement 4, logged per placement. Nothing in the
   corpus measures this today. Note the constraint the graveyard already found: `PartChecker` walks the
   **private** `Aircraft.partsWithAero`, not `partLookup`, so a mod-side re-derivation is looking at the
   wrong set, and any probe must read `attachInfo.parentPart.xform.InverseTransformPoint(xform.position)`
   against `attachInfo.localPosition` — the exact expression `CheckAttachment` uses — on that list.
2. **Read-only first, and it must be read-only.** The ANTI-invariant in `check-architecture.py` (no
   `Transform` write of any kind inside `MoveAssembly`) stands. A diagnostic that only *reads* transforms
   cannot repeat R36.
3. **It must explain the 4–5 placement threshold, not just the shed.** Any mechanism that predicts a
   per-run hazard is refuted by §5's 6-of-7-at-exactly-5.
4. **It must explain why replicate 1–4 are clean at 25.4 km of snapback and replicate 5 fails at 5.0 km.**
   Distance-proportional mechanisms are already refuted.
5. **It must survive the flight-time null.** 32 s and 126 s per replicate give the same answer.
6. **Prefer changing the card, not the code.** The threshold is at 4–5 placements. Capping Darkreach
   lanes at `repeat: 4` costs nothing and recovers the lane — R39's d10 and d26 are the proof — while a
   code fix is the thing that has cost two batches so far. The measurement in (1) is what earns the
   right to attempt more.

**The one experiment that would localise it**, and it needs no code — only a card:

> A Darkreach lane running a **near-no-op card**: place, hold 3 s of straight and level, stop,
> `repeat: 12`. Flight exposure per replicate drops ~15× against `e3-marker-ff` while placement count
> per minute rises ~15×. **If it dies on placement 4–5 the placement is causal and flight is
> irrelevant. If it runs 12 clean, the count is a proxy and flight exposure is required** — which would
> immediately re-target the search at what the aircraft does *between* placements. Cost: about one
> minute of flight, against 46 s × 5 for the current evidence. Pair it in the same launch with a
> 12-replicate `oblique-6-dwell-t040` Darkreach lane (§6b) and one 12-replicate EW1 lane and the batch
> answers placement-vs-flight, deflection-vs-airframe, and per-airframe threshold at once.

---

## 10. Ruled out

| candidate | evidence |
|---|---|
| a frame hitch caused the excursion | max `frameMs` 16.7–16.9 in the aborted captures; nearest batch hitch 16.2 s after / 171.7 s before; §7 |
| a floating-origin shift | `datumX/Y/Z` = `(0, −4032, 0)` on every row of every R39 capture |
| the placement is the proximate trigger (R39) | 23–26 full `PartChecker` sweeps between placement and abort; `arm` ran clean for 5.95 s |
| over-G / over-α | g 2.2–3.4 of a 4 g limit, AoA 3.1–5.4° of a 10° limiter; batch peaks are 7.15 g and 13.0° on airframes that never shed |
| `0.114` is four separate detachments | cascade via `onParentDetached`; four latches in one fixed step is impossible; §4 |
| `dmgFrac = 0` excludes damage | 641,555 rows, 0 nonzero, 8 damage aborts; structurally impossible; §3 |
| the R36 placement kill recurred | 0 `aircraft gone` in R39; all three aborts name a detached ratio |
| lane distance | Darkreach shed at 50 km, survived 62 km; Fighter1 at 68 km and CAS1 at 98 km never shed |
| a card property | 6 distinct cards produced the 7 Darkreach aborts |
| a mod-version property | 4 versions, spanning both sides of the `Repair` experiment |
| a swing-wing / variable-geometry part | FastBomber1 is the only airframe with `wingAngleMin/MaxDeg` (22–70°) and has never shed |
| cumulative flight time | 128 s vs 504 s, same failure replicate |
| cumulative or per-placement displacement | 13.8–75.9 km cumulative, 5.0–25.4 km per placement, same failure replicate |

## 11. What R39-F CANNOT prove

- **Any mechanism.** §8. The accumulating quantity is unmeasured and no column in the harness moves with it.
- **That the placement is causal.** Placement count and card-run count are collinear in every batch ever
  flown; §9's no-op card is what breaks the collinearity.
- **Anything about Darkreach specifically.** n = 1 airframe with 7 events, n = 1 with 1 event. §6.
- **That R33/R35-d10/R37's placement-window aborts and R39's mid-flight aborts are the same failure.**
  They share an airframe, a replicate index and a detached ratio; they do not share a location in the
  capture.
- **Anything about R28/R32.** No detector before v0.96.0. Their clean records are silence, not evidence.

## 12. Backlog

- **#55a — cap Darkreach lanes at `repeat: 4` until this is closed.** Card-only change. R39's d10 and
  d26 flew 4 and completed; every lane scheduled 5+ has died and taken its remaining replicates with it.
  Darkreach is already the thinnest lane in the corpus and this is the whole reason.
- **#55b — fly the no-op placement-repeat card (§9).** Highest-value minute of flight available.
- **#55c — index the abort's detached ratio as a capture column** (R37 `#54d`, still open). It is the only
  reliable damage signal and it currently lives in a string.
- **#55d — consider deleting `dmgFrac`.** §3 proves it can never be nonzero. Four analyses have already
  used it to "exclude damage".
- **#55e — name the part.** One reflection dump of `partsWithAero` with each part's `attachInfo.parentPart`
  would turn `0.114` into a part name and its 3 descendants, and 0.029 into a leaf. Read-only.
- **#55f — correct the `MoveAssembly` graveyard comment on two points**, both load-bearing for the next
  agent: (i) "always a POSITION bug, never a load one" — load produces position through `FixedJoint`
  solver residual (§6); (ii) the sweep-latency reading of `0.114` — the tracker recounts within one
  fixed step, so the ratio is subtree size, not a partially swept accumulator (§4). Do this in the same
  change as any code touch, per the standing rule.
- **#55g — R32's replicate-9/10 Darkreach break is unexplained** (§5). Different version and card, no
  detector, but it is the only other replicate-indexed Darkreach degradation in the corpus.
