# R40 — metric repair: `bankClampActivePct`, the wobble detector, `authorityUsedFrac`

Three proven-defective metrics in `debugtests/scorecard.py`, plus the general invariant behind two of
them. Everything below is a **read-only re-score** of the whole corpus (2,366 captures, 7,837
non-excluded segments) against the edited scorer; `debugtests/captures.db` has **not** been mutated —
see [Landing this](#landing-this).

No `.cs` file was touched. Two recorder-side follow-ups are listed at the end.

---

## Summary

| | before | after |
|---|---|---|
| `bankClampActivePct` | `\|targetBank\| >= maxBank` — a quantity no shipped law reads | `\|bankTR\| >= maxBank` — the demand the clamp acts on |
| segments where it moves > 5 pp | — | **2,155 of 7,834 (27.5%)**, range −100.0 to +96.9 pp |
| `railed` verdict flips | — | **17 (all 0 → 1)**, 0 the other way |
| `wobbleEpisodes*` | 318 corpus episodes, ~all entry transients | **5** — settled-window only, ≥ 6 crossings |
| `wobbleFreqHz*` | crossing count / duration; half of them the detector's floor | autocorrelation + DFT cross-check, amplitude-independent, **NULL** without evidence |
| `wobbleFreqHzAzErr` populated | 62 segments | **476** (436 new, 22 withdrawn) |
| `wobbleCoherence*` | — | new: the evidence beside the number |
| `authorityUsedFrac`, `authBank`, `authAoa`, `authStick` | 4 metrics | **deleted** |
| SLACK flag | 8 fires in corpus history, all one cell | **deleted** |
| dead columns | scored as 0.0 | withdrawn per capture + a `DEAD COLUMN` warning |

Selftests: `scorecard.py --selftest`, `compare-runs.py --selftest`, `index-captures.py --selftest`,
`analyze-wobble.py --selftest`, `flightscore.py --selftest`, `check-architecture.py` — all green.

---

## Defect 1 — `bankClampActivePct`

### What it actually read

The brief said `targetBank` "stopped being written in v0.96". That is **not** what happened, and the
correction matters because the error runs in **both** directions. There is **one writer**
(`ChaseController.cs:1455`), unconditional, no branch and no second code path — what makes it look
like two is that **one formula has three regimes**:

```
targetBank = Clamp(Lerp(linBank, bankTR, bankBlend), ±MaxBankAngle)         :1455
linBank    = deadbanded(azErr) · hdgConf · FineBankGain·(1 + BankAuthGain·assist)  :1057-1064
bankBlend  = YawAssistEnabled ? yawWeak·(1−bigTurn) : 0                     :1456
assist     = yawWeak·(1−bigTurn)·YawAssistStrength                          :1051
azDz       = FineBankDeadzone·(1−assist)                                    :1057
hdgConf    = |horizontal(t.forward)| = cos(nose pitch)                      :1007, :1059
```

Verified by reconstruction, not read off the source: rebuilding `targetBank` from the recorded
`azErr / bigTurn / yawWeak / bankTR / bankBlend` columns reproduces it to a **median 0.000°** over
446k corpus rows (R33/R35/R37).

**The `hdgConf` factor and its predicted failure mode — tested and confirmed.** `hdgConf` is *not* a
recorded column, so an offline reconstruction that omits it should degrade specifically on the
climbing and diving cards, not randomly. Solving the formula for `hdgConf` on 47k unclamped R28/R29
rows, grouped by card:

| card | n | solved `hdgConf` | median residual without it | median flight path |
|---|---|---|---|---|
| `oblique-6-c` | 2907 | 0.9992 | 0.029° | −2.1° |
| `oblique-12-c` | 10867 | 0.9964 | 0.038° | −1.9° |
| `oblique-6` | 5081 | 0.9946 | 0.043° | −3.7° |
| `oblique-12` | 11538 | 0.9947 | 0.045° | −1.9° |
| `oblique-05` | 2594 | 0.9735 | 0.360° | −16.4° |
| `oblique-below-c` | 3567 | **0.9399** | 0.265° | −21.1° |
| `oblique-below` | 5587 | **0.9404** | 0.358° | −22.1° |
| `oblique-dz` | 2516 | 0.9648 | 0.950° | −19.5° |
| `oblique-2` | 2083 | 0.9545 | **1.673°** | −21.9° |

Monotone along the belowness axis, exactly as predicted, and the magnitude checks out: at a −21…−22°
flight path the nose pitch is ≈ −20°, and `cos(20°) = 0.940` against a solved 0.9399–0.9404. Nothing
else is missing.

So `targetBank` is the **removed Legacy law's** bank target — proportional to azimuth error,
deprojected by nose pitch, blended toward `bankTR` only when the rudder is measured weak.
`ApplyEvolvedLegacy`, the only fixed-wing law since v0.60, has never read it: it computes
`tBankE = Clamp(bankTR, ±MaxBank)` and flies that (`:1827`); v0.96 merely deleted the two dead
parameters from the signature.

`|targetBank| == MaxBank` therefore means **"azErr exceeded MaxBank/bankGain"**, not "the clamp
discarded turn demand". Measured, both directions:

| direction | why | measured |
|---|---|---|
| **under-reads** | on a sustained turn `bigTurn → 1` zeroes the blend and `azErr → 0` collapses `linBank` | R39 `Fighter1·turn360rtl` **0.0%** vs `bankTR` 30.8% and mean\|bank\| 68.0 of 72; R27 `FastBomber1·turn360` **5.2% → 97.7%**; R28 `Darkreach·obUR12` **12.5% → 81.0%** |
| **over-reads** | a large azimuth step on a yaw-weak airframe drives `bankGain` to 3.0·(1+5.0·0.7) = **13.5**, so 5.4° of `azErr` already saturates | R28 `Darkreach·obUR6low` **100.0% → 0.0%**; R29 `Darkreach·obUR2` **42.3% → 0.0%**; the parallel STOL batch (`R40-stol.md` H3) measured 70.7% vs a `bankTR` at 4.6% |

**The over-read is one variable, not a coincidence of three.** `bankBlend`, `assist` and `azDz` all
key off the *same* `yawWeak·(1−bigTurn)`: the weakness that blends `bankTR` **in** is simultaneously
what inflates the gain 4.5× and collapses the deadband. That is why the over-read appears abruptly
rather than ramping — there is one knee, not three.

**A third regime exists and has never been flown.** With `YawAssistEnabled` off, `bankBlend ≡ 0` and
`targetBank ≡ Clamp(linBank)` — a genuinely different formula, not an extreme of this one, carrying
*zero* turn-demand information. Swept: **`yawAssist=1` on all 2,366 captures**, so nothing in the
corpus is in this regime and no existing finding is affected by it. Check that field on the
`# config` line before concluding anything about a batch that looks anomalous.

### Is it salvageable?

**No — and knowing the full formula is what settles it, rather than leaving it open.**

- In regime 1 (`bankBlend → 0`, big turn or strong rudder) and regime 3 (yaw assist off),
  `targetBank` is `Clamp(linBank)`: a pure azimuth-error rail with no turn-demand content to recover.
- Only regime 2 carries any, and it is diluted by `(1−bankBlend)·linBank`. Inverting it back to the
  demand requires `hdgConf`, which is not recorded — it would need a nose-pitch estimator, and the
  table above shows that is worth up to 1.67° on exactly the cards where it matters.
- `bankTR` **is** that demand, exactly, already in the CSV, needing no reconstruction.

So salvage would cost a pitch estimator and buy nothing. `targetBank` is a well-defined signal
answering a question nobody asks — *azimuth-error saturation of the removed Legacy bank servo*. The
repoint stands.

`tBankE` was considered and rejected for the reason given: it matches `bankTR` sample-for-sample on
the R39 turn legs, but it comes from `_tBankFlown`, which is post-`BankSlewRate` and post-B2 settle
injection (`:1847`), so during a roll-in it reads the slew rate rather than the clamp. `bankTR` is
the demand the clamp acts on and is the signal `bankDemandExcessDeg` already used — the two are now
consistent. The wall comes from the capture's own `# config maxBank=` (`Cfg.MaxBankAngle`, 72 by
default) — it was never hardcoded and still isn't.

### Re-score

7,834 segments have the metric both before and after (3 lose it: `bankTR` dead or absent).

- mean Δ **+3.88 pp**, median +1.56, range **−100.0 … +96.9**
- **2,155 (27.5%) move more than 5 pp**; 152 move up > 20 pp, 38 down > 20 pp
- on the 90% RAILED threshold: **77 cross up, 23 cross down**
- **`railed` verdict: 17 segments flip 0 → 1; none flips 1 → 0.** (The other 60 up-crossers and all
  23 down-crossers were already railed by `blendRailPct` / `aoaAboveCeilingPct`, so the composite
  verdict does not move.)

Mean Δ by segment type: `sustained_turn` **+9.70** (n=362), `az_step` +9.35 (n=125), `astern_wrap`
**−8.23** (n=12), `reversal` +4.16, `oblique_step` +3.72 (n=6830), `alpha_hold` +1.98,
`micro_step`/`alpha_step` +0.00.

The 17 flips: 15 `sustained_turn`, 1 `reversal`, 1 R39 `turn360rtl`. By batch — R11 ×2, R14 ×1,
R24 ×1, R26 ×4, **R27 ×8** (`FastBomber1·fixedwing-sweep·turn360`, every replicate, 5.2% → 97.7%),
R39 ×1 (`Darkreach·e2-rel-turn-lead·turn360rtl`, 73.2% → 91.3%).

The R21 headline is **strengthened, not invalidated**: `Multirole1·turn360` 96.98% → **99.40%** (and
R22/R23/R25 96.88% → 99.38%). The "bank clamp active on 97% while g sat at 5.4 of 9" finding stands.

---

## The general invariant — dead columns

`load_csv()` now computes, per capture, the set of numeric columns that are present in the header and
**0.0 (or empty) on every row**, stashes it in `meta["dead"]`, and **subtracts it from `cols`**. That
is the single seam every metric already routes through, so every existing `"x" in cols` /
`{"a","b"} <= cols` guard sends those metrics to `skipped` with no per-metric table anywhere. One
capture-level `DEAD COLUMN` warning names them on the same channel as RAILED / FLOOR / DAMAGED.

The rule is **zero-variance-at-zero**, not zero-variance. `assist=1`, `thr=0.7`, `aoaGD=1`, `bWt=1`
are constant over whole captures and all mean something (`bWt` railed at 1 for a whole capture *is*
the R21 finding); a constant non-zero value is its own evidence and cannot be mistaken for an
unmeasured 0.0. Constant-non-zero columns are **reported** by `--deadscan` and left scoring.

**`dmgFrac` is the clean case of the second shape** the coordinator named: always written, always
zero by construction, because ScenarioPlayer's damage abort runs *before* the row is written and
truncates the capture. 641,555 indexed rows, 0 non-zero, against 8 known damage aborts. Consequence:
`damage_warning()` was certifying every post-v0.96 capture as intact. It is now silent *because the
column was withdrawn*, and the withdrawal is on the page.

### `--deadscan`, and the third shape

`python debugtests/scorecard.py --deadscan <many.csv>` reports three things over a set of captures:
`DEAD` (identically 0 everywhere), `CONSTANT <v>` (never varied, non-zero), and — the shape neither
of us anticipated — **`FLAT WITHIN n/m FILE(S) but varying over the set`**: a column that is
constant inside each capture but takes different values between them. `assist` (0 vs 1) and `thr`
(0.40 vs 0.70) are the live examples; a column silently written by only one of two code paths would
land here. It is a report, not an input to scoring, and nothing is withdrawn on it.

### Corpus sweep — columns dead in ≥ 100 of 2,366 captures

| column | captures | reading |
|---|---|---|
| `flyLevel` | 2366 | removed feature; written as a literal 0 |
| `engP` / `engR` / `engY` | 2363 | manual-engagement flags; never set in a drone batch |
| `heliBlend` | 2362 | fixed-wing only |
| `aoaRec` | 1967 | v0.59 recovery bias never armed. `aoaRecoverActivePct` / `aoaRecoverPeak` were 0.0, now NULL |
| `bigTurn` | 792 | genuinely 0 on step cards (hard deadzone) |
| **`dmgFrac`** | **762** | **structurally 0 — see above** (the rest of the corpus predates the column) |
| `phiLead` | 704 | v0.85 bearing lead, off or inert |
| `datumX` / `datumZ` | 499 / 435 | no origin shift in that session |
| `bWt` | 450 | `blendRailPct` was 0.0, now NULL |
| `assist` | 411 | flight assist off for the whole capture — a real reading, but not distinguishable from an unwritten column |
| `targetBank` | 362 | it *is* flat on many cards, which is exactly how the defect hid |
| `bankBlend` / `yawWeak` | 358 / 351 | rudder never measured weak |
| `yawEff` | 217 | |
| `settleOn` | 105 | B2 micro-bank never armed |

A further 27 columns are dead in fewer than 100 captures each (`azErr` 36, `g` 33, `bankTR` 9,
`off` 1, …) — these are short or aborted captures where nothing is measurable anyway.

**Known boundary of the invariant, stated so nobody trips on it:** metrics that read a column via
`r.get(k, default)` *without* a `k in cols` guard are unaffected by the withdrawal — `assist`,
`aoaGU`, `airDensity` in `saturation_metrics`. That is correct there (all three have physically
meaningful defaults and none makes a "% of samples" claim), but a new metric of that shape would
bypass the invariant.

---

## Defect 2 — the wobble detector

### Root cause

`wobble_scan()` handed the **whole segment** to `aw.episodes()` and published the longest episode's
frequency. Three compounding failures, all confirmed:

1. **It scored the entry transient.** A step segment's ring-down crosses the dead-band a few times on
   its way to zero, and that is an "episode". 42 of 42 corpus episodes in R35/R36/R37/R39 began at
   `tSeg` 1.9–2.6 s; not one started after 3 s.
2. **Half were the detector's own floor.** `episodes()` requires ≥ 4 crossings and reports
   `(n−1)/2/(t1−t0)`, so a 4-crossing episode returns `1.5/(t1−t0)` **whatever the signal did**. The
   "0.319–0.328 Hz reproduced to three digits across three batches" was `3/(2 × the transient's
   fourth zero crossing)`.
3. **It was amplitude-censored.** A real mode whose settled amplitude never reaches the dead-band
   reads as *no mode* — which is how "the mode is absent on `obDR6`" was published.

### The rebuild

Three pieces, all in `scorecard.py`:

**(a) The settled window is derived per segment, not chosen.** `settled_from(t, xs)` bins `|x|` into
2 s rms bins and starts at the first bin that has come within **one e-fold of that segment's own
quietest bin** — "the decaying transient has stopped dominating what is left", in the signal's own
units, with the segment's own floor as reference. `e` is the natural unit of an exponential decay,
not a knob. Justification from the data, not from a round number: over **1,120 R35/R36/R37 oblique
legs** the envelope decays exponentially from ~4 s with a pooled time constant of **6.4 s** (9–11 s
fitted per leg), and this rule lands at **6–17 s on a 30 s leg**, moving with each leg's own decay.
(A fixed `tSeg ≥ 10 s`, which is what R39-C used by hand, is one point inside that spread; `2τ` was
tried and is too aggressive — it leaves too few cycles and drops three of eight Darkreach cells.)

**(b) The frequency is amplitude-independent, and NULL without evidence.** `osc_mode()` takes the
autocorrelation first peak of the linearly-detrended settled window and cross-checks it against an
**independent** Hann-windowed DFT peak. A frequency is published only when all three hold:

- the autocorrelation peak clears white noise at `3/√N` (derived from the window, not chosen);
- the two estimators land in the **same DFT bin**, `|f_acf − f_dft| ≤ 1/T` — one bin is the
  instrument's own resolution, and demanding better agreement than the instrument resolves is asking
  for precision nobody has;
- the window holds ≥ 4 periods (below ~3 cycles the first peak is not separable from the central
  lobe's tail, and at `f·T = 4` the agreement tolerance is already `f/4`).

Otherwise it is **absent** — not a floor value. `wobbleCoherence{Sig}` (the peak height) is published
**whenever a window exists**, including near zero: "measured, and incoherent" is a finding.

**(c) The episode count survives, honestly.** `wobbleEpisodes{Sig}` now counts dead-band episodes
**inside the settled window** with **> 4 crossings** (`aw.episodes()` gained a `min_cross`
parameter, default 4 so `analyze-wobble.py`'s own death-wobble scan is untouched; `scorecard` passes
6 = 2.5 cycles). A floor-valued episode can no longer be produced. It is still amplitude-gated by
construction — that is what an episode *is* — so **0 episodes beside a coherent `wobbleFreqHz` now
means "a real mode, under the dead-band"**, the exact case that used to read as "no mode".

### Validation against the confirmed Darkreach mode

Re-run over the four `oblique-6-dwell` batches (R35/R36/R37 at t070, R39 at t040 and t100), 1,632
legs, against R39-C's independently-established numbers:

| test | R39-C published | rebuilt detector |
|---|---|---|
| per-leg Δf, t100 − t040 (`obDR6`/`obDL6`/`obUL6`/`obUR6`) | +0.059 / +0.058 / +0.079 / +0.065 | **+0.078 / +0.059 / +0.075 / +0.069** — 4/4 above the 0.04 Hz bar, 4/4 monotone with V |
| cell frequencies, all 8 R39 cells | 0.298…0.435 Hz | 0.286…0.438 Hz — **every cell within 0.012 Hz** |
| four-batch log–log exponent (cell means) | **+0.304 ± 0.023**, r² 0.905, n=20 | **+0.305 ± 0.029**, r² 0.859, n=20 |
| Darkreach coherence | 0.72–0.81 | median **0.77**, p05 0.56 |
| mode present on Darkreach | 32/32 in the 0.25–0.50 Hz band | **69/72** legs publish a frequency, **69/72 in band** |
| other nine airframes in band | 3/480 | **9/1560**; with coherence ≥ 0.5, **0/1560** (Darkreach 66/72) |
| episodes at `tSeg ≈ 2 s` | 42 of 42 | **0** — across all 512 R39 legs, zero settled-window episodes |

The other airframes' ~1 Hz feature (median 0.94 Hz at coherence 0.30 — what R39-C called "a
different, weaker thing") is now *measured* rather than invisible, and separates from the Darkreach
mode cleanly on frequency and coherence together.

### Corpus effect

- `wobbleEpisodes*` (all six signals summed): **318 → 5**. R28 46→0, R29 39→0, R37 49→0, R39 72→1,
  R35 41→2, R36 23→0, R32 13→2.
- `wobbleFreqHzAzErr`: **62 → 476** segments (436 newly measurable, 22 withdrawn as unsupported).

---

## Defect 3 — `authorityUsedFrac` and SLACK: **deleted**

`authorityUsedFrac` = `max(turnRateDemandRatio, authBank, authAoa, authStick)`, and it equalled
`authBank = mean|bank| / maxBank` in all 32 cells examined. Bank in a coordinated turn is pinned by
`φ = atan(ωV/g)` **before any control law runs**, so the metric read 0.87–0.99 on every card that
demanded a fast turn and was reporting the *card's* demand, not the law's effort. It exceeded 1.0
(to 1.084). Sensitivity: the largest deliberate law defect in the corpus, a 2.3× error, moves it
0.03–0.11 — roughly 5× mis-scaled.

The denominator is wrong, not the window, so a re-gate or a peak/rise-window statistic is not a fix.
Deleted rather than rescaled: `authorityUsedFrac`, `authBank`, `authAoa`, `authStick`, `SLACK_FRAC`,
`SLACK_TYPES`, `AUTH_TERMS`, `AUTH_MIN_TERMS`, and the SLACK branch of `rail_warning()`.

`turnRateDemandRatio` — the one term with a real denominator (demanded ω over the probed `omegaMax`)
— **stays**, and is unchanged.

SLACK's corpus history: **8 fires ever, all one cell** — R27 `Trainer · sweep-creep ·
turn360creep`, 1.33 °/s, `authorityUsedFrac` 0.462 with `turnRateDemandRatio` 0.08. It fired **0
times in R39's 121 sustained turns**. Those 8 warnings disappear; no segment gains a warning.

Two callers were updated in the same change (grepped, not guessed):

- `compare-runs.py::_sat_cell` — the `auth<pct>%` fallback in the `rail/auth` summary column is gone;
  the column now prints a named rail, `none`, or `-`. Its selftest asserts the fallback stays gone.
- `index-captures.py` — the `segments.slack` column is **no longer written**. The column survives in
  existing databases; on a rebuild it goes NULL. Writing a constant 0 would assert "measured, not
  slack" about a detector that no longer exists — the exact failure this release went after.

A selftest asserts the four metric names and the four constants are **absent from the module**, so
the metric cannot be reintroduced by a well-meaning "the mirror question has no metric" patch.

**What a real replacement would need**, recorded in `saturation_metrics`' docstring: an
achieved-vs-achievable pair on one axis — `gSustained/gLimit`, or achieved turn rate over the probed
`omegaMax`. Not a mean over a limit.

---

## Previously-published findings that no longer stand

These documents were **not edited**. Named claims only; everything else in each document is
unaffected.

### Invalidated by the `bankClampActivePct` repoint

- **`R28-FINDINGS.md:592`** — "`bankClampActivePct` is 0.0% on every healthy [cell]". **Refuted.**
  Twenty `ob*12` cells across EW1 / FS-12 / FastBomber1 / Multirole1 / SmallFighter1 / Trainer /
  VTOLTrainer1 read 8.2–15.1%, and every Darkreach cell reads 35–89% (`obUL12` 49.9 → 89.2,
  `obUR12` 12.5 → 81.0). The same line's "`authBank > 1.0` on 0 of 1536" is moot — the metric is gone.
- **`R29-FINDINGS.md:621` and `:665`** — "`bankClampActivePct` 0.0% on all 6 cards" / "bank clamp
  never reached". **Refuted**: twelve cells now read 8.1–12.0%. Separately, R29 `Darkreach·obUL2`
  and `obUR2` were **over-read** (43.0 → 22.3, 42.3 → 0.0), so any Darkreach-specific clamp claim
  from R29 is wrong in the other direction.
- **`R39-A-ranking.md:314`** — "0 railed segments; `bankClampActivePct` = 0.0 on every leg-1 cell".
  The `oblique_step` half **survives** (those cells stay ≈ 0). The blanket "0 railed" is now only
  true of that card; it must not be quoted for R39's turn cards.
- **`R39-D-sustained-ab.md:178`** — "0.0% of unrailed `turn360mff` segments, inside the declared
  0–20% PASS band". **The input moved**: `turn360mff` now reads 17.2% (FastBomber1) to 44.3%
  (Multirole1) and `turn360rtl` 19.1% to 74.5%, with one `Darkreach·turn360rtl` segment crossing to
  `railed = 1` (73.2 → 91.3). Its own §7c and backlog item **#55d** predicted this; the *numbers* in
  §7c's table are superseded by the re-score.
- **`R36-FINDINGS.md:211`, `:470` and `R37-FINDINGS.md:20`, `:145`** — "0 railed, 0 slack" as a
  data-quality certificate. Still true for those `oblique_step` batches (no flips landed in R36/R37),
  but the *slack* half is now vacuous, and "0 railed" no longer rests on the same measurement.
- **`R21-FINDINGS.md:282`** — the metric definition table still says "% samples with `|targetBank|`
  at `Cfg.MaxBankAngle`". Stale. `:305`/`:311`'s headline (96.98%) is **strengthened to 99.40%**;
  the finding stands.
- **`CAPTURES-DB.md:136`** — the `slack` column description, and the "8 rows" count. The column is no
  longer written.

### Invalidated by the wobble rebuild

- **`R39-C-settle-mode.md:95–101` (§2a table)** — the `wobbleEpisodesAzErr` counts (Darkreach t040
  3, t100 12; all others 0) and the `wobbleFreqHzAzErr` cell means 0.3277 / 0.3677. All now NULL or
  0 on a re-score. §3's *diagnosis* of why they were wrong is confirmed in every particular and is
  the basis of the replacement; only the table is superseded.
- **`R39-C-settle-mode.md:314`** — its own standing caution ("anything built on `wobbleEpisodes*` /
  `wobbleFreqHz*` for a dwell card needs re-checking") is now discharged.
- **`R39-E-alpha.md:151`** — "`wobbleEpisodesAoa` = 0 → **PASS 8/8**, zero AoA wobble episodes in 60
  segments". **The pass is now vacuous**: the metric reads 0 on essentially every segment in the
  corpus after the transient exclusion (5 episodes in 7,837 segments), so it cannot discriminate.
  Re-run that criterion on `wobbleCoherenceAoa` / `wobbleFreqHzAoa`.
- **`R21-FINDINGS.md:356`** — the proposed watch signal "check `wobbleEpisodesBank`" needs
  re-pointing at `wobbleCoherenceBank`; on a sustained turn the episode count will now read 0
  regardless.

### Invalidated by the `authorityUsedFrac` deletion

Every use below loses its metric. None of them is merely re-scaled — the quantity is withdrawn.

- **`R39-D-sustained-ab.md:58`, `:177`, `:180`, `:195–198`, `:215`, `:517`, `:527` (#55a)** — the
  whole authority thread, including "`authorityUsedFrac` populated on 121/121", the per-card-family
  table, and the 0.03–0.11 sensitivity measurement. The *conclusion* (the metric is a kinematic
  restatement of the card's demand) is what motivated the deletion and stands; the numbers are gone
  with the column. **#55a is closed as WONTFIX, not as done** — no task-relative denominator was
  built.
- **`R39-E-alpha.md:52`, `:426` (criterion 10), `:438`, `:571` (#55e)** — the SLACK refutation is now
  moot rather than refuted (there is no SLACK to be unreachable), and #55e ("`authorityUsedFrac`
  exceeds 1.0 on four lanes") is closed by deletion.
- **`R39-F-darkreach-damage.md:54`, `:62`, `:304`, `:310`** — `authorityUsedFrac` 0.441 "the highest
  in the fleet by 1.6×" as a damage correlate, and the CAS1/COIN 0.274-vs-EW1 0.185 comparison.
  **These are `mean|bank|/72` comparisons across airframes flying different bank angles**, so they
  were never measuring effort; the damage argument needs a different covariate.
- **`R33-FINDINGS.md:26`, `:98`, `:323`** — `authorityUsedFrac` 0.092 vs 0.093 used as a
  **flight-condition matching criterion** between two lanes. Replace with the pair actually intended:
  mean bank + `turnRateDemandRatio`. The match itself is probably still true (both lanes flew the
  same card at the same speed), but the evidence quoted for it is gone.
- **`LOOP-AUDIT-FINDINGS.md:24`, `:344`, `:426`** — "`authorityUsedFrac` 0.717–0.748" / "0.476–0.748"
  quoted as evidence the airframe had authority in hand at the AoA-gate cells. Gone; the
  `aoaLimiterActivePct` finding those lines support does not depend on it.
- **`R28-FINDINGS.md:166`, `:189`, `:218`, `:623`, `:698`** and **`R29-FINDINGS.md:572`, `:624`,
  `:633`** — the `authUsed` summary columns and the "4.5–25% of available authority" reading.

### Invalidated by the dead-column invariant

- Anything filtering on **`dmgFrac == 0` as "undamaged"**. It is a guaranteed constant: 641,555
  indexed rows, 0 non-zero, 8 known damage aborts. `R39-F-darkreach-damage.md` is the document to
  re-read against this — the damage signal is the **abort and the truncated capture**, never the
  column. (`CAPTURES-DB.md` should stop offering `dmgFrac` as a filter.)
- Any aggregate over **`aoaRecoverActivePct` / `aoaRecoverPeak`** (1,967 captures move 0.0 → NULL)
  or **`blendRailPct`** (450 captures). Verdicts do not change — 0.0 never railed anything — but
  `avg()` and `count()` over those columns do. Read `count()` beside `avg()`, as `CAPTURES-DB.md`
  already insists.

### The general lesson — and it is not confined to these three metrics

**For a term that moves a *target*, the output that holds the target is the wrong observable.**

`bankClampActivePct` is one instance: it asked "is the bank clamp active" and looked at
`targetBank`, a *different* target computed by a law that no longer runs, instead of at the demand
the clamp acts on.

The `MarkerRateFeedForward` lever is the same mistake in a different place, and worth recording
beside this one: it was long believed inert because roll stick did not move (`|outR|` 0.0068–0.0109
on **both** arms). It is in fact worth **57% of the standing azimuth error**. It acts on the bank
*target*; `outR` is the servo output that *holds* bank, and in a settled turn a correct servo holding
a shifted target produces almost no extra deflection. The null was in the instrument.

Both failures share a shape, and it is cheap to screen for: **before reading a metric as evidence
that a term is inert, check that the column is downstream of the term and upstream of the loop that
cancels it.** A signal on the far side of a closed loop reads ~0 whether the term did nothing or
worked perfectly.

### Unrelated, flagged by the same investigation

- **The detach ratio is not an event count.** `UnitPart.Detach` cascades `onParentDetached` down the
  subtree and the tracker counts `detachedFromUnit`, so the ratio measures **subtree size**:
  0.029 = a leaf, 0.057 = one child, 0.114 = three descendants. Nothing in `scorecard.py` reads it,
  but `R35`/`R39-F` prose that reads 0.114 as "four events" is wrong by construction.

---

## Landing this

The re-score above is **read-only**. To land it in `captures.db`:

```
python debugtests/index-captures.py <game>/BepInEx --rebuild
python debugtests/index-captures.py <game>/BepInEx --with-rows R28   # ...and each of R29 R31 R33 R35 R36 R37
```

**`--rebuild` drops every materialized row** (`index-captures.py:384`, the `ON DELETE CASCADE`
made explicit) — that is ~590 MB across seven batches, and the `--with-rows` passes have to be
re-run. It was left undone deliberately because other agents are querying that database right now.

Scoring cost roughly doubles: a 5-segment 30 s capture goes from 0.05 s to 0.35 s (the
autocorrelation and DFT are O(N²) in pure Python over ~300-sample windows). A full corpus re-index
is ~10 minutes.

New/changed columns on rebuild: `wobbleCoherence{Bank,AzErr,OutR,OutP,OutY,Aoa}` added;
`authorityUsedFrac`, `authBank`, `authAoa`, `authStick`, `slack` stop being written (existing values
persist until rebuild, then NULL).

## Recorder-side follow-ups (C#, out of scope here)

1. **`dmgFrac` — write the row before the abort check.** The v0.96 damage abort in
   `ScenarioPlayer.Tick` truncates the capture ahead of the row write, so the column is structurally
   incapable of carrying a non-zero value. One row written before `Abort` would make it real.
2. **`targetBank` — stop writing it, or rename it.** It is the removed Legacy law's bank target and
   nothing flies it; `tBankE` is the flown one and is already recorded. Keeping a column named
   `targetBank` beside a law that ignores it is what produced this defect. If it stays, its header
   comment should say what it is.
