#!/usr/bin/env python3
"""Score a maneuver-recorder CSV into per-segment metrics (instructor-feedback-loop M0).

Stdlib only (no pandas/numpy), reuses analyze-wobble.py's CSV/header parsing, episode
(oscillation) detector and pitch-authority (relay) check rather than reimplementing them.

    python scorecard.py <recording.csv> [more.csv ...]        # human-readable table to stdout
    python scorecard.py --json score.json <recording.csv>     # write score.json (exactly 1 CSV)
    python scorecard.py --verbose <many.csv ...>              # per-file tables even past 10 files
    python scorecard.py --deadscan <many.csv ...>             # which columns never varied (a report)
    python scorecard.py --selftest                             # in-memory asserts, no file needed

OUTPUT VOLUME: the per-file table is ~15 lines, which is right for the 1-12 captures this was built
for and unreadable for the 100-450 an unattended batch now produces. Past DETAIL_FILE_LIMIT files the
per-file tables (and their stderr warning copies) are replaced by ONE roll-up — file/segment/card
counts plus every distinct warning with the number of files it fired on. `--verbose` forces the old
behaviour; at or below the limit nothing changes at all, because that is the interactive case.

SCOPE (v1 / M0, see plans/instructor-feedback-loop.md #4 and #8): RAW metrics only — no grading
against an airframe's theoretical bound (that's M3). Every metric is still stored as
{"value": X, "grade": null} so M3 can fill in "grade" later without reshaping this JSON.

COLUMNS: the base 45-column recorder header (see Recording.cs) is always present. M0 adds nine
more at the END — alt, airDensity, posX/Y/Z, velX/Y/Z, segTag — plus an optional sibling sidecar
`<basename>.airframe.json`. Older captures (and anything in debugtests/ predating this change)
won't have them. Every column is looked up BY HEADER NAME (csv.DictReader), never by position,
and a metric whose input column is missing is simply skipped with a reason recorded in that
segment's "skipped" dict — never a crash.

SEGMENTATION: consecutive rows sharing the same `segTag` form one segment; a CSV with no segTag
column at all becomes a single segment named "unsegmented". The segment TYPE is inferred from the
tag via TAG_TYPE_RULES (az_step, el_step, oblique_step, fine_track, sustained_turn, alpha_step,
alpha_hold, reversal, astern_wrap, micro_step, hover_hold, translate, bobup, transition, untagged,
arm); anything else — including "unsegmented"
— gets the generic metric set (the AoA/G discipline block, the only metrics that need no
segment-type logic). `arm` segments are reported (tag/type/count/duration) but carry no metrics —
the plan marks the post-spawn arm window excluded from scoring. A tag that matches nothing in
TAG_TYPE_RULES produces a WARNING (table output + the JSON's "warnings" list) rather than quietly
falling back to the generic set — see TAG_TYPE_RULES's docstring for why that used to be silent.
"""
import csv, json, math, os, re, statistics, sys
import importlib.util as _ilu

# --- reuse analyze-wobble.py's parsing/detector helpers (hyphenated filename => can't `import`) --
_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = _ilu.spec_from_file_location("analyze_wobble", os.path.join(_HERE, "analyze-wobble.py"))
aw = _ilu.module_from_spec(_spec)
_spec.loader.exec_module(aw)

# flightscore owns the cross-fight predicate and its deadband (see flightscore.opposed). Loaded the
# same way rather than by `import` because scorecard is itself exec_module'd from other dirs
# (index-captures.py, test-spec-grammar.py), where debugtests/ is not on sys.path. No cycle:
# flightscore imports stdlib only.
_fspec = _ilu.spec_from_file_location("flightscore", os.path.join(_HERE, "flightscore.py"))
fs = _ilu.module_from_spec(_fspec)
_fspec.loader.exec_module(fs)

G0 = 9.81  # m/s^2, for energy-height Eh = alt + V^2/2g (plan #4)

# Demand-scaled settle band for angular steps (see step_response_metrics). 10% of the step,
# floored at 0.05 deg (~0.9 mil — tighter than gun dispersion, so "settled" still means settled)
# and capped at 0.5 deg so steps >= 5 deg keep the classic fixed band.
BAND_FRAC, BAND_MIN_DEG, BAND_MAX_DEG = 0.10, 0.05, 0.5

# terminalOffDeg averages the last this-many seconds of a segment. 1.0 s ~= 16 recorder samples at
# the ~16 Hz sample rate (alternating 0.050/0.067 s steps), enough to average out the 0.01 deg
# column quantization without smearing in the approach.
TERMINAL_WINDOW_S = 1.0

# --- THE RESOLUTION FLOOR OF THE `off` COLUMN, and it is NOT the printed 0.01 deg -----------------
# `off` is written "{off:0.00}" (Recording.cs) from Vector3.Angle(t.forward, aimDir)
# (ChaseController.cs), i.e. acos(dot) evaluated in float32. Float32 spacing just below dot = 1.0 is
# 5.96e-8, so the smallest NON-ZERO angle acos can return is sqrt(2 * 5.96e-8) rad = 0.0198 deg. The
# print quantum is 0.01 deg; the MEASUREMENT quantum is twice that, so the column can only ever emit
# 0.00 or >= 0.02 near boresight. That is not inference: across 279k R35 oblique rows the value 0.01
# never occurs once, while 0.00 (43,285 rows) and 0.02 (15,122) both occur tens of thousands of times.
#
# Consequence, and the reason this constant exists: a pointing metric at or under it is FLOAT GRAIN,
# not a score. R35 measured what that does to a ranking -- the six wrapped airframes ranked by
# terminalOffDeg on their near lanes (0.6-24 km from the world origin) vs their far ones (68-98 km),
# same batch, same card, same 30 s legs, 32 legs per cell: Spearman +0.03, i.e. nothing. The identical
# cells ranked by rmsPointingErrorDeg, and by fixedWindowOffDeg below: +1.00 both. 94 of the 192
# near-lane terminal windows read exactly 0.0000 and three airframes tied there. Anything that can
# land here must be None'd or flagged, never published as a measurement -- see floor_warning.
OFF_QUANTUM_DEG = 0.0198
# ...and the test is TWO quanta, not one, because 0.02 is itself a floor reading: it is the FIRST
# rung, so a sample -- or a mean -- sitting on it is indistinguishable from 0.0198 or 0.0395 and can
# order nothing. ONE threshold, used by both the per-sample count (offFloorPct) and the
# is-this-a-measurement test on a mean; a "sample floor" and a separate "mean floor" would be two
# numbers for one fact and this corpus has enough of those.
OFF_FLOOR_DEG = 2.0 * OFF_QUANTUM_DEG      # 0.0396 deg == the printed 0.00 and 0.02 rungs

# --- fixedWindowOffDeg's window: anchored at segment START, never at its end ----------------------
# The window that makes an 8 s leg and a 30 s leg comparable AT ALL. terminalOffDeg is anchored at the
# END, so on an 8 s leg it scores a mid-transient and on a 30 s leg a settled residual -- two different
# quantities under one column name. Measured over the R33 (8 s legs) / R35 (30 s legs) pair: terminal
# vs terminal correlates +0.103 across the ten shared cells, while R33's terminal vs R35's off over
# THIS window correlates +0.782. 7-8 s because it is the last full second of the corpus's own 8 s leg,
# so the shortest scored leg is still measured over real samples rather than extrapolated. Nothing in
# R35 settled before 9.0 s (384 legs, median 15.5 s), which is the same fact from the other side: an
# 8 s leg cannot carry a settled value, so a window is all it can be compared on.
#
# CHANGING THESE CHANGES WHAT THE COLUMN MEANS for every capture at once, and the archived numbers do
# not move until a re-index -- so bump it only with a corpus-wide re-score, never to fit one batch.
FIXED_WINDOW_START_S, FIXED_WINDOW_END_S = 7.0, 8.0

# settleTime95's band is max(BAND_MIN_DEG, SETTLE95_FRAC * terminalOffDeg), and it must be HELD to the
# end of the segment for at least SETTLE95_HOLD_S. The hold is what makes the metric say "did not
# settle" (None) on a leg that is still decaying when time runs out, instead of the plausible wrong
# number terminalOffDeg gives there: a still-transient tail cannot sit inside 1.05x its own mean for a
# whole second. BAND_MIN_DEG (0.05) is reused as the floor -- it already sits above OFF_FLOOR_DEG, so
# "when did it settle" stays a resolvable question even where "what did it settle AT" is not.
SETTLE95_FRAC, SETTLE95_HOLD_S = 1.05, 1.0

# Tag -> metric-type mapping. The real tags are ScenarioPlayer.cs's, not free-form: the fixed-wing
# card emits arm/az10/az30/az90/az150/elUp/elDn/fine/turn360/reversal/astern/micro1..micro10; the
# rotorcraft card adds hover/hoveryaw/bobup on top (see FixedWingSegs/BuiltIns). The OLD table here
# was a list of prefixes ("az_step", "hover_hold", ...) that NO real tag ever starts with — so
# infer_type() silently returned "unknown" for 19 of 21 segments in a real capture, and every
# step-response/fine-tracking/sustained-turn metric went uncomputed with no warning at all (only
# arm/reversal happened to literally equal their own "prefix"). ORDER MATTERS: patterns are tried in
# order and the first match wins, so a more specific tag (hoveryaw) must precede a looser one it is
# a prefix of (hover) or it would always resolve to the loose one first.
TAG_TYPE_RULES = [
    (re.compile(r"arm"),        "arm"),
    (re.compile(r"reversal"),   "reversal"),
    (re.compile(r"astern"),     "astern_wrap"),
    (re.compile(r"az\d+"),      "az_step"),
    (re.compile(r"elUp"),       "el_step"),
    (re.compile(r"elDn"),       "el_step"),
    (re.compile(r"fine"),       "fine_track"),
    (re.compile(r"turn360"),    "sustained_turn"),
    (re.compile(r"micro\d+"),   "micro_step"),        # micro1 .. micro10, digit count doesn't matter
    (re.compile(r"hoveryaw"),   "hover_hold"),         # MUST precede "hover" below or it's swallowed
    (re.compile(r"hover"),      "hover_hold"),
    (re.compile(r"bobup"),      "bobup"),
    (re.compile(r"bobdn"),      "bobup"),              # rotor-hover's mirror of bobup: SAME metric (the
                                                       # vertical response is |alt - alt0|, sign-free), so
                                                       # this is a second tag on one type, not a new type.
    (re.compile(r"translate"),  "translate"),          # planned (Appendix A) -- no card emits it yet
    (re.compile(r"transition"), "transition"),         # planned (Appendix A) -- no card emits it yet
    # The two built-in tags this table did NOT cover until check-architecture.py started resolving
    # ScenarioPlayer.cs's tags against it (scorecard's own --selftest only ever scanned cards/*.json,
    # so a tag that exists solely in C# was invisible to it -- the v0.71 outage's exact shape, one
    # level up). Both are real segTags a capture can carry:
    #   `rec`  -- StopRecord's single track segment, the whole of a card RECORDED from a human flight
    #             (ScenarioPlayer.cs StopRecord). It is a continuous demand track, so it scores as
    #             fine tracking: wobble scan + pointing error, which is what a replayed human
    #             maneuver is asking about.
    #   `segN` -- Validate's fallback for a disk-card segment whose author left `tag` empty. There is
    #             genuinely nothing to teach the table here: the CARD did not say what the segment
    #             tests, so the honest score is the generic AoA/G + saturation + pointing block and
    #             no warning telling the reader to go add a rule that cannot exist.
    (re.compile(r"rec$"),       "fine_track"),
    (re.compile(r"seg\d+"),     "untagged"),           # generic metrics only -- compute_segment has no
                                                       # branch for this type, which IS the intent.
    # --- the disk cards in cards/ (installed into <game>/BepInEx/config/wtmouseaim-cards/) ---
    # Everything above is emitted by a BUILT-IN card in ScenarioPlayer.cs; everything below by a
    # shipped JSON card. Same rule either way: a tag with no entry here scores as "unknown".
    # Tags that only ADD a suffix to a built-in tag (az30R/az30L, elUp40/elDn40, turn360lowq,
    # hoveryawR/hoveryawL) need no rule -- these patterns are prefix matches. The suffixes exist
    # because compare-runs.py keys segments by TAG ALONE: a 90 m/s `az30` and a 250 m/s `az30`
    # would be pooled as replicates of each other.
    (re.compile(r"alphaHold"),          "alpha_hold"),   # alpha-ceiling: sustained turn AT the ceiling
    (re.compile(r"alpha(Pull|Push)"),   "alpha_step"),   # alpha-ceiling: mirrored +-45 deg pitch steps
    (re.compile(r"ob(UR|UL|DR|DL)\d+"), "oblique_step"), # oblique-steps: obDR2 .. obUR12
]


# --- card SETUP validation (v0.90, extended v0.91) ------------------------------------------------
# A card now carries its whole run configuration -- `repeat`, `armToggle`, a `config` list of knobs
# pinned for its duration, and since v0.91 the fleet itself (`airframe` as a comma list, `count`) --
# so that the operator ticks one checkbox and presses the spawn key, touching nothing in F1. That
# moves every way to misconfigure a batch out of the operator's hands and INTO the card file, where
# nothing at runtime rejects them: the deserializer ignores what it can't map, and ScenarioPlayer's
# apply path is fail-soft by design (one warning per bad override, then it flies anyway). A card that
# is quietly wrong still produces a capture that scores fine and answers a different question, which
# is the failure this whole release exists to remove -- so the check has to be here, offline, where
# it fails loudly before anything flies.


def split_spec(spec):
    """('Section', 'Key') for a config spec, or None if it's malformed. Mirrors
    ScenarioPlayer.SplitSpec: bare keys default to section 'Control', at most one slash, and
    neither half may be empty ('/Foo' and 'Foo/' are typos, not bare keys).

    debugtests/test-spec-grammar.py runs the shipped C# and this function over ONE shared case
    table, so keep the two in step: a copy that is stricter on something that DOES resolve would
    flag cards that fly perfectly well, and one that is looser would pass a card whose override the
    mod silently drops. (The multi-slash rule was this copy's alone until v0.96 tightened the C# to
    match -- refusing before the batch flies beats a resolve warning after it.)"""
    if not isinstance(spec, str):
        return None
    spec = spec.strip()
    if not spec:
        return None
    if "/" not in spec:
        return ("Control", spec)
    if spec.count("/") > 1:
        return None
    sec, key = (p.strip() for p in spec.split("/"))
    return (sec, key) if sec and key else None


def card_setup_problems(card):
    """List of human-readable problems with one card's run configuration; empty == fine."""
    out = []
    # `airframe` is what the drone harness SPAWNS since v0.90; before that nothing read it and all
    # sixteen shipped cards used it as prose ("any jet at the fixedwing-v2 entry condition"). An
    # Encyclopedia jsonKey never contains whitespace, so whitespace means the field is still being
    # written as documentation -- which the mod now heals at load, but silently enough that a night of
    # unattended runs would fly the wrong airframe. Human descriptions go in `note`.
    #
    # v0.91: the field is a COMMA LIST (one jsonKey per drone lane, wrapping), so the test is per
    # TOKEN and mirrors ScenarioPlayer.Validate exactly -- "Fighter1, Multirole1" is a two-airframe
    # fleet, "any jet at the fixedwing-v2 entry condition" is still prose. Mirroring matters more than
    # being strict here: a rule TIGHTER than the mod's flags cards that fly perfectly well, and a
    # false alarm in the one offline check for this is how the check stops being read.
    af = card.get("airframe", "")
    if not isinstance(af, str) or any(any(ch.isspace() for ch in tok.strip()) for tok in af.split(",")):
        out.append("airframe %r is not a comma list of spawnable jsonKeys (no whitespace inside a "
                   "key) or \"\" -- put the description in 'note'" % (af,))

    # `count` is the fleet size (v0.91), 0 = "as many as `airframe` names, else Cfg.DroneCount". Same
    # rationale as `repeat` below: the C# CLAMPS to 1..16, so a card asking for 40 flies 16 and no
    # artifact says so. A count that is not a multiple of the airframe list is legal and deliberate --
    # lanes wrap, so it just loads the early lanes -- and compare-runs.py groups by airframe anyway.
    cnt = card.get("count", 0)
    if not isinstance(cnt, int) or isinstance(cnt, bool) or not 0 <= cnt <= 16:
        out.append("count %r is not an integer in 0..16 (0 = as many as `airframe` names, "
                   "else Cfg.DroneCount)" % (cnt,))

    # `startSpeedCorner` (v0.93) is the entry speed as a multiple of the LANE AIRFRAME's own corner
    # speed, and it WINS over `startSpeed` when set. Nothing at runtime bounds it: the mod multiplies
    # whatever it finds, and the only backstop is v0.92's envelope gate, which refuses the lane --
    # so a typo'd 10.0 does not read as "10x", it reads as a whole batch of refused lanes and no
    # captures at all. 0.5..3.0 spans the roster's usable band (corner is 90..200 m/s, Vstall/corner
    # runs from 0.4 to 0.6 and 0.95*Vmax/corner reaches ~2.5 on the fastest jets), so anything
    # outside it is a mistake rather than an aggressive test point.
    ssc = card.get("startSpeedCorner", 0)
    if not isinstance(ssc, (int, float)) or isinstance(ssc, bool) or not (ssc == 0 or 0.5 <= ssc <= 3.0):
        out.append("startSpeedCorner %r is not 0 (unset -- use startSpeed) or a multiple in 0.5..3.0 "
                   "of the lane airframe's corner speed" % (ssc,))

    rep = card.get("repeat", 0)
    # 0 means "fall back to Cfg.ScenarioRepeat", so it is legal; the C# side CLAMPS to 1..20, which
    # means a card asking for 40 would silently fly 20 replicates and no artifact would say so.
    if not isinstance(rep, int) or isinstance(rep, bool) or not 0 <= rep <= 20:
        out.append("repeat %r is not an integer in 0..20 (0 = use Cfg.ScenarioRepeat)" % (rep,))

    arm = split_spec(card.get("armToggle") or "")
    for i, o in enumerate(card.get("config") or []):
        where = "config[%d]" % i
        parsed = split_spec((o or {}).get("key") or "")
        if parsed is None:
            out.append("%s key %r is not 'Key' or 'Section/Key'" % (where, (o or {}).get("key")))
            continue
        # Empty values are rejected rather than treated as "leave it alone": TomlTypeConverter would
        # throw on most types and the override would be skipped with a warning, i.e. the card would
        # silently fly with the knob unset -- indistinguishable in the capture from not asking.
        if not str((o or {}).get("value") or "").strip():
            out.append("%s ('%s/%s') has an empty value" % ((where,) + parsed))
        # THE ONE THAT MATTERS. Pinning the knob the card's own A/B schedule sweeps flies every
        # replicate on ONE arm while every capture still carries an honest-looking `arm=0`/`arm=1`
        # label -- so the A/B reads as "no difference" and nothing in the artifacts says why.
        # Compared AFTER the grammar split so 'Knob' and 'Control/Knob' are recognised as the same
        # entry; a raw string compare is exactly how this would sneak through.
        if arm is not None and parsed == arm:
            out.append("%s pins '%s/%s', which is the knob armToggle sweeps -- that collapses the "
                       "A/B onto one arm while the captures still label themselves A and B"
                       % ((where,) + parsed))
    return out


# --- CSV loading ---------------------------------------------------------------------------------

def load_csv(path):
    """(meta, rows, cols). meta has the same shape as analyze_wobble.load()'s (so fbw_params/
    fbw_corner are directly reusable) — reimplemented locally (not a call to analyze_wobble.load())
    because this meta also carries the `# card` line and this is the only place that returns `cols`,
    the header's field-name set, for "does this column exist" checks (never by index). The
    numeric-vs-string column split itself is NOT a second copy: it's aw.STRING_COLS. scorecard.py
    already imports analyze-wobble.py (as `aw`) and that module has no reason to import this one
    back, so keeping the one definition in the module that's already the import target is the
    direction that can't go circular. Two separate copies is exactly how this broke before: aw.load()
    had its own hardcoded ("phase","controlLaw") tuple missing segTag, and every row of a test-card
    capture — which is to say every row with a real segTag — silently failed to parse there."""
    meta = {"cfg": "", "headers": [], "cfg_marks": [], "session": "", "card": ""}
    data = []
    with open(path, newline="") as f:
        for raw in f:
            if raw.startswith("#"):
                s = raw.rstrip("\n")
                if s.startswith("# config"):
                    meta["cfg"] = s[8:].strip()
                elif s.startswith("# cfg t="):
                    m = re.match(r"# cfg t=([\d.]+)\s+(.*)", s)
                    if m:
                        meta["cfg_marks"].append((float(m.group(1)), m.group(2)))
                elif s.startswith("# card "):
                    # v0.71 (M1): a scripted card run names itself here. The ledger groups runs by
                    # card, so this is what makes "same card, two builds" comparable.
                    meta["card"] = s[7:].strip()
                else:
                    meta["headers"].append(s)
                    ms = re.search(r"session=(\S+)", s)
                    if ms:
                        meta["session"] = ms.group(1)
                continue
            data.append(raw)
    rdr = csv.DictReader(data)
    cols = set(rdr.fieldnames or [])
    rows = []
    dropped = 0
    for r in rdr:
        row, ok = {}, True
        for k, v in r.items():
            if v is None or v == "":
                continue
            if k in aw.STRING_COLS:
                row[k] = v
            else:
                try:
                    row[k] = float(v)
                except ValueError:
                    ok = False
                    break
        if ok:
            rows.append(row)
        else:
            dropped += 1
    if dropped:
        # Same fix, same wording as analyze_wobble.load() (see that function's docstring for the
        # v0.71 bug this class of silent drop caused) -- a couple of corrupt lines is plausible, but
        # a large fraction almost always means a column is missing from aw.STRING_COLS, not bad data.
        print(f"WARNING: {path}: dropped {dropped}/{dropped + len(rows)} row(s) that failed to parse "
              f"(non-numeric value in a column outside STRING_COLS={sorted(aw.STRING_COLS)})",
              file=sys.stderr)
    meta["dead"] = dead_columns(rows, cols)
    cols = cols - set(meta["dead"])          # -> every "is this column here" guard already says no
    return meta, rows, cols


# --- DEAD COLUMNS: a header that still prints the column after the code stopped filling it --------
# THE INVARIANT: a metric derived from a column that is not being written must come out NULL, never
# 0.0. Nothing here can tell the two apart from the CSV -- "0.0% of samples at the limit" and "the
# recorder no longer supplies that signal" print the same character -- and the second one silently
# clears a whole corpus. Three of these were live at once (R40):
#   * `dmgFrac` is ALWAYS written and ALWAYS 0: ScenarioPlayer's damage abort runs BEFORE the row is
#     written, so a damaged capture is truncated instead of carrying the flag. 641,555 indexed rows,
#     0 non-zero, against 8 known damage aborts. damage_warning() therefore certified every capture
#     in the corpus as intact, which is not a measurement, it is the column's shape.
#   * `flyLevel` / `engP` / `engR` / `engY` / `heliBlend` -- features removed or fixed-wing-only.
#   * `aoaRec`, on the batches where the recovery bias never armed.
# The rule is deliberately ZERO-variance-at-zero, not zero-variance: `assist=1`, `thr=0.7`,
# `aoaGD=1`, `bWt=1` are all constant over a whole capture and all mean something (bWt railed at 1
# for a whole capture IS the R21 finding). A constant NON-zero column is reported by --deadscan but
# not withdrawn, because there the value itself is the evidence and a 1.0 cannot be mistaken for an
# unmeasured 0.0.
# ponytail: per CAPTURE, not per corpus. A column dead in one batch and alive in the next is scored
# per capture, which is the granularity the guards already work at. A corpus-wide sweep is
# `--deadscan`, and it is a REPORT, not an input to scoring.

def dead_columns(rows, cols):
    """Sorted list of numeric columns present in the header that are 0.0 (or empty) on every row."""
    want = set(cols) - aw.STRING_COLS
    live = set()
    for r in rows:
        for k, v in r.items():
            if v:                                   # non-zero, non-empty -> the column is alive
                live.add(k)
        if want <= live:                            # all of them have spoken; nothing left to find
            break
    return sorted(want - live)


def dead_warning(meta):
    """None, or the DEAD COLUMN warning for a whole capture. Fourth member of the RAILED / SLACK-was
    / FLOOR / DAMAGED family, and the one that invalidates the others: a rail metric reading 0.0%
    off a column nobody writes is not a clean segment, it is no measurement at all."""
    d = meta.get("dead")
    if not d:
        return None
    return (f"capture has DEAD COLUMN(S): {', '.join(d)} -- present in the header, identically 0.0 "
            f"on every row. Every metric derived from them is reported as SKIPPED, not as 0.0: a "
            f"column nobody writes cannot certify that the thing it measures never happened.")


def sidecar_path(csv_path):
    return os.path.splitext(csv_path)[0] + ".airframe.json"


def provenance(path, meta):
    prov = {"file": os.path.basename(path)}
    if meta.get("session"):
        prov["session"] = meta["session"]
    if meta.get("card"):
        prov["card"] = meta["card"]
    for h in meta["headers"]:
        m = re.match(r"# mouseaim recording\s+v(\S+)\s+run=(\S+)\s+rec=(\S+)", h)
        if m:
            prov["modVersion"], prov["run"], prov["rec"] = m.groups()
        m2 = re.match(r"# aircraft '(.*)'", h)
        if m2:
            prov["aircraft"] = m2.group(1)
        # v0.72 footer: why the run ended. A capture aborted at the altitude floor or by a stick
        # touch is otherwise indistinguishable from a clean completion — it just has fewer rows —
        # so without this a batch silently averages truncated runs in with whole ones.
        m3 = re.match(r"# stop .*?reason=(.*)", h)
        if m3:
            prov["stop"] = m3.group(1).strip()
            prov["aborted"] = prov["stop"].startswith("abort:")
    if meta.get("cfg"):
        prov["config"] = meta["cfg"]
    fbw = aw.fbw_params(meta)  # reused as-is: same meta shape
    if fbw:
        prov["fbw"] = fbw
    side = sidecar_path(path)
    if os.path.isfile(side):
        try:
            with open(side, encoding="utf-8") as f:
                prov["airframeInfo"] = json.load(f)
        except (OSError, ValueError) as e:
            prov["airframeInfoError"] = str(e)
    return prov


# --- segmentation ----------------------------------------------------------------------------

def group_segments(rows, cols):
    """[(tag, rows)] — consecutive rows sharing segTag form one segment (mirrors analyze_wobble's
    own segment()-by-phase, reimplemented here because that one is hardcoded to the "phase" key)."""
    if "segTag" not in cols:
        return [("unsegmented", rows)]
    segs = []
    for r in rows:
        tag = r.get("segTag") or "unsegmented"
        if not segs or segs[-1][0] != tag:
            segs.append((tag, [r]))
        else:
            segs[-1][1].append(r)
    return segs


def infer_type(tag):
    for pattern, t in TAG_TYPE_RULES:
        if pattern.match(tag):
            return t
    return "unknown"


def _tag_warning(tag, seg_type):
    """None, or a WARNING string, for one segment's tag -> type resolution. "unsegmented" is the
    sentinel group_segments() uses when there is no segTag column at all (a legacy hand-flown
    capture) — that is normal, not a defect, and must never warn. Anything else that resolves to
    "unknown" is either a typo'd tag or a new card segment nobody taught TAG_TYPE_RULES about, and
    both of those need to be loud: the actual defect here was never the mismatch itself, it was that
    a mismatch produced confident-looking output ("unknown  n=240  dur=14.9s") with almost every
    metric silently missing and nothing telling you to distrust it."""
    if seg_type != "unknown" or tag == "unsegmented":
        return None
    return (f"segment tag '{tag}' does not match any known type in TAG_TYPE_RULES — only the "
            f"generic AoA/G metrics were computed for it (no step-response/fine-tracking/etc). "
            f"Add it to TAG_TYPE_RULES if this is a real card segment.")


# --- generic metric building blocks -----------------------------------------------------------

def rms(vals):
    return math.sqrt(sum(v * v for v in vals) / len(vals)) if vals else None


def signed_overshoot(vals):
    """Real overshoot off a SIGNED error signal (azErr/elevErr): find the first sign change, then
    return the worst |error| from there on — i.e. only error on the FAR side of the target counts.
    None when the signal never crosses (never reached the target, so nothing was overshot); 0.0 only
    if the post-crossing tail is empty/flat-zero. The reference sign is the first NON-ZERO sample,
    not literally the first one, because the columns are quantized to 0.01 deg and a segment can
    legitimately open at exactly 0.00.
    # ponytail: sample-wise, no interpolation of the crossing instant or of the peak. Ceiling is the
    # ~16 Hz sample rate + 0.01 deg quantization (~+-0.03 deg on a fast swing); parabolic-fit the
    # peak only if a change ever has to be resolved below that."""
    sgn = lambda v: (v > 0) - (v < 0)
    s0 = next((sgn(v) for v in vals if v), None)
    if s0 is None:
        return None                                  # dead-flat signal: no crossing at all
    i = next((k for k, v in enumerate(vals) if v and sgn(v) != s0), None)
    if i is None:
        return None                                  # never crossed to the far side
    tail = vals[i:]
    return max(abs(v) for v in tail) if tail else 0.0


def settle_time_95(t, offs, terminal):
    """Seconds from segment start to the LAST time `off` left the settle band -- i.e. the first
    instant after which it stays inside for the whole remainder of the segment. None = did not
    settle (see SETTLE95_HOLD_S); 0.0 = never left the band at all.

    This is the quantity terminalOffDeg was being used as a proxy for, and the difference is that it
    can say "not measurable here". A leg that is still decaying when the segment ends has no settled
    value to be within 5% of, and this returns None rather than the last sample.
    # ponytail: band from the terminal MEAN, not from a fitted steady state. Ceiling is a leg that
    # settles onto a slow drift -- the band then tracks the drift's mean and the metric reads late.
    # Fit a plateau only if that shows up in a capture."""
    n = len(t)
    if terminal is None or n < 2:
        return None
    band = max(BAND_MIN_DEG, SETTLE95_FRAC * terminal) + 1e-9   # same boundary epsilon as elsewhere
    i = n - 1
    while i >= 0 and offs[i] <= band:
        i -= 1
    j = i + 1                                  # first sample of the settled tail (0 = never left)
    if j >= n or (t[-1] - t[j]) < SETTLE95_HOLD_S - 1e-9:
        return None                            # the tail is too short to call it settled
    return t[j] - t[0]


def pointing_metrics(t, rows, cols):
    """Pointing-error metrics computed for EVERY segment type (not just fine_track). Same shape as
    aoa_g_metrics: (metrics, skipped).
      rmsPointingErrorDeg - RMS of `off` (unchanged definition; was fine_track-only, which hid a
                            steady ~9.4 deg azimuth lag through the whole 30 s turn360)
      minOffDeg           - best approach anywhere in the segment ("got there then drifted" vs "never got there")
      terminalOffDeg      - mean `off` over the last TERMINAL_WINDOW_S ("how badly it missed when time
                            ran out"). KEPT AS IS, and UNRELIABLE in two ways the two metrics below
                            exist to cover -- do not reach for it first:
                            (a) it is anchored at the segment END, so an 8 s leg and a 30 s leg are
                                not the same measurement (use fixedWindowOffDeg to compare them);
                            (b) under OFF_FLOOR_DEG it is float grain, not a score (read offFloorPct
                                beside it; floor_warning says so out loud).
                            Not removed and not redefined: 5,692 archived segments and every existing
                            analysis are keyed to it.
      fixedWindowOffDeg   - mean `off` over the FIXED window [FIXED_WINDOW_START_S, FIXED_WINDOW_END_S]
                            measured from segment START. The comparable-across-leg-lengths one. None
                            (with a reason in `skipped`) when the segment is shorter than the window,
                            or when the mean lands under OFF_FLOOR_DEG -- a short window and a
                            resolution floor are both "not measured", never a smaller number.
      settleTime95        - see settle_time_95(). None = did not settle inside the segment, which on
                            an 8 s leg is the honest answer terminalOffDeg cannot give. NOT the same
                            question as step_response_metrics' `settleTime`, and they will disagree
                            by an order of magnitude on the same segment (R35 trainer obDR6: 1.88 vs
                            10.9 s): that one is "when did it first get inside the DEMAND-scaled band"
                            (0.5 deg on a 6 deg step -- an arrival time), this one is "when did it
                            stop moving relative to where it ended up" (a settling time). Both are
                            emitted; neither replaces the other.
      offFloorPct         - % of samples with `off` under OFF_FLOOR_DEG. THE DENOMINATOR for
                            every other number here, the way bothActivePct is for rollYawOpposedPct:
                            at 100% the segment sat on the recorder's resolution and rms/min/terminal
                            are all grain. Numeric on purpose -- filter on this, not on the warning
                            prose.
      overshootAzDeg /
      overshootElDeg      - signed_overshoot() of azErr / elevErr (None = never crossed)
      entryAzSign         - sign of azErr in the FIRST sample. Exists for `astern`: that segment
                            commands a 180 deg reversal with the tie deliberately unbroken, so the
                            wrap direction is decided by a sub-0.35 deg residual carried in from the
                            previous segment and it changes terminal error ~4x. Recording the branch
                            turns one unusable population into two scorable ones."""
    m, skipped = {}, {}
    if "off" in cols:
        offs = [r.get("off", 0.0) for r in rows]
        m["rmsPointingErrorDeg"] = rms(offs)
        m["minOffDeg"] = min(offs)
        # 1e-9: same boundary epsilon as step_response_metrics -- a sample landing exactly on the
        # window edge must not fall out of it on an IEEE754 rounding of the subtraction.
        tail = [o for ti, o in zip(t, offs) if ti >= t[-1] - TERMINAL_WINDOW_S - 1e-9]
        m["terminalOffDeg"] = statistics.fmean(tail) if tail else None
        m["offFloorPct"] = 100.0 * sum(1 for o in offs if o < OFF_FLOOR_DEG) / len(offs)
        m["settleTime95"] = settle_time_95(t, offs, m["terminalOffDeg"])
        win = [o for ti, o in zip(t, offs)
               if t[0] + FIXED_WINDOW_START_S - 1e-9 <= ti <= t[0] + FIXED_WINDOW_END_S + 1e-9]
        if t[-1] - t[0] < FIXED_WINDOW_END_S - 1e-9 or not win:
            # A SHORT WINDOW IS NOT A SMALLER NUMBER. Silently averaging whatever samples exist would
            # put a 3 s micro-step's early transient in the same column as a 30 s leg's 7-8 s slice.
            skipped["fixedWindowOffDeg"] = (
                "segment shorter than the fixed window (%.0f-%.0f s)" % (FIXED_WINDOW_START_S,
                                                                         FIXED_WINDOW_END_S))
        else:
            w = statistics.fmean(win)
            if w < OFF_FLOOR_DEG:
                skipped["fixedWindowOffDeg"] = (
                    "under the `off` column's resolution floor (%.4f deg)" % OFF_FLOOR_DEG)
            else:
                m["fixedWindowOffDeg"] = w
    else:
        skipped["rmsPointingErrorDeg"] = skipped["minOffDeg"] = skipped["terminalOffDeg"] = \
            skipped["offFloorPct"] = skipped["settleTime95"] = skipped["fixedWindowOffDeg"] = \
            "missing column: off"
    if "azErr" in cols:
        az = [r.get("azErr", 0.0) for r in rows]
        m["overshootAzDeg"] = signed_overshoot(az)
        m["entryAzSign"] = (az[0] > 0) - (az[0] < 0)
    else:
        skipped["overshootAzDeg"] = skipped["entryAzSign"] = "missing column: azErr"
    if "elevErr" in cols:
        m["overshootElDeg"] = signed_overshoot([r.get("elevErr", 0.0) for r in rows])
    else:
        skipped["overshootElDeg"] = "missing column: elevErr"
    return m, skipped


def step_response_metrics(t, err, settle_band=0.5, settle_dur=1.0, rise_frac=0.9):
    """Classic step-response timing off an unsigned ERROR/DEVIATION signal that starts near the
    step size and decays toward 0 (works directly on `off`, or on the deviation to_deviation()
    builds for a rising response). None if the segment is too short to say anything.
      demand      - step size, taken as the peak within the first ~10% of samples (absorbs a
                    1-2 sample capture lag right at the jump)
      riseTime90  - first time `err` falls to <= (1-rise_frac)*demand, i.e. 90% of the way there
      settleTime  - first time `err` enters the settle band and STAYS there continuously for
                    settle_dur seconds (None if it never does)
      overshoot   - worst re-excursion above the band AFTER the signal first touched it
                    (0.0 if it never leaves again, None if it never gets there at all)
      settleBand  - the band actually used (returned so a score is self-explaining)

    settle_band=None derives the band from the demand: 10% of the step, clamped to
    [BAND_MIN_DEG, BAND_MAX_DEG]. A FIXED band cannot serve both ends of the card — 0.5 deg is
    right for a 90 deg slew but is WIDER THAN THE WHOLE DEMAND on a 0.2-1 deg micro-step, which
    would report "settled at t=0" and silently make the micro-step segment (the high-q
    small-correction regime) unmeasurable. Steps >= 5 deg still get exactly 0.5 deg, so
    large-step scores are unchanged.
    """
    n = len(t)
    if n < 2:
        return None
    t0 = t[0]
    eps = 1e-9  # boundary epsilon: e.g. 1.0-0.9 != 0.1 exactly in IEEE754, so an exact-equality
                # sample would otherwise land on the wrong side of the threshold by one sample.
    demand = max(err[:max(1, n // 10)])
    if settle_band is None:
        settle_band = min(BAND_MAX_DEG, max(BAND_MIN_DEG, BAND_FRAC * demand))
    if demand <= eps:
        return {"demand": 0.0, "riseTime90": 0.0, "settleTime": 0.0, "overshoot": 0.0,
                "settleBand": settle_band}
    thresh = demand * (1.0 - rise_frac)
    rise_t = next((ti - t0 for ti, e in zip(t, err) if e <= thresh + eps), None)

    settle_t, first_in_band = None, None
    i = 0
    while i < n:
        if err[i] > settle_band + eps:
            i += 1
            continue
        if first_in_band is None:
            first_in_band = i
        j = i
        while j < n and err[j] <= settle_band + eps:
            j += 1
        if t[j - 1] - t[i] >= settle_dur:
            settle_t = t[i] - t0
            break
        i = j

    # The COMBINED-UNSIGNED overshoot: `err` here is `off`, total angular offset, which cannot go
    # negative — so on a large step max(tail) IS the band-entry sample and this evaluates to exactly
    # 0.0 by construction (az10/az30/az90/elUp/reversal all read 0.0 across four R19 runs). Kept
    # unchanged for continuity. The real per-axis overshoot is signed_overshoot() /
    # overshootAzDeg / overshootElDeg in pointing_metrics().
    overshoot = None
    if first_in_band is not None:
        tail = err[first_in_band + 1:]
        overshoot = max(0.0, max(tail) - settle_band) if tail else 0.0
    return {"demand": demand, "riseTime90": rise_t, "settleTime": settle_t, "overshoot": overshoot,
            "settleBand": settle_band}


def to_deviation(response, plateau_frac=0.2):
    """Turn a RISING response (distance travelled toward a commanded translate/bob-up) into the
    same decaying-error shape step_response_metrics expects: plateau = median of the tail
    plateau_frac of samples (the settled value); deviation = |plateau - response(t)|."""
    n = len(response)
    tail = response[-max(1, int(n * plateau_frac)):]
    plateau = statistics.median(tail)
    return [abs(plateau - r) for r in response], plateau


def hover_metrics(t, xs, ys, zs):
    n = len(t)
    if n < 2:
        return {}
    mx, my, mz = statistics.fmean(xs), statistics.fmean(ys), statistics.fmean(zs)
    dists = [math.sqrt((xs[i] - mx) ** 2 + (ys[i] - my) ** 2 + (zs[i] - mz) ** 2) for i in range(n)]
    dur = t[-1] - t[0]
    dx, dy, dz = xs[-1] - xs[0], ys[-1] - ys[0], zs[-1] - zs[0]
    drift = math.sqrt(dx * dx + dy * dy + dz * dz) / dur if dur > 0 else None
    return {"positionRMSM": rms(dists), "driftRateMS": drift}


def aoa_g_metrics(rows, cols):
    """AoA peak / % time on limiter / G peak / sustained-G — applies to every non-excluded segment
    regardless of type. aoaGU/aoaGD are the mod's own ceiling-gate signals (1 = fully open); "on
    limiter" = either gate pulled below ~1."""
    m, skipped = {}, {}
    if "aoa" in cols:
        m["aoaPeakDeg"] = max(abs(r.get("aoa", 0.0)) for r in rows)
    else:
        skipped["aoaPeakDeg"] = "missing column: aoa"
    if {"aoaGU", "aoaGD"} <= cols:
        n = len(rows)
        on_lim = sum(1 for r in rows if r.get("aoaGU", 1.0) < 0.999 or r.get("aoaGD", 1.0) < 0.999)
        m["aoaLimiterActivePct"] = 100.0 * on_lim / n if n else 0.0
    else:
        skipped["aoaLimiterActivePct"] = "missing column(s): aoaGU/aoaGD"
    if "g" in cols:
        gs = [r.get("g", 0.0) for r in rows]
        m["gPeak"] = max(gs)
        m["gSustained"] = statistics.median(gs)  # median: robust to brief peaks, unlike a mean
        # gJitterG — mean |dg| between consecutive SAMPLES. Not an aero quantity: the game's
        # `Aircraft.gForce` is |v - vPrev| / (fixedDeltaTime * 9.81) taken off the COCKPIT PART's
        # rigidbody (decompile :61977-61979), so under complex physics it carries whatever the
        # multi-rigidbody joint solver is doing, and the recorder samples it at ~15-20 Hz — well
        # under any structural rate — so this is an ALIASED read of high-frequency solver noise,
        # not a spectrum. It is here because that noise is the dominant term in replicate scatter
        # and it is NOT a property of the airframe or the flight condition: R33 measured it
        # changing 12x on one lane, at one instant, with entry speed / AoA / authority unmoved,
        # and per-lane replicate stdev followed it in both directions (r = 0.886 over 9 lanes,
        # log-log slope 0.82 — debugtests/R33-FINDINGS.md). Read it as "how noisy was this lane's
        # physics while this segment flew", i.e. whether the cell can support an A/B at all.
        m["gJitterG"] = (statistics.fmean([abs(gs[i] - gs[i - 1]) for i in range(1, len(gs))])
                         if len(gs) >= 2 else None)
    else:
        skipped["gPeak"] = skipped["gSustained"] = skipped["gJitterG"] = "missing column: g"
    return m, skipped


def cfg_params(meta):
    """Numeric knobs from the '# config' header line as a dict — same regex/shape as
    aw.fbw_params(), applied to the other header line. Non-numeric values (law=EvolvedLegacy) simply
    don't match and are absent."""
    return {m.group(1): float(m.group(2)) for m in re.finditer(r"(\w+)=([-\d.]+)", meta.get("cfg", ""))}


def aoa_ceiling(fbw):
    """The AoA ceiling the mod ITSELF uses, mirrored from ChaseController.Apply
    (alphaLimiter - min(4, 0.15*alphaLimiter)) — so "fraction of the ceiling" here means the same
    thing it means in the law. None on a pre-v0.55 capture (no alphaLimiter on the '# fbw' header).
    Hoisted out of alpha_metrics because saturation_metrics needs the same number on EVERY segment
    type, not just alpha_*; two copies of this formula is exactly how the two would drift apart."""
    lim = fbw.get("alphaLimiter")
    return lim - min(4.0, 0.15 * lim) if lim else None


def saturation_metrics(rows, cols, cfg, fbw):
    """Is the LAW at a limit, or the PLANT? (metrics, skipped), same shape as aoa_g_metrics.

    R21 (10 replicates of fixedwing-sweep) needed a forensic dig to establish that the bank clamp
    was active on 97% of a sustained turn while g sat at 5.4 of 9 — i.e. the law was saturated and
    the airframe was not. Every saturation question below is answerable from columns already in the
    CSV, so a run should self-report it:
      bankClampActivePct    - % samples whose bank DEMAND (`bankTR`) is at or past Cfg.MaxBankAngle,
                              i.e. the clamp is discarding turn demand. READ OFF `bankTR`. It used
                              to be read off `targetBank`, WHICH IS A DIFFERENT QUANTITY (R40).
                              ONE writer (ChaseController.cs:1455), unconditional, no branch and no
                              second code path -- what made it look like two is that ONE FORMULA HAS
                              THREE REGIMES:
                                targetBank = Clamp(Lerp(linBank, bankTR, bankBlend), +-MaxBank)
                                linBank    = deadbanded(azErr) * hdgConf * FineBankGain*(1 + BankAuthGain*assist)
                                bankBlend  = YawAssistEnabled ? yawWeak*(1-bigTurn) : 0      (:1456)
                                assist     = yawWeak*(1-bigTurn)*YawAssistStrength           (:1051)
                                azDz       = FineBankDeadzone*(1-assist)                     (:1057)
                                hdgConf    = |horizontal(t.forward)| = cos(nose pitch)        (:1007,:1059)
                              Verified by reconstruction, not read off the source: median residual
                              0.000 deg over 446k corpus rows. The hdgConf factor is the one that
                              matters offline -- it is NOT a recorded column, and dropping it costs
                              0.03-0.05 deg on a level leg but 0.26-1.67 deg on a descending one
                              (solved hdgConf 0.994-0.999 at a -2 deg flight path against 0.940 at
                              -22 deg on oblique-below/-below-c). So any offline use of targetBank
                              needs a nose-pitch estimate. It is the REMOVED Legacy law's bank target:
                              ApplyEvolvedLegacy -- the only fixed-wing law since v0.60 -- has never
                              read it, computes its own tBankE = Clamp(bankTR, +-MaxBank), and flies
                              that (:1827); the dead parameters were finally deleted from the
                              signature in v0.96. So |targetBank| == MaxBank does not mean "the
                              clamp discarded turn demand", it means "azErr exceeded
                              MaxBank/bankGain", and the error is in BOTH directions:
                                UNDER-READS on a sustained turn -- bigTurn -> 1 zeroes the blend and
                                azErr -> 0 collapses linBank, so targetBank reads ~0 while the
                                aircraft is on the wall (R39 turn360rtl: 0.0% against bankTR 30.8%
                                and mean|bank| 68.0 of 72; R28 Darkreach obUR12 12.5% against 81.0%).
                                OVER-READS on a large azimuth step flown by a yaw-weak airframe --
                                assist drives bankGain to 3.0*(1+5.0*0.7) = 13.5, so 5.4 deg of azErr
                                already saturates linBank at the clamp (R29 Darkreach obUL2: 43.0%
                                against 29.3%; a parallel STOL batch measured 70.7% against 4.6%).
                                THE OVER-READ IS ONE VARIABLE, NOT A COINCIDENCE OF THREE: bankBlend,
                                assist and azDz all key off the SAME yawWeak*(1-bigTurn), so the
                                weakness that blends bankTR in is also what inflates the gain 4.5x
                                and collapses the deadband. That is why it appears abruptly instead
                                of ramping.
                                A THIRD REGIME EXISTS AND HAS NEVER BEEN FLOWN: with YawAssistEnabled
                                off, bankBlend is identically 0 and targetBank is Clamp(linBank) --
                                a different formula, not an extreme of this one, containing zero
                                turn-demand information. Every capture in the corpus has
                                'yawAssist=1' (2366 of 2366); check that field before concluding
                                anything about a batch that looks anomalous.
                              NOT SALVAGEABLE, and knowing the formula is what settles it: in regimes
                              1 and 3 targetBank carries no clamp information at all, and inverting
                              regime 2 back to the turn demand needs hdgConf, which is not recorded.
                              bankTR IS that demand, exactly, already in the CSV. The wall comes from
                              the capture's own '# config maxBank=' (Cfg.MaxBankAngle, 72 by default)
                              -- never hardcoded.
                              tBankE would answer nearly the same question -- it matched bankTR
                              sample-for-sample on the R39 turn legs -- but it is post-slew and
                              post-settle-injection, so on a roll-in it reads the BankSlewRate
                              rather than the clamp. bankTR is the demand the clamp acts on, and
                              is the same signal bankDemandExcessDeg already measured.
      bankDemandExcessDeg   - mean |bankTR| - MaxBankAngle over those samples (how much demand the
                              clamp DISCARDED; 0.0 when it never clamps). bankTR is the pre-clamp,
                              post-achievability-cap bank demand, so this is exactly the throw-away.
      turnRateCapActivePct  - % samples where the demanded turn rate is at the v0.55 achievability
                              cap (omegaMax). Distinct from the bank clamp: this one means the LAW
                              decided the airframe can't do it, the other means a fixed 72 deg wall.
      turnRateDemandRatio   - mean(demanded omega) / mean(omegaMax). >=1 means the card is asking
                              for a turn the probed airframe cannot fly and no A/B on it can mean
                              anything; well under 1 with the clamp active means the reverse.
      blendRailPct          - % samples with the roll blend weight (bWt) railed at 1. THE THIRD
                              LIMIT, and the one that decides whether a capture is comparable to
                              another at all: blendWeight = max(bigTurn, lateralHold) with
                              lateralHold = clamp01(|azAl| / Cfg.EvolvedAlignHoldDeg), so past that
                              angle (5.0 deg by default) the weight rails to 1 and eFine — the whole
                              fine bank pipeline — is multiplied by zero. Measured 100% of the
                              settled turn360 in R21: the sustained corpus is almost entirely on the
                              LATCHED side, where roll does not participate. A segment that
                              straddles it is two regimes averaged together, so read this before
                              pooling anything. Absent pre-v0.85 (bWt is a v0.85 column).
    THE MIRROR QUESTION -- "did the LAW leave performance on the table?" -- USED TO BE ANSWERED HERE,
    by authBank / authAoa / authStick / authorityUsedFrac and the SLACK flag on top of them. All five
    were DELETED in R40 and nothing replaced them, because the quantity was not a fraction of
    authority. authorityUsedFrac equalled authBank = mean|bank| / maxBank in all 32 cells examined,
    and bank in a coordinated turn is pinned by phi = atan(omega*V/g) BEFORE any control law runs --
    so it read 0.87-0.99 on every card that demanded a fast turn and was measuring the CARD's demand,
    not the law's effort. It exceeded 1.0 (to 1.084). The largest deliberate law defect in the corpus,
    a 2.3x error, moved it 0.03-0.11: roughly 5x mis-scaled. SLACK fired 0 times in R39's 121
    sustained turns, and all 8 fires in corpus history were one card geometry at 1.3 deg/s.
    Re-gating it or taking a peak instead of a mean does not fix it -- THE DENOMINATOR IS WRONG, not
    the window -- so a rescaled version of the same quantity is not wanted either. What a real
    replacement needs is an achieved-vs-achievable pair on one axis (gSustained/gLimit, or achieved
    turn rate over the probed omegaMax); turnRateDemandRatio above is the DEMAND side of exactly that
    and is still published. Do not reintroduce a mean-over-a-limit as an effort metric.

    AoA-gate occupancy is deliberately NOT re-added here — aoa_g_metrics already reports it as
    aoaLimiterActivePct on the same segments.

    omegaMax mirrors ChaseController.Apply's fixed-wing branch (gLimit*9.81/max(V, 0.75*corner),
    q-scaled below corner, times max(0.3, aoaGU), with the raw-law maxPitchAngularVel branch when
    assist is off and q is low). The demanded omega is recovered EXACTLY from bankTR by inverting
    its own definition, bankTR = atan(omega*V/g) -- no second copy of the demand chain to drift.
    # ponytail: BankSpeedFloor (50 m/s) is not on the config line; V is used unfloored. Only matters
    # below 50 m/s, i.e. a hover/taxi segment, where the turn-rate question is meaningless anyway.
    """
    m, skipped = {}, {}
    n = len(rows)
    max_bank = cfg.get("maxBank")
    if max_bank and "bankTR" in cols:
        clamped = sum(1 for r in rows if abs(r.get("bankTR", 0.0)) >= max_bank - 0.01)
        over = [abs(r.get("bankTR", 0.0)) - max_bank for r in rows if abs(r.get("bankTR", 0.0)) > max_bank]
        m["bankClampActivePct"] = 100.0 * clamped / n if n else 0.0
        m["bankDemandExcessDeg"] = statistics.fmean(over) if over else 0.0
    else:
        why = "missing column: bankTR" if max_bank else "no maxBank= on the '# config' header"
        skipped["bankClampActivePct"] = skipped["bankDemandExcessDeg"] = why

    corner, glim = fbw.get("cornerSpeed"), fbw.get("gLimit")
    if corner and glim and {"spd", "bankTR"} <= cols:
        des, omax = [], []
        for r in rows:
            v = max(1.0, r.get("spd", 0.0))
            rho = r.get("airDensity", 1.225) or 1.225
            q = v * v * rho / (corner * corner * 1.225)
            w = glim * G0 / max(v, 0.75 * corner)
            if q < 1.0:
                w *= min(1.0, max(0.3, q))
            if not (r.get("assist", 1.0) > 0.5 or q > 1.2):
                w = fbw.get("maxPitchAngVel", w)          # raw law: flat rate cap, no g protection
            omax.append(w * max(0.3, r.get("aoaGU", 1.0)))
            des.append(abs(math.tan(math.radians(r.get("bankTR", 0.0)))) * G0 / v)
        capped = sum(1 for d, w in zip(des, omax) if w > 0 and d >= 0.995 * w)
        mo = statistics.fmean(omax)
        m["turnRateCapActivePct"] = 100.0 * capped / n if n else 0.0
        m["turnRateDemandRatio"] = statistics.fmean(des) / mo if mo else None
    else:
        why = ("missing column(s): spd/bankTR" if corner and glim
               else "no cornerSpeed/gLimit on the '# fbw' header (pre-v0.55 capture)")
        skipped["turnRateCapActivePct"] = skipped["turnRateDemandRatio"] = why

    if "bWt" in cols:
        m["blendRailPct"] = 100.0 * sum(1 for r in rows if r.get("bWt", 0.0) >= BLEND_RAILED) / n if n else 0.0
    else:
        skipped["blendRailPct"] = "missing column: bWt (pre-v0.85 capture)"
    return m, skipped


# --- railed-segment flag ------------------------------------------------------------------------
# Occupancy at/above this = the segment spent essentially all of itself on that limit.
RAILED_PCT = 90.0

# The four occupancy metrics that mean "a limit, not the law, is doing the flying" — three from
# saturation_metrics, one from alpha_metrics. They are ALTERNATIVES, not additives: any ONE of them
# near 100% is already enough to make the segment's other numbers unresponsive.
RAIL_METRICS = ("bankClampActivePct", "turnRateCapActivePct", "blendRailPct", "aoaAboveCeilingPct")

# THE MIRROR FLAG, SLACK, IS GONE (R40). It thresholded authorityUsedFrac, which was not a fraction
# of authority -- see saturation_metrics' docstring for the measurement that killed it. Nothing
# replaced it: an un-calibrated flag on a mis-scaled quantity is worse than the silence it filled,
# because it reads like a verdict. If the question comes back, it needs an achieved-vs-achievable
# pair, not a mean over a limit.


def railed_metrics(seg):
    """The RAIL_METRICS this segment is pinned on, as a list of "name=value%" strings (empty = clean).

    Split out of rail_warning() so a caller can ask the QUESTION without parsing the ANSWER.
    index-captures.py wants a per-segment boolean for a database column and was reduced to sniffing
    " is RAILED:" out of the warning prose -- which is a reword away from silently marking a whole
    corpus un-railed, and "railed" is precisely the flag that decides whether a number is a score or
    no signal at all. One definition, two readers: the warning string is built from this, so they
    cannot disagree.
    """
    mv = lambda k: (seg["metrics"].get(k) or {}).get("value")
    return [f"{k}={seg['metrics'][k]['value']:.1f}%" for k in RAIL_METRICS
            if mv(k) is not None and seg["metrics"][k]["value"] >= RAILED_PCT]


def is_railed(seg):
    """True if this segment sat on a limit for >= RAILED_PCT of its samples -- i.e. its metrics are
    NO SIGNAL, not a score. The predicate form of railed_metrics(); use this, not a string match."""
    return bool(railed_metrics(seg))


def rail_warning(seg):
    """None, or the RAILED warning for one scored segment (compute_segment()'s dict).

    All four numbers below have been computed for a while and NOTHING thresholded them, so a segment
    pinned against the bank clamp for 100% of its samples printed exactly like a healthy one — same
    columns, same plausible terminalOffDeg — and a batch of them reads as "the change did nothing"
    rather than "nothing could have shown". At 100-450 captures nobody re-derives that per segment
    from a metrics dump, so the reason has to be ON THE PAGE: name the rail, quote its value, and say
    what it does to the other metrics. Surfaced through result["warnings"], which is the channel
    print_table, the roll-up, the JSON and compare-runs.py all already carry.

    Threshold is deliberately blunt (>= 90%, a literal like the rest of this file's read thresholds):
    it separates "on the stop" from "worked hard", and a capture can always be re-read with another.

    THE OTHER HALF -- "nothing railed, and the law still under-flew" -- has no flag any more; the
    SLACK case was removed in R40 with the quantity it thresholded. See RAIL_METRICS above.
    """
    hits = railed_metrics(seg)
    if not hits:
        return None
    # An alpha_* card exists to PUT the airframe past the ceiling — there, aoaAboveCeilingPct near
    # 100 is the card succeeding, and reading it as a defect would be the mirror of the mistake this
    # warning exists to prevent. Only say so when the AoA ceiling is the ONLY thing railed.
    note = ""
    if len(hits) == 1 and hits[0].startswith("aoaAboveCeilingPct") and seg.get("type") in ("alpha_step", "alpha_hold"):
        note = (" NOTE: on an alpha_* segment this is the card doing its job (the ceiling IS the "
                "stimulus) -- read the gate/recovery metrics, not the pointing ones.")
    # ASCII "--", like every other printed string in this file: these land in a Windows console.
    return (f"segment '{seg['tag']}' is RAILED: {', '.join(hits)} (>= {RAILED_PCT:.0f}% of samples). "
            f"A limit, not the control law, is flying that segment -- a gain change physically cannot "
            f"move its metrics, so read them as NO SIGNAL rather than as a score.{note}")


def floor_warning(seg):
    """None, or the RESOLUTION-FLOOR warning for one scored segment: its terminal pointing error is
    at or under what the `off` column can physically resolve (see OFF_QUANTUM_DEG).

    Third member of the same family as RAILED and SLACK, and there for the same reason: the number
    prints exactly like a score. R35's near lanes put 94 of 192 terminal windows at exactly 0.0000
    and three airframes tied there -- ranking anything on that is ranking float grain, and nothing in
    the artifacts said so. RAILED says "a limit is flying this"; SLACK says "the law is the limit";
    this one says "the INSTRUMENT is the limit". Unlike those two it is not mutually exclusive with
    either: a railed segment can also be sub-quantum, and both facts are worth printing.

    Match on the METRICS (`terminalOffDeg <= OFF_QUANTUM_DEG`, or `offFloorPct`), never on this
    prose -- same rule as the railed flag, for the same reason."""
    mv = lambda k: (seg["metrics"].get(k) or {}).get("value")
    term, pct = mv("terminalOffDeg"), mv("offFloorPct")
    if term is None or term >= OFF_FLOOR_DEG:
        return None
    frac = "" if pct is None else f", {pct:.0f}% of its samples read 0.00 or 0.02"
    # Both replacements are named unconditionally rather than branching on which one this segment
    # happens to have: norm_warning() masks NUMBERS, not words, so a branch here would split one
    # roll-up line into two that say the same thing.
    return (f"segment '{seg['tag']}' is AT THE RESOLUTION FLOOR: terminalOffDeg={term:.4f} deg is "
            f"under the {OFF_FLOOR_DEG:.4f} deg floor of the `off` column{frac}. That is float grain, "
            f"not a score -- it cannot rank anything. Use fixedWindowOffDeg / rmsPointingErrorDeg "
            f"(and settleTime95) instead.")


def damage_warning(rows, cols):
    """None, or the DAMAGED warning for a whole capture (the v0.96 `dmgFrac` column).

    Per CAPTURE, not per segment, unlike RAILED/SLACK: once a part has fallen off it stays off, so
    the fact is about the airframe for the rest of the run and would otherwise be repeated on every
    remaining segment. What matters is the worst it got and WHERE it started, since that is the
    segment whose numbers first stopped describing the airframe the other replicates flew.

    Three readings of the column and they are all different:
      absent  -- every capture written before v0.96. Not a warning: silence about damage is not a
                 claim of damage, and warning here would fire on the entire existing corpus.
      -1      -- the recorder could not read Aircraft.partDamageTracker. Also not a warning, for the
                 same reason, and emphatically not 0 (see Recording.cs's header comment).
      > 0     -- a part detached. ANY detachment: ScenarioPlayer aborts the run on the same test, so
                 a capture reaching here at all is either an abort's last rows or a hand-flown one.
    """
    if "dmgFrac" not in cols:
        return None
    worst, first_tag, first_t = 0.0, None, None
    for r in rows:
        v = r.get("dmgFrac")
        if v is None or v <= 0.0:
            continue
        if first_tag is None:
            first_tag, first_t = r.get("segTag") or "unsegmented", r.get("t")
        if v > worst:
            worst = v
    if first_tag is None:
        return None
    when = f" at t={first_t:.1f}s" if first_t is not None else ""
    return (f"capture is DAMAGED: dmgFrac reached {worst:.3f} ({100.0 * worst:.1f}% of parts detached), "
            f"first seen in segment '{first_tag}'{when}. This is not the airframe the other replicates "
            f"flew -- drop it rather than pool it.")


def norm_warning(w):
    """A warning's dedup key: every number replaced by '#'. Ten replicates of one card produce ten
    warnings that differ only in "blendRailPct=100.0%" vs "=97.3%", so a plain set() dedup would list
    all ten. compare-runs.py's group roll-up and this module's own roll-up both key on this."""
    return re.sub(r"[-\d.]+", "#", w)


# alpha_metrics / allocation_metrics thresholds. Deliberately literals, not knobs: they are read
# thresholds on already-recorded signals, so a capture can always be re-scored with a different one.
# ponytail: sample COUNTING against a fixed 0.5 gate threshold, not an integral of the gate deficit.
# Ceiling: a segment that hovers either side of 0.5 reads bimodally between runs. Upgrade to
# mean(1 - aoaGU) over the segment if an A/B ever lands inside that noise.
GATE_BITING = 0.5      # aoaGU/aoaGD below this = the ceiling gate is at least half shut
CMD_DEADBAND = 0.05    # |tgtPRaw| below this is noise around zero, not a command (the 0.05
                       # analyze-wobble's crossings() and wobble_scan use). Fine for the alpha
                       # segments, where the pitch command is near the rail.
ALLOC_DEADBAND = fs.STICK_DEADBAND  # ...but NOT fine for roll/yaw allocation on small steps:
                       # measured median |outR| is 0.006-0.017 on micro segments, so a 0.05 gate
                       # reports "no cross-fighting" for a segment that never cleared the gate.
                       # IMPORTED, not respelled as 0.02: it is the same threshold flightscore's
                       # opposed() applies below, and two spellings of one number are two tools
                       # answering differently after the next tweak. allocation_metrics reports the
                       # occupancy WITH it.
BLEND_RAILED = 0.999   # bWt at/above this = lateralHold railed, eFine weight 0 (see saturation)


def alpha_metrics(rows, cols, fbw):
    """Did the card reach the AoA ceiling, and what did the law do there? (metrics, skipped).

    This block was written on the premise that `aoaLimiterActivePct` was 0 in EVERY segment of every
    card ever run (INSTRUCTOR-LOOP.md §3). THAT PREMISE IS FALSE and was corrected 2026-07-31: the
    metric is non-zero on 66 (run, airframe, tag) cells, 23 of them with no railed segment anywhere,
    topped by R33 `Darkreach.obDR6` at 100.0% on 4 unrailed replicates (aoaPeakDeg 7.4-7.6 vs a 10
    limiter, at a 95 m/s entry). See LAW-CHARACTERIZATION.md 1.

    THE DESIGN SURVIVES THE CORRECTION, and deliberately was not changed with the comment: the
    alpha_* cards still need these eight metrics, nothing here reads the false premise as an input,
    and the block is correct wherever it runs. Only the justification was wrong.

    WHAT THE CORRECTION DOES EXPOSE, for whoever touches this next: the block is called ONLY for
    alpha_step / alpha_hold (see compute_segment), and the regime is in fact being provoked on
    oblique_step. So on the one clean capture that reached the ceiling, none of these metrics exist
    — aoa_g_metrics' aoaLimiterActivePct / aoaPeakDeg are all there is. Either tag the re-fly
    alpha*, or widen the gate. Do NOT read a missing aoaAboveCeilingPct as "the ceiling was not
    crossed"; on a non-alpha segment it was never computed.

    On an alpha_* segment `aoaLimiterActivePct == 0` still means THE CARD FAILED TO PROVOKE THE
    REGIME and every number here is about some other flight — that reading is unaffected.
      aoaCeilDeg            - the ceiling the mod itself uses, mirrored from ChaseController.Apply
                              (alphaLimiter - min(4, 0.15*alphaLimiter)), so "past the ceiling" here
                              means the same thing it means in the law. Skipped pre-v0.55 (no header).
      aoaAboveCeilingPct    - % samples with |aoa| past that ceiling. THE headline: 0 = card failed.
      aoaPeakOverCeiling    - max|aoa| / ceiling. v0.57 measured the reactive gate relaying at
                              1.3-2.5x here (Trainer 20.4 deg on an 8.5 ceiling); ~1.0 = the
                              predictive gate held.
      gateMinUp / gateMinDn - the deepest either ceiling gate closed (1.0 = never bit). Both sides,
                              because alphaPull/alphaPush are a mirrored pair and a one-sided guard
                              is exactly the asymmetry that sustains a relay.
      qSchedMin             - deepest the v0.59 AoA-utilization demand schedule cut (1.0 = inert).
      aoaRecoverActivePct /
      aoaRecoverPeak        - occupancy and peak of the v0.59 recovery bias (past the ceiling, the
                              gates command nothing and this is all that flies the recovery).
      commandIntoCeilingPct - % samples where a gate was at least half shut and the RAW law
                              (tgtPRaw, pre-gate) was still commanding INTO that ceiling. The
                              question the card is for: does the law back off, or does it keep
                              asking and leave the gate to do the backing off? Sign convention:
                              nose-up = NEGATIVE pitch (see CLAUDE.md), so up-gate + tgtPRaw < 0.
    """
    m, skipped = {}, {}
    n = len(rows)
    lim = fbw.get("alphaLimiter")
    ceil = aoa_ceiling(fbw)                      # one definition, shared with saturation_metrics
    if ceil and "aoa" in cols:
        peak = max(abs(r.get("aoa", 0.0)) for r in rows)
        m["aoaCeilDeg"] = ceil
        m["aoaAboveCeilingPct"] = 100.0 * sum(1 for r in rows if abs(r.get("aoa", 0.0)) > ceil) / n if n else 0.0
        m["aoaPeakOverCeiling"] = peak / ceil if ceil > 0 else None
    else:
        why = "missing column: aoa" if lim else "no alphaLimiter on the '# fbw' header (pre-v0.55 capture)"
        skipped["aoaCeilDeg"] = skipped["aoaAboveCeilingPct"] = skipped["aoaPeakOverCeiling"] = why
    if {"aoaGU", "aoaGD"} <= cols:
        m["gateMinUp"] = min(r.get("aoaGU", 1.0) for r in rows)
        m["gateMinDn"] = min(r.get("aoaGD", 1.0) for r in rows)
    else:
        skipped["gateMinUp"] = skipped["gateMinDn"] = "missing column(s): aoaGU/aoaGD"
    if "qSched" in cols:
        m["qSchedMin"] = min(r.get("qSched", 1.0) for r in rows)
    else:
        skipped["qSchedMin"] = "missing column: qSched"
    if "aoaRec" in cols:
        recs = [abs(r.get("aoaRec", 0.0)) for r in rows]
        m["aoaRecoverActivePct"] = 100.0 * sum(1 for v in recs if v > 0.01) / n if n else 0.0
        m["aoaRecoverPeak"] = max(recs) if recs else 0.0
    else:
        skipped["aoaRecoverActivePct"] = skipped["aoaRecoverPeak"] = "missing column: aoaRec"
    if {"aoaGU", "aoaGD", "tgtPRaw"} <= cols:
        into = 0
        for r in rows:
            p = r.get("tgtPRaw", 0.0)
            if r.get("aoaGU", 1.0) < GATE_BITING and p < -CMD_DEADBAND:      # nose-up into the + ceiling
                into += 1
            elif r.get("aoaGD", 1.0) < GATE_BITING and p > CMD_DEADBAND:     # nose-down into the - ceiling
                into += 1
        m["commandIntoCeilingPct"] = 100.0 * into / n if n else 0.0
    else:
        skipped["commandIntoCeilingPct"] = "missing column(s): aoaGU/aoaGD/tgtPRaw"
    return m, skipped


def allocation_metrics(rows, cols):
    """Roll-vs-yaw allocation on an oblique step — the "confused / cross-fighting" case.
    (metrics, skipped), same shape as aoa_g_metrics.

    An oblique demand is the only one where the allocation is ambiguous: a pure azimuth step is
    obviously roll-and-pull and a pure elevation step is obviously pitch, but 45 deg off-axis the
    law has to SPLIT the correction. Terminal error alone cannot see that — a segment can arrive
    dead on target having fought itself the whole way there.

    THE FLOOR IS REPORTED ALONGSIDE THE METRIC, ON PURPOSE. An opposed-command fraction needs both
    channels to be commanding at all, and measured over 25 `fixedwing-v2` captures they usually are
    not: median |outR| on the micro segments is 0.006–0.017 and on `fine` it is 0.005, against this
    0.02 deadband — so both-active occupancy is 0.0% on `fine` and 11–30% on micro, and the opposed
    fraction there is ~0 BY CONSTRUCTION. It would read the same whether allocation is perfect or
    catastrophic. (That is the same shape of blindness as scoring a sub-degree segment against a 1 deg
    on-target cone.) `bothActivePct` is the denominator that says whether `rollYawOpposedPct` means
    anything; `rollCmdMedian`/`yawCmdMedian` say why when it doesn't.
      rollCmdMedian /
      yawCmdMedian      - median |outR| / |outY|. Compare against ALLOC_DEADBAND before reading
                          anything below.
      bothActivePct     - % samples with BOTH channels past the deadband: the fraction of the
                          segment on which an allocation question was even being asked.
      rollYawOpposedPct - % of ALL samples where both are past the deadband with OPPOSITE signs.
                          Positive roll and positive yaw both move the nose right (CLAUDE.md sign
                          conventions), so opposite signs is the two channels pulling the azimuth
                          error apart: cross-fighting, stated as a number. Corpus: elDn 42.0%,
                          az10 20.4%, az30/az90 ~12%, micro*/fine/turn360 ~0.
      rollYawAllocFrac  - mean|outR| / (mean|outR| + mean|outY|). 1 = all roll, 0 = all yaw. Its
                          value matters less than its SPREAD across a mirrored pair (obDR vs obUL):
                          a law that allocates by geometry gives the same fraction to both.
      rollBlendMean     - mean bWt, the v0.85 post-suppression roll blend weight, i.e. the loop gain
                          the +0.918 azErr correlation was measured on. Absent pre-v0.85.
    Stick reversal RATE is not here: wobble_scan's stickFlipRate{P,R,Y} already reports it and is
    added to this segment type alongside these.
    """
    m, skipped = {}, {}
    n = len(rows)
    if {"outR", "outY"} <= cols:
        aR = [abs(r.get("outR", 0.0)) for r in rows]
        aY = [abs(r.get("outY", 0.0)) for r in rows]
        mr, my = statistics.fmean(aR), statistics.fmean(aY)
        m["rollCmdMedian"] = statistics.median(aR)
        m["yawCmdMedian"] = statistics.median(aY)
        both = [r for r in rows
                if abs(r.get("outR", 0.0)) > ALLOC_DEADBAND and abs(r.get("outY", 0.0)) > ALLOC_DEADBAND]
        m["bothActivePct"] = 100.0 * len(both) / n if n else 0.0
        # flightscore.opposed() is THE definition of a cross-fight (and owns the deadband above), so
        # this metric, its `rollYawAnti` and gatechatter's cannot answer differently.
        m["rollYawOpposedPct"] = 100.0 * sum(
            1 for r in rows if fs.opposed(r.get("outR", 0.0), r.get("outY", 0.0))) / n if n else 0.0
        m["rollYawAllocFrac"] = mr / (mr + my) if (mr + my) > 1e-9 else None
    else:
        for k in ("rollCmdMedian", "yawCmdMedian", "bothActivePct", "rollYawOpposedPct", "rollYawAllocFrac"):
            skipped[k] = "missing column(s): outR/outY"
    if "bWt" in cols:
        m["rollBlendMean"] = statistics.fmean(r.get("bWt", 0.0) for r in rows)
    else:
        skipped["rollBlendMean"] = "missing column: bWt (pre-v0.85 capture)"
    return m, skipped


def _cap(s):
    return s[0].upper() + s[1:]


# --- THE SETTLED-WINDOW OSCILLATION ESTIMATOR (R40) ----------------------------------------------
# WHAT WAS WRONG. wobble_scan used to hand the WHOLE segment to aw.episodes() and publish the longest
# episode's frequency. Both halves failed, and R39-C (debugtests/R39-C-settle-mode.md §3) measured
# how:
#   * IT SCORED THE ENTRY TRANSIENT. Every wobbleEpisodesAzErr episode in R35/R36/R37/R39 -- 42 of 42
#     -- began at tSeg 1.9-2.6 s. A step segment's ring-down crosses the dead-band a few times on its
#     way to zero and that is an "episode"; not one episode in four batches started after 3 s.
#   * HALF OF THEM WERE THE DETECTOR'S OWN FLOOR. At the 4-crossing minimum the reported frequency is
#     1.5/(t1-t0) whatever the signal did (see aw.episodes' min_cross). The "0.319-0.328 Hz reproduced
#     to three digits across three batches" was 3/(2 x the transient's fourth zero crossing).
#   * IT WAS AMPLITUDE-CENSORED. A real mode whose settled |azErr| never reaches the 0.5 deg dead-band
#     reads as NO MODE -- which is how "the mode is absent on obDR6" got published when the mode was
#     in fact present on every batch and both throttle arms.
# THE REPLACEMENT separates the two questions. WHERE does the transient end (per segment, from the
# signal's own envelope -- no fixed window anywhere in here), and WHAT is left after it, measured
# amplitude-independently. Validated against the one independently-confirmed mode in the corpus: the
# Darkreach azimuth mode, 32/32 legs, coherence 0.72-0.81 against 3/480 elsewhere. See selftest().

WOBBLE_BIN_S = 2.0   # envelope bin. ~34 samples at the recorder's ~17 Hz: enough for a stable rms,
                     # short against the 6-11 s decay it has to resolve.

# The peak must clear white noise at this many sigma. For an N-sample autocorrelation of white noise
# the peak height is 0 +- 1/sqrt(N), so the bar is derived from the window, not chosen.
WOBBLE_SIGMA = 3.0

# ...and the window must hold at least this many periods of whatever it reports. Two reasons, and the
# second one sets the number: the autocorrelation's first peak is not separable from the central
# lobe's tail below ~3 cycles, and the DFT cross-check below has resolution 1/T, so at f*T = 4 the
# agreement tolerance is f/4 -- any fewer cycles and "the two estimators agree" stops meaning anything.
WOBBLE_MIN_CYCLES = 4.0

# Episodes are still counted, but only inside the settled window and only past this many crossings,
# so aw.episodes' frequency floor cannot be published as a measurement. 6 crossings = 2.5 cycles.
WOBBLE_MIN_CROSS = 6


def _detrend(xs):
    """xs minus its least-squares line. Removes both the DC term and any residual ramp -- a drift
    left in place puts a large 1/T component in the DFT and a slow decay in the autocorrelation,
    which is the transient leaking back in through the estimator after being windowed out."""
    n = len(xs)
    mx = (n - 1) / 2.0
    my = statistics.fmean(xs)
    sxx = sum((i - mx) ** 2 for i in range(n))
    b = sum((i - mx) * (x - my) for i, x in enumerate(xs)) / sxx if sxx else 0.0
    return [x - (my + b * (i - mx)) for i, x in enumerate(xs)]


def settled_from(t, xs, bin_s=WOBBLE_BIN_S):
    """First index past this signal's own entry transient (0 = there wasn't one).

    NOT A CHOSEN WINDOW, which is the whole point -- a fixed "tSeg >= 10 s" is a constant tuned to
    one batch's leg length. Bin |x| into `bin_s` rms bins and start at the first bin that has come
    within one e-fold of the segment's own QUIETEST bin: the transient is a decay, so this is "the
    envelope has stopped dominating what is left", stated in the signal's own units with the
    segment's own floor as the reference. `e` is the natural unit of an exponential decay, not a
    tuning knob.

    Measured on 1120 R35/R36/R37 oblique legs: the envelope decays exponentially from ~4 s with a
    pooled time constant of 6.4 s (9-11 s fitted per leg), and this rule lands at 6-17 s on a 30 s
    leg -- moving with each leg's own decay instead of pinning every leg to the same number.
    # ponytail: |x| rms, not a Hilbert envelope. Ceiling is a signal with a large DC offset, where
    # the rms never falls; none of WOBBLE_SIGNALS has one (all are errors or stick commands about 0).
    """
    if not t:
        return 0
    t0 = t[0]
    bins = {}
    for ti, x in zip(t, xs):
        bins.setdefault(int((ti - t0) // bin_s), []).append(x)
    ks = sorted(b for b in bins if len(bins[b]) >= 8)
    if len(ks) < 4:
        return 0                                          # too short to have a resolvable transient
    r = {b: math.sqrt(sum(y * y for y in bins[b]) / len(bins[b])) for b in ks}
    floor = min(r.values())
    if floor <= 0:
        return 0
    start = next((b for b in ks if r[b] <= math.e * floor), ks[-1]) * bin_s + t0
    return next((i for i, ti in enumerate(t) if ti >= start), len(t))


def _acf_first_peak(xs):
    """(lag in samples, peak height) of the first non-central autocorrelation peak of a detrended
    signal, or (None, None). Height is the coherence: high = periodic, 0 = white.

    BIASED estimator (divide by the full sum-of-squares, not by the n-k overlap), which is the
    standard form and the one R39-C's published 0.72-0.81 was measured with -- keep it, or those
    numbers stop being comparable. Consequence worth knowing when reading the value: it tapers by
    (1 - lag/n), so a PERFECT sine peaks at ~0.75 when the lag is a quarter of the window, not at
    1.0. The unbiased form removes the taper and adds variance exactly where the peak is; the taper
    is monotone in lag and identical across a batch's equal-length legs, so it cannot reorder a
    comparison, which is all this number is used for."""
    n = len(xs)
    d = sum(x * x for x in xs)
    if d <= 0:
        return None, None
    r = [sum(xs[i] * xs[i + k] for i in range(n - k)) / d for k in range(n // 2)]
    k = 1
    while k < len(r) and r[k] > 0:            # walk off the central lobe first
        k += 1
    best, bk = None, None
    while k < len(r) - 1:
        if r[k] >= r[k - 1] and r[k] >= r[k + 1] and (best is None or r[k] > best):
            best, bk = r[k], k
        k += 1
    return bk, best


def _dft_peak_hz(xs, dt):
    """Frequency of the largest Hann-windowed DFT bin of a detrended signal (None if none resolvable).
    The INDEPENDENT half of the cross-check: it shares no arithmetic with the autocorrelation, so the
    two agreeing is evidence and the two disagreeing is the signature of a noise peak (R39-C measured
    0.003-0.005 Hz agreement on the real mode against 0.08-0.67 Hz elsewhere)."""
    n = len(xs)
    if n < 8:
        return None
    y = [x * (0.5 - 0.5 * math.cos(2 * math.pi * i / (n - 1))) for i, x in enumerate(xs)]
    best, bf = -1.0, None
    for k in range(1, n // 4):                # 1 cycle over the window .. half of Nyquist
        w = 2 * math.pi * k / n
        re = sum(y[i] * math.cos(w * i) for i in range(n))
        im = sum(y[i] * math.sin(w * i) for i in range(n))
        p = re * re + im * im
        if p > best:
            best, bf = p, k / (n * dt)
    return bf


def osc_mode(t, xs):
    """(freqHz | None, coherence | None) for one signal over its settled window.

    coherence is the autocorrelation first-peak height and is reported WHENEVER a window exists --
    including when it is near zero, because "measured, and incoherent" is a finding and a missing
    number is not. freqHz is reported only when all three hold:
      * the peak clears white noise at WOBBLE_SIGMA / sqrt(N);
      * the two independent estimators land in the SAME DFT bin (|f_acf - f_dft| <= 1/T -- one bin is
        the instrument's own resolution, so demanding better agreement than the instrument resolves
        would be asking for precision nobody has);
      * the window holds WOBBLE_MIN_CYCLES periods of it.
    Otherwise it is None -- NOT a floor value, which is exactly what the crossing detector published.
    # ponytail: dt = T/(n-1). The recorder alternates 0.050/0.067 s steps, so the grid is not uniform
    # and this is the mean. The resulting frequency error is second-order and ~50x under the 0.06 Hz
    # effects this is used to separate; resample onto a uniform grid if that ever stops being true.
    # ponytail: the ACF lag is integer samples, ~2% frequency quantization at 0.35 Hz. Parabolic
    # interpolation of the peak would remove it; not worth it against a 0.06 Hz effect.
    """
    i0 = settled_from(t, xs)
    tw, xw = t[i0:], xs[i0:]
    n = len(tw)
    if n < 32:
        return None, None
    xw = _detrend(xw)
    T = tw[-1] - tw[0]
    if T <= 0:
        return None, None
    dt = T / (n - 1)
    k, h = _acf_first_peak(xw)
    if k is None or h is None:
        return None, None
    f = 1.0 / (k * dt)
    fd = _dft_peak_hz(xw, dt)
    ok = (h >= WOBBLE_SIGMA / math.sqrt(n) and fd is not None and abs(f - fd) <= 1.0 / T
          and f * T >= WOBBLE_MIN_CYCLES)
    return (f if ok else None), h


def wobble_scan(t, rows, cols, dur):
    """Per-signal settled-window oscillation metrics, plus the per-axis stick sign-flip rate.

      stickFlipRate{P,R,Y}  - crossings/s of the raw command over the WHOLE segment (unchanged: it is
                              a rate, not an episode, and the transient's flips are part of it).
      wobbleFreqHz{Sig}     - osc_mode()'s frequency. ABSENT when the evidence does not support one.
      wobbleCoherence{Sig}  - osc_mode()'s autocorrelation peak height. THE DENOMINATOR: read the
                              frequency only with this beside it, the way rollYawOpposedPct is read
                              with bothActivePct.
      wobbleEpisodes{Sig}   - sustained dead-band episodes INSIDE the settled window, past
                              WOBBLE_MIN_CROSS crossings. Still amplitude-gated by construction (that
                              is what an episode is), so a 0 here with a coherent wobbleFreqHz beside
                              it means "a real mode, under the dead-band" -- the exact case that used
                              to read as "no mode".
    """
    m = {}
    for axis, lbl in (("outP", "P"), ("outR", "R"), ("outY", "Y")):
        if axis in cols:
            cnt = len(aw.crossings(None, [r.get(axis, 0.0) for r in rows], 0.05))
            m[f"stickFlipRate{lbl}"] = cnt / dur if dur > 0 else 0.0
    for name, dead in aw.WOBBLE_SIGNALS:  # the detector's own dead-bands, not a copy of them
        if name not in cols:
            continue
        xs = [r.get(name, 0.0) for r in rows]
        f, h = osc_mode(t, xs)
        if h is not None:
            m[f"wobbleCoherence{_cap(name)}"] = h
        if f is not None:
            m[f"wobbleFreqHz{_cap(name)}"] = f
        i0 = settled_from(t, xs)
        m[f"wobbleEpisodes{_cap(name)}"] = len(
            aw.episodes(t[i0:], xs[i0:], dead, min_cross=WOBBLE_MIN_CROSS))
    return m


# --- per-segment dispatch -----------------------------------------------------------------------

def compute_segment(tag, seg_type, rows, cols, ctx=None):
    """ctx = {"cfg": cfg_params(meta), "fbw": aw.fbw_params(meta)} — the header-derived constants the
    saturation block needs. Optional: without it those metrics land in "skipped", nothing crashes."""
    ctx = ctx or {}
    n = len(rows)
    t = [r["t"] for r in rows]
    dur = (t[-1] - t[0]) if n >= 2 else 0.0
    excluded = seg_type == "arm"
    metrics, skipped = {}, {}

    if not excluded:
        m, s = aoa_g_metrics(rows, cols)
        metrics.update(m)
        skipped.update(s)
        m, s = saturation_metrics(rows, cols, ctx.get("cfg", {}), ctx.get("fbw", {}))
        metrics.update(m)
        skipped.update(s)
        m, s = pointing_metrics(t, rows, cols)   # every segment type, not just fine_track
        metrics.update(m)
        skipped.update(s)

        if seg_type in ("az_step", "el_step", "micro_step", "oblique_step", "alpha_step"):
            if "off" in cols:
                sr = step_response_metrics(t, [r.get("off", 0.0) for r in rows], settle_band=None)
                if sr:
                    metrics["settleBandDeg"] = sr["settleBand"]
                    metrics["demandDeg"] = sr["demand"]
                    metrics["riseTime90"] = sr["riseTime90"]
                    metrics["settleTime"] = sr["settleTime"]
                    metrics["overshootDeg"] = sr["overshoot"]
                else:
                    skipped["riseTime90"] = "segment too short (<2 samples)"
            else:
                skipped["riseTime90"] = "missing column: off"

        elif seg_type == "fine_track":
            metrics.update(wobble_scan(t, rows, cols, dur))   # rmsPointingErrorDeg: pointing_metrics above

        elif seg_type in ("sustained_turn", "alpha_hold"):
            if "headingRateFilt" in cols:
                metrics["meanTurnRateDegS"] = statistics.fmean(abs(r.get("headingRateFilt", 0.0)) for r in rows)
            else:
                skipped["meanTurnRateDegS"] = "missing column: headingRateFilt"
            if "spd" in cols:
                metrics["deltaTAS"] = rows[-1].get("spd", 0.0) - rows[0].get("spd", 0.0)
            else:
                skipped["deltaTAS"] = "missing column: spd"
            if {"alt", "spd"} <= cols:
                eh0 = rows[0]["alt"] + rows[0].get("spd", 0.0) ** 2 / (2 * G0)
                eh1 = rows[-1]["alt"] + rows[-1].get("spd", 0.0) ** 2 / (2 * G0)
                metrics["deltaEnergyHeightM"] = eh1 - eh0
            else:
                skipped["deltaEnergyHeightM"] = "missing column: alt"

        elif seg_type in ("reversal", "astern_wrap"):
            if "off" in cols:
                sr = step_response_metrics(t, [r.get("off", 0.0) for r in rows], settle_band=None)
                if sr:
                    metrics["settleBandDeg"] = sr["settleBand"]
                    metrics["settleTime"] = sr["settleTime"]
                    metrics["overshootDeg"] = sr["overshoot"]
                else:
                    skipped["settleTime"] = "segment too short (<2 samples)"
            else:
                skipped["settleTime"] = "missing column: off"
            pa = aw.pitch_authority(rows)  # the relay/reversal signature (fbwTgtPR/fbwPR — base cols)
            if pa:
                metrics["pitchAuthorityMedian"], metrics["pitchAuthorityAntiPhaseFrac"], _ = pa
            metrics.update(wobble_scan(t, rows, cols, dur))

        elif seg_type == "hover_hold":
            if {"posX", "posY", "posZ"} <= cols:
                metrics.update(hover_metrics(t, [r["posX"] for r in rows],
                                              [r["posY"] for r in rows], [r["posZ"] for r in rows]))
            else:
                skipped["positionRMSM"] = skipped["driftRateMS"] = "missing column(s): posX/posY/posZ"

        elif seg_type in ("translate", "bobup"):
            if {"posX", "posY", "posZ"} <= cols:
                xs = [r["posX"] for r in rows]
                ys = [r["posY"] for r in rows]
                zs = [r["posZ"] for r in rows]
                if seg_type == "translate":
                    resp = [math.hypot(xs[i] - xs[0], zs[i] - zs[0]) for i in range(n)]
                elif "alt" in cols:
                    alt = [r["alt"] for r in rows]
                    resp = [abs(alt[i] - alt[0]) for i in range(n)]
                else:
                    resp = [abs(ys[i] - ys[0]) for i in range(n)]  # posY fallback, no alt column yet
                dev, _plateau = to_deviation(resp)
                sr = step_response_metrics(t, dev, settle_band=1.0, settle_dur=1.0)
                if sr:
                    metrics["demandM"] = sr["demand"]
                    metrics["riseTime90"] = sr["riseTime90"]
                    metrics["settleTime"] = sr["settleTime"]
                    metrics["overshootM"] = sr["overshoot"]
                else:
                    skipped["riseTime90"] = "segment too short (<2 samples)"
            else:
                skipped["riseTime90"] = "missing column(s): posX/posY/posZ"

        elif seg_type == "transition":
            if "alt" in cols:
                alt = [r["alt"] for r in rows]
                metrics["altExcursionM"] = max(alt) - min(alt)
            else:
                skipped["altExcursionM"] = "missing column: alt"
        # "unknown" (incl. "unsegmented"): AoA/G discipline above is the whole generic set.

        # Disk-card extras, ON TOP of the step/turn block the chain above already ran for them (an
        # oblique step is still a step; alphaHold is still a sustained turn). Kept out of that chain
        # so the shared metric set stays identical and only the card-specific question is added.
        if seg_type == "oblique_step":
            m, s = allocation_metrics(rows, cols)
            metrics.update(m)
            skipped.update(s)
            metrics.update(wobble_scan(t, rows, cols, dur))   # the reversal rate, per axis
        elif seg_type in ("alpha_step", "alpha_hold"):
            m, s = alpha_metrics(rows, cols, ctx.get("fbw", {}))
            metrics.update(m)
            skipped.update(s)
            metrics.update(wobble_scan(t, rows, cols, dur))   # incl. wobbleEpisodesAoa: the relay

    return {
        "tag": tag,
        "type": seg_type,
        "samples": n,
        "durationS": dur,
        "excluded": excluded,
        "metrics": {k: {"value": v, "grade": None} for k, v in metrics.items()},
        "skipped": skipped,
    }


def score_run(path):
    meta, rows, cols = load_csv(path)
    prov = provenance(path, meta)
    if not rows:
        return {"provenance": prov, "segments": [], "warnings": [], "note": "no data rows"}
    segments, warnings = [], []
    ctx = {"cfg": cfg_params(meta), "fbw": aw.fbw_params(meta)}
    for tag, seg_rows in group_segments(rows, cols):
        seg_type = infer_type(tag)
        seg = compute_segment(tag, seg_type, seg_rows, cols, ctx)
        segments.append(seg)
        for w in (_tag_warning(tag, seg_type), rail_warning(seg), floor_warning(seg)):
            if w:
                warnings.append(w)
    for w in (damage_warning(rows, cols), dead_warning(meta)):  # whole-capture: outside the loop
        if w:
            warnings.append(w)
    return {"provenance": prov, "segments": segments, "warnings": warnings}


# --- output ---------------------------------------------------------------------------------

def print_table(path, result):
    print(f"\n=== {path}")
    prov = result["provenance"]
    bits = [f"{k}={prov[k]}" for k in ("aircraft", "card", "modVersion", "session") if prov.get(k)]
    if bits:
        print("  " + "  ".join(bits))
    if prov.get("aborted"):                      # loud: this run did not finish its card
        print(f"  ABORTED: {prov['stop']} -- segments after this point are missing, not zero.")
    elif prov.get("stop"):
        print(f"  stop: {prov['stop']}")
    for w in result.get("warnings", []):
        print(f"  WARNING: {w}")
    if not result["segments"]:
        print(f"  {result.get('note', 'no segments')}")
        return
    for seg in result["segments"]:
        flag = "  [EXCLUDED]" if seg["excluded"] else ""
        print(f"  {seg['tag']:<22s} {seg['type']:<14s} n={seg['samples']:<5d} dur={seg['durationS']:6.1f}s{flag}")
        if seg["metrics"]:
            parts = []
            for name, mv in seg["metrics"].items():
                v = mv["value"]
                parts.append(f"{name}=n/a" if v is None else
                             f"{name}={v:.3g}" if isinstance(v, float) else f"{name}={v}")
            print("      " + "  ".join(parts))
        if seg["skipped"]:
            print("      skipped: " + "; ".join(f"{k} ({v})" for k, v in seg["skipped"].items()))


# Past this many CSVs the per-file table is replaced by print_rollup(). 10 is the boundary between
# "a person is reading these" and "a batch produced these": a dozen fits a screen, 300 x 15 lines
# does not, and the per-file detail is on disk either way (re-run with --verbose or fewer files).
DETAIL_FILE_LIMIT = 10


def print_rollup(n_files, n_segments, cards, warnings):
    """The >10-file substitute for N per-file tables: what the batch contained, and every distinct
    warning with the number of FILES it fired on. Warnings are deduped by norm_warning() (numbers
    masked) and shown with one real example, so "96 files have a railed turn360" is one line instead
    of 96 — which is the whole point, since the railed flag is at its most useful exactly when a
    batch is too big to read."""
    bits = ", ".join(f"{c} x{n}" for c, n in sorted(cards.items(), key=lambda kv: -kv[1]))
    print(f"scored {n_files} file(s), {n_segments} segment(s), {len(cards)} card(s)"
          + (f": {bits}" if cards else ""))
    for key in sorted(warnings, key=lambda k: -warnings[k][0]):
        cnt, example = warnings[key]
        print(f"  WARNING [{cnt}/{n_files} file(s), e.g.]: {example}")
    print(f"  per-file detail for {n_files} file(s) suppressed (over DETAIL_FILE_LIMIT="
          f"{DETAIL_FILE_LIMIT}) -- re-run with --verbose, or on a subset, to see it.")


# --- selftest ---------------------------------------------------------------------------------

def selftest():
    # step_response_metrics — monotonic decay, no overshoot: 10->0 over 1.0s then flat.
    t = [i * 0.1 for i in range(26)]
    off = [10 - i for i in range(11)] + [0.0] * 15
    sr = step_response_metrics(t, off)
    assert abs(sr["demand"] - 10.0) < 1e-9, sr
    assert abs(sr["riseTime90"] - 0.9) < 1e-6, sr   # off<=1.0 (10% of 10) first at i=9, t=0.9
    assert abs(sr["settleTime"] - 1.0) < 1e-6, sr   # off<=0.5 from i=10 (t=1.0) onward, sustained
    assert abs(sr["overshoot"] - 0.0) < 1e-9, sr

    # step_response_metrics — decays to 0, bounces to 3, then settles: known overshoot + late settle.
    off2 = [10 - i for i in range(11)] + [1, 2, 3, 2, 1, 0] + [0.0] * 24
    t2 = [i * 0.1 for i in range(len(off2))]
    sr2 = step_response_metrics(t2, off2)
    assert abs(sr2["riseTime90"] - 0.9) < 1e-6, sr2      # unaffected by the later bounce
    assert abs(sr2["settleTime"] - 1.6) < 1e-6, sr2      # first brief touch at t=1.0 doesn't hold;
                                                          # settles for good once back down at t=1.6
    assert abs(sr2["overshoot"] - 2.5) < 1e-6, sr2       # peak 3.0 after first touching the band, -0.5 band

    # Demand-scaled settle band (settle_band=None). A fixed 0.5 deg band would swallow a whole
    # micro-step and report settleTime==0; these assert the band tracks the demand instead.
    assert abs(step_response_metrics(t, off, settle_band=None)["settleBand"] - 0.5) < 1e-9   # 10deg -> capped 0.5
    micro_t = [i * 0.1 for i in range(40)]
    micro = [0.30] * 2 + [0.30 - 0.03 * i for i in range(1, 10)] + [0.03] * 29  # 0.3deg step -> 0.03 residual
    srm = step_response_metrics(micro_t, micro, settle_band=None)
    assert abs(srm["demand"] - 0.30) < 1e-9, srm
    assert abs(srm["settleBand"] - 0.05) < 1e-9, srm     # 10% of 0.3 = 0.03 -> floored at 0.05
    assert srm["settleTime"] is not None and srm["settleTime"] > 0.5, srm  # NOT the t=0 a fixed band gives
    # ...and the residual sitting just OUTSIDE its band must never register as settled.
    never = step_response_metrics(micro_t, [0.30] * 2 + [0.08] * 38, settle_band=None)
    assert never["settleTime"] is None, never

    # signed_overshoot: crosses to the far side and swings 2.0 past -> 2.0; never crosses -> None.
    assert abs(signed_overshoot([10, 5, 1, -0.5, -2.0, -1.0, 0.2]) - 2.0) < 1e-9
    assert signed_overshoot([10, 5, 1, 0.4, 0.2, 0.1]) is None          # decays, never goes past
    assert signed_overshoot([-10, -5, 1.5, 0.5]) == 1.5                 # mirror sign
    assert signed_overshoot([0.0] * 5) is None                          # dead flat
    assert signed_overshoot([0.0, 0.0, 3.0, 1.0, -0.7]) == 0.7          # leading 0.00s don't set the sign

    # pointing_metrics wiring on a synthetic az step: azimuth overshoots (crosses, swings to -1.6),
    # elevation never crosses (None), and `off` decays to 0.3 then drifts back out to 1.6 -- so
    # minOffDeg must be strictly below terminalOffDeg (the "got there then drifted off" case that
    # settleTime=NULL alone throws away).
    az = [10.0, 6.0, 2.0, 0.2, -1.6, -1.2, -0.8, -1.0, -1.2, -1.4, -1.6, -1.6]
    el = [0.5, 0.4, 0.35, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3]
    prow = [{"t": i * 0.1, "off": math.hypot(az[i], el[i]), "azErr": az[i], "elevErr": el[i]}
            for i in range(len(az))]
    pcols = {"t", "off", "azErr", "elevErr"}
    pm, pskip = pointing_metrics([r["t"] for r in prow], prow, pcols)
    # 1.1 s of samples: SHORTER than the 7-8 s fixed window, so that one metric must be absent with a
    # reason -- never a mean over whatever samples happen to exist (see FIXED_WINDOW_START_S).
    assert set(pskip) == {"fixedWindowOffDeg"}, pskip
    assert "fixedWindowOffDeg" not in pm, pm
    assert pm["overshootAzDeg"] > 1.5, pm                    # positive number, not 0-by-construction
    assert pm["overshootElDeg"] is None, pm                  # no crossing -> absent, distinguishable from 0.0
    assert pm["minOffDeg"] < pm["terminalOffDeg"], pm        # decayed then rose again
    assert abs(pm["minOffDeg"] - math.hypot(0.2, 0.3)) < 1e-9, pm
    assert abs(pm["rmsPointingErrorDeg"] - rms([r["off"] for r in prow])) < 1e-9, pm
    # terminal window = last 1.0 s; this segment is 1.1 s long, so it's samples 1..11, not all 12.
    assert abs(pm["terminalOffDeg"] - statistics.fmean([r["off"] for r in prow[1:]])) < 1e-9, pm
    # entryAzSign: both signs plus the exactly-0.00 case, and it must NOT follow the crossing.
    assert pm["entryAzSign"] == 1, pm
    neg = [dict(r, azErr=-r["azErr"]) for r in prow]
    assert pointing_metrics([r["t"] for r in neg], neg, pcols)[0]["entryAzSign"] == -1
    zero = [dict(r, azErr=0.0) for r in prow[:1]] + prow[1:]
    assert pointing_metrics([r["t"] for r in zero], zero, pcols)[0]["entryAzSign"] == 0

    # --- the 8 s / 30 s pair: settleTime95, fixedWindowOffDeg, offFloorPct ------------------------
    # WHY these exist (R35, 2026-08-01): every oblique leg in the corpus is 8 s long and
    # terminalOffDeg therefore scores a MID-TRANSIENT -- the off minimum lands after 8 s on 496 of
    # 496 legs of the 30 s twin card, off@16s < off@8s on 487 of them (median ratio 3.53x). And once
    # the residual reaches the recorder's own resolution, terminalOffDeg stops measuring the law at
    # all: 94 of 192 near-lane terminal windows read exactly 0.0000 and three airframes tied there.
    def point_of(ts, offs):
        return pointing_metrics(ts, [{"t": ti, "off": o} for ti, o in zip(ts, offs)], {"t", "off"})

    # (1) a 30 s leg that really does settle: 6 deg step, on 0.30 deg from t=5.0 onward.
    t30 = [0.5 * i for i in range(61)]
    off30 = [max(0.30, 6.0 - 0.6 * i) for i in range(61)]
    m30, s30 = point_of(t30, off30)
    assert "fixedWindowOffDeg" not in s30, s30    # (azErr/elevErr are absent by construction here)
    assert abs(m30["terminalOffDeg"] - 0.30) < 1e-9, m30
    assert abs(m30["settleTime95"] - 5.0) < 1e-9, m30          # last sample outside 1.05*0.30 is t=4.5
    assert abs(m30["fixedWindowOffDeg"] - 0.30) < 1e-9, m30    # 7-8 s from the START, not from the end
    assert m30["offFloorPct"] == 0.0, m30
    seg_of_m = lambda mm, tag="obDR6": {"tag": tag, "type": "oblique_step",
                                        "metrics": {k: {"value": v, "grade": None} for k, v in mm.items()}}
    assert floor_warning(seg_of_m(m30)) is None

    # (2) THE SAME DECAY TRUNCATED AT 8 S -- the corpus's actual leg length. Still transient, so
    # there is no settled value to be within 5% of and the honest answer is "did not settle".
    # terminalOffDeg cannot give that answer: it returns a plausible 2.35 deg either way.
    t8 = [0.5 * i for i in range(17)]
    off8 = [6.0 * math.exp(-ti / 8.0) for ti in t8]
    m8, _ = point_of(t8, off8)
    assert m8["settleTime95"] is None, m8
    assert m8["terminalOffDeg"] > 2.3, m8
    assert abs(m8["fixedWindowOffDeg"] - statistics.fmean(off8[14:])) < 1e-9, m8   # exactly 8 s: measurable
    # ...and one sample shorter than the window is NOT a short-window mean, it is nothing.
    assert "fixedWindowOffDeg" in point_of(t8[:-1], off8[:-1])[1], point_of(t8[:-1], off8[:-1])

    # (3) THE ADVERSARIAL ONE: a leg that dithers 0.00/0.02, i.e. sits on the recorder's resolution.
    # WHEN it settled is still resolvable (the band floors at BAND_MIN_DEG, above OFF_FLOOR_DEG);
    # WHAT it settled at is not, so the fixed-window value is withheld with a reason and the warning
    # fires. A number that cannot order two airframes must not be published as if it could.
    tdi = [0.0625 * i for i in range(320)]                                        # 20 s
    offdi = [max(0.0, 3.0 - 0.1 * i) if i < 32 else (0.02 if i % 2 else 0.0) for i in range(320)]
    mdi, sdi = point_of(tdi, offdi)
    assert mdi["terminalOffDeg"] < OFF_FLOOR_DEG, mdi
    assert "fixedWindowOffDeg" not in mdi and "resolution floor" in sdi["fixedWindowOffDeg"], (mdi, sdi)
    assert mdi["offFloorPct"] > 85.0, mdi                      # 0.02 counts as floor: it IS the first rung
    assert mdi["settleTime95"] is not None and mdi["settleTime95"] < 2.0, mdi
    fw = floor_warning(seg_of_m(mdi))
    assert fw is not None and "RESOLUTION FLOOR" in fw and "obDR6" in fw, fw
    assert "rmsPointingErrorDeg" in fw and "fixedWindowOffDeg" in fw, fw   # both replacements named
    # a steady 0.02 -- one rung, indistinguishable from 0.0198 or 0.0395 -- is floor too, and 0.05 is not.
    assert floor_warning(seg_of_m(point_of(t30, [0.02] * 61)[0])) is not None
    assert floor_warning(seg_of_m(point_of(t30, [0.05] * 61)[0])) is None

    # (4) settleTime95 must never fall back to "the last sample looked fine".
    assert point_of(t30, off30[:-1] + [3.0])[0]["settleTime95"] is None            # excursion at the end
    late = list(off30)
    late[-3] = 3.0                                                                 # 1.0 s before the end
    assert point_of(t30, late)[0]["settleTime95"] is None, late[-4:]               # tail too short to hold
    assert point_of(t30, [0.30] * 61)[0]["settleTime95"] == 0.0                    # never left the band
    assert settle_time_95([0.0, 0.2, 0.4], [0.3, 0.3, 0.3], 0.3) is None           # whole segment < hold
    assert settle_time_95(t30, off30, None) is None                                # no terminal value

    # ...and the whole block is now on EVERY segment type (the defect: it was fine_track-only, so a
    # steady lag through turn360 was invisible), including a missing-column segment that must skip
    # rather than crash.
    for st in ("az_step", "sustained_turn", "fine_track", "astern_wrap", "hover_hold", "unknown"):
        seg = compute_segment("t", st, prow, pcols)
        assert seg["metrics"]["rmsPointingErrorDeg"]["value"] > 0, (st, seg)
        assert seg["metrics"]["overshootElDeg"]["value"] is None, (st, seg)   # None survives the wrapper
    bare = compute_segment("t", "az_step", [{"t": 0.0}, {"t": 0.1}], {"t"})
    assert "rmsPointingErrorDeg" in bare["skipped"] and "entryAzSign" in bare["skipped"], bare

    # to_deviation + step_response_metrics reused for a RISING response (translate/bob-up shape):
    # ramps 0->50 over 5 samples then holds at the 50 plateau.
    resp = [0, 10, 20, 30, 40, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50]
    t3 = list(range(len(resp)))
    dev, plateau = to_deviation(resp)
    assert abs(plateau - 50.0) < 1e-9, plateau
    sr3 = step_response_metrics(t3, dev, settle_band=1.0, settle_dur=1.0)
    assert abs(sr3["demand"] - 50.0) < 1e-9, sr3
    assert abs(sr3["riseTime90"] - 5.0) < 1e-9, sr3
    assert abs(sr3["settleTime"] - 5.0) < 1e-9, sr3
    assert abs(sr3["overshoot"] - 0.0) < 1e-9, sr3

    # rms() — sine wave of known amplitude: RMS = A/sqrt(2), sampled over many full periods.
    A, f, dt, N = 4.0, 0.5, 1.0 / 60.0, 3600
    sine = [A * math.sin(2 * math.pi * f * i * dt) for i in range(N)]
    assert abs(rms(sine) - A / math.sqrt(2)) < 0.01, rms(sine)

    # --- the settled-window oscillation estimator (R40) ------------------------------------------
    # (1) a clean sustained 0.4 Hz mode: frequency recovered, coherence near 1, episodes counted.
    rows = [{"t": i * dt, "azErr": 5.0 * math.sin(2 * math.pi * 0.4 * i * dt),
             "outP": (0.6 if i % 2 else -0.6)} for i in range(1200)]
    tt = [r["t"] for r in rows]
    wm = wobble_scan(tt, rows, {"azErr", "outP"}, tt[-1] - tt[0])
    assert abs(wm["wobbleFreqHzAzErr"] - 0.4) < 0.02, wm
    assert wm["wobbleCoherenceAzErr"] > 0.8, wm      # < 1.0 by the biased estimator's taper
    assert wm["wobbleEpisodesAzErr"] >= 1, wm
    assert wm["stickFlipRateP"] > 0, wm                      # outP alternates every sample -> flips every row

    # (2) THE DEFECT ITSELF: a pure decaying entry transient with NO sustained mode. The old detector
    # reported an episode and a frequency here (42 of 42 corpus episodes were this shape, all
    # starting at tSeg 1.9-2.6 s); the rebuilt one must report NEITHER, and must place its window
    # past the ringing rather than at a fixed offset.
    trans = [{"t": i * dt, "azErr": 8.0 * math.exp(-i * dt / 2.0) * math.sin(2 * math.pi * 0.5 * i * dt)}
             for i in range(1200)]
    ttr = [r["t"] for r in trans]
    xtr = [r["azErr"] for r in trans]
    assert ttr[settled_from(ttr, xtr)] > 6.0, ttr[settled_from(ttr, xtr)]   # past ~3 time constants
    wt = wobble_scan(ttr, trans, {"azErr"}, ttr[-1] - ttr[0])
    assert "wobbleFreqHzAzErr" not in wt, wt                 # nothing coherent left after the decay
    assert wt["wobbleEpisodesAzErr"] == 0, wt
    # ...and the old detector DID fire on it -- otherwise this case proves nothing.
    assert len(aw.episodes(ttr, xtr, 0.5)) >= 1, "the pre-R40 detector must fire here"

    # (3) AMPLITUDE CENSORING, the "the mode is absent on obDR6" failure. Same mode at 1/50th the
    # amplitude: far under the 0.5 deg episode dead-band, so the episode count is 0 -- and the
    # frequency must still come out, because the new estimator is amplitude-independent.
    quiet = [{"t": i * dt, "azErr": 0.1 * math.sin(2 * math.pi * 0.4 * i * dt)} for i in range(1200)]
    wq = wobble_scan([r["t"] for r in quiet], quiet, {"azErr"}, quiet[-1]["t"])
    assert wq["wobbleEpisodesAzErr"] == 0, wq
    assert abs(wq["wobbleFreqHzAzErr"] - 0.4) < 0.02, wq
    assert not aw.crossings(None, [r["azErr"] for r in quiet], 0.5), "must be under the dead-band"

    # (4) NOISE IS NOT A MODE. Deterministic pseudo-random (no `random`, so the assert is stable):
    # coherence is published, a frequency is not.
    noise = [{"t": i * dt, "azErr": ((i * 1103515245 + 12345) % 2048) / 1024.0 - 1.0} for i in range(1200)]
    wn = wobble_scan([r["t"] for r in noise], noise, {"azErr"}, noise[-1]["t"])
    assert "wobbleFreqHzAzErr" not in wn, wn
    assert wn["wobbleCoherenceAzErr"] is not None, wn        # measured-and-incoherent is a finding
    # (5) the crossing floor can no longer be published: aw.episodes' own minimum is a constant
    # frequency, and wobble_scan passes 6 so a 4-crossing episode is not an episode here at all.
    short = [0.0, 1.0, -1.0, 1.0, -1.0, 1.0, 0.0]            # exactly 4 crossings, spanning 3 s
    ts = [0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
    assert len(aw.episodes(ts, short, 0.5)) == 1
    assert abs(aw.episodes(ts, short, 0.5)[0]["freq"] - 1.5 / 3.0) < 1e-9   # 1.5/(t1-t0): the floor
    assert aw.episodes(ts, short, 0.5, min_cross=WOBBLE_MIN_CROSS) == []
    # (6) a segment too short to hold WOBBLE_MIN_CYCLES gives None, not a number.
    stub = [{"t": i * dt, "azErr": math.sin(2 * math.pi * 0.4 * i * dt)} for i in range(40)]
    assert osc_mode([r["t"] for r in stub], [r["azErr"] for r in stub])[0] is None

    # hover_metrics — pure linear drift (no jitter): drift rate is exact; positionRMS is the RMS
    # deviation from the mean of a 0..10 ramp, i.e. sqrt(mean([-5,-3,-1,1,3,5]^2)) = sqrt(70/6).
    t4 = [0, 1, 2, 3, 4, 5]
    xs = [0, 2, 4, 6, 8, 10]
    ys = zs = [0] * 6
    hv = hover_metrics(t4, xs, ys, zs)
    assert abs(hv["driftRateMS"] - 2.0) < 1e-9, hv
    assert abs(hv["positionRMSM"] - math.sqrt(70.0 / 6.0)) < 1e-9, hv

    # aoa_g_metrics — exact counting/threshold check.
    rows = [{"aoa": 5.0, "g": 3.0, "aoaGU": 1.0, "aoaGD": 1.0},
            {"aoa": -12.0, "g": 8.0, "aoaGU": 0.4, "aoaGD": 1.0},
            {"aoa": 6.0, "g": 4.0, "aoaGU": 1.0, "aoaGD": 1.0},
            {"aoa": 7.0, "g": 5.0, "aoaGU": 1.0, "aoaGD": 1.0}]
    m, skipped = aoa_g_metrics(rows, {"aoa", "g", "aoaGU", "aoaGD"})
    assert abs(m["aoaPeakDeg"] - 12.0) < 1e-9, m
    assert abs(m["aoaLimiterActivePct"] - 25.0) < 1e-9, m   # 1 of 4 rows gated
    assert abs(m["gPeak"] - 8.0) < 1e-9 and abs(m["gSustained"] - 4.5) < 1e-9, m
    # gJitterG = mean |dg| over the 3 consecutive pairs: |8-3|, |4-8|, |5-4| -> (5+4+1)/3 = 10/3.
    assert abs(m["gJitterG"] - 10.0 / 3.0) < 1e-9, m
    # A perfectly smooth g at the SAME peak/median must read 0 — the point of the metric is that it
    # is orthogonal to gPeak/gSustained, which is why a lane can get noisier at an unchanged load.
    sm, _ = aoa_g_metrics([{"aoa": 1.0, "g": 4.5} for _ in range(4)], {"aoa", "g"})
    assert sm["gJitterG"] == 0.0, sm
    assert aoa_g_metrics([{"aoa": 1.0, "g": 4.5}], {"aoa", "g"})[0]["gJitterG"] is None
    assert skipped == {}, skipped
    m2, skipped2 = aoa_g_metrics(rows, {"aoa"})
    assert "aoaLimiterActivePct" in skipped2 and "gPeak" in skipped2, skipped2
    assert "gJitterG" in skipped2, skipped2

    # cfg_params: numeric knobs off the '# config' line; non-numeric values must be absent, not crash.
    cp = cfg_params({"cfg": "law=EvolvedLegacy sens=3.0 maxBank=72 leadT=0.65 mrFF=1 trGain=0.92"})
    assert cp["maxBank"] == 72.0 and cp["trGain"] == 0.92 and "law" not in cp, cp
    assert cfg_params({}) == {}

    # saturation_metrics — the R21 case, built to exact arithmetic. bankTR = atan(omega*V/g) with
    # V=266, so omega is recoverable from it: 81.75 deg -> tan=6.856 -> 0.2529 rad/s = 14.49 deg/s.
    # omegaMax = 9*9.81/max(266, 120) = 0.3319 rad/s = 19.02 deg/s -> ratio 0.762, cap NOT active
    # while the 72 deg bank clamp IS (that pairing is the whole point of this metric).
    satcfg, satfbw = {"maxBank": 72.0}, {"cornerSpeed": 160.0, "gLimit": 9.0, "maxPitchAngVel": 0.75}
    satcols = {"targetBank", "bankTR", "spd", "airDensity", "assist", "aoaGU", "bWt"}
    sat_rows = [{"targetBank": 72.0, "bankTR": 81.75, "spd": 266.0, "airDensity": 0.873,
                 "assist": 1.0, "aoaGU": 1.0, "bWt": 1.0}] * 3 + \
               [{"targetBank": 40.0, "bankTR": 40.0, "spd": 266.0, "airDensity": 0.873,
                 "assist": 1.0, "aoaGU": 1.0, "bWt": 0.4}]
    sm, sskip = saturation_metrics(sat_rows, satcols, satcfg, satfbw)
    assert sskip == {}, sskip
    assert abs(sm["bankClampActivePct"] - 75.0) < 1e-9, sm            # 3 of 4 at the wall
    # R40: it reads bankTR, and it must READ NOTHING FROM targetBank -- which is the removed Legacy
    # law's azErr-proportional bank command and errs in BOTH directions (see the docstring). Both
    # directions are asserted, because a fix aimed at only one of them would pass a one-sided test.
    #   UNDER-READ: on the wall, targetBank flat 0 (a settled turn: bigTurn 1, azErr ~0).
    under = [dict(r, targetBank=0.0) for r in sat_rows]
    assert abs(saturation_metrics(under, satcols, satcfg, satfbw)[0]["bankClampActivePct"] - 75.0) < 1e-9
    #   OVER-READ: targetBank pinned at the wall on every row while bankTR is nowhere near it
    #   (a big azimuth step on a yaw-weak airframe: bankGain up to 13.5, so 5.4 deg of azErr saturates).
    over = [dict(r, targetBank=72.0, bankTR=20.0) for r in sat_rows]
    assert saturation_metrics(over, satcols, satcfg, satfbw)[0]["bankClampActivePct"] == 0.0
    #   ...and the column's absence changes nothing at all.
    no_tb = saturation_metrics(sat_rows, satcols - {"targetBank"}, satcfg, satfbw)
    assert abs(no_tb[0]["bankClampActivePct"] - 75.0) < 1e-9 and no_tb[1] == {}, no_tb
    # ...and with bankTR itself gone it is SKIPPED, never 0.0.
    assert "bankClampActivePct" in saturation_metrics(sat_rows, satcols - {"bankTR"}, satcfg, satfbw)[1]
    # the lateralHold latch, the limit that decides comparability: 3 of 4 samples with eFine
    # multiplied by zero. 0.999 is a rail test, so 0.4 must NOT count and 1.0 must.
    assert abs(sm["blendRailPct"] - 75.0) < 1e-9, sm
    assert "blendRailPct" in saturation_metrics(sat_rows, satcols - {"bWt"}, satcfg, satfbw)[1]
    assert abs(sm["bankDemandExcessDeg"] - 9.75) < 1e-9, sm           # 81.75-72, only over clamped rows
    assert abs(sm["turnRateCapActivePct"] - 0.0) < 1e-9, sm           # nowhere near omegaMax
    want = statistics.fmean([math.tan(math.radians(b)) * G0 / 266.0 for b in (81.75, 81.75, 81.75, 40.0)]) \
        / (9.0 * G0 / 266.0)
    assert abs(sm["turnRateDemandRatio"] - want) < 1e-9, (sm, want)
    # the clamped rows alone: 14.49 / 19.02 = 0.762 -- the R21 pairing, "law saturated, plant is not".
    assert abs(saturation_metrics(sat_rows[:3], satcols, satcfg, satfbw)[0]["turnRateDemandRatio"]
               - 0.762) < 0.005, saturation_metrics(sat_rows[:3], satcols, satcfg, satfbw)

    # ...and the mirror case: demand AT the achievability cap reads 100% (this one is the "the card
    # is asking for something the airframe can't do" alarm, and it is a DIFFERENT limit from the wall).
    at_cap = math.degrees(math.atan((9.0 * G0 / 266.0) * 266.0 / G0))  # bankTR for omega == omegaMax
    capped, cskip = saturation_metrics(
        [{"targetBank": 72.0, "bankTR": at_cap, "spd": 266.0, "airDensity": 0.873, "assist": 1.0,
          "aoaGU": 1.0}], satcols, satcfg, satfbw)
    assert cskip == {}, cskip
    assert abs(capped["turnRateCapActivePct"] - 100.0) < 1e-9, capped
    assert abs(capped["turnRateDemandRatio"] - 1.0) < 1e-9, capped
    # never clamped must read 0.0 excess, not None -- "never clamped" has to be distinguishable from
    # "column missing", which is what the skipped/metrics split is for.
    unclamped, uskip = saturation_metrics(sat_rows[3:], satcols, satcfg, satfbw)
    assert uskip == {} and unclamped["bankClampActivePct"] == 0.0, (unclamped, uskip)
    assert abs(unclamped["bankDemandExcessDeg"] - 0.0) < 1e-9, unclamped
    # aoaGU shrinks omegaMax, so the SAME demand is then over the cap (the v0.67 AoA-margin cap).
    low_aoa = [{"targetBank": 60.0, "bankTR": at_cap, "spd": 266.0, "airDensity": 0.873,
                "assist": 1.0, "aoaGU": 0.5}]
    assert saturation_metrics(low_aoa, satcols, satcfg, satfbw)[0]["turnRateCapActivePct"] == 100.0
    assert saturation_metrics(low_aoa, satcols, satcfg, satfbw)[0]["turnRateDemandRatio"] > 1.9

    # THE AUTHORITY BLOCK IS GONE (R40) and must stay gone: authorityUsedFrac was mean|bank|/maxBank
    # wearing a general name, and bank in a coordinated turn is pinned by atan(omega*V/g) before the
    # law runs. Asserting its ABSENCE is the only thing that stops it being reintroduced by a
    # well-meaning "the mirror question has no metric" patch -- see saturation_metrics' docstring.
    authcfg = {"maxBank": 72.0}
    authfbw = {"cornerSpeed": 160.0, "gLimit": 9.0, "alphaLimiter": 20.0}
    authcols = {"bank", "aoa", "outP", "outR", "outY", "spd", "bankTR", "targetBank"}
    auth_rows = [{"bank": 36.0, "aoa": 8.5, "outP": -0.4, "outR": 0.1, "outY": 0.0,
                  "spd": 266.0, "bankTR": 10.0, "targetBank": 10.0},
                 {"bank": -36.0, "aoa": -8.5, "outP": 0.4, "outR": -0.1, "outY": 0.0,
                  "spd": 266.0, "bankTR": 10.0, "targetBank": 10.0}]
    aum, auskip = saturation_metrics(auth_rows, authcols, authcfg, authfbw)
    gone = {"authorityUsedFrac", "authBank", "authAoa", "authStick"}
    assert not (gone & set(aum)) and not (gone & set(auskip)), (aum, auskip)
    assert not ({"SLACK_FRAC", "SLACK_TYPES", "AUTH_TERMS", "AUTH_MIN_TERMS"} & set(globals()))
    # ...and the demand side of the question, which was measured against a real denominator, stays:
    # at bankTR 81.75 the demand is 0.762 of the probed omegaMax.
    hot, _ = saturation_metrics([dict(r, bankTR=81.75) for r in auth_rows], authcols, authcfg, authfbw)
    assert abs(hot["turnRateDemandRatio"] - 0.762) < 0.005, hot

    import tempfile, io, contextlib
    # rail_warning — the flag on top of those same numbers. A segment at the wall must SAY so, and
    # must name WHICH wall with its value; a hard-working but unsaturated one must stay silent (a
    # warning that fires on everything is the same as no warning).
    railed_seg = compute_segment("turn360", "sustained_turn",
                                 [dict(r, t=i * 0.1) for i, r in enumerate(sat_rows[:3])],
                                 satcols | {"t"}, {"cfg": satcfg, "fbw": satfbw})
    rw = rail_warning(railed_seg)
    assert rw is not None and "RAILED" in rw and "turn360" in rw, rw
    assert "bankClampActivePct=100.0%" in rw and "blendRailPct=100.0%" in rw, rw   # both rails named
    assert "turnRateCapActivePct" not in rw, rw          # 76% of the cap: NOT railed, must not appear
    assert rail_warning(compute_segment("turn360", "sustained_turn",
                                        [dict(r, t=i * 0.1) for i, r in enumerate(sat_rows[3:])],
                                        satcols | {"t"}, {"cfg": satcfg, "fbw": satfbw})) is None
    # exactly at the threshold counts (>=), just under does not.
    edge = lambda pct: {"tag": "x", "type": "az_step",
                        "metrics": {"blendRailPct": {"value": pct, "grade": None}}}
    assert rail_warning(edge(RAILED_PCT)) is not None and rail_warning(edge(RAILED_PCT - 0.1)) is None
    # ...and a None-valued metric is "not measured", never a rail.
    assert rail_warning({"tag": "x", "type": "az_step",
                         "metrics": {"blendRailPct": {"value": None, "grade": None}}}) is None
    # the alpha_* exemption note: an alpha card PUTS the airframe past the ceiling, so that alone is
    # the stimulus working. Still warned (the pointing metrics really are unresponsive), but said so.
    alpha_railed = {"tag": "alphaHold", "type": "alpha_hold",
                    "metrics": {"aoaAboveCeilingPct": {"value": 99.0, "grade": None}}}
    assert "doing its job" in rail_warning(alpha_railed), rail_warning(alpha_railed)
    assert "doing its job" not in rail_warning(dict(alpha_railed, type="sustained_turn"))
    assert "doing its job" not in rail_warning(          # not the ONLY rail -> no exemption
        dict(alpha_railed, metrics=dict(alpha_railed["metrics"],
                                        blendRailPct={"value": 100.0, "grade": None})))

    # An idle segment is NOT a warning any more (that was SLACK). Nothing on any stop, ~10% of every
    # axis: silence. Kept as a case because it is the one the removal changes.
    seg_of = lambda **kw: compute_segment(
        "turn360", "sustained_turn",
        [dict(r, t=i * 0.1, **kw) for i, r in enumerate(auth_rows * 8)],
        authcols | {"t"}, {"cfg": authcfg, "fbw": authfbw})
    assert rail_warning(seg_of(bank=7.2, aoa=1.7, outP=0.05, outR=0.05, outY=0.001)) is None
    # ...and an `arm` segment carries no metrics at all, so it can never be flagged.
    assert rail_warning(compute_segment("arm", "arm", [dict(r, t=0.1 * i) for i, r in enumerate(auth_rows)],
                                        authcols | {"t"}, {"cfg": authcfg, "fbw": authfbw})) is None
    # score_run's channel: the warning has to reach result["warnings"], where print_table, the
    # roll-up, the JSON and compare-runs.py all read from -- not just exist as a function.
    fd_r, rail_csv = tempfile.mkstemp(suffix=".csv")
    try:
        with os.fdopen(fd_r, "w", newline="") as f:
            f.write("# mouseaim recording v0.90.0 run=R1 rec=1 session=t\n"
                    "# card fixedwing-sweep\n# config maxBank=72.0\n"
                    "t,off,segTag,targetBank,bankTR,bWt\n"
                    + "".join(f"{i * 0.1:.1f},3.0,turn360,72.0,81.75,1.0\n" for i in range(20)))
        rr = score_run(rail_csv)
        assert any("RAILED" in w for w in rr["warnings"]), rr["warnings"]
        assert rr["provenance"]["card"] == "fixedwing-sweep", rr["provenance"]
        # norm_warning: ten replicates differ only in the quoted percentages, so the roll-up must
        # collapse them to one line -- the masked key is what makes that possible.
        a = "segment 'turn360' is RAILED: blendRailPct=100.0% (>= 90% of samples)."
        b = "segment 'turn360' is RAILED: blendRailPct=97.3% (>= 90% of samples)."
        assert norm_warning(a) == norm_warning(b), (norm_warning(a), norm_warning(b))
        assert norm_warning(a) != norm_warning(a.replace("turn360", "az30"))
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            print_rollup(11, 44, {"fixedwing-sweep": 11}, {norm_warning(a): (9, a)})
        out = buf.getvalue()
        assert "11 file(s)" in out and "fixedwing-sweep x11" in out, out
        assert "[9/11 file(s)" in out and "RAILED" in out and "--verbose" in out, out
        # The DEAD COLUMN warning rides the SAME channel and must dedup the same way.
        c = "capture has DEAD COLUMN(S): dmgFrac, engP -- present in the header, identically 0.0."
        assert norm_warning(c) != norm_warning(a)                 # never collapses into RAILED
    finally:
        os.remove(rail_csv)

    # --- DEAD COLUMNS, end to end (R40) ----------------------------------------------------------
    # THE INVARIANT: a column present in the header and never written must SKIP its metrics, never
    # score them as 0.0. Three shapes in one file: a live column, one that is identically 0 for the
    # whole capture (`dmgFrac` -- structurally so, the damage abort truncates before the row is
    # written, 0 non-zero in 641,555 indexed rows), and one that is constant NON-zero (`bWt` railed
    # at 1, which is a real finding and must survive).
    fd_dc, dc_csv = tempfile.mkstemp(suffix=".csv")
    try:
        with os.fdopen(fd_dc, "w", newline="") as f:
            f.write("# mouseaim recording v0.96.0 run=R1 rec=9 session=t\n"
                    "# card fixedwing-sweep\n# config maxBank=72.0\n"
                    "t,off,segTag,bankTR,bWt,dmgFrac,aoaRec\n"
                    + "".join(f"{i * 0.1:.1f},3.0,turn360,81.75,1.0,0.000,0.000\n" for i in range(20)))
        meta_dc, rows_dc, cols_dc = load_csv(dc_csv)
        assert meta_dc["dead"] == ["aoaRec", "dmgFrac"], meta_dc["dead"]
        assert "dmgFrac" not in cols_dc and "bWt" in cols_dc, cols_dc   # constant non-zero survives
        dcr = score_run(dc_csv)
        assert any("DEAD COLUMN" in w and "dmgFrac" in w for w in dcr["warnings"]), dcr["warnings"]
        # ...and the consequence, which is the whole point: damage_warning cannot certify a capture
        # intact off a column nobody writes -- it is silent either way, but now it is silent because
        # the column was WITHDRAWN, and the withdrawal is on the page.
        assert not any("DAMAGED" in w for w in dcr["warnings"]), dcr["warnings"]
        assert abs(dcr["segments"][0]["metrics"]["blendRailPct"]["value"] - 100.0) < 1e-9
    finally:
        os.remove(dc_csv)
    # the predicate on its own, including the boundary cases: a live column with one non-zero row is
    # NOT dead, and a string column is never judged.
    assert dead_columns([{"a": 0.0, "b": 0.0, "segTag": "x"}] * 5, {"a", "b", "segTag"}) == ["a", "b"]
    assert dead_columns([{"a": 0.0}] * 4 + [{"a": 1e-9}], {"a"}) == []
    assert dead_columns([{"a": -0.0}] * 4, {"a"}) == ["a"]         # signed zero is still zero
    assert dead_columns([], {"a"}) == ["a"]                        # no rows: nothing was written
    assert dead_warning({"dead": []}) is None and dead_warning({}) is None

    # --- DAMAGED (v0.96 dmgFrac) ----------------------------------------------------------------
    # The four readings that must stay apart. The absent case is the one that matters most: every
    # capture on disk predates the column, so a detector that treated "no column" as anything but
    # silence would flag the entire corpus.
    dmgcols = {"t", "segTag", "dmgFrac"}
    def dmg_rows(vals):
        return [{"t": 0.1 * i, "segTag": "az30" if i < 3 else "turn360", "dmgFrac": v}
                for i, v in enumerate(vals)]
    assert damage_warning(dmg_rows([0.0] * 6), {"t", "segTag"}) is None          # column absent
    assert damage_warning(dmg_rows([0.0] * 6), dmgcols) is None                  # intact throughout
    assert damage_warning(dmg_rows([-1.0] * 6), dmgcols) is None                 # unreadable != damaged
    dw_ = damage_warning(dmg_rows([0.0, 0.0, 0.0, 0.0, 0.04, 0.08]), dmgcols)    # a part comes off mid-run
    assert dw_ is not None and "DAMAGED" in dw_ and "0.080" in dw_ and "'turn360'" in dw_, dw_
    # ...and the -1 sentinel must not become the max, nor make a clean run look damaged.
    dw2 = damage_warning(dmg_rows([-1.0, -1.0, 0.0, 0.0, 0.04, 0.04]), dmgcols)
    assert "0.040" in dw2 and "-1" not in dw2, dw2
    # Same channel as RAILED: through score_run into result["warnings"].
    fd_d, dmg_csv = tempfile.mkstemp(suffix=".csv")
    try:
        with os.fdopen(fd_d, "w", newline="") as f:
            f.write("# mouseaim recording v0.96.0 run=R1 rec=3 session=t\n"
                    "# card fixedwing-sweep\n"
                    "t,off,segTag,dmgFrac\n"
                    + "".join(f"{i * 0.1:.1f},3.0,turn360,{0.0 if i < 15 else 0.06:.3f}\n" for i in range(20)))
        dr = score_run(dmg_csv)
        assert any("DAMAGED" in w for w in dr["warnings"]), dr["warnings"]
    finally:
        os.remove(dmg_csv)

    # --- AT THE RESOLUTION FLOOR, end to end -----------------------------------------------------
    # Same channel as RAILED/SLACK/DAMAGED, and the same reason for being on it: 94 of R35's 192
    # near-lane terminal windows read 0.0000 and NOTHING in the artifacts said the number was float
    # grain. The CSV below is a 20 s leg written the way the recorder writes one -- two decimals --
    # decaying to a 0.00/0.02 dither, so it also proves the new metrics survive the CSV round trip.
    fd_f, floor_csv = tempfile.mkstemp(suffix=".csv")
    try:
        with os.fdopen(fd_f, "w", newline="") as f:
            f.write("# mouseaim recording v0.96.2 run=R35 rec=4 session=t\n"
                    "# card oblique-6-dwell\n"
                    "t,off,segTag\n"
                    + "".join("%.3f,%.2f,obDR6\n" % (0.0625 * i,
                                                     max(0.0, 3.0 - 0.1 * i) if i < 32
                                                     else (0.02 if i % 2 else 0.0))
                              for i in range(320)))
        fr = score_run(floor_csv)
        assert any("RESOLUTION FLOOR" in w for w in fr["warnings"]), fr["warnings"]
        fm = fr["segments"][0]["metrics"]
        assert fm["settleTime95"]["value"] is not None, fm     # WHEN is still measurable...
        assert "fixedWindowOffDeg" not in fm, fm               # ...WHAT it settled at is not
        assert fm["offFloorPct"]["value"] > 85.0, fm
        # norm_warning must collapse eight replicates of it, like every other warning on this channel.
        assert norm_warning(fr["warnings"][0]) == norm_warning(fr["warnings"][0].replace("0.0100", "0.0000"))
    finally:
        os.remove(floor_csv)

    # graceful degradation, both halves independently: no '# config' line, and a pre-v0.55 '# fbw'.
    _, s_nocfg = saturation_metrics(sat_rows, satcols, {}, satfbw)
    assert set(s_nocfg) == {"bankClampActivePct", "bankDemandExcessDeg"}, s_nocfg
    _, s_nofbw = saturation_metrics(sat_rows, satcols, satcfg, {})
    assert set(s_nofbw) == {"turnRateCapActivePct", "turnRateDemandRatio"}, s_nofbw
    # compute_segment: with ctx the metrics land in "metrics"; WITHOUT ctx (the old 4-arg call that
    # every existing caller makes) they land in "skipped" and nothing crashes -- backward compatible.
    sat_t = [dict(r, t=i * 0.1) for i, r in enumerate(sat_rows)]
    with_ctx = compute_segment("turn360", "sustained_turn", sat_t, satcols | {"t"},
                               {"cfg": satcfg, "fbw": satfbw})
    assert abs(with_ctx["metrics"]["bankClampActivePct"]["value"] - 75.0) < 1e-9, with_ctx
    noctx = compute_segment("turn360", "sustained_turn", sat_t, satcols | {"t"})
    assert "bankClampActivePct" in noctx["skipped"], noctx
    assert "bankClampActivePct" not in noctx["metrics"], noctx
    # arm stays excluded: no saturation metrics on the post-spawn window either.
    assert compute_segment("arm", "arm", sat_t, satcols | {"t"}, {"cfg": satcfg, "fbw": satfbw})["metrics"] == {}

    # sustained_turn arithmetic wiring: constant turn rate, known deltaTAS/deltaEh.
    rows = [{"t": 0.0, "headingRateFilt": 15.0, "spd": 100.0, "alt": 1000.0},
            {"t": 1.0, "headingRateFilt": -15.0, "spd": 90.0, "alt": 1000.0}]
    seg = compute_segment("sustained_turn_01", "sustained_turn", rows, {"headingRateFilt", "spd", "alt", "t"})
    assert abs(seg["metrics"]["meanTurnRateDegS"]["value"] - 15.0) < 1e-9, seg
    assert abs(seg["metrics"]["deltaTAS"]["value"] - (-10.0)) < 1e-9, seg
    eh0 = 1000.0 + 100.0 ** 2 / (2 * G0)
    eh1 = 1000.0 + 90.0 ** 2 / (2 * G0)
    assert abs(seg["metrics"]["deltaEnergyHeightM"]["value"] - (eh1 - eh0)) < 1e-6, seg

    # graceful degradation: sustained_turn with no alt column skips deltaEnergyHeightM, doesn't crash.
    rows_noalt = [{"t": 0.0, "headingRateFilt": 10.0, "spd": 100.0},
                  {"t": 1.0, "headingRateFilt": 10.0, "spd": 100.0}]
    seg2 = compute_segment("sustained_turn_02", "sustained_turn", rows_noalt, {"headingRateFilt", "spd", "t"})
    assert "deltaEnergyHeightM" not in seg2["metrics"], seg2
    assert "deltaEnergyHeightM" in seg2["skipped"], seg2

    # segmentation + type inference + arm exclusion, using the REAL tags ScenarioPlayer.cs emits.
    # (The old version of this test used fictional tags like "az_step_10"/"hover_hold_01" that
    # happened to satisfy the old, buggy startswith(KNOWN_PREFIXES) table -- which is exactly how
    # the mismatch between infer_type() and the real cards went unnoticed.)
    rows = ([{"t": i * 0.1, "segTag": "arm", "off": 0.0} for i in range(5)]
            + [{"t": 0.5 + i * 0.1, "segTag": "az10", "off": max(0.0, 10 - i)} for i in range(12)]
            + [{"t": 1.7 + i * 0.1, "segTag": "hover", "off": 0.0} for i in range(3)])
    cols = {"t", "segTag", "off"}
    segs = group_segments(rows, cols)
    assert [tag for tag, _ in segs] == ["arm", "az10", "hover"], segs
    types = [infer_type(tag) for tag, _ in segs]
    assert types == ["arm", "az_step", "hover_hold"], types
    arm_out = compute_segment("arm", "arm", segs[0][1], cols)
    assert arm_out["excluded"] and arm_out["metrics"] == {}, arm_out

    # infer_type: every tag the real cards (FixedWingSegs + the rotorcraft appendix in
    # ScenarioPlayer.cs) actually emit, one per metric type -- this is the defect itself: NONE of
    # these matched the old KNOWN_PREFIXES table except arm/reversal. Plus the two planned-but-not-
    # yet-emitted types and one tag that is genuinely unrecognized.
    real_tags = {
        "arm": "arm", "az10": "az_step", "az30": "az_step", "az90": "az_step", "az150": "az_step",
        "elUp": "el_step", "elDn": "el_step", "fine": "fine_track", "turn360": "sustained_turn",
        "reversal": "reversal", "astern": "astern_wrap",
        "micro1": "micro_step", "micro10": "micro_step",       # both single- and double-digit
        "hover": "hover_hold", "hoveryaw": "hover_hold",        # hoveryaw must NOT fall to "unknown"
        "bobup": "bobup",
        "translate": "translate", "transition": "transition",  # planned (Appendix A), not emitted yet
        "not_a_real_card_tag": "unknown",
        # ...and every tag the shipped DISK cards (cards/*.json) emit. The suffixed ones are the
        # point of the prefix match: they must land on the built-in's type without a rule of their
        # own, while staying a DISTINCT tag so compare-runs.py can't pool them as replicates.
        "alphaPull": "alpha_step", "alphaPush": "alpha_step", "alphaHold": "alpha_hold",
        "obDR2": "oblique_step", "obDL2": "oblique_step", "obUL2": "oblique_step",
        "obUR2": "oblique_step", "obUR12": "oblique_step",     # both single- and double-digit
        "obDR05": "oblique_step", "obUR3": "oblique_step",     # the sub-degree and deadzone rungs
        "obDL6low": "oblique_step",                            # ...and the below-horizon diamond
        "az30R": "az_step", "az30L": "az_step",
        "az6sweepR": "az_step", "az6sweepL": "az_step",       # a step flown ON a moving marker
        "elUp40": "el_step", "elDn40": "el_step",
        "turn360loq": "sustained_turn", "turn360stol": "sustained_turn",
        "turn360slow": "sustained_turn", "turn360base": "sustained_turn",
        "turn360creep": "sustained_turn",
        "hoveryawR": "hover_hold", "hoveryawL": "hover_hold",
        "bobdn": "bobup",
        "alphabet": "unknown",     # NOT alpha_step: the rules are alphaHold / alpha(Pull|Push)
        "obscure": "unknown",      # NOT oblique_step: ob must be followed by a direction + digits
        # The two C#-only tags this table missed until check-architecture.py started resolving
        # ScenarioPlayer.cs's `tag = "..."` / Hold()/Walk() literals against it. Neither can appear
        # in cards/*.json, so the loop at the bottom of this selftest could never have caught them.
        "rec": "fine_track",       # StopRecord's recorded-demand track
        "seg0": "untagged", "seg7": "untagged",   # Validate's fallback for an untagged disk segment
        "recovery": "unknown",     # the `rec` rule is anchored: it is the whole tag, not a prefix
        "segment": "unknown",      # ...and `seg` alone is not `seg<digits>`
    }
    for tag, expected in real_tags.items():
        assert infer_type(tag) == expected, (tag, infer_type(tag), expected)

    # _tag_warning: loud on a genuinely unrecognized tag, silent on a known type AND on the
    # "unsegmented" sentinel (a legacy hand-flown capture with no segTag column -- normal, not a bug).
    assert _tag_warning("az10", "az_step") is None
    assert _tag_warning("unsegmented", "unknown") is None
    w = _tag_warning("mystery_seg", "unknown")
    assert w is not None and "mystery_seg" in w, w

    # segTag absent entirely -> one "unsegmented" segment, generic (AoA/G) metric set only.
    rows_flat = [{"t": i * 0.1, "aoa": 3.0, "g": 2.0} for i in range(5)]
    segs_flat = group_segments(rows_flat, {"t", "aoa", "g"})
    assert [tag for tag, _ in segs_flat] == ["unsegmented"], segs_flat
    assert infer_type("unsegmented") == "unknown"

    # load_csv(): a genuinely corrupt row (non-numeric value in a real numeric column) is dropped
    # -- that part was already correct -- but must now be COUNTED and warned about, not silently
    # eaten (the same gap analyze_wobble.load() had and already fixed; this was scorecard.py's own
    # unfixed copy of the same bug).
    import tempfile, io, contextlib
    fd, tmp_path = tempfile.mkstemp(suffix=".csv")
    try:
        with os.fdopen(fd, "w", newline="") as f:
            f.write("t,off,segTag\n0.0,1.5,arm\n0.1,notanumber,az10\n0.2,1.1,az10\n")
        buf = io.StringIO()
        with contextlib.redirect_stderr(buf):
            _, rows_lc, _ = load_csv(tmp_path)
        assert len(rows_lc) == 2, rows_lc                 # the one bad row dropped, the other two kept
        assert "WARNING" in buf.getvalue() and "1/3" in buf.getvalue(), buf.getvalue()
    finally:
        os.remove(tmp_path)

    # alpha_metrics — exact counting, on the case the alpha-ceiling card exists to produce: a
    # limiter 20 airframe (ceiling 20 - min(4, 3) = 17), one sample past it, gates biting, and the
    # law still commanding nose-up (tgtPRaw < 0) while the up-gate is shut = commandIntoCeiling.
    afbw = {"alphaLimiter": 20.0, "cornerSpeed": 160.0, "gLimit": 9.0}
    acols = {"aoa", "aoaGU", "aoaGD", "qSched", "aoaRec", "tgtPRaw"}
    arows = [{"aoa": 5.0,  "aoaGU": 1.0, "aoaGD": 1.0, "qSched": 1.0, "aoaRec": 0.0,  "tgtPRaw": -0.3},
             {"aoa": 22.1, "aoaGU": 0.2, "aoaGD": 1.0, "qSched": 0.4, "aoaRec": 0.6,  "tgtPRaw": -0.8},
             {"aoa": 16.0, "aoaGU": 0.6, "aoaGD": 1.0, "qSched": 0.7, "aoaRec": 0.0,  "tgtPRaw": -0.8},
             {"aoa": -9.0, "aoaGU": 1.0, "aoaGD": 0.3, "qSched": 1.0, "aoaRec": -0.1, "tgtPRaw": 0.5}]
    am, askip = alpha_metrics(arows, acols, afbw)
    assert askip == {}, askip
    assert abs(am["aoaCeilDeg"] - 17.0) < 1e-9, am
    assert abs(am["aoaAboveCeilingPct"] - 25.0) < 1e-9, am          # only the 22.1 sample is past it
    assert abs(am["aoaPeakOverCeiling"] - 22.1 / 17.0) < 1e-9, am   # 1.3 = the v0.57 relay signature
    assert abs(am["gateMinUp"] - 0.2) < 1e-9 and abs(am["gateMinDn"] - 0.3) < 1e-9, am
    assert abs(am["qSchedMin"] - 0.4) < 1e-9, am
    assert abs(am["aoaRecoverActivePct"] - 50.0) < 1e-9, am         # 0.6 and -0.1, both past 0.01
    assert abs(am["aoaRecoverPeak"] - 0.6) < 1e-9, am
    # rows 1 (gateUp 0.2, nose-up) and 3 (gateDn 0.3, nose-down) count; row 2's gate is 0.6, open.
    assert abs(am["commandIntoCeilingPct"] - 50.0) < 1e-9, am
    # ...and "the card never reached the regime" must be 0.0, not missing -- the two are different
    # answers and only one of them means the run is worthless.
    calm, _ = alpha_metrics([dict(arows[0])], acols, afbw)
    assert calm["aoaAboveCeilingPct"] == 0.0 and calm["commandIntoCeilingPct"] == 0.0, calm
    # pre-v0.55 capture: no alphaLimiter on the header -> the ceiling half skips, the rest computes.
    _, s_nolim = alpha_metrics(arows, acols, {})
    assert set(s_nolim) == {"aoaCeilDeg", "aoaAboveCeilingPct", "aoaPeakOverCeiling"}, s_nolim

    # allocation_metrics — mean|outR| = 0.4, mean|outY| = 0.1 -> 0.8 roll; 1 of 4 samples has both
    # channels live with opposite signs (row 2); the sub-deadband row 4 must NOT count as opposed.
    ocols = {"outR", "outY", "bWt"}
    orows = [{"outR": 0.4,  "outY": 0.1,   "bWt": 0.0},
             {"outR": -0.6, "outY": 0.2,   "bWt": 1.0},
             {"outR": 0.4,  "outY": 0.09,  "bWt": 0.5},
             {"outR": 0.2,  "outY": -0.01, "bWt": 0.5}]
    om, oskip = allocation_metrics(orows, ocols)
    assert oskip == {}, oskip
    assert abs(om["rollYawAllocFrac"] - 0.8) < 1e-9, om
    assert abs(om["rollYawOpposedPct"] - 25.0) < 1e-9, om
    assert abs(om["bothActivePct"] - 75.0) < 1e-9, om        # row 4's yaw (0.01) is under the deadband
    assert abs(om["rollCmdMedian"] - 0.4) < 1e-9, om
    assert abs(om["rollBlendMean"] - 0.5) < 1e-9, om
    assert allocation_metrics([{"outR": 0.0, "outY": 0.0}], {"outR", "outY"})[0]["rollYawAllocFrac"] is None
    assert "rollBlendMean" in allocation_metrics(orows, {"outR", "outY"})[1]   # pre-v0.85: skipped
    # THE FLOOR CASE, the whole reason bothActivePct exists: a micro-step-sized segment (measured
    # median |outR| ~0.01) scores 0% opposed no matter how badly the two channels are allocated,
    # because neither ever clears the deadband. The zero must be READABLE as a floor -- i.e. the
    # medians and bothActivePct must sit right next to it in the same metric set.
    tiny = [{"outR": 0.010, "outY": -0.012}, {"outR": -0.008, "outY": 0.014}] * 10
    tm, _ = allocation_metrics(tiny, {"outR", "outY"})
    assert tm["rollYawOpposedPct"] == 0.0 and tm["bothActivePct"] == 0.0, tm
    assert tm["rollCmdMedian"] < ALLOC_DEADBAND and tm["yawCmdMedian"] < ALLOC_DEADBAND, tm

    # compute_segment wiring for both new families: the card-specific block lands ON TOP of the
    # step / sustained-turn metrics, not instead of them.
    ob_rows = [dict(r, t=i * 0.1, off=max(0.0, 2.0 - 0.2 * i), azErr=1.0, elevErr=1.0)
               for i, r in enumerate(orows * 8)]
    ob = compute_segment("obDR2", "oblique_step", ob_rows, ocols | {"t", "off", "azErr", "elevErr"})
    assert abs(ob["metrics"]["rollYawAllocFrac"]["value"] - 0.8) < 1e-9, ob
    assert "settleTime" in ob["metrics"] and "stickFlipRateR" in ob["metrics"], ob
    ah_rows = [dict(r, t=i * 1.0, headingRateFilt=12.0, spd=250.0, alt=8000.0)
               for i, r in enumerate(arows)]
    ah = compute_segment("alphaHold", "alpha_hold", ah_rows, acols | {"t", "headingRateFilt", "spd", "alt"},
                         {"fbw": afbw})
    assert abs(ah["metrics"]["meanTurnRateDegS"]["value"] - 12.0) < 1e-9, ah
    assert abs(ah["metrics"]["aoaAboveCeilingPct"]["value"] - 25.0) < 1e-9, ah
    assert compute_segment("alphaPull", "alpha_step", ah_rows,
                           acols | {"t"}, {"fbw": afbw})["metrics"]["gateMinUp"]["value"] == 0.2

    # THE SHIPPED CARDS THEMSELVES (cards/*.json). This is the drift check CLAUDE.md warns about --
    # the card files and this table have no compile-time link, and that pair silently broke once
    # already (v0.71: 19 of 21 segments scored "unknown"). Mirrors ScenarioPlayer.Validate: name ==
    # basename, positive durations, first segment 'arm'. Skipped, not failed, when the directory is
    # absent -- scorecard.py is copied around on its own.
    card_dir = os.path.join(os.path.dirname(_HERE), "cards")
    if os.path.isdir(card_dir):
        names = sorted(f for f in os.listdir(card_dir) if f.endswith(".json"))
        assert names, card_dir
        for fn in names:
            with open(os.path.join(card_dir, fn), encoding="utf-8") as f:
                card = json.load(f)
            base = os.path.splitext(fn)[0]
            assert card.get("name") == base, (fn, card.get("name"))   # the FILE is the card id
            segs = card.get("segments") or []
            assert segs and segs[0]["tag"] == "arm", (fn, segs[:1])
            seen = set()
            for s in segs:
                assert s.get("dur", 0) > 0, (fn, s)
                t = s["tag"]
                assert infer_type(t) != "unknown", (fn, t)            # the drift this exists to catch
                # compare-runs.py keys segments by tag alone, so a repeated SCORED tag inside one
                # card would be read as two replicates of itself. Repeated 'arm' is fine: excluded.
                assert t == "arm" or t not in seen, (fn, t)
                seen.add(t)
                # A track SHORTER than its segment doesn't fail: ScenarioPlayer.Demand clamps the
                # index, so the demand silently freezes partway through and the tail of the segment
                # measures a hold that was meant to be a sweep.
                ta, te = s.get("trackAz") or [], s.get("trackEl") or []
                if ta:
                    assert len(te) == len(ta), (fn, t)                 # ScenarioPlayer.Validate's rule
                    assert len(ta) >= round(s["dur"] / card["step"]), (fn, t, len(ta))
            for problem in card_setup_problems(card):
                raise AssertionError("%s: %s" % (fn, problem))

    # ...and that the check above can actually FAIL. No shipped card has a swept-knob conflict, so
    # without a synthetic one the loop would pass just as happily with a broken checker.
    assert not card_setup_problems({"name": "ok", "repeat": 4, "armToggle": "Control/Knob",
                                    "config": [{"key": "Other", "value": "1"}]})
    assert card_setup_problems({"name": "x", "armToggle": "Knob",
                                "config": [{"key": "Control/Knob", "value": "true"}]})  # same entry, spelled long
    assert card_setup_problems({"name": "x", "config": [{"key": "", "value": "1"}]})
    assert card_setup_problems({"name": "x", "config": [{"key": "A/B/C", "value": "1"}]})
    assert card_setup_problems({"name": "x", "config": [{"key": "/B", "value": "1"}]})
    assert card_setup_problems({"name": "x", "config": [{"key": "A/", "value": "1"}]})
    assert card_setup_problems({"name": "x", "config": [{"key": "A", "value": ""}]})
    assert card_setup_problems({"name": "x", "repeat": 21})
    assert card_setup_problems({"name": "x", "repeat": -1})
    assert not card_setup_problems({"name": "x", "airframe": "Multirole1"})
    assert card_setup_problems({"name": "x", "airframe": "any jet at the fixedwing-v2 entry"})
    # v0.91 -- a comma list is a FLEET, and whitespace around the commas is formatting the mod trims.
    # Prose is still caught, including one prose token hiding in an otherwise valid list.
    assert not card_setup_problems({"name": "x", "airframe": "Fighter1, Multirole1"})
    assert not card_setup_problems({"name": "x", "airframe": "Multirole1 "})
    assert card_setup_problems({"name": "x", "airframe": "Fighter1, any jet will do"})
    assert not card_setup_problems({"name": "x", "count": 4})
    assert card_setup_problems({"name": "x", "count": 17})
    assert card_setup_problems({"name": "x", "count": -1})
    # v0.93 -- a corner multiple. 0 is "unset" (the absolute startSpeed stands); a typo'd 10.0 would
    # otherwise fly as a refused lane per airframe with nothing saying it was a card bug.
    assert not card_setup_problems({"name": "x", "startSpeedCorner": 0})
    assert not card_setup_problems({"name": "x", "startSpeedCorner": 1.0})
    assert not card_setup_problems({"name": "x", "startSpeedCorner": 2})       # ints are legal JSON floats
    assert card_setup_problems({"name": "x", "startSpeedCorner": 10.0})
    assert card_setup_problems({"name": "x", "startSpeedCorner": 0.1})
    assert card_setup_problems({"name": "x", "startSpeedCorner": -1.0})

    print("selftest OK")


def deadscan(files):
    """`--deadscan`: which columns never varied, across a whole batch. A REPORT, not an input to
    scoring -- load_csv already withdraws the always-zero ones per capture. This is the wider sweep
    the invariant cannot make on its own: it also names the constant NON-zero columns, which are
    suspect for the same reason (a column that cannot move cannot answer a question) but are left
    scoring, because there the value itself is the evidence."""
    seen, lo, hi, cnt, flat_in = set(), {}, {}, {}, {}
    for f in files:
        _meta, rows, cols = load_csv(f)
        f_lo, f_hi = {}, {}
        for r in rows:
            for k, v in r.items():
                if k in aw.STRING_COLS or not isinstance(v, float):
                    continue
                seen.add(k)
                cnt[k] = cnt.get(k, 0) + 1
                lo[k] = v if k not in lo else min(lo[k], v)
                hi[k] = v if k not in hi else max(hi[k], v)
                f_lo[k] = v if k not in f_lo else min(f_lo[k], v)
                f_hi[k] = v if k not in f_hi else max(f_hi[k], v)
        # THE THIRD SHAPE (R40): a column live in one batch and flat in another, on the same build.
        # `targetBank` is the cautionary case -- it is written unconditionally, but it is a
        # deadbanded function of azErr, so a card that never leaves the deadband writes a flat
        # column that looks structurally dead and is not. Counted per file, reported as a ratio.
        for k in seen:
            if k in f_lo and f_lo[k] == f_hi[k]:
                flat_in[k] = flat_in.get(k, 0) + 1
    print(f"{len(files)} file(s), {len(seen)} numeric column(s)")
    for k in sorted(seen):
        n_flat = flat_in.get(k, 0)
        if lo[k] == hi[k]:
            kind = "DEAD (identically 0.0)" if lo[k] == 0.0 else f"CONSTANT {lo[k]:g}"
        elif n_flat:
            kind = (f"FLAT WITHIN {n_flat}/{len(files)} FILE(S) but varying over the set "
                    f"[{lo[k]:g}..{hi[k]:g}] -- read per capture, never pooled")
        else:
            continue
        print(f"  {k:<16} {kind}   n={cnt[k]}")
    print("DEAD columns are already withdrawn from scoring per capture (see dead_columns). "
          "CONSTANT and FLAT-IN-SOME ones are not -- read them, do not rank on them.")


def main(argv):
    if not argv:
        sys.exit(__doc__)
    if argv[0] == "--selftest":
        selftest()
        return
    if argv[0] == "--deadscan":
        deadscan(argv[1:])
        return
    json_path, files, i = None, [], 0
    verbose = "--verbose" in argv
    argv = [a for a in argv if a != "--verbose"]
    while i < len(argv):
        if argv[i] == "--json":
            if i + 1 >= len(argv):
                sys.exit("--json requires a path")
            json_path = argv[i + 1]
            i += 2
        else:
            files.append(argv[i])
            i += 1
    if not files:
        sys.exit("usage: scorecard.py [--json out.json] [--verbose] <recording.csv> [more.csv ...]\n"
                  "       scorecard.py --selftest")
    if json_path and len(files) != 1:
        sys.exit("--json writes one run's score.json — pass exactly one CSV alongside --json")
    # --json always writes the full result, so the roll-up is a stdout question only.
    detail = verbose or len(files) <= DETAIL_FILE_LIMIT
    n_segments, cards, warnings = 0, {}, {}
    for f in files:
        result = score_run(f)
        if detail:
            for w in result.get("warnings", []):  # stderr too: visible even when stdout is a --json file
                print(f"WARNING: {f}: {w}", file=sys.stderr)
        else:
            n_segments += len(result["segments"])
            card = result["provenance"].get("card") or "<no card>"
            cards[card] = cards.get(card, 0) + 1
            for w in result.get("warnings", []):
                key = norm_warning(w)
                cnt, example = warnings.get(key, (0, w))
                warnings[key] = (cnt + 1, example)
        if json_path:
            with open(json_path, "w", encoding="utf-8") as out:
                json.dump(result, out, indent=2)
            print(f"wrote {json_path}")
        elif detail:
            print_table(f, result)
    if not detail:
        print_rollup(len(files), n_segments, cards, warnings)


if __name__ == "__main__":
    main(sys.argv[1:])
