#!/usr/bin/env python3
"""gatechatter.py — does REGRESSING tick density spike at ChaseController gate boundaries?

CLOSED INVESTIGATION, kept for reproduction only — do not reach for this to score a batch. The
hypothesis below was answered in v0.85 (the below-nose roll-to-align positive feedback loop, see
`debugtests/GATE-CHATTER-FINDINGS.md` §5a) and fixed behind `BelowAlignSuppress`/`AlignRateLead`.
Its durable half — the cross-fight measurement — was folded into flightscore.py's lever block as
`xfightPct` (off the shared `flightscore.opposed()`), which is what a routine batch should use;
this tool survives so §5a's numbers can be regenerated from the same captures.

Tests the hypothesis at `LAW-LEDGER.md` **X9**: "Apply allocates the pointing error across roll/yaw/pitch
through several independent gates and blends, each deciding 'am I active' per tick with no
hysteresis; independent thresholds chatter at their boundaries and the chatter presents as
commands fighting each other."

Four measurements, on the existing captures only — no new flying:

  1. CHATTER     per gate per segment: rail dwell and rail-state crossings/second. A gate
                 that crosses many times/s in micro*/elDn and sits pinned through turn360
                 is the signature the hypothesis predicts.
  2. COINCIDENCE REGRESSING density within +/-win of a crossing vs away from one, as a
                 Mantel-Haenszel risk ratio STRATIFIED by (run x segment block). Stratifying
                 is not decoration: R21 shows replicates drift with run index (r=-0.82), so
                 a pooled 2x2 would mix a between-run baseline shift into the effect. The
                 null is a per-stratum CIRCULAR SHIFT of the outcome series, which preserves
                 both series' autocorrelation and marginals and destroys only their
                 alignment. A tick-level chi-square would call everything significant: at
                 15 Hz neighbouring ticks are not independent.
  3. CONTRADICTION  ticks where two gates assert incompatible allocations, and (as a
                 condition in the same stratified test) ticks where roll and yaw command
                 OPPOSITE azimuth corrections — the literal form of "should I roll or yaw".
  4. LEAD/LAG    cross-correlation of gate crossings against deadbanded outR/outY sign flips
                 over +/-0.5 s. A peak at positive lag = crossings LEAD flips = the gates
                 chatter and the stick follows. A peak at negative lag = the gates are only
                 reporting a stick that chatters for some other reason.

    python debugtests/gatechatter.py <rec.csv> [...] [--win 0.20] [--cone 0.2] [--json]
                                     [--perm 399] [--skip 0.0] [--bytag]
    python debugtests/gatechatter.py --selftest

Reads the recorder CSV + its `# config` line only. Imports flightscore.py read-only for the
tick classifier, so the two tools cannot disagree about what REGRESSING means. Stdlib only.
Never writes anything.
"""

import csv
import json
import math
import os
import re
import statistics as st
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flightscore as fs  # read-only: score_ticks / load_airframe / STICK_DEADBAND / BINS

WIN_S = 0.20        # +/- half-width of the "near a crossing" window, seconds. NOTE the tick
                    # classifier itself uses a +/-0.20 s slope window, so a REGRESSING tick is
                    # already smeared by that much; the effective resolution is ~+/-0.4 s and
                    # this test cannot localize a crossing effect tighter than that.
CONE_DEG = 0.2      # flightscore --cone, PINNED on purpose. At the old fixed 1.0 the micro*/fine
                    # segments read 100% ON_TARGET, i.e. the sub-degree region this hypothesis is
                    # about was unscoreable -- this tool has always overridden it, and flightscore's
                    # default now derives the cone per segment instead (see its ON_TARGET_DEG block
                    # and `LAW-CHARACTERIZATION.md` §6). Kept PINNED rather than switched to auto so
                    # §5a's published numbers stay regenerable from the same captures; auto would
                    # give each segment a different cone and silently move them.
RAIL_EPS = 1e-3     # a blend within this of 0 or 1 IS at the rail
PERM = 399          # circular shifts for the null (p resolution 1/400)

# Cfg defaults, used only when a capture predates a knob or has no `# config` line. Same
# fail-soft convention as the mod's own probes: a documented number, never a crash.
CFG_DEFAULTS = {"fineAng": 6.0, "align": 25.0, "bankDz": 2.5, "alignHold": 5.0,
                "pullRel": 2.0, "maxBank": 72.0}

RECORDED_GATES = ("bigTurn", "bankBlend", "assist", "qSched", "aoaGU", "aoaGD", "settleOn")
NEED = ("t", "off", "azErr", "phi", "outR", "outY")


# ---- io --------------------------------------------------------------------------
def load(path):
    """(run, cfg). run holds every column as floats where parseable, plus the string ones."""
    cfg, lines = dict(CFG_DEFAULTS), []
    with open(path) as fh:
        for l in fh:
            if l.startswith("#"):
                if l.startswith("# config"):
                    for kv in l[8:].split():
                        k, _, v = kv.partition("=")
                        if k in cfg:
                            try:
                                cfg[k] = float(v)
                            except ValueError:
                                pass          # keep the documented default
            else:
                lines.append(l)
    rd = csv.DictReader(lines)
    cols = rd.fieldnames or []
    run = {c: [] for c in cols}
    for r in rd:
        if any(r.get(c) is None for c in cols):
            continue                          # torn last row on a crashed capture
        # `phase` and `controlLaw` are enum STRINGS mid-row. float()-ing every column drops
        # every row (this tool read 0 rows the first time). Keep whatever type parses; only
        # the NEED columns have to be numeric.
        vals = {}
        for c in cols:
            try:
                vals[c] = float(r[c])
            except (TypeError, ValueError):
                vals[c] = (r[c] or "").strip()
        if any(not isinstance(vals.get(c), float) for c in NEED if c in cols):
            continue
        for c in cols:
            run[c].append(vals[c])
    return run, cfg


# ---- the gates -------------------------------------------------------------------
def clamp01(x):
    return 0.0 if x < 0.0 else 1.0 if x > 1.0 else x


def gates(run, cfg):
    """Every gate/blend in the roll-yaw-pitch allocation, per tick.

    Recorded ones are read straight out. The rest are recomputed from the SAME expressions
    in ChaseController.ApplyEvolvedLegacy — exactly, because every input (azErr / off / phi /
    heliBlend / azErrPred) is a recorded column and every constant is either on the
    `# config` line or a documented regime const in the source.
    """
    n = len(run["t"])
    g = {k: [float(v) for v in run[k]] for k in RECORDED_GATES if k in run}
    az, off, phi = run["azErr"], run["off"], run["phi"]
    heli = run.get("heliBlend") or [0.0] * n
    pred = run.get("azErrPred") or [0.0] * n

    #   lateralHold = clamp01(max(0,|azErr|-FineBankDeadzone) / EvolvedAlignHoldDeg)
    g["lateralHold"] = [clamp01(max(0.0, abs(a) - cfg["bankDz"]) / max(0.01, cfg["alignHold"]))
                        for a in az]
    #   fineBlend = clamp01(1 - off/FineAngle)            (gates _iPitch/_iYaw and fineGain)
    g["fineBlend"] = [clamp01(1.0 - o / max(1.0, cfg["fineAng"])) for o in off]
    #   blendWeight = max(bigTurn,lateralHold)*(1-heliBlend)*(1-belowSuppress);
    #   alignFrac == cos(phi) since phi = atan2(local.x, local.y) and alignFrac = local.y/lateral
    bt = g.get("bigTurn") or [clamp01((o - cfg["fineAng"]) / max(1.0, cfg["align"] - cfg["fineAng"]))
                              for o in off]
    bw = []
    for i in range(n):
        below = (clamp01(-math.cos(math.radians(phi[i]))) * (1.0 - g["lateralHold"][i])
                 * clamp01((1.0 - bt[i]) / 0.3))          # downAlignTaper = 0.3
        bw.append(max(bt[i], g["lateralHold"][i]) * (1.0 - heli[i]) * (1.0 - below))
    g["blendWeight"] = bw
    #   azTR presence ramp = clamp01((|azErr|-0.5)/1.5)   (v0.67 settle-exit ramp)
    g["azRamp"] = [clamp01((abs(a) - 0.5) / 1.5) for a in az]
    #   predFloor: azErrPred pinned at 0.30*azErr -> binding (1) or not (0)
    g["predFloor"] = [1.0 if abs(a) > 0.05 and abs(p) <= 0.3005 * abs(a) else 0.0
                      for a, p in zip(az, pred)]
    #   eAlign conditioning gate: lateral = sin(off) > eAlignLatGate 0.10
    g["eAlignLat"] = [1.0 if math.sin(math.radians(min(o, 90.0))) > 0.10 else 0.0 for o in off]
    #   phi wrap region: |phi| > 135 swaps the eAlign slew rate 30/s <-> 3/s
    g["phiWrap"] = [1.0 if abs(p) > 135.0 else 0.0 for p in phi]
    #   coordPull release taper
    g["pullTaper"] = [clamp01(abs(a) / max(0.5, cfg["pullRel"])) for a in az]

    # SHAM GATES — the placebo arm, and the whole reason this tool can return a negative.
    # Every real gate above is a threshold on |azErr| or on `off`, and BOTH of those cross a
    # small value exactly when the nose passes through the target — which is also exactly
    # when a P-loop overshoot reads as REGRESSING. Gate crossing and regression would then
    # share a common cause and the coincidence would prove nothing about the gate.
    # These shams have the same functional form at thresholds that appear NOWHERE in
    # ChaseController (0.35/1.1/3.3 deg of azErr, 2.7 deg of off). If a sham scores like the
    # real gate beside it, the association is with the error crossing, not with the mod's
    # boundary, and the chatter hypothesis is not what the data is showing.
    for thr in (0.35, 1.1, 3.3):
        g[f"shamAz{thr}"] = [1.0 if abs(a) > thr else 0.0 for a in az]
    g["shamOff2.7"] = [1.0 if o > 2.7 else 0.0 for o in off]
    return g


def rail_state(v):
    """0 = pinned low, 1 = in transit, 2 = pinned high. A gate's identity is which rail it is
    on, so a crossing = a change of rail. Covers a binary flip AND a blend entering/leaving
    saturation, which is what "no hysteresis at the boundary" would produce."""
    return 0 if v <= RAIL_EPS else 2 if v >= 1.0 - RAIL_EPS else 1


def crossings(vals):
    s = [rail_state(v) for v in vals]
    return [i for i in range(1, len(s)) if s[i] != s[i - 1]], s


def near_mask(n, cross, half):
    m = [False] * n
    for c in cross:
        for j in range(max(0, c - half), min(n, c + half + 1)):
            m[j] = True
    return m


def reversal_events(run):
    """Deadbanded outR/outY sign flips, per flightscore's STICK_DEADBAND convention."""
    n = len(run["t"])
    ev = [0.0] * n
    for a in ("outR", "outY"):
        s, prev = run[a], 0.0
        for i in range(1, n):
            if abs(s[i]) > fs.STICK_DEADBAND:
                if prev != 0.0 and (s[i] > 0) != (prev > 0):
                    ev[i] = 1.0
                prev = s[i]
    return ev


# ---- stratified statistics -------------------------------------------------------
def mh_rr(strata):
    """Mantel-Haenszel risk ratio over strata of (k_near, n_near, k_far, n_far).

    Conditions on the stratum, so a run whose baseline REGRESSING rate is shifted (run-index
    drift) contributes its own contrast rather than its level. Returns (RR, p_near, p_far).
    """
    num = den = kn = nn = kf = nf = 0.0
    for a, n1, c, n0 in strata:
        N = n1 + n0
        if N <= 0 or n1 <= 0 or n0 <= 0:
            continue
        num += a * n0 / N
        den += c * n1 / N
        kn += a; nn += n1; kf += c; nf += n0
    if nn <= 0 or nf <= 0:
        return None, None, None
    pn, pf = kn / nn, kf / nf
    if den <= 0:
        return (None if num <= 0 else float("inf")), pn, pf
    return num / den, pn, pf


def build_strata(blocks, mask_of):
    out = []
    for b in blocks:
        m = mask_of(b)
        if m is None:
            continue
        reg = b["reg"]
        a = sum(r for r, x in zip(reg, m) if x)
        c = sum(r for r, x in zip(reg, m) if not x)
        n1 = sum(1 for x in m if x)
        out.append((a, n1, c, len(m) - n1))
    return out


def perm_p_strat(blocks, masks, nperm=PERM):
    """One-sided p for RR_MH > 1 under an INDEPENDENT circular shift of each stratum's
    outcome series. Deterministic offsets, so the p is reproducible without a seed."""
    obs = mh_rr([(a, n1, c, n0) for a, n1, c, n0 in
                 [(sum(r for r, x in zip(b["reg"], m) if x), sum(1 for x in m if x),
                   sum(r for r, x in zip(b["reg"], m) if not x), sum(1 for x in m if not x))
                  for b, m in zip(blocks, masks)]])[0]
    if obs is None or obs == float("inf"):
        return None
    ge = 0
    for p in range(nperm):
        strata = []
        for i, (b, m) in enumerate(zip(blocks, masks)):
            reg, n = b["reg"], len(b["reg"])
            s = ((p + 1) * max(1, n // (nperm + 1)) + 17 * i) % n
            rr_ = reg[s:] + reg[:s]
            a = sum(r for r, x in zip(rr_, m) if x)
            n1 = sum(1 for x in m if x)
            strata.append((a, n1, sum(rr_) - a, n - n1))
        v = mh_rr(strata)[0]
        if v is not None and v >= obs:
            ge += 1
    return (ge + 1) / (nperm + 1)


def xcorr(blocks, kmax):
    """P(stick reversal | k ticks after a gate crossing) / base reversal rate, pooled."""
    hits = {k: [0.0, 0] for k in range(-kmax, kmax + 1)}
    tot_ev = tot_n = 0
    for b in blocks:
        ev, n = b["ev"], len(b["ev"])
        tot_ev += sum(ev); tot_n += n
        for c in b["anyCross"]:
            for k in range(-kmax, kmax + 1):
                if 0 <= c + k < n:
                    hits[k][0] += ev[c + k]
                    hits[k][1] += 1
    base = tot_ev / tot_n if tot_n else 0.0
    if base <= 0:
        return {}
    return {k: (v[0] / v[1]) / base for k, v in hits.items() if v[1] >= 20}


# ---- per-file analysis -----------------------------------------------------------
def norm_tag(tag, keep=False):
    """micro1..micro10 are ten 2.1 s blocks of the SAME test; group them (each block stays
    its own stratum, so nothing is concatenated across a segment boundary). --bytag keeps
    them apart, which is how you see REGRESSING track the STEP SIZE (0.2..1.0 deg) rather
    than the gate activity."""
    return tag if keep else re.sub(r"^(micro)\d+$", r"\1", tag)


def blocks_of(run):
    tags, out, start = run["segTag"], [], 0
    for i in range(1, len(tags) + 1):
        if i == len(tags) or tags[i] != tags[start]:
            out.append((tags[start] or "", start, i))
            start = i
    return out


def analyse(path, win=WIN_S, cone=CONE_DEG, skip=0.0, bytag=False):
    """skip = seconds to drop from the START of every block. THE control for this whole
    test: a card step makes the gates cross AND makes the nose regress, both because of the
    step, so a raw coincidence is confounded by the transient. Re-running with skip >= 1 s
    asks whether the association survives once the common cause is gone."""
    run, cfg = load(path)
    n = len(run["t"])
    if n < 30:
        return []
    g = gates(run, cfg)
    cls = {t["i"]: t["cls"] for t in fs.score_ticks(run, fs.load_airframe(path), fs.TAU_FEEL, cone)}
    revs = reversal_events(run)
    dts = [run["t"][i + 1] - run["t"][i] for i in range(n - 1)]
    dt = st.median(dts) or 1 / 15.0
    half = max(1, int(round(win / dt)))
    fname = os.path.basename(path)
    out = []
    for tag, lo, hi in blocks_of(run):
        lo += int(round(skip / dt))
        m = hi - lo
        # 12 samples ~= 0.8 s at 15 Hz. Low on purpose: the micro* blocks are 2.1 s, so a 1 s
        # skip leaves 17 samples and a higher floor would silently delete the ten segments the
        # whole hypothesis is about. Power comes from 110 strata, not from long blocks.
        if m < 12 or not tag:
            continue
        dur = run["t"][hi - 1] - run["t"][lo]
        b = {"file": fname, "tag": norm_tag(tag, bytag), "raw": tag, "n": m, "dur": dur, "dt": dt,
             "half": half,
             "off": [run["off"][i] for i in range(lo, hi)],
             "reg": [1.0 if cls.get(i) == "REGRESSING" else 0.0 for i in range(lo, hi)],
             "scored": [1.0 if cls.get(i) in fs.BINS else 0.0 for i in range(lo, hi)],
             "ev": revs[lo:hi], "cross": {}, "state": {}}
        allc = set()
        for name, vals in g.items():
            c, s = crossings(vals[lo:hi])
            b["cross"][name] = c
            b["state"][name] = s
            allc |= set(c)
        b["anyCross"] = sorted(allc)
        # conditions that are STATES, not crossings: two gates asserting incompatible things
        b["cond"] = {
            # off < FineAngle (fine-aim regime; integrators armed, fineGain boosted) while the
            # roll loop has been handed entirely to eAlign because |azErr| railed lateralHold
            "fineVsAlign": [1.0 if g["fineBlend"][i] > 0 and g["lateralHold"][i] >= 1 - RAIL_EPS
                            else 0.0 for i in range(lo, hi)],
            # roll fully committed to align while the turn-rate demand is still ramping in
            "alignVsAzRamp": [1.0 if g["blendWeight"][i] >= 1 - RAIL_EPS and 0 < g["azRamp"][i] < 1
                              else 0.0 for i in range(lo, hi)],
            # roll and yaw commanding OPPOSITE azimuth corrections, both out of deadband —
            # flightscore.opposed() is the one definition, shared so xfightPct and this agree
            "rollYawAnti": [1.0 if fs.opposed(run["outR"][i], run["outY"][i]) else 0.0
                            for i in range(lo, hi)],
        }
        out.append(b)
    return out


# ---- aggregation -----------------------------------------------------------------
def summarize(blocks, nperm=PERM):
    """Per normalized tag: descriptive chatter stats + the stratified coincidence tests."""
    tags = list(dict.fromkeys(b["tag"] for b in blocks))
    agg = {}
    for tag in tags:
        bs = [b for b in blocks if b["tag"] == tag]
        dur = sum(b["dur"] for b in bs)
        ntot = sum(b["n"] for b in bs)
        a = {"blocks": len(bs), "runs": len({b["file"] for b in bs}), "durS": dur,
             "regPct": 100.0 * sum(sum(b["reg"]) for b in bs) / ntot,
             "offMax": st.median([max(b["off"]) for b in bs]),
             "scoredPct": 100.0 * sum(sum(b["scored"]) for b in bs) / ntot,
             "revPerS": sum(sum(b["ev"]) for b in bs) / dur if dur else None,
             "gates": {}, "cond": {}}
        for name in dict.fromkeys(k for b in bs for k in b["cross"]):
            sel = [b for b in bs if name in b["cross"]]
            nc = sum(len(b["cross"][name]) for b in sel)
            sd = sum(b["dur"] for b in sel)
            st_ = [b["state"][name] for b in sel]
            tot = sum(len(s) for s in st_)
            gd = {"crossPerS": nc / sd if sd else None,
                  "lowPct": 100.0 * sum(s.count(0) for s in st_) / tot,
                  "midPct": 100.0 * sum(s.count(1) for s in st_) / tot,
                  "highPct": 100.0 * sum(s.count(2) for s in st_) / tot,
                  "meanDwellS": sd / (nc + len(sel))}
            masks = [near_mask(b["n"], b["cross"][name], b["half"]) for b in sel]
            usable = [(b, m) for b, m in zip(sel, masks) if 0 < sum(m) < b["n"]]
            if usable:
                ub, um = [x[0] for x in usable], [x[1] for x in usable]
                rr, pn, pf = mh_rr(build_strata(ub, lambda b, _it=iter(um): next(_it)))
                gd.update({"rr": rr, "pNear": pn, "pFar": pf,
                           "p": perm_p_strat(ub, um, nperm) if rr not in (None, float("inf")) else None,
                           "nStrata": len(ub)})
            a["gates"][name] = gd
        # union-of-all-gates, plus the state conditions, through the same stratified test
        conds = {"anyGate": [near_mask(b["n"], b["anyCross"], b["half"]) for b in bs]}
        for cname in bs[0]["cond"]:
            conds[cname] = [[x > 0 for x in b["cond"][cname]] for b in bs]
        for cname, masks in conds.items():
            usable = [(b, m) for b, m in zip(bs, masks) if 0 < sum(m) < b["n"]]
            occ = 100.0 * sum(sum(m) for m in masks) / ntot
            d = {"occPct": occ, "nStrata": len(usable)}
            if cname == "anyGate":
                d["crossPerS"] = sum(len(b["anyCross"]) for b in bs) / dur if dur else None
            if usable:
                ub, um = [x[0] for x in usable], [x[1] for x in usable]
                rr, pn, pf = mh_rr(build_strata(ub, lambda b, _it=iter(um): next(_it)))
                d.update({"rr": rr, "pNear": pn, "pFar": pf,
                          "p": perm_p_strat(ub, um, nperm) if rr not in (None, float("inf")) else None})
            a["cond"][cname] = d
        dt = st.median([b["dt"] for b in bs])
        xc = xcorr(bs, max(1, int(round(0.5 / dt))))
        if xc:
            pk = max(xc.items(), key=lambda kv: kv[1])
            a["revLagS"], a["revPeakRatio"], a["revAt0"] = pk[0] * dt, pk[1], xc.get(0)
            a["revProfile"] = {round(k * dt, 3): round(v, 2) for k, v in sorted(xc.items())}
        agg[tag] = a
    return agg


# ---- report ----------------------------------------------------------------------
def num(v, w=6, d=2):
    if v is None:
        return " " * (w - 1) + "-"
    if isinstance(v, float) and math.isinf(v):
        return " " * (w - 3) + "inf"
    return f"{v:{w}.{d}f}"


def report(agg, order=None):
    tags = order or sorted(agg)
    names = list(dict.fromkeys(n for t in tags for n in agg[t]["gates"]))
    print("\n=== 1. CHATTER — gate rail-state crossings/s (pooled over blocks) ===")
    print(f"{'segment':<9}{'blk':>4}{'sec':>7}{'offMax':>7}{'reg%':>6}{'rev/s':>6} " +
          "".join(f"{n[:8]:>9}" for n in names))
    for t in tags:
        a = agg[t]
        print(f"{t:<9}{a['blocks']:>4}{a['durS']:>7.0f}{a['offMax']:>7.2f}"
              f"{a['regPct']:>6.1f}{num(a['revPerS'],6,2)} " +
              "".join(num(a["gates"].get(n, {}).get("crossPerS"), 9, 2) for n in names))

    print("\n=== 2. COINCIDENCE — REGRESSING near a gate crossing vs away (MH, stratified) ===")
    print(f"{'segment':<9}{'x/s':>6}{'near%':>7}{'pNear':>8}{'pFar':>8}{'RR':>7}{'perm p':>8}"
          f"{'strata':>7}")
    for t in tags:
        c = agg[t]["cond"].get("anyGate", {})
        print(f"{t:<9}{num(c.get('crossPerS'),6,2)}{num(c.get('occPct'),7,1)}"
              f"{num(c.get('pNear'),8,4)}{num(c.get('pFar'),8,4)}{num(c.get('rr'),7,2)}"
              f"{num(c.get('p'),8,3)}{c.get('nStrata',0):>7}")

    print("\n=== 3. CONTRADICTION — incompatible allocations, and REGRESSING risk there ===")
    for cname in ("fineVsAlign", "alignVsAzRamp", "rollYawAnti"):
        print(f"  -- {cname}")
        print(f"  {'segment':<9}{'occ%':>7}{'pIn':>8}{'pOut':>8}{'RR':>7}{'perm p':>8}")
        for t in tags:
            c = agg[t]["cond"].get(cname, {})
            if not c.get("occPct"):
                continue
            print(f"  {t:<9}{num(c.get('occPct'),7,1)}{num(c.get('pNear'),8,4)}"
                  f"{num(c.get('pFar'),8,4)}{num(c.get('rr'),7,2)}{num(c.get('p'),8,3)}")

    print("\n=== 4. LEAD/LAG — stick reversal rate vs gate crossing (ratio to base rate) ===")
    print(f"{'segment':<9}{'peak lag s':>11}{'peak':>7}{'at 0':>7}")
    for t in tags:
        a = agg[t]
        if "revLagS" not in a:
            continue
        print(f"{t:<9}{a['revLagS']:>11.3f}{num(a['revPeakRatio'],7,2)}{num(a.get('revAt0'),7,2)}")

    print("\n=== per-gate RR (REGRESSING near THAT gate's crossings), MH stratified ===")
    print(f"{'segment':<9}" + "".join(f"{n[:8]:>9}" for n in names))
    for t in tags:
        print(f"{t:<9}" + "".join(num(agg[t]["gates"].get(n, {}).get("rr"), 9, 2) for n in names))


# ---- selftest --------------------------------------------------------------------
def selftest():
    # rail_state / crossings: a blend leaving and re-entering saturation IS two crossings.
    assert [rail_state(v) for v in (0.0, 1e-4, 0.5, 1.0, 0.9999)] == [0, 0, 1, 2, 2]
    assert crossings([0, 0, 0.5, 1, 1, 0.5, 0, 0])[0] == [2, 3, 5, 6]
    assert crossings([1.0] * 10)[0] == []              # a pinned gate never crosses
    assert near_mask(10, [5], 2) == [False] * 3 + [True] * 5 + [False] * 2

    # mh_rr: one stratum reduces to a plain risk ratio...
    assert abs(mh_rr([(4, 10, 2, 20)])[0] - (0.4 / 0.1)) < 1e-12
    # ...and stratification removes a pure baseline shift between blocks (both blocks have
    # RR 2 at very different base rates; the pooled 2x2 would not return 2).
    rr = mh_rr([(20, 100, 10, 100), (4, 100, 2, 100)])[0]
    assert abs(rr - 2.0) < 1e-9, rr

    # perm null on aperiodic crossings: aligned outcome is significant, flat is not.
    # (Crossings are deliberately aperiodic — a periodic train re-aligns with itself under a
    # circular shift and caps the achievable p.)
    m = near_mask(120, [11, 47, 92], 2)
    blk = lambda reg: [{"reg": reg, "n": len(reg)}]
    # a sparse off-mask background keeps p_far > 0, so RR is finite and testable
    hot = [1.0 if x else (1.0 if i % 29 == 0 else 0.0) for i, x in enumerate(m)]
    assert mh_rr(build_strata(blk(hot), lambda b: m))[0] > 5
    assert perm_p_strat(blk(hot), [m], 199) <= 0.01, perm_p_strat(blk(hot), [m], 199)
    # an outcome that NEVER happens away from a crossing gives RR=inf, and the permutation
    # p is reported as None rather than a fabricated number.
    always = [1.0 if x else 0.0 for x in m]
    assert mh_rr(build_strata(blk(always), lambda b: m))[0] == float("inf")
    assert perm_p_strat(blk(always), [m], 199) is None
    flat = [1.0] * 120
    assert perm_p_strat(blk(flat), [m], 199) > 0.5
    offp = [1.0 if (i % 40) == 20 else 0.0 for i in range(120)]   # deliberately out of phase
    assert perm_p_strat(blk(offp), [m], 199) > 0.05

    # xcorr recovers a known lag: reversal always 3 ticks AFTER each crossing
    ev = [0.0] * 400
    cr = [i for i in range(20, 380, 17)]
    for c in cr:
        ev[c + 3] = 1.0
    x = xcorr([{"ev": ev, "anyCross": cr}], 8)
    assert max(x.items(), key=lambda kv: kv[1])[0] == 3, x

    # gates(): reconstructions match ChaseController's expressions at hand-checked points
    n = 4
    run = {"t": [i / 15 for i in range(n)],
           "azErr": [0.0, 2.5, 7.5, 12.0], "off": [0.0, 3.0, 6.0, 30.0],
           "phi": [0.0, 0.0, 180.0, 90.0], "azErrPred": [0.0, 0.75, 2.25, 3.6],
           "outR": [0] * n, "outY": [0] * n, "settleOn": [1, 0, 0, 0],
           "bigTurn": [0.0, 0.0, 0.0, 1.0]}
    g = gates(run, dict(CFG_DEFAULTS))
    assert g["lateralHold"] == [0.0, 0.0, 1.0, 1.0], g["lateralHold"]   # (|az|-2.5)/5
    assert g["fineBlend"] == [1.0, 0.5, 0.0, 0.0], g["fineBlend"]        # 1-off/6
    assert g["predFloor"] == [0.0, 1.0, 1.0, 1.0], g["predFloor"]        # pred == 0.30*az
    assert g["azRamp"] == [0.0, 1.0, 1.0, 1.0], g["azRamp"]              # (|az|-0.5)/1.5
    assert g["eAlignLat"] == [0.0, 0.0, 1.0, 1.0], g["eAlignLat"]        # sin(off) > 0.10
    assert g["phiWrap"] == [0.0, 0.0, 1.0, 0.0], g["phiWrap"]
    assert abs(g["blendWeight"][1]) < 1e-9 and abs(g["blendWeight"][3] - 1.0) < 1e-9
    # belowSuppress bites only for a BELOW target (phi->180) with lateralHold low
    run2 = dict(run, phi=[180.0] * n, azErr=[0.0] * n, azErrPred=[0.0] * n,
                bigTurn=[0.0] * n, off=[3.0] * n)
    assert gates(run2, dict(CFG_DEFAULTS))["blendWeight"][0] == 0.0

    # loader: enum string columns must not drop rows, and cfg parsing is fail-soft
    import tempfile
    hdr = "t,off,azErr,phi,outR,outY,phase,segTag"
    body = "\n".join(f"{i/15:.4f},1,1,0,0,0,Hold,x" for i in range(40))
    with tempfile.TemporaryDirectory() as d:
        fp = os.path.join(d, "t.csv")
        open(fp, "w").write("# config law=EvolvedLegacy fineAng=9 bogus=zz\n" + hdr + "\n" + body + "\n")
        r, cfg = load(fp)
    assert len(r["t"]) == 40, len(r["t"])
    assert cfg["fineAng"] == 9.0 and cfg["alignHold"] == 5.0, cfg

    # reversal_events honours the deadband
    tiny = {"t": [i / 15 for i in range(20)],
            "outR": [0.01 * (1 if i % 2 else -1) for i in range(20)], "outY": [0.0] * 20}
    assert sum(reversal_events(tiny)) == 0
    assert sum(reversal_events(dict(tiny, outR=[0.4 * (1 if i % 2 else -1) for i in range(20)]))) >= 17

    assert norm_tag("micro10") == "micro" and norm_tag("az10") == "az10"
    print("gatechatter selftest OK")


# ---- cli -------------------------------------------------------------------------
def main(argv):
    if "--selftest" in argv:
        return selftest()
    opt = {"--win": WIN_S, "--cone": CONE_DEG, "--perm": float(PERM), "--skip": 0.0}
    bytag = "--bytag" in argv
    for k in list(opt):
        if k in argv:
            i = argv.index(k)
            opt[k] = float(argv[i + 1])
            del argv[i:i + 2]
    as_json = "--json" in argv
    paths = [a for a in argv if not a.startswith("--")]
    if not paths:
        return print(__doc__.strip())
    blocks = [b for p in paths
              for b in analyse(p, opt["--win"], opt["--cone"], opt["--skip"], bytag)]
    if not blocks:
        return print("no tagged, scoreable segments in those captures")
    agg = summarize(blocks, int(opt["--perm"]))
    if as_json:
        print(json.dumps({"win_s": opt["--win"], "cone_deg": opt["--cone"],
                          "files": sorted({b["file"] for b in blocks}), "agg": agg},
                         indent=1, default=str))
        return
    print(f"{len({b['file'] for b in blocks})} capture(s), {len(blocks)} tagged blocks, "
          f"win=+/-{opt['--win']}s, cone={opt['--cone']} deg, skip={opt['--skip']}s, "
          f"{int(opt['--perm'])} shifts")
    report(agg)


if __name__ == "__main__":
    main(sys.argv[1:])
