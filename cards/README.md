# Test-card grid

Scripted test cards for `ScenarioPlayer` (see [CLAUDE.md](../CLAUDE.md) → *Test cards*). Plain JSON,
loaded from disk at game start; **the file basename is the card id**, and each card gets its own
checkbox in F1 → *Scenario Cards*. No C# change is needed to add or edit one.

They live in the repo so a fresh checkout has the grid; the game reads them from its own config
folder, so they have to be copied in:

```powershell
$dest = "<game>\BepInEx\config\wtmouseaim-cards"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item cards\*.json $dest -Force        # then restart the game: cards bind at startup
```

Built-in cards (`fixedwing-v2`, `rotorcraft-v2`, `fixedwing-sweep`) are **not** here — they live in
`ScenarioPlayer.cs`. This grid is additive: 16 cards, ~11 min of flying, sized against the law's own
thresholds (below) to cover the regimes the built-ins leave open.

## The three thresholds every card is sized against

Card geometry here is not round numbers, it is placement relative to the three angles that decide
which parts of the law are even connected. All three are `Cfg` defaults — **re-read them before
trusting this table**, and the measured columns come from 25 `fixedwing-v2` captures in
`<game>/BepInEx/`:

| angle | knob | what changes across it | measured |
|---|---|---|---|
| **0.5°** of \|azErr\| | (v0.65 settle cone) | below: **no bank is commanded at all** | `targetBank > 0.5°` on **0.0%** of samples below 0.5°, 43.5% in 0.5–2.5°, 97.8% above 2.5° |
| **2.5°** of \|azErr\| | `FineBankDeadzone` | the bank servo switches on — below it, yaw does the whole capture | mean \|targetBank\| 0.00° → 2.52° → 27.7° across the three bands |
| **5.0°** of \|azAl\| | `EvolvedAlignHoldDeg` | `lateralHold` **rails to 1** → `blendWeight` 1 → `eFine` × 0: the fine bank pipeline is disconnected (the latch) | 100% of the settled `turn360` in R21 |

Between 2.5° and 5.0° is the band where **both channels are live and neither is railed** — and
essentially nothing in the corpus sits there. Micro/fine segments are below the first threshold;
every sustained capture ever taken is above the third. **That gap is where this grid puts its
weight**: `oblique-6` and `sweep-slow` are the two cards aimed straight at it.

## The grid

Duration is one replicate, wall clock, excluding the ~1 s placement tick. "Band" is where the card
puts \|azErr\| — at step onset for the step cards, as the standing lag for the sustained ones.

### Oblique steps — the roll/yaw allocation ladder (6 cards, 3.8 min)

Every card is the same diamond: hold N, step to E, S, W, back to N. Each step is a 45° oblique of
magnitude r·√2, one per direction, all from the same energy state — so the four segments are two
mirrored pairs and the card is self-controlling. Only `r` changes.

| card | r = onset \|azErr\| | step | band | what it isolates | pass / fail |
|---|---|---|---|---|---|
| `oblique-05` | 0.35° | 0.5° | below 0.5 — **no roll at all** | pure fine yaw capture + the v0.65 settle micro-bank | `terminalOffDeg` ≲ 0.05°, `settleOn` fires. **Not** an allocation test — expect `bothActivePct` ≈ 0 |
| `oblique-2` | 1.41° | 2° | 0.5–2.5 — roll **partially** commanded | the ambiguous band: the bank servo is half in | `rollYawOpposedPct` vs its own `bothActivePct`; mirror pairs within 0.1 of each other |
| `oblique-dz` | 2.50° | 3.5° | **on** `FineBankDeadzone` | a servo flickering in and out of its own deadzone — the classic chatter site | `stickFlipRateR` no worse than `oblique-2`/`oblique-6`; a peak here is the finding |
| `oblique-6` | 4.24° | 6° | **the gap band** (2.5–5), unlatched | roll and yaw both fully live, blend not railed — the least-covered regime in the project | `rollYawOpposedPct` low with `bothActivePct` high (that pairing is what makes a low number mean something); `blendRailPct` ≈ 0 |
| `oblique-12` | 8.49° | 12° | above 5 — **latched** at onset | roll-to-align with `eFine` disconnected, then the recovery through the rail as error decays | `blendRailPct` should fall through the segment; `settleTime` monotonic against the smaller rungs |
| `oblique-below` | 4.24° | 6° | gap band, **20° below the horizon** | separates *belowness* from *step size* in the v0.85 `elDn` defect | if cross-fighting follows belowness it shows here at 6° steps; if it followed the 20° step size it will not |

`elDn` is the one segment in the whole corpus where roll and yaw genuinely fight — **42.0%** of
samples commanding opposite signs, against 20.4% (`az10`), ~12% (`az30`/`az90`) and ~0 everywhere
else. It is also the only segment flown deep below the nose *and* one of the largest steps, which is
why `oblique-below` exists: those two explanations have never been separated.

### Sustained demand — the productive shape (4 cards, 2.9 min)

A sustained demand is what produced the entire R21 finding set: steps show transient response,
sustained demand shows what the loop *settles to*, and settled error is where the defects were. The
standing lag against a sweep is ≈ rate / K with K ≈ 1.28 /s measured (R21: 12.1 °/s → 9.4°), so the
rate is how you *choose* which band the card sits in.

| card | demand | expected lag | band | pass / fail |
|---|---|---|---|---|
| `sweep-creep` | 1.5 °/s (track) | ~1.2° | 0.5–2.5, roll partial | `blendRailPct` = 0; mean \|azErr\| under 2.5. The regime where a standing error is *below* the servo that would correct it |
| `sweep-slow` | 4.5 °/s (track) | ~3.5° | **the gap band** | `blendRailPct` ≈ 0 **and** mean \|azErr\| in 2.5–5. This is the first sustained capture in which roll participates at all |
| `sweep-step` | ±6° steps on the 4.5 °/s sweep | 3.5° → 9.5° / −2.5° | crosses the latch | `riseTime90`/`overshootDeg` of `az6sweepR` (into the latch) vs `az6sweepL` (out of it) — the asymmetry IS the measurement; read `blendRailPct` per segment |
| `sweep-lowq` | `deriveAzRate`, 150 m/s @ 6000 m | ~9° | latched | `turnRateDemandRatio` < 1 (else the card asks the impossible); the low-q rung of the loading sweep |

The latched side is already well covered by the built-in `fixedwing-sweep` and by `alpha-sweep` /
`stol-sweep` below — all `deriveAzRate`, all measured 100% railed in the settled turn. **Do not pool
a latched sustained segment with an unlatched one**; `blendRailPct` on every segment is what makes
that checkable rather than assumed.

### AoA ceiling — the never-once-exercised case (2 cards, 1.2 min)

`aoaLimiterActivePct` is 0 in every capture ever taken, yet the ONE-LAW rule explicitly names "a
loaded jet mushing near its alpha limit above corner speed". Both cards fly at **8000 m**: thin air
is the airframe-agnostic lever that makes the wing hit its alpha ceiling before its g-limit.

| card | isolates | pass / fail |
|---|---|---|
| `alpha-steps` | transient α: mirrored ±45° pitch steps | `aoaAboveCeilingPct` > 0 (else the card missed the regime); `aoaPeakOverCeiling` ≲ 1.1 — v0.57 measured a *reactive* gate relaying at 1.3–2.5×; `wobbleEpisodesAoa` = 0 |
| `alpha-sweep` | sustained α at the ceiling — **the highest-value card in the set** | `aoaAboveCeilingPct` > 0 **and** `commandIntoCeilingPct` low: the law should back its own demand off, not leave the gate to do it. `qSchedMin` < 1 proves the v0.59 schedule engaged, `aoaRecoverActivePct` > 0 the recovery bias |

On these two, `aoaLimiterActivePct = 0` is **not** a good score — it means the card failed to reach
the regime and every other number describes some other flight. If that happens, raise `startAlt`
before touching anything else.

### Airframe coverage — the other two ONE-LAW cases (4 cards, 3.1 min)

| card | isolates | pass / fail |
|---|---|---|
| `stol-steps` | low-limit STOL trainer, mirrored 30° az and 40° el steps at 90 m/s | no `overshootDeg` blow-up; `aoaLimiterActivePct` graded, not bang-bang (the v0.61 4°-fade floor exists for exactly this airframe) |
| `stol-sweep` | the sustained shape on the trainer | `turnRateDemandRatio` < 1; `terminalOffDeg`/demand comparable to `fixedwing-sweep` — **normalized**, never in absolute degrees |
| `rotor-hover` | v0.58 hover regime (tilt-angle driven): station keeping + mirrored 90° pedal turns | `positionRMSM`/`driftRateMS` bounded and *equal* between `hoveryawR` and `hoveryawL`; no ~1 Hz limit cycle |
| `rotor-bob` | vertical demand at hover, mirrored ±25° | `riseTime90`/`overshootM` of `bobup` vs `bobdn` within noise |

Whole grid once ≈ **11.1 min** — oblique 3.8, sweep 2.9, alpha 1.2, trainer 1.6, rotorcraft 1.6. At
the usual `ScenarioRepeat = 4` the jet set alone (oblique + sweep + alpha) is ~30 min.

**All sixteen are hand-flyable today** — the card owns stick, throttle and marker from the moment the
run key is pressed; the pilot only has to be *in* the right aircraft. What the drone harness buys is
(a) replicate counts nobody wants to sit through, (b) more than one airframe per session, and (c)
`rotor-hover`/`rotor-bob`, the two that still need a human, because an ungated card gets no placement
and the hover has to exist before the card starts.

## What the measurements say about the "confused small movements" report

The oblique family was originally justified as "isolate the roll/yaw allocation decision". Measuring
the existing corpus first changed that story, and the cards were re-aimed accordingly:

- **Cross-fighting reads ~0% on `micro*`/`fine` — and that zero is a floor, not a result.** Median
  \|outR\| there is 0.006–0.017 against a 0.02 stick deadband, and both channels are simultaneously
  active on 0.0% (`fine`) to 30% (`micro10`) of samples. The metric would read the same whether
  allocation were perfect or catastrophic.
- **The reason is the law, not the analysis.** Below 2.5° of azimuth error the bank servo commands
  zero by design (`FineBankDeadzone`), and below 0.5° nothing commands bank at all. There is no
  allocation *decision* at micro scale — the law already made it: yaw only.
- **So a small-movement complaint at sub-degree scale is not an allocation fight.** It is a
  settling/overshoot question, and `oblique-05`/`oblique-2` are labelled as such. The allocation
  question starts at `oblique-dz` and lives in `oblique-6`.
- **`scorecard.py` now reports the floor next to the metric** — `bothActivePct`, `rollCmdMedian`,
  `yawCmdMedian` beside `rollYawOpposedPct` — so a 0 can be read as "nothing was commanded" rather
  than "nothing was wrong". That is the same failure the 1° on-target cone had, and it is the reason
  a card set can be invisible to its own metric.

## Rules this grid follows (and why)

**One card = one test.** `PlaceOnCondition` runs once **per card**, not per segment (`_placed` is
cleared in `NextCard`), and it is the only thing that resets position, speed, altitude, fuel and the
per-aircraft `ChaseController` state. A 20-segment mega-card measures segment 15 from whatever
attitude, energy and integrator wind-up segment 3 left behind — unattributable when it fails. Hence
the oblique ladder is six cards rather than one, and `alpha-steps`/`alpha-sweep` and
`stol-steps`/`stol-sweep` are split rather than chained.
<!-- ponytail: chaining long multi-test cards is a later upgrade, once the v0.84 reset is trusted
     across a long session. Nothing here depends on it; split cards cost only the extra `arm`. -->

**Mirrored pairs by default.** The v0.85 `elDn` defect (6.92° standing error vs 0.03° for the
*larger* mirror step) was only unambiguous because a maneuver was compared against its mirror; an
absolute number alone reads as "that's a hard maneuver". Every step card ships both signs at equal
magnitude. Three deliberate exceptions:

- **`alpha-steps` is geometry-matched but not energy-matched.** Both steps are 45° from the same
  level datum, but `alphaPush` is entered after the pull-up and the return, i.e. slower. A card
  cannot re-place mid-run, so read `spd` alongside the pair. Its question is qualitative — does the
  negative-α guard behave like the positive one — not a fine asymmetry measurement.
- **`sweep-step`'s pair is deliberately asymmetric in regime**: the +6° step crosses into the latch,
  the −6° step out of it. That is the measurement, not a defect; `blendRailPct` labels each side.
- **`rotor-hover`'s `hover` segment has no mirror.** Station keeping has no sign.

**Tags are distinct per card, even when the metric is the same.** `compare-runs.py` keys segments by
tag alone (`_segments_by_tag`), so a 90 m/s `az30` and a 250 m/s `az30` would be pooled as replicates
of each other. Hence `az30R`/`az30L`, `turn360creep`/`slow`/`base`/`loq`/`stol`,
`hoveryawR`/`hoveryawL`. They still resolve to the right metric type because `TAG_TYPE_RULES` matches
by prefix — adding a suffix costs nothing, reusing a name costs a silent pooling bug.

**Every tag is in `scorecard.py`'s `TAG_TYPE_RULES`, and its selftest checks it.**
`scorecard.py --selftest` parses every file in this directory and asserts: name == basename, first
segment `arm`, positive durations, no repeated scored tag, track arrays long enough for their
segment, and **every tag resolves to a known type**. That pair drifted silently once (v0.71: 19 of 21
segments scored "unknown"); this is the check that stops it happening again.

**Thresholds are normalized, not absolute.** The destination is multi-airframe × multi-loadout, so a
pass criterion in absolute degrees would have to be rewritten per aircraft. Everything above is a
ratio against probed capability (`turnRateDemandRatio`, `aoaPeakOverCeiling`, `terminalOffDeg`/demand),
an occupancy (`blendRailPct`, `bothActivePct`), or a mirror-pair difference. `deriveAzRate` follows
the same rule — it asks for the same *fraction* of the airframe's own structural g, not a fixed °/s.
The fixed-rate track cards (`sweep-creep`/`slow`/`step`) are the deliberate exception: their rate is
chosen to be sub-capability for *every* airframe on file, so the same track lands every aircraft in
the same **band**, which is the quantity that matters for those cards.

**Altitude floor.** A card aborts below `FloorAltM` = 500 m MSL and the run is truncated
(`compare-runs.py` excludes truncated segments rather than blending them). Every card starts at
2500 m or above and no segment can lose that much; the deepest is `oblique-below` (38 s nose-down,
~3.3 km, entered at 6000 m for that reason).

## Entry conditions — read before ticking a checkbox

`cls` gates only the airframe *class* (`Pilot.PilotType`), and a trainer and a fighter are both
`Plane`. The entry condition is what actually separates them, and `ScenarioForceEntry` (default on)
**writes** it:

- `stol-*` place the aircraft at **90 m/s**. A jet flown on those is placed near its stall and will
  mush — not invalid, just not the test. Untick them when flying a jet.
- `oblique-*`, `sweep-*`, `alpha-*` place at **150–250 m/s**, which a trainer cannot fly. Untick them
  when flying a trainer.
- `rotor-*` declare no entry condition (`startSpeed` 0). Nothing is placed, the collective stays with
  the pilot, and **no reset happens between replicates** — fly one replicate per hand-established
  hover.

For the **loaded** jet case the loadout is yours to choose: a card cannot set it. Fly `alpha-sweep`
and `alpha-steps` clean, then again with heavy stores. Once loadout variation lands in the drone
harness, the cards that discriminate mass are **`alpha-sweep`, `alpha-steps` and `sweep-lowq`** —
mass shows up as more α for the same commanded n, i.e. directly in `aoaAboveCeilingPct`, `gateMinUp`
and `qSchedMin`. The oblique cards will barely move (mass reaches them only as a slower `pEff`), so
they are not worth a loadout sweep.

## Self-describing cards — `repeat`, `armToggle`, `config`

A card carries its own run configuration, so the operator ticks **one** checkbox and presses the
spawn key. Every field falls back to the matching global when absent, so a card that declares
nothing behaves exactly as it always did.

```json
{
  "name": "sweep-slow", "cls": "Plane",
  "airframe": "Multirole1", "startSpeed": 250.0, "startAlt": 4000.0,
  "repeat": 8,
  "armToggle": "Control/MarkerRateFeedForward",
  "config": [ { "key": "Control/TurnLeadTime", "value": "0.35" },
              { "key": "Scenario/ScenarioThrottle", "value": "0.8" } ],
  "segments": [ … ]
}
```

| field | falls back to | notes |
|---|---|---|
| `airframe` | `Drone/DroneAirframe` | the drone harness **spawns** this jsonKey; overrides the whole lane list |
| `startAlt` / `startSpeed` | `Drone/DroneSpawnAlt` / `DroneSpawnSpeed` | used only when `> 0`; already the placement's target |
| `repeat` | `Scenario/ScenarioRepeat` | `0` = fall back. The **first selected card** decides for the whole queue |
| `armToggle` | `Scenario/ScenarioArmToggle` | must name a **bool**; interleaved ABBA. First card decides |
| `config[]` | — | `"Section/Key"` (bare key ⇒ section `Control`); pinned at card start, **restored** at card end |

Three rules the `scorecard.py --selftest` enforces, because nothing at runtime will:

- **A `config` entry may not pin the knob `armToggle` sweeps.** That flies every replicate on one arm
  while each capture still labels itself `arm=0`/`arm=1` — the A/B reads as "no difference" and
  nothing in the artifacts says why. Runtime refuses it too, loudly, and flies the rest.
- **`config[].key` is `Key` or `Section/Key`**, both halves non-empty; `value` is the TOML text form
  of whatever type that entry is (`true`, `0.35`, `F2`) and may not be empty.
- **`repeat` is 0..20.** The mod clamps, so `40` would silently fly 20.

What a card pinned is written into its own capture as a `# override Section/Key=value …` header
line — `# config` shows the values but cannot say the *card* chose them. Both the launch log and
`[card] suite start` name which source won for every field, so a batch is auditable before it flies.

## Regenerating the track cards

`sweep-creep.json`, `sweep-slow.json` and `sweep-step.json` carry per-step track arrays — a flat
sweep rate, which `deriveAzRate` cannot express because it derives per airframe. They were written by
a throwaway script; the shape is:

```python
STEP = 0.02                        # must equal the card's "step"
track = lambda dur, az0, rate: [round(az0 + rate * i * STEP, 4)
                                for i in range(int(round(dur / STEP)) + 1)]
# segment: {"tag": t, "dur": d, "az": az0, "el": 0.0,
#           "trackAz": track(d, az0, rate), "trackEl": [0.0] * (int(round(d / STEP)) + 1)}
# rate is chosen from the target BAND: standing lag ~= rate / 1.28 (K measured on R21).
# sweep-step's steps continue the sweep phase: az0 = (az at the end of the previous segment) +- 6.
```

A track shorter than its segment does not fail — `ScenarioPlayer.Demand` clamps the index and the
demand silently freezes — which is why the selftest asserts the length.

## Coverage this grid still does not have

- **The K ≈ 1.28 /s lag constant is measured on ONE airframe** (KR-67 at 250 m/s), and the fixed-rate
  cards' band placement depends on it. On another airframe the same rate may land in a different
  band. That is checkable from the capture itself (mean \|azErr\|, `blendRailPct`) — check it before
  reading the result, and retune the rate if it missed.
- **Beyond-capability sweep in dense air.** `alpha-sweep` gets there via altitude; a sea-level version
  would need a per-airframe rate, which `deriveAzRate` deliberately does not allow.
- **Reversal / astern at low q.** The built-ins cover them at 250 m/s only, where `flightscore.py`
  scores them 48–55% `AIRFRAME_LIMITED`; they may be more informative on the trainer.
- **Roll entry state.** "From an established roll" is on the INSTRUCTOR-LOOP axis list; an aim demand
  cannot command a roll rate directly.
- **Oblique at low q or on a trainer.** The whole oblique ladder is 250 m/s / 4000 m. Re-issuing it
  at the `stol` entry condition is a copy-and-edit away, once the jet ladder has said something.
