#!/usr/bin/env python3
"""flightscore.py — physics-normalized flight quality metric for maneuver-recorder CSVs.

Answers ONE question per tick: *given what this airframe could physically do at that
instant, was there a better way to get the nose where it was asked?*  Every normalizer
comes from the sibling `<stem>.airframe.json` probe + live state (V, air density,
velocity vector) — never a hand-tuned constant — so a light jet, a loaded jet, a STOL
trainer and a helo produce comparable numbers.  That is the whole point; the moment a
constant is tuned to suit one plane the metric stops being cross-airframe.

    python debugtests/flightscore.py <rec.csv> [...] [--tau 0.25] [--cone 1.0] [--json]
    python debugtests/flightscore.py --levers <rec.csv>      # lever table even on old captures
    python debugtests/flightscore.py --verbose <many.csv>    # per-file reports past 10 files
    python debugtests/flightscore.py --selftest

Past ten files the ~28-line per-file report is suppressed and only the spread block (the aggregate)
prints — a 300-capture batch is 8400 lines otherwise. `--verbose` restores it; ten or fewer files
behave exactly as before.

Reads: t, off, spd, airDensity, velX/Y/Z, outP/outR/outY, segTag (58-col recorder), plus the
optional v0.83/v0.85 lever columns iGate/leadDeg/bSup/bWt/phiLead (see `levers`).
Never edits anything. Stdlib only.
"""

import csv
import json
import math
import os
import re
import statistics as st
import sys

# ---- constants -------------------------------------------------------------------
# tau_feel is THE one human-anchored number in this file: the first-order time constant
# that reads as "instant" in a mouse-aim game. It is a FEEL constant, not physics — it
# says how fast a human wants the error gone, not how fast the plane can do it. Exposed
# as --tau precisely so it can be argued with.
TAU_FEEL = 0.25
SLOPE_WIN_S = 0.20   # least-squares window for d(off)/dt, in SECONDS not samples: the
                     # recorder rate is Cfg.RecordRateHz (15 Hz in the R21 corpus, not the
                     # fixed step), so a fixed sample count would mean a different filter
                     # bandwidth per capture and runs would stop being comparable.
ON_TARGET_DEG = 1.0  # inside this cone there is nothing left to score. --cone matters more
                     # than it looks: the card's micro1..10 / fine segments are ENTIRELY
                     # sub-degree, so at 1.0 they read 100% ON_TARGET and A is undefined for
                     # exactly the fine-aim "it gets confused" complaint. Drop it to ~0.2 to
                     # score those; the reversal-rate column sees them at any cone.
AF_LIMIT_FRAC = 0.85 # >= this fraction of omega_avail = the airframe, not the law
E_CLIP = 2.0
STICK_DEADBAND = 0.02  # |out| below this is noise around zero, not a commanded reversal
CHURN_MIN_S = 1.0    # churn needs this much weight in BOTH populations to mean anything
RHO0 = 1.225
G = 9.81
AXES = ("outP", "outR", "outY")
NEED = ("t", "off", "spd", "velX", "velY", "velZ") + AXES  # tuple: load order matters

# Fail-soft defaults, the mod's own probe convention: a missing/renamed field degrades to
# a documented number instead of crashing. These are deliberately CONSERVATIVE (a smaller
# gLimit => smaller omega_avail => more ticks called AIRFRAME_LIMITED => fewer false
# accusations against the control law when the sidecar is unreadable).
DEFAULTS = {"aircraftGLimit": 7.0, "cornerSpeed": 180.0, "maxPitchAngularVel": 0.75}

BINS = ("REGRESSING", "STALLED", "WORKING", "NEAR_OPTIMAL")
CLASSES = ("ON_TARGET", "AIRFRAME_LIMITED") + BINS

# ---- v0.83 / v0.85 lever columns --------------------------------------------------
# Five columns the mod records on BOTH sides of their config toggle, so a capture can tell
# "the fix fired and helped" from "the fix never fired" — both of which read as a smaller
# error. Everything derived from them is None when the column is absent: None means NOT
# MEASURED and is never rendered as 0.0. 162 captures predate them and must keep scoring
# byte-identically, which is why the whole lever block is gated on the columns existing.
OPT = ("azErr", "iGate", "leadDeg", "bSup", "bWt", "phiLead")
LEVER_COLS = OPT[1:]        # azErr is not new; it is only the x-axis of the correlations
PRED_FLOOR = 0.30    # ChaseController.cs `const float predFloor` — a const there, not a Cfg
                     # bind, so it cannot be read off the `# config` line.
                     # ponytail: mirrored constant. If it ever becomes a Cfg knob, pull it
                     # from cfg like fineAng/bankDz/alignHold below and delete this.
XF_SUSTAIN_S = 0.30  # a roll/yaw sign disagreement shorter than this is a zero crossing,
                     # not a fight. This IS the control on xfightPct — see `levers`.
BWT_LIVE = 0.20      # below this the roll-to-align channel is off and its correlation with
                     # |azErr| cannot be a loop gain, however large it looks.
BSUP_MIN = 0.01      # below this the suppressor applied nothing, i.e. the segment is not
                     # below-nose — which is the only hemisphere the v0.85 check is about.
LEVER_KEYS = ("iGate", "iStallPct", "leadFrac", "predFloorPct", "bSup", "bWt", "bWtSd",
              "rBwt", "rBsup", "rSham", "phiLeadPct", "xfightPct", "xfightSusPct", "xfightWt")


# ---- physics ---------------------------------------------------------------------
def omega_avail(v, rho, p):
    """(omega_avail, omega_turn, omega_pitch) in deg/s — the achievable reorientation rate.

    Below corner speed the load factor is LIFT-limited and scales with dynamic pressure,
    so n_avail = n_struct * min(1, q/q_corner). The density term is why `airDensity` is a
    recorded column: cornerSpeed is a sea-level number, and at 4 km rho is ~0.85, so
    (V/Vc)^2 alone overstates n by ~44%. At sea level the two are identical.

    ponytail: q_ratio via (rho,V,Vc) is the design-point form. The first-principles form
    is n = q*S*Cl_max/(m*g) from the sidecar's Cl curve + wingAreaTotal + massKg; upgrade
    to it if a capture ever flies far off design mass (heavy loadout, near-empty fuel).
    """
    v = max(float(v), 1.0)
    rho = float(rho) if float(rho) > 0.01 else RHO0
    q_ratio = (rho / RHO0) * (v / p["cornerSpeed"]) ** 2
    n = max(1.05, p["aircraftGLimit"] * min(1.0, q_ratio))
    turn = math.degrees(G * math.sqrt(n * n - 1.0) / v)   # steady level-turn rate
    # maxPitchAngularVel is the game's ASSIST-OFF flat rate cap (ControlsFilter.FlyByWire:
    # assist ON or q_ratio>1.2 uses gLimit*9.81/max(V,0.75*Vc_fbw) instead). Measured: on the
    # R21 corpus that assist branch is 19.26 deg/s vs `turn` 19.14 — the same number, so the
    # min() below is inert on every capture on file (assist=1 in 100% of 50k rows, and
    # |fbwTgtPR| never exceeds 0.46 of the 0.75 rad/s cap). Kept because it IS the ceiling
    # for an assist-off low-speed airframe. ponytail: if an assist-off capture ever appears,
    # branch on the recorded `assist` column instead of min()-ing the two.
    pitch = math.degrees(p["maxPitchAngularVel"])
    return min(turn, pitch), turn, pitch


def slope(ts, ys):
    """Least-squares dy/dt. Raw finite differences on a 15 Hz record are pure noise."""
    n = len(ts)
    mt, my = sum(ts) / n, sum(ys) / n
    den = sum((t - mt) ** 2 for t in ts)
    return 0.0 if den <= 0 else sum((t - mt) * (y - my) for t, y in zip(ts, ys)) / den


def pearson(xs, ys):
    """r over the pairs where both are present.

    None — not 0.0 — when a series is flat or too short. A correlation on a constant is
    undefined, and the v0.85 PASS case *is* a constant (`bWt` suppressed to ~0), so
    rendering it as 0.0 would be a lie in the one place the number matters most.
    """
    ps = [(x, y) for x, y in zip(xs, ys) if x is not None and y is not None]
    n = len(ps)
    if n < 8:
        return None
    mx = sum(p[0] for p in ps) / n
    my = sum(p[1] for p in ps) / n
    sxx = sum((p[0] - mx) ** 2 for p in ps)
    syy = sum((p[1] - my) ** 2 for p in ps)
    if sxx <= 1e-12 or syy <= 1e-12:
        return None
    return sum((p[0] - mx) * (p[1] - my) for p in ps) / math.sqrt(sxx * syy)


def angle_between(a, b):
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(x * x for x in b))
    if na < 1.0 or nb < 1.0:      # near-zero speed: direction is meaningless, not slow
        return 0.0
    c = sum(x * y for x, y in zip(a, b)) / (na * nb)
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


# ---- io --------------------------------------------------------------------------
def load_airframe(csv_path):
    """Probed capability snapshot, every read fail-soft (mod convention)."""
    p = dict(DEFAULTS)
    p["name"] = p["unit"] = "?"
    p["source"] = "defaults"
    side = os.path.splitext(csv_path)[0] + ".airframe.json"
    try:
        with open(side) as f:
            d = json.load(f)
    except Exception:
        return p
    p["source"] = os.path.basename(side)
    for k in DEFAULTS:
        try:
            v = float(d[k])
            if v > 0:
                p[k] = v
        except Exception:
            pass                      # keep the documented default
    p["name"] = str(d.get("jsonKey") or d.get("definitionName") or "?")
    p["unit"] = str(d.get("unitName") or "?")
    return p


def load_cfg(path):
    """The `# config` / `# fbw` scalars the lever metrics need, all fail-soft.

    A missing key is simply absent, and the one metric that needs it degrades to None.
    ponytail: 4-line regex rather than importing scorecard.py's `cfg_params` — a private
    regex is a smaller dependency than that module's shape; share them if a third tool
    ever needs the same parse.
    """
    out = {}
    try:
        with open(path) as f:
            for line in f:
                if not line.startswith("#"):
                    break
                for k, v in re.findall(r"(\w+)=(-?\d+(?:\.\d+)?)\b", line):
                    out.setdefault(k, float(v))
    except Exception:
        pass
    return out


def load_run(path):
    with open(path) as f:
        lines = [l for l in f if not l.startswith("#")]
    rd = csv.DictReader(lines)
    missing = set(NEED) - set(rd.fieldnames or [])
    if missing:
        raise SystemExit(f"{path}: missing columns {sorted(missing)}")
    has_rho = "airDensity" in (rd.fieldnames or [])   # optional: pre-v0.69 captures lack it
    # Optional lever columns: an absent column leaves the key ABSENT from `run` (never a list
    # of zeros), which is what makes "not measured" distinguishable downstream.
    opt = [k for k in OPT if k in (rd.fieldnames or [])]
    run = {k: [] for k in NEED + ("segTag", "airDensity") + tuple(opt)}
    for r in rd:
        try:
            vals = [float(r[k]) for k in NEED]
        except (ValueError, TypeError):
            continue                  # a torn last row on a crashed capture
        for k, v in zip(NEED, vals):
            run[k].append(v)
        try:
            rho = float(r["airDensity"]) if has_rho else RHO0
        except (ValueError, TypeError):
            rho = RHO0
        run["airDensity"].append(rho if rho > 0.01 else RHO0)
        run["segTag"].append((r.get("segTag") or "").strip())
        for k in opt:
            try:
                run[k].append(float(r[k]))
            except (ValueError, TypeError):
                run[k].append(None)   # one torn cell, not a zero
    return run


# ---- scoring ---------------------------------------------------------------------
def groups(run):
    """Contiguous runs of one segTag. Windows never straddle a tag change — `off` steps
    at a segment boundary and a straddling slope fit would invent a closure rate."""
    tags, out, start = run["segTag"], [], 0
    for i in range(1, len(tags) + 1):
        if i == len(tags) or tags[i] != tags[start]:
            out.append((tags[start] or "-", list(range(start, i))))
            start = i
    return out


def score_ticks(run, p, tau, cone=ON_TARGET_DEG):
    """One record per scoreable tick: class, efficiency e, time weight, control activity."""
    t, off, spd = run["t"], run["off"], run["spd"]
    rho = run.get("airDensity") or [RHO0] * len(t)
    vel = list(zip(run["velX"], run["velY"], run["velZ"]))
    ticks = []
    for tag, idx in groups(run):
        if len(idx) < 5:
            continue
        dts = [t[idx[i + 1]] - t[idx[i]] for i in range(len(idx) - 1)]
        dt = st.median(dts) or 0.02
        half = max(1, int(round(SLOPE_WIN_S / dt / 2.0)))
        for j in range(half, len(idx) - half):
            lo, i, hi = idx[j - half], idx[j], idx[j + half]
            span = t[hi] - t[lo]
            if span <= 0:
                continue
            w = span / (2.0 * half)                      # local time weight
            edot = -slope(t[lo:hi + 1], off[lo:hi + 1])  # closure rate, deg/s
            turn_actual = angle_between(vel[lo], vel[hi]) / span
            oa, _, _ = omega_avail(spd[i], rho[i], p)
            act = sum(abs(run[a][i] - run[a][i - 1]) for a in AXES) / max(dt, 1e-6)

            if off[i] <= cone:
                cls, e = "ON_TARGET", 0.0
            elif turn_actual >= AF_LIMIT_FRAC * oa:
                # There was no better way. Not a defect; law work on these ticks is wasted.
                cls, e = "AIRFRAME_LIMITED", 0.0
            else:
                omega_target = min(oa, off[i] / tau)
                e = max(-E_CLIP, min(E_CLIP, edot / omega_target)) if omega_target > 0 else 0.0
                cls = ("REGRESSING" if e < -0.05 else "STALLED" if e < 0.15
                       else "WORKING" if e < 0.7 else "NEAR_OPTIMAL")
            ticks.append({"i": i, "tag": tag, "cls": cls, "e": e, "w": w, "act": act,
                          "oa": oa, "turn": turn_actual, "off": off[i], "dt": dt})
    return ticks


def smoothness(run, ticks):
    """reversal rate / jerk RMS / churn — model-free, 'the controls fight each other'."""
    idx = [k["i"] for k in ticks]
    out = {"rev_per_s": {}, "jerk_rms": {}}
    if len(idx) < 3:
        return out
    dur = sum(k["w"] for k in ticks) or 1.0
    dts = {k["i"]: k["dt"] for k in ticks}   # dt varies per segment (own median), not per run
    for a in AXES:
        s, jerks, flips, prev = run[a], [], 0, 0.0
        for i in idx:
            d = s[i] - s[i - 1]
            jerks.append(d / max(dts[i], 1e-6))
            if abs(s[i]) > STICK_DEADBAND:      # deadband: noise around zero is not a reversal
                if prev != 0.0 and (s[i] > 0) != (prev > 0):
                    flips += 1
                prev = s[i]
        out["rev_per_s"][a] = flips / dur
        out["jerk_rms"][a] = math.sqrt(sum(j * j for j in jerks) / len(jerks))

    bad = [k for k in ticks if k["cls"] in ("REGRESSING", "STALLED")]
    good = [k for k in ticks if k["cls"] in ("WORKING", "NEAR_OPTIMAL")]
    # Both populations need real time behind them or churn is a lottery on 2-3 ticks. Below
    # CHURN_MIN_S you are measuring a transient, not a regime — report n/a and mean it.
    ok = (sum(k["w"] for k in bad) >= CHURN_MIN_S and sum(k["w"] for k in good) >= CHURN_MIN_S)
    mg = st.mean([k["act"] for k in good]) if good else 0.0
    out["churn"] = st.mean([k["act"] for k in bad]) / mg if ok and mg > 0 else None
    return out


def levers(run, ticks, cfg):
    """v0.83/v0.85 lever columns, per segment: did each mechanism FIRE, and is the v0.85
    below-nose roll loop still open?

    Firing and helping are reported as separate numbers on purpose — the mod records these
    on both sides of every toggle precisely because "the fix worked" and "the fix never ran"
    both read as a smaller error. Every field is None when its column (or its `# config`
    scalar) is missing: NOT MEASURED, never 0.0.

    Two of these carry their own control, because this project already confirmed a
    correlation-based gate hypothesis and then had sham gates falsify it
    (GATE-CHATTER-FINDINGS.md §1):

    * `rSham` is the definitional twin of `rBwt`. `bWt` is built from
      `lateralHold = clamp01((|azErr| - bankDz)/alignHold)`, so it is an explicit algebraic
      function of |azErr| and correlates with it BY CONSTRUCTION. `rSham` is that bare
      function's own correlation — the common-cause ceiling. `rBwt` at or above `rSham` is
      definitional and is NOT evidence of feedback; only a gap below it says the v0.85
      suppression decoupled the loop.
    * `xfightSusPct` is the control on `xfightPct`. Roll and yaw disagree in sign for a tick
      or two at every zero crossing by construction; only a disagreement that persists past
      XF_SUSTAIN_S is an allocation fight rather than a crossing.

    `xfightWt` has no sham and does not need one: its confound runs the OTHER WAY. Sign
    disagreement clusters at crossings, where |azErr| is small, where `lateralHold` and
    therefore `bWt` are small — so common cause pushes `xfightWt` NEGATIVE. A positive value
    is the direction the confound cannot produce; a negative one is just crossings and the
    output says so.
    """
    out = dict.fromkeys(LEVER_KEYS)
    idx = [k["i"] for k in ticks]
    w = [k["w"] for k in ticks]
    tw = sum(w)
    if tw <= 0:
        return out
    col = lambda n: None if run.get(n) is None else [run[n][i] for i in idx]

    def wpct(mask, over=None):
        den = sum(x for x, m in zip(w, over) if m) if over is not None else tw
        return None if den <= 0 else 100.0 * sum(x for x, m in zip(w, mask) if m) / den

    def wmean(vals, mask=None):
        num = den = 0.0
        for a, x, m in zip(vals, w, mask if mask is not None else [True] * len(vals)):
            if a is not None and m:
                num += a * x
                den += x
        return None if den <= 0 else num / den

    off = col("off")
    azE = col("azErr")
    aza = None if azE is None else [abs(a) for a in azE]
    iG, lead, bS, bW, pL = (col(k) for k in ("iGate", "leadDeg", "bSup", "bWt", "phiLead"))

    # v0.83(b) INTEGRAL STALL GATE. With IntegralStallGate OFF, iGate == fineBlend ==
    # clamp01(1 - off/FineAngle), which is EXACTLY 0 outside the fine cone. So "gate open at
    # off > FineAngle" is 0.0% by construction on the old path: a zero point, not a threshold.
    if iG is not None:
        out["iGate"] = wmean(iG)
        fa = cfg.get("fineAng")
        if fa:
            out["iStallPct"] = wpct([g is not None and g > 0.01 and o > fa
                                     for g, o in zip(iG, off)])

    # v0.83(a) RELATIVE TURN LEAD, and the predFloor it feeds. R21 settled window: the lead
    # ate 84% of azErr and the floor bound on 100% of samples. Floor binds (low side) iff
    # sign(azErr)*leadDeg > (1-predFloor)*|azErr| — exact, straight off the clamp in Apply.
    if lead is not None and aza is not None:
        big = [a > 1.0 for a in aza]          # below 1 deg the lead is noise on noise
        fr = [abs(l) / a for l, a, m in zip(lead, aza, big) if m and l is not None]
        if fr:
            out["leadFrac"] = st.median(fr)
        out["predFloorPct"] = wpct(
            [m and l is not None and (1.0 if e >= 0 else -1.0) * l > (1.0 - PRED_FLOOR) * a
             for l, e, a, m in zip(lead, azE, aza, big)], over=big)

    # v0.85 BELOW-NOSE ROLL-TO-ALIGN LOOP. bWt is the loop gain the +0.918 was measured on;
    # bSup is the suppressor v0.85 rebuilt. rBsup is the DISARM signature: pre-v0.85
    # belowSuppress carried a (1 - lateralHold) factor, so it SHRANK as the azimuth error its
    # own output was creating grew. There is no geometric reason for a belowness measure to
    # anticorrelate with azimuth error, so a clearly negative rBsup means that factor is back.
    if bS is not None:
        out["bSup"] = wmean(bS)
        if aza is not None:
            out["rBsup"] = pearson(aza, bS)
    if pL is not None:
        out["phiLeadPct"] = wpct([p is not None and abs(p) > 1e-4 for p in pL])
    if bW is not None:
        out["bWt"] = wmean(bW)
        vals = [b for b in bW if b is not None]
        if len(vals) > 1:
            out["bWtSd"] = st.pstdev(vals)
        if aza is not None:
            out["rBwt"] = pearson(aza, bW)
    dz, hold = cfg.get("bankDz"), cfg.get("alignHold")
    if aza is not None and dz is not None and hold:
        out["rSham"] = pearson(aza, [max(0.0, min(1.0, (a - dz) / hold)) for a in aza])

    # CROSS-FIGHTING — roll and yaw commanding OPPOSITE azimuth corrections. Same definition
    # gatechatter.py uses (`rollYawAnti`), so the two tools cannot disagree about what a fight
    # is. Needs no new column, hence the --levers flag to get it on the old corpus too.
    oR, oY = col("outR"), col("outY")
    anti = [abs(r) > STICK_DEADBAND and abs(y) > STICK_DEADBAND and (r > 0) != (y > 0)
            for r, y in zip(oR, oY)]
    out["xfightPct"] = wpct(anti)
    sus, j = [False] * len(anti), 0
    while j < len(anti):
        if not anti[j]:
            j += 1
            continue
        k = j
        while k < len(anti) and anti[k] and (k == j or idx[k] == idx[k - 1] + 1):
            k += 1                    # a run must not straddle a gap: on the pooled '= ALL'
                                      # tick list `i` jumps at every segment boundary.
        if sum(w[j:k]) >= XF_SUSTAIN_S:
            sus[j:k] = [True] * (k - j)
        j = k
    out["xfightSusPct"] = wpct(sus)
    if bW is not None:
        wa = sum(x for x, m in zip(w, anti) if m)
        hi, lo = wmean(bW, anti), wmean(bW, [not x for x in anti])
        if hi is not None and lo is not None and wa >= CHURN_MIN_S and tw - wa >= CHURN_MIN_S:
            out["xfightWt"] = hi - lo
    return out


def summarize(run, ticks):
    if not ticks:
        return None
    tw = sum(k["w"] for k in ticks)
    cov = {c: 100.0 * sum(k["w"] for k in ticks if k["cls"] == c) / tw for c in CLASSES}
    scored = [k for k in ticks if k["cls"] in BINS]
    sw = sum(k["w"] for k in scored)
    # A: time-weighted mean of clip(e,-1,1) over SCORED ticks, remapped [-1,1] -> [0,1].
    # 0.5 = nose stationary vs the demand, 1.0 = closing at the ideal rate, 0.0 = diverging
    # at the full available rate. ponytail: if you want stalled==0 instead of 0.5, clip to
    # [0,1] here — one edit — but that throws away the regressing-vs-stalled distinction
    # that the coverage percentages then have to carry alone.
    A = ((sum(max(-1.0, min(1.0, k["e"])) * k["w"] for k in scored) / sw) + 1.0) / 2.0 if sw else None
    sm = smoothness(run, ticks)
    # S from the churn ratio alone: it is already DIMENSIONLESS (effort while failing /
    # effort while working), so it needs no scale constant. Folding Hz and stick-units/s
    # into the same 0..1 would require inventing weights, which is exactly the per-plane
    # tuning this metric exists to avoid. Reversal rate + jerk stay as raw diagnostics.
    S = 1.0 / (1.0 + sm["churn"]) if sm.get("churn") is not None else None
    return {"dur": tw, "n": len(ticks), "A": A, "S": S, "coverage": cov,
            "e_med": st.median([k["e"] for k in scored]) if scored else None,
            "off_med": st.median([k["off"] for k in ticks]),
            "omega_avail_med": st.median([k["oa"] for k in ticks]),
            "turn_med": st.median([k["turn"] for k in ticks]), **sm}


def score_file(path, tau, cone=ON_TARGET_DEG, force_levers=False):
    run = load_run(path)
    p = load_airframe(path)
    ticks = score_ticks(run, p, tau, cone)
    segs = {}
    for tag in dict.fromkeys(k["tag"] for k in ticks):
        segs[tag] = summarize(run, [k for k in ticks if k["tag"] == tag])
    res = {"file": os.path.basename(path), "airframe": p, "cone_deg": cone,
           "segments": segs, "all": summarize(run, ticks)}
    # The lever block appears ONLY when the capture actually carries a lever column (or the
    # user forces it). That is what keeps every pre-v0.83 capture's text AND json output
    # byte-identical to before this feature existed.
    if ticks and ([k for k in LEVER_COLS if k in run] or force_levers):
        cfg = load_cfg(path)
        res["levers"] = {t: levers(run, [k for k in ticks if k["tag"] == t], cfg)
                         for t in segs}
        res["levers"]["= ALL"] = levers(run, ticks, cfg)
    return res


# ---- report ----------------------------------------------------------------------
def fmt(s, name):
    if not s:
        return f"  {name:<14} (no scoreable ticks)"
    c = s["coverage"]
    a = "  -  " if s["A"] is None else f"{s['A']:.3f}"
    sv = "  -  " if s["S"] is None else f"{s['S']:.3f}"
    em = "  -  " if s["e_med"] is None else f"{s['e_med']:+.2f}"
    rev = sum(s["rev_per_s"].values())
    jrk = max(s["jerk_rms"].values() or [0.0])
    return (f"  {name:<14}{s['dur']:6.1f}s  A={a} S={sv}  e~{em}  "
            f"off~{s['off_med']:5.1f}  w_act/w_avail={s['turn_med']:5.1f}/{s['omega_avail_med']:5.1f}  "
            f"rev{rev:5.1f}/s jrk{jrk:5.2f}  "
            f"| on-tgt {c['ON_TARGET']:4.0f}%  af-lim {c['AIRFRAME_LIMITED']:4.0f}%  "
            f"reg {c['REGRESSING']:4.0f}%  stall {c['STALLED']:4.0f}%  "
            f"work {c['WORKING']:4.0f}%  opt {c['NEAR_OPTIMAL']:4.0f}%")


LEVER_FMT = (("iGate~", "iGate", "{:.3f}"), ("iStal%", "iStallPct", "{:.0f}"),
             ("lead", "leadFrac", "{:.2f}"), ("floor%", "predFloorPct", "{:.0f}"),
             ("bSup~", "bSup", "{:.3f}"), ("bWt~", "bWt", "{:.3f}"),
             ("r(bWt)", "rBwt", "{:+.2f}"), ("r(bSup)", "rBsup", "{:+.2f}"),
             ("sham", "rSham", "{:+.2f}"), ("phiL%", "phiLeadPct", "{:.0f}"),
             ("xf%", "xfightPct", "{:.1f}"), ("xfSus%", "xfightSusPct", "{:.1f}"),
             ("xfWt", "xfightWt", "{:+.3f}"))


def loop_verdict(lv):
    """(tag, lever dict, failed) for the v0.85 below-nose loop-gain check, or None.

    Scoped, because an unscoped version fires on every large-azimuth segment: v0.85's defect
    is BELOW-NOSE, and in the upper hemisphere `bWt == lateralHold` by design and is SUPPOSED
    to track |azErr| — that is roll-to-align working, not a loop. So a segment only qualifies
    when the suppressor has something to suppress (`bSup~ >= BSUP_MIN`) and is live
    (`bWt~ >= BWT_LIVE`); a large correlation on a switched-off channel is not a loop gain.

    FAIL needs all three: a live below-nose channel, a real correlation, AND a correlation the
    definitional twin `rSham` does not already account for. `rBwt` on its own is not evidence
    — that is the lesson GATE-CHATTER-FINDINGS.md paid for.
    """
    live = [(t, l) for t, l in lv.items()
            if t != "= ALL" and l.get("rBwt") is not None
            and (l.get("bSup") or 0.0) >= BSUP_MIN and (l.get("bWt") or 0.0) >= BWT_LIVE]
    if not live:
        return None
    t, l = max(live, key=lambda kv: kv[1]["rBwt"])
    sh = l.get("rSham")
    return t, l, (l["rBwt"] >= 0.50 and (sh is None or l["rBwt"] >= sh - 0.20))


def lever_report(res):
    """The v0.83/v0.85 lever table + the one number the v0.85 fix lives or dies by."""
    lv = res.get("levers")
    if not lv:
        return
    print("  --- v0.83/v0.85 levers  ('-' = column absent from this capture = NOT MEASURED) ---")
    print("  " + "{:<14}".format("") + "".join(f"{h:>8}" for h, _, _ in LEVER_FMT))
    for tag, l in lv.items():
        cells = "".join(f"{('-' if l.get(k) is None else f.format(l[k])):>8}"
                        for _, k, f in LEVER_FMT)
        print(f"  {tag:<14}{cells}")
    print("  ('= ALL' pools segments — its correlations are Simpson-prone; read them per segment.)")
    v = loop_verdict(lv)
    if v:
        t, l, fail = v
        r, sh = l["rBwt"], l.get("rSham")
        print(f"  v0.85 loop-gain check: worst r(bWt,|azErr|)={r:+.3f} [{t}] bWt~{l['bWt']:.3f} "
              f"sd{l.get('bWtSd') or 0.0:.3f} sham={'-' if sh is None else f'{sh:+.2f}'} "
              f"gap={'-' if sh is None else f'{sh - r:+.2f}'} -> {'FAIL' if fail else 'pass'}"
              f"   (pre-fix elDn ref +0.918; FAIL = r>=+0.50 on a live below-nose channel AND "
              f"gap<+0.20, i.e. the suppression did not decouple it from its definitional twin)")
    elif any(l.get("bWt") is not None for l in lv.values()):
        print(f"  v0.85 loop-gain check: no segment is both below-nose (bSup~>={BSUP_MIN:.2f}) "
              f"and live (bWt~>={BWT_LIVE:.2f}) with a correlatable bWt — either this card never flies "
              f"below the nose, or the channel is suppressed, which IS the pass case. "
              f"Read bSup~/bWt~ above; do not read this as a pass on its own.")


def report(res):
    p = res["airframe"]
    print(f"\n{res['file']}")
    print(f"  {p['unit']} [{p['name']}]  gLimit={p['aircraftGLimit']:g} Vc={p['cornerSpeed']:g} "
          f"maxPitchAngVel={p['maxPitchAngularVel']:g} rad/s  ({p['source']})")
    for tag, s in res["segments"].items():
        print(fmt(s, tag))
    print(fmt(res["all"], "= ALL"))
    a = res["all"]
    if a:
        rv = "/".join(f"{a['rev_per_s'][x]:.1f}" for x in AXES)
        jk = "/".join(f"{a['jerk_rms'][x]:.2f}" for x in AXES)
        ch = "n/a" if a["churn"] is None else f"{a['churn']:.2f}"
        print(f"  smoothness: reversals/s P/R/Y {rv}   jerk rms {jk}   churn {ch}")
    lever_report(res)


def spread(results):
    """Run-to-run band of A = the metric's own noise floor. An effect smaller than the band
    is not an effect. A run that simply lacks a tag is EXCLUDED from that tag, never
    back-filled with its whole-run value (same discipline as compare-runs.py).

    Mixed airframes get one block EACH. This used to print "Not pooling." and then pool anyway —
    the warning was the whole implementation — so a trainer's `turn360` A landed in the same
    min/med/max band as a loaded jet's and the "band" read as the metric's noise floor when it was
    really the airframe difference, i.e. the one mistake the warning names. Same rule as
    compare-runs.py's grouping: never merged, one block per airframe, in name order.
    """
    frames = sorted({r["airframe"]["name"] for r in results})
    if len(frames) > 1:
        print(f"\nWARNING: mixed airframes {frames} — A is comparable across them, "
              "but a segTag flown by different planes is not the same test. Not pooling: "
              f"{len(frames)} separate blocks below.")
    for name in frames:
        _spread_one([r for r in results if r["airframe"]["name"] == name], name)


def _spread_one(results, name):
    tags = list(dict.fromkeys(t for r in results for t in r["segments"]))
    print(f"\n=== spread across {len(results)} runs of {name} (the metric's own noise floor) ===")
    meds = {}
    for tag in tags + ["= ALL"]:
        vals = [r["all"] if tag == "= ALL" else r["segments"].get(tag) for r in results]
        As = [v["A"] for v in vals if v and v["A"] is not None]
        if not As:
            continue
        meds[tag] = st.median(As)
        n = f"n={len(As)}/{len(results)}" if len(As) < len(results) else ""
        sd = f"{st.stdev(As):.3f}" if len(As) > 1 else "  -  "
        print(f"  {tag:<14} A min={min(As):.3f} med={meds[tag]:.3f} max={max(As):.3f} "
              f"sd={sd}  (band {max(As)-min(As):.3f}) {n}")
    worst = sorted((a, t) for t, a in meds.items() if t != "= ALL")
    print("  worst-scoring tags: " + ", ".join(f"{t}({a:.3f})" for a, t in worst[:5]))


# Past this many CSVs the ~28-line per-file report is suppressed and only spread() — which IS the
# aggregate, and gets better with n — is printed. 10 is the line between "someone is reading these"
# and "a batch produced these"; --verbose overrides, and at or below it nothing changes.
DETAIL_FILE_LIMIT = 10


# ---- selftest --------------------------------------------------------------------
def synth(off_fn, turn, dur=6.0, dt=1 / 15.0, v=250.0, out_fn=None, extra=None):
    """A fake run: `off` from off_fn(t), velocity vector rotated by `turn` (deg/s, or a
    callable giving cumulative degrees). `extra` = {column: fn(t, i)} for the optional lever
    columns — omit it and the run has NO lever columns, which is the pre-v0.83 case."""
    run = {k: [] for k in NEED + ("segTag", "airDensity")}
    run.update({k: [] for k in (extra or {})})
    n = int(dur / dt)
    for i in range(n):
        t = i * dt
        th = math.radians(turn(t) if callable(turn) else turn * t)
        run["t"].append(t); run["off"].append(off_fn(t)); run["spd"].append(v)
        run["airDensity"].append(RHO0)
        run["velX"].append(v * math.cos(th)); run["velY"].append(0.0)
        run["velZ"].append(v * math.sin(th))
        for a in AXES:
            run[a].append(out_fn(t, a) if out_fn else 0.0)
        run["segTag"].append("x")
        for k, fn in (extra or {}).items():
            run[k].append(fn(t, i))
    return run


def selftest():
    mr = {"aircraftGLimit": 9.0, "cornerSpeed": 180.0, "maxPitchAngularVel": 0.75}

    # 1. Multirole1 anchor at V=250 (sea level): the numbers the metric is pinned to.
    oa, turn, pitch = omega_avail(250.0, RHO0, mr)
    assert abs(turn - 20.1) < 0.2, turn
    assert abs(pitch - 43.0) < 0.5, pitch
    assert abs(oa - turn) < 1e-9
    # ...and the density term is a no-op at sea level, so this stays the (V/Vc)^2 formula.
    n = max(1.05, 9.0 * min(1.0, (250.0 / 180.0) ** 2))
    assert abs(math.degrees(G * math.sqrt(n * n - 1) / 250.0) - turn) < 1e-9
    # Below corner the lift limit binds and altitude matters.
    assert omega_avail(90.0, RHO0, mr)[1] < omega_avail(250.0, RHO0, mr)[1]
    assert omega_avail(120.0, 0.85, mr)[1] < omega_avail(120.0, RHO0, mr)[1]

    # 2. Perfect first-order closure at tau -> e == 1 -> A ~ 1. The velocity vector rotates
    # by exactly the error that was closed, so turn_actual == edot (physically coherent).
    tau = TAU_FEEL
    run = synth(lambda t: 3.0 * math.exp(-t / tau),
                turn=lambda t: 3.0 - 3.0 * math.exp(-t / tau), dur=1.0)
    s = summarize(run, score_ticks(run, mr, tau))
    assert s["A"] > 0.95, s["A"]
    c = s["coverage"]
    scored_pct = 100 - c["ON_TARGET"] - c["AIRFRAME_LIMITED"]   # a perfect chase lands in
    assert c["NEAR_OPTIMAL"] > 0.99 * scored_pct, c             # the cone fast, so most of
    assert scored_pct > 5, c                                    # the run is ON_TARGET

    # 3. Standing error, marker moving: SCORED+STALLED, low e, LOW airframe-limited.
    run = synth(lambda t: 9.4, turn=12.0)
    s = summarize(run, score_ticks(run, mr, tau))
    assert s["coverage"]["AIRFRAME_LIMITED"] < 1.0, s["coverage"]
    assert s["coverage"]["STALLED"] > 95, s["coverage"]
    assert abs(s["e_med"]) < 0.05, s["e_med"]
    assert abs(s["A"] - 0.5) < 0.05, s["A"]   # NOTE: 0.5 == "nose stationary", see summarize()

    # 3b. Actively diverging IS the 0 end of the scale (A=0 means "moving away at the full
    # rate the airframe could have been closing at" — here 20 deg/s vs omega_avail 20.1).
    run = synth(lambda t: 5.0 + 20.0 * t, turn=2.0)
    s = summarize(run, score_ticks(run, mr, tau))
    assert s["A"] < 0.2, s["A"]
    assert s["coverage"]["REGRESSING"] > 95, s["coverage"]

    # 4. Saturated max-rate turn -> AIRFRAME_LIMITED, not a law defect.
    run = synth(lambda t: 9.4, turn=0.95 * omega_avail(250.0, RHO0, mr)[0])
    s = summarize(run, score_ticks(run, mr, tau))
    assert s["coverage"]["AIRFRAME_LIMITED"] > 95, s["coverage"]
    assert s["A"] is None, s["A"]             # nothing was scored, so there is no A

    # 5. Inside the cone nothing is scored.
    run = synth(lambda t: 0.4, turn=0.0)
    s = summarize(run, score_ticks(run, mr, tau))
    assert s["coverage"]["ON_TARGET"] > 99, s["coverage"]

    # 6. Churn: same closure, more thrash while stalled -> lower S.
    thrash = lambda t, a: (0.4 if int(t * 15) % 2 else -0.4) if a == "outR" else 0.0
    calm = synth(lambda t: 9.4, 12.0)
    busy = synth(lambda t: 9.4, 12.0, out_fn=thrash)
    sm = smoothness(busy, score_ticks(busy, mr, tau))
    assert sm["rev_per_s"]["outR"] > 5.0, sm
    assert sm["jerk_rms"]["outR"] > smoothness(calm, score_ticks(calm, mr, tau))["jerk_rms"]["outR"]

    # 7. Fail-soft sidecar: unreadable path -> documented defaults, never a crash.
    p = load_airframe("no-such-file.csv")
    assert p["aircraftGLimit"] == 7.0 and p["source"] == "defaults", p

    # 8. airDensity really reaches omega_avail. This shipped broken once: the column was
    # read nowhere, every run silently scored at sea-level density, and nothing complained.
    import tempfile
    hdr = ",".join(NEED + ("airDensity", "segTag"))
    body = "\n".join(",".join(["%g" % v for v in (i / 15.0, 5.0, 250.0, 250, 0, 0, 0, 0, 0)]
                              + ["0.7000", "x"]) for i in range(40))
    with tempfile.TemporaryDirectory() as d:
        fp = os.path.join(d, "t.csv")
        open(fp, "w").write("# comment\n" + hdr + "\n" + body + "\n")
        r = load_run(fp)
    assert len(r["airDensity"]) == len(r["t"]) and r["airDensity"][0] == 0.7, r["airDensity"][:3]
    thin = {"aircraftGLimit": 9.0, "cornerSpeed": 300.0, "maxPitchAngularVel": 0.75}
    assert omega_avail(250, 0.7, thin)[1] < omega_avail(250, RHO0, thin)[1], "density is inert"

    # ---- v0.83 / v0.85 levers ----------------------------------------------------
    CFG = {"fineAng": 6.0, "bankDz": 2.5, "alignHold": 5.0}

    def lev(extra, cfg=CFG, off=9.4, out_fn=None, dur=20.0):
        run = synth((lambda t: off) if not callable(off) else off, 12.0, dur=dur,
                    out_fn=out_fn, extra=extra)
        return run, levers(run, score_ticks(run, mr, tau), cfg)

    # 9. COLUMN ABSENT is the case 162 captures are in: every lever field must be None, not
    # 0.0 — "the gate never opened" and "the gate is not recorded" are different findings.
    run, L = lev(None)
    for k in ("iGate", "iStallPct", "leadFrac", "predFloorPct", "bSup", "bWt", "bWtSd",
              "rBwt", "rBsup", "phiLeadPct", "xfightWt"):
        assert L[k] is None, (k, L[k])
    assert L["rSham"] is None, L                     # needs azErr, which is also optional
    assert L["xfightPct"] == 0.0, L                  # outR/outY exist: measured, and it is 0
    assert set(L) == set(LEVER_KEYS), set(L) ^ set(LEVER_KEYS)
    assert "bWt" not in run and "iGate" not in run, "absent column must leave the key absent"
    # ...and the whole block stays off a pre-v0.83 capture unless --levers forces it.
    assert [k for k in LEVER_COLS if k in run] == []

    # 10. iStallPct is 0.0 BY CONSTRUCTION on the old path (iGate == fineBlend == 0 outside
    # the fine cone) and > 0 only if IntegralStallGate actually fired. off = 9.4 > fineAng 6.
    _, L = lev({"iGate": lambda t, i: 0.0})
    assert L["iStallPct"] == 0.0 and L["iGate"] == 0.0, L
    _, L = lev({"iGate": lambda t, i: 0.55})
    assert abs(L["iStallPct"] - 100.0) < 1e-6 and abs(L["iGate"] - 0.55) < 1e-9, L
    _, L = lev({"iGate": lambda t, i: 0.55}, cfg={})          # no `# config` line
    assert L["iStallPct"] is None and L["iGate"] is not None, L

    # 11. leadFrac + predFloorPct, exact arithmetic. Floor binds iff
    # sign(azErr)*leadDeg > (1-0.30)*|azErr|; 0.8 binds, 0.5 does not, and the sign of azErr
    # must not change the answer (R21 read 0.84 / 100% on a right-hand sweep).
    for sgn in (+1.0, -1.0):
        _, L = lev({"azErr": lambda t, i: sgn * 9.31, "leadDeg": lambda t, i: sgn * 7.85})
        assert abs(L["leadFrac"] - 7.85 / 9.31) < 1e-9, L
        assert abs(L["predFloorPct"] - 100.0) < 1e-6, L
        _, L = lev({"azErr": lambda t, i: sgn * 9.31, "leadDeg": lambda t, i: sgn * 4.6})
        assert abs(L["predFloorPct"]) < 1e-9, L                # 0.49 < 0.70 -> never binds

    # 12. THE HEADLINE. bWt rising with |azErr| == the open loop; bWt suppressed to a
    # constant == the v0.85 pass case, and that must read None (flat), never a fake 0.0.
    ramp = {"azErr": lambda t, i: 2.6 + t / 5.0, "bWt": lambda t, i: t / 20.0}
    _, L = lev(ramp)
    assert L["rBwt"] > 0.99, L["rBwt"]
    assert L["rSham"] is not None and L["rSham"] > 0.99, L["rSham"]  # the definitional twin
    # ...and it rails: once |azErr| > bankDz + alignHold the twin is CONSTANT, so the sham
    # goes None and r(bWt,|azErr|) has nothing to be compared against. Say so, don't fake it.
    _, L = lev({"azErr": lambda t, i: 20.0, "bWt": lambda t, i: t / 20.0})
    assert L["rSham"] is None and L["rBwt"] is None, L
    _, L = lev({"azErr": lambda t, i: 1.0 + t, "bWt": lambda t, i: 0.0})
    assert L["rBwt"] is None and L["bWt"] == 0.0 and L["bWtSd"] == 0.0, L

    # 12b. The verdict, which is the actual pass/fail signal. It must (a) ignore a segment
    # with no belowness — up-hemisphere bWt tracks |azErr| BY DESIGN — (b) ignore a
    # switched-off channel, and (c) not call FAIL on a correlation the sham already explains.
    open_loop = dict(ramp, bSup=lambda t, i: 0.4)
    _, L = lev(open_loop)
    assert loop_verdict({"elDn": L})[2] is True, L                    # coupled + below-nose
    assert loop_verdict({"az90": dict(L, bSup=0.0)}) is None, "up-hemisphere must not qualify"
    assert loop_verdict({"elDn": dict(L, bWt=0.02)}) is None, "dead channel must not qualify"
    assert loop_verdict({"elDn": dict(L, rBwt=0.60, rSham=0.95)})[2] is False, "sham must acquit"
    assert loop_verdict({"= ALL": L}) is None, "the pooled row is never the verdict"

    # 13. rBsup is the DISARM signature: the deleted (1 - lateralHold) factor made the
    # suppressor shrink as the error it was meant to suppress grew.
    _, L = lev({"azErr": lambda t, i: 1.0 + t,
                "bSup": lambda t, i: max(0.0, 1.0 - t / 20.0)})
    assert L["rBsup"] < -0.99, L["rBsup"]
    _, L = lev({"azErr": lambda t, i: 1.0 + t, "bSup": lambda t, i: 0.6})
    assert L["rBsup"] is None and abs(L["bSup"] - 0.6) < 1e-9, L    # flat != uncorrelated

    # 14. phiLead fired / stood down.
    _, L = lev({"phiLead": lambda t, i: 0.0})
    assert L["phiLeadPct"] == 0.0, L
    _, L = lev({"phiLead": lambda t, i: 2.1 if i % 2 else 0.0})
    assert 40.0 < L["phiLeadPct"] < 60.0, L

    # 15. CROSS-FIGHTING, and its control. A 1-tick alternating disagreement is a zero
    # crossing: it must show in xfightPct and be REJECTED by xfightSusPct. A sustained one
    # must survive both. This is the whole reason xfightSusPct exists.
    flick = lambda t, a: 0.5 if a == "outR" else (-0.5 if (a == "outY" and int(t * 15) % 2) else 0.5)
    _, L = lev(None, out_fn=flick)
    assert L["xfightPct"] > 30.0 and L["xfightSusPct"] == 0.0, L
    hold = lambda t, a: 0.5 if a == "outR" else -0.5
    _, L = lev(None, out_fn=hold)
    assert L["xfightPct"] > 99.0 and L["xfightSusPct"] > 99.0, L
    # ...and the deadband still applies: sub-deadband noise is not a fight.
    tiny = lambda t, a: 0.5 * STICK_DEADBAND * (1 if a == "outR" else -1)
    _, L = lev(None, out_fn=tiny)
    assert L["xfightPct"] == 0.0, L

    # 16. xfightWt: the roll channel claiming the azimuth error while opposing yaw. Positive
    # is the direction common cause CANNOT produce (crossings sit at small |azErr|, i.e. small
    # bWt), so the sign is the finding.
    _, L = lev({"bWt": lambda t, i: 0.9 if int(t * 15) % 2 else 0.1}, out_fn=flick)
    assert L["xfightWt"] is not None and abs(abs(L["xfightWt"]) - 0.8) < 0.05, L["xfightWt"]
    _, L = lev({"bWt": lambda t, i: 0.5}, out_fn=hold)
    assert L["xfightWt"] is None, L        # no non-fighting population to contrast against

    # 17. End to end through a real file: an OLD capture gets no `levers` key at all (this is
    # the byte-identical guarantee), a NEW one gets it, and --levers forces it on the old one.
    hdr = ",".join(NEED + ("airDensity", "segTag"))
    row = lambda i, tail="": (",".join("%g" % v for v in
                              (i / 15.0, 9.4, 250.0, 250, 0, 0, 0, 0.5, -0.5))
                              + ",1.225,x" + tail)
    with tempfile.TemporaryDirectory() as d:
        old = os.path.join(d, "old.csv")
        open(old, "w").write(hdr + "\n" + "\n".join(row(i) for i in range(60)) + "\n")
        assert "levers" not in score_file(old, tau), "old capture must not grow a levers key"
        assert "levers" in score_file(old, tau, force_levers=True)
        new = os.path.join(d, "new.csv")
        open(new, "w").write("# config fineAng=6 bankDz=2.5 alignHold=5.0\n" + hdr + ",bWt\n"
                             + "\n".join(row(i, ",0.43") for i in range(60)) + "\n")
        r = score_file(new, tau)
        L = r["levers"]["x"]
        assert abs(L["bWt"] - 0.43) < 1e-9 and L["iGate"] is None, L
        assert load_cfg(new)["fineAng"] == 6.0 and load_cfg(old) == {}, load_cfg(new)

    # 18. spread() SPLITS BY AIRFRAME. It used to print "Not pooling." and then pool: a trainer's A
    # and a jet's A landed in one min/med/max band, so the "noise floor" was really the airframe
    # difference — the exact mistake the warning names. One block each, and neither block's median
    # may be the pooled one (0.60), which is the only assertion that can tell the two apart.
    import io, contextlib
    mk = lambda name, a: {"file": f"{name}-{a}.csv", "airframe": {"name": name},
                          "segments": {"turn360": {"A": a}}, "all": {"A": a}}
    mixed = [mk("Multirole1", 0.80), mk("trainer", 0.40), mk("Multirole1", 0.82), mk("trainer", 0.38)]
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        spread(mixed)
    out = buf.getvalue()
    assert out.count("=== spread across") == 2, out                 # one block per airframe
    assert "spread across 2 runs of Multirole1" in out, out         # ...each with its OWN n, not 4
    assert "spread across 2 runs of trainer" in out, out
    assert "Not pooling" in out, out
    blocks = out.split("=== spread across")[1:]
    jet = next(b for b in blocks if b.startswith(" 2 runs of Multirole1"))
    assert "med=0.810" in jet and "med=0.600" not in out, out       # 0.60 == the pooled median
    assert "0.400" not in jet and "0.380" not in jet, jet           # the trainer's runs stayed out
    # ...and the single-airframe case still prints exactly one block (the common case, unchanged).
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        spread([mk("Multirole1", 0.80), mk("Multirole1", 0.82)])
    assert buf.getvalue().count("=== spread across") == 1 and "Not pooling" not in buf.getvalue()

    print("flightscore selftest OK")


# ---- cli -------------------------------------------------------------------------
def main(argv):
    if "--selftest" in argv:
        return selftest()
    opt = {"--tau": TAU_FEEL, "--cone": ON_TARGET_DEG}
    for k in list(opt):
        if k in argv:
            i = argv.index(k)
            opt[k] = float(argv[i + 1])
            del argv[i:i + 2]
    tau, cone = opt["--tau"], opt["--cone"]
    as_json = "--json" in argv
    verbose = "--verbose" in argv
    force_levers = "--levers" in argv   # xfight*/ needs no new column, so it IS scoreable on
                                        # the old corpus — just never by default, so old
                                        # captures keep their exact pre-feature output.
    paths = [a for a in argv if not a.startswith("--")]
    if not paths:
        return print(__doc__.strip())
    results = [score_file(p, tau, cone, force_levers) for p in paths]
    if as_json:
        print(json.dumps({"tau_feel": tau, "cone_deg": cone, "runs": results}, indent=1, default=str))
        return
    if verbose or len(paths) <= DETAIL_FILE_LIMIT:
        for r in results:
            report(r)
    else:
        print(f"{len(paths)} file(s) scored; per-file report suppressed (over DETAIL_FILE_LIMIT="
              f"{DETAIL_FILE_LIMIT}) -- re-run with --verbose, or on a subset, to see it.")
    if len(results) > 1:
        spread(results)


if __name__ == "__main__":
    main(sys.argv[1:])
