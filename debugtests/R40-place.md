# R40 — `place-noop` / `place-deflect`: ledger #51, placement count vs flight exposure

Batch `R40`, session `20260802-141344`, mod **v0.99.1**, flown 2026-08-02 14:16–14:26.
Cards: `place-noop` (Darkreach + EW1, repeat 12), `place-deflect` (Darkreach, repeat 12),
`alpha-pullup` (10 lanes, repeat 8 — another agent's card, used here only as the control lane).

**Headline: neither branch of the F1 decision rule fires cleanly, because the failure did not occur
at all.** Darkreach completed **32 placements across three cards with zero damage aborts**, including
an 8-replicate 48-second card at a 6.7 km snapback that is the near-twin of the R39 lane which died at
replicate 5. R40 does not rank placement count against flight exposure — it removes the phenomenon,
and points at a third variable neither card controls: **`origDist`**.

---

## 0. The batch-structure question was a phantom — retracted, with the check that retracts it

The brief opened with "48 place-noop captures exist against 24 expected — exactly 2x", and gated
everything on explaining the doubling. **There is no doubling.** The count was taken over a filename
glob that matches both the capture and its sidecar; every capture is two files
(`…csv` + `…airframe.json`).

```
R40 files on disk:  109 .csv  +  109 .airframe.json  =  218
  place-noop      24 csv  (48 files)  = 2 airframes x repeat 12   <- exactly as declared
  place-deflect   12 csv  (24 files)  = 1 airframe  x repeat 12   <- exactly as declared
  alpha-pullup    73 csv  (146 files) = expected 80; see below
```

`index-captures.py` agrees independently: `R40 | 0.99.1 | 109 captures | 10 airframes | 3 cards | 1 abort`.

The one real shortfall is `alpha-pullup`: **73 of 80**, because lane d12 (`FastBomber1`) lost 7
replicates to a single fatal abort at replicate 1. That is one lane, not a queue mechanism — the other
nine lanes each flew 8 of 8. See §5; it is not a damage abort and it matters to #51 for a different reason.

No re-armed queue, no replayed fleet, no double launch, no two-lanes-per-airframe. The log carries
exactly three `[drone] launching` lines (`LogOutput.log:647`, `:950`, `:1182`) for exactly three fleets,
and `[drone] batch queue: 2 fleets — 'place-noop' -> 'place-deflect'` (`:645`) is the only queue line.

---

## 1. The abort indices — the actual readout

The readout is the `# stop` line, per the card notes. **All 36 place captures ran their full declared
duration with full sample counts.** A truncated capture is what an abort looks like; there are none.

| lane | airframe | card | replicates | `# stop` on every one | dur | samples |
|---|---|---|---|---|---|---|
| d1 | Darkreach | `place-noop` | **12 / 12** | `reason=card 'place-noop' complete` | 3.0 / 3.0 s | 48–49 |
| d2 | EW1 | `place-noop` | **12 / 12** | `reason=card 'place-noop' complete` | 3.0 / 3.0 s | 48–49 |
| d3 | Darkreach | `place-deflect` | **12 / 12** | `reason=card 'place-deflect' complete` | 5.0 / 5.0 s | 80–81 |

```
Darkreach place-noop     abort replicate index: NONE  (ran 12 clean)
Darkreach place-deflect  abort replicate index: NONE  (ran 12 clean)
EW1       place-noop     abort replicate index: NONE  (ran 12 clean)
```

`LogOutput.log` contains **zero** `[card] ABORT` lines for the two place fleets and zero occurrences of
`airframe damage` anywhere in the file. The only `ABORT` in the whole batch is `FastBomber1` on
`alpha-pullup` (§5).

### The placements were real

A card that never places would answer nothing. It placed, 11 real snapbacks per lane, `ctrlReset=1`
throughout:

```
Darkreach place-noop     rep 1: snapBackM=0.0 (spawn anchor)   reps 2-12: 291.8 .. 294.0 m
EW1       place-noop     rep 1: snapBackM=0.0                  reps 2-12: 382.3 .. 388.3 m
Darkreach place-deflect  rep 1: snapBackM=0.0                  reps 2-12: 488.2 .. 491.0 m
```

Every replicate — including replicate 1 — also takes a **vertical** `MoveAssembly` of 1500 m, from its
spawn deck to the card's `startAlt` 4000 m. So the placement count is 12 per lane, not 11.

### Which branch fired

The SESSION F1 rule reads: *dies at placement 4–5 ⇒ placement is causal; runs 12 clean ⇒ placement
count is a proxy and flight exposure is required.*

**Branch 2 fired on its face** — 12 clean, twice, on the airframe that has died at replicate 5 six times.
But branch 2's conclusion ("flight exposure is required") **is refuted inside the same batch** by the
control lane in §2. The rule was written assuming R40 would reproduce the failure somewhere. It did not
reproduce it anywhere, and a rule with no failure in it cannot discriminate.

---

## 2. The control lane, and why it breaks the flight-exposure branch too

`alpha-pullup` lane d13 flew **Darkreach, 8 replicates, 48 s each, snapback 6.7 km** — in the same
session, twenty minutes after the place cards.

| | R39 `e3-marker-ff` (d40) | R40 `alpha-pullup` (d13) |
|---|---|---|
| airframe | Darkreach | Darkreach |
| replicates scheduled | 5 | 8 |
| per-replicate flight | 46 s | 48 s |
| snapback per placement | 7 118.6 m | 6 742.0 m |
| **outcome** | **abort rep 5, `detached ratio 0.029`** | **8 / 8 complete, no abort** |

Same airframe, same order of flight exposure, same order of snapback, *more* replicates — and it
completed. Flight exposure cannot be the missing ingredient, because R40 supplied more of it than the
lane that died and still produced nothing.

Aggregate for the session:

```
R40 Darkreach:  place-noop 12 + place-deflect 12 + alpha-pullup 8  =  32 placements, 528 s flight, 0 damage aborts
R40 EW1:        place-noop 12 + alpha-pullup 8                     =  20 placements, 420 s flight, 0 damage aborts
```

Against R39, one day earlier, same two airframes:

```
R39 Darkreach damage aborts (from captures.db, `aborted=1`):
  rec 168  e3-marker-ff      replicate 5   detached ratio 0.029
  rec 229  alpha-sweep       replicate 5   detached ratio 0.114
  rec 282  e2-rel-turn-lead  replicate 4   detached ratio 0.029
  rec 350  stol-steps        replicate 4   detached ratio 0.029
R39 EW1 damage aborts: none (its only abort, rec 358, is the 500 m altitude floor)
```

The damage abort is still armed and unchanged in v0.99.1 — `ScenarioPlayer.cs:1915`,
`if (dmg > 0f) … Abort($"airframe damage (detached ratio {dmg:0.000})")`, threshold *any* detachment.
Zero aborts is a real zero, not a disabled check.

---

## 3. The candidate that survives: `origDist`

The one axis that moved decisively between R39 and R40 is distance from the Unity world origin — which
is exactly what **v0.99.0's ring geometry** was built to collapse.

| | R39 (v0.98.1, lanes on a line) | R40 (v0.99.1, lanes on a ring) |
|---|---|---|
| Darkreach `origDist` | **50 000 – 62 119 m** | **4 995 m** (place) / **9 195 m** (alpha-pullup) |
| EW1 `origDist` | 38 000 – 50 122 m | 4 995 m / ~9 200 m |
| Darkreach damage aborts | 4 | **0** |
| EW1 damage aborts | 0 | 0 |

Read per capture off the `origDist` column (v0.96.2), constant within each capture.

Two things line up:

- **Between batches.** All four R39 Darkreach detachments happened at 50–62 km. R40 put the same
  airframe at 5–9 km for 32 placements and produced none.
- **Within R39.** EW1 flew at 38–50 km and shed nothing; Darkreach flew at 50–62 km and shed four
  times. That has always been read as "Darkreach-specific". It is equally consistent with
  "furthest-lane-specific", and R39 cannot separate them because lane index sets both.

**Mechanism, stated at the strength the evidence supports — a multiplier, not a proof.** float32
position grain at distance *d* is ~`d · 1.2e-7` m: **0.0075 m at 62 km against 0.0006 m at 5 km**, a
12x change in the quantum the PhysX solver resolves positions at. That is still ~67x under
`AeroPart.CheckAttachment`'s 0.5 m threshold, so grain alone never trips it directly. But R39-F's one
surviving mechanism is *sustained deflection → sustained `FixedJoint` load → solver residual → position
error → what `CheckAttachment` measures*, and a solver residual floor scales with the quantum it is
computed at. `origDist` is a plausible amplifier of an existing residual, and the corpus already
carries a large measured `origDist` effect on a different noise channel (R35: r(`origDist`,
`gJitterG`) = 0.948).

**The confound, named rather than hidden.** R39 → R40 changed the mod version *and* the geometry
together. I checked the version half: nothing in v0.99.0 or v0.99.1's change list touches
`MoveAssembly`, `CheckAttachment`, `PartChecker` or the damage-abort path, and the abort clause is
byte-comparable to the one that fired four times in R39. v0.99.0 *is* the ring. So geometry is the
leading candidate — but it is a candidate, established by a between-batch contrast with n=1 batch on
each side, not by a controlled sweep. §7 says how to settle it for about three minutes of flying.

---

## 4. Deck × abort cross-tab (the stall-margin concern) — null, and inapplicable

`Drone/DroneAltDeckM` was **3000, not 0**, during the flight. The launch lines are the authority and
they say so explicitly, together with the warning:

```
:647  … 2 alt decks 2500/5500 m (spread 3000 m).
:648  [drone] Drone/DroneAltDeckM = 3000 m is being applied ON TOP OF the card's own startAlt 4000 m
      — no lane will fly at the altitude the card declares.
```

So the concern is live in principle. It does not apply here, for three independent reasons.

**(a) Darkreach never flew the upper deck on either place card.** Both Darkreach place lanes drew the
*lower* deck; the 5500 m deck went to EW1, whose margin is not in question.

| lane | airframe | card | spawn deck | n | aborts |
|---|---|---|---|---|---|
| d1 | Darkreach | place-noop | **2500 m** | 12 | 0 |
| d2 | EW1 | place-noop | 5500 m | 12 | 0 |
| d3 | Darkreach | place-deflect | **2500 m** | 12 | 0 |
| d4/d6/d8/d10/d12 | Fighter1, SmallFighter1, VTOLTrainer1, COIN, FastBomber1 | alpha-pullup | 6500 m | 33 | 1 |
| d5/d7/d9/d11/d13 | Multirole1, trainer, CAS1, EW1, Darkreach | alpha-pullup | 9500 m | 40 | 0 |

**(b) The deck is a one-instant condition, not the flight condition.** The placement writes the card's
`startAlt` on *every* replicate including the first (`alt=2500.0->4000.0`), so 4000 m is where all 12
replicates actually fly. The deck only sets where the aircraft exists for the seconds before its first
placement.

**(c) Measured, there is no stall signature.** Across the place captures:

```
Darkreach place-noop     585 rows   spd  95.0..99.5  (med 97.1)   aoa 0.0..7.2 (med 5.2)   alt 3975..4000
Darkreach place-deflect  969 rows   spd  95.0..101.6 (med 98.3)   aoa 0.0..8.1 (med 6.5)   alt 3945..4000
EW1       place-noop     585 rows   spd 123.5..131.5 (med 126.2)  aoa 0.1..2.9 (med 2.5)   alt 3985..4000
```

Darkreach *accelerates* 95 → 99.5 m/s and holds altitude within 25 m. A lane below its stall margin
decays and sinks; this one does neither. The AoA is loaded (median 5–6.5°, peak 8.1° against
Darkreach's `alphaLimiter` 10) but flying, and every capture ran full duration.

The cross-tab is reported as requested and it is null: **the single abort in the batch sits on the
6500 m deck, and it is a velocity blow-up, not a stall and not a detach** (§5).

---

## 5. The one abort in R40, and it belongs to #51's file

`FastBomber1`, lane d12, `alpha-pullup`, **replicate 1**, `reason=abort: aircraft gone`,
`dur=0.0 samples=1`.

The single recorded row, immediately after a placement that moved the aircraft 1500 m vertically
(`# entry v=230.0->230.0 alt=6500.0->8000.0 snapBackM=0.0 ctrlReset=1`):

```
t=364.283  spd=7876.6  aoa=46.28  vFwd=5397.3  pitchRate=-42.621  g=0.00
[card] SWEEP RATE CLIPPED: … 0.2 deg/s at 7877 m/s and 5.0 g …
[card] ABORT (aircraft gone) — 'alpha-pullup' segment arm at 0.0s
[drone] #12 despawned (pilot killed)
```

230 m/s in, 7877 m/s out, one placement in between, pilot killed by `TakeGForceDamage`. **That is the
R36 signature** (which reached 40 000–68 000 m/s) reappearing under v0.99.1, at ~1 in 10 lanes instead
of 32 of 32. `g=0.00` is expected — `ResetGLoadTrackers` zeroes `velocityPrev`, so the first row cannot
show the acceleration that produced the speed.

This is **not** a detachment and does not enter the R39-F count. But it is direct evidence that the
placement path can still produce a catastrophic excursion in the shipped build, which is #51's actual
subject. Nine other lanes took the same 1500 m vertical move in the same launch without incident, so it
is intermittent — the same character as the part sheds. Worth its own ledger line.

---

## 6. Every 0.0 I could not distinguish from "not exposed"

**Re-verified after the corpus `--rebuild` that landed the three-state mechanism.** The withdrawal is
now live and visible in the DB as a `segments.skipped` JSON map, and it answers caveat 1 mechanically
rather than leaving it as my inference.

1. **`dmgFrac` = `0.000` on all 2 139 rows of all 36 place captures — STRUCTURALLY UNINFORMATIVE, do not
   read it. The instrument now says so itself.** Scoring any place capture emits:

   ```
   WARNING: capture has DEAD COLUMN(S): aimRate, aoaRec, assist, bWt, bankBlend, bankTR, bigTurn,
   datumX, datumZ, dmgFrac, engP, engR, engY, flyLevel, headingRateFilt, heliBlend, iPitch, leadDeg,
   phiLead, targetBank, yawEff, yawWeak -- present in the header, identically 0.0 on every row.
   Every metric derived from them is reported as SKIPPED, not as 0.0.
   ```

   `dmgFrac` is in that list. The reason it is dead is datable: the one-row deferral
   (`_dmgSeenAt` / `DmgRowWaitS = 0.5f`, `ScenarioPlayer.cs:179,211-226,1913-1915`) **exists in the
   working tree but is stamped v1.0.0**, and `PluginVersion` is now `"1.0.0"` with `ScenarioPlayer.cs`
   uncommitted. The deployed DLL is dated **14:39**, *after* the 14:16–14:26 flight. R40 therefore flew
   the v0.99.1 semantics, where the abort closes the CSV before the row carrying a non-zero ratio lands.
   **Task #75 is fixed in source and unfixed in the binary that produced this batch.** My conclusions do
   not rest on this column.
2. **`detachedRatioAtStart` = `0` on all 36 place sidecars — this one is a genuine zero.** The field
   fails soft to *absent*, not to 0, so a present 0 is a real reading: no replicate started bent.
3. **No scored metric was used, so no metric-level 0.0 is load-bearing.** `place-noop` scores *nothing* —
   its single segment is tagged `arm` and comes back `[EXCLUDED]`, confirmed by running the scorer on a
   capture directly. The whole readout is `# stop` lines, `# entry` lines, the `origDist` raw column and
   `LogOutput.log`. The NULL-vs-0.0 change cannot move anything here.
4. **`place-deflect`'s two deflection segments are RAILED and partly withdrawn — irrelevant to this
   report, but do not mine them for flight quality.** All 24 `az25R`/`az25L` segments read
   `turnRateCapActivePct=100.0%, blendRailPct=100.0%`, and `fixedWindowOffDeg` is now withdrawn on all
   of them (`skipped: "segment shorter than the fixed window (7-8 s)"` — the segments are 2 s). A limit
   is flying those segments, not the control law. The card's job here was placements, and it did that.
5. `scorecard.py --selftest` **passed** both before and after the rebuild. All structural numbers above
   were re-queried after the rebuild and are unchanged — they come from indexer fields the scorer does
   not compute (`aborted`, `stop`, `entry_snapBackM`, `n_rows`) plus raw CSV headers and the log.
6. **Unaffected by the rotorcraft withdrawal.** `bankClampActivePct` / `bankDemandExcessDeg` moved on 120
   rotor segments; every lane in this report is fixed-wing, and nothing here quotes a rotor comparison.

---

## 7. What this means for ledger #51, and what to fly next

**Status of #51: the placement-count threshold did not reproduce, and neither did its flight-exposure
alternative.** R39-F's shape (6 of 7 lanes at replicate 5, likelihood ~3e-8) is not a property of the
placement path alone, because R40 ran 32 Darkreach placements through the same unchanged code with no
sheds. Both stated hypotheses predict aborts in R40's `alpha-pullup` lane (8 placements ≥ 5, 384 s of
flight, 6.7 km snapback) and there were none.

**Against R39-F specifically:**
- **The sub-5 abort is not reproduced.** R40 has zero Darkreach aborts at any replicate index, so
  tonight's data neither confirms nor rebuts the d66 replicate-4 counterexample — it removes the
  regime the counterexample lives in.
- **Mitigation #55a ("cap Darkreach lanes at `repeat: 4`") is moot here and should not be cited from
  this batch either way.** Twelve ran clean; a cap at 4 would have been unnecessary. That is not
  evidence the cap works, it is evidence the hazard was absent.
- **"Darkreach-specificity" needs re-examining.** Within R39, Darkreach was also the *furthest* lane
  (50–62 km) and EW1 the nearer (38–50 km), so airframe and `origDist` are collinear in exactly the
  dataset the specificity claim rests on.

**The discriminating batch, cheapest first.** All three vary `origDist` and nothing else:

1. **`place-noop`, Darkreach only, repeat 12, flown twice in one session — once with the operator's
   camera parked on the ring (`origDist` ~5 km) and once parked ~55 km away so the floating origin
   re-centres the lanes out to R39's band.** Total flying time ~2 x 36 s.
   *Pass for the origDist hypothesis:* the far run aborts at replicate 4–5 with a `detached ratio`
   stop reason, the near run runs 12 clean. *Fail:* both run clean, and `origDist` is exonerated —
   at which point the difference is the mod version and the diff between v0.98.1 and v0.99.1 becomes
   the search space.
2. **Re-fly R39's `e3-marker-ff` Darkreach lane unchanged under v0.99.1's ring.** Same card, same
   `repeat: 5`, same ~7 km snapback, same 46 s per replicate — only `origDist` differs. It died at
   replicate 5 in R39. *Pass:* it completes 5, and the attribution is settled with no new card.
   This is the strongest single test available and it requires writing nothing.
3. **Set `Drone/DroneAltDeckM = 0` before either.** Not for #51 — for hygiene. Both place cards
   currently fly 1500 m off their declared altitude, and it put Darkreach's entry margin closer to its
   density-corrected stall than anyone intended (§4). It did no harm this time; it is one knob and it
   removes a variable from the next run.

**Do not re-fly `place-noop`/`place-deflect` unchanged at the current ring radius.** They have already
returned their answer — 12 clean, twice — and a repeat at 5 km buys nothing. Their value now is as the
*near* arm of test 1.

**One thing to fix regardless:** the `FastBomber1` placement blow-up (§5) is the same code path #51 is
about and it is currently unticketed. `dmgFrac` will never show it — it is a velocity excursion, not a
detachment — so it needs the `# stop` reason `aircraft gone` plus a first-row speed check to be
detectable at all. With v1.0.0's one-row deferral now in the tree, the *detach* half will finally
record; the *velocity* half still will not.
