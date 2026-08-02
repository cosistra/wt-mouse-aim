#!/usr/bin/env python3
"""check-card.py -- refuse a test card that cannot fly its own experiment.

    python debugtests/check-card.py cards/alpha-pullup.json
    python debugtests/check-card.py cards/*.json
    python debugtests/check-card.py --selftest

Exit 1 if any card FAILs. WARN never fails the run.

WHY THIS EXISTS. Three cards flown on 2026-08-01/02 did not fly the experiment they were named
for, and all three failures were computable before the game launched:

  * `alpha-sweep`  -- demanded load through AZIMUTH, and an azimuth demand loads the wing only via
                      bank, clamped at `Cfg.MaxBankAngle` = 72 deg = n 3.24 g. The fighters need
                      4.5-6.4 g to reach their own alpha ceiling. 60 of 60 segments read
                      `aoaAboveCeilingPct` 0.0. (cards/ALPHA-CARD-REDESIGN.md, debugtests/R39-E-alpha.md)
  * `stol-sweep`   -- declared a 90 m/s entry and flew at 340-381 m/s. (debugtests/R39-stol.md)
  * `rotor-hover`  -- declared `startSpeed: 0` meaning hover; 0 WAS "the card doesn't say", so the
                      spawn fell through to `Drone/DroneSpawnSpeed`. (debugtests/R39-rotor.md)
                      FIXED IN THE HARNESS at v1.0.0, and this checker's rule is inverted to match
                      -- see the Card.Unset note below.

v1.0.0 MOVED THIS GROUND UNDER THE CHECKER. `ScenarioPlayer.Card` gained `Unset = -1f` and
`Declared(v) => v >= 0f`; `startSpeed` and `startAlt` now INITIALISE to `Unset`, so Newtonsoft
leaving an absent key alone is what "the card doesn't say" means, and a declared `0` is a real
condition (hover / sea level) at 15 converted sites. So the defect is no longer "0", it is ABSENT,
and the checker tests `declared()` -- never `> 0` -- exactly as the C# does. Three consequences the
checks below honour, all verified against the source rather than assumed:
  * `TestDrone.EntrySpeedFlyable` returns true at `speed <= 0` (a Vstall floor is a wing's number),
    so the envelope and stall-density checks must not re-impose what the harness now exempts;
  * `ScenarioPlayer.OwnInputs` still gates on `EntrySpeed(_card) <= 0f` deliberately -- a hover has
    no cruise for a fixed throttle to trim, and the source says so at the line;
  * `Preview` seeds the `Preflight` STRUCT's two fields to `Card.Unset` by hand, because a struct's
    fields default to 0 whatever the card class's initializer says. `source_invariants()` guards
    that one, since it is a C# regression a card file cannot express.

THE BUG CLASS IS THE SILENT FALLTHROUGH. A card field left at 0 / "" does not refuse -- it resolves
to a `Cfg` knob whose value nobody looked at, and the batch writes captures that score fine and
answer a different question. Every fallthrough this file knows about was read out of the C#, and
every constant it does arithmetic with is PARSED from the source rather than copied, so the checker
cannot quietly disagree with the harness after someone retunes a default.

Sibling checks, and where the line is:
  * `scorecard.card_setup_problems()` -- the card's own SETUP grammar (types, ranges, the armToggle
    /config collision). Called from here rather than re-spelled; this file adds the PHYSICS and the
    fallthroughs, which need the airframe table scorecard has no business reading.
  * `debugtests/test-fleet-resolve.py` -- compiles the shipped C# resolvers. That one proves the
    resolvers are right; this one proves a card survives them.
"""

import glob
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

sys.path.insert(0, HERE)
import scorecard  # noqa: E402  -- infer_type + card_setup_problems, the one definition of each


# --- constants, PARSED FROM SOURCE -----------------------------------------------------------
# Not copied. A hardcoded 72 or 250 here is a second definition that drifts the moment someone
# retunes the knob, and "the checker agrees with the mod" is this file's whole claim.

_BIND = re.compile(r'cf\.Bind\("(\w+)",\s*"(\w+)",\s*([^,]+?),')
_CONST_LINE = re.compile(r'private\s+const\s+(?:float|int)\s+(.+?);')
_CONST_PAIR = re.compile(r'(\w+)\s*=\s*(-?[\d.]+)')


def _read(rel):
    with open(os.path.join(REPO, rel), encoding="utf-8", errors="replace") as f:
        return f.read()


def _lit(tok):
    tok = tok.strip()
    if tok.startswith('"'):
        return tok.strip('"')
    if tok in ("true", "false"):
        return tok == "true"
    try:
        return float(tok.rstrip("f")) if ("." in tok or tok.endswith("f")) else int(tok)
    except ValueError:
        return None


def constants():
    """Every number this checker does arithmetic with, read out of the .cs that flies it."""
    k = {}
    for sec, key, tok in _BIND.findall(_read("Cfg.cs")):
        v = _lit(tok)
        if v is not None:
            k[sec + "/" + key] = v
    for rel in ("ScenarioPlayer.cs", "TestDrone.cs"):
        for body in _CONST_LINE.findall(_read(rel)):
            for name, num in _CONST_PAIR.findall(body):
                k[name] = float(num)
    missing = [n for n in ("Drone/DroneSpawnSpeed", "Drone/DroneSpawnAlt", "Drone/DroneAirframe",
                           "Drone/DroneCount", "Drone/DroneAltDeckM", "Drone/DroneStaggerSec",
                           "Scenario/ScenarioRepeat", "Scenario/ScenarioThrottle",
                           "Control/MaxBankAngle",
                           "MinThrottle", "StallMargin", "VMaxMargin",
                           "RateMinDegS", "RateMaxDegS", "FloorAltM") if n not in k]
    if missing:
        raise SystemExit("check-card: could not parse %s out of the source -- the .cs was "
                         "reformatted or a constant was renamed. Fix the regexes, do not "
                         "hardcode the values." % ", ".join(missing))
    return k


def source_invariants():
    """Problems with the HARNESS, not with a card -- the v1.0.0 sentinel coming undone.

    A card file cannot express these, and no card check can see them, but they silently restore the
    exact fallthrough this tool exists to catch, so they are worth one grep per invocation.

    1. `Preview`'s `Preflight` is a STRUCT. Its fields default to 0 whatever `Card`'s initializer
       says, so with no card selected an unseeded StartSpeed/StartAlt reads as a DECLARED HOVER AT
       SEA LEVEL and places the fleet there. It must be seeded to `Card.Unset` by hand.
    2. Any `StartSpeed > 0` / `startAlt > 0` style test is the old semantics coming back: it reads a
       declared 0 as "the card doesn't say" and re-lands on the Cfg knob."""
    out = []
    sp = _read("ScenarioPlayer.cs")
    prev = sp.split("internal static Preflight Preview", 1)
    head = prev[1][:900] if len(prev) > 1 else ""
    if not ("StartSpeed = Card.Unset" in head and "StartAlt = Card.Unset" in head):
        out.append("ScenarioPlayer.Preview does not seed the Preflight struct's StartSpeed/StartAlt "
                   "to Card.Unset -- a struct defaults them to 0, which now reads as a DECLARED "
                   "hover at sea level whenever no card is selected")
    for rel in ("ScenarioPlayer.cs", "TestDrone.cs"):
        for m in re.finditer(r"[Ss]tart(?:Speed|Alt)\s*>\s*0f", _read(rel)):
            out.append("%s: `%s` -- the v1.0.0 sentinel is being tested with `> 0` again, which "
                       "reads a DECLARED 0 (hover / sea level) as 'the card doesn't say'. Use "
                       "Card.Declared." % (rel, m.group(0)))
    return out


# --- the airframe roster, PARSED FROM AIRFRAMES.md -------------------------------------------
# Same reason. AIRFRAMES.md says outright that these numbers exist nowhere else in machine-readable
# form (they are Unity ScriptableObjects inside resources.assets), so that table IS the source.

_ROW = re.compile(r"^\|\s*`(\w+)`\s*\|(.*)$")


def _num(cell):
    cell = cell.replace("*", "").replace("`", "").replace("†", "").strip()
    try:
        return float(cell)
    except ValueError:
        return None      # "—" (an em dash) = never flown by this project, so no value to quote


def airframes():
    """jsonKey -> {cls, vstall, vmax, corner, glimit, event_only}. `corner` is the FBW one (the one
    startSpeedCorner resolves against since v0.96), None where the corpus has never seen it."""
    out = {}
    for line in _read("AIRFRAMES.md").splitlines():
        m = _ROW.match(line)
        if not m:
            continue
        c = [x.strip() for x in m.group(2).split("|")]
        if len(c) < 8:
            continue
        out[m.group(1)] = {
            "cls": c[2],
            "vstall": _num(c[3]), "vmax": _num(c[4]), "corner": _num(c[5]),
            "glimit": _num(c[7]),
            "event_only": "event-only" in c[2],
        }
    if len(out) < 10:
        raise SystemExit("check-card: parsed only %d airframes out of AIRFRAMES.md -- the table "
                         "shape changed." % len(out))
    return out


# --- physics ----------------------------------------------------------------------------------

G = 9.81


def rho_ratio(alt_m):
    """ISA troposphere density ratio. Checked against AIRFRAMES.md / ALPHA-CARD-REDESIGN.md: 0.4287
    at 8000 m and 0.539 at 6000 m, both of which those documents quote independently."""
    alt_m = max(0.0, min(11000.0, float(alt_m)))
    return (1.0 - 2.25577e-5 * alt_m) ** 4.2559


def n_at_alpha_ceiling(v, vstall, alt):
    """Load factor at which the wing sits AT its alpha ceiling.

    Stall IS the alpha ceiling at n=1 by definition, and lift scales as n ~ V^2*rho, so
    n_ceiling = (V/Vstall_sl)^2 * (rho/rho0). No separate alpha-ceiling table is needed or exists."""
    return (v / vstall) ** 2 * rho_ratio(alt)


def n_from_bank(max_bank_deg):
    """A level turn's load. This is the ONLY way an azimuth demand can load the wing, and the law
    clamps the bank, so it is a hard ceiling on an azimuth-driven card."""
    return 1.0 / math.cos(math.radians(max_bank_deg))


def n_from_pull(v, omega_deg_s):
    """n = cos(theta) + V*omega/g for a pull in the vertical plane, at theta = 0 (the optimistic
    bound). A TRACKED ramp's steady-state pitch rate is the marker's own sweep rate."""
    return 1.0 + v * math.radians(omega_deg_s) / G


def n_from_step(v, glimit, corner):
    """A constant-el STEP asks for everything, so the bound is the FBW's own full-stick rate command:
    targetPitchAngVel = pitch * gLimitPositive * 9.81 / max(V, 0.75*cornerSpeed) (decompile :65032).
    Load from that is V*omega/g, hence 1 + gLimit*V/max(V, 0.75*Vc) -- i.e. ~1 + gLimit above the
    knee, and less below it."""
    if not glimit:
        return None
    knee = max(v, 0.75 * corner) if corner else v
    return 1.0 + glimit * v / knee


# --- card model (mirrors ScenarioPlayer's resolvers) ------------------------------------------

def lane_airframes(card, K):
    """(keys, source) -- mirrors ScenarioPlayer.Validate's prose heal + TestDrone.AirframeList."""
    raw = card.get("airframe") or ""
    toks = [t.strip() for t in raw.split(",") if t.strip()]
    if any(any(ch.isspace() for ch in t) for t in toks):
        toks = []                                     # Validate blanks a prose field at load
    if toks:
        return toks, "card"
    fb = [t.strip() for t in str(K["Drone/DroneAirframe"]).split(",") if t.strip()]
    return fb, "FALLTHROUGH Drone/DroneAirframe"


UNSET = -1.0


def declared(card, key):
    """ScenarioPlayer.Card.Declared, in JSON terms. The C# field INITIALISES to Card.Unset (-1) and
    Newtonsoft assigns only keys the JSON actually carries, so an absent key IS the sentinel and a
    declared 0 is a real condition. Never `> 0` -- that test is the whole v1.0.0 bug class."""
    v = card.get(key, UNSET)
    return isinstance(v, (int, float)) and not isinstance(v, bool) and v >= 0


def entry_speed(card, af, K):
    """(m/s, how) for one lane -- mirrors ResolveStartSpeed + TestDrone.SpeedOfLane, fallthrough
    included. `af` is the airframe row or None. A returned 0 is a DECLARED HOVER, not a failure."""
    ssc = float(card.get("startSpeedCorner") or 0)
    ss = float(card["startSpeed"]) if declared(card, "startSpeed") else UNSET
    if ssc > 0:
        corner = af and af["corner"]
        if corner:
            return ssc * corner, "%.2fx FBW corner %g" % (ssc, corner)
        if ss >= 0:
            return ss, "startSpeedCorner unresolvable (no FBW corner) -> absolute %g" % ss
    if ss >= 0:
        return ss, "startSpeed %g" % ss
    return float(K["Drone/DroneSpawnSpeed"]), "FALLTHROUGH Drone/DroneSpawnSpeed"


def effective_throttle(card, K):
    """(value, how) -- the card's own pin if it has one, else Cfg. EntryThrottle then SNAPS anything
    under MinThrottle back to the DEFAULT (0.70), not to MinThrottle: a card asking for 0.10 flies
    at 0.70, which is the opposite of what it asked for."""
    for o in card.get("config") or []:
        parsed = scorecard.split_spec((o or {}).get("key") or "")
        if parsed == ("Scenario", "ScenarioThrottle"):
            try:
                return float(o.get("value")), "card pin"
            except (TypeError, ValueError):
                return None, "card pin (unparseable)"
    return float(K["Scenario/ScenarioThrottle"]), "FALLTHROUGH Scenario/ScenarioThrottle"


def replicates(card, K):
    r = int(card.get("repeat") or 0)
    return (min(max(r, 1), 20), "card") if r > 0 else \
           (min(max(int(K["Scenario/ScenarioRepeat"]), 1), 20), "FALLTHROUGH Scenario/ScenarioRepeat")


def fleet_size(card, K, n_keys, af_from_card):
    c = int(card.get("count") or 0)
    if c > 0:
        return min(max(c, 1), 16), "card count"
    if af_from_card and n_keys > 0:
        return min(max(n_keys, 1), 16), "airframe list (%d named)" % n_keys
    return min(max(int(K["Drone/DroneCount"]), 1), 16), "FALLTHROUGH Drone/DroneCount"


def seg_demand(seg, step):
    """(peak |az| deg, peak |el| deg, peak |d el/dt| deg/s, az_varies) for one segment."""
    ta, te = seg.get("trackAz") or [], seg.get("trackEl") or []
    if te:
        el_pk = max(abs(x) for x in te)
        rate = max((abs(te[i + 1] - te[i]) / step for i in range(len(te) - 1)), default=0.0)
    else:
        el_pk, rate = abs(float(seg.get("el") or 0)), 0.0
    if ta:
        az_pk = max(abs(x) for x in ta)
        az_var = max(ta) - min(ta) > 1e-9
    else:
        az_pk = abs(float(seg.get("az") or 0))
        az_var = False
    return az_pk, el_pk, rate, (az_var or bool(seg.get("deriveAzRate")))


# --- the checks --------------------------------------------------------------------------------

FAIL, WARN, INFO = "FAIL", "WARN", "INFO"


def check_card(card, K, AF):
    """[(level, check, message)] for one parsed card. Empty == nothing to say."""
    out = []
    add = lambda lvl, chk, msg: out.append((lvl, chk, msg))

    name = card.get("name") or "?"
    segs = card.get("segments") or []
    step = float(card.get("step") or 0.02) or 0.02

    # --- setup grammar: scorecard owns it, do not re-spell it here
    for p in scorecard.card_setup_problems(card):
        add(FAIL, "setup", p)

    # --- CHECK 1: every tag resolves to a scored type -----------------------------------------
    # An unrecognised tag is not an error at runtime; the segment simply becomes invisible to every
    # step-response / fine-tracking / alpha metric, with only the generic block computed.
    seen = {}
    for i, s in enumerate(segs):
        tag = (s.get("tag") or "").strip() or "seg%d" % i
        t = scorecard.infer_type(tag)
        if t == "unknown":
            add(FAIL, "tag", "segment %d tag '%s' matches no rule in scorecard.TAG_TYPE_RULES -- "
                             "it will be scored as 'unknown' and every metric this segment exists "
                             "to produce will be silently absent" % (i, tag))
        elif t == "untagged":
            add(WARN, "tag", "segment %d has no tag; Validate names it '%s', which scores as "
                             "'untagged' (generic metrics only)" % (i, tag))
        if t != "arm":
            # compare-runs.py keys segments by tag ALONE, so a repeat pools two different demands.
            if tag in seen:
                add(FAIL, "tag", "tag '%s' is used by segments %d and %d -- compare-runs.py keys "
                                 "segments by tag alone, so the two would be pooled as replicates "
                                 "of each other" % (tag, seen[tag], i))
            seen[tag] = i
    if segs and (segs[0].get("tag") or "") != "arm":
        add(FAIL, "tag", "first segment must be tagged 'arm' -- ScenarioPlayer.Validate refuses the "
                         "card outright and it will never load")

    # --- CHECK 4: the fleet ---------------------------------------------------------------------
    keys, af_src = lane_airframes(card, K)
    if af_src != "card":
        add(WARN, "airframe", "no airframe list, so the fleet is whatever %s holds (%r). The card "
                              "does not define its own test article." % (af_src, K["Drone/DroneAirframe"]))
    for k in keys:
        if k not in AF:
            add(FAIL, "airframe", "'%s' is not a jsonKey in AIRFRAMES.md -- TestDrone.Spawn refuses "
                                  "that lane (a list skips it, a single key cancels the launch)" % k)
        elif AF[k]["event_only"]:
            add(FAIL, "airframe", "'%s' is event-only content, gated by MissionManager."
                                  "AllowEventContent -- it will not spawn" % k)
    rows = [(k, AF[k]) for k in keys if k in AF and not AF[k]["event_only"]]

    # A rotorcraft card whose airframe list fell through lands on a fixed-wing key, and StartSuite's
    # `cls` filter then refuses the card on every lane -- the drones fly the built-in level-hold and
    # write nothing. This is the certain direction of the cls/airframe cross-check (a fixed-wing
    # airframe under a cls that does not admit Plane); the reverse needs a PilotType mapping the
    # decompile does not publish, so it is deliberately not checked.
    cls = [c.strip() for c in (card.get("cls") or "").split(",") if c.strip()]
    if cls and "Plane" not in cls:
        wrong = [k for k, r in rows if r["cls"].startswith("fixed-wing")]
        if wrong:
            add(FAIL, "cls", "cls is '%s' but the lanes resolve to fixed-wing %s (%s) -- "
                             "ScenarioPlayer.ClassMatches refuses the card on every lane and the "
                             "drones fly the built-in level-hold instead"
                             % (card.get("cls"), ", ".join(wrong), af_src))

    # --- CHECK 2: entry speed --------------------------------------------------------------------
    # INVERTED AT v1.0.0. The defect used to be `startSpeed: 0` (read as "doesn't say"); the harness
    # now reads it as a declared hover, so the defect is the field being ABSENT where the card's
    # intent needs it declared. Testing `> 0` here would refuse the two cards the fix repaired.
    ssc = float(card.get("startSpeedCorner") or 0)
    has_speed, has_alt = declared(card, "startSpeed"), declared(card, "startAlt")
    hover_card = any(scorecard.infer_type((s.get("tag") or "").strip() or "x") in ("hover_hold", "bobup")
                     for s in segs)
    if not has_speed and ssc <= 0:
        msg = ("no entry speed DECLARED (startSpeed absent, startSpeedCorner %g). The C# field stays "
               "at Card.Unset, so TestDrone.SpeedOfLane falls through to Drone/DroneSpawnSpeed = "
               "%g m/s and the spawn writes that velocity. The card is also UNGATED -- no "
               "PlaceOnCondition, so no per-replicate reset and no throttle ownership. Declare it: "
               "`startSpeed: 0` is a HOVER since v1.0.0 and is the right value for a rotorcraft card."
               % (ssc, K["Drone/DroneSpawnSpeed"]))
        if hover_card:
            msg += (" This card's segments are hover/bob, so it will be flown at %g m/s in forward "
                    "flight -- exactly the R39-rotor failure." % K["Drone/DroneSpawnSpeed"])
        add(FAIL, "entry-speed", msg)
    # The mirror: a declared hover under a fixed-wing card. A wing at 0 m/s is not an entry
    # condition, and `cls` does not stop it -- the placement writes the velocity either way.
    if has_speed and float(card["startSpeed"]) == 0 and ssc <= 0 and not hover_card:
        add(FAIL, "entry-speed", "declares `startSpeed: 0` (a HOVER since v1.0.0) but has no "
                                 "hover/bob segment -- the placement writes 0 m/s to a wing. Use a "
                                 "real entry speed, or tag the segments for what this actually is.")
    if not has_alt:
        add(WARN, "entry-alt", "no startAlt DECLARED, so the field stays at Card.Unset and the entry "
                               "altitude is Drone/DroneSpawnAlt = %g m. `startAlt: 0` means SEA "
                               "LEVEL since v1.0.0; absent means the knob decides."
                               % K["Drone/DroneSpawnAlt"])
    elif float(card["startAlt"]) < K["FloorAltM"]:
        add(FAIL, "entry-alt", "declares startAlt %g m, under the %g m MSL card floor -- "
                               "ScenarioPlayer aborts the card on the first tick below it, every "
                               "replicate. This is what `startAlt: 0` did to the rotor pair."
                               % (float(card["startAlt"]), K["FloorAltM"]))
    elif float(card["startAlt"]) - K["Drone/DroneAltDeckM"] / 2 < K["FloorAltM"]:
        add(WARN, "entry-alt", "startAlt %g m is fine, but Drone/DroneAltDeckM = %g puts the LOWER "
                               "deck at %g m -- under the %g m card floor, so half the fleet aborts "
                               "on its first tick. Set the knob to 0 for this card."
                               % (float(card["startAlt"]), K["Drone/DroneAltDeckM"],
                                  float(card["startAlt"]) - K["Drone/DroneAltDeckM"] / 2,
                                  K["FloorAltM"]))

    thr, thr_src = effective_throttle(card, K)
    if thr is not None and thr < K["MinThrottle"]:
        add(FAIL, "throttle", "throttle %.2f (%s) is under ScenarioPlayer.MinThrottle %.2f, so "
                              "EntryThrottle SNAPS IT TO THE DEFAULT %.2f -- not to the floor. The "
                              "card flies at %.2f, the opposite of what it asked for."
                              % (thr, thr_src, K["MinThrottle"], K["Scenario/ScenarioThrottle"],
                                 K["Scenario/ScenarioThrottle"]))

    alt = float(card["startAlt"]) if has_alt else float(K["Drone/DroneSpawnAlt"])
    deck = float(K["Drone/DroneAltDeckM"])

    for k, r in rows:
        v, how = entry_speed(card, r, K)
        vs, vm, corner = r["vstall"], r["vmax"], r["corner"]

        # A DECLARED HOVER SKIPS EVERY WING CHECK BELOW, because EntrySpeedFlyable does: v1.0.0 made
        # it return true at speed <= 0 outright ("a Vstall floor is a wing's number"), so re-imposing
        # one here would refuse the lanes the harness fix exists to allow.
        if v <= 0:
            continue

        # (a) the v0.92 pre-spawn envelope gate. A refused lane writes no capture at all.
        refused = False
        if vs and vm:
            lo, hi = vs * K["StallMargin"], vm * K["VMaxMargin"]
            if v < lo or v > hi:
                refused = True
                bound = ("below the %.2fx Vstall floor %.1f" % (K["StallMargin"], lo)) if v < lo else \
                        ("above the %.2fx Vmax ceiling %.1f" % (K["VMaxMargin"], hi))
                add(FAIL, "envelope", "%s: entry %.1f m/s (%s) is %s m/s (Vstall %.1f, Vmax %.1f) -- "
                                      "TestDrone.EntrySpeedFlyable REFUSES this lane pre-spawn, so "
                                      "it contributes no captures" % (k, v, how, bound, vs, vm))

        # (b) the gate is DENSITY-BLIND (its own comment says so). True stall TAS at the card's
        # altitude is Vstall/sqrt(rho/rho0), and the gate compares against the sea-level figure.
        if vs and not refused:
            for a, label in ((alt, "the card's %g m" % alt),
                             (alt + deck / 2.0, "the UPPER deck %g m (Drone/DroneAltDeckM = %g)"
                              % (alt + deck / 2.0, deck)) if deck > 0 else (None, None)):
                if a is None:
                    continue
                stall_tas = vs / math.sqrt(rho_ratio(a))
                if v < stall_tas * K["StallMargin"]:
                    lvl = FAIL if a == alt else WARN
                    add(lvl, "stall-density",
                        "%s: entry %.1f m/s (%s) is under %.2fx the DENSITY-CORRECTED stall at %s -- "
                        "Vstall %.1f / sqrt(%.4f) = %.1f m/s TAS, x%.2f = %.1f. "
                        "EntrySpeedFlyable checks the SEA-LEVEL %.1f and passes it."
                        % (k, v, how, K["StallMargin"], label, vs, rho_ratio(a), stall_tas,
                           K["StallMargin"], stall_tas * K["StallMargin"], vs))

        # (c) can the placement's speed survive the arm? The placement WRITES the speed once and
        # nothing HOLDS it (ScenarioPlayer.AuditHold). Below 0.75x the FBW corner the aircraft is in
        # the flat pitch-authority region (targetPitchAngVel's max(V, 0.75*Vc), decompile :65032) and
        # has large excess thrust at any throttle the mod will accept, so it accelerates out of the
        # band the card exists to measure.
        if corner and not refused and v < 0.75 * corner:
            add(FAIL, "entry-hold",
                "%s: entry %.1f m/s (%s) is %.2fx its FBW corner %g, i.e. under the 0.75x corner "
                "breakpoint (%.1f m/s) where FBW pitch authority goes flat. The placement writes the "
                "speed once and nothing holds it -- throttle is pinned at %s%.2f, and the floor is "
                "MinThrottle %.2f, so a lower pin is impossible. R39-stol measured this exact shape "
                "at 340-381 m/s against a declared 90."
                % (k, v, how, v / corner, corner, 0.75 * corner,
                   "" if thr_src == "card pin" else "the fallthrough ", thr or 0, K["MinThrottle"]))
        if ssc > 0 and not corner:
            add(WARN, "entry-speed", "%s: startSpeedCorner %.2fx, but AIRFRAMES.md has no measured "
                                     "FBW corner for it -- ResolveStartSpeed fails soft to the "
                                     "absolute startSpeed %g m/s, so this lane enters at a different "
                                     "aerodynamic state than the rest of the fleet" % (k, ssc, ss))

    # The deck spread lands on top of EVERY card's startAlt, so it is a run-level note printed once
    # by run() rather than 36 identical warnings -- what is card-specific is the upper-deck
    # stall-density case above, which is where it actually changes a verdict.

    # --- CHECK 3: is the demanded state physically reachable? ------------------------------------
    # Generalised from alpha-sweep. An ALPHA segment's whole claim is that it puts the wing on its
    # AoA ceiling; whether it can is arithmetic on the load the demand can generate against the load
    # the ceiling sits at.
    #
    # ONE of the two cases is REFUSABLE and the other is not, and the difference is trajectory:
    #   * AZIMUTH-driven. Load comes only from bank, and the law clamps bank at Cfg.MaxBankAngle, so
    #     n <= 1/cos(72) = 3.24 FOR THE WHOLE SEGMENT no matter what the demand grows to (past the
    #     clamp the surplus is thrown away -- `bankDemandExcessDeg` IS that throw-away). R39-E
    #     measured the card diving and GAINING q, so the ceiling does not come down to meet it
    #     either. Structural, trajectory-invariant => FAIL.
    #   * VERTICAL pull. The aircraft climbs and decelerates, so n_ceiling falls as V^2*rho while the
    #     available n falls only as V (ALPHA-CARD-REDESIGN.md 3.1) -- the entry-state numbers are a
    #     margin, not a verdict, and refusing on them would kill a card that reaches the ceiling
    #     three seconds in. Reported as INFO. Proving THAT case needs a trajectory integrator.
    # ponytail: no integrator. The entry-state margin plus the structural case caught the failure
    # that cost the flight time; add one if a vertical card ever fails in the air.
    max_bank = float(K["Control/MaxBankAngle"])
    n_bank = n_from_bank(max_bank)
    for i, s in enumerate(segs):
        tag = (s.get("tag") or "").strip() or "seg%d" % i
        if scorecard.infer_type(tag) not in ("alpha_hold", "alpha_step"):
            continue
        az_pk, el_pk, el_rate, az_var = seg_demand(s, step)
        az_only = (az_var or az_pk > 0.5) and el_rate < 0.5 and el_pk < 0.5
        for k, r in rows:
            if not r["vstall"]:
                continue
            v, how = entry_speed(card, r, K)
            need = n_at_alpha_ceiling(v, r["vstall"], alt)
            if az_only:
                lvl = FAIL if need > n_bank else INFO
                add(lvl, "alpha-reach",
                    "%s: segment '%s' demands load through AZIMUTH%s with el flat, and an azimuth "
                    "demand loads the wing ONLY through bank, which the law clamps at MaxBankAngle "
                    "%g deg -> n = 1/cos(%g) = %.2f g. Reaching the alpha ceiling at %.1f m/s (%s) "
                    "and %g m needs n = (%.1f/%.1f)^2 x %.4f = %.2f g. %s"
                    % (k, tag, " (deriveAzRate)" if s.get("deriveAzRate") else "",
                       max_bank, max_bank, n_bank, v, how, alt, v, r["vstall"], rho_ratio(alt), need,
                       ("SHORT BY %.2f g and the clamp is a hard ceiling -- no demand can close it. "
                        "This is alpha-sweep's failure exactly." % (need - n_bank)) if lvl == FAIL
                       else "Reachable, but only through the roll channel, so expect the bank / "
                            "turn-rate / blend rails to flag before the AoA metric says anything."))
                continue
            if el_rate > 0.5:
                got, src = n_from_pull(v, el_rate), "tracked pull %.1f deg/s" % el_rate
            elif el_pk > 0.5:
                got, src = n_from_step(v, r["glimit"], r["corner"]), \
                    "full-stick %.0f deg step, FBW rate bound" % el_pk
            else:
                got, src = 1.0, "no vertical demand"
            if got is None:
                continue
            add(INFO, "alpha-reach",
                "%s: '%s' %s -> %.2f g at the entry state vs %.2f g at the alpha ceiling "
                "((%.1f/%.1f)^2 x %.4f) = x%.2f. %s"
                % (k, tag, src, got, need, v, r["vstall"], rho_ratio(alt), got / need,
                   "Entry margin only -- the pull climbs and decelerates, which lowers the ceiling."
                   if got < need else "Reaches it at entry."))

    # --- CHECK 5: what this card costs in flight time ---------------------------------------------
    dur = sum(float(s.get("dur") or 0) for s in segs)
    rep, rep_src = replicates(card, K)
    cnt, cnt_src = fleet_size(card, K, len(keys), af_src == "card")
    stagger = float(K["Drone/DroneStaggerSec"])
    lane_s = dur * rep
    wall_s = lane_s + max(0, cnt - 1) * stagger
    add(INFO, "cost", "%.0f s/replicate x %d (%s) = %.1f min per lane; %d lane(s) (%s) fly "
                      "concurrently on a %gs stagger -> %.1f min wall clock, %.1f drone-minutes, "
                      "%d captures"
        % (dur, rep, rep_src, lane_s / 60.0, cnt, cnt_src, stagger,
           wall_s / 60.0, lane_s * cnt / 60.0, rep * cnt))
    if wall_s > 15 * 60:
        add(WARN, "cost", "%.1f min of wall clock for ONE card -- a batch that long is a night, not "
                          "a test. Split it or cut `repeat`." % (wall_s / 60.0))
    if name and card.get("note") and "SUPERSEDED" in str(card.get("note")).upper():
        add(WARN, "superseded", "the card's own note says SUPERSEDED -- it is on disk for the "
                                "capture index, not to be flown")
    return out


# --- driver -------------------------------------------------------------------------------------

def _emit(lvl, chk, msg, width=100):
    head = "    %-5s %-14s " % (lvl, chk)
    ind, line = " " * len(head), head
    for w in msg.split():
        if len(line) + len(w) > width and line != head and line != ind:
            print(line.rstrip())
            line = ind
        line += w + " "
    print(line.rstrip())


def run(paths, verbose=False):
    K, AF = constants(), airframes()
    bad = 0
    for p in source_invariants():
        _emit(FAIL, "*harness", p)
        bad = 1
    if float(K["Drone/DroneAltDeckM"]) > 0:
        _emit(WARN, "*altitude", "Drone/DroneAltDeckM = %g splits every fleet over decks at "
                                 "startAlt +/- %g m, so NO lane flies the altitude its card "
                                 "declares. Applies to all %d cards below; set it to 0 for a single "
                                 "deck. Every density figure below is quoted at the DECLARED "
                                 "altitude, with the binding upper deck called out per lane."
              % (K["Drone/DroneAltDeckM"], K["Drone/DroneAltDeckM"] / 2, len(paths)))
        print()
    for p in paths:
        try:
            with open(p, encoding="utf-8") as f:
                card = json.load(f)
        except Exception as e:                                     # noqa: BLE001 -- report, continue
            print("%s\n    FAIL  parse  %s" % (p, e))
            bad += 1
            continue
        card.setdefault("name", os.path.splitext(os.path.basename(p))[0])
        probs = check_card(card, K, AF)
        fails = [x for x in probs if x[0] == FAIL]
        warns = [x for x in probs if x[0] == WARN]
        if fails:
            bad += 1
        shown = probs if verbose else [x for x in probs if x[0] != INFO or x[1] == "cost"]
        head = "FAIL" if fails else ("warn" if warns else "ok  ")
        print("%s  %-22s %s" % (head, card["name"], p))
        for lvl, chk, msg in shown:
            _emit(lvl, chk, msg)
        print()
    print("%d card(s) checked, %d FAILED." % (len(paths), bad))
    return 1 if bad else 0


# --- selftest -------------------------------------------------------------------------------------
# The three regression cases are FROZEN COPIES of the cards as flown, not reads of cards/*.json:
# a fixture that tracks the file would stop testing the bug the moment someone fixes the file.

R_ALPHA_SWEEP = {
    "name": "alpha-sweep", "cls": "Plane", "step": 0.02,
    "airframe": "Fighter1, Multirole1, SmallFighter1, trainer, VTOLTrainer1, EW1, FastBomber1, Darkreach",
    "startSpeed": 250, "startAlt": 8000, "repeat": 8,
    "segments": [{"tag": "arm", "dur": 6, "az": 0, "el": 0},
                 {"tag": "alphaHold", "dur": 35, "az": 0, "el": 0, "deriveAzRate": True}],
}
R_STOL_SWEEP = {
    "name": "stol-sweep", "cls": "Plane", "step": 0.02, "airframe": "",
    "startSpeed": 90, "startAlt": 2500,
    "segments": [{"tag": "arm", "dur": 6, "az": 0, "el": 0},
                 {"tag": "turn360stol", "dur": 30, "az": 0, "el": 0, "deriveAzRate": True}],
}
# rotor-hover as flown, and as REWRITTEN. v1.0.0 fixed this one IN THE HARNESS, not in the file:
# `startSpeed: 0` is now a declared hover, so the as-flown JSON below is no longer a defective card
# -- it is the CORRECT one, minus the roster and the altitude the card author then fixed.
#
# So the fixture is deliberately NOT kept as a historical regression. Keeping it would mean asserting
# that the checker FAILs a card that now flies correctly, i.e. freezing the wrong rule into the one
# place meant to detect a wrong rule. The frozen-fixture principle protects against a FILE being
# fixed under the test; here the SEMANTICS moved, and a fixture cannot outvote the shipped code.
# It is kept as a POSITIVE control (must no longer fail on entry-speed), and the live defect -- the
# field being ABSENT -- gets its own fixture. Between them they pin both directions of the inversion.
R_ROTOR_HOVER_ASFLOWN = {
    "name": "rotor-hover", "cls": "Helo,Tiltwing,VTOL", "step": 0.02, "airframe": "",
    "startSpeed": 0, "startAlt": 0,
    "segments": [{"tag": "arm", "dur": 6, "az": 0, "el": 0},
                 {"tag": "hover", "dur": 25, "az": 0, "el": 0},
                 {"tag": "hoveryawR", "dur": 15, "az": 90, "el": 0},
                 {"tag": "hoveryawL", "dur": 15, "az": 0, "el": 0}],
}
# The live defect: the same card with the field ABSENT rather than declared 0. Nothing in the JSON
# distinguishes this from the one above by eye, and that is the point of the whole v1.0.0 sentinel.
R_ROTOR_ABSENT = {k: v for k, v in R_ROTOR_HOVER_ASFLOWN.items() if k not in ("startSpeed", "startAlt")}
R_ROTOR_ABSENT["airframe"] = "AttackHelo1, UtilityHelo1, QuadVTOL1"


def selftest():
    K, AF = constants(), airframes()

    # --- the parsers actually parsed something real
    assert K["Control/MaxBankAngle"] == 72.0, K["Control/MaxBankAngle"]
    assert K["Drone/DroneSpawnSpeed"] == 250.0
    assert K["MinThrottle"] == 0.25 and K["StallMargin"] == 1.10 and K["VMaxMargin"] == 0.95
    assert len(AF) == 14, len(AF)
    assert AF["Fighter1"]["vstall"] == 72.2 and AF["Fighter1"]["corner"] == 160.0
    assert AF["UtilityHelo1"]["corner"] is None      # em dash = never flown, not zero
    assert AF["UFO"]["event_only"]

    # --- physics, against numbers the repo states independently
    assert abs(rho_ratio(8000) - 0.4287) < 5e-4, rho_ratio(8000)   # ALPHA-CARD-REDESIGN.md 3.3
    assert abs(rho_ratio(6000) * 1.225 - 0.660) < 5e-3             # ScenarioPlayer/TestDrone comment
    assert abs(n_from_bank(72.0) - 3.236) < 1e-3                   # the 3.24 g the redesign quotes
    # Fighter1 at its own corner speed, sea level -- the prompt's "needs 4.9 g" case
    assert abs(n_at_alpha_ceiling(160, 72.2, 0) - 4.91) < 0.02
    assert n_from_pull(184, 18) > 6.8 and n_from_pull(184, 0) == 1.0
    assert abs(n_from_step(250, 9.0, 160) - 10.0) < 1e-9        # above the 0.75*Vc knee: 1 + gLimit
    assert n_from_step(60, 9.0, 160) < 5.6                      # below it, authority is flat

    def levels(card, check):
        return [(l, m) for l, c, m in check_card(card, K, AF) if c == check]

    # the baseline every negative control is a variation of -- nothing here may FAIL
    good = {"name": "ok", "cls": "Plane", "step": 0.02, "airframe": "Fighter1",
            "startSpeed": 250, "startAlt": 4000, "repeat": 4,
            "segments": [{"tag": "arm", "dur": 6, "az": 0, "el": 0},
                         {"tag": "az30R", "dur": 8, "az": 30, "el": 0}]}
    assert not [x for x in check_card(good, K, AF) if x[0] == FAIL], check_card(good, K, AF)

    # --- REGRESSION 1: alpha-sweep is structurally incapable of its own experiment
    hits = levels(R_ALPHA_SWEEP, "alpha-reach")
    assert hits and all(l == FAIL for l, _ in hits), hits
    assert len(hits) == 8, len(hits)                                # one per named airframe
    assert "3.24 g" in hits[0][1] and "SHORT BY" in hits[0][1], hits[0][1]
    # ...and the SAME card with the demand moved into the vertical plane must stop failing: the
    # verdict is about the demand axis, not about the card's name or its entry condition.
    vert = dict(R_ALPHA_SWEEP, segments=[
        R_ALPHA_SWEEP["segments"][0],
        {"tag": "alphaHold", "dur": 35, "az": 0, "el": 0, "step": 0.02,
         "trackAz": [0.0] * 101, "trackEl": [0.36 * i for i in range(101)]}])
    assert not [1 for l, _ in levels(vert, "alpha-reach") if l == FAIL], levels(vert, "alpha-reach")

    # --- REGRESSION 2: stol-sweep's declared 90 m/s will not be flown
    assert any(l == FAIL for l, _ in levels(R_STOL_SWEEP, "entry-hold")), \
        "stol-sweep must fail entry-hold: 90 m/s is 0.56x Multirole1's FBW corner"
    assert any(l == WARN for l, _ in levels(R_STOL_SWEEP, "airframe")), \
        "stol-sweep names no airframe -- the fleet is whatever F1 holds"
    # ...and the one airframe that DID fly it correctly (COIN, 104 m/s mean) must NOT be flagged:
    # 90 m/s is above 0.75 x COIN's 110 corner. A check that flags the working lane is noise.
    coin = dict(R_STOL_SWEEP, airframe="COIN")
    assert not levels(coin, "entry-hold"), levels(coin, "entry-hold")

    # --- REGRESSION 3: the rotor pair, BOTH DIRECTIONS of the v1.0.0 inversion.
    # (a) the live defect -- the field ABSENT, so it stays at Card.Unset and falls through
    hits = levels(R_ROTOR_ABSENT, "entry-speed")
    assert hits and hits[0][0] == FAIL and "DroneSpawnSpeed" in hits[0][1], hits
    assert "R39-rotor failure" in hits[0][1], hits[0][1]
    assert any(l == WARN for l, _ in levels(R_ROTOR_ABSENT, "entry-alt")), "absent startAlt"
    # (b) the positive control -- a DECLARED 0 is a hover and must NOT be refused any more
    assert not levels(R_ROTOR_HOVER_ASFLOWN, "entry-speed"), \
        "startSpeed: 0 is a declared hover since v1.0.0 -- refusing it refuses the fixed card"
    for chk in ("envelope", "stall-density", "entry-hold"):
        assert not levels(R_ROTOR_HOVER_ASFLOWN, chk), \
            "%s must exempt a declared hover, as TestDrone.EntrySpeedFlyable does" % chk
    # ...but its two REAL remaining defects still stand, and are what the card author then fixed
    assert any(l == FAIL for l, _ in levels(R_ROTOR_HOVER_ASFLOWN, "cls")), \
        "a rotorcraft cls with no airframe list falls through to a fixed-wing key"
    assert any(l == FAIL and "card floor" in m for l, m in levels(R_ROTOR_HOVER_ASFLOWN, "entry-alt")), \
        "startAlt 0 is sea level, under the 500 m card floor"
    # the shipped rewrite (roster + startAlt 1000) must now come out clean
    fixed = dict(R_ROTOR_HOVER_ASFLOWN, airframe="AttackHelo1, UtilityHelo1, QuadVTOL1", startAlt=1000)
    assert not [x for x in check_card(fixed, K, AF) if x[0] == FAIL], check_card(fixed, K, AF)
    # the mirror defect: a declared hover on a card with no hover segment
    assert any(l == FAIL for l, _ in levels(dict(good, startSpeed=0), "entry-speed"))

    # --- the harness invariants the sentinel rests on
    assert source_invariants() == [], source_invariants()

    # --- tag check: an unscored tag is invisible, and a duplicate pools two demands
    bad_tag = dict(good, segments=[{"tag": "arm", "dur": 6}, {"tag": "wibble", "dur": 8}])
    assert any(l == FAIL and "TAG_TYPE_RULES" in m for l, m in levels(bad_tag, "tag"))
    dup = dict(good, segments=[{"tag": "arm", "dur": 6}, {"tag": "az30R", "dur": 8},
                               {"tag": "az30R", "dur": 8}])
    assert any(l == FAIL and "pooled" in m for l, m in levels(dup, "tag"))
    # two `arm` segments are legal -- arm is excluded from scoring
    twoarm = dict(good, segments=[{"tag": "arm", "dur": 6}, {"tag": "az30R", "dur": 8},
                                  {"tag": "arm", "dur": 6}])
    assert not levels(twoarm, "tag"), levels(twoarm, "tag")

    # --- envelope + throttle + unknown airframe
    assert any(l == FAIL and "REFUSES" in m
               for l, m in levels(dict(good, airframe="COIN"), "envelope"))     # 250 > 0.95*141.7
    assert any(l == FAIL for l, _ in levels(dict(good, airframe="Attacker1"), "airframe"))
    lowthr = dict(good, config=[{"key": "Scenario/ScenarioThrottle", "value": "0.10"}])
    assert any(l == FAIL and "SNAPS" in m for l, m in levels(lowthr, "throttle"))

    # --- the density-blind gate: Darkreach at 1.0x corner (100 m/s) at 8000 m is below stall,
    #     and EntrySpeedFlyable passes it. ALPHA-CARD-REDESIGN.md 3.3 states exactly this.
    dk = dict(good, airframe="Darkreach", startSpeed=0, startSpeedCorner=1.0, startAlt=8000)
    assert any(l == FAIL and "DENSITY-CORRECTED" in m for l, m in levels(dk, "stall-density"))
    dk115 = dict(dk, startSpeedCorner=1.15)
    assert not [1 for l, _ in levels(dk115, "stall-density") if l == FAIL], "1.15x clears it"

    # --- cost arithmetic
    cost = [m for l, c, m in check_card(dict(good, repeat=8, airframe="Fighter1, Multirole1"), K, AF)
            if c == "cost"][0]
    assert "1.9 min per lane" in cost, cost      # (6+8)*8 = 112 s
    print("check-card selftest OK")
    return 0


def main(argv):
    if "--selftest" in argv:
        return selftest()
    verbose = "--verbose" in argv or "-v" in argv
    paths = []
    for a in argv:
        if a.startswith("-"):
            continue
        paths.extend(sorted(glob.glob(a)) if any(ch in a for ch in "*?[") else [a])
    if not paths:
        print(__doc__.strip().splitlines()[0])
        print("usage: check-card.py <card.json>... | --selftest [-v]")
        return 2
    return run(paths, verbose)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
