# R28 — the oblique family on 8 airframes, v0.92.1

**Batch 1 of `LAW-CHARACTERIZATION.md` §4, widened.** 8 airframes × 6 cards × 8 replicates = **384
captures**, 233 519 rows, one unattended run. Source:
`<game>/BepInEx/mouseaim-rec-v0.92.1-R28-d{1..8}-<airframe>-{01..384}-<card>-*.csv` (+ `.airframe.json`
sidecars) and `mouseaim-anomalies-v0.92.1-R28-20260730-192223.log`.

| | |
|---|---|
| airframes | `Fighter1` `Multirole1` `SmallFighter1` `trainer` `VTOLTrainer1` `EW1` `FastBomber1` `Darkreach` |
| cards | `oblique-05` `oblique-2` `oblique-dz` `oblique-6` `oblique-12` `oblique-below` |
| entry | 250 m/s / 4000 m (`oblique-below` 6000 m), throttle pinned 0.70 |
| A/B arm | none (baseline) — `arm=` absent from every `# config` line, which is correct |
| refused pre-spawn | `CAS1`, `COIN` — 250 m/s is above 0.95 × Vmax for both (v0.92 gate, working as designed) |

Read with `compare-runs.py --summary`, `scorecard.py`, `flightscore.py`, `analyze-wobble.py --digest`.
All four `--selftest` clean at the commit analysed. Nothing in this document recomputes a metric the
tools already produce; the aggregation drives `scorecard.score_run` and `flightscore.score_file` as
modules and groups their output.

---

## Verdict

1. **The instrument is sound and the batch is usable** — 384/384 complete, zero aborts, zero
   truncations, zero unrecognised tags, `ctrlReset=1` on all 384. One artifact is **missing**:
   `LogOutput.log` has been overwritten by a later R29/v0.93.0 session, so R28's launch, refusal and
   despawn lines are gone. Nothing in the findings below depends on them, but the standing rule
   "after any batch, grep `[drone]`" could not be executed. **Copy the log out with the captures.**
2. **`frameMs` is fixed and it caught two real hitches R27 would have missed — but it is
   vsync-censored and therefore cannot size the 8→12 drone decision.** 99.67 % of rows read exactly
   16.7 ms, p99 = 16.70, and there is not one dropped-frame row (33.3 ms) in 65 minutes. It is a
   tripwire, not a headroom gauge.
3. **It is not "one law that suits the Ifrit".** The Ifrit (`Multirole1`) ranks **4th of 8** on the
   normalized metric. But it is not one law either: the spread is **0.237 A against a 0.0034 A
   replicate noise floor (70×)**, and the two heaviest airframes fail outright.
4. **The largest single defect in the batch is a systematic down-step penalty**: at matched step
   magnitude, matched mirror geometry and matched terminal elevation, a step that moves the nose
   **down** leaves **1.2–17.9× the terminal error** of its exact mirror moving up. Universal across 7
   of 8 airframes, 20–1000× the replicate noise, and **not attributable to any instrumented lever**.
5. **Three of the four defects the plan queued for Q4 cannot be tested on this card family.** #20's
   floor branch runs on **0.00 %** of rows for every airframe that flies these cards; #21's rail
   fires on **0/1344** healthy-airframe segments because the bank pipeline is *dormant*, not railed;
   #23's predicted signature does not appear at all. The plan's premise — "the oblique family is it"
   — is wrong for #20 and #21.

---

## Q0 — did the instrument work?

### Sound

| check | result |
|---|---|
| captures | 384 — exactly 8 × 6 × 8, no cell short or over (matrix verified per airframe × card) |
| `# stop` present | 384/384 |
| `# stop` reason | 384/384 `card '<name>' complete`; **0 aborted**, 0 refused, 0 declined |
| duration | 38.00 s on all 384 (min = max = 38.00); 608–609 samples |
| segments | 5 per capture on all 384; `arm` 5.92–5.97 s, steps 7.93–7.98 s |
| truncated segments | **0** (`compare-runs.py` reported none; the 0.05 s spread is one fixed step) |
| unrecognised tags | **0** — 25 distinct tags, all resolved by `scorecard.py` |
| RAILED warnings | 82 of 1536 scored segments (5.3 %) — **all 82 on `Darkreach`** |
| columns | 64 on all 384, header/row lockstep intact |
| `ctrlReset=1` | **384/384** |
| `# entry` provenance | 384/384, all `v=…->250.0`, `alt=…->4000.0` (or 6000.0), `fuel=…->1.000` |
| `snapBackM` | 0.0 on the 8 first-placements, 9.5–10.7 km on the rest — the anchor snap-back is live |
| `# override` | absent on all 384 (these cards pin nothing) |
| `arm=` / `armKnob=` | absent on all 384 — correct, a baseline has no A/B arm |
| anomaly log | present, 3043 lines, `overshoot` / `over-roll` only |

`scorecard.py` excluded exactly 384 segments — one `arm` per capture, which is by design.

### The one gap

**`LogOutput.log` in the capture folder is not R28's.** Its header reads `v0.93.0` and
`[session] run R29 id 20260730-195904`; the `[drone]` lines in it describe a ten-lane
`oblique-05-c` launch at `0.95x corner (per airframe)`, i.e. the *next* experiment. R28's launch
line (which names whether the airframe/alt/speed/count came from the card or from F1), its refusal
lines for `CAS1`/`COIN`, and its despawn reasons are unrecoverable.

Everything above was therefore established from the capture headers rather than from the log. The
`CAS1`/`COIN` refusal is inferred from their absence (0 of 384 captures) plus the arithmetic:
`CAS1` Vmax 206 m/s and `COIN` 142 m/s, both under 250/0.95 = 263. That is consistent with the v0.92
gate but is *not* the same as having read the line.

> **Action:** the batch procedure should copy `LogOutput.log` alongside the CSVs at the end of a run.
> It is the only artifact that is overwritten rather than accumulated.

---

## Q0b — `frameMs`, specifically

First batch since the v0.92.1 fix (`#37`: sampled in `Update()`, not `FixedUpdate`).

**It is measuring.** R27 read exactly 16.70 on all 223 899 rows. R28 takes **9 distinct values**:

| | |
|---|---|
| rows | 233 519 |
| distinct values | **9** — 16.6, 16.7, 16.8, 16.9, 17.0, 17.3, 19.3, 370.2, 579.4 |
| mean | 16.732 ms |
| p50 / p90 / p99 | 16.70 / 16.70 / 16.70 |
| p99.9 | 16.90 |
| min / max | 16.60 / **579.40** |
| rows > 20 ms | **16** (0.0069 %) |
| rows > 50 ms | **16** (0.0069 %) |
| rows > 100 ms | **16** (0.0069 %) |
| rows at 33.3 ms (one dropped vsync frame) | **0** |

232 755 rows (99.67 %) read exactly 16.7 ms.

### The spikes are shared, not scattered

All 16 rows over 20 ms are **two events**, each written simultaneously by all eight drones with a
byte-identical value:

| wallclock `t` | frameMs | lanes affected |
|---|---|---|
| 450.350 | 370.2 | 1–8 (all) |
| 1607.283 | 579.4 | 1–8 (all) |

That is the expected shape — `TestDrone.FrameDt` is one process-wide sample — and it confirms the
column is wired to a real clock rather than to `fixedUnscaledDeltaTime`.

### The stagger is doing its job, and this is the first per-row proof

The same 579.4 ms hitch landed on eight *different* places in eight different cards:

```
Fighter1       obDL2   tSeg 2.817      EW1          obUR12  tSeg 3.833
Multirole1     obDR2   tSeg 7.817      FastBomber1  obUR12  tSeg 0.833
SmallFighter1  obDR2   tSeg 4.817      Darkreach    obUL12  tSeg 5.833
trainer        obDR2   tSeg 1.817      VTOLTrainer1 arm     tSeg 4.800
```

Sixteen poisoned rows out of 233 519, spread across sixteen different (capture, segment) cells at
roughly one row in 130 per affected segment. No segment needs dropping.

### What this supports for 8 → 12 drones

**It supports going to 12 — but `frameMs` is not the evidence, and it will not become the evidence.**

The frame clock is pinned at 16.7 ms ≈ 59.9 fps, which is the vsync period and also the fixed step.
A vsync-locked frame time is a **censored** measurement: it reads the cap right up until a frame is
missed, and then jumps to 33.3. There were **zero** 33.3 ms rows in 65 minutes of eight concurrent
complex-physics aircraft. So the honest statement is:

- **Positive evidence at 8:** not one missed frame in 233 519 samples. Combined with the operator's
  3 ms CPU / 5 ms GPU average, the render budget at 8 drones is roughly a fifth used.
- **What the data cannot say:** how much margin remains. `frameMs` would read an identical 16.7 at
  4 drones and at 11, and 33.3 at 12 — it has no gradient to extrapolate along.
- **Recommendation:** go to 12. Treat any non-zero count of 33.3 ms rows as the stop signal — that is
  now a one-line check on the column. If a real headroom number is wanted before committing, run
  **one** batch with vsync disabled; the column then becomes continuous and the CPU-bound frame time
  is directly readable. Do not run the vsync-off batch as a *science* batch: an uncapped frame rate
  changes `Time.unscaledDeltaTime` for every drone at once and is not comparable to R28.

The two 370/579 ms stalls are worth a separate note: both exceed the mod's own 50 ms warning
threshold by 7–12×, and at 8 drones they happened twice in an hour. At 12 lanes, whatever causes them
gets more expensive.

---

## Q1 — the liveness map

Per airframe × card, mean over the four scored segments. `authUsed` = `authorityUsedFrac`,
`trDemand` = `turnRateDemandRatio`.

| airframe | card | bankClamp | turnCap | blendRail | aoaLim | authUsed | trDemand | RAILED |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Fighter1 | 05 / 2 / dz / 6 | 0.0 % | 0.0 % | 0.0 % | 0.0 % | 0.064–0.079 | 0.003–0.043 | 0/32 |
| Fighter1 | 12 | 0.0 % | 3.5 % | 1.9 % | 0.0 % | 0.165 | 0.095 | 0/32 |
| Fighter1 | below | 0.0 % | 0.3 % | 0.0 % | 0.0 % | 0.076 | 0.048 | 0/32 |
| Multirole1 | 05→below | 0.0 % | 0.0–3.8 % | 0.0–2.3 % | 0.0 % | 0.045–0.245 | 0.003–0.128 | 0/32 |
| SmallFighter1 | 05→below | 0.0 % | 0.0–3.6 % | 0.0–2.4 % | 0.0 % | 0.060–0.249 | 0.003–0.121 | 0/32 |
| trainer | 05→below | 0.0 % | 0.0–3.2 % | 0.0–1.5 % | 0.0 % | 0.080–0.151 | 0.003–0.089 | 0/32 |
| VTOLTrainer1 | 05→below | 0.0 % | 0.0–3.6 % | 0.0–2.0 % | 0.0 % | 0.051–0.222 | 0.003–0.123 | 0/32 |
| EW1 | 05→below | 0.0 % | 0.0–5.3 % | 0.0–2.0 % | 0.0 % | 0.080–0.215 | 0.003–0.157 | 0/32 |
| FastBomber1 | 05→below | 0.0 % | 0.0–3.7 % | 0.0–2.0 % | 0.0 % | 0.091–0.186 | 0.003–0.106 | 0/32 |
| **Darkreach** | 05 | **13.3 %** | **19.9 %** | **45.3 %** | **11.6 %** | **0.723** | 0.559 | **14/32** |
| **Darkreach** | 2 | 19.3 % | 17.9 % | 50.4 % | 12.5 % | 0.889 | 0.605 | 16/32 |
| **Darkreach** | dz | 21.6 % | 18.2 % | 50.1 % | 11.8 % | 0.793 | 0.656 | 16/32 |
| **Darkreach** | 6 | 24.3 % | 44.6 % | 50.0 % | 11.1 % | 0.761 | 0.650 | 16/32 |
| **Darkreach** | 12 | 24.6 % | 43.6 % | 50.5 % | 7.9 % | 0.709 | 0.639 | 16/32 |
| **Darkreach** | below | 23.6 % | 20.2 % | 29.5 % | 15.8 % | 0.729 | 0.583 | 4/32 |

### 1.1 The oblique family is unsaturated on 7 of 8 airframes — R27's result generalizes

`authorityUsedFrac` **0.045–0.249** across the seven, i.e. **4.5–25 % of available authority**,
against R27's 4–23 % measured on fast jets alone. Not one RAILED warning on 1344 segments. This is
the first unsaturated multi-airframe corpus the project has, and it is what makes everything below
readable as a control-law result rather than a clamp result.

The one exception is `Darkreach`, which is at 71–89 % and produces **all 82** RAILED warnings in the
batch. Read every Darkreach metric as *no signal* — and see §2.3 for why it is worse than that.

### 1.2 What never fires at all

Across all six cards and all eight airframes:

| mechanism | measured | verdict |
|---|---|---|
| AoA limiter | `aoaLimiterActivePct` **0.0 %** on 7 airframes (Darkreach 7.9–15.8 %) | dormant **in this batch**. ⚠ The verdict originally written here — *"still never fired on a healthy airframe, in any capture ever taken"* — is **RETRACTED**, see below |
| AoA gates | `aoaGU` = `aoaGD` = **1.000** on every sampled segment | never close |
| AoA recovery bias | `aoaRec` **0.000** on all sampled healthy segments except EW1/FastBomber1 `obDR12` | effectively dormant |
| below-nose suppression | `bSup` **0.000–0.06**, incl. all four `oblique-below` legs at −20° | dormant even on the card built to exercise it |
| bank clamp | **0.0 %** on 7 airframes | never reached |
| **roll-to-align bank pipeline** | `bWt` **median 0.000 on every card, every healthy airframe** | **dormant** — see below |

> **RETRACTION 2026-07-31 — the AoA-limiter row's verdict, not its measurement.** The 0.0% / 7
> airframes / Darkreach 7.9–15.8% figures are correct **for R28**. The verdict attached to them —
> *"never fired on a healthy airframe, in any capture ever taken"* — was a corpus-wide claim this
> batch could not support, and it is false. It was already contradicted **within its own row**
> (Darkreach at 7.9–15.8%, reclassified away as "not healthy") and then directly by R29 §4.3
> (`trainer·obUL12`, 11.9% on 8 of 8 replicates, unrailed). Corpus-wide today:
> `aoaLimiterActivePct` is non-zero on **66** (run, airframe, tag) cells, **23 of them with no railed
> segment anywhere**, topped by **R33 `Darkreach·obDR6` at 100.0%** (n = 4, `railed = 0`,
> `aoaPeakDeg` 7.38–7.59 vs a 10° limiter, `authorityUsedFrac` 0.717–0.748). Reproduce:
> `index-captures.py --query "… GROUP BY run_tag, airframe, tag HAVING avg(aoaLimiterActivePct) > 0"`.
> **Read this section as a per-batch measurement, which is what it is; the generalisation was the
> error.** See `LAW-CHARACTERIZATION.md` §1 and `LAW-LEDGER.md` X7.

The bank pipeline is the significant one. Max `bWt` by card, worst case across the seven:

| card | 05 | 2 | dz | 6 | 12 | below |
|---|---:|---:|---:|---:|---:|---:|
| max `bWt` | 0.000 | 0.000 | 0.071 | 0.463 | 1.000 | 0.471 |
| median `bWt` | 0.000 | 0.000 | 0.000 | 0.000 | 0.000 | 0.000 |

So on `oblique-05` and `oblique-2` the roll-to-align channel is **identically zero** — it is not
railed, it is switched off — and it only reaches its rail transiently, on `oblique-12`, for under
2.4 % of samples. Azimuth on this family is closed mostly by yaw: `|outR|/(|outR|+|outY|)` is
0.25–0.43 on the light jets.

### 1.3 What is newly live: the fine integrator

`iPitch` peaks at **0.018–0.047** against the 0.12 cap (15–39 %) on the seven healthy airframes, with
`iGate` median 0.90–0.97. R21 measured the same integrator at **±0.001 for a whole 30 s turn**. The
oblique family is the first regime in the corpus where the anti-residual term actually winds. That is
a genuine unlock: `IntegralStallGate` (v0.83) and `iGain`/`iCap` become A/B-able here and were not
before.

### 1.4 `oblique-05` is below the instrument's resolution

For six of the eight airframes `flightscore` returns **A undefined** on all four `oblique-05`
segments: 100 % of samples sit inside the 1.0° ON_TARGET cone, so there is nothing to score. The
card's own note anticipates this ("expect both stick channels under the 0.02 analysis deadband"), and
`terminalOffDeg` is still meaningful (0.0004–0.028°), but the normalized metric is blind there. If
the 0.5° rung is to stay in the ladder it needs `--cone 0.2`, which should be recorded as the
standard invocation for that card.

---

## Q2 — one law, or one law that suits the Ifrit?

Metric: **`flightscore` A** — the time-weighted closure efficiency against what the airframe could
physically have done at that instant. 0.5 = the nose is stationary relative to the demand, 1.0 =
closing at the ideal rate, 0.0 = diverging at the full available rate. Every normalizer comes from
that capture's own `.airframe.json` and live state, which is what makes the numbers comparable across
airframes; nothing here is in absolute degrees.

Scored **per airframe separately** (48 files each) and compared **tag by tag**, so regime is matched.

### 2.1 The ranking

Median A over the 20 scored tags where A is defined:

| rank | airframe | median A | min tag | max tag | replicate sd (median) | mass kg | verdict |
|---:|---|---:|---:|---:|---:|---:|---|
| 1 | **Fighter1** (FS-12 Revoker) | **0.705** | 0.569 | 0.845 | 0.0087 | 13 570 | clear best |
| 2 | SmallFighter1 (FS-20 Vortex) | 0.667 | 0.552 | 0.761 | 0.0031 | 13 690 | |
| 3 | trainer (T/A-30 Compass) | 0.654 | 0.611 | 0.814 | 0.0026 | 9 806 | |
| 4 | **Multirole1** (KR-67 Ifrit) | 0.642 | 0.552 | 0.788 | 0.0037 | 25 560 | the incumbent |
| 5 | VTOLTrainer1 (VT-7 Vagrant) | 0.639 | 0.563 | 0.766 | 0.0013 | 11 180 | |
| 6 | EW1 (EW-25 Medusa) | 0.623 | 0.570 | 0.753 | 0.0013 | 24 580 | |
| 7 | FastBomber1 (Alkyon AB-4) | 0.559 | 0.469 | 0.801 | 0.0096 | 57 620 | degraded |
| 8 | **Darkreach** (SFB-81) | 0.468 | 0.403 | 0.563 | 0.1051 | 105 400 | **not flying the card** |

Top-to-bottom spread **0.237 A**. Median replicate sd **0.0034 A**. The spread is **70× the noise
floor**, so the ranking is real — but not every step in it is.

### 2.2 What is and is not inside the noise

| comparison | ΔA | × noise floor | separable? |
|---|---:|---:|---|
| Fighter1 vs SmallFighter1 | 0.038 | 11× | yes |
| SmallFighter1 vs trainer | 0.013 | 3.8× | marginal |
| trainer vs Multirole1 | 0.012 | 3.5× | marginal |
| **Multirole1 vs VTOLTrainer1** | **0.003** | **0.9×** | **no** |
| VTOLTrainer1 vs EW1 | 0.016 | 4.7× | yes |
| EW1 vs FastBomber1 | 0.064 | 19× | yes |
| FastBomber1 vs Darkreach | 0.091 | 27× | yes |

So the honest reading is **three groups, not eight ranks**:

- **Fighter1 alone at 0.705.**
- **A five-airframe band, 0.623–0.667**, inside which ranks 2–6 shuffle by amounts at or near the
  noise floor. `Multirole1` sits *in the middle of this band*, and `Multirole1` vs `VTOLTrainer1` is
  not a real difference.
- **Two failures, 0.559 and 0.468.**

**The headline question, answered literally: no, the law is not tuned to the Ifrit.** The airframe
every prior capture in the project was taken on ranks 4th, is beaten by two airframes it has never
been compared against (`SmallFighter1`, `trainer`), and is statistically indistinguishable from a
tiltwing trainer. Whatever is wrong with the law is not Ifrit-shaped.

**The rule the law is actually violating is the other half of the sentence.** "ONE control law for
ALL airframes" is not satisfied by a 0.237 A spread with two airframes below 0.56. The failure is at
the **heavy** end, not at the incumbent.

### 2.3 What distinguishes the bad ones — and what cannot be concluded

Spearman ρ of median A against each sidecar property, n = 8:

| ρ | property | values (Fighter1 → Darkreach) |
|---:|---|---|
| **+0.810** | `aircraftGLimit` | 9, 9, 9, 9, 8, 6, 5, 5 |
| +0.714 | thrust/weight | 1.09, 1.09, 1.01, 0.66, 0.91, 0.93, 0.53, 0.35 |
| −0.690 | `massKg` | 13.6 k, 25.6 k, 13.7 k, 9.8 k, 11.2 k, 24.6 k, 57.6 k, 105 k |
| −0.690 | `dragAreaTotal` | 2.21, 1.86, 1.66, 1.08, 1.72, 2.70, 2.34, 6.34 |
| −0.643 | `wingAreaTotal` | 49.9, 123, 55.7, 57.4, 41.4, 97.4, 103, 383 |
| +0.571 | `maxPitchAngularVel` | 0.90, 0.75, 0.70, 1.0, 1.0, 0.30, 0.50, 0.30 |
| +0.548 | speed change over the card | +52, +92, +67, +2, +27, +8, +38, −48 m/s |
| −0.524 | wing loading | 272, 208, 246, 171, 270, 252, **558**, 275 kg/m² |
| −0.524 | 250 m/s ÷ corner speed | 1.39, 1.39, 1.39, 1.56, 1.56, 2.08, 1.39, 1.39 |
| +0.048 | **FBW `assist` flag** | 1, 1, 1, 1, 1, **0**, 1, **0** |
| −0.024 | `alphaLimiterStrength` | 0.10, 0.05, 0.08, 0.05, 0.08, 0.05, **0.20**, 0.05 |

**These properties are collinear and n = 8 cannot separate them.** Fighters are simultaneously light,
high-g, high-T/W, small-winged and low-drag. `aircraftGLimit` leads at ρ = +0.810 (p ≈ 0.015) but it
is a label on the same cluster. Excluding Darkreach as a separate failure mode (n = 7) the leader is
unchanged (`aircraftGLimit` +0.750) with `maxRollSpeed` +0.714 close behind. **Do not read a cause
out of this table.** What it supports is one ordinal claim: *the two heaviest airframes in the roster
are the two that fail, and nothing lighter than 26 t fails.*

Three specific hypotheses can be **excluded**:

- **Two-seat vs single-seat: not a factor.** `trainer` (2 crew) ranks 3rd and `VTOLTrainer1`
  (2 crew) ranks 5th, both mid-band, both above the single-seat `EW1`. The v0.90.1 double-step fix is
  holding.
- **FBW `assist` off: not sufficient.** `EW1` and `Darkreach` both run `assist=0` — the game's
  fly-by-wire rate-command layer is not active on either, which means the law's stated premise ("the
  game FBW reads pitch/yaw as a commanded angular rate") is *false* for a quarter of the roster.
  `EW1` nevertheless scores 0.623, mid-band. So `assist=0` is survivable, and the ρ of +0.048 says it
  explains nothing on its own. It remains worth knowing that it is not universal.
- **Distance above corner speed: not a factor, and the sign is backwards.** `EW1` flies at 2.08×
  corner, the furthest above of any airframe, and outranks `FastBomber1` at 1.39×.

### 2.4 The confound this batch does not control

**The card pins throttle at 0.70 and does not hold speed.** Over one 38 s capture:

| airframe | speed 250 → | Δv | altitude 4000 → | Δalt |
|---|---:|---:|---:|---:|
| Multirole1 | 341.6 | **+91.6** | 3835 | −165 |
| SmallFighter1 | 316.9 | +66.8 | 3803 | −197 |
| Fighter1 | 301.8 | +51.8 | 3768 | −232 |
| FastBomber1 | 288.2 | +38.2 | 3582 | −418 |
| VTOLTrainer1 | 277.4 | +27.4 | 3932 | −68 |
| EW1 | 257.9 | +8.0 | 3931 | −69 |
| trainer | 251.9 | +1.9 | 3890 | −110 |
| **Darkreach** | **201.8** | **−48.2** | **2206** | **−1878** |

Dynamic pressure therefore rises 47 % across a Multirole1 capture and falls 35 % across a Darkreach
one. Any metric that depends on control authority is partly reading this. It is **not** the driver of
the ranking — ρ(A, Δv) is only +0.548, and the two energy-neutral airframes (`trainer` +1.9,
`EW1` +8.0) sit mid-band rather than at either end — but it is uncontrolled, and it is the reason the
five-airframe middle band should not be ranked more finely than it has been here.

The fix already exists in the next build: R29 (v0.93.0) spawns at `0.95 × corner (per airframe)`,
which is 171 m/s for `Fighter1` and 152 m/s for `trainer` rather than a flat 250. That is the right
change and it should make Batch 1 repeatable on a common footing.

---

## Q3 — where is it worst?

### 3.1 By airframe × card

Normalized terminal residual = `terminalOffDeg` ÷ step magnitude (`R·√2`), averaged over the four legs.
A value of 1.0 means the segment ended as far from the target as it started.

| airframe | 05 | 2 | dz | 6 | 12 | below |
|---|---:|---:|---:|---:|---:|---:|
| trainer | 0.002 | 0.014 | 0.017 | **0.019** | 0.016 | 0.018 |
| VTOLTrainer1 | 0.004 | 0.036 | **0.044** | 0.041 | 0.028 | 0.034 |
| EW1 | 0.010 | 0.029 | **0.036** | 0.026 | 0.024 | 0.033 |
| Fighter1 | 0.034 | 0.029 | 0.034 | 0.037 | 0.032 | **0.041** |
| Multirole1 | 0.040 | 0.061 | **0.068** | 0.062 | 0.054 | 0.061 |
| SmallFighter1 | 0.031 | 0.071 | **0.084** | 0.081 | 0.050 | 0.072 |
| FastBomber1 | **2.499** | 0.750 | 0.470 | 0.315 | 0.102 | 0.048 |
| Darkreach | **60.255** | 14.348 | 9.147 | 4.959 | 2.765 | 3.737 |

Two different shapes:

- **The six healthy airframes** hold 0.002–0.090 with no strong trend in step size, and their worst
  rung is **`oblique-dz` (2.5°, the `FineBankDeadzone` edge) for four of the six**. That is exactly
  what that card was built to catch — a servo flickering in and out of its own deadzone — and it is
  the first evidence the deadzone edge is measurably the worst small-step regime rather than merely
  the most suspected one. The effect is modest (dz is 5–35 % worse than its neighbours) but it is
  10–30× the replicate CV of 0.5–4 %.
- **`FastBomber1` and `Darkreach`** have a residual that is nearly **independent of the step size** —
  a fixed pointing error of roughly 0.9–1.2° and 20–40° respectively. Normalizing by the step is what
  makes their small-step numbers look astronomical; it is one absolute failure, not six.

### 3.2 The mirror-pair rule, and the down-step penalty

The diamond is `arm`(0, +R) → DR(+R, 0) → DL(0, −R) → UL(−R, 0) → UR(0, +R). The **exact geometric
mirrors are DR↔UL and DL↔UR** — equal magnitude, opposite sign in both axes. (DR↔DL is *not* a mirror
pair; it is two different steps.)

Ratio of `terminalOffDeg`, down-step ÷ its mirrored up-step. Replicate CV on these cells is
0.5–9.4 % on the ≥2° cards.

| airframe | card | DR/UL | DL/UR |
|---|---|---:|---:|
| Fighter1 | oblique-6 | 1.18 | 1.85 |
| Fighter1 | oblique-12 | **2.42** | **3.92** |
| Multirole1 | oblique-6 | 2.28 | 2.27 |
| Multirole1 | oblique-12 | 2.79 | **5.16** |
| SmallFighter1 | oblique-12 | 2.84 | **4.84** |
| trainer | oblique-12 | **4.71** | 3.94 |
| VTOLTrainer1 | oblique-12 | 4.18 | **6.73** |
| VTOLTrainer1 | oblique-below | 4.25 | 5.32 |
| EW1 | oblique-6 | **7.15** | **12.65** |
| EW1 | oblique-12 | 7.27 | **17.86** |
| EW1 | oblique-below | **11.83** | 6.89 |
| FastBomber1 | oblique-6 | **0.53** | **0.56** |
| FastBomber1 | oblique-12 | 0.93 | 2.01 |

**The finding: at matched step magnitude and mirrored geometry, moving the nose down costs 1.2–17.9×
the terminal error of moving it up.** Universal across all seven airframes that fly the card;
`FastBomber1` inverts it (0.31–0.63, up is worse). At 20–1000× the replicate CV this is by a wide
margin the largest systematic effect in the batch.

Three candidate explanations are **excluded by the batch's own variation**:

1. **Energy / dynamic pressure.** The down legs are segments 2–3 and the up legs 4–5, so they fly at
   different speeds — but `trainer` changes speed by **+1.9 m/s over the entire card** and still
   shows 3.94–4.71×, and `EW1` by +8.0 m/s and still shows 7.15–17.86×. A 3.7 % speed difference
   cannot produce a 4.7× error ratio.
2. **Terminal elevation (the v0.85 `elDn` belowness regime).** In `oblique-below`, `obDR6low` and
   `obUL6low` terminate at **the same** −20° elevation, and the ratio survives at 1.32–11.83 — in
   several cases *larger* than on `oblique-6` at the horizon. Moving the whole diamond 20° below the
   nose does not change the asymmetry, so belowness is not what it is keyed on. Consistently,
   `bSup` measures **0.000–0.06** on all four `oblique-below` legs: the v0.85 suppression barely runs.
3. **Error accumulation across the card.** Later segments are *better*, not worse, so residual carry-
   over is ruled out by sign.

The residual is **almost pure azimuth**: `Fighter1 obDR6` terminal `off` 0.336°, `azErr` +0.336°,
`elevErr` −0.005°. The elevation is fully nulled and the lag is all in heading.

**What covaries with it, and how far that goes.** `pEff` (the measured pitch-effectiveness estimator
that multiplies `pErrTerm`) is systematically lower on the down legs:

| airframe | card | leg | `pEff` | peak `\|outP\|` | terminal off |
|---|---|---|---:|---:|---:|
| trainer | oblique-12 | DR (down) | 0.542 | 0.198 | 0.388 |
| trainer | oblique-12 | UL (up) | **0.847** | **0.343** | **0.083** |
| EW1 | oblique-6 | DR (down) | 0.580 | 0.127 | 0.343 |
| EW1 | oblique-6 | UL (up) | **0.686** | **0.327** | **0.049** |
| Fighter1 | oblique-12 | DR (down) | 0.667 | 0.202 | 0.671 |
| Fighter1 | oblique-12 | UL (up) | **0.798** | **0.368** | **0.269** |

The law commands **1.7–2.6× less peak pitch stick on the down leg than on its exact mirror**, and
`pErrTerm` is directly multiplied by `pEff`.

**But this is not established as the mechanism, and the batch says why.** Within-card correlation of
`pEff` against the residual across the four legs flips sign with step size:

| airframe | 05 | 2 | dz | 6 | 12 | below |
|---|---:|---:|---:|---:|---:|---:|
| Fighter1 | 0.000 | **+0.538** | 0.000 | −0.379 | −0.761 | −0.602 |
| SmallFighter1 | 0.000 | **+0.547** | **+0.838** | −0.806 | −0.987 | −0.908 |
| trainer | 0.000 | **+0.518** | **+0.665** | −0.862 | −0.890 | −0.118 |
| VTOLTrainer1 | 0.000 | **+0.688** | **+0.711** | −0.996 | −0.924 | −0.714 |
| EW1 | 0.000 | 0.000 | 0.000 | −0.995 | −0.963 | −0.959 |

Strongly negative on the ≥6° cards, **strongly positive on 2° and dz**. A quantity whose association
with the error reverses sign inside the same family is a *correlate of demand magnitude*, not a
demonstrated cause. `pEff` is the best lead available and it is not proof.

**The one confound this batch cannot break is order.** The down legs are always segments 2–3 and the
up legs always 4–5; no card in the family reverses the sequence. Energy has been excluded as the
*content* of that order, but "the controller's filter states are 14 s old vs 30 s old" has not been.

> **The card that would settle it:** a mirrored-order oblique — `arm` at el **−R**, then UL, UR, DR,
> DL — identical geometry, reversed sequence. 8 replicates on 3 airframes (`Fighter1`, `trainer`,
> `EW1`) is ~10 minutes unattended. If the down legs are still worse when they run *last*, the effect
> is direction and the next step is instrumenting `pErrTerm`'s inputs. If the penalty follows the
> *position in the card* instead, the effect is filter warm-up and the fix is in `Forget`/`arm`
> duration, not in the pitch channel.

### 3.3 `FastBomber1` — a milder version of the same failure as `Darkreach`

Not a distinct third mode. Its signed terminal `elevErr` grows monotonically through the card
(0.61 → 1.32 → 1.96 → 3.47 on `oblique-6`): the nose sits progressively **low** and never catches up.
`pEff` median **0.472** (lowest of the seven), with **5.87 %** of rows in the `[0.15, 0.30)` band and
**1.47 %** below `PEffRevThresh`. It loses 418 m per card, the most of any airframe on the 4000 m
cards. `alphaLimiterStrength` is **0.20**, four times every other airframe's, and its FBW corner
speed is 200 m/s — the highest — so 250 m/s is only 1.25× corner for it. It is the airframe at which
the law starts to run out of pitch, and it inverts the mirror ratio because its *up* legs are the ones
it cannot complete.

---

## Q4 — do the known defects show?

### 4.1 #20 `PEffRevThresh` — **inert here; the plan's premise is wrong**

The code (`ChaseController.cs:1924`, `PEffRevThresh = 0.15f`, `effFloor = 0.3f`):

```csharp
if (!_collective) pErrTerm *= _pitchEff >= PEffRevThresh ? Mathf.Max(effFloor, _pitchEff) : _pitchEff;
```

The floor only changes anything when `_pitchEff ∈ [0.15, 0.30)`. Occupancy, per airframe, over all
196 624 fixed-wing scored rows:

| airframe | `<0.15` (no floor) | **`[0.15,0.30)` (floor branch)** | `≥0.30` (pEff itself) |
|---|---:|---:|---:|
| Fighter1 | 0.00 % | **0.00 %** | 100.00 % |
| Multirole1 | 0.00 % | 0.97 % | 99.03 % |
| SmallFighter1 | 0.00 % | **0.00 %** | 100.00 % |
| trainer | 0.00 % | **0.00 %** | 100.00 % |
| VTOLTrainer1 | 0.00 % | **0.00 %** | 100.00 % |
| EW1 | 0.00 % | **0.00 %** | 100.00 % |
| FastBomber1 | 1.47 % | 5.87 % | 92.66 % |
| Darkreach | **65.38 %** | 8.80 % | 25.82 % |
| **batch** | 8.36 % | **1.96 %** | 89.69 % |

**On five of eight airframes the floor branch runs on exactly zero rows**, and on the six that fly
the family well the expression reduces to `pErrTerm *= _pitchEff` unconditionally. `LAW-CHARACTERIZATION.md`
§7 schedules #20 as "the first law fix after the baseline… the oblique family is it." **The baseline
says it is not.** An A/B of #20 on any of these cards would return a confident null, on a branch that
provably never executed.

Where #20 *does* run is `Darkreach`, and there it runs in its most damaging form: on **65.4 %** of
rows `_pitchEff < 0.15`, so the law multiplies the pitch error term by a number near zero — it stops
commanding pitch. That is plausibly a *cause* of the Darkreach failure rather than a consequence, and
it is the only place in this batch where #20 has an observable effect at all.

### 4.2 #21 `lateralHold` rail — **cannot fire here; the pipeline is dormant, not railed**

Segments with `blendRailPct ≥ 90 %`:

| airframe | 05 | 2 | dz | 6 | 12 | below |
|---|---|---|---|---|---|---|
| all 7 healthy | 0/32 | 0/32 | 0/32 | 0/32 | 0/32 | 0/32 |
| Darkreach | 14/32 | 16/32 | 16/32 | 16/32 | 16/32 | 4/32 |

**0 of 1344 healthy-airframe segments.** Combined with §1.2 — `bWt` median 0.000 everywhere,
identically 0.000 on `oblique-05` and `oblique-2` — the state of the bank pipeline on this family is
*off*, not *stuck on*. #21 is a rail at the top of a channel that never opens here. It remains
A/B-able only on the sweep family, where R21 measured `blendRailPct` at 93–100 %.

`Darkreach` is the mirror image: `bWt` median **1.000** on `oblique-05` and `oblique-2`, i.e. the rail
is permanent and `eFine`'s weight is exactly zero for the whole capture.

### 4.3 #23 placement-tick reset — **does not reproduce, and the §6 prediction is card-specific**

`LAW-CHARACTERIZATION.md` §6 states this "will appear in every placed capture" as `rollRate ≈ −59`
and `leadDeg` 7–14° at `tSeg=0.000`. Measured over all 384:

| quantity at `tSeg=0.000` | median | p90 | p99 | max |
|---|---:|---:|---:|---:|
| \|`rollRate`\| | **0.725** | 4.97 | 40.37 | 57.51 |
| \|`leadDeg`\| | **0.140** | 18.56 | 205.88 | 308.56 |
| \|`headingRateFilt`\| | **0.210** | 28.57 | 316.77 | 474.69 |

- captures with \|`rollRate`\| > 50: **3 of 384**
- captures with \|`leadDeg`\| in the predicted 7–14° band: **0 of 384**
- captures with \|`leadDeg`\| > 1°: **48 of 384** — and **43 of those are Darkreach**, 5 `FastBomber1`

The defect is real; its *magnitude is set by how the previous card left the aircraft*. The predicted
signature came from a card whose preceding replicate ended in a ~79° banked sustained turn. These
cards end near wings-level, so the placement straddles almost no discontinuity. §6 should say "in
proportion to the bank the previous card ended in", not "in every placed capture".

**It still does not leak into the scored segments**, which was the operative claim:

| | at `tSeg=0.000` | at end of the 6 s `arm` |
|---|---:|---:|
| median \|`rollRate`\| | 0.725 | **0.006** |
| median \|`leadDeg`\| | 0.140 | **0.020** |
| median \|`off`\| | — | **0.030** |

The scored segment starts from a 0.03° error and a dead-still controller. Confirmed harmless.

### 4.4 `authBank > 1.0` (the R27 oddity) — **does not reproduce**

`authBank > 1.0` on **0 of 1536** scored segments. `bankClampActivePct` is 0.0 % on every healthy
airframe, so no capture in this batch even reaches the 72° clamp, let alone overruns it. Whatever
produced the R27 reading was specific to that batch's regime (the sweep family, at the clamp) and is
absent from the oblique family. It should be re-checked on the next sweep batch rather than closed.

### 4.5 `turnRateDemandRatio ≥ 1` — is the card asking the impossible?

**No, for seven of eight airframes: 0 segments.** The oblique cards are within reach everywhere the
airframe is flying.

`Darkreach` hits ≥ 1 on **26 of 192** segments (10/32 on `oblique-12`). That is *not* evidence that
the card is at fault: the demand ratio is computed from the demanded turn rate, which is large only
because Darkreach's pointing error has already grown to 20–40°. It is a symptom of the failure in
§4.6, not its cause.

### 4.6 `Darkreach` — the entry condition and the law are both wrong

`Darkreach` passed the v0.92 pre-spawn gate correctly: 250 m/s is 0.896 × its 279.2 m/s Vmax, inside
the 0.95 bound. The gate checks whether the speed can be *written*. It does not check whether it can
be *held*.

| measurement | value |
|---|---|
| speed over one 38 s card | 250.0 → **201.8** m/s (throttle pinned 0.70) |
| altitude over one 38 s card | 4000 → **2206** m |
| pre-placement speed across the batch | median **201.8**, min **98.6** m/s (stall speed 66.7) |
| pre-placement altitude across the batch | median **2201**, min **784** m |
| captures peaking over 90° pointing error | **6 of 48**, max **179.0°** |
| `_pitchEff < 0.15` | **65.4 %** of rows — the law zeroes its own pitch term |
| `bWt ≥ 0.999` | ~50 % of rows — bank pipeline permanently railed (#21) |
| `iPitch`, `iYaw` | both pinned at the **0.12 cap** — integrators saturated |
| `authorityUsedFrac` | 0.71–0.89 |

**Its replicates are not noisy, they are bimodal.** `obDR6` terminal error across the eight:

```
run  32:   0.515      run 224:   4.294
run  80: 143.222      run 272:   4.272
run 128:   4.244      run 320:   4.287
run 176:   4.264      run 368:   4.301
```

Seven cluster to ±0.03; one departs completely. The 150–210 % CV in the summary is that single
outlier, not measurement noise — and the first replicate (run 32, the only one placed from a fresh
spawn rather than from a degraded previous card) is eight times better than the rest, which is the
same first-placement advantage Gate B (R23) saw.

**Verdict: both.** The capture measures a decelerating descent, not tracking, so no Darkreach metric
in this batch is a law score. But every one of the law's own failure branches is also firing, so
fixing the entry condition is necessary and probably not sufficient. R29's `0.95 × corner` entry
(≈171 m/s for a fighter) is the right correction; Darkreach needs re-flying under it before any claim
about the law on heavy airframes is made.

### 4.7 Backlog #38 (card altitude budget) — confirmed, with numbers

Median altitude change per card:

| airframe | 05 | 2 | dz | 6 | 12 | **below** |
|---|---:|---:|---:|---:|---:|---:|
| Fighter1 | −271 | −244 | −220 | −163 | +3 | **−4066** |
| Multirole1 | −208 | −179 | −151 | −72 | +210 | **−4317** |
| SmallFighter1 | −242 | −213 | −182 | −103 | +195 | **−4164** |
| trainer | −148 | −122 | −98 | −53 | +44 | **−3577** |
| VTOLTrainer1 | −109 | −83 | −54 | +12 | +208 | **−3634** |
| EW1 | −106 | −79 | −59 | +8 | +197 | **−3331** |
| FastBomber1 | −434 | −416 | −419 | −391 | −192 | **−3897** |
| Darkreach | **−1788** | **−1780** | **−1807** | **−1888** | **−1897** | −3436 |

`oblique-below` costs 3331–4317 m. It declares `startAlt: 6000` and survives on that margin with
1683–2669 m to spare. From the 4000 m the other five cards use, **`Multirole1` would finish 317 m
below sea level.** And `Darkreach` loses 1788–1898 m on the *flat* cards, reaching 784 m worst case
from a 4000 m start. #38 is not theoretical.

---

## Ranked fix list

1. **The down-step penalty (§3.2).** Biggest measured effect in the batch: 1.2–17.9× on the terminal
   error, universal, 20–1000× the noise floor, unattributed. Everything else in the healthy-airframe
   band is a few percent.
   *Next action is not a fix, it is one card:* the **mirrored-order oblique** described in §3.2, to
   separate step direction from card position. ~10 minutes unattended, 3 airframes, no code change.
2. **A corner-referenced entry speed for every card (§2.4, §4.6).** A flat 250 m/s makes the card a
   different test on each airframe — +92 m/s of acceleration on one, −48 m/s of deceleration on
   another — and it is why `Darkreach` produced 48 unreadable captures. Already implemented in
   v0.93.0; the value here is confirming it was the right call, and that Batch 1 should be re-flown
   under it before its middle band is ranked any more finely.
3. **A card altitude-budget check at preflight (#38).** Measured, not projected: `oblique-below` from
   4000 m would put three airframes below sea level. Same shape as v0.92's speed gate.
4. **`_pitchEff` on `Darkreach` (§4.1).** 65.4 % of rows below `PEffRevThresh`, i.e. the law
   deliberately withholds pitch authority for two-thirds of the flight, on the airframe that most
   needs it. Whether the estimator is wrong or the plant genuinely is reversed cannot be told from
   this batch — but it is the one place #20's machinery is doing measurable work.

**Deprioritized on this evidence:** #20 and #21 as scheduled in `LAW-CHARACTERIZATION.md` §4 Batch 4
(E4, E5). Both are inert on the oblique family — 0.00 % floor-branch occupancy and 0/1344 rails — so
those two A/Bs would return nulls that mean nothing. Move them behind the sweep family, or behind
whatever card is found to make the bank pipeline live.

---

## Which single defect to fix first, and the card that would show it

**Fix the down-step penalty. Show it on `oblique-12` — but run the mirrored-order card first.**

`oblique-12` is the right discriminator: it has the largest mirror ratios among the healthy airframes
(2.42–7.27 DR/UL, 3.92–17.86 DL/UR), it is unsaturated (`authorityUsedFrac` 0.15–0.25), its replicate
CV is 1.4–7.6 %, and it is the only card where the bank pipeline reaches its rail at all — so a fix
that touches the roll/pitch handover will show there and nowhere else in this family. A pass is the
DR/UL and DL/UR ratios collapsing toward 1.0 with the *up* legs unchanged; a regression is the up legs
degrading to meet the down legs, which would be a gain reduction dressed as symmetry.

The mirrored-order card comes first because a fix aimed at the wrong half of the DIRECTION vs ORDER
question is a fix aimed at nothing.

---

## What would falsify this analysis

- **The down-step penalty is order, not direction.** The mirrored-order card returns the same
  down-worse ratio when the down legs run last → §3.2's exclusion of energy and elevation stands, but
  the cause is filter warm-up inside the card, and the fix moves to `arm` duration / `Forget`.
- **`A` is the wrong normalizer for heavy airframes.** `flightscore`'s `omega_avail` comes from
  `maxPitchAngularVel` and the g-limit; if those sidecar values are template junk for `FastBomber1`
  or `Darkreach` the way `emptyWeight` is (`AIRFRAMES.md` trap 3), the bottom two ranks are measuring
  the probe, not the law. Checkable against a hand-flown capture on either airframe.
- **The `oblique-dz` result is a deadband artefact of the analysis, not the law.** `scorecard`'s
  0.02 analysis deadband and the card's 2.5° step sit close together; re-scoring dz with a smaller
  deadband should leave the dz-is-worst ordering intact.
- **`Darkreach` flies the card fine at a corner-referenced entry.** If R29's 0.95 × corner entry
  produces a Darkreach `A` inside the middle band, then §4.6's "both" verdict collapses to "entry
  condition only" and the ONE-LAW spread in §2.1 shrinks from 0.237 to ~0.14.

---

## Reproducing

```bash
cd "<game>/BepInEx"
python <repo>/debugtests/compare-runs.py --summary mouseaim-rec-v0.92.1-R28-*.csv
for a in Fighter1 Multirole1 SmallFighter1 trainer VTOLTrainer1 EW1 FastBomber1 Darkreach; do
  python <repo>/debugtests/flightscore.py mouseaim-rec-v0.92.1-R28-*-$a-*.csv
done
python <repo>/debugtests/analyze-wobble.py --digest \
  $(ls mouseaim-rec-v0.92.1-R28-d8-Darkreach-*-oblique-6-*.csv | head -2)
```

Note the argument-length limit: 384 absolute paths exceed the Windows command line, so run from the
capture directory with relative globs. Per-airframe `scorecard`/`flightscore` aggregates in this
document were produced by importing the two tools as modules and calling `score_run` /`score_file`
per file — no metric here is a reimplementation.
