#!/usr/bin/env python3
"""flightscore.py — physics-normalized flight quality metric for maneuver-recorder CSVs.

Answers ONE question per tick: *given what this airframe could physically do at that
instant, was there a better way to get the nose where it was asked?*  Every normalizer
comes from the sibling `<stem>.airframe.json` probe + live state (V, air density,
velocity vector) — never a hand-tuned constant — so a light jet, a loaded jet, a STOL
trainer and a helo produce comparable numbers.  That is the whole point; the moment a
constant is tuned to suit one plane the metric stops being cross-airframe.

    python debugtests/flightscore.py <rec.csv> [...] [--tau 0.25] [--cone 1.0] [--json]
    python debugtests/flightscore.py --selftest

Reads: t, off, spd, airDensity, velX/Y/Z, outP/outR/outY, segTag (58-col recorder).
Never edits anything. Stdlib only.
"""

import csv
import json
import math
import os
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


def load_run(path):
    with open(path) as f:
        lines = [l for l in f if not l.startswith("#")]
    rd = csv.DictReader(lines)
    missing = set(NEED) - set(rd.fieldnames or [])
    if missing:
        raise SystemExit(f"{path}: missing columns {sorted(missing)}")
    has_rho = "airDensity" in (rd.fieldnames or [])   # optional: pre-v0.69 captures lack it
    run = {k: [] for k in NEED + ("segTag", "airDensity")}
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


def score_file(path, tau, cone=ON_TARGET_DEG):
    run = load_run(path)
    p = load_airframe(path)
    ticks = score_ticks(run, p, tau, cone)
    segs = {}
    for tag in dict.fromkeys(k["tag"] for k in ticks):
        segs[tag] = summarize(run, [k for k in ticks if k["tag"] == tag])
    return {"file": os.path.basename(path), "airframe": p, "cone_deg": cone,
            "segments": segs, "all": summarize(run, ticks)}


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


def spread(results):
    """Run-to-run band of A = the metric's own noise floor. An effect smaller than the band
    is not an effect. A run that simply lacks a tag is EXCLUDED from that tag, never
    back-filled with its whole-run value (same discipline as compare-runs.py)."""
    frames = {r["airframe"]["name"] for r in results}
    if len(frames) > 1:
        print(f"\nWARNING: mixed airframes {sorted(frames)} — A is comparable across them, "
              "but a segTag flown by different planes is not the same test. Not pooling.")
    tags = list(dict.fromkeys(t for r in results for t in r["segments"]))
    print(f"\n=== spread across {len(results)} runs (the metric's own noise floor) ===")
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


# ---- selftest --------------------------------------------------------------------
def synth(off_fn, turn, dur=6.0, dt=1 / 15.0, v=250.0, out_fn=None):
    """A fake run: `off` from off_fn(t), velocity vector rotated by `turn` (deg/s, or a
    callable giving cumulative degrees)."""
    run = {k: [] for k in NEED + ("segTag", "airDensity")}
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
    paths = [a for a in argv if not a.startswith("--")]
    if not paths:
        return print(__doc__.strip())
    results = [score_file(p, tau, cone) for p in paths]
    if as_json:
        print(json.dumps({"tau_feel": tau, "cone_deg": cone, "runs": results}, indent=1, default=str))
        return
    for r in results:
        report(r)
    if len(results) > 1:
        spread(results)


if __name__ == "__main__":
    main(sys.argv[1:])
