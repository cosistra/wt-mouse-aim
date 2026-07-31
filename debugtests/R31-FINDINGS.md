# R31 — `bSup` is NOT the transmission path for the down-step penalty, and `arm=0` was never "off", v0.94.0

**The A/B R30 §6.3 asked for**, flown as a 6-lane concurrent sweep. Two cards of identical geometry
and opposite traversal order × three airframes × two lanes each × 8 replicates =
**96 captures**, 58 411 rows, one unattended run, 690 s wall. Source:
`<game>/BepInEx/mouseaim-rec-v0.94.0-R31-d{1..6}-<airframe>-{01..96}-oblique-12-{fwd,rev}-*.csv`
(+ `.airframe.json` sidecars), `mouseaim-anomalies-v0.94.0-R31-20260730-215053.log`, and
`LogOutput-R31.log` (archived, and **complete this time** — see §0.3).

| | |
|---|---|
| airframes | `Fighter1` (d1, d4) `Multirole1` (d2, d5) `FastBomber1` (d3, d6) — **two lanes each** |
| cards | `oblique-12-fwd` (down legs first) `oblique-12-rev` (up legs first) |
| entry | 250 m/s / 4000 m, throttle pinned 0.70 — identical on both cards |
| A/B arm | `armKnob=BelowAlignSuppress`, ABBA per lane, **48 `arm=0` / 48 `arm=1`** |
| launch | two presses of the spawn key, 3 lanes each, 3 s stagger, 6 km lane spacing |

Metric is R30's and R28's, unchanged: **`terminalOffDeg`** from `scorecard.py` — the mean of `off`
over the last `TERMINAL_WINDOW_S` (1.0 s) of a segment. The aggregation imports
`scorecard.score_run` and groups its output. Nothing here reimplements a tool metric; the
`bSup`/`bWt`/`outR`/`iYaw` tables are direct recorder-column reads. **No analysis tool was modified.**

---

## Verdict

**OUTCOME 3 (R30 §6.3's "Null"), with one qualification that matters more than the result:
`bSup` is a correlate, not the mechanism — and the knob does not do what its name says.**

1. **`arm=0` DOES NOT DISABLE THE SUPPRESSION.** `ChaseController.cs:2048–2050` is a ternary between
   two *forms* of it, not between on and off. `arm=1` is the v0.85 roll-invariant form; `arm=0` is
   the **v0.67 body-frame form with its `(1 − lateralHold)` factor**. R31 is therefore a
   **v0.85-form vs v0.67-form** A/B. The question as posed — *"does disabling `BelowAlignSuppress`
   remove the down-step penalty?"* — **is not answerable from this batch**, and cannot be answered
   without a code change. See §1.
2. **The penalty does not go away on either form.** Down/up terminal ratio, position cancelled:
   `Fighter1` 2.918 (v0.67) vs 3.079 (v0.85), `Multirole1` 3.903 vs 5.507, `FastBomber1` 2.810 vs
   2.868. The weaker form removes **5 % / 29 % / 2 %** of it and leaves **×2.8–3.9** standing.
3. **`bSup` cannot be the transmission path for `terminalOffDeg`, and this is decisive rather than
   statistical.** `bWt` — the roll-to-align loop gain `bSup` multiplies — is **identically 0.000 over
   the terminal 1.0 s of all 384 scored segments**, and over the whole late 60 % of 379 of them. The
   channel closes at t = 0.83–3.10 s of an 8 s segment. The metric is read 5–7 s after the gate has
   shut. See §4.
4. **What the handover shows instead.** Both directions hand over from the roll channel at the *same*
   azimuth error (2.6–2.7°, the `FineBankDeadzone` crossing). The **up** leg then converges to
   0.05–0.07× that; the **down** leg only to 0.20–0.42×. The down-step penalty is created **after**
   `bSup` is out of the loop, in the fine regime. See §4.3.
5. **The cost is real, direction-specific and in the expected channel — but the cost this knob exists
   to prevent is UNMEASURABLE on this card.** The v0.67 form rails the roll stick (`|outR| ≥ 0.999`
   on 1.17–1.49 % of down-leg ticks, 59 of 96 down segments) where v0.85 rails **0.000 %** and peaks
   at 0.50, and `analyze-wobble.py` finds 17 `outR` oscillation episodes against 4. But the v0.85
   limit cycle needs `blendWeight` **live**, and it is zero throughout the window here — so the
   `corr(|azErr|, blendWeight)` signature is **undefined (zero variance) in 380 of 384 segments**.
   See §5.
6. **Up legs do not regress on either form** — 0.980 / 0.981 / 0.919 terminal, 0.979 / 0.985 / 0.972
   standing, every CI touching or containing 1. The hemisphere v0.85 promised not to touch is
   untouched by both forms.
7. **v0.94's concurrent per-aircraft A/B is now verified IN FLIGHT** — the thing R30 §0.4 could not
   do. 136 overlapping capture pairs on opposite arms; at 21:52:13 all six lanes are airborne with
   both arms live. But **the six lanes are not decorrelated by queue ordinal** — every lane runs the
   identical `0110011001100110`. See §7.3.
8. **R31's `arm=1` is R30's exact configuration and reproduces it to 0.4 % / 2.3 %** on the two light
   jets (3.079 vs 3.067, 5.507 vs 5.385) across a different session and a 6-lane fleet. Third
   independent measurement of the same number. `FastBomber1` does not reproduce (2.868 vs 1.389) and
   §8 shows why: its two lanes disagree by 3.8×.

---

## §1 — the knob is a FORM SELECTOR, not an on/off switch

This is the first thing to establish because getting it wrong inverts nothing but *renames*
everything. `ChaseController.cs:2047–2052`:

```csharp
const float downAlignTaper = 0.3f;
float belowSuppress = Arm(Cfg.BelowAlignSuppress)
    ? Mathf.Clamp01(-alignFracH) * Mathf.Clamp01((1f - bigTurn) / downAlignTaper)
    : Mathf.Clamp01(-alignFrac) * (1f - lateralHold) * Mathf.Clamp01((1f - bigTurn) / downAlignTaper);
blendWeight *= (1f - belowSuppress);
```

The `false` branch is **not zero**. It is the v0.67 suppressor: body-frame `alignFrac` instead of
roll-invariant `alignFracH`, and the `(1 − lateralHold)` factor v0.85 deleted. So:

| `arm` | `belowSup=` in `# config` | what actually flew |
|---|---|---|
| 0 | 0 | **v0.67 below-nose suppression** (body-frame, azErr-gated) |
| 1 | 1 | **v0.85 below-nose suppression** (roll-invariant, ungated) |

Polarity confirmed from source, not inferred: `ScenarioPlayer.ApplyArm` (`:1057`) computes
`_armIdx = ArmOf(_qi)` and calls `ChaseController.SetArm(_acId, key, _armIdx == 1)`;
`ChaseController.Arm(knobKey, live)` (`:405`) returns `_armValue` when the knob matches. `arm=1` ⇒
knob **true** ⇒ the v0.85 branch. Every capture's `# config` line agrees (§7.4).

**Measured consequence: `arm=0` is a WEAKER suppressor here, not an absent one.** Mean `bSup` over a
down leg is 0.145 / 0.404 / 0.330 on `arm=0` against 0.595 / 0.829 / 0.600 on `arm=1` — a 2–4×
reduction, not a removal. So every number in this document is a *partial-derivative* measurement, and
**the true "no suppression at all" arm has never been flown.**

**Action item.** Either rename the knob (`BelowAlignForm` / `BelowAlignRollInvariant`) or make the
`false` branch actually zero. As it stands, the config key reads as a capability toggle and behaves
as a version selector; a batch commissioned to test "off" tested "the previous version" instead, and
nothing in the artifacts said so.

---

## §2 — did the instrument work?

### 2.1 Sound

| check | result |
|---|---|
| captures | **96** — exactly 3 airframes × 2 lanes × 2 cards × 8 replicates, matrix verified |
| lanes | d1/d4 `Fighter1`, d2/d5 `Multirole1`, d3/d6 `FastBomber1` — 16 each |
| sidecar `jsonKey` == filename airframe | 96/96 |
| `# stop` present | 96/96 |
| `# stop` reason | 96/96 `card '<name>' complete`; **0 aborted**, 0 refused, 0 declined |
| samples | 608 (89 captures) or 609 (7); `samples=` matches row count on all 96 |
| segments | 5 per capture on all 96 |
| scored segments | **384** (`scorecard.py` excluded exactly 96 `arm` segments — by design) |
| segment duration | **7.916–7.984 s** across all 384 (0.068 s ≈ 1 fixed step) — **no truncation** |
| card duration | 37.65–37.75 s summed over segments; `# stop dur` **38.0 on all 96** |
| unrecognised tags | **0** |
| **RAILED warnings** | **0 of 384** |
| any other `scorecard` warning | **0** |
| columns | **64** on all 96 headers; **64** on all 58 411 rows — lockstep intact |
| `ctrlReset=1` | **96/96** |
| `# entry` provenance | 96/96; 91 distinct (`snapBackM` 0 on the two first-placements, ~9 677–9 762 m after) |
| `# override` | absent on all 96 (neither card pins anything) |
| `# config` | **exactly 2 distinct lines** — the two arms, differing only in `belowSup=`/`arm=` |
| anomaly log | present; `overshoot` (390) / `over-roll` (323) only, 713 entries |

Zero RAILED again: **no cell is excluded from any ratio below, on any airframe, at either arm.**
`compare-runs.py --summary` returns 48 rows of n = 8 with nothing dropped; its rail/auth column reads
`blend 4–6 %` and `cap 3–4 %`, an order of magnitude under the 90 % threshold.

The anomaly stream is **balanced across arms** — `over-roll` 64/64 (`Fighter1`), 40/32
(`Multirole1`), 64/59 (`FastBomber1`); `overshoot` 66/64, 66/64, 66/64. It carries no arm signal and
is not used below.

### 2.2 `frameMs` — stalls are CLUSTERED, and every cluster is a whole-fleet event

| | |
|---|---|
| rows | 58 411 |
| distinct values | 23 |
| mean / p50 / p99 / p99.9 | 16.726 / 16.70 / 16.70 / 20.90 |
| min / max | 4.20 / **112.80** |
| rows > 20 ms | **66** (0.113 %) |
| rows > 33.3 ms (a dropped vsync frame) | **36** (0.062 %) |
| rows > 50 ms | **18** (0.031 %) |

**The R29 question, answered: this is not an R29.** R29's stalls were 100 % inside one 8.7 s window
(the operator's monitor sleeping). R31's 66 stalled rows fall into **five clusters** spread over
254 s of a 690 s batch:

| # | wall clock | rows | span | lanes touched | worst | excess over 16.7 ms |
|---|---|---:|---:|---|---:|---:|
| 1 | 21:57:50 | 6 | 0.63 s | **all 6** | 21.0 ms | 26 ms |
| 2 | 21:59:05 | 18 | 0.50 s | **all 6** | 68.9 ms | 673 ms |
| 3 | 21:59:47 | 12 | 0.45 s | **all 6** | 112.8 ms | 741 ms |
| 4 | 22:01:37 | 6 | 0.37 s | **all 6** | 20.4 ms | 22 ms |
| 5 | 22:02:03 | 24 | 0.55 s | **all 6** | 36.6 ms | 334 ms |

30 of 96 captures are touched; **no capture is touched twice**; the largest single excursion is
1.8 vsync periods. But the shape is the point: **every stall is simultaneous across the whole
fleet.** The launch stagger decorrelates *segment phase* between lanes, which is what it was built
for, and it does not decorrelate the hitch itself — a 741 ms cluster lands on six aircraft at once,
each at a different point in its card. That is the correct behaviour of the stagger (six different
segments poisoned rather than six copies of one), and `frameMs` is now the per-row evidence that lets
it be checked instead of assumed. **No row was excluded on this basis**; the total excess across the
batch is 1.8 s out of 690 s (0.26 %), and dropping the 66 rows changes no cell mean past its third
decimal.

`LogOutput-R31.log` carries 10 `[drone] frame hitch` warnings (52–305 ms); the 166 ms and 305 ms
entries precede the launch line, matching R30's scene-load pattern. The 113 ms and 69 ms warnings
correspond to CSV clusters 3 and 2.

### 2.3 The log

Complete this time — it runs past mission quit, so the despawns are present (R30's open action item,
closed):

```
[drone] launching 3 x 'Fighter1,Multirole1,FastBomber1' (by lane, wrapping) at 4000 m / 250 m/s,
        3s apart, lanes 8000 m + 6000 m abeam.                                    [x2 — two presses]
[drone] card 'oblique-12-fwd' (2 selected, 38s each, x8 from card 'oblique-12-fwd',
        A/B on 'BelowAlignSuppress' from card 'oblique-12-fwd' armToggle):
        airframe 'Fighter1, Multirole1, FastBomber1' [card], 4000 m [card], 250 m/s [card],
        3 drone(s) [card 'oblique-12-fwd' airframe list (3 named)].
[drone] #1..#6 spawned … 1/1/2/1/1/2 crew.  6 live.
[drone] #1..#6 despawned (card finished).  0 live.
```

Every value from the card, **including the A/B knob** — v0.90's `armToggle` driving the sweep with
nothing hand-matched in F1. Zero refusals, zero pre-spawn gate rejections, zero `p.dead`/`ejected`,
zero exceptions. Both `FastBomber1` lanes report **2 crew**, and their segment durations (7.92–7.98 s)
match the single-seat lanes' exactly — the v0.90.1 `Time.fixedTime` guard is holding.

---

## §3 — noise floors

Two of them, because the design supports two units of analysis, and the second is the one that
governs Q1.

**(a) Within-cell replicate spread** — sd of `terminalOffDeg` across the 8 replicates of one
(airframe, card, arm, tag); 16 such cells per airframe.

| airframe | replicate CV (min–max) | median |
|---|---|---:|
| `Fighter1` | 0.20 – 3.13 % | **1.28 %** |
| `Multirole1` | 0.55 – 12.60 % | **3.12 %** |
| `FastBomber1` | 7.57 – 116.22 % | **74.61 %** |

**(b) The noise floor of the RATIO** — sd of the per-capture log direction ratio
(`geomean(down legs) / geomean(up legs)`, both legs from the same capture) within one
(airframe, card, arm) cell, n = 8.

| airframe | sd(log) min – max | median | ⇒ ratio noise floor |
|---|---|---:|---:|
| `Fighter1` | 0.79 – 1.52 % | 1.32 % | **~1.3 %** |
| `Multirole1` | 2.66 – 6.08 % | 2.94 % | **~2.9 %** |
| `FastBomber1` | 68.25 – 80.35 % | 74.45 % | **~74.5 %** |

`FastBomber1`'s floor is **2.5× R30's** (30 % → 74 %) and §8 identifies the cause: its two lanes are
not the same aircraft behaviourally. **No `FastBomber1` number in this document clears its own noise
floor**, and none is quoted as a result.

---

## §4 — Q1 / Q3: the ratio per arm, and why `bSup` cannot be carrying it

### 4.1 The 2×2, per arm

Geometric mean `terminalOffDeg` in degrees; 16 scored segments per cell (2 tags × 8 replicates).
Main effects on the log scale, R30's identical construction: `DIR × = √(dn-early·dn-late) /
√(up-early·up-late)`, `POS × = √(dn-early·up-early) / √(dn-late·up-late)` (**< 1 = late is worse**),
`int × = √((de/dl)/(ue/ul))`.

| airframe | arm | dn-early | dn-late | up-early | up-late | **DIR ×** | POS × | int × |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `Fighter1` | 0 (v0.67) | 0.5110 | 0.5661 | 0.1842 | 0.1844 | **2.918** | 0.950 | 0.951 |
| `Fighter1` | 1 (v0.85) | 0.5766 | 0.5815 | 0.1854 | 0.1907 | **3.079** | 0.982 | 1.010 |
| `Multirole1` | 0 | 0.6791 | 0.7005 | 0.1458 | 0.2142 | **3.903** | 0.812 | 1.194 |
| `Multirole1` | 1 | 0.8426 | 1.1669 | 0.1466 | 0.2211 | **5.507** | 0.692 | 1.044 |
| `FastBomber1` | 0 | 0.6178 | 0.6972 | 0.2432 | 0.2244 | **2.810** | 0.980 | 0.904 |
| `FastBomber1` | 1 | 0.6658 | 0.7979 | 0.2577 | 0.2507 | **2.868** | 0.926 | 0.901 |

Position is cancelled inside `DIR ×` by construction and each arm group is 8 `fwd` + 8 `rev` per
lane, so the arm contrast below is also position-free. The position effect itself survives at R30's
magnitude and sign (`Multirole1` 0.69–0.81, i.e. late is 24–44 % worse; `Fighter1` ~0.95–0.98) and is
**not** materially changed by the arm.

### 4.2 The arm contrast

Two statistics, because they answer slightly different questions and disagree about significance in
one cell. Both use the same per-capture log direction ratio.

**(i) Unpaired, over the 16 captures of each (airframe, arm) group**, t(15):

| airframe | arm 0 (v0.67) | 95 % CI | arm 1 (v0.85) | 95 % CI | **arm0/arm1** | Welch t |
|---|---:|---|---:|---|---:|---:|
| `Fighter1` | **2.918** | [2.834, 3.005] | **3.079** | [3.045, 3.114] | **0.948** | −3.6 |
| `Multirole1` | **3.903** | [3.478, 4.380] | **5.507** | [4.491, 6.754] | **0.709** | −3.1 |
| `FastBomber1` | **2.810** | [1.905, 4.144] | **2.868** | [1.959, 4.198] | **0.980** | −0.1 |

**(ii) Paired within (lane, card)** — removes lane, card, position and traversal order exactly; the
unit is the cell mean, n = 4 cells per airframe, t(3) = 3.182:

| airframe | d\<a\> fwd | d\<a\> rev | d\<b\> fwd | d\<b\> rev | **pooled arm0/arm1** | 95 % CI |
|---|---:|---:|---:|---:|---:|---|
| `Fighter1` | 0.912 | 0.976 | 0.921 | 0.984 | **0.9477** | [0.8910, 1.0081] |
| `Multirole1` | 0.828 | 0.633 | 0.835 | 0.576 | **0.7088** | [0.5246, 0.9575] |
| `FastBomber1` | 1.047 | 0.960 | 1.027 | 0.893 | **0.9798** | [0.8736, 1.0988] |

**Against the noise floors of §3(b):**

| airframe | shift in the log ratio | ratio noise floor | clearance |
|---|---:|---:|---:|
| `Fighter1` | 5.4 % | 1.3 % | **4.1×** — clears |
| `Multirole1` | 34.4 % | 2.9 % | **11.9×** — clears |
| `FastBomber1` | 2.0 % | 74.5 % | 0.03× — **does not clear; report as no measurement** |

The paired-cell CI on `Fighter1` touches 1 only because n = 4 with t(3) = 3.182 is a blunt interval;
all four cells move the same way, the unpaired t is −3.6, and the shift is 4× its own floor. Read it
as a small real effect, not as a null.

**Plain statement.** Reverting to the weaker v0.67 suppressor **shrinks the down-step penalty by
5 % on `Fighter1` and 29 % on `Multirole1`, does nothing measurable on `FastBomber1`, and leaves a
×2.8–3.9 penalty standing on all three.** It neither vanishes nor grows. This is R30 §6.3's
**Null** outcome with a small partial attribution on one airframe.

### 4.3 Q3 — the mechanism, verified rather than inferred

**On `arm=1`, R30 §6.1 reproduces.** Mean over 32 segments per cell (16 captures × 2 tags):

| airframe | arm | dir | mean `bSup` | % samples > 0.5 | mean `bWt` | `bWt` rail % | up/down `bWt` |
|---|---|---|---:|---:|---:|---:|---:|
| `Fighter1` | 1 | down | 0.5949 | **65.4** | 0.0186 | **0.0** | **4.86×** |
| `Fighter1` | 1 | up | 0.0414 | **0.0** | 0.0906 | 3.6 | |
| `Multirole1` | 1 | down | 0.8291 | **91.9** | 0.0307 | **0.0** | **4.15×** |
| `Multirole1` | 1 | up | 0.1452 | 7.3 | 0.1274 | 4.1 | |
| `FastBomber1` | 1 | down | 0.6004 | **66.1** | 0.0167 | **0.0** | **7.15×** |
| `FastBomber1` | 1 | up | 0.2202 | 22.5 | 0.1196 | 3.6 | |
| `Fighter1` | 0 | down | 0.1450 | 3.4 | 0.0614 | 3.1 | **1.45×** |
| `Fighter1` | 0 | up | 0.0293 | 0.0 | 0.0889 | 3.5 | |
| `Multirole1` | 0 | down | 0.4041 | 47.7 | 0.0903 | 3.0 | **1.40×** |
| `Multirole1` | 0 | up | 0.1334 | 7.1 | 0.1264 | 3.9 | |
| `FastBomber1` | 0 | down | 0.3296 | 36.2 | 0.0575 | 2.9 | **2.06×** |
| `FastBomber1` | 0 | up | 0.2164 | 23.3 | 0.1187 | 3.6 | |

Answering the three sub-questions exactly as posed:

- **Is `bSup` on for 41–98 % of down legs and 0–14 % of up legs on `arm=1`?** Yes for down (65–92 %
  above 0.5). For up: 0 % / 7 % / **22.5 %** — `FastBomber1` sits above R30's 14 % ceiling, which is
  a real widening, not a re-read, and it is on the airframe whose numbers §8 disqualifies.
- **Is `bWt` 3–10× lower on down legs on `arm=1`?** Yes — 4.86 / 4.15 / 7.15×, and it rails at 1.0
  for 3.6–4.1 % of every up leg and **0.0 % of every down leg**, exactly as R30 measured.
- **On `arm=0`, does `bSup` read ~0 and does `bWt` equalise?** **No, and no** — and the first "no"
  is §1's finding (the v0.67 form is still running). `bWt` remains **1.4× / 1.4× / 2.1×** lower on
  down legs. **So even at the weaker form, something is still suppressing the roll channel on down
  steps: `Clamp01(-alignFrac)` — belowness itself — is direction-keyed in BOTH forms.** By the
  prompt's own criterion this mechanism is *at best partial*.

**And then the decisive measurement, which makes the partial attribution moot for this metric:**

| window | segments where `bWt` is identically 0 |
|---|---|
| terminal 1.0 s (the window `terminalOffDeg` averages) | **384 / 384** |
| whole late 60 % (the GATE-CHATTER window) | **379 / 384** (the 5 exceptions are `Multirole1`/`FastBomber1` down legs on `arm=1`, peak `bWt` 0.001) |

Mean time into the segment of the **last** tick with `bWt` > 0.001:

| airframe | down `arm=0` | down `arm=1` | up `arm=0` | up `arm=1` |
|---|---:|---:|---:|---:|
| `Fighter1` | 0.83 s | 1.14 s | 1.32 s | 1.33 s |
| `Multirole1` | 2.49 s | 3.10 s | 1.63 s | 1.63 s |
| `FastBomber1` | 1.06 s | 1.23 s | 1.86 s | 1.88 s |

The roll-to-align channel closes when `|azErr|` falls under `FineBankDeadzone` (2.5°), which happens
in the first 1–3 s of an 8 s segment. `terminalOffDeg` is measured at 7.0–8.0 s. **`bSup` is out of
the loop for the entire window in which the metric is defined, on every arm, on every airframe, on
every one of 384 segments.** Whatever it contributes to the terminal number is carry-over from the
transient it shaped — which is exactly the 5–29 % §4.2 measured, and it caps the mechanism there.

**What sets the terminal number instead — the handover.** `|azErr|` at the last live-`bWt` tick, and
the ratio of the terminal `|azErr|` to it:

| airframe | dir | arm | handover t | `|azErr|` at handover | terminal `|azErr|` | **converged to** |
|---|---|---|---:|---:|---:|---:|
| `Fighter1` | down | 0 | 0.83 s | 2.674 | 0.541 | **0.202×** |
| `Fighter1` | down | 1 | 1.14 s | 2.628 | 0.579 | **0.220×** |
| `Fighter1` | up | 0 | 1.32 s | 2.648 | 0.187 | **0.071×** |
| `Fighter1` | up | 1 | 1.33 s | 2.615 | 0.192 | **0.073×** |
| `Multirole1` | down | 0 | 2.49 s | 1.867 | 0.686 | **0.367×** |
| `Multirole1` | down | 1 | 3.10 s | 1.940 | 0.811 | **0.418×** |
| `Multirole1` | up | 0 | 1.63 s | 2.663 | 0.182 | **0.068×** |
| `Multirole1` | up | 1 | 1.63 s | 2.686 | 0.187 | **0.070×** |
| `FastBomber1` | down | 0 | 1.06 s | 2.049 | 0.578 | **0.282×** |
| `FastBomber1` | down | 1 | 1.23 s | 2.346 | 0.597 | **0.254×** |
| `FastBomber1` | up | 0 | 1.86 s | 2.629 | 0.135 | **0.051×** |
| `FastBomber1` | up | 1 | 1.88 s | 2.586 | 0.152 | **0.059×** |

**Both hemispheres hand over at the same error.** The up leg then closes 93–95 % of it; the down leg
closes 58–80 %. The arm moves the *convergence ratio* by 2–5 percentage points and the *handover
error* by ~0. **The down-step penalty is manufactured downstream of everything `bSup` touches.**

Terminal state (last 1.0 s; `|·|` on the sign-carrying columns, since the DR/DL and UL/UR tags are
azimuth mirrors and would otherwise cancel):

| airframe | dir | arm | `off` | `|azErr|` | `elevErr` | `|outY|` | `|yawRate|` | `iGate` | `|iYaw|` | `bankTR` |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `Fighter1` | down | 0 | 0.538 | 0.541 | −0.000 | 0.105 | **0.004** | 0.910 | 0.016 | 0.455 |
| `Fighter1` | down | 1 | 0.577 | 0.579 | −0.014 | 0.099 | **0.004** | 0.904 | 0.018 | 0.817 |
| `Fighter1` | up | 0 | 0.186 | 0.187 | −0.011 | 0.038 | **0.004** | 0.969 | 0.006 | 0.000 |
| `Fighter1` | up | 1 | 0.191 | 0.192 | −0.012 | 0.039 | **0.004** | 0.968 | 0.006 | 0.000 |
| `Multirole1` | down | 0 | 0.686 | 0.686 | 0.003 | 0.115 | 0.008 | 0.886 | 0.018 | 2.363 |
| `Multirole1` | down | 1 | 0.990 | 0.811 | −0.485 | 0.123 | 0.009 | 0.835 | 0.018 | 4.713 |
| `Multirole1` | up | 0 | 0.181 | 0.182 | −0.010 | 0.036 | 0.006 | 0.970 | 0.006 | 0.000 |
| `Multirole1` | up | 1 | 0.187 | 0.188 | −0.010 | 0.037 | 0.006 | 0.969 | 0.006 | 0.000 |
| `FastBomber1` | down | 0 | 0.766 | 0.578 | **0.355** | 0.100 | 0.005 | 0.872 | 0.016 | 1.520 |
| `FastBomber1` | down | 1 | 0.809 | 0.597 | **0.346** | 0.097 | 0.005 | 0.865 | 0.016 | 1.423 |
| `FastBomber1` | up | 0 | 0.414 | 0.135 | **0.330** | 0.024 | 0.004 | 0.931 | 0.004 | 0.000 |
| `FastBomber1` | up | 1 | 0.434 | 0.152 | **0.342** | 0.027 | 0.005 | 0.782 | 0.004 | 0.000 |

Three non-circular facts in that table:

1. **The residual is near-pure azimuth on both light jets** (`elevErr` ≤ 0.014° against `azErr`
   0.19–0.81°), reproducing R30 §6.1. `FastBomber1` carries a 0.33–0.36° standing *elevation*
   residual on every leg including the up ones — the mushing R28 §3.3 flagged, still present.
2. **The achieved yaw rate is ~0.004–0.009 °/s in EVERY cell, up and down, both arms** — while the
   yaw command differs 3× (0.10 down vs 0.038 up). The yaw channel is equally dead in both
   hemispheres at these command levels; the difference is *where the aircraft got arrested*, not how
   hard the loop is pushing. `azErr/|outY|` is ~5.0–5.6 in every cell: same loop gain, different
   equilibrium.
3. **The fine integrator is not gated off** — `iGate` reads 0.87–0.97 on both directions (the v0.83
   `IntegralStallGate` is open) and `iYaw` sits at 13–15 % of its 0.12 cap on down legs against
   3–5 % on up. The anti-residual term is winding and is not enough. This is not an `iGate` defect.

`bankTR` (the commanded coordinated-turn bank) is 0.46–4.71 on down legs and **exactly 0.000 on
every up leg** — but `bankTR` is an algebraic function of the residual `azErr` that is being
measured, so it is a symptom, not evidence. Same for `settleOn` (1.000 on every up leg, 0.00–0.26 on
down): the v0.65 B2 settle gate requires `|azErr| < 0.5°`, and the down legs terminate above it. Both
are listed for completeness and **neither is offered as a mechanism**.

---

## §5 — Q2: what the weaker form costs

### 5.1 Up legs do not regress. Down legs improve.

Paired within (lane, card), n = 4 cells, t(3):

| airframe | quantity | arm 0 | arm 1 | **arm0/arm1** | 95 % CI |
|---|---|---:|---:|---:|---|
| `Fighter1` | UP terminal (geo, deg) | 0.1843 | 0.1880 | 0.980 | [0.955, 1.006] |
| `Fighter1` | UP standing (late 60 %) | 0.2558 | 0.2612 | 0.979 | [0.951, 1.008] |
| `Fighter1` | DOWN terminal | 0.5379 | 0.5791 | 0.929 | [0.852, 1.012] |
| `Fighter1` | DOWN standing | 0.6773 | 1.3303 | **0.509** | [0.337, 0.770] |
| `Multirole1` | UP terminal | 0.1767 | 0.1800 | 0.981 | [0.947, 1.017] |
| `Multirole1` | UP standing | 0.2936 | 0.2982 | 0.985 | [0.958, 1.012] |
| `Multirole1` | DOWN terminal | 0.6897 | 0.9916 | **0.696** | [0.527, 0.918] |
| `Multirole1` | DOWN standing | 2.2791 | 3.5912 | **0.635** | [0.534, 0.755] |
| `FastBomber1` | UP terminal | 0.2336 | 0.2542 | 0.919 | [0.762, 1.109] |
| `FastBomber1` | UP standing | 0.2938 | 0.3023 | 0.972 | [0.887, 1.065] |
| `FastBomber1` | DOWN terminal | 0.6563 | 0.7289 | 0.901 | [0.772, 1.051] |
| `FastBomber1` | DOWN standing | 0.9620 | 1.4048 | **0.685** | [0.494, 0.950] |

Time for `off` to first fall below 1.0° on a down leg:

| airframe | arm 0 | arm 1 | never reached |
|---|---:|---:|---|
| `Fighter1` | 3.27 s | 5.17 s | 0/32 both |
| `Multirole1` | 6.28 s | 7.28 s | 0/32 vs **2/32** |
| `FastBomber1` | 4.36 s | 5.14 s | 0/32 both |

`flightscore` per arm (median `A` across 16 runs per airframe; `--verbose` roll-up on 48 files each):

| airframe | `obDR12` | `obDL12` | `obUL12` | `obUR12` | ALL |
|---|---:|---:|---:|---:|---:|
| `Fighter1` arm 0 | 0.620 | 0.631 | 0.709 | 0.721 | **0.659** |
| `Fighter1` arm 1 | 0.568 | 0.589 | 0.706 | 0.721 | **0.619** |
| `Multirole1` arm 0 | 0.566 | 0.563 | 0.670 | 0.679 | **0.600** |
| `Multirole1` arm 1 | 0.557 | 0.554 | 0.670 | 0.677 | **0.589** |
| `FastBomber1` arm 0 | 0.607 | 0.643 | 0.731 | 0.756 | **0.642** |
| `FastBomber1` arm 1 | 0.586 | 0.627 | 0.730 | 0.741 | **0.633** |

Every down tag is better on `arm=0`; every up tag is identical to within 0.003. Consistent on all
three airframes.

### 5.2 But the roll channel pays for it, exactly as v0.85 predicted

| airframe | dir | arm | `|outR| ≥ 0.999` | peak `|outR|` | segments with any rail |
|---|---|---|---:|---:|---|
| `Fighter1` | down | 0 | **1.489 %** | **1.000** | **29 / 32** |
| `Fighter1` | down | 1 | 0.000 % | 0.497 | 0 / 32 |
| `Multirole1` | down | 0 | **1.221 %** | **1.000** | **16 / 32** |
| `Multirole1` | down | 1 | 0.000 % | 0.501 | 0 / 32 |
| `FastBomber1` | down | 0 | **1.172 %** | **1.000** | **14 / 32** |
| `FastBomber1` | down | 1 | 0.000 % | 0.537 | 0 / 32 |
| any | up | 0 or 1 | 0.000 % | 0.503–0.512 | 0 / 128 |

`analyze-wobble.py` over the two arms separately:

| | arm 0 | arm 1 |
|---|---|---|
| `outR` oscillation episodes | **17** (0.43–1.00 Hz, pp 0.26–1.34) | **4** (0.43–0.50 Hz, pp 0.41–0.79) |
| `outR` railed (per-file) | 0.5–1.0 % on **45 / 48** files | 0.0 % on **48 / 48** |
| VERDICT PASS / WARN / FAIL | 43 / 0 / 5 | 30 / 1 / 17 |

The rail and the oscillation count go the way v0.85 predicted: the v0.67 form lets roll-to-align run
harder below the nose and it saturates and chatters. The `FAIL` count goes the other way, but every
one of those 22 verdicts is `high-frequency pitch buzz` — a **pitch**-channel flag with 4–14 s of a
38 s capture affected, and unrelated to the roll loop under test. `arm=1`'s excess is driven by its
`Multirole1` down legs never converging (`tail off` 4.0–4.7°), which is the penalty itself.

### 5.3 THE COST THIS KNOB EXISTS TO PREVENT IS NOT MEASURABLE ON THIS CARD — and that is the finding

`GATE-CHATTER-FINDINGS.md` §5(a) measured the v0.85 defect on `elDn`, a 20° pure-down step, over the
**late 60 %** of the block:

| | GATE-CHATTER `elDn` (the defect) | GATE-CHATTER `elUp` | R31 down `arm=0` (worst airframe) | R31 down `arm=1` |
|---|---:|---:|---:|---:|
| mean `off` | **6.92 ± 2.40°** | 0.03° | 2.28° | 3.59° |
| sd of `off` in the window | 2.67 | 0.03 | 1.68 | 2.08 |
| bank half-amplitude | **43.3 ± 9.2°** | 0.11° | 11.9° | 16.5° |
| `outR` sign flips /s | **0.58** | 0.00 | 0.96 | 0.63 |
| `corr(|azErr|, blendWeight)` | **+0.918 ± 0.045** | — | **undefined** | **undefined** |

The correlation is **undefined in 380 of 384 segments**, on both arms, because `bWt` has **zero
variance** in that window — it is identically 0. (Over the *whole* segment the same correlation reads
+0.92 to +0.98 on **every** cell including every up leg, because `bWt` is a monotone function of
`lateralHold` which is a monotone function of `|azErr|` during a decaying transient; that number is
an identity, not a loop signature, and is not quoted as one.)

So: **R31 shows no reinstated loop, and R31 could not have shown one.** `oblique-12` is a 12° oblique
step that closes the roll channel inside 3 s; `elDn` is a sustained below-nose hold in which
`blendWeight` stays live and finds a false equilibrium. The safety case for v0.85 was never on trial
here. The one leading indicator that *is* present — the roll stick railing on 1.2–1.5 % of `arm=0`
down-leg ticks against 0.0 % on `arm=1` — points the same way v0.85 did.

**Verdict on Q2: NEEDS RESHAPING. Do not ship "revert to v0.67", and do not ship "delete the
suppression" either.** The measured benefit is real but small on the metric that matters (5–29 % of a
×2.8–3.9 penalty), the measured cost is real and in the predicted channel, and the *decisive* cost —
the elDn limit cycle — is untested. Trading a verified limit-cycle fix for a 5 % ratio improvement on
a card that cannot see the limit cycle is not a trade this batch is entitled to authorise.

---

## §6 — what this batch does NOT identify

- **Whether removing the suppression entirely removes the penalty.** §1. No arm flew with
  `belowSuppress == 0`. Requires a code change.
- **Whether the v0.67 form reinstates the elDn limit cycle.** §5.3. Requires `e1-below-suppress` /
  `e1-below-control`, `darkreach-05`, or another card that holds a below-nose attitude long enough
  for `blendWeight` to stay live — not `oblique-12`.
- **Why the fine regime converges 4× worse below the nose.** §4.3 localises the defect to the window
  after the roll handover and shows the yaw channel is equally dead in both hemispheres. It does not
  say what arrests the down leg at 0.55–0.81° while the up leg reaches 0.19°.
- **Whether `alignFrac` (belowness) is even the right suspect any more.** It gates `bSup` in both
  forms, and `bSup` is out of the loop in the measurement window. Nothing in the fine regime is known
  to be direction-keyed. It might not be a *gate* at all — it might be plant asymmetry (unloaded
  descent, `g` 0.14–0.55 in the terminal window on **both** directions) that the law does not model.
- **`FastBomber1` anything.** §8.
- **Whether the effect scales with step size.** One card family, one 12° magnitude, as in R30.
- **Whether it holds on the other 11 airframes.** Three here.

---

## §7 — Q4: did the concurrent A/B behave?

### 7.1 Balance, at every level that matters

| grouping | result |
|---|---|
| global | 48 `arm=0` / 48 `arm=1` |
| per (airframe, card, arm) | **8 / 8** in all 12 cells |
| per (lane, card, arm) | **4 / 4** in all 24 cells |
| per (airframe, card, arm, tag) | 8 in all 48 cells |
| distinct `armKnob` | `BelowAlignSuppress`, on all 96 |

### 7.2 ABBA is exact on every lane

`ScenarioPlayer.ArmOf(i) = ((i+1)>>1)&1` predicts `0110011001100110` for a 16-deep queue. All six
lanes flew exactly that, and every lane's card sequence is a strict `FRFRFRFRFRFRFRFR`:

```
d1 Fighter1     arms 0110011001100110   cards FRFRFRFRFRFRFRFR   mean queue index  arm0 7.50  arm1 7.50
d2 Multirole1   arms 0110011001100110   cards FRFRFRFRFRFRFRFR   mean queue index  arm0 7.50  arm1 7.50
d3 FastBomber1  arms 0110011001100110   cards FRFRFRFRFRFRFRFR   mean queue index  arm0 7.50  arm1 7.50
d4 Fighter1     arms 0110011001100110   cards FRFRFRFRFRFRFRFR   mean queue index  arm0 7.50  arm1 7.50
d5 Multirole1   arms 0110011001100110   cards FRFRFRFRFRFRFRFR   mean queue index  arm0 7.50  arm1 7.50
d6 FastBomber1  arms 0110011001100110   cards FRFRFRFRFRFRFRFR   mean queue index  arm0 7.50  arm1 7.50
```

Equal mean queue index at n = 16 is the ABBA invariant, so a monotonic within-lane drift cancels
exactly. Arm and card are also **orthogonal within every lane** (card period 2, arm period 4), which
is what makes the §4.2 paired-cell contrast possible at all.

### 7.3 The lanes are NOT decorrelated by ordinal — only by wall clock

**Asked: are the two lanes of the same airframe locked in step?** Measured: **yes, completely.** By
queue ordinal, `d1` and `d4` agree on 16/16 arms and 16/16 cards; same for `d2`/`d5` and `d3`/`d6`.
This is structural, not a coincidence — `ArmOf` is a pure function of the queue index and every lane
receives the same queue, so a fleet cannot help but run identical arm sequences.

**Asked: are lanes on opposite arms at the same wall-clock instant?** **Yes** — the launch stagger
supplies the offset the schedule does not. 136 overlapping capture pairs sit on opposite arms, and
at **21:52:13 all six lanes are airborne with both arms live**:

```
d1 arm1  R31-d1-Fighter1-05-oblique-12-rev
d2 arm1  R31-d2-Multirole1-07-oblique-12-rev
d3 arm0  R31-d3-FastBomber1-03-oblique-12-fwd
d4 arm0  R31-d4-Fighter1-04-oblique-12-fwd
d5 arm0  R31-d5-Multirole1-06-oblique-12-fwd
d6 arm0  R31-d6-FastBomber1-08-oblique-12-fwd
```

**This is the in-flight verification of v0.94 that R30 §0.4 could not perform**, and it holds: the
per-aircraft arm map really does let six aircraft fly different arms in the same instant, and the
run board's lines really can legitimately disagree.

**But the two answers together imply a limitation worth writing down.** CLAUDE.md's v0.94 note says a
4-lane fleet card is four independent A/Bs. It is — *against wall-clock confounds*. It is **not**
against **ordinal** confounds: anything that correlates with "the Nth replicate of this batch"
(thermal state, session age, accumulated placement error, a periodic hitch) hits every lane on the
same arm simultaneously, so the six lanes contribute one sample of that confound, not six. The
ABBA invariant still cancels a monotonic version of it; a period-4 version it cannot. Nothing in R31
shows such a confound — §2.2's stalls are the only fleet-wide event and they are aperiodic — but a
future fleet A/B that wants six *independent* ABBAs needs a lane-dependent phase offset in `ArmOf`,
which does not exist today.

### 7.4 The capture cannot lie about its arm — verified

The v0.94 claim under test: `# config` prints the five levers **as flown**, through the same `Arm()`
the law used, rather than the operator's F1 value. Across all 96 captures there are **exactly two
distinct `# config` lines**, and they differ in exactly two fields:

```
… mrFF=1 relLead=1 iStall=1 belowSup=0 alignLead=1 arm=0 armKnob=BelowAlignSuppress …
… mrFF=1 relLead=1 iStall=1 belowSup=1 alignLead=1 arm=1 armKnob=BelowAlignSuppress …
```

`belowSup` == `arm` on **96/96**. The bug where the line printed the global is not present. No `# cfg`
mark appears in any capture, so no live edit landed mid-batch, and no card override was pinned
(`# override` absent on all 96) — the card's `armToggle` drove the sweep without touching anything
else.

---

## §8 — the `FastBomber1` lanes are not the same aircraft, and its numbers are unusable

R30 flagged `FastBomber1` as a stressor whose ratios should not be quoted. R31 flew two of them and
they disagree by 3.8×:

| lane | `obDR12` | `obDL12` | `obUL12` | `obUR12` | **direction ratio** | sd(log) |
|---|---:|---:|---:|---:|---:|---:|
| d3 | 0.7469 | 0.3228 | **0.0410** | 0.1939 | **5.511** | 12.8 % |
| d6 | 0.7742 | 1.2259 | **0.7106** | 0.6249 | **1.462** | 29.9 % |

The other four lanes agree with their twins to within 1 %: `Fighter1` d1 2.981 / d4 3.015;
`Multirole1` d2 4.625 / d5 4.648.

**The covariate is visible.** Terminal `elevErr`, per lane per tag:

| lane | `obDR12` | `obDL12` | `obUL12` | `obUR12` |
|---|---:|---:|---:|---:|
| d3 | −0.109 | +0.043 | −0.023 | −0.026 |
| d6 | **+0.416** | **+1.052** | **+0.719** | **+0.673** |

d6 carries a standing 0.42–1.05° **elevation** residual on all four legs — the R28 mush — while d3
does not. That common elevation term inflates every one of d6's four `terminalOffDeg` values, and
since it is additive on the *smaller* (up) legs proportionally more, it compresses the ratio from
5.5 toward 1.5. Mass, fuel, station count, entry speed and entry altitude are byte-identical between
the two lanes (`massKg` 57 620, `fuelKg` 18 200, 0 stations, both spawned at 4000 m / 250 m/s). The
only recorded difference is spawn position (d6 is the far corner of the second 3-lane launch,
~33 km downrange in Z) and the terminal altitude trend (d6 ends at 3711 m, d3 at 3868 m).

**Consequences, honoured throughout:**

- `FastBomber1`'s replicate CV is 74.6 %, and **no `FastBomber1` number in this document clears its
  own noise floor**. Its arm contrast of 0.980 is reported as *no measurement*, not as a null.
- Its failure to reproduce R30's ×1.389 (R31 `arm=1` reads ×2.868) is **not** evidence against R30 —
  it is evidence that this airframe's ratio depends on which drone flew it.
- This is a **new** observation. R30 attributed `FastBomber1`'s 30 % CV to unexplained common-mode
  wander *within* one lane. R31 shows a large, persistent, *between-lane* component on top of that.
  A single-lane `FastBomber1` batch cannot see it and will report a confidently wrong ratio.
- **`FastBomber1` should be dropped from the oblique grid** or re-entered at a condition it can
  actually hold. Keeping it as a stressor costs nothing; quoting it costs a wrong conclusion.

---

## §9 — what to do next

**Do not sweep another lever on `oblique-12`.** §4.3 shows the metric is defined in a window where
the roll-to-align channel is closed, so no `Arm()` site that lives upstream of the handover can be
tested by it. `RelativeTurnLead`, `AlignRateLead` and `BelowAlignSuppress` are all in that class.

Three things, in order.

1. **Close the §1 defect first, because it is cheap and it invalidates the next batch otherwise.**
   Either rename `BelowAlignSuppress` to name what it selects, or make the `false` branch
   `0f` so the knob means what a reader assumes. If it becomes a true off-switch, R31 must be reflown
   to answer the question it was commissioned for — and the reflight is ~12 minutes.
2. **Test the SAFETY case on a card that can see it, before touching the suppressor.** `elDn`'s
   regime — sustained below-nose hold, `blendWeight` live throughout, bank rocking ±43° — is what
   v0.85 fixed and what R31 cannot observe. `cards/e1-below-suppress.json` and
   `cards/e1-below-control.json` exist; they should be flown as a fleet with
   `armToggle: "BelowAlignSuppress"` (which v0.94 now permits at `count > 1` — see `cards/README.md`
   on the `e*` cards' `"count": 1` no longer being required). Pass = the v0.67 arm does **not**
   re-develop the limit cycle; that would make the R31 benefit shippable. Fail = v0.85 stays and the
   down-step penalty needs a different fix entirely.
3. **Instrument the fine regime, which is where the penalty actually lives.** The open question is
   §6's third bullet: at the handover both hemispheres hold 2.6° and the up leg then closes 94 % of
   it while the down leg closes 70 %. The card needed is one whose *measurement* window is the fine
   regime — a below-nose step that reaches the deadzone early and then holds for 20–30 s — so that
   `terminalOffDeg` is an equilibrium rather than the tail of a transient.

**And if a fix is eventually needed, what it must key on, under the ONE-LAW rule.** Not belowness.
`Clamp01(-alignFrac)` is direction-keyed *by construction*, which is precisely why `bSup` is
untestable as a mechanism: it is perfectly correlated with the factor under study, so any effect it
has is unattributable and any effect it does not have is unfalsifiable. Two candidate keys that are
live physical state and are already measured:

- **The closing rate, not the error.** v0.85 deleted the `(1 − lateralHold)` factor because it keyed
  on the symptom — correct. But its stated job ("a genuine down-lateral keeps its roll-and-pull") is
  the right job and it now has no implementation beyond the `bigTurn` taper. The distinction between
  *"roll-to-align is working"* and *"roll-to-align is feeding itself"* is the sign of `d|azErr|/dt`,
  and the law already computes both terms of it — `_aimAzRateFilt` and `_headingRateFilt`, added in
  v0.78/v0.83 and recorded as `aimRate`/`leadDeg`. A suppressor released while the error is closing
  and held while it is not is direction-agnostic, needs no new probe, and would be a genuine A/B
  lever (unlike belowness, whose two levels are not exchangeable).
- **The measured effectiveness at the residual, not the geometry.** `_yawWeak` / `_yawEffFilt` and
  `_pitchEff` already exist and are already fail-soft per-airframe probes. **Caveat, and it blocks
  quoting `yawEff` today**: `yawEffInst` is only updated when `|outY| > 0.1` (`ChaseController.cs:1032`),
  and in the terminal window only the **down** legs exceed that (`|outY|` 0.10 vs 0.024–0.039). The
  recorded terminal `yawEff` therefore reads 0.036–0.098 on down legs and 0.34–0.51 on up legs, and
  that 10× split is **an artefact of the estimator gate** — the up-leg value is a frozen transient
  reading, not a terminal measurement. It is deliberately excluded from §4.3's table for that reason,
  and must not be quoted as evidence of a direction-keyed plant. Any fix keyed on yaw effectiveness
  needs that gate lowered, or `yawEffInst` recorded, first.

Per CLAUDE.md, **whatever lever is added must be read through `ChaseController.Arm()`** or it is
invisible to the schedule and the A/B will read as "no effect"; `debugtests/test-arm-schedule.py`
fails on exactly that.

---

## What would falsify this analysis

- **The v0.67 arm turns out to re-develop the elDn limit cycle.** Then §5's "no cost observed" is an
  artefact of the card, the ranking in §9 is right for the wrong reason, and the v0.67 form is dead.
- **A true zero-suppression arm collapses the ratio to ~1.** Then §4.3's "`bWt` is 0 in the
  measurement window" is somehow being routed around — e.g. the transient shape it sets is the whole
  story after all — and the handover analysis is wrong about where the error is created. This is the
  cleanest single test of §4.3 and it is one code change away.
- **The 2.6° handover error is an artefact of `FineBankDeadzone`, not a physical hand-off.** Lowering
  `FineBankDeadzone` (2.5°) should then move the handover point in both hemispheres equally. If it
  moves the *down* leg's terminal error disproportionately, the roll channel closing too early **is**
  the defect, and §4.3's "downstream of `bSup`" framing needs qualifying to "downstream of the gate
  that closes the channel `bSup` gates". `FineBankDeadzone` is not currently an `Arm()` site.
- **`FastBomber1`'s lane split reproduces with the lanes swapped.** If a rerun puts the mush on d3
  instead of d6, it is a lane/position property (terrain, floating-origin distance, launch order) and
  not the airframe. If it stays on d6, it is a property of the second launch press. Either answer
  changes what §8 means; neither is available from one batch.
- **`Fighter1`'s 1.3 % ratio noise floor is the instrument, not the aircraft.** R30's §"what would
  falsify" item is unresolved and R31 inherits it: eight replicates agreeing to 0.20 % on a cell is
  worth one check that the placement reset produces an identical *initial condition* rather than an
  identical *trajectory*.

---

## Reproducing

```bash
cd "<game>/BepInEx"
python <repo>/debugtests/compare-runs.py --summary mouseaim-rec-v0.94.0-R31-*.csv
python <repo>/debugtests/scorecard.py            mouseaim-rec-v0.94.0-R31-*.csv   # roll-up past 10 files
# per-arm splits (the `# config` line is the only place the arm is recorded):
python <repo>/debugtests/flightscore.py    $(grep -l "arm=0 armKnob" mouseaim-rec-v0.94.0-R31-*.csv)
python <repo>/debugtests/flightscore.py    $(grep -l "arm=1 armKnob" mouseaim-rec-v0.94.0-R31-*.csv)
python <repo>/debugtests/analyze-wobble.py $(grep -l "arm=0 armKnob" mouseaim-rec-v0.94.0-R31-*.csv)
python <repo>/debugtests/analyze-wobble.py $(grep -l "arm=1 armKnob" mouseaim-rec-v0.94.0-R31-*.csv)
```

The 2×2, the arm contrasts and the paired CIs were produced by importing `scorecard.score_run` per
file and grouping its `terminalOffDeg`; the `bSup`/`bWt`/`outR`/`iYaw`/`bankTR` tables and the
handover analysis are direct column reads (those are recorder columns, not tool metrics). No metric
in this document is a reimplementation and **no analysis tool was modified**. The GATE-CHATTER
reference row in §5.3 is quoted from `debugtests/GATE-CHATTER-FINDINGS.md` §5(a); no R28/R29/R30
artifact was read or modified except R30-FINDINGS.md, read for the metric definition.
