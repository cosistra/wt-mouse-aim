#!/usr/bin/env python3
"""Check that a drone lane which DIES and RESPAWNS recovers its replicates without a new confound.

The harness respawns a lane whose aircraft is destroyed, crashed or otherwise unflyable, so that the
lane's remaining replicates fly on fresh metal instead of dropping out of the batch (v1.0.2). The
data recovery is the obvious half. The half this check exists for is the other one:

  **A DEAD LANE SILENTLY CORRUPTS THE A/B DESIGN.** Replicates are armed ABBA and indexed by the
  lane's own position in its own queue (`ApplyArm`: `ArmOfRun(_qi / _block, _resumeRep)`). That is
  balanced — equal counts AND equal mean position, which is the property that cancels a one-way
  session drift — only when the lane flies ALL of it. A lane that dies at replicate 3 of 9 flew a truncated,
  leaning sequence, and NOTHING at runtime notices: `SetUpArmSchedule` prints the schedule the lane
  INTENDED to fly, `compare-runs.py` groups by (airframe, card, arm) and happily pools whatever
  arrived. Exactly the R21 confound ABBA was built to kill, reintroduced by one airframe losing a
  wing.

So the property asserted here is compositional and cannot be checked by either piece alone:

    a lane that dies at ANY replicate and resumes where it left off flies every queue index an
    undamaged lane would have flown — no replicate twice, none skipped...

...and the second half, which is where the respawn feature and v1.0.1's anchor fix actually collide
(ledger `X27`):

    ...and NO REPLICATE IT SCORES IS ANCHOR-CAPTURING.

A resumed lane is on FRESH METAL, so `StartSuite` re-anchors and its first placement captures the
run anchor: `snapBackM` ~0, flying from the spawn state while every sibling arrives teleported —
the *exact* stratum #55b removed from the arms one release earlier, reappearing at replicate 3 of 9
instead of replicate 0, where nothing keyed on the index could see it. `ArmOfRun(replicate, resume)`
arms that replicate as **neither**, so the recovered sequence is an undamaged lane's minus one
scored replicate rather than plus one confound. That cost is the point of the third property below:
it must be exactly one replicate per respawn, and it must be the anchor-capturing one.

...plus the bound, which is the other way this feature can burn a batch: R41's UtilityHelo1 sank on
16 of 16 replicates, and an uncapped rule would have relaunched it sixteen times and still produced
nothing. `MaxLaneRespawns` stops the lane, and the stop has to be a hard one.

Both halves live between markers in plain C# with no Unity/BepInEx type in them; this extracts the
`LANE-CONTINUITY` region (the resume index + the cap) together with `ARM-SCHEDULE` (the ABBA index),
wraps them in a throwaway console project and runs the .NET SDK over the cases below — the same trick
`test-arm-schedule.py`, `test-board-math.py` and `test-card-model.py` use, and for the same reason: a
Python reimplementation of the sequence would drift from the code and then agree with itself forever.

Plus five SOURCE assertions the regions cannot make about themselves — that the harness still asks
the card player what a lane owes, that it asks BEFORE the teardown that destroys the answer, that the
respawn goes through the ONE shared lane-spawn path, that the panic key does not respawn a fleet, and
that the resume reaches `StartSuite`. (`ApplyArm`/`SetUpArmSchedule` going through `ArmOfRun` is
asserted by `test-arm-schedule.py`, which owns that region — one assertion, one home.)

Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-lane-respawn.py
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

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>lanerespawn</AssemblyName>
    <RootNamespace>lanerespawn</RootNamespace>
  </PropertyGroup>
</Project>
"""

BANNED = ("UnityEngine", "Mathf.", "Vector3", "Aircraft", "ConfigEntry", "BepInEx")


def extract(path: Path, name: str) -> str:
    """The C# between the <name> BEGIN/END markers in `path`, verbatim."""
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
                f"FAIL  the {name} region now references {banned}; it must stay pure (plain types "
                f"only) or it cannot be compiled outside the game."
            )
    return body


def method_body(src: str, sig_rx: str):
    """The braces-balanced body of the first method matching `sig_rx`, or None."""
    m = re.search(sig_rx, src)
    if not m:
        return None
    i = src.find("{", m.end())
    if i < 0:
        return None
    depth = 0
    for j in range(i, len(src)):
        if src[j] == "{":
            depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0:
                return src[i + 1:j]
    return None


def source_checks() -> list:
    """What the extracted arithmetic cannot say about itself: how the harness wires it up."""
    bad = []
    scen = SCEN.read_text(encoding="utf-8")
    drone = DRONE.read_text(encoding="utf-8")

    # 1. THE SEAM. The harness owns lanes; only the card player knows what one still owes. If
    #    LaneLost stops asking, every removal path silently goes back to dropping the lane.
    lost = method_body(drone, r"private static void LaneLost\(Drone d, string reason\)")
    if lost is None:
        bad.append("TestDrone.LaneLost(Drone, string) not found — has the respawn seam moved?")
    else:
        if "ScenarioPlayer.OwedBy(" not in lost or "ScenarioPlayer.RespawnAt(" not in lost:
            bad.append(
                "TestDrone.LaneLost no longer asks ScenarioPlayer.OwedBy / RespawnAt. Those two are "
                "the whole decision: what the lane still owes, and whether the cap allows it."
            )
        if "Player != null" not in lost:
            bad.append(
                "TestDrone.LaneLost lost its `Player != null` refusal. You cannot respawn a human — "
                "it is unreachable by construction (Spawn destroys anything reporting a Player), but "
                "this is the one place that would otherwise put NEW metal under a crewed card run."
            )

    # 2. ORDER. ForgetState aborts the card and drops `_queue`, and `_queue` IS the record of what the
    #    lane owed. Asking after it is asking a torn-down object — it compiles, it flies, and every
    #    lane silently reports owing nothing.
    for meth, rx in (("Despawn", r"public static void Despawn\(Drone d[^)]*\)"),
                     ("PruneDead", r"private static void PruneDead\(\)")):
        b = method_body(drone, rx)
        if b is None:
            bad.append(f"TestDrone.{meth} not found.")
            continue
        i_lost, i_forget = b.find("LaneLost("), b.find("ForgetState(")
        if i_lost < 0:
            bad.append(
                f"TestDrone.{meth} does not call LaneLost. BOTH removal paths must: a lane whose "
                f"aircraft the game took is exactly the case this exists for, and the path that "
                f"misses it drops the lane's remaining replicates and unbalances its ABBA silently."
            )
        elif 0 <= i_forget < i_lost:
            bad.append(
                f"TestDrone.{meth} calls LaneLost AFTER ForgetState. ForgetState aborts the card and "
                f"nulls the queue, which is the only record of what the lane still owed — asked "
                f"afterwards, every lane reports owing nothing and no lane is ever respawned."
            )

    # 3. ONE SPAWN PATH. A respawned lane must come back on its own azimuth, deck, airframe and entry
    #    speed, or its capture is not comparable with the ones the lost aircraft already wrote — and
    #    the only way to guarantee that is to reuse the lane-spawn function rather than copy it.
    rel = method_body(drone, r"private static void RelaunchOwedLanes\(\)")
    if rel is None:
        bad.append("TestDrone.RelaunchOwedLanes() not found — has the respawn drain moved?")
    elif "LaunchLane(" not in rel:
        bad.append(
            "TestDrone.RelaunchOwedLanes does not go through LaunchLane. A second spawn site is the "
            "one thing that makes a respawned lane's capture non-comparable with its own earlier "
            "ones — the ring geometry, deck, per-lane airframe and per-lane entry speed are all part "
            "of the entry condition."
        )
    if "Spawn(" not in (method_body(drone, r"private static void LaunchLane\([^)]*\)") or ""):
        bad.append("TestDrone.LaunchLane no longer spawns — has the shared lane path changed shape?")

    # 4. THE PANIC KEY IS AN ABORT. Without the guard, DespawnAll's teardown routes every live lane
    #    through LaneLost and the key that clears the sky is followed by the fleet respawning itself.
    da = method_body(drone, r"public static void DespawnAll\(\)")
    if da is None:
        bad.append("TestDrone.DespawnAll() not found.")
    elif "_cancelling" not in da:
        bad.append(
            "TestDrone.DespawnAll does not set the _cancelling guard around its despawn loop. The "
            "panic key is an ABORT: without it every torn-down lane still owes replicates, so "
            "clearing the sky queues a whole fleet of respawns one fixed step behind it."
        )

    # 5. ...and the resume actually reaches the card player, or a replacement restarts at 0 and
    #    re-flies replicates that already have captures.
    ops = method_body(drone, r"internal static void OnPilotStep\(Pilot p\)")
    if ops is None:
        bad.append("TestDrone.OnPilotStep(Pilot) not found.")
    elif "StartSuite(ac, d.ResumeAt" not in ops:
        bad.append(
            "TestDrone.OnPilotStep does not pass d.ResumeAt into StartSuite. A replacement aircraft "
            "would restart its lane's queue at 0 — re-flying replicates that already wrote captures "
            "and leaving the lane's arm tally leaning, which is the defect the respawn exists to fix."
        )
    if not re.search(r"internal void StartSuite\(Aircraft ac, int startAt = 0, int respawn = 0\)", scen):
        bad.append(
            "ScenarioPlayer.StartSuite no longer takes (startAt, respawn) with defaults. The defaults "
            "are what keep the player's run key and every existing caller byte-identical."
        )
    return bad


def program(sched: str, lane: str) -> str:
    return f"""using System;

// The shipped ARM-SCHEDULE and LANE-CONTINUITY regions, verbatim, in one class.
internal static class S
{{
{sched}
{lane}
}}

internal static class P
{{
    static int fails;
    static void Ok(bool cond, string what)
    {{
        if (!cond) {{ Console.WriteLine($"  FAIL {{what}}"); fails++; }}
    }}

    // What one lane actually flies, given that it loses its aircraft at each of `deaths` (queue
    // indices, in order). One row per queue index flown: queue index, arm, and 1 if this was the
    // FIRST row of its suite. This is the harness's loop written out: fly until you die, ask what you
    // owe, respawn if the cap allows, resume there. The replicate that DIED is still flown (it is an
    // abort with its own truncated capture) — the resume is the one after it, which is why nothing
    // is flown twice.
    //
    // `resumeRep` is the harness's `_resumeRep`, per SUITE: every StartSuite sets `_anchorSet =
    // false`, so each suite anchors on its own first replicate and arms it as neither. That is the
    // one line of this model carrying the X27 fix, and case 2's invariant is asserted against the
    // "first row of its suite" flag rather than against `resumeRep` — so the model cannot pass by
    // agreeing with itself.
    static System.Collections.Generic.List<int[]> Fly(int queueLen, int nCards, int[] deaths, out int respawns)
    {{
        var flown = new System.Collections.Generic.List<int[]>();
        respawns = 0;
        int qi = 0, resumeRep = 0;
        bool first = true;
        foreach (int deathAt in deaths)
        {{
            for (; qi <= deathAt && qi < queueLen; qi++)
            {{
                flown.Add(new[] {{ qi, S.ArmOfRun(qi / nCards, resumeRep), first ? 1 : 0 }});
                first = false;
            }}
            int resume = S.RespawnAt(S.OwedFrom(deathAt, queueLen), respawns);
            if (resume < 0) return flown;            // the lane is out: finished, or the cap refused
            respawns++;
            qi = resume;
            resumeRep = resume / nCards;             // fresh metal ⇒ StartSuite re-anchors HERE
            first = true;
        }}
        for (; qi < queueLen; qi++)
        {{
            flown.Add(new[] {{ qi, S.ArmOfRun(qi / nCards, resumeRep), first ? 1 : 0 }});
            first = false;
        }}
        return flown;
    }}

    static int Main()
    {{
        // --- 1. THE RESUME IS THE REPLICATE AFTER THE ONE THAT DIED --------------------------
        // Never the one that died (it is an abort with a capture already on disk) and never one
        // beyond it (that would silently skip a replicate and shorten the lane's sequence).
        for (int len = 1; len <= 12; len++)
            for (int qi = 0; qi < len; qi++)
                Ok(S.OwedFrom(qi, len) == (qi + 1 < len ? qi + 1 : -1),
                   $"OwedFrom({{qi}},{{len}}) = {{S.OwedFrom(qi, len)}}");
        // A lane that finished owes nothing. NextCard walks `_qi` off the end, so this is the
        // ordinary suite-complete despawn and it must never respawn anything.
        Ok(S.OwedFrom(8, 8) == -1, "a lane whose cursor walked off the end owes nothing");
        Ok(S.OwedFrom(0, 0) == -1, "a lane that never had a queue owes nothing");

        // --- 2. THE TWO PROPERTIES THAT MATTER --------------------------------------------------
        // (a) a lane that dies flies EVERY queue index an undamaged one would — nothing skipped,
        //     nothing re-flown. That is the recovery.
        // (b) NO SCORED ROW IS ANCHOR-CAPTURING (ledger X27). Asserted against "was this the first
        //     row its suite flew?", which is what anchor-capturing physically means — StartSuite
        //     sets `_anchorSet = false`, so the first placement of every suite captures the anchor
        //     and its `# entry` reads snapBackM=0. Stated that way the property survives someone
        //     rewriting ArmOfRun, and it is the same property test-arm-schedule.py case 1b states
        //     for an undamaged lane. Without ArmOfRun this fails at every death point.
        foreach (int nCards in new[] {{ 1, 2, 3 }})
        foreach (int reps in new[] {{ 4, 8, 9 }})
        {{
            int len = reps * nCards;
            var whole = Fly(len, nCards, new int[0], out _);
            for (int deathAt = 0; deathAt < len; deathAt++)
            {{
                var got = Fly(len, nCards, new[] {{ deathAt }}, out int used);
                Ok(got.Count == whole.Count, $"{{nCards}}x{{reps}}, died at {{deathAt}}: flew "
                   + $"{{got.Count}} of {{len}} replicate-slots — a respawned lane must fly them all");
                bool same = got.Count == whole.Count;
                for (int i = 0; same && i < got.Count; i++) same = got[i][0] == whole[i][0];
                Ok(same, $"{{nCards}}x{{reps}}, died at {{deathAt}}: the queue indices flown differ "
                   + "from an undamaged lane's — something was skipped or re-flown");

                // (b) THE INVARIANT.
                foreach (var row in got)
                    Ok(row[2] == 0 || row[1] < 0,
                       $"{{nCards}}x{{reps}}, died at {{deathAt}}: queue index {{row[0]}} is the first "
                       + $"placement of its suite — anchor-capturing, snapBackM=0 — and is armed "
                       + $"{{row[1]}}. A SCORED anchor-capturing replicate is #55b (ledger X27)");

                // ...and the recovery is still real: exactly the anchor-capturing rows are lost, so
                // the shortfall is at most one REPLICATE per respawn. A fix that unscored the whole
                // resumed tail would also satisfy (b) and would be worthless.
                int scored = 0, wholeScored = 0;
                foreach (var row in got) if (row[1] >= 0) scored++;
                foreach (var row in whole) if (row[1] >= 0) wholeScored++;
                Ok(scored >= wholeScored - nCards * used,
                   $"{{nCards}}x{{reps}}, died at {{deathAt}}: scored {{scored}} of an undamaged "
                   + $"{{wholeScored}} after {{used}} respawn(s) — the resume may cost the lane its "
                   + "anchor-capturing REPLICATE and nothing more");

                // ...and the counterfactual: plain ArmOf — which is what a resume-unaware schedule
                // computes — SCORES that first placement whenever the resume lands past replicate 0.
                foreach (var row in got)
                    if (row[2] == 1 && row[0] / nCards > 0)
                        Ok(S.ArmOf(row[0] / nCards) >= 0,
                           $"counterfactual check is vacuous at queue index {{row[0]}} — plain ArmOf "
                           + "was supposed to SCORE the resumed replicate, which is the defect");
            }}
        }}

        // --- 2b. AND THE COST IS EXACTLY ONE REPLICATE, NAMED --------------------------------
        // One card per replicate, so replicate == queue index and nothing splits: a 9-replicate lane
        // that dies on replicate 3 resumes on replicate 4, and the ONLY difference from an undamaged
        // lane is that replicate 4 is now a warm-up. Not "the tail is unscored", not "the lane is
        // fine" — one replicate, and the operator is told (see StartSuite's resume line and
        // SetUpArmSchedule's tally, which is computed through the same ArmOfRun).
        {{
            var whole = Fly(9, 1, new int[0], out _);
            var got   = Fly(9, 1, new[] {{ 3 }}, out int used);
            Ok(used == 1, $"one death, one respawn — got {{used}}");
            int diffs = 0, warmGot = 0, warmWhole = 0;
            for (int i = 0; i < 9; i++)
            {{
                if (got[i][1] != whole[i][1]) {{ diffs++; Ok(i == 4,
                    $"replicate {{i}} changed arm ({{whole[i][1]}} -> {{got[i][1]}}) — only the "
                    + "resumed replicate 4 may"); }}
                if (got[i][1] < 0) warmGot++;
                if (whole[i][1] < 0) warmWhole++;
            }}
            Ok(diffs == 1, $"{{diffs}} replicate(s) changed arm, want exactly 1 (the resume)");
            Ok(warmWhole == 1 && warmGot == 2,
               $"warm-ups: undamaged {{warmWhole}} (replicate 0), respawned {{warmGot}} "
               + "(replicate 0 + the resume) — anything else is either a lost invariant or a "
               + "silently shortened lane");
            Ok(got[4][1] < 0 && whole[4][1] >= 0,
               "replicate 4 is scored on an undamaged lane and a warm-up on the respawned one — "
               + "that single downgrade IS the X27 fix");
        }}

        // --- 3. THE CAP IS HARD ---------------------------------------------------------------
        // The R41 case: a lane that sinks on EVERY replicate. It must stop, and it must stop after
        // exactly MaxLaneRespawns relaunches — not one more, and not on a timer.
        Ok(S.MaxLaneRespawns >= 1, "the cap must allow at least one retry or the feature does nothing");
        {{
            var deaths = new int[16];
            for (int i = 0; i < deaths.Length; i++) deaths[i] = i;   // dies on every replicate
            Fly(16, 1, deaths, out int used);
            Ok(used == S.MaxLaneRespawns,
               $"a lane that dies every replicate respawned {{used}} time(s), want exactly "
               + $"{{S.MaxLaneRespawns}} — an uncapped loop is R41's UtilityHelo1 spawned 16 times");
        }}
        // ...and the cap counts the LANE, not the aircraft: the used count carries across each
        // replacement, so the third loss is refused however far apart the first two were.
        Ok(S.RespawnAt(3, S.MaxLaneRespawns) == -1, "at the cap, an owed lane must be refused");
        Ok(S.RespawnAt(3, S.MaxLaneRespawns - 1) == 3, "under the cap, an owed lane must resume at what it owes");
        Ok(S.RespawnAt(-1, 0) == -1, "a lane owing nothing must never be respawned, cap or no cap");
        // A refusal and a completion are the SAME return value on purpose (there is one "do not
        // respawn"), which is exactly why LaneLost keeps `owed` in hand to tell them apart in the
        // log — the cap message is the one an unattended batch has to find the next morning.
        Ok(S.RespawnAt(S.OwedFrom(7, 8), 0) == -1, "the last replicate owes nothing, so it is not a cap hit");

        Console.WriteLine(fails == 0
            ? "ok  lane respawn: resume index, every queue index re-flown, no SCORED replicate is "
              + "anchor-capturing, the cost is one replicate, hard cap"
            : $"{{fails}} failure(s)");
        return fails == 0 ? 0 : 1;
    }}
}}
"""


def main() -> int:
    bad = source_checks()
    for b in bad:
        print(f"  FAIL {b}")
    if not bad:
        print("ok  source: LaneLost asks the card player and refuses a crewed aircraft, both removal "
              "paths ask BEFORE ForgetState, the respawn reuses LaunchLane, the panic key does not "
              "respawn, and the resume reaches StartSuite")

    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    sched = extract(SCEN, "ARM-SCHEDULE")
    lane = extract(SCEN, "LANE-CONTINUITY")
    tmp = Path(tempfile.mkdtemp(prefix="lanerespawn-"))
    try:
        (tmp / "lanerespawn.csproj").write_text(PROJ, encoding="utf-8")
        (tmp / "Program.cs").write_text(program(sched, lane), encoding="utf-8")
        r = subprocess.run(
            ["dotnet", "run", "--project", str(tmp), "-v", "quiet", "--nologo"],
            capture_output=True, text=True,
        )
        out = (r.stdout or "") + (r.stderr or "")
        print(out.strip())
        if r.returncode != 0 and "FAIL" not in out and "failure" not in out:
            print(f"FAIL  the generated project did not build/run (exit {r.returncode}).")
        return 1 if (bad or r.returncode != 0) else 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
