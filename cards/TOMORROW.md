# TOMORROW — the unattended multi-airframe campaign

> **MOSTLY EXECUTED 2026-08-02 (R39/R40). Do not run this end to end.** Of the campaign's launches:
> `alpha-sweep` (§5–6) flew and **the card is retired** — replaced by `alpha-pullup`
> (`cards/ALPHA-CARD-REDESIGN.md`); `alpha-steps` flew; `e2-rel-turn-lead` flew and **the card and its
> knob were deleted in v0.99.1**; `e3-marker-ff` flew; the `rotor-*` and `stol-*` cards flew and both
> produced **invalid** data for their intended purpose (see `debugtests/R39-rotor.md` §1a and
> `debugtests/R39-stol.md` §2).
>
> **Still live and still worth flying: §1–4's `e1-below-suppress` / `e1-below-control` /
> `e1b-align-lead`, and §7's `oblique-above-c`** — the belowness axis, which is §4 Batch 4 experiment
> **E1** in `LAW-CHARACTERIZATION.md` and the only never-flown group left in `cards/`. §0's install
> and preflight steps are current and still the right way to set up a launch.
>
> **Before any launch, run `python debugtests/check-card.py cards/*.json`** — it did not exist when
> this runbook was written, and three of the cards below failed on arithmetic it now catches.

The runbook for one morning. Seven launches, ~42 min of flying, ~480 captures, and it closes out the
**10 shipped cards that have never been flown**. Everything below is one checkbox (or one text field)
and the spawn key; nothing in F1 needs to be hand-matched to a card, and the two places where that is
still not quite true are called out explicitly.

Read [`README.md`](README.md) → *The `sel[0]` rule* first if you read nothing else here. It is the one
failure mode in this campaign that produces no refusal, no warning and a capture set that scores fine.

---

## 0. Install and preflight (once, before the first launch)

**Install the cards** — the build never touches `<game>`, so this is by hand, and the game must be
restarted afterwards because cards bind at startup:

```powershell
$game = "<game>"   # your Nuclear Option install — the folder containing NuclearOption.exe
$dest = "$game\BepInEx\config\wtmouseaim-cards"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item cards\*.json $dest -Force
```

Then start the game and check **one line** in `BepInEx\LogOutput.log`:

```
[card] N card(s) bound (3 built-in, 31 from disk) — ...
```

`0 from disk` with files in the folder is the v0.71–v0.90 serializer bug's shape, and it means nothing
below will fly. Note the `[session] run R<N>` line at the same time — **that R number tags every
capture and every log this session**, and it only increments on a game restart, so all eight batches
share it unless you quit in between.

### F1 knobs to set

| knob | value | why |
|---|---|---|
| `Drone/DroneEnabled` | **ON** | master switch; the harness is inert (and the hotkeys unread) while off |
| `Scenario/ScenarioCardSet` | **the batch's card name** | see below — this is how you pick the card |
| `Scenario/ScenarioRepeat` | ignored | every card in this campaign carries `repeat: 8` itself and wins — including `alpha-steps`/`alpha-pullup`. Nothing here needs it set |
| `Scenario/ScenarioArmToggle` | **empty** | must be blank. The `e*` cards name their own `armToggle` and win, but batches 6–8 declare none — a leftover value here would sweep a knob nobody asked to sweep |
| `Scenario/ScenarioForceEntry` | ON (default) | the placement writes the entry condition; off, a lane that is not already on condition simply refuses |
| `Control/Enabled`, `Control/WriteControl` | **ON** | with either off the card moves the marker and nothing chases it. `[card]` warns, and the capture is not a law measurement |
| `Recording/AnomalyLogging` | ON (default) | the primary artifact |
| `Recording/DebugLogging` | **OFF** | per-tick spam × 8 lanes × 8 replicates |

### F1 checkboxes to verify are OFF

Under **`Scenario Cards`** — and the first three are the trap, because they default to **ON**:

- [ ] **`fixedwing-v2`** ← built-in, defaults ON, registered **first**
- [ ] **`rotorcraft-v2`** ← built-in, defaults ON
- [ ] **`fixedwing-sweep`** ← built-in, defaults ON
- [ ] every disk card except the batch's own

**Why this matters more than it looks:** `airframe`, `count`, `repeat`, `armToggle`, `startAlt` and
`startSpeed` are all read off **`sel[0]`, the first selected card**, and applied to the whole launch.
`fixedwing-v2` declares none of them, so if it is still ticked, `sel[0]` is *it* and every batch below
silently becomes **one `Multirole1`, one replicate, no A/B** — with your card flying second in the
queue as a stimulus only. Nothing refuses.

**So use `Scenario/ScenarioCardSet` instead of the checkboxes.** A non-empty value overrides them
entirely, its order *is* `sel[0]`'s order, and a default cannot poison it. Every batch below is one
edit of that one field.

### Before each spawn press, read the run board

Top-left, **PREFLIGHT** panel. It shows the resolved card, replicate count, drone count, airframe,
altitude, speed and A/B knob, each marked `[from card]` or `[from F1]`, from the same `Preview()` the
launch itself uses. If it says `NO CARD SELECTED`, or a count/airframe you did not expect, fix it
*before* pressing the key — that panel is the only pre-flight confirmation you get, and after the key
the next confirmation is 6 minutes of flying.

Drones auto-despawn ~5 s after their card ends, including if it aborted or was refused. Press the
despawn key (default **F9**) between batches anyway; it is safe with none alive.

---

## 1–4. The `e*` attribution set — four one-checkbox A/Bs that have never flown

Four launches, ~24 min total, 256 captures. Each is one config knob swept **ABBA** against a fixed
geometry, and since v0.94 the arm is per-aircraft state, so **every lane runs its own independent
ABBA**: the answer comes back as eight A/Bs, one per airframe, which is the ONE-LAW question the
single-airframe version could not ask. Adding those seven extra lanes costs no wall clock (R28: 384
captures across 8 lanes in 30m14s).

All four fly the same fleet — the eight fixed-wing keys that clear the v0.92 envelope gate at 250 m/s:

```
Fighter1, Multirole1, SmallFighter1, trainer, VTOLTrainer1, EW1, FastBomber1, Darkreach
```

**Read `blendRailPct` before anything else in all four.** A railed segment cannot respond to a gain
change; it is *no signal*, not a good score, and measuring the clamp is precisely what the v0.83 A/B
for `RelativeTurnLead` did (96.9% clamped).

| # | `ScenarioCardSet` | knob swept | lanes | wall clock | captures |
|---|---|---|---|---|---|
| 1 | `e1-below-suppress` | `Control/BelowAlignSuppress` | 8 | ~5.6 min | 64 |
| 2 | `e1-below-control` | `Control/BelowAlignSuppress` | 8 | ~5.6 min | 64 |
| 3 | `e1b-align-lead` | `Control/AlignRateLead` | 8 | ~5.6 min | 64 |
| 4 | `e3-marker-ff` | `Control/MarkerRateFeedForward` | 8 | ~6.7 min | 64 |

**Batches 1 and 2 are a matched pair — fly them back to back, in that order, in the same session.**
Batch 2 is the *control*: the same knob on the horizon-centred diamond, where `alignFracH` ≈ 0 and the
suppression therefore cannot legitimately do anything. A control flown in a different session or on a
different roster controls for nothing.

**1 — `e1-below-suppress`.** Does the v0.85 roll-invariant below-nose suppression buy anything where
the `elDn` defect lives (6.92° of standing error at ±43° of bank, against 0.03° for the *larger* upper
mirror)?
*Pass:* on-arm `terminalOffDeg` and `rollYawOpposedPct` on the below-nose segments below the off arm,
and the mirror pairs closer together.
*Fail:* arms indistinguishable — the fix does nothing where it was aimed — or the on arm worse.

**2 — `e1-below-control`.** *Pass:* the two arms are **indistinguishable**, separating by less than
this card's own mirror-pair spread. *Fail:* **any** arm separation. That is a regression (the
suppression reaching a geometry it was never meant to touch) *and* it invalidates whatever batch 1
measured.

**3 — `e1b-align-lead`.** `AlignRateLead` is also a roll-**damping** change (finding 17: effective
roll-rate feedback ×1.00 at `blendWeight` 0 rising to ×1.64 at 1, mean ×1.39 on `elDn`) — hence its own
card rather than a second arm on batch 1.
*Pass:* the on arm lowers `overshootAzDeg` and `stickFlipRateR` on the below-nose segments at no cost
in `terminalOffDeg`. *Fail:* on-arm `stickFlipRateR` or `wobbleEpisodes` **up** — the damping side
effect is what the knob actually does.

**4 — `e3-marker-ff`.** The v0.78 feed-forward supplies 82.5% of the turn demand yet arrives as 0.0000
of roll stick above the `lateralHold` rail (finding 16), so it has never been A/B'd anywhere it can
reach the roll channel.
*Gate first:* `blendRailPct` ≈ 0. *Pass:* on-arm mean |azErr| and `terminalOffDeg` smaller. *Fail:*
arms matching while unrailed — the feed-forward is inert even off the rail, which extends finding 16
rather than closing it. `aimRate` is recorded on **both** arms so a null cannot be confused with the
feed-forward never firing.

---

## 5–6. `alpha-*` — the AoA ceiling

Two launches, ~12 min, 144 captures. The ONE-LAW rule explicitly names "a loaded jet mushing near its
alpha limit above corner speed". Both fly at **8000 m**: thin air is the airframe-agnostic lever that
makes the wing hit its alpha ceiling before its g-limit, and **lowering it is not an option** — that
raises q, which is the wrong direction. `alpha-steps` flies the eight-key 250 m/s fleet; `alpha-pullup`
flies **all ten** at 1.15× each lane's own FBW corner speed.

> **`alpha-sweep` HAS now flown (R39, 61 captures) and is SUPERSEDED — do not launch it.** It failed
> structurally: its demand is an AZIMUTH sweep derived from the airframe's structural g, so the
> roll/turn channel saturated first — bank clamp 74–97%, turn-rate cap 85–97%, blend rail 81–96% on
> **all 60** scorable segments, with `aoaAboveCeilingPct` **0.0 on 60 of 60** and 525–2428 m of
> DESCENT. `alpha-pullup` replaces it and pulls in the **vertical plane** (`az ≡ 0`), where load costs
> no bank and all three of those rails are unreachable by construction. See
> [`ALPHA-CARD-REDESIGN.md`](ALPHA-CARD-REDESIGN.md) and
> [`../debugtests/R39-E-alpha.md`](../debugtests/R39-E-alpha.md).

> **CORRECTED 2026-07-31 — and it changes the priority of these two batches.** The premise here was
> *"`aoaLimiterActivePct` is 0 in every capture this project has ever taken"*. **False:** non-zero on
> **66** (run, airframe, tag) cells, **23** fully unrailed, topped by **R33 `Darkreach·obDR6` at
> 100.0%** (n = 4, `railed = 0`). So the regime is already reachable on a *shipped, flown* card —
> `oblique-6-c` at the v0.96 corner-relative entry (95 m/s on `Darkreach`). **Cheaper than either
> batch below: re-fly `oblique-6-c` on `Darkreach` with more replicates first.** Caveat if you do:
> `alpha_metrics` only runs on `alpha_step`/`alpha_hold` tags, so an `oblique_step` capture carries
> `aoaLimiterActivePct`/`aoaPeakDeg` and **none** of `aoaAboveCeilingPct`, `qSchedMin`, `gateMinUp/Dn`,
> `commandIntoCeilingPct`. Decide the tagging before flying.

> **No hand-matched global here any more.** Both cards now declare `"repeat": 8` themselves, so the
> run board should read `repeat 8 [from card]` and `Scenario/ScenarioRepeat` is irrelevant to these two
> batches. (It was left as a global while a shipped card's `repeat` could break comparability with
> earlier flights of it.) **Both also pin their own `Scenario/ScenarioThrottle`** — `alpha-pullup` at
> 0.40, biased low on purpose — so `Scenario/ScenarioThrottle` in F1 is irrelevant to them too.
> That pin is **process-global and currently buggy under a concurrent fleet**: the first lane to
> finish un-pins it under everyone still flying. **Fly `alpha-pullup` only after the refcounting fix
> lands**, or read the `# override` header of every capture before trusting a throttle.

| # | `ScenarioCardSet` | lanes | wall clock | captures |
|---|---|---|---|---|
| 5 | `alpha-steps` | 8 | ~4.8 min | 64 |
| 6 | `alpha-pullup` | **10** | ~7.5 min | 80 |

**5 — `alpha-steps`** (transient α, mirrored ±45° pitch steps). *Pass:* `aoaAboveCeilingPct` > 0,
`aoaPeakOverCeiling` ≲ 1.1 (v0.57 measured a *reactive* gate relaying at 1.3–2.5×), `wobbleEpisodesAoa`
= 0.

**6 — `alpha-pullup`** (sustained α in the vertical plane — the replacement for `alpha-sweep`, and
the highest-value card in the grid). Two scored segments: `alphaHoldFast` (18°/s, 4 s) exposes every
one of the ten lanes, `alphaHoldSlow` (4°/s, 12 s) walks the lim-10 group across the guard's onset
band. *Gate first:* `bankClampActivePct` / `turnRateCapActivePct` / `blendRailPct` must all be ≈ 0 —
the demand commands no azimuth at all, so a non-zero reading is a **law** defect, not a card problem.
*Pass:* `aoaLimiterActivePct` > 0 on `alphaHoldFast` for 10 of 10; `wobbleEpisodesAoa` = 0 wherever
`gateMinUp` < 1; altitude **rising** across each scored segment. *Read `gateMinUp` beside every
`commandIntoCeilingPct`* — that metric reads 0.00 both for "the law behaved" and "the gate never
closed far enough to look", and five of `alpha-sweep`'s eight lanes published exactly that false pass.
The full fire/fail/not-exposed table for all six criteria is in
[`ALPHA-CARD-REDESIGN.md`](ALPHA-CARD-REDESIGN.md) §5.

**On both: `aoaLimiterActivePct` = 0 is a FAILED CARD, not a clean law.** It means the card missed the
regime and every other number describes some other flight — and on `alpha-pullup` it is the gate that
disqualifies every other criterion on that lane (§5 C1). Do **not** reach for `startAlt`: raise the
lane's pitch rate instead. Neither covers the *loaded* case — a card cannot set a loadout — so hand-fly them again with
heavy stores if the clean runs look interesting.

---

## 7. `oblique-above-c` — closing the belowness axis

One launch, ~5.7 min, 80 captures, **10 lanes** (the whole fixed-wing roster: this is a corner-speed
card, so `CAS1` and `COIN` fly too, at 0.95× their own corner).

| # | `ScenarioCardSet` | lanes | wall clock | captures |
|---|---|---|---|---|
| 7 | `oblique-above-c` | 10 | ~5.7 min | 80 |

`oblique-6-c` centres the 6° diamond **on** the horizon and `oblique-below-c` **20° below**; this
centres it **20° above**, making belowness — `alignFracH`, the exact quantity the v0.85 suppression
keys on — a 3-point axis instead of a pair, so a monotonic trend can be told from a below-only anomaly.

*Pass:* `rollYawOpposedPct` and `terminalOffDeg` at or below `oblique-6-c`'s, mirror pairs no further
apart — aboveness costs nothing, and the 42.0% cross-fighting on `elDn` is a property of being *below*
the nose rather than of being off the horizon in either direction.
*Fail:* as bad as `oblique-below-c` — the defect follows |elevation| and the roll-invariant suppression
is aimed at half of it.

*Before comparing:* check `alt` and `spd`. This card enters at 3000 m and climbs where
`oblique-below-c` enters at 6000 and dives; the altitudes were chosen to give comparable **mean** q,
but the climb will fall short of the dive's 3.3 km because thrust is finite. The energy asymmetry is
inherent to the axis, not a defect — read it, don't assume it away.

*What R31 already settled, so this batch does not re-learn it:* two findings bound what a result here
can mean, and both are easy to forget because they are about the *mechanism*, not the geometry.
(a) **`bWt` is identically 0 over the terminal 1.0 s of all 384 R31 segments** — the roll channel
closes at t = 0.83–3.10 s of 8 s, so whatever produces the down-step penalty, `bSup` is **not** the
path it travels on the scored window. A trend across this 3-point axis is therefore evidence about
*belowness*, not about the v0.85 suppression, and must not be written up as the latter.
(b) **`BelowAlignSuppress` `arm=0` selects the v0.67 *form*, not "off"** — the knob is a ternary
between two live formulas and a true null has never flown. That is why this card carries **no
`armToggle`**: the axis is a geometry sweep on one fixed law, which is the only clean thing to ask
until the knob grows a real off. See [`R31-FINDINGS.md`](../debugtests/R31-FINDINGS.md).

If `oblique-below-c` and `oblique-6-c` have not been flown on this build either, fly them straight
after (~5.7 min each, 10 lanes) — the axis needs all three arms from the same session to be worth
reading as an axis. **On this build that is mandatory, not a preference:** v0.96 resolves
`startSpeedCorner` against the FBW's corner speed instead of the encyclopedia's AI one
([`AIRFRAMES.md`](../AIRFRAMES.md) trap 6), so every `-c` capture taken before it entered at a
different speed (`Fighter1` 171 m/s then, 152 now) and cannot be the other two arms of this axis.

---

## After the campaign

```powershell
$game = "<game>"   # the folder containing NuclearOption.exe

# index everything (~30 s the first time, 0.2 s after)
python debugtests/index-captures.py "$game\BepInEx"

# archive the batch out of <game> — LogOutput.log is OVERWRITTEN on the next game start
python debugtests/index-captures.py "$game\BepInEx" `
       --archive debugtests/archive --run R<N>
```

`R<N>` is the number from the `[session] run R<N>` line at startup — one per **game session**, not per
batch, so all eight batches share it unless you restarted. Archive before the next game start or that
session's launch lines are gone for good.

Then read it. At ~530 captures the per-file tools are unusable by design:

```
python debugtests/compare-runs.py <csvs> --summary      # one line per (card, segment) — start here
python debugtests/index-captures.py ... --query "<sql>" # the A/B-by-arm query is in the module docstring
```

`compare-runs.py` groups by **(airframe, card, arm)** and refuses to pool across airframes, so each
`e*` batch comes back as eight rows per segment — eight independent A/Bs — which is what the fleet
lists were added for. Heed its truncated-segment and pooling warnings rather than working around them.

## Quick reference

| | |
|---|---|
| spawn a batch | `Drone/DroneSpawnKey`, default **F2** |
| despawn everything | `Drone/DroneDespawnKey`, default **F9** |
| config panel | **F1** (ConfigurationManager) |
| everything the harness did | grep `[drone]`, `[card]`, `[sandbox]` in `LogOutput.log` |
| a key that seemed to do nothing | it logged why — a refusal is always a log line, never a silent no-op |
