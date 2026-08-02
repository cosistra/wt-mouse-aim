# LAW-WEAKNESS-MAP

Where the flight law is weak, ranked by **effect size x confidence x how cheaply it can be settled**.
Written 2026-08-02 after a full adversarial pass over the corpus (9,137 segments, 2,181 captures,
R1–R37) in which every candidate weakness was assigned an agent whose job was to refute it. Most were
refuted. What is below either survived that, or is what survived *inside* something that did not.

**Scope.** This is a map of the *law* (`ChaseController.cs`) and of the *instrument* that measures it
(`debugtests/scorecard.py`, the cards, the corpus). It is not a task list — durable backlog lives in
`LAW-CHARACTERIZATION.md` §7 and `LAW-LEDGER.md`. Nothing here has been fixed; nothing here has been
flown against.

---

## TL;DR — thirty seconds

> **HEADLINE OVERTAKEN 2026-08-02 — read this before the table.** This file was written 2026-08-02
> *before* the R39 batch (v0.98.1) flew. Four of its eight "fly this" instructions have since been
> executed or invalidated. The corrections are inline at each W-section; the summary is:
> **W1 — DONE and the answer was worse than the hypothesis.** `-t040` and `-t100` both flew (R39,
> 64 captures each). `oblique-6-dwell` is now **RETIRED as a between-airframe ranking instrument,
> all 314 captures** — its four legs are four flight conditions and *no throttle makes them one*
> (drift 0.96–2.14×, descent throttle-independent at Pearson +0.997). It stays valid as a
> within-lane, within-airframe A/B. Source: `debugtests/R39-B-card-validity.md`.
> **W4 — the card named below is retired**; fly `alpha-pullup`, not `alpha-sweep`.
> **W5 — CLOSED WONTFIX**, the detector was deleted.
> The standing-hole paragraph at the end of this section is also stale — see its own correction.

~~**Fly `oblique-6-dwell-t040` and `oblique-6-dwell-t100` first. Tonight's single largest finding is that
the corpus's flagship ranking card does not hold the flight condition it claims**, so the 7.6x
"airframe spread" everyone has been ranking is not identified from a 2.6x live-speed spread. Both fix
cards are already written and have never been flown (§W1). Until they are, **no between-airframe claim
on `oblique-6-dwell` can be believed**, including several that were used as evidence tonight.~~

Then, in order:

| # | weakness | kind | fly / code |
|---|---|---|---|
| **W1** | `oblique-6-dwell` lets speed drift 1.09–2.03x within a capture; airframe ≡ live speed — **CONFIRMED AND WORSE (R39): the card is RETIRED for ranking, all 314 captures** | **measurement blocker** | ~~fly `-t040` + `-t100`~~ **done, R39**; needs a *new* ranking card |
| **W2** | The roll / bank / settle path is scheduled against **nothing the law can measure** — no probed roll parameter, no roll-effectiveness estimator | **ONE-LAW, structural** | code (build the roll twin of `_pitchEff`); Darkreach is the live exemplar |
| **W3** | Down-step legs converge worse than up-step legs, in both leg orders | plain effect, mechanism open | fly `e1-below-suppress` + `e1-below-control` (cards exist, never flown) |
| **W4** | `aoaFade`'s "proportional" band is **empty**: it is a 4°-or-6° constant on all ten flown airframes | **ONE-LAW, latent** | ~~fly `alpha-sweep`~~ **retired after R39 — fly `alpha-pullup`** (`cards/ALPHA-CARD-REDESIGN.md`) |
| **W5** | ~~The SLACK detector has been structurally dead since R28~~ **CLOSED WONTFIX v0.99.1 — the detector and the metric under it were DELETED.** The gap it named ("nothing detects the law leaving authority on the table") is real and now has **no** detector at all | instrument gap | design a real one; do **not** restore SLACK |
| **W6** | `terminalOffDeg` + the whole g family are lane-distance artifacts and are still un-caveated | instrument gap | docstrings only, ~20 lines |
| **W7** | FastBomber1 runs 4–10x hotter in steady-state pitch than the fleet, in every batch, for six mod versions | open, unexplained | re-measure on the t-cards |
| **W8** | The `_pitchEff` estimator's noise gate is an absolute rad/s constant | ONE-LAW smell, measured inert | one line in `GENERALITY-REVIEW.md` |

And the standing hole that dwarfs all of them: **two of the four airframe cases the ONE-LAW rule names
— the low-limit STOL trainer and the hovering helo — have essentially no drone data on the current law.**
~~`AttackHelo1` has 1 capture in 2,181, hand-flown at R11 on mod 0.71.0. `rotor-hover`, `rotor-bob`,
`stol-steps` and `stol-sweep` have **zero captures each, ever**.~~

> **UPDATED 2026-08-02 — all four cards have now flown (R39, v0.98.1), and the hole is still open,
> for new reasons.** `rotor-hover` 24 + `rotor-bob` 24 + `stol-steps` 40 + `stol-sweep` 13 = 101
> captures. Neither case is closed by them:
> - **Hovering helo:** `_heloOk` was **false on all 48** rotorcraft captures — the v0.58 rotorcraft
>   branch never executed, so they measure the *pre-v0.58* law, not the shipped one. Blocked on a
>   probe call-order fix. `debugtests/R39-rotor.md` §1a.
> - **STOL trainer:** the card declared 90 m/s but throttle was unpinned at 1.00, so eight of ten
>   lanes were at **340–381 m/s (2.1–2.4× corner)** by the last scored segment — *faster than
>   anything else in R39*. It is a second high-q dataset, not STOL data. `debugtests/R39-stol.md` §2.
>
> So the correct current statement is: **the two cases now have captures and still have no valid
> measurement.** Both need a re-fly, and both re-flies are gated on code fixes.

---

## W1 — `oblique-6-dwell` does not hold its flight condition; airframe is collinear with live speed

**Claim.** The card that produced 314 of the corpus's captures and every modern between-airframe
ranking pins entry speed to 0.95x each airframe's *probed* FBW corner speed — correct, ONE-LAW-compliant
— and then pins **throttle at 0.70 for every airframe**. Nothing holds speed afterwards. Each lane
therefore accelerates away from its entry condition at a rate set by thrust-to-weight, so the card's
four legs are four different flight conditions and the amount of difference is an airframe property.

**Evidence.** R37, n=496 scorable legs, V/Vcorner measured in the 7–8 s `fixedWindowOffDeg` window:

| | leg 1 `obDR6` | leg 4 `obUR6` | within-capture drift |
|---|---|---|---|
| CAS1 | 0.94 | 1.03 | 1.09x |
| COIN | 0.95 | 1.03 | 1.09x |
| Fighter1 | 1.28 | 1.89 | 1.48x |
| Multirole1 | 1.35 | 2.37 | 1.75x |
| **Darkreach** | 1.22 | **2.49** | **2.03x** |

Across the roster: 0.94 → 1.35 on leg 1 (1.4x spread at the scoring instant), 1.03 → 2.49 by leg 4
(2.4x). Live V/Vcorner at the window ranks the outcome at **Spearman +0.709** vs `fixedWindowOffDeg`
(n=10) and raw V at +0.588 — **as strong as or stronger than any probed parameter proposed all night**
(best probed: `omegaMax` from `sc_gLimitPositive` -0.78, mass +0.70, `sc_maxPitchAngularVel` -0.60).

**ONE-LAW or bug?** Neither — it is a **measurement blocker**, and it is why four separate adversarial
agents independently concluded "airframe is perfectly collinear with airspeed, the spread is
unidentifiable." Live dynamic state is exactly what the standing rule *permits* a schedule to key off,
so a ranking that tracks live speed is not evidence of a violation. It is also not evidence of
compliance. The card cannot tell them apart.

**Mechanism.** `cards/oblique-6-dwell.json` — `startSpeedCorner: 0.95`, no throttle override, so the
harness default `Scenario/ScenarioThrottle = 0.70` applies to all ten. The down legs run first
(`seg_index` 1,2 = `obDR6`/`obDL6`), which also confounds W3's direction effect with a ~1.5x speed change.

**Discriminating test — the cards already exist.** `cards/oblique-6-dwell-t040.json` and
`cards/oblique-6-dwell-t100.json` are byte-identical to the anchor except for a pinned
`Scenario/ScenarioThrottle` of 0.40 / 1.00, `repeat: 4`, and `t04`/`t10` tag suffixes so no tool can pool
them. Three throttle arms x 16 lanes is a **within-lane, within-airframe q axis** — the only design in
the card set that separates airframe identity from live speed.
*Pass:* the airframe ranking is stable across the three throttle arms ⇒ it is an airframe property and
W1 is closed as a caveat only.
*Fail:* the ranking reorders with throttle ⇒ "airframe spread" was speed spread, and every ranking claim
in `R28`–`R37`-FINDINGS needs re-reading. *Watch:* `turnRateDemandRatio` and `blendRailPct` per lane —
a lane that decelerates below ~0.7x corner will rail its demand and produce no signal.

**Status.** OPEN. Discovered R37 (2026-08-01). Fix cards written, never flown. Cost: ~2 x 20 min
unattended. **This is the first thing to fly.**

---

## W2 — the roll / bank / settle path is scheduled against nothing the law can measure

**Claim.** The pitch axis has two independent per-airframe adaptations: the probed FBW rate map
(`_fbwCorner`, `_fbwGLimit`, `_fbwAlphaLimit`, `maxPitchAngularVel`) and a *measured* plant-authority
estimator, `_pitchEff` = achieved/commanded pitch rate. The roll axis has **neither**. Every constant in
the roll, bank-target and settle path is a fixed number: `kSettle = 8f`, `settleGate = 0.5f`,
`settleCap = 4f`, `BankSlewRate = 60 deg/s`, `RollRateSmoothing = 0.06 s`, `FineBankDeadzone = 2.5°`.
`ChaseController.cs` reads no roll rate limit, no inertia, no mass, no wing area — 16 references to
`_pitchEff`, **zero** to any roll analogue.

**Evidence.** One airframe shows it, and it shows it in every batch it has flown:

- **A coherent ~0.35 Hz azimuth/bank mode on Darkreach only.** `wobbleFreqHzAzErr` = 0.349 Hz with
  `wobbleEpisodesAzErr` = 0.675 over its 40 `oblique-6-dwell` segments (R35+R36+R37), against
  **exactly 0.000 episodes and NULL frequency on all nine other airframes over 1,080 segments.** Per
  batch: 0.352 / 0.346 / 0.349 Hz. Also present at 0.337 Hz on a different card (R33 `oblique-6-c`,
  mod 0.96.0) and at 0.680 Hz in R28.
- **It converges** — this matters for what a fix must do. Detrended `azErr` amplitude across the 30 s
  leg: 0.588 → 0.306 → 0.127 → 0.087 → 0.063 (R37, n=16 legs), a 9.4x monotone decay, τ ≈ 11 s,
  ζ ≈ 0.044. Same curve in R35 (0.651 → 0.079) and R36 (0.657 → 0.083). Lightly damped and **stable**,
  not a limit cycle.
- **Frequency is amplitude-invariant** across a 9x amplitude range, two cards and four mod versions —
  the signature of a lightly damped *linear* mode (plant + fixed-gain loop), which is exactly what an
  unscheduled loop against a heavy plant produces.
- Darkreach is also the only airframe with a 0% `settleTime95` rate (0/40 across R35/R36/R37; 0/16 in
  R37 alone) and the only one whose fitted terminal asymptote clears the recorder quantum
  (A = 0.065°, 2.8x the next-worst).

**ONE-LAW or bug?** **ONE-LAW, and it is the cleanest structural instance in the codebase.** The
stability argument for the settle path (`ChaseController.cs:1834-1843`, referencing §"Why B2-A cannot
limit-cycle") is a pure *gain-margin* argument — "8 deg/deg is ≤3x below the 22 deg/deg that already
rocked at 220 m/s" — with **no phase term and no plant model**. Against a 105 t / 383 m² airframe with
`sc_maxRollAngularVel = 3.0`, gain margin alone does not bound the damping ratio.

**Mechanism, with citations.**
- `ChaseController.cs:1839-1849` — the B2 settle injection: `kSettle = 8f` (V-INDEPENDENT by design),
  `settleGate = 0.5f`, `settleCap = 4f`, `_settleOn`.
- `ChaseController.cs:1990-1997` — the eAlign lead; the only "adaptation" in the roll path is
  `_phiRateFilt * Cfg.RollDamping`, a live *rate*, not a plant estimate.
- `ChaseController.cs:1972` — the code concedes the gap: *"a P-only loop against a plant with real roll
  inertia, which overshoots by construction."*
- `ChaseController.cs:1980-1981` — and names the fix: *"the probe-based replacement would be a measured
  stick→roll-rate lag, i.e. **a roll twin of `_pitchEff`**."*
- `Cfg.cs:290` `FineBankDeadzone = 2.5f`, `Cfg.cs:332` `BankSlewRate = 60f`, `Cfg.cs:233`
  `RollRateSmoothing` — all fixed.

**Honest caveats.** (a) `sc_maxRollAngularVel` does **not** discriminate: EW1 has the identical 3.0 and
0.3 pitch cap and shows 30x less amplitude. The parameters on which Darkreach is *uniquely* extreme are
mass (105.4 t, 1.83x the next), wing area (383 m², 2.55x) and stall margin (1.425x, the fleet minimum).
(b) Darkreach is 40 of 1,120 segments across two lanes; it is the corpus's thinnest lane. (c) It also
has the largest W1 speed drift (2.03x), so amplitude and V/Vcorner are confounded on this card.
(d) The attribution specifically to the B2 settle branch is **not** established — `settleOn` duty cycle
is *inversely* related to amplitude (0.334 with amp 0.588 early, 1.000 with amp 0.063 late), and R28's
Darkreach rings at `settleOn` = 0.034. The *gap* is confirmed; *which* roll term rings is not.
(e) Terminal magnitude is small: 0.081° mean |azErr| in the last 6 s, ~4x the 0.0198° quantum and 3.5x
Fighter1's 0.023°.

**Discriminating test.** Two, in order of cost:
1. **Free, offline.** Simulate `kSettle * azErr` against a first-order roll plant with the probed
   `maxRollAngularVel` and a lag τ swept 0.1–1.0 s; find the τ at which ζ drops below 0.1 at
   kSettle = 8. If Darkreach's measured roll lag lands there and the fleet's does not, the mechanism is
   confirmed without flying.
2. **Flight.** Build the roll twin of `_pitchEff` (achieved/commanded roll rate, same 0.05 attack /
   1.0 release smoothing) as a *recorder column only* — no control coupling — and re-fly
   `oblique-6-dwell` on Darkreach + EW1 + Fighter1. *Pass:* the estimator separates Darkreach from EW1
   (which the probed parameter does not) and correlates with the measured ζ. *Fail:* it reads the same
   on all three ⇒ the driver is mass/inertia, which the mod cannot probe, and the honest fix is a
   `kSettle` scheduled on measured roll lag rather than on any airframe parameter.

**Status.** OPEN, and blocked on a **code change** (the estimator) before more flying helps. The
observation is confirmed; the mechanism is not.

---

## W3 — down-step legs converge worse than up-step legs, in both leg orders

**Claim.** On the oblique diamond cards, a step whose target moves *below* the nose converges
measurably worse than the mirrored step above it. This survives order counterbalancing, which is the
one control that kills the obvious confound.

**Evidence — the counterbalanced pair is the load-bearing part.** `cards/oblique-12-fwd.json` runs DOWN
legs at `seg_index` 1–2 and UP at 3–4; `cards/oblique-12-rev.json` runs UP first. n=72 segments per tag
(R30+R31), `excluded=0`, `railed=0`:

| card | seg 1 | seg 2 | seg 3 | seg 4 |
|---|---|---|---|---|
| `-fwd` `rms` | **obDR12 4.578** | **obDL12 4.408** | obUL12 4.447 | obUR12 3.973 |
| `-rev` `rms` | obUL12 3.892 | obUR12 3.848 | **obDR12 4.872** | **obDL12 4.636** |
| `-fwd` `terminalOffDeg` | **0.709** | **0.831** | 0.400 | 0.323 |
| `-rev` `terminalOffDeg` | 0.302 | 0.283 | **0.837** | **0.881** |

Down is worse whether it runs first or last, on both metrics, at ~2.6–3.1x on `terminalOffDeg`. On the
modern 30 s card (R35+R37, `oblique-6-dwell`, n=992) the floor-free form is a **1.43x modulation**:
down/up `rmsPointingErrorDeg` spans 0.834 (Darkreach) to 1.196 (COIN), 7 of 10 airframes down-worse.
`belowSuppress` is demonstrably firing and is a **uniform input**: `bSup` 0.72–0.88 on down legs vs
0.00–0.02 on up legs for every airframe.

**ONE-LAW or bug?** **Plain directional effect. The ONE-LAW attribution was tested tonight and REFUTED**
— see "Refuted" below. The 15x per-airframe spread was a censoring artifact (EW1's up-leg denominator
sat at 1.14x the resolution floor with 24 of 32 legs nulled); on the floor-free metric the spread is
1.43x and it correlates +0.685 with the *probed* `sc_maxPitchAngularVel`, i.e. the law tracking a probed
parameter. Note also that a *smaller* pitch command on the down leg is expected physics — gravity
supplies the nose-down flight-path change for free.

**Mechanism.** `belowSuppress` (v0.85 roll-invariant below-nose suppression) plus the eAlign path.
Recorded as `bSup`/`bWt`. Transient measurements (R35+R37, tSeg<2 s, n≈15,875 per direction):
`outP` +0.078 down / −0.162 up, `|bank|` 12.42 / 15.94, `g` 0.731 / 1.270. The old "the law rolls-and-pulls
instead of pushing" story is **false at 6°** — 79–100% of down-leg transient rows carry `outP > 0`.

**Discriminating test — the cards exist and have never been flown.**
`cards/e1-below-suppress.json` sweeps `Control/BelowAlignSuppress` ABBA on `oblique-below`'s geometry
(the regime where the defect lives), and `cards/e1-below-control.json` sweeps the **same knob on the
above-horizon diamond**, where `alignFracH ≈ 0` so the knob must have *no* effect. Fly them in the same
session, same eight keys, same order. *Pass:* the on arm lowers `terminalOffDeg` and `rollYawOpposedPct`
on the below-nose legs and the control arms are indistinguishable. *Fail (either way):* no separation on
`e1-below-suppress` ⇒ the v0.85 fix does nothing where it was aimed; **any** separation on
`e1-below-control` ⇒ the suppression is reaching a geometry it was never meant to touch, and it
invalidates the other card. `cards/e1b-align-lead.json` is the third arm (`Control/AlignRateLead`),
kept separate on purpose because the lead is also a roll-damping change and the two are unattributable
if armed together. Read `blendRailPct` first — a railed segment is no signal.

**Status.** Effect CONFIRMED and robust (survives order counterbalancing, entry-speed matching at 0.95x
each airframe's own probed corner, and three independent batches). Mechanism OPEN. ONE-LAW attribution
REFUTED. Caveat: the counterbalanced evidence is mod **0.94.0**, five versions old; the modern card is
not counterbalanced and is contaminated by W1 (down legs run first, ~1.5x slower).

---

## W4 — `aoaFade`'s proportional band is empty; it is a constant on every flown airframe

**Claim.** `ChaseController.cs:1222` computes `aoaFade = Mathf.Max(4f, Mathf.Min(6f, 0.25f * lim))` and
presents it as a schedule keyed to the probed `alphaLimiter`. Across the ten airframes the corpus
actually flies, the proportional term **never selects**: every airframe is clamped by the 4° floor or
the 6° cap. The advertised probed-parameter dependence does not exist in practice.

**Evidence.** `sc_alphaLimiter` takes exactly five values in the flown roster:

| `alphaLimiter` | airframes | `0.25*lim` | `aoaFade` | which term binds |
|---|---|---|---|---|
| 10 | Darkreach, trainer, COIN, EW1 | 2.50 | **4.00** | floor |
| 14 | CAS1 | 3.50 | **4.00** | floor |
| 15 | VTOLTrainer1, FastBomber1 | 3.75 | **4.00** | floor |
| 25 | SmallFighter1 | 6.25 | **6.00** | cap |
| 27 | Fighter1, Multirole1 | 6.75 | **6.00** | cap |

**7 of 10 at the 4° floor, 3 of 10 at the 6° cap, 0 of 10 in the proportional band** (which would need
16 ≤ lim ≤ 24 — a gap in the roster). The code comment at `:1219-1221` says *"only low-limit
STOL/trainers widen"*; in fact the floor binds on CAS1, VTOLTrainer1 and FastBomber1 too. The sibling
`aoaMargin = Mathf.Min(4f, 0.15f * lim)` (`:1216`) is genuinely proportional on 8 of 10 and caps only on
the two 27° airframes.

**ONE-LAW or bug?** **ONE-LAW, latent.** Both are **absolute constants in degrees** compared against a
quantity whose natural scale is the probed limiter. The three lowest-ceiling airframes get a fade band
that is 47% of their entire 8.5° usable ceiling; Fighter1 gets 26% of its 23°. That is a per-airframe
difference in gate softness produced by a constant, not by a probe. (Contrast `schedFloor = 0.3f` at
`:1255`, which an adversarial pass **cleared**: it is the dimensionless endpoint of a `Lerp` terminated
by `Clamp01` at `aoaUtil = 1.0`, i.e. at *this* airframe's probed ceiling — that one is compliant.)

**Effect size: UNMEASURED — but the reason given here was wrong.**
~~No card in the corpus drives the fleet near its alpha ceiling. On R35's `alpha-steps` — the only
alpha card ever flown — `aoaAboveCeilingPct = 0.0` on every airframe on both halves, peaks 5.7–16.6°
against ceilings 8.5–23°.~~

> **CORRECTED 2026-08-02.** R35's `alpha-steps` put **7 of 8 airframes on the AoA limiter and 2 of 8
> past the ceiling**: Darkreach `aoaAboveCeilingPct` **5.000**, `aoaPeakOverCeiling` **1.029**;
> trainer **4.375** / **1.024**. The other six read 0.0% above with peaks 0.601–0.930× ceiling;
> limiter-active means run 73.6 / 70.0 / 54.3 / 41.1 / 27.2 / 10.9 / 6.0 / 0.0%. So a card in the
> corpus *does* reach the ceiling — the claim "no card drives the fleet near its alpha ceiling" is
> false as stated. What remains true is the **W4 effect size itself**: nothing isolates `aoaFade`'s
> fade band, because reaching the ceiling is not the same as measuring the fade's shape. The
> weakness stays OPEN and the discriminating test below is unchanged in intent — but read
> `debugtests/R39-E-alpha.md` §3–§4 first: `alpha-sweep` has since been **retired** (its azimuth
> demand loads the wing only through bank, clamped at 72° ⇒ n = 3.24) and replaced by
> `alpha-pullup` (`cards/ALPHA-CARD-REDESIGN.md`).
> Reproduce: `index-captures.py --query` over R35 `alpha-steps`, `tag <> 'arm'`, `GROUP BY airframe`.

`commandIntoCeilingPct` is nonzero only on the
low-ceiling airframes (trainer 22.3%, Darkreach 9.9%, EW1 9.7%) and zero on the high-ceiling ones,
which is the schedule keying off the probe — but that is the *ceiling*, not the fade.

**Discriminating test.** ~~`cards/alpha-sweep.json` exists and has **zero captures, ever**. Fly it~~
— **SUPERSEDED 2026-08-02: `alpha-sweep` was flown (R39, 61 captures, v0.98.1) and is RETIRED.** All
60 scored segments RAILED on bank clamp / turn-rate cap / blend rail, never on AoA, and
`aoaAboveCeilingPct` was 0.0 on 60 of 60 — an azimuth demand cannot load the wing past
n = 1/cos 72° = 3.24. **Fly `alpha-pullup` instead** (`cards/ALPHA-CARD-REDESIGN.md`; it pulls in the
vertical plane, `az` identically 0.0). The test design below is unchanged and still the right one —
only the card carrying it has changed. Fly it on the
extremes of the limiter range — trainer / Darkreach / EW1 (lim 10, fade 4 = 47% of ceiling) against
Fighter1 / Multirole1 (lim 27, fade 6 = 26%) — and read `aoaGU`/`aoaGD` duty cycle, `aoaLimiterActivePct`
and `wobbleEpisodesAoa` as a function of AoA/ceiling. *Pass (no defect):* gate duty cycle collapses onto
one curve when plotted against AoA/ceiling. *Fail:* the low-limit group's gate is measurably harder
(more relay-like, higher `wobbleEpisodesAoa`) at the same normalised AoA ⇒ the fade must become
`k * aoaCeil` with no absolute floor, and the v0.61 relay the floor was added to prevent has to be
re-solved another way.

**Status.** OPEN, code fact CONFIRMED by inspection, effect UNMEASURED. Cheap: one unflown card.

---

## W5 — the SLACK detector has been structurally dead since R28

> **STATUS CHANGED 2026-08-02 (v0.99.1): SLACK and `authorityUsedFrac`/`authBank`/`authAoa`/`authStick`
> were DELETED from `debugtests/scorecard.py`, not repaired.** Read this section as the *diagnosis
> that justified the deletion*, not as a live remedy — the "add `oblique_step` to `SLACK_TYPES`"
> proposal below is **WONTFIX** and must not be actioned. The reason it was deleted rather than
> re-gated is stronger than the deadness described here: `authorityUsedFrac` was
> `mean|bank| / maxBank`, which is not a fraction of *authority* at all — it exceeded 1.0 in
> practice (0.977–1.084 measured on R39 `alpha_hold`), so a "fraction used" that reads > 1 was
> never measuring what its name claimed. Every number below is still an accurate description of the
> old detector; the **gap** it identified — no detector for "the law is leaving authority unused" —
> is still open and now has nothing covering it. See `debugtests/R40-metric-repair.md` and
> `debugtests/R39-D-sustained-ab.md`. Verified deleted: `grep -n "authorityUsedFrac" debugtests/scorecard.py`
> returns only tombstone comments plus the `gone = {...}` assertion at `:2136`.

**Claim.** `rail_warning`'s SLACK branch — the one detector in the whole pipeline that looks for the law
*under-using* an airframe — is gated to segment types the harness stopped flying ten batches ago. The
metric is still computed and stored; only the flag is suppressed.

**Evidence.** SLACK fired **8 times in 9,137 segments corpus-wide, all 8 in R27, all 8 on
`sustained_turn`.** Segment-type census: `sustained_turn` n=241, last flown 2026-07-30 19:01:58 (R27);
`alpha_hold` **n=0 — implemented in the scorer, no card ever written**. All 5,670 scorable segments of
R28–R37 carry zero SLACK (R28 0/1536, R29 0/1764, R30 0/192, R31 0/384, R32 0/250, R33 0/304, R35 0/616,
R36 0/128, R37 0/496). The type gate is the **sole** binding condition: `authorityUsedFrac` and all four
`AUTH_TERMS` are non-null on 5,670/5,670, so the `used is None` and `len(terms) < AUTH_MIN_TERMS` exits
never fire.

**ONE-LAW or bug?** Instrument gap, indirect but severe: ONE-LAW is enforced by evidence, and this is
the detector for the under-command half of it.

**Mechanism.** `debugtests/scorecard.py:844` `SLACK_TYPES = ("sustained_turn", "alpha_hold")`, enforced
at `scorecard.py:900-901` (`seg.get("type") not in SLACK_TYPES → return None`). The justification at
`:836-843` is **sound** — a 0.5° step legitimately needs 0.5° of authority, so a settle-dominated mean
would invert the detector — and the comment names the fix it did not build: *"A step's authority
question is about the TRANSIENT anyway... that would need a peak/rise-window statistic, not this one."*
That statistic was never written, so the gate is a permanent off-switch rather than a deferral.

**Do NOT simply ungate it.** 5,378 of 5,670 (94.8%) of those segments sit under `SLACK_FRAC`, so
ungating produces a detector firing on 95% of the corpus — the exact inversion the gate exists to
prevent, now confirmed at corpus scale. Its worth is its rate on the type it was built for: **8 of 104
non-railed R27 sustained turns = 7.7%**, a discriminating rate.

**Discriminating test.** Offline, no flying: implement the rise-window statistic the comment specifies —
**peak**, not mean, authority over `tSeg ≤ riseTime90`: `max(|outP|,|outR|,|outY|)`, `|aoa|/ceil`,
`|bank|/maxBank` — add `oblique_step` to `SLACK_TYPES` keyed to that peak metric, and re-index.
*Prediction:* it fires on the R28 Darkreach divergence and on the down-hemisphere 12° legs, and stays
quiet on the 992 R35/R37 dwell legs that actually converge. Cheaper alternative: restore one
sustained-turn card to the rotation so the detector has a live type again (~20 min).
**Do not add `alpha_step` without a ceiling-normalised threshold** — R35's 120 alpha segments would have
produced 86 flags whose ranking is entirely the probed alpha ceiling in the `authAoa` denominator
(ceil 21–23 → used 0.27–0.36; ceil 8.5–12.75 → used 0.46–0.70), i.e. probed-parameter false positives.

**Status.** OPEN, CONFIRMED, narrowed. Survived adversarial review with the remedy tightened.

---

## W6 — `terminalOffDeg` and the g family are lane-distance artifacts, still un-caveated in the scorer

**Claim.** `scorecard.py` carries the distance caveat on `gJitterG` alone. R37 measures the same
contamination on seven more columns, on a purpose-shaped control.

**Evidence.** R37 is the first batch with six airframes flying a NEAR and a FAR lane **~60 km apart in
the same batch, same law, same card** (Fighter1 8.0/68.0 km, Multirole1 14.0/74.0, SmallFighter1
20.0/80.0, trainer 26.0/86.0, VTOLTrainer1 32.0/92.0, CAS1 38.0/98.0). Far/near ratio, median over the
six matched pairs, n=496:

```
CONTAMINATED (6/6 same direction)          IMMUNE
  terminalOffDeg      5.26x                  fixedWindowOffDeg    1.01x  (3/6 up = coin flip)
  gJitterG            3.50x                  rmsPointingErrorDeg  1.01x
  overshootAzDeg      3.61x                  aoaPeakDeg           0.99x
  rollCmdMedian       3.40x                  stickFlipRateR       0.94x
  gSustained          2.19x
  gPeak               1.95x
  yawCmdMedian        1.47x
  authStick           1.13x
  settleTime95 rate   0.32x  (6/6 DOWN)
```

`aoaPeakDeg` flat at 0.99x is the control that makes this conclusive: the aircraft is at the same angle
of attack near and far, so nothing aerodynamic moved. Lane-level (16 cells) Spearman vs `origDist`:
`gJitterG` **+0.909**, `gSustained` +0.903, `terminalOffDeg` **+0.622**, `settleTime95` rate −0.485,
`rmsPointingErrorDeg` +0.103, `fixedWindowOffDeg` **−0.032**. Log-log slope of `gJitterG` vs `origDist`
= **+1.21** (r = 0.923).

**ONE-LAW or bug?** Instrument — with one uncomfortable wrinkle. `rollCmdMedian` moving **3.40x** means
the law really *is* commanding more roll out there, in response to a float-grained aim geometry. That
is a real command on a corrupted input, not a pure measurement artifact. It does not change the score
(`fixedWindowOffDeg` and `rms` are flat) but it is a reason to keep cards inside ~50 km.

**Mechanism.** `origDist` = `ac.transform.position.magnitude` (`Recording.cs:530`) — float32 grain in
world coordinates. `scorecard.py:656-657` gives `gPeak`/`gSustained` no distance note while the
`gJitterG` docstring immediately below does; `scorecard.py:1576-1577` asserts `gJitterG` is
"deliberately orthogonal to `gPeak`/`gSustained`", which is true by construction in the selftest and
false in the measured corpus at low g (r = +0.89, slope 1.0).

**Discriminating test.** None needed — R37 §2 *is* the test, and it is already flown. The action is
~20 lines of docstring plus a rule in `CAPTURES-DB.md`: those nine columns may never be compared across
lanes at different `origDist`, and the two ranking metrics may.

**Status.** CONFIRMED, closed as measurement, **open as documentation**.

---

## W7 — FastBomber1 runs 4–10x hotter in steady-state pitch than the fleet, in every batch

**Claim.** In the steady window (`tSeg ≥ 12 s`) of every card with materialized rows, FastBomber1 is
**rank 1 of 10 on `sd(outP)`**, across six mod versions and two game builds. No probed parameter
explains it.

**Evidence.** Pooled `sd(outP)`, steady window, `oblique-6-dwell`: FastBomber1 0.01906 (R35, n=9219
rows) / 0.03904 (R36) / 0.00377 (R37) against 0.0014–0.0050 for every other airframe in all three.
Corpus-wide it shows elevated steady pitch activity in R28 (0.0157–0.0774, six cards), R29
(0.0176–0.0583), R31 (0.0549/0.0589), R33 (0.0168), R35, R36 and R37. Its **rank** is invariant even in
R37, where its absolute level collapses 5x.

**ONE-LAW or bug?** Open, and the obvious probed explanations are dead. `sc_maxPitchAngularVel = 0.5`
is *not* the fleet's outlier — Darkreach and EW1 are at 0.3 and are among the quietest. `_pitchEff`
reads 0.560–0.748 on FastBomber1, never near `effFloor = 0.3`, and is *lower* in the quiet batch, so the
estimator is not absorbing a plant change. `dmgFrac = 0` does **not** exclude damage (see R37 §5).

**Mechanism.** Unknown. Ruled out tonight: `AeroPart.Repair` (R35 and R37 have the same Repair state and
opposite outcomes, and the effect is at full strength in replicate 1, which never calls the placement
path); wing geometry (replicate 1 is bit-identical across batches and still shows the full 5x);
frame hitching (R37 max in-capture `frameMs` 156 ms, 1 row of 250,074 over 25 ms). Uncontrolled and
co-varying: world radius (R35 flew ~89.6 km from origin, R37 ~59.7 km) — which is now W6's territory,
and 9 of 10 airframes got quieter R35 → R37 (ratios 1.02–2.71), so the *sign* is universal and only the
magnitude (5.06x) is FastBomber1's.

**Discriminating test.** It rides free on W1's throttle cards: FastBomber1 has the second-highest
thrust-to-weight-driven speed drift after Darkreach (1.02 → 1.45 V/Vcorner), so if the pitch hunt is a
q-schedule artifact it will move with pinned throttle. *Pass:* `sd(outP)` collapses onto the fleet at
throttle 0.40 ⇒ it was the drift. *Fail:* it stays rank 1 at all three throttles ⇒ genuine airframe
dependence, and worth its own card.

**Status.** OPEN, observation CONFIRMED across six mod versions, mechanism unknown, effect size
batch-unstable (5x). Do not act on it before W1.

---

## W8 — the `_pitchEff` noise gate is an absolute rad/s constant

**Claim.** `ChaseController.cs:1192` gates the estimator on `Mathf.Abs(cmd) > 0.05f` where `cmd` is the
FBW's commanded pitch rate in **rad/s** — an absolute constant compared against a quantity whose natural
scale is the probed `maxPitchAngularVel`, which spans 0.3–1.0 across the fleet (3.3x). In principle the
estimator's duty cycle is therefore airframe-dependent.

**Evidence that it is inert.** Measurable-frame fraction is **3.28–4.94% across all seven healthy
airframes with no relation to the cap** (EW1, cap 0.30 → 4.94%; Fighter1, cap 0.90 → 3.28%). The gated
fraction is 85–99% on every airframe in every batch, so it is not a discriminator anywhere in the corpus.

**ONE-LAW or bug?** A ONE-LAW **smell** with no measured effect. It belongs as one line in
`GENERALITY-REVIEW.md` as an unexercised constant, not as a finding. The natural form would be
`> 0.05f * maxPitchAngularVel / 0.5f` or simply a fraction of the probed cap. Do not change it without a
capture that shows the duty cycle actually splitting — a gain change with no measured motivation is how
per-plane tuning gets in.

**Status.** OPEN as documentation only.

---

# REFUTED / DO NOT RE-PROPOSE

Each of these was proposed with evidence, attacked by a dedicated agent, and did not survive. **The
arithmetic in most of them was correct; the attribution was not.** They are recorded here so they are
not re-derived next month.

### R1. "Airframe identity, not the law, sets the pointing score" (4.4–7.6x between/within spread)
**The arithmetic reproduces exactly** (obDL6 SD ratio 7.62x, η² 0.98; all ten airframe means to 4 dp).
Refuted because: (a) `fixedWindowOffDeg` is a **mid-transient snapshot by construction** —
`scorecard.py:104` anchors the window at 7–8 s from segment *start* and the file's own comment block
(`:92-103`) states nothing in R35 settled before 9.0 s, so it ranks settling **speed**, not accuracy;
(b) the effect collapses monotonically with measurement time on the same 496 legs — η² 0.90–0.98 at
7–8 s, 0.71–0.87 on whole-leg RMS, **0.14–0.48 terminal**, where between-airframe variance is *smaller*
than within; (c) at t = 25–29 s, 337 of 496 legs (68%) sit below `OFF_FLOOR_DEG = 0.0396` — nine of ten
airframes have arrived on boresight; (d) the ranking reorders, Spearman(fw@7–8 s, off@25–29 s) = +0.588,
and the "best" airframe COIN is 7th at steady state; (e) a proper two-way decomposition gives airframe
0.607 / geometry 0.090 / **interaction 0.255** / replicate 0.048, so 39% of the claimed airframe variance
is geometry and interaction; (f) **one airframe reproduces the whole claimed range** — EW1 spans 6.4x
across the four mirrored legs of the same card, identical to the claimed between-airframe low end.
**What survives:** convergence *rate* genuinely varies ~3–7x, and exactly one airframe fails to reach
the floor by 30 s — Darkreach. That is W2.

### R2. "No probed parameter explains the ranking; the correlates are mass and thrust, which the rule forbids"
Refuted on five counts. (a) `sc_maxThrustN` is **NULL for CAS1 and COIN** — ranked #3 and #1 best; honest
k=8 gives rho +0.595, permutation p = 0.13 (not significant), and the published +0.758 is only
recoverable by imputing zero thrust. Thrust-to-weight, the physically meaningful form, is **rho −0.095**.
(b) The whole scan sits at its own multiple-comparison noise floor: over 28 complete varying `sc_*`
params at k=10, Monte-Carlo median best |rho| for **pure noise** is 0.709 — numerically identical to the
headline mass figure — and `sc_modVersion`, a nuisance variable, scores +0.522. (c) The claim's own table
puts the *probed* `dFactorFast` (+0.743) above mass. (d) `omegaMax = g*sqrt(n²−1)/V`, built from probed
`sc_gLimitPositive` and live airspeed — **exactly the sanctioned form** — gives rho **−0.782** vs
`fixedWindowOffDeg` and **−0.842** vs `rms`, beating every correlate listed. (e) At k=10 mass, probed
pitch authority and derived turn-rate ceiling are one collinear bundle (|r| 0.70–0.82).

### R3. "The fixed-wing pitch and yaw channels have no plant-authority normalisation"
All four measured plant-gain numbers reproduce (roll 4.24x, pitch 2.37x, yaw 1.72x, off 7.8x). Refuted
because: the stated mechanism has **no explanatory power and the wrong sign** (Spearman(measured roll
plant gain, off) = +0.248, p = 0.49; partial r controlling for speed = **−0.478**, i.e. more unnormalised
gain goes with *less* error); "the law's own gain is flat" was established on the yaw channel only —
`tgtP` at `ChaseController.cs:1928` carries `qSched` (0.772–1.000, from probed `_fbwCorner` x live q) and
`_pitchEff` (0.498–0.833), and `:1927` is literally headed "PLANT-AUTHORITY SCALING"; the bank target at
`:1824-1827` is `atan(omega*Vb/g)` with `omega` clamped by probed authority; and the outcome metric is
~97% azimuth, so it cannot evidence a *pitch* normalisation defect at all.

### R4. "The ranking is not explained by live speed — the within-airframe slope has the opposite sign"
Reproduces to three decimals and is still refuted. The sign reversal exists on **one metric only**
(`fixedWindowOffDeg`, n=482); on `rmsPointingErrorDeg` the within and between slopes are nearly identical
(+0.00038 vs +0.00034), the signature of a *common* driver. There is **no within-airframe speed
manipulation at all** — per-airframe SD of `entry_v_to` is 0.0 for all ten, so every "within" difference
is realized speed, an outcome. The pooled within slope is an average over **opposite-signed populations**
(five airframes at r ≥ +0.30, five at r ≤ −0.38). And regressing out `log(sc_massKg)` alone drives the
residual between/within ratio to **1.04** (0.91 with speed), i.e. no residual airframe spread survives.

### R5. "The modern envelope map is one point: 94% of scored segments are a 0.5–12° oblique diamond"
The count is right (6,318/6,720 = 94.0%; duration-weighted 89.8%). The interpretation is not.
`cards/sweep-step.json` swept the commanded aim point 0 → 175.5° on 4 airframes at modern schema, 64
segments, 0 railed — the claim read `segments.demandDeg` (a step *relative to a moving marker*) as
absolute azimuth. `alpha-steps` is a ±45° elevation card, 3.75x the claimed 12° bound, and the claim
lists it in its own evidence. **The real coverage hole is orthogonal to demand magnitude** — see "What
we still cannot see".

### R6. "The law's worst regime is a large below-nose reorientation (13–32% of demand left standing)"
### R7. "At a 45° nose-down demand the outcome spans 0.22–31.9% and no probed parameter orders it"
Both refuted on the same root error: **the demand is not 45°**. Measured `demandDeg` on `alphaPush` is
**50.1–62.2°**, spanning 12° across airframes, because the preceding `arm` never returns to datum. The
percentages divide by a denominator that is not the commanded step. Further: it is not a "residual" —
every airframe is still monotonically converging at segment end (the two "worst" decay *fastest*), and
`settleTime95` is non-NULL on only 3 of 59 push segments, so `terminalOffDeg` is reading a mid-transient.
On the validated metric the spread is 15.6x, not 142x. The **mirrored control destroys the attribution**:
on `alphaPull`, where `demandDeg` is 45.00 for every airframe, 6 of 8 converge below 0.3° including the
flagship "failures" (Multirole1 0.014, SmallFighter1 0.010). And live recorder columns *do* order it —
`bigTurn` at segment end rho **+0.994**, terminal `|bank|` +0.881 — a bank-to-turn latch that has not
released, which the ONE-LAW rule explicitly permits gating on. Also: `sc_alphaLimiter` is 27.0 for
Fighter1/Multirole1, not 23.0 (23.0 is the *derived* `aoaCeilDeg`), and `aoaAboveCeilingPct = 0.0` on
every airframe on both halves — a nose-down unload is not an alpha-ceiling regime.

### R8. "The `arm` window fails on 8 of 8 airframes on `alpha-steps`"
Reproduces exactly and is a **pure mixture artifact**. `alpha-steps` has *two* arm windows per capture.
Split: arm1 (from spawn) ends at median **0.000–0.14°** on 8 of 8 — the best arm window in the entire
corpus, beating all 452 oblique arms offered as the healthy contrast. arm2 (after a +45° pull) ends at
5.19–18.09°. Histogram of the 118 pooled values: 59 in [0, 0.5), **zero in [0.5, 4.0)**, 59 in [4, 25).
Because the mixture is exactly 50/50, every quoted per-airframe median is mechanically arm2/2 and the
quoted P90 *is* the arm2 median. The affected population is 59 windows on one card = **0.65% of
segments**, not 22%.

### R9. "The down-step penalty carries a 15x airframe spread no probed parameter explains" (the ONE-LAW half of W3)
The down-step effect is real (W3). The **15x spread is a censoring artifact**: EW1's up-leg mean 0.0450
is 1.14x `OFF_FLOOR_DEG` with **24 of 32 legs nulled outright**; COIN sits at 1.50x floor, trainer at
2.00x. Excluding EW1 the spread is 6.2x; on the floor-free `rmsPointingErrorDeg` it is **1.43x**, and the
two anchor airframes cross to the other side (EW1 5.111 → 0.922, i.e. down leg *better*). The residual
correlates **+0.685 with the probed `sc_maxPitchAngularVel`** — the two airframes whose down leg beats
their up leg are exactly the two with the lowest probed pitch rate. It also decorrelates by 30 s
(Spearman(fw ratio @7–8 s, terminal ratio @30 s) = −0.164) and flips by card (Fighter1 0.898 on the 6°
dwell, 2.935/3.109 on `oblique-12-fwd/rev`).

### R10. "The law rolls-and-pulls instead of pushing on down steps"
**Refuted at 6°.** 79.2–100% of down-leg transient rows carry `outP > 0` on all ten airframes, and the
per-second profile is positive throughout. The ~2.1x pitch-magnitude asymmetry is real but expected:
gravity supplies the nose-down flight-path change for free.

### R11. "`authorityUsedFrac` is trim-AoA over a probed constant — it ranks airframes, not laws"
The 7.29x spread reproduces, but the decomposition inverts the claim: `authAoa` vs the *numerator*
(mean |aoa|) is rho +0.794 against +0.743 for `1/ceil`, and the numerator spans 3.71x, not "essentially
nothing". The ONE-LAW hazard is **direction-inverted** — SLACK fires when `used < SLACK_FRAC`, so a 27°
fighter at 0.066 fires *more* readily than a 10° bomber at 0.315. And 984/992 segments are already below
0.5, so no differential exists at this operating point. **What survives:** `authorityUsedFrac` is
law-blind on step cards — `gPeak` moved 14–25% across mod versions while `authAoa` moved under 0.2% —
which is `scorecard.py:842-843`'s own documented limitation, i.e. W5.

### R12. "53 Darkreach legs sat 27° off with 77% of the g envelope untouched, and the pipeline calls them railed"
**Selection on the dependent variable**: the filter conditions on `bankClampActivePct < 90 AND
turnRateCapActivePct < 90`, then reports that every airframe limit was inactive. It silently drops 29 of
Darkreach's 82 railed R28 segments, which sit **at or over** the probed 4.0 g limit (gFrac 1.14–1.35).
Those 53 segments have `authorityUsedFrac = 0.969` against `SLACK_FRAC = 0.5` — maximally *non*-slack.
And `turnRateDemandRatio`, built purely from probed `sc_gLimitPositive` + `sc_fbwCornerSpeed` and live V,
separates Darkreach (0.615) from the rest of the R28 fleet (0.036–0.061) by 10–17x and is monotone in rms
across 11 bins. Mass is directly disproved: the same 105.4 t Darkreach flies the *corner-relative* cards
at 1.13–2.04° with 0 railed. R28 asked an absolute-geometry card at 2.5x corner speed of the one airframe
with a 4.0 g limit; the `-c` cards from R29 fixed it.

### R13. "The v0.65 reversal gate fires on 40% of frames of the least-reversed plant"
The headline number does not reproduce (65.38%, not 40.1%). More decisively, **the evidence is
sign-inverted**: the proxy `tgtPRaw × pitchRate < 0` mixes two opposite sign conventions (`pitchRate`
+ = nose up at `ChaseController.cs:867`; `tgtPRaw` nose-up = negative), so it measures *following*, not
anti-phase. Corrected, Darkreach is the **worst**-tracking plant in the fleet (23.8% following) and EW1 —
nominated as most reversed — is the best (97.8%). The estimator divides `fbwPR`/`fbwTgtPR`, not
`tgtPRaw`. And the ungated branch is `Mathf.Max(_pitchEff, PEffRevThresh)`, which can only push pEff
**up**, so the claimed positive feedback has no path. Stale by five mod versions: R35/R36/R37 show 0.0%
anti-phase with median pEff 0.64–0.86.

### R14. "`PEffRevThresh` has two conflated populations, and reachability is undecidable"
It is **one** mechanism: `Max(_pitchEff, PEffRevThresh)` can never pull a healthy estimate down, so
parking at 0.150 *requires* a prior genuine sub-threshold measurement (verified: 38 of 39 captures have
a below-threshold row at a strictly earlier index). The correct scope is **replicate 1**, not Multirole1:
R35+R37 Multirole1 rep-1 = 5,968 parked rows, placed replicates 2–8 = **zero parked, zero below**.
Reachability is decidable in ten lines: simulating the float32 LPF from any start parks at 0.14999956,
strictly below `0.15f`, so `>=` is False.

### R15. "`schedFloor = 0.3f` costs an airframe / is a ONE-LAW violation"
`ChaseController.cs:1255-1257`: `schedFloor` is the **endpoint of a `Mathf.Lerp` terminated by `Clamp01`**,
reached exactly at `aoaUtil = 1.0` — this airframe's *probed* ceiling. Every quantity is dimensionless
and ceiling-relative; a per-airframe `schedFloor` would be the violation. Half the quoted floor
occupancy is the **dynamic-pressure clamp**, not the alpha schedule (per-row decomposition: VTOLTrainer1
0.3632 → q-clamp 0.3584, alpha min 0.5990). The genuinely absolute constants sit two lines up and are
**W4**.

### R16. "The rig reports 10 significant effects on a null A/B (R35 vs R37)"
**R35 vs R37 is not a null A/B.** Six of sixteen lanes were physically relocated between the runs
(VTOLTrainer1 0.6 → 32.1 km, CAS1 6.0 → 38.1, trainer 6.0 → 26.1, Fighter1 37.4 → 8.3); pooled `origDist`
+10.9%, paired t = +4.75. A permutation null *within* a single run gives mean 1.46 flags of 33 (4.4%,
nominal 5%) and P(≥10) = 0.000 — the rig is correctly calibrated and correctly reporting that the two
runs flew different conditions. The one geometry-matched pair, **R36 vs R37, gives 2 flags of 36 = 5.6%
at identical power.**

### R17. "Within-run replicate scatter is not the noise floor — 86–96% of A/B variance is batch-level"
Reproduces, and the non-shrinkage is real, but the mechanism clause is wrong: the dominant source **is**
absolute lane distance, not an unidentified batch term. Both the within-SD and the batch term scale ~3x
from lanes 1–8 to lanes 9–16, and the claim regressed on Δlog(`origDist`) — a covariate orthogonal to
the driver, since the far lanes agree *between* runs. Stratum-honest floors give `rms` on lanes 1–8 as
**84% replicate-limited**, not 14%. The prescription ("redesign toward interleaved A/B") is the
expensive answer: confining cards inside ~50 km recovers most of it.

### R18. "`settleTime95`'s availability is a noise gauge, not a law gauge"
The tercile table reproduces exactly and the causal reading fails three tests. A **law change moves it
at fixed geometry**: R35 → R37, same airframe/lane/`origDist`/card, 5 of 16 lanes move settle rate past
Bonferroni (CAS1 9.4% → 50.0% while jitter went *up*; VTOLTrainer1 0% → 34.4%). **Airframes separate at
matched noise and distance**: inside jitter tercile B alone the rate spans 0–100%. And distance's partial
correlation vanishes when controlling for `terminalOffDeg` (−0.694 → −0.061). It is a joint gauge; read
it per lane (W6).

### R19. "MODE-A: one universal first-order azimuth closure whose residual sorts by airframe"
The "residual" is **an unfinished transient**, not a steady state — the last-5 s / previous-5 s ratio is
0.64–0.78 on five airframes. Refit with an asymptote and the ordering inverts (SmallFighter1 has the
*smallest* asymptote and the 3rd-largest 30 s value). τ is 2.6–10.0 s, not 6.9–17.9 (the claim used a
log-linear fit with no offset, which always overestimates τ), and τ **does** rise with V (Pearson +0.565)
against the design slope. The stated deadzone mechanism is inverted: measured `assist` is exactly 1.0000
on 8 of 10 airframes, so `azDz = FineBankDeadzone * (1 - assist)` = **0** and the servo is fully active.
**What survives is W2**, at one third the claimed size.

### R20. "MODE-B: a self-sustaining 0.35 Hz limit cycle on Darkreach"
The mode is real (W2). "Limit cycle" is not: amplitude decays 9.4x monotonically over the leg
(ζ ≈ 0.044, stable), and the frequency is **amplitude-invariant** across a 9x range, two cards and four
mod versions — the signature of a lightly damped linear mode, not a describing-function limit cycle. The
classifier is off by ~300x (`pf ≥ 0.35` fires on 125 of 480 non-Darkreach R37 segments, not 1 of 1080)
because `pf` is amplitude-blind. The B2 attribution is circular — `ChaseController.cs:1847` *assigns*
`tBankE = kSettle*azErr` whenever the branch runs — and `settleOn` duty cycle is *inversely* related to
amplitude.

### R21. "The ranking metric samples the transient at 7–8 s and the 30 s order is different"
The disagreement is real (Spearman +0.515) and the conclusion is backwards. **Eight of the ten "30 s"
numbers are below `OFF_FLOOR_DEG`** (0.76–1.92 quanta; 44–65% of tail samples read exactly zero;
bootstrap 95% CIs for ranks 1–6 are fully interchangeable). The tail also **fails the exact validation
`fixedWindowOffDeg` passes**: log-log r = +0.633 vs `origDist` against −0.001 (W6). And the "hiding"
mechanism requires an airframe that scores well at 7–8 s and badly at 30 s; **no such airframe exists** —
the only two whose tail clears the floor are FastBomber1 and Darkreach, already ranked 7th and 10th.

### R22. "MODE-C: a FastBomber1 elevation hunt that appeared and vanished across a mod version"
The observation survives as **W7**; every causal and temporal element is wrong. The blamed
`AeroPart.Repair` was **absent in R35, present in R36, absent in R37** while the hunt was yes/yes/no —
R35 and R37 have the same Repair state and opposite outcomes. It is at full strength in replicate 1,
which never calls the placement path. `_pitchEff` (the measured plant response) is unchanged and never
approaches `effFloor`. "Appeared and vanished" is false in the "appeared" direction: FastBomber1 is hot
in every batch back to R28 across six mod versions and two game builds; **R37 is the outlier**.
`dmgFrac = 0` does not exclude damage.

### R23. "`gPeak`/`gSustained` are float artifacts that would flip a real g verdict"
The measurement is right (W6) and the impact claim is not. Inflation as jitter/`gSustained` is 1.1% on
`reversal` (5.98 g), 1.3% on `astern_wrap`, 3.4–3.5% on `az_step`/`sustained_turn` — versus 39.3% on the
0.40 g dwell legs. The literal R21-shaped verdict in the corpus (R19 `az_step`, `gSustained` 6.10 against
a probed 9.0 limit) is inflated **1.42%** and flips nothing. The artifact is also **additive**, not
multiplicative: subtracting `gJitterG` at a coefficient of 1.0 collapses the distance correlation from
+0.767 to −0.004.

---

# WHAT WE STILL CANNOT SEE

Questions the current corpus and instrumentation **cannot answer at all**, in rough order of how much
they matter. These are not open findings; they are blind spots.

**1. Rotorcraft and STOL — half the ONE-LAW rule has no data.** The standing rule names four cases: a
light jet at high q, a loaded jet mushing near its alpha limit above corner, **a low-limit STOL trainer**,
and **a hovering helo**. The first two are covered. For the other two: `rotor-hover`, `rotor-bob`,
`stol-steps` and `stol-sweep` have **zero captures each, ever**. `AttackHelo1` has **1 capture in 2,181**,
hand-flown at R11 on mod **0.71.0** — twenty-six mod versions ago. The v0.58 helo probe, `ResolveHelo`,
the tilt/nozzle archetype handling and the entire hover regime have never been measured by the harness.
Nothing in this document, and nothing in any FINDINGS file, constrains rotorcraft behaviour.
*Blocker:* `rotor-hover` is `startSpeed: 0` and ungated — no placement, no per-replicate reset, so it
needs a human to establish the hover and fly one replicate. `stol-steps` needs a low-limit STOL trainer
key that may not exist in the current roster (`airframe: ""`).

**2. Whether any control-law change helps, because there has been no A/B since R31.** R33, R35, R36 and
R37 all have `arm` and `arm_knob` NULL. Every "improvement" since v0.94 is an uncontrolled before/after
across batches that also moved lane geometry, mod version and sometimes game build. The five ABBA
attribution cards (`e1-below-suppress`, `e1-below-control`, `e1b-align-lead`, `e2-rel-turn-lead`,
`e3-marker-ff`) are written, per-aircraft-armed since v0.94, cost the same wall clock as one lane, and
have **zero captures each**.

**3. Steady-state pointing accuracy below ~0.04°.** `off` is written at a 0.0198° quantum and
`OFF_FLOOR_DEG` is two quanta. In R37, **74% of legs (367/496)** sit under it on `terminalOffDeg`, 44.6%
of settle-window `azErr` ticks read exactly 0.0000, and `elevErr` is written at 2 dp with 27–52% of
late-window rows reading exactly 0.00. Nine of ten airframes arrive on boresight and become
indistinguishable. **Any claim about terminal accuracy differences is unfalsifiable with the current
recorder precision.** Fixing this is a `Recording.cs` format change (more decimals on `off`/`azErr`/
`elevErr`/`outP`), not more flying.

**4. Whether the `_laneBase` fix works.** R36 and R37 both flew with a stationary camera, so
`datumX/Y/Z` never moved, and `test-lane-frame.py` states outright that fixed and broken layouts are
identical when the datum is parked. Two consecutive no-regressions, zero confirmations. The
discriminating run is five minutes: fly the camera past the 1024 m threshold mid-stagger and score the
datum column first.

**5. Transient authority.** `authorityUsedFrac` is a mean over a settle-dominated window
(`scorecard.py:842-843`), and the peak/rise-window statistic the file names has never been written. So
"did the law under-command during the transient" is a question the pipeline cannot ask on any step card
— which is 94% of the modern corpus (W5).

**6. Anything at large azimuth on the modern law.** `sweep-step`, `sweep-slow`, `sweep-creep` and
`turn360base` are the only cards that drive the marker far outside the cone (0 → 175.5°) and they were
last flown at **R27, mod 0.90.1**, on four airframes. Nine mod versions of control-law change have never
seen a large sustained azimuth demand.

**7. Whether damage is present.** `dmgFrac` reads 0 on a capture the harness aborted for
`detached ratio 0.114` (R37 rec 74, all five rows, plus `sc_detachedRatioAtStart = 0`), because
`Aircraft.PartChecker` sweeps one part per fixed step. Every "damage excluded, `dmgFrac = 0`" statement
in the corpus — including several used as evidence tonight — has excluded nothing. There is currently
**no reliable damage flag** other than the abort line.

**8. Mass and inertia.** `ChaseController` reads neither, and cannot: the game exposes no inertia tensor
through the seams the mod patches. Darkreach is 105.4 t and 383 m² against a fleet of 4.9–57.6 t and
49–150 m², and the roll path has no way to know (W2). If the answer to W2 turns out to be "schedule on
inertia", the honest form is a **measured** roll lag, not a probe — and that estimator does not exist yet.

**9. Beyond ~50 km from the world origin, nothing in the g family or the terminal metrics is a
measurement.** W6. The corpus's far lanes (56–98 km) contribute half of R37 and most of R35, and their
`gJitterG`, `gPeak`, `gSustained`, `terminalOffDeg`, `overshootAzDeg` and `rollCmdMedian` are float
grain. `fixedWindowOffDeg`, `rmsPointingErrorDeg` and `aoaPeakDeg` survive out there; nothing else
tested does.
