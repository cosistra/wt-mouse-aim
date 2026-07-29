# Flight protocol — v0.82 … v0.87

Six changes shipped without a single flight. This is the order to fly them in, and it is an
**order, not a menu**: each gate validates the instrument the next step measures with. A gate that
fails invalidates everything below it, so stop there rather than collecting data that cannot mean
anything.

Nothing in v0.82–v0.87 has been flown. Every "expected" number below is a **prediction**. A
prediction that misses is a result, not a failure of the test — write down what actually happened.

| ver | change | flown |
|---|---|---|
| 0.82 | `ChaseController` per-aircraft | no |
| 0.83 | relative turn lead + stall-gated integrator | no |
| 0.84 | entry reset + ABBA arms | no |
| 0.85 | below-nose roll-to-align loop broken | no |
| 0.86 | `ScenarioPlayer`/`ManeuverRecorder` per-aircraft, `frameMs` | no |
| 0.87 | drones fly the real control law | no |

Total for gates A–D: **~25 minutes**. Do not skip to the experiments.

---

## Gate A — the rig does not drift (no drones)

The most important test in this document. Until it passes, **no A/B result is admissible** — a
first-half/second-half split of a single *unchanged* arm previously beat its own detection
threshold, i.e. doing nothing scored as significant.

**Setup.** `DroneEnabled` **off**. `ScenarioArmToggle` **empty**. `ScenarioForceEntry` on.
`ScenarioRepeat` 8. Card: `fixedwing-sweep`. Multirole1, clean. Do not touch the stick.

**Pass — all three:**
- first-sample `spd` within **±0.3 m/s** across all 8 captures
- first-sample `|outP|` **< 0.05** (was 0.487 on late runs before v0.84)
- `terminalOffDeg` vs run index correlation **|r| < 0.4** (was −0.824)

**Fail.** The reset is still leaking. Read the `# entry` header line — it records `snapBackM`, the
pre-placement speed/altitude, the fuel write and `ctrlReset`, i.e. what the reset had to undo.
**Stop here.** Everything below assumes replicates are exchangeable.

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

`lateralHold` rails at `EvolvedAlignHoldDeg` = **5.0°**, which drives the bank pipeline's weight to
**exactly zero**. Every sustained capture in the corpus so far was above that rail — measuring a
disconnected pipeline. `sweep-slow` holds ~3.5° of lag, below it.

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
