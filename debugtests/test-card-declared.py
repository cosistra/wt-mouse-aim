#!/usr/bin/env python3
"""Check the DECLARED-ZERO rule on card entry conditions (v1.0.0) — the #80 fallthrough class.

THE BUG THIS EXISTS FOR. `Card.startSpeed` and `Card.startAlt` are the only two card fields whose
physical range includes zero: 0 m/s is a hover, 0 m MSL is the deck. They used to default to a plain
`0`, so an ABSENT field and a field the author explicitly wrote as `0` were the same number, and
every consumer resolved that number with `v > 0f ? v : Cfg.Whatever.Value` — i.e. "0 means the card
doesn't say". `cards/rotor-hover.json` said `"startSpeed": 0` and meant hover; `TestDrone.SpeedOf`
read it as unset and spawned the fleet at `DroneSpawnSpeed`. Forty-eight rotorcraft captures were
flown at 6-110 m/s in forward flight, climbing 80-1500 m, while the card, the log and the header all
named a hover (`debugtests/R39-rotor.md` §5 H1). Nothing refused. Nothing scored badly. The artifact
simply answered a different question.

THE FIX IS A SENTINEL, AND SENTINELS ROT. `startSpeed`/`startAlt` initialise to `Card.Unset` (-1),
Newtonsoft assigns only the keys the JSON carries, and every "did the card say?" test is
`Card.Declared(v)` (`v >= 0f`). That works right up until the next agent writes one more
`p.StartSpeed > 0f` — which compiles, flies, scores, and silently restores the whole defect for that
one path. So this check is a SCAN, not a case table: it fails on any comparison-against-zero of the
four names anywhere in the two files that own them.

WHY IT IS PURE SOURCE AND STDLIB. The serialisation half — that the region compiles and that every
card in `cards/` round-trips through the real Newtonsoft — is already `test-card-model.py`'s job and
is not duplicated here. What that one cannot see is the RESOLUTION rule, which lives in ordinary C#
expressions spread over two files; and what neither can see is a card on disk whose declared zero is
a typo rather than a hover. Both are here. No SDK, no game install.

Usage:
    python debugtests/test-card-declared.py
"""

import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCEN = REPO / "ScenarioPlayer.cs"
DRONE = REPO / "TestDrone.cs"
CARDS = REPO / "cards"

# The four spellings of the two zero-meaningful fields: the card's own, and the Preflight twins
# TestDrone resolves against with no aircraft in hand.
GUARDED = ("startSpeed", "startAlt", "StartSpeed", "StartAlt")

# THE ONE DELIBERATE EXCEPTION, and it has to be declared AT THE SITE rather than listed in here.
# A line carrying this marker (on it, or in the comment block immediately above it) is exempt. Kept
# as a source marker on purpose: an allowlist of file:line in this file rots on the next edit, and an
# allowlist of function names hides the reason from the person reading the C#. The count is printed,
# so a rule that quietly grows a second and third exception is visible rather than assumed.
EXEMPT = "declared-zero-ok:"

# Rotorcraft pilot types. A card outside these classes declaring a 0 entry speed is not a hover, it
# is a wing at zero airspeed — i.e. a typo that the sentinel now faithfully carries into a placement.
ROTOR_CLASSES = {"Helo", "Tiltwing", "VTOL"}


def strip_comments(src):
    """Blank out // and /* */ and string literals, keeping line count and offsets stable.

    Line-preserving so a hit can still be reported by line number. String literals go too: the log
    lines in these files quote `startSpeed` in prose, and prose is not a comparison.
    """
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join(ch if ch == "\n" else " " for ch in src[i:j]))
            i = j
        elif c in "\"'":
            j = i + 1
            while j < n and src[j] != c:
                j += 2 if src[j] == "\\" else 1
            j = min(j + 1, n)
            out.append("".join(ch if ch == "\n" else " " for ch in src[i:j]))
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def exempt_at(raw_lines, i):
    """Is line `i` (1-based) marked as a deliberate exception, on it or just above it?"""
    if EXEMPT in raw_lines[i - 1]:
        return True
    j = i - 2                                   # walk back over the contiguous comment block
    while j >= 0 and raw_lines[j].lstrip().startswith("//"):
        if EXEMPT in raw_lines[j]:
            return True
        j -= 1
    return False


def scan(problems, rx, message):
    """Run one regex over both files' code (comments and strings blanked), honouring the marker.

    Returns the number of exempted hits, which main() prints — a silent allowlist is how a rule with
    one stated exception becomes a rule with four unstated ones.
    """
    exempted = 0
    for path in (SCEN, DRONE):
        raw = path.read_text(encoding="utf-8", errors="replace")
        raw_lines = raw.splitlines()
        for i, line in enumerate(strip_comments(raw).splitlines(), 1):
            m = rx.search(line)
            if not m:
                continue
            if exempt_at(raw_lines, i):
                exempted += 1
                continue
            problems.append(message(path, i, line.strip(), m))
    return exempted


# `<name>` then an optional `)`/whitespace run, then a relational operator against a 0 literal —
# `p.StartSpeed > 0f`, `c.startAlt <= 0`, `(x.StartAlt) != 0`.
ZERO_TEST = re.compile(r"\b(" + "|".join(GUARDED) + r")\b\s*(?:\)\s*)*(<=|>=|<|>|==|!=)\s*0f?\b")
# The resolved form. Same sentinel, so the same rule; caught by call rather than by field name.
ENTRY_TEST = re.compile(r"\bEntrySpeed\s*\([^()]*\)\s*(<=|>=|<|>)\s*0f?\b")


def scan_zero_tests(problems):
    """No comparison-against-zero of a zero-meaningful field. The whole class, in one rule."""
    return scan(
        problems, ZERO_TEST,
        lambda path, i, line, m: (
            f"{path.name}:{i} tests `{m.group(1)} {m.group(2)} 0` ({line}). A card that DECLARES 0 "
            f"means it — 0 m/s is a hover, 0 m MSL is the deck — so a comparison against zero reads a "
            f"real condition as 'the card doesn't say' and substitutes a Cfg default. Use "
            f"Card.Declared(v), or mark the line `{EXEMPT} <reason>` if zero genuinely belongs with "
            f"unset here. (R39-rotor.md §5 H1: 48 'hover' captures flown at DroneSpawnSpeed.)"
        ),
    )


def scan_entryspeed_tests(problems):
    """`EntrySpeed(...)` resolves to the same sentinel, so it takes the same test."""
    return scan(
        problems, ENTRY_TEST,
        lambda path, i, line, m: (
            f"{path.name}:{i} tests `EntrySpeed(...) {m.group(1)} 0` ({line}). It returns Card.Unset "
            f"for an undeclared card and 0.0 for a declared hover; comparing against zero collapses "
            f"the two. Use Card.Declared(EntrySpeed(c)), or mark the line `{EXEMPT} <reason>`."
        ),
    )


def check_model(problems):
    """The sentinel itself: negative, on both fields, with Declared spelled `>= 0`."""
    src = SCEN.read_text(encoding="utf-8", errors="replace")
    m = re.search(r"public const float Unset\s*=\s*(-?[\d.]+)f?\s*;", src)
    if not m:
        problems.append("ScenarioPlayer.Card.Unset not found — has the card model's sentinel moved?")
    elif float(m.group(1)) >= 0:
        problems.append(
            f"ScenarioPlayer.Card.Unset is {m.group(1)}, which is not negative. It has to sit OUTSIDE "
            f"the physical range of both fields, or 'absent' becomes a flyable condition again."
        )
    if not re.search(r"public static bool Declared\(float v\)\s*=>\s*v\s*>=\s*0f\s*;", src):
        problems.append(
            "ScenarioPlayer.Card.Declared is not `v >= 0f`. `> 0f` would exclude exactly the declared "
            "zero the sentinel exists to admit, which is the original defect with extra steps."
        )
    for f in ("startSpeed", "startAlt"):
        if not re.search(r"public float\s+" + f + r"\s*=\s*Unset\s*;", src):
            problems.append(
                f"Card.{f} no longer initialises to `Unset`. Newtonsoft only assigns keys the JSON "
                f"carries, so the field initializer IS the 'absent' value — without it an absent "
                f"field is 0 again and reads as a declared hover / a declared sea-level entry."
            )


def check_preview_seed(problems):
    """A struct's default is 0; the card's is Unset. Preview is where the two meet."""
    src = strip_comments(SCEN.read_text(encoding="utf-8", errors="replace"))
    m = re.search(r"var p = new Preflight\s*\{(.*?)\}\s*;", src, re.S)
    if not m:
        problems.append("ScenarioPlayer.Preview's `new Preflight { … }` seed not found.")
    elif not ("StartSpeed = Card.Unset" in m.group(1) and "StartAlt = Card.Unset" in m.group(1)):
        problems.append(
            "ScenarioPlayer.Preview does not seed StartSpeed/StartAlt to Card.Unset. Preflight is a "
            "STRUCT, so its fields default to 0 — and with no card selected (or on the catch path) "
            "that 0 now reads as a DECLARED hover at sea level and the fleet is placed there."
        )


def check_flyable_hover_exemption(problems):
    """A hover must not be refused by a wing's stall floor, and the rule belongs in the shared gate."""
    src = strip_comments(DRONE.read_text(encoding="utf-8", errors="replace"))
    m = re.search(r"internal static bool EntrySpeedFlyable\([^)]*\)\s*\{(.*?)\n        \}", src, re.S)
    if not m:
        problems.append("TestDrone.EntrySpeedFlyable not found.")
    elif not re.search(r"if\s*\(\s*speed\s*<=\s*0f\s*\)\s*return true\s*;", m.group(1)):
        problems.append(
            "TestDrone.EntrySpeedFlyable no longer exempts `speed <= 0`. Vstall is a wing's number; "
            "applied to a declared hover it refuses every rotorcraft lane before it spawns, and the "
            "batch comes back as a log line instead of captures. It belongs HERE and not at the two "
            "call sites, which are the spawn velocity and PlaceOnCondition — the two writes of a "
            "speed to an aircraft, which must never disagree about what they gate."
        )


def check_cards(problems):
    """A declared zero on disk must be a rotorcraft hover, and the rotor pair must still declare it."""
    saw_rotor_zero = 0
    for path in sorted(CARDS.glob("*.json")):
        try:
            card = json.loads(path.read_text(encoding="utf-8"))
        except Exception as e:                                  # noqa: BLE001 - report, never raise
            problems.append(f"cards/{path.name} is not readable JSON ({e}).")
            continue
        classes = {c.strip() for c in (card.get("cls") or "").split(",") if c.strip()}
        rotor = bool(classes & ROTOR_CLASSES)
        if card.get("startSpeed") == 0:
            if rotor:
                saw_rotor_zero += 1
            else:
                problems.append(
                    f"cards/{path.name} declares `startSpeed: 0` on cls '{card.get('cls')}'. Since "
                    f"v1.0.0 that is a DECLARED hover and the placement writes 0 m/s — for a wing "
                    f"that is a stall, not an entry condition. Omit the field to mean 'unset'."
                )
        if card.get("startAlt") == 0 and card.get("startSpeed") == 0:
            problems.append(
                f"cards/{path.name} declares BOTH `startSpeed: 0` and `startAlt: 0`, i.e. a hover at "
                f"sea level — under the 500 m card floor, so the run aborts on its first tick. This "
                f"is the pre-v1.0.0 shape of the rotor cards, where both zeros meant 'unset'."
            )
    if saw_rotor_zero == 0:
        problems.append(
            "No card in cards/ declares `startSpeed: 0` any more. The rotor pair is the only in-tree "
            "exercise of the declared-zero path; without one, this whole rule is untested by the grid."
        )
    return saw_rotor_zero


def main():
    problems = []
    exempted = scan_zero_tests(problems) + scan_entryspeed_tests(problems)
    check_model(problems)
    check_preview_seed(problems)
    check_flyable_hover_exemption(problems)
    n_zero = check_cards(problems)

    for p in problems:
        print(f"FAIL  {p}")
    if problems:
        print(f"\n{len(problems)} problem(s). See ScenarioPlayer.Card.Unset for the rule.")
        return 1
    print(f"ok  Card.Unset is negative, Declared is `>= 0f`, both fields initialise to it")
    print(f"ok  no unmarked `startSpeed/startAlt/StartSpeed/StartAlt/EntrySpeed() <op> 0` test in "
          f"ScenarioPlayer.cs or TestDrone.cs ({exempted} marked `{EXEMPT}`)")
    print(f"ok  Preview seeds the Preflight struct to Card.Unset; EntrySpeedFlyable exempts a hover")
    print(f"ok  {len(list(CARDS.glob('*.json')))} card(s) scanned, {n_zero} declaring a hover entry")
    return 0


if __name__ == "__main__":
    sys.exit(main())
