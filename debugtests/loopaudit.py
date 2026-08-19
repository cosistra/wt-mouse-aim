#!/usr/bin/env python3
"""loopaudit.py — self-referential feedback loops in ChaseController.Apply.

The audit question (GENERALITY-REVIEW finding 13): **can the command this term gates move
this term?** This tool measures the three answers that turned out to be YES, on the existing
capture corpus, plus the arithmetic that makes two of them provable without any capture.

    python loopaudit.py --selftest                 # the closed forms, no data needed
    python loopaudit.py <rec.csv>...               # per-segment table
    python loopaudit.py --json <rec.csv>...
    python loopaudit.py --settled 20 <rec.csv>...  # only tSeg >= 20 s (drops entry transients)

Stdlib only, same as every other tool here. Findings write-up: LOOP-AUDIT-FINDINGS.md.

WHAT IS MEASURED, and why each column exists
--------------------------------------------
pEffLatch%  `_pitchEff` < PEffRevThresh AND |fbwTgtPR| < 0.05 — the estimator is in v0.67
            self-probe mode (no command to measure) while C1 has the effFloor switched OFF,
            so the law is scaling its pitch P term by ~0.15. `pEffTrue` reports what the
            plant was ACTUALLY delivering on those same ticks (|fbwPR/fbwTgtPR|): the latch
            is diagnosed from the recorded rate pair, not inferred.
eFineW      1 - blendWeight = the weight the roll servo gives eFine, the ONLY term carrying
            tBankE. It is the gain through which the whole bank pipeline (azErrPred, the
            v0.78 marker-rate feed-forward, omegaMax, predFloor, MaxBankAngle) reaches the
            roll command. bwRail% is how often it is exactly zero.
ffDeliv     the v0.78 feed-forward's share of the turn demand, and the stick it actually
            buys once the MaxBankAngle clamp and eFineW have taken their cut.
yawKept     1 - YawWeakFade*assist = the fraction of the yaw command `_yawWeak` leaves alive.

Everything recomputed here uses the SAME expressions as ChaseController.ApplyEvolvedLegacy,
with every input a recorded column and every constant either on the `# config` line or a
documented regime const in the source. Captures predating a column are skipped, not guessed.
"""

import csv
import json
import math
import statistics
import sys

# ---- constants that live in ChaseController.cs, not in Cfg -------------------------
PEFF_REV_THRESH = 0.15   # PEffRevThresh: C1 floor threshold AND the v0.67 self-probe target
PEFF_CMD_GATE = 0.05     # |cmd| under which the estimator has nothing to measure
PEFF_EFF_FLOOR = 0.30    # effFloor: the healthy-but-noisy floor C1 gates behind revThresh
PEFF_REL_TAU = 1.0       # slow-release tau the self-probe rises on
DOWN_ALIGN_TAPER = 0.3   # belowSuppress bigTurn taper width
G = 9.81

# Cfg fallbacks for a capture with no `# config` line — documented numbers, never a crash.
CFG_DEFAULTS = {"fineAng": 6.0, "align": 25.0, "bankDz": 2.5, "alignHold": 5.0,
                "pullRel": 2.0, "maxBank": 72.0, "yaStr": 0.7, "yawFade": 1.0,
                "coordPull": 0.8, "coordCap": 0.85, "rollDamp": 0.10, "trGain": 0.92}

NEED = ("t", "off", "azErr", "phi", "bigTurn", "spd", "outP", "outR", "yawWeak")


def clamp01(x):
    return 0.0 if x < 0.0 else 1.0 if x > 1.0 else x


def load(path):
    """(cfg, rows). Numeric where parseable; `phase`/`segTag` stay strings."""
    cfg, lines = dict(CFG_DEFAULTS), []
    with open(path) as fh:
        for line in fh:
            if line.startswith("#"):
                if line.startswith("# config"):
                    for kv in line[8:].split():
                        k, _, v = kv.partition("=")
                        try:
                            cfg[k] = float(v)
                        except ValueError:
                            pass          # a non-numeric knob (law=...) — keep the default
            else:
                lines.append(line)
    rd = csv.DictReader(lines)
    cols = rd.fieldnames or []
    rows = []
    for r in rd:
        if any(r.get(c) is None for c in cols):
            continue                      # torn last row on a crashed capture
        out = {}
        for c in cols:
            try:
                out[c] = float(r[c])
            except (TypeError, ValueError):
                out[c] = (r[c] or "").strip()
        if all(isinstance(out.get(c), float) for c in NEED):
            rows.append(out)
    return cfg, rows, cols


# ---- the three loops ---------------------------------------------------------------
def blend_weight(row, cfg):
    """ApplyEvolvedLegacy's roll blend weight, and the eFine weight it leaves over.

    Pre-v0.85 belowSuppress form (body-frame alignFrac == cos(phi), and the (1-lateralHold)
    factor) — that is what every capture in the corpus was flown on. A v0.85+ capture records
    bWt directly and this recompute is not needed.
    """
    az, phi, bt = row["azErr"], row["phi"], row["bigTurn"]
    heli = row.get("heliBlend", 0.0)
    lat_hold = clamp01(max(0.0, abs(az) - cfg["bankDz"]) / max(0.01, cfg["alignHold"]))
    below = (clamp01(-math.cos(math.radians(phi))) * (1.0 - lat_hold)
             * clamp01((1.0 - bt) / DOWN_ALIGN_TAPER))
    return max(bt, lat_hold) * (1.0 - heli) * (1.0 - below), lat_hold


def coord_pull(t_bank_deg, row, cfg):
    """coordPull = clamp(CoordPullGain*|sin(tBankE)|*pullTaper*assist, 0, CoordPullCap).

    Note `* assist`: the coordinating pull — which Cfg.cs calls "the REAL driver of a
    high-speed correction" — is scaled by `_yawWeak`. assist == 0 makes it identically 0.
    """
    assist = row["yawWeak"] * (1.0 - row["bigTurn"]) * cfg["yaStr"]
    taper = clamp01(abs(row["azErr"]) / max(0.5, cfg["pullRel"]))
    raw = cfg["coordPull"] * abs(math.sin(math.radians(t_bank_deg))) * taper * assist
    return min(max(raw, 0.0), cfg["coordCap"]), assist


def ff_delivered(row, cfg, bw):
    """What the v0.78 marker-rate feed-forward actually buys, after both bottlenecks.

    omegaDes is recoverable exactly by inverting the law's own bankTR = atan(omega*V/g)
    (the recorded bankTR is taken AFTER the achievability cap and BEFORE MaxBankAngle), so
    there is no second copy of the demand chain to drift out of lockstep with the source.
    Counterfactual = the same tick with `_aimAzRateFilt` removed from omega.
    """
    v = max(50.0, row["spd"])            # Cfg.BankSpeedFloor default
    om = math.degrees(math.tan(math.radians(row["bankTR"])) * G / v)
    aim = row.get("aimRate", 0.0)
    share = aim / om if abs(om) > 1e-9 else 0.0
    def bank_of(w):
        return math.degrees(math.atan(math.radians(w) * v / G))
    tb = max(-cfg["maxBank"], min(cfg["maxBank"], bank_of(om)))
    tb0 = max(-cfg["maxBank"], min(cfg["maxBank"], bank_of(om - aim)))
    cp, _ = coord_pull(tb, row, cfg)
    cp0, _ = coord_pull(tb0, row, cfg)
    d_efine = (math.sin(math.radians(tb)) - math.sin(math.radians(tb0))) * (1.0 - bw)
    return share, cp - cp0, d_efine


def roll_d_inflation(bw, roll_damp):
    """v0.85 AlignRateLead: the roll-rate feedback the servo ends up with.

    eAlign carries (phi + phiRate*RollDamping)/90 with phi in DEGREES; against a stationary
    marker d(phi)/dt = -rollRate (rad/s), so that lead contributes an extra
    rollRate*RollDamping*(180/pi)/90 of rate feedback, weighted by blendWeight, ON TOP of the
    servo's own -rollRateF*RollDamping. Total = RollDamping*(1 + 0.6366*blendWeight):
    a DERIVATIVE gain that is now a function of blendWeight, i.e. of |azErr|.
    """
    return roll_damp * (1.0 + (180.0 / math.pi) / 90.0 * bw)


# ---- per-file ----------------------------------------------------------------------
def analyse(path, settled=0.0):
    cfg, rows, cols = load(path)
    has_peff = "pEff" in cols and "fbwTgtPR" in cols and "fbwPR" in cols
    has_bank = "bankTR" in cols
    has_aim = "aimRate" in cols
    segs = {}
    for r in rows:
        if settled and r.get("tSeg", 0.0) < settled:
            continue
        tag = r.get("segTag", "") or "-"
        if isinstance(tag, str) and tag.startswith("micro"):
            tag = "micro"
        s = segs.setdefault(tag, {k: [] for k in
                                  ("bw", "ef", "lh", "yw", "kept", "ffs", "dcp", "def",
                                   "latch", "ptrue", "outP", "outR", "rd")})
        bw, lh = blend_weight(r, cfg)
        s["bw"].append(bw); s["ef"].append(1.0 - bw); s["lh"].append(lh)
        s["yw"].append(r["yawWeak"])
        _, assist = coord_pull(0.0, r, cfg)
        s["kept"].append(1.0 - cfg["yawFade"] * assist)
        s["rd"].append(roll_d_inflation(bw, cfg["rollDamp"]) / max(cfg["rollDamp"], 1e-9))
        s["outP"].append(abs(r["outP"])); s["outR"].append(abs(r["outR"]))
        if has_bank and has_aim:
            share, dcp, def_ = ff_delivered(r, cfg, bw)
            s["ffs"].append(share); s["dcp"].append(dcp); s["def"].append(def_)
        if has_peff:
            cmd, ach = r["fbwTgtPR"], r["fbwPR"]
            latched = r["pEff"] < PEFF_REV_THRESH and abs(cmd) < PEFF_CMD_GATE
            s["latch"].append(1.0 if latched else 0.0)
            if latched and abs(cmd) > 1e-4:
                s["ptrue"].append(abs(ach / cmd))
    return cfg, segs


def mean(xs, d=0.0):
    return statistics.mean(xs) if xs else d


def report(files, settled=0.0, as_json=False):
    agg = {}
    for p in files:
        cfg, segs = analyse(p, settled)
        for tag, s in segs.items():
            a = agg.setdefault(tag, {k: [] for k in s})
            for k, v in s.items():
                a[k].extend(v)
    out = {}
    for tag, s in agg.items():
        n = len(s["bw"])
        out[tag] = {
            "n": n,
            "blendWeight": round(mean(s["bw"]), 4),
            "eFineWeight": round(mean(s["ef"]), 4),
            "bwRailPct": round(100.0 * sum(1 for v in s["bw"] if v >= 1 - 1e-9) / max(n, 1), 1),
            "yawWeak": round(mean(s["yw"]), 3),
            "yawKeptFrac": round(mean(s["kept"], 1.0), 3),
            "rollDInflation": round(mean(s["rd"], 1.0), 3),
            "ffShareOfDemand": round(mean(s["ffs"]), 3) if s["ffs"] else None,
            "ffDeliveredPitch": round(mean(s["dcp"]), 4) if s["dcp"] else None,
            "ffDeliveredRoll": round(mean(s["def"]), 5) if s["def"] else None,
            "meanAbsOutP": round(mean(s["outP"]), 3),
            "pEffLatchPct": round(100.0 * mean(s["latch"]), 3) if s["latch"] else None,
            "pEffTrueDuringLatch": round(statistics.median(s["ptrue"]), 3) if s["ptrue"] else None,
        }
    if as_json:
        print(json.dumps(out, indent=2, sort_keys=True))
        return out
    hdr = ("seg", "n", "blendW", "eFineW", "%bw=1", "yawWk", "yawKept", "rollD x",
           "ffShare", "ffPitch", "|outP|", "%pLatch", "pTrue")
    print(f"{hdr[0]:<10s}{hdr[1]:>6s}{hdr[2]:>8s}{hdr[3]:>8s}{hdr[4]:>7s}{hdr[5]:>7s}"
          f"{hdr[6]:>8s}{hdr[7]:>8s}{hdr[8]:>8s}{hdr[9]:>8s}{hdr[10]:>7s}{hdr[11]:>8s}{hdr[12]:>7s}")
    for tag in sorted(out, key=lambda k: -out[k]["blendWeight"]):
        d = out[tag]
        f = lambda k, w, p, s="": ("-" if d[k] is None else f"{d[k]:.{p}f}" + s).rjust(w)
        print(f"{tag:<10s}{d['n']:>6d}{f('blendWeight',8,3)}{f('eFineWeight',8,3)}"
              f"{d['bwRailPct']:>6.1f}%{f('yawWeak',7,3)}{f('yawKeptFrac',8,3)}"
              f"{f('rollDInflation',8,3)}{f('ffShareOfDemand',8,3)}{f('ffDeliveredPitch',8,4)}"
              f"{f('meanAbsOutP',7,3)}{f('pEffLatchPct',8,3,'%')}{f('pEffTrueDuringLatch',7,3)}")
    return out


# ---- selftest ----------------------------------------------------------------------
def selftest():
    # --- L1. The _pitchEff self-probe CANNOT clear its own threshold. -----------------
    # v0.67 restores a self-probe by drifting pitchEffInst to Max(_pitchEff, PEffRevThresh)
    # whenever |cmd| < 0.05, so a latched-low estimate "rises toward ~15% so pitch
    # re-establishes". But PEffRevThresh is ALSO C1's floor threshold, tested with `>=`, and
    # the probe is a first-order LPF toward that same number: it approaches from BELOW and
    # asymptotes. float32 stalls it ~30 ulps short and rounding never carries it over, so
    # the floor branch is unreachable and the law keeps scaling pitch by ~0.15 forever.
    x = 0.0
    dt = 1.0 / 60.0
    alpha = dt / (PEFF_REL_TAU + dt)
    for _ in range(100000):                      # ~28 minutes of flight at 60 Hz
        x += alpha * (PEFF_REV_THRESH - x)
    assert x < PEFF_REV_THRESH, x                # asymptote, never reached
    assert PEFF_REV_THRESH - x < 1e-9, x         # and it is arbitrarily close, so it LOOKS fine
    # the consequence: the gain factor the law applies stays on the un-floored branch
    factor = (max(PEFF_EFF_FLOOR, x) if x >= PEFF_REV_THRESH else x)
    assert abs(factor - x) < 1e-9 and factor < 0.1500001, factor
    # one ulp over the threshold and the factor DOUBLES — a 2x discontinuity the probe parks on
    y = PEFF_REV_THRESH
    assert (max(PEFF_EFF_FLOOR, y) if y >= PEFF_REV_THRESH else y) == PEFF_EFF_FLOOR
    # and the latch is closed: a command scaled by 0.15 stays under the 0.05 re-measure gate
    # for any demand whose unscaled FBW target rate is below 0.333 rad/s.
    assert 0.33 * PEFF_REV_THRESH < PEFF_CMD_GATE
    assert 0.34 * PEFF_EFF_FLOOR > PEFF_CMD_GATE  # with the floor applied it WOULD clear

    # --- L2. lateralHold rails, and railing it zeroes the bank pipeline. --------------
    cfg = dict(CFG_DEFAULTS)
    rail_at = cfg["bankDz"] + cfg["alignHold"]           # 2.5 + 5.0 = 7.5 deg
    row = {"azErr": rail_at, "phi": 0.0, "bigTurn": 0.0, "heliBlend": 0.0}
    bw, lh = blend_weight(row, cfg)
    assert abs(lh - 1.0) < 1e-9 and abs(bw - 1.0) < 1e-9, (lh, bw)
    assert abs((1.0 - bw)) < 1e-9                        # eFine weight EXACTLY zero
    row2 = dict(row, azErr=rail_at - 0.01)
    assert blend_weight(row2, cfg)[0] < 1.0              # just under, the pipeline is alive
    # with the pipeline at zero weight, coordPull is tBankE's only consumer — and it is
    # gated on _yawWeak, so YawAssistEnabled=false deletes the LAST path.
    r3 = {"azErr": 10.0, "bigTurn": 0.2, "yawWeak": 1.0}
    assert coord_pull(72.0, r3, cfg)[0] > 0.0
    assert coord_pull(72.0, dict(r3, yawWeak=0.0), cfg)[0] == 0.0

    # --- L3. _yawWeak reads "the error did not close", not "the rudder is weak". ------
    # weakInst = 1 - clamp01(closeRate/6). R21's settled turn360 closes at 0.033 deg/s
    # while the game FBW delivers 99.4% of the commanded rate and no axis is near a rail.
    weak = lambda close: 1.0 - clamp01(close / 6.0)
    assert abs(weak(0.033) - 0.9945) < 1e-4, weak(0.033)  # recorded yawWeak max: 0.996
    assert weak(6.0) == 0.0 and weak(0.0) == 1.0
    # and the 6.0 is an ABSOLUTE deg/s — the per-airframe-constant-in-disguise form the
    # v0.83 _stallFilt comment explicitly forbids for exactly this reason.
    assert weak(3.0) == 0.5                               # "half weak" at 3 deg/s, any airframe

    # --- L4. v0.85 AlignRateLead makes roll D gain a function of blendWeight. ---------
    d = cfg["rollDamp"]
    assert abs(roll_d_inflation(0.0, d) - d) < 1e-12                 # eFine only: unchanged
    assert abs(roll_d_inflation(1.0, d) - d * 1.6366) < 1e-4         # eAlign only: +64%
    assert roll_d_inflation(0.43, d) > roll_d_inflation(0.0, d)      # elDn mean bWt 0.43

    # --- L5. belowSuppress's residual bigTurn loop, and where it re-arms. -------------
    # The v0.85 fix removed the (1-lateralHold) factor, but clamp01((1-bigTurn)/0.3) is a
    # function of `off`, which CONTAINS the azimuth error roll-to-align creates. Saturated
    # (inert) until bigTurn > 0.7, i.e. off > FineAngle + 0.7*(AlignAngle-FineAngle).
    knee = cfg["fineAng"] + (1.0 - DOWN_ALIGN_TAPER) * (cfg["align"] - cfg["fineAng"])
    assert abs(knee - 19.3) < 0.05, knee
    bt_of = lambda off: clamp01((off - cfg["fineAng"]) / (cfg["align"] - cfg["fineAng"]))
    assert clamp01((1.0 - bt_of(6.92)) / DOWN_ALIGN_TAPER) == 1.0    # elDn: inert
    assert clamp01((1.0 - bt_of(20.0)) / DOWN_ALIGN_TAPER) < 1.0     # the 20 deg step: live

    # --- L6. ff_delivered inverts the law's own bankTR definition exactly. ------------
    v, om_deg = 259.7, 14.63
    bank = math.degrees(math.atan(math.radians(om_deg) * v / G))
    assert abs(bank - 81.6) < 0.1, bank
    r = {"spd": v, "bankTR": bank, "aimRate": 12.06, "azErr": 9.31,
         "bigTurn": 0.18, "yawWeak": 0.99}
    share, dcp, dro = ff_delivered(r, dict(cfg, coordPull=0.40), 1.0)
    assert abs(share - 12.06 / om_deg) < 1e-3, share       # 82.5% of the demand is the FF
    assert 0.0 < dcp < 0.06, dcp                           # ~0.04 of pitch stick survives
    assert dro == 0.0, dro                                 # and at bw=1, NOTHING reaches roll

    print("loopaudit selftest OK")


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    if "--selftest" in argv:
        selftest()
        return 0
    settled = 0.0
    if "--settled" in argv:
        i = argv.index("--settled")
        settled = float(argv[i + 1])
        args = [a for a in args if a != argv[i + 1]]
    if not args:
        print(__doc__)
        return 1
    report(args, settled=settled, as_json="--json" in argv)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
