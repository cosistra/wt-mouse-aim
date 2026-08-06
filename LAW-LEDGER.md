# Law ledger — what we actually know, 2026-07-31

The backlog has grown faster than the conclusions. This file separates **what has been measured and
reproduced** from **what we merely believe**, with a citation on every line. It is a *state of
knowledge* document, not a plan: [`ROADMAP.md`](ROADMAP.md) says what to do next,
[`LAW-CHARACTERIZATION.md`](LAW-CHARACTERIZATION.md) §7 is the numbered backlog, and this says what
those two are entitled to rest on.

**Corpus** — ~~1 681 captures, 7 462 segments, 999 942 recorder rows, 27 batches (R1…R33), 11
airframes, 24 cards flown of 31 shipped~~ → **updated 2026-08-02: 2 576 captures, 11 015 segments,
2 117 598 recorder rows, 31 batches (R1…R40), 13 airframes, 38 cards flown of 36 shipped + 3
built-ins.** → **updated 2026-08-02 after R41+R42: 3 083 captures, 34 batches (R1…R42), 14 airframes**
(`--stats`). Every SQL figure below is reproducible against `debugtests/captures.db`; the query is
given where it is not obvious. **Re-derive with `--stats` rather than trusting any count in this file
— they were written at R33 and the corpus has since grown ~53%.**

> **THREE CORPUS-WIDE INVALIDATIONS. Apply them before reading any line below.**
> 1. **The metric repair (v0.99.1).** `bankClampActivePct` read a column written by a law deleted in
>    v0.60 (27.5% of segments move > 5 pp; 17 verdicts flip to RAILED); the wobble detector was
>    measuring entry transients (318 "episodes" → **5**); and `authorityUsedFrac` / `authBank` /
>    `authAoa` / `authStick` **and the SLACK flag are DELETED**. **Every `authorityUsedFrac` claim in
>    this file is withdrawn, not re-scaled** — the quantity was `mean|bank|/maxBank`, so it never
>    measured effort. What a real replacement needs: an achieved-vs-achievable pair on ONE axis
>    (`gSustained/gLimit`, or achieved turn rate over the probed `omegaMax`) — **never a mean over a
>    limit**. `turnRateDemandRatio`, the one term with a real denominator, is unchanged.
> 2. **The multi-card ABBA confound is wider than the R31 note below says.** v0.99.1 found `ArmOf`
>    keyed the **queue** index, and a multi-card selection blocks the queue — so **every multi-card
>    A/B batch on disk carries it and must be RE-FLOWN, not re-scored.** R31 is the worst case, not
>    the only one. Single-card batches are unaffected (`_block == 1`).
> 3. **No SCORED replicate may be ANCHOR-CAPTURING** (X27). Replicate 1 of every ABBA lane is a
>    different flight condition and was always arm 0; v1.0.2 found a second such replicate on the
>    lane-respawn path and restated the rule index-free (`ArmOfRun`). Landed on the analysis side
>    (`compare-runs.py`, a backstop) and in the law (v1.0.1, v1.0.2); **pre-v1.0.1 A/B data must
>    exclude it** — the SQL filter is `entry_snapBackM <> 0`.
>
> **Do not restore a per-batch findings document.** Findings land here; see
> [`debugtests/CAPTURES-DB.md`](debugtests/CAPTURES-DB.md) → *The batch index* for what each run tag
> flew and *The doc convention* for where a new analysis goes.

**Bucket rules, applied without mercy:**

| bucket | admission test |
|---|---|
| **ESTABLISHED** | measured, **reproduced in a second batch or by a crossed design**, and the measurement was neither RAILED nor confounded. Batch, n and effect size on every line. |
| **PLAUSIBLE** | measured once, or measured but confounded, or consistent with the data and never isolated by an A/B. Includes everything resting on n=1 or on a single airframe. |
| **REFUTED / RETRACTED** | believed, then disproved. Cheapest section in the file — every line here is a mistake nobody has to make twice. |
| **OPEN** | the real question, plus the measurement that would close it. |

A RAILED segment is **no signal**. Nothing scored from one appears in ESTABLISHED.

**BATCH SUSPECT — R31 (`20260730-215053`, 96 captures) — every ARM CONTRAST in it must be re-flown,
not re-scored.** R31 is the corpus's only **multi-card armed** batch: 3 airframes × **2 cards**
(`oblique-12-fwd`, `oblique-12-rev`) × 8 replicates, sweeping `BelowAlignSuppress`. The pre-v0.99.1
ABBA index keyed the **queue** position, and a multi-card selection blocks the queue, so within *each
card* the arm is confounded with position — roughly **12 `rec` positions of systematic separation**,
with the two cards leaning in **opposite** directions. That is why nothing warned: the balance tally
ran over the whole queue and cancelled, while `compare-runs.py` groups by (airframe, **card**, arm)
and slices along exactly the confounded axis. **A position confound is not recoverable by
re-analysis** — there is no unconfounded contrast in the data to recover. Consequences, precisely:

- **Suspect, do not cite:** **I5** (it certified the concurrent A/B *using the schedule that was
  broken*), **D11** and **D12** — all three are arm-vs-arm contrasts.
- **Unaffected, still citable:** **D8**, **D9**, **D10** (within-segment observations),
  **D1**'s R31 down/up ratio (geometry, pooled across arms), **X12** (a reading of the source),
  **X15**, **X16**.
- **No other batch is affected**: every other armed batch is one card per lane, where `_block == 1`
  makes the old and new index identical.

---

## Finding index — fetch by ID, don't read the file

**This file is ~101 KB (~25k tokens). Do not read it end to end.** Every finding has a stable ID.
Read this table (cheap), then jump to the one or two rows you need — `Read` with `offset`, or
grep the ID. IDs are permanent; **line numbers drift, so grep by ID if the offset misses.**
Cite as `LAW-LEDGER.md X27`, never by line.

**ID prefixes:** `I` instrument validation · `D` down-step / demand · `G` generality &
cross-airframe · `H` helo / rotorcraft · `K` the game's own code · `L` law behaviour · `X` refuted or
retracted · `O` open questions · `S`,`N`,`P`,`Q`,`A`,`E`,`F` topic groups (the bucket column is
authoritative, not the letter).

<!-- LEDGER-INDEX:BEGIN — regenerate if you add a finding; the ID and bucket are what matter -->
| ID | bucket | claim | line |
|---|---|---|---|
| `I1` | ESTABLISHED | The rig does not drift enough to invent an effect | 220 |
| `I2` | ESTABLISHED | Captures are labelled correctly and ABBA arms alternate | 221 |
| `I3` | ESTABLISHED | A drone flies the same law as the player, bit-for-bit, and does not touch the player's aircraft | 222 |
| `I8` | ESTABLISHED | Replicate scatter is dominated by the world-origin float grain, and it is a distance law | 223 |
| `I9` | ESTABLISHED | `fixedWindowOffDeg` is the metric `terminalOffDeg` was pretending to be | 224 |
| `I10` | ESTABLISHED | The large-displacement placement kill is fixed, and R37 is the only batch that proves it | 225 |
| `I11` | ESTABLISHED | The v0.99.0 ring lane geometry works, and it is confirmed by intervention rather than correlation | 226 |
| `I12` | ESTABLISHED | The surviving placement kill is **a non-zero ANCHOR placement on a variable-geometry airframe** — not speed, not displacement size | 227 |
| `I4` | ESTABLISHED | Three instrument defects were real and are fixed | 228 |
| `I5` | ESTABLISHED | The concurrent per-aircraft A/B (v0.94) works in flight | 229 |
| `I6` | ESTABLISHED | Frame-time cost of extra lanes is superlinear | 230 |
| `I7` | ESTABLISHED | The oblique family is UNSATURATED and is the only regime whose metrics can respond to a gain change | 231 |
| `D1` | ESTABLISHED | At matched step magnitude and mirrored geometry, moving the nose DOWN leaves more terminal error than moving it up | 237 |
| `D2` | ESTABLISHED | It is DIRECTION, not position in the card | 238 |
| `D3` | ESTABLISHED | It is not energy, dynamic pressure or airspeed | 239 |
| `D4` | ESTABLISHED | It is not terminal elevation | 240 |
| `D5` | ESTABLISHED | It is magnitude-gated, essentially absent below the `FineAngle = 6` knee | 241 |
| `D6` | ESTABLISHED | It is speed-insensitive | 242 |
| `D7` | ESTABLISHED | `Fighter1` INVERTS it — up is worse — in both batches | 243 |
| `D8` | ESTABLISHED | `bSup` / `BelowAlignSuppress` is NOT the transmission path | 244 |
| `D9` | ESTABLISHED | The penalty is created downstream of the roll handover, in the fine regime | 245 |
| `D10` | ESTABLISHED | The residual is almost pure azimuth | 246 |
| `D13` | ESTABLISHED | The corpus's first above-floor steady-state pointing measurement, and the ~3.5° standing lag it predicted | 247 |
| `D11` | ESTABLISHED | Reverting to the v0.67 suppressor moves it 5 %/29 %/2 % and leaves ×2.8–3.9 standing | 248 |
| `D12` | ESTABLISHED | The v0.67 form rails the roll stick and the v0.85 form does not | 249 |
| `G1` | ESTABLISHED | The law is NOT tuned to the Ifrit | 255 |
| `G2` | ESTABLISHED | The airframe ranking is stable across two independent changes of entry condition | 256 |
| `G3` | ESTABLISHED | Entry speed does not explain the spread | 257 |
| `G4` | ESTABLISHED | The residual spread is real but bounded, and it is not at the incumbent | 258 |
| `G5` | ESTABLISHED | The R28 spread was ~40 % entry condition and ~60 % law–airframe interaction | 259 |
| `G6` | ESTABLISHED | Two-seat crew, FBW `assist=0` and distance-above-corner are all EXCLUDED as causes of the spread | 260 |
| `G7` | ESTABLISHED | `CAS1` and `COIN` — the two airframes the flat-250 grid could never fly — are ordinary members of the band | 261 |
| `G8` | ESTABLISHED | The between-airframe spread survives at matched speed | 262 |
| `G9` | ESTABLISHED | No fixed-wing regression across eleven releases, v0.96.0 → v1.0.3 — and R41→R44 is the tightest repeat in the corpus | 263 |
| `P1` | ESTABLISHED | The game has NO G governor | 269 |
| `P2` | ESTABLISHED | Over-G damages the PILOT, never the airframe | 270 |
| `P3` | ESTABLISHED | The game's alpha limiter is gated `if (num2 < 1f)` (`:65033`) and is therefore INACTIVE above corner q — which is w… | 271 |
| `P4` | ESTABLISHED | `aeroPartCount` cannot see damage | 272 |
| `K1` | ESTABLISHED | The precursor reproduces | 278 |
| `K2` | ESTABLISHED | The departure is an AoA/authority failure, not a G failure | 279 |
| `K3` | ESTABLISHED | The law's entire response to a non-responding plant is a graded stand-down, and it runs out | 280 |
| `K4` | ESTABLISHED | The placement-tick transient (#23) is BIMODAL, and the upper mode is not benign | 281 |
| `K5` | ESTABLISHED | The airframe-side half is a specific combination, and `flightAssist = 0` is not it | 282 |
| `K6` | ESTABLISHED | At a genuine 0.95× FBW corner (95 m/s) the Darkreach flies the card | 283 |
| `K7` | ESTABLISHED | The Darkreach azimuth mode is real, V-dependent and Darkreach-only — and it is NOT the settle loop's | 284 |
| `H1` | ESTABLISHED | The v0.58 rotorcraft branch executes as of v1.0.0 — and had never executed before it, for ~40 versions | 307 |
| `H2` | ESTABLISHED | With the branch live, the law is excellent on the one airframe that genuinely hovered — and R42 shows NO REGRESSION… | 308 |
| `H3` | ESTABLISHED | ~~**The tiltwing blend sign is NOT inverted.**~~ **CORRECTED BY R42 — the narrow observation survives, the verdict… | 309 |
| `H4` | ESTABLISHED | Forward speed, not the yaw step, selects the outcome on a rotorcraft | 310 |
| `H5` | ESTABLISHED | Two of three rotorcraft never hovered, and it is a harness limitation, not a law result. REPRODUCED IN R42 UNCHANGE… | 311 |
| `H6` | ESTABLISHED | `AttackHelo1`'s R41 divergence was a STALE-CONFIG artifact, not a law defect. At the shipped 60/20 it converges | 312 |
| `H7` | ESTABLISHED | A DETERMINISTIC standing residual in the BLEND BAND — **MECHANISM IDENTIFIED: the GAME's `yawWeathervane` above 40 m/s, closed-form, 5/5. R44 REPRODUCES IT ON A SECOND CARD AND BREAKS R42's `heliBlend` CONFOUND — but the discriminating 60 m/s arm is STILL UNFLOWN (2 anchor captures, 0 scored segments)** | 313 |
| `A1` | ESTABLISHED | `MarkerRateFeedForward` is worth 48–75% of the standing azimuth error, measured OFF the bank rail — and it buys tha… | 396 |
| `A2` | ESTABLISHED | The three `e1*` below-nose A/Bs are ALL NULL, and two of them are structurally incapable of being anything else | 397 |
| `N1` | ESTABLISHED | The AoA guard's switch-on point, expressed in each airframe's own ceiling, spans 0.529 → 0.739 — a 40% spread produ… | 403 |
| `N2` | ESTABLISHED | THE LAW NEVER BACKS OFF. Not once | 404 |
| `N3` | ESTABLISHED | The guard nevertheless holds: nothing crossed the ceiling on 144 of 144 segments | 405 |
| `S1` | ESTABLISHED | The `MaxBankAngle` clamp is a bystander, not the cause of the sustained-turn lag | 411 |
| `S2` | ESTABLISHED | `lateralHold` rails at 7.5° and drops the entire bank pipeline to exactly zero weight in a sustained turn | 412 |
| `S3` | ESTABLISHED | `_iPitch` is dead outside the 6° fine cone | 413 |
| `S4` | ESTABLISHED | Gate chatter is NOT the cause of the fine-aim complaint | 414 |
| `S5` | ESTABLISHED | `elDn` is a sustained roll limit cycle in the below-nose hemisphere, and the mirror step in the upper hemisphere co… | 415 |
| `S6` | ESTABLISHED | The fine-cone regression scales with step size, not with gate activity | 416 |
| `L1` | PLAUSIBLE | `aircraftGLimit` is the property that tracks the per-airframe spread | 424 |
| `L2` | PLAUSIBLE | `pEff` is the mechanism of the down-step penalty | 425 |
| `L3` | PLAUSIBLE | #45 `schedFloor = 0.3f` is a genuine ONE-LAW violation that costs an airframe | 426 |
| `L4` | PLAUSIBLE | #21 (`lateralHold` rail) is what initiates the Darkreach precursor | 427 |
| `L5` | PLAUSIBLE | #23's placement transient is what makes the Darkreach cascade self-sustaining | 428 |
| `L6` | PLAUSIBLE | The per-replicate reset teleport can damage an airframe | 429 |
| `L7` | PLAUSIBLE | `predFloor = 0.30` is a real, distinct gate defect | 430 |
| `L8` | PLAUSIBLE | The position effect in a card is energy accumulation | 431 |
| `L9` | PLAUSIBLE | `FastBomber1`'s variable-geometry wing explains its 5–10× replicate sd | 432 |
| `L10` | PLAUSIBLE | The law's problem at the heavy end is pitch authority running out | 433 |
| `L11` | PLAUSIBLE | `trainer · oblique-12-c` is a card/airframe pair on which an AoA-gate A/B could return non-null | 434 |
| `L12` | PLAUSIBLE | `_yawWeak` measures "the error did not close", not "the rudder is weak" | 435 |
| `L13` | PLAUSIBLE | v0.85 `AlignRateLead` makes the roll DERIVATIVE gain a function of `blendWeight` | 436 |
| `L15` | PLAUSIBLE | ~~The H7 residual is BOTH turn channels de-rated at once~~ **RETRACTED — the yaw de-rater is BYPASSED on every rotorcraft row; replaced by the game's weathervane vs a leaky integrator** | 437 |
| `L14` | PLAUSIBLE | `_pitchEff` × `_alphaSchedFilt` are two de-raters of ONE physical event, multiplied to 0.09 | 438 |
| `X1` | REFUTED | *"No mod-side G-limiter — the game's stability control governs."* | 449 |
| `X2` | REFUTED | *"The law is bending airframes."* Stated to the maintainer | 450 |
| `X3` | REFUTED | v0.88's **aoaTrim theory** — that writing the placement velocity at AoA = 0 caused the entry thump | 451 |
| `X4` | REFUTED | Gate A: *"`iPitch`/`iYaw` read 0.0000 on every first row, so `ctrlReset` does what it claims."* | 452 |
| `X5` | REFUTED | #20: *"the `PEffRevThresh` floor branch is unreachable, so `_pitchEff` never goes below 0.15."* | 453 |
| `X6` | REFUTED | *"The oblique family is where #20 and #21 get A/B-ed"* (LAW-CHARACTERIZATION §4 Batch 4) | 454 |
| `X7` | REFUTED | *"`aoaLimiterActivePct` is 0 in every capture ever taken."* | 455 |
| `X8` | REFUTED | R21/LAW-CHARACTERIZATION: *"the bank clamp is what holds the 9.4° sustained-turn lag."* | 456 |
| `X9` | REFUTED | INSTRUCTOR-LOOP §5: *"independent hysteresis-free gates chatter and that is the cross-fighting the maintainer feels."* | 457 |
| `X10` | REFUTED | R28 §3.2: *"`bSup` reads 0.000–0.06, so belowness is excluded as the mechanism."* | 458 |
| `X11` | REFUTED | R28 §4.3, and the Gate B record: *"#23 does not reproduce and is confirmed harmless."* | 459 |
| `X12` | REFUTED | *"`arm=0` on `BelowAlignSuppress` disables the suppression."* A whole batch was commissioned on it | 460 |
| `X13` | REFUTED | R28's headline *"1.2–17.9× the terminal error"* as a property of the law | 461 |
| `X14` | REFUTED | R28's *"treat any non-zero count of 33.3 ms rows as the stop signal for going wider."* | 462 |
| `X15` | REFUTED | *"`FastBomber1` is a failure airframe."* | 463 |
| `X16` | REFUTED | LAW-CHARACTERIZATION §1: *"19 cards, ONE has ever been flown, on ONE airframe, and it is saturated."* | 464 |
| `X17` | REFUTED | *"The Darkreach is the only airframe with `flightAssist = 0`."* | 465 |
| `X18` | REFUTED | *"R29's 26.9 g means the airframe was overstressed."* | 466 |
| `X19` | REFUTED | *"`oblique-6-dwell` scores a property of the airframe."* All 314 captures of it were read that way | 467 |
| `X20` | REFUTED | R37 §4: *"the `oblique-6-dwell` drift is ordered by thrust-to-weight."* | 468 |
| `X21` | REFUTED | *"`wobbleEpisodes*` counts oscillation modes."* Six signals, the whole corpus, and four documents rested on it | 469 |
| `X22` | REFUTED | *"`RelativeTurnLead` is a live lever worth sweeping."* A card and a knob existed for it | 470 |
| `X23` | REFUTED | *"`alpha-sweep` measures the alpha regime"*, and *"low q is the route into it."* | 471 |
| `X24` | REFUTED | *"`dmgFrac` reports per-row damage"*, and *"a detach ratio of 0.114 is four detach events."* | 472 |
| `X25` | REFUTED | *"R39's `stol-*` batch is STOL data."* 53 captures, ONE-LAW standing case 3 | 473 |
| `X26` | REFUTED | *"The Darkreach damage failure fires at placement 4–5"* (ledger #51), as a rule the R40 cards would rank | 474 |
| `X27` | REFUTED | *"Replicate 1 is a normal replicate."* Every ABBA batch ever flown assumed it | 475 |
| `X29` | REFUTED | *"`AttackHelo1` diverges — the rotorcraft law fails on a plain helicopter."* R41's headline rotorcraft result: a mo… | 476 |
| `X30` | REFUTED | *"The tiltwing blend sign is NOT inverted"* (H3) — **and X30's OWN "monotone fall" reading is corrected: that is the tail of a spawn transient; the flip is the wrong fix (O13)** | 477 |
| `X31` | REFUTED | §7 Tier 3 / `GENERALITY-REVIEW.md` finding 6: *"`AttackHelo1` can never leave the hover regime at any speed it is c… | 478 |
| `X28` | REFUTED | *"A field capture from a user is readable."* | 479 |
| `X32` | REFUTED | *"`DroneAltDeckM` makes altitude a balanced experimental factor crossed with airframe."* The deck sets SPAWN altitude only — placement teleports every lane to the card's `startAlt` | 480 |
| `X33` | REFUTED | *"Placement above ~400 m/s destroys an aircraft"* — R43's `352 survives / 440 dies` bracket. R44's speed ladder kills it at both ends | 481 |
| `X34` | REFUTED | *"The unnormalised roll constants produce a measurable q-dependent roll response"* (finding 5's predicted consequence), and *"`outR` sd measures it"* — the crossed q pair says no on both counts | 482 |
| `O1` | OPEN | What arrests the down leg in the fine regime? | 490 |
| `O2` | OPEN | Is the residual spread a law problem or an airframe-capability difference? | 491 |
| `O3` | OPEN | Does #21 (`lateralHold` rail) cost anything? | 492 |
| `O4` | OPEN | Does the mod's AoA path work? | 493 |
| `O5` | OPEN | What sets the R32 onset at replicate ~32? | 494 |
| `O6` | OPEN | Does removing `belowSuppress` entirely remove the down-step penalty? | 495 |
| `O7` | OPEN | Does the precursor CAUSE the Darkreach departure, or share a cause? | 496 |
| `O8` | OPEN | Is `EW1` doing the same thing more slowly? | 497 |
| `O9` | OPEN | ~~**Rotorcraft, STOL, and the whole attribution set are UNFLOWN.**~~ **MOSTLY CLOSED (R39/R40/R41) — and the residu… | 498 |
| `O10` | OPEN | Does the law ever move the nose AWAY from the demand? | 499 |
| `O11` | OPEN | Does the roll axis limit-cycle above ~350 m/s on every airframe? **NARROWED BY R43 — flown at 407–505 m/s / q 71.6–112.3 kPa and CLEAN on 12/12; only the human-on-the-mouse condition is left** | 500 |
| `O12` | OPEN | ~~**Does `tiltFrac` actually rise toward 1 in the hover?**~~ **ANSWERED BY R42, AND THE ANSWER IS NO — see X30.** T… | 501 |
| `O13` | OPEN | ~~Is the `tiltFrac` defect the SIGN or the LIMITS?~~ **RESOLVED — NEITHER: the tiltwing branch is missing the `1f −`, hover reference is 0.18. THE PRE-FIX BASELINE IS NOW RECORDED (R44, n=10+10): `heliBlend` = 1.0000 ± 0.0000 wing-borne. The fix is UNBLOCKED — ship it** | 502 |
| `O14` | OPEN | Are the three v1.0.1 fixes actually working? **(c) is CLOSED by R44 — `arm=NULL` on replicate 1 of 4/4 lanes, `0,1,1,0` on 2–5. (a) and (b) still unexercised** | 503 |
<!-- LEDGER-INDEX:END -->

## 1. ESTABLISHED

### 1.1 The instrument

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| I1 | **The rig does not drift enough to invent an effect.** | Gate A / R22, n=8, `fixedwing-sweep` | first-sample `spd` spread **0.10 m/s**; null split worst metric **1.37 sd** vs a 1.40 threshold; noise floor on `turn360` — `terminalOffDeg` sd **0.046° (0.5 %)**, `rmsPointingErrorDeg` 0.093°, `gSustained` / `meanTurnRateDegS` 0.1%. Replicates snap back ~1740 m and arrive within **0.1 m/s** of each other. *Two of the gate's original criteria were wrong and were replaced: `\|outP\| < 0.05` at row 0 measured a deterministic entry transient (0.146 on 7 of 8, gone by t+0.7 s) rather than a stale demand — use `off` at row 0 (0.02–0.08°); and `\|r\| < 0.4` on `terminalOffDeg` vs run index has no effect-size floor, so a perfectly reproducible rig fails it (measured r = −0.885 across a **0.11° total range** on a 9.4° mean).* |
| I2 | **Captures are labelled correctly and ABBA arms alternate.** | Gate B / R23, n=4 | `arm=` 0,1,1,0 with `armKnob` recorded; 64 columns; no `d<n>` leak on a crewed capture; `compare-runs.py` splits A n=2 / B n=2 with no unbalanced-arm warning |
| I3 | **A drone flies the same law as the player, bit-for-bit, and does not touch the player's aircraft.** | Gates C/D — R24 (n=5), R25 (n=9) | Drone `terminalOffDeg` lands in the crewed band with the same arm ordering (A 7.00 vs crewed 6.21–6.28; B 10.3 vs 9.32–9.35), ~0.6–1.0° wider. **The strongest evidence is a shared defect, not a matched metric:** #23's placement-tick artifact reproduces on the drone to three digits (`rollRate` −58.99/−58.56/−58.49 against crewed −58.99/−58.66/−58.65), so the drone is not a parallel implementation. Gate D: with a human flying hard alongside (peak **8.79 g**, 41.7% of rows over 60° bank), two concurrent arm-A drones agree to **0.008°**, and a permutation test over 16 card-start/stop windows finds no marker or stick leak (p 0.145–0.780); the anomaly log cross-attributes **zero** lines. **Verified in the 0.34 decompile, not inferred:** an uncrewed aircraft gets *complex* physics (`CheckIfLocalSim` falls through to `Server.Active`), and the distance LOD `Aircraft.CheckPhysicsLod()` is dead code — `private`, zero callers. Holds only while `Server.Active`; the harness refuses to spawn as an MP client. |
| I8 | **Replicate scatter is dominated by the world-origin float grain, and it is a distance law.** The game's origin follows the OPERATOR'S CAMERA, so a lane's physics jitter moves with it — mid-batch, without warning, in opposite directions on different lanes. | R35 (186 caps), R36 (2 × 16 lanes), R37 (6 matched NEAR/FAR pairs ~60 km apart) | `gJitterG` vs lane index Spearman **+0.859 / +0.894**; log-log slope vs `origDist` **0.893 / 0.864** (R35: 0.885), matching the `d · 1.2e-7` float-grain prediction. Six airframes flown twice per launch 60 km apart: **12 of 12 pairs up, median 3.83×**, sign-test p = 0.0005 |
| I9 | **`fixedWindowOffDeg` is the metric `terminalOffDeg` was pretending to be**, and the airframe ranking built on it is the corpus's most reproducible number. | R35→R36→R37, 10 airframes, 24 (airframe, tag) cells spanning a 4× `gJitterG` change | Median CV **8.5%** (`fixedWindowOffDeg`) and 2.2% (`rmsPointingErrorDeg`) against **82.0%** (`terminalOffDeg`) — the same order as `gJitterG`'s own 66.8%, i.e. terminal error is *measuring the jitter*. Correlation with `origDist`: **−0.080** vs `gJitterG`'s **+0.886**. Ranking reproduces R35→R37 at Spearman **+1.000** (exact, n=10), R36→R37 +0.976, across two mod versions and two lane frames |
| I10 | **The large-displacement placement kill is fixed, and R37 is the only batch that proves it.** | R37, 109 placements at 13.9–41.2 km snapback | **109 of 109 survived**; zero `aircraft gone`, zero pilot kills. R36's separation was perfect the other way — 32/32 zero-displacement survived, 32/32 displaced died — on the identical card and roster, so the contrast is clean. Caveat carried forward: **74% of R37's legs (367/496) are at the `terminalOffDeg` resolution floor**; it is not a metric on that card |
| I11 | **The v0.99.0 ring lane geometry works, and it is confirmed by intervention rather than correlation.** | R41 vs R33 — same card, same ten airframes, same entry speeds; line layout (8–98 km) vs ring (11.3–14.0 km) | `gJitterG` mean **0.174 → 0.077**; between-lane spread **81.8× → 3.42×** (a 24× reduction, against ~3.4× predicted). Pointing scores did not move, so the fix cost nothing |
| I12 | **The placement kill that survived I10 is a NON-ZERO *ANCHOR* placement on a VARIABLE-GEOMETRY airframe.** Three factors, separated: (1) **anchor, not size** — `FastBomber1`'s *mid-run* placements are safe to **26.3 km snapback and 3 894 m of altitude change**; its *first* placement is fatal at **1 500 m**. (2) **airframe, not speed** — at the same card, deck and entry speed the other keys survive. (3) **speed is not a factor at all** (see `X33`). `FastBomber1` and `QuadVTOL1` are the corpus's only two variable-geometry keys (`sc_wingAngleMaxDeg` non-NULL: 420 + 84 captures, 0 on the other eleven), and `QuadVTOL1` has never been given a non-zero anchor — so the *mechanism* half stays PLAUSIBLE until it is. | R40 + R43 + R44, 63 `FastBomber1` anchor placements. SQL: `airframe='FastBomber1' AND entry_snapBackM=0 GROUP BY round(abs(entry_alt_to-entry_alt_from))`. Mechanism from the decompile: `SwingWingController.RotatorInput.Animate` (`:68680-68703`) writes `counterRotators[i].transform.Rotate(...)` and re-issues `part.SetHingeJoint(...)` **every FixedUpdate the wing is slewing** (`if (Mathf.Abs(num) > 0.0001f)`) — a Transform write on a body `AeroPart.CreateRB` unparented, which is exactly the lethal act the `MoveAssembly` graveyard names. `TiltWingController.RotatorLinkage` (`:70141-70146`) writes `linkage.transform.rotation`/`localScale` the same way. | **Anchor placement, `dz = ±1500` m: 31 of 31 FATAL** (R40 1, R43 3, R44 27). **Anchor placement, `dz = 0`: 0 of 32 placement kills** across R26…R44 at 90–**440 m/s** (the one abort in that stratum is `altitude floor`, R39 `stol-sweep`). **Mid-run placements: 0 of 86 at \|dz\| > 1 000 m** (max 3 894 m) and 0 of 88 at 440 m/s. Matched control within R44 — same card, same deck, same speed on `place-300`/`-375`/`-390`: `FastBomber1` **9/9 dead** vs `Fighter1`/`Multirole1`/`SmallFighter1` **8/8 alive**, Fisher exact **p = 4.1 × 10⁻⁵**. Signature: root ends **357 ± 2 m** back toward the spawn deck (sd 2 m over 27 kills, both deck signs, 300–440 m/s) with `velY` **3.3–12.3 km/s** — a constant *position* ratio with chaotic *velocity*, i.e. a constraint payback, not a ballistic one. Related: `L9` (the same wing explains this airframe's 5–10× replicate sd), `I10` |
| I4 | **Three instrument defects were real and are fixed.** #29 no disk card had loaded at all v0.71→v0.90 (`JsonUtility` dropped `Seg[]`); #30 two-seat airframes double-stepped the control law; #37 `frameMs` read a constant. | [`ROADMAP.md`](ROADMAP.md) "Where we actually are"; R26 (trainer/FastBomber1 flew a 30 s segment in 14.95 s); R27 (**223 899 rows all exactly 16.70 ms**) | all three would have corrupted any law A/B run against them |
| I5 | **The concurrent per-aircraft A/B (v0.94) works in flight.** | R31, 96 captures, 6 lanes | 48 `arm=0` / 48 `arm=1`, ABBA exact on every lane, 136 overlapping pairs on opposite arms, `# config` cannot lie about the arm (R31 §7.3–7.4) |
| I6 | **Frame-time cost of extra lanes is superlinear.** | R28 (8 lanes) vs R29 (10 lanes), comparable ~30 min sessions | rows > 20 ms **16 → 243 (13×)**; distinct stall events **2 → 23 (11×)** for a 25 % lane increase (R29 §5.3) |
| I7 | **The oblique family is UNSATURATED and is the only regime whose metrics can respond to a gain change.** | R27/R28/R29/R30/R31/R33, 4 894 oblique segments | `authorityUsedFrac` median **0.10–0.20** per batch, max 0.78; **0 railed segments in R30, R31 and R33** |

### 1.2 The down-step penalty — the largest measured law effect in the corpus

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| D1 | **At matched step magnitude and mirrored geometry, moving the nose DOWN leaves more terminal error than moving it up.** | R28 (384 caps, 7 of 8 airframes), R29 (441 caps, 9 of 10), R30 (48, order-controlled), R31 (96, arm-controlled), R33 (77) | geomean down/up ratio at 12°: R28 3.49, R29 2.37, R30 2.84 pooled, R31 2.8–5.5 |
| D2 | **It is DIRECTION, not position in the card.** Crossed design: identical geometry, reversed traversal. | R30, 3 airframes × 2 cards × 8 replicates, `oblique-12-fwd`/`-rev` | direction **×3.07 / ×5.39 / ×1.39**; position **×0.98 / 0.71 / 0.79** — 4–7× smaller **and the wrong sign**, so every shipped card *understates* it. Interaction ×1.005–1.150. |
| D3 | **It is not energy, dynamic pressure or airspeed.** Down is worse when it is the slow leg *and* when it is the fast leg. | R30 §5.1 — the crossing is free from the design | fwd: down at 252–276 m/s, up at 273–319; rev: reversed. Ratio > 1 both ways |
| D4 | **It is not terminal elevation.** The DR↔UL mirror terminates at the same commanded elevation in both cards. | R30 §5.2; R28 §3.2 (`oblique-below` at −20° shows a *larger* ratio) | ×2.646 (Fighter1), ×4.607 (Multirole1) on the matched pair |
| D5 | **It is magnitude-gated, essentially absent below the `FineAngle = 6` knee.** | R29 §3.3, 9 airframes | geomean ratio **1.04 (0.5°), 1.18 (2°), 1.06 (2.5°), 1.39 (6°), 3.33 (12°)**; ρ(log ratio, step magnitude) ≥ +0.8 on 8 of 9 airframes |
| D6 | **It is speed-insensitive.** R29→R33 changed the resolved entry speed by −44 % … +22 % per lane (the #41 AI-corner → FBW-corner fix) and the ratios barely moved on 7 of 10. | R29 vs R33, `oblique-6-c`, same card, same tags | COIN 1.54→1.60, VTOLTrainer1 1.35→1.37, EW1 1.17→1.29, CAS1 1.50→1.37, Multirole1 1.80→1.54, trainer 1.33→1.16, SmallFighter1 1.20→1.09 |
| D7 | **`Fighter1` INVERTS it — up is worse — in both batches.** Not noise; it is the airframe with the best score overall. | R29 0.74 → R33 0.61 (geomean of both mirror pairs, `oblique-6-c`) | R33 DR/UL **0.492**, DL/UR 0.921 |
| D8 | **`bSup` / `BelowAlignSuppress` is NOT the transmission path.** `bWt` — the loop gain `bSup` multiplies — is **identically 0.000 over the terminal 1.0 s of all 384 scored R31 segments**, and over the whole late 60 % of 379 of them. The metric is read 5–7 s after the gate shut. | R31 §4.3, 96 captures, both arms, 3 airframes | roll channel closes at t = **0.83–3.10 s** of an 8 s segment; `terminalOffDeg` is averaged over 7.0–8.0 s |
| D9 | **The penalty is created downstream of the roll handover, in the fine regime.** Both hemispheres hand over at the *same* azimuth error; the up leg then closes 93–95 % of it and the down leg 58–80 %. | R31 §4.3 | handover \|azErr\| 1.87–2.69° both directions; converged-to ratio **0.051–0.073× (up)** vs **0.202–0.418× (down)** |
| D10 | **The residual is almost pure azimuth.** | R28 §3.2, R29 §3.4, R31 §4.3 (light jets) | `Fighter1 obDR12` terminal `off` 0.608°, `azErr` +0.608°, `elevErr` **−0.015°** |
| D13 | **The corpus's first above-floor steady-state pointing measurement, and the ~3.5° standing lag it predicted.** | R39-D, `e3-marker-ff` / `e2-rel-turn-lead`, 121 caps, 8 lanes, `repeat: 8` | `offFloorPct` **0.000–0.059%** per lane against R37's 74% of legs at the floor; `fixedWindowOffDeg` non-NULL on **121/121**, zero `skipped`. With `MarkerRateFeedForward` OFF the settled \|`azErr`\| is **3.38–4.16°** on 7 of 8 lanes against the predicted rate/K = 4.5/1.28 = **3.52°**, and it is genuinely *standing* — flat by 10 s decade on 14 of 16 lane-arms |
| D11 | **Reverting to the v0.67 suppressor moves it 5 %/29 %/2 % and leaves ×2.8–3.9 standing.** Up legs do not regress on either form. | R31 §4.2, paired within (lane, card), n=4 cells | arm0/arm1 **0.948 / 0.709 / 0.980**; up terminal 0.980 / 0.981 / 0.919, every CI touching 1 |
| D12 | **The v0.67 form rails the roll stick and the v0.85 form does not** — the cost v0.85 was shipped to buy is real and in the predicted channel. | R31 §5.2 | `\|outR\| ≥ 0.999` on **1.17–1.49 %** of down-leg ticks (59 of 96 segments) on arm 0, **0.000 %** on arm 1; 17 vs 4 `outR` oscillation episodes |

### 1.3 Cross-airframe generality

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| G1 | **The law is NOT tuned to the Ifrit.** The airframe every pre-R26 capture was taken on is mid-band. | R28 (`Multirole1` rank 4 of 8, ΔA to VTOLTrainer1 = 0.9× noise), R29 (rank 5 of 10, ΔA to CAS1 = **0.1× noise**), R33 (rank 9 of 10 by terminal error) | the long-standing worry is dead |
| G2 | **The airframe ranking is stable across two independent changes of entry condition.** | R28→R29 Spearman **ρ = +0.929, permutation p = 0.0022** (n=8). R29→R33 on `oblique-6-c` **ρ = +0.903 (n=10), +0.967 (n=9 ex-Darkreach)** across the #41 corner-definition change (verified here by SQL) | no airframe moves more than one rank; every swap is inside or near the noise floor |
| G3 | **Entry speed does not explain the spread.** | R29 §Q2, n=10, speeds 86–190 m/s; independently R33 | ρ(A, entry speed) = **+0.188, p = 0.61**; ρ(A, entry ÷ Vstall) = −0.049. In R33, `terminalOffDeg` reproduces R29 to **±17 % on 9 of 10 airframes** while entry speed moved −44 % … +22 % |
| G4 | **The residual spread is real but bounded, and it is not at the incumbent.** | R33, 77 caps, 10 airframes, `oblique-6-c`, **zero railed segments** | per-airframe mean `terminalOffDeg` **0.0646 (trainer) … 0.3819 (SmallFighter1)** = **5.91×**, i.e. **29× the replicate noise floor** (median cell sd 0.0109°) — but on a 6.0° leg, even the worst removes **93.6 %** of the step and the best **98.9 %** |
| G5 | **The R28 spread was ~40 % entry condition and ~60 % law–airframe interaction.** | R28 vs R29, 8 common airframes | `flightscore` A spread 0.237 → **0.1455** (70× → 29× noise); ex-Darkreach 0.146 → **0.082** (16× noise) |
| G6 | **Two-seat crew, FBW `assist=0` and distance-above-corner are all EXCLUDED as causes of the spread.** | R28 §2.3, R29, R32 §7 | both twin-seaters mid-band; `EW1` has `assist=0` and scores mid-band (ρ +0.048); `EW1` flies furthest above corner and outranks `FastBomber1` |
| G7 | **`CAS1` and `COIN` — the two airframes the flat-250 grid could never fly — are ordinary members of the band.** | R29 §4.2, 48/48 captures each, **0 of 192 railed segments each** | rank 6 and 9 of 10; no clamp, cap, rail or AoA gate fires on either |
| G8 | **The between-airframe spread survives at matched speed** — it is not an artifact of each airframe flying its own entry condition. | R39-A, `oblique-6-dwell` throttle contrast (0.40 vs 1.00), 128 caps, 16 lanes, leg 1 only | Noise unit derived from the batch itself: σ = **0.0247°** (20 cells, 108 df). **\|Δ\| < 3σ on 7 of 10 airframes** across a throttle swing that tiles V/Vcorner from 0.87 to 1.61 with overlapping airframe coverage. *Caveat that came with it:* the pre-registered ≥1.25× speed ratio was reached on only **3 of 10** airframes, and COIN failed the manipulation outright at 1.094× |
| G9 | **No fixed-wing regression across eleven releases, v0.96.0 → v1.0.3** — and **R41→R44 is the tightest repeat measurement the corpus has**, which is what makes `oblique-6-c` a usable harness control rather than only a law check. | R41 vs R33, then **R44 vs R41 (2026-08-05)**: `oblique-6-c`, 10 airframes / 10 lanes, identical entry speeds (`entry_v_to` equal to 0.1 m/s per key across all three batches), anchor replicate 1 excluded (X27), 248–272 unrailed `oblique_step` segments per batch | **R41 vs R33:** `terminalOffDeg` within **0.5–12%** (8 of 10 inside the 0.1–4.7% null contrast; the two movers, −12%, both improve); `rollYawOpposedPct` within 0.1–1.9 pp. **R44 vs R41:** airframe ranking by `rmsPointingErrorDeg` reproduces at Spearman **+1.000** (exact, n=10); per-airframe drift **max \|0.56%\|, mean 0.00%, median −0.06%** — against a historical 1–3% acceptance band and against the **±3%** within-batch replicate scatter `compare-runs.py` reports when R41 and R44 `Fighter1` are pooled, i.e. the between-batch term is smaller than the within-batch term. `terminalOffDeg` within **1%** on 10 of 10, `gJitterG` per lane 0.042–0.091 with the ring unchanged (per-lane mean `origDist` 11.5–13.9 km vs R41's 11.3–14.0 — I11). Extends `CHANGELOG.md` §1.0.0's "byte-identical" claim back seven further releases and forward three. **Consequence for R44:** the instrument did not move, so every other R44 verdict rests on a control that reproduced |

### 1.4 The game — three corrections verified against the 181 878-line 0.34 decompile

| # | Claim | Evidence |
|---|---|---|
| P1 | **The game has NO G governor.** `ControlsFilter.GLimiter` is dead code — the identifier occurs **exactly once** (`:65242`, its own `protected class` declaration), no field of that type exists, nothing instantiates it, and `LimitG(...)` (`:65277`) has **zero call sites**. | R32 §1.1 |
| P2 | **Over-G damages the PILOT, never the airframe.** `Pilot.TakeGForceDamage` (`:85989`) fires above 20 g and applies `(sqrG − 400)·0.007` to **one part index — the pilot's own**. No structural-G path exists anywhere in the decompile. | R32 §2; confirmed in flight — 3 R32 lanes ended `despawned (pilot killed)` with `aeroPartCount` **35 on all 63 captures** and `massKg` constant to 5 kg |
| P3 | **The game's alpha limiter is gated `if (num2 < 1f)` (`:65033`) and is therefore INACTIVE above corner q — which is where every shipped card flies.** The mod's own AoA block is the only alpha protection in the loop at card speeds. | R32 §1.3 — `num2 < 1` on **2.3 %** of 37 868 R32 rows; **86.3 %** of the 5 541 rows past the airframe's own 10° `alphaLimiter` had the limiter structurally inactive |
| P4 | **`aeroPartCount` cannot see damage.** Nothing on the detach path calls `RemoveFromUnit()`, the only caller of `DeregisterAeroPart` (`AeroPart:74749-74755`), so it never decreases. | CLAUDE.md `Recording.cs` bullet; v0.96 replaced it with `dmgFrac` off `partDamageTracker.GetDetachedRatio()` |

### 1.5 The Darkreach failure

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| K1 | **The precursor reproduces**: the roll-to-align channel commands large bank against a sub-degree azimuth error on a card whose largest demanded step is 0.35°. | R29 (n=1, 55–63° at 1.3° `azErr`) **reproduced in R32** (63 caps, 5 lanes, fresh session, different mod version) | recs 01–31: **0.0° of `targetBank` at \|azErr\| < 5° on every capture**. Recs 32–63: up to **55.5°**, 12 captures over 30°. Precursor appears **1–2 replicates before the first departure in every lane** |
| K2 | **The departure is an AoA/authority failure, not a G failure.** The mod commands \|`outP`\| ≤ 0.24 *against* the excursion the whole way; the plant delivers pitch rate in the opposite direction. | R32 §5, 18 departed captures | `\|fbwPR/fbwTgtPR\|` median **7.73**, p90 13.0, max 28.2 on departed captures vs **1.56** on clean |
| K3 | **The law's entire response to a non-responding plant is a graded stand-down, and it runs out.** Five terms reduce authority (`qSched`'s two 0.3 floors, `Max(0.3f, aoaGateUp)`, `pErrTerm *= _pitchEff`, `aoaRecover *= _pitchEff`); **nothing in `Apply` increases authority or changes strategy.** | R32 §6; [`GENERALITY-REVIEW.md`](GENERALITY-REVIEW.md) finding 18 | `qSched` **exactly 0.300 on 100.0 %** of the 2 314 rows past \|AoA\| 20°, against **0.0 %** on all 31 clean pre-onset replicates of the same card and airframe |
| K4 | **The placement-tick transient (#23) is BIMODAL, and the upper mode is not benign.** | R32 §8, 58 placed `Darkreach` captures | median \|`rollRate`\| **0.753** (reproduces R28's 0.725) but **19 of 58 above 5**, max 54.2; \|`leadDeg`\| max **314°**; **\|`outP`\| rails at 1.000 on 15 of 58 placement ticks** |
| K5 | **The airframe-side half is a specific combination, and `flightAssist = 0` is not it.** | R32 §7, FBW headers of all 10 airframes | unique to Darkreach: `gLimitPositive = 4` (lowest; next is 6), `maxPitchAngularVel = 0.3` and `alphaLimiter = 10` **on 105 409 kg as flown**, `fbwCornerSpeed = 100` against a published 180 |
| K6 | **At a genuine 0.95× FBW corner (95 m/s) the Darkreach flies the card.** | R33, 4 replicates before the damage abort | `terminalOffDeg` **0.2178** (R29 at 171 m/s: 0.5366 — a 2.5× improvement); **zero railed segments**; `authorityUsedFrac` 0.48–0.73 |
| K7 | **The Darkreach azimuth mode is real, V-dependent and Darkreach-only — and it is NOT the settle loop's.** | R39-C, 512 legs, two throttle arms, amplitude-independent estimator on the settled window | Darkreach is **32/32** with its `azErr` autocorrelation peak in the 0.25–0.50 Hz band at coherence **0.72–0.81**; the other nine airframes are **3 of 480**. Frequency tracks airspeed on all four legs (+0.058…+0.079 Hz between arms) and fits **f ∝ V^0.305 ± 0.015** (r² 0.853, n = 72). It is 4–22× above the quantisation floor, so it is not float grain |

### 1.6 Rotorcraft — the v0.58 branch, and the one airframe that hovered

Standing ONE-LAW case 4. R39 produced the first rotorcraft captures in the corpus; R41 was the first
batch in which the branch **executed**; **R42 is the first batch that measured the shipped
CONFIGURATION**, because R41's live `com.no.wtmouseaim.cfg` still held the v0.43 pair
`HeliForwardSpeed = 150` / `HeliHoverSpeed = 40.28` against the shipped 60 / 20 — verified from the
captures' own `# config` line, `heliFwd=150 heliHover=40` on 56/56 R41 rotor captures and
`heliFwd=60 heliHover=20` on 56/56 R42 ones.

> **READ THIS BEFORE ANY PRE-R42 ROTORCRAFT NUMBER.** `heliBlend = max(speedRamp, tiltFrac)` with
> `speedRamp = clamp01((HeliForwardSpeed − vFwd)/(HeliForwardSpeed − HeliHoverSpeed))`
> (`ChaseController.cs:1111-1135`, read 2026-08-02). At 150/40 that expression is **identically 1.0
> for every vFwd ≤ 40 m/s**, i.e. for the whole of R41's hover cards — so R41 measured the law with
> `tBankE *= (1 − heliBlend)` **zeroing the bank channel outright** and with `tiltFrac` invisible
> under the `max()`. **Where R41 and R42 agree they agree because the two configurations are the same
> expression** (both clamp to 1.0 at vFwd < 20 and at any negative vFwd): that covers `QuadVTOL1` and
> `UtilityHelo1` entirely, and `AttackHelo1`'s `hover` / `hoverbase` / `hoverstep5` / `hoveryawR`
> segments. Only `AttackHelo1` above ~20 m/s and `QuadVTOL1`'s `rotor-transition` are contrasts.

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| H1 | **The v0.58 rotorcraft branch executes as of v1.0.0 — and had never executed before it, for ~40 versions.** | R39-rotor (`_heloOk` false on **48/48**, established by row-by-row reconstruction because the column did not exist); R41 (`heloOk = 1` on **108,987 of 108,987** rows, all three airframes, all three cards) | 56 `[helofbw]` probe lines against R39's **zero**. Every R39 rotorcraft number is a measurement of the *pre-v0.58* law with `HeliYawScale = 2.0` bolted on — the configuration the v0.58 comment says limit-cycles |
| H2 | **With the branch live, the law is excellent on the one airframe that genuinely hovered — and R42 shows NO REGRESSION at the shipped config.** | R41 and **R42**, `QuadVTOL1`, `rotor-hover` + `rotor-bistab`, n=8 per cell in both | R41 terminal azimuth error **0.0004–0.013°** on the 90° yaw-to-point step against **38.7–40.0°** for the same airframe and card in R39; pedal rail 8.5–9.6% against R39's 40–87%. **R42 reproduces it to 2–3 decimals on all 13 cells** — `fixedWindowOffDeg` 0.041/0.046, 0.074/0.073, 0.177/0.169, 0.179/0.169; `settleTime95` 7.3/7.3, 8.6/8.4, 9.6/9.7; `stickRailPctY` 8.5/9.2 and 9.6/9.0. `persistent-miss` 10 → 11, all eleven the same signature (`phase=ALIGN`, `off≈21`, pedal 0.90–0.96, `spd` 3–7) — the airframe's yaw-rate limit during the 90° slew, not a failure. **State the caveat with the pass:** on these two cards `QuadVTOL1` flies at `vFwd` −0.7…−2.7 m/s, where `speedRamp` clamps to 1.0 under *both* configurations, so this is a reproducibility check, **not** evidence that the law is insensitive to the knob pair |
| H3 | ~~**The tiltwing blend sign is NOT inverted.**~~ **CORRECTED BY R42 — the narrow observation survives, the verdict does not.** What still holds: `heliBlend` is **not** pinned at 1.000 in wing-borne cruise, which was the one failure mode R41 could see. What is now measured and runs the wrong way: **`tiltFrac` FALLS as the aircraft decelerates toward the hover** — see **X30**. | R41 `rotor-transition` (8 caps) for the surviving half; **R42** (8 caps, same card, same lane pair) for the correction | R41: `heliBlend` − speedRamp residual **+0.002** in the three slowest bins, which is why the tilt term was invisible — it was under the `max()`. **The `ponytail:` comment at `ChaseController.cs:1128` named the wrong repair and is now RESOLVED AGAINST: the defect is real but it is NOT a sign flip.** The tiltwing branch is missing the `1f −` its nozzle twin has, and the game pins the hover end of the tilt command at **0.18**, not 0 — mechanism, citations and the corrected fix in **O13** |
| H4 | **Forward speed, not the yaw step, selects the outcome on a rotorcraft** — and R42 sharpens the threshold to a knob rather than a speed: the selector is **`heliBlend < 1`**, i.e. `vFwd > HeliHoverSpeed`. | R41 (both hover cards) and **R42** (same cards, same 14 lanes, `repeat: 4`) | R42 `AttackHelo1` `rotor-bistab`, settled window: at `vFwd` 5.7 / 17.9 m/s (`heliBlend` 1.00 / 0.99) `\|azErr\|` is **0.021 / 0.022°**; at 30.2–37.9 m/s (`heliBlend` 0.55–0.75) it is **1.54–2.49°** — a **103×** step for a 1.6× step in demand (5° → 8°). `QuadVTOL1` never exceeds `vFwd` −0.7…−2.7 m/s on either hover card and terminates at 0.000 in both batches. **The R41 evidence for this line — the monotone runaway to 34.1° — is RETRACTED as a config artifact (X29); the claim survives on R42's own contrast** |
| H5 | **Two of three rotorcraft never hovered, and it is a harness limitation, not a law result. REPRODUCED IN R42 UNCHANGED — the collective fix is not in the deployed DLL.** | R41 (16/16 `UtilityHelo1` aborts) and **R42 (16/16 again, both cards)**, mean `vFwd` over scored segments | `UtilityHelo1` sinks at `velY` **−24.2…−28.7 m/s** at the harness's fixed `HoldThrottle = 0.60`, `thr` pinned at 0.600 on every row of both batches, altitude 2453 → 530 m, every capture ending `abort: altitude floor (500 m MSL)`. R42's per-tag numbers match R41's to **three decimals** on all seven tags — the config change is a structural no-op here (`vFwd` 6–10 m/s ⇒ `speedRamp` clamps to 1.0 under both pairs). **Not fixable from a card** — `ScenarioPlayer.OwnInputs` early-returns at `EntrySpeed ≤ 0` by design, so a throttle pin is read after the return. Needs a collective/altitude hold or a per-airframe `HoldThrottle` |
| H6 | **`AttackHelo1`'s R41 divergence was a STALE-CONFIG artifact, not a law defect. At the shipped 60/20 it converges** — and the mechanism is the bank channel being handed back. | R41 vs R42: identical card, identical lane pair, identical roster, 8 replicates per cell, one knob pair changed | `fixedWindowOffDeg` on `rotor-bistab`: `hoverstep8` **15.33 → 2.31**, `hoverstep12` **25.81 → 1.32**, `hoverrec8` **18.61 → 1.76**, `hoverrec12` **23.76 → 1.69** (−85…−95%, distributions non-overlapping). `rotor-hover` `hoveryawL` terminal **7.41 → 1.77**. Pedal rail `stickRailPctY` **66.1% → 0.0%** on `hoverstep12`; `iYaw` off the 0.12 cap (0.111 → 0.036). Mechanism, settled window: delivered `bank` **0.02–0.56° → 2.26–3.02°** as `heliBlend` falls 1.000 → 0.55–0.75, and `\|outY\|` **0.70–0.99 → 0.086–0.125**. **The R41 azimuth mode goes with it:** `wobbleFreqHzAzErr` published 0.445–0.485 Hz at coherence 0.37–0.54 on the three diverging tags in R41 and publishes **nothing** in R42 (coherence −0.25…+0.07). Anomaly log: **143 `persistent-miss` on `AttackHelo1` in R41 → 0 in R42** |
| H7 | **There is nevertheless a DETERMINISTIC standing residual in the BLEND BAND, and it is the corpus's first above-floor rotorcraft pointing measurement.** It is not scatter and it is not the resolution floor. | R42 `AttackHelo1`, 6 scored tags × n=8, `railed = 0` on 274/274 R42 segments | `terminalOffDeg` **1.56 / 1.71 / 1.88 / 2.07 / 2.35°** at replicate CV **±1–3%** (`compare-runs.py`, n=8) — against `QuadVTOL1`'s ±97–197% CV, which is what the float grain looks like. Present on every tag with `heliBlend` ∈ [0.55, 0.75] and absent (0.02°, at the floor) on every tag with `heliBlend` ≥ 0.99. It **parks**: `off` flat at 1.5–1.7° for the last 19–25 s of a 30 s leg. Fails the card's own PASS criterion (`hoverrec*` under 1°) on 3 of 3 recovery legs. **MECHANISM IDENTIFIED 2026-08-02 — it is the GAME's `yawWeathervane`, not the mod's de-raters** (L15, corrected). The selector is `spd` crossing **40 m/s** = `yawWeathervaneMinSpeed`, *not* `heliBlend`: every converged tag sits below it (34.96 / 18.18 / 7.33 m/s), every parked tag at **41.5–41.8 m/s**, and the threshold lands inside the corpus's 35.0 → 41.5 gap. Closed form, citations and the 5/5 prediction are in the block under this table. **R44 (`rotor-weathervane-35`, `AttackHelo1`, n=5) reproduces it on a second card — 0.000/0.010° below 40 m/s vs 1.356/1.728/1.482° at 40.7–41.2 m/s, CV 0.6–1.2% — and eliminates `heliBlend` as the selector over [0.65, 0.75] (it is FLAT, and anti-correlated, across the 34 → 42 m/s bins where `|off|` moves 0.012 → 2.112). The discriminating 60 m/s arm produced ZERO scored segments; see the R44 paragraphs in the block below** |

> **H7's mechanism, closed-form and with NO free parameters — read this before proposing a fix.**
> `HeloFlyByWire` carries a weathervane that biases the yaw **rate error** by sideslip once the
> aircraft is moving: `yawWeathervaneStrength = 0.4` (`:36028`), `yawWeathervaneMinSpeed = 40`
> (`:36031`), `yawWeathervaneMaxSpeed = 60` (`:36034`), applied in `Filter` at `:36047-36052` as
> `vector2.y += 0.1 · beta_deg · 0.4 · clamp01((speed − 40)/20)`. The compensator behind it (`:36053`)
> is a **pure integrator**, so the equilibrium the mod's pedal has to live with is
> `achievedRate = commandedRate − 0.0032·beta` at 41.5 m/s. Predicting `beta` from the recorded `outY`
> and then `off` from `off_ss = omega_cmd / (YawGain·(kHelo/wMaxY + ki·iGate/leak))` — R42
> `AttackHelo1 rotor-bistab`, settled window, n=8 per tag:
>
> | tag | spd | outY | beta predicted | beta measured | off | off predicted |
> |---|---|---|---|---|---|---|
> | hoverstep5 | 34.96 | 0.002 | n/a (wv off) | — | 0.025 | 0.026 |
> | hoverrec12 | 41.80 | 0.087 | 24.2 | 24.0 | 1.578 | 1.573 |
> | hoverrec8 | 41.60 | 0.094 | 29.5 | 27.9 | 1.729 | 1.721 |
> | hoverstep12 | 41.50 | 0.103 | 34.4 | 33.1 | 1.921 | 1.913 |
> | hoverstep8 | 41.50 | 0.116 | 38.6 | 37.5 | 2.208 | 2.201 |
> | hoverrec5 | 41.52 | 0.128 | 42.0 | 40.0 | 2.525 | 2.506 |
>
> `beta` predicted within **0.8–5.7% on 5 of 5**; `off` reproduced to **0.4%**. The physical picture is
> not a wobble: achieved `|yawRate|` **0.001–0.002 °/s** and `headingRateFilt` **0.000** — a steady
> crabbed drift with the nose frozen **24–40° of sideslip** off the velocity vector, pedal fighting the
> weathervane to a draw. **`_iYaw` sits at 0.032–0.040, its LEAK equilibrium, not the 0.12 cap**
> (`ChaseController.cs:1551`, `leak = 0.5`): a leaky integrator has finite DC gain and structurally
> **cannot** reject a standing disturbance, so no amount of `iCap` fixes this.
>
> **Within-card, the threshold is visible on one pair:** `hoverstep5` (5° step, **35.0 m/s**) converges
> to **0.025°**; `hoverrec5` (5° return, **41.5 m/s**) parks at **2.53°**. Same card, same demand
> magnitude, opposite verdicts, and `yawWeathervaneMinSpeed = 40` lands inside the gap.
>
> **A ONE-LAW fix exists and is NOT YET SHIPPED — do not pick one of the two candidates from this
> corpus.** All three weathervane fields are on the same `heloFlyByWire` object `ResolveHelo` already
> Traverses (`ChaseController.cs:673-682`), so they are probeable, not constants. (i) Cancel the known
> bias in the helo yaw branch (`ChaseController.cs:1994-1996`), fail-soft to today's behaviour. (ii)
> Re-key `heliBlend` off the **probed weathervane band** instead of the absolute 60/20 — X31's
> structural complaint, answered with the same probe. **The corpus cannot choose**: every failing
> sample sits in a **0.3 m/s window (41.50–41.80) at the very bottom of a 20 m/s ramp**, where the two
> candidates are numerically indistinguishable. The discriminating experiment is in
> `LAW-CHARACTERIZATION.md` §7, rotorcraft item **(d)**.
>
> **R44 (2026-08-05) — THE RESIDUAL REPRODUCES ON A SECOND CARD AND `heliBlend` IS ELIMINATED AS THE
> SELECTOR, BUT THE TIE-BREAK IS STILL UNFLOWN.** `rotor-weathervane-60` — the arm that carries the
> whole discriminating prediction — wrote **2 captures totalling 76 and 27 rows (1.5 s and 0.5 s)**,
> both `replicate 1` / `arm = NULL` (X27's anchor), both with a NULL `stop` footer, and both
> containing **only their `arm` segment** (`excluded = 1`, no metrics by construction). **Scored
> segments: 0. There is no 60 m/s measurement of any kind — this is absent data, not weak data.**
> What the *control* arm delivered instead is a within-card threshold crossing on `AttackHelo1`
> (`rotor-weathervane-35`, thr 0.45, n=5 replicates): the two segments that stayed below 40 m/s
> (`fineWV35` 34.4 m/s, `az8wv35` 36.6 m/s, 0% of rows ≥ 40) terminate at **0.000° / 0.010°** — at
> the floor, as both models predict — while the three that drifted above it (`az8wv35rec` 41.0,
> `az12wv35` 40.7, `az12wv35rec` 41.2 m/s; 98.7 / 84.5 / 100% of rows ≥ 40) **park at 1.356 / 1.728 /
> 1.482°, replicate CV 0.6 / 1.2 / 1.0%**. That is H7's magnitude and its determinism reproduced on a
> different card with a different demand schedule. **And it breaks the confound R42 could not**: over
> `AttackHelo1`'s 12,908 non-arm rows binned at 2 m/s, mean `|off|` is **0.240 / 0.012 / 0.271** in the
> 34 / 36 / 38 bins and **1.571 / 2.112** in the 40 / 42 bins, while `heliBlend` stays **flat at
> 0.655–0.749 across the entire range** — and is *highest* (0.749) in the 38 bin where the error is
> 0.271°, *lower* (0.653–0.709) where it is 1.3–2.1°. Tag-for-tag: `az8wv35` `heliBlend` 0.706 →
> 0.010°, `az8wv35rec` `heliBlend` 0.653 → 1.356°. **`heliBlend` falls while the residual rises 7×;
> `spd` crossing 40 tracks it exactly. `heliBlend` is not the selector over [0.65, 0.75].** The
> `heliBlend = 0` endpoint is still untested, so the model is downgraded, not refuted.
>
> **THE CARD'S OWN 60 m/s PREDICTION IS WITHDRAWN AS STATED — DO NOT RE-FLY AGAINST IT UNCHANGED.**
> "Weathervane ⇒ ≥ 10–30° or the pedal rails at 60 m/s" assumes `beta` holds while `num2` ramps to
> 1.0. `beta` is not an input: it is the **equilibrium of the weathervane loop against the airframe's
> own directional stability**, so a saturated `num2` on an airframe whose fin actually works buys
> nothing. R44 shows this directly — `UtilityHelo1` ran away to **90–120 m/s** on the *35* card
> (`heliBlend` **0.0000** on all four scored tags, i.e. it accidentally flew the 60 arm's condition on
> the wrong airframe) and there the pedal **never rails** (`stickRailPctY` 0, max `|outY|` 0.50),
> `terminalOffDeg` is **0.212–0.279°**, and the bank channel is live (`tBankE` 0.86–1.61, `|bank|`
> 1.5–3.1° tracking `|targetBank|` 1.1–2.1°). A small residual at 60 m/s is therefore consistent with
> **both** models and decides nothing. **The re-flown 60 arm must score a reconstructed `beta` (from
> `outY` via the closed form above), not `off` alone.**
>
> The card's `armToggle: Control/IntegralStallGate` is **null on this residual** and should not be
> re-proposed as a lever: `terminalOffDeg` arm 0 vs arm 1 is 1.734/1.739, 1.481/1.473, 1.357/1.359,
> 0.010/0.009, 0.000/0.001 (n=2 per cell). It was never meant to choose between the models.

### 1.7 Attribution — what the levers are actually worth

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| A1 | **`MarkerRateFeedForward` is worth 48–75% of the standing azimuth error, measured OFF the bank rail — and it buys that with energy.** | R39-D (55–58%, but on the rail); **R41, 8 lanes, replicate 1 excluded** — `bankClampActivePct` **0.0 on all eight arm-0 lanes**, `blendRailPct` 0.0 | `terminalOffDeg` improves on **8 of 8 lanes by 48–75%**; `fixedWindowOffDeg` on 7 of 8. R39-D's headline confound is removed and the effect survives at the same magnitude. **The price is not a confound, it is the mechanism:** arm 1 demands 67% more turn rate (`turnRateDemandRatio` 0.205 → 0.343) and gives up **456 m of energy height** (+458 → +2 m). It also owns half the batch's `over-roll` anomalies (1,057 of 2,113) on 14% of captures, so `over-roll` counts cannot be compared across its arms |
| A2 | **The three `e1*` below-nose A/Bs are ALL NULL, and two of them are structurally incapable of being anything else.** | R41, replicate 1 excluded; `e1-below-suppress`, `e1-below-control`, `e1b-align-lead` | Every separation falls to **0.2–3%**, inside R39-D's 0.1–4.7% null contrast. **`e1b-align-lead` is a VALID test answering "inert":** `phiLead` max is *exactly* 0.00000 on arm 0 and 2.72–2.77° on arm 1, so the term fires — and `stickFlipRateR` is identical to three decimals on three of four segments. **`e1-below-suppress` is NOT valid, and R31 predicted it:** `bSup` moves 19% with the arm, but `bWt` — the roll blend weight it multiplies into — is **0.003–0.007** on the scored window, so the product is ~0. A null there is not evidence about `BelowAlignSuppress` |

### 1.8 The AoA path

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| N1 | **The AoA guard's switch-on point, expressed in each airframe's own ceiling, spans 0.529 → 0.739 — a 40% spread produced entirely by two absolute-degree clamps.** Provable from source before any data is consulted. | R39-E, 8 lanes; the quantitative model reproduces `gateMinUp` to ±0.024 on 7 of 8 | Under the unclamped proportional expression the onset would be a constant **0.7059 for every airframe**. **Not one of the eight ran the unclamped form** — it is live only for `alphaLimiter` ∈ [16, 24] and the roster is 10, 10, 10, 15, 15, 25, 27, 27. Counterfactual: remove both clamps and `EW1` and `Darkreach` stop being gated at all — they are gated *only* because `aoaFade` is floored at 4° |
| N2 | **THE LAW NEVER BACKS OFF. Not once.** Conditional on the up-gate being at least half shut, the raw pre-gate command is still commanding INTO the ceiling. | R40 `alpha-pullup`, 144 scored `alpha_hold` segments, 9 lanes × 8 replicates × 2 arms, **zero railed** | **5,113 of 5,128 gate-biting samples** — 100.0% on 12 of 13 exposed lane-arms, 97.1% on the thirteenth. Median `tgtPRaw` under a half-shut gate is **−0.79 to −0.97** (near full nose-up), *after* `qSched` had already cut demand to 0.30–0.59 |
| N3 | **The guard nevertheless holds: nothing crossed the ceiling on 144 of 144 segments** — and that is the guard working, not the card missing. | R40 `alpha-pullup` | `aoaAboveCeilingPct` = **0.00 on 144/144**; peak `aoaPeakOverCeiling` **0.9882** (Darkreach, fast arm) — 98.8% of the ceiling with the up-gate 97% shut for 100% of samples, and still no crossing. `aoaRecover` is identically zero below the ceiling (`ChaseController.cs:1280`), so the recovery bias and a sound guard are **mutually exclusive by construction** — "recovery never fired" is the expected outcome, not a failure |

### 1.9 R21 / gate-chatter — what the sustained turn actually showed

| # | Claim | Evidence | Effect size |
|---|---|---|---|
| S1 | **The `MaxBankAngle` clamp is a bystander, not the cause of the sustained-turn lag.** The roll servo gives the clamped bank target 2 % weight and flies +8.1° *past* it. | R21, 10 replicates, 4 802 pooled `turn360` samples | `eFine` weight = 1 − `blendWeight` = **0.020**; `eAlign` outweighs the bank path **34:1**; unclamping moves bank by ~0.1° |
| S2 | **`lateralHold` rails at 7.5° and drops the entire bank pipeline to exactly zero weight in a sustained turn.** | R21 (`blendWeight` **1.0000 on 100.0 %** of the settled `turn360`, n=1601); LOOP-AUDIT F2; GENERALITY-REVIEW finding 16 | `blendWeight` = 1 on 97.6 % of the whole segment, 83.6 % of `astern`, 63.4 % of `reversal` |
| S3 | **`_iPitch` is dead outside the 6° fine cone** — it is gated on error *magnitude*, so it is identically ~0 in a large standing error. | R21 (±0.001 against a 0.12 cap for a whole 30 s turn); R32 §5 reproduces it during a departure | v0.83's `IntegralStallGate` exists because of this |
| S4 | **Gate chatter is NOT the cause of the fine-aim complaint.** In the three segments that most resemble the complaint the risk ratio goes the *wrong way*, and real gates score no better than sham gates. | the gate-chatter investigation (`debugtests/gatechatter.py`, kept for reproduction), 11 complete `fixedwing-v2` captures, 231 blocks, Mantel-Haenszel + circular-shift null + 4 sham gates | `fine` RR **0.82** at the corpus's highest crossing rate (5.66/s); `micro` RR 0.88; `elDn` RR 1.01. Pooled median RR real **3.65** vs sham **3.16** |
| S5 | **`elDn` is a sustained roll limit cycle in the below-nose hemisphere, and the mirror step in the upper hemisphere converges.** | GATE-CHATTER §5a, 11 runs, late 60 % of the block | `elDn` mean `off` **6.92 ± 2.40°**, bank half-amplitude **43.3 ± 9.2°**, corr(\|azErr\|, `blendWeight`) **+0.918 ± 0.045**; `elUp` (a *larger* step) mean `off` **0.03°**, bank 0.11° |
| S6 | **The fine-cone regression scales with step size, not with gate activity.** | GATE-CHATTER §5b, 10 micro steps × 11 runs | r(REGRESSING %, \|step\|) **+0.785**; **partial** r(REGRESSING %, crossings/s \| off) **−0.632** |

---

## 2. PLAUSIBLE — measured once, confounded, or never isolated

| # | Claim | Why it is not established | What it currently rests on |
|---|---|---|---|
| L1 | **`aircraftGLimit` is the property that tracks the per-airframe spread.** | Collinear with mass / wing area / drag area at pairwise ρ 0.72–0.90; n=10 cannot separate them. "gLimit" is a label on a cluster. | R28 ρ +0.810 (n=8) → R29 **ρ +0.872, p = 0.0023** (n=10), strengthening while its confounders weakened (R29 §2.3). Still ordinal, not mechanistic |
| L2 | **`pEff` is the mechanism of the down-step penalty.** | Its within-card correlation with the residual **flips sign with step size** (+0.52…+0.84 at 2–2.5°, −0.76…−0.99 at ≥6°) — a correlate of demand magnitude. It is also in the **pitch** channel while the residual (D10) is azimuth. | R28 §3.2: 1.7–2.6× less peak pitch stick on the down leg than its mirror; R30 §6.2 rank corr −0.67…−0.93 |
| L3 | **#45 `schedFloor = 0.3f` is a genuine ONE-LAW violation that costs an airframe.** | Measured on **one airframe, one card, one batch**, and R32 itself says the railing is *downstream* of the precursor. `qSched` railing may be a symptom of the departure, not its cause. | R32 §6; GENERALITY-REVIEW finding 18. The *structural* argument (a hardcoded absolute floor on a schedule whose input is correctly relative) is strong independently of the flight data |
| L4 | **#21 (`lateralHold` rail) is what initiates the Darkreach precursor.** | Named as "the standing candidate" and never tested. `darkreach-05` carries no arm. **And the test scheduled for it (§4 Batch 4 row E4) CANNOT RETURN A RESULT as written — verified 2026-07-31.** | R32 §10 — an inference from "34–56° of `targetBank` is the roll-to-align channel", nothing more. **E4 blocker:** on the recs 01–31 "clean baseline" `blendRailPct` is **0.000 on all 124 segments** and `bankClampActivePct` 0.0, so the arm would suppress a channel already at zero weight — arm A ≡ arm B, and the null would read as "#21 is not the precursor". The half where the channel *is* live is the departed half: **20 of 32 captures / 74 of 126 segments RAILED**. No window in this card has the arm both mattering and measurable. Annotated in `LAW-CHARACTERIZATION.md` §4; **not redesigned** |
| L5 | **#23's placement transient is what makes the Darkreach cascade self-sustaining.** | Ordering only: a bad replicate precedes a bad placement precedes a bad replicate. No intervention has been run. | R32 §8; rec 51's first row `outP −0.800` against `off 0.42°`, departed by tSeg 5.967 inside the `arm` |
| L6 | **The per-replicate reset teleport can damage an airframe.** | **n = 1.** `detachedRatioAtStart = 0` on all 77 R33 captures including the one that aborted, so the part came off *during* rec 50; the four preceding replicates were gentle (max AoA 7.6°, max g 2.1) so in-flight loads are not a credible cause — but "during rec 50" is not "at the placement tick". | R33: `Darkreach` recs 10/20/30/40 complete, rec 50 `abort: airframe damage (detached ratio 0.029)`, all five with identical `# entry` (`v=165.8->95.0 snapBackM≈5071`) |
| L7 | **`predFloor = 0.30` is a real, distinct gate defect.** | The relative risk is huge because the baseline is tiny (in `az150`, 4.4 % near a crossing vs 0.16 % away, on a segment that regresses on 1.4 % of ticks overall). | Two independent analyses: GATE-CHATTER §5c (RR 6.5–36.1, p ≤ 0.01, beating every sham by 2–16×, surviving both skip controls) and R21 (binds on **100.0 %** of the settled window, holding `azErrPred/azErr` at exactly 0.300) |
| L8 | **The position effect in a card is energy accumulation.** | n=3 airframes. R30 deliberately lumps order + energy + `arm` attitude because they are all properties of card order. | R30 §4: `Multirole1` has both the largest early→late speed walk (+37.6 m/s) and the largest position effect (0.710); `Fighter1` the smallest of both (0.983) |
| L9 | **`FastBomber1`'s variable-geometry wing explains its 5–10× replicate sd.** | Plausible and unproven; within-card ρ(wingArea, A) = +0.02. | R29 §2.4(b): `wingAreaTotal` **100.2–135.1 m²** with `aeroPartCount` constant at 35 |
| L10 | **The law's problem at the heavy end is pitch authority running out**, not a gain. | `FastBomber1` was "the airframe where the law runs out of pitch" in R28 and then joined the band in R29 at a lower entry speed. | R28 §3.3 (`pEff` median 0.472, standing `elevErr` growing 0.61→3.47° through the card) vs R29 (`pEff` median 1.000, floor-branch occupancy 5.87 % → **0.00 %**) |
| L11 | **`trainer · oblique-12-c` is a card/airframe pair on which an AoA-gate A/B could return non-null.** | One cell, one batch, small activation. | R29 §4.3: `aoaLimiterActivePct` 11.9 % mean on **8 of 8 replicates**, every other healthy cell in the batch 0.0 % |
| L12 | **`_yawWeak` measures "the error did not close", not "the rudder is weak"** — and both its consumers move what it measures. | STRUCTURAL + closed-form; never A/B-ed. | LOOP-AUDIT F3 / GENERALITY-REVIEW 15: closed form on R21's settled turn gives `weakInst` 0.9945 against a recorded max **0.996**, on ticks where the FBW delivers **99.4 %** of commanded rate. `yawWeakFade` removes 57 % of the yaw command; `coordPull *= assist` gates the pitch term on rudder health |
| L13 | **v0.85 `AlignRateLead` makes the roll DERIVATIVE gain a function of `blendWeight`** — 1.00× at 0, **1.64× at 1** — i.e. of the `azErr` the roll loop itself produces. | STRUCTURAL, arithmetic only; the batch that would show it has not been flown, and D8 says `bWt` is 0 over the scored window of every card that has. | LOOP-AUDIT F4 / GENERALITY-REVIEW 17. Measured mean multiplier `turn360` 1.63, `elDn` 1.39. Always stabilising in sign, but it breaks the change's own premise that `RollDamping` is preserved |
| L15 | ~~**The H7 blend-band residual is K3's shape in rotorcraft form: BOTH turn channels are de-rated at once, and neither hands over**~~ — **THE YAW HALF IS RETRACTED, 2026-08-02, and the bank half is refuted by L15's own batch.** (a) `yawWeakFade` and `HeliYawScale` are **BYPASSED** whenever `_collective && _heloOk` — `ChaseController.cs:1994-1996` takes the normalized-rate branch instead — and `heloOk = 1.00` on **every R42 row**, so the yaw de-rater L15 named was never in the loop. (b) `hoverstep5` carries `heliBlend = 0.985`, the **most** de-rated bank channel in the batch (98.5% of `tBankE` removed), and it is the tag that **converges to 0.025°**: maximum de-rating, best convergence. **REPLACED BY:** the residual is the GAME's `yawWeathervane` sideslip bias, which the mod's **leaky** yaw integrator has finite DC gain against — closed form and the 5/5 prediction table under the §1.6 table; H7. | **Single batch, no intervention.** The replacement mechanism is a closed-form derivation from the decompile matched against R42, not an A/B: nothing has been held or disabled in flight, and every failing sample sits inside a **0.3 m/s window** at the bottom of the weathervane's 20 m/s ramp, so the two candidate fixes are numerically indistinguishable on this data. | R42 `AttackHelo1` `rotor-bistab`, settled window, n=8 per tag. The *observations* that carried the old reading are unchanged and still correct as observations: on the five non-converging tags `_yawWeak` is **0.89–1.00**, `yawEff` (= low-passed \|yawRate\|/\|outY\|, `ChaseController.cs:1054`) **0.009–0.053**, `\|outY\|` 0.086–0.125, `\|yawRate\|` **0.001–0.002 °/s**; on the two converging tags `_yawWeak` **0.000–0.002**, `yawEff` 0.33–0.64. What changed is the reading: with the yaw branch bypassing `yawWeakFade`, a saturated `_yawWeak` is **pure readout** — L12's tautology with nothing downstream of it — and `\|yawRate\| ≈ 0` against 24–40° of standing sideslip is the weathervane holding the nose, not the mod de-rating itself. `_yawWeak`'s normaliser is still the absolute `Clamp01(closeRate / 6f)` (`:1062`), `GENERALITY-REVIEW.md` finding 15, and that finding is untouched by this correction |
| L14 | **`_pitchEff` × `_alphaSchedFilt` are two de-raters of ONE physical event, multiplied to 0.09** where each is documented as flooring at 0.30. | Called "unfalsifiable on a corpus where `aoaLimiterActivePct` is 0" — **which X7 shows is wrong.** It is falsifiable today on R27's `turn360loq` (railed, so read with care) and on R33's Darkreach legs (unrailed). | LOOP-AUDIT F6 / GENERALITY-REVIEW 17 |

---

## 3. REFUTED / RETRACTED — believed, then disproved

**This is the most valuable section in the file.** Each line is a claim that was written down, acted
on, and turned out to be wrong.

| # | The claim that was believed | What killed it | Where it still lives (fix or annotate) |
|---|---|---|---|
| X1 | *"No mod-side G-limiter — the game's stability control governs."* | `GLimiter` is dead code: one occurrence in 181 878 lines, `LimitG` zero call sites. **THE GAME HAS NO G GOVERNOR.** | Corrected in CLAUDE.md Conventions (v0.96), R32 §1.1 |
| X2 | *"The law is bending airframes."* Stated to the maintainer. | Over-G damages the pilot only (`Pilot.TakeGForceDamage :85989`, one part index). No structural-G path exists. `aeroPartCount` 35 on all 63 R32 captures. | Retracted explicitly in R32 §2. **"The law bent an airframe" is not a possible diagnosis.** |
| X3 | v0.88's **aoaTrim theory** — that writing the placement velocity at AoA = 0 caused the entry thump. | Gate B / R23: run 01 is the run's *first* placement, so it was written **untrimmed** — the exact condition v0.88 blamed — and it has the **cleanest entry of the four** (AoA 0.07→1.46° with no overshoot, `off` peak 0.59° vs 1.72–2.87° on the three trimmed ones). | Reverted in v0.89. Gate B (R23) finding 1 — see I2/I3 |
| X4 | Gate A: *"`iPitch`/`iYaw` read 0.0000 on every first row, so `ctrlReset` does what it claims."* | R21 measured `_iPitch` at ±0.001 for an entire 30 s turn — it is ~0 coming out of a turn **whether or not anything reset it**. The observation stands; the inference does not. | Retracted against Gate A; the observation stands, the inference does not (see I1) |
| X5 | #20: *"the `PEffRevThresh` floor branch is unreachable, so `_pitchEff` never goes below 0.15."* | True only of the **self-probe path**. Corpus-wide, 28 209 rows (4.50 % of 627 110) sit below the threshold, min 0.000, on two fixed-wing airframes — genuine reversed-plant measurements where the no-floor branch is *correct*. | Premise corrected v0.96; re-scoped from experiment to hygiene (LAW-CHARACTERIZATION §7 #20). The old "5.2 % / 8 captures / three airframes" figure **reproduces against no batch** |
| X6 | *"The oblique family is where #20 and #21 get A/B-ed"* (LAW-CHARACTERIZATION §4 Batch 4). | R28: #20's floor branch runs on **0.00 %** of rows on 5 of 8 airframes; #21's rail on **0 of 1344** healthy segments. R29: #20 on **0.00 % of all 10 airframes**, #21 on **0 of 1740**. | Both deprioritized in R28/R29 ranked fix lists; still listed as E4/E5-adjacent in the plan |
| X7 | *"`aoaLimiterActivePct` is 0 in every capture ever taken."* | **FALSE at corpus scale, and this is a new finding.** `SELECT run_tag, airframe, tag, avg(aoaLimiterActivePct) … WHERE aoaLimiterActivePct > 0` returns R26 `trainer·turn360` **99.2 %** and `FastBomber1·turn360` 86.7 %; R27 `turn360loq` **78.7–97.7 % on four airframes**; R11/R13/R18 azimuth steps 20–56 %. **66 (run, airframe, tag) cells in total.** **The unrailed count is 23 or 32 depending on the question, and both are right** (resolved 2026-07-31): `WHERE railed = 0 GROUP BY (run, airframe, tag)` keeps the unrailed segments *of a partly-railed cell* → **34**, or **32** excluding the two legacy no-sidecar `unsegmented` cells (R1, R2) — that is where the 32 came from. `HAVING max(railed) = 0` demands the whole cell be clean → **23**. **Cite 23 for "can an A/B run here"** (a cell whose sibling replicates railed is not a comparison group), 32 for "how much unrailed evidence exists". Partial rescue: the loudest cells (the R26/R27 `turn360` family, 79–99 %) are all RAILED — bank clamp 79–97 %, `authorityUsedFrac` 0.95–1.08 — so they are *no signal*, and they are in the 32 but not the 23. The weaker form, "…never fired in an UNSATURATED capture", is **also false**: R29 `trainer·obUL12` 11.9 % on 8/8 replicates (L11) and R33 `Darkreach·obDR6` **100 % on 4 unrailed segments** (O4). Mechanism, so it reproduces: v0.96's #41 fix dropped that lane's entry 171 → **95 m/s**, so low q — not load — reached the ceiling. | **FIXED 2026-07-31 in all five named sites plus four more the audit missed**: `LAW-CHARACTERIZATION.md` §1 (rewritten wholesale) + §4 Batch 3, `GENERALITY-REVIEW.md` finding 17, the loop-audit write-up (4 sites), the R28 batch doc, `debugtests/scorecard.py` `alpha_metrics` docstring — **and** `INSTRUCTOR-LOOP.md` §3 (the ORIGIN the scorecard comment cited), `cards/README.md`. Those batch docs have since been consolidated away; the correction survives here and in the standing docs named |
| X8 | R21/LAW-CHARACTERIZATION: *"the bank clamp is what holds the 9.4° sustained-turn lag."* | The clamp is active on 97 % of the turn and discards ~10° of demand — and the roll servo gives that target **2 % weight** and flies **+8.1° past it**. Raising `MaxBankAngle` would move bank by ~0.1°. | R21 §Q1 "The causal half: REFUTED" |
| X9 | INSTRUCTOR-LOOP §5: *"independent hysteresis-free gates chatter and that is the cross-fighting the maintainer feels."* | Killed where proposed: RR 0.82 / 0.88 / 1.01 in the three most relevant segments, real gates indistinguishable from sham gates. The prescription "fewer gates, with hysteresis" is **not supported and should not be spent on**. | GATE-CHATTER verdict; `gatechatter.py` kept for reproduction only |
| X10 | R28 §3.2: *"`bSup` reads 0.000–0.06, so belowness is excluded as the mechanism."* | **That was a median.** The mean is 6× asymmetric (0.240/0.293 down vs 0.045/0.041 up on R28's own captures). | R30 §6 re-measured R28's data and reinstated `bSup` as a lead — which R31 then killed on different grounds (D8) |
| X11 | R28 §4.3, and the Gate B record: *"#23 does not reproduce and is confirmed harmless."* | R28 measured only the **lower mode of a bimodal distribution**. R32's upper mode: \|`outP`\| rails at 1.000 on 15 of 58 placement ticks. | Scope corrected in LAW-CHARACTERIZATION §7 #23; the bimodality is K4. "Harmless to results so far" is **retired** |
| X12 | *"`arm=0` on `BelowAlignSuppress` disables the suppression."* A whole batch was commissioned on it. | `ChaseController.cs:2048–2050` is a ternary between two **forms**, not on/off. `arm=0` is the v0.67 body-frame form. Mean `bSup` on a down leg is 0.145–0.404 on arm 0, not 0. **The true "off" arm has never been flown.** | R31 §1. Action item still open: rename the knob or make the `false` branch zero |
| X13 | R28's headline *"1.2–17.9× the terminal error"* as a property of the law. | The **sign** is robust; the **size** is not. Geomean over 26 matched cells R28 3.49 → R29 2.37, with individual cells moving up to 7× in either direction and two airframes flipping sign. | R29 §3.2 |
| X14 | R28's *"treat any non-zero count of 33.3 ms rows as the stop signal for going wider."* | **Zero rows at 33.3 ms, and zero rows anywhere in [30, 40] ms** — the frame time does not quantise to 2× vsync on this machine. A rule that provably cannot fire is not evidence when it does not fire. | Retired in R29 §5.3; replace with a rate |
| X15 | *"`FastBomber1` is a failure airframe."* | R29: 0.559 → 0.662, joined the band, `pEff` median 0.472 → 1.000. But R30/R31 then showed its **replicate CV is 30–43 %** and its two lanes disagree by 3.8×, so it is not a *failure* — it is **unusable as a measurement**. R33 confirms: mean cell CV **74 %**, against 4–17 % on every other airframe. | Keep it as a stressor; **do not quote its ratios** (R30 §7.5, R31 §8) |
| X16 | LAW-CHARACTERIZATION §1: *"19 cards, ONE has ever been flown, on ONE airframe, and it is saturated."* | Badly stale. **24 cards flown, 11 airframes, 1 681 captures, 27 batches**; R30/R31/R33 have **zero railed segments** between them. | §1 of the standing plan reads as if R26–R33 never happened; it is the first thing a new agent reads |
| X17 | *"The Darkreach is the only airframe with `flightAssist = 0`."* | `EW1` has it too and scores mid-band. | Corrected inside R32 §7 before it could propagate |
| X18 | *"R29's 26.9 g means the airframe was overstressed."* | It is a **readout** of a departed airframe at 80 m/s and −87° AoA, falling. It damaged the pilot, nothing else. | R32 §2/§9. Consequence: **do NOT add a mod-side G-limiter** — it protects nothing, deletes the most legible failure signal, and would be a sixth de-authorizing term on a law whose defect is that it already has five |
| X19 | *"`oblique-6-dwell` scores a property of the airframe."* All 314 captures of it were read that way. | The card's four legs are **four flight conditions** and no throttle makes them one: drift 0.96–2.14×, and the descent is throttle-independent at Pearson **+0.997**. The demand is elevation-symmetric, but "nose on the marker" at positive AoA is a flight path angle of −α, so every lane loses 221–1194 m on **both** arms. | R39-B. **Retired as a between-airframe ranking instrument.** Still valid as a *within-lane, within-airframe* A/B (arms reproduce to ±0.01). Its down/up asymmetry is *partly* a slow/fast asymmetry — up legs run 1.01–1.61× the speed of down legs |
| X20 | R37 §4: *"the `oblique-6-dwell` drift is ordered by thrust-to-weight."* | Spearman(T/W, drift) = **+0.14 / −0.36 / −0.02** across three throttle arms. Darkreach has the *lowest* T/W in the fleet (0.348) and the *largest* drift on all three. | R39-B §3. The predictor that does hold on all three arms is **Vmax/Vcorner** — speed headroom above the pinned entry — at +0.84 / +0.83 / +0.68 (n=10) |
| X21 | *"`wobbleEpisodes*` counts oscillation modes."* Six signals, the whole corpus, and four documents rested on it. | **42 of 42** episodes in R35/R36/R37/R39 start at `tSeg` 1.9–2.6 s and end by 17.4 s: they are the **entry transient**. Worse, the reproduced-to-three-digits value 0.319–0.328 is `3/(2 × 4.6 s)` = **the detector's own floor**. Corpus episodes 318 → **5** after repair. | R39-C, R40 metric repair. Corollary, and it inverts a published claim: *"`obDR6` is the leg where the mode is ABSENT"* is **wrong** — settled \|azErr\| never reached the 0.5° dead-band. That is **amplitude censoring, not suppression**. Use `wobbleCoherence*`, which publishes whenever a window exists |
| X22 | *"`RelativeTurnLead` is a live lever worth sweeping."* A card and a knob existed for it. | R39-D spent its A/B: it moved the standing error **0.2–3.8%** against a null contrast of 0.1–4.7%. | Knob, branch and card **deleted in v0.99.1**. The lead stays relative; the *lever* is gone. Precedent for retiring a spent lever outright |
| X23 | *"`alpha-sweep` measures the alpha regime"*, and *"low q is the route into it."* | **All 60 segments RAILED** — on bank clamp (74–97%), turn-rate cap (85–97%) and blend rail (81–96%), **never on AoA** — and `aoaAboveCeilingPct` = 0.0 on 60 of 60. The card physically cannot get there: an azimuth demand loads the wing only through bank, clamped at 72° ⇒ **n = 3.24** against the 4.8–24 g its lanes needed. | R39-E. Card **retired**, replaced by `alpha-pullup` (`cards/ALPHA-CARD-REDESIGN.md`). The withdrawn inference matters as much as the card: the difference is **demand shape, not q** |
| X24 | *"`dmgFrac` reports per-row damage"*, and *"a detach ratio of 0.114 is four detach events."* | `dmgFrac` is **structurally incapable** of ever being nonzero on a capture the harness aborts — the row is written *after* the abort check. 641,555 rows, **zero** nonzero, against 8 known damage aborts. And `UnitPart.Detach` cascades `onParentDetached` down the subtree, so the ratio is **subtree size**: 0.029 = a leaf, 0.057 = one child, 0.114 = three descendants. | R39-F, R40 metric repair. `WHERE dmgFrac = 0` selects **everything** — see `CAPTURES-DB.md` gotcha 9b. The real damage signal is the abort itself plus the sidecar's `detachedRatioAtStart`. The graveyard's "sweep latency" reading of 0.114 is wrong |
| X25 | *"R39's `stol-*` batch is STOL data."* 53 captures, ONE-LAW standing case 3. | The card declared 90 m/s but throttle was unpinned at 1.00: the jets were at 144–147 m/s by the end of the 6 s `arm` and **340–381 m/s (2.1–2.4× corner)** by the last scored segment — *faster than anything else in R39*. It is a second **high-q** dataset. | R39-stol. Cards now pin `ScenarioThrottle = 0.25`; R41 holds 85–178 m/s. **Consequence:** R41's 10–70× `elDn40` "improvement" over R39 is a **card fix, not a law change** — the two are different flight conditions, not an A/B |
| X26 | *"The Darkreach damage failure fires at placement 4–5"* (ledger #51), as a rule the R40 cards would rank. | It did not occur at all: Darkreach completed **32 placements across three cards with zero damage aborts**, including an 8-replicate card at a 6.7 km snapback that is the near-twin of the R39 lane that died at replicate 5. | R40-place. #51 stays **OPEN and instrumented**, not fixed and not reproduced on demand; R40 points at a third variable neither card controls (`origDist`). *Also retracted in the same batch: the "48 captures against 24 expected — exactly 2×" premise was a filename glob counting each capture's sidecar* |
| X27 | *"Replicate 1 is a normal replicate."* Every ABBA batch ever flown assumed it. | `ArmOf` made index 0 **always arm 0** (32 of 32 captures across four R41 cards), and replicate 1 is the placement that **captures** the run anchor — so it snaps back 0 m by construction and flies from the spawn (`v = 250→250`, `alt = 6000`) while every sibling arrives teleported (`snapBackM ≈ 11,190`, `v ≈ 352→250`, `alt ≈ 2,180`). **Arm 0 was systematically handed a different flight condition.** | R41 §2 — caught it converting a null into an apparent **30% win** (`FastBomber1 · e1-below-suppress`: 1.572 vs 0.261 pooled, **0.223 vs 0.226** with the stratum dropped). Fixed both sides: `compare-runs.py._anchor_replicate_filter` drops an arm-unbalanced `snapBackM = 0` stratum and warns (fed by `scorecard.provenance` parsing `snapBackM` off the `# entry` line, which nothing read before); v1.0.1 makes replicate 0 a **warm-up armed as neither** — `ArmOf` is `replicateIndex == 0 ? -1 : (replicateIndex >> 1) & 1`, `ApplyArm` assigns nothing at `_armIdx < 0`, and the capture self-labels `arm=-1`, which `compare-runs.py` files as a third group no A/B reaches. SQL filter for direct work: **`entry_snapBackM <> 0`**. *An absent `snapBackM` is unknown, never 0.* **Why EXCLUDE rather than balance — the alternative is arithmetically unavailable, not merely worse:** the stratum is exactly **one** replicate per lane per run, and the lane *is* the unit of analysis (`compare-runs.py` groups by (airframe, card, arm) and refuses to pool across airframes). One capture cannot be split across two arms; alternating it across lanes or runs balances a pool nothing ever compares and leaves the per-lane A/B exactly as confounded. **The cost, because it lands on cards:** it is `repeat − 1` that must divide by 4, so cards want **4k+1** (5, 9, 13) and `repeat: 8` scores seven and warns UNBALANCED — shipped cards were deliberately left to warn rather than be silently changed in the same commit. `test-arm-schedule.py` asserts the **stratum property** ("no scored arm may ever contain replicate 0") rather than the pattern, and carries the pre-v1.0.1 form as a failing counterfactual. **v1.0.2 — CORRECTION TO THE v1.0.1 FIX: "replicate 0" was the INSTANCE, not the property, and a second instance arrived one release later.** Lane respawn (same release) resumes a dead lane's queue on **fresh metal**, and `StartSuite` sets `_anchorSet = false` on every suite — so the resumed replicate's placement *captures* the anchor exactly as replicate 0's does (`snapBackM ≈ 0`, flying from the spawn state, `# entry` reading `v = laneSpeed→laneSpeed`) while sitting at replicate 3 of 9, where an index test cannot see it. Merging the two features as written would have **scored** it: #55b restored on a different path, in the same release that closed it. The property is therefore restated index-free — **no SCORED replicate may be ANCHOR-CAPTURING** — and enforced by `ArmOfRun(replicateIndex, resumeReplicate) => replicateIndex == resumeReplicate ? -1 : ArmOf(replicateIndex)`, read by `ApplyArm` **and tallied by `SetUpArmSchedule`**, with `resumeReplicate == 0` making a never-respawned lane `ArmOf` **verbatim** (asserted: `ArmOfRun(i, 0) == ArmOf(i)` for i in 0..63). **Cost, stated because it is a real one:** a respawn spends **one more scored replicate** — a 9-replicate lane that dies twice scores 6 of 8 rather than 8 of 8, against R41's actual alternative of **0** (5 `Darkreach` + 1 `EW1` lanes lost outright, #51). It is never silent: the suite-start line names the resumed replicate as a warm-up, the schedule prints a second `.`, and the existing UNBALANCED warning fires off a tally computed through the same function. **REJECTED, and why.** *(a) Score it and let `_anchor_replicate_filter` catch it.* The filter only drops when the stratum is arm-**unbalanced** (equal counts return the pools untouched, by design, so `place-noop` cards survive) — a single respawn is exactly the balanced-looking case it declines to act on. It is also the wrong layer: the confound would be in the artifact, and every reader that is not `compare-runs.py` (`index-captures.py` SQL, `scorecard.py`, a hand analysis) pools it. The filter stays a **backstop** for the paths no arm reaches — an ungated card, a hand-flown capture, the pre-v1.0.1 corpus. *(b) Carry the lost aircraft's anchor to the replacement, so the resumed replicate snaps back like a sibling.* The tempting one, and it is **arithmetically empty**: `LaunchLane(slot, …)` is deterministic in `slot` (same azimuth, ring radius, deck offset and `SpawnAlt`), so the replacement spawns at the point the lost aircraft anchored on — which was its own spawn point. `snapBackM` reads ~0 either way. The distinguishing condition is not *where* the anchor is but that the placement is a **no-op on freshly spawned metal** (R41: `v = 250→250 alt = 6000→6000`) against an 11,190 m teleport with a ~100 m/s deceleration. Transferring it buys nothing measurable and adds cross-aircraft state. *(c) Fly the resumed replicate twice — once as a warm-up, then scored — or append one replicate to the queue.* Recovers the full scored count and breaks the respawn's own invariant: two captures share one replicate index (which `index-captures.py` keys on), the queue outgrows the card's declared `repeat` so `_block`/`reps` and the printed schedule stop describing the run, and each retry costs a full card duration against a cap of 2. One replicate is not worth the queue invariant. *(d) Restart the lane at 0.* Already rejected by the respawn design for re-flying captured replicates, and it does not even solve this: a restarted lane's own replicate 0 is anchor-capturing too. *(e) Key the warm-up off `!_anchorSet` inside `ApplyArm` rather than off an index.* Physically the exact invariant, and the call order permits it (`ApplyArm` runs before `PlaceOnCondition`). Fatal anyway: with `ScenarioForceEntry` off, or a card declaring no entry speed, **no placement ever runs**, so `_anchorSet` stays false forever and every replicate becomes a warm-up — silently disarming every ungated card. It is also not a pure function, so no extract-and-compile test could hold it. **Also corrected:** the respawn branch's own test asserted "the same complete, balanced arm sequence an undamaged lane would have" — written against v1.0.0's `ArmOf` and false under v1.0.1's, before the respawn was even considered. Replaced by the two properties that survive: every queue index re-flown, and no scored row is the first placement of its suite |
| X29 | *"`AttackHelo1` diverges — the rotorcraft law fails on a plain helicopter."* R41's headline rotorcraft result: a monotone forward-speed runaway to 51 m/s with the bank railed at 72° and \|azErr\| growing to 34.1°. It was written into H4, into `GENERALITY-REVIEW.md` finding 6 and into §7's Tier 3. | **A stale live config, not the law.** `com.no.wtmouseaim.cfg` held the v0.43 pair 150 / 40.28 against the shipped 60 / 20, which makes `speedRamp ≡ 1.0` for every speed the card reaches and so deletes the bank channel outright. R42 re-flew the identical cards at 60/20: `fixedWindowOffDeg` **−85…−95%**, pedal rail 66.1% → 0.0%, 143 `persistent-miss` → **0** (H6). **A second, smaller correction rides along: "bank railed at 72°" was the TARGET.** In R41 `bankTR` reached 79.9° while the delivered `bank` never exceeded **3.3°** and averaged 0.02–0.88° — the aircraft was not banking at all, which is the *evidence* for the deleted channel and the opposite of what "railed bank" reads as. | Corrected here and in H4/H2; `GENERALITY-REVIEW.md` finding 6; `LAW-CHARACTERIZATION.md` §7 Tier 3. **The residue is real and is now H7** — convergence is not the same as closure |
| X30 | *"The tiltwing blend sign is NOT inverted"* (H3), and the standing instruction that **the `ponytail:` flip at `ChaseController.cs:1128` must not be actioned.** | **R42 isolates `tiltFrac` for the first time and it runs the wrong way.** At `HeliForwardSpeed = 60` the whole `rotor-transition` leg flies above it (min `vFwd` ≥ 60 m/s over 8 captures × 8,960 rows), so `speedRamp ≡ 0.000` and `heliBlend` **is** `tiltFrac` — exactly the readout O12 predicted. Measured, by 10 m/s bin: **0.620 (100–110) → 0.535 (90–100) → 0.377 (80–90) → 0.224 (70–80) → 0.182 (60–70)**. Time runs high-speed → low-speed, so `tiltFrac` **falls** across that binned range as the aircraft decelerates toward the hover, where a correctly-oriented `tiltFrac` (1 = hover) must rise. **CORRECTED 2026-08-02 — "monotone fall" IS ONLY THE TAIL, and the inference drawn from it was wrong.** The full series over the capture is **0.179 @ 139.9 m/s (spawn) → 0.603 @ 101 m/s → 0.181 @ 70 m/s**: a rise then a fall, i.e. a **rate-limited chase off the spawn state**, not a property of the airframe. The 0.62 peak is a transient; the settled plateau is **0.181–0.184 over 80% of rows at 68–78 m/s**, which is the game's own hover pin of **0.18** (`:70352`) to within **0.002**. Two consequences: the tilt term is not "backwards over the whole flight", and **the card confounds speed with throttle** — the tilt command is a function of throttle as well as speed (`:70342-70343`) and `rotor-transition` pins `ScenarioThrottle = 0.25`. Cross-checked against R41's own data: R41's `heliBlend` − speedRamp residual is +0.002 in the three slow bins and **+0.190 at 100–110**, i.e. `tiltFrac ≈ 0.619` there — agreeing with R42's 0.620 to **0.001**. The signal was in R41 all along, under the `max()`. | **Still do NOT action the `ponytail:` flip — but the reason given here was ALSO wrong and is withdrawn.** This cell used to argue *"neither orientation lands on 0, so the defect may be the limits `(lo, hi)`, and a flip leaves an 0.38 offset."* Both halves die with the transient: the plateau **is** the endpoint, at the game's 0.18 pin. `GetAngleLimits()` is innocent and `(lo, hi)` are correct — **the defect is that the tiltwing branch is missing the `1f −` its nozzle twin has, and that the hover end of the game's tilt command is 0.18, not 0.** Mechanism, citations and the corrected one-line fix: **O13**. `tiltwing=1` is confirmed in the log, so the term did resolve |
| X31 | §7 Tier 3 / `GENERALITY-REVIEW.md` finding 6: *"`AttackHelo1` can never leave the hover regime at any speed it is capable of flying — Vmax 100 m/s gives a lowest reachable `heliBlend` of 0.455."* | **Arithmetic done against the stale config.** 0.455 = (150 − 100)/110. At the **shipped** 60/20, `(60 − 100)/40` clamps to **0**, so `AttackHelo1` reaches full fixed-wing behaviour at any `vFwd ≥ 60` m/s. R42 measured it down to **0.55** at 38 m/s, matching `(60 − 38)/40 = 0.55` exactly. | The *structural* half of finding 6 stands untouched — these are still absolute m/s constants and a heavy compound heli still blends identically to a light scout. Only the "cannot leave the regime" consequence is withdrawn |
| X28 | *"A field capture from a user is readable."* | **Every numeric the mod wrote was formatted in the ambient culture.** On a comma-decimal locale (ro/de/fr/es/…) the recorder wrote `0,22` into a comma-**delimited** CSV and destroyed its own file — a posted capture parsed as **0 rows, 1652/1652 dropped**. The `# config` and `# fbw` headers and every `[anomaly]` line had it too, so a locale user could not hand in a usable artifact at all. | Discord v0.68 bundle §1. **FIXED v1.0.1** via `WTMouseAimPlugin.Inv(...)` at every write site — deliberately *not* `DefaultThreadCurrentCulture`, which would restyle the game's own HUD for every non-English player. Repair of the posted files was exact (85 tokens/row re-paired against the known per-column decimal count; 2501/2501 rows recovered), which is what validated the diagnosis |
| X32 | *"`DroneAltDeckM` makes altitude a balanced experimental factor crossed with airframe."* — `TestDrone.cs`'s own design comment, `cards/hs-hold.json`'s q-contrast rationale, and the harness's `[drone] … the decks are at 2500 and 5500 m and no lane flies 4000` log line. | **The deck never reaches the flight.** It sets the drone's **spawn/loiter** altitude; the card's placement then teleports every lane to its declared `startAlt`. Across R41 + R42 + R43, `entry_alt_to` has exactly **ONE distinct value per card** — always that card's own `startAlt` — while `entry_alt_from` spans the deck (R43: 2500 / 5500 / 2500 m → **4000 m on all 15 captures**). R43's own rows fly 4000 m descending to ~3570 m in **every** lane, so `hs-hold`'s declared "2500 vs 5500 m, 59.5 vs 43.5 kPa, crossed with airframe on the Latin-square diagonal" factor **does not exist in the data**. The log line is stating the intent, not the outcome. | **Blast radius is exactly one card.** Every other shipped card declares `DroneAltDeckM = 0` and its note already says *spawn* altitude, so no prior finding rests on this. The deck remains valid for what `RingRadius` needs it for — lane packing. Either make placement honour the lane deck or stop advertising altitude as a factor: `LAW-CHARACTERIZATION.md` §7 Tier 1 **(h)**. Until then **the only q lever a card has is speed**, and on `hs-hold` q is confounded with airframe |
| X33 | *"Placement above ~400 m/s destroys an aircraft."* R43's bracket — `FastBomber1` placed at 440 m/s died 3/3 while `Fighter1`/`Multirole1`/`SmallFighter1` placed at 341–352 m/s all flew — written into all seven `cards/place-*.json` notes as the ladder's premise. | **R44's ladder refutes it at both ends.** `place-440-noteleport` — identical to `place-440` except `DroneAltDeckM: 0` — placed `FastBomber1` at **440 m/s and wrote 10 of 10 full captures** (2 lanes × 5 replicates, 736–737 rows, the full 46 s). `place-300` placed the same airframe at **300 m/s and killed 3 of 3**, and R40 had already killed one at **230 m/s**. Every rung between (375/390/400/420/440) is 100% fatal for `FastBomber1` and 100% clean for the other three keys **on the same rung**, so the single-airframe rungs (400/420/440) measure the airframe, never the speed. What R43 actually varied was `DroneAltDeckM` — the bracket was airframe × deck, read as speed. | The premise is dead; the mechanism is `I12`. Every `place-*` note carries a corrected verdict. **The ladder is not worth re-flying**: the isolator (`place-440-noteleport`) already answered it in 10 captures. The high-speed band is reachable today — pin `DroneAltDeckM: 0` on any card that flies `FastBomber1` |
| X34 | *"The unnormalised roll constants (`RollGain` / `RollDamping` / `RollRateSmoothing`) produce a measurable q-dependent roll response"* — `GENERALITY-REVIEW.md` finding 5's predicted **consequence**; and the instrument that appeared to confirm it, *"`outR` sd measures roll activity in the fine regime"* (R43, Spearman(q, `outR` sd) **+0.891** within `Multirole1`). | **The first crossed q design in the corpus, and it says no on both counts.** `q-hi-300` / `q-lo-300` (R44, v1.0.3): identical roster, geometry, 300 m/s entry and 0.45 throttle pin; **the only declared difference is `startAlt`, 2500 vs 8000 m** — the design X32 forced after the deck mechanism was killed. **The lever is real and clean:** measured q (from `airDensity` × `spd`², settled tails) **37.0–46.0 kPa** high vs **25.0–32.0 kPa** low, a **1.44–1.52×** ratio *per airframe* with **zero within-airframe overlap** (below the designed 1.82× only because the thin-air lanes accelerated off the pin — 300–331 m/s vs 277–303). **The card's own CONFIRM criterion — `outR` sd higher at 2500 m for EVERY airframe — fails: 2 of 4** (`Fighter1` +35%, `SmallFighter1` +4%; `FastBomber1` −22%, `Multirole1` −34%). Pooled it leans the **wrong** way: 0.00119 hi vs 0.00134 lo, ratio **0.884**, d = −0.36, permutation **p = 0.158** (n = 32 / 32 segment-tails, replicate 1 excluded per X27). The card's designated second signal `stickFlipRateR` is **identical to four decimals, 0.0564 both cards**. The better-resolved physical proxy `rollRate` sd moves *against* finding 5 and is the only one that reaches significance — 0.00435 hi vs 0.00571 lo, ratio 0.761, **p = 0.039** — which is what aerodynamic roll damping ∝ q predicts, not what an unnormalised gain does. On the valid pointing metrics: `fixedWindowOffDeg` 0.160 vs 0.133 (ratio 1.21, p = 0.054, 3 of 4 airframes), `rmsPointingErrorDeg` 0.3046 vs 0.3032 (**ratio 1.005, p = 0.905**). | **Two separable retractions.** (1) **R43's +0.891 is withdrawn as a quantisation artifact, not merely as a within-airframe result:** `outR` prints `{0.000}` (`Recording.cs:590`), and in these tails it occupies **14–32 distinct codes** with ~34% of samples exactly 0.000, so a per-cell sd of 0.0010–0.0021 is **one to two print quanta** and R43's own 0.0007 low end is *sub*-quantum. `debugtests/CAPTURES-DB.md` gotcha 18. (2) **Finding 5's structural claim STANDS and is unaffected** — the three constants really are global and really are unnormalised by q, and `qSched`, the law's only dynamic-pressure term, is **railed at exactly 1.000 on every row of both cards** (it is a *low*-q pitch schedule, `Mathf.Clamp(qRatio, 0.3f, 1f)` at `ChaseController.cs:1174`, and both cards fly above corner), so the roll channel has no q term anywhere in this range. What dies is the *consequence*: over a 1.5× q lever crossed with four airframes, that structure produces **no detectable difference in roll behaviour at fine-tracking amplitudes.** Finding 5 keeps its O11 field evidence, which is a **~1.15 peak-to-peak** limit cycle — three orders of magnitude above anything this card can excite. `GENERALITY-REVIEW.md` finding 5; `LAW-CHARACTERIZATION.md` §7 Tier 3 (the roll twin) and Tier 1 **(g)** — the hand-flown capture is now the only test left that can justify building it |

---

## 4. OPEN — the real questions, and what would close each

| # | Question | Why it is still open | The measurement that closes it |
|---|---|---|---|
| O1 | **What arrests the down leg in the fine regime?** (D8/D9) | `bSup` is out of the loop by t = 3.1 s and the metric is read at 7–8 s. The yaw channel is *equally dead* in both hemispheres (achieved yaw rate 0.004–0.009 °/s in every cell) while the command differs 3× — so it is *where the aircraft got arrested*, not how hard the loop pushes. `iGate` reads 0.87–0.97 both directions, so it is not an integrator gate. R31 §6 names the untested alternative: **plant asymmetry the law does not model** (g falls to 0.14–0.55 in the terminal window on both directions). | A **long-dwell** oblique: the same 12° diamond with 30 s legs instead of 8 s, 8 lanes. If the down leg eventually converges, it is bandwidth; if it parks at 0.55–0.81°, it is a standing equilibrium and the next instrument is a per-term decomposition of `outY`/`outP` in the fine cone |
| O2 | **Is the residual spread a law problem or an airframe-capability difference?** | The four candidate properties (gLimit / mass / wing area / drag area) are collinear at ρ 0.72–0.90 and no fixed-wing key in the game breaks the cluster (R29 §Q2.3 says this may be *unresolvable with current game content*). | A card that varies **loading on ONE airframe** — the only way to move mass without moving gLimit. Blocked on backlog **#19** (the `Loadout` object), which is blocked on one in-game dump |
| O3 | **Does #21 (`lateralHold` rail) cost anything?** | **It is currently unmeasurable anywhere in the corpus.** `SELECT count(*) FROM segments WHERE blendRailPct>=90 AND railed=0 AND excluded=0` returns **0** of 7 462. Every time the bank pipeline rails, so does everything else. The unsaturated sweep cards (`sweep-slow`/`-creep`/`-step`) have `blendRailPct` = **0.0**; the ones that rail it (`sweep-lowq` 93–98 %, `fixedwing-sweep`/`turn360` 27–97 %) are railed 8/8. | A card that holds \|azErr\| between 7.5° and the bank clamp for 20+ s. Nothing shipped does this. **This invalidates the current shape of Batch 4 row E4**: on `darkreach-05`, recs 01–31 (the clean baseline) have `blendRailPct` **0.0** and `authorityUsedFrac` 0.24–0.34, so the arm would suppress a channel that is already at zero weight; recs 32–63 are railed on 18–19 of 32 |
| O4 | **Does the mod's AoA path work?** | Never scored in an unsaturated capture until now. The α-cards (`alpha-steps`, `alpha-sweep`) have **never been flown**. | R33 just produced the first clean data: `Darkreach obDR6` at **100 % `aoaLimiterActivePct`, `railed = 0`, `authorityUsedFrac` 0.725, terminal 0.257°** (n=4, one lane, aborted on damage). Re-fly it, plus `alpha-steps` on the 8-key roster |
| O5 | **What sets the R32 onset at replicate ~32?** | Ruled out by measurement: frame hitches, mass, fuel, damage, config edits, entry state. Wall clock and replicate index are confounded because the lanes launched together. | A card with a deliberately staggered *start* (not just a staggered launch), so wall clock and replicate index separate |
| O6 | **Does removing `belowSuppress` entirely remove the down-step penalty?** | No arm has ever flown with `belowSuppress == 0` (X12). | A **code change** — make the `false` branch zero, or add a third form — then re-fly `oblique-12-fwd`/`-rev`. Not a card question |
| O7 | **Does the precursor CAUSE the Darkreach departure, or share a cause?** | R32 establishes only ordering (precursor 1–2 replicates earlier, in every lane). | A card that suppresses the roll channel and changes nothing else — but see O3: the arm has no effect during the clean period |
| O8 | **Is `EW1` doing the same thing more slowly?** | Same `assist = 0`, same `maxPitchAngVel` 0.3, same `alphaLimiter` 10, at a quarter of the mass. Never flown on this card. | One lane of `darkreach-05` with `EW1` in the airframe list. Cheap |
| O9 | ~~**Rotorcraft, STOL, and the whole attribution set are UNFLOWN.**~~ **MOSTLY CLOSED (R39/R40/R41) — and the residue is the interesting part.** | The attribution set flew (A1, A2), the alpha path flew (N1–N3), rotorcraft flew twice (H1–H5) and `#39` is closed (`startSpeed: 0` is now DECLARED). **What is still not measured, and must not be claimed:** ONE-LAW case 2 — **the loaded jet — has never been flown at all**, because a card cannot set stores; ONE-LAW case 3 — a genuine **STOL** condition — is still unmet for the fast jets, whose corner-relative entry puts them at 128–160 m/s (X25); and hover rests on **`QuadVTOL1` alone**, because the other two rotorcraft never hovered (H5) — **still true after R42, which reproduced 16/16 `UtilityHelo1` altitude-floor aborts unchanged.** What R42 *added* is that `AttackHelo1`'s hover-card result is a **translating-flight** result, not a hover one: it accelerates from 4.5 to 38 m/s across the card and never returns (H7). | The loaded case needs backlog **#19** (the `Loadout` object) or a hand-flown capture. STOL needs a genuinely low entry for the fast jets, or the question dropped. Hover needs a collective (H5). **`oblique-above-c` is now the only never-flown card in `cards/`** |
| O10 | **Does the law ever move the nose AWAY from the demand?** (Pillar 1, backlog #33) | Never measured. Every "is the law converging or fighting itself?" question so far was answered by proxy. | **Zero flying.** Derivable from the `off` column on all 1 681 captures already on disk |
| O11 | **Does the roll axis limit-cycle above ~350 m/s on every airframe?** — **NARROWED BY R43, NOT CLOSED.** The test O11 itself prescribed was flown and came back **clean on 12 of 12 captures**; what R43 could not reproduce is the *harness* form of the field condition. | **The field evidence, unchanged.** Two Discord captures on `FS-12` at 348–423 m/s score `VERDICT: FAIL` with a **~2 Hz `outR` limit cycle while on target**: over a 22.7 s `HOLD`, `targetBank` mean **−0.162°** and `off` 0.188° (the outer loop commanding nothing) while the inner servo swings **±0.5 stick and ±3° of bank**, `Rflip:105` in 29.6 s. `azErr` oscillates *in phase with* `bank` and the marker is world-locked. Mechanism in raw rows: a live P-error of +5.1° **inverted** to `outR` −0.230 by `RollDamping × rollRateF` 0.18 s after commanding +0.509. `GENERALITY-REVIEW.md` **finding 5**; internal twin at 1.28 Hz on `Multirole1` ~450 m/s (rec 014141). **R43 (v1.0.3, card `hs-hold`, 12 valid captures — `Fighter1` = the field airframe FS-12, `Multirole1`, `SmallFighter1`, 4 replicates each) closed the speed hole and found nothing.** Settled tails (last 15 s of each 30 s hold, 48 segment-tails, mean tail speed **407–505 m/s**, **q 71.6–112.3 kPa** against the corpus's previous ceiling of 25.6): `outR` sd **0.0007–0.0045** (fail threshold ~0.05), peak-to-peak **0.004–0.025** against the field's ~1.15 — **46–290× smaller**; `rollRate` sd **0.003–0.021 °/s** against ±0.77; `stickFlipRateR` **0.033–0.084 /s** against the field's 3.55 /s (**42–107×** lower) and *at or below* the `oblique-6-dwell` family's own 0.055 mean. `wobbleEpisodesOutR` = **0 on 48/48**; `wobbleFreqHzOutR` resolves on only **5/48**, each with `wobbleCoherenceOutR` 0.23–0.35 — a frequency estimate on a 0.002-amplitude signal, never the high-coherence fail shape. And the tails **were** in the state the field report describes: `targetBank` ≡ 0.000, `tBankE` \|mean\| 0.09–0.56°, `off` 0.01–0.06°. | **Three uncontrolled differences, in order of what would discriminate.** (1) **The pilot.** The harness marker is scripted and the drone takes zero manual input; the field case has a hand on a mouse feeding `azErr` continuously. R43 cannot separate *"the servo cannot self-sustain"* from *"the servo needs mouse micro-motion to excite it"* — and that is the cheapest remaining test: hand-fly the sandbox (`PlayerSpawn`) `Fighter1` at ≥350 m/s, `ShowDebugHud` on, marker ~1° off boresight, 30 s. (2) **Loadout.** R43's FS-12 was clean — 13.57 t, cannon only; a pylon-loaded FS-12 has different roll inertia and a card cannot set stores. (3) **Version.** Field was v0.68.0, R43 v1.0.3 — but the roll constants are *identical* (`rollG=1.00 rollDamp=0.10 rollSm=0.06`, unchanged since v0.33.0) and the CHANGELOG carries no roll-servo change between the two, so a silent fix is unlikely rather than excluded. **O11's own 180 m/s control leg was not flown and is now low-value** — the prediction was "180 clean, 350+ not", and 350–505 came back clean. If (1) and (2) also come back clean, O11 is a v0.68.0-condition artifact and can be moved to REFUTED for v1.0.3. `LAW-CHARACTERIZATION.md` §7 Tier 1 **(g)**. |
| O12 | ~~**Does `tiltFrac` actually rise toward 1 in the hover?**~~ **ANSWERED BY R42, AND THE ANSWER IS NO — see X30.** The knobs were reset, the card became the direct readout it was designed to be, and `tiltFrac` falls 0.620 → 0.182 as the aircraft decelerates 108 → 60 m/s. | — | — |
| O13 | ~~**What is `tiltFrac` at and below `HeliHoverSpeed`, and is the defect the SIGN or the LIMITS?**~~ **RESOLVED AS TO MECHANISM, 2026-08-02 — NEITHER. `GetAngleLimits()` is innocent and the sign is not the bug.** It correctly forwards `rotatingJoints[0]`'s `(minAngle, maxAngle)` (`:70286-70289` → `:70205-70208`). But `RotatorInput.Animate` sets `currentAngle = Lerp(minAngle, maxAngle, t)` (`:70237`), so the mod's `(GetWingAngle() − lo)/(hi − lo)` at `ChaseController.cs:1131-1132` recovers **`t` — the game's tilt COMMAND `customAxis1` — not a hover fraction**, and the game pins that command's hover end at **0.18**, not 0 and not 1: `:70352` `AutoHover ⇒ customAxis1 = 0.18`, `:70347` settles to 0.18 on the ground, `:70344` `customAxis1 = Lerp(~0.18, 1f, tiltAtSpeed(speed))` with **1.0 = wing-borne**. The `_sds` branch one line below (`ChaseController.cs:1134`) is correct **because** `swivelPosition = 1f − customAxis1` (`:69365-69366`, hover ⇒ `customAxis1 = 0` at `:69348`). **The two archetypes use OPPOSITE conventions and the tiltwing branch is missing the `1f −`.** | **Corpus confirmation, independent of the source reading:** R42 `rotor-transition` (8 caps, `QuadVTOL1`, `ScenarioThrottle = 0.25`) settles at 68–78 m/s for **80% of rows** with `heliBlend` **0.181–0.184** — the game's 0.18 pin to within **0.002**. That is also what retires X30's "0.38 offset" argument: the plateau is the endpoint. | ~~**The fix is NOT SHIPPED and MUST NOT SHIP FIRST.**~~ **THE GATE IS LIFTED — R44 RECORDED THE PRE-FIX BASELINE, 2026-08-05. SHIP THE FIX.** Proposed, one line at `ChaseController.cs:1131-1132`: `tiltFrac = Mathf.InverseLerp(1f, 0.18f, (GetWingAngle() − lo)/(hi − lo))`, with 0.18 cited as `TiltWingController`'s **archetype-wide** hover reference (`:70336`/`:70352`) — it is a property of the game's tiltwing controller, not of an airframe, so this stays **ONE-LAW compliant**. **THE BASELINE OF RECORD (R44, `QuadVTOL1`, `tiltwing=1` confirmed in the spawn log, `speedRamp ≡ 0` on every scored row so `heliBlend` IS `tiltFrac`):** `rotor-tilt-hold` (thr 1.00) `az8thhi` **`heliBlend` = 1.0000, sd 0.0000, n=10 captures / 9,602 of 9,602 rows exactly 1.000** at 150.1–152.9 m/s; `fineTHhi` 0.9915 ± 0.0170 (n=10; the sub-unity rows are the 93 → 150 m/s spawn chase). `rotor-tilt-hold-lo` (thr 0.25) `az8thlo` **0.1820 ± 0.0002, n=10** at 67–72 m/s; `fineTHlo` 0.2350 ± 0.0020. **The card's pre-fix criterion (`≥ 0.8`) is CONFIRMED at its worst branch: the blend SATURATES, so `tBankE *= (1 − heliBlend)` = 0 — the bank-to-turn channel is entirely deleted in the most wing-borne condition this airframe has.** Both ends of the game's `customAxis1 = Lerp(0.18, 1, tiltAtSpeed)` are now measured from flight — 1.000 wing-borne and 0.182 at the hover pin (to within 0.002, matching R42's plateau), so the source reading is confirmed at both endpoints rather than one. **POST-FIX PREDICTION, RECORDED IN ADVANCE so the after-fly is a check and not an interpretation:** `InverseLerp(1, 0.18, ·)` maps 1.000 → **0.000** (hi arm passes `≤ 0.2`) and 0.182 → **0.998** — i.e. **the `-lo` arm must FLIP to ~1.0 at 70 m/s.** That is correct, not a new bug: the game has the wings at their hover pin there, so the aircraft really is rotor-borne. `LAW-CHARACTERIZATION.md` §7 rotorcraft **(a)**; the throttle-latching this exposes is `GENERALITY-REVIEW.md` finding **19** |
| O14 | **Are the three v1.0.1 fixes actually working?** #72 (invariant-culture numerics), #73 (zero-data-row warning) and #55b (`arm=-1` warm-up). | **R42 EXERCISED NONE OF THEM, and this must not be read as a pass.** (a) #72: the maintainer's machine is already invariant-culture — zero comma-decimals in R42 *or* in R41, so the batch cannot distinguish "fixed" from "was never broken here". (b) #73: no R42 capture closed with zero data rows (min 1,265 samples over 56 captures), so the warning had nothing to fire on; `LogOutput.log` carries no such line. (c) #55b: **none of the three rotor cards declares an `armToggle`**, so no `arm=` appears on any R42 `# config` line at all — `arm=-1` cannot appear, and `compare-runs.py`'s silence is because `_anchor_replicate_filter` is only reached from `_arm_comparisons`, which needs both arms present. | (a) one capture on a comma-decimal locale, or a `--selftest` that forces the culture. (b) covered by `scorecard`'s own selftest, or abort a card inside its `arm` window. (c) **CLOSED BY R44, 2026-08-05.** `rotor-weathervane-35`/`-60` flew armed and the v1.0.1 warm-up behaves exactly as specified: `arm = NULL` on **replicate 1 of 4 of 4 lanes** (both cards, both airframes), then `arm = 0, 1, 1, 0` on replicates 2–5 — the ABBA schedule with the anchor belonging to neither arm. `arm_knob = IntegralStallGate` is present on all 12 captures. *(The knob itself is null on the H7 residual — see H7 — which is a separate result and not a defect in the warm-up.)* ~~one armed batch~~ — historical: **The discriminating rotor cards now exist:** `rotor-weathervane-35` / `rotor-weathervane-60`, written for H7, both declare an `armToggle` — exactly what O14(c) needs and exactly what R42 could not exercise, so flying that pair closes (c) as a side effect (`LAW-CHARACTERIZATION.md` §7 rotorcraft **(d)**). Until then the fix is code-reviewed, not measured. *Note for that batch: the `snapBackM = 0` stratum is still present and still one capture per lane (6/24 on each R42 rotor card) — v1.0.1 makes replicate 0 belong to no arm, it does not remove it from an UNARMED card's pool* |

---

## 5. Is it one law?

**Yes in form; no in outcome — and the residual is small enough that the honest answer is
"unproven either way, and probably not worth calling a violation."**

Three numbers, in descending order of how much they should move your opinion:

**(a) The spread is real and it is not at the incumbent.** R33, 77 captures, 10 airframes, one card,
**zero railed segments**: per-airframe mean `terminalOffDeg` runs **0.0646° (trainer) → 0.3819°
(SmallFighter1)**, a **5.91× ratio** and **29× the replicate noise floor** (median cell sd 0.0109°).
That is far outside noise. It is not measurement scatter and it is not the Ifrit (G1).

**(b) But every airframe removes 94–99 % of the demanded step.** On the 6.0° leg the worst airframe
ends **0.382°** off and the best **0.065°** — i.e. **93.6 %** and **98.9 %** of the step closed. A
5.9× ratio between two small numbers is not the same finding as a 5.9× ratio between two large ones,
and nothing in the repo currently says this out loud. The R28 headline ("the two heaviest airframes
fail outright") was true of the flat-250 entry condition and is **no longer true**: at
`0.95× fbwCornerSpeed` the heaviest airframe in the game flies the card at 0.218° (K6), and the two
airframes that could not fly it at all are ordinary members of the band (G7).

**(c) The spread does not track the flight condition, so it is a property of the law–airframe
interaction.** ρ(A, entry speed) = +0.188, p = 0.61 at n=10 (G3), and in R33 the terminal error
reproduced R29 to **±17 % on 9 of 10 airframes** while the resolved entry speed moved by −44 % to
+22 % (G2, G6). Rank order survives at ρ = +0.903 (n=10) / +0.967 (n=9).

**So: law problem or airframe-capability difference?** The evidence supports *neither* cleanly, and
that is the honest state:

- The best correlate is `aircraftGLimit` (ρ +0.872, p 0.0023) — an **airframe capability**. If that
  is the truth, the spread is not a ONE-LAW violation at all; it is the law correctly getting less
  out of a less capable airframe, and `flightscore`'s `A` normalizer (which divides by
  `omega_avail`, itself derived from `maxPitchAngularVel` and `gLimitPositive`) is supposed to have
  removed exactly that.
- But gLimit is collinear with mass/wing/drag at ρ 0.72–0.90 and **n = 10 cannot separate them**
  (L1, O2). Calling it "airframe capability" is currently a label, not an identification.

**The two places where the ONE-LAW rule is genuinely violated are structural, not statistical**, and
both are visible in the source rather than in the spread:

| violation | why it is a violation regardless of the flight data |
|---|---|
| `schedFloor = 0.3f` (`ChaseController.cs:1255`) and its sibling `Max(0.3f, aoaGateUp)` (`:1296`) | a hardcoded absolute terminates a schedule whose input (`aoaUtil`) is correctly *relative* to a probed ceiling — same floor for a 27° ceiling on 8.7 t and a 10° ceiling on 105 t (R32 §6, GENERALITY-REVIEW 18) |
| `_yawWeak`'s normaliser `Clamp01(closeRate / 6f)` | an absolute deg/s constant — "a per-airframe constant in disguise", which the v0.83 `_stallFilt` comment two blocks away explicitly forbids. The right denominator (`omegaMax`, probed + live) is computed nearby (GENERALITY-REVIEW 15) |

And the deeper structural statement, which is the single most important thing in this file:
**every one of the five terms that responds to a non-responding plant REDUCES authority, and there is
no sixth term that does anything else** (K3). That is a design property, not a per-airframe one — it
just happens to be survivable on nine airframes out of ten because their own stability covers the gap.

---

## 6. The next three measurements, and why those three

Ordered by **information gained per minute of flying.**

> **STATUS 2026-08-02, after R39/R40/R41.** Item 3 (the AoA path) **flew** — see N1–N3 and X23; the
> route in was `alpha-pullup`, not `alpha-sweep`, which could not reach the regime. Item 1 (the
> retreat integral, #33) is **still not started** and still free. Item 2 (the long-dwell oblique) is
> **still the right test for O1** and still unflown, but note X19: `oblique-6-dwell` is not
> state-stationary, so the long-dwell card must control its own flight condition rather than assume it.
>
> **The measurement that now outranks all three is O11 — the high-q roll limit cycle.** It is the only
> field-confirmed law defect in this file. **R43 (2026-08-02) closed the speed hole and the defect did
> NOT reproduce** — 12 captures at 407–505 m/s / q 71.6–112.3 kPa, `outR` sd 0.0007–0.0045 against a
> 0.05 threshold, 0 wobble episodes on 48/48 tails. What is left is the part a card cannot script:
> **two hand-flown captures** (`PlayerSpawn`, `Fighter1`, ≥350 m/s, mouse on target) and one with
> stores. Fly those first. See O11 and `LAW-CHARACTERIZATION.md` §7 Tier 1 (g).
>
> **STATUS 2026-08-02, after R42 (the rotor re-fly).** The rotorcraft picture inverted: the batch's
> stated purpose — "does `AttackHelo1` still diverge?" — resolved to **no, it was the config** (H6,
> X29), and the batch's *incidental* readout became the finding, because resetting the two knobs
> turned `rotor-transition` into the direct `tiltFrac` measurement O12 asked for and **it reads
> backwards** (X30). Two things are now cheap and both gate a code change rather than a card:
> **O13** (extend `rotor-transition` below `HeliHoverSpeed`, ~10 min, settles sign-vs-limits) and the
> **H5 collective** (two of three rotorcraft still cannot hold altitude, unchanged since R41). H7 —
> the blend-band residual — is the first rotorcraft law question the corpus can actually score, but
> it needs an `armToggle` on `rotor-bistab` before it is an experiment rather than an observation.

### 1. The retreat integral (#33) — **0 minutes of flying**

Re-score all 1 681 archived captures for `retreatDeg` / `retreatEpisodes` / a monotonicity index off
the existing `off` column, then re-index (`index-captures.py`, ~30 s).

**Why first:** it is free, it applies retroactively to every batch already flown, and it answers the
one question the whole corpus has been answering by proxy — *does the nose ever move away from the
demand?* O1 (the largest open law question) is a convergence question and there is currently no
convergence metric. `terminalOffDeg` cannot distinguish "converged slowly to 0.6°" from "reached
0.2°, backed off, and settled at 0.6°", and those two have different fixes.

**~~Fold in for free while re-scoring: fix X7 in the same pass.~~ DONE 2026-07-31** — corrected in
**nine** files, not five (the audit missed `INSTRUCTOR-LOOP.md` §3, which is the origin the
`scorecard.py` comment cited, plus `cards/README.md`).
The index says non-zero on **66** (run, airframe, tag) cells — **23 fully unrailed / 32 with some
unrailed segment**, see X7 for which to cite. `scorecard.py`'s comment block was the one justifying
the `alpha_metrics` design: **the design was re-checked and stands** (nothing there consumes the
false premise; only the justification was wrong), so the comment was fixed and the code was not
touched. What the check *did* surface: `alpha_metrics` runs only on `alpha_step`/`alpha_hold`
(`scorecard.py:1143`), so on the one clean capture that reached the ceiling — an `oblique_step` —
none of its eight metrics exist. Tag the re-fly `alpha*` or widen the gate; recorded in
`LAW-CHARACTERIZATION.md` §1 and §4 Batch 3.

### 2. The long-dwell oblique — **~20 min unattended, 8 lanes**

`oblique-12-fwd` / `-rev` geometry with **30 s legs instead of 8 s**, on the eight fixed-wing keys
that clear the pre-spawn gate, 8 replicates, no arm.

**Why second:** the down-step penalty is the **largest measured law effect in the corpus** (×2.8–5.5,
60–240× the replicate noise), it is universal across 7–10 airframes, it survives a crossed order
control, and after four batches (R28, R29, R30, R31) it is localised to a 5-second window nobody has
looked inside. R31 §4.3 is precise about the gap: both hemispheres hand over at the same error, and
the down leg then stops closing. **This is the cheapest test that can distinguish the two remaining
hypotheses** — bandwidth (it converges eventually) versus standing equilibrium (it parks) — and it
needs no code change and no new lever.

**Not** another lever sweep. R31 spent a whole batch proving `bSup` is out of the loop before the
metric is even defined; a second sweep of a gate that closes at t = 3 s would repeat that.

### 3. The AoA path, on the one airframe that just produced clean data — **~15 min, 2 lanes**

Re-fly `oblique-6-c` on `Darkreach` + `EW1` at `0.95× fbwCornerSpeed`, 8 replicates each, plus
`alpha-steps` (never flown) on the 8-key roster with `repeat: 8`.

**Why third and why now:** R33 produced the **first unsaturated capture in the corpus where the mod's
AoA machinery is live on a healthy fixed-wing airframe** — `Darkreach obDR6`, gate active on 100 % of
samples, `railed = 0`, `authorityUsedFrac` 0.725, terminal error 0.257°. The α-path has been the
blocked item in `LAW-CHARACTERIZATION.md` Batch 3 since it was written, on the grounds that nothing
ever provoked the regime. **That is no longer true.** It also settles O8 (`EW1` shares Darkreach's
`assist=0` / `maxPitchAngVel 0.3` / `alphaLimiter 10` at a quarter of the mass) in the same launch,
and it is the only route to L3/#45 that does not require a departure.

**Bring the L6 caveat:** R33's Darkreach lane shed a part on its fifth placement. Expect to lose the
lane, watch `dmgFrac`, and treat 4–8 replicates as the realistic yield rather than 8.

---

### What is deliberately NOT in the next three

| item | why not |
|---|---|
| **E4 — the Darkreach precursor with the roll channel as the arm** | O3: on `darkreach-05`'s clean baseline (recs 01–31) `blendRailPct` is **0.0** and `bWt` median is 0.000, so the arm suppresses a channel already at zero weight; the departed half is railed on 18–19 of 32. The card would produce a null in the control period and no signal in the treatment period. Fix the card before flying the experiment. |
| **E5 — the `schedFloor` A/B (#45)** | R32 is explicit that fixing the stand-down before the precursor is understood makes *some* departures survivable, which is worse than a departure that is legible. Also L3: n=1 airframe. |
| **#20 as an experiment** | X5/X6: the branch executes on 0.00 % of rows on all 10 airframes at the current entry conditions. Ship the `>=` → `>` as hygiene behind a checkbox; do not commission a batch. |
| **The belowness axis (E1, `oblique-above-c`)** | X12: `arm=0` is a form selector, not "off". Flying the axis before the knob semantics are fixed repeats R31. |
| **Rotorcraft** | O9: blocked on #39 and a hover entry mode. Genuinely the riskiest item on the list (writing physics state to a rotorcraft) and it must not be run unattended first. |
