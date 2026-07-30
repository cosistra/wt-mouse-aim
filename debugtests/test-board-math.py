#!/usr/bin/env python3
"""Check the harness run board's arithmetic — by RUNNING IT, not by re-implementing it.

The board (WTMouseAimPlugin.DrawRunBoard) shows an operator how long an unattended drone batch has
left. Two pieces of it are non-trivial enough to get wrong silently: the m:ss / "0.0s" formatter and
the seconds-left-in-this-card sum. Both live in ScenarioPlayer.cs between the BOARD-MATH markers,
deliberately written with plain numbers and no Unity types.

This extracts that region verbatim, wraps it in a throwaway console project, and runs the .NET SDK
over it against the case table below. So a change to the C# is checked against the table, and the
usual failure of a "check" like this — a Python reimplementation that drifts from the code and then
agrees with itself forever — cannot happen here.

Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-board-math.py
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "ScenarioPlayer.cs"
BEGIN = "// --- BOARD-MATH BEGIN ---"
END = "// --- BOARD-MATH END ---"

# (seconds, expected text). Edge cases first, because they are the ones that regress:
# negatives and NaN clamp (the board reads a card mid-boundary), the 59.95 hand-off exists so
# nothing ever prints "60.0s", and the minute side truncates rather than rounds.
CLOCK_CASES = [
    ("-5f", "0.0s"),
    ("float.NaN", "0.0s"),
    ("0f", "0.0s"),
    ("0.04f", "0.0s"),
    ("3.14f", "3.1s"),
    ("12.44f", "12.4s"),
    ("59.94f", "59.9s"),
    ("59.96f", "0:59"),      # would round to "60.0s" on the sub-minute side
    ("60f", "1:00"),
    ("89.6f", "1:29"),
    ("120f", "2:00"),
    ("252.4f", "4:12"),
    ("1392f", "23:12"),      # 174 s x 8 replicates — the preflight's "per drone" number
    ("3600f", "60:00"),
]

# (segment durations, current index, seconds into it, expected seconds left in the card).
SEGS_CASES = [
    ("4f,15f,10f", 0, "0f", 29.0),
    ("4f,15f,10f", 0, "1.5f", 27.5),
    ("4f,15f,10f", 1, "3f", 22.0),
    ("4f,15f,10f", 2, "10f", 0.0),     # exactly spent
    ("4f,15f,10f", 2, "12f", 0.0),     # overrun clamps rather than going negative
    ("4f,15f,10f", 3, "0f", 0.0),      # index past the end: legal for one tick before NextCard
    ("4f,15f,10f", -1, "0f", 29.0),    # ...and the mirror guard on the low side
    ("6f,30f", 1, "7.25f", 22.75),
    ("", 0, "0f", 0.0),
]

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>boardmath</AssemblyName>
    <RootNamespace>boardmath</RootNamespace>
  </PropertyGroup>
</Project>
"""


def extract(src: str) -> str:
    """The C# between the BOARD-MATH markers, verbatim."""
    i, j = src.find(BEGIN), src.find(END)
    if i < 0 or j < 0 or j < i:
        raise SystemExit(
            f"FAIL  could not find the {BEGIN} / {END} markers in {SRC.name}. If the functions "
            f"moved, move the markers with them — this check is the only thing verifying them."
        )
    body = src[i + len(BEGIN):j]
    for banned in ("UnityEngine", "Mathf.", "Vector3", "Aircraft"):
        if banned in body:
            raise SystemExit(
                f"FAIL  the BOARD-MATH region now references {banned}; it must stay pure "
                f"(plain numbers only) or it cannot be compiled outside the game."
            )
    return body


def program(body: str) -> str:
    checks = []
    for arg, want in CLOCK_CASES:
        checks.append(f'        Eq(M.Clock({arg}), "{want}", "Clock({arg})");')
    for durs, si, tseg, want in SEGS_CASES:
        checks.append(
            f'        Near(M.SegsLeft(new float[]{{{durs}}}, {si}, {tseg}), {want}f, '
            f'"SegsLeft([{durs}], {si}, {tseg})");'
        )
    return (
        "using System;\nusing System.Globalization;\n\n"
        "internal static class M\n{\n" + body + "\n}\n\n"
        "internal static class P\n{\n"
        "    static int fails;\n"
        "    static void Eq(string got, string want, string what)\n"
        "    {\n"
        "        if (got != want) { Console.WriteLine($\"  FAIL {what}: got '{got}', want '{want}'\"); fails++; }\n"
        "    }\n"
        "    static void Near(float got, float want, string what)\n"
        "    {\n"
        "        if (Math.Abs(got - want) > 1e-4f) { Console.WriteLine($\"  FAIL {what}: got {got}, want {want}\"); fails++; }\n"
        "    }\n"
        "    static int Main()\n"
        "    {\n"
        # The mod formats with the ambient culture like the rest of the codebase; pin it here so the
        # check gives the same verdict on a machine whose decimal separator is a comma.
        "        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;\n"
        + "\n".join(checks) + "\n"
        "        Console.WriteLine(fails == 0 ? \"ok  board math\" : $\"{fails} failure(s)\");\n"
        "        return fails == 0 ? 0 : 1;\n"
        "    }\n}\n"
    )


def main() -> int:
    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    body = extract(SRC.read_text(encoding="utf-8"))
    tmp = Path(tempfile.mkdtemp(prefix="boardmath-"))
    try:
        (tmp / "boardmath.csproj").write_text(PROJ, encoding="utf-8")
        (tmp / "Program.cs").write_text(program(body), encoding="utf-8")
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
