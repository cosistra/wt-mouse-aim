# R30 — the down-step penalty is DIRECTION, not card position (order control), v0.94.0

**The order control R28 asked for** (`R28-FINDINGS.md` §3.2, ranked fix #1). Two cards of **identical
geometry and opposite traversal order**, flown interleaved in one session on three airframes.
3 airframes × 2 cards × 8 replicates = **48 captures**, 29 199 rows, one unattended run. Source:
`<game>/BepInEx/mouseaim-rec-v0.94.0-R30-d{1..3}-<airframe>-{01..48}-oblique-12-{fwd,rev}-*.csv`
(+ `.airframe.json` sidecars), `mouseaim-anomalies-v0.94.0-R30-20260730-203337.log`, and
`LogOutput-R30.log` (archived — see §0.3).

| | |
|---|---|
| airframes | `Fighter1` (FS-12) `Multirole1` (KR-67) `FastBomber1` (AB-4) — one lane each |
| cards | `oblique-12-fwd` (down legs first) `oblique-12-rev` (up legs first) |
| entry | 250 m/s / 4000 m, throttle pinned 0.70 — identical on both cards |
| A/B arm | **none ran** — see §0.4. All five levers ON (`mrFF=1 relLead=1 iStall=1 belowSup=1 alignLead=1`) |
| run order | strict **fwd/rev alternation** within every lane (see §0.2) |

Both cards emit the **same four measurement tags** (`obDR12` `obDL12` `obUL12` `obUR12`) with the same
step vectors; only the slot each occupies differs. Direction and position are therefore **crossed**,
not confounded, and every capture contributes all four tags — both factors are within-capture at the
tag level.

| card | slot 1 | slot 2 | slot 3 | slot 4 | slot 5 |
|---|---|---|---|---|---|
| `oblique-12-fwd` | `arm` (el +8.49) | `obDR12` **down** | `obDL12` **down** | `obUL12` up | `obUR12` up |
| `oblique-12-rev` | `arm` (el −8.49) | `obUL12` up | `obUR12` up | `obDR12` **down** | `obDL12` **down** |

Metric is R28's, unchanged: **`terminalOffDeg`** from `scorecard.py` — the mean of `off` over the last
`TERMINAL_WINDOW_S` (1.0 s) of a segment. Nothing here reimplements a tool metric; the aggregation
imports `scorecard.score_run` and groups its output.

---

## Verdict

**OUTCOME 1 — DIRECTION IS REAL.** The down/up ratio is **> 1 in both cards on all three airframes**,
and it does **not** invert when the up legs are moved to slots 2–3. R28's finding survives the order
control.

1. **Direction main effect (position removed): ×3.07 `Fighter1`, ×5.39 `Multirole1`, ×1.39
   `FastBomber1`.** Every 95% CI excludes 1. On the two light jets this is 60–240× the replicate noise
   floor.
2. **There IS a position effect, it is real, and it is roughly 4–7× smaller than the direction
   effect — and it points the WRONG WAY to explain R28.** Early/late = **0.98 / 0.71 / 0.79**: a leg
   run at slot 4–5 is 1.0–1.4× *worse* than the same leg at slot 2–3. In every shipped oblique card
   the down legs are the EARLY ones, so the position effect **suppresses** the measured down/up ratio.
   R28 if anything **understated** the direction effect.
3. **The interaction is negligible** — ×1.005 / ×1.048 / ×1.150 (pooled ×1.066). The two factors are
   additive on the log scale to within a few percent.
4. **R28's `oblique-12` numbers reproduce to within 3 %** on the forward card across a mod-version and
   session boundary (v0.92.1 → v0.94.0). This is an independent replication, not a re-read.
5. **One cell is the exception and it must be stated: `FastBomber1`'s `DR/UL` mirror pair has NO
   direction effect** (×1.024, CI spanning 1) and its entire apparent asymmetry is position. Its
   `DL/UR` pair carries the whole ×1.39. `FastBomber1` is the airframe R28 already flagged as failing
   the card, and its replicate noise here is 30–43×the light jets'.
6. **A mechanism candidate that R28 excluded on a statistic that hid it: `bSup` / `BelowAlignSuppress`.**
   The v0.85 below-nose suppression is **on for 41–98 % of a down leg and 0–14 % of an up leg**, by
   construction — and R28's "`bSup` reads ~0" was a **median**; the **mean** is 6× asymmetric. See §6.
7. **v0.94's concurrent per-aircraft A/B was NOT exercised by this batch.** No arm was swept at all.
   The requested verification cannot be performed from R30. See §0.4.

---

## §0 — did the instrument work?

### 0.1 Sound

| check | result |
|---|---|
| captures | **48** — exactly 3 × 2 × 8, matrix verified per (lane, airframe, card) |
| lanes | d1 `Fighter1`, d2 `Multirole1`, d3 `FastBomber1` — 8 fwd + 8 rev each |
| `# stop` present | 48/48 |
| `# stop` reason | 48/48 `card '<name>' complete`; **0 aborted**, 0 refused, 0 declined |
| samples | 608 (33 captures) or 609 (15) |
| segments | 5 per capture on all 48 |
| scored segments | **192** (`scorecard.py` excluded exactly 48 `arm` segments — by design) |
| segment duration | 7.90–7.98 s across all 192 (0.08 s = ~1 fixed step) — **no truncation** |
| card duration | 37.65–37.73 s summed over segments; `# stop dur` 38.00 on all 48 |
| unrecognised tags | **0** |
| RAILED warnings | **0 of 192** |
| any other warning | **0** |
| columns | **64** on all 48 headers; **64** on all 29 199 rows — lockstep intact |
| `ctrlReset=1` | **48/48** |
| `# entry` provenance | 48/48, all `v=…->250.0 alt=…->4000.0 fuel=…->1.000` |
| `# override` | absent on all 48 (neither card pins anything) |
| `# config` | **all 48 byte-identical** — no live edit landed mid-batch |
| anomaly log | present; `overshoot` / `over-roll` only, 340 entries |

Zero RAILED is the material one: **no cell is excluded from any ratio below, on any airframe.** Every
comparison in this document is made on the full n.

### 0.2 The two cards are temporally interleaved 1:1 — this is what protects the position contrast

Both cards were ticked, so the suite queued them alternately. Per lane the run indices are:

```
Fighter1   fwd  1  7 13 19 25 31 37 43      rev  4 10 16 22 28 34 40 46
```

i.e. strict **ABAB**, mean run index 22 (fwd) vs 25 (rev) — a separation of exactly **one lane-run
(~38 s)** in a 12-minute batch. The direction contrast is paired within a single capture and needs no
such protection; the **position** contrast is the across-card one, and this is its guard. Measured
session drift on the capture-level mean is **0.4–0.5 % (Fighter1)**, **1.7–3.0 % (Multirole1)**, with
no monotonic trend on `Fighter1`; on `Multirole1` the mild drift is *downward* (improving) while `rev`
runs *later*, so drift works **against** the position effect reported in §4 rather than producing it.

Note this is ABAB, not ABBA. A monotonic drift therefore leaves a residual of half a period rather
than cancelling exactly. At the drift magnitudes measured that residual is ~0.5 %, against a position
effect of 21–29 %. It is not the explanation, but a future order-control pair should use ABBA.

### 0.3 The log

`LogOutput-R30.log` is R30's (header `v0.94.0`, `run R30 id 20260730-203337`) and carries the launch:

```
[drone] launching 3 x 'Fighter1,Multirole1,FastBomber1' (by lane, wrapping) at 4000 m / 250 m/s,
        3s apart, lanes 8000 m + 6000 m abeam.
[drone] card 'oblique-12-fwd' (2 selected, 38s each, x8 from card 'oblique-12-fwd'):
        airframe 'Fighter1, Multirole1, FastBomber1' [card], 4000 m [card], 250 m/s [card],
        3 drone(s) [card 'oblique-12-fwd' airframe list (3 named)].
[drone] #1 'Fighter1'    spawned … 1 crew.
[drone] #2 'Multirole1'  spawned … 1 crew.
[drone] #3 'FastBomber1' spawned … 2 crew.
```

Every value came **from the card**; nothing fell back to F1. Zero refusals, zero pre-spawn gate
rejections, zero `p.dead`/`ejected` despawns, zero exceptions.

**Gap: the log is truncated mid-batch.** It ends inside capture 48's segment 4/5, so the despawn lines
are absent. Nothing in this document rests on them (all 48 CSVs carry a `complete` stop line), but the
R28 action item — *copy the log out at the END of the run, not during it* — is still open.

`FastBomber1` reports **2 crew**, confirming the v0.90.1 `Time.fixedTime` double-step guard is the
thing keeping its card clock honest: its segment durations (7.90–7.95 s) match the single-seat lanes'
exactly.

### 0.4 The A/B arm did not run — the requested v0.94 verification is not answerable from R30

**`arm=` and `armKnob=` are absent from all 48 `# config` lines**, and all 48 lines are byte-identical.
That is not a formatting quirk: `ScenarioPlayer.ArmTag` is `""` exactly when `_armEntry == null`, so
the absence is positive evidence that **no knob was swept**. Neither card declares an `armToggle` and
`Cfg.ScenarioArmToggle` was empty, so the runner had nothing to alternate.

Consequences, stated plainly:

- **This is a clean single-arm baseline**, which is the right design for an order control — an arm
  sweep would have added a nuisance factor to a 2×2 that is already fully crossed. Nothing below is
  compromised.
- **"Were the arms balanced within each cell?"** — vacuously yes; there is one arm.
- **"Were the three lanes on different arms at the same instant?"** — **unanswerable.** v0.94's
  concurrent per-aircraft sweep was not exercised. It remains verified only by
  `debugtests/test-arm-schedule.py` (which does assert two aircraft on opposite arms at once) and
  **not** in flight. The first fleet card that declares an `armToggle` is still the live proof.
- **"Does the swept knob move the down/up ratio?"** — not testable here. But §6 identifies
  `BelowAlignSuppress` as the knob most likely to, and it was **ON** for all 48 captures.

---

## §1 — `frameMs`

| | |
|---|---|
| rows | 29 199 |
| distinct values | **11** — 6.3, 11.9, 16.6, 16.7, 16.8, 16.9, 17.0, 17.1, 17.2, 17.9, 27.0 |
| mean / p50 / p99 / p99.9 | 16.700 / 16.70 / 16.70 / 16.80 |
| min / max | 6.30 / **27.00** |
| rows > 20 ms | **1** (0.003 %) |
| **rows at 33.3 ms (a dropped vsync frame)** | **0** |

99.736 % of rows read exactly 16.7 ms. **The agreed stop signal for adding more drones is not
present** — zero dropped frames in 12 minutes of three concurrent complex-physics aircraft, with a
worst case of 27.0 ms (still inside one vsync period + jitter, and 2.9 ms under the two-frame
boundary). The measurement remains vsync-censored and still cannot size headroom (R28 §Q0b); it can
only say the cap was never missed. Three lanes is well under the eight R28 flew, so this batch adds
no new information about the 8→12 decision beyond "3 is free".

The log's three `[drone] frame hitch` warnings (50, 60, 323 ms) are all at log lines 109/143/237,
**before** the launch at line 520 — they are scene-load hitches, not batch hitches. No row of any
capture is poisoned.

---

## §2 — the noise floor

Established R28's way: **replicate spread within a matched cell** — the sd of `terminalOffDeg` across
the 8 replicates of one (airframe, card, tag), of which there are 24.

| airframe | per-cell replicate CV (min–max) | median | capture-level CV (geomean of the 4 legs) |
|---|---|---:|---:|
| `Fighter1` | 0.13 – 1.72 % | **0.61 %** | 0.4 – 0.5 % |
| `Multirole1` | 1.07 – 5.43 % | **3.32 %** | 1.7 – 3.0 % |
| `FastBomber1` | 13.44 – 43.17 % | **29.94 %** | 16.6 – 27.1 % |

**`FastBomber1`'s variance is common-mode wander, not per-leg noise and not bimodality.** The
capture-level geometric mean over its four legs, in run order:

```
fwd:  0.928  1.129  1.172  1.761  1.537  1.295  0.943  0.811     (CV 27.1 %)
rev:  1.215  0.896  0.842  0.992  1.163  1.270  0.924  0.865     (CV 16.6 %)
Fighter1 fwd, for contrast:
      0.330  0.331  0.333  0.332  0.334  0.331  0.330  0.331     (CV  0.4 %)
```

A smooth rise-and-fall across replicates 3–6 that moves **all four legs of a capture together**. This
is not the R28 `Darkreach` pattern (seven clustered + one outlier); it is a slowly varying state the
capture does not record. Two consequences, and both are honoured below:

- The **paired within-capture direction contrast cancels it**, which is why `FastBomber1`'s direction
  CIs are usable at all despite a 30 % cell CV.
- The **unpaired across-card position contrast does not**, which is why `FastBomber1`'s position CIs
  are the widest in the batch and its DR/UL direction estimate is the one null in §3.

The right noise floor for a *ratio* is the sd of the paired log ratio itself, which is reported
alongside every ratio in §3.

---

## §3 — the 2×2, and the direction effect

### 3.1 Per-cell terminal error

`terminalOffDeg`, mean ± sd over 8 replicates, degrees.

| airframe | card | tag | slot | dir | pos | mean | sd | CV |
|---|---|---|---:|---|---|---:|---:|---:|
| `Fighter1` | fwd | `obDR12` | 2 | down | early | 0.6083 | 0.0013 | 0.21 % |
| `Fighter1` | fwd | `obDL12` | 3 | down | early | 0.5442 | 0.0021 | 0.39 % |
| `Fighter1` | fwd | `obUL12` | 4 | up | late | 0.2532 | 0.0027 | 1.06 % |
| `Fighter1` | fwd | `obUR12` | 5 | up | late | 0.1437 | 0.0025 | 1.72 % |
| `Fighter1` | rev | `obUL12` | 2 | up | early | 0.2143 | 0.0016 | 0.73 % |
| `Fighter1` | rev | `obUR12` | 3 | up | early | 0.1626 | 0.0023 | 1.41 % |
| `Fighter1` | rev | `obDR12` | 4 | down | late | 0.6243 | 0.0030 | 0.49 % |
| `Fighter1` | rev | `obDL12` | 5 | down | late | 0.5427 | 0.0007 | 0.13 % |
| `Multirole1` | fwd | `obDR12` | 2 | down | early | 0.7940 | 0.0085 | 1.07 % |
| `Multirole1` | fwd | `obDL12` | 3 | down | early | 0.8661 | 0.0326 | 3.77 % |
| `Multirole1` | fwd | `obUL12` | 4 | up | late | 0.2739 | 0.0080 | 2.92 % |
| `Multirole1` | fwd | `obUR12` | 5 | up | late | 0.1717 | 0.0052 | 3.00 % |
| `Multirole1` | rev | `obUL12` | 2 | up | early | 0.1416 | 0.0063 | 4.47 % |
| `Multirole1` | rev | `obUR12` | 3 | up | early | 0.1528 | 0.0083 | 5.43 % |
| `Multirole1` | rev | `obDR12` | 4 | down | late | 1.0358 | 0.0161 | 1.55 % |
| `Multirole1` | rev | `obDL12` | 5 | down | late | 1.1992 | 0.0435 | 3.63 % |
| `FastBomber1` | fwd | `obDR12` | 2 | down | early | 0.9269 | 0.4001 | 43.17 % |
| `FastBomber1` | fwd | `obDL12` | 3 | down | early | 1.7988 | 0.5418 | 30.12 % |
| `FastBomber1` | fwd | `obUL12` | 4 | up | late | 1.1901 | 0.4338 | 36.45 % |
| `FastBomber1` | fwd | `obUR12` | 5 | up | late | 1.1043 | 0.1944 | 17.60 % |
| `FastBomber1` | rev | `obUL12` | 2 | up | early | 0.8810 | 0.2622 | 29.76 % |
| `FastBomber1` | rev | `obUR12` | 3 | up | early | 0.7040 | 0.1781 | 25.29 % |
| `FastBomber1` | rev | `obDR12` | 4 | down | late | 1.2145 | 0.3935 | 32.40 % |
| `FastBomber1` | rev | `obDL12` | 5 | down | late | 1.5529 | 0.2087 | 13.44 % |

### 3.2 The 2×2

Geometric mean `terminalOffDeg` in degrees; 16 scored segments per cell per airframe (2 tags × 8
replicates). Main effects and interaction computed on the log scale and reported as ratios.

| airframe | down-early | down-late | up-early | up-late | **direction ×** | **position ×** | interaction × |
|---|---:|---:|---:|---:|---:|---:|---:|
| `Fighter1` | 0.5754 | 0.5821 | 0.1867 | 0.1908 | **3.067** | 0.983 | 1.005 |
| `Multirole1` | 0.8290 | 1.1141 | 0.1469 | 0.2168 | **5.385** | 0.710 | 1.048 |
| `FastBomber1` | 1.2183 | 1.3351 | 0.7625 | 1.1059 | **1.389** | 0.793 | 1.150 |
| **pooled** | 0.8345 | 0.9531 | 0.2755 | 0.3576 | **2.841** | 0.821 | 1.066 |

`direction ×` = geomean(down) / geomean(up), position cancelled.
`position ×` = geomean(early) / geomean(late), direction cancelled. **< 1 means late is worse.**

**Read the down column against the up column and the answer is already there.** Moving a down leg from
slot 2–3 to slot 4–5 changes its error by 1–34 %; changing its *direction* changes it by 39–439 %.

### 3.3 The mirror-pair ratios — R28's exact comparison, and its reversal

Geometric mean of `terminalOffDeg`, down-step ÷ its exact mirrored up-step. The mirrors are
**DR↔UL** and **DL↔UR** (equal magnitude, opposite sign in both axes); DR↔DL is not a mirror pair.
`fwd ratio` is R28's geometry reproduced (down early, up late); `rev ratio` is the reversal.

| airframe | pair | fwd ratio (down early) | rev ratio (down late) | **R28 `oblique-12`** |
|---|---|---:|---:|---:|
| `Fighter1` | DR/UL | 2.402 | 2.914 | 2.42 |
| `Fighter1` | DL/UR | 3.786 | 3.337 | 3.92 |
| `Multirole1` | DR/UL | 2.900 | 7.320 | 2.79 |
| `Multirole1` | DL/UR | 5.043 | 7.854 | 5.16 |
| `FastBomber1` | DR/UL | 0.769 | 1.365 | 0.93 |
| `FastBomber1` | DL/UR | 1.577 | 2.246 | 2.01 |

**The `fwd` column reproduces R28 to within 3 % on the four light-jet cells** (2.402 vs 2.42, 3.786 vs
3.92, 2.900 vs 2.79, 5.043 vs 5.16) across a mod-version boundary and a different session. That is an
independent replication of the R28 measurement, and it is what licenses reading the `rev` column as a
change of condition rather than a change of instrument.

**Not one of the six `rev` ratios drops below its `fwd` value in a way that inverts the finding.** Four
of six *rise*. `Fighter1 DL/UR` falls (3.786 → 3.337) and stays far above 1.

### 3.4 The direction effect with its own noise floor

Paired **within-capture** log ratio (the same capture's down leg over its own mirrored up leg),
n = 8 per cell. The sd column is the noise floor of the ratio itself.

| airframe | pair | card | ratio | 95 % CI | sd of log ratio |
|---|---|---|---:|---|---:|
| `Fighter1` | DR/UL | fwd | 2.402 | [2.384, 2.421] | 1.1 % |
| `Fighter1` | DR/UL | rev | **2.914** | [2.903, 2.924] | 0.5 % |
| `Fighter1` | DL/UR | fwd | 3.786 | [3.747, 3.825] | 1.5 % |
| `Fighter1` | DL/UR | rev | **3.337** | [3.305, 3.369] | 1.4 % |
| `Multirole1` | DR/UL | fwd | 2.900 | [2.835, 2.966] | 3.3 % |
| `Multirole1` | DR/UL | rev | **7.320** | [7.110, 7.536] | 4.2 % |
| `Multirole1` | DL/UR | fwd | 5.043 | [4.876, 5.216] | 4.9 % |
| `Multirole1` | DL/UR | rev | **7.854** | [7.582, 8.136] | 5.1 % |
| `FastBomber1` | DR/UL | fwd | 0.769 | [0.611, 0.968] | 33.2 % |
| `FastBomber1` | DR/UL | rev | **1.365** | [1.058, 1.760] | 36.7 % |
| `FastBomber1` | DL/UR | fwd | 1.577 | [1.265, 1.966] | 31.8 % |
| `FastBomber1` | DL/UR | rev | **2.246** | [1.961, 2.574] | 19.6 % |

Direction main effect per mirror pair (`sqrt(fwd × rev)`), and pooled per airframe:

| airframe | DR/UL | DL/UR | pooled (both pairs, both cards) | 95 % CI |
|---|---:|---:|---:|---|
| `Fighter1` | 2.646 | 3.554 | **3.067** | [2.826, 3.328] |
| `Multirole1` | 4.607 | 6.293 | **5.385** | [4.836, 5.996] |
| `FastBomber1` | **1.024** | 1.882 | **1.389** | [1.158, 1.665] |

Against the noise floors in §2, the light-jet effects are **~240× (`Fighter1`) and ~93×
(`Multirole1`) the replicate CV**. All three pooled CIs exclude 1.

**The single null in the batch, stated as a null:** `FastBomber1` `DR/UL` has a direction effect of
**×1.024** — no effect at all — and its `fwd` ratio of 0.769 is *entirely* the position effect
(0.751 for that pair) applied to a symmetric pair. On that one mirror pair, on that one airframe, the
data shows **OUTCOME 2**. `FastBomber1`'s `DL/UR` pair carries all of its ×1.389, and `FastBomber1` is
the airframe R28 §3.3 already classified as not completing the card (its terminal *elevation* residual
is 0.65–1.64° on every leg, against −0.01 to −0.02° on `Fighter1`; it is mushing, and its azimuth
ratio is measured on top of that).

---

## §4 — the position effect, separately

Same direction, compared across the two cards (unpaired; the interleave in §0.2 is its protection).

| airframe | direction | early/late | 95 % CI |
|---|---|---:|---|
| `Fighter1` | down | 0.988 | [0.945, 1.034] |
| `Fighter1` | up | 0.978 | [0.834, 1.148] |
| `Multirole1` | down | **0.744** | [0.710, 0.780] |
| `Multirole1` | up | **0.678** | [0.600, 0.766] |
| `FastBomber1` | down | 0.913 | [0.688, 1.211] |
| `FastBomber1` | up | **0.689** | [0.568, 0.837] |

Pooled over both directions: `Fighter1` **0.983** [0.735, 1.315]; `Multirole1` **0.710** [0.465,
1.085]; `FastBomber1` **0.793** [0.656, 0.960].

Three things about it, in order of importance:

1. **It has the wrong sign to be R28's explanation.** A leg at slot 4–5 is **worse** than the same leg
   at slot 2–3 (ratio < 1). In every shipped oblique card the down legs are the early ones, so this
   effect *reduces* the down/up ratio those cards measure. R28's exclusion of order was correct, and
   its numbers were conservative.
2. **It is absent on `Fighter1`** (0.988 / 0.978, both CIs spanning 1) and present on `Multirole1`
   (0.744 / 0.678, both CIs clear of 1). It is not universal.
3. **What "position" means here is unidentifiable in this batch, and lumping it is deliberate.** The
   two cards differ in slot order **and** in the `arm` segment's elevation (fwd +8.49°, rev −8.49°)
   **and** in the accumulated energy at slots 4–5. All three are properties of card order, which is
   the alternative R28 could not exclude, so testing them together is the right test. But **do not
   read this as "filter warm-up"** — nothing here separates warm-up from energy accumulation from arm
   attitude.

The energy component is at least visible. Entry speed at each cell (throttle is pinned; the drone
accelerates through the card):

| airframe | down-early | down-late | up-early | up-late |
|---|---:|---:|---:|---:|
| `Fighter1` | 262.8 | 283.9 | 276.1 | 293.5 |
| `Multirole1` | 276.0 | 313.6 | 291.0 | 319.1 |
| `FastBomber1` | 252.0 | 267.1 | 266.7 | 272.6 |

`Multirole1` has both the largest early→late speed excursion (+37.6 / +28.1 m/s) and the largest
position effect; `Fighter1` has the smallest of both. That is consistent with an energy mechanism —
the achievable turn rate at a fixed g falls as 1/v, so a later leg is a harder leg at the same angular
demand — but n = 3 airframes supports no more than "consistent with".

---

## §5 — what is excluded, and what is not

### 5.1 Speed is crossed with direction, and direction survives both ways

This is the strongest single argument in the batch, and it is free — the design produced it.

| card | the down legs fly at | the up legs fly at | down/up ratio |
|---|---|---|---|
| `oblique-12-fwd` | the **slower** entry (252–276 m/s) | the **faster** entry (273–319 m/s) | 1.10 – 5.04 |
| `oblique-12-rev` | the **faster** entry (267–314 m/s) | the **slower** entry (267–291 m/s) | 1.37 – 7.85 |

Down is worse when it is the slow leg *and* when it is the fast leg. **Dynamic pressure, airspeed and
energy state cannot produce the direction effect** — they would have to reverse their own sign between
the two cards to do it. R28 excluded energy by an airframe-comparison argument (`trainer` changes
speed by +1.9 m/s and still shows 4.7×); this excludes it by direct crossing within one airframe.

### 5.2 Terminal elevation is excluded, again and more cleanly

The `DR/UL` mirror pair terminates at **the same commanded elevation (0°)** in both cards — DR arrives
there from above, UL from below. The direction effect on that matched pair is ×2.646 (`Fighter1`) and
×4.607 (`Multirole1`). Where the nose ends up does not matter; how it got there does.

### 5.3 Error accumulation across the card is excluded by sign — but weakly, and differently from R28

R28 excluded carry-over because later segments were *better*. In R30 later segments are *worse*
(§4) — so that particular exclusion no longer holds, and it is exactly the position effect. It is
still too small, and the wrong sign, to be R28's finding.

### 5.4 What this batch does NOT identify

- **Why down is worse.** §6 is a lead, not a demonstration.
- **What "position" is made of** (order vs energy vs arm attitude) — see §4.3.
- **Whether the effect scales with step size.** One card family, one step magnitude (12°). R28's
  `oblique-6` and `oblique-below` show it too but were never order-controlled.
- **Whether it holds on the other five airframes** R28 measured it on. Three lanes here.
- **Anything about v0.94's concurrent A/B.** §0.4.
- **`FastBomber1`'s common-mode wander** (§2) — 16–27 % of capture-to-capture variation with no
  recorded covariate. Not investigated; it is a separate defect and it is on the airframe R28 already
  flagged.

---

## §6 — the mechanism lead: `bSup` is direction-keyed by construction, and R28's exclusion of it rests on a median

R28 §3.2 recorded the down-step penalty as "**not attributable to any instrumented lever**", excluding
belowness on the grounds that "`bSup` measures 0.000–0.06 on all four `oblique-below` legs". **That
number is a median.** Re-measured on R28's own captures:

| R28 `oblique-below`, `Fighter1` | mean `bSup` | median `bSup` |
|---|---:|---:|
| `obDR6low` (down) | **0.240** | 0.032 |
| `obDL6low` (down) | **0.293** | 0.037 |
| `obUL6low` (up) | 0.045 | 0.041 |
| `obUR6low` (up) | 0.041 | 0.016 |

`bSup` is zero for most of a segment and large during the acquisition transient, so the median is ~0
on **both** directions while the mean is **6× asymmetric**. The exclusion does not hold.

### 6.1 The gate is on for a down step and off for an up step, definitionally

`ChaseController.cs:2048`:

```csharp
float belowSuppress = Arm(Cfg.BelowAlignSuppress)
    ? Mathf.Clamp01(-alignFracH) * Mathf.Clamp01((1f - bigTurn) / downAlignTaper) : …;
blendWeight *= (1f - belowSuppress);
…
float rollErr = Mathf.Lerp(eFine, eAlign, blendWeight);
```

`alignFracH` (`:963`) is the **horizon-referenced belowness of the aim target relative to the nose**.
A down step puts the target below the nose for its entire approach ⇒ `alignFracH < 0` ⇒
`Clamp01(-alignFracH) → 1` ⇒ suppression on. An up step puts it above ⇒ `Clamp01(-alignFracH) = 0` ⇒
suppression off. **The v0.85 below-nose suppression is a gain reduction on the roll-to-align channel
that is keyed on the sign of the step**, and it therefore cannot be symmetric between a mirror pair no
matter how well the pair is matched geometrically.

Measured over R30's 192 scored segments (mean over the segment, then over 8 replicates):

| airframe | leg | mean `bSup` | % samples `bSup` > 0.5 | mean `bWt` | % `bWt` railed | terminal \|`outY`\| |
|---|---|---:|---:|---:|---:|---:|
| `Fighter1` | down (4 cells) | 0.44 – 0.71 | 47 – 78 % | 0.015 – 0.023 | **0.0 %** | 0.094 – 0.104 |
| `Fighter1` | up (4 cells) | 0.037 – 0.045 | **0.0 %** | 0.072 – 0.132 | 3.1 – 4.5 % | 0.029 – 0.051 |
| `Multirole1` | down | 0.74 – 0.88 | 82 – 98 % | 0.023 – 0.040 | **0.0 %** | 0.116 – 0.132 |
| `Multirole1` | up | 0.11 – 0.16 | 0 – 10 % | 0.110 – 0.166 | 3.1 – 6.1 % | 0.029 – 0.054 |
| `FastBomber1` | down | 0.37 – 0.69 | 41 – 76 % | 0.014 – 0.021 | **0.0 %** | 0.096 – 0.115 |
| `FastBomber1` | up | 0.043 – 0.112 | 3.5 – 14 % | 0.105 – 0.207 | 2.9 – 5.1 % | 0.011 – 0.056 |

`bWt` — the roll-to-align loop gain *after* suppression, the quantity R28's `GATE-CHATTER-FINDINGS.md`
§5a measured the +0.918 correlation on — is **3–10× lower on every down leg than on its mirrored up
leg**, and rails at 1.0 for 3–6 % of every up leg and **0.0 % of every down leg**. The residual is in
the channel `bWt` gates: on the light jets it is almost pure azimuth (`Fighter1 fwd obDR12`: terminal
`azErr` +0.608°, `elevErr` −0.015°), and the down leg terminates with a **2–4× larger standing yaw
command** — the loop is still pushing and not getting there.

Rank correlation across the 8 cells within each airframe, log terminal error against each signal:

| signal | `Fighter1` | `Multirole1` | `FastBomber1` |
|---|---:|---:|---:|
| mean `bSup` | +0.714 | +0.738 | +0.286 |
| mean `bWt` | −0.548 | −0.643 | −0.643 |
| mean `pEff` | −0.690 | −0.929 | −0.667 |
| terminal \|`outY`\| | +0.905 | +0.952 | +0.690 |

**This reproduces across batches.** R28's `oblique-12` `Fighter1` legs give mean `bSup` 0.434/0.571
(down) and 0.044/0.064 (up), `bWt` 0.016/0.020 and 0.131/0.076 — matching R30's 0.440/0.571 and
0.039/0.045, 0.015/0.020 and 0.132/0.076 to three decimals across two mod versions.

### 6.2 Why this is a lead and not a conclusion

**On the smaller-step cards the channel is too small to carry the effect.** On R28's `oblique-6` and
`oblique-below`, `bWt` is 0.002–0.004 on the down legs and 0.009–0.034 on the up legs. The asymmetry
is the same 5–10×, but the *absolute* roll-to-align contribution is ~0 on both sides, and gating
nothing by 5× cannot produce the 1.2–12.6× error ratios R28 measured there. So:

- On `oblique-12`, where `bWt` reaches 0.07–0.21 on the up legs, `bSup` is a **plausible sufficient**
  mechanism.
- On `oblique-6` / `oblique-below` it is **not sufficient**, and something else in the pitch→azimuth
  handover is also direction-keyed.
- `pEff` remains the R28 lead and still correlates (−0.67 to −0.93), but it is in the **pitch**
  channel while the residual is in **azimuth**, and R28 already showed its sign flips with step size.
  `bSup` is the better candidate because it is in the right channel, it is direction-keyed by
  construction rather than by correlation, and — unlike `pEff` — **it is already an A/B lever.**

### 6.3 The test, and it is one batch

`Cfg.BelowAlignSuppress` is one of the six `Arm()` sites. The cards already exist
(`cards/e1-below-suppress.json`, `cards/e1-below-control.json`). **Fly `oblique-12-fwd` + `oblique-12-rev`
again with `armToggle: "BelowAlignSuppress"`** on three airframes, 8 replicates, ~12 minutes:

- **Pass** — the arm with suppression OFF collapses the down/up ratio toward 1.0 **with the up legs
  unchanged**. Then §6.1 is the mechanism and the fix is in the gate.
- **Regression** — the up legs degrade to meet the down legs. That is a gain reduction dressed as
  symmetry; reject it.
- **Null** — the ratio is unchanged with the gate off. Then `bSup` is a correlate too, and the next
  instrument is the pitch→azimuth handover on the down leg, not another lever sweep.

This also finally exercises v0.94's concurrent per-aircraft A/B in flight (§0.4), on a 3-lane fleet,
which is the other thing R30 was supposed to demonstrate and did not.

---

## §7 — what this means for the shipped oblique grid

**R28's direction claims stand. The grid does not need re-flying to defend them, and it does need
changing before it is trusted on anything else.**

1. **The claims stand.** Direction is real, it is 4–7× the position effect, the position effect has
   the wrong sign to have manufactured it, and R28's `oblique-12` numbers replicate to 3 %. Ranked fix
   #1 in `R28-FINDINGS.md` survives intact, and the "run the mirrored-order card first" precondition
   on it is now **satisfied**.
2. **But every shipped oblique card still confounds direction with position**, and the confound is now
   *measured* rather than merely acknowledged: **21–29 % on `Multirole1`, 21 % on `FastBomber1`**. Any
   future direction claim from a single-traversal card carries that as a known bias of unknown sign
   for the airframe in question. It does not need to be fixed by re-flying the grid; it needs to be
   fixed by **shipping the pair**.
3. **Recommendation — pair the grid, do not randomize it.** Add a `-rev` twin for each oblique card
   rather than randomizing traversal per replicate. Reasons: the tags are already identical and
   `compare-runs.py` keys on `(airframe, card, arm)` so the card name keeps them apart with no tooling
   change; a `-rev` twin makes the position effect **measurable** in every batch instead of merely
   averaged away; and randomized traversal would need a new card field, a new source of run-to-run
   variation, and a `scorecard.py` change. The two cards R30 flew are already in `cards/` — this is
   five files, no code.
4. **Use ABBA, not ABAB, for the pair** (§0.2). R30's interleave left a half-period drift residual of
   ~0.5 %, immaterial here only because the drift was small.
5. **`FastBomber1` should not be carrying a direction claim at all.** 30 % cell CV, 16–27 % unexplained
   common-mode wander, a 0.65–1.64° standing elevation residual on every leg, and one mirror pair with
   no direction effect. R28 §3.3 called it "the airframe at which the law starts to run out of pitch";
   R30 agrees and adds that its *azimuth* asymmetry is measured on top of a *pitch* failure. Keep it in
   batches as a stressor; do not quote its ratios.
6. **Unchanged from R28:** the corner-referenced entry (v0.93 `startSpeedCorner`) is still the right
   fix for the flat 250 m/s. R30 flew the flat entry on purpose — it had to match R28 to be a
   replication — and its cost is visible as the +15 to +43 m/s early→late speed walk in §4 that the
   position effect partly rides on. **The `-rev` twins should adopt `startSpeedCorner` together with
   the originals, or not at all**; changing one of a mirrored pair re-bands only half the experiment.

---

## What would falsify this analysis

- **The direction effect disappears with `BelowAlignSuppress` off.** Then §6 is the mechanism, this
  document's "unattributed" framing is superseded, and the fix is a one-gate change. (This is the
  *hoped-for* falsification — §6.3.)
- **It persists with the gate off and with the roll channel forced open.** Then the effect is in the
  pitch→azimuth handover and neither `bSup` nor `pEff` is more than a correlate.
- **The position effect is the arm elevation, not the slot.** A third card — forward traversal but
  arming at el −8.49° — would separate them. If its slot 2–3 down legs match `rev`'s slot 4–5 down
  legs, "position" was arm attitude all along and §4.3's caveat becomes the finding.
- **`Fighter1`'s 0.4 % capture-level CV is the instrument, not the aircraft.** Eight replicates
  agreeing to 0.13 % on `obDL12` is remarkable enough to be worth one check that the placement reset
  is not making replicates *artificially* identical (e.g. an identical trajectory rather than an
  identical initial condition). If it is, every noise floor in R28 and R30 is optimistic.
- **The effect is step-magnitude-specific.** Only 12° was order-controlled. If a `-rev` twin of
  `oblique-2` or `oblique-dz` shows inversion, the finding is confined to the large-step regime where
  `bWt` is live.

---

## Reproducing

```bash
cd "<game>/BepInEx"
python <repo>/debugtests/compare-runs.py --summary mouseaim-rec-v0.94.0-R30-*.csv
python <repo>/debugtests/scorecard.py mouseaim-rec-v0.94.0-R30-*.csv          # roll-up past 10 files
python <repo>/debugtests/analyze-wobble.py --digest \
  $(ls mouseaim-rec-v0.94.0-R30-d3-FastBomber1-*-oblique-12-fwd-*.csv | head -2)
```

The 2×2, the mirror ratios and the paired CIs were produced by importing `scorecard.score_run` per
file and grouping its `terminalOffDeg`; the `bSup`/`bWt` tables are direct column reads (those are
recorder columns, not tool metrics). No metric in this document is a reimplementation. The R28
cross-checks in §6 read `mouseaim-rec-v0.92.1-R28-*.csv` in place — no R28 or R29 artifact was
modified.
