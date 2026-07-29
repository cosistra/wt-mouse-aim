# Flight protocol — v0.82 … v0.87

Six changes shipped without a single flight. This is the order to fly them in, and it is an
**order, not a menu**: each gate validates the instrument the next step measures with. A gate that
fails invalidates everything below it, so stop there rather than collecting data that cannot mean
anything.

Nothing in v0.82–v0.87 has been flown. Every "expected" number below is a **prediction**. A
prediction that misses is a result, not a failure of the test — write down what actually happened.

| ver | change | flown |
|---|---|---|
| 0.82 | `ChaseController` per-aircraft | **crewed, Gate A** |
| 0.83 | relative turn lead + stall-gated integrator | crewed only — never A/B'd |
| 0.84 | entry reset + ABBA arms | **reset yes; ABBA not yet (Gate B)** |
| 0.85 | below-nose roll-to-align loop broken | not exercised — `fixedwing-sweep` is above-nose |
| 0.86 | `ScenarioPlayer`/`ManeuverRecorder` per-aircraft, `frameMs` | **crewed, Gate A** |
| 0.87 | drones fly the real control law | no — Gate C |
| 0.88 | trimmed entry placement (from Gate A) | no |

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
pre-placement speed/altitude, the fuel write, `ctrlReset` and `aoaTrim`, i.e. what the reset had to
undo. **Stop here.** Everything below assumes replicates are exchangeable.

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
  FBW is catching the resulting 1-g drop. It is gone by t+0.7 s, well inside the 6 s `arm`. Fixed at
  source in v0.88 (see CHANGELOG); the criterion is dropped rather than retuned because `off0` already
  covers what it was for.
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

---

## Gate C — one drone flies the law

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

---

## Gate D — drones do not touch your aircraft

**Non-negotiable.** `DroneCount` **2**, cards running, and **you fly** — a hard reversal, the most
demanding thing you'd normally do.

**Pass.** Your stick feels identical. Your aim marker stays where you put it. The `[maneuver]` line
for **your** turn is indistinguishable from a no-drones baseline.

**Fail.** The marker jumping to a drone's heading — that is `ManualReorients` leaking through the
`_uncrewed` gate, and it would have dragged your marker onto the drone's nose. Or your crosshair
blanking on a drone's engage.

---

## Experiments — only after A–D pass

Replicate counts must be a **multiple of 4** so the ABBA schedule balances on the sum of run
indices, not just counts.

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
