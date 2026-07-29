#!/usr/bin/env python3
"""Validate a mission JSON against the WTM-Range isolation/pinning invariants. Stdlib only.

The sweep harness (plans/instructor-feedback-loop.md S5.1) needs every batch to fly in an
identical, threat-free range: no other units to collide with or get shot by, no wind/weather/
time-of-day drift between runs, and wreck cleanup actually wired up so a multi-hour sweep doesn't
accumulate corpses forever. A silently unpinned or non-isolated range doesn't crash anything — it
just quietly corrupts every score run against it. This is the check that catches that before a
sweep starts.

Schema ground truth: plans/research/research-D-batch.md S8, cross-checked against the 0.34 decompile
AND against the real shipped "Free Flight - Ignus Archipelago" mission, which lives as plain-text
JSON inside NuclearOption_Data/resources.assets (built-in missions are classic Unity
Resources.LoadAll<TextAsset>("Missions"), not addressables, so they are greppable). The faction and
airbase names below are verbatim from that file, not reconstructed.

ISOLATION IS NOT AN EMPTY FACTION LIST — that was this checker's own bug, and it enforced it.
FactionHQ.OnMissionLoad runs for every faction HQ baked into the map (Terrain_naval always has
Boscali and Primeva), and Mission.EnsureFactionExists auto-inserts a DEFAULT MissionFaction for any
faction the JSON omits — with AIAircraftLimit = 6, not 0. So `"factions": []` doesn't mean "no
factions", it means "both factions, each free to deploy 6 AI aircraft", and FactionHQ.DeployAIAircraft
starts doing exactly that about 5 seconds in. Isolation is achieved by listing every HQ the map has
and zeroing its AI budget. An empty list also leaves the player with no faction to join and no
airbase to spawn from, which is the symptom that exposed this.

Checks:
  - no free-standing units present (vehicles, ships, buildings, scenery, containers, missiles,
    pilots all empty)
  - every faction HQ the map carries is listed, each with AIAircraftLimit / reduceAIPerFriendlyPlayer
    / addAIPerEnemyPlayer explicitly 0
  - at least one airbase, each naming a real built-in UniqueName for the map (an unknown name is NOT
    a load failure — Mission.SetupAirbase only logs and drops the entry — so this validator is the
    only thing between a typo and a wasted test flight)
  - exactly one playerControlled aircraft, in a faction the file actually lists, startingSpeed > 0
    (air start)
  - environment.timeFactor / weatherIntensity / windSpeed / windTurbulence / windRandomHeading all 0
  - missionSettings.allowRespawn true
  - missionSettings.wrecksMaxNumber > 0 AND wrecksDecayTime > 0 (both cleanup paths are dead if
    either is 0 — research-D S4.2)
  - an objective named exactly "Mission Start" exists, and the player aircraft has a savedLoadout

The last two are load-time hard requirements read straight out of the 0.34 decompile, not harness
policy, and both are silent-until-fatal — which is why they're here:
  - MissionObjectivesFactory.Load() errors "Mission must have objective with name Mission Start"
    and throws MissionLoadException; MissionManager.StartMission() catches it and tears the session
    down BEFORE spawning anything. Symptom in game is an empty map with nothing to fly.
  - SavedAircraft.savedLoadout has no field initializer, so an absent key deserializes to null and
    Spawner.TrySpawnAircraft() calls savedAircraft.savedLoadout.CreateLoadout(prefab)
    unconditionally -> NullReferenceException during the player's auto-spawn.

Usage:
    python debugtests/check-mission.py [path/to/mission.json]   # default: harness/WTM-Range/WTM-Range.json
    python debugtests/check-mission.py --selftest
"""

import argparse
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEFAULT_MISSION = REPO / "harness" / "WTM-Range" / "WTM-Range.json"

# Free-standing unit lists: these really must be empty. `factions` and `airbases` are NOT here —
# see the module docstring; emptying them is what broke the range.
EMPTY_LISTS = (
    "vehicles", "ships", "buildings", "scenery", "containers", "missiles", "pilots",
)

# Per-map ground truth, verbatim from the shipped Terrain_naval missions inside resources.assets.
# Keyed by MapKey.Path. A map that isn't listed here skips the faction/airbase name checks with a
# warning rather than guessing — a wrong airbase name fails silently in-game, so a guess is worse
# than no check.
MAP_FACTIONS = {"Terrain_naval": {"Boscali", "Primeva"}}
MAP_AIRBASES = {
    "Terrain_naval": {
        "Island5 Airstrip", "NE Airbase", "SE Airbase", "Island14 Airstrip",   # Boscali by default
        "NW Airbase", "Island7 Airstrip", "Island9 Airstrip", "SW Airbase",    # Primeva by default
    }
}
# Listed in every shipped mission as {"faction": "None", "Disabled": true} — not a usable spawn point.
DISABLED_AIRBASES = {"Terrain_naval": {"City Airport"}}

# The AI-budget fields. All three must be present AND zero: relying on the class defaults is the
# trap, since MissionFaction's own default AIAircraftLimit is 6.
AI_BUDGET_ZERO = ("AIAircraftLimit", "reduceAIPerFriendlyPlayer", "addAIPerEnemyPlayer")

# The pinned environment fields (plan S5.1 / S9): all must be exactly 0.
PINNED_ZERO_ENV = ("timeFactor", "weatherIntensity", "windSpeed", "windTurbulence", "windRandomHeading")

# MissionObjectivesFactory.MissionStartName — the game requires this exact objective name to load.
MISSION_START = "Mission Start"


def validate(mission) -> list:
    """Return human-readable problems for a parsed mission dict; [] means it's a valid range."""
    if not isinstance(mission, dict):
        return ["top level is not a JSON object"]

    problems = []

    for key in EMPTY_LISTS:
        val = mission.get(key)
        if val is None:
            problems.append(f"'{key}' is missing (must be an empty list)")
        elif val != []:
            n = len(val) if isinstance(val, list) else "?"
            problems.append(f"'{key}' is not empty ({n} entries) — isolation requires no other units/factions/airbases")

    map_path = (mission.get("MapKey") or {}).get("Path") if isinstance(mission.get("MapKey"), dict) else None
    known_factions = MAP_FACTIONS.get(map_path)
    known_airbases = MAP_AIRBASES.get(map_path)
    if known_factions is None:
        problems.append(
            f"MapKey.Path is {map_path!r}, which this checker has no ground truth for — faction and "
            f"airbase names cannot be verified (known: {sorted(MAP_FACTIONS)})"
        )

    factions = mission.get("factions")
    if not isinstance(factions, list) or not factions:
        problems.append(
            "'factions' is empty or missing — this does NOT mean 'no factions': the game auto-creates "
            "one per map HQ with AIAircraftLimit=6, so the range fills with AI and the player has no "
            "faction to join and no airbase to spawn from"
        )
        factions = []
    listed = {f.get("factionName") for f in factions if isinstance(f, dict)}
    if known_factions is not None:
        for missing in sorted(known_factions - listed):
            problems.append(
                f"faction {missing!r} exists on {map_path} but is not listed — it will be auto-created "
                "with AIAircraftLimit=6 and start deploying AI aircraft"
            )
    for f in factions:
        if not isinstance(f, dict):
            problems.append(f"'factions' entry is not an object: {f!r}")
            continue
        fname = f.get("factionName", "?")
        for key in AI_BUDGET_ZERO:
            if f.get(key) != 0:
                problems.append(
                    f"faction {fname!r}: {key} is {f.get(key)!r}, must be explicitly 0 "
                    "(omitting it is not the same — the class default is non-zero)"
                )

    airbases = mission.get("airbases")
    if not isinstance(airbases, list) or not airbases:
        problems.append("'airbases' is empty or missing — the player has nowhere to spawn from")
        airbases = []
    for b in airbases:
        if not isinstance(b, dict):
            problems.append(f"'airbases' entry is not an object: {b!r}")
            continue
        uname = b.get("UniqueName")
        if known_airbases is not None and uname not in known_airbases:
            disabled = DISABLED_AIRBASES.get(map_path, set())
            why = ("it is disabled by default on this map" if uname in disabled
                   else f"not a built-in {map_path} airbase (known: {sorted(known_airbases)})")
            problems.append(
                f"airbase {uname!r}: {why}. Mission.SetupAirbase only logs and DROPS an unresolved "
                "override, so this costs a test flight rather than failing at load"
            )
        if b.get("faction") not in listed:
            problems.append(f"airbase {uname!r}: faction {b.get('faction')!r} is not one of the listed factions {sorted(listed)}")
        if b.get("Disabled") is True:
            problems.append(f"airbase {uname!r}: Disabled is true — not a usable spawn point")

    aircraft = mission.get("aircraft")
    if not isinstance(aircraft, list):
        problems.append("'aircraft' is missing or not a list")
        aircraft = []
    player_craft = [a for a in aircraft if isinstance(a, dict) and a.get("playerControlled") is True]
    if len(player_craft) != 1:
        problems.append(f"expected exactly 1 playerControlled aircraft, found {len(player_craft)}")
    if len(aircraft) != len(player_craft):
        problems.append(
            f"'aircraft' has {len(aircraft)} entries but only {len(player_craft)} are playerControlled "
            "— a non-player aircraft is itself a unit and breaks isolation"
        )
    for a in player_craft:
        name = a.get("UniqueName", "?")
        speed = a.get("startingSpeed")
        if not isinstance(speed, (int, float)) or isinstance(speed, bool) or speed <= 0:
            problems.append(f"aircraft '{name}': startingSpeed is {speed!r}, must be > 0 (air start)")
        if a.get("faction") not in listed:
            problems.append(
                f"aircraft '{name}': faction is {a.get('faction')!r}, which is not one of the listed "
                f"factions {sorted(listed)} — a factionless playerControlled aircraft spawns neutral "
                "and uncontrollable, and no shipped mission does it"
            )
        if not isinstance(a.get("savedLoadout"), dict):
            problems.append(
                f"aircraft '{name}': savedLoadout is {a.get('savedLoadout')!r}, must be an object "
                '(e.g. {"Selected": []}) — the game dereferences it unconditionally when spawning'
            )

    env = mission.get("environment")
    if not isinstance(env, dict):
        problems.append("'environment' is missing or not an object")
        env = {}
    for key in PINNED_ZERO_ENV:
        val = env.get(key)
        if val != 0:
            problems.append(f"environment.{key} is {val!r}, must be 0 (pinning/isolation)")

    objectives = mission.get("objectives")
    if not isinstance(objectives, list):
        problems.append("'objectives' is missing or not a list")
        objectives = []
    if not any(isinstance(o, dict) and o.get("UniqueName") == MISSION_START for o in objectives):
        problems.append(
            f"no objective named {MISSION_START!r} — the mission will fail to load and nothing will "
            "spawn (MissionObjectivesFactory.Load throws; StartMission tears the session down)"
        )

    ms = mission.get("missionSettings")
    if not isinstance(ms, dict):
        problems.append("'missionSettings' is missing or not an object")
        ms = {}
    if ms.get("allowRespawn") is not True:
        problems.append(f"missionSettings.allowRespawn is {ms.get('allowRespawn')!r}, must be true (disarms defeat-on-death)")
    for key in ("wrecksMaxNumber", "wrecksDecayTime"):
        val = ms.get(key)
        if not isinstance(val, (int, float)) or isinstance(val, bool) or val <= 0:
            problems.append(f"missionSettings.{key} is {val!r}, must be > 0 (both cleanup paths are dead if either is 0)")

    return problems


def check_file(path: Path) -> int:
    if not path.exists():
        print(f"FAIL  no such file: {path}")
        return 1
    try:
        mission = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        print(f"FAIL  {path} is not valid JSON: {e}")
        return 1

    problems = validate(mission)
    if problems:
        print(f"FAIL  {path} violates {len(problems)} invariant(s):\n")
        for p in problems:
            print(f"  - {p}")
        return 1

    print(f"ok  {path} passes all isolation/pinning invariants")
    return 0


def _valid_mission() -> dict:
    m = {
        "MapKey": {"Type": "GameWorldPrefab", "Path": "Terrain_naval"},
        "factions": [{"factionName": f, **{k: 0 for k in AI_BUDGET_ZERO}} for f in sorted(MAP_FACTIONS["Terrain_naval"])],
        "airbases": [{"IsOverride": True, "faction": "Boscali", "UniqueName": "NE Airbase", "Disabled": False}],
        "aircraft": [{"playerControlled": True, "startingSpeed": 250.0, "faction": "Boscali", "UniqueName": "x",
                      "savedLoadout": {"Selected": []}}],
        "environment": {k: 0 for k in PINNED_ZERO_ENV},
        "missionSettings": {"allowRespawn": True, "wrecksMaxNumber": 4, "wrecksDecayTime": 0.2},
        "objectives": [{"Type": "None", "UniqueName": MISSION_START, "Hidden": True, "Outcomes": []}],
    }
    for key in EMPTY_LISTS:
        m[key] = []
    return m


def selftest() -> int:
    assert validate(_valid_mission()) == []
    assert validate("not a dict") == ["top level is not a JSON object"]

    m = _valid_mission(); m["ships"] = [{"type": "x"}]
    assert any("'ships'" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["aircraft"][0]["startingSpeed"] = 0
    assert any("startingSpeed" in p for p in validate(m))

    m = _valid_mission(); m["aircraft"][0]["startingSpeed"] = -5
    assert any("startingSpeed" in p for p in validate(m))

    m = _valid_mission(); m["aircraft"] = []
    assert any("playerControlled aircraft, found 0" in p for p in validate(m))

    m = _valid_mission(); m["aircraft"].append({"playerControlled": False, "startingSpeed": 200, "faction": "", "UniqueName": "y"})
    assert any("non-player aircraft" in p for p in validate(m))

    m = _valid_mission(); m["aircraft"][0]["faction"] = ""
    assert any("neutral and uncontrollable" in p for p in validate(m)), validate(m)

    # The isolation inversion: an empty factions list is a FAILURE now, not the invariant.
    m = _valid_mission(); m["factions"] = []
    assert any("auto-creates" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["factions"] = [f for f in m["factions"] if f["factionName"] != "Primeva"]
    assert any("Primeva" in p and "AIAircraftLimit=6" in p for p in validate(m)), validate(m)

    m = _valid_mission(); del m["factions"][0]["AIAircraftLimit"]
    assert any("AIAircraftLimit" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["factions"][0]["AIAircraftLimit"] = 6
    assert any("AIAircraftLimit" in p for p in validate(m))

    m = _valid_mission(); m["airbases"] = []
    assert any("nowhere to spawn" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["airbases"][0]["UniqueName"] = "Definitely Not An Airbase"
    assert any("not a built-in" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["airbases"][0]["UniqueName"] = "City Airport"
    assert any("disabled by default" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["airbases"][0]["faction"] = "Nobody"
    assert any("not one of the listed factions" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["MapKey"] = {"Path": "Terrain_desert"}
    assert any("no ground truth" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["environment"]["windTurbulence"] = 0.1
    assert any("windTurbulence" in p for p in validate(m))

    m = _valid_mission(); m["environment"]["timeFactor"] = 0.0  # float zero must still pass
    assert validate(m) == []

    m = _valid_mission(); m["missionSettings"]["allowRespawn"] = False
    assert any("allowRespawn" in p for p in validate(m))

    m = _valid_mission(); m["missionSettings"]["wrecksMaxNumber"] = 0
    assert any("wrecksMaxNumber" in p for p in validate(m))

    m = _valid_mission(); m["missionSettings"]["wrecksDecayTime"] = 0
    assert any("wrecksDecayTime" in p for p in validate(m))

    m = _valid_mission(); del m["vehicles"]
    assert any("'vehicles' is missing" in p for p in validate(m))

    # The two load-time hard requirements — both silent until the mission simply fails to start.
    m = _valid_mission(); m["objectives"] = []
    assert any("Mission Start" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["objectives"] = [{"Type": "None", "UniqueName": "Some Other Name"}]
    assert any("Mission Start" in p for p in validate(m)), validate(m)

    m = _valid_mission(); del m["objectives"]
    assert any("'objectives' is missing" in p for p in validate(m))

    m = _valid_mission(); del m["aircraft"][0]["savedLoadout"]
    assert any("savedLoadout" in p for p in validate(m)), validate(m)

    m = _valid_mission(); m["aircraft"][0]["savedLoadout"] = None
    assert any("savedLoadout" in p for p in validate(m))

    print("ok  selftest passed")
    return 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("path", nargs="?", default=str(DEFAULT_MISSION),
                     help="mission JSON to check (default: harness/WTM-Range/WTM-Range.json)")
    ap.add_argument("--selftest", action="store_true", help="run in-memory asserts on the validator")
    a = ap.parse_args()
    if a.selftest:
        sys.exit(selftest())
    sys.exit(check_file(Path(a.path)))
