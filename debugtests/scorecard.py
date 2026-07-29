#!/usr/bin/env python3
"""Score a maneuver-recorder CSV into per-segment metrics (instructor-feedback-loop M0).

Stdlib only (no pandas/numpy), reuses analyze-wobble.py's CSV/header parsing, episode
(oscillation) detector and pitch-authority (relay) check rather than reimplementing them.

    python scorecard.py <recording.csv> [more.csv ...]        # human-readable table to stdout
    python scorecard.py --json score.json <recording.csv>     # write score.json (exactly 1 CSV)
    python scorecard.py --selftest                             # in-memory asserts, no file needed

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
alpha_hold, reversal, astern_wrap, micro_step, hover_hold, translate, bobup, transition, arm);
anything else — including "unsegmented"
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

G0 = 9.81  # m/s^2, for energy-height Eh = alt + V^2/2g (plan #4)

# Same per-signal dead-bands analyze-wobble's analyze() scans for oscillation episodes — kept as a
# local literal (that tuple isn't a module-level constant there) rather than editing that file.
WOBBLE_SIGNALS = (("bank", 3.0), ("azErr", 0.5), ("outR", 0.05), ("outP", 0.05), ("outY", 0.05), ("aoa", 2.0))

# Demand-scaled settle band for angular steps (see step_response_metrics). 10% of the step,
# floored at 0.05 deg (~0.9 mil — tighter than gun dispersion, so "settled" still means settled)
# and capped at 0.5 deg so steps >= 5 deg keep the classic fixed band.
BAND_FRAC, BAND_MIN_DEG, BAND_MAX_DEG = 0.10, 0.05, 0.5

# terminalOffDeg averages the last this-many seconds of a segment. 1.0 s ~= 16 recorder samples at
# the ~16 Hz sample rate (alternating 0.050/0.067 s steps), enough to average out the 0.01 deg
# column quantization without smearing in the approach.
TERMINAL_WINDOW_S = 1.0

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
    return meta, rows, cols


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


def pointing_metrics(t, rows, cols):
    """Pointing-error metrics computed for EVERY segment type (not just fine_track). Same shape as
    aoa_g_metrics: (metrics, skipped).
      rmsPointingErrorDeg - RMS of `off` (unchanged definition; was fine_track-only, which hid a
                            steady ~9.4 deg azimuth lag through the whole 30 s turn360)
      minOffDeg           - best approach anywhere in the segment ("got there then drifted" vs "never got there")
      terminalOffDeg      - mean `off` over the last TERMINAL_WINDOW_S ("how badly it missed when time ran out")
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
    else:
        skipped["rmsPointingErrorDeg"] = skipped["minOffDeg"] = skipped["terminalOffDeg"] = "missing column: off"
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
    else:
        skipped["gPeak"] = skipped["gSustained"] = "missing column: g"
    return m, skipped


def cfg_params(meta):
    """Numeric knobs from the '# config' header line as a dict — same regex/shape as
    aw.fbw_params(), applied to the other header line. Non-numeric values (law=EvolvedLegacy) simply
    don't match and are absent."""
    return {m.group(1): float(m.group(2)) for m in re.finditer(r"(\w+)=([-\d.]+)", meta.get("cfg", ""))}


def saturation_metrics(rows, cols, cfg, fbw):
    """Is the LAW at a limit, or the PLANT? (metrics, skipped), same shape as aoa_g_metrics.

    R21 (10 replicates of fixedwing-sweep) needed a forensic dig to establish that the bank clamp
    was active on 97% of a sustained turn while g sat at 5.4 of 9 — i.e. the law was saturated and
    the airframe was not. Every saturation question below is answerable from columns already in the
    CSV, so a run should self-report it:
      bankClampActivePct    - % samples with |targetBank| at Cfg.MaxBankAngle (the clamp is ON)
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
    if max_bank and {"targetBank", "bankTR"} <= cols:
        clamped = sum(1 for r in rows if abs(r.get("targetBank", 0.0)) >= max_bank - 0.01)
        over = [abs(r.get("bankTR", 0.0)) - max_bank for r in rows if abs(r.get("bankTR", 0.0)) > max_bank]
        m["bankClampActivePct"] = 100.0 * clamped / n if n else 0.0
        m["bankDemandExcessDeg"] = statistics.fmean(over) if over else 0.0
    else:
        why = "missing column(s): targetBank/bankTR" if max_bank else "no maxBank= on the '# config' header"
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


# alpha_metrics / allocation_metrics thresholds. Deliberately literals, not knobs: they are read
# thresholds on already-recorded signals, so a capture can always be re-scored with a different one.
# ponytail: sample COUNTING against a fixed 0.5 gate threshold, not an integral of the gate deficit.
# Ceiling: a segment that hovers either side of 0.5 reads bimodally between runs. Upgrade to
# mean(1 - aoaGU) over the segment if an A/B ever lands inside that noise.
GATE_BITING = 0.5      # aoaGU/aoaGD below this = the ceiling gate is at least half shut
CMD_DEADBAND = 0.05    # |tgtPRaw| below this is noise around zero, not a command (the 0.05
                       # analyze-wobble's crossings() and wobble_scan use). Fine for the alpha
                       # segments, where the pitch command is near the rail.
ALLOC_DEADBAND = 0.02  # ...but NOT fine for roll/yaw allocation on small steps: measured median
                       # |outR| is 0.006-0.017 on micro segments, so a 0.05 gate reports "no
                       # cross-fighting" for a segment that never cleared the gate. 0.02 is
                       # flightscore.py's STICK_DEADBAND — same number, so the two tools' answers
                       # are comparable — and allocation_metrics reports the occupancy WITH it.
BLEND_RAILED = 0.999   # bWt at/above this = lateralHold railed, eFine weight 0 (see saturation)


def alpha_metrics(rows, cols, fbw):
    """Did the card reach the AoA ceiling, and what did the law do there? (metrics, skipped).

    This block exists because `aoaLimiterActivePct` was 0 in EVERY segment of every card ever run
    (INSTRUCTOR-LOOP.md §3) — the "loaded jet mushing near its alpha limit above corner speed" case
    the ONE-LAW rule demands has never been exercised, so nothing downstream of it has ever been
    measured either. On an alpha_* segment `aoaLimiterActivePct == 0` is not a good score, it means
    THE CARD FAILED TO PROVOKE THE REGIME and every number here is about some other flight.
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
    if lim and "aoa" in cols:
        ceil = lim - min(4.0, 0.15 * lim)
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
        m["rollYawOpposedPct"] = 100.0 * sum(1 for r in both if r["outR"] * r["outY"] < 0) / n if n else 0.0
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


def wobble_scan(t, rows, cols, dur):
    """Oscillation-episode counts/frequencies via analyze_wobble.episodes() — the same detector,
    same signals/dead-bands, that analyze-wobble.py's own analyze() scans. Also stick sign-flip
    rate per axis via analyze_wobble.crossings()."""
    m = {}
    for axis, lbl in (("outP", "P"), ("outR", "R"), ("outY", "Y")):
        if axis in cols:
            cnt = len(aw.crossings(None, [r.get(axis, 0.0) for r in rows], 0.05))
            m[f"stickFlipRate{lbl}"] = cnt / dur if dur > 0 else 0.0
    for name, dead in WOBBLE_SIGNALS:
        if name not in cols:
            continue
        eps = aw.episodes(t, [r.get(name, 0.0) for r in rows], dead)
        m[f"wobbleEpisodes{_cap(name)}"] = len(eps)
        if eps:
            worst = max(eps, key=lambda e: e["dur"])
            m[f"wobbleFreqHz{_cap(name)}"] = worst["freq"]
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
        segments.append(compute_segment(tag, seg_type, seg_rows, cols, ctx))
        w = _tag_warning(tag, seg_type)
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
    assert pskip == {}, pskip
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

    # fine_track wiring: feed that same-shaped oscillation (now on azErr, amplitude clears the
    # 0.5deg dead-band) through wobble_scan and confirm the reused episode detector reports both
    # a nonzero count and roughly the known frequency.
    rows = [{"t": i * dt, "azErr": 5.0 * math.sin(2 * math.pi * 0.4 * i * dt),
             "outP": (0.6 if i % 2 else -0.6)} for i in range(600)]
    tt = [r["t"] for r in rows]
    wm = wobble_scan(tt, rows, {"azErr", "outP"}, tt[-1] - tt[0])
    assert wm["wobbleEpisodesAzErr"] >= 1, wm
    assert abs(wm["wobbleFreqHzAzErr"] - 0.4) < 0.08, wm     # crossing-based estimate, generous tolerance
    assert wm["stickFlipRateP"] > 0, wm                      # outP alternates every sample -> flips every row

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
    assert skipped == {}, skipped
    m2, skipped2 = aoa_g_metrics(rows, {"aoa"})
    assert "aoaLimiterActivePct" in skipped2 and "gPeak" in skipped2, skipped2

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

    print("selftest OK")


def main(argv):
    if not argv:
        sys.exit(__doc__)
    if argv[0] == "--selftest":
        selftest()
        return
    json_path, files, i = None, [], 0
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
        sys.exit("usage: scorecard.py [--json out.json] <recording.csv> [more.csv ...]\n"
                  "       scorecard.py --selftest")
    if json_path and len(files) != 1:
        sys.exit("--json writes one run's score.json — pass exactly one CSV alongside --json")
    for f in files:
        result = score_run(f)
        for w in result.get("warnings", []):    # stderr too: visible even when stdout is a --json file
            print(f"WARNING: {f}: {w}", file=sys.stderr)
        if json_path:
            with open(json_path, "w", encoding="utf-8") as out:
                json.dump(result, out, indent=2)
            print(f"wrote {json_path}")
        else:
            print_table(f, result)


if __name__ == "__main__":
    main(sys.argv[1:])
