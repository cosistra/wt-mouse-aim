#!/usr/bin/env python3
"""Check what the harness decides to FLY — by running the shipped C#, not a copy of it.

Three small resolvers between them answer "how many drones, and what is each one?", and a wrong
answer here does not refuse anything: it launches a batch that completes, scores cleanly, and
answers a different question.

  * `ScenarioPlayer.CountKeys`   — how many airframes a card's comma list names.
  * `ScenarioPlayer.ResolveCount`— the fleet size: the card's `count`, else CountKeys, else
                                   Cfg.DroneCount, clamped 1..16. The MIDDLE rule is the point —
                                   12 keys against a global 4 flies the first four lanes silently.
  * `TestDrone.AirframeList` / `AirframeForLane` — the lane assignment, wrapping.

CountKeys and AirframeList are a deliberate count-only / assignment PAIR (one runs from `Preview`
with no aircraft in hand, the other needs the harness), which is exactly the shape that drifts: the
first assertion below is that they count the same tokens over one table.

Also pinned: `TestDrone`'s v0.92 entry-speed margins, `StallMargin` / `VMaxMargin`. The floor is
1.10 and NOT the obvious 1.20 because the shipped grid's tightest legitimate pairing is `stol-*` at
90 m/s on `SmallFighter1`, whose Vstall is exactly 75.0 — a ratio of exactly 1.200, i.e. a 1.20
floor would decide a card AIRFRAMES.md calls flyable by the float rounding of `stallSpeed / 3.6`.
That is the kind of number someone rounds up during a tidy-up, so the roster arithmetic is asserted
against the constants as they are compiled, not against a Python copy of them.

Extracted verbatim from between the FLEET-RESOLVE / ENTRY-MARGINS markers and compiled — same trick,
and same reason, as `test-board-math.py` / `test-arm-schedule.py` / `test-spec-grammar.py`.

Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-fleet-resolve.py
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCEN = REPO / "ScenarioPlayer.cs"
DRONE = REPO / "TestDrone.cs"

# (airframe string, lanes it assigns). CountKeys must return len(lanes) for every one of them, and
# AirframeList must return exactly `lanes` — that equality IS the invariant, since the two are
# separate implementations of "the non-empty comma-separated tokens".
#
# The empty case is the asymmetric one and is deliberate: CountKeys returns 0 (= "the card names no
# airframes", which is what makes ResolveCount fall through to Cfg.DroneCount), while AirframeList
# returns a single empty lane so `Spawn` refuses it with its own log line rather than the launch
# dividing by zero.
LISTS = [
    ("Fighter1",                        ["Fighter1"]),
    ("Fighter1,Multirole1",             ["Fighter1", "Multirole1"]),
    ("Fighter1, Multirole1",            ["Fighter1", "Multirole1"]),   # space after the comma
    ("  Fighter1  ,  Multirole1  ",     ["Fighter1", "Multirole1"]),   # ...and around both
    ("Fighter1,,Multirole1",            ["Fighter1", "Multirole1"]),   # empty token dropped
    ("Fighter1,",                       ["Fighter1"]),                 # trailing comma
    (",Fighter1",                       ["Fighter1"]),                 # leading comma
    ("A,B,C,D,E,F,G,H,I,J,K,L",         list("ABCDEFGHIJKL")),         # 12 keys vs DroneCount 4
    ("Fighter1, Multirole1, SmallFighter1", ["Fighter1", "Multirole1", "SmallFighter1"]),
]
EMPTY_LISTS = ["", "   ", ",", " , , "]     # CountKeys 0; AirframeList one empty lane

# (card count, card airframe, Cfg.DroneCount, expected fleet size, expected source substring).
COUNTS = [
    (0,  "",                     4,  4,  "Cfg.DroneCount"),      # pre-v0.91 behaviour
    (0,  "Fighter1,Multirole1",  4,  2,  "airframe list"),       # THE MIDDLE RULE
    (8,  "Fighter1,Multirole1",  4,  8,  "count"),               # explicit multiple of the list
    (2,  "",                     4,  2,  "count"),
    (40, "Fighter1",             4,  16, "count"),               # clamped, not obeyed
    (0,  "A,B,C,D,E,F,G,H,I,J,K,L,M,N,O,P,Q", 4, 16, "airframe list"),   # ...and on the middle rule
    (-1, "Fighter1,Multirole1",  4,  2,  "airframe list"),       # count <= 0 is "unset"
    (0,  "",                     99, 16, "Cfg.DroneCount"),      # ...and on the global
    (0,  "",                     0,  1,  "Cfg.DroneCount"),      # low clamp
]

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>fleetresolve</AssemblyName>
    <RootNamespace>fleetresolve</RootNamespace>
  </PropertyGroup>
</Project>
"""

# Mathf/Cfg/Card are STUBBED below rather than banned: ResolveCount's clamp and its Cfg fallback are
# part of what is being tested, and AirframeList's card-vs-global choice is one call away. Everything
# genuinely of the game — an Aircraft, a Rigidbody, a ConfigEntry — must still be absent.
BANNED = ("UnityEngine", "Vector3", "Aircraft", "ConfigEntry", "BepInEx", "Encyclopedia")


def extract(path: Path, name: str) -> str:
    begin, end = f"// --- {name} BEGIN ---", f"// --- {name} END ---"
    src = path.read_text(encoding="utf-8")
    i, j = src.find(begin), src.find(end)
    if i < 0 or j < 0 or j < i:
        raise SystemExit(
            f"FAIL  could not find the {begin} / {end} markers in {path.name}. If the code moved, "
            f"move the markers with it — this check is the only thing verifying it."
        )
    body = src[i + len(begin):j]
    code = re.sub(r"//.*", "", body)
    for banned in BANNED:
        if re.search(rf"\b{re.escape(banned)}", code):
            raise SystemExit(
                f"FAIL  the {name} region in {path.name} now references {banned}; it must stay "
                f"compilable outside the game (Mathf / Cfg / Card are stubbed, nothing else)."
            )
    return body


def cs(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def program(scen: str, drone: str, margins: str) -> str:
    checks = []
    for s, lanes in LISTS:
        arr = ", ".join(cs(x) for x in lanes)
        checks.append(f"        List({cs(s)}, new[]{{{arr}}});")
    for s in EMPTY_LISTS:
        checks.append(f"        EmptyList({cs(s)});")
    for cnt, af, cfg, want, src in COUNTS:
        checks.append(f"        Count({cnt}, {cs(af)}, {cfg}, {want}, {cs(src)});")
    return f"""using System;

// --- stubs. Not the game: just enough for the two regions to compile unchanged. -----------------
internal static class Mathf
{{
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}}
internal sealed class Entry<T> {{ public T Value; }}
internal static class Cfg
{{
    public static Entry<int> DroneCount = new Entry<int>();
    public static Entry<string> DroneAirframe = new Entry<string>();
}}
internal sealed class Card
{{
    public string name = "c";
    public string airframe = "";
    public int count;
}}

// The shipped FLEET-RESOLVE region of ScenarioPlayer.cs, verbatim, plus accessor shims (the real
// members are `private static`, so the harness has to live in the same class).
internal static class Scen
{{
{scen}
    internal static int Count(Card c, out string src) => ResolveCount(c, out src);
    internal static int Keys(string s) => CountKeys(s);
}}

// The shipped FLEET-RESOLVE + ENTRY-MARGINS regions of TestDrone.cs, verbatim. `_plan` stands in for
// the batch's one resolved Preflight and `AirframeOf` for the card-beats-global choice, which is the
// only thing AirframeList reaches out to.
internal static class Drone
{{
    internal struct Preflight {{ public string Airframe; }}
    internal static Preflight _plan;
    internal static string AirframeOf(Preflight p) =>
        string.IsNullOrEmpty(p.Airframe) ? (Cfg.DroneAirframe.Value ?? "") : p.Airframe;

{drone}
{margins}
    internal static string[] List() => AirframeList();
    internal static string ForLane(int slot) => AirframeForLane(slot);
    internal static float Stall => StallMargin;
    internal static float VMax  => VMaxMargin;
}}

internal static class P
{{
    static int fails;
    static void Ok(bool cond, string what)
    {{
        if (!cond) {{ Console.WriteLine($"  FAIL {{what}}"); fails++; }}
    }}

    // THE PAIR INVARIANT: CountKeys counts exactly the tokens AirframeList hands to the lanes.
    static void List(string spec, string[] want)
    {{
        Drone._plan = new Drone.Preflight {{ Airframe = spec }};
        var got = Drone.List();
        Ok(string.Join("|", got) == string.Join("|", want),
           $"AirframeList('{{spec}}') = [{{string.Join(",", got)}}], want [{{string.Join(",", want)}}]");
        Ok(Scen.Keys(spec) == want.Length,
           $"CountKeys('{{spec}}') = {{Scen.Keys(spec)}}, want {{want.Length}} — CountKeys and "
           + "AirframeList must count the same tokens, or the fleet size and the lane assignment "
           + "come from two different readings of one string");
        // WRAPPING: lane k flies list[k % n], which is what makes `count` 8 over a 4-key list two
        // of each rather than four lanes and four refusals.
        for (int k = 0; k < want.Length * 2 + 1; k++)
            Ok(Drone.ForLane(k) == want[k % want.Length], $"AirframeForLane({{k}}) of '{{spec}}'");
    }}

    // The asymmetry, on purpose: 0 keys (so ResolveCount falls through to the global), but ONE lane
    // carrying the empty key, which Spawn refuses with its own log line.
    static void EmptyList(string spec)
    {{
        Drone._plan = new Drone.Preflight {{ Airframe = spec }};
        Cfg.DroneAirframe.Value = "";
        var got = Drone.List();
        Ok(got.Length == 1 && got[0] == "", $"AirframeList('{{spec}}') must be one empty lane, got {{got.Length}}");
        Ok(Scen.Keys(spec) == 0, $"CountKeys('{{spec}}') must be 0 (= the card names no airframe)");
    }}

    static void Count(int cardCount, string airframe, int cfgCount, int want, string wantSrc)
    {{
        Cfg.DroneCount.Value = cfgCount;
        var c = new Card {{ count = cardCount, airframe = airframe }};
        int got = Scen.Count(c, out string src);
        Ok(got == want, $"ResolveCount(count={{cardCount}}, '{{airframe}}', cfg={{cfgCount}}) = {{got}}, want {{want}}");
        Ok(src != null && src.Contains(wantSrc),
           $"ResolveCount source for (count={{cardCount}}, '{{airframe}}', cfg={{cfgCount}}) = '{{src}}', "
           + $"want it to name '{{wantSrc}}' — the launch log is the operator's only confirmation of "
           + "which of the three sources won");
    }}

    static int Main()
    {{
{chr(10).join(checks)}

        // No card at all: the pre-v0.91 path, and it must not throw on the null.
        Cfg.DroneCount.Value = 3;
        Ok(Scen.Count(null, out string ns) == 3 && ns.Contains("Cfg.DroneCount"), "ResolveCount(null)");

        // The card's list beats Cfg.DroneAirframe WHOLESALE — one test, one fleet definition.
        Cfg.DroneAirframe.Value = "COIN,COIN,COIN";
        Drone._plan = new Drone.Preflight {{ Airframe = "Fighter1,Multirole1" }};
        Ok(string.Join("|", Drone.List()) == "Fighter1|Multirole1",
           "a card's airframe list must replace Cfg.DroneAirframe entirely, not merge with it");
        Drone._plan = new Drone.Preflight {{ Airframe = "" }};
        Ok(string.Join("|", Drone.List()) == "COIN|COIN|COIN",
           "with no card airframe the Cfg list stands (the pre-v0.90 behaviour)");

        // --- v0.92 ENTRY-SPEED MARGINS, against AIRFRAMES.md's roster -------------------------
        // stol-* at 90 m/s on SmallFighter1: published stallSpeed 270 km/h, so Vstall is exactly
        // 75.0 and the ratio is exactly 1.200. THE reason the floor is 1.10 and not 1.20.
        float vsStol = 270f / 3.6f;
        Ok(Math.Abs(vsStol - 75f) < 1e-4f, $"SmallFighter1 Vstall should be 75.0 m/s, got {{vsStol}}");
        Ok(Math.Abs(90f / vsStol - 1.20f) < 1e-4f,
           "the stol pairing is a ratio of exactly 1.200 — that knife edge is the whole argument");
        Ok(Drone.Stall < 1.20f,
           $"StallMargin is {{Drone.Stall}}. A 1.20 floor decides the shipped stol-* card by the float "
           + "rounding of stallSpeed / 3.6 — that pairing is exactly 1.200. Keep it below 1.20.");
        Ok(90f >= vsStol * Drone.Stall,
           $"stol-* at 90 m/s on SmallFighter1 must CLEAR the {{Drone.Stall}}x Vstall floor "
           + $"({{vsStol * Drone.Stall}} m/s) — AIRFRAMES.md calls that pairing flyable");

        // Ceiling: 0.95 x Vmax, straight off AIRFRAMES.md's table in m/s (no /3.6 round trip here —
        // unlike the floor above, this bound is nowhere near a rounding knife edge). CAS1 must refuse
        // the 250 m/s grid AND its own 200 m/s published corner; Darkreach, the tightest airframe
        // that does fly the family at 0.895, must clear it.
        float vmaxCas = 205.6f, vmaxDark = 279.2f;
        Ok(250f > vmaxCas * Drone.VMax,
           $"CAS1 must refuse the 250 m/s grid ({{Drone.VMax}}x Vmax = {{vmaxCas * Drone.VMax}} m/s)");
        Ok(200f > vmaxCas * Drone.VMax,
           "CAS1 must also refuse its own 200 m/s published corner — the 1.00x startSpeedCorner case "
           + "AIRFRAMES.md calls out, and the reason 0.95x is the roster-wide multiple");
        Ok(250f <= vmaxDark * Drone.VMax,
           $"Darkreach must CLEAR the 250 m/s grid at {{Drone.VMax}}x Vmax ({{vmaxDark * Drone.VMax}} m/s) "
           + "— it is the tightest airframe that flies the shipped family");

        Console.WriteLine(fails == 0 ? "ok  fleet resolve + entry margins" : $"{{fails}} failure(s)");
        return fails == 0 ? 0 : 1;
    }}
}}
"""


def main() -> int:
    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    scen = extract(SCEN, "FLEET-RESOLVE")
    drone = extract(DRONE, "FLEET-RESOLVE")
    margins = extract(DRONE, "ENTRY-MARGINS")
    tmp = Path(tempfile.mkdtemp(prefix="fleetresolve-"))
    try:
        (tmp / "fleetresolve.csproj").write_text(PROJ, encoding="utf-8")
        (tmp / "Program.cs").write_text(program(scen, drone, margins), encoding="utf-8")
        r = subprocess.run(
            ["dotnet", "run", "--project", str(tmp), "-v", "quiet", "--nologo"],
            capture_output=True, text=True,
        )
        out = (r.stdout or "") + (r.stderr or "")
        print(out.strip())
        if r.returncode != 0 and "FAIL" not in out and "failure" not in out:
            print(f"FAIL  the generated project did not build/run (exit {r.returncode}).")
        return r.returncode
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
