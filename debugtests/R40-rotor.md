# R40 — rotorcraft: the first hover captures, and what they say

**48 captures, R39 run tag, mod v0.98.1, game 0.34.1** — 24 `rotor-hover` + 24 `rotor-bob`, three
airframes (`AttackHelo1` SAH-46, `UtilityHelo1` UH-90, `QuadVTOL1` VL-49), 36,516 rows. ONE-LAW
standing case 4 ("a hovering helo") had **zero prior captures in the corpus**; these are the first.

Everything below is scored **from the CSVs**, by importing `scorecard.py` and calling `score_run()` /
`wobble_scan()` directly — the post-repair scorer, per `R40-metric-repair.md`. **`captures.db` was
neither read nor written.** No `.cs`, no card JSON and no other findings document was touched.

---

## Verdict, up front

1. **The v0.58 rotorcraft branch of the control law never executed.** `_heloOk` is **false** on all 48
   captures: the rate-normalised helo command, the `wMaxP`/`wMaxY` authority bound and the tilt-driven
   regime blend are all inert. The airframes were flown by the **fixed-wing direct-P term** with
   `HeliYawScale = 2.0` bolted on — which is precisely the configuration the v0.58 comment says
   limit-cycles. Measured two ways (a row-by-row reconstruction that matches to a **median 0.0005**,
   and the absence of the probe's own unconditional log line across 12 probe events). Root cause is a
   **call-order bug**, located below. Everything else in this document is a measurement of the
   *pre-v0.58* law, not of the shipped rotorcraft law.
2. **The law does not hold in hover.** All three airframes end the 90° yaw-to-point step with a
   standing **azimuth** error of 0.9–33.4° against 43–92% pedal, and two of the three fall into a
   **rail-to-rail yaw limit cycle at 0.11–0.19 Hz, ±19–56°**, with the pedal on the stop 40–87% of the
   time. The cycle is **bistable** — it needs a kick of roughly 8° to enter and then self-sustains
   after the stimulus is removed. Pitch is clean everywhere (terminal `|elevErr|` 0.00–6.70°).
3. **The transition band is traversed, but only its upper half, and only by one airframe.**
   `UtilityHelo1` spends 36.2% of its rows in `heliBlend ∈ [0.2, 0.8]`; `AttackHelo1` and `QuadVTOL1`
   spend **0.00%**. Global minimum `heliBlend` anywhere in the batch is **0.369**. **There is no ~1 Hz
   pedal oscillation in the band** — `stickFlipRateY` is 0.000–0.083 /s over eighteen ≥4 s in-band
   runs, and one run in eighteen publishes a frequency at all (0.534 Hz, coherence 0.248).
4. **The hover captures are sound despite the slow-motion key.** Verdict and evidence in §0.
5. **The game models VRS.** Envelope identified exactly; **no segment entered it** — simulating the
   game's own filter over every row gives peak `VRSSmoothed` = **0.0000** on the reliable estimator.
6. **Neither card is a hover test.** `startSpeed: 0` fell through to `DroneSpawnSpeed = 50 m/s`; the
   fleet flew at 6–110 m/s, climbing 80–1500 m per capture, with no per-replicate reset. §5.

---

## §0 — Data validity: the slow-motion key

**Verdict: the hover data is sound. Every recorded aerodynamic quantity is unaffected. Only
wall-clock quantities moved.** Two independent lines of evidence, both dispositive.

### Which captures were affected, measured

`dt/dtWall` over each capture (`t` = `Time.time`, scaled; `tWall` = `Time.realtimeSinceStartup`,
unscaled — `Recording.cs:555`, and the header comment at `:131-136` names the ratio as the intended
diagnostic):

| card | captures | `dt/dtWall` | per-row range |
|---|---|---|---|
| `rotor-hover` recs 364–375 | 12 | **0.0500** | 0.040 … 0.051 |
| `rotor-hover` recs 376–381 | 6 | 0.139 … 0.394 | 0.049 … **1.087** (the key was released mid-capture) |
| `rotor-hover` recs 382–387 | 6 | **1.0000** | 0.943 … 1.047 |
| `rotor-bob` recs 388–411 | 24 | **1.0000** | 0.931 … 1.111 |

So 18 of 24 hover captures carry slow motion, six of them containing the transition; **`rotor-bob` is
clean, as the operator believed.**

### What the game actually scales — read, not assumed

- `GameplayUI.Update` (`:20735-20742`): the "Slow Motion" button toggles `GameSlowMotion` and calls
  `SetTimeFactor(GameSlowMotion ? 0.05f : 1f)`. **0.05 is exactly the measured ratio.**
- `GameplayUI.SetTimeFactor` (`:20810`) → `TimeScaleManager.Scale` (`:30913-30923`), whose setter body
  is **one statement**: `Time.timeScale = value`.
- **`Time.fixedDeltaTime` is never assigned anywhere in the 181k-line 0.34.1 decompile.** The
  identifier appears as an assignment target exactly twice and both are *reads* into other storage
  (`:169079` a shared-struct field read, `:170260` `reference.fixedDeltaTime = Time.fixedDeltaTime`).
  The mod does not write it either (`Time.timeScale` appears in the mod only as a read, `AimRig.cs:13`).

Unity semantics follow directly: `Time.fixedDeltaTime` is expressed in **scaled** seconds, so with it
unchanged every `FixedUpdate` advances the simulation by the same interval and the integration is
step-for-step identical; only the number of steps per *real* second changes. `ChaseController` uses
`Time.fixedDeltaTime` for its own `dt` (`:820`), and the recorder's sample throttle and `tSeg` clock
both key off `Time.time` (`:499-501`, `:262`), so nothing in the mod's loop sees the difference either.

### Confirmed against the data, not just the code

If the step size had changed, sample counts and segment durations in simulation time would move. They
do not:

| | tScale 0.05 (n=12) | tScale 1.00 (n=6) |
|---|---|---|
| rows per capture | **977** | **977** |
| capture duration in `t` | **61.0 s** | **61.0 s** |
| median row spacing in `t` | 0.0660 s | 0.0665 s |
| minimum row spacing in `t` | 0.0500 s | 0.0500 s |
| `frameMs` median / max | 16.7 / 16.8 ms | 16.7 / 16.9 ms |
| duration in `tWall` | 1219.7 s | 61.0 s |

Identical sample counts, identical simulation-time durations, identical rendered-frame time (so the
machine was never the bottleneck — the extra wall time is idle frames, not stall), and a 20× wall
clock. The six mid-capture transitions are the strongest form of this: a 20× step in integration
step size cannot happen inside a capture without a visible discontinuity, and the row spacing in `t`
does not move across it.

**Use the hover data.** The only quantities that are not comparable across the split are `tWall` and
anything derived from it — no metric in `scorecard.py` reads it.

---

## §1 — Does the law hold in hover?

### 1a. The headline: `_heloOk` is false, so the v0.58 rotorcraft path never ran

`ApplyEvolvedLegacy` has two rotorcraft-specific branches, both gated on `_collective && _heloOk`
(`ChaseController.cs:1926-1931`, `:1959-1961`): the pitch and yaw error terms become a **rate command
normalised by the probed authority**, `omegaDes = kHelo·err`, `stick = omegaDes / wMax`. With
`_heloOk` false they fall back to the pre-v0.58 direct-P terms.

**Measured — reconstruction.** Row-by-row rebuild of `outY` from the recorded state (`azErr`,
`elevErr`, `iYaw`, `yawRate`, `bigTurn`, `yawWeak`, `heliBlend`) and the capture's own `# config`
line, restricted to rows that are quasi-static (|Δ`outY`| ≤ 0.02 between samples, so the `slew=6.0`
output limiter is not in play), unsaturated (|`outY`| < 0.98), and where the two candidate branches
differ by more than 0.15:

| airframe | n rows | wins probe-**ON** | wins probe-**OFF** | median residual ON | median residual OFF |
|---|---|---|---|---|---|
| AttackHelo1 | 2176 | 7.4% | **92.6%** | 0.0223 | **0.0005** |
| UtilityHelo1 | 831 | 5.2% | **94.8%** | 0.0287 | **0.0016** |
| QuadVTOL1 | 165 | 21.8% | **78.2%** | 0.0271 | **0.0125** |

A median residual of **0.0005 stick units** is not a preference between two models, it is an identity.
The winning form is
`yErrTerm = local.x · sens · yawScale · (1 − YawWeakFade·assist)` with
`yawScale = Lerp(Lerp(1, TurnYawScale, bigTurn), HeliYawScale, heliBlend)` and
`assist = yawWeak·(1−bigTurn)·YawAssistStrength` — i.e. `ChaseController.cs:1956-1961`'s `else` arm.

> A first pass at this test got the *opposite* answer because it left `yawWeakFade` at 1.0. That
> variant (`OFFnofade`) wins **0.0%** of rows in the table above. The CSV's `assist` column is the
> game's `flightAssist` **bool** (0/1 per airframe), not the law's `assist` blend — the law's is not
> recorded and has to be rebuilt from `yawWeak` and `bigTurn`. Worth knowing before anyone else
> reconstructs a yaw command from these columns.

**Measured — the log, independently.** `ResolveHelo` logs `[helofbw] '<name>' enabled=… gLimit=…
maxAngularVel=… tiltwing=… swivelduct=… compound=…` **unconditionally** once it gets past its
`_collective` gate and finds the filter (`ChaseController.cs:661-665`), and logs `[helofbw] probe
failed` from its catch (`:671`). `LogOutput.log` contains **12 `[canard] resolve` lines** for the
rotor batch — one per aircraft-change edge, i.e. every spawn — and **zero `[helofbw]` lines of either
kind**. `ResolveHelo` therefore hit its `if (!_collective) return;` early exit every time.

**Root cause — call order.** `ResolveHelo` is reachable only from inside `ResolveFbw`'s
aircraft-change edge (`:520-525`, `if (id != _fbwAcId)`), and `_collective` is latched in
`BeginFrame` (`:725`). For a drone the **first** `ResolveFbw` call comes from
`ManeuverRecorder`'s `FbwHeader(ac)` while writing the `# fbw` header, which `ScenarioPlayer.StartCard`
triggers **before** `TestDrone.ChaseCard` ever calls `FlyUncrewed`. The log ordering shows it directly
(drone #77, `LogOutput.log:11864-11871`):

```
11864  [canard] resolve 'AttackHelo1' ...        <- ResolveFbw edge fires here; ResolveHelo runs with _collective == false
11865  [fbw] 'AttackHelo1' enabled=False ...
11866  [rec] recording -> ...-364-rotor-hover-...csv
11869  [card] 'rotor-hover' start (4 segments, 61s)
11870  [card] rotor-hover seg 1/4 'arm' (6s)
11871  WT Mouse Aim: ON (rotorcraft) - chase control engaged [drone]   <- _collective becomes true, seven lines too late
```

The edge is consumed (`_fbwAcId` is now set), so it is never re-probed for the life of the aircraft.
Consequences, all confirmed in the data:

- pitch and yaw run the fixed-wing gains (§1a above);
- `_twc`/`_sds` stay null, so the **v0.58 tilt-driven regime blend never ran on `QuadVTOL1`**, a
  `Tiltwing` — its `heliBlend` came from the speed ramp alone;
- `_hasCompound` is false regardless.

**This is not observable from a capture without reconstruction**, which is the reason it survived: the
CSV has no `heloOk` column and the drone path emits no probe line at all.

### 1b. Pointing performance, per airframe

Terminal 2.5 s mean, n=8 per cell (post-repair scorer):

| card / airframe / segment | \|azErr\| | \|elevErr\| | `off` | \|outY\| | \|outP\| |
|---|---|---|---|---|---|
| rotor-hover / AttackHelo1 / hover | 3.61 | 0.01 | 3.61 | 0.176 | 0.001 |
| rotor-hover / AttackHelo1 / hoveryawL | 0.91 | 0.01 | 0.91 | 0.045 | 0.001 |
| rotor-hover / AttackHelo1 / hoveryawR | **13.73** | 0.16 | 13.74 | 0.429 | 0.005 |
| rotor-hover / UtilityHelo1 / hover | 0.20 | 0.00 | 0.20 | 0.024 | 0.000 |
| rotor-hover / UtilityHelo1 / hoveryawL | **13.34** | 0.32 | 13.37 | 0.666 | 0.014 |
| rotor-hover / UtilityHelo1 / hoveryawR | **9.98** | 0.26 | 9.99 | 0.598 | 0.007 |
| rotor-hover / QuadVTOL1 / hover | **20.89** | 0.11 | 20.89 | 0.573 | 0.003 |
| rotor-hover / QuadVTOL1 / hoveryawL | **30.91** | 0.31 | 30.91 | 0.917 | 0.006 |
| rotor-hover / QuadVTOL1 / hoveryawR | **33.41** | 0.25 | 33.42 | 0.923 | 0.004 |
| rotor-bob / AttackHelo1 / bobup | 0.04 | 0.37 | 0.37 | 0.004 | 0.027 |
| rotor-bob / AttackHelo1 / bobdn | 1.23 | 0.07 | 1.13 | 0.095 | 0.007 |
| rotor-bob / UtilityHelo1 / bobup | 0.18 | 0.02 | 0.16 | 0.020 | 0.002 |
| rotor-bob / UtilityHelo1 / bobdn | 0.20 | 0.01 | 0.18 | 0.020 | 0.001 |
| rotor-bob / QuadVTOL1 / bobup | **17.76** | 3.10 | 16.84 | 0.489 | 0.069 |
| rotor-bob / QuadVTOL1 / bobdn | **32.92** | 6.70 | 31.03 | 0.882 | 0.061 |

**The residual is azimuth, essentially in full.** `|elevErr|` is at or under 0.37° on eleven of
fifteen cells; the pitch channel closes everywhere it is asked to. This is the mirror image of every
fixed-wing finding in the corpus, where the pitch relay is the recurring failure.

The 90° yaw step itself (`hoveryawR`, then `hoveryawL` back to boresight):

| airframe / seg | peak `off` | t to <10° | t to <5° | reached 5° | final `off` | peak yaw rate | `outY` rail % |
|---|---|---|---|---|---|---|---|
| AttackHelo1 / hoveryawL | 77.4° | 1.40 s | 1.46 s | 8/8 | 0.96° | 85.0 °/s | 24.5 |
| AttackHelo1 / hoveryawR | 93.7° | 2.38 s | 2.25 s | **4/8** | 13.29° | 50.6 °/s | 47.9 |
| UtilityHelo1 / hoveryawL | 91.3° | 2.81 s | 2.98 s | 8/8 | 5.61° | 37.1 °/s | 52.4 |
| UtilityHelo1 / hoveryawR | 89.8° | 7.61 s | 9.37 s | 8/8 | 8.46° | 29.5 °/s | 54.8 |
| QuadVTOL1 / hoveryawL | 136.1° | 4.37 s | 4.47 s | 8/8 | **37.59°** | 45.2 °/s | **88.1** |
| QuadVTOL1 / hoveryawR | 104.2° | 3.83 s | 3.94 s | 8/8 | **38.59°** | 43.4 °/s | **86.8** |

**The slew is not the problem — the settle is.** Every cell touches 5° within 1.4–9.4 s, and then
half of them leave again and never come back.

### 1c. What fails that does not fail on a fixed wing: a bistable rail-to-rail yaw relay

Direct measurement of `azErr` zero crossings, over the segment minus its first 4 s (so the slew-in is
excluded), plus the pedal-rail fraction over the same window:

| card / airframe / segment | relay f (Hz) | cells resolving | half-amplitude | `outY` rail % |
|---|---|---|---|---|
| rotor-hover / QuadVTOL1 / hoveryawR | **0.111** | 1/8 | 54.3° | 86.9 |
| rotor-hover / QuadVTOL1 / hoveryawL | **0.109** | 2/8 | 55.9° | 86.2 |
| rotor-hover / QuadVTOL1 / hover | **0.112** | 6/8 | 30.9° | 52.8 |
| rotor-bob / QuadVTOL1 / bobdn | 0.192 | 1/8 | 38.4° | 76.5 |
| rotor-hover / UtilityHelo1 / hoveryawL | **0.126** | 8/8 | 18.5° | 48.4 |
| rotor-hover / UtilityHelo1 / hoveryawR | **0.187** | 6/8 | 25.7° | 40.0 |
| rotor-hover / AttackHelo1 / hoveryawL | 0.379 | 8/8 | 16.1° | 4.0 |
| rotor-hover / AttackHelo1 / hoveryawR | 0.400 | 2/8 | 12.8° | 37.6 |
| every `rotor-bob` cell on the two true helos | — | 0/8 | ≤ 2.2° | 0.0 |

**Coherence, as required by the metric-repair rules.** These frequencies come from a **direct
zero-crossing count**, not from `osc_mode`, because the segments are too short for `osc_mode`'s own
evidence rule. Beside them, the rebuilt detector run manually over the same segments reports:
`UtilityHelo1/hoveryawL` **`wobbleCoherenceAzErr` = 0.493 ± 0.056 (n=6/8)** with
`wobbleCoherenceOutY` 0.527, `OutR` 0.444, `Bank` 0.477 — coherent across five signals at once — and
**`wobbleFreqHzAzErr` NULL on all eight**. `AttackHelo1/hoveryawR` publishes
`wobbleFreqHzAzErr = 0.337 ± 0.005 (n=2/8)` at `wobbleCoherenceAzErr = 0.196 ± 0.13`, and
`wobbleFreqHzOutY = 0.417 ± 0.096 (n=4/8)` at coherence 0.311. `wobbleEpisodes*` is **0 in every
cell**. All three readings are the same fact stated three ways: **four periods of a 0.11–0.19 Hz mode
is 21–36 s and the segments are 12–15 s long**, so the detector correctly declines to publish, and the
episode counter correctly sees fewer than its six crossings. The instrument is behaving; the *card* is
too short. See §5.

**The mechanism, from one trace** (`rec366`, QuadVTOL1, `hoveryawR` onward — `heliBlend` = 1.00,
`bank` < 3°, `tBankE` = 0 throughout):

```
 t(s)  seg          azErr    off   outY  yawRate(deg/s)  spd  vFwd    hB    iYaw
 34.1  hoveryawR     23.5   23.5   1.00        41.1     25.4   8.4  1.00   0.016
 35.0  hoveryawR    -12.3   12.3  -1.00        37.6     24.9  -6.6  1.00   0.009
 36.7  hoveryawR    -45.3   45.3  -1.00         0.7     23.7 -16.7  1.00  -0.120   <- iYaw at the +-0.12 cap
 39.3  hoveryawR     14.5   14.5   1.00       -35.5     22.4   5.3  1.00  -0.039
 41.1  hoveryawR     49.7   49.8   1.00        -3.7     22.3  15.8  1.00   0.120   <- and back
 43.7  hoveryawR     -1.7    1.7  -0.23        37.5     22.0  -0.8  1.00   0.048
 45.5  hoveryawR    -42.6   42.6  -1.00         5.6     21.1 -13.4  1.00  -0.102
```

Bang-bang pedal, `iYaw` slamming between its `iCap = 0.12` rails in antiphase, error amplitude
approximately constant. Peak yaw rate 43–45 °/s against `heloMaxAngularVel.y` = 0.8 rad/s = **45.8
°/s** — the plant is delivering its full probed authority. **This is an outer-loop relay, not an
authority shortfall.** With the probe off, the yaw error gain is
`sens · HeliYawScale · yawWeakFade` ≈ 3.0 × 2.0 × ~0.9 = **5.4 per radian**, which saturates the pedal
at **11° of azimuth error** — confirmed by the measured `|outY|` vs `|azErr|` curve, which reaches
1.000 by the 15–20° bin on all three airframes. Against a measured yaw plant lag of **0.59–1.39 s**
(peak cross-correlation of commanded body rate against recorded `yawRate`, r = 0.68–0.84, n=8 per
cell: AttackHelo1 0.59/0.80 s, UtilityHelo1 1.33/1.35 s, QuadVTOL1 1.39/1.39 s), a loop that saturates
at 11° is a relay by construction. The relay frequency ordering matches the lag ordering:
AttackHelo1 (shortest lag) 0.38–0.40 Hz, UtilityHelo1 and QuadVTOL1 (longest) 0.11–0.19 Hz.

**It is bistable, and that is the load-bearing part.** Per-replicate, `rotor-hover` `hover` segment
(a *constant boresight hold* — no stimulus at all):

| airframe | rec 1 / 2 | rec 3 / 4 | rec 5 / 6 | rec 7 / 8 |
|---|---|---|---|---|
| AttackHelo1 `azErr` rms | 0.43 / 0.43 | 0.03 / 0.02 | 2.39 / 2.26 | 6.31 / 5.91 |
| UtilityHelo1 `azErr` rms | 0.23 / 0.23 | 1.42 / 1.51 | 4.46 / 5.04 | 7.85 / 7.32 |
| QuadVTOL1 `azErr` rms | 0.01 / 0.02 | **35.45 / 34.50** | **37.10 / 36.40** | 0.01 / **30.90** |
| QuadVTOL1 `outY` rail % | 0.0 / 0.0 | 84.0 / 82.5 | 86.2 / 86.2 | 0.0 / 77.8 |

`rec384` and `rec387` are the decisive pair: same airframe, same card, same replicate index, both at
6 m/s and ~2160 m. `rec384` enters `hover` with `azErr` = **−0.1°** and stays at rms 0.01 for the full
25 s. `rec387` enters with **−7.9°** and spends the segment railed at ±50°. **A quiet equilibrium and
a ±50° limit cycle coexist, and roughly 8° of disturbance selects between them.** The cycle also
survives removal of the stimulus — replicates 3–6 carry it into the `hover` segment from the previous
replicate's yaw step and never recover inside 25 s.

Speed is the other axis: the QuadVTOL1 replicates that stay quiet are the ones entering at 42 m/s;
every relaying one enters at ≤ 15 m/s. Aerodynamic yaw damping is what is missing at true hover, and
the outer loop was never re-sized for its absence — because the branch that would have re-sized it
(§1a) did not run.

### 1d. Which rails engage

| rail | fixed-wing corpus | here |
|---|---|---|
| **yaw stick at ±1.0** | not a metric; not a fixed-wing failure mode | **0–88% of samples**, the dominant rail |
| `iYaw` at `iCap` = 0.12 | ±0.001 for a whole 30 s turn (R21) | pinned to both rails alternately, ~0.1 Hz |
| `bankClampActivePct` | the fixed-wing rail | fires 0–50%, **but see the caveat below** |
| `blendRailPct` | | 0.0 or column dead (`bWt` identically 0 on 17 captures) |
| `aoaAboveCeilingPct` / AoA gates | the fixed-wing rail | **cannot fire** — §3 |
| pitch stick at ±1.0 | | 0.0–1.8% |
| roll stick at ±1.0 | | **0.0% everywhere** |

**`bankClampActivePct` must not be read on a rotorcraft.** It reads `bankTR` (correctly, post-R40),
which is computed **upstream** of `tBankE *= (1f - _heliBlend)` (`ChaseController.cs:1875`). At
`heliBlend` = 1 the law deletes that demand entirely: measured, `|bankTR|` p95 reaches **76–86°**
against a 72° `maxBank` while `|tBankE|` p95 is **0.00** and the `tBankE` column is *dead* (identically
zero) on 19 of 48 captures. `scorecard.py` flagged `rotor-hover/AttackHelo1/hoveryawR` **RAILED at
100.0%** on this basis, on a segment where the roll channel never moved (`|outR|` median 0.005, roll
saturation 0.0%). This is the same shape as the defect `R40-metric-repair.md` fixed, one gate further
downstream: *before reading a metric as a rail, check the column is downstream of everything that can
delete it.*

---

## §2 — The transition band (`heliBlend` 0.2 … 0.8)

**Answer: partially traversed, upper half only, by one airframe; no ~1 Hz pedal oscillation there.**

Row coverage, % of each airframe's 12,172 rows:

| `heliBlend` bin | 0.0–0.2 | 0.2–0.3 | 0.3–0.4 | 0.4–0.5 | 0.5–0.6 | 0.6–0.7 | 0.7–0.8 | 0.8–0.9 | 0.9–1.0 | 1.0 |
|---|---|---|---|---|---|---|---|---|---|---|
| AttackHelo1 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 16.01 | 83.99 |
| QuadVTOL1 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.62 | 3.44 | 95.93 |
| UtilityHelo1 | 0.00 | 0.00 | 0.41 | 3.04 | 4.86 | 11.61 | 16.23 | 16.43 | 15.95 | 31.47 |

Per-capture minimum `heliBlend` over all 48 captures: AttackHelo1 **0.911**, QuadVTOL1 **0.817**,
UtilityHelo1 **0.369**. **Nothing in this batch ever goes below 0.369**, so the lower half of the band
(0.2–0.37) is untouched, and two of three airframes never enter the band at all.

Eighteen contiguous ≥4 s runs with `heliBlend ∈ [0.2, 0.8]` exist, **all UtilityHelo1** (~4,400 rows).
In them:

- **The predicted coexistence is real.** On the eight `rotor-hover` runs (spanning the `hover` →
  `hoveryawR` boundary at `heliBlend` 0.59–0.80) the partially-faded bank command is live:
  `|tBankE|` max **22.0–29.3°** and achieved `|bank|` max **33.7–46.5°**, while `|outY|` median is only
  0.027–0.067. So the law does bank a helicopter to 46° to make a turn while the yaw channel idles.
  On the ten `rotor-bob` in-band runs `|tBankE|` max is **0.0** and `|bank|` max ≤ 0.6° — because those
  segments carry no azimuth demand, so there is nothing for the bank channel to be asked for.
- **No pedal oscillation.** `stickFlipRateY` = **0.000–0.083 crossings/s** over all eighteen runs
  (fourteen of them exactly 0.000). A 1 Hz cycle would read ≈ 2/s.
- **One of eighteen publishes a frequency:** `rec410` `bobdn`, `wobbleFreqHzAzErr = 0.534 Hz` at
  **`wobbleCoherenceAzErr = 0.358`**, with `wobbleFreqHzOutY` the same 0.534 Hz at coherence 0.248.
  The other seventeen are NULL, with coherence spanning −0.31 … +0.47.

**Not exposed, not passed.** The 0.2–0.37 sub-band has no samples, so nothing about it is
established. Note also that the band was traversed *with `_heloOk` false* (§1a), so even the covered
part measured the pre-v0.58 law.

**The card that would answer it:** a rotorcraft **deceleration sweep** — enter at
`0.95 × infoMaxSpeed` on `UtilityHelo1` (Vmax 134 m/s) or `QuadVTOL1` (153 m/s), throttle pinned low
enough to decelerate through the whole 150 → 40 m/s ramp in one leg, with a **constant** small azimuth
demand (say 10°, inside the 11° pedal-saturation knee) held throughout. That sweeps `heliBlend` 0 → 1
monotonically with the turn channel excited but unsaturated, and `bankTR`, `tBankE`, `outR` and `outY`
recorded through the hand-off. `AttackHelo1` **cannot** fly it — see §3a. Give the leg **≥ 45 s** so
the wobble detector can resolve anything down to 0.09 Hz.

---

## §3 — ONE-LAW audit

### 3a. Confirmed violation, exposed and consequential: the hover-blend speeds are absolute

```
_heliBlend = Clamp01((HeliForwardSpeed - vFwd) / max(1, HeliForwardSpeed - HeliHoverSpeed))
```
(`ChaseController.cs:1089-1092`; this batch flew `heliFwd=150`, `heliHover=40`, off the `# config` line.)

Both are **absolute m/s constants** with no reference to anything probed. Against each airframe's own
`infoMaxSpeed` from its sidecar:

| airframe | `infoMaxSpeed` | lowest reachable `heliBlend` | lowest observed |
|---|---|---|---|
| **AttackHelo1** (SAH-46) | **100 m/s** | **0.455 — at its own Vmax** | 0.911 |
| UtilityHelo1 (UH-90) | 134 m/s | 0.145 | 0.369 |
| QuadVTOL1 (VL-49) | 153 m/s | 0.000 | 0.817 |

**`AttackHelo1` can never leave the hover regime at any speed it is capable of flying.** Its bank
command is permanently attenuated by at least 45%, its `blendWeight` roll-to-align permanently
suppressed by at least 45%, and its yaw scale permanently boosted at least 45% of the way to
`HeliYawScale`. The same schedule puts `QuadVTOL1` at full fixed-wing behaviour at its Vmax. This is
exactly the named signature: one constant, three airframes, three different places relative to their
own envelopes. The probed replacement is already in the sidecar and already read by the harness —
`infoMaxSpeed`, or `ControlsFilter.FlyByWire.cornerSpeed` via the same public accessor
`TestDrone.FbwCornerSpeed` uses.

### 3b. Confirmed violation, currently the *dominant* one: `HeliYawScale` is an absolute stick gain

`yawScale = Lerp(yawScale, Cfg.HeliYawScale.Value, _heliBlend)` (`:1957`) multiplies the error→stick
gain by a flat **2.0** for every rotorcraft, with no reference to the probed `heloMaxAngularVel`. With
`_heloOk` false it is the *only* thing setting rotorcraft yaw authority, and it puts the
pedal-saturation knee at **11° of azimuth error** on all three airframes identically — measured
`|outY|` reaches 1.000 by the 15–20° bin for AttackHelo1 (`maxAngularVel.y` 1.0 rad/s), UtilityHelo1
(0.8) and QuadVTOL1 (0.8) alike, despite a 1.25× spread in their actual yaw authority. That saturation
knee is the direct cause of the §1c relay.

The v0.58 comment anticipates this precisely ("HeliYawScale is deliberately NOT applied in this branch:
normalization already delivers the hover yaw authority that knob existed to patch (it still works when
the probe is unresolved)") — the fallback is doing exactly what it was designed to do, in a case that
was supposed to be rare and is in fact universal.

### 3c. Assumed-constant plant lag behind `kHelo`

`const float kHelo = 2.0f` (`:1924`) is justified in place by "an integrator plant with ~0.3 s lag goes
unstable near pi/(2*0.3) ~ 5 s^-1; 2.0 leaves ~55 deg phase margin", and defended as
"plant-independent because the probe supplies each airframe's true authority". **The probe supplies the
authority, not the lag**, and the lag is what sets the margin. Measured here (peak cross-correlation of
commanded body rate against recorded `yawRate`, n=8 per cell):

| airframe | hoveryawL | hoveryawR | r |
|---|---|---|---|
| AttackHelo1 | 0.798 s | 0.594 s | 0.68–0.80 |
| UtilityHelo1 | 1.330 s | 1.353 s | 0.78–0.84 |
| QuadVTOL1 | 1.386 s | 1.386 s | 0.76–0.78 |

**2.0–4.6× the assumed 0.3 s, with a 2.3× spread across the roster.** Applying the comment's own
stability bound `π/(2τ)` gives 1.13 s⁻¹ for UtilityHelo1 and QuadVTOL1 — **below** the 2.0 the constant
is set to. Caveat, stated: this is a peak-cross-correlation lag over a segment containing a large step,
so it mixes transport lag with step-response shape; the *spread* is the robust part, and the spread
alone refutes "plant-independent". This is latent, not active — the branch it sits in never ran.

### 3d. The three named violations are structurally **not exposed** on rotorcraft

All three sit inside `if (!_collective)` or `if (fbwOk)`, and `fbwOk = fbwResolved && !_collective`
(`ChaseController.cs:1140`):

| violation | site | gate | exposed here? |
|---|---|---|---|
| `aoaMargin = Min(4f, 0.15f*lim)` | `:1216` | `if (!_collective)` `:1208` | **no** |
| `aoaFade = Max(4f, Min(6f, 0.25f*lim))` | `:1222` | same | **no** |
| `omegaMax *= Max(0.3f, aoaGateUp)` | `:1296` | `if (fbwOk)` | **no** |
| `qSched = Clamp(qRatio, 0.3f, 1f)` | `:1152` | `if (fbwOk)` | **no** |

Confirmed in the data, not inferred from the gate: across **all 36,516 rows**, `qSched`, `aoaGU` and
`aoaGD` take exactly **one value each — 1.000** — and `aoaRec`, `settleOn` and `pEff` are constant
(`aoaRec`/`settleOn` identically 0 and withdrawn by the dead-column invariant; `pEff` held at its 1.0
init, confirming the `!_collective` guard on the estimator).

**So the brief's expectation is inverted: hover is a low-`q` regime, and it is the one regime where the
`qSched` floor cannot fire at all.** `else _alphaSchedFilt = 1f;` (`:1282`) makes it explicit. That is
"not exposed", not "passed" — and it means these captures contribute **no** evidence for or against
those three floors. The same applies to the B2 micro-bank constants (`settleGate` 0.5, `kSettle` 8,
`settleCap` 4, `:1860-1864`), gated `!_collective`; `settleOn` is dead on all 48.

### 3e. The helo path's own floors — measured inert

`wMaxP = Max(0.05f, Min(_heloMaxAngVel.x, _heloGLimit * 9.81f / Max(vMag, 10f)))` (`:1928`) carries two
absolute floors. Against the probed roster values (`heloMaxAngularVel` = [0.8, 1.0, 1.2] / [0.8, 0.8,
1.2] / [0.5, 0.8, 0.8], `heloGLimit` = 3 / 4 / 3):

- the `Max(vMag, 10f)` floor gives `gLimit·9.81/10` = 2.94–3.92 rad/s, which is 3.7–7.8× above every
  roster `maxAngularVel.x`, so `Min()` always takes the probed value and the floor **is structurally
  unreachable on all three airframes**;
- the `Max(0.05f, …)` floor is likewise unreachable (the binding term is 0.5–0.8 rad/s at hover speeds,
  and 0.60–0.68 at the 43–65 m/s these actually flew).

Both are latent hardcodes rather than active violations. The `10f` in particular is a mod-side
invention where the game's own analogue is `Max(V, 0.75·Vc)` — airframe-relative. Not worth changing
on this evidence; worth noting so the next reader does not have to re-derive it.

---

## §4 — Vortex ring state

**The game models VRS. Nothing in this batch entered it.**

### The mechanism, read from `RotorShaft.RotorPhysics` (`:37318-37391`)

```
vector2 = wind + unitPart.rb.velocity                                            :37336
num4    = |dot(xform.forward, vector2)| + |dot(xform.right, vector2)|            :37374   // in-plane airspeed, L1
b       = Clamp01( Min(dot(vector2, -xform.up), 10) - (VRSThreshold + num4*0.4) ) :37378
VRSSmoothed = Lerp(VRSSmoothed, b, 0.25f * Time.fixedDeltaTime)                  :37379
...
vector -= VRSSmoothed * VRSStrength * xform.up;                                  :37338
```

`VRSThreshold` = **4** and `VRSStrength` = **1** (serialized, `:36960`/`:36963`). So:

> **VRS engages when the descent rate through the rotor disk exceeds `4 + 0.4 × (in-plane airspeed)`
> m/s, and saturates 1 m/s above that.**

The penalty is applied the same way as `downdraft`: an extra downward component is added to the
freestream the blade-element sampler sees (`:37338`, feeding `SwashRotor.SampleForces` →
`CalculateLift(..., VRSFactor, ...)` `:37782`), reducing inflow angle and therefore lift. The filter
constant is `0.25/s`, i.e. a **~4 s** time constant in both directions. It is a first-class modelled
effect: `GetVRSFactor()` (`:37101`) is consumed by the `VRSWarning` HUD app (`:46401-46487`, with its
own `inVRS` latch) and by the helicopter AI, which abandons its destination and flies straight ahead
above `VRSFactor > 0.4` (`:15515`, `:15547`, `:15553`).

### Did any segment enter it?

Simulating the game's own filter row by row over all 48 captures (same `b`, same `Lerp`, same 4 s
constant), under two attitude interpretations because the CSV carries no full attitude:

| estimator of `dot(v, −shaft up)` | peak `VRSSmoothed` over the whole batch |
|---|---|
| world-frame descent rate `−velY` (exact for a level rotor; `\|bank\|` p95 ≤ 15° on 20 of 21 cells) | **0.0000** on every one of 21 card/airframe/segment cells |
| `spd·sin(aoa)`, the most VRS-favourable reading (over-states inflow whenever there is sideslip, which a hovering rotorcraft in a yaw step has in quantity) | 0.0590, on one cell (`rotor-bob/QuadVTOL1/bobdn`); **0.0000** on the other twenty |

The closest single row anywhere, on the reliable estimator, misses the threshold by **1.3 m/s**
(`rotor-bob/UtilityHelo1/bobdn`: 22.5 m/s median descent against a `4 + 0.4×58.9 = 28.4` m/s
threshold at that forward speed). Against the AI's own 0.4 reaction level and a 4 s build-up
constant, a momentary 1.3 m/s shortfall on one row is not close.

**So descent-rate lift loss is not available as an explanation for anything here.** Two further
reasons it was never going to be:

- **`rotor-bob` does not command a descent.** The mod writes an **aim direction** and a throttle pin;
  it never modulates collective. `bobdn` (`el: -25`) is a nose-down *pointing* step. What it actually
  produced: `UtilityHelo1` −22.5 m/s (at 60 m/s forward — a fast descending translation, the opposite
  corner of the envelope from VRS), `AttackHelo1` **+1.0 m/s** and `QuadVTOL1` **+0.5 m/s** — both
  still climbing during the "descend" segment.
- The two airframes that stayed nearly level are the two with the pointing failures, and their
  failures are **azimuth** (§1b), which no lift mechanism can produce.

---

## §5 — Harness defects

Reported separately from the law findings, in rough order of how much measurement they cost.

**H1 — Neither card flew the condition it names, and `startSpeed: 0` is the reason.**
Both cards declare `"startSpeed": 0` meaning *hover*, with a note reading "UNGATED (startSpeed 0): no
placement, no collective ownership and therefore NO per-replicate reset — establish the hover by hand
and fly one replicate." `SpeedOf` reads 0 as "the card doesn't say" and falls back to
`DroneSpawnSpeed`. The launch line records it plainly (`LogOutput.log:11859-11860`):
`launching 6 x 'AttackHelo1,UtilityHelo1,QuadVTOL1' ... at 1000 m / 50 m/s` with
`50 m/s [DroneSpawnSpeed]`. Measured consequence: the fleet flew at **6–110 m/s** and climbed
**+81 to +321 m median per capture, to 2761 m maximum**. `rotor-hover`'s `hover` segment records
`driftRateMS` of **42.6 ± 2.6** (AttackHelo1) and **65.3 ± 5.8** (UtilityHelo1) m/s, with
`positionRMSM` 308 and 474 m. The trailing replicates *do* decay into something like a hover — which
is the only reason §1c has any true-hover data at all, and it arrived by accident. CLAUDE.md already
flags this exact interaction under v0.92; the cards were never updated for it.

**H2 — Ungated cards were flown 4× in an unattended batch queue, so replicates are not exchangeable.**
No `# entry` header line on any of the 48 captures (headers present: `# mouseaim`, `# started`,
`# aircraft`, `# drone`, `# card`, `# config`, `# fbw` — nothing else), confirming `PlaceOnCondition`
never ran and therefore neither did `ChaseController.Forget`. Each replicate starts wherever the last
one ended. The drift is monotone and large: QuadVTOL1's entry speed to the `hover` segment goes
42 → 15 → 9 → 6 m/s and its altitude 1018 → 1463 → 1829 → 2157 m across the four replicates, and its
`azErr` rms goes 0.01 → 35.5 → 37.1 → 0.01/30.9. **`n = 8` overstates the sample.** It is 4 conditions
× 2 lanes, and the two lanes of an airframe are near-duplicates (AttackHelo1 `hover` rms
0.43/0.43, 0.03/0.02, 2.39/2.26, 6.31/5.91). Any aggregate over the eight is averaging a
monotone trend, not a noise floor.

**H3 — `hover_hold` and `bobup` get no oscillation metrics at all.** `scorecard.py:1468` and `:1475`
are the only two dispatch arms that do **not** call `wobble_scan` (`az_step`, `fine_track`,
`sustained_turn`/`alpha_hold`, `reversal`/`astern_wrap`, `oblique_step`, `alpha_step` all do). Those
two arms are **the entire rotorcraft vocabulary** — `hover`, `hoveryawR`, `hoveryawL`, `bobup`,
`bobdn`. So the shipped scorer produces **no `wobbleFreqHz*`, no `wobbleCoherence*`, no
`wobbleEpisodes*` and no `stickFlipRate{P,R,Y}`** for any rotorcraft segment ever recorded — while the
v0.58 helo work exists *because of* reported rotorcraft wobble, and §1c is a rail-to-rail limit cycle.
Adding `metrics.update(wobble_scan(t, rows, cols, dur))` to both arms costs nothing and is the single
highest-value change here.

**H4 — `hover_hold` gets no step-response metrics, but `hoveryawR/L` is a 90° azimuth step.**
`riseTime90` / `settleTime` / `overshootDeg` are computed for `az_step` only. `hoveryawR` is an
az-step in everything but its tag, and the numbers in §1b's step table had to be computed by hand.

**H5 — Segment length cannot resolve the mode that is present.** `osc_mode` requires ≥ 4 periods in
the settled window (correctly). The measured rotorcraft mode is 0.11–0.19 Hz, i.e. 21–36 s for four
periods, against 15 s `hoveryaw` and 12 s `bob` segments. The detector's own outputs say so
consistently — high `wobbleCoherence` (0.41–0.53 across five signals on
`UtilityHelo1/hoveryawL`) with `wobbleFreqHz*` NULL and `wobbleEpisodes*` 0. **A rotorcraft card needs
≥ 45 s scored segments.**

**H6 — `aoaPeakDeg` / `aoaLimiterActivePct` are meaningless below ~20 m/s and are reported anyway.**
`aoaNow` is the signed angle between the nose and the velocity vector, computed whenever
`|v|² > 4` (i.e. above 2 m/s). At hover the velocity is dominated by its vertical and lateral
components, so the "angle of attack" reads whatever the translation direction happens to be:
measured `aoaPeakDeg` **174 ± 8** on `rotor-bob/QuadVTOL1/bobdn`, **150 ± 5** on
`rotor-bob/AttackHelo1/bobdn`, **133 ± 21** on `rotor-hover/QuadVTOL1/hoveryawR`. These are printed
without qualification beside real numbers. (It also affects the offline VRS reconstruction in §4 —
see the caveat there.)

**H7 — `bankClampActivePct` fires spuriously on rotorcraft.** Detailed in §1d. It reads a demand that
`heliBlend` deletes before it reaches an actuator, and it produced a **RAILED** verdict on a segment
whose roll channel never moved.

**H8 — `dmgFrac` is 0 on all 36,516 rows and is withdrawn by the dead-column invariant on every
capture**, exactly as `R40-metric-repair.md` predicts. Recorded here only to confirm the invariant is
firing on a batch it has not previously seen.

**H9 — the `thr` column and the card-start log disagree.** Every capture records `thr = 0.600`
constant, while `[card] 'rotor-hover' start ... throttle 1.00` is logged for all twelve card starts.
This batch is v0.98.1, i.e. **before** v0.99.1 refcounted the shared config pins — two cards and six
lanes is precisely the configuration that release fixed. Whichever number is right, one of the two
artifacts is lying about the collective setting on a card family where collective is the only energy
input. Worth resolving before any rotorcraft card is re-flown.

**H10 — the probe result is not observable from a capture.** §1a took a reconstruction to establish.
There is no `heloOk` column, and the drone path emits no `[helofbw]` line because the probe's early
return precedes its own logging. A one-bit column, or moving the log line above the `_collective`
gate, would have made this a grep.

---

## §6 — What to fly, and in what order

1. **Fix the probe order first; nothing else here is worth re-measuring until then.** Everything in
   §1 and §2 measures the pre-v0.58 law. The cheapest correct fix is to re-probe when `_collective`
   changes rather than only when the aircraft changes — the `_fbwAcId` edge is the wrong edge for a
   flag that is written after it. **Pass signal:** a `[helofbw]` line per rotorcraft spawn, and the
   §1a reconstruction flipping to probe-ON.
2. **Re-fly `rotor-hover` with a real hover entry and long segments.** `startSpeed` expressed so the
   spawn does not fall back to `DroneSpawnSpeed`, `repeat: 1` (the card is ungated, so replicates are
   not independent — H2), and `hoveryawR`/`hoveryawL` at **45 s**. Airframes: all three.
   **Pass:** terminal `|azErr|` < 2° on all three, `outY` rail % < 10, and `wobbleCoherenceAzErr`
   below 0.3 with no frequency published in 0.08–0.5 Hz. **Fail:** the §1c signature returns —
   pedal railed > 40%, `iYaw` alternating between ±`iCap`, `azErr` amplitude > 15° at 0.1–0.4 Hz.
3. **A bistability probe, because that is what §1c actually is.** Same card, one extra segment: after
   the aircraft is quiet at boresight, inject a **single small azimuth step of 5°, then 8°, then 12°**
   with 30 s of hold after each. **Pass:** all three return to and stay under 1°. **Fail:** one of
   them drops into the ±50° cycle and stays there — which is what `rec387` (entered at −7.9°, relayed
   for 25 s) did against `rec384` (entered at −0.1°, rms 0.01) at byte-identical conditions.
4. **The transition-band card from §2** — a `UtilityHelo1`/`QuadVTOL1` deceleration sweep through
   `heliBlend` 0 → 1 at a constant 10° azimuth demand, ≥ 45 s. **Pass:** `bank` and `outY` hand over
   monotonically with no sign reversals in `outR`; `off` stays under 3° throughout.
   **Fail:** a hand-off transient anywhere in 0.2–0.8, or the roll channel fighting the yaw channel
   (use `flightscore.py`'s `xfightPct`).
5. **`rotor-bob` needs redesigning or retiring.** It cannot command a vertical maneuver: the mod writes
   an aim direction and never touches collective (§4). If a genuine descent test is wanted — and it
   would be worth having, since VRS is real and its envelope is now known exactly — it needs a card
   that pins a **low** throttle, and the pass/fail signal is the descent rate against
   `4 + 0.4 × Vh` m/s rather than anything the control law does.
