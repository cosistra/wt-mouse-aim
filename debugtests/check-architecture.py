#!/usr/bin/env python3
"""Verify ARCHITECTURE.md still describes the code. Stdlib only; no game install needed.

The diagram in ARCHITECTURE.md rots the moment code moves, and a stale map causes wrong-file
edits. This checks the parts that are mechanically checkable:

  1. every *.cs file at the repo root appears in the node index
  2. every top-level type (class/struct/enum) in those files appears in the node index
  3. the node index names no type that has disappeared
  4. every [HarmonyPatch(typeof(X), "M")] target is listed in the game-types table
  5. the <!-- ARCH-VERSION --> stamp matches PluginVersion in WTMouseAimPlugin.cs
  6. every segment tag ScenarioPlayer.cs can emit resolves to a real metric type in scorecard.py
  7. a handful of SOURCE INVARIANTS that compile fine when broken (see source_invariants)

It CANNOT check that the prose and arrows are still true. A reordered Apply stage or a law that
now does something different passes clean here and still needs a human/agent to re-read the L1
section they touched.

Usage:
    python debugtests/check-architecture.py                # verify (exit 1 on drift)
    python debugtests/check-architecture.py --fix-version   # rewrite the version stamp, then verify
    python debugtests/check-architecture.py --selftest      # in-memory asserts on the parsers
    python debugtests/check-architecture.py --hook          # Claude Code Stop-hook mode (see below)
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ARCH = REPO / "ARCHITECTURE.md"
PLUGIN = REPO / "WTMouseAimPlugin.cs"

# Types that live in the code but are deliberately not their own diagram node — nested helpers and
# attribute payloads. Keep this list SHORT: if something here starts carrying real behaviour, it
# has earned a row in the node index instead of an exemption.
EXEMPT_TYPES = {
    "AnFrame",                        # private nested struct: the anomaly ring-buffer frame
    "POINT",                          # Win32 interop struct
}


def strip_comments_and_strings(src: str) -> str:
    """Blank out //, /* */ and "..." so declarations inside them don't parse as real code."""
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", "", src)
    src = re.sub(r'"(?:\\.|[^"\\\n])*"', '""', src)
    return src


def top_level_types(src: str):
    """Top-level (non-nested) class/struct/enum names, i.e. brace depth 1 inside the namespace."""
    src = strip_comments_and_strings(src)
    found, depth = [], 0
    decl = re.compile(r"\b(?:class|struct|enum)\s+([A-Za-z_]\w*)")
    i = 0
    while i < len(src):
        c = src[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        else:
            m = decl.match(src, i)
            # namespace body is depth 1, so its direct children are declared at depth 1
            if m and depth == 1:
                found.append(m.group(1))
                i = m.end()
                continue
        i += 1
    return found


def harmony_targets(src: str):
    """(TypeName, "member") pairs from [HarmonyPatch(typeof(T), "m")] attributes."""
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", "", src)
    return re.findall(r'\[HarmonyPatch\(\s*typeof\(\s*([\w.]+)\s*\)\s*,\s*"([^"]+)"', src)


def plugin_version(src: str):
    m = re.search(r'PluginVersion\s*=\s*"([^"]+)"', src)
    return m.group(1) if m else None


def _literal_commas(block: str) -> int:
    """Commas in the LITERAL text of a C# string-concatenation block, ignoring $"{...}" holes.

    Both the recorder's Header const and its Sample() row are `$"a,b," + $"c,d"` chains, so the
    field count of each is (commas in the literal text) + 1. Interpolation holes are dropped first
    because a hole can contain commas-free expressions but never a field separator.
    """
    block = re.sub(r"//[^\n]*", "", block)              # trailing comments carry commas of their own
    block = re.sub(r"\{[^{}]*\}", "", block)            # $"{x:0.000}" -> ""
    return sum(s.count(",") for s in re.findall(r'"((?:\\.|[^"\\])*)"', block))


def recorder_columns(src: str):
    """(header_count, row_count) for ManeuverRecorder, or (None, None) if either can't be parsed."""
    h = re.search(r"private const string Header\s*=(.*?);\s*\n", src, flags=re.S)
    r = re.search(r"_w\.WriteLine\(\s*\n(.*?)\);\s*\n", src, flags=re.S)
    if not h or not r:
        return None, None
    return _literal_commas(h.group(1)) + 1, _literal_commas(r.group(1)) + 1


def arch_version(src: str):
    m = re.search(r"<!--\s*ARCH-VERSION:\s*([^\s>]+)\s*-->", src)
    return m.group(1) if m else None


# The drone harness (v0.87) routes uncrewed aircraft through the SAME ChaseController.Apply the
# human flies. What keeps the human's flight path out of it is one per-instance flag, `_uncrewed`,
# which gates the three things in Apply that are one-per-process and all his: the AimRig marker, the
# Rewired player-0 stick, and the FlightHud crosshair. That guarantee is only as good as its reach:
#   * ONE writer of the flag — FlyUncrewed. A second assignment anywhere (a "reset", a convenience
#     setter on the player's path) turns a compile-time-provable property into a runtime argument.
#   * ONE file calling FlyUncrewed — TestDrone.cs, whose dictionary an aircraft can only enter
#     through Spawn, which asserts `ac.Player == null`.
# Neither is visible to the type system and neither fails to compile, so check it here.
UNCREWED_FLAG = "_uncrewed"
UNCREWED_ENTRY = "FlyUncrewed"
UNCREWED_CALLERS = {"TestDrone.cs"}   # files allowed to call the uncrewed entry point


def uncrewed_isolation(sources: dict) -> list:
    """Problems with the crewed/uncrewed separation. `sources` maps filename -> C# source."""
    problems, writers, callers = [], 0, set()
    for name, src in sources.items():
        clean = strip_comments_and_strings(src)
        # assignments only: `_uncrewed = ...`, never `!_uncrewed` / `_uncrewed ?` reads
        writers += len(re.findall(rf"\b{UNCREWED_FLAG}\s*=[^=]", clean))
        # a call, not the declaration — the declaring file has a return type in front of the name
        if re.search(rf"\b{UNCREWED_ENTRY}\s*\(", clean) and not re.search(
                rf"\b\w+\s+{UNCREWED_ENTRY}\s*\(", clean):
            callers.add(name)
    if writers != 1:
        problems.append(
            f"`{UNCREWED_FLAG}` is assigned {writers} time(s); it must be exactly 1 (in "
            f"{UNCREWED_ENTRY}). More than one writer means the crewed path can reach the uncrewed "
            f"branches of ChaseController.Apply — the human's stick would fly a drone, or a drone "
            f"would drag the human's aim marker."
        )
    stray = callers - UNCREWED_CALLERS
    if stray:
        problems.append(
            f"{UNCREWED_ENTRY} is called from {sorted(stray)}; only {sorted(UNCREWED_CALLERS)} may "
            f"call it (an aircraft reaches it only via TestDrone's dictionary, which Spawn gates on "
            f"ac.Player == null)."
        )
    return problems


# =====================================================================================================
# SEGMENT TAGS: ScenarioPlayer.cs -> scorecard.py's TAG_TYPE_RULES
#
# The tag vocabulary lives in TWO places with no compile-time link — the built-in cards here in C#
# and the shipped JSON in cards/ — while the tag -> metric table lives in scorecard.py. That pair has
# already drifted once catastrophically (v0.71: 19 of 21 segments scored "unknown", every step-
# response / fine-tracking / sustained-turn metric silently uncomputed, no output at all).
# `scorecard.py --selftest` closed the DISK half of the gap; it parses cards/*.json and asserts each
# tag resolves. It cannot see a tag that exists only in C#, and two of those were broken when this
# check was written: StopRecord's `rec` and Validate's `seg<i>` fallback.
# =====================================================================================================

# The `private static Seg X(string tag, ...)` factories in ScenarioPlayer.cs. A THIRD one would carry
# tags this scan cannot see, so the set is asserted rather than assumed.
SEG_FACTORIES = ("Hold", "Walk")

# Both forms accept an optional trailing `+`, because two real sites build the tag by concatenation:
# `s.tag = "seg" + i` (Validate's fallback) and `Hold("micro" + (i + 1), ...)`. In both the suffix is
# a number, so the literal is probed with a "1" appended — which is what makes `seg\d+` / `micro\d+`
# the thing scorecard has to match, not the bare prefix.
_TAG_SITES = (
    re.compile(r'\btag\s*=\s*"([^"]*)"(\s*\+)?'),                          # object initialiser
    re.compile(r"\b(?:" + "|".join(SEG_FACTORIES) + r')\(\s*"([^"]*)"(\s*\+)?'),
)


def strip_comments(src: str) -> str:
    """Comments only — strings LEFT INTACT.

    Deliberately not strip_comments_and_strings(): that one pairs quotes left to right, and
    ScenarioPlayer.cs contains a string literal nested inside an interpolation hole (Abort's
    `{... ? seg.tag : "?"}`), after which its brace depth is wrong. Every check below either needs
    the string literals (the tag scan) or only needs to find a method's closing brace, so none of
    them can afford that. See the same warning in TestDrone.cs's log lines.
    """
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


def method_body(src: str, sig_rx: str):
    """A method's body text, or None. `src` must be comment-stripped (see strip_comments).

    Matched from the signature to the first close-brace at METHOD indent (8 spaces = a member of a
    class inside the namespace), which is the whole repo's layout. Nested blocks sit deeper, so the
    first such brace is the method's own.
    """
    m = re.search(sig_rx + r"\s*\{(.*?)\n        \}", src, flags=re.S)
    return m.group(1) if m else None


def segment_tags(src: str):
    """Every segTag ScenarioPlayer.cs's built-in cards / recorder can stamp into a capture."""
    src = strip_comments(src)
    tags = set()
    for rx in _TAG_SITES:
        for lit, concat in rx.findall(src):
            if lit:                       # `public string tag = "";` is the field default, not a tag
                tags.add(lit + "1" if concat else lit)
    return sorted(tags)


def _infer_type():
    """scorecard.py's infer_type, imported (hyphenated filenames can't be `import`ed). ~25 ms."""
    import importlib.util
    p = Path(__file__).resolve().parent / "scorecard.py"
    if not p.exists():
        return None
    try:
        spec = importlib.util.spec_from_file_location("_scorecard_for_archcheck", p)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod.infer_type
    except Exception:                     # a broken scorecard is its own problem; report, don't crash
        return None


def segment_tag_problems(scen_src: str) -> list:
    out = []
    if not scen_src:
        return out
    found = sorted(set(re.findall(r"\bstatic\s+Seg\s+(\w+)\s*\(", strip_comments(scen_src))))
    if found != sorted(SEG_FACTORIES):
        out.append(
            f"ScenarioPlayer.cs's Seg factories are {found}, expected {sorted(SEG_FACTORIES)} — the "
            f"tag scan below only reads {sorted(SEG_FACTORIES)}(\"tag\", ...), so a new factory's "
            f"tags would be invisible to it. Add it to SEG_FACTORIES."
        )
    infer = _infer_type()
    if infer is None:
        out.append("could not import debugtests/scorecard.py — segment tags went unchecked")
        return out
    for tag in segment_tags(scen_src):
        if infer(tag) == "unknown":
            out.append(
                f"segment tag '{tag}' (ScenarioPlayer.cs) resolves to \"unknown\" in scorecard.py's "
                f"TAG_TYPE_RULES, so every capture carrying it scores with the generic AoA/G metrics "
                f"only — no step response, no tracking, no turn metrics. Adding or renaming a card "
                f"segment means updating BOTH, in the same change."
            )
    return out


# =====================================================================================================
# SOURCE INVARIANTS.
#
# Every rule here is one this repo has already been burned by, and every one of them COMPILES FINE
# when broken: the mod flies, the batch completes, every capture scores, and the answer is wrong.
# That is why they are greps in the Stop hook rather than review notes. Each message names the
# invariant and the measurement that forced it.
#
# ponytail: greps, not a parser. Coarse where coarseness is safe (rule 3 is per FILE, not per method)
# — the point is to fail loudly on the shape of the regression, not to model C#.
# =====================================================================================================

def source_invariants(sources: dict) -> list:
    p = []
    scen = strip_comments(sources.get("ScenarioPlayer.cs", ""))
    drone = strip_comments(sources.get("TestDrone.cs", ""))
    plug = strip_comments(sources.get("WTMouseAimPlugin.cs", ""))

    # --- 1. `frameMs` is a FRAME time only if it is sampled per frame -------------------------
    # Time.unscaledDeltaTime read from FixedUpdate returns fixedUnscaledDeltaTime, a CONSTANT. That
    # is what v0.86..v0.92 shipped: R27's frameMs read exactly 16.70 ms on all 223,899 rows of a
    # 352-capture batch, zero variance, and missed a 119 ms hitch the log caught while four recorders
    # were sampling through it. Moving the call "for tidiness" restores the bug silently.
    upd, fx = method_body(plug, r"private void Update\(\)"), method_body(plug, r"private void FixedUpdate\(\)")
    if upd is None or fx is None:
        p.append("WTMouseAimPlugin.Update()/FixedUpdate() not found — did the MonoBehaviour change shape?")
    else:
        if "SampleFrameTime" not in upd:
            p.append(
                "TestDrone.SampleFrameTime() is not called from WTMouseAimPlugin.Update(). It MUST be: "
                "Time.unscaledDeltaTime is the rendered-frame delta only in a per-frame callback, and "
                "the recorder's frameMs column is otherwise a constant (v0.86-v0.92, R27)."
            )
        if "SampleFrameTime" in fx:
            p.append(
                "TestDrone.SampleFrameTime() is called from WTMouseAimPlugin.FixedUpdate(), where "
                "Time.unscaledDeltaTime returns the CONSTANT fixedUnscaledDeltaTime — this is exactly "
                "the v0.92.1 bug (frameMs identical on all 223,899 rows of R27)."
            )

    # --- 2. one control-law step per FIXED STEP, not per PILOT --------------------------------
    # Aircraft.pilots is an array and every Pilot fires the postfix, so a two-seater ran OnPilotStep
    # twice per fixed step: R26 flew a 30 s segment in 14.95 s on `trainer`/`FastBomber1`, and the
    # control law's integrators/rate filters double-stepped inside one physics step. The guard must
    # sit AFTER the dead/ejected despawn, or a killed crewman stops despawning the drone.
    ops = method_body(drone, r"internal static void OnPilotStep\(Pilot p\)")
    if ops is None:
        p.append("TestDrone.OnPilotStep(Pilot) not found — has the drone seam changed shape?")
    else:
        i_dead, i_step = ops.find("p.dead"), ops.find("d.LastStep == Time.fixedTime")
        if i_step < 0:
            p.append(
                "TestDrone.OnPilotStep has no `d.LastStep == Time.fixedTime` guard. Without it a "
                "TWO-SEAT airframe runs the card clock and the control law twice per fixed step "
                "(R26: a 30 s segment flown in 14.95 s, finite differences reading zero)."
            )
        elif i_dead < 0:
            p.append("TestDrone.OnPilotStep no longer checks p.dead/p.ejected — a shot-down drone never despawns.")
        elif i_dead > i_step:
            p.append(
                "TestDrone.OnPilotStep's per-fixed-step guard sits BEFORE the p.dead/p.ejected "
                "despawn. It must sit after: the guard returns early on the second seat, so a drone "
                "whose front-seater is killed would never reach the despawn (v0.90.1)."
            )

    # --- 3. the safe-teleport PAIR ------------------------------------------------------------
    # ResetGLoadTrackers (zero velocityPrev, or the game reads the teleport as G) + MoveAssembly
    # (move every part rigidbody, or the FixedJoints stretch and PhysX returns ~err/dt of velocity).
    # Both halves were learned by destroying the airframe; shipping one is shipping the crash.
    for name, src in sources.items():
        clean = strip_comments(src)
        if re.search(r"(?<!void )\bMoveAssembly\s*\(", clean) and "ResetGLoadTrackers" not in clean:
            p.append(
                f"{name} calls MoveAssembly without ResetGLoadTrackers. They are ONE primitive: the "
                f"velocity write is read as a G spike unless Pilot.velocityPrev is zeroed first, and "
                f"both halves were learned by destroying the airframe (see ScenarioPlayer.cs)."
            )

    # --- 4. card setup order in Tick ----------------------------------------------------------
    # ApplyOverrides -> ApplyArm -> StartCard, and RestoreOverrides AFTER _rec.Stop. Arm second so
    # the sweep beats a card that pins its own swept knob; both before the recorder opens (and the
    # restore after it closes) because SettingChanged stamps a '# cfg' line into every OPEN capture —
    # a card's own setup landing in its own CSV reads as the law changing mid-run.
    tick = method_body(scen, r"public void Tick\(Aircraft ac\)")
    if tick is None:
        p.append("ScenarioPlayer.Tick(Aircraft) not found.")
    else:
        want = ("ApplyOverrides(", "ApplyArm(", "StartCard(")
        at = [tick.find(s) for s in want]
        if min(at) < 0:
            p.append(f"ScenarioPlayer.Tick no longer calls all of {want} — the card setup path moved.")
        elif at != sorted(at):
            p.append(
                f"ScenarioPlayer.Tick calls the card setup out of order (found {want} at {at}). It "
                f"must be ApplyOverrides -> ApplyArm -> StartCard: arm-after-overrides makes the "
                f"swept arm win, and both-before-the-recorder keeps a card's own setup out of its "
                f"own capture's '# cfg' lines."
            )
    for meth, rx in (("Finish", r"private void Finish\(string reason\)"),
                     ("NextCard", r"private void NextCard\(\)")):
        b = method_body(scen, rx)
        if b is None:
            p.append(f"ScenarioPlayer.{meth} not found.")
            continue
        i_stop, i_restore = b.find("_rec.Stop"), b.find("RestoreOverrides()")
        if i_stop < 0 or i_restore < 0:
            p.append(f"ScenarioPlayer.{meth} no longer both stops the recorder and restores the card's overrides.")
        elif i_restore < i_stop:
            p.append(
                f"ScenarioPlayer.{meth} restores the card's config overrides BEFORE _rec.Stop. "
                f"Restoring fires SettingChanged, which stamps a '# cfg' line into the still-open "
                f"capture — reading as the law changing during the run it just finished."
            )

    # --- 5. every entry-speed read routes through the ONE resolver ----------------------------
    # v0.93: a card may declare its entry speed as a multiple of the lane airframe's corner speed.
    # Converting only the spawn is the failure this design exists to prevent — the aircraft would be
    # placed at 180 m/s while the gate still demanded the card's raw 250 and refused the run forever.
    for name in ("ScenarioPlayer.cs", "TestDrone.cs"):
        for i, line in enumerate(strip_comments(sources.get(name, "")).splitlines(), 1):
            if not re.search(r"\.startSpeed\b", line):
                continue
            # The resolver itself, and Preview's deliberate carry of the (speed, corner) PAIR — it is
            # answered with no aircraft in hand, so there is no lane to resolve against yet.
            if "ResolveStartSpeed(" in line or ".StartSpeed =" in line:
                continue
            p.append(
                f"{name}:{i} reads Card.startSpeed directly ({line.strip()}). Every playback-path "
                f"read must go through ScenarioPlayer.ResolveStartSpeed / EffectiveStartSpeed, or a "
                f"startSpeedCorner card is checked at one speed and placed at another (v0.93)."
            )

    # --- 6. both removal paths drop every per-aircraft registry -------------------------------
    # ForgetState exists so the next registry added cannot be forgotten on one of the two paths;
    # missing it leaves a StreamWriter alive past its aircraft and a capture with no '# stop' line,
    # which reads as a clean run.
    for meth, rx in (("Despawn", r"public static void Despawn\(Drone d[^)]*\)"),
                     ("PruneDead", r"private static void PruneDead\(\)")):
        b = method_body(drone, rx)
        if b is None:
            p.append(f"TestDrone.{meth} not found.")
        elif "ForgetState(" not in b:
            p.append(
                f"TestDrone.{meth} does not call ForgetState. BOTH removal paths must: a despawned "
                f"drone otherwise leaves its recorder open (a capture with no '# stop' line reads as "
                f"a clean run) and its arm assignment behind for a recycled instance id."
            )

    # --- 7. the crewed/uncrewed proof's root ---------------------------------------------------
    # The whole uncrewed-isolation argument above is "an aircraft can only enter TestDrone's
    # dictionary through Spawn, which asserts ac.Player == null". Delete the assert and the postfix
    # can write ControlInputs for an aircraft somebody is sitting in.
    spawn = method_body(drone, r"public static Drone Spawn\([^)]*\)")
    if spawn is None:
        p.append("TestDrone.Spawn not found.")
    elif "ac.Player != null" not in spawn:
        p.append(
            "TestDrone.Spawn no longer verifies `ac.Player == null` before registering. That check is "
            "the root of the crewed/uncrewed separation — the postfix writes ControlInputs for "
            "everything in the dictionary, so a player aircraft in it means the harness flies the human."
        )
    return p


def check(fix_version: bool) -> int:
    if not ARCH.exists():
        print(f"FAIL  {ARCH.name} is missing — the architecture diagram is not optional.")
        return 1

    arch = ARCH.read_text(encoding="utf-8")
    cs_files = sorted(p for p in REPO.glob("*.cs"))
    if not cs_files:
        print("FAIL  no *.cs at the repo root — is this the right directory?")
        return 1

    problems = []

    # --- version stamp ------------------------------------------------------------------
    ver = plugin_version(PLUGIN.read_text(encoding="utf-8")) if PLUGIN.exists() else None
    stamp = arch_version(arch)
    if ver is None:
        problems.append("could not read PluginVersion from WTMouseAimPlugin.cs")
    elif stamp is None:
        problems.append("ARCHITECTURE.md has no <!-- ARCH-VERSION: x.y.z --> stamp")
    elif stamp != ver:
        if fix_version:
            arch = re.sub(r"(<!--\s*ARCH-VERSION:\s*)[^\s>]+(\s*-->)", rf"\g<1>{ver}\g<2>", arch)
            ARCH.write_text(arch, encoding="utf-8")
            print(f"fixed  ARCH-VERSION {stamp} -> {ver}")
        else:
            problems.append(
                f"ARCH-VERSION is {stamp} but PluginVersion is {ver} "
                f"— re-read the diagram, then run with --fix-version"
            )

    # --- files + types in the node index ------------------------------------------------
    all_types, patch_targets, sources = set(), [], {}
    for p in cs_files:
        src = p.read_text(encoding="utf-8")
        sources[p.name] = src
        if f"`{p.name}`" not in arch:
            problems.append(f"{p.name} is not named in ARCHITECTURE.md (add a node-index row)")
        for t in top_level_types(src):
            if t in EXEMPT_TYPES:
                continue
            all_types.add(t)
            if f"`{t}`" not in arch:
                problems.append(f"type {t} ({p.name}) is not in the node index")
        patch_targets += harmony_targets(src)

    # --- Harmony targets in the game-types table ---------------------------------------
    for typ, member in patch_targets:
        if f"`{typ}.{member}`" not in arch:
            problems.append(
                f"Harmony patch {typ}.{member} is not listed in the "
                f"'Game types we patch or read' table"
            )

    # --- crewed / uncrewed separation (v0.87) -------------------------------------------
    problems += uncrewed_isolation(sources)

    # --- segment tag vocabulary + the source invariants ---------------------------------
    problems += segment_tag_problems(sources.get("ScenarioPlayer.cs", ""))
    problems += source_invariants(sources)

    # --- recorder CSV contract ----------------------------------------------------------
    # The header string and the Sample() row are two hand-maintained lists that MUST stay in
    # lockstep; nothing in C# links them, and a mismatch does not fail to compile — it produces
    # a capture whose columns are silently shifted from the names above them, which every offline
    # tool then reads as real data. Cheap to check mechanically, so check it.
    rec_cs = REPO / "Recording.cs"
    ncols = None
    if rec_cs.exists():
        nh, nr = recorder_columns(rec_cs.read_text(encoding="utf-8"))
        if nh is None:
            problems.append("could not parse ManeuverRecorder's Header / Sample row (did the shape change?)")
        elif nh != nr:
            problems.append(
                f"Recording.cs: the CSV header has {nh} columns but the Sample() row writes {nr} "
                f"— they must stay in lockstep, and new columns append at the END"
            )
        else:
            ncols = nh
            # CLAUDE.md documents the count; keep that honest too, it is what an agent reads first.
            claude = REPO / "CLAUDE.md"
            if claude.exists():
                m = re.search(r"\((\d+) CSV columns", claude.read_text(encoding="utf-8"))
                if m and int(m.group(1)) != ncols:
                    problems.append(
                        f"CLAUDE.md says '{m.group(1)} CSV columns' but Recording.cs writes {ncols}"
                    )

    # --- stale node-index rows ----------------------------------------------------------
    # Types the index claims exist. Parsed from the type(s) COLUMN only — scraping the whole
    # table also catches prose like `PluginVersion` in the role column, which is not a type.
    idx = re.search(r"\n## Node index\n(.*?)\n### Game types", arch, flags=re.S)
    if idx:
        claimed = set()
        for row in idx.group(1).splitlines():
            cells = [c.strip() for c in row.strip().strip("|").split("|")]
            if len(cells) < 4 or cells[0] in ("node id", "---") or set(cells[0]) <= {"-", " "}:
                continue
            claimed |= set(re.findall(r"`([A-Z]\w+)`", cells[2]))
        for t in sorted(claimed - all_types - EXEMPT_TYPES):
            problems.append(f"node index names `{t}`, which no longer exists in any *.cs")
    else:
        problems.append("could not find the '## Node index' section in ARCHITECTURE.md")

    if problems:
        print(f"ARCHITECTURE.md is out of date ({len(problems)} problem(s)):\n")
        for pr in problems:
            print(f"  - {pr}")
        print("\nUpdate the diagram + node index in the same change as the code.")
        return 1

    print(
        f"ok  ARCHITECTURE.md matches the code "
        f"({len(cs_files)} files, {len(all_types)} types, {len(patch_targets)} Harmony patches, v{ver}"
        + (f", {ncols} CSV columns)" if ncols else ")")
    )
    return 0


def hook_mode() -> int:
    """Claude Code Stop-hook entry point (wired up in .claude/settings.json).

    Runs when an agent finishes a turn — the moment the diagram *should* be current, rather than
    nagging on every intermediate edit of a refactor. Exit 2 feeds the message back to the agent as
    actionable feedback so it can fix the drift before handing back.

    Reads the hook payload on stdin. Honours `stop_hook_active`: if we already blocked once and the
    agent still could not resolve it, let the turn end rather than looping forever.
    """
    try:
        payload = json.loads(sys.stdin.read() or "{}")
    except (json.JSONDecodeError, ValueError):
        payload = {}
    if payload.get("stop_hook_active"):
        return 0

    # Only speak up in a repo that actually has the diagram (e.g. a partial checkout).
    if not ARCH.exists():
        return 0

    # Reuse the real check, but keep its stdout out of the transcript on success.
    devnull = open(os.devnull, "w")
    real_stdout, sys.stdout = sys.stdout, devnull
    try:
        rc = check(fix_version=False)
    finally:
        sys.stdout = real_stdout
        devnull.close()

    if rc == 0:
        return 0

    # Re-run visibly so the agent sees exactly which rows are missing.
    print(
        "ARCHITECTURE.md is out of date with the code. Update the affected L1 diagram and the "
        "node index before finishing (see CLAUDE.md 'Keeping the diagram current'). Details:",
        file=sys.stderr,
    )
    real_out, sys.stdout = sys.stdout, sys.stderr
    try:
        check(fix_version=False)
    finally:
        sys.stdout = real_out
    return 2  # exit 2 = feed stderr back to the agent


def selftest() -> int:
    src = """
    namespace N {
        // class CommentedOut
        internal enum Mode { A, B }
        internal static class Outer {
            private struct Nested { public float t; }
            private const string S = "class NotAType";
        }
        [HarmonyPatch(typeof(Foo), "Bar")]
        internal static class FooPatch { }
    }
    """
    types = top_level_types(src)
    assert types == ["Mode", "Outer", "FooPatch"], types
    assert "Nested" not in types
    assert "CommentedOut" not in types
    assert "NotAType" not in types
    assert harmony_targets(src) == [("Foo", "Bar")]
    assert plugin_version('public const string PluginVersion = "0.58.0";') == "0.58.0"
    assert arch_version("<!-- ARCH-VERSION: 0.58.0 -->") == "0.58.0"

    # Recorder header/row lockstep. The fake below mirrors the real shape: a concatenated header
    # with an interleaved comment, and a row whose interpolation holes contain format specifiers,
    # a ternary and a subtraction — none of which may be miscounted as a field separator.
    rec = '''
        private const string Header =
            "t,off,azErr," +
            // a comment, with a comma in it
            "phase,flyLevel," +
            "frameMs";

        public void Sample(float t, bool flyLevel, float segStart)
        {
            _w.WriteLine(
                $"{t:0.000},{off:0.00},{azErr:0.00}," +
                $"{phase},{(flyLevel ? 1 : 0)}," +
                $"{(now - segStart) * 1000f:0.0}");
        }
    '''
    assert recorder_columns(rec) == (6, 6), recorder_columns(rec)
    # ...and it must FAIL when they drift, which is the only thing this check is for.
    assert recorder_columns(rec.replace('"frameMs"', '"frameMs,extra"')) == (7, 6)
    assert recorder_columns("no recorder here") == (None, None)

    # Crewed/uncrewed separation. The good shape passes; each way of breaking it is caught.
    ok = {
        "ChaseController.cs": """
            private bool _uncrewed;
            internal bool FlyUncrewed(Aircraft ac, Vector3 aimDir) { _uncrewed = true; return true; }
            void Apply() { if (Cfg.ManualOverride.Value && !_uncrewed) { } }
        """,
        "TestDrone.cs": "bool ChaseCard(Drone d) { return ChaseController.For(ac).FlyUncrewed(ac, v); }",
    }
    assert uncrewed_isolation(ok) == [], uncrewed_isolation(ok)
    # a second writer (the flag stops being provable from the call graph)
    bad = dict(ok, **{"ChaseController.cs": ok["ChaseController.cs"] + "\nvoid R() { _uncrewed = false; }"})
    assert len(uncrewed_isolation(bad)) == 1
    # the player's seam calling the uncrewed entry point
    bad = dict(ok, **{"ScenarioPlayer.cs": "void T() { ChaseController.For(ac).FlyUncrewed(ac, v); }"})
    assert len(uncrewed_isolation(bad)) == 1
    # a comment mentioning either is not a call and not a write
    ok2 = dict(ok, **{"Cfg.cs": "// FlyUncrewed sets _uncrewed = true; see ChaseController"})
    assert uncrewed_isolation(ok2) == [], uncrewed_isolation(ok2)

    # Segment-tag scan. Every real shape: the object initialiser, the two Seg factories, and the two
    # CONCATENATED sites (Validate's `"seg" + i` and FixedWingSegs' `"micro" + (i + 1)`) whose literal
    # is a prefix — probed with a "1" so it lands on scorecard's seg\d+ / micro\d+ rules rather than
    # on the bare prefix, which matches nothing.
    tagsrc = '''
        public string tag = "";                       // the field default, not a tag
        // tag = "commented"
        var t = new Seg { tag = "rec", dur = 1f };
        s.tag = "seg" + i;
        s.Add(Hold("arm", 4f, az0, 0f));
        s.Add(Hold("micro" + (i + 1), 2f, cur, 0f));
        s.Add(Walk("fine", 20f, cur, 0f, 0.3f, 1337));
    '''
    assert segment_tags(tagsrc) == ["arm", "fine", "micro1", "rec", "seg1"], segment_tags(tagsrc)

    # method_body: the first close-brace at METHOD indent ends it, nested blocks do not.
    mb = '''
        private void Finish(string reason)
        {
            _rec.Stop(reason);
            if (x) { RestoreOverrides(); }
        }

        private void Other() { }
    '''
    b = method_body(mb, r"private void Finish\(string reason\)")
    assert b is not None and "RestoreOverrides" in b and "Other" not in b, b
    assert method_body(mb, r"private void Nope\(\)") is None

    # source_invariants: the good shape passes, and each documented regression is caught. Only the
    # files each rule reads are supplied; a missing file makes its rules no-ops, never crashes.
    good = {
        "WTMouseAimPlugin.cs": '''
        private void Update()
        {
            TestDrone.SampleFrameTime();
        }

        private void FixedUpdate()
        {
            TestDrone.FixedTick();
        }
    ''',
        "TestDrone.cs": '''
        internal static void OnPilotStep(Pilot p)
        {
            if (p.dead || p.ejected) { Despawn(d, "x"); return; }
            if (d.LastStep == Time.fixedTime) return;
        }

        public static void Despawn(Drone d, string reason = "requested")
        {
            ForgetState(d.AircraftId);
        }

        private static void PruneDead()
        {
            ForgetState(d.AircraftId);
        }

        public static Drone Spawn(string jsonKey, Vector3 p)
        {
            if (ac.Player != null) return null;
        }
    ''',
        "ScenarioPlayer.cs": '''
        public void Tick(Aircraft ac)
        {
            ApplyOverrides(_card);
            ApplyArm();
            StartCard(ac);
        }

        private void Finish(string reason)
        {
            _rec.Stop(reason);
            RestoreOverrides();
        }

        private void NextCard()
        {
            _rec.Stop("done");
            RestoreOverrides();
        }
    ''',
        "PlayerSpawn.cs": "ResetGLoadTrackers(ac); MoveAssembly(ac, rb, q, v, w);",
    }
    assert source_invariants(good) == [], source_invariants(good)

    def broken(name, old, new):
        d = dict(good)
        d[name] = d[name].replace(old, new)
        assert d[name] != good[name], (name, old)          # the substitution itself must have landed
        return len(source_invariants(d))

    # 1. frameMs sampled from the fixed step again (v0.86-v0.92, R27) — caught twice, once per half:
    #    missing from Update, and present in FixedUpdate.
    moved = dict(good, **{"WTMouseAimPlugin.cs": '''
        private void Update()
        {
            AimRig.Update();
        }

        private void FixedUpdate()
        {
            TestDrone.SampleFrameTime();
            TestDrone.FixedTick();
        }
    '''})
    assert len(source_invariants(moved)) == 2, source_invariants(moved)
    # 2. the two-seat double-step guard, removed and then moved ahead of the despawn (v0.90.1).
    assert broken("TestDrone.cs", "if (d.LastStep == Time.fixedTime) return;", "") == 1
    assert broken("TestDrone.cs",
                  'if (p.dead || p.ejected) { Despawn(d, "x"); return; }\n            if (d.LastStep == Time.fixedTime) return;',
                  'if (d.LastStep == Time.fixedTime) return;\n            if (p.dead || p.ejected) { Despawn(d, "x"); return; }') == 1
    # 3. half the safe-teleport primitive.
    assert broken("PlayerSpawn.cs", "ResetGLoadTrackers(ac); ", "") == 1
    # 4. card setup order, both halves.
    assert broken("ScenarioPlayer.cs", "ApplyOverrides(_card);\n            ApplyArm();",
                  "ApplyArm();\n            ApplyOverrides(_card);") == 1
    assert broken("ScenarioPlayer.cs", '_rec.Stop("done");\n            RestoreOverrides();',
                  'RestoreOverrides();\n            _rec.Stop("done");') == 1
    # 5. an entry-speed read that skips the resolver.
    assert broken("ScenarioPlayer.cs", "StartCard(ac);",
                  "StartCard(ac);\n            float v = c.startSpeed;") == 1
    # ...and the two deliberate exemptions stay silent: the resolver's own call, and Preview carrying
    # the (speed, corner) pair for a question asked with no lane in hand.
    assert broken("ScenarioPlayer.cs", "StartCard(ac);",
                  "StartCard(ac);\n            float v = ResolveStartSpeed(c.startSpeed, c.startSpeedCorner, k);"
                  "\n            p.StartSpeed = c.startSpeed;") == 0
    # 6. one removal path forgetting the registries.
    assert broken("TestDrone.cs", "        {\n            ForgetState(d.AircraftId);\n        }\n\n        private static void PruneDead()",
                  "        {\n            _live.Remove(d);\n        }\n\n        private static void PruneDead()") == 1
    # 7. the assert the whole crewed/uncrewed proof rests on.
    assert broken("TestDrone.cs", "if (ac.Player != null) return null;", "") == 1

    # The real file, if we are running inside the repo: header and row must agree today.
    real = REPO / "Recording.cs"
    if real.exists():
        nh, nr = recorder_columns(real.read_text(encoding="utf-8"))
        assert nh is not None, "Recording.cs no longer parses — fix recorder_columns()"
        assert nh == nr, f"Recording.cs header {nh} != row {nr}"
    print("ok  selftest passed")
    return 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--fix-version", action="store_true", help="sync the ARCH-VERSION stamp to PluginVersion")
    ap.add_argument("--selftest", action="store_true", help="run in-memory asserts on the parsers")
    ap.add_argument("--hook", action="store_true", help="Claude Code Stop-hook mode (reads stdin JSON)")
    a = ap.parse_args()
    if a.selftest:
        sys.exit(selftest())
    sys.exit(hook_mode() if a.hook else check(a.fix_version))
