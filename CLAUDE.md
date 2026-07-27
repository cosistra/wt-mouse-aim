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
  - `ChaseController.cs` — `ChaseController` (the control law in `Apply`, nested
    `AnFrame`; also `DetectAnomalies`/`TrackManeuver`, the v0.55 FBW probe — per-airframe
    stick→pitch-rate params read from the game's `ControlsFilter.FlyByWire`, fail-soft — and the
    v0.57 canard probe/`InvertCanardRemap` — undoes the KR-67's `RelaxedStabilityController`
    pitch remap, fail-soft — and the v0.58 helo probe/`ResolveHelo` — reads the private
    `heloFlyByWire` of `HeloControlsFilter` + tilt/nozzle archetype components to rate-normalize
    rotorcraft commands and drive the hover regime by tilt angle, fail-soft; rotorcraft are
    force-flown with EvolvedLegacy — and the v0.59 AoA-utilization demand schedule/recovery
    bias — folds live AoA vs the probed alpha ceiling into `qSched`, gates `FineGainBoost`,
    and adds a restoring pitch past the ceiling; the loaded-jet pitch-relay fix). **ONE fixed-wing
    law now: `ApplyEvolvedLegacy`** (the Unified A/B alternative + its enum/hotkey were removed in
    v0.65 — see CHANGELOG); rotorcraft are force-flown through it too. v0.64 scales `pErrTerm` by the
    measured `_pitchEff` estimator; **v0.65 C1** reversal-gates that floor (below `revThresh` the floor
    is dropped so a reversed plant stops being forced), and **v0.65 B2** injects a bounded, V-independent,
    marker-stationary-gated micro-bank in the sub-0.5° azimuth cone so a high-q residual settles on a
    gentle coordinated turn (gate `_settleOK` from the aim-direction angular rate; new recorder column
    `settleOn`). v0.61 Track A (shared) gated the eAlign anti-relay slew to the dead-astern wrap region
    and decoupled the azTR presence gate from the predFloor. `Legacy`/`BankToTurn` removed in v0.60;
    `BankToTurnVmin` renamed `BankSpeedFloor` (shared bank airspeed floor). Also holds
    `PilotPlayerStatePatch` (Harmony seam on `PilotPlayerState.PlayerAxisControls`).
  - `Recording.cs` — `ManeuverRecorder` + `AnomalyLog` (the log/recorder sinks ChaseController emits to).
  - `CameraPatches.cs` — `CockpitCameraPatch` + `CameraOrbitPatch` + `CameraSwitchStatePatch`.
- Project: `NuclearOption-MouseAim.csproj`. Target `netstandard2.1`, GUID `com.no.wtmouseaim`.

## Paths (all under `<game>`)
- Build reference DLLs: `<game>\NuclearOption_Data\Managed\` and `<game>\BepInEx\core\` — the latter
  falls back to the repo-local cache `.deps\BepInEx\core` (auto-downloaded) when `<game>` has no
  BepInEx installed. Both are resolved by `build/locate-game.ps1`, not hardcoded.
- Deploy target: `<game>\BepInEx\plugins\WTMouseAim\NuclearOption-MouseAim.dll`.
- BepInEx log (read after a flight): `<game>\BepInEx\LogOutput.log`.
- Live config: `<game>\BepInEx\config\com.no.wtmouseaim.cfg`.

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
