#!/usr/bin/env python3
"""Check THE OWNERSHIP RULE — by running the shipped C#, not a copy of it.

    A card's declared value beats the F1 value, for every parameter that decides what a run
    measures. If the card says nothing, and only then, the live Cfg value stands.

That rule is what makes a card a repeatable experiment instead of a stimulus whose meaning depends
on whatever the config file happened to hold. Two field failures are why it is a test and not a
convention:

  * `hs-hold` was designed around `Drone/DroneAltDeckM = 3000` for its dynamic-pressure contrast.
    The live config held 0. Nothing refused — the batch would have measured a different experiment
    and scored fine, and the operator was told to hand-edit F1 instead.
  * batch R41's entire rotorcraft verdict was withdrawn because `Control/HeliForwardSpeed` and
    `Control/HeliHoverSpeed` sat at stale v0.43 values that no card declared and no artifact
    recorded.

WHAT IS COMPILED HERE, verbatim, out of the shipped source:

  * `ScenarioPlayer.DeclaredText / DeclaredFloat / DeclaredBool`  (CARD-OWNS)      — the resolver
  * `ScenarioPlayer.SplitSpec`                                     (SPEC-GRAMMAR)  — the one grammar
  * `TestDrone.DeckSpreadM / StaggerSec`                           (CARD-OWNS-SPAWN) — the two
    PRE-SPAWN call sites, which are the ones that were broken: a card's `config[]` pins are applied
    when the card STARTS, and the fleet is laid out before that, so these cannot read the pin and
    must read the card.

The sibling checks and where the line is:
  * `check-card.py` CHECK 6 — does each SHIPPED CARD declare the inventory? (the JSON half)
  * this file          — does the HARNESS honour a declaration once made? (the C# half)
Both are needed: a card that declares nothing and a resolver that ignores what it declares fail the
same way, and neither check sees the other's half.

Same extract-and-compile trick, and same reason, as test-board-math.py / test-fleet-resolve.py.
Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-card-owns.py
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

# Nothing of the game may appear in an extracted region, or it cannot be compiled outside it.
# Mathf / Cfg / CfgOverride are stubbed below — the clamp and the Cfg fallback are part of what is
# being tested, so banning them would ban the property.
BANNED = ("UnityEngine", "Vector3", "Aircraft", "ConfigEntry", "BepInEx", "Encyclopedia")

# (card config as (key, value) pairs, spec asked for, Cfg fallback, expected value).
# The FIRST GROUP is the property itself: declared beats the live value, every type, every spelling.
FLOAT_CASES = [
    # the hs-hold defect, both directions: the card wins whether it asks for more or for less
    ([("Drone/DroneAltDeckM", "3000")], "Drone/DroneAltDeckM", 0.0, 3000.0),
    ([("Drone/DroneAltDeckM", "0")],    "Drone/DroneAltDeckM", 3000.0, 0.0),
    # ...and a card that says NOTHING still gets the live value. This is the half that keeps every
    # pre-v1.0.2 card and every ad-hoc spawn behaving exactly as it did.
    ([],                                "Drone/DroneAltDeckM", 3000.0, 3000.0),
    ([("Scenario/ScenarioThrottle", "0.40")], "Drone/DroneAltDeckM", 3000.0, 3000.0),
    # THE GRAMMAR IS SHARED, so a bare key and a Section/Key spelling of the same entry match. A
    # card writing "HeliHoverSpeed" means Control/HeliHoverSpeed, exactly as armToggle does.
    ([("HeliHoverSpeed", "35")],         "Control/HeliHoverSpeed", 20.0, 35.0),
    ([("Control/HeliHoverSpeed", "35")], "HeliHoverSpeed",         20.0, 35.0),
    ([("Control/HeliForwardSpeed", "80")], "Control/HeliHoverSpeed", 20.0, 20.0),  # near-miss key
    # INVARIANT CULTURE (v1.0.1's rule): a card file travels, and "0.40" must not become 40 on a
    # machine whose decimal separator is a comma. The harness pins the thread culture below.
    ([("Drone/DroneStaggerSec", "0.5")], "Drone/DroneStaggerSec", 3.0, 0.5),
    ([("Drone/DroneStaggerSec", " 12 ")], "Drone/DroneStaggerSec", 3.0, 12.0),   # whitespace
    # FAIL-SOFT. An unparseable literal falls back to the live value rather than throwing on a
    # hotkey path; ApplyOverrides prints the one warning for the same string a moment later.
    ([("Drone/DroneStaggerSec", "wibble")], "Drone/DroneStaggerSec", 3.0, 3.0),
    ([("Drone/DroneStaggerSec", "")],       "Drone/DroneStaggerSec", 3.0, 3.0),
    # Malformed keys are not matches: "A/B/C" is a typo, not section A key "B/C".
    ([("A/B/C", "9")], "A/B/C", 3.0, 3.0),
    ([("", "9")],      "Drone/DroneStaggerSec", 3.0, 3.0),
    # First wins on a duplicate — the same "first value stands" rule PinShared uses.
    ([("Drone/DroneStaggerSec", "5"), ("Drone/DroneStaggerSec", "9")],
     "Drone/DroneStaggerSec", 3.0, 5.0),
]

BOOL_CASES = [
    ([("Scenario/ScenarioForceEntry", "false")], "Scenario/ScenarioForceEntry", True, False),
    ([("Scenario/ScenarioForceEntry", "true")],  "Scenario/ScenarioForceEntry", False, True),
    ([("Scenario/ScenarioForceEntry", "True")],  "Scenario/ScenarioForceEntry", False, True),
    ([],                                          "Scenario/ScenarioForceEntry", True, True),
    ([("Scenario/ScenarioForceEntry", "yes")],   "Scenario/ScenarioForceEntry", True, True),  # soft
]

# THE PRE-SPAWN CALL SITES, which is where the rule was actually broken. (config, Cfg value, want).
# Negative clamps to 0 on both, exactly as the C# does — the Cfg range forbids it, but the CARD path
# has no range to trust.
DECK_CASES = [
    ([("Drone/DroneAltDeckM", "3000")], 0.0, 3000.0),      # hs-hold: card 3000 vs live 0
    ([("Drone/DroneAltDeckM", "0")], 3000.0, 0.0),
    ([], 3000.0, 3000.0),
    ([("Drone/DroneAltDeckM", "-500")], 3000.0, 0.0),
]
STAG_CASES = [
    ([("Drone/DroneStaggerSec", "8")], 3.0, 8.0),
    ([], 3.0, 3.0),
    ([("Drone/DroneStaggerSec", "-1")], 3.0, 0.0),
]

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>cardowns</AssemblyName>
    <RootNamespace>cardowns</RootNamespace>
  </PropertyGroup>
</Project>
"""


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
                f"compilable outside the game (Mathf / Cfg / CfgOverride are stubbed, nothing else)."
            )
    return body


def cs(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def cfg_array(pairs) -> str:
    if not pairs:
        return "new CfgOverride[0]"
    items = ", ".join(f"new CfgOverride {{ key = {cs(k)}, value = {cs(v)} }}" for k, v in pairs)
    return "new[]{ " + items + " }"


def program(scen: str, spawn: str) -> str:
    checks = []
    for cfg, spec, fb, want in FLOAT_CASES:
        checks.append(f"        F({cfg_array(cfg)}, {cs(spec)}, {fb}f, {want}f);")
    for cfg, spec, fb, want in BOOL_CASES:
        checks.append(f"        B({cfg_array(cfg)}, {cs(spec)}, {str(fb).lower()}, "
                      f"{str(want).lower()});")
    for cfg, live, want in DECK_CASES:
        checks.append(f"        Deck({cfg_array(cfg)}, {live}f, {want}f);")
    for cfg, live, want in STAG_CASES:
        checks.append(f"        Stag({cfg_array(cfg)}, {live}f, {want}f);")
    return f"""using System;
using System.Globalization;

// --- stubs. Not the game: just enough for the shipped regions to compile unchanged. -------------
internal static class Mathf
{{
    public static float Max(float a, float b) => a > b ? a : b;
}}
internal sealed class Entry<T> {{ public T Value; }}
internal static class Cfg
{{
    public static Entry<float> DroneAltDeckM   = new Entry<float>();
    public static Entry<float> DroneStaggerSec = new Entry<float>();
}}
// The real one is [System.Serializable] with the same two public string fields; nothing in the
// extracted region touches anything else on it.
internal sealed class CfgOverride {{ public string key = ""; public string value = ""; }}

// The shipped SPEC-GRAMMAR + CARD-OWNS regions of ScenarioPlayer.cs, verbatim, plus accessor shims
// (SplitSpec is private static, so the harness has to live in the same class).
internal static class ScenarioPlayer
{{
{scen}
    internal struct Preflight {{ public CfgOverride[] Config; }}
}}

// The shipped CARD-OWNS-SPAWN region of TestDrone.cs, verbatim. These are the two reads that happen
// BEFORE any card starts, i.e. the ones a pin cannot reach.
internal static class TestDrone
{{
{spawn}
}}

internal static class P
{{
    static int fails;
    static void Ok(bool cond, string what)
    {{
        if (!cond) {{ Console.WriteLine($"  FAIL {{what}}"); fails++; }}
    }}

    static void F(CfgOverride[] cfg, string spec, float fb, float want)
    {{
        float got = ScenarioPlayer.DeclaredFloat(cfg, spec, fb);
        Ok(Math.Abs(got - want) < 1e-4f,
           $"DeclaredFloat('{{spec}}', live={{fb}}) = {{got}}, want {{want}} — a card's declared "
           + "value must beat the F1 value, and its silence must not");
    }}

    static void B(CfgOverride[] cfg, string spec, bool fb, bool want)
    {{
        bool got = ScenarioPlayer.DeclaredBool(cfg, spec, fb);
        Ok(got == want, $"DeclaredBool('{{spec}}', live={{fb}}) = {{got}}, want {{want}}");
    }}

    static void Deck(CfgOverride[] cfg, float live, float want)
    {{
        Cfg.DroneAltDeckM.Value = live;
        float got = TestDrone.DeckSpreadM(new ScenarioPlayer.Preflight {{ Config = cfg }});
        Ok(Math.Abs(got - want) < 1e-4f,
           $"DeckSpreadM(live={{live}}) = {{got}}, want {{want}} — THE hs-hold DEFECT: this is read "
           + "while the fleet is laid out, before any pin exists, so it must read the card");
    }}

    static void Stag(CfgOverride[] cfg, float live, float want)
    {{
        Cfg.DroneStaggerSec.Value = live;
        float got = TestDrone.StaggerSec(new ScenarioPlayer.Preflight {{ Config = cfg }});
        Ok(Math.Abs(got - want) < 1e-4f, $"StaggerSec(live={{live}}) = {{got}}, want {{want}}");
    }}

    static int Main()
    {{
        // A COMMA-DECIMAL MACHINE IS THE POINT of this line, not a formality: the resolver parses
        // with InvariantCulture, and a card file is an artifact that travels between machines.
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");
{chr(10).join(checks)}

        // Null config (no card selected at all) must resolve to the live value, not throw — this
        // runs on a hotkey path before anything is spawned.
        Ok(ScenarioPlayer.DeclaredText(null, "Drone/DroneAltDeckM") == null, "DeclaredText(null)");
        Cfg.DroneAltDeckM.Value = 1234f;
        Ok(Math.Abs(TestDrone.DeckSpreadM(new ScenarioPlayer.Preflight()) - 1234f) < 1e-4f,
           "DeckSpreadM with no card must fall through to Cfg, not throw");

        // A null ENTRY inside the array (a card with a stray comma) is skipped, not dereferenced.
        var holey = new[] {{ (CfgOverride)null, new CfgOverride {{ key = "Drone/DroneAltDeckM", value = "77" }} }};
        Ok(Math.Abs(ScenarioPlayer.DeclaredFloat(holey, "Drone/DroneAltDeckM", 0f) - 77f) < 1e-4f,
           "a null entry in config[] must be skipped, not throw");

        Console.WriteLine(fails == 0 ? "ok  card ownership — declared beats F1 at every site"
                                     : $"{{fails}} failure(s)");
        return fails == 0 ? 0 : 1;
    }}
}}
"""


# THE KNOBS THAT MAY NEVER BE READ BARE. One rule, no per-site argument about whether the pin has
# landed yet: every harness read of a card-owned Cfg entry goes through DeclaredFloat/DeclaredBool,
# which is what makes the card's value win. `hs-hold` broke because ONE site read the global.
NEVER_BARE = ("Cfg.DroneAltDeckM", "Cfg.DroneStaggerSec",
              "Cfg.ScenarioForceEntry", "Cfg.ScenarioEntryFuel")
HARNESS = ("ScenarioPlayer.cs", "TestDrone.cs", "WTMouseAimPlugin.cs")


def source_invariants():
    """Problems the compiled region cannot see: a read that bypasses it entirely.

    The regions above prove the resolver is right. They cannot prove it is USED — and a resolver
    nobody calls is exactly the state the harness was in before v1.0.2, when `DeckSpreadM` read
    `Cfg.DroneAltDeckM` directly and a card declaring 3000 against a live 0 flew the 0."""
    bad = []
    for name in HARNESS:
        # Comments name these knobs freely, and the STATEMENT is the unit — not the line. A resolver
        # call wrapped across two lines is still one read, and checking per line would fail it.
        code = re.sub(r"//.*", "", (REPO / name).read_text(encoding="utf-8"))
        for knob in NEVER_BARE:
            for m in re.finditer(re.escape(knob) + r"\b", code):
                start = max(code.rfind(c, 0, m.start()) for c in ";{}")
                stmt = " ".join(code[start + 1:m.end() + 200].split())
                # The ONE legal shape: the bare read is the FALLBACK argument of the resolver, in the
                # same statement. That is what "the card wins, and its silence does not" compiles to.
                if "Declared" in stmt:
                    continue
                bad.append(
                    f"{name}:{code.count(chr(10), 0, m.start()) + 1} reads {knob} directly "
                    f"({stmt[:120]}). A card-owned knob must be read through "
                    f"ScenarioPlayer.DeclaredFloat/DeclaredBool with this as the FALLBACK, or the "
                    f"card cannot own it — see the CARD-OWNS region."
                )
    return bad


def main() -> int:
    problems = source_invariants()
    for p in problems:
        print("  FAIL " + p)
    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    scen = extract(SCEN, "SPEC-GRAMMAR") + "\n" + extract(SCEN, "CARD-OWNS")
    spawn = extract(DRONE, "CARD-OWNS-SPAWN")
    tmp = Path(tempfile.mkdtemp(prefix="cardowns-"))
    try:
        (tmp / "cardowns.csproj").write_text(PROJ, encoding="utf-8")
        (tmp / "Program.cs").write_text(program(scen, spawn), encoding="utf-8")
        r = subprocess.run(
            ["dotnet", "run", "--project", str(tmp), "-v", "quiet", "--nologo"],
            capture_output=True, text=True,
        )
        out = (r.stdout or "") + (r.stderr or "")
        print(out.strip())
        if r.returncode != 0 and "FAIL" not in out and "failure" not in out:
            print(f"FAIL  the generated project did not build/run (exit {r.returncode}).")
        if problems:
            print(f"{len(problems)} source invariant failure(s) — a card-owned knob is read bare.")
        return r.returncode or (1 if problems else 0)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
