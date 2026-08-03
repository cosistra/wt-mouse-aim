# Dev notes — WT Mouse Aim

User-facing docs are in [README.md](README.md). **This file is the map for Claude Code / any coding
agent working in this repo**: where code lives, how to build/deploy/test, and the conventions.
Committed on purpose — a fresh checkout should be enough to get productive.

## READ A SLICE, NOT A FILE

**This is a rule, not a suggestion, and it exists for a measured reason.** The standing docs in this
repo total ~500 KB. An agent that opened the five it is usually pointed at spent **~85k tokens before
doing any work**, then spent more re-reading sections to edit them. One batch-analysis run cost ~300k
tokens, almost none of it on the 563 MB SQLite corpus — the prose was the cost.

So every large doc here now starts with an **index** that maps topic → section. The protocol:

1. **Read the index at the top of the doc** (cheap — a screenful).
2. **Read only the section you need**, by `Read` with `offset`/`limit`, or by grepping the heading.
3. **Never read a large doc end-to-end** unless you are rewriting it.

| doc | size | how to slice it |
|---|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | ~190 KB | index at top → one `L1.x` subsystem section |
| [`LAW-LEDGER.md`](LAW-LEDGER.md) | ~101 KB | **summary table at top** → fetch findings by ID (`X27`, `H7`, …) |
| [`LAW-CHARACTERIZATION.md`](LAW-CHARACTERIZATION.md) | ~60 KB | §-index at top; **§7 is the numbered backlog** and has its own item index |
| [`GENERALITY-REVIEW.md`](GENERALITY-REVIEW.md) | ~44 KB | finding index at top → one finding |
| [`debugtests/CAPTURES-DB.md`](debugtests/CAPTURES-DB.md) | ~46 KB | index at top → schema, or the metric matrix, or the batch index |
| [`debugtests/TOOLS.md`](debugtests/TOOLS.md) | ~41 KB | tool table at top → the one tool you are running |
| [`CHANGELOG.md`](CHANGELOG.md) | ~274 KB | **never read whole** — grep for a version or a symbol |

`grep` already does most of this; there is deliberately no query helper to learn.

## The other standing docs, and what each is authoritative for

- **[`ORIENTATION.md`](ORIENTATION.md)** — start here if you know nothing about this repo. The
  doc-by-doc reading order and the distilled failure modes of this project.
- **[`ARCHITECTURE.md`](ARCHITECTURE.md)** — the system diagram: how it *works*. This file tells you
  where code *lives*; that one tells you how it behaves and why. **You are required to keep it
  current — see [Keeping the diagram current](#keeping-the-diagram-current).**
- **[`LAW-CHARACTERIZATION.md`](LAW-CHARACTERIZATION.md)** — the standing test plan: what to fly, in
  what order, and why. Read it before proposing an experiment; most obvious ones are already
  scheduled, and several would currently measure a railed actuator rather than the control law.
  **§7 is the durable backlog.**
- **[`LAW-LEDGER.md`](LAW-LEDGER.md)** — the arbiter of what we are entitled to believe:
  ESTABLISHED / PLAUSIBLE / REFUTED / OPEN, one citation per line. It carries the
  instrument-validation record (gates A–D, at `I1`–`I3`) and every per-batch finding the repo has.
  **Do not build on a claim that is not in ESTABLISHED**, and read the corpus-wide invalidations in
  its header before quoting any pre-R40 number.

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

Mod code is **one file per concern**, all at the repo root, single namespace `NuclearOptionMouseAim`.
The `.csproj` (`Microsoft.NET.Sdk`) globs every `*.cs` automatically — no project edits when adding or
splitting a file. Project: `NuclearOption-MouseAim.csproj`, target `netstandard2.1`, GUID
`com.no.wtmouseaim`.

**This table is the map. The behaviour is in `ARCHITECTURE.md`** — follow the link rather than
duplicating it here; that file is kept honest by `debugtests/check-architecture.py`, this one is not.

| file | types | owns | how it works |
|---|---|---|---|
| `WTMouseAimPlugin.cs` | `WTMouseAimPlugin` | BepInEx entry point, `Awake`/`OnGUI` overlay, hotkeys, `PluginVersion` (the version SoT), session id, and the `FixedUpdate` that drives `TestDrone.FixedTick`. Also draws the harness run board | [L1.6 `plugin`](ARCHITECTURE.md#plugin--the-harness-run-board) |
| `Cfg.cs` | `Cfg`, `ConfigurationManagerAttributes` | every config bind + the F1 metadata. **The live list of hotkeys and knobs lives here, not in this file** — grep it for `ConfigEntry<KeyCode>` | [L1.7 `cfg`](ARCHITECTURE.md#l17--cfg-configuration--live-tuning) |
| `AimRig.cs` | `AimRig`, `Guards` | the world-locked aim marker + Win32 raw mouse; `Guards` answers "should the mod be passive right now" | [L1.2 `aim_rig`](ARCHITECTURE.md#l12--aim_rig-the-world-locked-marker) |
| `ChaseController.cs` | `ChaseController`, `PilotPlayerStatePatch`, `PilotThrottlePatch` | **the control law** (`Apply`), the per-airframe probes, the A/B arm seam, and the two Harmony seams on `PilotPlayerState`. **One instance per aircraft** — `ChaseController.For(ac)`, never `new` | [L1.3 `chase`](ARCHITECTURE.md#l13--chase-the-instructor-the-heart-of-the-mod) |
| `Recording.cs` | `ManeuverRecorder`, `AnomalyLog` | the instrumentation sinks the law emits to: the CSV capture (72 CSV columns as of v1.0.0) and the anomaly log. One recorder per aircraft | [L1.5 `telem`](ARCHITECTURE.md#l15--telem-the-instrumentation-loop) |
| `ScenarioPlayer.cs` | `ScenarioPlayer` | test-card playback + card recording; owns the **safe-teleport primitive**, the per-card config pins and the A/B arm schedule. One instance per aircraft | [L1.6 `scenario`](ARCHITECTURE.md#scenario--playback-placement-cards-and-arms) |
| `TestDrone.cs` | `TestDrone`, `Drone`, `TestDronePatch` | the uncrewed harness: spawn · fly · despawn, N lanes at once on a ring, plus the pre-spawn entry gates | [L1.6 `drone`](ARCHITECTURE.md#drone--spawn-lanes-and-the-entry-gates) |
| `PlayerSpawn.cs` | `PlayerSpawn` | the sandbox: one key puts the **operator** airborne to hand-fly the law with no mission built | [L1.6 `sandbox`](ARCHITECTURE.md#sandbox--hand-flying-the-law) |
| `CameraPatches.cs` | `CockpitCameraPatch`, `CameraOrbitPatch`, `CameraSwitchStatePatch` | the view follows the marker, in cockpit and orbit | [L1.4 `campatch`](ARCHITECTURE.md#l14--campatch-camera-follow) |

**Before touching aircraft placement**, read the `MoveAssembly`
[graveyard](ARCHITECTURE.md#v0972--moveassembly-is-a-rigid-transform-and-nothing-else-the-graveyard):
two fixes have been tried there and both were reverted, one of which destroyed the aircraft on 32 of
32 placements. `check-architecture.py` enforces the result as an anti-invariant.

### Data and docs beside the code
| path | what it is | read it before |
|---|---|---|
| `cards/` + [`cards/README.md`](cards/README.md) | the shipped test-card **grid** (JSON, no C#) — the field table, the `sel[0]` rule, the launch procedure, and the rules `scorecard.py --selftest` enforces | adding or flying a card |
| [`AIRFRAMES.md`](AIRFRAMES.md) | the 14 real jsonKeys with Vstall/Vmax/corner/gLimit/mass, and six traps in the underlying fields. **Nothing else in the repo records this** | writing any `airframe` list or `startSpeed` |
| [`debugtests/CAPTURES-DB.md`](debugtests/CAPTURES-DB.md) | column-by-column reference for `captures.db`, plus the batch index | writing any SQL |
| [`debugtests/TOOLS.md`](debugtests/TOOLS.md) | every offline tool: what it answers, when to run it | running any `debugtests/*.py` |
| `harness/WTM-Range/` | the isolated test mission (no units, pinned weather/wind/time) | changing the test range |

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

Diagnostics are **instrument-first** — the mod tells you what it did rather than you guessing.

- **Anomaly log.** When a commanded stick output looks wrong the mod writes one compact line to
  `LogOutput.log`. Grep it after a flight for `[anomaly]`, `[anomaly:trail]` and `[maneuver]`.
  Leave `AnomalyLogging` **on**; it's cheap and it's the primary bug-report artifact.
- **Verbose trace.** `DebugLogging` dumps per-tick detail — very noisy; turn it on only when chasing
  a specific issue, off otherwise.
- **On-screen HUD.** `ShowDebugHud` reveals status / live stick command / anomaly+phase readouts
  (hidden by default). Use it to watch the control law react in real time while flying.
- **Live tuning without a rebuild.** With the BepInEx ConfigurationManager plugin installed, **F1**
  opens every `Cfg` knob in-game — change a gain, feel it immediately, then write the good value back
  into `Cfg.cs` defaults. Config is logged once at startup and again on each live edit (not per
  anomaly line).
- **In-flight keys:** the mod's hotkeys are all `Cfg` binds — for the current set + defaults, grep
  `Cfg.cs` for `ConfigEntry<KeyCode>`, or read the startup load-line in `LogOutput.log` (it logs every
  active binding). Don't hardcode the key list here; it drifts. (F1 = config and RMB = free-look
  aren't mod binds — F1 is ConfigurationManager's own key, RMB is the game's.)

**Everything offline — the analysers, the scorers, the SQLite corpus, the drone-harness procedure —
is in [`debugtests/TOOLS.md`](debugtests/TOOLS.md).** It has a tool-by-tool index at the top; read the
row you need, not the file. The three you will want first:

```
python debugtests/check-card.py cards/*.json     # BEFORE flying — the cheapest check in the repo
python debugtests/analyze-wobble.py --digest <rec.csv>   # what happened in one capture
python debugtests/scorecard.py <rec.csv>         # per-segment metrics
```

> **Read a capture with `--digest` first and only open raw rows for a segment it flags.** Feeding raw
> CSV to an LLM is expensive and mostly steady-state redundancy.

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

**Navigate it with `debugtests/index-decompiled.py` (v0.97.1), don't grep a 182k-line file.** Stdlib
only; cold parse 1.15 s, warm **0.03 s** off a cache written *beside the decompile* (never in the repo —
`--json` warns loudly if you aim it inside) and keyed on `(path, mtime, size, PARSER_VERSION)`. Source
resolves from the positional arg, else `$NUCLEAR_OPTION_DECOMPILE`, else the **0.34.1** default
`E:/Downloads/modNO/decompiled-0341/Assembly-CSharp.decompiled.cs`.
```
python debugtests/index-decompiled.py --at 74349        # REVERSE a :NNNNN citation — the repo's own idiom
python debugtests/index-decompiled.py --type AeroPart   # fuzzy; lists candidates rather than guessing
python debugtests/index-decompiled.py --member partLookup   # exact, falls back to substring
python debugtests/index-decompiled.py --grep 'Detach|Attach' --list --selftest
```
It covers **1,655 types / 23,032 members / 0 skipped declarations** (braces balanced, frame stack
empty), and `--selftest` re-derives 16 known-good citations, so a bad `:NNNNN` in this repo fails a
check instead of sending the next agent to the wrong line. Rough on purpose: base-vs-interface is an
`^I[A-Z]` heuristic, explicit impls keep their qualifier, field `type` is head-minus-modifiers.
**`--member` is the one to reach for on an inherited field** — `partLookup` is declared on `Unit`, so
`--type Aircraft` will never list it.

**The per-class tree at `E:/Downloads/modNO/decompiled/` is STALE — delete it, don't read it.** 27
classes from Jun–Jul, pre-0.34: 14 still match exactly, **12 have drifted** (`Aircraft` alone is
+10/−18 members) and **`CameraManager` no longer exists as a type at all**. Half-right is the worst
state a reference can be in, because the half that is right is what makes you trust the half that
isn't. Every `:NNNNN` in this repo indexes the single **0.34.1** monolith above and nothing else —
the whole repo was swept from 0.34 to 0.34.1 line numbers in v0.97.2, so a citation in an old
CHANGELOG entry or findings doc resolves against the current decompile like any other.

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
- **Ten source invariants that compile fine when broken.** `SampleFrameTime` called from `Update()`
  and **not** `FixedUpdate` (v0.92.1, R27's 223,899 identical rows); `OnPilotStep`'s
  `d.LastStep == Time.fixedTime` guard existing **and** sitting *after* the `p.dead || p.ejected`
  despawn (v0.90.1, R26); no file calling `MoveAssembly` without `ResetGLoadTrackers`; **`MoveAssembly`
  containing NO `Transform` write at all, and exactly one `Physics.SyncTransforms` as its last
  statement (v0.97.2 — an ANTI-invariant, so it fires on `.position`/`.rotation`/`.eulerAngles` on
  an `xform`/`transform`, on `.localPosition`/`.localRotation`/`SetPositionAndRotation`, and on
  `.Repair()`. Rigidbody writes deliberately do NOT match: moving bodies is what the function is
  for, and MIXING the two schemes is what cost R36 32 of 32 placements)**; the
  `ApplyOverrides → ApplyArm → StartCard` order in `Tick` plus `RestoreOverrides` after `_rec.Stop` in
  **both** `Finish` and `NextCard`; every `.startSpeed` read routing through `ResolveStartSpeed`
  (v0.93; exempting the resolver itself and `Preview`'s deliberate pair-carry); `ForgetState` called
  from **both** `Despawn` and `PruneDead`; and `Spawn` still asserting `ac.Player == null`.
  **v0.99.1 adds two.** (9) **the card pins stay refcounted** — only `PinShared`/`UnpinShared` may
  write a pinned entry's `BoxedValue`, `ApplyOverrides` keeps its acquire-once `_ovEntries != null`
  guard, `RestoreOverrides` keeps the `== null` early return *and* nulls the list (together those are
  what make a double release a no-op rather than a decrement of a count another aircraft still holds),
  and `ScenarioPlayer.Forget(int)` aborts **before** `_byAc.Remove`. (10) **the per-replicate reset** —
  since a non-fatal abort hands over to `NextCard`, every `_field =` that `Finish` resets must also be
  reset by `NextCard`, minus a named per-RUN allowlist (`_card`, `_queue`, `_qi`, `_armEntry`,
  `_armIdx`, `_anchorSet`, `_aborted`), plus `Abort` still taking its `fatal` flag. Ledger #12's
  shape: adding a field to one teardown and not the other is silent.
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
- **A batch analysis UPDATES a standing doc. Never mint a new `R##-*.md`, `SESSION-*.md` or
  `*-FINDINGS.md`.** That habit produced ~25 files that went stale faster than they could be
  maintained, disagreed with each other and with the code, and were consolidated away on 2026-08-02.
  There is a fixed home for every kind of finding:
  - a claim you can now believe, or must stop believing → **`LAW-LEDGER.md`** (ESTABLISHED /
    PLAUSIBLE / **REFUTED** / OPEN — one line, with batch, n and effect size). It is the single home
    for findings.
  - an open action item → **`LAW-CHARACTERIZATION.md` §7**, the durable backlog.
  - a ONE-LAW violation (a constant that should be a probe) → **`GENERALITY-REVIEW.md`**.
  - a ranked weakness, or a hypothesis to stop re-proposing → **`LAW-WEAKNESS-MAP.md`**.
  - a schema / metric / SQL trap → **`debugtests/CAPTURES-DB.md`**.
  - a card's validity verdict → that card's own `note` field, plus `cards/README.md`.
  - what shipped → **`CHANGELOG.md`** (append-only).

  Then add one row to the **batch index** in `debugtests/CAPTURES-DB.md` naming the run tag and where
  its conclusions went, and archive the raw captures. The ledger line is the finding; the CSVs are the
  evidence; nothing in between needs to exist. A batch that changed no standing doc is a result — say
  so in one line. **Deleting a doc is fine; deleting the only record of why something is the way it is
  is not** — move the fact first, then delete.
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
  (`LAW-LEDGER.md` P1–P3). `ControlsFilter.GLimiter` is **dead code**: the identifier
  occurs exactly ONCE in the 181,878-line 0.34 decompile (`:65242`), as its own `protected class`
  declaration — no field of that type exists, nothing instantiates it, and its `LimitG(...)` (`:65277`)
  has zero call sites. What *does* exist is
  `targetPitchAngVel = pitch · gLimitPositive · 9.81 / max(V, 0.75·Vc)` (`:65032`) — a rate command
  *scaled by* a g-limit, with no feedback on achieved G. The mod reconstructs exactly that as
  `rpsRef`/`omegaMax`, which is a feed-forward cap on **demand**, never a governor on **outcome**.
  Two consequences that have already cost a batch:
  - **The FBW's alpha limiter is gated `if (num2 < 1f)` (`:65033`) — i.e. inactive above corner q,
    which is where every shipped card flies** (97.7% of R32's rows). The mod's own AoA block is the
    ONLY alpha protection in the loop at card speeds; there is nothing behind it.
  - **Over-G damages the PILOT, never the airframe.** `Pilot.TakeGForceDamage` (`:85989`) fires above
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
