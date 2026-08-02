#!/usr/bin/env python3
"""The v0.58 helo probe fires in the DRONE call order (v1.0.0). Stdlib only, no SDK, no game.

WHY THIS EXISTS. `ChaseController.ResolveHelo` reads `_collective`, which `BeginFrame` latches, but
it was reachable only from `ResolveFbw`'s aircraft-change edge. On the drone path the FIRST
`ResolveFbw` comes from `ManeuverRecorder`'s `# fbw` header write (`ScenarioPlayer.StartCard` ->
`Toggle` -> `FbwHeader` -> `ResolveFbw`), which runs BEFORE that aircraft's first `BeginFrame` — so
the probe hit `if (!_collective) return;` and the edge was CONSUMED. `_heloOk` stayed false for the
life of the aircraft and the whole v0.58 rotorcraft branch was dead on all 48 rotorcraft captures in
the corpus, across 40 versions (`debugtests/R39-rotor.md` §1a).

It survived that long because NOTHING FAILS. The law falls back to the pre-v0.58 direct-P path and
writes a capture that scores fine; the probe's own unconditional log line sits *after* the early
return, so its absence was the only evidence, and absence is not greppable per capture. Establishing
it took a row-by-row reconstruction of `outY` against both candidate formulas. So the fix needs a
check that runs offline, and the check has to cover the ORDER, which no compiler sees.

Two halves, the same shape as `test-lane-frame.py`:
  1. SOURCE — the retry structure is present in ChaseController.cs, and the three liveness columns
     are wired end to end in Recording.cs. Reverting any of it compiles and flies.
  2. MODEL — the trigger transcribed to Python and run over the three real call orders, with the
     PRE-FIX trigger as a counterfactual that must fail the drone case. Without the counterfactual
     this file would pass against the bug it exists to catch.

Run: python debugtests/test-helo-probe-order.py
"""

import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
CHASE = REPO / "ChaseController.cs"
REC = REPO / "Recording.cs"

# The liveness columns, in header order. Also the argument order at the Sample() call site.
LIVENESS = ("fbwOk", "canOk", "heloOk")


def body_of(src: str, signature: str) -> str:
    """The brace-matched body of the method whose declaration line contains `signature`."""
    i = src.index(signature)
    start = src.index("{", i)
    depth, j = 0, start
    while j < len(src):
        if src[j] == "{":
            depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0:
                return src[start : j + 1]
        j += 1
    raise AssertionError(f"unbalanced braces after {signature!r}")


def strip_comments(src: str) -> str:
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


# ---------------------------------------------------------------------------------------------
# 1. SOURCE
# ---------------------------------------------------------------------------------------------
def source_checks() -> None:
    chase = CHASE.read_text(encoding="utf-8")
    rec = REC.read_text(encoding="utf-8")
    chase_c = strip_comments(chase)
    rec_c = strip_comments(rec)

    assert re.search(r"\bprivate bool _heloProbedAs\s*;", chase_c), \
        "ChaseController: the _heloProbedAs latch is gone — the probe is one-shot again"

    helo = strip_comments(body_of(chase, "private void ResolveHelo(Aircraft ac)"))
    # The early return keeps fixed-wing byte-identical, and it is NOT redundant with the probe's own
    # `is HeloControlsFilter` test: VTOLTrainer1 is a FIXED-WING VTOL (AIRFRAMES.md), so dropping it
    # would bind _sds on a fixed wing and the tilt-driven blend at ChaseController's
    # `if (_twc != null || _sds != null)` — which is NOT gated on _collective — would start moving
    # _heliBlend on an airframe that has always flown with it pinned at 0.
    assert "if (!_collective) return" in helo, \
        "ResolveHelo: the fixed-wing early return is gone — fixed-wing behaviour is no longer identical"
    assert "_heloProbedAs = _collective" in helo, "ResolveHelo: nothing records what it probed under"
    assert "try" in helo, "ResolveHelo: the fail-soft try is gone"
    # The write must come FIRST, on every path: before the early return (so a fixed wing records
    # "probed as fixed-wing" and never retries) and before the try (so a THROWING probe is not re-run
    # every fixed step for the rest of the flight — fail-soft means fail once).
    i_set = helo.index("_heloProbedAs = _collective")
    assert i_set < helo.index("if (!_collective) return"), \
        "ResolveHelo: _heloProbedAs must be set BEFORE the fixed-wing early return"
    assert i_set < helo.index("try"), \
        "ResolveHelo: _heloProbedAs must be set BEFORE the try — a throwing probe must not retry forever"

    fbw = strip_comments(body_of(chase, "private bool ResolveFbw(Aircraft ac)"))
    assert re.search(r"else if \(_collective != _heloProbedAs\) ResolveHelo\(ac\);", fbw), \
        "ResolveFbw: the helo re-probe is gone — the aircraft-change edge is the wrong edge alone"
    assert fbw.index("if (id != _fbwAcId)") < fbw.index("_collective != _heloProbedAs"), \
        "ResolveFbw: the retry must follow the aircraft-change edge, not precede it"

    # The premise of the whole defect: BeginFrame is what writes the flag the probe reads, and
    # FbwHeader is the caller that fires the edge early. If either stops being true the retry is
    # harmless, but this test is no longer testing what it claims to.
    assert re.search(r"_collective = !fixedWing", chase_c), "BeginFrame no longer latches _collective"
    assert "ResolveFbw(ac)" in strip_comments(body_of(chase, "internal string FbwHeader(Aircraft ac)")), \
        "FbwHeader no longer calls ResolveFbw — re-read this test's premise before deleting it"
    assert "cc.FbwHeader(" in rec_c, "Recording.cs no longer writes the # fbw header via FbwHeader"

    # --- the liveness columns, end to end -----------------------------------------------------
    header = re.search(r"private const string Header\s*=(.*?);\s*\n", strip_comments(rec), flags=re.S)
    assert header, "Recording.cs: could not parse the CSV Header"
    cols = "".join(re.findall(r'"([^"]*)"', header.group(1))).split(",")
    assert tuple(cols[-3:]) == LIVENESS, \
        f"CSV header must END with {','.join(LIVENESS)} (new columns append at the end); got {cols[-3:]}"
    assert len(cols) == len(set(cols)), "duplicate CSV column name"

    sample = strip_comments(body_of(rec, "public void Sample("))
    for name in LIVENESS:
        assert re.search(rf"\{{\({name} \? 1 : 0\)\}}", sample), \
            f"Sample(): {name} is a header column but nothing writes it as a 0/1 bit"
    tail = rec_c[rec_c.index("public void Sample(") :]
    sig = tail[: tail.index("Aircraft ac)")]
    assert f"bool {LIVENESS[0]}, bool {LIVENESS[1]}, bool {LIVENESS[2]}," in sig, \
        "Sample(): the three liveness bools must sit together, immediately before `Aircraft ac`"

    ctail = chase_c[chase_c.index("rec.Sample(") :]
    call = ctail[: ctail.index("aircraft)")]
    assert "fbwResolved, _rsCtrl != null, _heloOk," in call, (
        "the Sample() call must pass ResolveFbw's RETURN (fbwResolved), not Apply's narrower `fbwOk` "
        "local, which is fbwResolved && !_collective and would read 0 on every rotorcraft"
    )
    print(f"  source: retry structure + {len(cols)} CSV columns ending {','.join(LIVENESS)}")


# ---------------------------------------------------------------------------------------------
# 2. MODEL — the trigger, transcribed. `retry=False` is the pre-v1.0.0 code.
# ---------------------------------------------------------------------------------------------
class Trigger:
    def __init__(self, retry: bool):
        self.retry = retry
        self._fbwAcId = 0            # C# default; a real GetInstanceID() is never 0
        self._collective = False
        self._heloProbedAs = False
        self.probed = []             # _collective for each ResolveHelo that got PAST the early return
        self.calls = 0               # every ResolveHelo invocation, early returns included

    def begin_frame(self, fixed_wing: bool):
        self._collective = not fixed_wing

    def _resolve_helo(self):
        self.calls += 1
        self._heloProbedAs = self._collective      # first statement, every path
        if not self._collective:
            return
        self.probed.append(self._collective)

    def resolve_fbw(self, ac_id: int):
        if ac_id != self._fbwAcId:
            self._fbwAcId = ac_id
            self._resolve_helo()
        elif self.retry and self._collective != self._heloProbedAs:
            self._resolve_helo()


AC, AC2, TICKS = 101, 202, 200


def drone(t: Trigger, fixed_wing: bool):
    """ScenarioPlayer.StartCard -> recorder header -> FbwHeader -> ResolveFbw, THEN FlyUncrewed."""
    t.resolve_fbw(AC)                       # the premature edge
    t.begin_frame(fixed_wing)               # FlyUncrewed -> BeginFrame, seven log lines later
    for _ in range(TICKS):
        t.resolve_fbw(AC)                   # Apply, every fixed step


def player(t: Trigger, fixed_wing: bool):
    """PilotPlayerStatePatch: BeginFrame then Apply; the header write comes later, on RecordKey."""
    for i in range(TICKS):
        t.begin_frame(fixed_wing)
        t.resolve_fbw(AC)
        if i == TICKS // 2:
            t.resolve_fbw(AC)               # FbwHeader when the operator arms a capture


def model_checks() -> None:
    # THE DEFECT AND THE FIX.
    fixed = Trigger(retry=True)
    drone(fixed, fixed_wing=False)
    assert fixed.probed == [True], f"drone rotorcraft: probe never ran under collective ({fixed.probed})"
    assert fixed.calls == 2, f"the retry must fire ONCE, not per fixed step (calls={fixed.calls})"

    buggy = Trigger(retry=False)
    drone(buggy, fixed_wing=False)
    assert buggy.probed == [], "COUNTERFACTUAL FAILED: the pre-fix trigger passes, so this test proves nothing"

    # FIXED-WING IS BYTE-IDENTICAL — the constraint on the fix. Same probe count, same arguments,
    # under both triggers and both call orders.
    for order in (drone, player):
        a, b = Trigger(retry=True), Trigger(retry=False)
        order(a, fixed_wing=True)
        order(b, fixed_wing=True)
        assert a.probed == b.probed == [], f"{order.__name__} fixed-wing: probe ran"
        assert a.calls == b.calls == 1, f"{order.__name__} fixed-wing: {a.calls} vs {b.calls} probe calls"

    # THE CREWED ROTORCRAFT PATH ALSO DOES NOT MOVE — it already latched _collective before the
    # edge, which is why the corpus has 12 [canard] lines and the player was never affected.
    a, b = Trigger(retry=True), Trigger(retry=False)
    player(a, fixed_wing=False)
    player(b, fixed_wing=False)
    assert a.probed == b.probed == [True] and a.calls == b.calls == 1, "crewed rotorcraft path changed"

    # An aircraft change still re-probes, and the retry does not eat the edge.
    t = Trigger(retry=True)
    drone(t, fixed_wing=False)
    t.resolve_fbw(AC2)
    assert t.probed == [True, True] and t.calls == 3, f"aircraft change did not re-probe ({t.probed})"

    print(f"  model:  drone probes under collective (2 calls, not {TICKS}); "
          "pre-fix trigger fails it; fixed-wing + crewed identical")


if __name__ == "__main__":
    print("test-helo-probe-order.py")
    try:
        source_checks()
        model_checks()
    except AssertionError as e:
        print(f"FAIL: {e}")
        sys.exit(1)
    print("ok")
