#!/usr/bin/env python3
"""Check that a card on DISK still round-trips through the card model — by RUNNING the real classes.

The bug this exists for: until v0.90.1 the model was (de)serialised with UnityEngine.JsonUtility,
which SILENTLY DROPS `public Seg[] segments`. Every card written to disk had no `segments` key and
every card read back had `segments == null`, so Validate rejected it with "no segments — skipped" —
from v0.71 to v0.90 not one file card ever loaded. Nothing caught it, because the built-in cards are
constructed in C# and never touch a serializer, so every gate and every batch flew the one path that
could not fail.

So this extracts the CARD-MODEL region of ScenarioPlayer.cs VERBATIM, wraps it in a throwaway console
project referencing the game's own Newtonsoft.Json.dll, and deserialises every cards/*.json through
it. Same shape as test-board-math.py, and for the same reason: a Python reimplementation of the model
would drift from the C# and then agree with itself forever.

Needs the .NET SDK (this repo already requires it to build at all) and the game folder (for
Newtonsoft.Json.dll) — located the same way the build does, via build/locate-game.ps1 /
NUCLEAR_OPTION_PATH. With no game found it SKIPS and exits 0: a fresh checkout has none.

Usage:
    python debugtests/test-card-model.py [cards_dir]     # default: <repo>/cards
"""

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "ScenarioPlayer.cs"
LOCATE = REPO / "build" / "locate-game.ps1"
BEGIN = "// --- CARD-MODEL BEGIN ---"
END = "// --- CARD-MODEL END ---"

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>cardmodel</AssemblyName>
    <RootNamespace>cardmodel</RootNamespace>
    <!-- Every model field is written by the deserializer, never by this program. -->
    <NoWarn>CS0649</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Newtonsoft.Json">
      <HintPath>{json_dll}</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"""

# The fields no shipped card exercises, all on one card because they are independent scalars and a
# second synthetic would be a second compile for nothing:
#   `config`   — a nested ARRAY OF OBJECTS, exactly the shape JsonUtility mishandled;
#   `airframe` — a COMMA LIST since v0.91 (one jsonKey per drone lane, wrapping). The SPACE after the
#                comma is deliberate: `Validate`'s prose detector splits first and trims per token, so
#                a serializer that reformatted the string would change which lane flies what;
#   `count`    — v0.91 fleet size. An int, so it round-trips through a JSON number rather than a
#                string, and 0 is "unset" — hence a non-zero value here or the check is vacuous;
#   `startSpeedCorner` — v0.93 airframe-relative entry speed. A FLOAT with a fractional part on
#                purpose: it is multiplied by the lane airframe's corner speed, so a serializer that
#                round-tripped it through an int (or through a locale-dependent decimal separator)
#                would place every drone at the wrong speed while the capture still looked clean.
SYNTHETIC = (
    '{"name":"synthetic","note":"not a real card","armToggle":"Control/MarkerRateFeedForward",'
    '"repeat":8,"airframe":"Fighter1, Multirole1","count":6,"startSpeedCorner":1.25,'
    '"config":[{"key":"Control/TurnLeadTime","value":"0.35"},'
    '{"key":"Control/AssistTurnRateGain","value":"1.5"}],'
    '"segments":[{"tag":"arm","dur":6.0},{"tag":"sweep","dur":30.0}]}'
)


def extract(src: str) -> str:
    """The C# between the CARD-MODEL markers, verbatim."""
    i, j = src.find(BEGIN), src.find(END)
    if i < 0 or j < 0 or j < i:
        raise SystemExit(
            f"FAIL  could not find the {BEGIN} / {END} markers in {SRC.name}. If the model classes "
            f"moved, move the markers with them — this check is the only thing verifying that a card "
            f"on disk still loads."
        )
    body = src[i + len(BEGIN):j]
    if "UnityEngine" in body:
        raise SystemExit(
            "FAIL  the CARD-MODEL region now references UnityEngine; the model must stay plain "
            "fields or it cannot be compiled — and serialised — outside the game."
        )
    return body


def find_game() -> Path:
    """The game folder, resolved exactly the way the build resolves it. None if unavailable."""
    env = os.environ.get("NUCLEAR_OPTION_PATH", "")
    if env and (Path(env) / "NuclearOption.exe").is_file():
        return Path(env)
    ps = shutil.which("powershell") or shutil.which("pwsh")
    if ps is None or not LOCATE.is_file():
        return None
    r = subprocess.run(
        [ps, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(LOCATE)],
        capture_output=True, text=True,
    )
    # locate-game.ps1 prints exactly one stdout line "<game>|<bepinex core>"; paths never contain '|'.
    for line in reversed((r.stdout or "").splitlines()):
        if "|" in line:
            game = Path(line.split("|")[0].strip())
            if (game / "NuclearOption.exe").is_file():
                return game
    return None


def program(body: str, cards: Path) -> str:
    return (
        "using System;\nusing System.Globalization;\nusing System.IO;\n"
        "using Newtonsoft.Json;\n\n"
        + body + "\n\n"
        "internal static class P\n{\n"
        "    static int fails;\n"
        "    static void Fail(string what) { Console.WriteLine(\"  FAIL \" + what); fails++; }\n"
        "\n"
        "    // Everything a card file must survive. Returns the parsed card, or null if it did not.\n"
        "    static Card Check(string name, string raw)\n"
        "    {\n"
        "        Card c;\n"
        "        try { c = JsonConvert.DeserializeObject<Card>(raw); }\n"
        "        catch (Exception e) { Fail(name + \": threw \" + e.GetType().Name + \": \" + e.Message); return null; }\n"
        "        if (c == null) { Fail(name + \": deserialised to null\"); return null; }\n"
        "        if (c.segments == null || c.segments.Length == 0)\n"
        "        {\n"
        "            Fail(name + \": segments is \" + (c.segments == null ? \"null\" : \"empty\")\n"
        "                 + \" — the serializer dropped the field (this is the JsonUtility bug)\");\n"
        "            return null;\n"
        "        }\n"
        "        for (int i = 0; i < c.segments.Length; i++)\n"
        "            if (!(c.segments[i].dur > 0f))\n"
        "                Fail(name + \": segments[\" + i + \"] '\" + c.segments[i].tag + \"' has dur \" + c.segments[i].dur);\n"
        "        if (c.segments[0].tag != \"arm\")\n"
        "            Fail(name + \": segments[0].tag is '\" + c.segments[0].tag + \"', want 'arm'\");\n"
        "\n"
        "        // WRITE side of the same bug: a recorded card used to be written with no segments key.\n"
        "        Card rt;\n"
        "        try { rt = JsonConvert.DeserializeObject<Card>(JsonConvert.SerializeObject(c)); }\n"
        "        catch (Exception e) { Fail(name + \": round-trip threw \" + e.GetType().Name + \": \" + e.Message); return c; }\n"
        "        if (rt == null || rt.segments == null || rt.segments.Length != c.segments.Length)\n"
        "        {\n"
        "            Fail(name + \": round-trip kept \"\n"
        "                 + (rt == null || rt.segments == null ? \"no\" : rt.segments.Length.ToString())\n"
        "                 + \" of \" + c.segments.Length + \" segments\");\n"
        "            return c;\n"
        "        }\n"
        "        for (int i = 0; i < c.segments.Length; i++)\n"
        "            if (rt.segments[i].tag != c.segments[i].tag || rt.segments[i].dur != c.segments[i].dur)\n"
        "                Fail(name + \": round-trip changed segments[\" + i + \"] '\" + c.segments[i].tag + \"'/\"\n"
        "                     + c.segments[i].dur + \" -> '\" + rt.segments[i].tag + \"'/\" + rt.segments[i].dur);\n"
        "        return c;\n"
        "    }\n"
        "\n"
        "    static int Main()\n"
        "    {\n"
        "        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;\n"
        f"        string dir = @\"{str(cards)}\";\n"
        "        string[] files = Directory.GetFiles(dir, \"*.json\");\n"
        "        Array.Sort(files, StringComparer.Ordinal);\n"
        "        if (files.Length == 0) { Console.WriteLine(\"  FAIL no *.json in \" + dir); return 1; }\n"
        "\n"
        "        int segs = 0, withNote = 0, withArm = 0, withCfg = 0;\n"
        "        foreach (string f in files)\n"
        "        {\n"
        "            string raw = File.ReadAllText(f);\n"
        "            Card c = Check(Path.GetFileName(f), raw);\n"
        "            if (c == null) continue;\n"
        "            segs += c.segments.Length;\n"
        "            if (raw.Contains(\"\\\"note\\\"\")) withNote++;   // parsed fine WITH an unknown key present\n"
        "            if (!string.IsNullOrEmpty(c.armToggle)) withArm++;\n"
        "            if (c.config != null && c.config.Length > 0) withCfg++;\n"
        "        }\n"
        "\n"
        "        // Unknown keys must not be fatal, and the assertion must not be vacuous.\n"
        "        if (withNote == 0)\n"
        "            Fail(\"no card carries a `note` key, so nothing here proves an unknown key is ignored\");\n"
        "        if (withArm == 0)\n"
        "            Fail(\"no card carries a non-empty `armToggle` — the v0.90 A/B field is uncovered\");\n"
        "\n"
        f"        Card syn = Check(\"<synthetic>\", @\"{SYNTHETIC.replace(chr(34), chr(34) * 2)}\");\n"
        "        if (syn != null)\n"
        "        {\n"
        "            if (syn.config == null || syn.config.Length != 2)\n"
        "                Fail(\"<synthetic>: config is \" + (syn.config == null ? \"null\" : syn.config.Length.ToString()) + \", want 2\");\n"
        "            else if (syn.config[0].key != \"Control/TurnLeadTime\" || syn.config[0].value != \"0.35\")\n"
        "                Fail(\"<synthetic>: config[0] is '\" + syn.config[0].key + \"'='\" + syn.config[0].value + \"'\");\n"
        "            if (syn.repeat != 8) Fail(\"<synthetic>: repeat is \" + syn.repeat + \", want 8\");\n"
        "            // v0.91. The airframe string must survive BYTE FOR BYTE: TestDrone.AirframeList\n"
        "            // splits it per lane and ScenarioPlayer.CountKeys counts the same tokens, so a\n"
        "            // serializer that rewrote it would change both the fleet size and who flies what.\n"
        "            if (syn.airframe != \"Fighter1, Multirole1\")\n"
        "                Fail(\"<synthetic>: airframe is '\" + syn.airframe + \"', want 'Fighter1, Multirole1'\");\n"
        "            if (syn.count != 6) Fail(\"<synthetic>: count is \" + syn.count + \", want 6\");\n"
        "            // v0.93. Exact float compare is right here: 1.25 is representable, and the\n"
        "            // failure this guards against (dropped field, int truncation, ',' decimal\n"
        "            // separator) is never off by an ulp.\n"
        "            if (syn.startSpeedCorner != 1.25f)\n"
        "                Fail(\"<synthetic>: startSpeedCorner is \" + syn.startSpeedCorner + \", want 1.25\");\n"
        "            Card rt = JsonConvert.DeserializeObject<Card>(JsonConvert.SerializeObject(syn));\n"
        "            if (rt.config == null || rt.config.Length != 2 || rt.config[1].value != \"1.5\")\n"
        "                Fail(\"<synthetic>: config did not survive the round-trip\");\n"
        "            if (rt.airframe != syn.airframe)\n"
        "                Fail(\"<synthetic>: airframe round-tripped '\" + syn.airframe + \"' -> '\" + rt.airframe + \"'\");\n"
        "            if (rt.count != syn.count)\n"
        "                Fail(\"<synthetic>: count round-tripped \" + syn.count + \" -> \" + rt.count);\n"
        "            if (rt.startSpeedCorner != syn.startSpeedCorner)\n"
        "                Fail(\"<synthetic>: startSpeedCorner round-tripped \" + syn.startSpeedCorner\n"
        "                     + \" -> \" + rt.startSpeedCorner);\n"
        "        }\n"
        "\n"
        "        if (fails == 0)\n"
        "        {\n"
        "            Console.WriteLine($\"ok  card model — {files.Length} cards, {segs} segments, \"\n"
        "                + $\"round-trip clean; {withArm} with armToggle, {withCfg} with config\");\n"
        "            if (withCfg == 0)\n"
        "                Console.WriteLine(\"    note: no shipped card pins a `config` override; that field is \"\n"
        "                    + \"covered by the synthetic card only.\");\n"
        "        }\n"
        "        else Console.WriteLine($\"{fails} failure(s)\");\n"
        "        return fails == 0 ? 0 : 1;\n"
        "    }\n}\n"
    )


def main() -> int:
    cards = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else REPO / "cards"
    if not cards.is_dir():
        print(f"FAIL  no cards directory at {cards}")
        return 1
    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    game = find_game()
    if game is None:
        print("SKIP  Nuclear Option not found (needed for its Newtonsoft.Json.dll). "
              "Set NUCLEAR_OPTION_PATH to run this check.")
        return 0
    json_dll = game / "NuclearOption_Data" / "Managed" / "Newtonsoft.Json.dll"
    if not json_dll.is_file():
        print(f"SKIP  {json_dll} is missing — cannot compile the model against the game's serializer.")
        return 0

    body = extract(SRC.read_text(encoding="utf-8"))
    tmp = Path(tempfile.mkdtemp(prefix="cardmodel-"))
    try:
        (tmp / "cardmodel.csproj").write_text(PROJ.format(json_dll=json_dll), encoding="utf-8")
        (tmp / "Program.cs").write_text(program(body, cards), encoding="utf-8")
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
