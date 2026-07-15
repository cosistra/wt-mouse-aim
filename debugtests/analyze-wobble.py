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
  - v0.55 model fit: pitchRate ~ -outP*G*9.81/V (the decompiled FBW g-command law), split at the
    airframe's corner speed (from the # fbw header when present, else 170 m/s). High-q corr should
    be strongly positive (law confirmed: +0.92..+0.99 on the v54 tester CSVs); a NEGATIVE low-q
    corr means the aircraft stopped following pitch commands (stall dynamics) while the mod kept
    commanding — the low-speed oscillation fingerprint.
  - v0.55 stall-oscillation verdict: below corner speed, (a) AoA pp > 25 deg with a sustained
    azErr sign-alternation episode (stall blow-through, the crash case) or (b) a GROWING azErr
    episode (compounding oscillation) => FAIL "low-speed stall oscillation" (the Draken failure
    mode, named).
  - v0.56 verdict split: FAIL now requires DYNAMIC evidence (an oscillation episode, growing
    azErr, or AoA blow-through). Rail-only evidence (roll stick railed, low- or high-speed) is a
    WARN, not a FAIL — the v55 Trainer/FS-12 captures showed plain railing is usually a benign
    max-performance reversal (roll authority saturated while azErr converges cleanly).
Frames with any manual engagement (engP/engR/engY > 0) are excluded from episode detection.
Acceptance (post-fix): no sustained (>4 s) 0.25-2.0 Hz bank episode (slow v0.50 cycle AND the
fast v0.51 lead-loop chatter); no 0.8+ Hz rail-to-rail outR chatter; outR rail ~0%;
azErr episode amplitudes decaying, not growing; no stall-oscillation verdict; no negative
low-q model-fit corr.

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


def fbw_corner(meta, default=170.0):
    """Per-airframe corner speed from the v0.55 '# fbw cornerSpeed=...' header; 170 m/s fallback
    for pre-v0.55 recordings (a middling fixed-wing value — the split is a regime hint, not law)."""
    for h in meta["headers"]:
        m = re.search(r"cornerSpeed=([\d.]+)", h)
        if m:
            return float(m.group(1))
    return default


def fbw_params(meta):
    """All numeric params from the '# fbw' header line as a dict ({} pre-v0.55)."""
    for h in meta["headers"]:
        if h.startswith("# fbw") and "cornerSpeed=" in h:
            return {m.group(1): float(m.group(2))
                    for m in re.finditer(r"(\w+)=([-\d.]+)", h)}
    return {}


def derived_guards(rows, fbw):
    """(min qSched, % rows nose-up AoA gate < 1) reconstructed offline from the spd/aoa columns +
    the # fbw header — no recorder columns needed, the guards are pure functions of what's already
    recorded. v0.56 formulas (relative margin/fade; density assumed sea level, so qSched is
    approximate at altitude, and the gate is approximate for pre-0.56 recordings)."""
    corner, lim = fbw.get("cornerSpeed"), fbw.get("alphaLimiter")
    if not corner or not rows:
        return None, None
    qmin = min(max(0.3, min(1.0, (r.get("spd", 0.0) / corner) ** 2)) for r in rows)
    gated = None
    if lim:
        margin, fade = min(4.0, 0.15 * lim), min(6.0, 0.25 * lim)
        ceil = lim - margin
        gated = 100.0 * sum(1 for r in rows if (ceil - r.get("aoa", 0.0)) / fade < 1.0) / len(rows)
    return qmin, gated


def model_fit(rows, corner):
    """Least-squares fit of the decompiled FBW g-command law pitchRate ~ -outP*G*9.81/V, split at
    corner speed. Returns {"hi": (G, corr, n) | None, "lo": ...}. Mod frame: pitchRate + = nose up,
    outP negative = nose up, hence the minus sign. Only rows with real stick input count."""
    def fit(rs):
        pts = [(-r.get("outP", 0.0) * 9.81 / max(r.get("spd", 0.0), 1.0), r.get("pitchRate", 0.0))
               for r in rs if abs(r.get("outP", 0.0)) > 0.05 and r.get("spd", 0.0) > 20]
        if len(pts) < 20:
            return None
        sxx = sum(x * x for x, _ in pts)
        g = sum(x * y for x, y in pts) / sxx if sxx else 0.0
        return g, corr([p[0] for p in pts], [p[1] for p in pts]), len(pts)
    return {"hi": fit([r for r in rows if r.get("spd", 0.0) >= corner]),
            "lo": fit([r for r in rows if r.get("spd", 0.0) < corner])}


def low_speed_check(rows, corner):
    """The Draken failure mode, in its measured shapes (calibrated on the four v54 low-speed FAIL
    captures vs the two clean low-speed ones): below corner speed the mod over-commands what the
    collapsed pitch rate can fly, so either (a) AoA blows through stall while azErr oscillates
    (the crash case, 184314: AoA pp 56 deg), or (b) the azErr oscillation is GROWING (compounding,
    183929) — those are DYNAMIC evidence and FAIL. A railed roll stick chasing a slammed bank
    target is only rail evidence and WARNs (v0.56 split: the v55 captures showed rail-only is
    usually a benign max-performance reversal). Returns (dynamic_ev, rail_ev) string lists."""
    lo = [r for r in rows if r.get("spd", 1e9) < corner]
    if len(lo) < 40:
        return [], []
    dyn, rail_ev = [], []
    aoas = [r.get("aoa", 0.0) for r in lo]
    aoa_pp = max(aoas) - min(aoas)
    eps = episodes([r["t"] for r in lo], [r.get("azErr", 0.0) for r in lo], 1.0, min_dur=3.0)
    if aoa_pp > 25 and eps:
        worst = max(eps, key=lambda e: e["dur"])
        dyn.append(f"AoA pp {aoa_pp:.0f} deg with azErr oscillation "
                   f"{worst['freq']:.2f} Hz x {worst['dur']:.0f}s (stall blow-through)")
    grow = [e for e in eps if e["trend"] == "GROW"]
    if grow:
        g = max(grow, key=lambda e: e["pp"])
        dyn.append(f"growing azErr oscillation ({g['freq']:.2f} Hz, pp {g['pp']:.0f} deg)")
    rail = 100.0 * sum(1 for r in lo if abs(r.get("outR", 0.0)) > 0.98) / len(lo)
    if rail > 2.0:
        rail_ev.append(f"roll railed {rail:.0f}% below corner speed (bank demand vs roll authority)")
    return dyn, rail_ev


def buzz_scan(rows, win_s=3.0, flip_thr=0.55, pp_thr=0.04):
    """v0.57 high-frequency pitch-buzz detector (the KR-67 Ifrit canard-remap limit cycle,
    ~5.3 Hz — far too small/fast for the episode detector; the v56 Ifrit files PASSed while
    buzzing 82% of the time). Slides a ~3 s window over mod-flown rows; a window is buzzing
    when the HIGH-PASSED outP (x minus a 5-sample moving mean) flips sign on >flip_thr of
    samples AND its pp exceeds pp_thr. flip_thr=0.55 separates the measured buzz (57-70%
    on the sustained Ifrit captures) from honest hard maneuvering (41-50% on the v54
    FS-12/Trainer files) — weaker buzz expressions below it are deliberately let through.
    Returns (total_buzz_s, mod_flown_s, max_pp, max_flip_frac)."""
    xs = [r.get("outP", 0.0) for r in rows]
    ts = [r["t"] for r in rows]
    if len(xs) < 30:
        return 0.0, 0.0, 0.0, 0.0
    hf = [xs[i] - sum(xs[max(0, i - 2):i + 3]) / len(xs[max(0, i - 2):i + 3]) for i in range(len(xs))]
    n = len(xs)
    total = 0.0
    max_pp = max_flip = 0.0
    spans = []
    i, step = 0, 15
    while i + 45 <= n:
        t0, t1 = ts[i], ts[i + 44]
        if t1 - t0 < win_s * 1.5:  # contiguous (no big human-row / recording gap inside)
            w = hf[i:i + 45]
            flips = sum(1 for a, b in zip(w, w[1:]) if a * b < 0) / 44.0
            pp = max(w) - min(w)
            if flips > flip_thr and pp > pp_thr:
                spans.append([t0, t1])
                max_pp = max(max_pp, pp)
                max_flip = max(max_flip, flips)
        i += step
    # merge overlapping windows, sum the merged span durations
    last_end = None
    start = None
    for s0, s1 in spans:
        if start is None:
            start, last_end = s0, s1
        elif s0 <= last_end:
            last_end = max(last_end, s1)
        else:
            total += last_end - start
            start, last_end = s0, s1
    if start is not None:
        total += last_end - start
    return total, (ts[-1] - ts[0]), max_pp, max_flip


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

    corner = fbw_corner(meta)
    mf = model_fit(auto, corner)
    fmt = lambda f: f"G={f[0]:.1f} r={f[1]:+.2f} (n={f[2]})" if f else "n/a"
    neg_lo = mf["lo"] and mf["lo"][1] < -0.2
    print(f"  model-fit pitchRate~-outP*G*9.81/V (corner {corner:.0f} m/s): "
          f"high-q {fmt(mf['hi'])} | low-q {fmt(mf['lo'])}"
          + ("   << LOW-Q NEGATIVE CORR: aircraft not following pitch commands" if neg_lo else ""))

    # v0.56 verdict split: FAIL needs DYNAMIC evidence (oscillation episode / growing azErr /
    # AoA blow-through); rail-only evidence is a WARN (benign max-performance reversal unless
    # something is actually oscillating — the v55 capture lesson).
    fail, warn = [], []
    fbw = fbw_params(meta)
    stall_dyn, stall_rail = low_speed_check(auto, corner)
    if stall_dyn:
        fail.append("low-speed stall oscillation — " + "; ".join(stall_dyn + stall_rail))
    else:
        warn.extend(stall_rail)
    # v0.57 AoA-pump thresholds are LIMITER-RELATIVE: pp 20 on a 27-deg-limiter FS-12 is honest
    # hard maneuvering, the same pp on a 10-deg Trainer is a blow-through cycle.
    lim = fbw.get("alphaLimiter") or 12.5
    pump_pp, pump_pp_grow = max(10.0, 0.8 * lim), max(6.0, 0.5 * lim)
    for name, dead in (("bank", 3.0), ("azErr", 0.5), ("outR", 0.05), ("outP", 0.05), ("outY", 0.05),
                       ("aoa", 2.0)):
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
            # v0.57 — the assist-off AoA pump/buck cycle (reactive gate relay: outP slams 0<->-1,
            # AoA overshoots the ceiling by 1.3-2.5x per cycle; Trainer 010051 GREW 8.8->20.4 deg).
            if name == "aoa" and ((e["trend"] == "GROW" and e["pp"] > pump_pp_grow)
                                  or (e["dur"] > 4 and e["pp"] > pump_pp)):
                fail.append(f"AoA pump cycle {e['freq']:.2f} Hz pp {e['pp']:.0f} deg for {e['dur']:.0f}s ({e['trend']})")
    # v0.57 — high-frequency pitch buzz (canard-remap limit cycle; sub-episode amplitude)
    buzz_s, mod_s, buzz_pp, buzz_flip = buzz_scan(auto)
    if buzz_s >= 3.0:
        fail.append(f"high-frequency pitch buzz {buzz_s:.0f}s of {mod_s:.0f}s mod-flown "
                    f"(hf pp {buzz_pp:.2f}, flip {buzz_flip*100:.0f}%)")
    # v0.57 — overstress vs the # fbw header limits (informational: assist-off may exceed by design)
    if auto and fbw.get("alphaLimiter"):
        amax = max(abs(x) for x in col("aoa"))
        if amax > fbw["alphaLimiter"]:
            warn.append(f"AoA peaked {amax:.1f} deg vs {fbw['alphaLimiter']:.0f} limiter")
    if auto and fbw.get("gLimit"):
        gmax = max(col("g"))
        if gmax > 1.25 * fbw["gLimit"]:
            warn.append(f"g peaked {gmax:.1f} vs {fbw['gLimit']:.0f} limit")
    if rail > 2.0:
        warn.append(f"roll stick railed {rail:.0f}% of frames (benign when azErr converges)")
    if fail:
        print("  VERDICT: FAIL — " + "; ".join(fail))
    elif warn:
        print("  VERDICT: WARN — " + "; ".join(warn))
    else:
        print("  VERDICT: PASS — no wobble signature")


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
    fbw = fbw_params(meta)
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
        # v0.56 guard attribution (derived, see derived_guards): flag segments where the low-q
        # schedule or the AoA gate was plausibly shaping the pitch command.
        qmin, gated = derived_guards(seg["rows"], fbw)
        if qmin is not None and qmin < 0.95:
            line += f"   sched min {qmin:.2f}"
        if gated:
            line += f"   pitch gated {gated:.0f}%"
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

    # v0.55 model fit: synthetic high-q rows exactly following the g-command law at G=9, V=400
    rows = [{"t": i * 0.05, "outP": math.sin(i * 0.3) * 0.6, "spd": 400.0} for i in range(200)]
    for r in rows:
        r["pitchRate"] = -r["outP"] * 9.0 * 9.81 / r["spd"]
    mf = model_fit(rows, 170.0)
    assert mf["hi"] and abs(mf["hi"][0] - 9.0) < 0.1 and mf["hi"][1] > 0.99, mf
    assert mf["lo"] is None, mf  # no low-q rows in this capture

    # stall detector: 60 m/s with AoA swinging +-20 deg and azErr alternating at ~0.25 Hz
    # must produce DYNAMIC stall-blow-through evidence (a FAIL)...
    rows = [{"t": i * 0.1, "spd": 60.0,
             "aoa": 20.0 * math.sin(i * 0.1 * 2 * math.pi * 0.25),
             "azErr": 8.0 * math.sin(i * 0.1 * 2 * math.pi * 0.25)} for i in range(300)]
    dyn, _ = low_speed_check(rows, 170.0)
    assert dyn and any("stall blow-through" in e for e in dyn), dyn
    # ...a low-speed roll-rail with NO oscillation is rail-only evidence (v0.56: WARN, not FAIL)...
    rows = [{"t": i * 0.1, "spd": 90.0, "aoa": 5.0, "azErr": 0.1, "outR": 1.0} for i in range(300)]
    dyn, rail_ev = low_speed_check(rows, 170.0)
    assert dyn == [] and rail_ev and "roll railed" in rail_ev[0], (dyn, rail_ev)
    # ...and a calm low-speed cruise must NOT trip anything.
    rows = [{"t": i * 0.1, "spd": 60.0, "aoa": 3.0, "azErr": 0.1, "outR": 0.1} for i in range(300)]
    assert low_speed_check(rows, 170.0) == ([], [])

    # v0.56 derived guards: Trainer params (corner 130, limiter 10 => margin 1.5, fade 2.5,
    # ceiling 8.5). 80 m/s => qRatio (80/130)^2 = 0.379; aoa 9 is inside the fade band (gated),
    # aoa 3 is not (the v0.55 fixed 4/6 margins would have gated it — the Trainer bug).
    fbw = {"cornerSpeed": 130.0, "alphaLimiter": 10.0}
    qmin, gated = derived_guards([{"spd": 80.0, "aoa": 9.0}, {"spd": 130.0, "aoa": 3.0}], fbw)
    assert abs(qmin - (80.0 / 130.0) ** 2) < 1e-6, qmin
    assert abs(gated - 50.0) < 1e-6, gated
    assert derived_guards([{"spd": 80.0}], {}) == (None, None)

    # v0.57 buzz detector: 5 Hz square dither at pp 0.06 riding a slow ramp, 15 Hz sampling
    # (the Ifrit signature: sign-flips nearly every 1-2 samples after high-passing) => detected...
    rows = [{"t": i / 15.0, "outP": 0.2 * math.sin(i * 0.05) + 0.03 * (1 if (i * 5.0 / 15.0) % 1 < 0.5 else -1)}
            for i in range(300)]
    total, mod_s, pp, flip = buzz_scan(rows)
    assert total > 10.0 and pp > 0.04 and flip > 0.40, (total, pp, flip)
    # ...while the same slow ramp WITHOUT the dither stays clean (smooth maneuvering != buzz)
    rows = [{"t": i / 15.0, "outP": 0.5 * math.sin(i * 0.05)} for i in range(300)]
    assert buzz_scan(rows)[0] == 0.0
    # gap tolerance: windows spanning a recording gap are skipped, not misread
    rows = [{"t": (i if i < 100 else i + 200) / 15.0, "outP": 0.03 * (1 if i % 2 else -1)} for i in range(200)]
    total, _, _, _ = buzz_scan(rows)
    assert total > 0.0  # buzz on both sides of the gap still found

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
