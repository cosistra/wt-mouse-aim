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
`ScenarioPlayer.cs`. This grid is additive: 16 baseline cards, ~11 min of flying, sized against the
law's own thresholds (below) to cover the regimes the built-ins leave open — plus **4 `e*`
attribution cards**, each one A/B experiment wired into its own file (see
[Attribution A/B](#attribution-ab--one-checkbox-per-experiment-4-cards)), and **4 follow-up cards**
written against a specific finding rather than a regime (see
[Follow-ups](#follow-ups--cards-written-against-a-finding-4-cards)).

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
| `oblique-above-c` | 4.24° | 6° | gap band, **20° above the horizon** | the third arm of that axis — see below. **`-c` only**: there is no absolute twin | `rollYawOpposedPct`/`terminalOffDeg` at or below `oblique-6-c`'s. As bad as `oblique-below-c` = the defect follows \|elevation\|, not belowness |

`elDn` is the one segment in the whole corpus where roll and yaw genuinely fight — **42.0%** of
samples commanding opposite signs, against 20.4% (`az10`), ~12% (`az30`/`az90`) and ~0 everywhere
else. It is also the only segment flown deep below the nose *and* one of the largest steps, which is
why `oblique-below` exists: those two explanations have never been separated.

#### `oblique-*-c` — the corner-speed twins (6 cards, v0.93) + `oblique-above-c`

Six cards, `oblique-05-c` … `oblique-below-c`, each **geometrically identical** to the card it is
named after — same steps, same segments, same tags, same `repeat: 8`. The only difference is
`"startSpeedCorner": 0.95`, so every lane enters at 95% of **its own** corner speed instead of a flat
250 m/s.

Two things that buys, and they are separate:

1. **The whole roster flies.** At 250 m/s the pre-spawn envelope gate correctly refuses `CAS1`
   (0.95 × Vmax = 195.3) and `COIN` (134.6), so a ten-key card flies eight. At 0.95× corner all ten
   spawn. Those two airframes have never been measured by this project.
2. **The comparison becomes aerodynamic rather than numeric.** 250 m/s is 1.56× corner for
   `Fighter1` and 2.27× for `COIN` — the "same" card was asking ten airframes ten different questions.
   The twins ask one.

**Since v0.96 the multiple resolves against the FBW's corner speed, not the encyclopedia's AI one**
([`AIRFRAMES.md`](../AIRFRAMES.md) trap 6), which is a real change to what these seven cards fly:
`0.95x` on `Fighter1` was 171 m/s and is now 152, on `Darkreach` was 171 and is now 95. **Do not pool
a `-c` capture from before v0.96 with one after it** — the `# entry` header carries the speed actually
placed, so the check is one line of the capture. The one thing it buys back: at `1.0x` all ten
fixed-wing keys now clear the envelope gate (`CAS1`'s refusal was an artefact of the AI field), so the
`0.95` these cards ship is a comparability choice, not a workaround.

**A seventh `-c` card, `oblique-above-c`, has no absolute twin** — it is not a re-entry of an existing
card, it is the missing third point of an axis. `oblique-6-c` centres the 6° diamond **on** the
horizon and `oblique-below-c` centres it **20° below**; `oblique-above-c` centres it **20° above**, so
belowness — `alignFracH`, the exact quantity the v0.85 suppression keys on — becomes a 3-point line
(−20, 0, +20) instead of a pair, and a monotonic trend is distinguishable from a below-only anomaly.
Two things about it that are easy to get wrong if you copy it:

- **It is an OFFSET of `oblique-below-c`'s diamond, not a negation of its elevations.** Negating `el`
  flips the diamond as well as the centre: the arm would start at the *bottom* and the step tagged
  `obDR` (down-right) would move up. The relative step sequence is byte-identical to the other two
  arms; only the centre moves. That is what makes it one axis rather than three stimuli.
- **It enters at 3000 m, not 6000.** `oblique-below-c` descends ~3.3 km from 6000 (mean ≈ 4350 m);
  this one climbs, so 3000 puts the pair at a comparable **mean** altitude and therefore comparable
  mean q. The climb will fall short of the dive's 3.3 km — thrust is finite, gravity is not — so read
  `alt` and `spd` off the capture before comparing arms. The energy asymmetry (this card decelerates
  where `oblique-below-c` accelerates) is inherent to the axis, not a defect.

Read the twins **against their absolute twin**, not pooled with it: `compare-runs.py` keys on
(airframe, card, arm) and the `-c` suffix is a different card, so the two come back as separate rows
by construction. That pairing is the actual experiment — geometry held, entry condition swept —
and it is also the first measurement of whether the law's behaviour is a property of the *speed* or
of the *regime*, which is the core design rule's own question.

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

The ONE-LAW rule explicitly names "a loaded jet mushing near its alpha limit above corner speed", and
neither card has ever been flown. Both fly at **8000 m**: thin air is the airframe-agnostic lever
that makes the wing hit its alpha ceiling before its g-limit.

> **CORRECTED 2026-07-31.** This paragraph used to open *"`aoaLimiterActivePct` is 0 in every capture
> ever taken"*. **False.** It is non-zero on **66** (run, airframe, tag) cells, **23** of them fully
> unrailed — topped by **R33 `Darkreach·obDR6` at 100.0%** (n = 4, `railed = 0`, `aoaPeakDeg`
> 7.38–7.59° vs a 10° limiter). Low q got there, not load: v0.96's #41 fix put that lane's entry at
> **95 m/s**. The α regime is reachable *without* these cards — which makes them cheaper to justify,
> not harder, since the open question is now what the law *does* at the ceiling rather than whether it
> ever gets there. See `LAW-CHARACTERIZATION.md` §1.

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

### Attribution A/B — one checkbox per experiment (4 cards)

> **`e2-rel-turn-lead` was DELETED**, along with the `Control/RelativeTurnLead` knob and its branch,
> after its A/B came back spent: the lever separated `leadDeg` 38× and moved the standing error
> 0.2–3.8%, inside that batch's own 0.1–4.7% null contrast. The card had to go with the knob because
> **`ResolveArm` fails soft** — with the toggle unresolvable it warns once and then flies every
> replicate on the same arm while each capture still labels itself `arm=0`/`arm=1`, i.e. a complete,
> well-formed, entirely fictional A/B. That is the exact silent null the arm machinery exists to
> prevent, so a deleted card beats a card that warns. Its R39-D captures are archived and analysed.
> **The sweepable set is now four levers at five `Arm()` sites** (`BelowAlignSuppress`,
> `AlignRateLead`, `MarkerRateFeedForward` ×2, `IntegralStallGate`), down from five at six.

[`LAW-CHARACTERIZATION.md`](../LAW-CHARACTERIZATION.md) → *Batch 4* is the spec. Each card is an
**existing** card's geometry copied verbatim with the knob wired in via `armToggle` and `repeat: 8`
— a new stimulus and a new knob moving at once would be unattributable — so running an experiment is
one checkbox and the spawn key, and the capture says which arm it flew. Only the segment tags are
renamed (suffix `bs`/`bc`/`al`/`rtl`/`mff`), because `compare-runs.py` keys segments by tag alone.

**All of them used to carry `"count": 1`; they no longer do.** It was forced until v0.94: the swept knob
was one process-global `Cfg` entry the control law read globally, so with more than one drone the arm
scheduler **stood down**, the whole batch flew one arm while every capture still labelled itself
`arm=0`/`arm=1`, the A/B read as "no difference" and no artifact said why. v0.94 moved the arm onto the
aircraft (read through `ChaseController.Arm()`), so **every lane now sweeps its own independent ABBA**.
With that gone, `count: 1` was a pure waste: wall clock is set by *replicates per lane*, not by lane
count (R28 flew 384 captures across 8 lanes in 30m14s), so nine of the ten lanes a launch can hold were
being left empty for nothing. All four now name the **eight fixed-wing keys that clear the v0.92
envelope gate at their 250 m/s entry** — `Fighter1, Multirole1, SmallFighter1, trainer, VTOLTrainer1,
EW1, FastBomber1, Darkreach`; `CAS1` (0.95 × Vmax = 195.3), `COIN` (134.6) and all three rotorcraft are
left out rather than shipped as guaranteed pre-spawn refusals. `compare-runs.py` groups by
(airframe, card, arm) and refuses to pool, so each comes back as **eight independent A/Bs**, which is
the one-law question the single-airframe version could not ask. Two consequences: replicates are **per
lane** (`repeat: 8` × 8 airframes = 64 runs, one lane's worth of wall clock), and the ABBA balance is
per lane too, so every lane wants a `repeat` that is a multiple of 4 — `8` is, which is why the field
was left alone when the fleet was added. Widening the *geometry* is still a change to what the card
measures; widening the *roster* is not, because nothing is pooled across airframes.
`count` lives in the card rather than in `Drone/DroneCount` (v0.91) because that was a global the
operator had to remember to set *back down* after any other batch, and forgetting it does not refuse.
`blendRailPct` first on the two sweeps: a railed segment cannot show a gain change, which is what
made every previous A/B measure the clamp.

| card | knob swept | geometry from | pass / fail |
|---|---|---|---|
| `e1-below-suppress` | `Control/BelowAlignSuppress` | `oblique-below` | on-arm `terminalOffDeg`/`rollYawOpposedPct` below the off arm and the mirror pairs closer together. No separation = the v0.85 fix does nothing where it was aimed |
| `e1-below-control` | `Control/BelowAlignSuppress` | `oblique-6` | **the control — the arms must be indistinguishable.** `alignFracH` is ~0 on the horizon-centred diamond, so any arm separation beyond this card's own mirror-pair spread is a regression *and* invalidates `e1-below-suppress` |
| `e1b-align-lead` | `Control/AlignRateLead` | `oblique-below` | on-arm `overshootAzDeg`/`stickFlipRateR` down at no cost in `terminalOffDeg`. Up = finding 17's 64% roll-damping side effect is what the knob actually does |
| `e3-marker-ff` | `Control/MarkerRateFeedForward` | `sweep-slow` | same rail gate, then on-arm mean \|azErr\| down. Arms matching *while unrailed* extends finding 16 (0.0000 of roll stick above the rail) rather than closing it; `aimRate` on both arms is what separates a null from "never fired" |

`e1b` is a separate card rather than a second arm on `e1-below-suppress` for the reason Batch 4
gives: armed together, a below-suppression change and a 64% roll-damping change are unattributable.
Each is 8 replicates — 5.1 min for an oblique card, 6.1 for a sweep, ~22 min for all four, **and that
is the same ~28 min whether each flies one airframe or eight** (see the paragraph above).

### Follow-ups — cards written against a finding (4 cards)

Unlike everything above, these are not regime coverage: each was written to settle one specific
question a batch raised, and the first three have flown (`hs-hold` has not). They stay in the grid
because the question can be re-asked on a new build — that is the point of a card.

| card | what it isolates | pass / fail |
|---|---|---|
| `oblique-12-fwd` | **direction vs card position**, forward arm. Identical `oblique-12` diamond, down legs in slots 2–3; `Fighter1, Multirole1, FastBomber1` | read **only** against `oblique-12-rev` — a number from one arm alone measures the confound, not the effect |
| `oblique-12-rev` | the same diamond with the traversal **reversed**, up legs in slots 2–3 | the down/up `terminalOffDeg` ratio must **not invert** when the up legs move early. It does not: R30 measured ×3.07 / ×5.39 / ×1.39 on the three airframes, every 95% CI excluding 1, with a real but 4–7× smaller position effect pointing the *other* way |
| `darkreach-05` | the **R29 departure precursor** — `oblique-05` geometry, `Darkreach` alone, at the absolute 171 m/s R29 flew | the *precursor*, not the crash: a healthy capture commands **no** bank below 0.5° of `azErr` (0.0% of samples on 25 `fixedwing-v2` captures), so `targetBank` > ~10° at \|`azErr`\| < 2° is the defect firing. R32 reproduced it (34–56° at \|`azErr`\| < 5°) and **18 of 63 captures departed**. A truncated capture here is a **result**, not a failed run |
| `hs-hold` | the **250–400 m/s hole** and the roll limit cycle that lives in it — `LAW-LEDGER.md` O11 / `GENERALITY-REVIEW.md` finding 5. Level on-boresight cruise: mirrored 30 s holds at ±1° plus a mirrored 0.5° pair, at **2.2× FBW corner** (352/352/341/440 m/s) on `Fighter1, Multirole1, SmallFighter1, FastBomber1`. **NOT a maneuver card** — no elevation demand anywhere | pass = `outR` sd ≲ 0.05 over the settled tail with no sustained sign-flipping: **`wobbleFreqHzOutR` absent**, `wobbleEpisodesOutR` = 0, `stickFlipRateR` at the oblique family's level, R/L mirror pairs within noise. fail = the R6-02 signature — `wobbleFreqHzOutR` **1–2 Hz** with high `wobbleCoherenceOutR` and episodes at pp 0.3+, *while* `targetBank`/`tBankE` ≈ 0 and `off` < 1°. A limit cycle **with** a live bank demand is an outer-loop defect and belongs to the oblique family, not here |

`hs-hold` has three things that are easy to get wrong if you copy it. **2.2 is the largest multiple
the whole roster clears** — `0.95 × Vmax / FBW corner` is 2.28 on `FastBomber1`, so 2.3 refuses a lane
pre-spawn — and the roster is not a preference: 2.2× refuses `trainer`, `VTOLTrainer1`, `CAS1`, `COIN`
and `EW1` at the gate outright, and `Darkreach` is left out by hand because 2.2 × its 100 corner is
220 m/s, i.e. not in the band the card exists to fill. **`ScenarioThrottle` is pinned at `1.00` and
must not be lowered**: R39-stol measured these same jets running to 340–381 m/s on a pinned 1.00
throttle, so full throttle *trims* them right here — this card asks for the speed the pin already
holds, which is the inverse of the `stol-*` problem (declared 90, flew 381). At 0.70 the entry becomes
a deceleration and the card slides back out of the band. And **`Drone/DroneAltDeckM` can be left at its
3000 default here**, the opposite of the `stol-*` instruction: the decks put half the fleet at 2500 m
and half at 5500 m — q 59.3 vs 43.2 kPa against the corpus's all-time high of 25.6 — crossed with
airframe on the Latin-square diagonal, so the decks become a balanced dynamic-pressure factor inside
one card, and q is exactly what finding 5 is about. There is no vertical demand anywhere, so unlike
every other high-energy card this one has no altitude budget to blow and cannot reach the 500 m floor.

Why `oblique-12-fwd`/`rev` are a pair and not one card with a flag: every other oblique card traverses
N→E→S→W→N, so the two **down** legs are always slots 2–3 and the two **up** legs always 4–5 —
direction is perfectly confounded with position, and R28 could exclude energy and elevation but not
order. Flying both traversals in one session crosses the two factors instead. Both emit the **same
four tags**, which is deliberate: `compare-runs.py` keys segments by tag, so each tag is compared
across the two slots it occupies.

Both also declare `"armToggle": "BelowAlignSuppress"` — R31 swept it, 48 `arm=0` / 48 `arm=1`, and
found `bSup` is a correlate rather than the transmission path (and that `arm=0` selects the **v0.67
form** of the suppression rather than turning it off — the knob does not do what its name says).

> **Consistency note, not a bug: those two spell `armToggle` as a BARE key** (`BelowAlignSuppress`)
> while every other card writes `Control/…`. `SplitSpec` defaults a bare key to section `Control`, so
> the two spellings resolve to the identical entry and R31 swept it correctly. **The grid prefers the
> explicit `Control/BelowAlignSuppress`** — same rule as `config[].key`, and it is the form that keeps
> reading right the day a same-named knob appears in another section.

`darkreach-05` keeps an **absolute** `startSpeed` on purpose and should stay that way: 171 m/s is the
condition R29 and R32 flew, and reproducing that departure is the whole card. `startSpeedCorner`
resolves correctly since v0.96, but on this airframe `0.95x` is **95 m/s** (FBW corner 100, against
the AI table's 180) — a different flight, not a more portable spelling of this one.

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
`hoveryawR`/`hoveryawL`, and the attribution set's `obDR6bs`/`obDR6bc`/`obDR6al` and
`turn360rtl`/`turn360mff` — same geometry as the card they copy, deliberately not the same tag.
They still resolve to the right metric type because `TAG_TYPE_RULES` matches
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

## Launch procedure — the four things that silently ruin a batch

Cards bind at **startup**, so install then restart the game:

```powershell
Copy-Item cards\*.json "<game>\BepInEx\config\wtmouseaim-cards" -Force
```

Then, in `BepInEx\LogOutput.log`, check **two lines** before you press anything:

```
[card] N card(s) bound (3 built-in, 36 from disk) — ...
[session] run R<N>
```

`0 from disk` **with files in the folder** means nothing below will fly. The `R<N>` tags every capture
and every log this session and **only increments on a game restart**, so consecutive batches share it
unless you quit in between.

| knob | value | why |
|---|---|---|
| `Drone/DroneEnabled` | **ON** | master switch; the harness is inert and its hotkeys unread while off |
| `Scenario/ScenarioCardSet` | **the card name** | the only safe way to pick a card — see the `sel[0]` rule below |
| `Scenario/ScenarioArmToggle` | **empty** unless the batch says otherwise | a leftover value here sweeps a knob nobody asked to sweep. `e*` cards name their own `armToggle` and win; other cards declare none |
| `Scenario/ScenarioForceEntry` | ON (default) | the placement writes the entry condition; off, a lane not already on condition simply refuses |
| `Control/Enabled`, `Control/WriteControl` | **ON** | with either off the card moves the marker and nothing chases it. `[card]` warns, and the capture is not a law measurement |
| `Recording/DebugLogging` | **OFF** | per-tick spam; it costs frames on a wide fleet |

**Run `python debugtests/check-card.py cards/*.json` before every batch.** Three cards in two days
failed on arithmetic it computes in advance — `alpha-sweep` (a 3.24 g ceiling against the 4.8–24 g its
lanes needed), `stol-*` (declared 90 m/s, flew 340–381), `rotor-*` (never hovered). The flight is the
most expensive step and must not be where design errors are discovered.

**Read the PREFLIGHT run board before every spawn press** — it is the only confirmation that the card,
and not the F1 checkboxes, is driving.

**After the batch:** index it, then archive it out of `<game>` before the next game start overwrites
`LogOutput.log`:

```bash
python debugtests/index-captures.py "<game>/BepInEx"
python debugtests/index-captures.py "<game>/BepInEx" --archive debugtests/archive --run R<N>
```

## Entry conditions — read before ticking a checkbox

`cls` gates only the airframe *class* (`Pilot.PilotType`), and a trainer and a fighter are both
`Plane`. The entry condition is what actually separates them, and `ScenarioForceEntry` (default on)
**writes** it — it writes the speed whether or not the airframe can hold it, so check the target
against [`AIRFRAMES.md`](../AIRFRAMES.md) (Vstall / Vmax / corner per jsonKey) before ticking a card
for an airframe it was not written for:

- `stol-*` place the aircraft at **90 m/s**. A jet flown on those is placed near its stall and will
  mush — not invalid, just not the test. Untick them when flying a jet.
- `oblique-*`, `sweep-*`, `alpha-*` place at **150–250 m/s**. The `trainer` reaches that (Vmax 294)
  but only well above its 130 m/s FBW corner speed, so it is placed somewhere it cannot maneuver — untick
  them when flying one. `CAS1` (Vmax 205.6), `COIN` (141.7) and every rotorcraft cannot reach 250 at
  all: the placement writes the speed anyway and the capture measures the decay.
- `rotor-*` declare a **hover** (`startSpeed: 0`). Since v1.0.0 that is a *declared* zero rather than
  an absent field, so the placement runs and every replicate carries its own `# entry` header — the
  replicates are independent, which R39's were not. **The collective is still unowned:**
  `ScenarioPlayer.OwnInputs` early-returns at `EntrySpeed <= 0`, so a `ScenarioThrottle` pin is read
  after the return and does nothing. At the harness's fixed `HoldThrottle = 0.60` one rotorcraft in
  three sinks at 25 m/s and aborts on the altitude floor (`LAW-LEDGER.md` H5), so a hover card cannot
  yet hold a hover on every airframe.

For the **loaded** jet case the loadout is yours to choose: a card cannot set it. Fly `alpha-sweep`
and `alpha-steps` clean, then again with heavy stores. Once loadout variation lands in the drone
harness, the cards that discriminate mass are **`alpha-sweep`, `alpha-steps` and `sweep-lowq`** —
mass shows up as more α for the same commanded n, i.e. directly in `aoaAboveCeilingPct`, `gateMinUp`
and `qSchedMin`. The oblique cards will barely move (mass reaches them only as a slower `pEff`), so
they are not worth a loadout sweep.

## Self-describing cards — the card IS the run

A card carries its own run configuration, so the operator ticks **one** checkbox and presses the
spawn key. Every field falls back to the matching global when absent, so a card that declares
nothing behaves exactly as it always did.

**Since v0.91 that covers the fleet too**, which was the last thing still living in F1: `airframe` is
a comma list (one jsonKey per drone lane, wrapping) and `count` says how many drones one press
launches. So the whole procedure for a batch is now **tick `Drone/DroneEnabled`, tick one card, press
the spawn key** — no `Drone*` or `Scenario*` knob needs to match anything, because the card already
says it. That matters more than the keystrokes saved: hand-matching a global to a card does not
*refuse* when you get it wrong, it writes a capture that scores fine and answers a different
question, and the `Drone*` knobs are now purely the fallback for a card that declares nothing.

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
| `airframe` | `Drone/DroneAirframe` | comma list of Encyclopedia **jsonKeys**, one per drone lane, **wrapping** (`"Fighter1, Multirole1"` with 4 drones = two of each), or `""`. The drone harness **spawns** it, replacing the `DroneAirframe` list outright rather than merging with it. **Never prose**: the human description goes in `note` |
| `count` | the number of keys in `airframe`, else `Drone/DroneCount` | drones one spawn-key press launches. `0` = unset. Clamped 1..16 |
| `startAlt` / `startSpeed` | `Drone/DroneSpawnAlt` / `DroneSpawnSpeed` | used only when `> 0`; already the placement's target |
| `startSpeedCorner` | `startSpeed` | **v0.93.** Entry speed as a multiple of **the lane airframe's own corner speed** — the **FBW's** since v0.96, not the encyclopedia's AI one ([`AIRFRAMES.md`](../AIRFRAMES.md) trap 6) — resolved per lane. `0` = unset. When `> 0` it **wins over `startSpeed`**, which stays as the fail-soft fallback if the envelope cannot be read. Sane range 0.5–3.0 |
| `repeat` | `Scenario/ScenarioRepeat` | `0` = fall back. The **first selected card** decides for the whole queue |
| `armToggle` | `Scenario/ScenarioArmToggle` | must name a **bool**; interleaved ABBA. First card decides. Since v0.94 the arm is **per aircraft** (never written to the config), so every lane sweeps its own schedule concurrently |
| `config[]` | — | `"Section/Key"` (bare key ⇒ section `Control`); pinned at card start, **restored** at card end |

### The `sel[0]` rule — tick ONE card, and know which one is first

Ticking several cards is supported and it does something useful: `SelectRaw` returns **every** ticked
card and each drone flies the whole queue round-robin. What it does **not** do is give each card its
own run configuration. `airframe`, `count`, `repeat`, `armToggle`, `startAlt` and `startSpeed` are all
read off **`sel[0]` — the first selected card — and applied to the entire launch** (`Preview` and
`StartSuite` both take `sel[0]`; the spawn is one `Preflight` resolved once per batch). Card 2's
`airframe` list is never read. Card 2's `armToggle` is never swept. Card 2 is flown as a *stimulus*
only, at card 1's entry condition, on card 1's fleet.

**The trap that makes this bite on a fresh config: the built-ins are ticked and they are first.**
`Register` binds each checkbox with `builtIn` as its **default value**, so `fixedwing-v2`,
`rotorcraft-v2` and `fixedwing-sweep` default **TRUE**, and `LoadCards` registers the built-ins before
it scans the disk folder — so `_cards[0]` is `fixedwing-v2` and, unless someone has unticked it,
`sel[0]` is `fixedwing-v2` no matter which disk card you just ticked. It declares no `airframe`, no
`count`, no `repeat` and no `armToggle`, so the whole batch silently falls back to the globals:
**one `Multirole1`, one replicate, no A/B**, with your card flying second in the queue at
`fixedwing-v2`'s 250 m/s / 4000 m. Nothing refuses. The capture scores fine and answers a different
question — the exact failure the self-describing card exists to remove, reintroduced by a default.

Note also that the spawn's `sel[0]` is the **unfiltered** one: `Preview` deliberately applies no `cls`
filter (it runs before anything exists to have a class), while `StartSuite` filters by class. So a
ticked `rotorcraft-v2` can dictate the *spawn* while a `Plane` card downstream is what actually flies.

Two ways to be sure, in order of preference:

1. **`Scenario/ScenarioCardSet`** — a comma list of card names that overrides the checkboxes entirely.
   Its **order is `sel[0]`'s order**, it is one text field instead of N checkbox states, and it cannot
   be poisoned by a default. `ScenarioCardSet = e1-below-suppress` is the whole selection.
2. **Untick the three built-ins once** and leave them unticked — they persist in
   `com.no.wtmouseaim.cfg` — then use the checkboxes normally.

Either way the launch log's `[card] suite start` and the `[drone]` launch line name the source that
won for every field, and the **run board's PREFLIGHT panel** shows the resolved card, count, airframe
and A/B knob *before* you press the spawn key. Read one of them; the failure mode here is silent.

**Wall clock is set by replicates per lane, not by lane count.** The lanes fly concurrently, so eight
airframes take the same wall clock as one — R28 wrote **384 captures across 8 lanes in 30m14s**, i.e.
~38 s per capture per lane, matching a single-lane run of the same card. Budget
`repeat × card duration` and add `DroneStaggerSec × count` (default 3 s) for the launch ramp. The
practical consequence: **a card with a short `airframe` list is leaving measurement on the floor** —
if every key in the list clears the envelope gate, adding it costs nothing but the disk space of its
capture.

**Why `count` falls back to the airframe list before the global.** A card whose `airframe` is the
fleet it wants tested has already said how many drones it needs. Name twelve airframes, leave
`DroneCount` at 4, and the batch flies the first four lanes — no refusal, no warning, just a capture
set missing two thirds of the airframes the card exists to compare. Set `count` explicitly only to
fly a **multiple** of the list (`count: 8` over a 4-key list = two drones per airframe, since lanes
wrap); a non-multiple is legal and simply loads the early lanes.

### A multi-airframe card, worked

The one-law rule says a gain must hold across the roster, which until v0.91 meant a separate session
per airframe with `DroneAirframe` retyped between them — different session ages, and a chance to
mistype on each. As one card it is one press. **Pick the keys against
[`AIRFRAMES.md`](../AIRFRAMES.md), not from memory**: the entry condition is a card-level field, so
every airframe in the list has to be able to fly it, and this one asks for 250 m/s: fine for these
four (Vmax 401–479 m/s), impossible for `CAS1` (205.6) or `COIN` (141.7) and meaningless for a helo.

```json
{
  "name": "sweep-fleet", "cls": "Plane", "step": 0.02,
  "note": "The sustained-sweep geometry across the fixed-wing roster, one drone each.",
  "airframe": "Fighter1, Multirole1, SmallFighter1, FastBomber1",
  "startSpeed": 250.0, "startAlt": 4000.0,
  "repeat": 4,
  "segments": [ { "tag": "arm", "dur": 6.0 }, { "tag": "turn360fleet", "dur": 30.0, "…": "…" } ]
}
```

Four keys and no `count`, so four drones launch — lane 0 gets `Fighter1`, lane 3 `FastBomber1` — each
flying all four replicates of the same card, `DroneStaggerSec` apart. Read the result with
`compare-runs.py`, which **groups by airframe and refuses to pool across jsonKeys**: the four
airframes come back as four rows of the same segment, which is the comparison the card was written
to make. Three things to know before copying this shape:

- **A slower airframe needs its own card, or `startSpeedCorner` — not a slower `startSpeed` on this
  one.** One card is one test; dropping the absolute `startSpeed` to suit `CAS1` re-bands every other
  lane and the comparison stops being between airframes. Since v0.93 the third option is usually the
  right one: `"startSpeedCorner": 0.95` enters **every** lane at 95% of its own corner speed — 152 m/s
  for `Fighter1`, 152 for `CAS1`, 104.5 for `COIN` — which is both flyable by the whole roster and the
  same *aerodynamic* state on each, rather than the same number on each. Reach for a separate card
  when the test is about a specific speed rather than a specific regime.
  **The multiple is of the FBW's corner speed, not the encyclopedia's** (v0.96;
  [`AIRFRAMES.md`](../AIRFRAMES.md) trap 6 and its **FBW corner** column) — they differ by 0.556× to
  1.417×, so read the number off that column rather than the familiar one. All ten fixed-wing keys
  clear the envelope gate at `1.0x`; the shipped `oblique-*-c` family uses `0.95` and new cards should
  match it for comparability, not because 1.0 refuses. Nothing warns you in advance except the
  pre-spawn refusal line.
- **Keep `startSpeed` as the fallback, don't delete it.** `startSpeedCorner` needs the Encyclopedia
  envelope, and the resolver is fail-soft: if it cannot be read it warns and uses `startSpeed`. With
  no `startSpeed` the card degrades to `Drone/DroneSpawnSpeed` — the operator global, i.e. exactly
  the hand-matching the self-describing card exists to remove. The `oblique-*-c` cards keep
  `"startSpeed": 250` so a fall-through lands on their absolute twin's known-good condition.
- **`FastBomber1` is a two-seater.** Harmless since v0.90.1, when the per-aircraft step stopped
  running once per pilot — before that a two-seater flew everything at double rate. Seat count is
  prefab data with no code-side definition, so the spawn line's crew count is the in-game way to check
  it; `AIRFRAMES.md` records which airframes have two.
- **An A/B on a card like this is allowed since v0.94, and it is nearly free.** The old reason not to
  — one global knob, so the schedule stood down under concurrency — is gone: the arm is per-aircraft
  state read through the controller and every lane runs its own ABBA. What is left is arithmetic, and
  it is smaller than it looks: the replicate count is **per lane**, so `repeat: 8` across a 10-airframe
  roster is 80 *captures* but still 8 replicates of wall clock, because the lanes fly at once. Each
  lane needs its own multiple of 4 to stay balanced. The `e*` set is the attribution half and all four
  now name the eight-key 250 m/s roster for exactly this reason.

Five rules `scorecard.py --selftest` enforces, because nothing at runtime will:

- **`airframe` holds jsonKeys or nothing.** It was documentation for every card written before v0.90
  gave it behaviour, so every shipped card leaves it `""` and describes the airframe in `note`
  (`"note": "… AIRFRAME: any jet at the fixedwing-v2 entry condition."`). A jsonKey contains no
  whitespace, so the test is **per comma-separated token**: `"Fighter1, Multirole1"` is a fleet,
  `"any jet at the fixedwing-v2 entry condition"` is prose and the mod blanks the whole field at load
  with a `[card]` warning rather than trying to spawn a sentence. Whitespace *around* the commas is
  formatting and is trimmed.
- **`count` is 0..16.** The mod clamps, so `40` would silently fly 16 — the same silent-truncation
  shape as `repeat`.
- **`startSpeedCorner` is 0 or 0.5..3.0.** Nothing at runtime bounds it — the mod multiplies whatever
  it finds, and the only backstop is v0.92's envelope gate, which *refuses the lane*. So a typo'd
  `10.0` does not fly ten times too fast, it flies nothing at all and the batch comes back empty.

- **A `config` entry may not pin the knob `armToggle` sweeps.** Since v0.94 the arm *wins* (the law
  reads the swept lever through the controller, not off the config), so the pin changes nothing about
  what flew while `# config` prints its value and `# override` claims the card set it. Before v0.94 it
  failed the other way — every replicate on one arm, each capture still labelled `arm=0`/`arm=1`.
  Either direction is a capture that scores fine and describes a run that did not happen, so runtime
  refuses it too, loudly, and flies the rest.
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
- **A speed SWEEP, at any speed.** `startSpeedCorner` is one multiple for the whole card, resolved per
  lane against each airframe's own corner — so lane-by-lane speeds exist only as a side effect of the
  roster, confounded with airframe. `hs-hold` gets 341–440 m/s across four keys that way, and 250–300
  m/s and >450 m/s stay unsampled; a genuine sweep needs either four cards or a per-lane multiple the
  grammar does not have. **`hs-hold` also ships no matched low-speed control** — a twin at `1.0x` would
  accelerate straight out of its own band on any throttle ≥ `MinThrottle`, which is the `stol-*`
  failure again, so the low-q arm is the existing corpus (R39-D's mean \|`outR`\| 0.0068–0.0109 in
  sustained tracking, and the `oblique-*-c` family's `stickFlipRateR` on the same four airframes)
  rather than a card. That is a weaker comparison than a twin and is the first thing to fix if the
  card comes back FAIL.
