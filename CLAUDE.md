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
  - `WTMouseAimPlugin.cs` — `WTMouseAimPlugin` (Awake/OnGUI overlay). Holds `PluginVersion`, the version SoT.
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
    Also holds
    `PilotPlayerStatePatch` (Harmony seam on `PilotPlayerState.PlayerAxisControls`).
  - `Recording.cs` — `ManeuverRecorder` + `AnomalyLog` (the log/recorder sinks ChaseController emits to).
    v0.69/0.70 added the instructor-loop instrumentation (63 CSV columns as of v0.85): alt/airDensity/pos/vel/
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
  - `ScenarioPlayer.cs` — `ScenarioPlayer` (v0.71, milestone M1). Scripted **test cards**: plays a
    card by writing `AimRig.SetAimForward` from the *seam prefix* (so `Apply` reads the demand the
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
    `Cfg.ScenarioArmToggle` names a bool knob the runner alternates **ABBA** by queue index
    (`((i+1)>>1)&1`) — never A×N then B×N, which is the pattern that turns drift into a fake effect —
    restoring it at suite end, and each capture self-identifies via `arm=`/`armKnob=` on its
    `# config` line (`arm=` parses out of `scorecard.py`'s existing `cfg_params()` regex unchanged).
  - `TestDrone.cs` — `TestDrone` + `Drone` + `TestDronePatch` (v0.81, **phase 1** of the uncrewed
    harness). Spawns aircraft nobody is sitting in, flies them, despawns them — **N alive at once**,
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
    while it is (the hotkeys are not even read; the postfix is one int compare). Phase 2 attaches
    `ChaseController` to `Drone.Fly` — unblocked in v0.82, when the controller became one instance
    per aircraft; the built-in level-hold there is a deliberately trivial
    altitude/wings hold and is **not** the mod's control law — never tune it or compare against it.
    Also the reason `WTMouseAimPlugin` now has a `FixedUpdate`: the launch stagger needs a fixed-step
    clock that exists before any drone does. **Both** removal paths (`Despawn` and `PruneDead`) call
    `ChaseController.Forget(d.AircraftId)` so the control state dies with the aircraft — keyed by the
    CACHED id for the same reason the dictionary is (the aircraft may already be destroyed).
  - `CameraPatches.cs` — `CockpitCameraPatch` + `CameraOrbitPatch` + `CameraSwitchStatePatch`.
- Project: `NuclearOption-MouseAim.csproj`. Target `netstandard2.1`, GUID `com.no.wtmouseaim`.

## Paths (all under `<game>`)
- Build reference DLLs: `<game>\NuclearOption_Data\Managed\` and `<game>\BepInEx\core\` — the latter
  falls back to the repo-local cache `.deps\BepInEx\core` (auto-downloaded) when `<game>` has no
  BepInEx installed. Both are resolved by `build/locate-game.ps1`, not hardcoded.
- Deploy target: `<game>\BepInEx\plugins\WTMouseAim\NuclearOption-MouseAim.dll`.
- BepInEx log (read after a flight): `<game>\BepInEx\LogOutput.log`.
- Live config: `<game>\BepInEx\config\com.no.wtmouseaim.cfg`.
- Test cards (M1): `<game>\BepInEx\config\wtmouseaim-cards\<name>.json` — recorded cards land here and
  are picked up at startup (one F1 checkbox each; the **file basename is the card id**). Built-in
  cards live in `ScenarioPlayer.cs`, not on disk.

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
  Run this on user-reported recordings before theorizing.
- **Scoring a test-card run.** `python debugtests/scorecard.py <rec.csv>` segments by `segTag` and
  emits per-segment metrics (`--json`, `--selftest`). **An unrecognised tag prints a WARNING** —
  never ignore it: the tag vocabulary lives in `ScenarioPlayer.cs` and the tag→metric table lives in
  `scorecard.py`, with no compile-time link between them and no coverage from `check-architecture.py`.
  That pair silently drifted once already (v0.71: 19 of 21 segments scored as "unknown" with no
  output at all). **Adding or renaming a card segment means updating both, in the same change.**
- **Comparing runs.** `python debugtests/compare-runs.py <rec1.csv> <rec2.csv> ...` reports
  per-segment spread across N runs — the noise floor, and the A/B of a law change. It **groups by
  airframe and refuses to pool**, and excludes truncated segments rather than blending them; heed
  both warnings rather than working around them.
- **Uncrewed drones (v0.81).** Tick `Drone/DroneEnabled` in F1, then the spawn key launches
  `DroneCount` drones `DroneStaggerSec` apart. Everything it does is one grep: `[drone]` in
  `LogOutput.log` covers spawn/despawn, every refusal (no server, unknown `DroneAirframe`, no
  `Spawner`), a drone the game removed under us, and `[drone] frame hitch` for any rendered frame over
  50 ms. **A refusal is always a log line, never a silent no-op** — the harness runs unattended, so a
  key that appears to do nothing has to be explainable after the fact. `TestDrone.FrameDt` (the
  fixed-step `Time.unscaledDeltaTime` sample) is the signal the stagger exists to defend against.
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
