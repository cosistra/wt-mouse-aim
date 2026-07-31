# R32 — the Darkreach departure is an AoA/authority defect on one airframe, and the game has no G governor, v0.94.0

**The R29 follow-up the `darkreach-05` card was written for**, plus a decompile audit run beside it.
Five lanes of `Darkreach`, one card, 16 replicates each = **63 captures**, 37 868 rows, one
unattended run, ~9.7 min wall. Source:
`<game>/BepInEx/mouseaim-rec-v0.94.0-R32-d{1..5}-Darkreach-{01..63}-darkreach-05-*.csv`
(+ `.airframe.json` sidecars), `mouseaim-anomalies-v0.94.0-R32-20260730-220701.log`, and
`LogOutput-R32.log`. Archived at `debugtests/archive/R32-20260730/`.

| | |
|---|---|
| airframe | `Darkreach` (SFB-81) on all five lanes — the only airframe that has ever shown this |
| card | `darkreach-05` — the `oblique-05` diamond, 0.35° legs, 6 s `arm` + 4 × 8 s |
| entry | **171 m/s / 4000 m**, throttle pinned 0.70, absolute (not corner-relative — see the card `note`) |
| A/B arm | **none** — one card, one test, establish the precursor before sweeping anything against it |
| launch | five presses of the spawn key, 1 lane each, 6 km lane spacing |
| outcome | **18 of 63 captures departed**; 3 of 5 drones ended `despawned (pilot killed)` |

The two findings below are **root-cause** findings and they matter more than the batch result. Both
were reached by reading the decompile against the captures, and both **correct things this project
has been saying**. §1 and §2 are the corrections; §3 onward is R32 itself.

---

## Verdict

1. **The game has NO G governor.** `ControlsFilter.GLimiter` is dead code — the identifier occurs
   **exactly once in 181 878 lines** of the 0.34 decompile, as a `protected class` declaration, never
   as a field, never instantiated, never called; its `LimitG(...)` method has zero call sites.
   CLAUDE.md's Conventions line — *"No mod-side G-limiter — the game's stability control governs"* —
   is **false for G**. What actually governs is the FBW's `gLimitPositive` term inside
   `targetPitchAngVel`, which is a *rate command shaped by* g-limit, not a governor, and §1 shows the
   two places it stops applying. See §1.
2. **Over-G damages the PILOT, never the airframe**, so the standing theory that the law was
   *bending airframes* is wrong — **and I told the user that theory earlier. It was wrong. Retracted
   here.** `Pilot.TakeGForceDamage` (`:85779`) fires at `magnitude > 20f` g and applies
   `(sqrGForces − 400f) * 0.007f` as `impactDamage` to **one part index — the pilot's own**
   (`Unit.Damage(byte index, DamageInfo)`, `:88655`). No airframe structural-G path exists anywhere
   in the decompile. R32 confirms it empirically: three drones despawned `(pilot killed)` with the
   aircraft still flying. See §2.
3. **The R29 precursor REPRODUCED**, which is what the card was for. From replicate ~32 onward, the
   roll-to-align channel commands **34–56° of `targetBank` at |`azErr`| < 5°** (25–28° at |`azErr`|
   < 2°, the card's stated pass/fail line) against a card whose largest demanded step is **0.35°**.
   Recs 01–31 show **zero**. See §4.
4. **The departure is an AoA/authority failure, not a G failure.** At the moment it starts the mod is
   commanding |`outP`| ≤ 0.24, the plant delivers pitch rate in the **opposite** direction, and the
   FBW is asking for `fbwTgtPR` −0.050 rad/s while measuring `fbwPR` +0.60 — a **12×** overshoot
   (batch median on departed captures **7.7×**, p90 **13.0×**, max **28.2×**; on clean captures
   1.56×). The G is a *consequence*: 26.9 g happens later, at 80 m/s and AoA −77°, falling. See §5.
5. **The law's entire departure response is a graded stand-down, and it runs out.** `qSched` rails at
   its hardcoded **0.300** floor (`ChaseController.cs:1255`) on **100.0 %** of the 2 314 rows past
   |AoA| 20°; `aoaRecover` — the one term whose job is to fly the nose back inside the envelope — is
   multiplied by `_pitchEff` (`:1557`), which reads 0.03–0.14 throughout; `pErrTerm` is multiplied by
   `_pitchEff` with no floor below `PEffRevThresh`. Every one of those reduces authority. **There is
   no recovery mode, only de-authorisation**, and on this airframe nothing else recovers it. See §6.
6. **The game's own alpha limiter is structurally absent where the cards fly.** It is gated
   `if (num2 < 1f)` (`:64860`), `num2` = q / q_corner against the FBW's `cornerSpeed = 100`. At the
   card's entry condition `num2 = 2.03`. Across R32, `num2 < 1` on **2.3 %** of rows — so the limiter
   is inactive on 97.7 % of them, and on **86.3 %** of the 5 541 rows that exceed this airframe's own
   10° `alphaLimiter`. See §1.3.
7. **The airframe, not the law, is what is unusual — but the "only `flightAssist = 0`" claim is
   WRONG.** Two of the ten airframes flown ship `assist=0`: `Darkreach` **and `EW1`**, and `EW1`
   scores mid-band (ROADMAP already excludes `assist=0` as the R28 spread's cause, for that reason).
   What *is* unique to the Darkreach is `gLimitPositive = 4`, the lowest of the ten, on the heaviest
   airframe flown (**105.4 t as flown**; AIRFRAMES.md's 54 311 kg is the Encyclopedia figure, before
   25 t of fuel and 13 t of ordnance). See §7.
8. **The placement-tick reset defect (#23) is NOT benign here, and the standing note saying it is
   should be narrowed.** At `tSeg = 0.000` of the 58 placed captures, `|rollRate|` has median 0.753
   (matching R28's 0.725) but is **bimodal**: 19 of 58 above 5 rad/s, max 54.2; `|leadDeg|` reaches
   **314°**, `|headingRateFilt|` **483 °/s**, and `|outP|` **rails at 1.000 in 15 of 58**. It does
   **not** "decay inside the 6 s `arm`" on this airframe — it departs the aircraft inside it. See §8.
9. **Do NOT add a mod-side G-limiter.** It would clip the symptom (§4) while leaving the cause (§5–6)
   in place, and it has nothing to protect: the airframe cannot be over-G'd (§2). See §9.

---

## §1 — the game has no G governor

### 1.1 `GLimiter` is dead code

```
$ grep -c "" Assembly-CSharp.decompiled.cs
181878
$ grep -n "GLimiter" Assembly-CSharp.decompiled.cs
65069:	protected class GLimiter
$ grep -n "LimitG(" Assembly-CSharp.decompiled.cs
65104:		public void LimitG(ControlInputs inputs, Aircraft aircraft, float inverseDynamicPressure)
$ grep -n "gLimiter" Assembly-CSharp.decompiled.cs      # (no output)
```

One occurrence of the type name — its own declaration. No field of that type, no `new GLimiter()`,
no call to `LimitG`. It is a `[Serializable]` nested class inside `ControlsFilter` with
`Enabled`/`gLimit`/`limitStrength`/`predictionStrength`/`predictionTime`/`smoothing`/`rollonRate`/
`rolloffRate` — a feature that was designed, serialized, and never wired up. Reachable only if some
prefab holds a serialized instance, and nothing in code reads one.

**Consequence for this repo:** CLAUDE.md's Conventions bullet ends *"No mod-side G-limiter — the
game's stability control governs."* For pitch **rate** that is true; for **G** it is not. The exact
replacement text is in the DOCS block of the handoff.

### 1.2 what actually shapes G

`FlyByWire.Filter` (`:64838`) computes

```csharp
float num  = cornerSpeed * cornerSpeed * 1.225f;                       // :64844  (FBW's OWN cornerSpeed)
float num2 = aircraft.speed * aircraft.speed * aircraft.airDensity / num;   // :64845  = q / q_corner
...
targetPitchAngVel = inputs.pitch * gLimitPositive * 9.81f / Mathf.Max(aircraft.speed, cornerSpeed * 0.75f);  // :64859
```

That is a *rate* command whose scale happens to be `gLimit·g/V`. Nothing measures achieved G and
nothing rolls the command back when G is exceeded. The mod already reconstructs this same expression
as `rpsRef` (`ChaseController.cs:1149`) — correctly — and the `omegaMax` cap built on it is the
project's only G-shaped protection. It is a **feed-forward cap on the demand**, not a governor on the
outcome, and R32 is a demonstration of the difference: the demand was capped and the aircraft still
reached 26.9 g in R29 and 9.2 g in R32, because the rotation was not coming from the command.

### 1.3 and the alpha limiter is gated off exactly where the cards fly

```csharp
if (num2 < 1f)                                                          // :64860
{
    targetPitchAngVel *= Mathf.Clamp(num2, 0.3f, 1f);
    ... if (Mathf.Abs(f) > alphaLimiter && Mathf.Sign(f) == Mathf.Sign(targetPitchAngVel))
            targetPitchAngVel *= 1f - Mathf.Clamp(value, 0f, 10f) * alphaLimiterStrength;
}                                                                       // :64870
```

The alpha limiter is **inside** the sub-corner-q branch. Above corner q there is no alpha protection
at all from the game. `Darkreach`'s FBW `cornerSpeed` is **100** (not the roster's 180, which is
`aircraftParameters.cornerSpeed`, a different quantity), so at the card's 171 m/s / 4000 m entry
`num2 = 171² × 0.8506 / (100² × 1.225) = 2.03`.

Measured across all 37 868 R32 rows: `num2 < 1` on **2.3 %**, `num2 < 1.2` on **3.7 %**. Of the
**5 541 rows (14.6 %)** where |AoA| exceeded this airframe's own 10° `alphaLimiter`, **86.3 %** had
the limiter structurally inactive.

There is a second consequence of the same `num2` at `:64847`:
`limitFactorSmoothed → (stabilityAssist || num2 > 1.2) ? 1 : 0`. With `flightAssist = 0` and
`num2 = 2.03`, the Darkreach flies the **protected g-based law with the alpha limiter removed** —
the one combination that has neither guard. The mod mirrors this branch faithfully at
`ChaseController.cs:1151`, so it is not a probe error; it is what the game does.

---

## §2 — over-G damages the pilot, not the airframe

`Pilot`'s fixed step (`:85857`):

```csharp
accel = ((velocityPrev == Vector3.zero) ? Vector3.zero : (unitPart.rb.velocity - velocityPrev));
velocityPrev = unitPart.rb.velocity;
accel /= Time.fixedDeltaTime * 9.81f;
float magnitude = accel.magnitude;
if (magnitude > 20f) TakeGForceDamage(magnitude * magnitude);
```

```csharp
public void TakeGForceDamage(float sqrGForces)          // :85779
{
    float num = (sqrGForces - 400f) * 0.007f;
    if (aircraft != null) aircraft.Damage(index, new DamageInfo(0f, 0f, 0f, num));
    ...
}
```

`index` is the **pilot's own part index**, and `Unit.Damage(byte index, DamageInfo)` (`:88655`) routes
to that one part. `DamageInfo(0, 0, 0, num)` is pure `impactDamage`. Threshold: 20 g; at 26.9 g the
damage per fixed step is `(723 − 400) × 0.007 = 2.26`.

**There is no structural-G path for the airframe anywhere in the decompile.** Grepping for other
`TakeGForceDamage` call sites returns exactly the one above.

**R32 confirms it in flight.** Three lanes ended `[drone] #N despawned (pilot killed)` (d5 at log
:1528, d2 at :1551, d4 at :1632) — a despawn reason that only exists because `OnPilotStep` checks
`p.dead || p.ejected`. In every case the *aircraft* was still a live, undamaged GameObject: the CSV
before it reads `# stop … reason=abort: altitude floor (500 m MSL)`, so the card had already quit
and the drone was on the built-in level-hold when the pilot died. Sidecar `aeroPartCount` is **35 on
all 63 captures** and `massKg` is constant to 5 kg — the airframe took no damage at all across the
whole batch.

**Retraction.** The "the law is bending airframes" theory was mine, it was stated to the user, and it
is wrong. The mod cannot break an airframe with G. What it can do is put the pilot past 20 g, and
what it did in R32 is put the *aircraft* somewhere the pilot then reached 20 g on the way down.

---

## §3 — what R32 flew, and the shape of what happened

Five single-lane launches, `darkreach-05` × 16, no arm schedule. Frame time was clean throughout
(mean **16.70 ms**, zero rows over 33 ms except two 92–103 ms hitches in recs 11–15 and one 42 ms in
58/59 — none near any onset), so `frameMs` excludes the documented confound.

The batch is **not** 63 independent replicates. It is five lanes each of which flew clean, then
degraded, then departed and never came back:

| lane | replicates | clean | first precursor | first departure | ended |
|---|---|---|---|---|---|
| d1 | 16 | 01–36 | rec 41 | rec 46 | card finished (16/16) |
| d2 | 10 | 02–27 | rec 32 | rec 37 | **pilot killed** after rec 47 |
| d3 | 16 | 03–43 | rec 48 | rec 52 | card finished (16/16) |
| d4 | 11 | 04–39 | rec 44 | rec 49 | **pilot killed** after rec 53 |
| d5 | 10 | 05–30 | rec 35 | rec 45 | **pilot killed** after rec 50 |

A clean replicate is extremely repeatable: max |AoA| **3.4–3.6°** on recs 01–34, entry speed
`232.0–232.3 → 171.0`, end altitude 3671–3679 m, `qSched` never leaving 1.000, `pEff` never below
0.15. A departed replicate is max |AoA| **22–70°**, end altitude 970–2160 m, `qSched` railed at 0.300
on 13–91 % of rows and `pEff < PEffRevThresh` on 67–97 %.

**Once a lane departs it does not recover, even though the placement resets it.** `PlaceOnCondition`
puts it back at 171 m/s / 4000 m / wings level / on the anchor every replicate, and the very next
replicate departs again — inside the 6 s `arm`, before the card has demanded anything but +0.35° of
elevation. §8 is why.

**Onset is not explained by this batch.** Ruled out by measurement: frame hitches (above), mass and
fuel (`massKg` 105 409 ± 5 kg, `fuelKg` reset to 24 998 every replicate), airframe damage
(`aeroPartCount` 35 throughout), a live config edit (**zero** `# cfg` lines in 63 captures), and the
entry state itself (recs 32/35/41/44/48 all enter from a clean 232 m/s / 3675 m and still show the
precursor). Onset clusters at wall clock 22:11:30–22:13:30 across all five lanes, which is also each
lane's 7th–10th replicate — the two are confounded because the lanes launched together. **Open.**

---

## §4 — the R29 precursor reproduced

The card exists to test one prediction: *"a healthy capture commands NO bank at all below 0.5° of
`azErr`; any capture where `targetBank` exceeds ~10° while |`azErr`| < 2° is the defect firing."*

Max |`targetBank`| under an azimuth-error ceiling, per capture:

| ceiling | recs 01–31 (n=31) | recs 32–63 (n=32) |
|---|---|---|
| |`azErr`| < 2° | **0.0° on every capture** | up to **27.8°**; 3 captures over 10° |
| |`azErr`| < 5° | **0.0° on every capture** | up to **55.5°**; 12 captures over 30° |

Recs 32, 35, 37, 40, 41, 44, 48 and 52 all command **34–56° of bank** against an azimuth error of
under 2°, on a card whose largest demanded step in any axis is **0.35°**. That is the R29 signature
(55–63° of bank against a 1.3° error), on the same airframe, at the same entry condition, in a
different session and a different mod version.

Crucially, in **every** lane the precursor appears **1–2 replicates before** the first departure and
while max |AoA| is still 3.4°. The ordering is clean: precursor → departure, not the reverse.

---

## §5 — the departure mechanism, at full resolution

Rec 37 (d2) is the cleanest instance: a clean entry (`# entry v=230.2->171.0 alt=3678.5->4000.0`), a
clean placement tick, the precursor during `arm`, and the departure 5 s into the first scored segment.

`arm` (the precursor):

| tSeg | `azErr` | `targetBank` | `bank` | `bWt` | AoA | `qSched` | `pEff` |
|---|---|---|---|---|---|---|---|
| 1.217 | −3.91 | **−37.6** | −5.5 | 0.254 | 2.36 | 1.000 | 1.000 |
| 1.833 | −5.48 | **−55.9** | −13.3 | 0.509 | 2.52 | 1.000 | 1.000 |
| 3.083 | −5.14 | **−54.9** | −36.2 | 0.185 | 2.72 | 1.000 | 1.000 |

`obDR05`, tSeg 4.30 → 6.30 — the aircraft is level, `off` 1.48°, everything nominal, and then:

| tSeg | `off` | `outP` | `iPitch` | `pitchRate` | AoA | `g` | `qSched` | `pEff` | `fbwTgtPR` | `fbwPR` | `aoaGD` |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 4.600 | 0.75 | −0.018 | 0.011 | −0.087 | +3.11 | 0.15 | 1.000 | 0.857 | −0.001 | 0.087 | 1.000 |
| 4.850 | 1.63 | −0.239 | 0.008 | −0.145 | +1.79 | 0.64 | 1.000 | 0.735 | −0.049 | 0.142 | 1.000 |
| 4.917 | 2.17 | −0.218 | 0.006 | −0.161 | +1.35 | 0.72 | 1.000 | **0.463** | −0.050 | 0.157 | 1.000 |
| 5.233 | 5.73 | −0.225 | 0.000 | −0.240 | −1.20 | 1.55 | 1.000 | **0.157** | −0.050 | 0.235 | 1.000 |
| 5.733 | 14.19 | −0.182 | −0.002 | −0.348 | −6.07 | 3.29 | 0.345 | 0.140 | −0.040 | 0.346 | **0.000** |
| 6.050 | 20.89 | −0.193 | −0.002 | −0.381 | −8.86 | 4.46 | **0.300** | 0.143 | −0.042 | 0.374 | 0.000 |
| 6.300 | 27.00 | −0.226 | −0.002 | −0.465 | −11.15 | 5.41 | **0.300** | 0.144 | −0.049 | 0.460 | 0.000 |
| 7.100 | 53.28 | −0.218 | — | — | −21.96 | 5.42 | **0.300** | 0.036 | −0.049 | **0.603** | 0.000 |

Read the three things this rules out.

**It is not a large command.** |`outP`| never exceeds **0.24** anywhere in the divergence, and the
sign is nose-**up** the whole way — i.e. *against* the excursion, which is downward. The mod is not
pushing the nose down; the nose is going down while the mod asks for up.

**It is not the integrator.** `iPitch` is 0.011 → −0.002 across the entire event, against a 0.12 cap.
(Consistent with R21: `_iPitch` is ~0 in this regime regardless.)

**It is the plant.** `fbwTgtPR` −0.049 rad/s commanded against `fbwPR` +0.603 achieved is a **12.3×**
magnitude overshoot in the **opposite direction**. Batch-wide, on rows with |`fbwTgtPR`| > 0.02:

| | captures | n | median |`fbwPR`/`fbwTgtPR`| | p90 | max |
|---|---|---|---|---|---|
| clean captures | 45 | 1 964 | **1.56** | 2.53 | 6.2 |
| departed captures | 18 | 9 400 | **7.73** | 13.00 | 28.2 |

At those rates the game's own FBW pitch PID is saturated too: `num4 = clamp(localAngVel.x −
targetPitchAngVel, −0.25, 0.25)` (`:64884`) is railed at 0.25 whenever the achieved rate exceeds the
commanded one by more than 0.25 rad/s, which is most of the departure, and `remapFactor = 1/num2` has
already halved the FBW's own corrective authority at `num2 = 2.03`. Both loops are asking correctly
and neither is being obeyed.

---

## §6 — the authority failure, and why it is a ONE-LAW finding

Past |AoA| 20° (n = **2 314 rows**, all of them **negative** AoA — there is not a single row in R32
with AoA > +20°):

- `outP` mean **−0.193**, and **0.0 %** of those rows command *into* the excursion. The law's
  direction is right.
- `qSched` is **exactly 0.300 on 100.0 %** of them.

`qSched` reaching its floor is not a detail, it is the end of the schedule's range. Three hardcoded
constants own the whole stand-down:

| site | constant | what it decides |
|---|---|---|
| `ChaseController.cs:1152` | `Mathf.Clamp(qRatio, 0.3f, 1f)` | the speed schedule's floor (mirrors the game's own `:64861` clamp — defensible) |
| `ChaseController.cs:1255` | `schedFloor = 0.3f` | the **AoA-utilization** schedule's floor — the one that rails here |
| `ChaseController.cs:1296` | `Mathf.Max(0.3f, aoaGateUp)` | the achievability cap's floor |

and two more multiply authority away on the same rows:

- `:1927` — `pErrTerm *= _pitchEff >= PEffRevThresh ? Max(0.3f, _pitchEff) : _pitchEff`. With
  `_pitchEff` at 0.036–0.144 the no-floor branch is taken and the P term is cut by **7–28×**.
- `:1557` — `aoaRecover *= _pitchEff`. **The one term whose stated job is "the term that flies the
  nose back INSIDE the envelope" is scaled by the same 0.036–0.144.** Its comment is explicit that
  this is deliberate ("If the plant is not following, adding command is pumping"), and against a
  genuinely reversed plant the reasoning holds. R32 is the case where it holds *and the aircraft
  still never comes back*.

**So the law's complete response to a departure is: cut the P term, close the AoA gates, floor the
schedule, and scale the recovery bias toward zero.** Every one of those is a reduction in authority.
There is no branch anywhere in `Apply` that *increases* authority, or changes strategy, when the
plant stops responding. On nine of ten airframes that is fine — their own stability recovers them
inside a second, which is why 1 000+ archived captures on the other nine show nothing like this. On
the Darkreach, whose FBW has the roster's lowest `gLimitPositive`, whose alpha limiter is off at this
q, and whose `flightAssist` is 0, it means the aircraft descends 3 000 m and kills the pilot.

**This is the ONE-LAW smell `GENERALITY-REVIEW.md` exists to track**: a hardcoded constant is deciding
the outcome, and it is deciding it differently on one airframe. Recorded there as finding 18.

---

## §7 — what is actually unusual about this airframe (and a correction)

Every archived capture's `# fbw` header line, mapped to the sidecar `jsonKey`:

| jsonKey | FBW cornerSpeed | maxPitchAngVel | gLimitPositive | alphaLimiter | strength | assist |
|---|---|---|---|---|---|---|
| **`Darkreach`** | **100** | **0.3** | **4** | **10** | 0.05 | **0** |
| `EW1` | 130 | 0.3 | 6 | 10 | 0.05 | **0** |
| `COIN` | 110 | 1 | 6 | 10 | 0.1 | 1 |
| `CAS1` | 160 | 1 | 7.5 | 14 | 0.1 | 1 |
| `trainer` | 130 | 1 | 8 | 10 | 0.05 | 1 |
| `VTOLTrainer1` | 160 | 1 | 8 | 15 | 0.08 | 1 |
| `FastBomber1` | 200 | 0.5 | 8 | 15 | 0.2 | 1 |
| `SmallFighter1` | 155 | 0.7 | 9 | 25 | 0.08 | 1 |
| `Multirole1` | 160 | 0.75 | 9 | 27 | 0.05 | 1 |
| `Fighter1` | 160 | 0.9 | 9 | 27 | 0.1 | 1 |

**Correction to the audit note this batch was written up from:** the Darkreach is **not** the only
airframe with `flightAssist = 0`. `EW1` (EW-25 Medusa) has it too, and scores mid-band — which is
exactly why ROADMAP's R28 section already lists `assist=0` among the *excluded* explanations for the
per-airframe spread. Repeating "only Darkreach has it" would have re-introduced a hypothesis the
corpus has already killed.

What **is** unique:

- **`gLimitPositive = 4`** — the lowest of the ten by a clear margin (next is `EW1`/`COIN` at 6).
  Note this is a *different quantity* from AIRFRAMES.md's `gLimit` column, which is
  `aircraftGLimit` = 5 for this airframe.
- **`maxPitchAngularVel = 0.3` and `alphaLimiter = 10` on 105 t.** Tied-tightest on both counts (with
  `EW1`, which weighs 24.6 t). As flown the Darkreach is **105 409 kg** — 54 311 kg of airframe plus
  ~25 t of fuel and 13 050 kg across three ordnance stations. It carries a light aircraft's pitch
  authority on four times `FastBomber1`'s mass.
- **`fbwCornerSpeed = 100`** against a roster `cornerSpeed` of 180, which is what puts `num2` at 2.03
  — i.e. deep into the no-alpha-limiter region — at an entry speed that is *below* its published
  corner.

That combination, not `flightAssist` alone, is the airframe-side half of the defect.

---

## §8 — the placement-tick defect is not benign on this airframe

`FLIGHT-PROTOCOL.md` §Gate-B records the placement transient as "deterministic … and decays inside
the 6 s `arm`, before the scored segment starts", and ROADMAP #23 records R28's refinement (median
|`rollRate`| at `tSeg = 0` **0.725** over 384 captures, **0 of 384** in the 7–14° `leadDeg` band).
Both are true *for a healthy lane*. R32's 58 placed captures show the other mode of the distribution:

| signal at `tSeg = 0.000` | median | p90 | max | n > 5 |
|---|---|---|---|---|
| \|`rollRate`\| | 0.753 | 38.35 | **54.16** | 19 / 58 |
| \|`headingRateFilt`\| | 0.395 | 445.87 | **483.09** | 20 / 58 |
| \|`leadDeg`\| | 0.290 | 289.84 | **314.02** | 20 / 58 |
| \|`outP`\| | 0.039 | 0.900 | **1.000** | **rails at 1.000 in 15 / 58** |

The median reproduces R28 exactly. The distribution is **bimodal**, and the upper mode is what makes
the cascade self-sustaining: rec 51's first row is `outP −0.800`, `rollRate 8.442`, `aoaRec 5.089`
against `off 0.42°`, and by `tSeg = 5.967` — still in the `arm` segment, before the card has demanded
anything — `off` is 88.9° and AoA is −23.0°. The mechanism is the documented one: the finite
differences straddle the teleport, and the *magnitude* of what they straddle is set by the attitude
the **previous** replicate ended in. A precursor replicate ends banked; a departed one ends tumbling;
either way the next placement injects a full-authority spurious command on tick zero of a 105 t
airframe with `maxPitchAngularVel = 0.3`.

**This is not an argument for symptom-patching it.** The reasoning in CLAUDE.md still holds — a
discontinuity guard on `rollRate` would clean the one signal and leave `headingRateFilt`/`leadDeg`
alone, making it *look* fixed. What changes is the claim attached to it: it is harmless where it has
been measured (Multirole1, FastBomber1, Fighter1 — light, high-authority, high-`gLimitPositive`), and
it is *not* harmless on the heavy end. R32 is the first batch where the defect is load-bearing.

---

## §9 — what this means for the law

**1. Do NOT add a mod-side G-limiter.** Three reasons, in order of decisiveness:
   - It would protect nothing. §2: the airframe cannot be damaged by G; only the pilot can, and only
     above 20 g, which is reached *after* the departure, at 80 m/s, falling.
   - It would clip the symptom and hide the cause. The 26.9 g and 9.2 g rows are the *readout* of a
     departed airframe. A limiter would remove the readout — the most visible signal that a lane has
     failed — while the aircraft still descended 3 000 m. R29's departure was found *because* of that
     26.9 g row.
   - It is a second de-authorisation on a law whose §6 problem is that it already has five.

**2. The real target is the AoA-schedule authority failure** (`GENERALITY-REVIEW.md` #18,
`ROADMAP.md`). The principled shape is not "floor at 0.3" but a schedule whose floor is a function of
the probed envelope — the same generality pattern the rest of the law follows. Do not touch it before
the precursor (§4) is understood, though: the schedule railing is downstream of a departure the
roll-to-align channel appears to initiate.

**3. `darkreach-05` did its job and should be flown again after any fix.** It reproduced the R29
precursor in a fresh session, on a fresh mod version, in 4 of 5 lanes, with a clean 31-replicate
control period in front of it. That control period is what makes it a usable A/B card: recs 01–31 are
a genuine "no defect" baseline on the *same airframe and card*.

**4. Two things about the batch that must be honoured before it is used as an A/B baseline.**
   - **Replicates within a lane are not exchangeable after onset.** A departed replicate poisons the
     next one's placement (§8). Only recs 01–31 are exchangeable.
   - **Nothing here is a fleet result.** Five lanes, one airframe. `compare-runs.py` will group them
     as one cell, which is correct.

---

## §10 — open

| | |
|---|---|
| **What sets the onset at rec ~32?** | Not frame time, mass, fuel, damage, config edits or entry state (§3). It is wall-clock- and replicate-index-confounded because the lanes launched together. A card with a deliberately staggered *start* (not just a staggered launch) would separate them. |
| **What initiates the precursor?** | 34–56° of `targetBank` at <5° of `azErr` is the roll-to-align channel, and `GENERALITY-REVIEW.md` #16 (`lateralHold` rails ⇒ `blendWeight` = 1 ⇒ the whole bank pipeline disconnects) is the standing candidate. Untested here — this card carries no arm. |
| **Does the precursor cause the departure, or share a cause?** | R32 establishes only the ordering (precursor 1–2 replicates earlier, in every lane). A card that suppresses the roll channel and keeps everything else would settle it. |
| **Is `EW1` quietly doing the same thing more slowly?** | Same `assist=0`, same `maxPitchAngVel`, same `alphaLimiter`, quarter the mass. It has never been flown at a corner-relative entry condition on this card. Cheap to check. |
