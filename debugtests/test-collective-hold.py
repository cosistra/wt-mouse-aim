#!/usr/bin/env python3
"""Check the harness's rotorcraft collective hold — by RUNNING IT, not by re-implementing it.

`TestDrone.CollectiveStep` is the PI loop that gives a drone-flown rotorcraft hover card a
collective. It exists because a fixed throttle is not a hover for more than one airframe: R41 flew
all three rotorcraft at `HoldThrottle` 0.60 and got a three-way split — `QuadVTOL1` and
`AttackHelo1` climbed, `UtilityHelo1` sank at -25 m/s and aborted 16 of 16 replicates on the 500 m
altitude floor (debugtests/R41-rotor.md SS5a).

Two things about it are worth a check and neither is cheap to establish by flying:

  1. THE SIGN. A collective loop with an inverted term does not wobble, it flies the aircraft into
     the ground at full deflection, and the capture that results looks like a control-law failure.
  2. THE ONE-LAW PROPERTY. The loop must converge on the hover collective of ANY airframe without
     being told what it is. That is the whole reason it carries an integrator instead of a fixed
     trim, so the check runs it against a plant whose hover collective is a free parameter and
     asserts convergence for values spanning well past the roster.

The region between the COLLECTIVE-HOLD markers in TestDrone.cs is extracted VERBATIM, wrapped in a
throwaway console project and run by the .NET SDK, exactly as test-board-math.py / test-card-model.py
/ test-fleet-resolve.py do. So a change to the C# is checked against the table below, and the usual
failure of a check like this — a Python reimplementation that drifts from the code and then agrees
with itself forever — cannot happen.

`Mathf` is STUBBED rather than banned: the region's clamps are the anti-windup and dropping them to
keep the region "pure" would mean testing a different function from the one that ships.

Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-collective-hold.py
"""

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "TestDrone.cs"
BEGIN = "// --- COLLECTIVE-HOLD BEGIN ---"
END = "// --- COLLECTIVE-HOLD END ---"

# Anything here would make the region uncompilable outside the game. Mathf is stubbed below, so it is
# deliberately absent from this list.
BANNED = ("UnityEngine", "Vector3", "Aircraft", "Time.", "Drone", "Cfg.")

# ---------------------------------------------------------------------------------------------
# ONE-STEP CASES: (trim in, altErr, velY, dt, expected trim out, expected throttle out).
#
# Computed from the shipped gains by hand, so a silent gain change fails here rather than in a
# night of flying. VsPerAltErr 0.05, VsMax 25, CollP 0.02, CollI 0.002, MinColl 0.05.
# ---------------------------------------------------------------------------------------------
STEP_CASES = [
    # On target, not moving: nothing to correct, the trim is held and flown as-is. THE case that
    # says the loop is a hold and not a drift.
    ("0.60f", "0f", "0f", "0.02f", 0.60, 0.60),
    # BELOW target, stationary -> vsWant = +5, vsErr = +5 -> MORE collective on both terms.
    # trim 0.60 + 0.002*5*0.02 = 0.6002 ; out 0.6002 + 0.02*5 = 0.7002
    ("0.60f", "100f", "0f", "0.02f", 0.6002, 0.7002),
    # ABOVE target, stationary -> the exact mirror. Sign symmetry, which an abs() would break.
    ("0.60f", "-100f", "0f", "0.02f", 0.5998, 0.4998),
    # SINKING on target (the UtilityHelo1 case): vsWant 0, velY -25 -> vsErr +25 -> +0.5 of P.
    # The P term alone is what arrests it; the integrator moves 0.001 in this one tick.
    ("0.60f", "0f", "-25f", "0.02f", 0.601, 1.0),   # 0.601 + 0.5 = 1.101, clamped to 1
    # CLIMBING on target -> collective comes off.
    ("0.60f", "0f", "5f", "0.02f", 0.5998, 0.4998),
    # The outer loop CLAMPS: 2000 m low would ask 100 m/s of climb; VsMax caps it at 25.
    # Same answer as an altErr of 500 (0.05*500 = 25 exactly) -> proves the cap, not the gain.
    ("0.60f", "2000f", "0f", "0.02f", 0.601, 1.0),
    ("0.60f", "500f", "0f", "0.02f", 0.601, 1.0),
    # ANTI-WINDUP, the reason the trim is clamped and not only the output. An underpowered airframe
    # already at full collective and still sinking must not bank integral it has to unwind later.
    ("1f", "0f", "-25f", "0.02f", 1.0, 1.0),
    # ...and the floor. Never exact 0: that is the game's airbrake trigger (Airbrake.Update).
    ("0.05f", "0f", "25f", "0.02f", 0.05, 0.05),
    # dt SCALES THE INTEGRATOR AND NOTHING ELSE. Ten times the step, ten times the trim move, same
    # P contribution -> the loop is rate-correct rather than tick-counted.
    ("0.60f", "0f", "-5f", "0.02f", 0.6002, 0.7002),
    ("0.60f", "0f", "-5f", "0.2f", 0.602, 0.702),
]

# ---------------------------------------------------------------------------------------------
# CLOSED-LOOP CASES: (this airframe's true hover collective, its throttle->vertical-accel gain).
#
# The ONE-LAW assertion. A first-order plant — accel = K*(throttle - hover), integrated to velY and
# altitude — flown from the harness's neutral 0.60 seed for 120 s at the real 0.02 fixed step. The
# loop is never told `hover`; it has to find it. Asserted: altitude ends within 60 m of target (the
# R41 failure lost 1,903 m and hit a floor 500 m down), it never sinks more than 400 m below at any
# point, and the learned trim lands near the true hover value.
#
# The span is deliberately wider than the roster: 0.35 is a helo with power to spare, 0.90 one that
# barely hovers, and 0.60 is the value the old fixed throttle happened to be right about — which
# must not be the only one that works.
# ---------------------------------------------------------------------------------------------
PLANT_CASES = [
    ("0.85f", "10f"),   # the UtilityHelo1 shape: sinks hard at the old fixed 0.60
    ("0.90f", "6f"),    # worse, and a sluggish rotor with it
    ("0.35f", "20f"),   # climbs hard at 0.60 — the opposite failure, same loop
    ("0.60f", "10f"),   # the seed is already right: must not be disturbed off it
    ("0.75f", "30f"),   # a punchy quad, where too much integral would ring
    ("0.50f", "4f"),    # very low authority: slow, but it must still get there
]

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>collhold</AssemblyName>
    <RootNamespace>collhold</RootNamespace>
  </PropertyGroup>
</Project>
"""

# The only game type the region touches. Same shim trick as test-fleet-resolve.py: stub what the
# shipped code legitimately uses, so the region compiles unmodified.
SHIM = """
internal static class Mathf
{
    internal static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
"""


def extract(src: str) -> str:
    """The C# between the COLLECTIVE-HOLD markers, verbatim."""
    i, j = src.find(BEGIN), src.find(END)
    if i < 0 or j < 0 or j < i:
        raise SystemExit(
            f"FAIL  could not find the {BEGIN} / {END} markers in {SRC.name}. If the loop moved, "
            f"move the markers with it — this check is the only thing verifying its sign."
        )
    body = src[i + len(BEGIN):j]
    for banned in BANNED:
        if banned in body:
            raise SystemExit(
                f"FAIL  the COLLECTIVE-HOLD region in {SRC.name} now references {banned}; it must "
                f"stay compilable outside the game (only Mathf is stubbed)."
            )
    return body


def program(body: str) -> str:
    checks = []
    for trim, alt, vel, dt, want_trim, want_out in STEP_CASES:
        checks.append(
            f'        Step({trim}, {alt}, {vel}, {dt}, {want_trim}f, {want_out}f, '
            f'"CollectiveStep(trim={trim}, altErr={alt}, velY={vel}, dt={dt})");'
        )
    for hover, k in PLANT_CASES:
        checks.append(f'        Plant({hover}, {k}, "hover={hover} K={k}");')
    return (
        "using System;\nusing System.Globalization;\n"
        + SHIM
        + "\ninternal static class M\n{\n" + body + "\n}\n\n"
        "internal static class P\n{\n"
        "    static int fails;\n"
        "    static void Fail(string s) { Console.WriteLine(\"  FAIL \" + s); fails++; }\n"
        "\n"
        "    // ONE STEP, both outputs. The trim is by-ref, so the check has to hold it itself —\n"
        "    // which is also the shape the caller uses (Drone.Collective lives across ticks).\n"
        "    static void Step(float trim, float altErr, float velY, float dt,\n"
        "                     float wantTrim, float wantOut, string what)\n"
        "    {\n"
        "        float t = trim;\n"
        "        float got = M.CollectiveStep(ref t, altErr, velY, dt);\n"
        "        if (Math.Abs(t - wantTrim) > 1e-5f) Fail($\"{what}: trim {t}, want {wantTrim}\");\n"
        "        if (Math.Abs(got - wantOut) > 1e-5f) Fail($\"{what}: out {got}, want {wantOut}\");\n"
        "    }\n"
        "\n"
        "    // 120 s against a first-order rotorcraft whose hover collective the loop is never told.\n"
        "    // accel = K*(throttle - hover) with a little drag on the climb rate, semi-implicit\n"
        "    // Euler at the real fixed step.\n"
        "    static void Plant(float hover, float k, string what)\n"
        "    {\n"
        "        const float dt = 0.02f;\n"
        "        float trim = 0.60f;   // TestDrone.HoldThrottle — the seed the drone starts from\n"
        "        float alt = 0f, vel = 0f, worst = 0f;\n"
        "        for (int i = 0; i < 6000; i++)\n"
        "        {\n"
        "            float thr = M.CollectiveStep(ref trim, 0f - alt, vel, dt);\n"
        "            vel += (k * (thr - hover) - 0.15f * vel) * dt;\n"
        "            alt += vel * dt;\n"
        "            if (alt < worst) worst = alt;\n"
        "        }\n"
        "        if (Math.Abs(alt) > 60f)  Fail($\"{what}: settled {alt:0.0} m off target, want |alt| <= 60\");\n"
        "        if (worst < -400f)        Fail($\"{what}: dipped {worst:0.0} m, want > -400 (the floor is -500)\");\n"
        "        if (Math.Abs(trim - hover) > 0.05f)\n"
        "            Fail($\"{what}: learned trim {trim:0.000}, want within 0.05 of {hover:0.000}\");\n"
        "    }\n"
        "\n"
        "    static int Main()\n"
        "    {\n"
        "        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;\n"
        + "\n".join(checks) + "\n"
        "        Console.WriteLine(fails == 0 ? \"ok  collective hold\" : $\"{fails} failure(s)\");\n"
        "        return fails == 0 ? 0 : 1;\n"
        "    }\n}\n"
    )


def main() -> int:
    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    body = extract(SRC.read_text(encoding="utf-8"))
    tmp = Path(tempfile.mkdtemp(prefix="collhold-"))
    try:
        (tmp / "collhold.csproj").write_text(PROJ, encoding="utf-8")
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
