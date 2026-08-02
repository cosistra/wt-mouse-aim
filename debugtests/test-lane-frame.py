#!/usr/bin/env python3
"""Lanes must be laid out in a frame that survives a floating-origin shift mid-stagger.

THE DEFECT THIS PINS (fixed in v0.97.1, measured in R33 and R35).
`TestDrone.RequestLaunch` captures the lane origin once and `LaunchDue` spends it one lane at a
time, `DroneStaggerSec` apart. That window is long enough for the game to move the world underneath
it: `FloatingOrigin.OriginShift` (decompile :19365) re-centres the Unity world on the OPERATOR'S
CAMERA whenever it drifts past 1024 m, translating every root GameObject by -round(cam/64)*64 and
moving `Datum.originPosition` with them. A base held as a raw `Vector3` still names the same numbers
afterwards but they now point at a different PLACE, so every lane launched after the shift is laid
out from a base the world has already moved out from under.

It hid for 30 batches because it is invisible at the spawn instant: each lane reads a clean
`7.709 + 6.000k` km from the origin on its own first replicate, before the next shift. Only a
cross-lane comparison later in the batch shows the rift.

Three parts, and the last two are the ones with teeth:
  1. SOURCE - `_laneBase` is a `GlobalPosition`, every write to it converts, and the read converts
     back at the launch instant. None of the three fails to compile if someone "simplifies" it back
     to a Vector3 pair, and the resulting batch still scores fine.
  2. MODEL - the frame arithmetic, run over the R35 launch (16 lanes, one 32 km shift between lane 6
     and lane 7, which is where R33's spawn log records the datum jumping). The broken formula must
     reproduce the measured `origDist` medians; the fixed one must produce a uniform 6 km ladder.
  3. RING (v0.99) - the layout itself. The line abeam put lane 0 at 8 km and lane 15 at 98 km, and
     distance to the world origin is a measured noise axis, not a free parameter: R35 gives
     r(`origDist`, `gJitterG`) = 0.948 at a log-log slope of 0.885, R39 gives far-lane replicate
     sigma at 1.50x near-lane on `fixedWindowOffDeg`. So the lanes now sit on a ring, each heading
     outward along its own radius, optionally split over two altitude decks. What must hold: the
     radius keeps the 6 km neighbour chord at any lanes-per-ring; every lane is the same distance
     from the base; and - the whole reason the heading is radial and not shared - it STAYS the same
     distance after the card's ~31 km of translation.

Stdlib only. Usage:
    python debugtests/test-lane-frame.py
"""

import math
import re
import sys
from pathlib import Path

SRC = Path(__file__).resolve().parent.parent / "TestDrone.cs"

# Lane layout, from TestDrone.cs. Not imported - asserted against the source below, so a change to
# either constant fails here rather than quietly re-scaling the model.
ABEAM_M = 5000.0
LANE_M = 6000.0

# The abeam distance R33 and R35 WERE ACTUALLY FLOWN AT, frozen on purpose. Part 2 replays a launch
# that happened, and the archived `origDist` medians it has to reproduce were produced by a build
# whose AbeamM was 8 km. v0.99 lowered the live constant to 5 km (every lane now flies AWAY from the
# observer, so the bound is the drone's own 4.14 km turn circle, not "far enough to not be in the
# way"). Re-pointing the historical model at the new value would silently stop reproducing the data
# it exists to reproduce - which is the only thing keeping the v0.97.1 datum fix honest.
LEGACY_ABEAM_M = 8000.0

# Deck spread used by the 3-D separation checks below. Not a source constant - `Cfg.DroneAltDeckM`
# is an operator knob with no single value - so the test picks the WORST case it can: the smaller
# the spread, the less vertical separation there is to help, and 0 is already covered by the
# "decks off reproduces the single ring exactly" assertion. 500 m is a plausible small setting.
TEST_DECK_M = 500.0


# --------------------------------------------------------------------------------------------
# 1. SOURCE INVARIANTS
# --------------------------------------------------------------------------------------------
def check_source():
    src = SRC.read_text(encoding="utf-8")

    decl = re.search(r"private\s+static\s+(\S+)\s+_laneBase\s*;", src)
    assert decl, "no `_laneBase` declaration found in TestDrone.cs"
    assert decl.group(1) == "GlobalPosition", (
        f"_laneBase is declared `{decl.group(1)}`, not `GlobalPosition`. A Unity world coordinate "
        "does not survive an OriginShift mid-stagger - see the header of this file."
    )

    writes = re.findall(r"_laneBase\s*=\s*([^;]+);", src)
    assert writes, "nothing assigns `_laneBase` - did RequestLaunch lose its geometry capture?"
    for w in writes:
        assert ".ToGlobalPosition()" in w, (
            f"`_laneBase = {w.strip()}` stores a raw world position. Every write must convert with "
            "`.ToGlobalPosition()`, or the base is stale the moment the camera moves."
        )

    reads = re.findall(r"_laneBase(?!\s*=)(\.\w+\(\))?", src)
    lane_expr = re.search(r"Vector3\s+pos\s*=\s*([^;]+);", src)
    assert lane_expr, "no `Vector3 pos = ...` lane expression found in LaunchDue"
    assert "_laneBase.ToLocalPosition()" in lane_expr.group(1), (
        f"the lane expression is `{lane_expr.group(1).strip()}` - it must convert the datum-relative "
        "base back with `.ToLocalPosition()` AT THE LAUNCH INSTANT, so the current origin is used."
    )
    assert any(r == ".ToLocalPosition()" for r in reads), "no datum->world conversion of _laneBase"

    for name, want in (("AbeamM", ABEAM_M), ("LaneM", LANE_M)):
        m = re.search(rf"const\s+float\s+{name}\s*=\s*([0-9.]+)f", src)
        assert m, f"`{name}` not found in TestDrone.cs"
        assert float(m.group(1)) == want, (
            f"{name} is {m.group(1)}, this model assumes {want}. LaneM in particular is 6 km "
            "because the sustained-turn cards sweep a 4.1 km circle - narrowing it causes mid-airs."
        )

    # --- the ring, v0.99 -----------------------------------------------------------------------
    # The radius formula, asserted against the source rather than only re-implemented below. A
    # Python copy that has drifted from the C# passes every model assertion in this file happily.
    assert re.search(r"LaneM\s*/\s*\(\s*2f\s*\*\s*Mathf\.Sin\s*\(\s*Mathf\.PI\s*/\s*lanesPerRing\s*\)\s*\)", src), (
        "RingRadius no longer solves `LaneM / (2*sin(pi/M))`. That expression IS the in-deck chord "
        "constraint - the 6 km neighbour gap the turn cards need is not maintained without it."
    )
    assert re.search(r"Mathf\.Sin\s*\(\s*Mathf\.PI\s*/\s*\(\s*2f\s*\*\s*lanesPerRing\s*\)\s*\)", src), (
        "RingRadius lost its CROSS-DECK term. The half-step offset puts a lane of one deck half a "
        "step from two lanes of the other, so that pair needs `sqrt(LaneM^2 - spread^2)` of "
        "horizontal gap; without the term a 500 m spread leaves two aircraft 3.06 km apart."
    )
    assert re.search(r"if\s*\(\s*lanesPerRing\s*>=\s*2\s*\)", src), (
        "RingRadius lost its `lanesPerRing >= 2` guard - sin(pi/1) is 0 and a one-lane ring "
        "divides by it."
    )
    assert re.search(r"private\s+static\s+Vector3\s+_laneFwd\s*;", src), (
        "no `_laneFwd` - the ring needs a second basis ray to place a lane off the abeam axis."
    )
    # The DECLARATION, not the name: the surrounding comments name the field they replaced.
    assert not re.search(r"^\s*private\s+static\s+\w+\s+_laneRot\s*;", src, re.M), (
        "`_laneRot` is back. A SHARED spawn rotation means every lane flies the same heading, and "
        "a ring flown on one heading smears back into a distance spread mid-card (16 -> 47 km) - "
        "which is the entire thing the radial layout buys."
    )
    assert "Quaternion.LookRotation(u, Vector3.up)" in src, (
        "the spawn rotation is no longer built from the lane's own outward ray `u`."
    )
    # The shipped deck spread. 3000 against a card declaring 4500 m puts the decks at 3000/6000 m,
    # the band the roster is characterised over - and it is a DEFAULT rather than 0 on purpose,
    # because a feature that has to be switched on ships inert. 0 stays available and is still
    # asserted below to reproduce the single ring exactly.
    deck = re.search(r'Bind\("Drone",\s*"DroneAltDeckM",\s*([0-9.]+)f',
                     (SRC.parent / "Cfg.cs").read_text(encoding="utf-8"))
    assert deck, "`Cfg.DroneAltDeckM` is not bound in the Drone section."
    assert float(deck.group(1)) == 3000.0, (
        f"DroneAltDeckM defaults to {deck.group(1)}, not 3000 - decks at 3000/6000 m under a "
        "4500 m card is the intended shipped geometry."
    )
    # The deck rule is the diagonal, not the alternation it replaced.
    assert re.search(r"\(\s*\(\s*k\s*/\s*_laneRoster\s*\)\s*\+\s*\(\s*k\s*%\s*_laneRoster\s*\)\s*\)\s*&\s*1", src), (
        "DeckOf is no longer the Latin-square diagonal `((k/A) + (k%A)) & 1`. Plain `k % 2` "
        "confounds deck with airframe for every EVEN-length airframe list, and `(k/A) & 1` "
        "confounds it with azimuth sector - both are checked as counterfactuals in check_ring."
    )
    print(f"  source: _laneBase is GlobalPosition, {len(writes)} write(s) convert, "
          f"read converts at launch, AbeamM/LaneM unchanged, ring formula + per-lane "
          f"LookRotation present, DroneAltDeckM 3000, DeckOf is the diagonal")


# --------------------------------------------------------------------------------------------
# 2. THE FRAME MODEL
#
# One axis is enough: the shift's dominant component is along the lane axis, and both features the
# measurement shows (the step, and the reversal) are one-dimensional. Sign convention is the game's:
#     world  = global + Datum.originPosition
#     global = world  - Datum.originPosition
# and a shift by V does `root.position -= V` to EVERY root - the datum transform included - so
# originPosition -= V and every existing object's `global` is preserved. That is the whole trick,
# and it is exactly what a cached `Vector3` opts out of.
# --------------------------------------------------------------------------------------------
def launch(n_lanes, shift_after_lane, shift_m, datum_frame, abeam_m=LEGACY_ABEAM_M):
    """Return each lane's position in the DATUM frame, and the origin offset at the end.

    `datum_frame=False` is the pre-v0.97.1 code: the base is a Unity world coordinate, so the
    formula spends numbers that stopped meaning the same place. `True` is the fix.

    `abeam_m` defaults to what R33/R35 flew, NOT to the live constant - see LEGACY_ABEAM_M. This is
    a replay of the archived batches, and the layout it replays is the LINE the v0.99 ring replaced.
    """
    orig = 0.0                       # Datum.originPosition
    base_world = 0.0                 # observer at the press instant
    base_global = base_world - orig  # what the fix stores instead
    lanes = []
    for k in range(n_lanes):
        base = base_global + orig if datum_frame else base_world
        world = base + (abeam_m + LANE_M * k)
        lanes.append(world - orig)   # the lane's datum position, which its run anchor then pins
        if k + 1 == shift_after_lane:
            orig -= shift_m          # the camera moved `shift_m`; every root follows, the datum too
    return lanes, orig


def orig_dist(lanes, orig):
    """The `origDist` column: |position in the Unity world frame| = |global + originPosition|."""
    return [abs(g + orig) for g in lanes]


def check_model():
    # R35: 16 lanes, and R33's spawn log (`local y` 2400 -> -32, i.e. the camera moved onto a drone
    # at 4 km MSL) puts the jump between lane 6 and lane 7 in BOTH batches. Fitting the medians
    # below gives 32.2 km; lane 5 sits at exactly 8 + 6*4 = 32.0 km, which is where the camera went.
    N, AFTER, SHIFT = 16, 6, 32000.0

    # --- the broken build must reproduce what was measured -------------------------------------
    lanes, orig = launch(N, AFTER, SHIFT, datum_frame=False)
    got = [d / 1000.0 for d in orig_dist(lanes, orig)]
    r35 = [24.0, 18.5, 12.8, 6.2, 0.6, 7.4,                     # lanes 1-6: carried by the shift
           44.0, 49.8, 55.8, 62.0, 67.8, 74.0,                  # lanes 7-16: laid out from the
           80.0, 86.1, 92.1, 98.5]                              #   base the world had moved off
    # 2 km of slack, and it is accounted for rather than fudged: this model is ONE-dimensional
    # while the real shift had a ~2.4 km vertical component (R33's logged `local y`), and R35's
    # medians are taken over a card in which the camera's 1024 m leash fired another 237 times.
    for k, (g, m) in enumerate(zip(got, r35), start=1):
        assert abs(g - m) < 2.0, (
            f"lane {k}: the stale-base model says {g:.1f} km, R35 measured {m:.1f} km. The model no "
            "longer explains the defect it was written for - re-derive before trusting the fix."
        )
    # The two features any correct theory has to produce, asserted as features and not as numbers.
    assert got[6] - got[5] > 30.0, "no step at the lane 6 -> 7 stagger boundary"
    assert all(got[i] > got[i + 1] for i in range(4)), "lanes 1-4 do not run BACKWARDS"
    assert all(got[i] < got[i + 1] for i in range(6, N - 1)), "lanes 7+ do not run forwards"
    print(f"  broken: reproduces R35 within {max(abs(g - m) for g, m in zip(got, r35)):.2f} km - "
          f"step of {got[5]:.1f}->{got[6]:.1f} km at 6->7, lanes 1-6 descending")

    # --- the fix: one layout, whatever the origin does ------------------------------------------
    lanes, orig = launch(N, AFTER, SHIFT, datum_frame=True)
    gaps = [lanes[i + 1] - lanes[i] for i in range(N - 1)]
    assert all(abs(g - LANE_M) < 1e-6 for g in gaps), (
        f"lane spacing in the datum frame is {[round(g) for g in gaps]}, not a uniform {LANE_M:.0f} m"
    )
    # `origDist` is still measured from a moving origin, so it does NOT go back to 7.709 + 6k - the
    # camera really is 32 km away now. What it must be is a single unbroken 6 km ladder: |d| = 6 km
    # between every adjacent pair, folding through zero at most once. That is the in-game signal.
    d = orig_dist(lanes, orig)
    steps = [d[i + 1] - d[i] for i in range(N - 1)]
    assert all(abs(abs(s) - LANE_M) < 1e-6 for s in steps), (
        f"origDist steps are {[round(s) for s in steps]} - every adjacent lane pair must differ by "
        f"exactly {LANE_M:.0f} m"
    )
    assert sum(1 for i in range(len(steps) - 1) if steps[i] * steps[i + 1] < 0) <= 1, (
        "more than one sign change in the origDist ladder - the origin can only pass through the "
        "lane line once"
    )
    print(f"  fixed:  datum spacing uniform {LANE_M:.0f} m over {N} lanes; origDist ladder "
          f"{d[0]/1000:.0f} -> {min(d)/1000:.0f} -> {d[-1]/1000:.0f} km, all steps +-6 km")

    # No shift at all must be untouched by the change: the fix is a no-op on a parked camera.
    a, _ = launch(N, 0, 0.0, datum_frame=False)
    b, _ = launch(N, 0, 0.0, datum_frame=True)
    assert a == b, "the fix changes the layout even with no origin shift - it must be a no-op there"
    print("  parked camera: fixed and broken layouts identical (the change is a no-op)")


# --------------------------------------------------------------------------------------------
# 3. THE RING (v0.99)
#
# Mirrors TestDrone.LaunchDue's placement exactly. Coordinates are (east, north, up) relative to
# `_laneBase`: east = `_laneRight`, north = `_laneFwd`. The origin-frame machinery above is
# irrelevant here - an origin shift translates every lane identically, so it cannot change any
# lane-to-lane or lane-to-base quantity this section asserts.
# --------------------------------------------------------------------------------------------
def ring_radius(lanes_per_ring, decks=1, spread=0.0):
    r = ABEAM_M
    if lanes_per_ring >= 2:                                  # in-deck neighbours: the full chord
        r = max(r, LANE_M / (2.0 * math.sin(math.pi / lanes_per_ring)))
    if decks > 1:                                            # cross-deck: the HALF-chord, in 3-D
        horiz = math.sqrt(max(0.0, LANE_M ** 2 - spread ** 2))
        r = max(r, horiz / (2.0 * math.sin(math.pi / (2.0 * lanes_per_ring))))
    return r


def lanes_per_ring(n, decks):
    return -(-n // decks)           # ceil


def deck_of(k, decks, roster):
    """TestDrone.DeckOf: the Latin-square diagonal over (roster pass, airframe)."""
    if decks < 2:
        return 0
    return ((k // roster) + (k % roster)) & 1


def lane_pos(slot, n, decks, spread, out_m=0.0, roster=1):
    """Lane `slot`'s position relative to the base, after a common outward run of `out_m`.

    `out_m` is the card's translation: every lane flies along its OWN radial, so the run adds to
    the radius and to nothing else. That is the whole reason the heading is radial.
    """
    turn = slot // n
    k = slot % n
    deck = deck_of(k, decks, roster)
    m = lanes_per_ring(n, decks)
    idx = sum(1 for j in range(k) if deck_of(j, decks, roster) == deck)
    th = 2.0 * math.pi * idx / m + deck * math.pi / m
    r = ring_radius(m, decks, spread) + LANE_M * turn + out_m
    return (r * math.cos(th), r * math.sin(th), spread * (deck - (decks - 1) * 0.5))


def dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def check_ring():
    # --- the radius formula ---------------------------------------------------------------------
    # Spot values, so a "simplification" of the closed form has to agree with arithmetic done by
    # hand. N=8 is the interesting one: at the old 8 km floor it was floor-bound, at 5 km it is the
    # chord that binds, which is exactly the packing the lower floor was lowered to buy.
    for m, want in ((1, 5000.0), (2, 5000.0), (3, 5000.0), (8, 7839.38), (16, 15377.49)):
        got = ring_radius(m)
        assert abs(got - want) < 0.05, f"ring_radius({m}) = {got:.2f}, expected {want:.2f}"

    # The constraint the radius exists to satisfy, at every fleet size the harness can launch, with
    # and without decks and at any spread. This is the collision-avoidance guarantee, so it is
    # checked as an inequality on the CHORD and not on the radius that produced it.
    for n in range(2, 17):
        for decks in (1, 2):
            for spread in (0.0, TEST_DECK_M, 3000.0, LANE_M, 9000.0):
                m = lanes_per_ring(n, decks)
                if m < 2:
                    continue
                chord = 2.0 * ring_radius(m, decks, spread) * math.sin(math.pi / m)
                assert chord >= LANE_M - 1e-6, (
                    f"N={n} decks={decks} spread={spread:.0f}: adjacent in-deck chord {chord:.0f} m "
                    f"is under LaneM {LANE_M:.0f} m - two lanes flying the sustained-turn family "
                    "would sweep overlapping ground tracks at the same altitude."
                )

    # WHAT THE DECKS ACTUALLY BUY, and the two ends of it. The cross-deck pairs sit half a step
    # apart in azimuth, so they need the spread to reach LaneM in 3-D and the radius is charged for
    # whatever the spread does not cover:
    #   spread 0     -> the charge exactly reproduces the single-deck radius. Decks buy NOTHING, and
    #                   asserting that is what stops someone "simplifying" the third term away and
    #                   shipping a layout with 3.06 km between two aircraft.
    #   spread LaneM -> the charge vanishes and the half-fleet-per-ring packing arrives in full.
    # N=2,3,4 are equal at both ends rather than smaller: the 5 km AbeamM floor is above every term
    # there. Asserted as the exact crossover, so raising that floor (which would spread the equality
    # to bigger fleets and quietly cost the decks their packing) fails here.
    for n in range(2, 17):
        r1 = ring_radius(lanes_per_ring(n, 1))
        m2 = lanes_per_ring(n, 2)
        assert ring_radius(m2, 2, 0.0) >= r1 - 1e-6, (
            f"N={n}: two decks at ZERO spread claim a smaller ring than one deck. They are the "
            "same lanes at the same altitude - the radius cannot legitimately shrink."
        )
        rfull = ring_radius(m2, 2, LANE_M)
        assert rfull <= r1 + 1e-6, f"N={n}: decks want a BIGGER ring ({rfull:.0f} > {r1:.0f})"
        if n >= 5:
            assert rfull < r1 - 1e-6, (
                f"N={n}: two decks at a full spread buy no radius at all ({rfull:.0f} vs "
                f"{r1:.0f}) - the packing is the first of the feature's two reasons to exist."
            )
    print(f"  ring:   radius 1/2/3/8/16 lanes-per-ring = "
          f"{'/'.join(f'{ring_radius(m):.0f}' for m in (1, 2, 3, 8, 16))} m; in-deck chord >= "
          f"{LANE_M:.0f} m for every N in 2..16, 1-2 decks, 5 spreads")
    print(f"  decks:  N=16 ring {ring_radius(16):.0f} m -> "
          f"{'/'.join(f'{ring_radius(8, 2, s):.0f}' for s in (0.0, TEST_DECK_M, 3000.0, LANE_M))} m "
          f"at spreads 0/{TEST_DECK_M:.0f}/3000/{LANE_M:.0f} m - the packing scales with the spread")

    # --- every lane the same distance out, at t=0 AND mid-card ------------------------------------
    # THE POINT OF THE WHOLE CHANGE. origDist is a measured noise axis (r = 0.948 against gJitterG),
    # so a layout that spreads it across lanes confounds lane with noise floor. Horizontal distance
    # only: the deck offset is a deliberate few hundred metres of altitude, not a lane spread.
    for n in (1, 2, 3, 8, 16):
        for out_m in (0.0, 31500.0):        # 250 m/s x 126 s: a full sustained-turn card
            d = [math.hypot(*lane_pos(k, n, 1, 0.0, out_m)[:2]) for k in range(n)]
            assert max(d) - min(d) < 1e-6, (
                f"N={n} after {out_m:.0f} m: lanes sit {min(d):.0f}..{max(d):.0f} m out. Every "
                "lane must be the same distance from the base at EVERY instant, which is what "
                "flying each one along its own radius buys."
            )
    # And the counterfactual, so the assertion above is not vacuous: one SHARED heading passes it at
    # t=0 and fails it by 31 km mid-card, which is the mistake the radial rule exists to avoid.
    n, run = 16, 31500.0
    r = ring_radius(n)
    shared = [math.hypot(r * math.cos(2 * math.pi * k / n),
                         r * math.sin(2 * math.pi * k / n) + run) for k in range(n)]
    assert max(shared) - min(shared) > 30000.0, (
        "the shared-heading counterfactual no longer smears - re-derive before trusting the "
        "radial rule, because this is the comparison that justifies it."
    )
    print(f"  radial: |pos-base| identical across lanes at t=0 and after 31.5 km, N=1..16; "
          f"one shared heading would spread 16 lanes {min(shared)/1000:.0f}-{max(shared)/1000:.0f} km")

    # Separation GROWS on diverging rays instead of shrinking.
    a0, b0 = lane_pos(0, 16, 1, 0.0), lane_pos(1, 16, 1, 0.0)
    a1, b1 = lane_pos(0, 16, 1, 0.0, 31500.0), lane_pos(1, 16, 1, 0.0, 31500.0)
    assert dist(a1, b1) > dist(a0, b0), "neighbouring lanes converge over the card"

    # --- decks off is byte-identical to the single ring -------------------------------------------
    for n in range(1, 17):
        for k in range(n):
            x, y, z = lane_pos(k, n, 1, 0.0)
            th = 2.0 * math.pi * k / n
            r = ring_radius(n)
            assert abs(x - r * math.cos(th)) < 1e-9 and abs(y - r * math.sin(th)) < 1e-9 and z == 0.0, (
                f"N={n} lane {k}: DroneAltDeckM=0 must reproduce the plain single ring exactly."
            )

    # --- the 3-D separation, at every fleet size and every spread ---------------------------------
    # The real safety invariant, and the reason the cross-deck term above is not optional: no PAIR
    # of lanes, on either deck, closer than LaneM in three dimensions. Swept over spreads rather
    # than asserted at one friendly constant, because the guarantee has to hold for whatever the
    # operator types into F1 - including a token 100 m, which is the setting that looks harmless.
    # Swept over the ROSTER LENGTH too, because the diagonal assigns decks from it: a different A
    # is a different partition of the lanes into decks, and the guarantee has to hold for all of
    # them. It also pins the precondition the azimuth indices rest on - neither deck may hold more
    # than `lanes_per_ring` lanes, or a lane would be handed an index off the end of its own ring.
    worst_all = (1e9, None)
    for n in range(2, 17):
        for roster in (1, 2, 3, 4, 5):
            m = lanes_per_ring(n, 2)
            for d in (0, 1):
                held = sum(1 for k in range(n) if deck_of(k, 2, roster) == d)
                assert held <= m, (
                    f"N={n} A={roster}: deck {d} holds {held} lanes but the ring has {m} slots. "
                    "The diagonal must split the fleet within one lane of even."
                )
            for spread in (0.0, 100.0, TEST_DECK_M, 3000.0, LANE_M, 9000.0):
                pts = [lane_pos(k, n, 2, spread, roster=roster) for k in range(n)]
                worst = min(dist(pts[i], pts[j]) for i in range(n) for j in range(i + 1, n))
                assert worst >= LANE_M - 1e-6, (
                    f"N={n} A={roster} decks=2 spread={spread:.0f}: closest pair is {worst:.0f} m, "
                    f"under LaneM {LANE_M:.0f} m. The half-step offset makes cross-deck neighbours "
                    "HALF a step apart, so the spread and the radius together have to cover it."
                )
                if worst < worst_all[0]:
                    worst_all = (worst, (n, spread))
    # The counterfactual that pins WHY the radius is charged: keep the half-step but drop the
    # cross-deck term from the radius, and a small spread leaves two aircraft ~3 km apart.
    naive = []
    for k in range(16):
        deck, m = k % 2, 8
        th = 2.0 * math.pi * (k // 2) / m + deck * math.pi / m
        r = ring_radius(m)                              # in-deck term only
        naive.append((r * math.cos(th), r * math.sin(th), TEST_DECK_M * (deck - 0.5)))
    bad = min(dist(naive[i], naive[j]) for i in range(16) for j in range(i + 1, 16))
    assert bad < LANE_M, "the uncharged-radius counterfactual no longer fails - re-derive"
    print(f"  3-D:    min pair separation {worst_all[0]:.0f} m (N={worst_all[1][0]}, spread "
          f"{worst_all[1][1]:.0f} m) over N=2..16 x 6 spreads; charging the radius is what buys it "
          f"- without it a {TEST_DECK_M:.0f} m spread gives {bad:.0f} m")

    # --- the overflow guard (a relaunch over a live fleet) ----------------------------------------
    # `_slot` starts at `_live.Count`, so a second press indexes past the ring. Azimuth alone wraps
    # onto an occupied ray; the `turn` term pushes each wrap out by one LaneM instead.
    for n in range(1, 17):
        for decks in (1, 2):
            pts = [lane_pos(k, n, decks, TEST_DECK_M) for k in range(2 * n)]
            worst = min(dist(pts[i], pts[j]) for i in range(2 * n) for j in range(i + 1, 2 * n))
            assert worst >= LANE_M - 1e-6, (
                f"N={n} decks={decks}: a relaunch over a live fleet puts two drones {worst:.0f} m "
                f"apart, under LaneM {LANE_M:.0f} m - the _live.Count offset stopped protecting."
            )
    print(f"  overflow: a second fleet launched over a live one keeps >= {LANE_M:.0f} m, N=1..16")

    # --- deck x airframe: the property, not a pinned set ------------------------------------------
    # The decks' second and larger return is altitude as a BALANCED factor crossed with airframe.
    # Asserted as the PROPERTY so it survives a change to the enumeration order; the previous
    # version of this test pinned an exact set of broken roster sizes, which was the right holding
    # move while `deck = k % 2` was in place and is the wrong test now that there is a fix.
    for a in range(1, 6):
        for n in range(2, 17):
            per = {}
            for k in range(n):
                per.setdefault(k % a, []).append(deck_of(k, 2, a))
            for af, decks_of_af in per.items():
                lo, hi = decks_of_af.count(0), decks_of_af.count(1)
                assert abs(lo - hi) <= 1, (
                    f"roster A={a} N={n}: airframe {af} flies {lo} low-deck lanes and {hi} high - "
                    "altitude is meant to be crossed with airframe, not correlated with it."
                )
                if n >= 2 * a:
                    assert lo > 0 and hi > 0, (
                        f"roster A={a} N={n}: airframe {af} lands entirely on deck "
                        f"{0 if hi == 0 else 1}. With N >= 2A every airframe has at least two "
                        "lanes and must fly both decks - see DeckOf's Latin-square diagonal."
                    )
            # And each deck carries the WHOLE roster, which is the same statement read by column.
            if n >= 2 * a:
                for d in (0, 1):
                    on_deck = {k % a for k in range(n) if deck_of(k, 2, a) == d}
                    assert on_deck == set(range(a)), (
                        f"roster A={a} N={n}: deck {d} is missing airframes "
                        f"{sorted(set(range(a)) - on_deck)} - it is not a full replicate of the fleet."
                    )

    # THE COUNTERFACTUALS, so the assertions above are not vacuous and the two rejected rules stay
    # rejected. (a) `k % 2` confounds deck with AIRFRAME at every even roster length.
    assert any(len({k % 2 for k in range(8) if k % a == 0}) == 1 for a in (2, 4)), (
        "plain `k % 2` no longer confounds - re-derive before trusting the diagonal"
    )
    # (b) `(k / A) & 1` is balanced per airframe but confounds deck with AZIMUTH SECTOR: it assigns
    # decks in contiguous blocks of A lanes, so one deck occupies an arc of the ring. Measured as
    # the spread of within-deck indices, which is contiguous for the block rule and interleaved for
    # the diagonal.
    a, n = 4, 16
    block = [k for k in range(n) if ((k // a) & 1) == 0]
    diag = [k for k in range(n) if deck_of(k, 2, a) == 0]
    assert max(block) - min(block) + 1 > len(block), "sanity: the block rule should be contiguous"
    assert block[:a] == list(range(a)), (
        "`(k / A) & 1` no longer assigns a contiguous first block - the azimuth-sector confound "
        "this rule is rejected for has changed shape."
    )
    assert diag[:a] != list(range(a)), (
        "the diagonal now assigns a contiguous first block, i.e. it has degenerated into the "
        "block rule and deck is confounded with azimuth sector."
    )

    # (c) DECK vs A/B ARM ARE INDEPENDENT, asserted rather than left to be rediscovered. At A=2 the
    # deck sequence over lanes is 0,1,1,0,0,1,1,0 - the same SHAPE as ScenarioPlayer's ABBA `ArmOf`
    # (`((i+1)>>1)&1`). That is a coincidence of shape, not a confound: deck is indexed by LANE
    # within one fleet, arm by REPLICATE index across a run, and the two indices are independent -
    # so the 2x2 stays balanced. Someone who spots the matching pattern will otherwise "fix" it.
    def arm_of(i):
        return ((i + 1) >> 1) & 1

    for a in (1, 2, 3, 4):
        for n in (4, 8, 16):
            for runs in (4, 8):
                cell = {(d, m): 0 for d in (0, 1) for m in (0, 1)}
                for k in range(n):
                    for i in range(runs):
                        cell[(deck_of(k, 2, a), arm_of(i))] += 1
                want = n * runs // 4
                assert all(v == want for v in cell.values()), (
                    f"roster A={a} N={n} runs={runs}: the deck x arm table is {cell}, not {want} "
                    "in every cell. Deck (by lane) and arm (by replicate) are crossed on "
                    "independent indices; if this fails one of them has started reading the other."
                )
    print("  factor: every airframe flies BOTH decks (A=1..5, N=2..16, counts differ by <=1); "
          "deck x arm 2x2 balanced - independent indices, not the ABBA pattern")


if __name__ == "__main__":
    print("lane frame:")
    try:
        check_source()
        check_model()
        check_ring()
    except AssertionError as e:
        print(f"\nFAIL: {e}", file=sys.stderr)
        sys.exit(1)
    print("OK")
