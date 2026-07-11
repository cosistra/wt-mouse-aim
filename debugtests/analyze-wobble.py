#!/usr/bin/env python3
"""Score / digest mouseaim recorder CSVs (v0.51 investigation).

Stdlib only (no pandas). Usage:
    python analyze-wobble.py <recording.csv> [more.csv ...]   # wobble score + PASS/FAIL verdict
    python analyze-wobble.py --digest <recording.csv> [...]   # compact phase-segmented timeline
    python analyze-wobble.py --selftest                       # in-memory asserts, no file needed

WOBBLE SCORE (default) — the metrics from the 2026-07 wobble investigation (see WOBBLE-FINDINGS.md):
  - oscillation episodes: sustained sign-alternation windows on bank / azErr / outR / outP / outY,
    with zero-crossing frequency, peak-to-peak amplitude, amplitude trend (grow/steady/decay)
  - outR rail % (|outR| > 0.98) and targetBank clamp % (|targetBank| >= MaxBank-0.5)
  - bank-vs-targetBank cross-correlation lag (the measured ~0.7 s actuation lag)
  - corr(azErr, bankTR) (the P-only outer-loop fingerprint; ~+0.9 pre-fix)
Frames with any manual engagement (engP/engR/engY > 0) are excluded from episode detection.
Acceptance (post-fix): no sustained (>4 s) 0.25-2.0 Hz bank episode (slow v0.50 cycle AND the
fast v0.51 lead-loop chatter); no 0.8+ Hz rail-to-rail outR chatter; outR rail ~0%;
azErr episode amplitudes decaying, not growing.

DIGEST (--digest) — collapse consecutive same-phase rows into one segment each, so a ~900-row
capture reads as a ~30-line timeline: per segment the phase, duration, the signals that actually
moved (start->end, interior peak when it overshoots the endpoints) and per-axis stick sign-flip
counts. Inline # cfg t=... changes and any [anomaly] lines from the sibling
mouseaim-anomalies-<session>.log (matched by rec=<this file>) are slotted at their timestamp.
The raw CSV stays the ground truth — open raw rows only for a segment the digest flags.
"""
import csv, math, sys, os, re, statistics


def load(path):
    """Return (meta, rows). meta = {cfg, headers[], cfg_marks[(t,text)], session}."""
    meta = {"cfg": "", "headers": [], "cfg_marks": [], "session": ""}
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
                else:
                    meta["headers"].append(s)
                    ms = re.search(r"session=(\S+)", s)
                    if ms:
                        meta["session"] = ms.group(1)
                continue
            data.append(raw)
    rdr = csv.DictReader(data)
    out = []
    for r in rdr:
        try:
            out.append({k: (r[k] if k in ("phase", "controlLaw") else float(r[k]))
                        for k in r if r[k] is not None and r[k] != ""})
        except ValueError:
            continue
    return meta, out


def crossings(ts, xs, dead):
    """Indices where the signal crosses through the ±dead band with a sign change."""
    idx, prev = [], 0.0
    for i, x in enumerate(xs):
        if abs(x) < dead:
            continue
        s = math.copysign(1.0, x)
        if prev and s != prev:
            idx.append(i)
        prev = s
    return idx


def episodes(ts, xs, dead, min_dur=2.0, max_gap=2.5):
    """Group sign-flips into sustained-oscillation episodes; report freq/amplitude/trend."""
    xi = crossings(ts, xs, dead)
    eps, start = [], 0
    for k in range(1, len(xi) + 1):
        if k == len(xi) or ts[xi[k]] - ts[xi[k - 1]] > max_gap:
            seg = xi[start:k]
            start = k
            if len(seg) < 4:
                continue
            t0, t1 = ts[seg[0]], ts[seg[-1]]
            if t1 - t0 < min_dur:
                continue
            i0, i1 = seg[0], seg[-1]
            vals = xs[i0:i1 + 1]
            freq = (len(seg) - 1) / 2.0 / (t1 - t0)            # 2 crossings per cycle
            pp = max(vals) - min(vals)
            third = max(1, len(vals) // 3)
            pp_a = max(vals[:third]) - min(vals[:third])
            pp_b = max(vals[-third:]) - min(vals[-third:])
            trend = "GROW" if pp_b > pp_a * 1.3 else ("decay" if pp_b < pp_a * 0.7 else "steady")
            eps.append(dict(t0=t0, t1=t1, dur=t1 - t0, freq=freq, pp=pp, trend=trend))
    return eps


def corr(a, b):
    n = min(len(a), len(b))
    if n < 8:
        return 0.0
    a, b = a[:n], b[:n]
    ma, mb = statistics.fmean(a), statistics.fmean(b)
    sa = math.sqrt(sum((x - ma) ** 2 for x in a))
    sb = math.sqrt(sum((x - mb) ** 2 for x in b))
    if sa * sb == 0:
        return 0.0
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / (sa * sb)


def xcorr_lag(ts, a, b, max_lag=2.0):
    """Lag (s) at which b best matches a shifted forward: positive = b lags a."""
    if len(ts) < 20:
        return 0.0, 0.0
    dt = statistics.median(ts[i + 1] - ts[i] for i in range(len(ts) - 1))
    best, bestlag = 0.0, 0.0
    for sh in range(0, int(max_lag / dt) + 1):
        c = corr(a[:len(a) - sh] if sh else a, b[sh:])
        if abs(c) > abs(best):
            best, bestlag = c, sh * dt
    return bestlag, best


def analyze(path):
    meta, rows = load(path)
    cfg = meta["cfg"]
    if not rows:
        print(f"{path}: no data rows")
        return
    human = [r for r in rows if r.get("engP", 0) or r.get("engR", 0) or r.get("engY", 0)]
    auto = [r for r in rows if not (r.get("engP", 0) or r.get("engR", 0) or r.get("engY", 0))]
    ts = [r["t"] for r in auto]
    col = lambda k: [r.get(k, 0.0) for r in auto]
    spd = col("spd")

    print(f"\n=== {path}")
    print(f"  {len(rows)} rows ({len(human)} human, excluded), t {rows[0]['t']:.1f}-{rows[-1]['t']:.1f}, "
          f"spd {min(spd):.0f}-{max(spd):.0f} m/s")
    if "leadT" in cfg:
        print(f"  config: {' '.join(w for w in cfg.split() if w.startswith(('law=', 'trGain', 'leadT', 'aOffP')))}")

    rail = 100.0 * sum(1 for x in col("outR") if abs(x) > 0.98) / max(1, len(auto))
    tb = col("targetBank")
    clamp = 100.0 * sum(1 for x in tb if abs(x) >= 71.5) / max(1, len(auto))
    lag, lc = xcorr_lag(ts, tb, col("bank"))
    print(f"  outR railed {rail:.1f}%   targetBank clamped {clamp:.1f}%   "
          f"bank lags targetBank by {lag:.2f}s (corr {lc:+.2f})   corr(azErr,bankTR) {corr(col('azErr'), col('bankTR')):+.2f}")

    fail = []
    for name, dead in (("bank", 3.0), ("azErr", 0.5), ("outR", 0.05), ("outP", 0.05), ("outY", 0.05)):
        for e in episodes(ts, col(name), dead):
            print(f"  [{name:7s}] t {e['t0']:.1f}-{e['t1']:.1f} ({e['dur']:.1f}s) "
                  f"{e['freq']:.2f} Hz  pp {e['pp']:.2f}  {e['trend']}")
            # 0.25-2.0 Hz: covers both the v0.50 slow outer-loop cycle (0.3-0.85 Hz) and the
            # v0.51 fast lead-loop chatter (1.1-1.35 Hz) — the band was 0.3-0.9 and PASSed the latter.
            if name == "bank" and e["dur"] > 4 and 0.25 <= e["freq"] <= 2.0 and e["pp"] > 15:
                fail.append(f"sustained bank limit cycle {e['freq']:.2f} Hz for {e['dur']:.0f}s (pp {e['pp']:.0f} deg)")
            if name == "outR" and e["freq"] >= 0.8 and e["pp"] > 1.2 \
                    and (e["dur"] > 3 or e["trend"] == "GROW"):
                fail.append(f"roll-stick chatter {e['freq']:.2f} Hz rail-to-rail for {e['dur']:.0f}s ({e['trend']})")
            if name == "azErr" and e["trend"] == "GROW" and e["dur"] > 5:
                fail.append(f"growing azErr oscillation over {e['dur']:.0f}s")
    if rail > 2.0:
        fail.append(f"roll stick railed {rail:.0f}% of frames")
    print("  VERDICT: " + ("FAIL — " + "; ".join(fail) if fail else "PASS — no wobble signature"))


# --- digest mode -------------------------------------------------------------------------------

# (column, flat-threshold, decimals) — a signal is printed only when its span exceeds the
# threshold, so steady-state rows collapse to nothing. `off` leads (the headline tracking error).
DIGEST_SIGS = [("off", 1.0, 1), ("azErr", 1.0, 1), ("elevErr", 1.0, 1),
               ("bank", 2.0, 1), ("targetBank", 2.0, 1), ("g", 0.15, 2),
               ("aoa", 0.5, 2), ("spd", 3.0, 0), ("yawWeak", 0.05, 2),
               ("outP", 0.05, 3), ("outR", 0.05, 3), ("outY", 0.05, 3)]
_LABEL = {"targetBank": "tgtBank"}


def segment(rows):
    """Collapse consecutive rows sharing a phase into one segment (single pass)."""
    segs = []
    for r in rows:
        ph = r.get("phase", "?")
        if not segs or segs[-1]["phase"] != ph:
            segs.append({"phase": ph, "rows": [r]})
        else:
            segs[-1]["rows"].append(r)
    return segs


def sig(name, vals, thr, prec):
    """`name a->b` when the signal moved (plus `[lo..hi]` if it peaked past the endpoints)."""
    lo, hi = min(vals), max(vals)
    if hi - lo < thr:
        return None
    a, b = vals[0], vals[-1]
    out = f"{_LABEL.get(name, name)} {a:.{prec}f}->{b:.{prec}f}"
    if hi - max(a, b) > thr or min(a, b) - lo > thr:
        out += f"[{lo:.{prec}f}..{hi:.{prec}f}]"
    return out


def seg_flips(segrows):
    """Stick sign-flip count per axis (same hunt/wobble signal the anomaly detectors use)."""
    out = []
    for name, lbl in (("outP", "Pflip"), ("outR", "Rflip"), ("outY", "Yflip")):
        n = len(crossings(None, [r.get(name, 0.0) for r in segrows], 0.05))
        if n > 1:
            out.append(f"{lbl}:{n}")
    return out


def load_anomalies(csv_path, session):
    """[(t, type, line)] from the sibling anomaly log, filtered to rec=<this csv>."""
    if not session:
        return []
    logp = os.path.join(os.path.dirname(csv_path), f"mouseaim-anomalies-{session}.log")
    base = os.path.basename(csv_path)
    out = []
    try:
        with open(logp) as f:
            for line in f:
                if f"rec={base}" not in line:
                    continue
                mt = re.search(r"\bt=([\d.]+)", line)
                mty = re.search(r"\[anomaly[^\]]*\]\s+(\S+)", line)
                if mt:
                    out.append((float(mt.group(1)), mty.group(1) if mty else "anomaly", line.strip()))
    except OSError:
        pass
    return out


def digest(path):
    meta, rows = load(path)
    if not rows:
        print(f"{path}: no data rows")
        return
    segs = segment(rows)
    anoms = load_anomalies(path, meta["session"])
    t0, tN = rows[0]["t"], rows[-1]["t"]
    events = sorted([(t, f"# cfg {txt}") for t, txt in meta["cfg_marks"]]
                    + [(t, f"! [anomaly] {typ}") for t, typ, _ in anoms])

    print(f"\n=== {path}")
    for h in meta["headers"]:
        print(h)
    if meta["cfg"]:
        print(f"# config {meta['cfg']}")
    print(f"timeline: {len(segs)} segments, t {t0:.1f}-{tN:.1f} ({tN - t0:.1f}s)")

    ei = 0
    for k, seg in enumerate(segs):
        s = seg["rows"][0]["t"]
        e = segs[k + 1]["rows"][0]["t"] if k + 1 < len(segs) else tN
        parts = [p for p in (sig(n, [r.get(n, 0.0) for r in seg["rows"]], thr, prec)
                             for n, thr, prec in DIGEST_SIGS) if p]
        line = f"  {s:6.1f}-{e:6.1f} {seg['phase']:5s} {e - s:4.1f}s  " + "  ".join(parts)
        flips = seg_flips(seg["rows"])
        if flips:
            line += "   " + " ".join(flips)
        print(line)
        while ei < len(events) and events[ei][0] < e:
            print(f"        @{events[ei][0]:.1f} {events[ei][1]}")
            ei += 1
    for t, txt in events[ei:]:
        print(f"        @{t:.1f} {txt}")

    rate = len(rows) / (tN - t0) if tN > t0 else 0.0
    print(f"footer: {tN - t0:.1f}s, {len(rows)} samples (~{rate:.0f} Hz), "
          f"{len(segs)} segments, {len(anoms)} anomalies")


def selftest():
    rows = [{"t": i * 0.2, "phase": "HOLD", "off": 0.1, "outR": 0.0} for i in range(10)]
    rows += [{"t": 2.0 + i * 0.2, "phase": "TURN", "off": i * 3.0,
              "outR": (0.5 if i % 2 else -0.5)} for i in range(10)]
    segs = segment(rows)
    assert len(segs) == 2, len(segs)
    assert [s["phase"] for s in segs] == ["HOLD", "TURN"], segs
    # 10 rows alternating sign => 9 sign flips (all magnitudes clear the 0.05 dead band)
    assert len(crossings(None, [r["outR"] for r in segs[1]["rows"]], 0.05)) == 9
    # durations telescope to the capture length; flat HOLD off is dropped, rising TURN off kept
    d0 = segs[1]["rows"][0]["t"] - segs[0]["rows"][0]["t"]
    assert abs(d0 - 2.0) < 1e-9, d0
    assert sig("off", [r["off"] for r in segs[0]["rows"]], 1.0, 1) is None
    assert sig("off", [r["off"] for r in segs[1]["rows"]], 1.0, 1) == "off 0.0->27.0"
    print("selftest OK")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    if args[0] == "--selftest":
        selftest()
    elif args[0] == "--digest":
        for p in args[1:]:
            digest(p)
    else:
        for p in args:
            analyze(p)
