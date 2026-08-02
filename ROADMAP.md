# Roadmap — from here to a measurably better control law

`LAW-CHARACTERIZATION.md` is the *test plan* (what to fly, in what order, and why that).
This is the **decision tree**: what each result means and what we do next because of it.
Short by design. If a section can't say "and then we do X", it doesn't belong here.

Written 2026-07-30, during R29. **Updated 2026-07-30 after R30, R31 and R32 all reported** — the
decision tree below had four open branches when it was written and three of them now have answers.
**Updated again 2026-07-31**: the "Now" section still said R28 → R29 after both had landed, and the
backlog numbering was reconciled against `LAW-CHARACTERIZATION.md` §7, which is now the single
authority on what a `#n` means.

---

## What changed since this was written

| batch | question it was flown to answer | answer | where |
|---|---|---|---|
| **R29** | do all ten airframes fly, at `0.95x` their own corner? | yes, and the Darkreach departed on card 7 of 10 at 26.9 g — the event this whole thread comes from | `debugtests/R29-FINDINGS.md` |
| **R30** | is the down-step penalty direction, or position in the card? | **direction** — it survives traversal reversal | `debugtests/R30-FINDINGS.md` |
| **R31** | does disabling `BelowAlignSuppress` remove the penalty? | **unanswerable as posed** — `arm=0` selects the v0.67 *form*, not "off"; and `bWt` is identically 0 over the scored window, so `bSup` cannot be the transmission path | `debugtests/R31-FINDINGS.md` |
| **R32** | does the Darkreach precursor reproduce? | **yes**, in 4 of 5 lanes — and the departure turns out to be an **AoA/authority** defect, not a G defect | `debugtests/R32-FINDINGS.md` |

**Two premises this document rested on were false and are corrected below**: that the game governs G
(it does not — `ControlsFilter.GLimiter` is dead code), and that the law was *bending airframes*
(it cannot — over-G damages only the pilot). See "The G premise was wrong".

---

## Where we actually are

The honest summary of the last stretch: **there had been no law progress because the instrument
was broken, three separate ways, and all three were found inside about 24 hours.**

| | what was wrong | found |
|---|---|---|
| #29 | **No disk card had ever loaded** — `JsonUtility` silently dropped `Seg[]` in both directions, v0.71 → v0.90. Every gate and batch ran through built-in cards, the one path that could not fail. | v0.90.1 |
| #30 | **Two-seat airframes double-stepped the control law.** Integrators and rate filters advanced twice per physics step; finite differences read zero on the second call. | v0.90.1 |
| #37 | **`frameMs` measured nothing** — sampled inside `FixedUpdate`, where `Time.unscaledDeltaTime` returns a constant. 223,899 rows of R27 all read exactly 16.70 ms. | v0.92.1 |

None of those were control-law bugs. All of them would have corrupted any law A/B run against
them. That is the reason the loop stalled, and it is why the fix order below is *instrument first,
law second* — a law change measured on a broken instrument is worse than no law change, because it
produces a confident number.

**The instrument is now believed sound.** R28 (384 captures, 8 airframes) is the first batch flown
entirely after all three fixes. Its `Q0` verdict is the gate on everything below.

---

## The two pillars

Both come from the same question — *is the law doing the best it could, or is the airframe the
limit?* — and neither is fully built yet.

**Pillar 1 — convergence quality.** Does the nose ever move *away* from the commanded direction?
Does it approach and then recede? A law that converges monotonically is doing its job; one that
overshoots, backs off, and re-approaches is fighting itself. Metrics: `retreatDeg` (integral of
error increase while a demand is standing), `retreatEpisodes`, a monotonicity index.
**Status: not built** (#33). **Needs no new flying**: it is derivable from the existing `off`
column, so it applies retroactively to every capture already on disk — 1 032 archived captures
across R28–R32. No card, no batch, no game session.

**Pillar 2 — capacity utilization.** Was the *airframe* the limit, or the *law*? A segment sitting
on the bank clamp is measuring an actuator, not a control law, and its metrics cannot respond to a
gain change — `scorecard.py` flags those `RAILED` and they must be read as **no signal**, not as a
bad score. The inverse case is the interesting one: plenty of authority left and still a standing
error, which means the law chose not to use what it had.
**Status: half built.** v0.92 shipped `authorityUsedFrac` — authority *used*. The rework (#36) is
authority used vs authority **needed** (`omega_target = min(omega_avail, off/tau)`), because a 0.5°
step legitimately uses 4% of authority and the first version would have flagged every small step as
slack. That gating is a stopgap and it's honest about being one.

**Pillar 1 is the higher-value build**, it is the cheaper one, and it is the only item on this page
that costs **zero flying**. Do it first.

---

## The decision tree

### Now: the next decision, after R29–R32 all reported

R28 and R29 are flown and analysed; R30 and R31 answered their questions; R32 found the law defect.
**Nothing is waiting on a batch to land.** The next decision is *what to build and fly next*, and it
splits cleanly in two because one half needs no flying at all:

**(a) Build #33 — retreat integral. Start here.** It is the cheapest high-value item left, it needs
**no new flying**, and it is derivable from the `off` column on captures **already on disk** (1 032
archived captures, R28–R32). Every batch above was read without it, so every "is the law converging
or fighting itself?" question so far was answered by proxy. See "The two pillars".

**(b) Fly E4 — the precursor, not the floor.** `LAW-CHARACTERIZATION.md` §4 Batch 4 row E4: the
`darkreach-05` geometry with the **roll-to-align channel as the arm**. That card does not exist yet
— `darkreach-05` deliberately carries **no** `armToggle` — so writing it is the work. Recs 01–31 of
R32 are its 31-replicate clean baseline on the same airframe and card. **Only then** E5, the
`schedFloor` A/B (#45).

**Two things to settle before re-flying the belowness axis (E1).** R31 recorded both:
`BelowAlignSuppress` `arm=0` selects the v0.67 *form*, not "off", so the A/B as posed cannot answer
"does the suppression cause it"; and `bWt` is identically 0 over the scored window, so `bSup` cannot
be the transmission path. The third card (`oblique-above-c`) is written and unflown
(`cards/TOMORROW.md` §8), but flying the axis without fixing the arm semantics repeats R31.

**And a hard comparability line: v0.96 changed which corner speed a corner-relative card resolves
against** (#41 — the FBW's, not the AI's; they differ 0.556×–1.417× across the roster). **Every
corner-relative capture from R29 and earlier is NOT poolable with anything flown after it.** Do not
re-baseline across that line, and do not read a v0.96 batch as a continuation of R29's. Absolute
`startSpeed` cards — `darkreach-05` among them, deliberately — are unaffected.

The oblique family is the workhorse because R27 measured it at **4–23% authority used** —
unsaturated, so its metrics can actually respond to a gain change. The sweep/turn360 family sits at
~100% and mostly measures actuators. That reordering is the single most useful thing R27 produced.

### The branch that matters: is it one law, or one law that suits the Ifrit?

This sits directly under the core design rule and it is the largest open risk in the project.
R28/R29 answer it. Three outcomes, three different projects:

| if the per-airframe spread is… | it means | next |
|---|---|---|
| **inside the replicate noise floor** | the law generalizes. The rule is being met. | Go straight to the defect A/Bs below. This is the good case and it makes everything after it cheap. |
| **outside noise, and tracks a *measurable* quantity** (wing loading, distance above corner, mass) | the law generalizes in *form* but a schedule is mis-keyed | Fix the schedule to key off the probed quantity. Still one law — this is exactly what the rule permits, since the parameter is measured, not hand-set. |
| **outside noise and tracks nothing measurable** | the law is tuned to one airframe | Stop and rethink before any defect work. Fixing a defect on a law that only suits one airframe is polishing the wrong thing. |

#### R28's answer: **it is not tuned to the Ifrit — but it is not one law either**

Median `flightscore` A, tag-matched, never pooled. Noise floor **0.0034 A** (median replicate sd).

| # | airframe | A | gap to next |
|---|---|---|---|
| 1 | Fighter1 | **0.705** | 11× noise |
| 2 | SmallFighter1 | 0.667 | 3.8× |
| 3 | trainer | 0.654 | 3.5× |
| 4 | **Multirole1 (Ifrit)** | 0.642 | **0.9× — not separable** |
| 5 | VTOLTrainer1 | 0.639 | 4.7× |
| 6 | EW1 | 0.623 | 19× |
| 7 | FastBomber1 | 0.559 | 27× |
| 8 | Darkreach | 0.468 | — |

Spread 0.237 A = **70× noise**, so the ranking is real. But read it as **three groups, not eight
ranks**: Fighter1 alone; a five-airframe band 0.623–0.667 where adjacent pairs are inside noise;
two failures. **The Ifrit is 4th, mid-band** — the long-standing worry that the law was tuned around
it is not supported.

Row 2 of the table above is the live branch: the spread is real and the failure is at the **heavy**
end. Best correlate is `aircraftGLimit` (ρ **+0.810**), but mass / g-limit / T:W / wing area are
collinear at n=8 and **the cause is not identifiable from this batch**. Explicitly excluded:
two-seat (both twin-seaters rank mid-band), FBW `assist=0` (EW1 has it and scores fine), distance
above corner (sign is backwards). R29's ten airframes and swept entry condition are the next
evidence; identifying the cause probably needs a card that varies loading on **one** airframe.

R29 sharpens this: R28 asks all eight airframes the same *numeric* question (250 m/s is 1.39×
corner for `Fighter1` and 2.8× for `COIN` — not the same question at all), R29 asks them the same
*aerodynamic* one. **Read pairwise.** Same geometry, entry condition swept, which is also the first
test of whether the law's behaviour is a property of the speed or of the regime.

### Then: the defects — **REWRITTEN 2026-07-30 after R28. The old order was wrong.**

This section used to say "fix #20 first, then #21". **R28 measured both of them inert on the
unsaturated family**, so that plan would have produced two confident nulls:

- **#20's premise is CORRECTED, not closed — and it is no longer an experiment.** "Unreachable" is
  true only of the v0.67 self-probe path (the latch-breaker LPFs toward 0.15 from below and
  asymptotes, so `>=` never trips *from there*); it is **not** true that `_pitchEff` never goes
  below 0.15. Over all **1 032** archived captures (627 110 rows, R28–R32), **28 209 rows = 4.50%**
  sit below the threshold, min **0.000**, across **89** captures on **two** airframes (`Darkreach`
  27 622, `FastBomber1` 587) — genuine reversed-plant rows where the no-floor branch is *correct*.
  The signature that pins the defect is sharper than the occupancy: **2 811 rows read exactly
  `0.150` and only 8 read above it**, the LPF parked on its own target, which is the asymptote
  mechanism confirmed directly. So `>=` → `>` is right and cannot regress anything, but it moves
  **0.45%** of corpus rows, all at the boundary. **Ship it as hygiene behind its own checkbox; do
  not schedule an A/B.** An A/B written as "unlock a dormant branch" reports a null, and that null
  reads as "the diagnosis was wrong" when it is not. (This also retires the earlier "5.2% / 8
  captures / three airframes" figure, which reproduces against no batch.)
- **#21 rails on 0 of 1344 healthy segments.** `bWt` median is **0.000 everywhere**: the bank
  pipeline is not *railed*, it is **dormant**. A rail you never reach costs nothing.

Neither is retracted as a code defect. Both are demoted to "real but unmeasurable here", and
neither gets an A/B until some card actually wakes the bank pipeline.

**The new first target: the down-step penalty (R28 F1).** At matched magnitude, mirrored geometry
and matched terminal elevation, stepping the nose **down** leaves **1.2–17.9×** the terminal error
of stepping up. Universal on 7 of 8 airframes, 20–1000× the noise floor, and *not attributable to
any instrumented lever* — energy excluded, belowness excluded (it persists where `bSup` reads
0.000–0.06), residual nearly pure azimuth. It is the largest unexplained effect in the corpus.

**One control card runs first, before any fix.** Every oblique card traverses N→E→S→W→N, so the two
down legs are **always** segments 2–3 and the up legs always 4–5: direction is perfectly confounded
with position in the card. R28 could exclude energy and elevation but not order. `oblique-12-fwd` /
`oblique-12-rev` fly the identical diamond in both traversal directions, 3 airframes × 8 replicates,
one batch, ~10 min, no code change. Direction ⇒ the penalty survives reversal unchanged.
Position ⇒ it inverts and the *up* legs become the bad ones. **A fix aimed at the wrong half of
that question is aimed at nothing.**

**The new highest-value target after that: the AoA-schedule authority failure (R32).** This is the
**first genuine law defect in a while** — every other thing found lately was instrument. See the
section below.

Then, still open and unchanged in priority:

- **#14 — `predFloor`'s hard 0.30 step** wants a continuous lead-confidence blend. A ONE-LAW smell
  (a constant where a probed quantity belongs), not yet shown to cost anything. Fix it when the
  measurement says it does, not because it's ugly. **R32 makes its sibling — `schedFloor = 0.3f` —
  the one that *is* shown to cost something**; fix that one first and this one becomes the same
  change applied twice.
- **#23 — the placement-tick reset defect.** Still deliberately unfixed and *deliberately not*
  symptom-patched. R28 corrected its signature: the "`rollRate ≈ −59`, `leadDeg` 7–14°" claim was an
  artifact of one batch whose previous card ended in a hard turn. Median `|rollRate|` at `tSeg=0`
  across 384 captures is **0.725**, and **0 of 384** land in that `leadDeg` band. Decays to 0.006 by
  end of `arm`. Untraced lead: `PlaceOnCondition` has two call sites, only one followed.
  **R32 corrected it again, and this time it is load-bearing.** The distribution is **bimodal**, and
  R28 measured only the lower mode. Over R32's 58 placed captures: median `|rollRate|` **0.753** (R28
  reproduces exactly) but **19 of 58 above 5**, max **54.2**; `|leadDeg|` max **314°**;
  `|headingRateFilt|` max **483 °/s**; and `|outP|` **rails at 1.000 on 15 of 58 placement ticks**.
  The magnitude is set by the attitude the *previous* replicate ended in, so on the Darkreach a
  departed replicate hands the next one a full-authority spurious command on tick zero — which
  departs it inside the 6 s `arm`, before the card demands anything. It does **not** "decay before
  the scored segment starts" outside the light, high-authority airframes it was measured on.
  Still not to be symptom-patched (a guard on `rollRate` alone hides `headingRateFilt`/`leadDeg`),
  but it is no longer "harmless to results so far".
- **#41 — DONE (v0.96), and it draws a comparability line through the corpus.**
  `startSpeedCorner` now resolves against `ControlsFilter.FlyByWire.cornerSpeed` (the flight model's)
  instead of `aircraftParameters.cornerSpeed` (the AI's); over 1 604 archived sidecars the two differ
  by 0.556× to 1.417×, a 2.2× spread. **Corner-relative captures from R29 and earlier are NOT
  poolable with anything flown from v0.96 on** — say so in any writeup that spans the line, and never
  re-baseline across it. `AIRFRAMES.md` trap 6 is the record of why the two fields are different
  quantities. `darkreach-05` keeps an **absolute** 171 m/s so it reproduces R29 bit-for-bit — do not
  "modernise" it to the corner-relative form, it is a reproduction card (its `note` still says the
  fix is pending; that wording is stale, the card is right).
- **#44 — DONE (v0.96).** Damage is recorded and it ends the replicate: CSV column `dmgFrac` (65),
  the sidecar's `detachedRatioAtStart`, an any-detachment abort in `ScenarioPlayer.Tick`, and a
  `DAMAGED` warning from `scorecard.py`. **Standing rule for reading a batch: DROP damaged runs, do
  not covary them out** — damage changes the airframe, so a damaged replicate is not a noisy sample
  of the same thing. This matters most on exactly the batches this page is about: R32's departures
  killed three pilots.

### The G premise was wrong — twice, and both corrections change what to build

Found 2026-07-30 by reading the 0.34 decompile against R29/R32. Full evidence in
`debugtests/R32-FINDINGS.md` §1–§2.

**1. The game has no G governor.** `ControlsFilter.GLimiter` is dead code: the identifier occurs
**exactly once in 181 878 lines**, as its own `protected class` declaration; no field of that type
exists, nothing instantiates it, and its `LimitG(...)` has zero call sites. CLAUDE.md's Conventions
line *"No mod-side G-limiter — the game's stability control governs"* is false for G. What exists is
the FBW's `targetPitchAngVel = pitch · gLimitPositive · 9.81 / max(V, 0.75·Vc)` (`:65032`) — a rate
command *scaled by* g-limit, with no feedback on achieved G. The mod already reconstructs it
correctly as `rpsRef`; that is a feed-forward cap on demand, not a governor on outcome.

**Worse, the FBW's alpha limiter is gated `if (num2 < 1f)` (`:65033`) and is therefore inactive
above corner q — which is where every shipped card flies.** On R32, `num2 < 1` on 2.3% of rows; of
the 5 541 rows past the airframe's own 10° `alphaLimiter`, 86.3% had the game's limiter structurally
off. **The mod's AoA block is the only alpha protection in the loop at card speeds.**

**2. Over-G damages the pilot, never the airframe.** `Pilot.TakeGForceDamage` (`:85989`) fires above
20 g and applies `(sqrG − 400)·0.007` as `impactDamage` to **one part index — the pilot's own**
(`Unit.Damage(byte index, DamageInfo)`, `:88865`). No structural-G path for the airframe exists
anywhere in the decompile. R32 confirms it: three drones ended `despawned (pilot killed)` while
`aeroPartCount` stayed 35 and `massKg` constant on all 63 captures.

**So the "the law is bending airframes" theory is retracted.** It was stated to the user and it was
wrong. The 26.9 g R29 row previously cited as the law over-G-ing an airframe is a **departed**
airframe at 80 m/s, AoA **−77.3°**, falling through 944 m — a consequence of the departure, not its
cause (`R29-d10-Darkreach-90-oblique-2-c`, min AoA −100.5°, min speed 79.7 m/s, `# stop reason=abort:
aircraft gone`).

**Recommendation: do NOT add a mod-side G-limiter.** Three reasons, in order:
- It protects nothing. The airframe cannot be over-G'd; only the pilot can, above 20 g, which is
  reached *after* the departure while falling.
- **It would mask the defect.** The high-G rows are the *readout* of a departed airframe and are the
  most legible failure signal in the corpus — R29's departure was found *because* of that 26.9 g row.
  A limiter clips the readout and changes nothing about the descent.
- It is a **sixth de-authorizing term** on a law whose actual defect (below) is that it already has
  five and no recovery mode at all.

### #45 — the AoA-schedule authority failure (R32). The first genuine LAW defect in a while.

Everything else found lately was instrument (#29/#30/#37). This one is the control law.
`GENERALITY-REVIEW.md` finding **18** — that file's finding numbers are a **separate namespace**
from the backlog's; evidence in `debugtests/R32-FINDINGS.md` §5–§6. **The backlog number is #45 and
it means this and only this** — see `LAW-CHARACTERIZATION.md` §7, which is the sole authority on
what a `#n` means. (The "belowness axis" work briefly also called #45 is not backlog: it is
experiment **E1**, card `cards/oblique-above-c.json`, runbook `cards/TOMORROW.md` §8.)

**What was measured.** `Darkreach` on `darkreach-05`, 63 captures / 37 868 rows, 18 departures, 3
dead pilots. At the moment a departure starts the mod commands |`outP`| ≤ 0.24 in the *correct*
direction, `iPitch` is ±0.011 against a 0.12 cap, and the plant delivers pitch rate the other way:
`fbwTgtPR` −0.050 rad/s against `fbwPR` +0.60 — **12×** overshoot (median on departed captures 7.7×,
p90 13.0×, max 28.2×; clean captures 1.56×). Over the 2 314 rows past |AoA| 20°, `qSched` is exactly
**0.300 on 100.0%** of them.

**Why it is a ONE-LAW defect and not just a bad airframe.** `schedFloor = 0.3f`
(`ChaseController.cs:1255`) is a hardcoded constant terminating the schedule's range at the same
place for a 27° ceiling on an 8.7 t `Fighter1` and a 10° ceiling on a 105 t `Darkreach`. Its two
siblings are the same shape (`:1152`, defensible — it mirrors the game's own `:65034` clamp; and
`:1296` `Max(0.3f, aoaGateUp)`, which is not). And the law's *entire* response to a non-responding
plant is five terms that each **reduce authority** — including `aoaRecover *= _pitchEff` (`:1557`),
which scales the one term documented as "the term that flies the nose back INSIDE the envelope" by an
estimator reading 0.036–0.144 for the whole departure. **Nothing in `Apply` increases authority or
changes strategy.** On nine airframes their own stability covers the gap; on this one it does not.

**Sequencing — do not fix the stand-down first.** R32 §4 shows the schedule railing is *downstream*
of a precursor: 34–56° of `targetBank` at |`azErr`| < 5° on a card whose largest step is 0.35°, on
0 of recs 01–31 and on 12 of recs 32–63, appearing 1–2 replicates before the departure in every lane.
`GENERALITY-REVIEW.md` #16 (`lateralHold` rails ⇒ `blendWeight` = 1 ⇒ the bank pipeline disconnects)
is the standing candidate. Fixing the floor first would make some departures survivable, which is
strictly worse than a departure that is legible.

**What to fly next, in order:**
1. A card that isolates the **precursor** — `darkreach-05` geometry with the roll-to-align channel
   swept as the arm. `darkreach-05` gives it a 31-replicate clean baseline on the same airframe and
   card, which is what makes it usable as an A/B.
2. Only then, an A/B on `schedFloor` expressed relative to a probed quantity (`omegaMax`,
   `_fbwMaxPitchVel`, the alpha ceiling `aoaUtil` already normalises by) rather than as 0.3.
3. Cheap side-check: **`EW1`** shares `assist=0`, `maxPitchAngVel = 0.3` and `alphaLimiter = 10` at a
   quarter of the mass, and has never been flown on this card. If it shows the precursor, the
   airframe-side half of the story is the FBW params, not the mass.

### The two airframes that aren't flying the card

Separate from the law, and they contaminate any roster average that includes them:

- **Darkreach** — 82 of the batch's 82 RAILED warnings. Decelerates 250→202 m/s and descends
  4000→2206 m per card, reaching 98.6 m/s / 784 m at worst; 6 of 48 captures peak over 90° error
  (max 179°). `_pitchEff < 0.15` on **65.4%** of rows, so the law zeroes its own pitch term.
  Replicates are **bimodal, not noisy** (seven at 4.24–4.30, one at 143.2). Both the card and the
  law are at fault and they cannot be separated from this data.
  **R32 separated them.** At an entry condition this airframe *can* fly (171 m/s / 4000 m, absolute),
  31 consecutive replicates are clean to within 0.2° of peak AoA — so the card was most of the R28
  story. What remains is the real law defect above. The `_pitchEff < 0.15` occupancy is not peculiar
  to R28's card either: across all 1 032 archived captures (627 110 rows, R28–R32) it is **4.50%** of
  rows, and **27 622 of those 28 209 rows are `Darkreach`** — the other 587 are `FastBomber1` and no
  other airframe reaches it at all.
- **FastBomber1** — 0.559 A, 27× the noise below the main band. Heaviest sinker on every card.

**Uncontrolled confound across the whole batch:** the cards pin throttle 0.70 and do not hold
speed. Multirole1 gains **+92 m/s** over a capture; Darkreach loses **−48**. Any cross-airframe
comparison inherits that.

### What each defect fix costs to prove

One A/B each, on a card where the defect can actually bite, gated on `blendRailPct ≈ 0` so we're
not measuring the clamp again (that mistake is what made the v0.83 A/B worthless — it ran at 96.9%
clamped).

**Until v0.94 this was a 10× serial grind.** The arm knob was a process-global `Cfg` bool, so N
drones could not fly different arms and the scheduler stood down — hence `"count": 1` on all five
`e*` cards. v0.94 moves the swept knob onto the aircraft, and each aircraft becomes its own
internally-balanced ABBA experiment. That turns "one airframe per A/B run" into "the whole roster
in one batch", and it is the reason the defect phase is now affordable at all.

---

## Ordered backlog for the harness itself

Only the ones that block something. Full list in `LAW-CHARACTERIZATION.md` §7 — **which is the only
authority on what a `#n` means.** Allocate a new number there, as `max(existing) + 1`, never into a
gap; highest in use is **#46**.

| | blocks | note |
|---|---|---|
| **#33** Pillar 1 retreat integral | reading any batch as a law verdict | Cheapest high-value item left, and it needs **no new flying** — derivable from `off` on the 1 032 captures already on disk. **Build this first.** |
| **#27** concurrent A/B | the entire defect phase | **DONE — v0.94, and verified IN FLIGHT by R31**: 136 overlapping capture pairs on opposite arms, all six lanes airborne on both arms at once. One caveat R31 recorded: the lanes are **not decorrelated by queue ordinal** — every lane runs the identical `0110011001100110`. |
| **#36** Pillar 2 rework | trusting the slack signal outside the two gated card types | Stopgap shipped and honest about it. |
| **#38** card altitude budget | nothing yet, but it will bite | `oblique-below` loses 4323 m worst-replicate; from a 1500 m start it would finish below sea level. Nothing checks this. |
| **#39** `startSpeed: 0` is both "hover" and "unset" | **the whole rotorcraft phase** | Fix with a nullable `float?` — Newtonsoft distinguishes absent from explicit 0. Do it with the hover entry condition. |
| **#46** `SplitSpec` multi-slash divergence | nothing — it blocked nothing, both sides were fail-soft | **DONE — v0.96**, one-line C# refusal, and `python debugtests/test-spec-grammar.py` now runs both halves off ONE case table. Verified green 2026-07-31. |

## After fixed-wing

Helicopters and VTOLs, as a second run — agreed sequencing. Blocked on **#39**, and on the
rotorcraft entry condition being expressible at all (`rotor-*` cards declare `startSpeed: 0`
meaning *hover*, which the resolver currently reads as "the card doesn't say" and falls back to
250 m/s — v0.92's envelope gate now refuses those lanes, which is the gate correctly exposing a
pre-existing mismatch rather than a new problem).

---

## Standing rules that keep this honest

- **A railed segment is no signal, not a bad score.** Read `RAILED` warnings before any metric.
- **Never pool across airframes.** The tools refuse; don't work around it.
- **A refusal is always a log line.** The harness runs unattended, so a key that appears to do
  nothing must be explainable afterwards.
- **One card = one test**, and the reset is per card, not per segment.
- **A mismatch never refuses** — it writes a capture that scores fine and answers a different
  question. That is the failure mode this whole harness is built against, and it is why the card
  now owns its own airframe, count, altitude, speed, replicate count and A/B knob.
