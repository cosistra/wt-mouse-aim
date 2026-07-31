# R33 — the noise floor is set by the CAMERA, not by the entry speed, v0.96.0

**Backlog #52: explain the replicate-scatter widening between R29 (v0.93.0) and R33 (v0.96.0).**
Offline analysis only — nothing was flown for this, no control-law code was touched. Sources:
`debugtests/archive/R29-20260730/` (441 captures, 73 of them `oblique-6-c`) and
`debugtests/archive/R33-20260731/` (77 captures, all `oblique-6-c`), their `.airframe.json`
sidecars, and `LogOutput-R29.log` / `LogOutput-R33.log`. Every number below is reproducible from
`debugtests/captures.db` after `python debugtests/index-captures.py debugtests/archive/R29-20260730
debugtests/archive/R33-20260731 --rebuild`.

| | R29 | R33 |
|---|---|---|
| mod | v0.93.0 | v0.96.0 |
| card | `oblique-6-c` (one of six in the queue) | `oblique-6-c` (the only card) |
| entry | `0.95 x` **`aircraftParameters.cornerSpeed`** (the AI's) | `0.95 x` **`FlyByWire.cornerSpeed`** (the flight model's) |
| lanes | 10 fixed-wing, 6 km apart | same ten, same order, same spacing |
| replicates | 8 per lane | 8 per lane |
| usable | 9 lanes (`Darkreach` flew **1** `oblique-6-c` capture — the R29 dead lane) | 9 lanes (`Darkreach` aborted at rec 50 on `detached ratio 0.029`) |

---

## Verdict

1. **The loop-gain hypothesis is REFUTED.** `VTOLTrainer1` entered **152.0 m/s in both batches** —
   its AI corner speed and its FBW corner speed are both 160 — and flew the scored segments at an
   identical mean airspeed (202.6 m/s), AoA (1.17 vs 1.18°) and `authorityUsedFrac`
   (0.092 vs 0.093), with mean `terminalOffDeg` within 1%. Its replicate stdev still went **2.8–5.1x
   wider**. A mechanism that requires the entry speed to change cannot explain a lane whose entry
   speed did not change. §2.
2. **The real mechanism is high-frequency jitter in the game's own `Aircraft.gForce`**, which the
   control law reads and passes into the stick. It is not an aero quantity: `gForce` is
   `|v − vPrev| / (fixedDeltaTime · 9.81)` taken off the **cockpit part's** rigidbody
   (decompile `:61804-61806`), so under complex physics it carries whatever the multi-rigidbody
   joint solver is doing. Per lane, the change in `terminalOffDeg` stdev tracks the change in that
   jitter with **r = 0.886, log-log slope 0.82, over all 9 lanes and in BOTH directions** — including
   the three lanes that got *quieter*, which the briefing offered as a refutation and which are
   actually the strongest confirmation. §3, §6.
3. **The jitter flipped at ONE instant** — game clock t ≈ 177.5 s, wall 07:33:35, between replicate 2
   and replicate 3 — **up 2.2–12.4x on lanes 1–6 and down to 0.05–0.42x on lanes 7–10, simultaneously**.
   Nothing about the airframes, the card, the entry condition or the mod changed at that instant.
   R29 shows no such event: its per-lane jitter is flat to ±10% across all 8 replicates over 30
   minutes. §4.
4. **The cause is the floating origin, and the floating origin follows the camera.**
   `OriginShift(Vector3 cameraPosition)` (decompile `:19361`) translates **every root GameObject** by
   `−round(cameraPos / originShiftStep) · originShiftStep` whenever the camera passes `threshold`,
   then calls `Physics.SyncTransforms()`. A 10-lane fleet at 6 km spacing necessarily spans
   **8 km to 62 km** from that origin, and the mod's own spawn log records the datum moving *during
   the launch sequence* in both batches. Move the camera and every lane's world-coordinate magnitude
   changes at once — which is exactly the observed signature. §5.
5. **Consequence for the rig: the replicate noise floor is a property of the SESSION, not of the
   airframe.** `CAS1` is the tightest lane in the corpus (cv 0.5–1.2%) and the noisiest
   (cv 2.7–4.4%) in the same card two days apart; `FastBomber1` goes the other way by 10–30x. Any
   per-airframe noise table that does not carry the jitter beside it is a table of one session's
   camera position. §9 gives both regimes and the MDE for each.
6. **Correction to the briefing's premise.** Four of the ten stated entry speeds are wrong, and two
   of them reverse the sign of the change. §1.

---

## 1. The entry-speed table in the brief is wrong for four airframes

`entry_v_to` off the `# entry` header, confirmed against the `[drone] … spawned at … N m/s` lines in
both logs:

```sql
SELECT run_tag, airframe, round(avg(entry_v_to),1) v, round(avg(sc_cornerSpeed),1) ai,
       round(avg(sc_fbwCornerSpeed),1) fbw
  FROM captures WHERE card='oblique-6-c' AND run_tag IN ('R29','R33') GROUP BY 1,2;
```

| airframe | AI corner | FBW corner | R29 entry | R33 entry | change | briefing said |
|---|---|---|---|---|---|---|
| Fighter1 | 180 | 160 | 171.0 | 152.0 | −11% | 171→152 ✔ |
| Multirole1 | 180 | 160 | 171.0 | 152.0 | −11% | 171→152 ✔ |
| SmallFighter1 | 180 | 155 | 171.0 | 147.3 | −14% | 171→147.3 ✔ |
| trainer | 160 | 130 | **152.0** | 123.5 | −19% | ✘ said 171→123.5 |
| **VTOLTrainer1** | 160 | **160** | **152.0** | **152.0** | **0%** | ✘ said 171→152 |
| CAS1 | 200 | 160 | 190.0 | 152.0 | −20% | 190→152 ✔ |
| COIN | 90 | 110 | **85.5** | 104.5 | **+22%** | ✘ said 171→104.5 |
| EW1 | 120 | 130 | **114.0** | 123.5 | **+8%** | ✘ said 171→123.5 |
| FastBomber1 | 180 | 200 | 171.0 | 190.0 | +11% | 171→190 ✔ |
| Darkreach | 180 | 100 | 171.0 | 95.0 | −44% | 171→95 ✔ |

The corrections matter: **`VTOLTrainer1` is a matched control** (identical entry speed, everything
else changed), and COIN/EW1 moved *up* only 8–22%, not down from 171.

---

## 2. The matched control refutes loop gain

`VTOLTrainer1`, `obDL6`, whole-segment means from the raw rows:

| | R29 (n=8) | R33 reps 3–8 (n=6) |
|---|---|---|
| entry speed | 152.0 | 152.0 |
| segment mean airspeed | 202.6–202.8 | 202.5–202.6 |
| segment mean AoA | 1.17° | 1.17–1.18° |
| `authorityUsedFrac` | 0.092 | 0.093 |
| mean `terminalOffDeg` | 0.2318 | 0.2304 |
| **stdev `terminalOffDeg`** | **0.0036** | **0.0118** |

`targetPitchAngVel = pitch · gLimitPositive · 9.81 / max(V, 0.75·Vc)` (`:64859`) is unchanged for
this lane in every term. The same holds on the other three tags (stdev 0.0030→0.0170,
0.0025→0.0105, 0.0035→0.0113) with the mean moving ≤ 2.9%.

The loop-gain story is not merely incomplete — **it is not needed anywhere**. The two airframes the
brief flagged as contradictions (COIN, EW1 dropped most in speed, got quieter) are contradictions
only under the brief's wrong speeds; under the real ones they went *up* in speed. But the mechanism
in §3 explains their quieting without reference to speed at all, and explains VTOLTrainer1 too,
which no speed-based mechanism can.

---

## 3. What actually changed: `gForce` jitter

New per-segment metric `gJitterG` (`scorecard.aoa_g_metrics`, added by this investigation, §10) —
the mean |Δg| between consecutive recorder samples. Per lane, mean over all non-`arm` segments:

| lane | airframe | R29 (all 8 reps) | R33 reps 1–2 | R33 reps 3–8 | ratio 33/29 | `terminalOffDeg` stdev ratio (geo. mean over 4 tags) |
|---|---|---|---|---|---|---|
| 1 | Fighter1 | 0.100 | 0.089 | 0.246 | **2.47** | **2.87** |
| 2 | Multirole1 | 0.157 | 0.109 | 0.347 | 2.21 | 2.00 |
| 3 | SmallFighter1 | 0.073 | 0.059 | 0.197 | 2.68 | 2.11 |
| 4 | trainer | 0.059 | 0.030 | 0.199 | 3.36 | 2.90 |
| 5 | VTOLTrainer1 | 0.042 | 0.016 | 0.285 | 6.72 | 3.97 |
| 6 | CAS1 | 0.015 | 0.046 | 0.183 | 12.41 | 4.37 |
| 7 | COIN | 0.283 | 0.242 | 0.013 | **0.05** | **0.15** |
| 8 | EW1 | 0.192 | 0.252 | 0.048 | 0.25 | 0.25 |
| 9 | FastBomber1 | 0.321 | 0.405 | 0.104 | 0.32 | 0.05 |
| 10 | Darkreach | 0.311 | 0.386 | 0.131 | 0.42 | (n too small) |

**Regression of log(stdev ratio) on log(jitter ratio) over the nine usable lanes: slope 0.823,
r = 0.886, r² = 0.785.** Both directions, one line.

`gForce` is not lift. `Aircraft.LocalSimFixedUpdate` (`:61802-61806`):

```csharp
accel = ((velocityPrev == Vector3.zero) ? Vector3.zero : (CockpitRB().velocity - velocityPrev));
velocityPrev = CockpitRB().velocity;
accel /= Time.fixedDeltaTime * 9.81f;
gForce = accel.magnitude;
```

Three properties matter. It is a **one-fixed-step finite difference**, so any solver noise is
divided by `dt` (0.0167 s) and appears amplified ~60x. It is a **magnitude**, so noise biases the
mean upward — and it does: VTOLTrainer1 `obDL6` mean g goes 0.41 → 0.64 across the flip while AoA
and airspeed are unmoved, which is aerodynamically impossible and is the tell that this is
measurement noise, not load. And it is read off **`CockpitRB()`**, a *part* rigidbody under complex
physics, joint-coupled to every other part.

The control law reads the same physics through `pitchRate`, `rollRate` and `_rollRateFilt`, and
those move with it: on VTOLTrainer1 `obDL6`, mean |Δ pitchRate| per sample goes 0.0014 → 0.0083 and
mean |Δ outP| goes 0.0017 → 0.0057 across the flip. The noise reaches the stick.

---

## 4. The flip: one instant, ten lanes, two directions

`gJitterG` per capture, in replicate order (from `captures.db`):

```
lane  airframe        R33 per-replicate gJitterG
 1    Fighter1        0.075, 0.103, 0.155, 0.252, 0.254, 0.279, 0.275, 0.263
 2    Multirole1      0.098, 0.119, 0.223, 0.332, 0.362, 0.392, 0.385, 0.386
 3    SmallFighter1   0.059, 0.058, 0.133, 0.184, 0.214, 0.211, 0.226, 0.213
 4    trainer         0.032, 0.028, 0.127, 0.190, 0.214, 0.212, 0.224, 0.225
 5    VTOLTrainer1    0.015, 0.016, 0.210, 0.314, 0.300, 0.316, 0.279, 0.292
 6    CAS1            0.047, 0.045, 0.172, 0.165, 0.190, 0.207, 0.182, 0.181
 7    COIN            0.243, 0.242, 0.025, 0.011, 0.011, 0.011, 0.011, 0.011
 8    EW1             0.242, 0.261, 0.050, 0.050, 0.048, 0.048, 0.048, 0.045
 9    FastBomber1     0.416, 0.395, 0.105, 0.104, 0.103, 0.101, 0.106, 0.102
10    Darkreach       0.387, 0.386, 0.127, 0.134   (lane died at rec 50)
```

Replicate 3 is the transition capture on every lane — an intermediate value, because the event
lands *inside* it. Locating the step inside each replicate-3 capture (16-sample sliding |Δg|, largest
log-ratio step) puts it at game clock **t = 177.25** (lanes 2, 5, 6), **177.75** (lane 3) and
**178.25** (lanes 8, 9): one instant, ±0.5 s, across lanes 44 km apart. Lanes 7 and 10 read later
(192.25 / 190.75) only because their step detector locks onto the larger of two nearby edges.

Inside lane 5's replicate 3 the onset is abrupt — 2 s wide:

```
t=176.00  obDR6  gJitter 0.005   gMean 0.30
t=177.25  obDR6  gJitter 0.103   gMean 0.36
t=178.50  obDR6  gJitter 0.431   gMean 0.70     <- and it never comes back down
```

**R29 shows no event at all.** Per-lane `gJitterG` is flat to ±10% across all 8 replicates spanning
30 minutes (e.g. CAS1: 0.015 × 8 identical to three decimals; FastBomber1: 0.305–0.344). Its lanes
1–6 were the quiet ones and 7–10 the noisy ones for the whole batch — the same partition R33 started
with and then inverted.

---

## 5. Why: the floating origin follows the camera

`OriginShift` (decompile `:19361`, in the class that owns `Datum`):

```csharp
private Vector3 ShiftPosition(Vector3 cameraPosition) =>
    new Vector3(Mathf.Round(cameraPosition.x / originShiftStep) * originShiftStep, … y, … z);

public void OriginShift(Vector3 cameraPosition) {
    if (EditorHandle.DraggingHandle || !ShouldShift(cameraPosition)) return;   // |cam.x|,|y|,|z| > threshold
    Vector3 vector = ShiftPosition(cameraPosition);
    foreach (GameObject root in roots) root.transform.position -= vector;      // EVERY root
    Datum.AfterOriginShift();
    Physics.SyncTransforms();
}
```

So the Unity world origin is re-centred on **the operator's camera**, in quantized steps, whenever
the camera drifts far enough. Everything the physics solver works in — every rigidbody position,
every `FixedJoint` anchor — is expressed relative to that origin in single precision.

The harness's own log records the datum moving, by accident. `TestDrone` (`TestDrone.cs:326`)
computes each lane's spawn y as `new GlobalPosition(0, SpawnAlt(), 0).ToLocalPosition().y` **at that
lane's launch instant**, and prints it. That value is a direct readout of the datum offset:

```
R33   #1..#6  local y = 2400      #7,#8,#9 = -32     #10 = +32      (one shift, mid-launch)
R29   #1..#3  local y = 2144      #4..#6  = 3168     #7,#8 = -32   #9,#10 = +32   (two shifts)
```

The shifts are 1024, 3200 and 2432 m — all multiples of 64, i.e. `originShiftStep = 64` (or 32).
Both batches converge to a datum whose y sits at ~4000 m MSL by lane 7, because the camera climbed
with the drones.

The lane geometry then guarantees a gradient: `pos = _laneBase + _laneRight * (AbeamM + LaneM*slot)`
with `AbeamM = 8000`, `LaneM = 6000` puts lane *k* at **8 + 6k km** from the origin — 8, 14, 20, 26,
32, 38, 44, 50, 56, 62 km — in *both* batches (the log confirms it: R29 lane 1 at (7396, ·, −2924)
= 8.0 km, lane 10 at (57508, ·, −23043) = 62 km; R33 lane 1 at (−4810, ·, 6439) = 8.0 km, lane 10 at
(−37292, ·, 49577) = 62 km). Both batches' initial jitter partitions at the same place — lanes 1–6
quiet, 7–10 noisy — with a 5x step between lane 6 and lane 7. When the origin moved in R33 at
t ≈ 177.5, the partition inverted, and the post-flip ordering *within* the newly-quiet group is
monotonic in lane index (0.013 < 0.048 < 0.104 < 0.131 for lanes 7 → 10), i.e. increasing with
distance from the new origin.

**What is established:** the jitter is a function of the world-coordinate frame, that frame is set by
the camera, and it changes mid-batch without warning. **What is not established:** the exact numerical
law. It is not a clean IEEE-754 exponent boundary — R29's lane 6 sits at |x| = 35 236 m, past the
32 768 m boundary, and is the *quietest* lane in that batch. Candidates left open are the solver's
own tolerance scaling and the fact that the aircraft fly ~8 km per replicate from their anchors.
Pinning it needs an experiment (backlog, §11), not more of this corpus.

---

## 6. Why it reaches `terminalOffDeg` — and why `rmsPointingErrorDeg` barely moves

`terminalOffDeg` is the mean of `off` over the last `TERMINAL_WINDOW_S = 1.0` s — about 16 samples.
An additive high-frequency noise term survives a 16-sample mean but not a 128-sample one. Recomputing
the same statistic at other window lengths, on `obDL6`:

| window | R29 VTOL sd | R33 VTOL r3–8 sd | ratio | R29 Fighter1 sd | R33 Fighter1 r3–8 sd | ratio |
|---|---|---|---|---|---|---|
| 0.5 s | 0.0035 | 0.0181 | 5.2x | 0.0036 | 0.0164 | 4.6x |
| **1.0 s** (shipped) | 0.0036 | 0.0118 | **3.3x** | 0.0035 | 0.0112 | **3.2x** |
| 2.0 s | 0.0034 | 0.0050 | 1.5x | 0.0027 | 0.0079 | 2.9x |
| 4.0 s | 0.0041 | 0.0044 | 1.1x | 0.0018 | 0.0052 | 2.9x |
| 8.0 s (whole segment) | 0.0125 | 0.0115 | 0.9x | 0.0294 | 0.0350 | 1.2x |

VTOLTrainer1's excess **vanishes entirely** by a 4 s window: the trajectory is the same, only the
one-second terminal sample is noisier. That is the signature of additive measurement/solver noise,
not of a different flight path. It is also why `rmsPointingErrorDeg` (a whole-segment RMS) moved so
little in the `--diff` table while `terminalOffDeg` moved 3–6x.

Fighter1 does *not* fully collapse (2.9x still at 4 s), so that lane carries some genuine
trajectory-level variance on top — consistent with its being the one lane whose entry speed also
moved 11% and whose mean shifted 8–21%.

**Do not "fix" this by widening `TERMINAL_WINDOW_S`.** The metric's definition is "how badly it
missed when time ran out"; widening it to hide solver noise would change what it measures and
would break comparability with the whole corpus. The fix is to control or measure the jitter (§9).

---

## 7. A one-card batch has a cold first replicate; a six-card batch does not

Separate, smaller, and worth knowing on its own. `# entry` provenance, `VTOLTrainer1`:

```
R29 rep 1..8   v_from 240.8 (identical to 0.1 m/s on all eight)  alt_from 3818.0  snapBack 7711..7723
R33 rep 1      v_from 152.0                                      alt_from 4000.0  snapBack 0.0
R33 rep 2      v_from 234.4                                      alt_from 3866.0  snapBack 7514.9
R33 rep 3..8   v_from 236.3 (identical)                          alt_from 3870.x  snapBack 7627..7636
```

In R29 `oblique-6-c` was card 4 of 6, so **every** replicate was preceded by a full card and started
from the same converged state. In R33 it is the only card, so replicate 1 is the *spawn* (no prior
card, no placement to snap back from) and replicate 2 inherits replicate 1's terminal state. By
replicate 3 the chain has converged.

Effect size, `terminalOffDeg` stdev with replicates 1–2 dropped:

| lane | tag | R33 all 8 | R33 reps 3–8 |
|---|---|---|---|
| FastBomber1 | obUR6 | 0.4087 | **0.0068** |
| FastBomber1 | obUL6 | 0.1515 | 0.0013 |
| COIN | obDR6 | 0.0225 | 0.0023 |
| EW1 | obUL6 | 0.0091 | 0.0034 |

**FastBomber1's headline ±199% is replicates 1–2, not the law.** On replicates 3–8 it is the most
improved lane in the batch — stdev 0.0013–0.0068 against R29's 0.0462–0.0820, a 10–30x *reduction*.
The lanes where dropping 1–2 changes nothing (Fighter1, Multirole1, VTOLTrainer1, trainer, CAS1,
SmallFighter1) are exactly the lanes that got noisier in §3, which is how the two effects separate.

**Rule for the runbook: on a single-card batch, budget `repeat` for two throwaway replicates, or
score from replicate 3.** `compare-runs.py` and `index-captures.py --diff` do not know this and will
happily pool them.

---

## 8. Ruled out

| candidate | evidence |
|---|---|
| entry speed / loop gain | §2 — VTOLTrainer1 unchanged at 152.0 m/s, 3.3x wider |
| airframe damage | 77/77 R33 captures have `dmgFrac == 0` on every row. The single abort (Darkreach, 0.029) killed its own capture; nothing else ever detached |
| frame hitches | zero `[drone] frame hitch` lines after the launch line in `LogOutput-R33.log`; all seven are pre-launch |
| A/B arm | `oblique-6-c` declares no `armToggle`; `arm` / `arm_knob` NULL on all 150 captures |
| segment length | `samples` 128.0–128.3 and `duration_s` 7.93–7.95 in both batches, every cell |
| flight condition | segment-mean airspeed, AoA and `authorityUsedFrac` match to <1% on the matched lane (§2) |
| `pEff` / probe noise | `pEff` segment means match (0.550 vs 0.543–0.558 on VTOLTrainer1 `obDL6`) |
| mod version | v0.94's `Arm()` returns `e.Value` with no assignment, and R30–R32 flew v0.94 without this; the flip is mid-batch on one version |
| `Aircraft.CheckPhysicsLod` (10 km camera-distance physics LOD, `:61819`) | **dead code** — one occurrence in 181 878 lines, its own declaration, zero call sites. Same shape as `GLimiter` in R32. Checked because it was the obvious suspect; it is not the mechanism |

---

## 9. The usable noise floor, and what to do with it

**The floor is not an airframe property.** Every airframe below appears in the corpus with two
noise floors up to 20x apart, on the same card, decided by which side of the flip it was on.
So the table carries both regimes; take the one whose `gJitterG` matches the batch you are about
to size.

`n` is replicates **per arm** to detect a **10% shift in mean `terminalOffDeg`** at α = 0.05
two-sided, 80% power: `n = 2·(1.96+0.84)²·(cv/0.10)² ≈ 1568·cv²`, floored at 2. An ABBA lane needs
`2n` replicates. `cv` is `stdev/|mean|` per (airframe, tag); *median* is the typical cell, *max* is
the worst of the four and is what you must budget for if the card is scored on all four arms.

| lane | airframe | `gJitterG` | cv% med / max | **n per arm** | `gJitterG` | cv% med / max | **n per arm** |
|---|---|---|---|---|---|---|---|
| | | **R29 regime** | | | **R33 reps 3–8 regime** | | |
| 1 | Fighter1 | 0.100 | 2.85 / 3.75 | 3 | 0.246 | 9.67 / 14.52 | 34 |
| 2 | Multirole1 | 0.157 | 1.91 / 2.53 | 2 | 0.347 | 3.57 / 6.62 | 7 |
| 3 | SmallFighter1 | 0.073 | 1.32 / 2.17 | 2 | 0.197 | 2.59 / 2.85 | 2 |
| 4 | trainer | 0.059 | 2.57 / 10.05 | 16 | 0.199 | 13.25 / 16.44 | 43 |
| 5 | VTOLTrainer1 | 0.042 | 1.40 / 2.14 | 2 | 0.285 | 5.87 / 6.55 | 7 |
| 6 | CAS1 | 0.015 | 0.77 / 1.24 | 2 | 0.183 | 3.45 / 4.37 | 3 |
| 7 | COIN | 0.283 | 26.36 / 32.16 | 163 | 0.013 | 4.24 / 5.24 | 5 |
| 8 | EW1 | 0.192 | 9.71 / 14.15 | 32 | 0.048 | 2.16 / 2.29 | 2 |
| 9 | FastBomber1 | 0.321 | 22.25 / 56.29 | 497 | 0.104 | 1.38 / **44.35** | 309 |

**Best-case per airframe** (take the quieter regime, use the max-cv column, ABBA replicates = 2n):

| airframe | best cv% (worst tag) | n / arm | **replicates per lane** |
|---|---|---|---|
| CAS1 | 1.24 | 2 | **4** |
| SmallFighter1 | 2.17 | 2 | **4** |
| VTOLTrainer1 | 2.14 | 2 | **4** |
| EW1 | 2.29 | 2 | **4** |
| Multirole1 | 2.53 | 2 | **4** |
| Fighter1 | 3.75 | 3 | **6** |
| COIN | 5.24 | 5 | **10** |
| trainer | 10.05 | 16 | **32** |
| FastBomber1 | 44.35 (obUR6) | 309 | **618** — see caveat |

Two caveats that decide how to read the last two rows:

- **`FastBomber1/obUR6` and `trainer/obUR6` have near-zero means** (0.0154 and 0.0424 deg). A 10%
  *relative* effect there is 0.0015–0.004 deg, and the recorder writes `off` at **0.01 deg**
  resolution, so a 16-sample terminal mean quantizes at 0.00059 deg. Asking for 10% on a cell whose
  mean is 15 quanta wide is not a noise problem, it is a **units** problem: score those cells on an
  *absolute* threshold, or drop `obUR6` from the FastBomber1/trainer analysis. On their other three
  tags FastBomber1 needs **n = 2** and trainer **n ≤ 16**.
- **Read the shipped-grid A/B results of R28–R31 with this in mind.** Any cell whose declared effect
  is smaller than `2.8·cv·√(2/n)` in its own batch's regime was never detectable, and a batch that
  changed regime mid-run has two populations in one column.

**Practical procedure for the next A/B**, in order:

1. Re-index and read `gJitterG` per lane *before* interpreting anything:
   `SELECT c.airframe, c.drone, avg(s.gJitterG) FROM segments s JOIN captures c ON c.id=s.capture_id
    WHERE c.run_tag='RNN' AND s.excluded=0 GROUP BY 1,2;`
2. **A lane whose `gJitterG` is not constant across its replicates was not one experiment.** Check
   the per-replicate list, not just the mean; the flip is invisible in the mean.
3. Park the camera. It sets the datum, the datum sets every lane's jitter, and nothing in the
   harness records where it was. Spectating a drone means the datum tracks *that drone*.
4. Prefer `rmsPointingErrorDeg` (whole-segment) over `terminalOffDeg` when the jitter is high — §6
   shows the excess variance is concentrated in the short terminal window.
5. Score from replicate 3 on a single-card batch (§7).

---

## 10. Tool change

One metric, in the tool that already owns every metric — `scorecard.aoa_g_metrics`:

- **`gJitterG`** — mean |Δg| between consecutive samples, per segment. Deliberately orthogonal to
  `gPeak`/`gSustained`: the whole point is that a lane's jitter moved 12x at an unchanged load. It
  is an *aliased* read (the recorder samples at ~15–20 Hz, far below any structural rate), so it is
  a comparator, not a spectrum — comparable across lanes and batches, not convertible to a frequency.
  `None` on a 1-sample segment; `skipped["gJitterG"]` on a capture with no `g` column.
- `scorecard.py --selftest` asserts the exact arithmetic (`|8−3|+|4−8|+|5−4| over 3 = 10/3`), that a
  *smooth* g at the same peak and median reads exactly 0, the 1-sample `None`, and the `skipped` path.
- `index-captures.py` picks it up with no change — segment metric columns are dynamic and come
  straight from `score_run()`. `captures.db` now has 50 metric columns; re-index to get it on old
  batches (`--rebuild` for captures already indexed).

Nothing else was touched. No control-law code, no recorder column, no card.

---

## 11. Backlog

- **#52a — pin the numerical law behind §5.** One card, one airframe, N lanes, flown twice with the
  operator's camera deliberately parked at two known places; `gJitterG` vs each lane's distance
  from the datum. Cheap (one batch, ~5 min) and it converts §5 from "established mechanism, unknown
  law" to a rule the runbook can state.
- **#52b — record the datum.** `Datum.originPosition` is a public static `Vector3`. One `# datum`
  header line per capture (or a column, if it can move mid-capture — §4 says it can) would make this
  entire investigation a `GROUP BY` next time. Today the only trace is an accident of the spawn log.
- **#52c — the corner-speed comparability note stands but for a different reason.** CLAUDE.md says
  R29-and-earlier corner-relative captures are not comparable with later ones because the entry speed
  moved. True, but §3 says the *dominant* term between those two batches was not the entry speed at
  all. Neither batch is a clean measurement of the other's question.
- **#52d — `oblique-6-c` on `FastBomber1`/`trainer` should not be scored on `obUR6` in relative
  terms** (§9 caveat). Either the card's up-right leg is too easy for those airframes or the metric
  needs an absolute floor; one line in `cards/README.md` either way.
