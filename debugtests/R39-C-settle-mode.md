# R39 Criterion C — the Darkreach azimuth mode: V-dependent, Darkreach-only, real — and NOT the settle loop's, v0.98.1

**Pre-registered prediction:** *"Darkreach, and only Darkreach, shows a ~0.35 Hz oscillation in its
azimuth error. Its frequency tracks airspeed. The same tag flown at t040 vs t100 separates by
|Δf| > 0.04 Hz, monotone with V."*

**Batch R39**, cards `oblique-6-dwell-t040` / `oblique-6-dwell-t100` (`ScenarioThrottle` 0.40 / 1.00),
128 captures, 16 lanes × 10 airframes × `repeat: 4`, entry 0.95× each airframe's FBW corner speed at
4000 m. **512 scorable legs, 0 railed, 0 aborted, 0 parse warnings**, `n_cols = 69` uniform. Filtered
on `card`, never on `run_tag` (R39 is shared with a concurrent batch). `ChaseController.cs` last
changed at **v0.96.0**, so R35 (v0.96.2), R36 (v0.97.1), R37 (v0.97.2) and R39 (v0.98.1) all fly the
**same control law** — the three historical batches are directly poolable as a third throttle arm
(0.70). Everything below is reproducible from `debugtests/captures.db` plus the raw CSVs.

---

## Verdict

1. **The numeric prediction PASSES, on all four legs, not one.** Measured with an amplitude-independent
   estimator on the settled window (`tSeg ≥ 10 s`): `obDR6` +0.059, `obDL6` +0.058, `obUL6` +0.079,
   `obUR6` +0.065 Hz, all monotone with V, all above the 0.04 Hz threshold. n = 4 replicates per cell.
   §1, §4.
2. **The mode is Darkreach-only, and R39 proves it far harder than the episode counter could.** Across
   all 512 legs, Darkreach is **32/32** with its azErr autocorrelation peak in the 2.0–4.0 s band
   (0.25–0.50 Hz) at coherence 0.72–0.81; the other nine airframes are **3 of 480** in that band, with
   peaks at ~1.0 s and coherence 0.15–0.35. §2.
3. **The mode is NOT at the quantisation floor. It is 4–22× above it and strongly coherent.** Settled
   rms `azErr` 0.073–0.427°, against a 0.0198° `acos` quantum and a 0.01° print grain; autocorrelation
   peak height 0.25–0.84 where white grain noise would give 0 ± 0.056. §3.
4. **BUT THE PRE-REGISTERED METRIC WAS MEASURING THE WRONG THING, AND THIS IS THE FINDING WITH THE
   LONGEST REACH.** Every `wobbleEpisodesAzErr` episode in R35/R36/R37/R39 — **42 of 42** — starts at
   `tSeg` 1.9–2.6 s and ends by 17.4 s. They are the **entry transient**, not a settled mode. Worse,
   the reproduced-to-three-digits `obDL6` value (0.319–0.328 across three batches) is
   `3 / (2 × 4.6 s)` = **the detector's own floor**: `episodes()` requires ≥ 4 crossings
   (`analyze-wobble.py:120`) and reports `(len(seg)-1)/2/(t1-t0)` (`:127`), so a 4-crossing episode
   returns a constant divided by whenever the fourth crossing landed. §3.
5. **"`obDR6` is the leg where the mode is ABSENT" is wrong.** The mode is present on `obDR6` on every
   batch and both arms (R35/R36/R37 f 0.333–0.351 at coherence 0.66–0.79; R39 t040 0.298, t100 0.356).
   The alpha limiter does not suppress it. `wobbleEpisodesAzErr` read 0 there because settled
   `|azErr|` never reaches the detector's 0.5° dead-band — **amplitude censoring, not suppression**.
   §2, §3.
6. **Fitted exponent f ∝ V^0.305 ± 0.015** (95% CI 0.276–0.335, r² 0.853, n = 72 legs over four
   batches, V 109–283 m/s). R39 alone gives **0.356 ± 0.026** (CI 0.304–0.407), which brackets the
   historical 0.37; the pooled fit does not. The historical 0.37 was fit on three points of a censored
   entry-transient estimator, so the disagreement is instrument, not physics. §4.
7. **`kSettle` SCHEDULING IS NOT JUSTIFIED, and the pre-registration's own "if it does" clause does not
   survive its refutation test.** At the mode frequency the settle path reproduces
   `tBankE = 8.00 × azErr` (7.94–8.08 on the 24 legs where `cos(pitch) ≈ 1`) at **|phase| ≤ 0.5° on 29
   of 32 legs** — so it is unambiguously the active bank command — but its **return** gain is
   0.054–0.325, i.e. **10–25 dB of margin, and the margin IMPROVES with V** (0.29 at 110 m/s → 0.055 at
   283 m/s). The bank ripple the settle path produces can only account for **5.4–33%** of the observed
   `azErr` at that frequency. The settle loop is a passenger. §5.
8. **The mode is in the yaw/heading channel, not the bank channel.** The nose heading rate implied by
   the observed `azErr` is **3–19× larger than the commanded bank can produce**, `aimRate` is
   identically **0.0000** (stationary marker — the mode is not stimulus), and `outY` sits **in phase**
   with `azErr` at 0.16–0.17 stick/deg while `outR` leads it by ~+39°. §5.
9. **`obDR6` at t040 fails the manipulation criterion on its own (V100/V040 = 1.203 < 1.25) and still
   separates by 0.059 Hz.** The other three legs clear it at 1.495–1.562. §1.
10. **The t100 arm rides Vmax on legs 3 and 4** (V/`sc_infoMaxSpeed` 0.99 and **1.01**), so the
    *within-arm* leg-3-vs-leg-4 speed contrast is compressed to 1.02× on that arm. The *between-arm*
    contrast, which is what the prediction tests, is unaffected. §1.

---

## 1. Did throttle actually move Darkreach's speed?

Mean `spd` over `tSeg` 7–8 s, ÷ `sc_fbwCornerSpeed` = 100 and ÷ `sc_infoMaxSpeed` = 279.2. n = 4 per
cell.

| leg | V t040 | /Vc | /Vmax | V t100 | /Vc | /Vmax | **V100/V040** |
|---|---|---|---|---|---|---|---|
| `obDR6` | 109.5 | 1.10 | 0.39 | 131.7 | 1.32 | 0.47 | **1.203** |
| `obDL6` | 143.5 | 1.43 | 0.51 | 214.5 | 2.14 | 0.77 | **1.495** |
| `obUL6` | 176.3 | 1.76 | 0.63 | 275.4 | 2.75 | **0.99** | **1.562** |
| `obUR6` | 183.2 | 1.83 | 0.66 | 281.7 | 2.82 | **1.01** | **1.537** |

Three of four legs clear the 1.25× bar. `obDR6` does not: at t040 the alpha limiter runs **100.0%** of
that leg (R37 at t070 ran 60–63%), so the throttle cut buys almost nothing there — the airframe is
already demand-clamped. Read `obDR6` as a bonus point, not a test.

Legs 3 and 4 at t100 are **at or over the published Vmax** (275.4 and 281.7 against 279.2). Those two
cells are not independent speed conditions from each other (1.02× apart, and f differs by 0.005 Hz);
they are one high-V cluster. The design still has four distinct V clusters per leg once the historical
t070 arm is pooled in (§4).

Pooled across all 10 airframes the manipulation worked everywhere: V100/V040 spans 1.09 (COIN leg 1)
to 2.09 (FastBomber1 leg 4), median 1.44 over 40 cells.

---

## 2. Is the mode still Darkreach-only?

### 2a. On the pre-registered metric — yes, trivially

`wobbleEpisodesAzErr`, `s.excluded=0 AND s.railed=0`, both cards:

| | legs | episodes | `wobbleFreqHzAzErr` |
|---|---|---|---|
| Darkreach t040 | 16 | 3 | 0.3277 (n=3) |
| Darkreach t100 | 16 | 12 | 0.3677 (n=12) |
| **all nine others, both arms** | **480** | **0** | — |

### 2b. On an amplitude-independent instrument — yes, and much more strongly

`wobbleEpisodesAzErr = 0` only says "`|azErr|` never crossed ±0.5° with a sign change". It cannot
distinguish "no mode" from "a mode under the dead-band". Replace it: over the settled window
(`tSeg ≥ 10 s`, 320 samples/leg) take the **autocorrelation first peak** and an independent
**Hann-windowed DFT peak** of `azErr`. White quantisation noise gives a peak height of 0 ± 1/√320 =
0.056.

| airframe | arm | legs | median peak height | median lag | in the 2.0–4.0 s band |
|---|---|---|---|---|---|
| **Darkreach** | t040 | 16 | **0.807** | 2.81 s | **16/16** |
| **Darkreach** | t100 | 16 | **0.717** | 2.40 s | **16/16** |
| CAS1 | both | 64 | 0.20 | 1.1 s | 0/64 |
| COIN | both | 32 | 0.11 | 1.2 s | 0/32 |
| EW1 | both | 32 | 0.26 | 1.0 s | 1/32 |
| FastBomber1 | both | 32 | 0.25 | 1.1 s | 0/32 |
| Fighter1 | both | 64 | 0.25 | 0.95 s | 0/64 |
| Multirole1 | both | 64 | 0.30 | 1.0 s | 1/64 |
| SmallFighter1 | both | 64 | 0.28 | 1.0 s | 0/64 |
| VTOLTrainer1 | both | 64 | 0.25 | 1.0 s | 1/64 |
| trainer | both | 64 | 0.17 | 1.0 s | 0/64 |

On Darkreach the two estimators agree to **0.003–0.005 Hz**; on every other airframe they disagree by
0.08–0.67 Hz, which is what a noise peak looks like. The ~1 Hz feature the others carry (coherence
0.15–0.35) is a **different, weaker thing** and is not this mode — do not conflate them.

**Darkreach-only survives.** It also now survives on `obDR6`, where the episode counter said the mode
was absent: R35/R36/R37 read 0.333–0.351 Hz at coherence 0.66–0.79, R39 t040 0.298 and t100 0.356.

---

## 3. Is `wobbleFreqHzAzErr` trustworthy at these amplitudes? — NO, and here is exactly how it fails

Three separate defects, compounding. None of them refutes the mode; all of them refute the metric.

### 3a. It is not the quantisation floor

`azErr` is `Vector3.SignedAngle` in float32, so it shares `off`'s `acos` grain of **0.0198°** near zero
(`scorecard.OFF_FLOOR_DEG = 0.0396`, `scorecard.py:90`); the recorder prints it at **0.01°**. Settled
Darkreach `azErr` reads rms **0.073–0.427°** and p95 |azErr| **0.12–1.01°** — 7.3× to 43× the print
grain. Grain noise is white and would give an autocorrelation peak of 0 ± 0.056; measured **0.25–0.84**
(4.5σ to 14σ). `offFloorPct` (the `off` column, not `azErr`) is 0.0–20.4% across the 32 legs, nowhere
near saturating. **The mode is real signal.**

### 3b. It scores the ENTRY TRANSIENT, not the mode

Every `wobbleEpisodesAzErr` episode in the four batches — **42 of 42** — begins at `tSeg` **1.9–2.6 s**
and ends by 17.4 s. Not one starts after 3 s. In the settled window (`tSeg ≥ 10 s`) `|azErr| ≥ 0.5°`
on **0.0%** of samples for six of the eight Darkreach cells, and 12.1% / 17.2% on the two fastest — so
the detector *cannot* fire on the settled mode almost anywhere. The pre-registration's table is a
table of transient decays.

### 3c. Half the reproduced values are the detector's own floor

`episodes()` needs ≥ 4 crossings (`analyze-wobble.py:120`) and reports
`freq = (len(seg)-1)/2/(t1-t0)` (`:127`). A 4-crossing episode therefore reports `1.5/(t1-t0)`
regardless of the signal. Measured:

```
R35 obDL6  4 crossings / 4.6-4.7 s  ->  0.320, 0.328
R36 obDL6  4 crossings / 4.7 s      ->  0.319, 0.321
R37 obDL6  4 crossings / 4.6-4.7 s  ->  0.320, 0.324, 0.325
R39 obUR6t04 4 crossings / 4.6 s    ->  0.325, 0.328, 0.330
R39 obDL6t10 4 crossings / 4.4-4.5 s->  0.333, 0.333, 0.333, 0.338
```

`1.5/4.6 = 0.326`. **The "reproduced to three digits across three batches" number is
`3/(2 × the transient's fourth zero crossing)`** — it reproduces because the entry transient
reproduces, not because a frequency was measured. The `obUL6`/`obUR6` episodes (6–12 crossings over
7–17 s) are genuine, and those are the ones that carried the historical V-trend.

### 3d. The amplitude-censoring test

Scale each leg's `azErr` and re-run the *same* detector. The reported frequency rises toward the true
(DFT) value as amplitude rises, and the episode appears/disappears purely on amplitude:

```
obUL6t04 rec42 (DFT 0.358): x1.0 -> no episode ; x2.5 -> 0.369 (4 crossings)
obUR6t10 rec90 (DFT 0.435): x0.8 -> 0.405 (4c) ; x1.0 -> 0.407 ; x1.8 -> 0.421 ; x2.5 -> 0.423 (12c)
```

Settled `rms(azErr)` scales as **V^2.07 ± 0.22** (n = 24 unclamped legs, r² 0.805) — essentially ∝ q —
while f scales as V^0.31. So amplitude crosses the fixed 0.5° dead-band as a function of speed, and the
episode counter turns a smooth mode into an apparent onset. **That is the whole shape of the historical
finding.**

---

## 4. f vs V

Estimator: DFT peak and autocorrelation `1/T` of `azErr` over `tSeg ≥ 10 s`, per leg. Cell means
(n = 2–4 per cell), all four batches, one law:

| leg | R39-t040 | R35-t070 | R36-t070 | R37-t070 | R39-t100 |
|---|---|---|---|---|---|
| `obDR6` | 109.5 / **0.298** | 122.3 / 0.351 | 120.8 / 0.333 | 122.4 / 0.339 | 131.7 / **0.356** |
| `obDL6` | 143.5 / **0.350** | 182.0 / 0.377 | 180.5 / 0.380 | 182.1 / 0.380 | 214.5 / **0.408** |
| `obUL6` | 176.3 / **0.356** | 236.8 / 0.409 | 236.0 / 0.411 | 236.8 / 0.406 | 275.4 / **0.435** |
| `obUR6` | 183.2 / **0.365** | 249.1 / 0.399 | 248.6 / 0.410 | 249.0 / 0.409 | 281.7 / **0.430** |

(V in m/s at the 7–8 s window / f in Hz.) Monotone in V in all four rows, across three throttle arms
and four batches.

**Pre-registered test, per leg (t100 − t040):** +0.059, +0.058, +0.079, +0.065 Hz. **4/4 above the
0.04 Hz threshold, 4/4 monotone with V.**

Log–log fits:

| set | exponent | 95% CI | r² | n |
|---|---|---|---|---|
| all four batches, per leg | **+0.305 ± 0.015** | 0.276 – 0.335 | 0.853 | 72 |
| all four batches, cell means | +0.304 ± 0.023 | 0.258 – 0.350 | 0.905 | 20 |
| R39 only (t040 + t100) | +0.356 ± 0.026 | 0.304 – 0.407 | 0.860 | 32 |
| R35/R36/R37 only (t070) | +0.251 ± 0.012 | 0.228 – 0.274 | 0.923 | 40 |
| unclamped legs only (no `obDR6`) | +0.321 ± 0.016 | 0.289 – 0.353 | 0.880 | 54 |
| autocorrelation instead of DFT, pooled | +0.309 ± 0.016 | 0.278 – 0.340 | 0.845 | 72 |
| **`bank` DFT peak** (same mode, different signal) | +0.343 ± 0.026 | 0.291 – 0.394 | 0.886 | 24 |

**Against the historical 0.37:** the R39-only CI contains it; the pooled CI (0.276–0.335) excludes it.
The spread between the arms' own fits (0.251 vs 0.356) is real curvature — the relation flattens
approaching Vmax — so a single power law is a summary, not a model. Quote **0.30 ± 0.02** for the
corpus and **0.36 ± 0.03** for the clean within-batch contrast.

Air density is not a competing abscissa here: `airDensity` spans 0.873–0.963 (10%) across all 32 legs
while V spans 2.6×, so V and √q are collinear to within the fit's own scatter. This design cannot say
whether the driver is V or q.

---

## 5. The refutation test

**Conclusion stated:** the mode's frequency is set by airspeed, `kSettle` is V-independent, therefore
`kSettle`'s phase margin moves with a quantity the law does not read.

**What would refute it:** if the settle loop's *own* return path — `azErr → kSettle → tBankE →
roll servo → bank → heading rate → azErr` — is too weak to close this oscillation, then whatever sets
its frequency, it is not `kSettle`'s crossover, and scheduling `kSettle` cannot move it. Measured, not
argued.

Narrowband amplitude and phase at each leg's own mode frequency (Hann-windowed phasor, `tSeg ≥ 10 s`,
32 legs):

| quantity | result |
|---|---|
| `tBankE` / `azErr` amplitude | **7.94–8.08** on 24 legs; 6.68–7.71 on the eight t100 up-legs, where `settleConf = cos(pitch)` < 1 |
| `tBankE` phase vs `azErr` | **\|phase\| ≤ 0.5°** on 29 of 32 legs; 1.3–1.9° on three `obUR6` t100 legs |
| `settleOn` duty in the window | **100%** on 24 legs, 79.8–89.7% on the eight t100 up-legs |
| `bank` / `tBankE` | 0.52–0.89, phase **−72° to −108°** (median −85°) |
| `aimRate` amplitude | **0.0000** on all 32 legs — the marker is stationary; the mode is not stimulus |

So the settle path **is** the bank command at this frequency: it reproduces `kSettle = 8` exactly, with
no phase, on essentially every sample. That much of the pre-registration is confirmed.

Now close the loop. `d(azErr)/dt = −(g/V)·bank_deg`, so the azErr the observed bank ripple can itself
produce at frequency ω is `(g/V)·A_bank/ω`:

| leg / arm | V | f | A(bank) | azErr the bank ripple explains | **fraction of observed** | loop gain |
|---|---|---|---|---|---|---|
| `obDR6` t040 | 110 | 0.285 | 0.65° | 0.029° | **29%** | 0.29 |
| `obDL6` t040 | 143 | 0.345 | 0.53° | 0.015° | **14%** | 0.14 |
| `obUL6` t040 | 176 | 0.358 | 0.89° | 0.021° | **11%** | 0.11 |
| `obDL6` t100 | 215 | 0.405 | 0.74° | 0.012° | **7.4%** | 0.074 |
| `obUL6` t100 | 275 | 0.435 | 1.45° | 0.018° | **5.7%** | 0.057 |
| `obUR6` t100 | 283 | 0.430 | 1.58° | 0.020° | **5.4%** | 0.054 |

**The settle loop carries 5–29% of the observed oscillation and has 10–25 dB of gain margin at the
mode frequency — and the margin gets BETTER with speed, not worse.** That is the exact opposite of the
"phase margin erodes with V" story the pre-registration's first branch rests on. `kSettle`'s own
crossover is `8g/V` rad/s = **0.044–0.11 Hz**, four to ten times *below* the mode and moving the wrong
way.

**Where the mode actually lives.** The last column inverted says it directly: the nose heading rate
implied by the observed `azErr` (= ω·|azErr|) is **3.1× to 18.5× larger than `g·bank/V`**, so the nose
is being swung in **yaw**, not by the turn. `outY` is in phase with `azErr` at 0.16–0.17 stick/deg
while `outR` leads it by ~+39°, and the disturbance is not external (`aimRate` = 0). The signature — nose yawing about a nearly-fixed flight path, bank ~90° out of phase,
frequency rising with speed, one heavy airframe only (Darkreach: 105 t, FBW corner 100 m/s against an
AI corner of 180) — is a lateral-directional / yaw-channel mode, and `kSettle` is downstream of it.

---

## 6. Consequence for `kSettle`

`ChaseController.cs:1839–1849` — `kSettle` declared **:1841**, gate **:1844**, applied **:1847**;
`_settleOK` set at **:909**; the `bankTR` target it overrides built at **:1824–1827**.

```csharp
const float settleGate = 0.5f;          // :1840
const float kSettle    = 8f;            // :1841  V-INDEPENDENT
const float settleCap  = 4f;            // :1843
if (!_collective && _settleOK && Mathf.Abs(azErr) < settleGate && Mathf.Abs(tBankE) < 1e-3f)  // :1844
    tBankE = Mathf.Clamp(kSettle * azErr, -settleCap, settleCap) * settleConf;                 // :1847
```

**Do not schedule it against V or q.** The pre-registered numeric threshold was cleared, but the
inference it was supposed to license is refuted by §5: the loop `kSettle` sits in is 10–25 dB from
instability at the mode frequency and gets *further* from it as V rises. A V-scaled `kSettle` would
change the bank ripple's amplitude by the scheduled factor, would move the mode's frequency by
approximately nothing (5% authority), and would trade away closure rate in exactly the regime — high
q, sub-degree residual — the term was added in v0.65 to fix. The comment at :1841
(*"≤3× below the 22 deg/deg that already rocked at 220 m/s, so loop gain stays inside margin"*) is
**measured correct**: at 220 m/s the return gain is 0.07.

If the mode is worth chasing, the target is the yaw channel at 0.3–0.44 Hz on a heavy airframe, and
the discriminating batch is a `Cfg.YawAssistEnabled` / `YawAssistStrength` A/B on Darkreach at
throttle 1.00 (where the amplitude is largest and the detector actually fires), scored on the settled
autocorrelation peak — **not** on `wobbleEpisodesAzErr`.

**Tooling debt this exposes.** `scorecard.wobble_scan` (`scorecard.py:1159`) picks the *longest*
episode (`:1174`) from a detector whose minimum is 4 crossings, over a fixed 0.5° dead-band
(`analyze-wobble.py:360`). On 30 s dwell legs that reliably returns the entry transient at the
detector's floor and silently reports 0 for any settled mode under the dead-band. Any future finding
built on `wobbleEpisodes*` / `wobbleFreqHz*` for a **dwell** card needs re-checking against a settled
window. (The metric is fine for what it was written for — the v0.51 rail-to-rail death wobble, where
amplitude is tens of degrees.)

---

## 7. Ruled out, and not ruled out

| candidate | evidence |
|---|---|
| the mode is float32 grain | rms 0.057–0.427° vs a 0.0198° quantum; autocorrelation peak 0.25–0.84 vs 0 ± 0.056 for white noise |
| the frequency shift is amplitude censoring | two amplitude-independent estimators (DFT, autocorrelation) agree to 0.005 Hz and both move; the `bank` signal moves with the same exponent |
| the throttle manipulation did nothing | V100/V040 = 1.50–1.56 on the three unclamped legs |
| the mode appeared on other airframes | 3 of 480 non-Darkreach legs even land in the 0.25–0.50 Hz band, all at coherence < 0.53 |
| distance / `origDist` confound between the arms | t040 lane 10 at 62.1–64.2 km, t100 lane 26 at 62.2–66.7 km — the arms are within 4% |
| a law change between the historical arm and R39 | `ChaseController.cs` unchanged since v0.96.0; R35–R39 are one law |
| railed / slack / unknown-tag / damage contamination | 0 / 0 / 0 / 0 aborts in 512 segments |
| the mode is stimulus (marker motion) | `aimRate` amplitude is **0.0000** at the mode frequency on all 32 legs |
| `kSettle` is near its stability limit | return gain 0.054–0.325 → 10–25 dB margin, improving with V |

**Confounds NOT ruled out:**

- **V vs q vs AoA are collinear by construction on this card.** Throttle is the only manipulated
  variable; raising it raises V, lowers trim AoA (Darkreach mean AoA 6.1° at V = 110 → 1.29° at
  V = 283) and raises q as V² at near-constant density (ρ spans only 10%). Nothing here distinguishes
  "f tracks V" from "f tracks q" or "f tracks 1/AoA". A card that holds V and varies altitude would.
- **Leg identity is confounded with altitude.** The down legs settle at 3190–3750 m and the up legs at
  2750–2960 m, so a per-leg effect and a per-altitude effect are not separable. The four legs agree on
  the exponent, which argues against it mattering, but does not exclude it.
- **The 2.0–4.0 s band choice is post hoc.** It was picked from Darkreach's own measured lag. It is not
  a blind band, and a mode outside it on another airframe would be missed by the §2b test as written
  (the ~1 s feature all nine others carry is exactly such a thing, and it is not characterised here).
- **`obDR6` at t040 is alpha-clamped 100% of the leg.** Its point is kept in the fits because dropping
  it moves the exponent by 0.016; it is nonetheless a different flight condition from the other three.
- **The yaw-channel attribution in §5 is an inference from amplitude and phase, not a closed
  identification.** It is strong enough to refute the `kSettle` attribution (a 5–29% contributor
  cannot be the driver) and not strong enough to name the driver. Naming it needs the yaw A/B.

## 8. What R39-C cannot prove

- Anything about rotorcraft, STOL or a loaded jet. Ten fixed-wing keys, clean.
- Anything about large demand. One geometry: the 6° oblique diamond, four mirrored legs.
- Anything about whether the mode *matters*. Settled `azErr` p95 is 0.12–1.01°; the largest cell is
  `obUR6` t100 at 0.92° p95 on a 0.43 Hz cycle. No card in the corpus scores gun-solution dispersion,
  so "1° of nose wander at 0.43 Hz" has no cost attached to it yet.
- Anything about the `~1 Hz` feature the other nine airframes carry (coherence 0.15–0.35, 480 legs).
  It was found by this analysis and not investigated.
