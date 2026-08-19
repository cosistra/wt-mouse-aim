# WTM-Range test mission

Purpose-built, empty mission for the automated sweep harness. No units, pinned
weather/wind/time-of-day, wreck cleanup wired up so a multi-hour sweep doesn't accumulate corpses
forever.

> **Link repair 2026-08-02.** This paragraph used to cite `plans/instructor-feedback-loop.md` §5.1
> and `plans/research/research-D-batch.md` §8 for the isolation requirement and the schema
> rationale. **Neither file exists any more** (`plans/` is git-ignored and both were removed), so
> both links were dead. The rationale now lives in executable form instead — prefer it, it cannot
> rot the same way:
> - **`python debugtests/check-mission.py`** validates this file against the isolation/pinning
>   invariants and is the authority on what they are.
> - [`../../CLAUDE.md`](../../CLAUDE.md) → *Validating the test range* explains the one
>   counter-intuitive invariant: **isolation is NOT an empty faction list.**
>   `Mission.EnsureFactionExists` auto-inserts a default `MissionFaction` with `AIAircraftLimit = 6`,
>   so `"factions": []` means "both factions, six AI aircraft each, deploying about five seconds in."

## Install

The game only loads missions from `Application.persistentDataPath/Missions/<folder>/<name>.json`
— on Windows:
```
%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Missions\WTM-Range\WTM-Range.json
```
(`Shockfront` is the studio — confirmed from this machine's actual persistentDataPath, not
guessed.) Copy `WTM-Range.json` there, keeping the folder name and file name **both** exactly
`WTM-Range` — `UserGroup.GetJsonPath` builds the load path as `<Missions>/<name>/<name>.json` from
the bare name passed to `-mission`, so a mismatch (or a typo) fails to resolve and silently falls
back to the default `Free Flight` mission instead of erroring.

```powershell
$dest = "$env:USERPROFILE\AppData\LocalLow\Shockfront\NuclearOption\Missions\WTM-Range"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item harness\WTM-Range\WTM-Range.json $dest\WTM-Range.json -Force
```

No `meta.json` is needed alongside it — that file is written by the in-game "Save Mission" UI for
its own bookkeeping, but the load path this harness uses (`UserGroup.TryGetJson`, driven by
`-mission "WTM-Range"`) reads `WTM-Range.json` directly by folder+file name and never touches
metadata.

## Verify the game loads it

Boot straight into it — this is also the harness's own boot line (the `research-D-batch.md` §8.4
citation that stood here pointed at a file that no longer exists; the command itself is unchanged
and still correct):
```
NuclearOption.exe -autoHost -mission "WTM-Range" -state SinglePlayer -socket Offline -limitframerate 60
```
`-autoHost` defaults to multiplayer/UDP if you omit any of the other three flags — pass all four.

Then check `<game>\BepInEx\LogOutput.log` (or `Player.log` if running without BepInEx) for the
game's own load-confirmation line, `Loaded Mission with key:...` — it should name `WTM-Range`. A
silent fallback to `Free Flight` (the stock carrier/destroyer map) is the failure mode to watch
for: it means the folder/file name didn't resolve, and the "run" would actually be flying against
live threats instead of an isolated range.

Alternatively, open the in-game "Load Mission" browser from the main menu — user missions are
listed by folder name (`UserGroup.GetMissions()` just scans directory names under `Missions/`), so
`WTM-Range` should appear in that list.

Before trusting a sweep against the installed copy, re-run the validator against it directly:
```
python debugtests/check-mission.py "%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Missions\WTM-Range\WTM-Range.json"
```
