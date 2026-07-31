# Dev notes — WT Mouse Aim

User-facing docs are in [README.md](README.md). **This file is the context for Claude Code / any
coding agent working in this repo**: how the code is laid out, how to build/deploy/test, how to
debug in-game, and how to read the decompiled game source. Committed on purpose — a fresh checkout
should be enough to get productive.

**Read [ARCHITECTURE.md](ARCHITECTURE.md) before your first edit.** It is the system diagram: an
at-a-glance map of every subsystem, then per-subsystem zoom-ins (frame timeline, the aim rig, the
`Apply` pipeline, camera patches, telemetry, config), with the mod / game / platform boundary drawn
explicitly. This file tells you where code *lives*; that one tells you how it *works* and why.
**You are required to keep it current — see [Keeping the diagram current](#keeping-the-diagram-current).**

**What to fly, and why that:** [`LAW-CHARACTERIZATION.md`](LAW-CHARACTERIZATION.md) is the standing
test plan — the state of what has actually been measured (one card, one airframe, saturated), the
batches to run in order, and the airframe roster with the entry conditions each one can survive. It
supersedes the Experiments section of [`FLIGHT-PROTOCOL.md`](FLIGHT-PROTOCOL.md), which remains the
record of how the *instrument* was validated (gates A–D, all passed). Read the characterization plan
before proposing an experiment; most of the obvious ones are already scheduled, and several would
currently measure a railed actuator rather than the control law.

Machine-specific paths are written as placeholders:
- `<game>` = your Nuclear Option install folder (the one containing `NuclearOption.exe`). The build
  **auto-discovers** it (Steam scan) — no path is committed anywhere. To **run** the mod you also
  need **BepInEx 5 (Mono x64)** installed into `<game>`; the build itself doesn't (it self-caches
  the reference DLLs — see setup below).

## First-time setup (no edits needed)
1. `dotnet build -c Release` — that's it. Confirm 0 errors (the `MSB3277` warning is harmless). The
   build runs [`build/locate-game.ps1`](build/locate-game.ps1), which finds `<game>` via Steam
   metadata (registry `SteamPath`/`InstallPath` + every library in `steamapps\libraryfolders.vdf`)
   and self-caches the BepInEx 5 reference DLLs under `.deps\` (downloaded once if absent). No
   `<GamePath>` to edit — nothing machine-specific is committed.
   - **Override** (only if auto-discovery can't find the game): set env var
     `NUCLEAR_OPTION_PATH=<game>`, or build with `/p:GamePath="<game>"`.
   - **No .NET SDK?** The official user-local installer needs no admin:
     `iwr https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; ./dotnet-install.ps1 -Channel 8.0`
     then build with that `dotnet` (e.g. `~/.dotnet/dotnet.exe`).
2. To actually **run** the mod in-game: install [BepInEx 5 Mono x64](https://github.com/BepInEx/BepInEx/releases)
   into `<game>`, run the game once so it generates `BepInEx/`, then deploy the DLL (see build/test
   loop below). Installing BepInEx is a *run* requirement only — the build never touches `<game>`.

## Layout
- **Mod code is split one file per concern** (all at repo root, single namespace
  `NuclearOptionMouseAim`). The `.csproj` (`Microsoft.NET.Sdk`) globs every `*.cs` automatically —
  no project edits when adding/splitting a file. Files:
  - `WTMouseAimPlugin.cs` — `WTMouseAimPlugin` (Awake/OnGUI overlay + the `FixedUpdate` that drives
    `TestDrone.FixedTick`). Holds `PluginVersion`, the version SoT. **v0.90 added `DrawRunBoard`**, the
    harness run board (top-left): drawn in `OnGUI`'s **pre-gate** band — before `ShowOverlay`/`Enabled`
    and before the local-aircraft resolve — because the operator watching a drone batch is usually in
    no aircraft at all, which is exactly when every gate below has already returned. Gated on
    `Cfg.DroneEnabled` alone (one bool read when the harness is idle). Two states, FLYING and
    PREFLIGHT; every preflight number comes from `ScenarioPlayer.Preview()` and
    `TestDrone.AirframeOf/AltOf/SpeedOf` — the same pair the launch itself uses, so the board cannot
    promise something the spawn will not do — and is polled at 2 Hz, since `OnGUI` runs at least twice
    a frame. The board is read-only by construction: an instrument that can change what it measures
    is not one.
  - `Cfg.cs` — `Cfg` (all config binds) + `ConfigurationManagerAttributes`.
  - `AimRig.cs` — `AimRig` (world-locked marker + Win32 raw mouse) + `Guards`.
  - `ChaseController.cs` — `ChaseController`. **An instance class, ONE PER AIRCRAFT (v0.82)** —
    get one with `ChaseController.For(aircraft)` (keyed by `Aircraft.GetInstanceID()`), release it
    with `Forget`, and **never `new` one**: a second instance for the same aircraft is a silently
    reset integrator. Every integrator/filter/ring buffer/probe cache in it is per-aircraft state, so
    the drone harness flying N at once needs N controllers. Only **three** things stay `static` and
    each says why in place: the Rewired player-0 cache (one input device per process), the anomaly
    stream's index/flash fields + trail throttle (one log stream per process), and — **v0.94** — the
    **A/B arm map** `_armByAircraft`, which is static precisely BECAUSE it is keyed by aircraft: it
    holds N independent assignments and it has to OUTLIVE the per-replicate `Forget` (see the `Arm()`
    seam below). `ChaseController.Player`
    is the LOCAL player's controller — published from `BeginFrame` only when
    `GameManager.GetLocalAircraft` names that aircraft — and it exists solely because `OnGUI` has no
    aircraft in hand; the HUD must never render a drone's numbers. Contents: the control law in `Apply`, nested
    `AnFrame`; also `DetectAnomalies`/`TrackManeuver`, the v0.55 FBW probe — per-airframe
    stick→pitch-rate params read from the game's `ControlsFilter.FlyByWire`, fail-soft — and the
    v0.57 canard probe/`InvertCanardRemap` — undoes the KR-67's `RelaxedStabilityController`
    pitch remap, fail-soft — and the v0.58 helo probe/`ResolveHelo` — reads the private
    `heloFlyByWire` of `HeloControlsFilter` + tilt/nozzle archetype components to rate-normalize
    rotorcraft commands and drive the hover regime by tilt angle, fail-soft; rotorcraft are
    force-flown with EvolvedLegacy — and the v0.59 AoA-utilization demand schedule/recovery
    bias — folds live AoA vs the probed alpha ceiling into `qSched`, gates `FineGainBoost`,
    and adds a restoring pitch past the ceiling; the loaded-jet pitch-relay fix. **ONE fixed-wing
    law now: `ApplyEvolvedLegacy`** (the Unified A/B alternative + its enum/hotkey were removed in
    v0.65 — see CHANGELOG); rotorcraft are force-flown through it too. **v0.96 dropped its unused
    `off`/`targetBank` parameters** — dead since v0.60 removed `Legacy` (change 2a computes its own
    `tBankE`); `Apply` still holds both as locals, for `DetectAnomalies`' over-roll check and the
    `tBankE` recorder column. v0.64 scales `pErrTerm` by the
    measured `_pitchEff` estimator; **v0.65 C1** reversal-gates that floor (below `revThresh` the floor
    is dropped so a reversed plant stops being forced), and **v0.65 B2** injects a bounded, V-independent,
    marker-stationary-gated micro-bank in the sub-0.5° azimuth cone so a high-q residual settles on a
    gentle coordinated turn (gate `_settleOK` from the aim-direction angular rate; new recorder column
    `settleOn`). v0.61 Track A (shared) gated the eAlign anti-relay slew to the dead-astern wrap region
    and decoupled the azTR presence gate from the predFloor. `Legacy`/`BankToTurn` removed in v0.60;
    `BankToTurnVmin` renamed `BankSpeedFloor` (shared bank airspeed floor). **v0.78** adds the
    marker-rate feed-forward: `_aimAzRateFilt` (signed marker azimuth rate, same `HdgRateTau` LPF as
    `_headingRateFilt`) is added at UNIT gain to `omega` at **both** lockstep sites, before the
    achievability cap so `omegaMax` bounds it — the fix for the standing 9.5° lag a P-only loop must
    hold to fly a sweeping marker; gated by `Cfg.MarkerRateFeedForward` as the in-session A/B lever.
    **v0.83** fixes the two law defects R21 pinned behind the *rest* of that lag (see
    `debugtests/R21-FINDINGS.md`), each behind its own checkbox, both default ON:
    (a) `Cfg.RelativeTurnLead` — the v0.51 anticipatory lead subtracted the **absolute** nose heading
    rate, but `d(azErr)/dt = markerRate − noseRate`, so that was the true derivative only against a
    **stationary** marker; tracking a sweep it braked the tracking rotation itself (7.85° of lead
    against a real 9.31° error) and cancelled `TurnLeadTime·AssistTurnRateGain = 0.60` of the unit-gain
    v0.78 feed-forward. Now `leadRate = _headingRateFilt − _aimAzRateFilt`, i.e. true PD on the azimuth
    error. **`predFloor` stays** — it guards the v0.54 rectifier, which lives entirely in the
    stationary-marker regime where the two lead forms are identical; the change just stops it binding
    in a matched turn. (b) `Cfg.IntegralStallGate` — `_iPitch`/`_iYaw` wound on `fineBlend`, i.e. on
    error **magnitude**, so the anti-residual term was identically zero at `off > FineAngle` (R21:
    ±0.001 against a 0.12 cap for a whole 30 s turn). The gate is now `max(fineBlend, _stallFilt)`
    where `_stallFilt` is the **dimensionless** fraction of the nose's own rotation *not* going into
    closing the error, held through a slow-attack (4 s) / fast-release (0.2 s) filter — the persistence
    filter **is** the anti-windup, and `yawCapped` suppresses the new path only. New recorder columns
    `iGate`/`leadDeg`.
    **v0.85** fixes the below-nose roll-to-align **positive feedback loop** (`elDn`: 6.92° standing
    error at ±43° bank, `blendWeight` correlating +0.918 with the `azErr` it is itself generating,
    against 0.03° for the *larger* mirror step `elUp` — see `debugtests/GATE-CHATTER-FINDINGS.md` §5a).
    Two independent checkboxes, both default ON: (a) `Cfg.BelowAlignSuppress` — the v0.67
    `belowSuppress` keyed on **body-frame** belowness, so the aircraft's own roll erased it (at 90° of
    bank a straight-down target reads abeam), and its `(1 − lateralHold)` factor gated it on the very
    azimuth error roll-to-align creates (51% of the suppression removed, on 88% of ticks). It now keys
    on the new **roll-invariant** `alignFracH` (horizon-referenced belowness, derived in `Apply` beside
    `alignFrac`, falling back to it near the vertical) and the `lateralHold` factor is **deleted**.
    (b) `Cfg.AlignRateLead` — the `eAlign` channel was a pure `phi/90` P map; `phi` is now led by the
    new measured `_phiRateFilt` (same `HdgRateTau`, zeroed **and invalidated** under `EAlignLatGate`)
    times `Cfg.RollDamping` as the lead time, stood down inside `phiWrapGate` where the two-rate
    anti-relay slew owns the dynamics. Separate levers on purpose: (a)'s risk is an upper-hemisphere
    regression, which is unattributable if both move under one `ScenarioArmToggle` knob. New recorder
    columns `bSup`/`bWt`/`phiLead`.
    **v0.87 — the same law now flies UNCREWED aircraft** (harness phase 2), with no change to any
    gain, gate or schedule. `Apply` reached for exactly three things that are one-per-*process* and
    all of them the human's: the `AimRig` marker, the Rewired player-0 stick and the `FlightHud`
    virtual-joystick crosshair. The marker became a **parameter** — `Apply(ac)` is a one-line wrapper
    over `Apply(ac, aimTarget)` that passes `AimRig.AimForward`, and a drone passes its own
    `ScenarioPlayer.For(ac).AimDemand` — and the other two are gated on `_uncrewed`, a per-instance
    bool whose **only writer** is `FlyUncrewed(ac, aimDir)` (= `BeginFrame` + `Apply` in one call,
    because a drone has ONE seam where the player has two). Without that gate a drone would be flown
    by whatever the human's stick was doing and `ManualReorients` would drag *his* marker onto the
    *drone's* nose. The crewed path cannot reach any of it by construction: `FlyUncrewed` is called
    only from `TestDrone`, whose dictionary an aircraft can only enter through `Spawn`, which asserts
    `ac.Player == null` — and `check-architecture.py` enforces the one-writer / one-calling-file pair,
    since neither fails to compile.
    **v0.94 — `Arm(ConfigEntry<bool>)`, the A/B SWEEP SEAM, and the reason the arm outlives `Forget`.**
    The five `Cfg` bools marked `(A/B lever)` used to be read straight off the process-global entry, so
    N aircraft could not fly different arms in the same instant and `ScenarioPlayer` had to stand the
    whole schedule down whenever a second aircraft was mid-card — every A/B was a one-drone serial run.
    Now `Arm(e)` returns THIS aircraft's assigned value when the assignment names `e`, and `e.Value`
    otherwise, and exactly **six** sites were converted: `RelativeTurnLead`, `IntegralStallGate`,
    `BelowAlignSuppress`, `AlignRateLead` and `MarkerRateFeedForward` at **both** of its lockstep sites.
    Nothing else — this is the sweep seam, not a general config indirection layer, and every extra
    conversion is a hot-path string compare buying nothing. **A NEW A/B LEVER MUST BE READ THROUGH
    `Arm()` TO BE SWEEPABLE**: `Cfg.X.Value` compiles and flies, it is just invisible to the schedule,
    which reads as "the A/B found nothing" — `debugtests/test-arm-schedule.py` fails on exactly that.
    The assignment lives in the **registry map** `_armByAircraft`, not in the instance, and that is the
    load-bearing bit: `ScenarioPlayer.PlaceOnCondition` calls `ChaseController.Forget(ac)` on EVERY
    replicate, so an instance field would be wiped at the start of every single replicate and the sweep
    would silently do nothing. It is also correct semantically — the arm is a property of the
    *aircraft's current test assignment*, not of the controller's integrator state. `For(ac)` seeds a
    freshly built controller from the map (`SeedArm`); **`Forget` must NOT clear it**; exactly two
    things do — the suite's own `Finish` and `TestDrone.ForgetState` on despawn. `SetArm(int, knob,
    val)` is keyed by instance id (like `Forget(int)`, and for the same reason) and also pushes onto a
    live instance, because a card with no entry condition never triggers a `Forget`. The pure part sits
    between `// --- ARM-SEAM BEGIN/END ---` markers with no Unity/BepInEx type in it, because
    `test-arm-schedule.py` extracts and compiles that region verbatim — keep it that way.
    Also holds
    `PilotPlayerStatePatch` (Harmony seam on `PilotPlayerState.PlayerAxisControls`).
  - `Recording.cs` — `ManeuverRecorder` + `AnomalyLog` (the log/recorder sinks ChaseController emits to).
    **`ManeuverRecorder` is an instance class, ONE PER AIRCRAFT (v0.86)** — same registry as
    `ChaseController`: `For(aircraft)`, `Forget(aircraft)`/`Forget(int id)` (which **closes an open
    capture** so a despawned drone doesn't leave a writer open with no `# stop` line), `Sweep()`, and
    `ManeuverRecorder.Player` (derived from `GameManager.GetLocalAircraft`) for the HUD and the
    RecordKey hotkey (`ToggleLocal`). N drones = N concurrent CSVs. Only `_recSeq` stays `static` and
    says why in place: it counts FILES OPENED this run (one artifact-stream numbering per process),
    which both keeps every take number unique across concurrent writers and keeps `rec=` monotonic in
    time — the key `compare-runs.py` orders its A/B balance check by. Filenames carry `d<drone>-` and
    the airframe `jsonKey` for a drone; a crewed capture's name is byte-identical to v0.85. The header
    now describes **this recorder's** aircraft, not `GetLocalAircraft`'s (a drone capture used to name
    the player's airframe). `NoteConfigChange` broadcasts to every open recorder — every `Cfg` knob is
    process-global, so a live edit lands on every aircraft flying.
    v0.69/0.70 added the instructor-loop instrumentation (65 CSV columns as of v0.96): alt/airDensity/pos/vel/
    segTag/tSeg/tWall) and the per-run `.airframe.json` sidecar (the readable per-airframe capability
    snapshot — masses, thrust, envelope, FBW params, Cl/Cd curves — every read fail-soft). v0.77 added
    `thr` (COMMANDED throttle) — a card owns the throttle, and until then a capture could not tell a
    bad throttle from a bad control law (R18 flew a whole card at idle and it read as an energy bug).
    v0.78 added `aimRate` (SIGNED marker azimuth rate, deg/s) — the v0.78 feed-forward adds exactly
    this quantity to the turn demand, and it is recorded on BOTH sides of the `MarkerRateFeedForward`
    toggle, because otherwise a capture cannot tell "the feed-forward fired and helped" from "the
    feed-forward never fired" (both read as a smaller azimuth lag). v0.83 added `iGate` (the wind gate
    the fine integrator actually used — with `IntegralStallGate` off it equals the old `fineBlend`
    exactly) and `leadDeg` (the anticipatory lead actually subtracted from `azErr`), under the same
    rule and on both sides of both v0.83 toggles. v0.85 added `bSup`/`bWt`/`phiLead` (the below-nose
    suppression actually applied, the roll blend weight **after** it — the loop gain the +0.918
    correlation was measured on — and the bearing lead), same rule again; unlike `leadDeg` these are
    **not** recoverable by arithmetic, since neither `alignFrac` nor `alignFracH` is a column.
    **New columns are appended at the END** — the Python
    tools index by header name but the contract in `Recording.cs` is positional-safe; keep it. v0.84
    added no column: it added the `# entry` **header line** (`EntryNote`, set by `ScenarioPlayer` at
    its placement) carrying the per-replicate reset provenance — `snapBackM`, the pre-placement
    speed/altitude, the fuel write, `ctrlReset` — i.e. the record of what the reset had to undo, so a
    batch can covary out what it *couldn't* undo (damage, session age) instead of being poisoned by it.
    v0.86 added `frameMs` (the rendered-frame time that fixed step saw, from `TestDrone.FrameDt` —
    **sampled in `Update()`, and it must stay there**: `Time.unscaledDeltaTime` read from
    `FixedUpdate` returns `fixedUnscaledDeltaTime`, a CONSTANT, which is what it did from v0.86 to
    v0.92 — R27 read exactly 16.70 ms on all 223,899 rows of a 352-capture batch and missed a logged
    119 ms hitch. Fixed in v0.92.1; captures older than that have a `frameMs` column that means
    "the fixed step", not "the frame"). The
    drone launch stagger exists *because* a frame hitch lands on whatever segment is running when it
    happens, so N replicates flying the same segment at that instant stop being independent samples —
    until now that was an assumption backed only by a `[drone] frame hitch` warning in a log nobody
    diffs. As a column it is per-row evidence, so a batch can drop or covary out the rows that were
    actually stalled. **The header/row lockstep is now checked**: `check-architecture.py` counts both
    and fails on a mismatch (and on CLAUDE.md's documented count drifting from the code).
    v0.92 added no column either (still **64**) — it added three SIDECAR fields:
    `infoStallSpeed`/`infoMaxSpeed` (from `aircraftInfo`, converted to m/s) and `infoMaxWeight`. The
    sidecar recorded the whole capability block but not the two numbers a flyability question needs,
    so no capture was self-contained on "could this airframe fly the entry condition it was given?" —
    the question v0.92's pre-spawn gate asks, and the one an unflyable lane's capture looks innocent
    under. **Distinct key names from the existing `maxSpeed`**, which is `aircraftParameters.maxSpeed`
    — a normalizer, a different quantity, and two quantities must never share a key. `infoMaxWeight`
    is advisory only: its sibling `emptyWeight` is documented template junk (`AIRFRAMES.md` trap 3),
    so keep normalising by `massKg`.
    v0.90 added no column either (still **64**): it added the `# override` **header line**
    (`OverrideNote`, set by `ScenarioPlayer` from the card's `config` list just before `Toggle()`,
    written directly under `# card` because it only ever exists for one) listing the `Section/Key=value`
    knobs THAT CARD pinned for itself. Not a column on purpose: the value is constant for the whole
    capture by construction (pins go on before the recorder opens and come off after it closes), and
    it is not redundant with `# config` — that shows the live *values*, and what it cannot show is that
    the **card** chose them rather than the operator, which is what separates "this run was configured
    by its card" from "someone left a knob set". Sanitised on assignment like `EntryNote`; absent
    entirely for a hand-flown capture or a card that pins nothing.
    **v0.96 added column 65, `dmgFrac`** — the fraction of THIS aircraft's parts currently
    **detached**, straight off the game's own `Aircraft.partDamageTracker.GetDetachedRatio()` (public
    field `:60388`, constructed `:61084`, class `:79217`, getter `:79244` — event-driven and
    self-throttled to 1 Hz, so it returns a cached float and is free to read on every row). Read off
    the recorder's OWN aircraft (the `ac` parameter), in its own try/catch: **−1 means COULD NOT READ
    IT, never 0**, because 0 is the perfectly ordinary "intact" reading — which is also why it is not
    folded into the M0 state block, whose catch leaves zeros. v0.84 named damage as one of the two
    things the per-replicate reset cannot undo and must therefore *record*; this is that record.
    `aeroPartCount` is **not** a substitute: nothing on the detach path calls `RemoveFromUnit()`, the
    only caller of `DeregisterAeroPart` (`AeroPart:74558-74564`), so it never decreases. Over-G
    damages the PILOT only (see Conventions), so joint-break detachment is the only in-flight
    airframe damage this harness produces. The sidecar also gained `detachedRatioAtStart` (did this
    replicate START bent? — the column cannot say, since it only reports *now*), fail-soft to
    **absent**, not 0. A capture whose `dmgFrac` ever exceeds 0 is flagged **DAMAGED** by
    `scorecard.py`.
  - `ScenarioPlayer.cs` — `ScenarioPlayer` (v0.71, milestone M1). **An instance class, ONE PER
    AIRCRAFT (v0.86)** — `For(aircraft)` / `Forget` / `Sweep` / `Player`, same registry as
    `ChaseController`. All *playback* state is per-instance (queue, segment index, segment clock,
    heading frame, anchor, placement audit, card-recording buffers, **and since v0.94 the A/B arm
    schedule**); **two** things stay `static` and each says why in place: the **card library**
    (`_cards`/`_enable`/`_cf` — shared read-only config) and the
    **on-screen notice** (one screen per process). The
    hotkey doors (`ToggleSuite`/`ToggleRecord`/`ForceEntryNow`/`AbortLocal`) stay static and resolve
    the LOCAL aircraft, then call the instance body — so a phase-2 drone runner drives the same code
    (`For(droneAc).StartSuite(droneAc)`) with no second copy to drift. `Tick` is called from the seam
    prefix for the player and from **`TestDrone.OnPilotStep`, immediately before `Drone.Fly`**, for a
    drone — the same zero-tick property at that aircraft's own seam. Each instance publishes its
    demand as `AimDemand`; the local one *also* writes `AimRig.SetAimForward` (unchanged v0.85
    behaviour). **v0.87 gave `AimDemand` its consumer**: `TestDrone.ChaseCard` hands it to
    `ChaseController.FlyUncrewed`, so a drone chases its own card through the same `Apply`. The other
    two entry points a drone's seam drives are `StartSuite` (once, on the drone's first pilot step)
    and `OwnInputs` (throttle, between the stick write and `FilterInputs`) — the same three the
    player's seam uses, no second copy.
    Plays a card by writing the aim demand from the *seam prefix* (so `Apply` reads it the
    same tick — zero-tick lag is structural, don't move it), tags each segment via
    `ManeuverRecorder.SegmentTag`, and brackets the run with the recorder. Also **records** a card
    from a human flight (samples the aim demand on the fixed step) and binds one config checkbox per
    card so F1 is the selection UI. Entirely idle unless a card is running.
    **Owns the safe-teleport primitive — the mod's only way of writing aircraft PHYSICS state, and
    since v0.95 it has TWO callers.** `PlaceOnCondition` (the per-replicate card reset) is one;
    `PlayerSpawn.Place` (the F4 sandbox, hand-flight only) is the other, and it reuses
    `ResetGLoadTrackers` + `MoveAssembly` — `internal static` for that reason — instead of copying
    them. Everything *around* the write is still card-only and stays here: the run anchor, the fuel
    write, the demand write, the entry audit and the `# entry` header. Two pairings
    are mandatory and both were learned by destroying the airframe: (1) zero
    `Pilot.velocityPrev` before the velocity write — the game reads G as a velocity difference across
    fixed steps; (2) move the **whole assembly** — under complex physics an aircraft is one `Rigidbody`
    **per part**, joined by `FixedJoint`s, so moving only `Aircraft.rb` stretches every joint and PhysX
    returns it as ~`err/dt` of velocity. Apply the same rigid transform (same rotation about the same
    pivot, same translation, same velocity) to every `ac.partLookup[].rb` and no joint sees a relative
    change. **Do not** reach for `SetSimplePhysics`/`SetComplexPhysics` to sidestep this: its `Destroy`
    is deferred to end-of-frame, so a FixedUpdate caller still simulates with live stretched joints,
    and destroying components silently invalidates anything the game cached (Unity reports a destroyed
    object as `null` without throwing).
    **v0.84 — `PlaceOnCondition` is a full RESET, and the harness interleaves A/B arms.** Ten identical
    replicates of one card came out non-exchangeable (`terminalOffDeg` vs run index r = −0.824; a
    first-half/second-half split of one *unchanged* arm beat its own detection threshold, i.e. doing
    nothing scored as significant). The placement itself was fine — the leaks were around it, and all
    three landed on the `arm` window and therefore on the state the *scored* segment starts from:
    position was never reset (30 km of downrange walk); the aim demand was **stale for one tick**
    (`Apply` runs from the same call's postfix, so the teleport tick chased the previous card's marker
    from the new attitude — `outP` −0.487 at the first sample of the late runs); and the per-*aircraft*
    `ChaseController` (v0.82) carried integrators/filters/`_pitchEff` between replicates flown by the
    same aircraft. So the placement now snaps back to an **anchor** (position + heading, captured on
    the first placement of a run, held in the datum-relative `GlobalPosition` frame so a floating-origin
    rebase can't move it), writes the demand, and calls `ChaseController.Forget(ac)`. Engine spool is
    deliberately not reset (throttle is pinned across the card boundary, so it doesn't drift); damage
    and session age can't be reset and are **recorded** in the `# entry` header line instead.
    **v0.88 trimmed the placement; v0.89 REVERTED it — and the entry transient has a different
    cause.** v0.88 wrote the velocity one measured trim-AoA below the level nose, on the theory that
    AoA = 0 is zero lift and the resulting ~1 g catch was the thump. Gate B (R23) disproved it: run 01
    is the run's first placement, so no trim had been measured and it was written **untrimmed** — the
    exact condition v0.88 blamed — and it has the *cleanest entry of the four* (AoA 0.07° → 1.46° with
    no overshoot, `off` peak 0.59°) against 2.74–2.87° and 1.72–1.97° on the three trimmed ones. It
    also coupled each replicate's entry to a value measured during the **previous** replicate, in a rig
    whose whole purpose is replicate independence. Gone; the `# entry` line no longer carries
    `aoaTrim=` (the CSV stays at 64 columns — it was never one).
    **The real defect, measured and NOT yet fixed: `ChaseController.Forget(ac)` does not take effect on
    the placement tick.** At `tSeg=0.000` of every *placed* capture the controller still holds
    pre-placement state — `rollRate` −58.99/−58.66/−58.65 (vs −0.16 on the unplaced run 01),
    `rollRateF` −12.83 bleeding through the roll damper for ~0.2 s, `headingRateFilt` 10.4–19.3, and
    `leadDeg` **6.8–12.5°** of phantom anticipatory lead against a 0.04° error. `rollRate` is
    `(t.up − _prevUp)/dt`, so −59 requires `_prevUp` to hold the *banked* attitude: the placement snaps
    a ~79° banked turn wings-level in one fixed step and the finite difference straddles it. Every
    **direct** measurement on that row (`bank`/`alt`/`pos`/`spd`/`aoa`) is correctly post-placement;
    only the derivatives are poisoned, which a freshly-`Forget`-ed instance cannot do. **`iPitch`
    reading 0.0000 there is not evidence of a reset** — R21 measured it at ±0.001 for an entire 30 s
    turn, so it is ~0 coming out of a turn regardless (this retracts a Gate A claim). Deliberately
    unfixed: a discontinuity guard on the finite difference would clean `rollRate` and leave
    `headingRateFilt`/`leadDeg` alone, making the symptom look fixed while hiding the cause. Harmless
    to results so far — deterministic to within 0.02 across replicates, and it decays inside the 6 s
    `arm` before the scored segment starts.
    `Cfg.ScenarioArmToggle` — or, since v0.90, the first selected card's own `armToggle`, which wins
    over it — names a bool knob the runner alternates **ABBA** by queue index
    (`((i+1)>>1)&1`) — never A×N then B×N, which is the pattern that turns drift into a fake effect —
    and each capture self-identifies via `arm=`/`armKnob=` on its
    `# config` line (`arm=` parses out of `scorecard.py`'s existing `cfg_params()` regex unchanged).
    **v0.94 — THE A/B SCHEDULE IS PER AIRCRAFT, AND A FLEET SWEEPS CONCURRENTLY.** (This replaces the
    v0.86 "the schedule stays static, and that is forced" note, which described a limitation that no
    longer exists — do not restore it.) The knob is no longer written at all: `ApplyArm` calls
    `ChaseController.SetArm(_acId, key, arm)` and the law reads the lever through
    `ChaseController.Arm()`, so N aircraft fly N arms in the same instant. `_armEntry`/`_armIdx` are
    per-instance; `_armSaved`, `_armOwner`, the save/restore dance, the "another aircraft owns the
    schedule" refusal, the stand-down warning and the `SettingChanged` re-entrancy guard around the arm
    write are all **deleted** — nothing writes the setting, so there is nothing to restore, nothing to
    own and nothing to fire the event. `ArmTag` and `ArmLabel` are per-instance too (with the static
    non-creating `ArmTagFor(ac)` for the recorder header), and the run board's lines can now
    legitimately disagree: four drones mid-ABBA read A/B/B/A. **What each aircraft's own ABBA buys and
    does not buy:** the invariant — both arms at the same *mean position in the batch*, so a monotonic
    drift cancels — holds **within every lane**, which is the right unit of analysis because
    `compare-runs.py` groups by (airframe, card, arm) and refuses to pool across airframes anyway; a
    4-lane fleet card is four independent A/Bs. It does **not** balance the arms across aircraft at a
    given wall-clock instant, and that is deliberate: a confound would have to hit the fleet at one
    instant AND correlate with lane, and both candidates are already handled (`frameMs` is a per-row
    column, airframes are never pooled). `ArmOf` lives between `// --- ARM-SCHEDULE BEGIN/END ---`
    markers because `debugtests/test-arm-schedule.py` compiles it verbatim.
    **The capture cannot lie about its arm**, which is the whole point: `# config` prints the five
    levers **as flown** — through the same `Arm()` the law used, via `Cfg.SnapshotString(controller,
    armTag)` — instead of the operator's F1 value, which would otherwise sit on the same line as
    `arm=1` and contradict it.
    **v0.90 — A CARD IS THE WHOLE TEST, not just the stimulus; v0.91 finished the sentence by giving
    it the FLEET.** `Card` gained `repeat`, `armToggle` and a generic `config` list of `{key, value}`
    overrides in v0.90, then `count` in v0.91, where `airframe` also became a **comma list** (one
    jsonKey per drone lane, wrapping, exactly like `Cfg.DroneAirframe`); `Preview()` reports what a run
    *would* fly. Every one of them falls back to the matching `Cfg` knob when absent, so a card that
    declares nothing behaves exactly as it did in v0.89 — which is what keeps the shipped grid and
    every ad-hoc recording valid. Together they are what makes an unattended batch **one checkbox and
    the spawn key**, with nothing in F1 to hand-match: a mismatched global never refuses, it writes a
    capture that scores fine and answers a different question. What to know before touching this:
    - **One grammar, one parser.** `SplitSpec` (`"Key"` or `"Section/Key"`, bare keys ⇒ `Control`,
      both halves non-empty) is shared by `ScenarioArmToggle`, a card's `armToggle` and every
      `config[].key`. `ResolveEntry` finds an entry of ANY type (a card can pin a float or a KeyCode);
      `ResolveArm` additionally insists on `ConfigEntry<bool>` and says so *distinctly* from
      "not found", because until v0.90 pointing the sweep at a real-but-numeric knob read as a typo.
      Values are parsed by BepInEx's own `TomlTypeConverter` — one call covers bool/int/float/string/
      KeyCode; do not hand-roll a second definition of what a config value looks like.
    - **Order in `Tick` is load-bearing twice over**: `ApplyOverrides` → `ApplyArm` → `StartCard`
      (which is what calls `_rec.Toggle()`), and `RestoreOverrides` **after** `_rec.Stop` in both
      `Finish` and `NextCard`. Arm-after-overrides makes the swept arm win regardless of the refusal
      below; both-before-the-recorder is because `ConfigFile.SettingChanged` drives
      `ManeuverRecorder.NoteConfigChange`, which stamps a `# cfg` line into every OPEN capture — a
      card's own setup landing in its own CSV would read as the law changing mid-run, which is exactly
      what those lines exist to flag. `_ovEntries != null` is the re-entry guard (the placement
      re-enters this path a tick later).
    - **Pinning the knob the A/B schedule sweeps is REFUSED, loudly**, and the rest of the list still
      applies. Pinning it flies every replicate on one arm while each capture still labels itself
      `arm=0`/`arm=1`, so the A/B reads as "no difference" and nothing in the artifacts says why.
      Everything else is fail-soft: one named warning per bad override, then fly.
    - **`Validate` heals a prose `airframe`, PER TOKEN since v0.91.** The field was documentation
      until v0.90 gave it behaviour (the drone harness now SPAWNS it), so an `airframe` that is still
      being written as prose is blanked with a named warning and the launch falls back to
      `Cfg.DroneAirframe` — the pre-v0.90 behaviour. Now that the field is a comma list the test
      splits first and looks for whitespace **inside a trimmed token**, so `"Fighter1, Multirole1"` is
      a two-airframe fleet and `"any jet at the fixedwing-v2 entry condition"` is still prose; a
      jsonKey never contains whitespace, so a token that does is unambiguous, and whitespace merely
      around the commas is formatting `TestDrone.AirframeList` trims anyway. Human description goes in
      `note`, which the C# ignores by construction. `scorecard.py`'s `card_setup_problems` mirrors this
      rule and must be changed **with** it — it is the only offline check on a card's setup, and a
      copy that is *stricter* than the mod flags cards that fly perfectly well.
    - **`ResolveCount` — how many drones, three sources, and the middle one is the point.** The card's
      own `count` if > 0; else the NUMBER OF KEYS its `airframe` names (`CountKeys`); else
      `Cfg.DroneCount`, i.e. pre-v0.91 behaviour. The middle rule is not a convenience: a card whose
      airframe list is the fleet it wants tested has already said how many drones it needs, and 12 keys
      against `DroneCount` 4 flies the first four lanes and silently answers a different question. One
      `Mathf.Clamp(1, 16)` for the value wherever it came from, so a card cannot reach a fleet size the
      operator could not have set by hand. Set `count` explicitly only to fly a MULTIPLE of the list
      (8 over a 4-key list = two per airframe, since lanes wrap). `CountKeys` is a deliberate
      count-only twin of `AirframeList` rather than a shared helper: that one returns the lane
      assignment and needs the harness, this one runs from `Preview` with no aircraft in hand.
    - **`Preview()`/`Preflight` answer "what would fly?" with NO AIRCRAFT IN HAND**, because its
      caller (`TestDrone`) is choosing what metal to spawn. Hence no `cls` filter (that is a
      per-aircraft, post-spawn test — reading it as a spawn instruction would let `"Plane"` mean an
      airframe) and no replicate expansion. It **never throws**: it runs on a hotkey path before
      anything is spawned. `Preview(quiet: true)` exists for the run board's GUI poll — a repaint must
      not spam the log the way a keypress may. Every resolved value is paired with a `*Src` string
      (`RepeatSrc`, `ArmSrc`, and v0.91's `Count`/`CountSrc`) because "4 drones" reads identically
      whether the card asked for it or a knob was left there, and that difference is the whole point of
      resolving it in one place. **v0.93 adds `StartSpeedCorner` beside `StartSpeed`** — carried as the
      PAIR, not resolved into one number, because a corner-relative card has a different entry speed in
      every lane and this struct is answered with no aircraft in hand.
    - **v0.93 — `startSpeedCorner`, and the reason it is a RESOLVER and not a read.** A card may declare
      its entry speed as a multiple of **the lane airframe's own corner speed** instead of an absolute
      m/s; when `> 0` it wins over `startSpeed`. `ResolveStartSpeed(startSpeed, startSpeedCorner,
      jsonKey)` is the single definition — `startSpeedCorner <= 0` ⇒ `startSpeed` (byte-identical
      pre-v0.93); else `TestDrone.TryEnvelope`'s `Corner` × the multiple (**v0.96: that `Corner` is the
      FBW's `cornerSpeed`, not the AI's** — see the `TestDrone.cs` bullet; corner-relative captures
      before and after that change are not comparable); else **fail-soft to
      `startSpeed` with a named warning**, the probe doctrine ("could not read it" is never "the corner
      speed is zero"), and a card with neither field is simply ungated as the `rotor-*` cards already
      are. It takes PRIMITIVES because the other caller (`TestDrone.SpeedOfLane`, pre-spawn, per lane)
      holds a `Preflight` and no aircraft; `EffectiveStartSpeed(Card, jsonKey)` is the one-line Card
      wrapper. **Every read of `c.startSpeed` in the playback paths routes through it** — the placement,
      `EntryConditionError` (now an INSTANCE method for exactly this), `ForceEntry`'s "does any card
      declare an entry condition" scan, `OwnInputs`' ungated-card test and the `Tick` placement gate.
      Converting only the spawn is the failure mode this design exists to prevent: the aircraft would be
      placed at 180 m/s while the gate still demanded 250 and refused the run forever. The instance form
      `EntrySpeed(Card)` caches on a **reference compare** (the queue holds the same `Card` object once
      per replicate) so `OwnInputs` does not do an Encyclopedia lookup every fixed step. The card
      RECORDING path still writes an absolute `startSpeed` with `startSpeedCorner` unset — a human flight
      has one real speed, not a multiple. No CSV column: the `# entry` header already carries the
      resolved speed and the sidecar carries this airframe's `cornerSpeed`, so the multiple is
      recoverable.
    - **Run-board accessors** (`CardName`/`RunIndex`/`SegTag`/`ArmLabel`/`SegSecondsLeft`/… and the
      static `CollectRunning`) are field reads by design: `OnGUI` runs twice a frame, so nothing there
      may allocate or walk the queue. `IndexCard()` caches the segment durations and must follow every
      write to `_card`/`_qi`/`_queue`. The two non-trivial pieces of arithmetic live between the
      `// --- BOARD-MATH BEGIN/END ---` markers, in plain floats with no Unity types, because
      `debugtests/test-board-math.py` extracts that region verbatim and compiles it — keep them inside
      the markers and keep them SDK-compilable.
    - **v0.96 — `Tick` has a SECOND safety abort, beside the altitude floor: airframe damage.** One
      clause, same `_frameSet` gate, threshold **`> 0f`** — *any* detachment. Deliberately **not** the
      game AI's `> 0.12` (`:12203`/`:13463`): that number asks "can this aircraft still fight?", and
      this is a measurement rig, where an aircraft with a part missing is not the same airframe the
      previous replicate flew and cannot contribute a comparable sample. The reason names the ratio
      (`airframe damage (detached ratio N.NNN)`) and reaches the CSV's `# stop` line through the
      existing `Abort`→`Finish` path. One placement covers drones and the player. Note the read here
      is fail-soft **the other way round** from the recorder's `dmgFrac`: unreadable ⇒ *not* damaged,
      so a failed probe can never kill a good run, while the −1 in the column is what says the probe
      failed. Do not "unify" the two defaults; they point in opposite directions on purpose.
    - **Two more extract-and-compile marker regions (v0.96)**, same rule as `BOARD-MATH` /
      `ARM-SCHEDULE` / `CARD-MODEL` — keep the code inside them SDK-compilable and move the markers
      *with* the code: **`SPEC-GRAMMAR`** (`SplitSpec`, checked by `debugtests/test-spec-grammar.py`)
      and **`FLEET-RESOLVE`** (`ResolveCount` + `CountKeys`, checked by
      `debugtests/test-fleet-resolve.py`).
  - `TestDrone.cs` — `TestDrone` + `Drone` + `TestDronePatch` (v0.81 harness, **v0.87 phase 2**).
    Spawns aircraft nobody is sitting in, flies them, despawns them — **N alive at once**,
    launched on a stagger. `TestDrone` is the manager (live list + a dictionary keyed by
    `Aircraft.GetInstanceID()`, the launch countdown, and the fixed-step `FrameDt` sample);
    `Drone` is one aircraft and carries its **own** `Fly` delegate, because N drones need N
    independent controllers; `TestDronePatch` is a Harmony **postfix on
    `Pilot.Pilot_OnAeroInputsApplied`** that writes a drone's `ControlInputs` and then calls
    `Aircraft.FilterInputs()` by hand (the FBW/`RelaxedStabilityController` pass is only ever run
    *from a pilot state*, and an uncrewed aircraft has none). It **no-ops for every other aircraft**,
    the player's first of all — an aircraft can only enter that dictionary through `Spawn`, which
    spawns with `player=null` and then asserts `ac.Player == null` before registering.
    **The AI is off by construction, not by fighting it:** spawning with `HQ = null` makes
    `Pilot.SetStartingAiState` bail straight to `parkedState`, so the AI states are never built.
    Needs an **active server** — single player is a host, so SP and hosting work; as an MP client the
    spawn is refused with a log line. `Cfg.DroneEnabled` is off by default and the subsystem is inert
    while it is (the hotkeys are not even read; the postfix is one int compare).
    **v0.87 — PHASE 2: the mod's real control law flies a drone.** `Drone.Fly` defaults to
    `TestDrone.ChaseCard`: card running ⇒ `ChaseController.For(ac).FlyUncrewed(ac, sp.AimDemand)`; no
    card ⇒ the built-in level-hold, which is a deliberately trivial altitude/wings hold and is **not**
    the mod's control law (never tune it, never compare a level-hold capture against a card capture);
    the instructor *declining* mid-card ⇒ **abort the card** with the reason in the CSV's `# stop`
    line, because finishing the run on a different controller writes a capture that reads as clean.
    `OnPilotStep` starts each drone's card on **its own first pilot step** — not at `Spawn` (a card's
    first act rigid-moves every part rigidbody, which must not hit a half-built assembly) and not from
    one shared key (that would align every replicate's segment boundaries, which is what the launch
    stagger exists to prevent) — and calls `ScenarioPlayer.OwnInputs` for the throttle between the
    stick write and `FilterInputs`, mirroring the player's seam postfix (a card at `throttle == 0` is
    the game's airbrake trigger; that was R18's false "energy failure").
    Also the reason `WTMouseAimPlugin` now has a `FixedUpdate`: the launch stagger needs a fixed-step
    clock that exists before any drone does. **Both** removal paths (`Despawn` and `PruneDead`) call
    one `ForgetState(aircraftId)` that drops **every** per-aircraft registry — `ScenarioPlayer`,
    `ManeuverRecorder` (closing an open capture), `ChaseController` — keyed by the CACHED id for the
    same reason the dictionary is (the aircraft may already be destroyed). One function, so the next
    per-aircraft registry cannot be forgotten on one of the two paths.
    **v0.86 — `Cfg.DroneAirframe` is a COMMA LIST**, indexed by lane and wrapping, so a batch can be
    heterogeneous (`"Multirole1, CAS1"` with `DroneCount` 4 = two of each) and a single value
    behaves exactly as it did. An unknown `jsonKey` still refuses with a log line naming it: with one
    key that cancels the launch (the next lane fails identically), with a list only that lane is
    skipped. Each capture self-identifies — the `.airframe.json` sidecar's `jsonKey` is what
    `compare-runs.py` groups on and refuses to pool across, and the CSV filename carries it too.
    (v0.90: a card that names an `airframe` overrides **the whole list**, not one lane — one test, one
    fleet definition, and a card contributing a single lane to a `Cfg` list would be neither.
    **v0.91: the card's `airframe` is itself a comma list**, read by the same `AirframeList` splitter,
    so the heterogeneous batch moved INTO the card instead of being the reason to leave the field
    empty. That is the whole shape of the release: `Cfg.DroneAirframe` is now purely the fallback for
    a card that names nothing, not the place a multi-airframe run has to be configured by hand.
    `compare-runs.py` still groups by the sidecar's `jsonKey` and refuses to pool across airframes, so
    a mixed batch comes back as one row per airframe — which is what makes a fleet card readable
    rather than a pooling bug.)
    **Loadout is still `null`** at the `Spawn` call: the game's parameter is a `Loadout` object, not a
    name. The lane index is the hook when that API is known; the sidecar already records the resulting
    stations/masses/drag per capture, so nothing on the analysis side changes.
    Also ticks each drone's `ScenarioPlayer` inside `OnPilotStep`, immediately before `Drone.Fly`.
    **v0.90 — the batch cleans up after itself and the CARD picks the metal.** Four things:
    - **Auto-despawn.** `PruneDead` despawns any drone that has had no card running for
      `IdleDespawnSec` (5 s, a `const`; the despawn key covers "now"). ONE rule, which is why it covers
      suite-complete, aborted, refused *and* never-started — before this the only automatic despawn was
      the exception path, so a finished drone fell back to the level-hold and circled forever. The
      grace window is sized by the gap between `NextCard` closing one recorder and `StartCard` opening
      the next; anything shorter despawns a drone between its own replicates. Not tidiness: a live
      drone keeps a full complex-physics aero job and all three per-aircraft registries alive — the
      same frame budget the stagger protects and `frameMs` measures. `Despawn` now takes a reason and
      logs it.
    - **A shot-down drone is caught in `OnPilotStep`, not `PruneDead`.** `PruneDead`'s predicate is
      `Aircraft == null || Aircraft.disabled`, and the game **never self-disables an `Aircraft` on
      damage**: `Unit.disabled` is written only by `ServerDisableUnit`/`ReturnToInventory`/`OnDestroy`,
      and `WaitRemoveAircraft` fires *from* the disabled hook — so a shot-down aircraft keeps a live
      GameObject with `disabled == false` indefinitely (measured in R25: it stayed registered until the
      mission quit). `OnPilotStep` holds the `Pilot` the damage lands on, so `p.dead || p.ejected` now
      despawns with the reason instead of early-returning — and that check must stay **ahead of every
      write below**, since the patched method itself early-returns on both. An airframe destroyed
      without killing the pilot needs no third case: the card's altitude floor aborts it on the way
      down and the idle rule then despawns.
    - **Lanes fall back to `Camera.main`, not `Vector3.zero`.** With no local aircraft (ejected, dead,
      spectating — what an operator watching a batch usually is) the origin was not merely invisible,
      it was the SAME point on every press, so a second launch stacked lane *k* on the first launch's
      lane *k* while each drone's card anchor is its own spawn point. `_slot` also starts at
      `_live.Count`, so "press it twice" is safe.
    - **The spawn reads the card first.** `RequestLaunch` keeps one `ScenarioPlayer.Preflight`
      (`_plan`), resolved ONCE per batch — per lane would let a checkbox ticked mid-stagger change the
      airframe half way through — and `AirframeOf`/`AltOf`/`SpeedOf`, plus **v0.91's `CountOf`**, take
      a `Preflight` as an argument so the run board can ask the same questions of a fresh preview and
      get, by construction, what the launch will use. The launch log names **which source won for each
      value**; that line is the operator's only confirmation, since "4000 m" reads identically whether
      the card asked for it or a knob was left there. Empty / `<= 0` means "the card doesn't say" and
      the `Drone*` knob stands, which is what keeps a hand-configured launch working.
      **v0.91 moved the `_plan` resolve AHEAD of the `_pending` write**, and that order is now
      load-bearing: `_pending` (the launch countdown) *is* the fleet size, so setting it from
      `Cfg.DroneCount` before the preview existed would pin every batch to the global and quietly
      undo the card's `count`. Unlike the other three, `CountOf`'s `Cfg` fallback lives in
      `ScenarioPlayer.ResolveCount` rather than here — the "as many as the airframe list names" rule
      needs the CARD, and a `Preflight` with no card already carries `Count` 0 — so `CountOf` is a
      clamp and a no-card guard, not a second copy of the policy.
    - **v0.93 — the entry speed is a PER-LANE question, so there are two accessors and they mean
      different things.** `SpeedOf(Preflight)` is the batch-wide answer and deliberately handles the
      ABSOLUTE form only; `SpeedOfLane(Preflight, jsonKey)` calls `ScenarioPlayer.ResolveStartSpeed` and
      is the real one. `LaunchDue` resolves it ONCE into a local and hands the same value to both
      `EntrySpeedFlyable` and the spawn velocity — asking twice, or gating on `SpeedOf`, is exactly how
      a lane gets checked at one speed and placed at another. The no-argument `SpawnSpeed()` twin was
      **deleted** for that reason (`SpawnAlt()` remains; altitude has no per-airframe form). For the
      operator-facing text, `SpeedText(Preflight)` prints `1.00x corner (per airframe)` rather than a
      number no lane will fly, and `SpeedFromCard(Preflight)` is the shared "did the card decide this?"
      test the launch log and the run board spell differently.

    **v0.92 — a lane whose airframe cannot fly the card's entry condition is REFUSED before it
    spawns.** `TryEnvelope(jsonKey, out Envelope)` reads Vstall/Vmax/corner/gLimit off
    `Encyclopedia.Lookup` (public static `Dictionary<string, UnitDefinition>`, decompile `:9715`) with
    **no aircraft instance** — refusing after the spawn would already have created the unit — reusing
    the spawn's own `Encyclopedia.i == null` readiness test. **Vstall/Vmax come from `aircraftInfo`,
    which is KM/H (÷3.6); `aircraftParameters.maxSpeed` is a NORMALIZER reading a flat 600 for every
    fast jet** (`:15554`), and a check built on it concludes the 141 m/s Cricket can do 250. Fail-soft
    like the FBW/canard/helo probes: `false` = "could not read it", never "the bounds are zero" (the
    out-value is untouched, so there is no zero to mistake for data), and an unknown envelope **never
    refuses**. `EntrySpeedFlyable` gates the LANE's resolved speed (`SpeedOfLane`, v0.93 — it was
    `SpeedOf(_plan)`, which is now the wrong question) against `1.10 x Vstall` … `0.95 x Vmax` —
    the floor is **not** 1.20 because the shipped grid's tightest legitimate pairing is `stol-*` at
    90 m/s on `SmallFighter1` (Vstall exactly 75.0, ratio exactly 1.200), so a 1.2 floor would decide
    a flyable card by the float rounding of `stallSpeed / 3.6`. The refusal is ONE log line naming the
    airframe, the requested speed, the violated bound and its value, then it returns `null` into the
    **same** skip-or-cancel decision an unknown `jsonKey` takes (list ⇒ skip that lane; single key ⇒
    cancel, the next lane would fail identically). Speed only: there is no service ceiling anywhere in
    the decompile (`AIRFRAMES.md` trap 5), so altitude has no per-airframe bound to check.
    Consequence worth knowing: `rotor-*` cards declare `startSpeed: 0` meaning *hover*, `SpeedOf`
    reads 0 as "the card doesn't say" and falls back to `DroneSpawnSpeed` (250), so a rotorcraft lane
    now refuses on the ceiling instead of spawning at 250 m/s and decelerating — the gate exposing a
    pre-existing mismatch, whose fix belongs in how the entry condition is expressed.
    **The gate is NOT superseded by v0.93's `startSpeedCorner`** — it now also catches a card declaring
    a bad multiple (`2.0x` corner is over the Vmax ceiling on most of the roster), which is why it
    checks the resolved per-lane speed rather than the card's raw number.

    **v0.96 — `Corner` now comes from the FLIGHT MODEL's corner speed, not the AI's** (backlog #41).
    `TryEnvelope`'s `Corner` reads `ControlsFilter.FlyByWire.cornerSpeed` (`:64704`), **not**
    `aircraftParameters.cornerSpeed` (`:62924`). The two are different quantities that happen to share
    a name: the `aircraftParameters` one drives AI behaviour only (throttle policy `:12993`,
    glideslope `:13624`, effort scaling `:15773`) and the flight model never reads it, while the FBW
    one *is* the pitch-rate demand's saturation speed (`:64859`) and the G-limit knee (`:64672`).
    Measured over 1604 archived sidecars they differ by **0.556×** (Darkreach 100 vs 180) to **1.417×**
    (AttackHelo1 170 vs 120) — a 2.2× spread, against a `startSpeedCorner` card whose entire claim is a
    *uniform aerodynamic* entry state. **No reflection**: `ControlsFilter.GetFlyByWireParameters()` is
    public (`:65521`) and packs `cornerSpeed` at index 2 (`FlyByWire.GetParameters()`, `:64786`) — the
    same public accessor `ChaseController`'s v0.55 in-flight probe already uses, here just asked of a
    **prefab**: `TestDrone.FbwCornerSpeed(jsonKey)` = `Encyclopedia.i.TryGetPrefab` →
    `GetComponentInChildren<ControlsFilter>(true)` (which also covers `HeloControlsFilter :
    ControlsFilter`, `:35847`) → `p[2]`. Fail-soft via a **NaN sentinel** — 0 is a speed and would
    quietly become an entry condition — falling back to the encyclopedia value with a named warning;
    cached per jsonKey in `_fbwCornerByKey`, and **the cache IS the once-per-airframe warning
    mechanism**. In-game confirmation is the *absence* of that warning; if it fires for every airframe
    the prefab read failed and the fix is silently a no-op, so grep for it first. Worth knowing before
    writing a roster card: at `1.0x`, **all ten** fixed-wing keys now pass (`CAS1`'s FBW corner is 160,
    under its 195.3 ceiling — it used to refuse on the AI's 200). `0.95x` still clears all ten.
    **Analysis consequence:** every corner-relative capture from R29 and earlier is **not comparable**
    with post-v0.96 ones, since the per-lane entry speed moves 0.556×–1.111× (Darkreach 171 → 95 m/s).
    Both numbers were already in the sidecar (`cornerSpeed` and `fbwCornerSpeed`), so an archived
    capture can be re-read to see which corner it flew — no new field, no new column.
    **v0.96 also added two extract-and-compile marker regions here** — **`FLEET-RESOLVE`**
    (`AirframeList` + `AirframeForLane`) and **`ENTRY-MARGINS`** (`StallMargin`/`VMaxMargin`) — both
    checked by `debugtests/test-fleet-resolve.py`.

    **v0.90.1 — the per-aircraft step runs once per FIXED STEP, not once per PILOT.** `OnPilotStep`
    stamps `Drone.LastStep` with `Time.fixedTime` and returns early if it has already run this step.
    `Aircraft.pilots` is an **array** (`Aircraft:60288` in the 0.34 decompile), every `Pilot` registers
    itself with `JobManager` in its own `Awake` (`Pilot:85535`), and `JobManager.PilotAeroInputs` walks
    that flat list calling `Pilot_OnAeroInputsApplied` on each one (`:168794`) — so a **two-seat
    airframe ran the whole body of this method twice per fixed step**. Measured in R26: `trainer` and
    `FastBomber1` flew a 6 s segment in 2.97 s and a 30 s segment in 14.95 s, against 5.97/29.95 for
    the single-seat `Fighter1`/`Multirole1`. The 2× card clock is the visible half; the damaging half
    is that the **control law was double-stepped inside one physics step** — integrators and rate
    filters advanced twice per `dt`, and every finite difference taken against a cached previous
    attitude (`rollRate = (t.up − _prevUp)/dt`) read **zero** on the second call, because nothing had
    moved between them. Two placements are load-bearing and both get "simplified" back into a bug:
    the guard sits **after** the `p.dead || p.ejected` despawn, so any seat's death still despawns it;
    and it is a **time stamp, not** the game's own `aircraft.pilots[0] == p` identity idiom
    (`Pilot:85536`, `:85645`) — a dead pilot returns `PartResult.Remove` and is dropped from
    `JobManager`'s list (`:168804`), so keying on seat 0 would silently stop ticking a drone whose
    front-seater was killed, and it could never reach the despawn either, since that check sits on the
    *invoking* pilot. The spawn log line now reports `ac.pilots.Length` as a crew count: seat count is
    prefab data with **no code-side definition anywhere in the decompile** (there is no `crew` field to
    read), so the log is the only place an operator can find out that `trainer` has two seats. The
    crewed path was never affected — the player's seam is `PilotPlayerState.PlayerAxisControls`, and
    there is one player state per player.
    **Lane spacing `LaneM` 2 km → 6 km**, same release, sized by the cards rather than by taste: a 360
    at the 72° bank clamp and the 250 m/s entry condition has radius `v²/(g·tan φ)` = 2.07 km, i.e. a
    4.1 km circle, so two neighbouring lanes flying the sustained-turn family swept **overlapping
    ground tracks** and only ever missed because the launch stagger put them at different points on it.
  - `PlayerSpawn.cs` — `PlayerSpawn` (v0.95, static). **The sandbox: one key puts the OPERATOR
    airborne**, so a law change can be hand-flown at the cards' entry condition without building a
    mission, taking off and climbing to it. `Cfg.SandboxKey` (default **F4**) calls one entry point,
    `Trigger()`, which never throws into the game loop and logs every refusal under a new
    **`[sandbox]`** prefix — same doctrine as `[drone]`/`[card]`: a key that appears to do nothing has
    to be explainable after the fact. Two cases, and the split is the design:
    **(A) already in an aircraft ⇒ PLACE it** at `SandboxAlt`/`SandboxSpeed`, wings level, **keeping
    current position and heading**. Nothing spawns and nothing is lost. This is
    `ScenarioPlayer.PlaceOnCondition` minus the card, minus the run anchor, minus the fuel write and
    minus the entry audit — a card needs every replicate in the *same place*, whereas a pilot wants to
    be where he was pointing, higher and faster; none of the rest is a measurement here. It does call
    `ChaseController.Forget(ac)`, for the same reason the card placement does.
    **(B) not in one ⇒ SPAWN one around you**, 500 m ahead of `Camera.main` on its flattened heading
    (the camera is the only thing that reliably exists when the operator is spectating or dead — the
    same fallback v0.90 gave the drone lanes), and the game seats you.
    **THE ONE THING TO KNOW BEFORE EDITING THIS FILE OR WRITING ANYTHING LIKE IT:** case A reuses
    `ScenarioPlayer.ResetGLoadTrackers` + `MoveAssembly` (made `internal static` in v0.95 for exactly
    this) rather than copying them. That pair **is** the safe-teleport primitive and both halves were
    learned by destroying the airframe — see the `ScenarioPlayer.cs` bullet below. **Anything that
    moves an aircraft must call both**; a second copy is a second chance to ship one half of it.
    Case B is `TestDrone.Spawn` with `player` and `HQ` filled in where the drone call passes `null`
    for both, and everything downstream is **the game's** — `Player.SetAircraft`, the pilot's player
    state (`SetStartingAiState` is skipped precisely *because* `player != null`, the mirror of what
    turns the drone AI off), the cockpit camera, HUD, map icon, throttle and gear. Do not hand-roll
    any of it; swapping while alive is supported because the game ejects the old airframe itself.
    **Why it is not in `TestDrone.cs`**: that file's load-bearing invariant is that an aircraft only
    enters its dictionary via `Spawn`, which asserts `ac.Player == null`, and a `player != null` spawn
    path sitting beside that assertion would be a trap. Nothing here touches the drone registry.
    Config lives in its own **`Sandbox`** section (`SandboxKey`/`SandboxAirframe`/`SandboxAlt`/
    `SandboxSpeed`) and deliberately shares nothing with `Drone`: overloading `DroneSpawnKey` would
    fire the batch launcher on the same press, and reusing `DroneSpawnAlt`/`DroneSpawnSpeed` would let
    setting up a hand-flight silently re-band the next batch. `SandboxAirframe` is the mod's **first
    `AcceptableValueList`** — so ConfigurationManager renders it as a **dropdown** — carrying the 13
    flyable jsonKeys, i.e. [`AIRFRAMES.md`](AIRFRAMES.md)'s 14 minus the event-only `UFO`; it is a
    single key, not a comma list, because it is one aircraft for one pilot. `SandboxSpeed` is
    **not** envelope-checked the way a drone lane is (v0.92 gates those pre-spawn): a pilot is not a
    batch, and refusing to place him would be more annoying than a slow acceleration.
  - `CameraPatches.cs` — `CockpitCameraPatch` + `CameraOrbitPatch` + `CameraSwitchStatePatch`.
- Project: `NuclearOption-MouseAim.csproj`. Target `netstandard2.1`, GUID `com.no.wtmouseaim`.
- **`cards/`** — the shipped test-card **grid** (JSON, no C#): the oblique small-step set, the
  AoA-ceiling pair, the sustained-sweep family at several rates/loadings, and the STOL-trainer and
  rotorcraft cards — i.e. the airframe/regime coverage the three built-ins leave open. Read
  [`cards/README.md`](cards/README.md) before adding one: it holds the grid table (card → what it
  isolates → pass/fail signal), the install command, the mirrored-pair rule, and the two rules that
  are easy to break — **one card = one test** (the reset is per CARD, not per segment) and **tags
  must be unique per card** (`compare-runs.py` keys segments by tag alone). Cards are copied into
  `<game>` by hand; the build never touches them.
  **v0.90 — a card carries its own run configuration** (`repeat`, `armToggle`, a `config` list of
  pinned knobs, and an `airframe`/`startAlt`/`startSpeed` the drone spawn now obeys); **v0.91 added
  the fleet** (`count`, and `airframe` as a comma list of jsonKeys, one per lane), so the operator
  ticks one checkbox and presses the spawn key with nothing left to hand-match in F1.
  **v0.93 added `startSpeedCorner`** — the entry speed as a multiple of *the lane airframe's own*
  corner speed, which is what lets one card be flyable by the whole roster AND enter every airframe at
  the same aerodynamic state rather than the same number. The eight `oblique-*-c` cards use it at
  `0.95`; reach for it instead of lowering a shared `startSpeed`, which re-bands every other lane at
  once. **v0.96 changed which corner speed it resolves against** (the FBW's, not the AI's — see the
  `TestDrone.cs` bullet), so corner-relative captures from R29 and earlier are **not comparable** with
  later ones.
  `cards/README.md` has the field table, a worked multi-airframe card, and the six rules
  `scorecard.py --selftest` enforces offline — nothing at runtime will, since the
  deserializer ignores what it cannot map and the apply path is fail-soft by design. **`airframe`
  holds jsonKeys or `""`, never prose**: every shipped card puts the description in
  `note` (the mod blanks the field at load, with a warning, if any comma-separated token contains
  whitespace — so an old prose card degrades rather than failing).
  **v0.96 — the grid is 31 cards, and the attribution set flies a FLEET.** New:
  **`oblique-above-c`**, the third arm of the belowness axis (`-c` only, no absolute twin) — the 6°
  diamond centred **20° above** the horizon, so `alignFracH` becomes a 3-point line (−20 / 0 / +20)
  with `oblique-6-c` and `oblique-below-c` instead of a pair. It is an **offset** of `oblique-below-c`,
  not a negation of its elevations (negating flips the diamond, so the arm would start at the bottom
  and `obDR` would move up), and it enters at 3000 m rather than 6000 so the climb gives comparable
  *mean* altitude and q to below-c's 3.3 km descent. The five `e*` attribution cards **no longer pin
  `"count": 1`** and, with both `alpha-*` cards, now name the **eight fixed-wing keys that clear the
  v0.92 gate at their absolute 250 m/s entry** (Fighter1, Multirole1, SmallFighter1, trainer,
  VTOLTrainer1, EW1, FastBomber1, Darkreach). `CAS1`, `COIN` and all three rotorcraft are excluded by
  **arithmetic** rather than shipped as guaranteed pre-spawn refusals. That is free because v0.94
  removed the arm scheduler's concurrency stand-down *and* because **wall clock is set by replicates
  per lane, not by lane count** — lanes fly concurrently, so eight airframes cost what one does
  (R28: 384 captures across 8 lanes in 30m14s). A card with a short airframe list is leaving
  measurement on the floor. Their entry stays **absolute** on purpose: converting to
  `startSpeedCorner` would change what each card measures, and `e1-below-control` only works as a
  control if it matches `e1-below-suppress` exactly. `cards/TOMORROW.md` is the campaign runbook
  (8 ordered batches, ~46 min, ~528 captures, with the pass/fail question for each).
- **[`AIRFRAMES.md`](AIRFRAMES.md)** — the airframe reference: all **14** real jsonKeys with
  Vstall/Vmax/corner/gLimit/turnR/mass, which of them have two seats, and the pre-spawn
  `Encyclopedia.Lookup` query. **Read it before writing an `airframe` list or a `startSpeed`**: the
  data lives in Unity ScriptableObjects inside `resources.assets` with no text file anywhere, so
  nothing else in the repo records it, and it is not guessable — `aircraftParameters.maxSpeed` is a
  *normalizer* that reads a flat 600 for every fast jet, `aircraftInfo` is km/h while
  `aircraftParameters` is m/s, and `emptyWeight` is shared template junk. It also states which shipped
  cards a given airframe cannot fly (the 250 m/s grid is out of reach for `CAS1`, `COIN` and every
  rotorcraft), and that `Attacker1` — long used as the doc example here — **does not exist**.

## Paths (all under `<game>`)
- Build reference DLLs: `<game>\NuclearOption_Data\Managed\` and `<game>\BepInEx\core\` — the latter
  falls back to the repo-local cache `.deps\BepInEx\core` (auto-downloaded) when `<game>` has no
  BepInEx installed. Both are resolved by `build/locate-game.ps1`, not hardcoded.
- Deploy target: `<game>\BepInEx\plugins\WTMouseAim\NuclearOption-MouseAim.dll`.
- BepInEx log (read after a flight): `<game>\BepInEx\LogOutput.log`.
- Live config: `<game>\BepInEx\config\com.no.wtmouseaim.cfg`.
- Test cards (M1): `<game>\BepInEx\config\wtmouseaim-cards\<name>.json` — recorded cards land here and
  are picked up at startup (one F1 checkbox each; the **file basename is the card id**). Built-in
  cards live in `ScenarioPlayer.cs`, not on disk. The repo's own grid lives in `cards/` and is
  **copied in by hand** (the build never touches `<game>`), then the game is restarted:
  `Copy-Item cards\*.json "<game>\BepInEx\config\wtmouseaim-cards" -Force`.

## Build / deploy / test loop
**Deploying IS part of testing.** A change is not "tested" — or even testable — until the DLL is
built AND copied into the BepInEx plugins folder; the source tree alone never runs in-game. So
after every code change do both steps below (don't stop at a green build), then have the user fly
it.
```
dotnet build NuclearOption-MouseAim.csproj -c Release          # expect 0 errors; MSB3277 warning is harmless
cp bin/Release/NuclearOption-MouseAim.dll "<game>/BepInEx/plugins/WTMouseAim/"   # game must be closed to overwrite
```
> **Local automation:** this repo's maintainer has git-ignored Claude Code hooks under `.claude/hooks/`
> that auto build+deploy on any `*.cs` edit. A fresh checkout has **no** hooks — run the two commands
> above manually (the DLL is locked while the game is running, so close it first).

## Debugging in-game
Diagnostics are **instrument-first** — the mod tells you what it did rather than you guessing:
- **Anomaly log.** When a commanded stick output looks wrong the mod writes one compact line to
  `LogOutput.log`. Grep it after a flight for `[anomaly]`, `[anomaly:trail]`, and `[maneuver]`.
  Leave `AnomalyLogging` **on**; it's cheap and it's the primary bug-report artifact.
- **Verbose trace.** `DebugLogging` dumps per-tick detail — very noisy; turn it on only when
  chasing a specific issue, off otherwise.
- **Offline recording tool.** `python debugtests/analyze-wobble.py <rec.csv>...` (stdlib-only) has
  two modes. **Default** scores any maneuver-recorder CSV for the death-wobble signature:
  oscillation episodes with frequency/amplitude/trend, roll-rail %, targetBank clamp %, and the
  bank-vs-command lag (built from the v0.51 investigation — see `WOBBLE-FINDINGS.md`).
  **`--digest <rec.csv>`** collapses the 900-row-ish capture into a ~30-line phase-segmented
  timeline (per segment: duration, the signals that moved, per-axis stick sign-flip counts, and any
  `# cfg` change / `[anomaly]` from the sibling `mouseaim-anomalies-<session>.log`). **To read a
  recording, run `--digest` first and only open raw rows for a segment it flags** — feeding raw CSV
  to an LLM is expensive and mostly steady-state redundancy. `--selftest` runs the in-memory asserts.
  Run this on user-reported recordings before theorizing. Past **10** captures `--digest` collapses
  one level further, to one line per file; `--verbose` keeps the full timelines.
  It also **exports `WOBBLE_SIGNALS`** (v0.96) — the per-signal oscillation dead-bands (bank 3.0,
  azErr 0.5, outR/outP/outY 0.05, aoa 2.0) — as the one definition, consumed by `scorecard.py`. The
  direction rule is the non-obvious bit and is worth stating: `scorecard.py` `exec_module()`s
  analyze-wobble (the hyphenated filename means no plain `import`), so the dependency runs
  **scorecard → analyze-wobble**, and anything shared between the two must be defined on the
  *analyze-wobble* side. Defining it in scorecard would close the cycle.
- **Batch-sized output (v0.90).** `scorecard.py`, `flightscore.py` and `analyze-wobble.py --digest`
  are all O(files) — at the 100-450 captures an unattended batch now writes, that is thousands of
  lines. All three suppress the per-file detail past **10** files and print only the roll-up /
  aggregate, with `--verbose` to force the old behaviour; at ≤10 files nothing changed. Read a big
  batch through `compare-runs.py --summary` (one line per card+segment) and open the full table only
  for what it points at.
- **Scoring a test-card run.** `python debugtests/scorecard.py <rec.csv>` segments by `segTag` and
  emits per-segment metrics (`--json`, `--selftest`). A segment sitting on a limit for ≥90% of its
  samples (bank clamp / turn-rate cap / blend rail / past the AoA ceiling) is flagged **RAILED** in
  `warnings` — its metrics cannot respond to a gain change, so read them as *no signal*, not as a
  score. A capture whose `dmgFrac` (column 65, v0.96) ever exceeds 0 is flagged **DAMAGED** in the
  same `warnings` channel — whole-capture, not per-segment (detachment is permanent, so a per-segment
  form would just repeat itself), naming the max ratio, the first segment and the `t` it appeared at.
  An absent column (every capture on disk predates it) and the −1 "could not read it" sentinel never
  warn. **An unrecognised tag prints a WARNING** —
  never ignore it: the tag vocabulary lives in `ScenarioPlayer.cs` **and in `cards/*.json`**, while the
  tag→metric table lives in `scorecard.py`, with no compile-time link between them. That pair silently
  drifted once already (v0.71: 19 of 21 segments scored as "unknown" with no output at all).
  **Adding or renaming a card segment means updating both, in the same change.** **Both halves are now
  checked (v0.96)**: disk cards by `scorecard.py --selftest` (which parses every file in `cards/` and
  asserts each tag resolves), and the **built-ins** by `check-architecture.py`, which scrapes every tag
  `ScenarioPlayer.cs` can emit and resolves it through `scorecard.infer_type`. That check found two
  built-in tags scoring as "unknown" on its first run: `rec` (`StopRecord`'s recorded-demand track) and
  `seg<i>` (`Validate`'s fallback for a disk card whose author left `tag` empty). `rec` now maps to
  `fine_track`; `seg\d+` maps to `untagged`, which gets the generic metrics and **deliberately no
  warning** — the *card* is what is underspecified, not the table, so telling the reader to add a rule
  would point at a rule that cannot exist.
- **Cross-batch index (SQLite).** **Read [`debugtests/CAPTURES-DB.md`](debugtests/CAPTURES-DB.md)
  BEFORE writing a query against it** — the column-by-column reference (type + provenance), the
  metric × segment-type matrix, the NULL idioms and a cookbook of 13 queries each verified against the
  live index. It exists because **every trap in this schema returns a plausible number rather than an
  error**: metrics are SPARSE by segment type, so a corpus-wide `avg(metric)` silently averages a
  handful of rows; the `n_cols` staircase (38/44/45/54/56/57/58/64/65) is how you filter by era; and
  the six `sc_*` raw-sidecar twins each have a right and a wrong side to join on. Always
  `select count(metric)` beside `avg(metric)`.
  `python debugtests/index-captures.py <game>/BepInEx` builds
  `debugtests/captures.db` — one row per capture, one per (capture, segment) — in ~30 s over the
  whole corpus, and re-runs in 0.2 s because it skips captures whose (mtime, size) are unchanged. It
  is where a question spanning batches lives: "does this effect hold in R28, R29 AND R30?" is one
  `GROUP BY c.run_tag`, not three tool runs stitched into prose. **Every metric in it comes from
  `scorecard.py`** — the module is imported and `score_run()` called, so the tag→metric table, the
  RAILED threshold and the unrecognised-tag rule stay in ONE place; re-index and the database
  follows. (`scorecard.is_railed(seg)` / `railed_metrics(seg)` exist for exactly this: the index
  needs the railed *predicate*, and matching on the warning prose would have been one reword away
  from silently marking a whole corpus un-railed.) What the index parses itself is only header text
  scorecard's `provenance()` skips (`# entry`, `# override`, `# drone`, `arm=`/`armKnob=`). Sidecar
  scalars land as `sc_*` columns and entry fields as `entry_*`, both added **dynamically** so a new
  field appears rather than disappears. Raw rows stay in CSV unless you ask (`--with-rows R30`, one
  batch at a time — all ~1.1M rows would be ~500 MB and mostly unread). `--archive <dir> --run R29`
  copies that batch's CSVs, sidecars and `LogOutput-R29.log` out of `<game>`: **do that after every
  batch**, because `LogOutput.log` is overwritten each session and R28's launch lines are already
  gone for good. `--selftest` runs on a
  synthetic capture with no game folder needed. The `.db` is gitignored — it is derived, and the
  CSVs are the source of truth.
  **Three orientation commands (v0.96), and `--stats` is the one to run first:**
  - `--stats` — totals, a per-batch table (mod version, captures, airframes, cards, aborts, `n_cols`,
    materialized rows), the `n_cols` era histogram, per-airframe counts and the parse-failure count.
  - `--check [RUNTAG]` — completeness. Per-(run, airframe) capture counts with an outlier and
    **STOPPED EARLY** flag, `rec` gaps **per session** (`rec` is a per-*process* counter, so a
    corpus-wide gap scan is meaningless), aborted captures with their stop reasons, parse warnings and
    unknown tags. With no RUNTAG it scans all 26 batches and prints only the flagged lanes. This is
    what catches a dead lane: R29's Darkreach flew **9** captures against 48 for every other lane, and
    that is invisible in every aggregate view.
  - `--diff RUNA RUNB [--metric M] [--tag T]` — per (airframe, card, tag): `mean ± stdev%` in both
    batches and the B/A ratio, railed and `arm` segments excluded, grouped the way `compare-runs.py`
    groups and never pooled across airframes.

  `--query "<sql>"` prints a table; four worked queries (rank airframes, compare batches, find railed
  cells, A/B by arm) are in the module docstring and thirteen more in `CAPTURES-DB.md`. Since v0.96 it
  is **read-only by default** (`file:…?mode=ro`; `--write` opts out) — the db costs ~30 s over 344 MB
  to rebuild and a mistyped query should not be able to spend that — and takes
  `--format table|csv|json` and `--limit` (default 1000, `0` = uncapped; truncation is a **loud stderr
  line**, never silent). A write attempt on the read-only handle is turned into a one-line refusal
  naming `--write`, not a traceback.
  **`stdev()` and `median()` are registered SQL aggregates** (SQLite ships neither), available in
  `--query` but **not** in a bare `sqlite3` shell. `stdev` is the **sample** (n−1) form, matched
  deliberately to `compare-runs.py`'s `statistics.stdev` so a SQL noise floor and a compare-runs table
  cannot disagree — the population form is 6.9% smaller at the n=8 the shipped grid flies, which would
  read as "the noise floor moved".
  **`--cards <dir>`** loads `cards/*.json` into `cards` / `card_airframes` dimension tables (card id =
  the file basename, so it joins straight to `captures.card`), which turns "which grid cells have we
  NEVER flown?" into a `LEFT JOIN`. Lanes are expanded only for cards whose `airframe` is a real
  jsonKey list, and `scorecard.card_setup_problems()` is the arbiter — not a second copy of the rule.
- **Cross-airframe flight quality.** `python debugtests/flightscore.py <rec.csv>...` answers one
  question per tick — *given what this airframe could physically do at that instant, was there a
  better way to get the nose where it was asked?* Every normalizer comes from the sibling
  `.airframe.json` probe plus live state (V, air density, velocity vector), **never a hand-tuned
  constant**, which is what makes a light jet, a loaded jet, a STOL trainer and a helo comparable —
  the offline mirror of the one-law rule. `--levers` prints the lever block (incl. `xfightPct`) even
  on old captures; `--json`, `--selftest`. **It also owns `opposed(r, y)`** — the ONE definition of a
  roll/yaw cross-fight (both channels clear of `STICK_DEADBAND`, opposite signs). flightscore owns it
  because it owns the constant and imports nothing but stdlib, so every other tool can reach it
  without a cycle; `gatechatter.rollYawAnti` and `scorecard.rollYawOpposedPct` **call** it rather than
  re-spelling it. Before v0.96 that predicate existed inline in three files against two spellings of
  the same 0.02 — three answers waiting to diverge on the next threshold tweak.
- **Self-referential feedback loops.** `python debugtests/loopaudit.py <rec.csv>...` asks
  GENERALITY-REVIEW finding 13's question — *can the command this term gates move this term?* — by
  recomputing `blendWeight`/`assist`/`coordPull` and inverting `bankTR` to recover `omegaDes`, so it
  can report what fraction of the demand chain actually **reaches** a control output, plus the
  `_pitchEff` self-probe latch, diagnosed from the recorded rate pair rather than inferred.
  `--settled 20` drops entry transients; `--json`, `--selftest` (the closed forms, no data needed).
  Write-up: `LOOP-AUDIT-FINDINGS.md`.
- **Gate chatter — CLOSED INVESTIGATION.** `python debugtests/gatechatter.py <rec.csv>...
  [--win 0.20] [--cone 0.2] [--json] [--perm 399] [--skip 0.0] [--bytag] [--selftest]`. Kept for
  reproduction only: its hypothesis was answered in v0.85 (`GATE-CHATTER-FINDINGS.md` §5a — the
  below-nose roll-to-align positive feedback loop) and fixed behind
  `BelowAlignSuppress`/`AlignRateLead`. Its durable half is `flightscore`'s `xfightPct`. **Do not
  reach for it to score a routine batch.**
- **Validating the test range.** `python debugtests/check-mission.py <mission.json>` checks a mission
  against the WTM-Range isolation/pinning invariants: no free-standing units, every faction HQ the map
  carries listed with its AI budget explicitly zeroed, at least one real airbase, weather/wind/
  time-of-day pinned, wreck cleanup wired. **Isolation is NOT an empty faction list** — that was this
  checker's own bug: `Mission.EnsureFactionExists` auto-inserts a default `MissionFaction` with
  `AIAircraftLimit = 6`, so `"factions": []` means "both factions, six AI aircraft each, deploying
  about five seconds in". An unpinned range crashes nothing; it quietly corrupts every score run
  against it. `--selftest`.
- **Comparing runs.** `python debugtests/compare-runs.py <rec1.csv> <rec2.csv> ...` reports
  per-segment spread across N runs — the noise floor, and the A/B of a law change. It **groups by
  (airframe, card, arm) and refuses to pool**, and excludes truncated segments rather than blending
  them; heed both warnings rather than working around them. The card is in the key because segment
  tags are unique per card **by convention only** and that already leaks (`hover`/`bobup` are shared
  by the rotor disk cards and the built-in `rotorcraft-v2`). **`--summary`** prints one line per
  (card, segment) — n, duration, worst rail, and three headline metrics as `mean +- stdev%` — which
  is the only readable form at ~40 card/tag pairs; scorecard's per-run warnings (incl. RAILED) are
  carried through, deduped with a count.
- **Uncrewed drones (v0.81; flying the real law since v0.87; self-configuring since v0.90, fleet and
  all since v0.91; **concurrent A/B since v0.94**).** The whole procedure is now: tick `Drone/DroneEnabled` in F1, tick **one** card in
  `Scenario Cards`, press the spawn key. The card supplies the airframe(s), altitude, speed, replicate
  count, A/B knob and — since v0.91 — **how many drones fly and what each one is**: `airframe` is a
  comma list indexed by lane and wrapping (`"Fighter1, Multirole1, SmallFighter1"` = a three-airframe
  fleet), and `count` defaults to the number of keys in it. **Keys come from
  [`AIRFRAMES.md`](AIRFRAMES.md)**, which is also where you check that every airframe in the list can
  actually fly the card's `startSpeed` — there are 14 real jsonKeys and an invented one costs a
  refused lane. **v0.93: a card can instead say `startSpeedCorner`**, an entry speed as a multiple of
  each lane airframe's own corner speed, which is the way to make one card flyable by a roster whose
  Vmax spans 141–479 m/s. So **nothing in F1 needs to match the
  card** — **do not hand-match the `Drone*`/`Scenario*` globals to it**; they are the fallback for a
  card that declares nothing, and hand-matching was the mismatch this removes (a mismatch does not
  refuse, it writes a capture that scores fine and answers a different question). Those drones launch
  `DroneStaggerSec` apart, each starts that card itself, flies it with the mod's control
  law, writes its own CSV (`d<N>-<airframe>` in the filename) and **despawns itself ~5 s after its
  card ends** — including if it was aborted, refused or never started. A mixed batch reads back
  correctly because `compare-runs.py` groups on the sidecar's `jsonKey` and refuses to pool across
  airframes: one row per airframe, which is what a fleet card is asking for. **An A/B no longer needs
  one drone (v0.94)** — the swept arm is per-aircraft state read through the controller, so every lane
  runs its own independent ABBA off its own queue index and a 10-airframe attribution batch is one
  launch instead of ten serial ones. Nothing writes the `Cfg` knob any more, so your own aircraft keeps
  flying whatever F1 says while the fleet sweeps around you. Everything it does is one grep:
  `[drone]` in `LogOutput.log` covers spawn/despawn (with the reason), every refusal (no server,
  unknown airframe key, **an airframe that cannot fly the card's entry speed — v0.92, checked
  pre-spawn off `Encyclopedia.Lookup`; the line carries the requested speed, the bound it violated
  and that bound's value**, no `Spawner`, the instructor declining to engage), a pilot killed or ejected,
  a drone the game removed under us, and `[drone] frame hitch` for any rendered frame over 50 ms. The
  launch line also names, item by item, whether the airframe/alt/speed/**drone count** came from the
  card or from F1 — read it, it is the only confirmation that the card drove the spawn. The **spawn** line carries the
  **crew count** (v0.90.1): every seat fires the pilot postfix independently, which double-stepped both
  the card clock and the control law until the `Time.fixedTime` guard in `OnPilotStep`, and seat count
  is prefab data with no code-side definition — that line is the only way to learn a `trainer` has two.
  `[card]` lines carry the
  card/segment progress for every aircraft flying one. **A refusal is always a log line, never a
  silent no-op** — the harness runs unattended, so a key that appears to do nothing has to be
  explainable after the fact. `TestDrone.FrameDt` (the fixed-step `Time.unscaledDeltaTime` sample) is
  the signal the stagger exists to defend against.
  **THE `sel[0]` RULE (know this before ticking two checkboxes).** Multi-card selection is supported
  and each drone flies the whole queue round-robin — but `airframe`, `count`, `repeat`, `armToggle`,
  `startAlt` and `startSpeed` are **all read off `sel[0]`** (`ScenarioPlayer.Preview` and `StartSuite`
  both take `sel[0]`; the spawn resolves ONE `Preflight` per batch) and applied to the entire launch.
  The trap: `Register` binds each card's checkbox with `builtIn` as its **default value**
  (`ScenarioPlayer.cs:497`) and `LoadCards` registers the built-ins **before** scanning disk, so on a
  fresh config `sel[0]` is `fixedwing-v2` — which declares no airframe/count/repeat/armToggle. The
  whole batch silently becomes one `Multirole1`, one replicate, no A/B, with the card you actually
  ticked flying second as a stimulus only. **Nothing refuses.** Compounding it: the spawn's `sel[0]` is
  the UNFILTERED one (`Preview` applies no `cls` filter, by design — it has no aircraft in hand) while
  `StartSuite` filters by class, so a ticked `rotorcraft-v2` can dictate the spawn while a `Plane` card
  is what flies. **`Scenario/ScenarioCardSet`** (an ordered comma list that overrides the checkboxes
  entirely) is the reliable selector.
- **Harness run board (v0.90).** With `DroneEnabled` on, a panel top-left shows either **PREFLIGHT**
  (what the spawn key *would* fly: card, replicate count, per-drone total time, and the airframe /
  altitude / speed / **drone count** each marked `[from card]` or `[from F1]` — v0.91 reads that count
  through `TestDrone.CountOf` rather than quoting `Cfg.DroneCount`, which would be wrong exactly when
  the card is driving, i.e. the case this panel exists for — plus the A/B knob, and an amber
  **NO CARD SELECTED** line, which is the commonest setup mistake and used to surface only as a log
  warning *after* N drones were airborne measuring nothing) or, once anything is flying, one line per
  aircraft with card, run *x*/*y*, arm, segment and tag, seconds left in the segment and in the card,
  and the recorder's sample count. It draws through `ShowOverlay` being off and through the operator
  having no aircraft, because that is the state you watch a batch from. Its two pieces of arithmetic
  live between the `BOARD-MATH` markers in `ScenarioPlayer.cs` and are checked by
  `python debugtests/test-board-math.py`, which extracts that region **verbatim**, compiles it with
  the .NET SDK and runs 23 cases — so it tests the shipped code, not a Python copy that would drift.
- **Hand-flying the law: the sandbox key (v0.95).** `Cfg.SandboxKey` (default **F4**, `Sandbox`
  section, read whether or not `DroneEnabled` is on) puts **you** airborne at
  `SandboxAlt`/`SandboxSpeed` — 4000 m / 250 m/s, i.e. the shipped grid's entry condition — so a
  law change can be *felt* without loading a mission, taking off and climbing to it. Already in an
  aircraft: it is placed there, wings level, over its current position and on its current heading,
  and nothing spawns. Not in one (spectating, ejected, on the ramp): a `SandboxAirframe` is spawned
  500 m ahead of the camera and the game seats you; pressing it again while alive swaps airframe (the
  game ejects the old one). Needs an active server, exactly like the drone spawn — SP is a host, so SP
  and hosting work and an MP client refuses. Everything it does is one grep: **`[sandbox]`** in
  `LogOutput.log`, alongside `[drone]` and `[card]`, covering the placement/spawn line (airframe, alt,
  speed, heading, crew count) and every refusal — no server, no `Spawner`, `Encyclopedia` not loaded,
  no local player, no faction HQ yet, an unresolvable `SandboxAirframe` key, `SpawnAircraft` returning
  nothing. Same doctrine as the harness: **a refusal is always a log line, never a silent no-op.**
  Two things it deliberately does *not* do: it is not envelope-checked (unlike a drone lane — see
  [`AIRFRAMES.md`](AIRFRAMES.md) if the speed matters for your airframe), and it writes **no capture** —
  it is a way to get to a state, not an instrument. Hit `RecordKey` afterwards if you want a CSV.
- **Concurrent A/B arms (v0.94).** `python debugtests/test-arm-schedule.py` — same trick again, on two
  regions at once: `ARM-SCHEDULE` in `ScenarioPlayer.cs` (the ABBA index) and `ARM-SEAM` in
  `ChaseController.cs` (the per-aircraft arm map). Asserts the sequence `0,1,1,0,0,1,1,0`, equal mean
  queue position at every multiple of 4 (and that `n=6` is equal-COUNT but unequal mean — the case that
  proves counting arms cannot detect an imbalance), the arm surviving a rebuilt controller, two
  aircraft on opposite arms at once, and per-aircraft clearing. Plus **five** source assertions the
  compiled region cannot make about itself: `ChaseController.Forget` must NOT clear the arm (the
  per-replicate reset calls it every run — clearing there silently un-sweeps the experiment), `For`
  must seed it, `ApplyArm` must never write the global `ConfigEntry`, all six lever sites must read
  through `Arm()`, and — **v0.96** — `LEVERS` must equal exactly the set of `ConfigEntry<bool>`
  declarations `Cfg.cs` marks `(A/B lever)`, so adding a sixth lever fails here until `LEVERS` /
  `LEVER_SITES` are updated with it. **Run it after touching the arm machinery or adding an A/B
  lever** — none of those five fails to compile, and all five produce a batch that scores fine and
  answers a different question.
- **Config-spec grammar (v0.96).** `python debugtests/test-spec-grammar.py` extracts `SplitSpec` from
  the `SPEC-GRAMMAR` markers in `ScenarioPlayer.cs`, compiles it, and runs 16 cases against **both**
  it and `scorecard.py`'s hand-written `split_spec` (which powers `card_setup_problems`) from one
  shared table. Run it after touching either. **One known divergence, deliberate and pinned by the
  test**: the C# splits on the *first* slash and accepts `"A/B/C"` as section `A` / key `B/C`; the
  Python copy refuses more than one slash. Neither is dangerous — the mod's lookup then finds no such
  entry and warns by name, fail-soft — and the stricter offline side is the more useful one, because
  it says so *before* the batch flies rather than after. See `LAW-CHARACTERIZATION.md` §7 for the
  one-line C# change that would collapse the two columns.
- **Fleet resolvers and the entry-speed gate (v0.96).** `python debugtests/test-fleet-resolve.py`
  compiles `ResolveCount`+`CountKeys` (`FLEET-RESOLVE` in `ScenarioPlayer.cs`) and
  `AirframeList`+`AirframeForLane` (`FLEET-RESOLVE`) + `StallMargin`/`VMaxMargin` (`ENTRY-MARGINS`,
  both `TestDrone.cs`) verbatim, then asserts the pair invariant `CountKeys(s) == len(AirframeList(s))`
  over a token table, lane wrapping, all three `ResolveCount` sources **with their `src` strings**,
  both `1..16` clamps, the card-list-beats-`Cfg`-wholesale rule, and the v0.92 margins against
  `AIRFRAMES.md`'s roster — including that `StallMargin` stays **below 1.20**, because `stol-*` at
  90 m/s on `SmallFighter1` is a ratio of exactly 1.200 (`270/3.6 == 75.0` exactly). Run it after
  touching the fleet resolvers or the entry-speed gate.
- **Card (de)serialisation (v0.90.1).** `python debugtests/test-card-model.py` does the same trick to
  the `CARD-MODEL` region of `ScenarioPlayer.cs`: extracts the three model classes verbatim, compiles
  them against the game's `Newtonsoft.Json.dll`, and round-trips **every file in `cards/`**. It exists
  because `UnityEngine.JsonUtility` silently dropped the `Seg[] segments` field in both directions —
  written cards had no `segments` key, read cards were rejected as "no segments", and **no disk card
  loaded at all from v0.71 to v0.90**. Nothing caught it because the built-in cards are constructed in
  C# and never touch a serializer, so every gate and batch went through the one path that could not
  fail. Run it after ANY change to the card model, and read `[card] N card(s) bound (… X from disk)`
  in the log as the in-game confirmation — `0 from disk` with files in the folder is the bug's shape.
  It also checks one **synthetic** card carrying the fields no shipped card uses — `config`, and since
  v0.91 a comma-list `airframe` and a non-zero `count` — because a field only the grid exercises is a
  field the round-trip stops covering the moment the grid stops using it. The airframe string is
  asserted byte-for-byte: `AirframeList` splits it per lane and `CountKeys` counts the same tokens, so
  a serializer that reformatted it would change both the fleet size and which lane flies what.
- **On-screen HUD.** `ShowDebugHud` reveals status / live stick command / anomaly+phase readouts
  (hidden by default). Use it to watch the control law react in real time while flying.
- **Live tuning without a rebuild.** With the BepInEx ConfigurationManager plugin installed, **F1**
  opens every `Cfg` knob in-game — change a gain, feel it immediately, then write the good value
  back into `Cfg.cs` defaults. Config is logged once at startup and again on each live edit (not
  per anomaly line).
- **In-flight keys:** the mod's hotkeys are all `Cfg` binds — for the current set + defaults, grep
  `Cfg.cs` for `ConfigEntry<KeyCode>`, or read the startup load-line in `LogOutput.log` (it logs
  every active binding). Don't hardcode the key list here; it drifts. (F1 = config and RMB =
  free-look aren't mod binds — F1 is ConfigurationManager's own key, RMB is the game's.)

## Decompiling the game (read-only reference)
The mod hooks the game's own classes, so before guessing at an API (FBW rate-command, AoA calc,
camera state machine, sign conventions) **read the real decompiled source**. Generate it once:
```
# ILSpy CLI — install once, then decompile the game assembly to C#
dotnet tool install -g ilspycmd
ilspycmd "<game>/NuclearOption_Data/Managed/Assembly-CSharp.dll" -o <somewhere>/decompiled
```
(Or open that same `Assembly-CSharp.dll` in the [ILSpy](https://github.com/icsharpcode/ILSpy) or
[dnSpy](https://github.com/dnSpy/dnSpy) GUI.) The classes worth reading: `Aircraft`,
`PilotPlayerState`, `ControlsFilter`, `RelaxedStabilityController`, `CameraCockpitState`,
`CameraOrbitState`, `CameraStateManager`, `CameraManager`, `CursorManager`, `Gun`. These are the
seams the mod patches or reads. Keep the decompiled output **outside** the repo (it's game code, not
redistributable — see `LICENSE`).

## Releasing (distinct from the test-deploy above)
The manual `dotnet build` + `cp` loop above is for **testing**. To **release** a version, use
[`release.ps1`](release.ps1). `PluginVersion` in `WTMouseAimPlugin.cs` is the **single source of
truth**: bump it, then run
```
./release.ps1 -Notes "short summary"      # add -Deploy to also copy into the local BepInEx folder
```
It commits pending changes, builds Release, tags `vX.Y.Z`, pushes branch + tag, creates the GitHub
Release with the DLL asset (`gh` CLI), then refreshes the NOMNOM manifest (`*.nomnom.json`)
version/downloadUrl/hash and commits that bump as a follow-up. After the first release is listed,
NOMNOM's hourly job auto-picks up later ones.

**Commit-then-build is load-bearing, don't reorder it.** The compiler stamps `SourceRevisionId`
from HEAD at build time, so building first ships a DLL that names the *previous* commit — which
breaks the one check NOMNOM policy clause 2.2 rests on (rebuild the tag, get the same binary).
Correspondingly the manifest bump lands *after* the tag: the tag must stay on the exact commit the
DLL was built from.
> **Agents can't run this:** `release.ps1` is PowerShell and drives `git push` + a GitHub release —
> outward-facing and hard to reverse. The agent's job is to bump `PluginVersion`, get a clean
> Release build, and let the **user** run `release.ps1` in a normal PowerShell window.

## Keeping the diagram current
[`ARCHITECTURE.md`](ARCHITECTURE.md) is treated as **code, not documentation**. A stale system map is
worse than none — it sends the next agent to the wrong file with confidence.

**The rule: a structural change updates the diagram in the SAME change.** Structural means any of —
- a `.cs` file added, removed, or renamed;
- a top-level type added or removed;
- a Harmony patch added, removed, or retargeted;
- a stage added, removed, or **reordered** in the `ChaseController.Apply` pipeline (the L1.3 diagram
  is ordered — reordering it silently is the easiest way to make the map lie);
- a new game type read by reflection (add it to the game-types table, note the fail-soft behaviour);
- a new artifact, sink, or offline tool.

**Verify before you hand back:**
```
python debugtests/check-architecture.py            # exit 1 on drift; run this after any code change
python debugtests/check-architecture.py --fix-version   # sync the ARCH-VERSION stamp after a version bump
python debugtests/check-architecture.py --selftest      # asserts on the parsers
```
Two automatic gates back this up, so it isn't only a matter of the agent remembering:
- **Stop hook** — `.claude/settings.json` (committed, so it applies in a fresh checkout too) runs the
  checker when an agent finishes a turn. On drift it exits 2, which feeds the problem list back to
  the agent to fix before handing back. It is silent when clean, and it checks at end-of-turn rather
  than on every edit so a multi-step refactor isn't nagged mid-flight.
- **Release gate** — `release.ps1` runs the same check before it builds, so a drifted diagram cannot
  ship. Bypass with `-SkipArchCheck` if you ever need to.

**What the checker checks BEYOND the diagram (v0.96).** It is no longer only a diagram checker — it
imports `scorecard.py` and reads method bodies out of `Cfg.cs` / `ScenarioPlayer.cs` / `TestDrone.cs` /
`WTMouseAimPlugin.cs` / `PlayerSpawn.cs`:
- **The built-in segment-tag vocabulary.** Every tag `ScenarioPlayer.cs` can emit (`tag = "…"`
  initialisers, `Hold(…)`/`Walk(…)` first args, and the two CONCATENATED sites `"seg" + i` and
  `"micro" + (i+1)`, probed with a `"1"` suffix) is resolved through `scorecard.infer_type`. It also
  asserts the `private static Seg X(…)` factory set is still exactly `{Hold, Walk}` — a third factory
  would carry tags the scan cannot see. This is the half `scorecard.py --selftest` could never cover,
  because that one scans `cards/*.json` only.
- **Seven source invariants that compile fine when broken.** `SampleFrameTime` called from `Update()`
  and **not** `FixedUpdate` (v0.92.1, R27's 223,899 identical rows); `OnPilotStep`'s
  `d.LastStep == Time.fixedTime` guard existing **and** sitting *after* the `p.dead || p.ejected`
  despawn (v0.90.1, R26); no file calling `MoveAssembly` without `ResetGLoadTrackers`; the
  `ApplyOverrides → ApplyArm → StartCard` order in `Tick` plus `RestoreOverrides` after `_rec.Stop` in
  **both** `Finish` and `NextCard`; every `.startSpeed` read routing through `ResolveStartSpeed`
  (v0.93; exempting the resolver itself and `Preview`'s deliberate pair-carry); `ForgetState` called
  from **both** `Despawn` and `PruneDead`; and `Spawn` still asserting `ac.Player == null`.
- **The CSV header/row lockstep**, and that CLAUDE.md's documented column count matches the code.

These greps assume the repo's 8-space method indentation. If a file is ever reformatted they degrade
to loud "X not found" problems, never to silent passes.

**What the checker still cannot see.** It verifies files/types/patches/version/tags/invariants — the
mechanical half. It cannot tell that an arrow now points the wrong way, that a signal was renamed, or
that a control law changed what it does. So: **after touching a subsystem, re-read that L1 section and
fix the prose too.** A green checker on a wrong diagram is the failure mode to avoid.

## Conventions
- **ONE control law for ALL airframes, at all loads and speeds — no per-plane tuning.** This is
  the core design requirement (maintainer, 2026-07-18). Every gain, schedule, and gate must key
  off (a) per-airframe parameters probed from the game's own components (the FBW/canard/helo
  probes — always fail-soft) and (b) live physical state (dynamic pressure, AoA, measured rates
  and effectiveness — loadout/mass shows up as achieved-vs-commanded discrepancy, never as a
  constant). A fix that only works because a constant suits one plane is wrong even if it fixes
  the report. Before shipping a control-law change, check it against: a light jet at high q, a
  loaded jet mushing near its alpha limit above corner speed, a low-limit STOL trainer, and a
  hovering helo. `GENERALITY-REVIEW.md` is the standing audit of the law against this rule —
  update it when a finding is fixed or a new one is discovered.
- **Keep this CLAUDE.md current in the same change.** When a change alters file structure, types,
  paths, the build/release flow, or a sign convention, update the matching section here as part of
  that change — the Layout/Paths sections are the agent's map, and stale notes cause wrong-file edits.
  The same standing rule applies to `ARCHITECTURE.md` (above) — CLAUDE.md is the *where*, that is
  the *how*; a change that alters structure usually touches both.
- **Suggest flight tests in your answer, don't file them.** A control-law / flight-model change is
  green-built but not confirmed until someone flies it — so when you ship one, end the response with
  the specific scenarios that would prove or break it: airframe + loadout, speed band, the maneuver,
  and what a pass vs. a failure looks like (name the signal, e.g. "no 0.5 Hz rail-to-rail pitch
  cycle", "AoA stays under the limiter"). Cite a comparable capture in `debugtests/` when one exists.
  Keep it to the few tests that actually discriminate; there is no tracking file to append to.
- Bump `PluginVersion` on every shipped change. The Awake load-line stays a SHORT one-liner
  (version + hotkeys + "see CHANGELOG.md") — version history goes in `CHANGELOG.md` only, never
  into the log string (it used to mirror the whole changelog; deliberately cut in v0.57).
  Commit messages: `vX.Y.Z — short summary` (see `git log`).
- Sign conventions in `Apply` (verify against the decompiled source before changing): `local` =
  `InverseTransformDirection(aimDir)`, x=right / y=up / z=forward. Nose-up = **negative**
  `ci.pitch`; positive `ci.roll` = roll right; positive `ci.yaw` = yaw right; `azErr` + =
  marker right of heading. `t.right.y` < 0 = right wing down.
- The game FBW reads pitch/yaw as a commanded **angular rate** (hence the fine integrator to kill
  steady-state residual). **There is no mod-side G-limiter and THE GAME HAS NO G GOVERNOR EITHER** —
  do not write one, and do not assume something downstream is catching G. This bullet used to say
  "the game's stability control governs"; that was **false**, corrected in v0.96 after R32
  (`debugtests/R32-FINDINGS.md` §1–§2). `ControlsFilter.GLimiter` is **dead code**: the identifier
  occurs exactly ONCE in the 181,878-line 0.34 decompile (`:65069`), as its own `protected class`
  declaration — no field of that type exists, nothing instantiates it, and its `LimitG(...)` (`:65104`)
  has zero call sites. What *does* exist is
  `targetPitchAngVel = pitch · gLimitPositive · 9.81 / max(V, 0.75·Vc)` (`:64859`) — a rate command
  *scaled by* a g-limit, with no feedback on achieved G. The mod reconstructs exactly that as
  `rpsRef`/`omegaMax`, which is a feed-forward cap on **demand**, never a governor on **outcome**.
  Two consequences that have already cost a batch:
  - **The FBW's alpha limiter is gated `if (num2 < 1f)` (`:64860`) — i.e. inactive above corner q,
    which is where every shipped card flies** (97.7% of R32's rows). The mod's own AoA block is the
    ONLY alpha protection in the loop at card speeds; there is nothing behind it.
  - **Over-G damages the PILOT, never the airframe.** `Pilot.TakeGForceDamage` (`:85779`) fires above
    20 g and applies `(sqrG − 400)·0.007` as `impactDamage` to one part index — the pilot's own. No
    structural-G path exists anywhere in the decompile. So "the law bent an airframe" is not a
    possible diagnosis; a high-G row is a *departed* airframe's readout, and clipping it would delete
    the most legible failure signal the corpus has. The standing decision (no mod-side G-limiter) is
    unchanged, but its justification is now the opposite of what it was: not "something else has it
    covered" but **"there is nothing to protect, and the number is evidence."**

## Local-only, not in a fresh checkout
These are git-ignored (machine-specific or work-in-progress) — mentioned so an agent knows what the
maintainer's tree has that yours won't:
- `.claude/hooks/`, `.claude/settings.local.json` — the auto build+deploy hooks and local deploy paths.
- `plans/` — design plans agreed but **not yet built** (parked "potential improvements"). Drop a new
  standalone markdown file here instead of starting code when an idea should be captured for later.
