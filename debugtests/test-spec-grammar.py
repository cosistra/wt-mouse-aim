#!/usr/bin/env python3
"""Check the config-spec grammar — the shipped C# AND scorecard.py's Python copy of it, against ONE
case table.

`ScenarioPlayer.SplitSpec` is the single parser for every way the mod names a config entry: the
global `ScenarioArmToggle`, a card's `armToggle`, and every `config[].key` a card pins. One grammar,
one parser — "Key" (section defaults to `Control`, where every control-law lever lives) or
"Section/Key", both halves non-empty, at most one slash.

There is a SECOND implementation: `scorecard.py`'s `split_spec`, which powers `card_setup_problems`
— the only offline check on a card's setup, and the one that refuses a card pinning the very knob
its own A/B sweeps. A copy is exactly the drift these tests exist to abolish, so this runs both
against the same table:

  * a Python copy that is STRICTER than the mod flags cards that fly perfectly well, which is how an
    offline check stops being read;
  * a Python copy that is LOOSER passes a card whose `config[].key` the mod will silently drop with
    one warning, and the batch then flies with the knob unset — indistinguishable in the capture
    from not having asked.

The C# side is extracted verbatim from between the SPEC-GRAMMAR markers, compiled and run, so it is
the shipped code being tested and not a third copy. Same trick, and same reason, as
`test-board-math.py` / `test-arm-schedule.py`.

Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-spec-grammar.py
"""

import importlib.util
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "ScenarioPlayer.cs"
SCORECARD = Path(__file__).resolve().parent / "scorecard.py"
BEGIN = "// --- SPEC-GRAMMAR BEGIN ---"
END = "// --- SPEC-GRAMMAR END ---"

# (spec, expected). None = malformed/refused; otherwise the (section, key) pair. ONE column, because
# the two implementations must agree on every row — the multi-slash divergence this test found the
# day it was written was closed in v0.96 by tightening the C# to the Python's rule (see "A/B/C").
#
# The refusals are the point of the table. "/Foo" and "Foo/" read as bare keys under any
# split-on-the-first-slash implementation that forgets to test both halves, and they are typos: they
# would resolve the wrong entry, or none, without saying so. Whitespace is trimmed everywhere
# because a hand-written JSON card is where these come from and " Control / Knob " is formatting,
# not a different knob.
CASES = [
    # spec                  expected
    ("Knob",                ("Control", "Knob")),       # bare key -> Control
    ("Control/Knob",        ("Control", "Knob")),       # ...long form: THE SAME ENTRY
    ("Drone/DroneCount",    ("Drone", "DroneCount")),
    ("Sandbox/SandboxAlt",  ("Sandbox", "SandboxAlt")),
    ("  Knob  ",            ("Control", "Knob")),       # a card's stray space
    (" Control / Knob ",    ("Control", "Knob")),       # ...on both halves
    ("A/B/C",               None),                      # >1 slash is a typo: no bound entry can ever
                                                        # match, so both sides refuse it BEFORE the
                                                        # batch flies rather than warning after.
    ("/Knob",               None),                      # empty section, not bare
    ("Knob/",               None),                      # empty key, not bare
    ("/",                   None),
    ("//",                  None),
    ("",                    None),
    ("   ",                 None),                      # whitespace-only is empty
    ("Control/ ",           None),                      # ...trims to empty
    (" /Knob",              None),
    ("A B",                 ("Control", "A B")),        # a space INSIDE a key is not this parser's
                                                        # problem: ResolveEntry finds nothing and every
                                                        # caller warns. Pinned so nobody "fixes" it on
                                                        # one side only.
]

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>specgrammar</AssemblyName>
    <RootNamespace>specgrammar</RootNamespace>
  </PropertyGroup>
</Project>
"""

BANNED = ("UnityEngine", "Mathf.", "Vector3", "Aircraft", "ConfigEntry", "BepInEx")


def extract(src: str) -> str:
    """The C# between the SPEC-GRAMMAR markers, verbatim."""
    i, j = src.find(BEGIN), src.find(END)
    if i < 0 or j < 0 or j < i:
        raise SystemExit(
            f"FAIL  could not find the {BEGIN} / {END} markers in {SRC.name}. If SplitSpec moved, "
            f"move the markers with it — this check is the only thing verifying it."
        )
    body = src[i + len(BEGIN):j]
    code = re.sub(r"//.*", "", body)
    for banned in BANNED:
        if re.search(rf"\b{re.escape(banned)}", code):
            raise SystemExit(
                f"FAIL  the SPEC-GRAMMAR region now references {banned}; it must stay pure (plain "
                f"strings only) or it cannot be compiled outside the game."
            )
    return body


def cs_literal(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def program(body: str) -> str:
    checks = []
    for spec, want in CASES:
        lit = cs_literal(spec)
        if want is None:
            checks.append(f"        Bad({lit});")
        else:
            checks.append(f"        Good({lit}, {cs_literal(want[0])}, {cs_literal(want[1])});")
    # One class, not two: SplitSpec is `private static` in the shipped source and stays verbatim, so
    # the harness has to live inside the same class to call it.
    return (
        "using System;\n\n"
        "internal static class P\n{\n" + body + "\n"
        "    static int fails;\n"
        "    static void Good(string spec, string wsec, string wkey)\n"
        "    {\n"
        "        bool ok = SplitSpec(spec, out string sec, out string key);\n"
        "        if (!ok || sec != wsec || key != wkey)\n"
        "        {\n"
        "            Console.WriteLine($\"  FAIL SplitSpec('{spec}'): got ok={ok} '{sec}'/'{key}', "
        "want '{wsec}'/'{wkey}'\");\n"
        "            fails++;\n"
        "        }\n"
        "    }\n"
        "    static void Bad(string spec)\n"
        "    {\n"
        "        if (SplitSpec(spec, out string sec, out string key))\n"
        "        {\n"
        "            Console.WriteLine($\"  FAIL SplitSpec('{spec}'): accepted as '{sec}'/'{key}', "
        "want refused\");\n"
        "            fails++;\n"
        "        }\n"
        "    }\n"
        "    static int Main()\n"
        "    {\n"
        + "\n".join(checks) + "\n"
        "        Console.WriteLine(fails == 0 ? \"ok  spec grammar (C#)\" : $\"{fails} failure(s)\");\n"
        "        return fails == 0 ? 0 : 1;\n"
        "    }\n}\n"
    )


def check_python_copy() -> int:
    """scorecard.py's split_spec against the same table. Returns the failure count."""
    if not SCORECARD.exists():
        print("FAIL  debugtests/scorecard.py is missing — its split_spec copy went unchecked.")
        return 1
    spec = importlib.util.spec_from_file_location("_scorecard_for_specgrammar", SCORECARD)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    fails = 0
    for s, want in CASES:
        got = mod.split_spec(s)
        if got != want:
            print(f"  FAIL scorecard.split_spec({s!r}): got {got!r}, want {want!r} — the offline "
                  f"card check must mirror ScenarioPlayer.SplitSpec, in both directions")
            fails += 1
    # Non-strings reach it straight off json.load (a card writing `"key": 3`), so it must refuse
    # rather than throw: card_setup_problems is the thing that reports the bad card.
    for junk in (None, 3, ["A"], {"A": 1}, True):
        if mod.split_spec(junk) is not None:
            print(f"  FAIL scorecard.split_spec({junk!r}) must refuse a non-string, not parse it")
            fails += 1
    print("ok  spec grammar (scorecard.py copy)" if fails == 0 else f"{fails} failure(s) in the Python copy")
    return fails


def main() -> int:
    py_fails = check_python_copy()
    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    body = extract(SRC.read_text(encoding="utf-8"))
    tmp = Path(tempfile.mkdtemp(prefix="specgrammar-"))
    try:
        (tmp / "specgrammar.csproj").write_text(PROJ, encoding="utf-8")
        (tmp / "Program.cs").write_text(program(body), encoding="utf-8")
        r = subprocess.run(
            ["dotnet", "run", "--project", str(tmp), "-v", "quiet", "--nologo"],
            capture_output=True, text=True,
        )
        out = (r.stdout or "") + (r.stderr or "")
        print(out.strip())
        if r.returncode != 0 and "FAIL" not in out and "failure" not in out:
            print(f"FAIL  the generated project did not build/run (exit {r.returncode}).")
        return 1 if (py_fails or r.returncode != 0) else 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
