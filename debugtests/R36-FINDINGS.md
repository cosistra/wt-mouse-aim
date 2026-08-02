# R36 — half a batch: the lane ladder is clean but untested, the new metrics are not, v0.97.1

**First flight of v0.97.1.** Two F2 presses, 16 lanes each, card `oblique-6-dwell`, 10 fixed-wing
airframes wrapping a 10-key list. **Replicate 1 of every lane flew normally (32 captures,
`dur=126.0s samples=2017/2018`). Every replicate 2 died in one fixed step (32 captures,
`dur=0.0s samples=1`).** The kill is under investigation elsewhere and is not diagnosed here;
§5 reports only what the 32 dead captures prove on their own, because two of those facts narrow it
sharply.

| | R33 | R35 | **R36** |
|---|---|---|---|
| mod | v0.96.0 | v0.96.2 | **v0.97.1** |
| card | `oblique-6-c` | `oblique-6-dwell` + `alpha-steps` | `oblique-6-dwell` only |
| lanes | 10 | 16 (one launch) | **16 x 2 launches** |
| captures | 77 | 186 | **64** (32 usable) |
| replicates per lane | 8 | 8 | **1** (`repeat: 8` requested) |
| aborted | 1 (detach) | 3 (detach) | **32 (`aircraft gone`)** |
| `n_cols` | 65 | 66 | **69** (`datumX/Y/Z` added) |

Everything below is reproducible from `debugtests/captures.db` after
`python debugtests/index-captures.py "<game>/BepInEx" debugtests/archive --rebuild`, plus
`debugtests/archive/R36-20260801/`. **Every claim states its n.** Half a batch is half a batch.

---

## Verdict

1. **The `_laneBase` fix PASSES the first-differences test, and the pass is VACUOUS — twice over.**
   Both runs lay down a perfect ladder: `origDist` 8.084 -> 98.067 km, all fifteen adjacent steps
   +5.992..+6.000 km, **zero sign changes**, `posX/posZ` exactly 6000.0 +- 0.1 m apart. But (a) **R35
   passes the identical spawn-row test** (7.709 -> 97.689 km, steps 5.990..6.000, zero sign changes)
   and R35 was the broken batch — the defect is in the *frame*, not the number, so the discriminating
   form is per-lane **median** `origDist` in one common late frame, where R35 shows its +36.62 km step
   at the 6->7 boundary and R36 shows +5.05; and (b) `datumX/Y/Z` reads **`(0.0, -4032.0, -1024.0)` on
   all 64,583 rows of all 64 captures in both runs**, so the origin never shifted and the broken code
   would have produced the same layout. `debugtests/test-lane-frame.py` says so itself — *"parked
   camera: fixed and broken layouts identical (the change is a no-op)"*. R36 is a **no-regression**
   result, not a confirmation, and the datum column is the only thing here that could have told the
   difference. §1.
2. **`gJitterG` is now strongly increasing in lane index — Spearman +0.859 (run 1) and +0.894
   (run 2), 16 lanes each — and the R33/R35 inverted ordering is GONE.** It is not *strictly*
   monotone (18 and 15 rank inversions out of 120 pairs); the residual is airframe, and it reproduces
   across the two runs. The distance law survives without the airframe confound: the six airframes
   that fly twice per launch, 60 km apart, go **12 of 12 pairs up, median 3.83x** (p = 0.0005, sign
   test). Log-log slope vs `origDist` **0.893 / 0.864**, reproducing R35's 0.885 and the `d.1.2e-7`
   float-grain prediction on a single clean frame. §2.
3. **`fixedWindowOffDeg` is the metric `terminalOffDeg` was pretending to be.** Over 24
   (airframe, tag) cells whose four members span a 4x change in `gJitterG` and 60 km of distance,
   median CV is **8.5%** for `fixedWindowOffDeg` and **2.2%** for `rmsPointingErrorDeg`, against
   **82.0%** for `terminalOffDeg` — which is the same order as `gJitterG`'s own 66.8%, i.e. terminal
   error is measuring the jitter. Pooled over 32 lane-cells, `fixedWindowOffDeg` correlates with
   `origDist` at Spearman **-0.080** (none) while `gJitterG` does at **+0.886**. §3.
4. **The 10-airframe ranking reproduces R35 -> R36 at Spearman +0.976 / Pearson +0.995 on
   `fixedWindowOffDeg`** (ratios 0.94-1.12), across two mod versions, two lane frames and two
   placement regimes — against +0.855 for `rmsPointingErrorDeg` and +0.758 for `terminalOffDeg`. §3.
5. **`settleTime95` censoring is confirmed and is a distance effect, not a batch property.** Settle
   rate 95% on lanes 1-5 (8-32 km), 35% on lanes 6-10, **8% on lanes 11-16** (68-98 km); Spearman
   (settle rate, `origDist`) = **-0.734**, (settle rate, `gJitterG`) = **-0.688**. Batch rate 56/128 =
   **43.8%**. Nothing settled before **11.68 s**; median 20.65 s. Rank on the *rate*, never the mean. §3.
6. **The kill is airframe-independent and snapback-gated, with perfect separation.** 10 of 10
   airframes, 32 of 32 lanes. Every placement with `snapBackM = 0.0` survived (32/32); every
   placement with `snapBackM` 13.9-41.2 km died (32/32). `dmgFrac = 0.0` and `g = 0.00` on the killing
   row — this is **not** the v0.96 detach abort that fired in R33/R35; the pilot is G-killed by a
   velocity explosion of 10,602-172,586 m/s. Its magnitude is **not** reproducible between the two
   runs while every placement input is, so it is solver-state-dependent, not an arithmetic error. §5.
7. **Replicate 1 is NOT placement-free.** Every one of the 32 carries an `# entry` line
   (`snapBackM=0.0 v=152.0->152.0 alt=4000.0->4000.0 ctrlReset=1`), so `PlaceOnCondition` ran on it —
   as a **zero-displacement** placement. Correct the briefing's premise before reasoning from it; it
   is also what makes §5's separation a statement about displacement rather than about the code path. §5.
8. **`LogOutput-R36.log` IS LOST.** The R37 session (v0.97.2, 22:19) overwrote `LogOutput.log` before
   it could be renamed. §6 preserves the launch and abort lines that had already been read out.
9. **Two instrument gaps found.** The `AT THE RESOLUTION FLOOR` warning never reaches
   `captures.db` (`index-captures.py:390` omits `sc.floor_warning`), and R36 contains **no within-lane
   replicate at all**, so it cannot produce a replicate noise floor — only a run-to-run one. §4, §7.

---

## 1. The `_laneBase` verdict: pass, on a test that was never stressed

The test the briefing specified, run per launch on each lane's replicate-1 spawn row (the only rows
in R36 that are all in one common frame, which they are — see the datum below):

```
F2 run 1 (drones 1-16)                            F2 run 2 (drones 17-32)
lane  airframe        origDist_km  step(km)       identical to 0.1 m, lane for lane:
   1  Fighter1            8.084        -            8.084
   2  Multirole1         14.076     +5.992         14.076
   3  SmallFighter1      20.073     +5.997         20.073
   4  trainer            26.071     +5.998         26.071
   5  VTOLTrainer1       32.070     +5.999         32.070
   6  CAS1               38.069     +5.999         38.069
   7  COIN               44.069     +6.000         44.069      <- R35's rift was HERE (7.4 -> 44.0)
   8  EW1                50.069     +6.000         50.069
   9  FastBomber1        56.068     +6.000         56.068
  10  Darkreach          62.068     +6.000         62.068
  11  Fighter1           68.068     +6.000         68.068
  12  Multirole1         74.067     +6.000         74.067
  13  SmallFighter1      80.067     +6.000         80.067
  14  trainer            86.067     +6.000         86.067
  15  VTOLTrainer1       92.067     +6.000         92.067
  16  CAS1               98.067     +6.000         98.067
|step| range 5.992 .. 6.000 km      sign changes: 0        (pass condition: 6.0 km, <= 1 sign change)
```

Independent cross-check, the one `origDist` cannot fake: consecutive lanes' `posX/posY/posZ` (which
are datum-relative by construction) at their spawn rows are **6000.0 m** apart, worst case 6000.1,
in both runs.

**The two runs are the same frame, not two frames.** The briefing warned they might sit on different
datums. They do not: `datumX/Y/Z` = `(0.0, -4032.0, -1024.0)` on **every row of every one of the 64
captures**, and run 2's spawn coordinates are byte-identical to run 1's. There is nothing to reconcile.

### 1a. The spawn-row form of the test is NOT discriminating — R35 passes it too

Run the identical procedure on R35's archived replicate-1 spawn rows, the batch that was split by a
32 km rift:

```
R35 oblique-6-dwell, each lane at its OWN replicate-1 spawn row:
 lane 1..16   7.709 13.700 19.698 25.695 31.694 37.692 43.691 49.690
             55.690 61.689 67.689 73.689 79.689 85.689 91.689 97.689  km
 |step| range 5.990 .. 6.000 km      sign changes: 0        <- PASSES. And R35 was broken.
```

That is the `7.709 + 6.000k` trap, and it catches the first-differences form too, because the
defect is in the **frame**, not the number: each lane's spawn row is taken in *its own* datum at
*its own* instant, so the sixteen values are sixteen incomparable measurements that happen to line
up. The form that does discriminate takes every lane in one **common late** frame — per-lane median
`origDist` over the whole batch, which is how the briefing's R35 figures were derived:

| | R35 (broken, camera moved) | R36 run 1 (fixed, camera parked) |
|---|---|---|
| per-lane median `origDist` | 24.03 18.52 12.84 6.15 0.58 7.42 **44.04** 49.77 55.80 61.98 67.84 73.97 79.95 86.14 92.12 98.52 | 18.39 23.18 26.55 29.30 35.63 39.80 44.85 52.08 58.49 … |
| step at the 6->7 boundary | **+36.62 km** | +5.05 km |
| \|step\| range | 5.51 .. **36.62** km | 2.75 .. 7.23 km |
| sign changes | 1 (the legitimate V through lane 5) | 0 |

R36 shows no rift. But note this form is *itself* contaminated — R36's median steps run 2.75-7.23 km
rather than a clean 6.0, because each lane walks a different downrange distance during the card
(COIN at 105 m/s versus Multirole1 at 380). It resolves a 36 km rift, not a 1 km one.

**So the only decisive instrument here is the `datumX/Y/Z` column itself**, and that is the point of
v0.97 adding it: a constant datum across all 32 spawns is what licenses the spawn-row ladder as a
common-frame measurement in the first place. R35 is `n_cols` 66 — **it has no datum column at all**,
so in R35 that validity precondition could not be checked even in principle.

**And a constant datum is also exactly why the pass proves nothing about the fix.** The failure mode
requires an
`OriginShift` *during* the launch stagger. R35's had one — its own spawn log shows the datum's local
y walking `-32, -32, 288, 288, 288, 288, -32, -32, -32, 32, 32, 32, 32, 96, 96, 96` across the
sixteen lanes. R36's reads `-32` for all thirty-two. With a parked camera, a `Vector3` `_laneBase`
and a `GlobalPosition` one are the same number, and `debugtests/test-lane-frame.py` asserts precisely
that (`parked camera: fixed and broken layouts identical (the change is a no-op)`).

So: **v0.97.1 did not break the lane layout, and R36 cannot say more than that.** The discriminating
experiment is unchanged and still owed — fly the same card while deliberately moving the camera past
the 1024 m threshold mid-stagger, and check that the ladder holds. Until then the operator parking
the camera is doing the work, not the code.

---

## 2. `gJitterG` vs lane index: strongly increasing, not strictly monotone, and the airframe
   confound is now separable

```
lane        1     2     3     4     5     6     7     8     9    10    11    12    13    14    15    16
run 1   0.071 0.108 0.079 0.111 0.184 0.335 0.241 0.172 0.325 0.288 0.276 0.681 0.285 0.428 0.640 0.645
run 2   0.061 0.118 0.104 0.123 0.179 0.269 0.263 0.233 0.308 0.249 0.252 0.568 0.280 0.467 0.730 0.540
```

| | run 1 (16 lanes) | run 2 (16 lanes) |
|---|---|---|
| Spearman(`gJitterG`, lane index) | **+0.859** | **+0.894** |
| Pearson(log `gJitterG`, log `origDist`) | +0.896 | +0.927 |
| log-log slope | **0.893** | **0.864** |
| rank inversions (of 120 pairs) | 18 | 15 |

**Answer: increasing, reproducibly, but not monotone.** The inversions are not noise — they are the
same inversions in both runs (CAS1 high for its distance at lanes 6/16, SmallFighter1 low at 3/13),
i.e. an airframe term riding on the distance term. R33/R35's *inverted* ordering, where the far lanes
were the quiet ones, does not appear anywhere in R36. That inversion was the rift, as R35 concluded;
with the rift gone the ordering is the geometric one.

**The airframe confound is separable for the first time.** Sixteen lanes over a 10-key list means the
first six airframes fly **twice per launch, 60 km apart, on the same datum, in the same run** — the
matched contrast no previous batch had:

| run | airframe | 8-38 km | 68-98 km | ratio |
|---|---|---|---|---|
| 1 | Fighter1 | 0.0712 | 0.2762 | 3.88x |
| 1 | Multirole1 | 0.1078 | 0.6808 | 6.31x |
| 1 | SmallFighter1 | 0.0787 | 0.2848 | 3.62x |
| 1 | trainer | 0.1105 | 0.4282 | 3.87x |
| 1 | VTOLTrainer1 | 0.1838 | 0.6400 | 3.48x |
| 1 | CAS1 | 0.3352 | 0.6447 | 1.92x |
| 2 | Fighter1 | 0.0608 | 0.2515 | 4.14x |
| 2 | Multirole1 | 0.1178 | 0.5683 | 4.82x |
| 2 | SmallFighter1 | 0.1044 | 0.2796 | 2.68x |
| 2 | trainer | 0.1232 | 0.4668 | 3.79x |
| 2 | VTOLTrainer1 | 0.1792 | 0.7304 | 4.08x |
| 2 | CAS1 | 0.2689 | 0.5399 | 2.01x |

**12 of 12 increase**, median 3.83x, range 1.92-6.31x; sign test p = 0.00049. Distance drives the
jitter and the airframe modulates it. Underpowered as an *airframe* ranking (n = 2 per airframe per
run) — do not read the per-airframe ratios as an ordering.

---

## 3. What the new metrics say about the 32 clean legs

**Batch totals** (128 legs = 32 captures x 4 x 30 s `oblique_step`; **0 railed, 0 slack, 0 unknown
tags**, every leg 29.93-29.96 s):

| metric | R36 (128 legs) | R35 `oblique-6-dwell` (496 legs) | legitimate? |
|---|---|---|---|
| `fixedWindowOffDeg` | 0.269 (125 of 128 non-NULL) | 0.2467 (485 of 496) | **yes** — same card, same 30 s legs |
| `rmsPointingErrorDeg` | 0.954 | — | |
| settle rate (`settleTime95` non-NULL) | **43.8%** (56/128) | 48.8% (242/496) | **no** — different lane distribution, see below |
| `settleTime95` (settlers only) | min 11.68, med 20.65, mean 21.46, max 28.98; **0 under 9.0 s** | med 18.9 | partially |
| `offFloorPct` | 31.6% | 33.1% | yes |
| `terminalOffDeg` | 0.0486; **87 of 128 (68%) below the 0.0396 floor** | 0.0309; 372 of 496 (75%) | yes |
| `gJitterG` | 0.300 | 0.254 | **no** — R36's lanes are uniformly spread 8-98 km |

`fixedWindowOffDeg` is NULL on exactly **3** legs, all EW1, all with the reason
`under the 'off' column's resolution floor (0.0396 deg)` — the metric declining to publish a number
it cannot resolve, which is the designed behaviour. No leg was too short (every leg is 30 s).

**`AT THE RESOLUTION FLOOR` fires on 87 of 128 legs (68%)** by its own criterion
(`terminalOffDeg < OFF_FLOOR_DEG`), confirmed by running `scorecard.py` directly — all four legs of
`d1-Fighter1-01` warn, at `terminalOffDeg` 0.0094/0.0082/0.0047/0.0188. **It does not reach the
database**: `segments.warnings` is NULL on all 128 (and on all 615 R35 and 304 R33 legs) because
`index-captures.py:390` rebuilds the per-segment warning list from `sc._tag_warning` and
`sc.rail_warning` only. §7.

### 3a. The metric that survives the jitter, and the one that does not

The four cells of each (airframe, tag) — two lanes 60 km apart, two runs — are *not* replicates: they
differ by a 2-4x change in `gJitterG`. A metric whose CV over them is small is a metric that is not
measuring the jitter. Median over the 24 cells of the six twice-flown airframes (n = 4 each):

| metric | median CV over the 4 cells |
|---|---|
| `rmsPointingErrorDeg` | **2.2%** |
| `fixedWindowOffDeg` | **8.5%** |
| `gJitterG` | 66.8% |
| `terminalOffDeg` | **82.0%** |

`terminalOffDeg`'s CV sits on top of `gJitterG`'s. That is the R35 conclusion, re-derived on a data
set whose distance axis is clean and single-framed.

Pooled over all 32 lane-cells:

| | Spearman vs `origDist` |
|---|---|
| `gJitterG` | **+0.886** |
| `settle rate` | **-0.734** |
| `offFloorPct` | -0.263 |
| `rmsPointingErrorDeg` | +0.114 |
| `fixedWindowOffDeg` | **-0.080** |

### 3b. The airframe ranking reproduces across batches

R35 `oblique-6-dwell` vs R36, per airframe (R35 n = 32-16 legs, R36 n = 16 or 8):

| airframe | R35 `fw` | R36 `fw` | ratio | R35 `term` | R36 `term` |
|---|---|---|---|---|---|
| trainer | 0.0893 | 0.0958 | 1.07 | 0.0176 | 0.0273 |
| COIN | 0.0825 | 0.0891 | 1.08 | 0.0333 | 0.0307 |
| EW1 | 0.1894 | 0.1788 | 0.94 | 0.0170 | 0.0138 |
| CAS1 | 0.1770 | 0.1927 | 1.09 | 0.0202 | 0.0336 |
| VTOLTrainer1 | 0.2188 | 0.2091 | 0.96 | 0.0225 | 0.0182 |
| Fighter1 | 0.2142 | 0.2158 | 1.01 | 0.0179 | 0.0191 |
| FastBomber1 | 0.2952 | 0.3274 | 1.11 | 0.1447 | 0.3404 |
| Multirole1 | 0.3414 | 0.3757 | 1.10 | 0.0158 | 0.0218 |
| SmallFighter1 | 0.4471 | 0.4566 | 1.02 | 0.0324 | 0.0342 |
| Darkreach | 0.5221 | 0.5829 | 1.12 | 0.0623 | 0.0846 |

**Spearman R35 vs R36: `fixedWindowOffDeg` +0.976 (Pearson +0.995), `rmsPointingErrorDeg` +0.855,
`terminalOffDeg` +0.758, `gJitterG` +0.745, settle rate +0.673.** n = 10 airframes.

Two honest limits on that table. It is a **reproducibility** result, not an accuracy one — the same
card on the same airframes at the same entry condition *should* reproduce, and what is being tested
is the metric's ability to say so through a changed lane frame. And R35 pools eight replicates with
30 km snapback resets while R36 has one spawn-only replicate, so the +0.976 additionally says the
placement regime does not move `fixedWindowOffDeg` much — worth knowing, but it is an inference from
two batches, not a controlled comparison.

### 3c. The settle censoring, by distance

| lane group | `origDist` | legs | settled | rate | mean `settleTime95` | `gJitterG` |
|---|---|---|---|---|---|---|
| 1-5 | 8-32 km | 40 | 38 | **95%** | 19.4 s | 0.114 |
| 6-10 | 38-62 km | 40 | 14 | 35% | 25.1 s | 0.268 |
| 11-16 | 68-98 km | 48 | 4 | **8%** | 28.8 s | 0.483 |

R35's split was 100% on lanes 1-6 and 0-63% on lanes 7-16, but there the two groups were separated by
the rift rather than by distance alone. R36 reproduces the gradient **monotonically across a
continuous distance ladder in one frame**, which the R35 data could not do. It also shows the
survivors' mean rising with distance (19.4 -> 28.8 s) at the same time as the rate collapses, so the
mean is biased in the *opposite* direction to the censoring — averaging `settleTime95` across lane
groups is worse than useless, it inverts.

**Do not compare R36's 43.8% batch settle rate against R35's 48.8%.** R35's oblique lanes were 6 near
and 10 far; R36's are uniformly 8-98 km. The rate is a function of the lane distribution, and the two
distributions differ.

---

## 4. The noise floor R36 can and cannot give

**R36 has no within-lane replicate.** Every lane flew exactly one scorable capture, so nothing in
this batch measures replicate-to-replicate spread, and no MDE can be computed from it. The
`repeat: 8` the card asked for would have given eight; the batch delivered **64 of 256 planned
captures, 32 of them usable — 12.5%**.

What it *does* give is a run-to-run pair: 16 lanes x 4 tags flown twice, from two separate F2 presses,
at the same distance on the same datum, **with no placement anywhere in either member**. That is the
cleanest reproducibility pair in the corpus.

| metric | pairs | median \|rel diff\| | 90th pct | max |
|---|---|---|---|---|
| `rmsPointingErrorDeg` | 64 | **2.3%** | 7.6% | 41.4% |
| `fixedWindowOffDeg` | 62 | **6.0%** | 29.5% | 102.4% |
| `gJitterG` | 64 | 13.5% | 29.7% | 39.2% |
| `offFloorPct` | 64 | 13.8% | 39.3% | 200.0% |
| `settleTime95` | 21 | 14.6% | 42.1% | 72.2% |
| `terminalOffDeg` | 64 | **72.9%** | 138.5% | 200.0% |

Reading it as a CV: for a normal pair, median\|x-y\|/mean ~= 1.35 sigma/mu, so roughly **1.7% CV for
`rmsPointingErrorDeg`, 4.4% for `fixedWindowOffDeg`, 54% for `terminalOffDeg`**. `settleTime95`'s row
is over 21 pairs only — both members must have settled — and is therefore near-lane biased.

---

## 5. What the 32 dead replicates prove on their own

Not the mechanism; that is elsewhere. These five facts are artifact-only and constrain it.

**(a) Airframe-independent, and total.** 10 of 10 airframes, 32 of 32 lanes, both runs, all at
`segment arm at 0.0s`, all `ABORT (aircraft gone)` -> `[drone] #N despawned (pilot killed)`. Not
intermittent: R33's and R35's detach aborts were caught by `PartChecker`'s one-part-per-step
round-robin at ~k/N probability; this is 100%.

**(b) It is NOT the v0.96 damage abort, and nothing had detached.** `dmgFrac = 0.0` and `g = 0.00` on
the killing row of all 32, and the stop reason is `aircraft gone`, not `airframe damage (detached
ratio ...)`. What the row does carry is a speed of **10,602 to 172,586 m/s** (mean 72,524) against an
entry condition of 95-190. The pilot is G-killed by that; the mod's damage abort never had anything
to see. Contrast the reasons across batches — v0.97 did not make the old failure more frequent, it
introduced a different one:

| batch | mod | captures | placements with snapback > 1 km | aborts | reason |
|---|---|---|---|---|---|
| R33 | 0.96.0 | 77 | 67 (mean 7.0, max 9.0 km) | 1 | `detached ratio 0.029` |
| R35 | 0.96.2 | 186 | 162 (mean 22.6, max 41.1 km) | 3 | `detached ratio 0.026-0.114` |
| **R36** | **0.97.1** | **64** | **32 (mean 30.3, max 41.2 km)** | **32** | **`aircraft gone`** |

**(c) Perfect separation on snapback distance — and it is displacement, not distance to the origin.**

| | captures | aborted | `snapBackM` |
|---|---|---|---|
| replicate 1 | 32 | **0** | 0.0 |
| replicate 2 | 32 | **32** | 13,866 - 41,160 |

Within the dead 32, though, **nothing grades**: the explosion magnitude correlates with `snapBackM`
at r = **+0.102**, with the lane's `origDist` at r = **+0.052** (Spearman +0.069), and with the
placement's altitude change at r = +0.206. The velocity direction is not systematic either —
`velY/|v|` runs from **-0.978 to +0.957** with mean +0.019, which kills the obvious "the altitude
write was returned as `err/dt`" reading (it predicts a strongly +Y vector of ~50,000 m/s; the
observed vectors are near-isotropic). So the trigger is **whether** the placement displaced the
aircraft, not **how far** and not **in what direction**.

**(d) The inputs are deterministic; the outcome is not.** `snapBackM` for a given airframe agrees
between the two runs to five significant figures (Fighter1 34,871.4 / 34,862.5 / 34,872.1 / 34,864.6;
Multirole1 41,160.1 / 41,160.1), as do `v_from`, `alt_from` and every lane coordinate. The resulting
speed does not: lane 1 blew up at **25,114 m/s** in run 1 and **46,987 m/s** in run 2 from
byte-identical inputs. A deterministic transform with a non-deterministic consequence points at
solver state, not at the arithmetic.

**(e) Replicate 1 showed no precursor, and it was not placement-free.** All 32 clean captures:
`dmgFrac` max **0.0** on every one of 64,551 rows (no part loss, no damage anywhere in the batch);
all 32 stopped with `card 'oblique-6-dwell' complete`; zero `[place]` lines in the log, so the
`AeroPart.Repair` pass never threw; one 156 ms frame hitch in ~4,000 s of flight. `gJitterG` rises
mildly through the card in tag order (0.267 / 0.305 / 0.297 / 0.333 for obDR6/obDL6/obUL6/obUR6, n=32
each) identically in both runs — that is the card's own ordering, not a degradation.

**And the correction that matters most to the next investigator:** every replicate-1 capture carries
`# entry v=152.0->152.0 alt=4000.0->4000.0 snapBackM=0.0 fuel=1.000->1.000 ctrlReset=1`. The
placement **ran** on replicate 1 — that line is what writes it, and `ctrlReset=1` means
`ChaseController.Forget` fired too. Replicate 1 is a **zero-displacement placement**, not the absence
of one. So (c) is a statement about displacement magnitude crossing zero, not about a code path being
skipped.

---

## 6. Archive, and the log that was lost

```
debugtests/archive/R36-20260801/    129 files
   64 x mouseaim-rec-v0.97.1-R36-d<1..32>-<airframe>-<01..64>-oblique-6-dwell-*.csv
   64 x matching .airframe.json sidecars
    1 x mouseaim-anomalies-v0.97.1-R36-20260801-215434.log
    0 x LogOutput-R36.log                                    <-- LOST
```

**`LogOutput.log` was overwritten by the R37 session (v0.97.2) at 22:19-22:20**, before the rename in
the archive step could run. This is exactly the hazard `index-captures.py:archive()` documents in its
own docstring. The lines below had already been read out and are preserved here because nothing else
records them; the rest of that file is gone.

```
[drone] launching 16 x 'Fighter1,Multirole1,SmallFighter1,trainer,VTOLTrainer1,CAS1,COIN,EW1,
        FastBomber1,Darkreach' (by lane, wrapping) at 4000 m / 0.95x corner (per airframe),
        3s apart, lanes 8000 m + 6000 m abeam.
[drone] card 'oblique-6-dwell' (1 selected, 126s each, x8 from card 'oblique-6-dwell'):
        airframe [card], 4000 m [card], 0.95x corner (per airframe) [card], 16 drone(s) [card count].

  spawn local coordinates, IDENTICAL in both launches (run 1 = #1..#16, run 2 = #17..#32):
    #1  Fighter1       (  7906, -32,  1686)  152 m/s  1 crew      #9  FastBomber1   ( 55417, -32,  8517) 190 m/s 2 crew
    #2  Multirole1     ( 13845, -32,  2540)  152 m/s  1 crew     #10  Darkreach     ( 61356, -32,  9371)  95 m/s 2 crew
    #3  SmallFighter1  ( 19784, -32,  3394)  147 m/s  1 crew     #11  Fighter1      ( 67295, -32, 10225) 152 m/s 1 crew
    #4  trainer        ( 25723, -32,  4248)  124 m/s  2 crew     #12  Multirole1    ( 73234, -32, 11078) 152 m/s 1 crew
    #5  VTOLTrainer1   ( 31662, -32,  5101)  152 m/s  2 crew     #13  SmallFighter1 ( 79173, -32, 11932) 147 m/s 1 crew
    #6  CAS1           ( 37601, -32,  5955)  152 m/s  1 crew     #14  trainer       ( 85112, -32, 12786) 124 m/s 2 crew
    #7  COIN           ( 43540, -32,  6809)  105 m/s  2 crew     #15  VTOLTrainer1  ( 91051, -32, 13640) 152 m/s 2 crew
    #8  EW1            ( 49479, -32,  7663)  124 m/s  4 crew     #16  CAS1          ( 96990, -32, 14494) 152 m/s 1 crew

  local y = -32 on all 32 spawns  ->  the datum did not move during either stagger
  32 x "[card] ABORT (aircraft gone) - 'oblique-6-dwell' segment arm at 0.0s"
  32 x "[rec] done (abort: aircraft gone) dur=0.0s samples=1"
  32 x "[drone] #N despawned (pilot killed)"
   1 x "[drone] frame hitch: 156 ms" after the first launch line;  0 x "[place]"
```

**Reconciliation.** 16 lanes x 2 F2 runs = 32 drone lanes; 2 captures each = **64 CSVs + 64 sidecars**,
which is what is on disk and what `--check R36` indexes (`captures 64 ... aborted 32, rec 1..64,
contiguous`). 32 complete + 32 dead = 64. **The arithmetic closes.** Note `--check` flags COIN, EW1,
FastBomber1 and Darkreach at "4 vs median 8 (50%)" — that is **not** a dead lane, it is the 10-key
list wrapping into 16 lanes, so six airframes get two lanes per launch and four get one.

---

## 7. Tool gaps found

1. **`floor_warning` never reaches the database.** `scorecard.py:1326` appends it to the run-level
   warning list, but `index-captures.py:390` rebuilds the *per-segment* list from
   `sc._tag_warning` + `sc.rail_warning` only, so `segments.warnings` is NULL on every leg that trips
   it — 87 in R36, 396 in R35, 10 in R33 by the metric criterion. One-line fix: add `sc.floor_warning(seg)`
   to that tuple. Until then, `CAPTURES-DB.md`'s "scorecard emits an `AT THE RESOLUTION FLOOR` warning
   into `segments.warnings`" is true of the scorer and false of the index — **match on
   `terminalOffDeg < 0.0396`**, which is the documented rule anyway (gotcha 9).
2. **No `railed`-style flag for it.** `railed`/`slack`/`unknown_tag` each have a boolean column; the
   floor does not, which is why (1) is invisible. `sum(terminalOffDeg < 0.0396)` is the workaround.

---

## 8. Ruled out

| candidate | evidence |
|---|---|
| the two F2 runs sat on different datums | `datumX/Y/Z` = `(0, -4032, -1024)` on all 64 captures; run 2's spawn coords byte-identical to run 1's |
| an origin shift during either stagger | same, plus local y = -32 on all 32 spawn lines |
| the lane rift recurred | per-lane median `origDist` steps 2.75-7.23 km, 0 sign changes, no 36 km step (the spawn-row form alone would not have shown one either — §1a) |
| airframe damage anywhere in the clean half | `dmgFrac` max 0.0 on all 64,551 rows of all 32 captures |
| the kill being airframe-specific | 10 of 10 airframes, 32 of 32 lanes |
| the kill being the v0.96 detach abort | `dmgFrac = 0.0`, stop reason `aircraft gone`, not `airframe damage` |
| the kill scaling with snapback / distance / altitude delta | r = +0.102 / +0.052 / +0.206 over n=32 |
| the kill being an `err/dt` return of the altitude write | `velY/|v|` spans -0.978..+0.957, mean +0.019 |
| frame hitches | 1 hitch (156 ms) after launch across ~4,000 s of flight |
| the `Repair` pass throwing | 0 `[place]` lines |
| railed / slack segments biasing the metrics | 0 railed, 0 slack, 0 unknown tags in 128 legs |
| A/B arm | `oblique-6-dwell` declares no `armToggle`; `arm`/`arm_knob` NULL on all 64 |

---

## 9. Backlog

- **#53a — the `_laneBase` fix is still unconfirmed.** One card, 16 lanes, and the operator
  *deliberately* flying the camera past the 1024 m threshold mid-stagger (spectate lane 1, then lane
  16). Score it on **`datumX/Y/Z` first** — if the datum is constant the batch proves nothing, whatever
  the ladder says (§1a) — then on the ladder. Five minutes, and it is the only thing that converts §1
  from "no regression" to "fixed".
- **#53a2 — `test-lane-frame.py` should assert the spawn-row trap.** Its `broken:` case reproduces
  R35's *median* layout; it does not state that the spawn-row first-difference test passes on both
  layouts, which is the mistake an analyst will make next time. One assertion, and the docstring
  sentence "a lane-layout claim requires a constant `datumX/Y/Z` across the stagger".
- **#53b — `index-captures.py:390` should call `sc.floor_warning`.** One line. §7.
- **#53c — R36 owes its replicates.** Nothing in it measures replicate spread, so the R33 §9 noise
  table gains no rows from it. The run-to-run pair in §4 is the substitute and is a *different*
  quantity (two spawns vs two placements) — do not paste it into that table.
- **#53d — the corpus now has a within-airframe distance contrast.** §2's 12 matched pairs are the
  design R33 backlog #52a asked for, arrived at by accident (a 10-key list in 16 lanes). Making it
  deliberate — the same airframe in the near and far group of every roster card — costs nothing and
  removes the airframe confound from every future jitter statement.
- **#53e — the parked camera is doing measurement work and nothing records it.** R36's datum was
  constant because the operator did not move; R35's was not. `datumX/Y/Z` now makes that auditable
  after the fact, which is the point of the column, but the runbook should say "park it" out loud —
  R33 §9 step 3 already does, and R35 shows it being forgotten.
