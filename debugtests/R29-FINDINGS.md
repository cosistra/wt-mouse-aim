# R29 — the oblique family on 10 airframes at a corner-relative entry, v0.93.0

**Batch 1 of `LAW-CHARACTERIZATION.md` §4, re-flown under a different entry condition.** 10 airframes
× 6 cards × 8 replicates = **480 planned, 441 captured**, 268 113 rows, one unattended run of 30 min
(20:00:04 → 20:30:19). Source:
`<game>/BepInEx/mouseaim-rec-v0.93.0-R29-d{1..10}-<airframe>-{01..441}-<card>-*.csv` (+ `.airframe.json`
sidecars), `mouseaim-anomalies-v0.93.0-R29-20260730-195904.log`, and — **this time it was copied out** —
`LogOutput-R29.log`.

| | |
|---|---|
| airframes | `Fighter1` `Multirole1` `SmallFighter1` `trainer` `VTOLTrainer1` `CAS1` `COIN` `EW1` `FastBomber1` `Darkreach` |
| cards | `oblique-05-c` `oblique-2-c` `oblique-dz-c` `oblique-6-c` `oblique-12-c` `oblique-below-c` |
| entry | `startSpeedCorner: 0.95`, 4000 m (`oblique-below-c` 6000 m), throttle pinned 0.70 |
| A/B arm | none (baseline) — `arm=` absent from every `# config` line, which is correct |
| refused pre-spawn | **none**. All 10 lanes spawned; the v0.92 gate passed every one |
| short cells | `Darkreach` only — 9 of 48. Pilot killed at t=415.9 s (§Q5.2) |

Read with `compare-runs.py --summary`, `scorecard.py`, `flightscore.py`, `analyze-wobble.py --digest`.
All four `--selftest` clean at the commit analysed; `check-architecture.py` clean. Nothing in this
document recomputes a metric the tools already produce: the aggregation imports `scorecard.score_run`
and `flightscore.score_file` as modules and groups their output, and every table below was
spot-checked against `compare-runs.py --summary` (Fighter1 `obDR6` term 0.137 here / 0.137 there).

---

## The entry condition is NOT what the label says. Read this before any other section.

Every capture's `# entry` line and the launch log say `0.95x corner (per airframe)`. **The field it
resolved against is `AircraftParameters.cornerSpeed` — the AI pilot's corner speed — not
`fbwCornerSpeed`, which is what the flight-control system uses** (`AIRFRAMES.md` trap 6). The two
differ by up to 1.8× on this roster. Placed speeds, verified against the `[drone] #N spawned` lines:

| lane | airframe | placed m/s | AI corner | fbwCorner | **placed ÷ fbwCorner** | ÷ Vstall |
|---:|---|---:|---:|---:|---:|---:|
| 1 | Fighter1 | 171 | 180 | 160 | **1.069** | 2.37 |
| 2 | Multirole1 | 171 | 180 | 160 | **1.069** | 2.57 |
| 3 | SmallFighter1 | 171 | 180 | 155 | **1.103** | 2.28 |
| 4 | trainer | 152 | 160 | 130 | **1.169** | 3.04 |
| 5 | VTOLTrainer1 | 152 | 160 | 160 | **0.950** | 3.04 |
| 6 | CAS1 | 190 | 200 | 160 | **1.188** | 3.11 |
| 7 | COIN | 86 | 90 | 110 | **0.777** | 2.20 |
| 8 | EW1 | 114 | 120 | 130 | **0.878** | 3.42 |
| 9 | FastBomber1 | 171 | 180 | 200 | **0.855** | 2.68 |
| 10 | Darkreach | 171 | 180 | 100 | **1.710** | 2.57 |

So relative to the corner speed the *law* keys off, R29 entered at **0.78×–1.71×** — a 2.2× spread,
not a constant. **R29 is not "every airframe at the same aerodynamic state."** It is a batch in which
entry speed varies 86–190 m/s and is a *measured covariate*, which is the only reason Q2 below can say
anything at all. Every claim in this document treats it that way.

> Two corrections to the tasking note, both verified against `LogOutput-R29.log:678` and its siblings:
> the lane order is the card's own `airframe` list (Fighter1, Multirole1, SmallFighter1, trainer,
> VTOLTrainer1, CAS1, COIN, EW1, FastBomber1, Darkreach), so **`Multirole1`/`SmallFighter1` flew 171,
> and `trainer`/`VTOLTrainer1` flew 152** — the reverse of the mapping in the brief.

---

## Verdict

1. **The airframe ranking survives the change of entry condition. Spearman ρ = +0.929 (n = 8,
   permutation p = 0.0022); no airframe moves more than one rank.** Fighter1 is first in both,
   Darkreach last in both. Whatever the ranking is measuring, it is not the entry speed — because the
   entry speed changed by −79 to +40 m/s per airframe and the order did not.
2. **The spread halved, and it did not collapse to the noise floor.** Over the 8 airframes common to
   both batches: R28 0.237 A → R29 **0.1455** A, against a replicate noise floor of 0.0051 (**29×
   noise**, was 70×). Excluding Darkreach as a separate failure mode: 0.146 → **0.082** (16× noise).
   So roughly **40 % of R28's healthy-airframe spread was entry condition and 60 % is not**. The law
   still does not fly every airframe alike.
3. **Entry speed explains almost none of the residual spread.** ρ(A, placed speed) = **+0.188**
   (p = 0.61) at n = 10. The top correlate is `aircraftGLimit` at **+0.872** (p = 0.0023), *stronger*
   than R28's +0.810 despite the widened roster. Entry speed is now near-orthogonal to gLimit
   (ρ = +0.104), so those two are genuinely separable — and it is gLimit that survives.
4. **The down-step penalty reproduces, and R29 adds the thing R28 could not see: it scales with step
   magnitude.** Geometric-mean down/up terminal-error ratio by card: 1.04 (0.5°), 1.18 (2°), 1.06
   (2.5°), 1.39 (6°), **3.33 (12°)**, 1.82 (6° below-horizon). Per-airframe ρ(log ratio, step
   magnitude) is **+0.8 to +1.0 on 8 of 9** airframes. It does *not* track entry speed (ρ −0.30 to
   +0.14 within any card).
5. **`FastBomber1` is no longer a failure; `Darkreach` still is, but for a completely different
   reason than in R28.** FastBomber1 went 0.559 → 0.662, joining the middle band, with `pEff` median
   0.472 → 1.000 and its floor-branch occupancy 5.87 % → **0.00 %**. Darkreach flew its first six
   cards *cleanly* (terminal error median 0.33°, zero rails) and then suffered an unrecovered bank
   excursion on its seventh, departed on its ninth at 26.9 g / −87° AoA, and was destroyed.
6. **The 8 → 12 drone decision: R28's agreed stop signal did not fire, and it is the wrong
   instrument.** Zero rows at 33.3 ms — zero rows anywhere in [30, 40] ms. But rows over 20 ms went
   from 16 (R28, 8 drones) to **243** (R29, 10 drones) over a comparable wall-clock session, and
   distinct stall events from **2 to 23**. Going to 12 on the strength of "no 33.3 ms rows" would be
   reading a censored gauge as a clean bill of health.

---

## Q5 — batch hygiene

### 5.1 Sound

| check | result |
|---|---|
| captures | **441**. 9 airframes × 6 cards × 8 = 432 complete cells; `Darkreach` 9 (see 5.2) |
| cell matrix | every non-Darkreach (airframe, card) cell is exactly 8. No cell over |
| `# stop` present | 441/441 |
| `# stop` reason | 440 `card '<name>' complete`; **1 `abort: aircraft gone`** (Darkreach run 90) |
| duration | 38.0 s on 440; 32.4 s on the aborted one. 608–610 samples (519 aborted) |
| segments | 5 per capture on all 441. `arm` 5.7–5.9 s, steps 7.7–7.9 s |
| truncated segments | **1** — `darkreach oblique-2-c obUR2`, flagged `[1 TRUNC]` by `compare-runs.py` and excluded by it |
| unrecognised tags | **0** — 25 distinct tags, all resolved by `scorecard.py` |
| RAILED warnings | **6** of 1764 scored segments (0.34 %) — **all 6 on `Darkreach`**, all in its final two captures |
| SLACK warnings | 0 (structurally impossible here: `SLACK_TYPES` is `sustained_turn`/`alpha_hold`, and no oblique segment is either) |
| columns | **64** on all 441, header/row lockstep intact |
| `ctrlReset=1` | **441/441** |
| `# entry` provenance | 441/441. `snapBackM` 0.0 on the 10 first-placements, 3.2–10.9 km on the rest |
| `# override` | absent on all 441 (these cards pin nothing) |
| `# cfg` mid-run edits | **0** — no knob moved during the batch |
| `arm=` / `armKnob=` | absent on all 441 — correct, a baseline has no A/B arm |
| replicate drift | none. Median A by replicate index (r0…r7) varies < 0.006 on every airframe checked |
| anomaly log | 2054 lines: 1020 `over-roll`, 1002 `overshoot`, **30 `overstress`**, **2 `persistent-miss`** |

The last row is the only new anomaly vocabulary since R28 (which had `overshoot`/`over-roll` only).
**All 32 of the `overstress` and `persistent-miss` lines belong to `Darkreach` runs 80 and 90** — they
are the departure in §Q4.1, not a batch-wide phenomenon.

`scorecard.py` excluded exactly 441 segments — one `arm` per capture, by design.

### 5.2 The 39 missing captures: one airframe, one cause, on the record

All 39 are `Darkreach`. It flew 9 captures and was then lost:

```
[drone] #10 despawned (pilot killed). 9 live.                     LogOutput-R29.log:2685
[card]  ABORT (aircraft gone) — 'oblique-2-c' segment obUR2 at 2.4s.
```

Preceded immediately by three `overstress` anomalies on that same capture:

```
t=413.883 g=14.5/4 aoa=-97.6/10 for 0.5s off=148.8 out P/R/Y=(-0.17, 1.00, 1.00) spd=88
t=414.883 g=19.2/4 aoa=-72.1/10 for 0.5s off= 43.9 out P/R/Y=(-0.46,-1.00, 1.00) spd=84
t=415.883 g=26.9/4 aoa=-87.1/10 for 0.5s off= 65.4 out P/R/Y=(-0.43,-1.00, 0.38) spd=81
```

The aircraft was tumbling — AoA −72° to −98°, 81–88 m/s, roll and yaw stick both railed — and broke up
or hit terrain. **It was not damaged beforehand.** The `.airframe.json` sidecar is written at the start
of each capture, and across all nine Darkreach captures `massKg` (105 409.2), `aeroPartCount` (35),
`wingAreaTotal` (383.00) and `dragAreaTotal` (6.342) are byte-identical. Whatever went wrong is in
flight, not in the airframe. §Q4.1 has the sequence.

This is the harness behaving correctly — the abort is logged, the reason is in the CSV's `# stop`
line, the drone despawned, and the other nine lanes were unaffected (their capture counts are exact).
**Copying `LogOutput.log` out with the captures — R28's one open action item — is what made this
diagnosable.** Keep doing it.

### 5.3 `frameMs` — the 8 → 12 question, answered differently than R28 expected

| | R28 (8 drones) | **R29 (10 drones)** |
|---|---:|---:|
| rows | 233 519 | **268 113** |
| distinct values | 9 | **38** |
| rows exactly 16.7 ms | 99.67 % | **99.53 %** |
| p50 / p90 / p99 | 16.70 / 16.70 / 16.70 | 16.70 / 16.70 / 16.70 |
| min / max | 16.60 / 579.40 | **13.10** / **687.40** |
| rows > 20 ms | 16 (0.0069 %) | **243 (0.0906 %)** |
| rows > 50 ms | 16 | **162** |
| **rows at 33.3 ms** | **0** | **0** |
| rows anywhere in [30, 40] ms | — | **0** |
| distinct stall events | **2** | **23** |

Each stall value lands on **exactly 9 rows** (occasionally 18, when two events round to the same
tenth) — nine lanes writing one process-wide `TestDrone.FrameDt` sample, which is the expected shape
and confirms the column is still wired to the real frame clock. The independent count from the log is
16 `[drone] frame hitch` lines (its threshold is 50 ms), against 162/9 = 18 CSV events over 50 ms.
Consistent.

**The agreed stop signal did not fire, and should be retired.** R28 proposed "treat any non-zero count
of 33.3 ms rows as the stop signal for going wider." There are zero such rows — and zero rows anywhere
between 30 and 40 ms. The frame time on this machine does not quantise to 2 × vsync when it misses; it
either holds 16.7 or jumps to 41.5, 51.7, 72.6, 336.8, 687.4. **A rule keyed on 33.3 ms is a rule that
can never fire here**, so its not firing is not evidence.

What the data *does* say, comparing like with like (both sessions ~30 min of concurrent
complex-physics aircraft):

- **13× the over-20 ms row rate and 11× the stall-event count for a 25 % increase in drone count.**
  That is not a linear cost, and it is the first measurement in the corpus with a gradient in it.
- The stalls are still rare in absolute terms — 243 poisoned rows in 268 113 (0.09 %), spread over 23
  events × 9 lanes, i.e. roughly one row per affected (capture, segment) cell. **No segment needs
  dropping**, and the stagger is still doing its job (the events land on different tags per lane).
- 8 rows read **13.1 ms**, below the vsync period — so the cap is not perfectly rigid either.

**Recommendation: do not go to 12 on this evidence.** Replace the stop signal with one that can
actually fire: `rows > 20 ms` as a rate, and `distinct stall events per 30 min`. If a headroom number
is wanted, R28's suggestion still stands — one vsync-off batch makes the column continuous. Do not run
it as a science batch.

---

## Q1 — does the airframe ranking survive a change of entry condition?

Metric and method identical to R28 §2.1: **`flightscore` A**, median across replicates per tag, then
median over the **20 scored tags where A is defined for every airframe** — the same 20 as R28 (the
four `oblique-05` tags are undefined for 9 of 10 airframes because 100 % of samples sit inside the
1.0° ON_TARGET cone). Railed segments excluded (6, all Darkreach). Tag-matched, so regime is matched.

### 1.1 The R29 ranking

| rank | airframe | median A | min tag | max tag | replicate sd (median) | entry m/s | n/tag |
|---:|---|---:|---:|---:|---:|---:|---:|
| 1 | **Fighter1** (FS-12 Revoker) | **0.7341** | 0.6882 | 0.8535 | 0.0077 | 171 | 8 |
| 2 | trainer (T/A-30 Compass) | 0.6855 | 0.6271 | 0.7986 | 0.0068 | 152 | 8 |
| 3 | SmallFighter1 (FS-20 Vortex) | 0.6765 | 0.5600 | 0.7494 | 0.0035 | 171 | 8 |
| 4 | VTOLTrainer1 (VT-7 Vagrant) | 0.6761 | 0.5694 | 0.7640 | 0.0026 | 152 | 8 |
| 5 | Multirole1 (KR-67 Ifrit) | 0.6662 | 0.5551 | 0.7791 | 0.0026 | 171 | 8 |
| 6 | **CAS1** (A-19 Brawler) — new | 0.6657 | 0.5907 | 0.8033 | 0.0029 | 190 | 8 |
| 7 | FastBomber1 (Alkyon AB-4) | 0.6621 | 0.6115 | 0.7681 | **0.0273** | 171 | 8 |
| 8 | EW1 (EW-25 Medusa) | 0.6519 | 0.5916 | 0.7782 | 0.0035 | 114 | 8 |
| 9 | **COIN** (CI-22 Cricket) — new | 0.6469 | 0.5859 | 0.7592 | 0.0054 | 86 | 8 |
| 10 | **Darkreach** (SFB-81) | 0.5886 | 0.5342 | 0.6833 | **n/a** | 171 | **1–2** |

**Noise floor: median replicate sd 0.0051 A** over 180 (airframe × tag) cells with n ≥ 3. p90 = 0.0221,
max = 0.0654.

Two cells must be read with a warning attached:

- **`Darkreach`, n = 1–2 per tag.** No replicate sd exists, and its six railed segments — the worst
  ones — were *excluded*, which biases its 0.5886 **upward**. Treat it as an upper bound.
- **`FastBomber1`, replicate sd 0.0273**, 5–10× every other airframe. This is not one outlier
  replicate: the per-tag sd is 0.009–0.043 across all 20 tags. It is genuinely the least repeatable
  airframe in the batch, which its rank-7 position does not convey. §Q2.4 has a candidate cause.

### 1.2 Rank order: preserved

| airframe | R28 A | rank | R29 A | rank | ΔA |
|---|---:|---:|---:|---:|---:|
| Fighter1 | 0.705 | 1 | 0.7341 | **1** | +0.029 |
| SmallFighter1 | 0.667 | 2 | 0.6765 | 3 | +0.010 |
| trainer | 0.654 | 3 | 0.6855 | 2 | +0.032 |
| Multirole1 | 0.642 | 4 | 0.6662 | 5 | +0.024 |
| VTOLTrainer1 | 0.639 | 5 | 0.6761 | 4 | +0.037 |
| EW1 | 0.623 | 6 | 0.6519 | 7 | +0.029 |
| FastBomber1 | 0.559 | 7 | 0.6621 | 6 | **+0.103** |
| Darkreach | 0.468 | 8 | 0.5886 | **8** | **+0.121** |

**Spearman ρ = +0.9286, exact permutation p = 0.00223 (90/40320).** Excluding Darkreach, ρ = +0.8929
(n = 7). Every airframe moved at most one rank, and each of the four swaps (2↔3, 4↔5, 6↔7) is between
airframes separated by less than 0.002–0.016 A — i.e. **all four rank swaps are inside or near the
noise floor**. The order is preserved to the resolution the instrument has.

**Every airframe improved.** The mean improvement is +0.048 A, and the two worst improved most. That
is a level shift, not a reordering — consistent with the whole roster being flown at a lower dynamic
pressure and therefore closer to where the law's gains suit it.

### 1.3 The spread: halved, did not collapse

| pool | R28 spread | R29 spread | R28 ÷ noise | R29 ÷ noise |
|---|---:|---:|---:|---:|
| the 8 common airframes | 0.237 | **0.1455** | 70× | **29×** |
| the 7 common, ex-Darkreach | 0.146 | **0.0823** | 43× | **16×** |
| all 10 R29 airframes | — | 0.1455 | — | 29× |
| all 9 R29 ex-Darkreach | — | 0.0872 | — | 17× |

**This is the discriminator the batch was flown to settle, and the answer is "both, roughly 40/60".**
If the entry condition explained the R28 spread, the spread would sit at the noise floor now. It
doesn't — it is still 16–29× noise. If the entry condition explained nothing, the spread would be
unchanged. It isn't — it fell by 44 %.

So: **a substantial minority of what R28 measured as "the law flies airframes differently" was the law
being handed a different flight condition on each airframe. The majority is a property of the
law–airframe interaction and survives.**

The honest structure is now **two groups, not ten ranks**:

- **`Fighter1` alone at 0.734**, 0.049 clear of second place — 9.6× the noise floor, the only
  separation in the top half that is unambiguous.
- **An eight-airframe band, 0.647–0.686** (ranks 2–9), spanning 0.039 A. Within it, consecutive gaps
  are 0.009, 0.0004, 0.010, 0.0005, 0.004, 0.010, 0.005 — **five of the seven are at or under 2× the
  noise floor.** `Multirole1` (the incumbent) sits at rank 5 of 10, mid-band, indistinguishable from
  `VTOLTrainer1` (ΔA = 0.0099, 1.9× noise) and from `CAS1` (ΔA = 0.0005, **0.1× noise**).
- **`Darkreach` alone at ≤ 0.589**, and see §Q4.1 before believing that number.

### 1.4 Where the two new airframes land

Both land **in the middle of the band, not at either extreme**, and both fly the cards cleanly.

- **`CAS1` (A-19 Brawler), rank 6 at 0.6657** — statistically tied with `Multirole1` (ΔA = 0.0005).
  This is the airframe the v0.92 gate *refused* at R28's flat 250 m/s. At 190 m/s it is an ordinary
  member of the band.
- **`COIN` (CI-22 Cricket), rank 9 at 0.6469** — last of the nine healthy, but only 0.0050 below
  `EW1` (1.0× noise, i.e. not separable) and 0.019 below the band's midpoint.

Neither is a new failure mode, and neither is at the top. The roster widening did not find a worse
airframe than the ones already known; it found two more members of the same band.

### 1.5 Caveat: what `A` normalizes by, and why COIN's rank needs an asterisk

`flightscore` A divides achieved closure rate by `omega_avail`, the rate the airframe could physically
have produced. Median `omega_avail` on `oblique-6-c`:

| airframe | Fighter1 | trainer | SmallFighter1 | VTOLTrainer1 | Multirole1 | CAS1 | COIN | EW1 | FastBomber1 | Darkreach |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| deg/s | 20.5 | 24.2 | 19.7 | 20.9 | 19.0 | 12.7 | **32.1** | 17.2 | 12.1 | 12.1 |

A 2.7× range. `COIN` at 86 m/s and 6.5 g has the *highest* available turn rate in the batch and
therefore the harshest normalizer; `FastBomber1` and `CAS1` have the gentlest. This is the metric
working as designed — it is what makes cross-airframe comparison possible at all — but it means the
band's internal order rests on `maxPitchAngularVel` and `gLimitPositive` being accurate sidecar
readings. R28 flagged this as a falsifier and it remains one.

---

## Q2 — is the spread explained by entry speed rather than by airframe?

**No.** Entry speed now varies 86–190 m/s (2.2× in absolute terms, 0.78–1.71× relative to
`fbwCornerSpeed`) and correlates with A at **ρ = +0.188, p = 0.61**.

### 2.1 The covariate table

| airframe | med A | entry m/s | ÷fbwCnr | ÷Vstall | mass kg | gLim | wing m² | drag m² | Δv/card | Δalt/card |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Fighter1 | 0.7341 | 171 | 1.07 | 2.37 | 13 573 | 9.0 | 49.9 | 2.21 | **+107** | −297 |
| trainer | 0.6855 | 152 | 1.17 | 3.04 | 9 806 | 9.0 | 57.4 | 1.08 | +65 | −179 |
| SmallFighter1 | 0.6765 | 171 | 1.10 | 2.28 | 13 690 | 9.0 | 55.7 | 1.66 | +122 | −263 |
| VTOLTrainer1 | 0.6761 | 152 | 0.95 | 3.04 | 11 180 | 8.0 | 41.4 | 1.72 | +88 | −173 |
| Multirole1 | 0.6662 | 171 | 1.07 | 2.57 | 25 560 | 9.0 | 123.0 | 1.86 | **+142** | −223 |
| CAS1 | 0.6657 | 190 | 1.19 | 3.11 | 20 280 | 7.5 | 96.0 | 2.43 | **−19** | −230 |
| FastBomber1 | 0.6621 | 171 | 0.86 | 2.68 | 57 620 | 5.0 | 100–135 | 2.3–4.2 | +51 | −308 |
| EW1 | 0.6519 | 114 | 0.88 | 3.42 | 24 580 | 6.0 | 97.4 | 2.70 | +92 | −157 |
| COIN | 0.6469 | 86 | 0.78 | 2.20 | 4 854 | 6.5 | 48.8 | 1.61 | +21 | −163 |
| Darkreach | 0.5886 | 171 | **1.71** | 2.57 | 105 400 | 5.0 | 383.0 | 6.34 | +51 | −327 |

### 2.2 Correlations

Spearman ρ of median A against each property, p by 20 000 permutations. Sidecar values are **medians
over that airframe's own captures**, not first-capture snapshots (see §2.4).

| n = 10 (all) | | | n = 9 (ex-Darkreach) | | |
|---:|---|---|---:|---|---|
| **+0.872** | `aircraftGLimit` | **p = 0.0023** | **+0.844** | `aircraftGLimit` | **p = 0.0073** |
| +0.598 | `alphaLimiter` (deg) | p = 0.077 | **+0.829** | `maxRollSpeed` | **p = 0.0085** |
| −0.515 | `dragAreaTotal` | p = 0.13 | +0.636 | entry ÷ fbwCorner | p = 0.071 |
| +0.515 | Δv over the card | p = 0.14 | +0.522 | `alphaLimiter` | p = 0.15 |
| +0.492 | `infoMaxSpeed` | p = 0.15 | +0.467 | Δv over the card | p = 0.21 |
| −0.479 | `wingAreaTotal` | p = 0.16 | +0.385 | `infoMaxSpeed` | p = 0.30 |
| +0.459 | `maxRollSpeed` | p = 0.18 | +0.367 | **entry speed** | p = 0.33 |
| −0.442 | `massKg` | p = 0.20 | −0.333 | `dragAreaTotal` | p = 0.38 |
| +0.414 | `maxPitchAngularVel` | p = 0.23 | −0.233 | `massKg` | p = 0.56 |
| **+0.188** | **entry speed** | **p = 0.61** | +0.183 | wing loading | p = 0.64 |
| **+0.188** | **entry ÷ fbwCorner** | **p = 0.61** | −0.117 | entry ÷ Vstall | p = 0.77 |
| −0.103 | wing loading | p = 0.79 | | | |
| **−0.049** | **entry ÷ Vstall** | **p = 0.90** | | | |
| +0.044 | `alphaLimiterStrength` | p = 0.90 | | | |

`T:W` cannot be computed for the roster: **`CAS1` and `COIN` carry `maxThrustN = null`** (both are
propeller/propfan aircraft with no jet thrust field). R28's +0.714 for T:W therefore has no n = 10
successor, and dropping the two new airframes to compute it would defeat the point of flying them.

### 2.3 What is now separable, and what is still confounded

Collinearity among the candidates, R28's 8-airframe pool vs R29's 10:

| pair | ρ at n = 8 | ρ at n = 10 | verdict |
|---|---:|---:|---|
| gLimit × entry speed | +0.066 | **+0.104** | **orthogonal — separable** |
| gLimit × massKg | −0.651 | **−0.496** | partially broken |
| gLimit × dragArea | −0.805 | **−0.696** | partially broken |
| gLimit × wingArea | −0.587 | −0.489 | partially broken |
| massKg × wingArea | +0.857 | +0.903 | **still fully confounded** |
| massKg × dragArea | — | +0.869 | **still fully confounded** |
| gLimit × maxRollSpeed | +0.473 | +0.498 | unchanged |

**Separable now, and the finding:**

- **Entry speed vs everything else.** It is orthogonal to gLimit (+0.10) and it explains nothing
  (ρ = +0.19, p = 0.61). This is the single clean identification the batch buys, and it is the answer
  to Q2. Note it is *not* orthogonal to mass (+0.545) — heavier airframes happened to get higher
  entry speeds — so "entry speed doesn't matter" is established against gLimit, not against mass.
- **Entry ÷ Vstall is dead.** ρ = −0.049. Whatever the law is sensitive to, it is not stall margin.
- **`gLimit` is not a proxy for mass any more.** Adding `CAS1` (20 t, g 7.5) and `COIN` (4.9 t, g 6.5)
  broke the fighters-are-light-and-high-g alignment: `CAS1` at 20 t outranks `EW1` at 24.6 t *and*
  `COIN` at 4.9 t. gLimit's ρ went **up** (+0.810 → +0.872) while mass's went **down** (−0.690 →
  −0.442). At n = 8 those two were the same variable wearing different labels; at n = 10 they part
  company and gLimit is the one that tracks the score.

**Still confounded, do not read a cause out of these:**

- **mass / wingArea / dragArea remain a single cluster** (pairwise ρ 0.72–0.90). Nothing in R29
  separates them.
- **`maxRollSpeed`** jumps to +0.829 (p = 0.0085) once Darkreach is dropped — Darkreach has
  `maxRollSpeed` 300, the joint highest, and the worst score, so it single-handedly suppresses that
  correlation at n = 10. A correlate that depends on which single airframe is in the pool is not
  established at n = 9.
- **`alphaLimiter`** (the AoA ceiling in degrees) is new at +0.598 but is collinear with gLimit.

**Identifiability at n = 10, stated plainly:** one property (`aircraftGLimit`) reaches p < 0.01 with
one degree of freedom to spare, and one candidate (entry speed) is cleanly excluded. Everything else
in the table is a label on a cluster of ~4 correlated airframe properties that 10 points cannot
resolve. **`gLimit` leading is now a genuinely stronger result than in R28** — because it strengthened
while its main confounders weakened — but "high-g airframes score better" is still an ordinal
observation, not a mechanism. The mechanism would have to come from a card that varies g-limit
*within* an airframe, which no card can do.

### 2.4 Two confounds the batch does not control

**(a) The card still does not hold speed, and at a lower entry it is WORSE.** Throttle is pinned at
0.70 and the airframe accelerates freely:

| | R28 (250 m/s entry) | R29 (corner-relative entry) |
|---|---|---|
| Fighter1 | 250 → 302 (+52, **+21 %**) | 171 → **278** (+107, **+62 %**) |
| Multirole1 | 250 → 342 (+92, +37 %) | 171 → **313** (+142, **+83 %**) |
| SmallFighter1 | 250 → 317 (+67, +27 %) | 171 → 293 (+122, **+71 %**) |
| EW1 | 250 → 258 (+8, +3 %) | 114 → 207 (+92, **+81 %**) |
| CAS1 | (refused) | 190 → 172 (**−19**, −10 %) |

**This directly contradicts R28's ranked-fix-list item 2**, which recommended the corner-relative entry
on the grounds that a flat 250 "makes the card a different test on each airframe" and called v0.93.0
"the right change." It *is* a better-conditioned starting point — it let `CAS1` and `COIN` fly at all,
and it halved the spread — but it did **not** put the airframes on a common footing, because nothing
holds them there. Every fast jet now traverses a *wider* speed range during the card than it did at
250 m/s, and by the last segment `Multirole1` is at 313 m/s while `CAS1` is at 172. **The entry
condition was fixed; the card's energy behaviour was not, and the two were never the same problem.**
A card that pins speed (throttle authority to a target, or a shorter card) is the outstanding item.

**(b) `FastBomber1`'s wing is variable-geometry, and the sidecar samples whatever position it is in.**
`aeroPartCount` is a constant 35 across all 40 of its captures, but `wingAreaTotal` ranges
**100.2–135.1 m²** and `dragAreaTotal` **2.30–4.16 m²** — a 35 % swing in wing area with no part
change. (R28 recorded 103 m² / 2.34 m², i.e. near the swept end; R29's median is 131 m² / 3.86 m².)
The variation is **between cards, not within one**: inside `oblique-6-c` the range is 130.5–130.6 m².
So it does not confound the tag-matched ranking, and within-airframe ρ(wingArea, A) is +0.15 (n = 40),
+0.02 within a card — no effect. But it does mean:

- `FastBomber1`'s row in any covariate table is a **snapshot of one wing position**, not a property.
  Two batches can legitimately disagree about it by 35 %.
- It is a plausible partial explanation for its 5–10× replicate sd (§1.1), since the wing sweep is
  presumably scheduling on airspeed and the card's speed trace is what varies.
- No other airframe shows this. `CAS1`'s 96.00/96.07 m² is float noise; the other eight are exact.

---

## Q3 — does the down-step penalty persist?

**Yes.** Method identical to R28 §3.2: the diamond is `arm`(0, +R) → DR(+R, 0) → DL(0, −R) →
UL(−R, 0) → UR(0, +R); the **exact geometric mirrors are DR↔UL and DL↔UR**. Ratio of median
`terminalOffDeg`, down-step ÷ its mirrored up-step.

### 3.1 The ratios, all 10 airframes, all 6 cards

| airframe | 05 (0.5°) | 2 (2°) | dz (2.5°) | 6 (6°) | **12 (12°)** | below (6°) |
|---|---:|---:|---:|---:|---:|---:|
| | DR/UL · DL/UR | | | | | |
| Fighter1 | 0.33 · 2.00 | 0.45 · 1.21 | 0.48 · 1.19 | 0.57 · 1.22 | **0.92 · 1.68** | 0.95 · 1.46 |
| Multirole1 | 1.47 · 0.45 | 0.88 · 1.21 | 0.92 · 1.15 | 2.02 · 1.56 | **3.24 · 4.81** | 2.79 · 2.30 |
| SmallFighter1 | 1.59 · 1.57 | 0.81 · 1.27 | 0.80 · 1.33 | 1.15 · 1.29 | **1.76 · 2.40** | 1.22 · 1.80 |
| trainer | n/a · n/a | 0.89 · 3.90 | 0.96 · 1.48 | 1.09 · 1.79 | **1.59 · 2.13** | 1.46 · 1.50 |
| VTOLTrainer1 | 0.25 · n/a | 0.91 · 1.45 | 0.96 · 1.36 | 1.31 · 1.42 | **3.53 · 3.44** | 2.00 · 1.54 |
| CAS1 | n/a · n/a | 1.12 · 1.40 | 1.17 · 1.39 | 1.63 · 1.35 | **2.71 · 2.90** | 5.35 · 2.81 |
| COIN | 1.15 · 1.20 | 1.63 · 1.02 | 1.40 · 0.98 | 1.96 · 1.14 | **4.07 · 3.47** | 1.30 · 2.08 |
| EW1 | 1.24 · 0.90 | 0.73 · 0.90 | 0.81 · 1.03 | 1.03 · 1.36 | **16.79 · 17.15** | 1.98 · 2.89 |
| FastBomber1 | 1.74 · 2.17 | 1.37 · 3.25 | 1.13 · 1.19 | 1.22 · 3.79 | **3.39 · 5.46** | 0.73 · 2.16 |
| *Darkreach (n=1)* | *1.25 · 1.06* | *0.83 · 0.81* | *0.73 · 2.47* | *1.36 · 0.38* | *15.97 · 23.13* | *0.61 · 0.32* |

Aggregated over the nine airframes with n = 8 (Darkreach excluded — n = 1, no replicate variance):

| card | step mag | geomean ratio | min | max | cells > 1 | median replicate CV |
|---|---:|---:|---:|---:|---:|---:|
| oblique-05-c | 0.5° | **1.04** | 0.25 | 2.17 | 9/13 | 60 % |
| oblique-2-c | 2.0° | **1.18** | 0.45 | 3.90 | 11/18 | 10 % |
| oblique-dz-c | 2.5° | **1.06** | 0.48 | 1.48 | 11/18 | 4.5 % |
| oblique-6-c | 6.0° | **1.39** | 0.57 | 3.79 | **17/18** | 2.5 % |
| **oblique-12-c** | **12.0°** | **3.33** | 0.92 | 17.15 | **17/18** | 2.6 % |
| oblique-below-c | 6.0° | **1.82** | 0.73 | 5.35 | 16/18 | 4.0 % |

### 3.2 It reproduces — but the *size* is not stable across batches, and the *shape* is new

**The effect is present and unambiguous at ≥ 6°.** At 12° every one of the ten airframes is > 1 on
both mirrors except `Fighter1`'s DR/UL at 0.92 (i.e. ≈ 1). At 2.5 % replicate CV, a ratio of 3.33 is
~90× the noise. R28's core claim — *at matched magnitude and mirrored geometry, moving the nose down
costs more terminal error than moving it up* — reproduces at entry speeds 45–95 m/s away from R28's.

**The magnitudes are not reproducible, and this matters.** Matched (airframe, card) cells:

| airframe · card | R28 DR/UL → R29 | R28 DL/UR → R29 |
|---|---|---|
| Fighter1 · 6 | 1.18 → **0.57** | 1.85 → 1.22 |
| Fighter1 · 12 | 2.42 → **0.92** | 3.92 → 1.68 |
| trainer · 12 | 4.71 → **1.59** | 3.94 → 2.13 |
| SmallFighter1 · 12 | 2.84 → 1.76 | 4.84 → 2.40 |
| EW1 · 6 | 7.15 → **1.03** | 12.65 → **1.36** |
| EW1 · 12 | 7.27 → **16.79** | 17.86 → 17.15 |
| FastBomber1 · 6 | 0.53 → **1.22** | 0.56 → **3.79** |
| FastBomber1 · 12 | 0.93 → **3.39** | 2.01 → 5.46 |

Geometric mean over the 26 matched cells: **R28 3.49 → R29 2.37**. Individual cells move by up to 7×
in either direction, and two airframes flip sign of the effect (`FastBomber1` inverted in R28 and does
not now; `Fighter1`'s DR/UL inverted in R29 and did not then). **The down/up ratio is a
batch-condition-dependent number, not an airframe constant** — so quoting "1.2–17.9×" as a property of
the law, as R28's headline does, over-states what a single batch establishes. The *sign* is robust;
the *size* is not.

### 3.3 What it tracks: magnitude, not entry speed

**Magnitude — yes, strongly.** ρ(log of the paired ratio, step magnitude) across the four horizon
cards (2°, 2.5°, 6°, 12°), per airframe:

| CAS1 | EW1 | Fighter1 | SmallFighter1 | COIN | FastBomber1 | Multirole1 | VTOLTrainer1 | trainer |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| +1.00 | +1.00 | +1.00 | +1.00 | +0.80 | +0.80 | +0.80 | +0.80 | −0.20 |

Eight of nine at +0.8 or better. **This is new information R28 did not report** — its §3.2 table
listed only the 6°/12°/below cards, so the near-unity ratios at 0.5–2.5° were not visible. The
asymmetry is essentially absent below the `FineAngle = 6` threshold and grows sharply through it.

**Entry speed — no.** Within each card, across the nine airframes:

| card | ρ(log ratio, entry speed) | ρ(…, entry ÷ fbwCorner) | ρ(…, gLimit) |
|---|---:|---:|---:|
| 2 | −0.070 | −0.126 | −0.383 |
| dz | +0.061 | +0.251 | −0.226 |
| 6 | +0.140 | −0.226 | −0.339 |
| **12** | **−0.297** | **−0.653** | **−0.740** |
| below | +0.070 | +0.192 | −0.139 |

Nothing at ≤ 6°. At 12° the ratio correlates *negatively* with gLimit (−0.740) — high-g airframes show
less asymmetry — and with entry ÷ fbwCorner (−0.653), but those two are themselves correlated and
n = 9 cannot separate them.

### 3.4 What is unchanged from R28, and what is still not identifiable

- **The residual is almost pure azimuth**, exactly as R28 found. Terminal row of `obDR12`:
  `Fighter1` off 0.26 / azErr +0.26 / elevErr −0.01; `CAS1` 0.52 / +0.48 / +0.21; `COIN` 0.19 / +0.18
  / +0.05. Elevation is nulled; the lag is in heading.
- **Terminal elevation is still not the key.** `oblique-below-c` puts the whole diamond 20° low, and
  its geomean ratio (1.82) is *higher* than `oblique-6-c`'s (1.39) at identical magnitude — same
  direction as R28.
- **The order confound is intact and untouched.** Down legs are always segments 2–3, up legs always
  4–5. R29 was not designed to break it and does not. **Nothing here supports a causal claim about
  step direction**; the R30 `oblique-12-fwd`/`-rev` pair remains the experiment that decides it, and
  §3.3's magnitude scaling makes `oblique-12` the right card to run it on.
- One thing R29 *does* narrow: because the ratio is near 1.0 at ≤ 2.5° and 3.3 at 12° on the same
  five-segment card structure, whatever produces it is **demand-magnitude gated**, not a fixed
  per-segment offset. A pure "segment 2–3 filters are colder than segment 4–5 filters" mechanism would
  have to be magnitude-dependent too to fit.

---

## Q4 — did the two problem airframes improve?

### 4.1 `Darkreach` — it flies the card now, and then it dies

**The first six captures are clean.** Runs 10–60 (one of each card, in order) completed with zero
railed segments, median terminal error 0.33°, and peak pointing error 0.6–17.1° (the 17.1 is
`oblique-below-c`, whose own step total is 16°).

| run | card | spd | alt | peak off | pEff med | RAILED | stop |
|---:|---|---|---|---:|---:|---:|---|
| 10 | oblique-05-c | 171 → 230 | 4000 → 3673 | **0.59** | 1.000 | 0 | complete |
| 20 | oblique-12-c | 171 → 214 | 4000 → 3785 | 12.76 | 0.397 | 0 | complete |
| 30 | oblique-2-c | 171 → 230 | 4000 → 3694 | 2.13 | 1.000 | 0 | complete |
| 40 | oblique-6-c | 171 → 222 | 4000 → 3744 | 6.17 | 0.945 | 0 | complete |
| 50 | oblique-below-c | 171 → **304** | 6000 → 2880 | 17.06 | 0.463 | 0 | complete |
| 60 | oblique-dz-c | 171 → 227 | 4000 → 3703 | 3.87 | 0.540 | 0 | complete |
| 70 | oblique-05-c | 171 → **164** | 4000 → 3514 | **10.62** | 1.000 | 0 | complete |
| 80 | oblique-12-c | 171 → 154 | 4000 → **2015** | **166.31** | 0.410 | 2 | complete |
| 90 | oblique-2-c | 171 → **80** | 4000 → **940** | **169.05** | 0.065 | 4 | **abort: aircraft gone** |

Against R28's Darkreach at 250 m/s, the improvement on the first six is total:

| | R28 (250 m/s) | R29 runs 10–60 (171 m/s) |
|---|---|---|
| speed over one card | 250 → **202** (−48) | 171 → **214–304** (+43 to +133) |
| altitude over one card | 4000 → **2206** (−1794) | 4000 → 3673–3785 (−215 to −327) |
| `_pitchEff < 0.15` | **65.4 %** of rows | **0.00 %** of rows |
| `bWt ≥ 0.999` | ~50 % of rows | median `bWt` **0.000** |
| RAILED segments | 82 of 192 | **0** of 24 |
| `authorityUsedFrac` | 0.71–0.89 | 0.19–0.36 |
| peak pointing error | up to 179° | 0.6–17.1° |
| `iPitch`/`iYaw` | pinned at the 0.12 cap | peak 0.017 (14 % of cap) |

**So: yes, at 171 m/s `Darkreach` flies the card.** The deceleration, the 1800 m descent, the
saturated integrators, the permanently railed bank pipeline and the near-zero `pEff` — every one of
R28 §4.6's symptoms is gone. R28's falsifier "`Darkreach` flies the card fine at a corner-referenced
entry" is **confirmed for the first six replicates**.

**Then it breaks, and not from where R28 was looking.** The `analyze-wobble --digest` of run 70 (the
0.5° card, the *smallest* in the family) shows the failure onset precisely:

```
308.7-310.1 FINE  1.4s  off 1.5->5.8  bank -0.3->17.8  tgtBank 0.0->55.6  outR 0.149->0.509
310.1-332.2 TURN 22.1s  off 6.1->6.0[6.0..10.6]  bank 18.7->28.0[..48.5]  tgtBank 57.2->34.3[17.9..63.0]
                        yawWeak 0.82->0.97   outR 0.708->0.111
```

**The roll-to-align channel demanded 55–63° of bank against a 1.3° azimuth error on a 0.5° card**, the
aircraft rolled to 48°, and it then spent 22 seconds in a TURN it could not exit, sitting at 6–10.6°
of error. That excursion is what starts the sequence: run 80 loses 2000 m, run 90 departs at
−72° to −98° AoA and 26.9 g with roll and yaw stick railed, and the pilot is killed.

Three things bound the interpretation:

- **It is not damage.** Sidecar mass / part count / wing area / drag area are byte-identical across all
  nine captures (§5.2).
- **It is not the entry condition failing to be set.** Every `# entry` line reads
  `v=…->171.0 alt=…->4000.0 ctrlReset=1`, and the placement audit is clean.
- **n = 1.** One card produced the excursion. Nothing here separates "this is reproducible on
  `Darkreach`" from "one bad tick that happened to be unrecoverable on a 105-tonne aircraft."

What is worth noting is that `Darkreach` is the extreme of exactly the covariate the corner-speed bug
created: **entry ÷ `fbwCornerSpeed` = 1.71, by far the highest in the batch** (next is CAS1 at 1.19),
because its `fbwCornerSpeed` is 100 while the resolver used its AI corner of 180. It also runs
`assist = 0` and `fbw gLimit = 4`. Whether the bank runaway is a function of that ratio is untestable
at n = 1 — **and it is the reason to re-fly `Darkreach` alone, at a genuine 0.95 × `fbwCornerSpeed`
(95 m/s), before any claim is made about the law on heavy airframes.** The `bankDemandExcessDeg`
column says no other airframe demanded any excess bank at all on the 0.5° or 2° cards (0.00 on all
nine); Darkreach reached 4.00° on `oblique-2-c`.

### 4.2 `CAS1` and `COIN` — both fly cleanly; neither is at a limit

Both are new to the corpus and both were unflyable at R28's flat 250 m/s (Vmax 205.6 and 141.7).

| | CAS1 @ 190 m/s | COIN @ 86 m/s |
|---|---|---|
| captures | 48/48 complete | 48/48 complete |
| RAILED segments | **0** of 192 | **0** of 192 |
| `bankClampActivePct` | 0.0 % on all 6 cards | 0.0 % on all 6 cards |
| `turnRateCapActivePct` | 0.0 %, except 2.2 % on `-12-c` | 0.0 %, except 3.3 % on `-12-c` |
| `aoaLimiterActivePct` | **0.0 %** everywhere | **0.0 %** everywhere |
| `authorityUsedFrac` | 0.19 median, 0.33 max | 0.31 median, **0.49 max** |
| `_pitchEff < 0.15` | **0.00 %** of rows | **0.00 %** of rows |
| min speed over all rows | 165.1 (Vstall 61.1) | **73.7** (Vstall 38.9) |
| peak AoA | 6.2° (limiter 15°) | 4.8° (limiter 10°) |
| peak pointing error | 16.6° (card total 16°) | 16.3° |
| altitude loss per card | −230 m | −163 m |
| overshoot | median 0.000°, max 0.16° | median 0.000°, max 0.13° |

**Neither is at a different kind of limit.** No clamp, cap, rail or AoA gate fires on either. `COIN`'s
`authorityUsedFrac` of 0.31 (max 0.49) is the highest in the batch and is worth watching — it has the
least margin of the nine healthy airframes — but 0.49 is half the available authority and it is
`authAoa`-dominated, i.e. it is using AoA, not stick or bank.

The one thing that distinguishes them: **`CAS1` is the only airframe in the batch that *decelerates*
over the card** (190 → 172 m/s at the same pinned 0.70 throttle that accelerates `Multirole1` by
142 m/s). That is not a failure — its terminal residuals are mid-band — but it means `CAS1` and
`Multirole1` are flying opposite energy trajectories inside what is nominally one test, which is
§2.4(a) again.

### 4.3 The AoA limiter fired on a healthy airframe — a first, and it contradicts R28

R28 §1.2 recorded `aoaLimiterActivePct` at **0.0 % on all 7 healthy airframes**, and stated the
limiter had "still never fired on a healthy airframe, in any capture ever taken."

**In R29 it fires on `trainer`, on `oblique-12-c`, segment `obUL12`, on 8 of 8 replicates**, mean
11.9 % of samples, max 12.5 %. Every other (airframe, card, tag) cell in the batch is 0.0 % except
`Darkreach`'s (which is in departure). It is a small, perfectly repeatable activation on exactly one
segment of one card on one airframe — the up-left 12° step on the airframe with the lowest entry speed
of the 9-g group (152 m/s) and `alphaLimiter = 27°`.

This is not a defect; it is the first datum the AoA machinery has ever produced outside a failing
airframe. It should be recorded in `LAW-CHARACTERIZATION.md` as such, and it makes `trainer` ·
`oblique-12-c` the only currently-known card/airframe pair on which an AoA-gate A/B could return a
non-null.

### 4.4 What is still dormant

Unchanged from R28, across all 10 airframes and 6 cards:

| mechanism | measured | verdict |
|---|---|---|
| bank clamp | `bankClampActivePct` **0.0 %** on all 9 healthy | never reached |
| roll-to-align pipeline | `bWt` **median 0.000** on every airframe, every card | dormant, not railed |
| `_pitchEff` floor branch (#20) | `[0.15, 0.30)` occupancy **0.00 % on all 10 airframes** | **inert — worse than R28** |
| `lateralHold` rail (#21) | `blendRailPct ≥ 90 %` on **0 of 1740** healthy segments | cannot fire here |
| below-nose suppression | dormant on `oblique-below-c` as in R28 | dormant |

**#20 is now inert on the entire roster, including the two airframes that carried it in R28.**
`FastBomber1` went from 5.87 % floor-branch occupancy and 1.47 % below-threshold to **0.00 % / 0.00 %**;
`Darkreach` from 8.80 % / 65.38 % to **0.00 % / 0.00 %**. The lower entry speed removed the only
occupancy the branch had. R28 deprioritized the #20 and #21 A/Bs on the oblique family; R29 closes the
question — **there is no fixed-wing card in the shipped grid on which #20's branch executes at all.**

The fine integrator remains live but is less exercised than in R28: peak `iPitch` 0.013–0.022
(11–18 % of the 0.12 cap) against R28's 0.018–0.047 (15–39 %). Still far above R21's ±0.001, so the
unlock holds, but the lower dynamic pressure reduced it.

---

## Ranked fix list

1. **Fix `startSpeedCorner` to resolve against `fbwCornerSpeed` (`AIRFRAMES.md` trap 6).** Everything
   in this document is qualified by the fact that the field the resolver read is the AI pilot's corner
   speed. The batch is still valid — the speeds are real and recorded — but the *label* on it is
   wrong, the entry states span 0.78–1.71× the corner the law actually keys off, and the one airframe
   at the extreme of that ratio is the one that departed. One-line fix in
   `ScenarioPlayer.ResolveStartSpeed`; the pre-spawn gate (`TestDrone.EntrySpeedFlyable`) should be
   re-checked against the new numbers before the next roster batch, since several lanes will move.
2. **Give the card control of speed, not just of the entry speed.** §2.4(a): at the corner-relative
   entry the fast jets now traverse a *wider* speed range than they did at a flat 250 (+62 % to +83 %
   over one 38 s card), while `CAS1` decelerates. R28 recommended the corner-relative entry as the fix
   for "the card is a different test on each airframe"; it is not, because nothing holds the airframe
   at the entry condition after the placement. This is the largest remaining uncontrolled variable in
   the ranking.
3. **Re-fly `Darkreach` alone at a genuine 0.95 × `fbwCornerSpeed` (95 m/s), 8 replicates per card.**
   Its six clean captures say the R28 failure was the entry condition; its seventh says something else
   is waiting at 1.71× corner. n = 1 cannot tell which. This is ~20 minutes of one lane and it is the
   only open question about the heavy end of the roster.
4. **Retire the 33.3 ms stop signal; adopt a rate.** §5.3. The current rule provably cannot fire on
   this machine, and the measurement it was meant to protect against went up 13× between R28 and R29.
5. **Record `FastBomber1`'s variable-geometry wing in `AIRFRAMES.md`** as a seventh trap: `wingAreaTotal`
   and `dragAreaTotal` are not constants for it (100–135 m², 2.3–4.2 m², same `aeroPartCount`), so any
   cross-batch comparison that uses them for that airframe is comparing wing positions.

**Deprioritized on this evidence:** #20 (`PEffRevThresh`) and #21 (`lateralHold` rail) as scheduled in
`LAW-CHARACTERIZATION.md` §4 Batch 4. R28 found them inert on 5–7 of 8 airframes; R29 finds #20's
branch executing on **0.00 % of rows on all 10**, and #21's rail on **0 of 1740** healthy segments.
Neither can be A/B-ed anywhere in the current fixed-wing grid.

---

## What would falsify this analysis

- **The ranking is an artefact of `omega_avail`.** §1.5: the normalizer spans 2.7× across the roster
  and comes from `maxPitchAngularVel` / `gLimitPositive`. If either is template junk for a given
  airframe (`AIRFRAMES.md` trap 3 shows `emptyWeight` is), that airframe's rank is measuring the
  probe. `COIN` (32.1 deg/s, the harshest normalizer, rank 9) and `FastBomber1`/`Darkreach` (12.1,
  the gentlest) are the ones to check against a hand-flown capture.
- **`gLimit` is still a label.** ρ = +0.872 at n = 10 with mass/wing/drag still 0.72–0.90 collinear.
  Adding two more airframes that break the remaining cluster — a light low-g or a heavy high-g — would
  either confirm it or dissolve it. There is no such airframe in the fixed-wing roster, so this may be
  unresolvable with the current game content.
- **The down-step penalty is order, not direction.** Unchanged from R28 and untouched by R29. The
  R30 `oblique-12-fwd`/`-rev` pair decides it. §3.3 adds one prediction to test against: whatever the
  mechanism is, it must be **magnitude-gated**, since the ratio is ~1.0 at ≤ 2.5° and 3.3 at 12°.
- **`Darkreach`'s excursion is reproducible.** If a re-fly at 95 m/s produces it again, the bank
  runaway is a law defect at high entry-to-corner ratio and the airframe ranking's bottom entry is
  measuring it. If the re-fly is clean for 48 captures, R29's Darkreach number is one unlucky card and
  its rank should be re-derived from the clean data.
- **The spread halving is a level shift, not a compression.** Every airframe improved by +0.010 to
  +0.121 and the spread fell 44 %. If the improvement is uniform in *some other* transform of A, the
  "40 % was entry condition" split is arithmetic on the wrong scale. Checkable by re-scoring both
  batches with `--cone 0.2`, which changes what counts as scored.

---

## Reproducing

```bash
cd "<game>/BepInEx"
python <repo>/debugtests/compare-runs.py --summary mouseaim-rec-v0.93.0-R29-*.csv
for a in Fighter1 Multirole1 SmallFighter1 trainer VTOLTrainer1 CAS1 COIN EW1 FastBomber1 Darkreach; do
  python <repo>/debugtests/flightscore.py mouseaim-rec-v0.93.0-R29-*-$a-*.csv
done
python <repo>/debugtests/analyze-wobble.py --digest \
  mouseaim-rec-v0.93.0-R29-d10-Darkreach-60-oblique-dz-c-*.csv \
  mouseaim-rec-v0.93.0-R29-d10-Darkreach-70-oblique-05-c-*.csv
grep -E "\[drone\]" LogOutput-R29.log | grep -v "frame hitch"
```

441 absolute paths exceed the Windows command line — run from the capture directory with relative
globs. Per-airframe aggregates in this document were produced by importing `scorecard` and
`flightscore` as modules and calling `score_run` / `score_file` per file; no metric here is a
reimplementation. Spearman ρ and its permutation p-values are computed in the analysis script only
(neither tool provides them), with the exact 8! permutation used at n = 8 and 20 000 sampled
permutations at n = 9–10.
