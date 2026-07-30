# Changelog

All notable changes to WT Mouse Aim. Versions are the `PluginVersion` in `WTMouseAimPlugin.cs`
(the single source of truth); each release is published via `release.ps1`.

## 0.90.0

**The harness runs itself: the sky empties when a batch finishes, and a card now carries the whole
test rather than half of it.** Four changes, all in the uncrewed rig. **No control-law change
whatsoever** — no gain, gate or schedule moved, so every capture taken since 0.87 stays comparable.
The through-line is the same in each: an unattended batch is only worth flying if a setup mistake is
visible *before* the launch and nothing is left circling *after* it.

**1. A drone that has finished its card despawns itself.** The only automatic despawn was the
exception path, so a drone whose suite completed fell back to the built-in level-hold and orbited
until the despawn key or the mission end. `PruneDead` now also despawns any drone that has had **no
card running** for `IdleDespawnSec` (5 s, a const) — ONE rule, which is why it covers suite-complete,
aborted, refused *and* never-started with no path left over. The grace window is sized by what it has
to clear, not by taste: the gap between `NextCard` closing one recorder and `StartCard` opening the
next is a placement tick plus a frame, and anything shorter would despawn a drone between its own
replicates. This is not tidiness — a live drone keeps a full complex-physics aero job and all three
of its per-aircraft registries alive, which is the same frame budget the launch stagger exists to
protect and that `frameMs` was added to measure. Every despawn line now carries its reason.

**2. A shot-down drone is noticed.** Measured in R25: the operator destroyed a drone that had
finished its card and it stayed registered until the mission quit. `PruneDead`'s predicate is
`Aircraft == null || Aircraft.disabled`, and the game **never self-disables an `Aircraft` on damage** —
`Unit.disabled` is written only by `ServerDisableUnit` / `ReturnToInventory` / `OnDestroy`, and
`WaitRemoveAircraft` is fired *from* the disabled hook, so a shot-down aircraft keeps a live
GameObject reading `disabled == false` indefinitely. The check therefore moved to
`TestDrone.OnPilotStep`, the one place holding the `Pilot` the damage actually lands on: `p.dead ||
p.ejected` now despawns with the reason instead of early-returning. An airframe destroyed *without*
killing the pilot is covered one layer out — the card's own altitude floor aborts it on the way down
and the idle rule then despawns it — so there is no third case to add.

**3. Lanes key off the camera, not the scene origin.** With no local aircraft (ejected, dead,
spectating — which is what an operator watching a batch usually is) the lane fallback was
`Vector3.zero`. That is not merely invisible: it is the **same point on every press**, so a second
launch put lane *k* exactly where the first one did, while each drone's card anchor is its own spawn
point. `Camera.main` is both visible and observer-dependent. `_slot` also starts at `_live.Count`
rather than 0, so "press it twice" is safe even when nothing has moved.

**4. Cards are self-describing — the operator ticks ONE checkbox and presses the spawn key.** A card
already knew the airframe it was designed on and the speed and altitude it intends; `DroneAirframe`,
`DroneSpawnAlt`, `DroneSpawnSpeed`, `ScenarioRepeat` and `ScenarioArmToggle` had to be matched to it
by hand, five per batch, and a mismatch does not refuse — it produces a capture that scores fine and
answers a different question (R18's "energy failure" was exactly that). So:

- **`repeat`, `armToggle` and a generic `config` override list** are now card fields. `config` is a
  list of `{key, value}` pairs in the `"Section/Key"` grammar (bare key ⇒ section `Control`), with the
  value parsed by BepInEx's own `TomlTypeConverter` — one path covers bool/int/float/string/KeyCode,
  instead of a hand-rolled parser that would be a second, subtly different definition of what a config
  value is. That grammar now has **one** implementation (`SplitSpec`), shared by `ScenarioArmToggle`,
  a card's `armToggle` and every `config[].key`, so the three cannot drift into three spellings.
- **The drone spawn reads the card's `airframe`/`startAlt`/`startSpeed` in preference to the `Drone*`
  knobs**, resolved once per batch (not per lane — a checkbox ticked mid-stagger would otherwise change
  the airframe half way through), and the launch log names **which source won for each value**. That
  line is the operator's only confirmation: "4000 m" reads identically whether the card asked for it or
  a knob was just left there, and telling those apart is the whole point of the feature.
- **Every field falls back to its global when absent**, so a card that declares nothing behaves exactly
  as it did in 0.89 — which is what keeps the shipped grid and every ad-hoc recording valid.
- **Overrides apply BEFORE `ApplyArm` and BEFORE the recorder opens, and restore AFTER it closes.**
  Both halves are load-bearing. `ConfigFile.SettingChanged` drives `ManeuverRecorder.NoteConfigChange`,
  which stamps a `# cfg` line into every OPEN capture — so writing a card's own setup after its own
  recorder opened would record the card configuring itself as a mid-run config *change*, which is
  precisely the signal those lines exist to flag. Arm-after-overrides guarantees the swept arm wins
  even if the refusal below were ever bypassed.
- **A card that pins the very knob the A/B schedule is sweeping is REFUSED, loudly.** Pinning it flies
  every replicate on one arm while each capture still carries an honest-looking `arm=0`/`arm=1` label,
  so the A/B reads as "no measurable difference" and nothing in the artifacts says why. That is worse
  than a run that refuses and worse than one that visibly breaks, so it is the one override failure
  that is named and skipped rather than silently won by either side.
- **New `# override Section/Key=value …` header line** in the CSV, written directly under `# card`.
  It is a **header line, not a column** — the CSV stays at **64 columns** — because the value is
  constant for the whole capture by construction, and because `# config` already shows the *values*
  but cannot say the **card** chose them, which is the distinction a batch needs.
- **All 16 shipped cards migrated.** `airframe` held PROSE in every one of them ("any jet at the
  fixedwing-v2 entry condition") because nothing read it before this release gave it behaviour; the
  prose moved into `note` and `airframe` is now `""`. A jsonKey never contains whitespace, so
  `Validate` **blanks** any `airframe` that does, with a named warning — a hand-written card degrades
  to the pre-0.90 behaviour instead of failing a launch or trying to spawn a sentence.
- **`scorecard.py --selftest` enforces four card-setup rules offline**, because nothing at runtime
  will: `JsonUtility` ignores what it cannot parse and the apply path is fail-soft by design. It checks
  the jsonKey rule, the swept-knob conflict (compared *after* the grammar split, so `Knob` and
  `Control/Knob` are recognised as the same entry — a raw string compare is exactly how this would
  sneak through), the key grammar and non-empty values, and `repeat` in 0..20 (the mod clamps, so `40`
  would silently fly 20).

**5. An on-screen harness run board.** An unattended batch is 20+ minutes of wall clock whose only
progress signal was `[card]` lines in a text file. Top-left, drawn in `OnGUI`'s **pre-gate** band —
before `ShowOverlay`/`Enabled` and before the local-aircraft resolve, because the operator watching a
batch is usually in no aircraft at all, which is exactly when every gate below has already returned.
Two states: **FLYING** (one line per aircraft — drone number or `YOU`, card, run *x*/*y*, arm, segment
*x*/*y* and tag, seconds left in the segment, time left in the card, recorder sample count; the header
aggregates over the **max**, since the batch ends when the slowest lane does and the leader's ETA would
read as nearly-done with a full card still to fly) and **PREFLIGHT** (what WILL fly: card, replicate
count, per-drone total, and airframe/altitude/speed each marked `[from card]` or `[from F1]`), plus an
amber **NO CARD SELECTED** line for the commonest setup mistake — until now it surfaced only as a log
warning *after* the launch, by which point N drones are airborne measuring nothing. The preflight
values come from `ScenarioPlayer.Preview()` and `TestDrone`'s own three resolvers — the same pair the
launch itself uses — so the board physically cannot promise something the spawn will not do. It draws
nothing at all when `Drone/DroneEnabled` is off, and the preview is polled at 2 Hz because `OnGUI`
runs at least twice a frame and a repaint must not spam the log the way an operator keypress may.

**New offline check: `debugtests/test-board-math.py`.** The board's two non-trivial pieces — the
m:ss / "0.0s" formatter and the seconds-left-in-this-card sum — live between `BOARD-MATH` markers in
`ScenarioPlayer.cs`, written in plain numbers with no Unity types. The tool extracts that region
**verbatim**, compiles it with the .NET SDK and runs it against 23 cases, so it exercises the shipped
code rather than a Python copy of it that would drift and then agree with itself forever.

## 0.89.0

**The 0.88 entry trim is reverted — it was aimed at a phantom — and the real cause of the entry
transient is now measured.** Gate B (R23, 4 replicates of `fixedwing-sweep`, Multirole1, ABBA on
`RelativeTurnLead`) passed all four labelling criteria, and in doing so produced the capture that
disproves 0.88.

- **Run 01 disproves the lift-hole theory.** It is the first placement of the run, so no trim had been
  measured yet and it was written **untrimmed** — the exact AoA = 0 condition 0.88 blamed for the
  thump. It has the **cleanest entry of the four**: AoA rises smoothly 0.07° → 1.46° with *no
  overshoot at all*, and `off` peaks at 0.59°. The three trimmed replicates overshoot to 2.74–2.87°
  and peak at `off` 1.72–1.97°. Trimming the velocity did not remove the transient; it stacked on top
  of one.
- Reverted rather than kept-and-ignored, on a second ground: `_trimAoA` made each replicate's entry
  depend on a value measured during the **previous** replicate, which is a cross-replicate coupling in
  a rig whose entire purpose (Gate A) is replicate independence.
- The `# entry` line loses `aoaTrim=`; the CSV is unchanged at **64 columns** (it was a header field,
  never a column).

**The real finding: the per-replicate controller reset does not take effect on the placement tick.**
`PlaceOnCondition` calls `ChaseController.Forget(ac)` and logs `controller reset` immediately after,
yet at `tSeg=0.000` of every *placed* capture the controller still holds pre-placement state:

| signal | placed runs (02–04) | run 01 (no preceding card) |
|---|---|---|
| `rollRate` | **−58.99 / −58.66 / −58.65** | −0.16 |
| `rollRateF` (filtered, feeds roll damping) | −12.83, bleeding out over ~0.2 s | ~0 |
| `headingRateFilt` | **10.4 / 19.0 / 19.3** | 0.00 |
| `leadDeg` (anticipatory lead actually subtracted) | **6.8 / 12.4 / 12.5°** | 0.00 |

`rollRate = (t.up − _prevUp)/dt` reading −59 requires `_prevUp` to hold the *banked* attitude: the
placement snaps a ~79° banked turn to wings-level in one fixed step and the finite difference
straddles it (Δup·right ≈ 1.18 over dt 0.02 = 59). Every **direct** measurement on that row —
`bank`, `alt`, `pos`, `spd`, `aoa` — is correctly post-placement; only the derivatives are poisoned.
A freshly-`Forget`-ed instance cannot produce this, so the controller flying that tick is not fresh.

**This also retracts a Gate A claim.** R22 concluded "`iPitch`/`iYaw` read exactly 0.0000 on every
first row, so v0.84's `ctrlReset` does what it claims." That is not evidence: R21 already measured
`_iPitch` sitting at ±0.001 against a 0.12 cap for an entire 30 s turn, so it is ~0 coming out of a
turn whether or not anything reset it. `FLIGHT-PROTOCOL.md` is corrected.

No fix shipped for it in this release, deliberately: a discontinuity guard on the finite difference
would clean up `rollRate` while leaving `headingRateFilt`/`leadDeg` untouched, which would make the
symptom look fixed and hide the root cause on the next capture.

**Impact on results so far: none that invalidates a gate.** The transient is deterministic (the three
placed runs agree to within 0.02 on every affected signal), it decays inside the 6 s `arm`, and the
scored `turn360` segment starts after it. Gate A passed *with* it present.

## 0.88.0

**The entry placement is trimmed — the reset no longer drops the aircraft for a physics step.**
First finding out of the Gate A batch (R22, 8 replicates of `fixedwing-sweep`, Multirole1). The
placement wrote the velocity exactly along a level nose, which is **AoA = 0, i.e. zero lift**: row 0
of every capture read `aoa=-0.05 g=0.00`, the FBW then caught the fall at ~1 g (the audible thump on
every reset), AoA overshot to 2.14° and took ~0.7 s to settle at its true trim of 1.41°.

- **`_trimAoA`** is sampled every tick of a card's opening `arm` segment, so by the end of it the
  value IS that airframe's trim AoA at that card's speed, altitude and mass — the aircraft's own
  answer, not a solver's and not a constant. `PlaceOnCondition` then writes the velocity that far
  **below** the level nose. Zero until an arm has been flown, so a run's first placement is
  byte-identical to 0.87.
- **The nose stays level and the velocity is pitched down**, not the reverse. A card's `arm` demand is
  horizontal and the law puts the *nose* on it, so the equilibrium already has the flight path one AoA
  low; pitching the nose up instead would trim the aerodynamics and be corrected straight back down,
  trading one transient for another. This lands the placement in the steady state the arm was going to
  reach anyway, and leaves `off` at row 0 near zero so the stale-demand signal keeps its meaning.
- Recorded, not asserted: the `# entry` header line and the `[card] entry condition set` log line both
  carry `aoaTrim=`. The check is one row — `g` at row 0 should no longer read 0.00.
- **No control-law change.** Harness only, and it applies to both arms of any A/B identically, so it
  cannot confound an experiment.

Also in this release: `FLIGHT-PROTOCOL.md` Gate A criteria A2/A3 corrected — both were written
before there was a noise floor to write them against, and both flagged a rig that passes. See the
gate text for the replacements and why bare correlation was the wrong statistic.

## 0.87.0

**Uncrewed harness, phase 2: a drone flies the mod's real control law.** Everything under the
consumer was already per-aircraft (`ChaseController` v0.82, `ScenarioPlayer` + `ManeuverRecorder`
v0.86) — a drone's card demand was written and read by nothing, and `Drone.Fly` was still the
built-in level-hold. Now a drone starts a test card on its own first pilot step and chases that
card's demand through `ChaseController.Apply`: same law, same pipeline, same per-aircraft controller
and recorder the human flies, so a drone capture and a crewed capture measure the same thing.
**No gain, gate or schedule in the law changed.**

- **The aim demand is a parameter, not a global.** `Apply(ac)` is now a one-line wrapper over
  `Apply(ac, aimTarget)`; the player's wrapper passes `AimRig.AimForward` (one marker per process,
  and it is the human's), a drone passes its own `ScenarioPlayer.AimDemand`. That is the single
  reason `Apply` could not be shared before.
- **What `Apply` reads that a drone does not have** was exactly three things, all one-per-process and
  all the player's: the AimRig marker (passed in), the Rewired player-0 stick (the whole manual-override
  block is now gated on `!_uncrewed`, so a drone never reads it — and can never drag the human's marker
  onto its own nose via `ManualReorients`), and the native virtual-joystick crosshair in `FlightHud`
  (same gate). Nothing else in the pipeline is player-scoped.
- **`ChaseController._uncrewed`** is a per-instance bool with exactly one writer, `FlyUncrewed`, which
  is reachable only from `TestDronePatch` → a dictionary an aircraft can only enter through
  `TestDrone.Spawn`, which asserts `ac.Player == null`. So the crewed path cannot reach the new
  branches; `check-architecture.py` now enforces both halves of that (one writer, one calling file)
  rather than leaving it as an argument.
- **`FlyUncrewed(ac, aimDir)`** is `BeginFrame` + `Apply` in one call, because a drone has one seam
  where the player has two (prefix/postfix). The order is identical, and `TestDrone.OnPilotStep`
  still runs `Aircraft.FilterInputs()` afterwards — the FBW pass no pilot state is there to run.
- **The card owns the drone's throttle too.** `OnPilotStep` now calls `ScenarioPlayer.OwnInputs`
  between the stick write and `FilterInputs`, mirroring the player's seam postfix. Without it a drone
  would fly a whole card at whatever `ControlInputs.throttle` held, and `0` is the game's airbrake
  trigger — the R18 failure, where a bad throttle read as a control-law energy failure.
- **Cards start per drone, at its own spawn instant** (first pilot step, not at `Spawn`: a card's
  first act is a placement that rigid-moves every part rigidbody, which is not a thing to do to a
  half-built assembly). Per drone on purpose — one key starting N cards together would put every
  replicate on the same segment boundary, which is exactly what the launch stagger exists to prevent.
  `StartSuite` is the same body the player's run key calls; it refuses with its own `[card]` line when
  no card is enabled for that airframe class, and the drone then level-holds.
- **Refusals are loud.** If the instructor declines to engage mid-card (Enabled / WriteControl off, a
  rotorcraft without `ControlRotorcraft`, a detached cockpit) the card is **aborted** with the reason
  in the CSV's `# stop` line plus a `[drone]` warning — rather than quietly finishing the run on the
  level-hold and writing a capture that reads as clean. The engage line itself is tagged `[drone]`.
- The built-in level-hold survives for the one case with nothing to chase (no card running). It is
  still not the mod's control law: never tune it, and never compare a level-hold capture against a
  card capture.

## 0.86.0

**`ScenarioPlayer` and `ManeuverRecorder` are per-aircraft instances.** They were the last two
process-wide singletons in the harness: one card, one CSV, no matter how many aircraft were flying.
With N drones that is not a worse measurement, it is N aircraft flying whichever card started last
and their rows interleaved into one file under one header. Both now follow the registry `chase` got
in v0.82 — `For(aircraft)` keyed on `Aircraft.GetInstanceID()`, `Forget`, `Sweep`, and a `Player`
accessor for the HUD and hotkeys so a drone's numbers can never reach the screen. N drones now fly N
cards and write N CSVs.

- **What stayed static, and why** (the test is: *does this value reach a CSV row or a per-flight
  decision?*). `AnomalyLog` — one log stream per process. `WTMouseAimPlugin.RunIndex` — one run per
  process. The **card library** (`_cards`/`_enable`/`_cf`) — shared read-only config. The **on-screen
  notice** — one screen. The recorder's **take counter** — it numbers *files opened*, not aircraft
  state, which is what keeps takes unique across concurrent writers *and* `rec=` monotonic in time
  (`compare-runs.py` orders its A/B balance check by it). The **A/B arm schedule** — see below.
- **The ABBA invariant under N aircraft.** The swept knob is a `Cfg` entry the control law reads
  *globally*, so N aircraft physically cannot fly different arms at the same instant. The invariant
  ABBA exists for — both arms hold the same mean position in the batch, so a monotonic drift cancels
  — is preserved by keeping the queue index **and** honouring the schedule only while one aircraft is
  flying a card. It now has one owner: a second suite neither resolves its own (it would save the
  first suite's already-written value as the "original") nor restores one on finish, and `ApplyArm`
  **stands the schedule down loudly** if another aircraft is mid-card instead of flipping a global
  knob under it. Flipping mid-card mislabels part of the other capture; "don't advance while anyone
  else flies" degenerates to arm A forever under a launch stagger. Real concurrent A/B needs the knob
  to become per-aircraft state read through the controller — a change to how the law reads config.
- **`Forget` closes an open capture.** A drone despawned mid-card used to be able to leave a
  `StreamWriter` open with no `# stop` line — a truncated file that reads as a clean completion. Both
  of `TestDrone`'s removal paths now call one `ForgetState(id)` covering all three registries.
- **New column `frameMs`** (64 total), the rendered-frame time that fixed step saw. The launch
  stagger exists *because* a frame hitch lands on whatever segment is running, so N replicates flying
  the same segment at that instant stop being independent samples — an assumption backed only by a
  `[drone] frame hitch` warning in a log nobody diffs. Now per-row evidence.
- **Per-drone airframe.** `Cfg.DroneAirframe` accepts a **comma list**, indexed by lane and wrapping,
  so a batch can be heterogeneous; a single value behaves exactly as before. A bad `jsonKey` in a
  list costs its own lane and nothing else. Each capture self-identifies (sidecar `jsonKey`, the
  `# aircraft` header, and the drone filename), so `compare-runs.py`'s refusal to pool across
  airframes keeps working. **Loadout is still `null`** — the game's parameter is a `Loadout` object,
  not a name.
- **Header/row lockstep is now checked.** `check-architecture.py` counts the recorder's header
  columns and its `Sample()` row and fails on a mismatch, plus on CLAUDE.md's documented count
  drifting from the code. Two hand-maintained lists with no compile-time link had none.
- Drone captures now describe **their own** airframe: the header block read `GetLocalAircraft`, so a
  drone's CSV would have named the *player's* aircraft — silently defeating the pooling guard.

Not wired yet: a drone's card demand (`ScenarioPlayer.AimDemand`) has no consumer, because nothing
routes a drone through `ChaseController.Apply` — that is phase 2, and it is now unblocked on both
sides.

## 0.85.0

**The below-nose roll-to-align loop was positive feedback, and its own suppressor was being switched
off by the oscillation it exists to suppress.** Measured over 11 captures of the `elDn` card segment
(a 20° *down* elevation step), late 60% of the block, against its mirror `elUp` (a **larger** step,
upper hemisphere, same law) — `debugtests/GATE-CHATTER-FINDINGS.md` §5a:

| | `elDn` | `elUp` |
|---|---:|---:|
| mean `off` | **6.92° ± 2.40** | 0.03° |
| bank half-amplitude | **43.3°** | 0.11° |
| `outR` sign flips | **0.58/s** | 0.00 |
| corr(\|`azErr`\|, `blendWeight`) | **+0.918** | — |

`elDn` is the corpus's worst cross-fighting case (24% `REGRESSING`, jerk RMS 1.61 — ~3× any other
segment) with the plant *unloaded* and full authority available, while the larger step in the other
hemisphere converges to 0.03° and never touches the roll stick again. The loop: roll-to-align banks
the aircraft → bank plus pull swings the nose in azimuth → `azErr` rises → `lateralHold` rises →
`blendWeight` rises → more roll-to-align. Note what it is **not**: `blendWeight` sits 81% in the mid
band, so nothing rails and hysteresis would have done nothing (that hypothesis was tested against
sham-gate controls and killed — same document, §2).

- **`belowSuppress` is keyed to ROLL-INVARIANT belowness** (`BelowAlignSuppress`, default ON). The
  v0.67 suppressor asked "is the target below the nose" using `alignFrac`, which is measured in the
  **aircraft's own frame** — so the aircraft's bank changed the answer, and at 90° of bank a target
  straight down reads as exactly abeam. Rolling deleted the reason not to roll. That is the false
  ~85° bank equilibrium v0.67's own comment describes, restated as a feedback path. The same question
  is now asked in a **horizon-referenced frame around the nose** — axes built from `t.forward` alone,
  so no amount of roll can move the answer, and identical to the old value with the wings level.
- **The `(1 − lateralHold)` factor is deleted.** It gated the suppressor on azimuth error, i.e. on the
  symptom: `lateralHold > 0` on **88%** of ticks in that window and it removed **51%** of the intended
  suppression. Its stated job (a genuine down-*lateral* keeps its roll-and-pull) is already done twice
  over — `Clamp01(-alignFracH)` is itself a continuous belowness, so a target that is below *and*
  abeam is barely suppressed, and the existing `bigTurn` taper hands full roll-and-pull back for any
  large below-reorientation. This was the only term that let the loop's own output re-open its gate.
- **The `eAlign` channel gets a rate lead** (`AlignRateLead`, default ON). `phi` is that channel's
  entire error signal and the map was pure `phi/90` — a P-only loop against a plant with real roll
  inertia. `phi` is now led by its own **measured** rate before the map, exactly as `azErrPred` leads
  `azErr` for the turn command. It is *not* a second copy of the servo's `-rollRateF*RollDamping`: the
  bearing's total rate also carries the pitch/yaw closure (in a below-nose pushover the bearing sweeps
  while roll rate is ~0, and the align channel should stand down for precisely that) and the marker's
  own motion, so a marker sweeping around the boresight is **tracked, not braked** — the v0.83
  relative-rate lesson applied to this channel. Stands down inside the dead-astern wrap region, where
  `phi` is discontinuous and the existing two-rate anti-relay slew owns the dynamics.
  **No new constant:** the lead *time* reuses `Cfg.RollDamping`, the roll channel's already-tuned
  derivative time against the same physical loop, and the lead *angle* is that time × a live measured
  rate — so a sluggish airframe generates a small lead and a fast-rolling one a large lead, with no
  per-plane number anywhere. Same argument as the v0.78 feed-forward: the tuning-free part is the
  kinematics.

- **Recorder: 60 → 63 columns**, `bSup`, `bWt`, `phiLead`, for the same reason `aimRate` and
  `iGate`/`leadDeg` exist — "the fix fired and helped" and "the fix never fired" both read as a
  smaller roll oscillation. `bWt` in particular is the loop gain the +0.918 correlation was measured
  on, so it is the number that says whether the feedback path is still open. Recorded on **both**
  sides of **both** toggles, and unlike `leadDeg` these are *not* recoverable by arithmetic from the
  existing columns (neither `alignFrac` nor its roll-invariant twin was ever a column).

Both levers are checkboxes rather than a rebuild so the change is A/B-able inside one session with
`ScenarioArmToggle` — and they are **separate** levers on purpose: the main risk of the belowness
change is a regression in the upper hemisphere, which is unattributable if both mechanisms move under
one knob. Everything above is live geometry and measured rates only; a target at or above the nose is
untouched by construction, which is where the already-perfect hemisphere lives.

## 0.84.0

**The harness was manufacturing false positives; this is the gate on every A/B downstream of it.**
Forensics over ten sequential replicates of one card, one build, one config
(`debugtests/R21-FINDINGS.md`) found the replicates are **not exchangeable**: `terminalOffDeg`
correlates with run index at **r = −0.824** and `gSustained` at **−0.839**. Split the ten runs of that
*single unchanged arm* in half and the halves differ by **0.077° against their own 0.073° minimum
detectable effect** — doing nothing scored as a statistically significant result. Nothing in this
release touches the control law; it is entirely about making a measurement mean something.

- **The entry condition now actually re-establishes the entry condition.** Reading the ten captures
  back, the *placement* was never the problem — the first recorded sample of all ten runs is 250.1 m/s
  at 4000.0 m, the recorder's own precision. Three things leaked around it, and all three landed on
  the 6 s `arm` window, i.e. on the state the **scored** segment starts from:
  - **Position was never reset.** Only an altitude delta was applied, so the aircraft walked **30 km
    downrange** across the batch (`posZ` 527 → 30 395 m) and no two replicates flew the same air.
    The placement now snaps back to an **anchor** — position *and* heading, captured from the pilot
    on the first placement of a run and re-imposed by every replicate after it. Held in the
    `GlobalPosition` (datum-relative) frame, so a floating-origin rebase partway through a long batch
    cannot move the target out from under it.
  - **The aim demand was stale for one tick.** The placement returned without writing one, so `Apply`
    ran that same tick against the *previous* card's last marker and the freshly levelled attitude.
    Measured at the first recorded sample: `outP` +0.089 / +0.021 / +0.061 on runs 1–3 against
    **−0.487 / −0.487 / −0.487** on runs 8–10 — half a stick of leftover pitch. Those runs climbed
    during `arm` (3972 m vs 3965 m) and therefore entered `turn360` **slower** (271.3 vs 273.2 m/s).
    That is the observed entry-airspeed drift, visible in the recorded columns. The placement now
    writes the demand the card is about to ask for.
  - **The controller carried over.** `ChaseController` became per-*aircraft* in v0.82 and every
    replicate is flown by the same aircraft, so integrators, the heading/marker-rate filters, the
    `_pitchEff` estimator and the slewed output all crossed from the end of one run's 80°-bank
    descending turn into the next run's entry. The placement now calls `ChaseController.Forget(ac)`;
    `For()` rebuilds it on the next postfix call, probes and all.

  Both physics-write rules are unchanged and still mandatory: `Pilot.velocityPrev` is zeroed before
  the velocity write, and the snap-back is one rigid transform applied to **every** `partLookup[].rb`
  so no `FixedJoint` sees a relative change (a 30 km root-only move would be a spectacular version of
  the R15 explosion).

- **What is deliberately NOT reset, and what now records it.** *Engine spool* is not reset and does
  not drift — `OwnInputs` pins `ci.throttle` on every tick a card is loaded, including across the
  card boundary, so the engine is at the same steady state for every replicate after the first (`thr`
  records the commanded value, first-sample `spd` the achieved one). *Airframe damage* has no repair
  call and is permanent, so it is instrumented instead. *Session age* is unresettable by definition
  and is already the `tWall` column. New `# entry` header line per capture carries the reset
  provenance — pre-placement speed and altitude, `snapBackM` (how far the aircraft had wandered from
  the anchor), the fuel write, and `ctrlReset=1` — so a batch can covary out what it could not undo
  rather than be silently poisoned by it.

- **`ScenarioArmToggle` — A/B arms interleave ABBA instead of blocking A×N then B×N.** Name any ON/OFF
  setting (`RelativeTurnLead`, `IntegralStallGate`, …; `Section/Key` if it is not in `Control`) and
  one press of the run key flies both sides of it, alternating **off, on, on, off, off, on, on, off**
  by run. Blocking is exactly the design that converts a one-way session drift into a fake effect;
  ABBA lands the drift on both arms equally and demotes it to nuisance variance. The full schedule and
  its A/B tally are printed **before the batch flies** (and a count that is not a multiple of 4 is
  warned about, loudly), the knob is put back the way you left it when the suite ends, and each
  capture **names its own arm on its `# config` header line** — `arm=0` is A, `arm=1` is B, `armKnob=`
  names the setting. No filename convention, and `arm=` falls straight out of `scorecard.py`'s
  existing `cfg_params()` regex with no change on the Python side. Empty by default: off.

  *Follow-up, not done here:* `compare-runs.py` groups by airframe only, so grouping a batch **by
  arm** is a one-function change in `debugtests/` that this release does not make.

## 0.83.0

**The two defects behind the standing sustained-turn lag** (`debugtests/R21-FINDINGS.md`, ten
replicates of `fixedwing-sweep` on the KR-67). The card parked at a 9.4° azimuth lag that never
closed, with **nothing saturated**: 5.44 g of 9, 6.96° AoA of a 27° limiter, 39% pitch-stick reserve,
and only 63% of the airframe's available turn rate commanded. Every limit that *was* binding
(`predFloor` 82%, `MaxBankAngle` 97%, the roll blend 97%) was a limit in the **law**, not the plant.
Both fixes are behind their own checkbox, both default **ON**, and both are toggleable in-session
via F1 with no rebuild — the flip genuinely restores the old path.

- **`RelativeTurnLead` — the anticipatory lead was leading against the wrong rate.** `azErrPred =
  azErr - headingRateFilt·TurnLeadTime` treats the *absolute* nose heading rate as overshoot to be
  braked. But `azErr` is the nose-to-marker heading angle, so its own derivative is
  `markerRate - noseRate`; the v0.51 form is the true derivative **only when the marker is standing
  still** — which is exactly the regime it was measured in (eight recordings of a pilot correcting
  onto a fixed point). Tracking a *sweeping* marker, the nose is deliberately rotating **at** the
  target and the lead was braking that tracking rotation: measured 7.85° of lead against a real 9.31°
  error, i.e. **84% of a genuine error cancelled**, with `headingRate − aimRate = +0.009 °/s`. It also
  fought the v0.78 feed-forward head-on — that term adds `aimRate` to `omega` at unit gain while this
  one subtracted `TurnLeadTime·AssistTurnRateGain = 0.60` of the same rate back out, so **60% of the
  feed-forward never reached the plant**. Leading on the *relative* rate
  (`headingRateFilt − _aimAzRateFilt`) makes the term true PD damping on the azimuth error: identical
  to v0.82 against a stationary marker (marker rate is zero there), and no braking at all in a matched
  sustained turn, where the standing error finally sees the full configured `AssistTurnRateGain`.
  **Bounded by construction** — the brake/floor clamp still confines the result to
  `[azErr·predFloor, azErr]`, so no marker sweep in either direction can command more bank than the
  raw error already justified, and the v0.52 anti-relay argument is untouched.
- **`predFloor` was reviewed for deletion and KEPT.** It was binding on 100% of the settled window and
  holding the effective proportional gain at 0.28 of a configured 0.92, so it looked like the third
  stacked saturation to remove. It isn't: what it defends against is the v0.54 *rectifier* — heading-
  rate ripple pinning the prediction to 0 while degrees of real error remain, which produced the 0↔65°
  bank sawtooth at ~1.5 Hz — and that failure lives entirely in the **stationary-marker** regime, where
  `_aimAzRateFilt` is zero and the relative-rate lead is bit-identical to the absolute one. The change
  removes nothing the floor was guarding. What it does instead is make the floor **self-release** in
  the sustained case: with the tracking rotation no longer subtracted, `azErrPred` lands near `azErr`
  and the floor simply stops being the binding constraint. Deletion over addition, except when the
  thing being deleted is still load-bearing somewhere else.
- **`IntegralStallGate` — the integrator was gated dead exactly where it was needed.** `_iPitch`/
  `_iYaw` wound on `fineBlend = clamp01(1 − off/FineAngle)`, i.e. on error **magnitude**. At the
  observed `off ≈ 10.2°` against `FineAngle = 6` that gate is *identically zero*, and the capture
  bears it out: `iPitch` at ±0.001 against its 0.12 cap for the whole 30 s turn. The term whose entire
  stated purpose is killing steady-state residual was switched off precisely because a steady-state
  residual existed. The condition for integral action is not "is the error small" but "**has the
  proportional path failed to close this error**", so the gate is now `max(fineBlend, stall)` where
  `stall` is a **dimensionless ratio** — what fraction of the nose's own rotation is going into
  shrinking the error (R21: nose 11.58 °/s, error closing 0.033 °/s → 0.3% → stalled). A ratio on
  purpose: an absolute deg/s "is it closing" threshold is a per-airframe constant in disguise.
- **Windup is what the persistence half exists for.** The ratio alone cannot tell "stalled forever"
  from "hasn't started yet" — only time separates them — so it is held through an asymmetric filter,
  4 s to believe a stall and 0.2 s to drop it. Through a full-authority roll-in the gate reaches only
  ~0.25, and the instant the pull starts closing the error it collapses, leaving the existing leak the
  whole pull phase to bleed off whatever wound. Cap, leak and both v0.55 anti-windup freezes are
  unchanged, and `yawCapped` additionally suppresses the *new* path only — winding against a turn
  demand the airframe cannot fly is the one way a persistence gate winds forever, and that is the
  regime a low-limit STOL trainer lives in.
- **Recorder: 58 → 60 columns**, `iGate` and `leadDeg`, for the same reason `aimRate` exists. Both
  fixes make a standing lag smaller and so does a fix that never fired; without these a capture cannot
  tell those apart. `iGate` is the wind gate the integrator *actually* used (with the toggle off it
  equals the old `fineBlend` exactly, so "the gate never opened" is visible rather than inferred);
  `leadDeg` is the lead *actually* subtracted from `azErr`. Since `azErr`, `headingRateFilt` and
  `aimRate` are already columns, which branch ran is checkable by arithmetic, and `predFloor` binding
  is recoverable as `azErrPred` vs `azErr − leadDeg`. Both recorded on **both** sides of both toggles.

## 0.82.0

**`ChaseController` is one instance per aircraft instead of one pile of statics.** This is the
blocker phase 2 of the drone harness sits behind: the controller held ~90 mutable `static` fields —
integrators, low-pass filters, the anomaly ring buffer, the FBW/canard/helo reflection caches, the
phase and maneuver trackers — so N aircraft flown at once would not merely degrade each other's
captures, they would share one integrator and make every capture meaningless.

- **The control law is untouched, and provably so.** The refactor is `internal static class` →
  `internal sealed class` plus deleting the word `static` from 107 per-aircraft declarations. **No
  method body was edited** — not a gain, not a clamp, not a sign, not a reordering. That is the
  entire reason for doing it this way: with the bodies untouched the law is identical by
  construction, and any reference that *should* have been converted and wasn't becomes a compile
  error rather than a silently-shared float. The diff bears this out — of 110 removed lines, 107 are
  a pure `static` deletion and the other three are the class declaration and the two seam call sites.
- **`ChaseController.For(aircraft)`** is the only way to get one; it is keyed by
  `Aircraft.GetInstanceID()`, the same key `TestDrone` uses. Never `new ChaseController()`: a second
  instance for the same aircraft is a silently-reset integrator.
- **Eviction, so an unattended session stays flat.** `Forget(aircraft)`/`Forget(id)` is called from
  **both** of `TestDrone`'s removal paths — the deliberate despawn and the prune of a drone the game
  removed under us — and `For` sweeps out controllers whose aircraft Unity has destroyed. The sweep
  runs on the dictionary-**miss** path only, which happens once per aircraft rather than once per
  fixed step, so it costs nothing on the hot path.
- **The HUD reads `ChaseController.Player`.** `OnGUI` has no aircraft in hand and must show the local
  player's numbers, never a drone's, so `BeginFrame` publishes itself there only when its aircraft is
  the one `GameManager.GetLocalAircraft` calls local — the game's own definition, which an uncrewed
  drone cannot satisfy. Every HUD read is null-guarded with its pre-0.82 static default, so the
  overlay is visually identical.
- **What deliberately stayed `static`:** the Rewired player-0 cache, the anomaly stream's index and
  its three HUD-flash fields, and the `[anomaly:trail]` dump throttle — one process-wide input
  device and one process-wide log stream. Everything else, **including `LastPhase`**, is per-aircraft:
  that one feeds `ManeuverRecorder.Sample`, so leaving it shared would have written one aircraft's
  phase into another's CSV — capture corruption of exactly the kind this change exists to prevent.

## 0.81.0

**The harness can fly its own aircraft now — phase 1: spawn, fly, despawn, N at a time.** Every
measurement this project has taken cost a human sitting in a cockpit for the length of the card; a
four-replicate suite of `fixedwing-v2` is ~12 minutes of watching a marker sweep. New `TestDrone.cs`
spawns aircraft nobody is sitting in, owns their `ControlInputs`, and removes them again. **Nothing
is wired to the control law or the scenario player yet** — that is phase 2, and it lands on
`Drone.Fly`.

- **N drones, not one.** `TestDrone` keeps a live list plus a dictionary keyed by
  `Aircraft.GetInstanceID()`, and each `Drone` carries its **own** `Fly` delegate. Per-drone rather
  than one static because N drones need N independent controllers — a single shared delegate would
  funnel every drone through one instance's state, which is the same whole-file-of-statics problem
  the control law is being unwound from.
- **The AI is off by construction.** Spawning with `HQ = null` makes `Pilot.SetStartingAiState` bail
  straight to `parkedState` before any AI state is constructed, so there is no combat brain to fight
  for the stick. `SwitchState(null)` on top is belt and braces; the call site is null-safe.
  `PilotParkedState.EnterState` cuts throttle and sets the wheel brake below 1 m radar altitude,
  which is why drones spawn **airborne**.
- **`Aircraft.FilterInputs()` is called by hand.** The FBW and `RelaxedStabilityController` pass runs
  only *from a pilot state*, and an uncrewed aircraft has none — so raw `ControlInputs` would reach
  the surfaces and the drone would be a **different plant** from the one the law is tuned against
  (the FBW reads pitch/yaw as a commanded angular **rate**). Writing from a postfix on
  `Pilot.Pilot_OnAeroInputsApplied` also puts the write at the same point in the frame as the player
  path, which is what makes a drone capture comparable to a human one.
- **The player's aircraft can never be flown by this.** It can only enter the drone dictionary via
  `Spawn`, which spawns with `player=null` and then refuses (and destroys) anything reporting a
  `Player`. The postfix is a dictionary probe with a miss-returns; every other aircraft in the
  mission, and every AI, costs one lookup.
- **Staggered launch, because replicates have to stay independent.** `DroneCount` drones launch
  `DroneStaggerSec` apart (default 3 s) in parallel lanes 8 km abeam, 2 km apart, on the player's
  heading at key-press. A frame hitch lands on whatever segment is running when it happens — launch N
  drones on the same instant and one hitch corrupts the *same* segment in all N identically, which
  destroys exactly the independence they were flown for. Because that is an assumption until it is
  measured, `Time.unscaledDeltaTime` is now sampled every fixed step (`TestDrone.FrameDt`) and any
  frame over 50 ms logs `[drone] frame hitch` on the rising edge.
- **Refuses cleanly instead of throwing.** `SpawnAircraft` carries no `[Server]` attribute but ends
  in `ServerObjectManager.Spawn`, which needs an active server — so the launch is gated on
  `Spawner.IsServer`, the same question that will be enforced. Single player **is** a host, so SP and
  hosting work and an MP client is refused with a log line. Same for a missing `Spawner`, an
  unloaded `Encyclopedia`, and an unknown `DroneAirframe` key. A failed spawn leaves nothing behind.
- **New `Drone` config section, `DroneEnabled` off by default and genuinely inert while off** — the
  hotkeys are not read, and the seam costs one integer compare per aircraft per fixed step.
  `DroneAirframe` takes the same `jsonKey` a mission file's `aircraft[].type` uses (default
  `Multirole1`, which is what `harness/WTM-Range` already flies).
- Two known behaviours documented rather than worked around: despawning posts a kill message to the
  HUD (`Aircraft.ServerDisableUnit` calls `ReportKilled()` off a friendly airbase), and
  `UnitRegistry.persistentUnitLookup` is never pruned by the game, so each spawn leaks one dictionary
  entry for the life of the mission.
- The built-in level-hold is a two-gain cascade that exists **only** to prove the inputs land and the
  physics is real. It is not the mod's control law, shares nothing with it, and is scheduled for
  deletion in phase 2.

## 0.80.0

**`ScenarioRepeat` — fly the selection N times back to back from one key press.** Replicates were
already possible, but only by typing a card name repeatedly into `ScenarioCardSet`, a text field that
in practice nobody finds; ticking the checkbox instead gave exactly one run and looked like the card
was broken. A single run measures nothing — every metric needs a spread before a change can be called
real — so the replicate count is now the obvious control it should always have been.

- Default 1, range 1–20. Each replicate re-applies the full entry condition (speed, altitude,
  attitude, fuel) and writes its **own** capture file, so 4 replicates give 4 independent CSVs.
- The selection repeats as a **block** (A,B,A,B — not A,A,B,B) so one-way session drift lands on every
  card equally instead of stacking on the last one.
- Expanded after the airframe-class filter, so the number means "runs you will fly", not "runs
  requested, some of which silently vanish". `ToggleSuite`'s existing `suite start: N card(s)` line
  stays the single authoritative count — the expansion deliberately logs nothing of its own, since
  `SelectCards` is also called by the standalone entry key, which flies nothing.

## 0.79.0

**New built-in card `fixedwing-sweep` — 36 s of `arm` + `turn360`, nothing else.** The v0.78 question
is about one segment, and answering it with `fixedwing-v2` costs ~3 minutes per replicate. Four
replicates of the short card cost 2.5 minutes total, which is the difference between an A/B that gets
run on both arms and one that gets run once and argued about.

- **It carries its own baseline; it is NOT comparable to `fixedwing-v2`'s `turn360`.** There the sweep
  runs last, entering at ~235 m/s off a spent energy state; here it enters at the gated 250 m/s, so
  the same derived 12.066 °/s demand needs n=5.46 instead of 5.24 — about 4% harder. Both A/B arms
  must therefore be flown on *this* card. Scoring an `ON` run of the short card against R19's 9.536°
  would be comparing two different flight conditions and calling the difference a law change.
- No new segment tags (`arm` and `turn360` are both already in `scorecard.py`), so the card/scorer
  pair that silently drifted in v0.71 is not touched here.
- Nothing else changed: the control law, the config surface and the recorder are bit-identical to
  0.78.0. `MarkerRateFeedForward` remains the live A/B toggle and `mrFF=` in the CSV header remains
  what identifies which arm a capture flew.

## 0.78.0

**The aircraft tracked a sweeping marker perfectly and stayed 9.54° behind it the whole way.** Four
valid replicate runs of the `turn360` segment: the marker sweeps at a constant **12.066 °/s**, the
aircraft **achieves 12.02 °/s** (±0.01 across runs), and it holds a **standing 9.54° azimuth lag**
with a stdev of 0.021°. Nothing is saturated — |outP| max 0.587, |outR| max 0.793, 5.17 g of a 9 g
limit, AoA 7.7° of a 27° ceiling. The rate is right and the pointing is wrong, steadily, with margin
everywhere.

- **The azimuth loop is pure proportional in this regime, and its one integral term is dead.**
  `_iYaw`/`_iPitch` wind on `fineBlend = clamp01(1 - off/FineAngle)`, and with `FineAngle` = 6 that is
  *exactly* 0 at `off` = 9.5. The CSV confirms it: both integrators flat 0.000 for the whole 30 s. A
  P-only loop cannot produce a rate without an error to produce it from — e_ss = ω/K = 12.07/1.31 =
  **9.2°**, which is the measurement to a tenth of a degree. The lag was never a tuning failure; it
  was the loop doing exactly what its structure requires.
- **Feed the rate forward instead of making the loop earn it.** The marker's own signed world-azimuth
  rate is differentiated across the fixed step, low-passed with the *same* time constant as the nose
  heading rate (one shared const now, so they cannot drift apart — they meet inside the same `omega`,
  where a tau mismatch reads as phase, not gain), and added straight into the commanded turn rate at
  **both** lockstep omega sites. Differentiated via `Atan2`/`DeltaAngle`, so the ±180° wrap is a no-op
  instead of a 21600 °/s spike.
- **Gain is exactly 1.0 and there is nothing to tune.** Matching the marker's rate is kinematics, not
  a per-airframe constant. It is added *before* the existing achievability cap, so the probed
  per-airframe `omegaMax` bounds it exactly like the proportional demand — it can never command a turn
  the airframe cannot fly — and `yawCapped` still reflects the total demand.
- **Nothing that points, captures or holds can move.** The feed-forward is identically zero whenever
  the marker is stationary, and every card segment except `turn360` is a step to a fixed direction
  followed by a hold. Only sustained tracking of a moving marker sees any change at all — which is
  what makes this scoreable against the R19 baseline in a single session.
- **New config `MarkerRateFeedForward`** (Control, default on). It exists purely as the A/B lever: fly
  four runs on, toggle it in F1, fly four off, no restart and no DLL swap, everything else
  bit-identical. Off is byte-for-byte v0.77 behaviour.
- **New CSV column `aimRate`** (58 total, appended last so column-indexed analyzers keep working) — the
  filtered signed marker azimuth rate, recorded on *both* sides of the toggle. Without it a run cannot
  distinguish "the feed-forward fired and helped" from "the feed-forward never fired": both show up as
  a smaller azimuth lag, and only one of them is the fix.

Not fixed here, deliberately — one variable per scored change: the az-step steady residuals
(0.16 / 0.23 / 0.49 / 0.77°), the 89° bank that overshoots the law's own declared 72° `MaxBankAngle`
(that ceiling is enforced on `targetBank`/`tBankE`, and the sustained-turn path bypasses both), and
the dead integrator gate itself (`fineBlend` zeroing every integral term outside the 6° fine cone).
Each gets its own scored change.

## 0.77.0

**R18 flew the whole card at idle, and nothing in the capture said so.** The entry force and the
placement key both worked (v0.76 holds), so all four runs got airborne on condition — and then bled
250 → 116 m/s, dropping 3.5 km until two of them tripped the altitude-floor abort. The control law
was never involved: engine at 33% RPM for 189 seconds.

- **The cause is a config value that outlived the meaning it was written with.** BepInEx only writes
  a key it has not already written, so changing a code default never reaches an existing install.
  v0.73's `ScenarioThrottle = 0` meant *"use the airframe's own cruiseThrottle"*; v0.74 rewrote the
  knob so 0 meant a literal zero throttle, and the 0 already on disk silently changed meaning under
  it. The stored value wins forever, and it never announces itself.
- **The floor was an epsilon where it should have been a throttle.** `EntryThrottle()` clamped to
  0.001 — one ulp clear of the game's exact-zero airbrake test (`Airbrake.Update` reads the *same*
  `ControlInputs` the card writes: `openAmount += (throttle == 0f ? +open : -open)`), so it was
  technically safe and completely useless. 0.001 is idle: `Turbojet.FixedUpdate` spools toward
  `Mathf.Lerp(minRPM, …, t)`, and at t≈0 the target is still `minRPM` — the observed 33% is the
  engine's own idle floor, not a throttle-mapping bug.
- **Below a manoeuvring throttle now means UNSET, and heals itself.** Under 0.25 snaps back to the
  default in the config entry, which fires `SettingChanged` — so the heal is logged as a `[config]`
  line, lands in the recording header, and leaves F1 showing what is actually flying instead of a
  value the card is quietly ignoring. A stale install fixes itself on the next card; the card path
  can no longer write exact zero, so it can no longer open the airbrake either.
- **New CSV column `thr`** (commanded throttle, 57 total, appended at the end so column-indexing
  analyzers keep working). Throttle is the one flight input a card takes over and the only one the
  capture could not show — so an idle run was indistinguishable from a control-law energy failure.
  Commanded, not achieved: the engine lags it through its own spool, so a disagreement between this
  column and the speed trace is itself the signal.
- **The card-start log line now states the throttle it is flying.**
- **`analyze-wobble.py --digest` was silently reporting zero anomalies — on every capture ever.**
  `load_anomalies()` rebuilt the sidecar path as `mouseaim-anomalies-<session>.log`, but the real
  name carries version and run first (`mouseaim-anomalies-v0.76.0-R18-<session>.log`), so the
  `open()` always missed and a bare `except OSError: pass` turned the miss into an empty list. R18
  had 11 anomalies per run and the digest showed none. It now scans every `mouseaim-anomalies-*.log`
  beside the CSV and filters on the `rec=` field the lines already carry — the filename stops being
  load-bearing. Regression test added; verified against R18 (0 → 11).

Not fixed here: the recorder still has no aircraft-destroyed column, `scorecard.py` does not yet
reject a run on out-of-band `thr`, and the altitude floor is still a fixed 500 m constant rather
than a card-declared one.

## 0.76.0

**Same explosion, one layer down — and the fix for it was the wrong fix.** v0.75 merged the aircraft's
part rigidbodies onto the root before moving it. That worked from the entry key and destroyed the
aircraft from the run key: identical code, opposite outcome.

- **`Destroy` is deferred.** `SetSimplePhysics` → `MergeWithParent` calls `Destroy()` on the part
  rigidbodies and joints, and Unity defers destruction to **end of frame**. So when the call returns,
  every joint is still alive and still connected.
  - The **entry key** fires in `Update`: merge → write → frame ends → destroys processed → *then*
    physics. The stretched joints never exist during a simulation step.
  - The **run key** ran in `FixedUpdate`: merge → write → `Physics.Simulate()` immediately, same
    frame, joints alive and now stretched by the full displacement. Same explosion as v0.74.
  - The pilot found the tell without the mechanism: pressing the entry key *then* the run key
    survived, because the aircraft was already on condition and the displacement was ~0.

- **Replaced the merge with a rigid transform of the whole assembly.** Apply the same rotation about
  the same pivot, the same translation and the same velocity to `Aircraft.rb` *and* every
  `partLookup[].rb`. No joint sees a relative change, so there is nothing for the solver to correct.
  - Destroys nothing, so there is no deferred-destruction hazard and **no frame staging** — the
    intermediate 3-phase placement machine, the owed rebuild, and its Update-side safety net are all
    deleted. It is a single call again, correct from either clock.
  - Works unchanged in simple physics: merged parts share the root's `Rigidbody` and are skipped by an
    identity check, so there is no physics-mode branch at all.
- **It also fixes the broken cockpit HUD**, which was the same bug wearing a different hat.
  `FlightHud` caches the cockpit's `Rigidbody` in a private `cockpitRB` field, set **once** in
  `SetAircraft()` — which is called from exactly one place (`CameraCockpitState.EnterState`) and is
  guarded by `this.aircraft != aircraft`, so it never re-fires for an aircraft you are already in.
  `FlightHud.Update()` then early-outs on `if (!(cockpitRB != null)) return;`. The merge destroyed
  that Rigidbody, Unity's `==` override started reporting the stale reference as null, and the whole
  Update body stopped for the rest of the flight: flight-path marker, velocity vector, pitch ladder
  and heading tape all froze together. The AoA *numbers* were still being computed correctly — they
  re-read `cockpit.rb` fresh every call — but the AoA bracket is anchored to the frozen marker
  cluster, so it looked like the AoA readout had died. Nothing is destroyed now, so nothing goes stale.
  - **A HUD already broken by v0.75 does not heal itself** — `SetAircraft` only re-fires on a
    *different* aircraft, so changing camera view will not fix it. Respawn or restart the mission.
  - Two further round-trip defects found and now moot: control-surface `HingeJoint`s (`movingJoints`)
    were destroyed by the merge and **never rebuilt** by `SetComplexPhysics`, and
    `SetComplexPhysics` resets centre of mass but **not** the inertia tensor.
  - The game itself never does this round trip on a player-occupied aircraft: `SetSimplePhysics`/
    `SetComplexPhysics` run only via `SetLocalSim`, i.e. once at spawn. Every subsystem downstream was
    written assuming that invariant, which is why so much broke at once.

- Placement failure now refuses the run from the card path too, not just the entry key.
- The altitude floor no longer aborts a card **before** the placement has run. It gated on raw
  altitude, so pressing the run key on the runway refused the run at 151 m instead of lifting the
  aircraft to the card's 4000 m entry — the floor now guards a *running* card only.

## 0.75.0

**The entry force was still destroying the aircraft — for a second, unrelated reason.** v0.74 fixed
the phantom G reading; this fixes the real velocity spike underneath it. Session R15 pressed the run
key three times while already on condition (4000 m, ~250 m/s, level) and lost the airframe every
time, which ruled out the velocity delta as the cause outright.

- **An aircraft is not one rigidbody.** Under complex physics — what anything the player flies is in
  — `Aircraft.SetComplexPhysics` unparents every `AeroPart` to the world, gives it **its own
  `Rigidbody`**, and joins it back to its parent part with a `FixedJoint`. Writing `Aircraft.rb`
  moves the fuselage root and leaves the wings, tail and gear where they were: every joint is
  stretched by the full displacement, and PhysX pays that back as a velocity impulse of roughly
  `err/dt` across the whole assembly. The G path then reads the result and destroys the airframe.
  - Measured from the R15 anomaly trails: a **14 m** altitude step added **~262 m/s**, a **35 m**
    step added **~665 m/s** — **19× `err` in both**, i.e. linear in the displacement. That linearity
    is the constraint solver's signature; it is not game logic and there is nothing in C# to patch.
    Reported G: **133 g** and **342 g**.
  - Fix: `SetSimplePhysics()` **before** any `rb` write. That merges every part back onto the root,
    restoring its exact local position and destroying the joints, so there is one body to move and
    nothing left to stretch. It is the same ordering the game itself uses at spawn — `OnStartClient`
    writes position, rotation and velocity *before* the call that builds the per-part bodies.
  - `SetComplexPhysics()` rebuilds them, but **two frames later**: `MergeWithParent` `Destroy`s the
    part rigidbodies and Unity defers that to end-of-frame, so rebuilding on the same frame would
    have `AddComponent<Rigidbody>()` land on a component that is still present.
  - That rebuild is **owed, not opportunistic**: `Aircraft.CheckPhysicsLod` — the game's own restore
    path — has no callers in 0.34. Nothing else will ever undo the merge.
  - v0.74's `velocityPrev` zeroing stays. It was correct and is still needed; it just addressed the
    smaller half. The two failures look identical and have nothing to do with each other: one is a
    phantom reading of our own write, the other is real velocity the aircraft genuinely acquires,
    one or two ticks later, from a mechanism the mod never touched.

- **New key: on-condition (F3, `Scenario/ScenarioEntryKey`).** Puts the aircraft on the first enabled
  card's declared entry condition — speed, altitude, wings level, heading unchanged, fuel set —
  **without starting the run**. Set up, look around, press the run key when ready. It is also the
  isolated test for the teleport itself, which is worth having: the placement has now been the cause
  of two separate airframe losses, and until this key existed it could only be exercised by
  committing to a 3-minute capture.
  - Card selection is now shared between the two keys (`SelectCards`), so F3 places you exactly where
    the run key would have started you. Two answers to "which card" is how they drift apart.
  - `ForceEntryCondition` no longer carries the "is forcing enabled / does this card declare a
    condition" guards — those belong to the card path and were in the way of calling it directly.

## 0.74.0

**Three bugs the first forced-entry flight session (R14) found, all in v0.73's own new code.**

- **The entry force was destroying the aircraft.** Pressing the run key while slow, diving, or at
  high AoA killed the airframe on the spot; pressing it while already near the card's entry
  condition was harmless. Cause: the game derives G by **differencing velocity across fixed steps**
  (`Pilot.Pilot_OnAeroInputsApplied`), and past **20 g** calls `TakeGForceDamage(g²)`, whose damage
  goes as `(g² − 400) × 0.007`. A teleport is a one-tick velocity *step*, so setting 250 m/s onto an
  aircraft whose velocity vector was 28° off the nose reads as ~870 g and applies four figures of
  damage. The fix zeroes `Pilot.velocityPrev` (and `Aircraft.velocityPrev`) *before* the velocity
  write, taking the engine's own escape hatch — that zero check exists so a freshly-spawned aircraft
  doesn't report a spike on its first tick, which is exactly why the game's own spawner can set
  `rb.velocity` on an airborne mission aircraft and get away with it.
  - **Because damage goes as the square of G, this was never binary.** R14's one "good" run still
    took a ~150 g step at entry and roughly 155 points of damage before it flew a single segment.
    Runs captured under v0.73 with a non-trivial entry delta were flown on a damaged airframe and
    should not be pooled with v0.74 captures.
  - A failed entry force now **refuses the run** instead of falling through. Forcing bypasses the
    pre-flight gate, so falling through would have flown the card from whatever state the pilot was
    in and scored it as if it were on condition.
- **The card was flying on afterburner.** `ScenarioThrottle` defaulted to the airframe's own
  `cruiseThrottle`, on the assumption it meant "the throttle this aircraft cruises at". It does not
  — it is the **AI pilot's cruise-hold setpoint** (0.9 on the Ifrit), used only by the AI state
  machine, and it lit the burner. The aircraft then accelerated to **439 m/s** and pulled **9.34 g**
  against a 9 g limit on the `az150` segment (and 9.25 g on `reversal`) at only 4.3° AoA — a
  high-speed overstress, not a stall. **A baseline has to be a speed the airframe can still
  manoeuvre at, not its fastest.** Default is now a flat **0.7** for every airframe.
- **The airbrake was still the pilot's.** v0.73 forced `ControlInputs.brake = 0` — but that field is
  the *wheel* brake. The game's `Airbrake.Update` opens the boards whenever `ControlInputs.throttle`
  is **exactly 0**, and it reads that every *rendered* frame while v0.73 only wrote throttle on the
  *fixed* step, so an idle lever cracked the boards open on every frame in between and bled energy
  off-script. New Harmony patch **`PilotThrottlePatch`** on `PilotPlayerState.PlayerThrottleAxis1Controls`
  (which runs in `Update`, and which the mod had never patched) skips the native body outright while
  a card plays, so the hardware axis never reaches `ControlInputs` at all. Side benefits: the
  throttle HUD now shows what the card is actually flying, and `customAxis1` — written by the same
  method — stops being an uncontrolled input during a capture. Throttle is floored at 0.001 so a
  pinned value of 0 can never re-trigger the airbrake.
- A hover card (one that declares no entry airspeed) keeps the pilot's collective — pinning a fixed
  throttle on a rotorcraft would just drive it into the ground or the sky.

### WTM-Range — the "isolated" range was spawning AI, and had no way in

The range had `"factions": []` and `"airbases": []`, on the assumption that empty meant isolated. It
means the opposite. `FactionHQ.OnMissionLoad` runs for every faction HQ baked into the map —
`Terrain_naval` always carries Boscali and Primeva — and `Mission.EnsureFactionExists` auto-inserts a
**default** `MissionFaction` for any the JSON omits, with `AIAircraftLimit = 6`. `DeployAIAircraft`
then starts filling the range about five seconds in. Emptying the list didn't remove the factions, it
removed the mod's control over them. It also left the player with no faction to join and no airbase
to spawn from, and the pre-placed `"faction": ""` aircraft spawned neutral and uncontrollable.

Fixed by listing **both** factions with an explicitly zeroed AI budget (the shipped "Free Flight —
Ignus Archipelago" mission does exactly this), enabling one real airbase, and giving the subject
aircraft a real faction. Ground truth came from the shipped mission itself: built-in missions are
plain-text JSON inside `NuclearOption_Data/resources.assets` (classic `Resources.LoadAll<TextAsset>`,
not addressables), so the faction and airbase names are verbatim rather than reconstructed.

`check-mission.py` enforced the wrong invariant and passed the broken file, so it now checks the
inverse: every map HQ listed, each AI-budget field explicitly 0 (the class defaults are the trap), at
least one airbase, and every `UniqueName` matched against the real per-map list. An unrecognised
airbase name is **not** a load failure — `Mission.SetupAirbase` only logs and drops the entry — so the
validator is the only thing standing between a typo and a wasted test flight.

**Known gaps this session exposed, not fixed here:** the recorder has no aircraft-destroyed column
and the card has no watchdog, so a dead aircraft leaves the capture silently open until the run key
is pressed again (both failed runs produced a 1-row CSV closed by a manual keypress). And the card's
demanded turn rate is derived **once**, from entry airspeed — if the aircraft still accelerates at
0.7 throttle, that demand drifts toward the airframe's limit as speed builds.

## 0.73.0

**The harness now puts the aircraft on condition, and says so on screen.** Both changes come
straight out of the first clean measurement session (R13, four Ifrit runs of `fixedwing-v2`), which
established a **1–3% run-to-run noise floor** on every metric that matters — tight enough that
hand-flown entry state is now the dominant error term rather than a rounding detail.

- **`ScenarioForceEntry` (new, default on).** A card that declares `startSpeed`/`startAlt` now has
  them **applied** at card start — speed, altitude and a wings-level attitude, heading preserved —
  instead of refusing to run until the pilot flies there. Hand-flying to "roughly 250 m/s at roughly
  4000 m" is not repeatable to the precision the metrics now resolve; R13 held everything else
  constant and `turn360`'s `deltaEnergyHeightM` still spread **35.5%** (−107 m to −285 m) on
  throttle setting alone, while `meanTurnRateDegS` in the same segment held 1.3%. The card's opening
  `arm` segment (already excluded from scoring) absorbs the transient. Turn the setting off to get
  the old refuse-to-start gate back; ungated cards are unaffected either way.
  - **Order is load-bearing in `StartCard`:** the force runs *before* `SustainableTurnRate`, which
    reads live airspeed. Deriving the sweep rate first would key the card's headline stimulus to
    whatever speed the pilot happened to be at — the exact variable the entry condition removes.
  - This is **the first place the mod writes aircraft physics state** (`rb.position/rotation/
    velocity`) rather than only control inputs. Noted as such in `ARCHITECTURE.md`. It is fail-soft:
    a throw is caught and logged, and the entry gate remains as the backstop.
- **`ScenarioEntryFuel` (new, default 1.0 = full tanks) pins MASS.** Fuel burn is a one-way drift,
  which is the dangerous kind — the four R13 runs lost **1255 kg (5.1% of gross) monotonically**,
  larger than the noise floor they were used to measure, so an uncontrolled tank turns a mass trend
  into what reads as a law difference. Set to 0 to leave fuel alone; lower it to fly the same card
  at a lighter weight. Stores are deliberately untouched: a card fires nothing, so loadout mass is
  already constant within a session.
  - `Aircraft.fuelLevel` is **not** the current gauge — it is the target ratio `Refuel()` writes into
    the tanks, and `FuelTank.Refuel` sets absolutely (`fuelMass = fuelCapacity * ratio`, with a signed
    `part.ModifyMass`), so it drains down as readily as it fills. `Refuel(null)` suppresses the
    "Refueled by …" banner. The sibling InfiniteAmmo mod documents this trap; credited in the source.
- **A running card now OWNS every manual input, instead of asking the pilot not to touch them.** A
  variable the harness merely requests is not controlled: one slipped mouse nudge rewrote the
  stimulus and the run still scored, looking like a law difference.
  - **Mouse → marker is locked out** (`AimRig` drops `aimCapture` while a card plays). This was the
    real hole — the card wrote the demand on the fixed step while the mouse kept nudging the same
    vector every rendered frame, and the two simply summed. Free-look still works: it moves the
    camera, not the aircraft.
  - **Throttle and airbrake are written by the card** (`ScenarioThrottle`, new; default −1 = the
    airframe's own `cruiseThrottle` read from the game, so it generalises with no hand-tuned
    per-plane number). Throttle needed its own seam: the game reads it in
    `PlayerThrottleAxis1Controls` during `Update`, which the mod does not patch, so it is written
    from the postfix — after native's write, immediately before `FilterInputs` consumes it.
  - Pitch/roll/yaw needed no change; they were already owned.
  - **A stick twitch no longer aborts the run.** Killing a 3-minute capture by accident is the same
    class of failure as silently polluting one, and with the axes owned a twitch changes nothing about
    what was flown. Stopping is deliberate: the abort key (now named in the card HUD line), the run
    key, the altitude floor, or losing the aircraft.
  - `customAxis1` (flaps/tilt/nozzles) is deliberately left alone — a blanket write would retract a
    tiltrotor's nozzles mid-card.
- **Every card refusal is now on screen, not only in the log.** Pressing the run key out of
  condition, with no card enabled for the airframe, or with no aircraft, wrote a `LogWarning` and
  nothing else — indistinguishable from pressing a dead key, which is exactly how it was reported.
  A 4-second amber notice now names the reason, drawn before the HUD gates so it appears on the
  clean HUD too. Reuses the existing toast slot rather than adding a second overlay.

## 0.72.0

**The first M1 flight session found the test card, not the control law, to be the problem.** Four
acceptance runs on v0.71.0 ended with the aircraft spiralling into the sea. No control-law change
here either — every fix is to the card, the guards around it, or the recorder.

- **`turn360` demanded 100% of the airframe's structural limit.** Its fixed 20 °/s sweep is, for a
  KR-67 Ifrit at 250 m/s with n=9, exactly the instantaneous ceiling `g·√(n²−1)/V` = 20.1 °/s. No
  jet can *sustain* its instantaneous rate, so the aircraft banked to 85° pulling 9 g and descended
  — a spiral by definition, and the captures show it pinned at `outP` −1.00 with AoA a modest 6–8°
  (not stalled: energy-limited). The rate is now **derived per airframe** at card start from the
  readable `aircraftGLimit` and the entry speed, at 60% of the instantaneous ceiling. Derived once,
  not from live speed — a rate that chased V would be a feedback loop and two builds could be fed
  different demands.
- **Segment order is now load-bearing.** v1 ran the sustained turn ninth of nineteen, so the
  reversal, dead-astern wrap and all ten micro-steps were flown from a wrecked energy state — and
  the micro-steps are the entire reason the card exists. v2 runs cheap-and-precise first (micro-steps
  and fine tracking at the gated entry condition), then step response, then the energy sink **last**,
  where it can contaminate nothing after it.
- **`elDn` shortened** to 10 s at −20° (was 15 s at −30°, ~1900 m of altitude at 250 m/s on its own).
- **Cards renamed `fixedwing-v2` / `rotorcraft-v2`** — order and stimulus both changed, so a v1
  capture is not comparable to a v2 one. The card id is in the CSV filename and the `# card` header,
  so renaming makes an accidental cross-version comparison impossible rather than merely unwise.
- **Entry-condition gate.** `startSpeed`/`startAlt` were written by the card recorder and read by
  nothing; a card that declares them now refuses to start outside tolerance (fixed-wing: 250 m/s
  ±15%, 4000 m ±800 m). Uncontrolled entry state was feeding straight into every score. Cards that
  declare nothing stay ungated, so ad-hoc recordings still just work.
- **Altitude floor.** A card aborts below 500 m MSL **and hands back a wings-level climbing demand**
  as it does — `AimRig` keeps whatever the card last wrote and the instructor keeps chasing it, so
  stopping the card alone would fly into the water anyway.
- **The recorder no longer flushes to disk every row.** `AutoFlush` meant ~50 main-thread flushes a
  second; the v0.71 captures contain multi-second holes with position continuous across them (a
  freeze, not a teleport). Now flushed every 50 rows — same ~1 s crash-loss bound, a fraction of the
  syscalls.
- **Why a run ended is written into the CSV** (`# stop … reason=…`), not just the log. A run aborted
  at the floor was otherwise indistinguishable from a clean completion to anything reading the
  capture — it just had fewer rows — so a batch would silently average truncated runs in with whole
  ones. `scorecard.py` parses it and prints a loud `ABORTED:` line.

**Offline tooling — two defects that made the first session's data unreadable.**

- **`scorecard.py` scored 19 of 21 segments as "unknown", silently.** Its `KNOWN_PREFIXES` list
  (`az_step`, `hover_hold`, …) predates the cards and matched no tag `ScenarioPlayer.cs` actually
  emits except `arm` and `reversal`; everything else fell through to a 4-metric generic path, so no
  step-response, fine-tracking or sustained-turn metric was ever computed — with nothing printed.
  Replaced with a regex table keyed to the real tags, and **an unresolved tag is now a loud warning**
  in both the table and the `--json` output. The silence was the worse half of the bug: the tag
  vocabulary spans C# and Python with no compile-time link, and `check-architecture.py` cannot see it.
- **`analyze-wobble.py --digest` returned "no data rows" for every test-card capture.** Its loader
  casts all non-`phase`/`controlLaw` columns to float, so M0's `segTag` string column made every row
  throw and get dropped silently. `STRING_COLS` now has one definition (in `analyze-wobble.py`, the
  already-imported module — the reverse direction would be circular) and a dropped row is counted
  and warned about. This had made the documented first step for reading any recording a no-op.
- **New `debugtests/compare-runs.py`.** Scores N captures via `scorecard.py` and reports per-segment
  spread (min/max/mean/stdev/%). **Groups by airframe and refuses to pool** — the first session
  unknowingly compared an Ifrit against a Trainer. Truncated segments are excluded and listed rather
  than blended into a spread. Grouping key is lowercased: the sidecar says `trainer` and the CSV
  header says `Trainer` for the same aircraft, which would otherwise split one airframe into two.

**Harness range — `WTM-Range` never loaded at all.** Reported as "no airport to spawn into"; the
real cause is that the mission was rejected at load, so nothing spawned and the session tore down.
Three defects, all found by reading the 0.34 decompile:

- **No `"Mission Start"` objective.** `MissionObjectivesFactory.Load()` requires an objective with
  that exact `UniqueName` and throws `MissionLoadException` without it; `MissionManager.StartMission()`
  catches it and returns before `AddStartingUnits`. Added as a hidden `None`-type objective — which
  is also what the stock *Free Flight — Ignus Archipelago* mission uses, and what makes the
  pre-placed player aircraft auto-spawn.
- **Missing `savedLoadout`.** `SavedAircraft.savedLoadout` has no field initializer, and
  `Spawner.TrySpawnAircraft()` calls `savedLoadout.CreateLoadout(prefab)` unconditionally — an absent
  key is a `NullReferenceException` during the player's own spawn. Now `{"Selected": []}` (unarmed).
- **Wrong aircraft, and below the card's own entry gate.** `type` was `Fighter1`; the KR-67 Ifrit
  every baseline capture was flown on is `Multirole1` (the sidecar's `jsonKey`). Spawn altitude was
  3000 m, which `fixedwing-v2`'s 4000 m ±800 m gate rejects — the range would have refused to run
  the card it exists to host. Both corrected.
- **`check-mission.py` now checks the two load-time requirements** rather than only the harness's own
  isolation/pinning invariants. Both are silent-until-fatal, and neither was covered; the validator
  passed this mission while it could not load. Faction/budget turned out to be a red herring — a
  pre-placed `playerControlled` aircraft with `faction: ""` bypasses `FactionHQ` entirely.

## 0.71.0

**Milestone M1 of the instructor loop (`plans/instructor-feedback-loop.md`): scripted test cards.**
No control-law change — the mod is byte-identical when no card is running.

- **New `ScenarioPlayer.cs`.** A *card* is an ordered list of segments, each a tagged aim demand held
  for a duration, expressed in the aircraft's heading frame captured at card start (world-fixed
  after that, so the demand never chases the nose). Playback writes `AimRig.SetAimForward` from the
  **seam prefix**, i.e. the same patched `PlayerAxisControls` call whose postfix runs
  `ChaseController.Apply` — so the law reads the scripted demand in the same tick it was written.
  Zero-tick lag is structural, not a Harmony priority or an Update/FixedUpdate race.
- **Two built-in cards, chosen by `pilotType`** (not the old takeoff-distance heuristic):
  `fixedwing-v1` (194 s — arm, az steps 10/30/90/150°, ±30° elevation, 20 s fine tracking, a 360°
  sustained turn, a 180° reversal, a dead-astern wrap, ten 0.2–1° micro-steps) and `rotorcraft-v1`
  (254 s — the same plus hover hold, a 90° pedal turn and a bob-up).
- **Record your own card:** a hotkey captures what the aim demand does while you fly, sampled on the
  fixed step (so replay is frame-rate independent), saved to
  `BepInEx/config/wtmouseaim-cards/<name>.json`. Replay is indistinguishable from a scripted card.
- **Selection without a new UI:** each card gets a config checkbox, so the F1 ConfigurationManager
  panel *is* the enable/disable list; a config string overrides the set for scripted runs.
- Hotkeys (all rebindable): **F6** run suite, **F5** record card, **F4** abort. A card also aborts on
  any manual stick input — via `ChaseController.ManualStickInput()`, now the single shared definition
  rather than a second detector.
- Recorder: card name folds into the CSV filename and a `# card <name>` header line, so two builds'
  runs of the same card sort together and diff.
- Card JSON is parsed with Unity's own `JsonUtility` (ships with the game, referenced like the
  existing IMGUI/InputLegacy modules) rather than a hand-rolled parser — about 120 fewer lines we own.
- Appendix A's `translate` and `transition` segments are **deliberately absent**: neither can be
  commanded through an aim direction (they need position demand / `customAxis1`), and a segment that
  can't produce its own stimulus would score as perfect tracking — worse than not existing. They move
  to M2's `TestPilotState`.

## 0.70.0

**Instrumentation only — no control-law change.** Fixes a real defect in 0.69.0's sidecar, found by
checking it against two real captures (KR-67 Ifrit, SAH-46 Chicane) rather than trusting it.

- **Fixed: wing/drag area was read from the wrong place.** 0.69.0 summed `AeroPart`s found by
  `GetComponentsInChildren`, which reported **1 aero part and a 2 m² wing** for an 18-tonne jet
  (level flight needs S ≈ 20 m²). A complex-physics aircraft is multi-rigidbody and its parts
  *register themselves* into `Aircraft.partsWithAero`, so a hierarchy scan sees only the root part.
  Now read from that list by reflection, falling back to the old scan; new sidecar field
  `aeroPartSource` records which path was used. This matters because wing area is a required input
  to the physics-normalized turn-rate bound (`n_max = ½ρV²·S·Clmax / mg`) the scorer will grade
  against — a 10× wrong S would have made every airframe look like it beat its own theoretical best.
- **Recorder: 54 → 56 columns.** `tSeg` — seconds since the current `segTag` began, so a card
  segment's metrics don't depend on when in the session it ran; and `tWall` — unscaled wall clock
  (`Time.realtimeSinceStartup`), pinned to absolute time by the existing `# started` header line and
  the sidecar's `utc`. The pair is also a diagnostic: `dt/dtWall` should equal `timeScale`, so a run
  whose physics got clamped by a CPU stall is visible in its own capture instead of inferred.

## 0.69.0

**Instrumentation only — no control-law change.** v0.68's flight behaviour is carried forward
untouched as the frozen baseline for the instructor feedback loop
(`plans/instructor-feedback-loop.md`, milestone M0).

- **Recorder: 45 → 54 columns.** New: `alt` (m MSL), `airDensity` (kg/m³), `posX/Y/Z` (datum-relative
  `GlobalPosition`, so a floating-origin rebase can't step the trace), `velX/Y/Z` (`rb.velocity`),
  and `segTag` — a settable string tag (`ManeuverRecorder.SegmentTag`) that lets a scripted test
  card label each segment of a capture. True dynamic pressure `q = ½·airDensity·V²` is now derivable
  offline; no law code was changed to use it yet.
- **New per-run artifact: `mouseaim-rec-*.airframe.json`** alongside each CSV — a one-shot snapshot
  of everything the game will *tell* us about the airframe: `pilotType`, live mass, fuel, per-store
  loadout mass/drag, max thrust, the `AircraftParameters` envelope (G limit, corner speed, turning
  radius, max speed), buffet AoA, wing/drag area, the FBW parameter block, and sampled Cl(α)/Cd(α)
  curves (−5°…+40°). This is what makes offline *physics-normalized* scoring possible — grading a
  maneuver against what that airframe could theoretically have done. Every field is fail-soft: a
  missing type or member omits the key and never interrupts recording.
- Reflection-backed sidecar fields (fail-soft per field): `gLimitPositive`, `alphaLimiter`,
  `alphaLimiterStrength`, `maxRollAngularVel`, `maxRollSpeed`, `heloGLimit`, `heloMaxAngularVel`.
- **New offline tool `debugtests/scorecard.py`** (stdlib-only, `--selftest`): splits a capture by
  `segTag` and scores each segment by type — step response (rise/settle/overshoot), fine-tracking
  RMS and stick sign-flip rate, sustained-turn energy-height delta, hover position RMS and drift,
  AoA-limiter time and G peaks — emitting `score.json` plus a terminal table. It reuses
  `analyze-wobble.py`'s oscillation and pitch-authority detectors rather than duplicating them, and
  degrades gracefully on pre-0.69 captures that lack the new columns. Angular step segments use a
  **demand-scaled settle band** (10% of the step, clamped to 0.05–0.5°): a fixed 0.5° band is wider
  than an entire 0.2–1° micro-step and would report it settled at t=0, silently making the
  high-q small-correction regime unmeasurable.

## 0.68.0

Compatibility fix for **Nuclear Option 0.34.0**. No control-law change.

### The outage: `Leaderboard` was removed

0.34 renamed the type and moved it out of the global namespace:

```
0.33  public class Leaderboard     : SceneSingleton<Leaderboard>        // global namespace
0.34  public class LeaderboardMenu : SceneSingleton<LeaderboardMenu>    // namespace NuclearOption.UI
```

`Guards.MenusOpen()` (`AimRig.cs`) called `Leaderboard.IsOpen()`. That guard runs **every frame** from the
aim update and the cursor-visibility path, and neither call site is inside a `try`/`catch` — so on 0.34 the
first in-flight call threw and the aim rig went inert. The plugin still *loaded* cleanly (Harmony bound all
four patches, zero log lines), which is why the failure looked like "the mod does nothing" rather than a
crash: the faulting method is only JIT'd once you actually fly.

Fixed by calling `NuclearOption.UI.LeaderboardMenu.IsOpen()`. The game's own code carries the identical
three-condition guard (`!DynamicMap.mapMaximized && !RadialMenuMain.IsInUse() && !LeaderboardMenu.IsOpen()`),
confirming the 1:1 successor. Two benign semantic shifts ride along: the open-test is now
`gameObject.activeSelf` rather than `.enabled`, and the class now backs a two-mode menu
(`MenuMode { Join, Leaderboard }`), so the guard also trips for the join/spectator menu — strictly more
passivity, which is the safe direction for a guard.

### Verified unchanged in 0.34 (audited, no action needed)

All four Harmony targets — `PilotPlayerState.PlayerAxisControls`, `CameraCockpitState.UpdateState`,
`CameraOrbitState.UpdateState`, `CameraStateManager.SwitchState` — kept their signatures, and every
reflection/`Traverse` seam still resolves: the FBW probe fields (`gLimitPositive`, `alphaLimiter`,
`alphaLimiterStrength`), `Aircraft.relaxedStabilityController`, `RelaxedStabilityController`
(`canardRange`, `effectiveness`), `HeloControlsFilter.heloFlyByWire` and its nested fields, the
`CameraOrbitState` Traverse fields, and `CursorManager.visible`. `Aircraft.FilterInputs()` is byte-identical.

The changelog's **Rewired update was a red herring** — `PlayerAxisControls`, its single caller
`FixedUpdateState`, and the `ControlInputs` struct it fills all diff to identical; nothing new writes the
inputs downstream of the patch. The new cockpit camera-elevation control writes
`cam.transform.localPosition`, while the mod's cockpit postfix only writes `localRotation`, so they compose
without conflict.

### Known drift, deliberately not changed (needs a flight to characterize)

0.34 rewrote native `CameraOrbitState` framing from `2r behind + 0.8r up` to
`pivot.position - 2r * pivot.forward`, with elevation now coming from `tiltView` (which `EnterState` seeds
to 20°, was 0°), and `UpdateState` now clamps `tiltView` to ±89° and wraps `panView` *after* the mod's
prefix writes them. `CameraPatches.PlaceOrbitCamera` keeps its own framing — it is the mod's deliberate,
user-tunable geometry (`CameraDistanceOffset`/`HeightOffset`/`SideOffset`), not a mirror of native — so it
still produces a valid camera. Left alone rather than re-tuned against an unflown inference; the handoff
between mod and native orbit view may sit differently than before. Report it if the transition jumps.

## 0.67.0

Four v0.65 flight-assessment fixes (all one-law, keyed to live state / probed params, no new Cfg binds).

### C1 estimator latch — the priority (rec14 regression)

v0.65 C1 dropped the `_pitchEff` floor below `revThresh` so a reversed plant stops being forced — but
the estimator *held* its value on a dead command (`|cmd| < 0.05`). A hard low-q pull mushed, `pEff→0`,
C1 collapsed pitch to ~0, the stick sat at 0 → `cmd` stayed dead → the estimator could never re-measure:
the pitch **froze for ~4 s** at railed bank with no pull (rec14). The dead-command branch now floors the
estimate at `Max(_pitchEff, revThresh)` — a latched-low estimate rises toward the ~15% self-probe level
(at the slow release tau) so pitch re-establishes and re-measures; a healthy estimate is untouched, so a
brief neutral stick never drags a good jet down. A *genuine* reversal keeps `cmd > 0.05` and re-drops on
the next real command (bounded pulse, not a freeze). The pre-C1 `effFloor=0.3` gave this self-probe for
free; C1 removed it, this restores it at 0.15. Replay-verified: v64 rec04/rec05 (real reversal) stay
collapsed and identical (not re-armed); v65 rec14 pins 30%→13% of frames at ~0 (latch lifted).

### Settle-exit gain seam — high-speed roll relay (rec30/rec31)

The turn-rate presence gate was a hard step at `|azErr| = 0.5°`: below it B2's V-independent micro-bank
owns the settle, but it can't close azimuth at high q, so any drift walks azErr across 0.5° — and at that
instant the full V-scaled `bankTR` (22–44°/° at 285 m/s) slammed a sub-1° error into 16–44° of bank →
overshoot → reversal → a 0.43 Hz relay re-armed at the boundary (a step the heading-rate lead can't
anticipate). Both lockstep sites now **ramp** the demand in proportionally over `[0.5°, 2.0°]`
(`azTR = azErrPred · clamp01((|azErr|−0.5)/1.5)`) instead of stepping, so the lead/predFloor/slew get a
graded signal. Still exactly 0 below 0.5° (B2 keeps ownership — no dead zone), and `azErrPred` is still
predFloor-floored, so a genuine 1–2° error still sizes a real bank (the v0.61 reason the gate exists).

### Down-hemisphere pushover — the below-target 90° hang (rec24)

A target *below* the nose gives `phi ≈ ±180°`, so the roll-to-align setpoint `eAlignTgt` saturates to
±1.5 and the half-committed blend finds a **false ~85° bank equilibrium** in the moderate-off band; at
85° the target is abeam, pitch drops out, and yaw slowly wanders the nose down — the reported "rolls to
90° then yaws down". The up hemisphere has no such trap (`phi ≈ 0` → pitch closes it directly). The
roll-to-align is now suppressed for a below-target in the moderate band and a **bounded pushover** closes
it instead (the `pullGate`/`aoaGateDn` pair already caps the nose-down authority — maintainer-blessed
bounded negative-g). Keyed to live geometry only: belowness `clamp01(-alignFrac)`, gated to the
azErr≈0 hang by `(1-lateralHold)` (a genuine down-lateral keeps its roll-and-pull), tapering back to full
roll-and-pull for large below-reorientations (`bigTurn→1`). No per-plane constant.

### AoA-ceiling turn demand cap (rec16)

`omegaMax` (the achievability cap) folded the g-limit but not the *live* alpha-limiter, so at low q —
where the wing stalls before it reaches gLimit — the law demanded a turn the limiter then chopped: the
bank target railed, the pull was gated, AoA pinned ~20° on a 23° ceiling, and the roll hunted around a
bank the wing couldn't sustain. `omegaMax` now folds the live alpha margin (`Max(0.3, aoaGateUp)`), so
the demanded turn shrinks to what the wing can pull at this AoA and the roll stops hunting. Floored at
0.3 (a wing at the ceiling still holds a sustained turn, not level). Both lockstep achievability sites
inherit it (EL receives the capped `omegaMax`); rotorcraft untouched (`fbwOk` is `!_collective`).

## 0.66.0

### QoL — show the take identity while recording

The on-screen `● REC` indicator and the start/stop toast now show the take's `R<run>-<take>` tag (the
`R2-05` part of `mouseaim-rec-v0.66.0-R2-05-<stamp>.csv`), so the maintainer can note which take they're
on mid-flight and see the same tag in the stop feedback afterwards. Surfaced straight from
`ManeuverRecorder`'s run index + per-session take counter (new `ManeuverRecorder.Tag`) — not re-parsed
from the filename. No new UI; reuses the existing overlay/toast.

## 0.65.0

### R1 — remove the `Unified` control law (one fixed-wing law now)

The v0.60 `Unified` A/B alternative is **deleted** — the enum, the `ControlLawKey` (F9) toggle, the
Cfg bind, the on-screen law toast, and all Unified-only state (`_rollEffFilt`/`_rollEffValid` roll-
authority measurement, the `_prevRollSign` dead-astern hysteresis, `_lawOverrideLogged`, `qSchedRaw`).
The v64 A/B evidence settled it: in the fine regime `bigTurn = 0` gates Unified's geodesic `|phi|` roll
term OFF, so bank magnitude collapses to `|bankTR|`, and with yaw coordination-only and pitch `-local.y`
≈ 0 on an abeam target, **nothing closes a near-boresight lateral error** — rec12 parked 5.7 s at
`phi = +90°` with `outP ≈ 0` while EvolvedLegacy closes the same capture in 0.7 s. Fixing it means
porting EL's fine stage back into Unified, at which point Unified is "EL plus a different pitch term" —
not worth a parallel law. So `ApplyEvolvedLegacy` is now the only fixed-wing law (rotorcraft were
already forced through it). Fail-soft on a stale cfg is automatic: BepInEx never binds the removed keys,
so an orphan `ControlLawMode = Unified` / `ControlLawKey = F9` line is left untouched and unread — the
same mechanism the removed `Legacy`/`BankToTurn` values already relied on. The recorder's `controlLaw`
column now emits the literal `EvolvedLegacy` (44 columns unchanged by R1). Unified's one genuine idea —
pitch as a desired rate normalized by probed authority — is documented in `plans` for possible revival
as an EL pitch-mode flag, not a second law.

### C1 — reversal-gate the EvolvedLegacy `_pitchEff` floor

v0.64 gave EvolvedLegacy the signed `_pitchEff` scaling, but it floored the factor at `effFloor = 0.3`.
On a **reversed** plant (rec04/rec05: `_pitchEff` median 0.04, 71% of frames anti-phase, AoA relaying
+25.9↔−10.8° at ~0.8 Hz) that floor clamped demand UP to 0.3 and kept feeding 30% pitch into a plant
moving the opposite way — the forcing that sustained the 14 s relay. The floor now applies only above a
`revThresh` (0.15): signed `_pitchEff` sits near 0 **only** on a reversed/lost plant, while genuine
low-q mush reads a small positive ratio (~0.15–0.3), so below the threshold the demand is allowed to
collapse to the measured near-0 value and the law stops forcing a reversed plant. Healthy
(`≥ effFloor`) and mush (`[revThresh, effFloor)`) cases are byte-identical to v0.64. Single-site edit —
R1 removed the Unified consumer, so no lockstep helper is needed.

### B2 — sub-0.5° fine-settle micro-bank (EvolvedLegacy, azimuth only)

The `bankTR` turn-rate bank is gated at 0.5° of azimuth error (its V-scaled slope, 22–44°/° at speed,
is the rocking liability and must not extend below the gate), so at 220–470 m/s EL parks the last half-
degree with no turn command (rec13 ~0.5° at 220 m/s, rec20 1.48° at 271 m/s) — and yaw barely turns the
flight path at high q, so only a small coordinated bank can close it. B2 injects a **bounded, V-
independent, heavily-damped** bank in the sub-gate cone: `tBankE = clamp(kSettle·azErr, ±settleCap)`
with `kSettle = 8` deg/deg (≥3× below the 22°/° that already rocked at 220 m/s, so loop gain sits well
inside margin) and `settleCap = 4°` (no large-signal relay to ride), flown by EL's existing rate-damped
roll servo. It **cannot limit-cycle**: the gain cut is ≥3× and V-independent (unlike the V-scaled
`bankTR`), the output is hard-capped, the term is convergent with no integrator (`→ 0` as `azErr → 0`),
and it stands down whenever the marker is moving. The stand-down is a new `_settleOK` gate derived from
the aim direction's own angular rate (low-passed; below `aimStillRate = 3` deg/s = quasi-stationary),
so B2 never fights the user's live sub-degree aiming. A new recorder column `settleOn` (0/1) records
whether the injection fired each frame — the gate engages on a runtime signal the CSV doesn't otherwise
carry, so this is how a capture proves it engaged during a settle and stood down during a sweep. The
CSV is now **45 columns**; the analyzer/replay tools read by header name and are unaffected.

## 0.64.0

### Fix the FS-12 pitch departure limit cycle (F1-F3 from `plans/v63-pitch-authority-reversal.md`)

Root cause, from the v62 captures: past ~25-30 deg AoA the FS-12 delivers pitch rate **opposite to
and ~3x larger than** what the game's own FBW commanded (`outP = -1.000` full nose-up, `fbwTgt =
-0.523`, `fbwAch = +1.703`). Confirmed against the decompiled `ControlsFilter.FlyByWire`: both
getters read the same body frame in rad/s, unfiltered, and the FBW's own PID subtracts them
directly (`localAngularVelocity.x - targetPitchAngVel`), so a sustained sign disagreement cannot be
a convention artifact -- it is genuine authority loss with the FBW's PID pinned at its clamp. The
alpha limiter only attenuates (it cannot invert), and no thrust-vectoring or FS-12-specific pitch
component exists in the game code at all.

- **F1 - `_pitchEff` is now a SIGNED ratio.** It previously took `Mathf.Abs` of both sides, making
  it blind to the one failure it exists to catch: in every failing v62 capture it read **1.00**
  ("plant healthy") while the signed ratio was -1.00 for 52-89% of commanded frames. `Clamp01` now
  floors a reversed plant to 0 with no added logic. The noise gate stays on magnitude -- gating on
  the signed command would skip every nose-down command and silently make it one-sided.
- **F2 - `ApplyEvolvedLegacy` consumes `_pitchEff`.** It was consumed only by `ApplyUnified`, so the
  default law -- the one every v62 failure was flown on -- had no plant feedback whatsoever. Same
  `effFloor = 0.3` shape as Unified so both laws degrade identically. Fixed-wing only: the estimator
  is measured under a `!_collective` guard, so on rotorcraft it is a stale hold, not a live reading.
  **Rotorcraft behaviour is bit-for-bit unchanged.**
- **F3 - the AoA recovery bias is scaled by `_pitchEff`.** Past the ceiling on a reversed plant this
  term was actively harmful: the traces show it commanding full nose-down at 12 deg AoA (rising, so
  predicted past the ceiling) which, against the 0.31-0.75 s plant lag measured there (vs 0.12 s
  when healthy), landed in phase with the next downswing and sustained the cycle. No `effFloor`
  here on purpose -- unlike the law's P-term this *should* reach zero when authority does.

**Offline verification** (`debugtests/replay-pitcheff.py`, new, with `--selftest`): replaying the
estimator against all 17 v62 traces, the 5 reversal captures collapse (median 0.03-0.75, min 0.00,
49-98% of frames below 0.5) and **all 12 healthy captures are unchanged**. The fix bites only where
the failure is. This is not proof it flies -- there is no plant model, so it cannot predict the
closed loop -- but it rules out a fix that is wrong on its face.

## 0.63.0

**Instrumentation only — no control-law change.** The v0.62 flight test showed the wobble is still
there and that the existing artifacts could not explain it; this release closes the visibility gaps
so the next law change is made against evidence rather than a replay guess.

### Self-identifying artifacts

- **Run index that survives restarts.** New `WTMouseAimPlugin.RunIndex`, backed by a one-line counter
  file (`BepInEx/mouseaim-run.txt`), bumped once per `Awake`. Two boots of the game are now `R7` and
  `R8` instead of two unorderable wallclock ids. Fail-soft: any IO problem yields run 0 = "unknown".
- **Recording filenames carry version + run + index within run.**
  `mouseaim-rec-v0.63.0-R8-03-20260719-111208.csv`. Previously the filename was wallclock-only, so a
  folder of 17 captures could not be attributed to a build without opening each one — which is exactly
  how the v0.61-vs-v0.62 comparison got muddled. The anomaly log gets the same tag, and both headers
  now record `run=` and `rec=`.

### New recorded signals (6 columns)

- `tgtPRaw, aoaGU, aoaGD, aoaRec, qSched, pEff` — the pitch decision variables, logged as computed.
  Everything previously recorded was an input or an output; nothing said *why* the law produced the
  pitch it did, so diagnosing the FS-12 cycle meant re-deriving the AoA gate and recovery bias offline
  and hoping the reimplementation matched. `tgtPRaw -> outP` is now fully reconstructible from the CSV.

### Analyzer

- **`pitch authority` check (the discriminator).** The mod's `_pitchEff` estimator takes `abs()` of
  both sides of the FBW's commanded-vs-achieved pitch rate, so it cannot distinguish "the plant did
  what I asked" from "the plant did the OPPOSITE, at equal magnitude". Scoring the **signed** ratio
  separates the v62 set perfectly: 12 healthy captures `+0.88..+1.00` with 0% anti-phase frames,
  5 failing ones `-0.12..-1.00` with 52-89%. Reversal is now a standalone FAIL — it caught two
  captures the episode scanners scored PASS.
- **`convergence` check.** Per settling episode: time to reach 0.5 deg off-angle and the tail median.
  Encodes the standing requirement (<0.5 deg within 3 s); the v62 set plateaus at 1.4-3.7 deg.

## 0.62.0

### Fix the FS-12 pitch death-wobble (affects BOTH laws — shared post-dispatch code)

- **Damp the v0.59 AoA recovery bias.** v0.61 flight recordings (`debugtests/v61/`, FS-12 Revoker,
  alphaLimiter 27) show a 0.54 Hz rail-to-rail pitch limit cycle with AoA swinging **+43° → −47°** —
  a ~1:1 overshoot, the signature of an undamped bang-bang element. Cause: `aoaRecover` was fed the
  *gates'* one-sided predicted AoA (`aoaPredUp` clips rate to ≥0, `aoaPredDn` to ≤0). That clipping
  is deliberate hysteresis for the gates, but it made the recovery bias blind to the recovery it was
  itself producing: while AoA plunged from +43° at ~60°/s, `max(0, rate)` was 0, so the bias held
  near its +0.5 nose-down asymptote until AoA physically re-entered the envelope — by which point the
  plant carried −2.1 rad/s of pitch rate (2.3× the FBW's own `maxPitchAngVel`) straight through to
  AoA −47°, where the mirrored term fired with equal authority and restarted the cycle.
  The bias now uses a **two-sided** predicted AoA (`aoaNow + _aoaRateFilt * aoaLead`); the lead term
  *is* its damping, making it a PD that fades as recovery develops instead of holding to the crossing.
  Gates keep their one-sided predictions — that asymmetry is still correct for them.
  Replayed against the recorded trace: bias at t=393.62 drops **+0.338 → +0.075** and releases 0.15 s
  earlier. Identical on the approaching side by construction (the clip only differs while recovering).

## 0.61.0

### Track A — surgical shared-code fixes (affects BOTH laws)

- **Fix S1 counter-roll at maneuver onset.** The `_eAlignSlew` anti-relay slew was a persistent
  static, never reset, rate-limited toward `clamp(phi/90)` at a fixed 3/s. On a fresh down-lateral
  demand it ramped the previous sign *through zero* (~0.3 s of wrong-way roll), and a near-boresight
  HOLD frame — where `phi = atan2(local.x, local.y)` is numerically meaningless — seeded it with
  saturated junk. It is now gated to the dead-astern **wrap region** (`|phi| > 135°`, the only place
  `phi` is discontinuous) and zeroed where `lateral` is below the atan2 conditioning floor; elsewhere
  it passes through (no lag, no counter-roll). Reset on engage. The v0.57 dead-astern relay protection
  is retained. (rec `20260718-232130`)
- **Fix S2 (partial) — azTR presence gate keyed to raw azErr.** The turn-rate bank zeroed on
  `|azErrPred| ≤ 0.5°`, but `azErrPred` is floored at `0.30·azErr`, so a genuine 1–2° error was
  shrunk below the 0.5° gate and routed to yaw instead of a bank. The presence gate now keys off the
  **raw** `azErr` (is there a turn to make?) while the magnitude stays the lead-shaped `azErrPred`.
  Both lockstep sites updated identically. (rec `20260719-084655`)
- **Fix S3c — Trainer AoA relay.** The AoA fade width was proportional (`min(6, 0.25·lim)`),
  collapsing to 2.5° on a low limiter — narrower than the one-lead-time AoA overshoot a low-q plant
  produces, so the ceiling gate became a relay (a 0.46 Hz AoA pump). Floor the fade at **4°** (jets
  with `alphaLimiter ≥ 16°` are byte-identical — the floor only widens low-limit STOL/trainers), and
  make the recovery bias **continuous** (tanh: same initial slope and same ±0.5 asymptote as the old
  hard clamp, but rolls off smoothly instead of a fixed-step relay). (rec `20260719-083213`)

### Track B — geodesic roll/pitch restructure (Unified only; EvolvedLegacy unchanged)

- **Roll direction from body-frame bearing `phi`, magnitude from `bankTR`.** v0.60 computed the
  geometric roll solution (`phi`, the bank that puts the target in the lift plane) and threw it away,
  driving roll from the horizontal-plane `azErr` chain — which is exactly why down-lateral demands
  broke (S1 wrong-sign, S2 yaw-carry). Unified now takes roll **direction** from `phi` and keeps
  `bankTR` only as the roll **magnitude** reference; a dead-astern sign flip is handled by
  `_prevRollSign` hysteresis (no rate limiter, so no S1 lag).
- **Roll loop normalized by measured roll authority.** New `_rollEffFilt` estimator (the roll twin of
  `_pitchEff` / `_yawEffFilt`): low-passed `|rollRate|/|outR|`, spike-guarded, so a fixed
  `RollGain`/`RollDamping` no longer has to serve an order-of-magnitude plant swing (GENERALITY
  finding 5). Fail-soft to the pre-0.61 fixed-gain path until a real command has been measured.
  (rec `20260719-085036`)
- **Pitch: `coordPull` dropped** (the pull is explicit in the normalized rate term — as roll pulls the
  target into the lift plane, `local.y` grows and the pull emerges). **Yaw: coordination only** (the
  `fineGain` boost — the S2 over-yaw amplifier — and the yaw-weakness fade removed; `_iYaw` still
  closes fine-lateral residual). Subsumes the S1/S2 root cause for Unified: down-lateral targets
  bank+pull instead of yaw-slewing. EvolvedLegacy (the F9 A/B fallback) retains all of it.

## 0.60.0

- **New `Unified` control law — rate-normalized pitch + measured pitch effectiveness.** Behind the
  F9 A/B switch (now **EvolvedLegacy ↔ Unified**), `Unified` reuses EvolvedLegacy's proven roll and
  yaw verbatim and replaces **only the pitch error term** with the one structural pattern the v0.58
  helo path already uses: command a desired pitch **rate** and normalize by the probed achievable
  rate (`stick = k·err/ωmax`). This realizes GENERALITY-REVIEW findings 1 + 2 for fixed-wing pitch.
  EvolvedLegacy is **byte-unchanged** and stays the default + safe fallback (and is still forced for
  all rotorcraft); `Unified` is fixed-wing only.
- **Measured pitch-effectiveness estimator (`_pitchEff`).** The pitch twin of `_yawWeak`: a
  low-passed achieved/commanded ratio of the game FBW's own pitch-rate pair (already logged by the
  recorder), with fast-attack/slow-release hysteresis, so loadout/mass/density/damage/mush all show
  up generically as achieved &lt; commanded and back the demand off — no schedule guessing. It scales
  `Unified`'s normalized pitch command; the two demand *schedules* (`qSched`, the AoA-utilization
  fold-in) are demoted to safety nets on this law (`qSchedRaw`, the q-only value, is retained as an
  instantaneous low-q floor). No FineGainBoost on the normalized branch — fine capture is closed by
  the shared `_iPitch` integrator, which structurally removes the "boost rails the stick near the
  alpha ceiling" mode. Fail-soft throughout: any FBW probe miss degrades pitch to EvolvedLegacy's
  exact pre-normalization raw term.
- **`Legacy` and `BankToTurn` laws removed.** Both were superseded — Legacy by EvolvedLegacy (v0.42),
  BankToTurn abandoned since v0.41. Their methods, enum members, and BankToTurn-only config binds
  (`BankToTurnOmegaMax`, `BankToTurnDeadband`) are deleted. Config serializes enums by name, so a
  stale cfg holding either falls back to the bound default `EvolvedLegacy` (F9 then rescues to
  `Unified`).
- **`BankToTurnVmin` renamed `BankSpeedFloor`** and now used at all three lockstep bank-target sites
  (Apply's shared `bankTR`, EvolvedLegacy, Unified) — fixing GENERALITY-REVIEW finding 8, where the
  shared site used a hardcoded `50` while the law used the bind. Default stays **50**, so behaviour is
  identical at defaults; an old cfg line named `BankToTurnVmin` is orphaned and `BankSpeedFloor` binds
  at 50 (numerically the same). No migration needed.
- **Findings status:** 1, 2, 8 fixed; 9 resolved by construction (Legacy deleted — the only fixed-wing
  alternative is `Unified`, which carries its own low-q/loaded protection). Roll normalization
  (finding 5) deferred: the FBW roll authority isn't read anywhere yet and needs a decompiled-source
  check of `GetFlyByWireParameters` before a principled fix.

## 0.59.0

- **AoA-utilization demand schedule — the loaded-jet pitch-oscillation fix.** The v58 Discord
  FS-12 (Revoker) recording (loaded, 229–330 kt) showed a rail-to-rail ~0.55 Hz pitch relay:
  AoA swung +43°…−18° on a ~23° ceiling while `iPitch` sat at 0.003 (integrators innocent).
  Root cause: the v0.56 low-q gain schedule keys off **dynamic pressure only**, so a *loaded*
  airframe that needs high AoA to make its commanded G *above* corner speed reads as high-q
  while the plant is actually mushing — outer-loop gain stays hot and the nose departs. The
  schedule now also folds in live **AoA utilization against the airframe's own probed alpha
  ceiling** (predicted AoA, same lead as the gates; fast-attack/slow-release hysteresis so
  demand can't snap hot mid-cycle), easing to the same 0.3 floor as the game's q clamp.
  Airframe-agnostic: probed ceiling + live state, no per-plane constants.
- **`FineGainBoost` gated by the same schedule.** The up-to-3.5× boresight boost railed the
  stick on ~5° of error exactly where the loaded plant was mushing — the kick that starts the
  relay. Gated by the AoA schedule (not the speed one), so light jets at any speed keep the
  current capture feel; only genuine near-ceiling AoA softens it.
- **AoA recovery bias.** The v0.55 gates only *cut* the command driving AoA outward — past
  the ceiling the command is zero and recovery was left to raw aero + reactive damping (the
  asymmetric relay the FS-12 cycle rode: +43° overshoot, then an unopposed −18° bunt). A
  restoring pitch proportional to the predicted excess past either ceiling (normalized by the
  airframe-proportional fade width, capped at 0.5 stick) now actively flies the nose back
  inside the envelope. Continuous and symmetric — no discontinuity left to relay on.
- **Dev-guide requirement (CLAUDE.md).** Codified the design rule the above follows: one
  control law for all airframes at all loads and speeds — every gain/schedule/gate keys off
  probed per-airframe parameters and live physical state, never per-plane tuning constants.
- **Build system self-configures — no machine-specific paths committed.** The csproj no longer
  hardcodes a game path: a new `build/locate-game.ps1` discovers the Nuclear Option install by
  scanning Steam metadata (registry `SteamPath`/`InstallPath` + every `libraryfolders.vdf` library),
  overridable by `NUCLEAR_OPTION_PATH` or `/p:GamePath=`, and self-caches the BepInEx 5 reference
  DLLs under `.deps/` (downloaded once if absent — never installed into the game). Any checkout
  builds with zero edits. New `PENDING-TESTS.md` tracks shipped-but-unflown changes.
- Analysis artifacts: `GENERALITY-REVIEW.md` — a full review of the control law against that
  rule, with ranked findings for future work (Ifrit hover-yaw hypothesis included; no Ifrit
  recording existed in the v58 batch, so that fix waits on data).

## 0.58.0

- **Rotorcraft stabilization — the heli wobble fix.** The UH-90's ~1 Hz forward-flight pitch
  buzz and the RAH-72's ~1.2 Hz hover "sideways wobble" (Discord reports) are one disease: the
  mod's fixed-wing error→stick gains ran an outer loop at 10–15 s⁻¹ around the game's own
  `HeloFlyByWire` — a competent 3-axis **rate-command** PID with ~0.3 s lag (fitted from the
  recordings at corr 0.88–0.97) — guaranteeing a ~1 Hz limit cycle on any heli, in *both* laws
  (a user A/B'd to Legacy and the buzz was identical). Fix is the v0.55 fixed-wing pattern,
  helo edition: a new fail-soft probe reads the private `heloFlyByWire` params (Enabled,
  gLimit, maxAngularVel) and pitch/yaw become normalized rate commands
  (`stick = 2.0·err/ωmax`, ~55° phase margin), auto-adapting to modded helis; `FineGainBoost`
  no longer applies to collective airframes (it peaked the gain exactly at boresight).
- **VTOL/heli regime from what the aircraft is, not a speed guess.** `heliBlend` is now driven
  by the live tilt/nozzle angle where the airframe exposes one (`TiltWingController` wing
  angle, `SwivelDuctSystem` nozzle angle — the higher of tilt-fraction and speed-blend wins);
  `HeliForwardSpeed` default 150→**60** m/s and `HeliHoverSpeed` 40→**20** (the game's own helo
  yaw weathervane fades in at 40–60 m/s — above that, yaw commands sideslip the game actively
  fights; 150 kept a *cruising* UH-90 40% in hover regime, the mushy/skiddy-turn complaint).
  `CompoundHeloController` (thruster heli) presence is detected and logged.
- **Rotorcraft always fly EvolvedLegacy.** Every bit of heli handling lives in that law;
  switching to Legacy silently dropped all of it (that user's A/B). `ControlLawMode` is now
  ignored for collective airframes (logged once); fixed-wing selection untouched.
- **Heading deprojection — the vertical-zoom roll oscillation fix.** The one FAIL of the v57
  round (growing 0.46 Hz roll/azimuth cycle at 250–280 m/s) was not a gain problem: the nose
  was ~82° up in a zoom climb, where horizontal-plane `azErr` inflates by 1/cos(pitch) —
  a real sub-degree offset read as ±9.5° (a sibling capture at ~89° read ±170°!) and the
  V-scaled bank map chased the phantom rail-to-rail. The bank-path errors (`azBank`,
  `azErrPred`) are now multiplied by cos(pitch): exact at level flight (clean files 0.99–1.00,
  replay ×1.00), zeroing bank authority only where heading is genuinely meaningless — pitch/yaw
  fly body-frame errors and still close the capture. Replay: phantom bank-target swing cut
  ×7–12 on the two pathological files, ≤0.5° change on every clean/big-turn file.
- **Detector fixes.** `overstress` now requires real load (g > 2.5) and valid geometry
  (|AoA| ≤ 90°, speed > 100 m/s) for the alpha branch — the v57 session's 41 lines (all from
  one unloaded 1 g departure, "AoA −176°" at 21 m/s) are all suppressed, the genuine v56
  9.7 g events all still fire. New `az-limit-cycle` anomaly: azErr sign-flipping with a rising
  envelope + active roll stick in the fine cone — the signature that logged zero lines in the
  v57 FAIL file (fires at t=380 on that recording, 4 s before the stick railed; silent on all
  54 other sample files). `[canard]` now logs unconditionally with a log-only child-scan
  (`field=`/`childScan=`), to settle why one session's KR-67 carries the canard controller and
  another's doesn't — binding stays field-only on purpose: the game guards its remap on the
  same field, so a null field means no remap exists to invert.
- Knob delta: **0 new Cfg entries**; two default changes (`HeliForwardSpeed` 150→60,
  `HeliHoverSpeed` 40→20 — F1-reset or delete the cfg line to pick them up on an existing
  install).

## 0.57.0

- **KR-67 Ifrit canard linearization — the straight-line buzz fix.** The 47-recording v0.56 fleet
  round (8 airframes) confirmed the user-reported tiny straight-line oscillation on the Ifrit: a
  ~5.3 Hz pitch buzz (stick sign-flipping on 60–70% of samples, g wiggling ±0.4) active on up to
  82% of a recording, both assist states, Ifrit-exclusive, present since at least v0.53. Root
  cause (decompiled `Aircraft.FilterInputs`): the Ifrit is the one airframe with a
  `RelaxedStabilityController`, which **replaces** the pitch stick with
  `Lerp(AoA/canardRange, stick, |stick|)` before the FBW sees it — small inputs act
  quadratically (0.05 stick delivers 0.0025) and the response is locally **reversed** for
  `0 < stick < a/2`, a textbook deadzone/relay limit cycle around boresight. The mod now probes
  the component (fail-soft, like the FBW probe) and inverts the remap closed-form, so the FBW
  receives exactly the pitch the control law intended. Identity on every other airframe; on the
  Ifrit this also un-warps the whole mid-stick response (half stick used to deliver a quarter).
- **Predictive AoA gate — the assist-off anti-pump.** The reactive v0.55/0.56 gate is a relay on
  a hard pull: AoA blows 1.3–2.5× past the ceiling before the fade bites (Trainer 20.4° on an
  8.5° ceiling, growing; CAS1 dragged to 9.7 g on a 6 g airframe for 16 s), the gate slams shut,
  AoA falls, full pull re-engages — a ~0.7 Hz buck cycle. The gate now closes on **predicted**
  AoA (`AoA + max(0, rate)·0.30 s` toward each ceiling) but reopens on the real AoA — the
  asymmetric lead is hysteresis, which is what actually breaks a relay.
- **Slewed big-turn roll alignment.** Third member of the v0.53 raw-error→roll relay family: when
  the target crosses dead-astern, `phi` flips sign in one tick and `eAlign` followed it
  rail-to-rail (0.86–0.98 Hz roll-stick chatter in the FS-12/CI-22/KR-67 scissors captures),
  bypassing both the v0.53 fine-cone deadzone and the v0.54 bank-target slew. `eAlign` is now
  slew-limited at 3/s (full reversal ~1 s) — the chatter needs ~5.5/s to sustain, a genuine
  over-the-top sweep barely notices.
- **Overstress anomaly line.** The 9.7 g / 22° AoA assist-off episodes produced **zero**
  `[anomaly]` lines — every detector watched stick patterns, none watched the airframe. New
  `overstress` anomaly fires when g/AoA stay past the airframe's own FBW limits for 0.5 s.
- **Analyzer:** high-frequency pitch-buzz detector (the buzz was invisible to the episode
  detector — the v56 Ifrit files PASSed while buzzing 82% of the time); AoA-pump FAIL verdicts
  with limiter-relative thresholds (pp 20 on a 27° limiter is honest maneuvering, on a 10°
  Trainer it's a blow-through cycle); AoA/g overstress WARN lines against the `# fbw` header
  (which now also records `canardRange`); WARN verdict text generalized.
- Knob delta: **0 new Cfg entries** (canard inversion is probe-driven; gate lead / eAlign slew
  are `ponytail:` constants).

## 0.56.0

- **Q-scheduled pitch gain — the takeoff-oscillation fix.** The 31-recording v0.55 test round
  (FS-12 + Trainer) caught a 100% mod-driven 0.55 Hz pitch limit cycle below corner speed that
  needs ~30–55 s of uninterrupted tracking to ratchet up (bank 18°→85°, g 0.7→7.5, AoA −29..+37°)
  and then departs — which is why short low-speed tests never reproduced it; takeoff climb-out
  supplied the window. Phase forensics (WOBBLE-FINDINGS.md UPDATE 6): the mod's P-response to the
  aim error is instant (+0.13 s), but the *achieved* pitch rate lags the command by >1 s at
  113 m/s — the low-q plant supplies essentially all the loop phase, and the outer P gain (tuned
  against the fast high-q plant) is too hot there. The pitch demand terms (error P + coordPull)
  are now scaled by the game's own q clamp (`clamp(q_ratio, 0.3, 1)`, ≡ 1 at/above corner speed —
  high-speed feel untouched), leaving the rate-damping term unscaled so damping is relatively ~3×
  stronger exactly where the plant is slow. One mechanism, both assist regimes; big errors still
  rail the stick, so max-performance pulls keep full authority.
- **Relative AoA ceiling margins.** The fixed −4°/6° margin+fade collapsed on low-limit airframes:
  the Trainer's 10° `alphaLimiter` gave a 6° ceiling with the fade starting at **0° AoA** —
  measured 60–90% of pitch authority cut at completely ordinary 3–5° turning AoA. Margins are now
  proportional (`min(4°, 0.15·lim)` margin, `min(6°, 0.25·lim)` fade): Trainer gets full authority
  below 6° AoA (ceiling 8.5°); the FS-12 (27°) keeps exactly the old 4°/6°.
- **Assist-OFF is now a performance mode** (v0.55's assist-off pitch normalization DELETED). The
  v55 sweep showed the normalization (×0.32–0.5 below corner speed) was the single biggest
  mod-side restriction — ~15% of demanding assist-off samples held back at safe AoA — and the
  airframe itself tracks its commanded rate at r 0.85–0.99 (the plane was never the bottleneck).
  With assist OFF the game's raw law now passes through at full command, guarded by the AoA
  ceiling + the q schedule (both assist-independent). High-speed assist parity needs no mod-side
  scaling — the game runs the identical protected law above ~1.2× corner-q itself.
- **Analyzer: FAIL/WARN verdict split + guard attribution.** Rail-only evidence (roll stick
  railed, any speed) is now a WARN — the v55 captures showed plain railing is usually a benign
  max-performance reversal; FAIL requires dynamic evidence (oscillation episode / growing azErr /
  AoA blow-through). The digest derives per-segment `sched min` / `pitch gated %` from the
  existing columns + `# fbw` header (no new CSV columns needed — the guards are pure functions of
  what's already recorded). Selftest coverage for both. Config knob delta: **0**.

## 0.55.0

- **Low-speed pitch oscillation fixed + stability-assist parity — the Draken round-2 report**
  (compounding oscillation below ~450 km/h on Ifrit/Compass/Revoker that could crash the plane,
  and stability-assist OFF turning ~3× slower). A fresh decompile of the game's
  `ControlsFilter.FlyByWire.Filter` plus an offline fit on the 11 tester recordings nailed both
  root causes (see WOBBLE-FINDINGS.md UPDATE 5):
  - **FBW probe (new)**: the mod now reads each airframe's fly-by-wire parameters from the game
    at runtime (cornerSpeed/maxPitchAngularVel via the public API; gLimitPositive/alphaLimiter
    via reflection, everything fail-soft — helicopters and FBW-less airframes keep the old
    behaviour) and reconstructs the game's own stick→pitch-rate gain per tick.
  - **`AssistOffPitchScale` REMOVED** (knob deleted): the decompiled law shows assist-off changes
    *nothing* above ~1.2× corner-speed dynamic pressure, so v0.51's flat 0.5 cut was simply
    halving high-speed assist-off turns (the "3× slower"). Replaced by an exact per-airframe,
    per-speed normalization (protected/achievable rate ratio) that is ≡ 1 with assist on or at
    speed — the verified high-speed feel is byte-identical — and the physically-correct cut with
    assist off at low q.
  - **Achievability cap**: the turn-rate demand ωdes is capped at the achievable pitch rate (in
    both bank-target sites, shared pre-compute + EvolvedLegacy), so at low speed the bank target
    shrinks physically instead of slamming ±72° into a turn the collapsed pitch rate can't fly;
    the fine integrators freeze while capped (anti-windup — the offline fit showed
    corr(command, response) going *negative* below corner speed: the plane had stopped following
    while the loop kept winding).
  - **Mod-side AoA ceiling**: the pitch command driving AoA past the game's per-airframe
    `alphaLimiter` − 4° is scaled out (sign-aware, so recovery is never blocked), active
    regardless of the assist state — assist-OFF now gets the AoA protection the game withholds.
- **G-LOC fade-to-black warning**: a gradual full-screen grey-out driven by the same
  `pilotStrength` signal as the amber OVER-G text, so third-person pilots (who get none of the
  game's cockpit-only black-out) see G-LOC coming instead of an instant control cut. New knobs
  `GLocFadeEnabled` / `GLocFadeOnset` (0.4) / `GLocFadeMaxAlpha` (0.7).
- **Instrumentation**: recordings gain `assist`/`fbwTgtPR`/`fbwPR` columns and a `# fbw`
  per-airframe params header line; `analyze-wobble.py` gains a per-file stick→rate model fit
  (high-q/low-q split at the airframe's corner speed) and a named `low-speed stall oscillation`
  FAIL verdict (stall blow-through / growing azErr / low-speed roll-rail), with `--selftest`
  coverage. Config knob delta: −1.

## 0.54.0

- **De-rectified turn lead + slew-limited bank target — the ~1.5 Hz wing rock and the
  "self-leveling fights the turn" drift.** Nineteen v0.53 recordings (KR67 EFRET 450–536 m/s, AB4
  Alcyon 226–518 m/s) verified the v0.53 deadzone (the eAlign relay is dead — `outR` no longer
  tracks `sign(azErr)` anywhere) but exposed the next loop underneath: the v0.52 brake-clamp is a
  **rectifier**. Bank oscillation ripples the filtered heading rate ±3°/s; `azErrPred =
  clamp(azErr − hRF·leadT, [0, azErr])` therefore slams between exactly 0 and full `azErr` every
  half-cycle, and the ~44°-bank-per-degree atan slope at 500 m/s amplifies that sawtooth into a
  bank target banging 0↔48–65° at ~1.5 Hz from a 1–3° error that never changes sign. The roll
  servo chased it faithfully (corr `outR` vs `bankTR−bank` = 0.79–0.96) — wings rocking ±14–30°.
  The slow 0.5 Hz big-turn cycle is the same rectification at scale: the prediction pinned to 0
  while 1.5–5.7° of real error remained, commanding full wings-level mid-correction (the
  user-reported self-leveling drift), sustained by the bank overshooting the collapsing target by
  15–20°. Three fixes in the same pipeline:
  - **Proportional floor on the brake-clamp**: `azErrPred` now floors at `0.30·azErr` instead of
    0 — early rollout (the lead's job) still happens, but level flight is never commanded while
    real error remains; the floor self-releases as `azErr → 0`.
  - **`hrTau` 0.18 → 0.35** (hardcoded): ~2× more attenuation of the 1.3–1.5 Hz ripple feeding the
    rectifier, at a cost of ~0.2 s of rollout timing.
  - **New knob `BankSlewRate` (default 60°/s, 0 = off)**: rate-limits EvolvedLegacy's bank target
    so it physically can't flap above the airframe's own roll response; also shrinks the servo
    overshoot that sustained the slow cycle. Applied before `coordPull` so the pull sizes off the
    bank actually commanded.
- **New CSV column `tBankE`** — the bank target EvolvedLegacy's roll servo *actually* flies
  (slew-limited). The existing `targetBank` column is the shared yawWeak-gated blend, which this
  law does not fly; reading it produced two red herrings in the v0.53 analysis.
- Analysis: `WOBBLE-FINDINGS.md` UPDATE 4. The "yaws instead of banking" report was refuted for
  large diagonal snaps (roll rails within ~120 ms, yaw never exceeds 0.46 across all 19 files) —
  the real small-error mechanism was the flapping bank target never *holding* a bank.

## 0.53.0

- **Fine-cone deadzone on the align-hold roll weight — kills the KR67-class 570 m/s wing-rock.**
  Twelve v0.52 recordings (KR67 EFRET at 480–588 m/s + Trainer) confirmed the v0.52 clamp works —
  `targetBank` stays inside ±3° while station-keeping — yet the wings still rocked ±20–33° with the
  roll stick flipping at ~1.2 Hz (worst file: 14 s of sustained chatter). The driver was a **second,
  unguarded azimuth→roll path**: near boresight `phi` snaps between ±90° with the *sign* of a
  sub-degree error, making `eAlign = phi/90` a full-scale directional relay, and the v0.42
  align-hold blend weight (`|azErr| / EvolvedAlignHoldDeg`) fed it with **raw** error — ±0.2 of
  roll stick per degree, no lead, no deadzone, bypassing the entire atan/lead/clamp bank pipeline.
  At 570+ m/s roll authority that loop self-sustains. Fix: the blend weight now subtracts
  `FineBankDeadzone` (2.5°) from `|azErr|` first — the exact guard the linear bank servo has had
  since v0.29 (`azBank`). Inside the fine cone the roll servo is purely the wings-leveler + the
  braked/clamped `tBankE`; big turns unchanged (`bigTurn` still dominates the blend), medium
  errors reach full align weight at ~7.5° instead of 5°.

## 0.52.0

- **Brake-only lead — fixes the fast chatter the v0.51 lead introduced.** Sixteen v0.51 recordings
  (Ifrit + Compass, 108–508 m/s) showed the old slow 0.3–0.85 Hz wobble genuinely gone, but a NEW
  ~1.1–1.35 Hz bank/roll-stick chatter appeared while station-keeping (HOLD phase, aim error under
  ~2°), from ~280 m/s up. Cause (confirmed by phase analysis): near boresight the v0.51 prediction
  `azErr − headingRate·TurnLeadTime` was **dominated by the heading-rate term** (2.1–2.7× the real
  error), and the speed-scaled bank slope (~44° of bank per degree of error at 470 m/s) turned
  ±2°/s of nose-rate ripple into a ±65° bank relay — the lead had closed its own faster loop
  through the roll actuator (heading rate measurably *led* bank, the causal smoking gun). Fix:
  `azErrPred` is now **clamped to [0, azErr]** — the lead may shrink the error toward zero (the
  early rollout that killed the slow wobble) but can never flip its sign or exceed it, so the
  commanded bank is always bounded by the *real* aim error and the chatter loop can't self-sustain.
  Genuine big turns are unchanged (the clamp only engages when the rate term outruns the error);
  offline replay of the 16 recordings shows the chatter files' mean bank command dropping ~60–90%
  with the deliberate-turn files byte-identical. Side benefit: `TurnLeadTime` is now safe to raise
  (more anticipation can only advance the rollout, not feed the relay).
- `debugtests/analyze-wobble.py`: FAIL band widened 0.3–0.9 → 0.25–2.0 Hz (it was passing the new
  fast mode) + a roll-stick chatter criterion (≥0.8 Hz rail-to-rail). Note: recordings from this
  session were 5 Hz — check `Recorder/RecordRateHz` (should be 20) for better diagnosis data.

## 0.51.0

- **THE death-wobble fix: anticipatory lead on the turn-rate bank command.** Ten user recordings
  (Kryrins, Draken — thanks!) pinned the reported fixed-wing "death wobble" to a single mechanism:
  the `atan(ω·V/g)` azimuth→bank command was **pure proportional** in the heading error while the
  achieved bank **lags that command by a constant ~0.7 s** (measured by cross-correlation,
  identical across airframes) — at the observed 0.3–0.85 Hz oscillation that lag is ~90–180° of
  phase, so the loop self-sustained a limit cycle: ±88° of bank from a ±6° aim error, roll stick
  railed for 47 s straight, **at every speed tested (70–390 m/s)** — speed only set the violence.
  The bank target is now computed from the *predicted* heading error
  `azErrPred = azErr − noseHeadingRate·TurnLeadTime` (new knob, default **0.65 s**, just under the
  measured lag; `0` = old behaviour), so the bank rolls out *early* — including a brief
  anticipatory counter-bank that brakes the turn — instead of after the overshoot. Nose-only rate
  (marker-independent), so the lead can never fight a mouse flick. Applied to both copies of the
  turn-rate bank math (shared pre-compute + EvolvedLegacy); the linear low-speed servo and the
  coordinating-pull release taper deliberately keep the *raw* error (release timing must track
  real arrival). This is the v0.38 fix `WOBBLE-FINDINGS.md` planned and never shipped.
- **Assist-off pitch guard.** With the game's own flight-assist (AoA limiter) OFF, the game FBW's
  stick→pitch-rate gain roughly doubles-triples (decompiled `ControlsFilter.FlyByWire`), and the
  mod's fixed pitch gains then diverged (recorded FS-12: elevator railed, AoA −29°→+52°). New knob
  `AssistOffPitchScale` (default **0.5**, `1` = old behaviour) flatly scales the instructor's pitch
  command while flight-assist is off. A rough compensating cut, not a per-airframe FBW inversion
  (that's deferred — see WOBBLE-FINDINGS).
- **Instrumentation:** recorder CSVs gain trailing `headingRateFilt,azErrPred` columns; the config
  snapshot gains `leadT=`/`aOffP=`; the `[chase]` trace logs `azPred=`. New
  `debugtests/analyze-wobble.py` (stdlib-only) scores any recording for the wobble signature
  (episodes, frequencies, rail %, the 0.7 s lag) — it flags the two violent baseline recordings
  FAIL and the mild one PASS.
- Known remaining (parked, in `WOBBLE-FINDINGS.md`): helicopter sideways wobble (distinct ~1.15 Hz
  yaw-loop cycle at hover), full per-airframe FBW pitch-gain inversion, motion-profile shaping.

## 0.44.0

- **Recordings are now self-describing.** Each maneuver-recorder CSV (`F8`) starts with a `#` comment
  header block carrying the plugin version, the session id, the wallclock + `Time.time` start, the
  aircraft, and the **full control-law gain set** (the same dump the startup `[config]` line emits, via
  a shared `Cfg.SnapshotString()`). Any setting changed live (F1) *during* a recording is appended as a
  `# cfg t=… Section/Key = value` row, so a feel change is inline with the data — you can debug a run
  from the CSV alone without cross-referencing the log.
- **New diagnostic CSV columns:** `rollRateF` (the filtered roll rate that feeds the damping term — the
  key signal for high-speed roll-PIO/wobble, previously only in the anomaly trail), `iPitch`/`iYaw`
  (fine-integrator state), and `bankTR`/`bankBlend` (the EvolvedLegacy `atan(ωV/g)` commanded bank and
  its blend weight).
- **Anomalies get their own file.** A dedicated, session-scoped `mouseaim-anomalies-<session>.log` (next
  to `LogOutput.log`) collects only the `[anomaly]`/`[anomaly:trail]` lines, separated from the noisy
  shared BepInEx log. Each anomaly is tagged with the active **control law** and, when a recording is
  running, the **CSV it belongs to** (`rec=…`); a session id ties the anomaly file, every recording, and
  the BepInEx config log together. The on-screen flash and the BepInEx warning still fire as before.
- **Less log spam / lower context.** Dropped the full gain snapshot that every `[anomaly]` line repeated
  (gains are already logged once at startup + on each change, and embedded in each recording header).
  Halved the verbose `DebugLogging` trace cadences (`[chase]` ~5→2.5/sec, fine-capture ~10→5/sec;
  `[aim]` and `[orbitcam]` likewise) so a debug run stays readable without losing shape. The recorder
  rate stays 20 Hz (`Recorder/RecordRateHz` is the knob if files get large).

## 0.43.0

- **Regime-aware hover handling for EvolvedLegacy.** On collective aircraft (helicopters / hover-VTOLs,
  `takeoffDistance == 0`) the `atan(ωV/g)` bank-to-turn law degenerates at low forward speed — it lays
  the aircraft over without slewing the nose. EvolvedLegacy now ramps from bank-to-turn to *yaw-to-point*
  as forward speed (`vFwd`, the nose-direction velocity component) drops between new knobs
  `Control/HeliForwardSpeed` (60 m/s, full fixed-wing) and `Control/HeliHoverSpeed` (20 m/s, full hover),
  via a per-frame blend `heliBlend`. In hover the commanded bank is suppressed (the roll axis becomes a
  wings-leveler) and yaw authority is raised by `Control/HeliYawScale` (2.0) so the tail rotor points the
  nose. Forced fully on whenever the game's AutoHover is engaged. Fixed-wing airframes are unaffected
  (`heliBlend == 0`, byte-identical to 0.42). New recorder columns `heliBlend`/`vFwd`; `[seam]` logs the
  collective/AutoHover flags.

## 0.33.0

- **Fixed the high-speed roll buzz for real — by cutting the damping, not adding more.** Restoring
  full roll authority in 0.32 brought back a violent roll PIO at high dynamic pressure (logs: roll
  stick dithering ±0.45 at ~3 Hz on-heading, bank overshooting its target to ±40° rolling out). The
  driver is the roll-*damping* term itself: with the wings level the roll command is essentially
  `−rollRate · RollDamping · RollGain`, and that delayed rate feedback flips from damping to driving
  the cycle — so raising `RollDamping` (the old "fix overshoot" advice) made it *worse*. New defaults
  **`RollDamping` 0.6 → 0.1** and **`RollGain` 1.3 → 1.0** drop the loop gain below the limit-cycle
  threshold: the buzz is gone and the wings hold steady at speed, while fast rolls stay crisp. (Both
  remain live-tunable; 0 damping is a touch jittery on-heading, hence 0.1.)
- **Controls every airframe now, not just fixed-wing.** Helicopters and hover-VTOLs (collective
  aircraft, flagged by `takeoffDistance == 0`) fly off the same chase law — they drive the same
  pitch/roll/yaw (cyclic + tail rotor); collective stays on your throttle, untouched. New
  `Control/ControlRotorcraft` (default on) opts them out if you'd rather keep rotorcraft on stock
  controls.
- **Master ON/OFF hotkey (`General/ToggleKey`, default F10).** Flip the whole mod on/off in flight
  without opening the menu; a brief on-screen toast confirms the change.
- **Clean reticle-only HUD by default.** The diagnostic text readouts (status line, live
  pitch/yaw/roll, anomaly flash, phase) are now hidden behind `HUD/ShowDebugHud` (default off). Out
  of the box you see just the reticle, the airframe marker, the FLY LEVEL banner, and the G-LOC
  warning — turn `ShowDebugHud` on for tuning.

## 0.32.0

- **Restored high-speed roll authority.** Removed the v0.30 dynamic-pressure roll gain schedule
  (`RollGainRefSpeed` / `RollGainSpeedExp` / `RollGainMinScale` and the `qScale` term) entirely. It
  never measurably reduced the high-speed roll wobble — that was a rate-feedback limit cycle, fixed
  in 0.31 — and it was silently cutting roll authority by up to ~65% at speed. The plane now keeps
  full roll authority across the envelope.
- **Anomaly logging is suspended while you're on the stick.** When any axis is manually engaged
  (and through the ease-back window after release), the `[anomaly]` detectors no longer fire on the
  attitude/rates *you* are driving. The trail ring buffer keeps filling, so a genuine anomaly right
  after hand-back still has its pre-frames.

## 0.31.0

- **Fixed the high-speed roll wobble** (the felt one). It's a derivative-feedback limit cycle: level
  on-heading the roll command is essentially `-rollRate · RollDamping`, and `rollRate` is a one-frame
  finite difference; at high dynamic pressure that delayed feedback flips from damping to driving at
  ~6–7 Hz. Added a first-order low-pass on the roll rate feeding the damping term
  (`Control/RollRateSmoothing`, default 0.06 s) — rolls off the high-frequency content so the damping
  only opposes real, low-frequency roll motion. Kills the wobble without touching steering authority.

## 0.30.0 *(superseded by 0.31/0.32 — never the active fix)*

- Experimental dynamic-pressure roll gain schedule, on the theory the wobble was a proportional-gain
  instability. Flight logs showed cutting the gain to 0.35× left the wobble amplitude unchanged,
  disproving that theory. Mechanism removed in 0.32.0.

## 0.29.0

- **Fixed the mid-speed (fine-cone) roll wobble**: a soft azimuth deadband on the bank servo
  (`Control/FineBankDeadzone`). Inside a few degrees of heading error the wings just level and yaw
  alone does the final capture, instead of the bank servo amplifying a sub-degree heading hunt into a
  continuous roll-stick dither.
