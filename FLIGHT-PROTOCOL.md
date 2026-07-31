# Flight protocol — v0.82 … v0.89

Six changes shipped without a single flight. This is the order to fly them in, and it is an
**order, not a menu**: each gate validates the instrument the next step measures with. A gate that
fails invalidates everything below it, so stop there rather than collecting data that cannot mean
anything.

Gates A, B, C and D have all flown (R22/R23/R24/R25) and all four passed; **the experiments below
are still unflown**, and every "expected" number from there down is a **prediction**. A prediction
that misses is a result, not a failure of the test — write down what actually happened. Two of the
passed gates corrected a claim made above them, so read each `### R__ result` section, not just the
verdict.

| ver | change | flown |
|---|---|---|
| 0.82 | `ChaseController` per-aircraft | **crewed, Gate A** |
| 0.83 | relative turn lead + stall-gated integrator | **A/B'd crewed (R23) and on the drone (R24) — same sign, same size** |
| 0.84 | entry reset + ABBA arms | **ABBA yes, Gate B; reset only PARTLY works — see B result** |
| 0.85 | below-nose roll-to-align loop broken | not exercised — `fixedwing-sweep` is above-nose |
| 0.86 | `ScenarioPlayer`/`ManeuverRecorder` per-aircraft, `frameMs` | **crewed, Gate A** |
| 0.87 | drones fly the real control law | **PASSED Gate C (R24) and Gate D (R25)** |
| 0.88 | trimmed entry placement (from Gate A) | **flown Gate B — disproved, reverted in 0.89** |
| 0.89 | 0.88 reverted; placement-tick reset defect measured | **flown Gate C (R24) + D (R25) — the defect reproduces on the drone** |

Total for gates A–D: **~25 minutes**. Do not skip to the experiments.

---

## Gate A — the rig does not drift (no drones) — **PASSED 2026-07-29 (R22)**

The most important test in this document. Until it passes, **no A/B result is admissible** — a
first-half/second-half split of a single *unchanged* arm previously beat its own detection
threshold, i.e. doing nothing scored as significant.

**Setup.** `DroneEnabled` **off**. `ScenarioArmToggle` **empty**. `ScenarioForceEntry` on.
`ScenarioRepeat` 8. Card: `fixedwing-sweep`. Multirole1, clean. Do not touch the stick.

**Pass — both:**
- first-sample `spd` within **±0.3 m/s** across all 8 captures
- **the null split.** Split the batch in half by run index and score both halves. **No metric's
  between-half difference may reach the batch's own detection threshold** (≈1.4·sd at n=4/arm).
  Nothing changed between the halves, so anything that clears it is the rig inventing an effect.

**Fail.** The reset is leaking. Read the `# entry` header line — it records `snapBackM`, the
pre-placement speed/altitude, the fuel write and `ctrlReset`, i.e. what the reset had to
undo. (It carried `aoaTrim` too when this was written; v0.88 added that field and v0.89 removed it.) **Stop here.** Everything below assumes replicates are exchangeable.

### R22 result

| criterion | measured | verdict |
|---|---|---|
| `spd` spread at row 0 | **0.10 m/s** (250.1–250.2) | pass |
| null split, worst metric | `overshootElDeg` **1.37 sd** vs 1.40 threshold | pass |
| null split, `terminalOffDeg` | 0.93 sd | pass |

Noise floor on `turn360`, n=8: `terminalOffDeg` sd **0.046° (0.5%)**, `rmsPointingErrorDeg` 0.093°
(0.9%), `gSustained` 0.1%, `meanTurnRateDegS` 0.1%. Entry provenance is tight — replicates 2–8 all
snap back ~1740 m and arrive within **0.1 m/s** of each other. `iPitch`/`iYaw` read exactly 0.0000 on
every first row, so v0.84's `ctrlReset` does what it claims.

### Two criteria in the original gate were wrong — both replaced above

Written before there was a noise floor to write them against, and both flagged a rig that passes.

- **`|outP| < 0.05` at the first sample** measured the wrong quantity at the wrong tick. It was
  written to catch a *stale aim demand*; the signal for that is `off` at row 0, which reads
  **0.02–0.08°** — clean. `|outP|` reads 0.146 on seven of eight runs, identical to three decimals,
  because it is a **deterministic entry transient**, not drift: the placement wrote AoA = 0 and the
  FBW is catching the resulting 1-g drop. It is gone by t+0.7 s, well inside the 6 s `arm`. The
  criterion is dropped rather than retuned because `off0` already covers what it was for.
  ~~Fixed at source in v0.88.~~ **It was not.** v0.88's trimmed placement was disproved by this very
  batch and reverted in v0.89 — see Gate B finding 1 below, which contradicts this sentence and is
  the one to believe. The AoA = 0 explanation for the transient is *also* what R23 disproved; the
  transient is real, its cause is not the trim.
- **`|r| < 0.4` on `terminalOffDeg` vs run index** is the wrong *statistic*. Correlation has no
  effect-size floor: as noise falls, any residual trend drives |r| → 1, so a perfectly reproducible
  rig fails it. Measured r = **−0.885** — across a total range of **0.11°** on a 9.4° mean, with
  sd 0.046°. Real, and ~2% of the smallest effect any experiment here is hunting (E1 predicts 5.4°).
  The null split replaces it because it compares drift against the batch's own detection threshold
  instead of against an absolute number. The residual trend is also what ABBA interleaving exists to
  cancel, and Gate A deliberately runs with **no** arm schedule, so this batch is the worst case.

---

## Gate B — captures are labelled correctly (no drones)

**Setup.** As above but `ScenarioRepeat` 4 and `ScenarioArmToggle = RelativeTurnLead`.

**Pass — all four:**
- 4 CSVs named exactly as v0.85 named them — `mouseaim-rec-v0.87.0-R<n>-01..04-fixedwing-sweep-*.csv`,
  **no `d<n>` or airframe segment** (that discriminator must never appear on a crewed capture)
- each has **64 columns ending `frameMs`**
- `arm=` alternates **A, B, B, A** across the four
- `compare-runs.py` on all four reports **one** airframe group, **no** unbalanced-arm warning

**Fail.** `d<n>` present = the drone discriminator leaked into the crewed path. `arm=` not
alternating = the v0.86 ownership guard misfiring on a single aircraft. `frameMs` all zero =
`FrameDt` not sampling with the harness off, which it is meant to do always.

### R23 result — **PASSED 2026-07-29**, all four

| criterion | measured |
|---|---|
| filenames | `mouseaim-rec-v0.88.0-R23-01..04-fixedwing-sweep-*.csv` — no `d<n>`, no airframe segment |
| columns | **64**, last three `bWt,phiLead,frameMs`; `frameMs` = 16.7 throughout |
| `arm=` | **0, 1, 1, 0** = A,B,B,A, all with `armKnob=RelativeTurnLead` |
| `compare-runs.py` | one airframe group (`multirole1`), split A n=2 / B n=2, no unbalanced-arm warning |

The log confirms the scheduler announced itself and cleaned up: `A/B arms on 'RelativeTurnLead'
(A = OFF, B = ON): ABBA — 2 A / 2 B … restored to True when the suite ends`.

### Two findings out of the same batch

**1. The v0.88 entry trim was aimed at a phantom, and is reverted in v0.89.** Run 01 is the run's
first placement, so no trim had been measured and it was written **untrimmed** — the exact AoA = 0
condition v0.88 blamed for the thump. It has the cleanest entry of the four: AoA 0.07° → 1.46° with
*no overshoot*, `off` peak 0.59°, against 2.74–2.87° and 1.72–1.97° on the three trimmed replicates.

**2. The per-replicate controller reset does not take effect on the placement tick.** At
`tSeg=0.000` of every *placed* capture the controller still holds pre-placement state:

| signal | runs 02–04 (placed) | run 01 (no preceding card) |
|---|---|---|
| `rollRate` | **−58.99 / −58.66 / −58.65** | −0.16 |
| `rollRateF` | −12.83, bleeding out over ~0.2 s | ~0 |
| `headingRateFilt` | 10.4 / 19.0 / 19.3 | 0.00 |
| `leadDeg` | **6.8 / 12.4 / 12.5°** against a 0.04° error | 0.00 |

`rollRate = (t.up − _prevUp)/dt`, so −59 requires `_prevUp` at the *banked* attitude — the placement
snaps a ~79° banked turn wings-level in one fixed step and the difference straddles it (Δup·right
≈ 1.18 over dt 0.02). Direct measurements on that row (`bank`, `alt`, `pos`, `spd`, `aoa`) are all
correctly post-placement; only derivatives are poisoned, which a freshly-`Forget`-ed instance cannot
do. `PlaceOnCondition` calls `ChaseController.Forget(ac)` and logs `controller reset` right after it,
so the call happens and does not stick.

**Deliberately unfixed.** A discontinuity guard on the finite difference would clean up `rollRate`
while leaving `headingRateFilt`/`leadDeg` untouched — the symptom would look fixed and the cause
would hide. **This does not invalidate Gate A or B**: the transient is deterministic (the three
placed runs agree within 0.02 on every affected signal) and decays inside the 6 s `arm`, before the
scored segment starts.

> **Scope correction, 2026-07-30 (R32).** The sentence above is true of Gate A/B and of R28 — and
> **only** of the light, high-`gLimitPositive` airframes both were flown on. The transient's
> distribution is **bimodal**, and everything measured until R32 was the lower mode. Over R32's 58
> placed `Darkreach` captures: median `|rollRate|` at `tSeg=0` is **0.753** (R28's 0.725, reproduced)
> but **19 of 58 exceed 5**, max **54.2**; `|leadDeg|` reaches **314°**, `|headingRateFilt|`
> **483 °/s**, and **`|outP|` rails at 1.000 on 15 of 58 placement ticks**. The magnitude is set by
> the attitude the *previous* replicate ended in, so once a lane has one bad replicate the next
> placement injects a full-authority spurious command on tick zero — and on a 105 t airframe with
> `maxPitchAngularVel = 0.3` that departs it **inside the `arm` segment**, before the card has
> demanded anything. So: deterministic yes, harmless **no**, and "decays before the scored segment"
> must be read as an airframe-scoped observation rather than a property of the rig.
> See [`debugtests/R32-FINDINGS.md`](debugtests/R32-FINDINGS.md) §8. Still deliberately unfixed, for
> the unchanged reason in the paragraph above.

### This retracts one Gate A claim

Gate A concluded "`iPitch`/`iYaw` read exactly 0.0000 on every first row, so v0.84's `ctrlReset` does
what it claims." **That is not evidence.** R21 measured `_iPitch` at ±0.001 against a 0.12 cap for an
entire 30 s turn, so it is ~0 coming out of a turn whether or not anything reset it. The A-batch
observation stands as a fact; the inference drawn from it does not. Gate A's own pass criteria
(`spd` spread and the null split) are unaffected.

---

## Gate C — one drone flies the law — **PASSED 2026-07-29 (R24)**

**Setup.** `DroneEnabled` **on**, `DroneCount` **1**, `DroneAirframe` `Multirole1`,
`DroneSpawnAlt` 4000, `DroneSpawnSpeed` 250 (matched to the card's entry condition). Tick one
fixed-wing card. Press the spawn key. **Do not touch the stick.**

**Pass.** Log shows `[card] entry condition set:` → `[card] '<name>' start` →
`WT Mouse Aim: ON (fixed-wing) — chase control engaged [drone]`. A CSV appears named
`…-d1-Multirole1-…`. `scorecard.py` emits per-segment metrics with **no `unknown` tag warnings**,
and `terminalOffDeg` on the sweep segments lands in the same band as a crewed capture of the
same card.

**Fail — each signal means one specific thing:**

| signal | meaning |
|---|---|
| `outR` matching `2.0·t.right.y`, `thr` 0.6 | the built-in level-hold — the card never started; grep `[card] no enabled card matches airframe class` |
| `thr` ≠ `ScenarioThrottle` | `OwnInputs` not landing before `FilterInputs`. **This is the R18 signature** and it reads as an energy failure, not a throttle bug |
| `reason=abort: no aim demand written` / `abort: the instructor is not flying` | the new refusals fired — real, not noise |
| `the placement injected velocity`, or G damage at spawn | the first-pilot-step deferral wasn't late enough; move the start behind a fixed-step count |

### R24 result — **PASSED 2026-07-29**

v0.89.0, session `20260729-220059`. **One F2 press** launched drone #1 (`Multirole1` = KR-67 Ifrit),
which flew `fixedwing-sweep` **4× unattended**.

| criterion | measured |
|---|---|
| log sequence | `[drone] #1 'Multirole1' spawned at (8000, -32, 719) local / 4000 m MSL, 250 m/s, hdg 0deg. 1 live.` → `[card] entry condition set` → `WT Mouse Aim: ON (fixed-wing) — chase control engaged [drone].` ×4 |
| CSV name | `d1-Multirole1` present on all 4; every one `reason=card 'fixedwing-sweep' complete`, dur 36.0 s, 576–578 samples |
| scorecard tags | `arm` (EXCLUDED) + `turn360` only — **no `unknown` warnings** |
| `thr` | **0.700 constant** = `ScenarioThrottle` exactly. Not the R18 signature |
| `outR` | first 0.000, range −0.034…0.705, mean −0.005 — the control law, **not** the `2.0·t.right.y` level-hold |
| `frameMs` | **16.7 constant** across all four; zero `[drone] frame hitch` during the runs (the 617/767/1048 ms hitches in the log are all *after* run 04, around the quit) |
| ABBA | `arm=` **0, 1, 1, 0** on `armKnob=RelativeTurnLead`, restored to True at suite end |
| entry audits | run 01 `snapBackM=0.0` (anchor capture), runs 02–04 `snapBackM=1763.2 / 1730.3 / 1719.6`, all `v=…->250.0 alt=…->4000.0 fuel=…->1.000 ctrlReset=1`. No damage |

A fifth capture (`d2-…-05`) from a second F2 press aborted at 8.4 s with `reason=abort: aircraft
gone` when the mission was quit (`QuitMissionButton.QuitGame` in the log) — a clean, correctly-
reasoned abort, not a failure.

`terminalOffDeg` on `turn360`, drone against the crewed R23 reference band for the same card and the
same airframe:

| arm | crewed R23 | drone R24 |
|---|---|---|
| A (`RelativeTurnLead` OFF) | 6.21–6.28 | 6.56 / 7.44 (mean **7.00**) |
| B (ON) | 9.32–9.35 | 10.0 / 10.5 (mean **10.3**) |

Same band, same ordering, drone ~0.6–1.0° wider. The **uncrewed spread is coarser than crewed** —
arm A sd 0.63 against ~0.04 crewed — so the drone rig has its own, larger noise floor on this card.
n=2/arm, so that is an observation to size the next batch with, not yet a result.

The drone arm comparison also **reproduced the crewed R23 `RelativeTurnLead` finding**, same sign and
similar magnitude on every metric: `terminalOffDeg` **+3.28**, `rmsPointingErrorDeg` +2.73,
`blendRailPct` +52.5 (41.4 → 93.9), `turnRateCapActivePct` +91.9 (4.99 → 96.9),
`turnRateDemandRatio` 0.755 → 0.994, `bankDemandExcessDeg` +2.03. `bankClampActivePct` is 96.9% in
**both** arms.

### Two findings out of the same batch

**1. The drone path is the crewed path, and defect #23 is the proof.** The placement-tick artifact
from Gate B reproduces on the drone with numbers indistinguishable from the crewed batch — at
`tSeg=0.000`, run 01 (no preceding card) reads `rollRate` 0.00 / `leadDeg` 0.00, while runs 02–04
read `rollRate` **−58.99 / −58.56 / −58.49** (crewed: −58.99 / −58.66 / −58.65) and `leadDeg`
**7.01 / 13.58 / 14.37** against a <0.04° error. Same defect, same magnitude, same run-index
progression. That is stronger evidence for Gate C than any metric in the table above: the drone is
**not** running a parallel implementation of the law. #23 stays open and is **not** a Gate C failure.

**2. Distance and uncrewed-ness do not degrade the flight model** — verified in the decompiled source
(0.34), not inferred. The only live simple/complex-physics switch is `Aircraft.SetLocalSim`, fed by
`CheckIfLocalSim()`: `Player != null ? Player.IsLocalPlayer : (Editor ? false : Server.Active)`. An
uncrewed aircraft has `Player == null`, so on a host or in SP it falls through to
`Server.Active == true` and gets **complex** physics. There *is* a distance LOD —
`Aircraft.CheckPhysicsLod()`, 10 km off the camera with a `gForce < 2` hysteresis — but it is dead
code: `private`, **zero callers** in the whole assembly, and 8 km is inside its keep-complex radius
anyway. No other distance gate touches dynamics: `displayDetail` drives only particles/audio/
animation, the aero job has no culling, `Time.fixedDeltaTime` is never rewritten per-unit, and
`Aircraft.FilterInputs` / `ControlsFilter` / `RelaxedStabilityController` contain no
`Player`/`LocalSim`/distance branch. **Caveat:** this holds only while `Server.Active` — as an MP
client an aircraft you don't own is simple/remote, and the harness already refuses to spawn there.

---

## Gate D — drones do not touch your aircraft — **PASSED 2026-07-29 (R25)**

**Non-negotiable.** `DroneCount` **2**, cards running, and **you fly** — a hard reversal, the most
demanding thing you'd normally do.

**Pass.** Your stick feels identical. Your aim marker stays where you put it. The `[maneuver]` line
for **your** turn is indistinguishable from a no-drones baseline.

**Fail.** The marker jumping to a drone's heading — that is `ManualReorients` leaking through the
`_uncrewed` gate, and it would have dragged your marker onto the drone's nose. Or your crosshair
blanking on a drone's engage.

### R25 result — **PASSED 2026-07-29**

v0.89.0. `DroneCount` **2**, both `Multirole1` (KR-67 Ifrit), each flying `fixedwing-sweep` 4× = **8
drone captures** (`R25-d1-…-02/04/06/08`, `R25-d2-…-03/05/07/09`) while the human flew his own
aircraft and recorded (`mouseaim-rec-v0.89.0-R25-01-20260729-222331.csv`, 191.4 s, 3065 samples, no
card, `reason=toggled off`).

**The human's flight was a genuinely hard test, not a cruise:** peak **8.79 g**, AoA −18.7…+41.5°,
bank ±88°, **41.7%** of rows over 60° bank, **26.5%** over 5 g, a 135° marker reversal at t≈89 and a
±180° wrap at t≈54.7.

**Drone side — the human's presence did not change what the drones did** (`turn360`):

| metric | R24 A (solo, n=2) | R25 A (n=2) | R24 B (n=2) | R25 B (n=6) |
|---|---|---|---|---|
| `terminalOffDeg` | 6.56, 7.44 | 6.30, 6.31 | 10.04, 10.52 | 9.37–9.43 |
| `rmsPointingErrorDeg` | 7.99, 9.01 | 7.89, 7.93 | 11.04, 11.42 | 10.50–10.61 |
| `blendRailPct` | 26.4, 56.3 | 25.0, 26.0 | 94.2, 93.5 | 92.7–94.2 |
| `turnRateCapActivePct` | 3.53, 6.45 | 3.75, 3.96 | 96.88 | 96.88–96.89 |
| `aoaPeakDeg` | 7.84, 8.28 | 7.82, 7.84 | 7.76, 7.80 | 7.68–7.76 |
| `deltaEnergyHeightM` | −922, −896 | −929, −931 | −856, −854 | −851…−859 |

All 8: `reason=card 'fixedwing-sweep' complete`, 36.0 s, 576–578 samples, `thr` flat 0.700, `frameMs`
flat 16.7 with **0 rows > 25 ms**, entries clean (`snapBackM=0.0` first, 1745–1775 after,
`ctrlReset=1`). The two **concurrent** arm-A drones agree to **0.008°**; the six arm-B captures span
0.06°.

One honest caveat: arm-B `terminalOffDeg` is **0.88° below** R24's, against R24's own 0.48° spread —
but R24's arm A drifts +0.89° across replicates 1→4 while R25 is flat, so this reads equally as R24
session drift at n=2. **Not attributable either way.**

**Human side — no leak into the marker or the stick.** For each of the 8 drone card-start and 8
card-stop instants, the max consecutive-row |Δ| of `azErr`/`aimRate`/`outP`/`outR`/`outY` inside a
±0.5 s window, against the whole-capture p99, plus a permutation test (the real 16 windows vs 400
random draws):

| metric | worst window | 14 of 16 windows | permutation p |
|---|---|---|---|
| `azErr` | 2.0× p99 | ≤ 0.3× | 0.300 |
| `aimRate` | 2.0× p99 | ≤ 0.4× | 0.145 |
| `outP` / `outR` / `outY` | 1.8× / 1.4× / 2.6× | ≤ 0.4× | 0.780 / 0.670 / 0.237 |

The one elevated window **starts 0.2 s BEFORE** its drone event and ramps over 8 rows (`aimRate`
−5→−51) — a mouse sweep, not a one-frame teleport. All four rows in the capture with |ΔazErr| > 25°
sit 1.8–3.2 s from any drone event; the largest (`azErr` 0→134°, `aimRate` 257 °/s at t≈89.1) is a
human flick **1.8 s after** d2's start. The anomaly log partitions correctly: 47 `[anomaly]` lines
name the human capture, exactly **1** names each drone capture, **zero** cross-attribution.

### The one real finding — config, not the marker and not the stick

The drone A/B scheduler writes a **process-global `Cfg` bool**, and it landed in the human's flight:
`# cfg t=12.400 Control/RelativeTurnLead = False` (17 ms before d1's card start) and `= True` at
t=48.433 — so the human flew **36 s of a 191 s capture on a different control law**. On the drone
side the same limitation meant **ABBA did not hold at `DroneCount` 2**: only `d1-02` and `d2-03`
carry `arm=`/`armKnob=` (d2 inheriting the live global value, truthfully recorded) and the scheduler
stood itself down loudly for the other six, so R25 is **2×A then 6×B, not ABBA**.

Both are the documented v0.86 `Cfg`-global limitation behaving **as designed** (loud stand-down, not
silent mislabel), not a defect in the crewed/uncrewed decoupling Gate D tests. Consequence: **any A/B
experiment must run `DroneCount` = 1** until the swept knob becomes per-aircraft state read through
the controller. 8 replicates × 36 s serial is under 5 minutes, so this does not block E1–E3.

---

## Experiments — only after A–D pass

**A–D have all passed (R22/R23/R24/R25) — these are cleared to fly.**

Replicate counts must be a **multiple of 4** so the ABBA schedule balances on the sum of run
indices, not just counts. Run them at **`DroneCount` = 1** (R25: the swept knob is a process-global
`Cfg` bool, so ABBA cannot hold across concurrent drones — 8 × 36 s serial is under 5 minutes).

### E1 — the elDn feedback loop (v0.85)

`ScenarioArmToggle = BelowAlignSuppress`, 8 replicates, cards `oblique-below` + the `elDn`/`elUp`
mirror pair.

- `elDn` mean `off` **6.92° → under 1.5°**
- `elDn` bank half-amplitude **43.3° → under 5°**
- **`elUp` unchanged at ≤ 0.1°** — this is the control. A change here is a regression, and it is why
  `BelowAlignSuppress` and `AlignRateLead` are separate checkboxes.
- `flightscore.py` verdict line: `r(bWt)` must clear its **`sham`** twin by a margin. `bWt` is an
  algebraic function of `|azErr|` and correlates with it *by construction* — the raw +0.918 is not
  evidence of feedback on its own, and only a gap below the sham says the suppression decoupled
  anything.

Read this knowing `AlignRateLead` is also a **64% roll-damping change** (`RollDamping·(1 + 0.6366·blendWeight)`),
not only a lead. If E1 moves, arm the two checkboxes separately to attribute it.

### E2 — the first unlatched sustained capture (`sweep-slow`)

**F2 is now confirmed in flight, not just from the code:** R22 measured `blendRailPct` = **93.0%**
(sd 0.46) across `turn360`. The bank pipeline's weight is zero for 93% of the scored segment.

`lateralHold` rails at `EvolvedAlignHoldDeg` = **5.0°**, which drives the bank pipeline's weight to
**exactly zero**. Every sustained capture in the corpus so far was above that rail — measuring a
disconnected pipeline. `sweep-slow` holds ~3.5° of lag, below it.

**Do not run E1/E2/F1 against `fixedwing-sweep` — R22 shows it is a saturated card.** On `turn360`
the law is at its ceiling essentially all the time: `bankClampActivePct` **96.9%**,
`turnRateCapActivePct` **96.9%**, `bankDemandExcessDeg` **11.6°** (the law asks for 11.6° more bank
than the clamp allows), and the airframe still delivers `turnRateDemandRatio` **0.994** — i.e. 99.4%
of what was asked, while holding a 9.4° terminal lag. At 12.1 °/s and 5.7 g sustained that lag is
mostly the *airframe*, not the law, and a saturated actuator cannot show a gain change. This is
exactly why `sweep-slow` exists; it is now the primary sustained card, not a supplement.

- **Pass:** `blendRailPct ≈ 0` **and** mean `|azErr|` in 2.5–5°. The card is on-condition.
- If `blendRailPct` is high the card missed its band — the lag constant it was sized with was
  measured on **one airframe** (KR-67 @ 250 m/s). Re-read before concluding anything about the law.

This is also where v0.78/v0.83 can first be seen at all: above the rail their roll contribution is
0.0000.

### E3 — the AoA ceiling (`alpha-sweep`)

`aoaLimiterActivePct` is **0 in every capture ever taken**, against a ONE-LAW rule that explicitly
requires "a loaded jet mushing near its alpha limit above corner speed".

- **Gate on `aoaAboveCeilingPct > 0` at all.** If it is 0, **the card failed, not the law** — raise
  `startAlt` and refly. 8000 m is a reasoned choice, not a validated one.
- **R22 says the gap is bigger than an altitude bump can close.** `alphaLimiter` reads **27°** on the
  Multirole1 `# fbw` header, and the hardest card in the corpus peaked at `aoaPeakDeg` **7.68°** —
  28% of the ceiling, at 5.7 g sustained and with the turn rate cap already active 96.9% of the time.
  The law caps turn rate *before* AoA ever approaches the limiter, so no amount of altitude will get
  there while that cap holds. Expect `alpha-sweep` to need a demand the cap does not bound (a pull,
  not a sweep) — and treat "the AoA path is unreachable through the turn-rate cap" as a finding in
  its own right if it reproduces.
- Then: `aoaPeakOverCeiling` ≲ 1.1, and low `commandIntoCeilingPct` (the law should stop commanding
  into a ceiling it cannot cross).

This single capture settles three open findings at once.

---

## Queued behind these flights — do not fix first

Both are real, both are proven from the code, and both are deliberately **not** shipped: a change
landing in the same batch as six unflown ones makes every effect unattributable.

**F1 — 2× of pitch authority lost to a `>=`.** `PEffRevThresh = 0.15f` is *both* the self-probe's LPF
target and the floor threshold tested `>=`. The probe approaches 0.15 from below and asymptotes;
float32 stalls it ~30 ulps short at `0.1499995`, so the `Max(0.30, ·)` branch is **unreachable** and
pitch P is multiplied by 0.15. Measured: 3.07 s episode, 17.9% of `az30` ticks, plant delivering
**110% of commanded** — the airframe was fine, the law was halving itself.
Recommended first change after this batch. Behind its own checkbox: it is a 2× gain change, and 2×
gain changes destabilise control loops regardless of intent.

**F2 — the latch.** Above 5.0° of azimuth error the bank pipeline's weight is exactly zero, so lag
above the rail disconnects the machinery that reduces lag. E2 is the capture that characterises it.

`debugtests/LOOP-AUDIT-FINDINGS.md` has the closed forms and the cleared list.
