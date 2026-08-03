#!/usr/bin/env python3
"""Check the concurrent A/B arm machinery (v0.94) — by RUNNING the shipped C#, not a copy of it.

Two pieces of logic here regress silently, i.e. the batch still flies, every capture still scores,
and the answer is just wrong:

  1. THE ABBA SEQUENCE (`ScenarioPlayer.ArmOf`). Its whole job is that both arms have the same MEAN
     POSITION, so a one-way session drift cancels instead of reading as an effect. A,A,B,B has equal
     counts and is fully confounded; nothing at runtime would notice.
  2. THE ARM SURVIVING `ChaseController.Forget` (`ChaseController`'s ARM-SEAM region). The v0.84
     per-replicate reset calls `Forget(ac)` on EVERY replicate, so if the assignment lived in the
     controller instance the sweep would quietly do nothing while each capture still labelled itself
     `arm=0`/`arm=1`. That is why it lives in the registry map, keyed by aircraft.

WHAT THE SEQUENCE IS INDEXED BY, since v0.99.1: the REPLICATE (`_qi / _block`), not the queue
position. With more than one card selected the queue is BLOCKED — `SelectCards` repeats the whole
selection, c1,c2,c1,c2… — so `ArmOf(_qi)` gave card c1 the arms at queue indices 0,2,4,6 = A,B,A,B:
equal counts, but mean position 1 vs 2 inside c1's OWN sequence. The suite-start balance check ran
over the whole queue (A at 0,3,4,7 and B at 1,2,5,6, both summing 14), so it reported balanced and
printed nothing, while `compare-runs.py` groups by (airframe, CARD, arm) and sliced along exactly
the confounded axis. **So the balance subject below is the CARD, not the queue** — case 2b asserts
the fixed form and the old one as a counterfactual. `ArmOf` itself is unchanged, which is why the
extracted program still compiles against it.

Both live between markers in plain C# with no Unity/BepInEx type in them; this extracts those two
regions verbatim, wraps them in a throwaway console project and runs the .NET SDK over the case
table below — the same trick `test-board-math.py` and `test-card-model.py` use, and for the same
reason: a Python reimplementation would drift from the code and then agree with itself forever.

Plus five SOURCE assertions the regions cannot make about themselves: that the lever list below is
still exactly what Cfg.cs marks `(A/B lever)`, that `Forget` does not clear the arm, that `For`
seeds it, that `ApplyArm` never writes the global knob, and that all four A/B levers are read
through `Arm()` rather than off `Cfg` directly. Plus two more since v0.99.1: that `ApplyArm` indexes
by `_qi / _block` and not by a bare `_qi`, and that `SetUpArmSchedule` tallies balance with `_block`
in hand — neither fails to compile, and both put the per-card confound straight back.

Needs the .NET SDK, which this repo already requires to build at all (see CLAUDE.md).

Usage:
    python debugtests/test-arm-schedule.py
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CHASE = REPO / "ChaseController.cs"
SCEN = REPO / "ScenarioPlayer.cs"
CFG = REPO / "Cfg.cs"

# `public static ConfigEntry<bool>  Foo;   // ... (A/B lever)` — the declaration marker in Cfg.cs
# that says "this knob is meant to be swept". Nothing links it to the list below except this scan.
CFG_LEVER = re.compile(r"^\s*public static ConfigEntry<bool>\s+(\w+)\s*;.*\(A/B lever\)", re.M)

# The four bools Cfg.cs marks "(A/B lever)". Every one must be read through Arm() to be sweepable;
# a lever read as Cfg.X.Value still compiles and still flies, it is just invisible to the schedule.
# v0.99.1: RelativeTurnLead was DELETED (knob and branch) after R39-D spent its A/B — the lead is now
# unconditionally the relative rate, so there is no arm left to sweep and no site left to read.
LEVERS = [
    "MarkerRateFeedForward",
    "IntegralStallGate",
    "BelowAlignSuppress",
    "AlignRateLead",
]
# MarkerRateFeedForward is read at BOTH lockstep sites (the shared omegaDes and
# ApplyEvolvedLegacy's own omega), so four levers = five call sites.
LEVER_SITES = 5

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>armsched</AssemblyName>
    <RootNamespace>armsched</RootNamespace>
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
    # Scan CODE only — the comments in these regions name the game types they deliberately avoid
    # (and `_armByAircraft` has "Aircraft" inside its own name). The compile below is the real
    # check; this list only turns "CS0246 in a temp file" into a sentence.
    code = re.sub(r"//.*", "", body)
    for banned in BANNED:
        if re.search(rf"\b{re.escape(banned)}", code):
            raise SystemExit(
                f"FAIL  the {name} region now references {banned}; it must stay pure (plain types "
                f"only) or it cannot be compiled outside the game."
            )
    return body


def source_checks() -> list:
    """What the extracted regions cannot say about themselves: how the rest of the file uses them."""
    bad = []
    chase = CHASE.read_text(encoding="utf-8")
    scen = SCEN.read_text(encoding="utf-8")

    # 0. THE LIST ITSELF. Everything below asserts things about LEVERS, so LEVERS being stale is the
    #    one failure that makes the whole check agree with itself forever: add a sixth `(A/B lever)`
    #    knob, read it as Cfg.X.Value, and nothing here would notice it is unsweepable. Cfg.cs's
    #    marker comment is the declaration of intent, so that is what this compares against.
    declared = sorted(CFG_LEVER.findall(CFG.read_text(encoding="utf-8")))
    if declared != sorted(LEVERS):
        bad.append(
            f"Cfg.cs marks {declared} as '(A/B lever)' but this test's LEVERS list is "
            f"{sorted(LEVERS)}. A lever missing from LEVERS is never checked for reading through "
            f"Arm(), i.e. it can be unsweepable and every capture still labels itself arm=0/arm=1; "
            f"a lever here that Cfg.cs no longer marks is a dead assertion. Fix LEVERS (and "
            f"LEVER_SITES) in the same change as the knob."
        )

    # 1. THE TRAP. Forget(int) drops the controller on every replicate; if it also dropped the arm,
    #    every A/B would silently fly one arm while both captures claimed their own.
    m = re.search(r"internal static void Forget\(int aircraftId\)\s*\{(.*?)\n        \}", chase, re.S)
    if not m:
        bad.append("ChaseController.Forget(int) not found — has the registry changed shape?")
    elif "_armByAircraft" in m.group(1):
        bad.append(
            "ChaseController.Forget(int) touches _armByAircraft. The arm MUST survive Forget: "
            "PlaceOnCondition calls it on every replicate, so clearing it there un-sweeps every A/B "
            "while the captures still label themselves arm=0/arm=1."
        )

    # 2. ...and the other half: a freshly built controller has to pick the standing assignment up.
    m = re.search(r"internal static ChaseController For\(Aircraft ac\)\s*\{(.*?)\n        \}", chase, re.S)
    if not m:
        bad.append("ChaseController.For(Aircraft) not found — has the registry changed shape?")
    elif "SeedArm" not in m.group(1):
        bad.append(
            "ChaseController.For does not call SeedArm, so the controller rebuilt after each "
            "per-replicate Forget starts with no arm and the sweep does nothing."
        )

    # 3. The point of v0.94: the schedule writes the AIRCRAFT, never the process-global knob. A
    #    write here is what forced the old one-drone-at-a-time stand-down.
    m = re.search(r"private void ApplyArm\(\)\s*\{(.*?)\n        \}", scen, re.S)
    if not m:
        bad.append("ScenarioPlayer.ApplyArm not found.")
    else:
        body = m.group(1)
        if "_armEntry.Value =" in body or "_armEntry.BoxedValue" in body:
            bad.append(
                "ScenarioPlayer.ApplyArm writes the swept ConfigEntry. That is process-global, so it "
                "puts every concurrent aircraft on one arm — the whole limitation v0.94 removed."
            )
        if "ChaseController.SetArm" not in body:
            bad.append("ScenarioPlayer.ApplyArm no longer assigns the arm through ChaseController.SetArm.")
        # v0.99.1 — the index is the REPLICATE. `ArmOf(_qi)` compiles, flies, and gives every card
        # A,B,A,B internally while the queue-wide tally reports balanced (see the docstring).
        if "ArmOf(_qi / _block)" not in body:
            bad.append(
                "ScenarioPlayer.ApplyArm does not index the arm by ArmOf(_qi / _block). Indexing by "
                "the raw queue position confounds arm with position WITHIN each card as soon as a "
                "second card is selected — 2 cards x repeat 4 gives every card A,B,A,B, mean position "
                "1 vs 2 — and the queue-wide balance check reports it as balanced."
            )

    # 3b. ...and the balance check has to be tallied over the same thing the arm is indexed by, or it
    #     goes back to certifying a per-card imbalance as balanced. That is the half that made the
    #     defect invisible for as long as it existed.
    m = re.search(r"private void SetUpArmSchedule\(int runs, Card first\)\s*\{(.*?)\n        \}", scen, re.S)
    if not m:
        bad.append("ScenarioPlayer.SetUpArmSchedule not found.")
    elif "_block" not in m.group(1):
        bad.append(
            "ScenarioPlayer.SetUpArmSchedule does not use _block. Its balance tally must run over the "
            "REPLICATE index, i.e. the same thing ApplyArm indexes by — tallying the whole queue is "
            "what let 2 cards x repeat 4 print 'balanced' while every card flew A,B,A,B."
        )

    # 4. Every lever read through Arm(). Cfg.X.Value in the law is not a compile error and not a
    #    flight bug — it just makes that lever unsweepable, which reads as "the A/B found nothing".
    for lever in LEVERS:
        if f"Cfg.{lever}.Value" in chase:
            bad.append(
                f"ChaseController reads Cfg.{lever}.Value directly. An A/B lever must be read as "
                f"Arm(Cfg.{lever}) or the schedule cannot sweep it."
            )
        if f"Arm(Cfg.{lever})" not in chase:
            bad.append(f"ChaseController never reads {lever} through Arm() — is it still a lever?")
    n = len(re.findall(r"\bArm\(Cfg\.", chase))
    if n != LEVER_SITES:
        bad.append(
            f"ChaseController has {n} Arm(Cfg.*) call sites, expected {LEVER_SITES}. Adding a lever "
            f"means updating LEVERS/LEVER_SITES here; losing one means a site stopped being swept "
            f"(MarkerRateFeedForward has TWO, and they must stay in lockstep)."
        )
    return bad


def program(seam: str, sched: str) -> str:
    return f"""using System;
using System.Collections.Generic;

// The shipped ARM-SEAM region, verbatim, in a class of the same name.
internal sealed class ChaseController
{{
{seam}
}}

// The shipped ARM-SCHEDULE region, verbatim.
internal static class Sched
{{
{sched}
}}

internal static class P
{{
    static int fails;
    static void Ok(bool cond, string what)
    {{
        if (!cond) {{ Console.WriteLine($"  FAIL {{what}}"); fails++; }}
    }}

    static int Main()
    {{
        // --- 1. Replicate 0 is the WARM-UP; ABBA runs over replicates 1..N (v1.0.1, #55b) ------
        // Replicate 0's placement is the one that CAPTURES the run anchor, so it cannot snap back to
        // it: it flies from the spawn state (R41 e1-below-suppress/FastBomber1: `v=250->250
        // alt=6000->6000 snapBackM=0` against `v~352->250 alt~2180->6000 snapBackM~11000` for every
        // later replicate) and scored 5.495 deg against its own siblings' 0.252-0.268. A permanently
        // distinct flight condition cannot be balanced into an arm, so it is armed as NEITHER.
        int[] want = {{ -1, 0, 1, 1, 0, 0, 1, 1, 0 }};
        for (int i = 0; i < want.Length; i++)
            Ok(Sched.ArmOf(i) == want[i], $"ArmOf({{i}}) = {{Sched.ArmOf(i)}}, want {{want[i]}}");

        // --- 1b. THE ANCHOR-STRATUM PROPERTY, asserted rather than assumed -------------------
        // The defect was not "the pattern is wrong" -- ABBAABBA was a correct ABBA. It was that the
        // anchor stratum (exactly one replicate, index 0) landed on arm A in 100% of lanes. So the
        // property to hold is about that STRATUM, not about the pattern: no scored arm may ever
        // contain replicate 0. Stated as a property it survives someone rewriting the pattern.
        Ok(Sched.ArmOf(0) < 0, $"ArmOf(0) = {{Sched.ArmOf(0)}} — the anchor replicate must be on "
                               + "NEITHER arm; any value in {{0,1}} reintroduces #55b");
        for (int i = 1; i < 64; i++)
            Ok(Sched.ArmOf(i) == 0 || Sched.ArmOf(i) == 1,
               $"ArmOf({{i}}) = {{Sched.ArmOf(i)}} — only replicate 0 may be unarmed");
        // ...and the counterfactual: the shipped-until-v1.0.0 form armed it, which is the bug.
        Ok((((0 + 1) >> 1) & 1) == 0,
           "counterfactual: the pre-v1.0.1 ArmOf(0) = ((0+1)>>1)&1 = 0 put the anchor replicate on "
           + "arm A on every ABBA card ever flown — 12.5% of one arm, 0% of the other");

        // --- 2. THE INVARIANT: equal counts AND equal mean position, at every multiple of 4 ---
        // Equal counts alone is not it: ABBAAB has 3/3 and still leans A early, which is why the
        // suite-start balance check compares sum(index) rather than n.
        // v1.0.1: the subject is the SCORED replicates, 1..scored — replicate 0 is the warm-up and is
        // tallied by neither arm (mirrors SetUpArmSchedule's own loop). So the invariant now holds at
        // every multiple of 4 SCORED, i.e. at repeat = 4k+1.
        foreach (int scored in new[] {{ 4, 8, 12, 16, 40 }})
        {{
            int nA = 0, nB = 0, sumA = 0, sumB = 0;
            for (int i = 1; i <= scored; i++)
            {{
                int a = Sched.ArmOf(i);
                if (a < 0) continue;
                if (a == 1) {{ nB++; sumB += i; }} else {{ nA++; sumA += i; }}
            }}
            Ok(nA == nB, $"scored={{scored}}: {{nA}} A vs {{nB}} B — counts must match");
            Ok(sumA == sumB, $"scored={{scored}}: sum(index) {{sumA}} A vs {{sumB}} B — mean position must match");
        }}
        // ...and the documented failure this pins in place: 6 SCORED is A,B,B,A,A,B, which has EQUAL
        // COUNTS (3/3) and still leans A early. That is exactly why the suite-start check compares
        // sum(index) and why the UNBALANCED warning exists — not something to "fix" here.
        {{
            int nA = 0, nB = 0, sumA = 0, sumB = 0;
            for (int i = 1; i <= 6; i++)
            {{
                int a = Sched.ArmOf(i);
                if (a < 0) continue;
                if (a == 1) {{ nB++; sumB += i; }} else {{ nA++; sumA += i; }}
            }}
            Ok(nA == nB && sumA != sumB,
               $"6 scored must be equal-count ({{nA}}/{{nB}}) but UNEQUAL mean position ({{sumA}}/{{sumB}}) — "
               + "the case that proves counts alone do not detect an imbalance");
        }}
        // ...and the consequence a card author has to act on: a repeat of 8 now scores SEVEN, which
        // cannot be balanced at all. This asserts the warning path is reachable, so the "use 4k+1"
        // advice in SetUpArmSchedule is load-bearing rather than decorative.
        {{
            int nA = 0, nB = 0, sumA = 0, sumB = 0;
            for (int r = 0; r < 8; r++)
            {{
                int a = Sched.ArmOf(r);
                if (a < 0) continue;
                if (a == 1) {{ nB++; sumB += r; }} else {{ nA++; sumA += r; }}
            }}
            Ok(nA + nB == 7 && !(nA == nB && sumA == sumB),
               $"repeat=8 scores {{nA + nB}} replicate(s) ({{nA}}A/{{nB}}B) and MUST read unbalanced — "
               + "cards want repeat 4k+1 (5, 9, 13) now that replicate 0 is a warm-up");
        }}

        // --- 2b. THE SUBJECT OF THE BALANCE CHECK IS THE CARD, NOT THE QUEUE (v0.99.1) --------
        // The queue is BLOCKED for a multi-card selection (c1,c2,c1,c2...), and compare-runs.py
        // groups by (airframe, CARD, arm) — so the sequence that has to be balanced is the one a
        // SINGLE card flies. Indexing by replicate (_qi / _block) makes every card fly the same
        // ArmOf-over-replicates sequence; the counterfactual below is the shipped-until-v0.99.1
        // bare ArmOf(_qi), asserted UNBALANCED so nobody can quietly go back to it.
        // reps are 4k+1 here (v1.0.1): replicate 0 is the warm-up, so 5 and 9 are what balance.
        foreach (int nCards in new[] {{ 1, 2, 3, 4 }})
        foreach (int reps in new[] {{ 5, 9 }})
        {{
            for (int k = 0; k < nCards; k++)
            {{
                int nA = 0, nB = 0, sumA = 0, sumB = 0;
                for (int r = 0; r < reps; r++)
                {{
                    int qi = r * nCards + k;                 // card k's r-th run in the blocked queue
                    int a = Sched.ArmOf(qi / nCards);
                    if (a < 0) continue;                     // the warm-up is on neither arm
                    if (a == 1) {{ nB++; sumB += r; }} else {{ nA++; sumA += r; }}
                }}
                Ok(nA == nB && sumA == sumB,
                   $"{{nCards}} card(s) x {{reps}} reps, card {{k}}: {{nA}}A/{{nB}}B, sum {{sumA}}/{{sumB}} — "
                   + "each card's OWN arm sequence must be balanced");
            }}
        }}
        {{
            // 2 cards x repeat 5 under the OLD rule (bare ArmOf(_qi), no divide): card 0 samples the
            // schedule at queue positions 0,2,4,6,8 and comes out UNBALANCED. Asserted as "not
            // balanced" rather than as one specific shape, because the shape moved in v1.0.1 while
            // the defect did not — pinning the shape is what would make this test rot.
            int nA = 0, nB = 0, sumA = 0, sumB = 0;
            for (int r = 0; r < 5; r++)
            {{
                int qi = r * 2 + 0;
                int a = Sched.ArmOf(qi);
                if (a < 0) continue;
                if (a == 1) {{ nB++; sumB += r; }} else {{ nA++; sumA += r; }}
            }}
            Ok(!(nA == nB && sumA == sumB),
               $"counterfactual: bare ArmOf(_qi) must leave card 0 UNBALANCED ({{nA}}A/{{nB}}B, "
               + $"sum {{sumA}}/{{sumB}}) — the defect that indexing by replicate fixed");
        }}

        // --- 3. No assignment: the live config value passes straight through ------------------
        var c0 = new ChaseController();
        c0.SeedArm(1);
        Ok(c0.Arm("K", true), "unassigned: Arm should return the live value (true)");
        Ok(!c0.Arm("K", false), "unassigned: Arm should return the live value (false)");

        // --- 4. Assigned: the arm wins for ITS knob and only for its knob ---------------------
        ChaseController.StoreArm(1, "K", true);
        var c1 = new ChaseController();
        c1.SeedArm(1);
        Ok(c1.Arm("K", false), "assigned: the arm must beat the live config value");
        Ok(!c1.Arm("Other", false), "assigned: an UNSWEPT knob must still read the live value");
        Ok(c1.Arm("Other", true), "assigned: an UNSWEPT knob must still read the live value");

        // --- 5. THE TRAP: the arm outlives the controller -------------------------------------
        // PlaceOnCondition calls ChaseController.Forget(ac) on every replicate, so the instance the
        // next tick flies is a brand new one. It must come up on the same arm; if it does not, the
        // sweep does nothing and every capture still claims an arm it never flew.
        var afterForget = new ChaseController();
        afterForget.SeedArm(1);
        Ok(afterForget.Arm("K", false), "a controller rebuilt after Forget must keep the aircraft's arm");

        // --- 6. THE RELEASE: two aircraft, opposite arms, same instant ------------------------
        ChaseController.StoreArm(2, "K", false);
        var a1 = new ChaseController(); a1.SeedArm(1);
        var a2 = new ChaseController(); a2.SeedArm(2);
        Ok(a1.Arm("K", false) && !a2.Arm("K", false),
           "two aircraft must be able to fly opposite arms of the same knob at once");

        // --- 7. Clearing (suite Finish / despawn) hands the knob back to the config -----------
        ChaseController.StoreArm(1, null, false);
        var cleared = new ChaseController();
        cleared.SeedArm(1);
        Ok(!cleared.Arm("K", false), "cleared: must fall back to the live value (false)");
        Ok(cleared.Arm("K", true), "cleared: must fall back to the live value (true)");
        // ...and clearing one aircraft must not disturb another's, which is what let the old
        // ownership machinery go.
        var still2 = new ChaseController(); still2.SeedArm(2);
        Ok(!still2.Arm("K", true), "clearing aircraft 1 must not clear aircraft 2");

        Console.WriteLine(fails == 0 ? "ok  arm schedule + seam" : $"{{fails}} failure(s)");
        return fails == 0 ? 0 : 1;
    }}
}}
"""


def main() -> int:
    bad = source_checks()
    for b in bad:
        print(f"  FAIL {b}")
    if not bad:
        print(f"ok  source: LEVERS == Cfg.cs's {len(LEVERS)} '(A/B lever)' knobs, Forget keeps the "
              f"arm, For seeds it, ApplyArm writes no global, {LEVER_SITES} lever sites through Arm()")

    if shutil.which("dotnet") is None:
        print("FAIL  the .NET SDK is not on PATH — this check compiles and runs the real C#.")
        return 1
    seam = extract(CHASE, "ARM-SEAM")
    sched = extract(SCEN, "ARM-SCHEDULE")
    tmp = Path(tempfile.mkdtemp(prefix="armsched-"))
    try:
        (tmp / "armsched.csproj").write_text(PROJ, encoding="utf-8")
        (tmp / "Program.cs").write_text(program(seam, sched), encoding="utf-8")
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
