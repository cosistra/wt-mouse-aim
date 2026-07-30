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
    the drone harness flying N at once needs N controllers. Only two things stay `static` and both
    say why in place: the Rewired player-0 cache (one input device per process) and the anomaly
    stream's index/flash fields + trail throttle (one log stream per process). `ChaseController.Player`
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
    v0.65 — see CHANGELOG); rotorcraft are force-flown through it too. v0.64 scales `pErrTerm` by the
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
    v0.69/0.70 added the instructor-loop instrumentation (64 CSV columns as of v0.86): alt/airDensity/pos/vel/
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
    v0.86 added `frameMs` (the rendered-frame time that fixed step saw, from `TestDrone.FrameDt`). The
    drone launch stagger exists *because* a frame hitch lands on whatever segment is running when it
    happens, so N replicates flying the same segment at that instant stop being independent samples —
    until now that was an assumption backed only by a `[drone] frame hitch` warning in a log nobody
    diffs. As a column it is per-row evidence, so a batch can drop or covary out the rows that were
    actually stalled. **The header/row lockstep is now checked**: `check-architecture.py` counts both
    and fails on a mismatch (and on CLAUDE.md's documented count drifting from the code).
    v0.90 added no column either (still **64**): it added the `# override` **header line**
    (`OverrideNote`, set by `ScenarioPlayer` from the card's `config` list just before `Toggle()`,
    written directly under `# card` because it only ever exists for one) listing the `Section/Key=value`
    knobs THAT CARD pinned for itself. Not a column on purpose: the value is constant for the whole
    capture by construction (pins go on before the recorder opens and come off after it closes), and
    it is not redundant with `# config` — that shows the live *values*, and what it cannot show is that
    the **card** chose them rather than the operator, which is what separates "this run was configured
    by its card" from "someone left a knob set". Sanitised on assignment like `EntryNote`; absent
    entirely for a hand-flown capture or a card that pins nothing.
  - `ScenarioPlayer.cs` — `ScenarioPlayer` (v0.71, milestone M1). **An instance class, ONE PER
    AIRCRAFT (v0.86)** — `For(aircraft)` / `Forget` / `Sweep` / `Player`, same registry as
    `ChaseController`. All *playback* state is per-instance (queue, segment index, segment clock,
    heading frame, anchor, placement audit, card-recording buffers); three things stay `static` and
    each says why in place: the **card library** (`_cards`/`_enable`/`_cf` — shared read-only config),
    the **on-screen notice** (one screen per process), and the **A/B arm schedule** (see below). The
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
    **The only place the mod writes aircraft PHYSICS state** (`PlaceOnCondition`). Two pairings
    there are mandatory and both were learned by destroying the airframe: (1) zero
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
    restoring it at suite end, and each capture self-identifies via `arm=`/`armKnob=` on its
    `# config` line (`arm=` parses out of `scorecard.py`'s existing `cfg_params()` regex unchanged).
    **v0.86 — the arm schedule stays `static`, and that is forced, not lazy.** The knob is a `Cfg`
    `ConfigEntry<bool>` that the control law reads **globally**, so N aircraft physically cannot fly
    different arms in the same instant. The invariant ABBA exists for is *both arms have the same mean
    position in the batch*, so a monotonic drift cancels — and the queue index is still exactly that,
    **because the schedule is only honoured while one aircraft is flying a card**. It has ONE owner
    (`_armOwner`, the suite that resolved it): a second suite neither resolves its own (it would save
    the first suite's already-written value as the "original") nor restores one on finish, and
    `ApplyArm` **stands the schedule down loudly** if another aircraft is mid-card rather than flipping
    a global knob under it. Both alternatives are worse: flipping mid-card silently mislabels part of
    the other capture, and "don't advance while anyone else flies" degenerates to arm A forever under
    a launch stagger. Concurrent A/B needs the swept knob to become per-aircraft state read through
    the controller instead of through `Cfg` — a change to how the law reads config, not to this
    scheduler.
    **v0.90 — A CARD IS THE WHOLE TEST, not just the stimulus.** `Card` gained `repeat`, `armToggle`
    and a generic `config` list of `{key, value}` overrides; `Preview()` reports what a run *would*
    fly. Every one of them falls back to the matching `Cfg` knob when absent, so a card that declares
    nothing behaves exactly as it did in v0.89 — which is what keeps the shipped grid and every ad-hoc
    recording valid. What to know before touching this:
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
    - **`Validate` heals a prose `airframe`.** The field was documentation until v0.90 gave it
      behaviour (the drone harness now SPAWNS it), so any `airframe` containing whitespace is blanked
      with a named warning and the launch falls back to `Cfg.DroneAirframe` — the pre-v0.90 behaviour.
      Human description goes in `note`, which the C# ignores by construction.
    - **`Preview()`/`Preflight` answer "what would fly?" with NO AIRCRAFT IN HAND**, because its
      caller (`TestDrone`) is choosing what metal to spawn. Hence no `cls` filter (that is a
      per-aircraft, post-spawn test — reading it as a spawn instruction would let `"Plane"` mean an
      airframe) and no replicate expansion. It **never throws**: it runs on a hotkey path before
      anything is spawned. `Preview(quiet: true)` exists for the run board's GUI poll — a repaint must
      not spam the log the way a keypress may.
    - **Run-board accessors** (`CardName`/`RunIndex`/`SegTag`/`ArmLabel`/`SegSecondsLeft`/… and the
      static `CollectRunning`) are field reads by design: `OnGUI` runs twice a frame, so nothing there
      may allocate or walk the queue. `IndexCard()` caches the segment durations and must follow every
      write to `_card`/`_qi`/`_queue`. The two non-trivial pieces of arithmetic live between the
      `// --- BOARD-MATH BEGIN/END ---` markers, in plain floats with no Unity types, because
      `debugtests/test-board-math.py` extracts that region verbatim and compiles it — keep them inside
      the markers and keep them SDK-compilable.
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
    heterogeneous (`"Multirole1, Attacker1"` with `DroneCount` 4 = two of each) and a single value
    behaves exactly as it did. An unknown `jsonKey` still refuses with a log line naming it: with one
    key that cancels the launch (the next lane fails identically), with a list only that lane is
    skipped. Each capture self-identifies — the `.airframe.json` sidecar's `jsonKey` is what
    `compare-runs.py` groups on and refuses to pool across, and the CSV filename carries it too.
    (v0.90: a card that names an `airframe` overrides **the whole list**, not one lane — the card is
    one test, and a batch flying it on a mix of airframes is not replicates of anything. A
    heterogeneous batch is still available: leave `airframe` out of the card and use the `Cfg` list.)
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
      airframe half way through — and `AirframeOf`/`AltOf`/`SpeedOf` take a `Preflight` as an argument
      so the run board can ask the same three questions of a fresh preview and get, by construction,
      what the launch will use. The launch log names **which source won for each value**; that line is
      the operator's only confirmation, since "4000 m" reads identically whether the card asked for it
      or a knob was left there. Empty / `<= 0` means "the card doesn't say" and the `Drone*` knob
      stands, which is what keeps a hand-configured launch working.
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
  pinned knobs, and an `airframe`/`startAlt`/`startSpeed` the drone spawn now obeys), so the operator
  ticks one checkbox instead of hand-matching five globals. `cards/README.md` has the field table and
  the four rules `scorecard.py --selftest` enforces offline — nothing at runtime will, since
  `JsonUtility` ignores what it cannot parse and the apply path is fail-soft by design. **`airframe`
  is a jsonKey or `""`, never prose**: all 16 shipped cards were migrated to put the description in
  `note` (the mod blanks a whitespace-bearing `airframe` at load with a warning, so an old card
  degrades rather than failing).

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
  score. **An unrecognised tag prints a WARNING** —
  never ignore it: the tag vocabulary lives in `ScenarioPlayer.cs` **and in `cards/*.json`**, while the
  tag→metric table lives in `scorecard.py`, with no compile-time link between them and no coverage
  from `check-architecture.py`. That pair silently drifted once already (v0.71: 19 of 21 segments
  scored as "unknown" with no output at all). **Adding or renaming a card segment means updating
  both, in the same change.** `scorecard.py --selftest` now parses every file in `cards/` and asserts
  each tag resolves — so for a *disk* card the drift check is automatic; a built-in still isn't.
- **Comparing runs.** `python debugtests/compare-runs.py <rec1.csv> <rec2.csv> ...` reports
  per-segment spread across N runs — the noise floor, and the A/B of a law change. It **groups by
  (airframe, card, arm) and refuses to pool**, and excludes truncated segments rather than blending
  them; heed both warnings rather than working around them. The card is in the key because segment
  tags are unique per card **by convention only** and that already leaks (`hover`/`bobup` are shared
  by the rotor disk cards and the built-in `rotorcraft-v2`). **`--summary`** prints one line per
  (card, segment) — n, duration, worst rail, and three headline metrics as `mean +- stdev%` — which
  is the only readable form at ~40 card/tag pairs; scorecard's per-run warnings (incl. RAILED) are
  carried through, deduped with a count.
- **Uncrewed drones (v0.81; flying the real law since v0.87; self-configuring since v0.90).** The
  whole procedure is now: tick `Drone/DroneEnabled` in F1, tick **one** card in `Scenario Cards`,
  press the spawn key. Since v0.90 the card supplies the airframe, altitude, speed, replicate count
  and A/B knob — **do not hand-match the `Drone*`/`Scenario*` globals to it**; they are the fallback
  for a card that declares nothing, and hand-matching was the mismatch this removes (a mismatch does
  not refuse, it writes a capture that scores fine and answers a different question). `DroneCount`
  drones launch `DroneStaggerSec` apart, each starts that card itself, flies it with the mod's control
  law, writes its own CSV (`d<N>-<airframe>` in the filename) and **despawns itself ~5 s after its
  card ends** — including if it was aborted, refused or never started. Everything it does is one grep:
  `[drone]` in `LogOutput.log` covers spawn/despawn (with the reason), every refusal (no server,
  unknown airframe key, no `Spawner`, the instructor declining to engage), a pilot killed or ejected,
  a drone the game removed under us, and `[drone] frame hitch` for any rendered frame over 50 ms. The
  launch line also names, item by item, whether the airframe/alt/speed came from the card or from F1 —
  read it, it is the only confirmation that the card drove the spawn. `[card]` lines carry the
  card/segment progress for every aircraft flying one. **A refusal is always a log line, never a
  silent no-op** — the harness runs unattended, so a key that appears to do nothing has to be
  explainable after the fact. `TestDrone.FrameDt` (the fixed-step `Time.unscaledDeltaTime` sample) is
  the signal the stagger exists to defend against.
- **Harness run board (v0.90).** With `DroneEnabled` on, a panel top-left shows either **PREFLIGHT**
  (what the spawn key *would* fly: card, replicate count, per-drone total time, and the airframe /
  altitude / speed each marked `[from card]` or `[from F1]`, plus the A/B knob — and an amber
  **NO CARD SELECTED** line, which is the commonest setup mistake and used to surface only as a log
  warning *after* N drones were airborne measuring nothing) or, once anything is flying, one line per
  aircraft with card, run *x*/*y*, arm, segment and tag, seconds left in the segment and in the card,
  and the recorder's sample count. It draws through `ShowOverlay` being off and through the operator
  having no aircraft, because that is the state you watch a batch from. Its two pieces of arithmetic
  live between the `BOARD-MATH` markers in `ScenarioPlayer.cs` and are checked by
  `python debugtests/test-board-math.py`, which extracts that region **verbatim**, compiles it with
  the .NET SDK and runs 23 cases — so it tests the shipped code, not a Python copy that would drift.
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

**What the checker cannot see.** It verifies files/types/patches/version — the mechanical half. It
cannot tell that an arrow now points the wrong way, that a signal was renamed, or that a control law
changed what it does. So: **after touching a subsystem, re-read that L1 section and fix the prose
too.** A green checker on a wrong diagram is the failure mode to avoid.

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
  steady-state residual). No mod-side G-limiter — the game's stability control governs.

## Local-only, not in a fresh checkout
These are git-ignored (machine-specific or work-in-progress) — mentioned so an agent knows what the
maintainer's tree has that yours won't:
- `.claude/hooks/`, `.claude/settings.local.json` — the auto build+deploy hooks and local deploy paths.
- `plans/` — design plans agreed but **not yet built** (parked "potential improvements"). Drop a new
  standalone markdown file here instead of starting code when an idea should be captured for later.
