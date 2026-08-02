#!/usr/bin/env python3
"""Compare N scored recorder captures (instructor-feedback-loop M0 -- noise floor + A/B testing).

scorecard.py scores ONE run; this reports the SPREAD across several runs of (nominally) the same
test card, per segment x metric: min/max/mean/stdev/stdev-as-%-of-mean, n. That is what measuring
the score noise floor and A/B-ing a control-law change both need, and hand-comparing the ~50-run
batches now being generated is not viable.

    python debugtests/compare-runs.py <rec1.csv> <rec2.csv> [more.csv ...]   # table to stdout
    python debugtests/compare-runs.py --summary <rec1.csv> [...]            # one line per (card, tag)
    python debugtests/compare-runs.py --json out.json <rec1.csv> [...]      # write out.json
    python debugtests/compare-runs.py --selftest                            # in-memory asserts

Reuses scorecard.score_run() for the actual per-run scoring (imported, not reimplemented) -- this
file only aggregates already-scored results.

GROUPING: runs are grouped by (airframe, CARD, arm).

  * airframe -- the .airframe.json sidecar's "jsonKey", e.g. "Multirole1" / "trainer", falling back
    to the CSV's "# aircraft" header when the sidecar is missing or that key is absent, and to a
    per-file singleton group when neither is available. Pooling two different airframes into one
    spread is meaningless (a heavier jet's turn rate is not noise around a trainer's) -- exactly the
    mistake that wasted a prior test session -- so groups are NEVER merged; each is scored and
    printed separately, and a mixed input warns loudly on stderr (and in the table) about the split
    rather than silently averaging across it. Note: the sidecar's jsonKey and the CSV header's
    aircraft name can differ only in case for the SAME airframe (observed on real captures: jsonKey
    "trainer" vs header "Trainer") -- the key is lowercased before grouping so that difference alone
    can never fragment one airframe into two.
  * card -- the capture's `# card` header line. Segments are keyed by TAG ALONE below, and tags are
    unique per card BY CONVENTION ONLY (cards/README.md's rule). That convention already leaks:
    `hover` and `bobup` are emitted both by the rotor disk cards and by the built-in rotorcraft-v2,
    so a batch flying both pooled two different stimuli into one spread and inflated the noise floor
    it exists to measure. Grouping on the card makes the convention unnecessary rather than merely
    documented. A hand-recorded free flight has no `# card` line -> None, shared by every such
    capture, so those still group by airframe exactly as before (a constant cannot fragment a group).
  * arm -- see A/B ARMS below.

TRUNCATION: a card segment has a fixed scripted duration, so if two runs share a segTag but one
CSV's copy is materially shorter, that run was aborted or cut off mid-segment (manual stick input,
altitude floor, end of session) -- comparing its metrics against a full-duration run would be
comparing unequal stimuli, not noise. Per segment, the longest observed durationS among the group's
runs is treated as "full"; any run whose copy is under 95% of that is excluded from the spread and
listed separately as truncated (never silently dropped without a trace).

Only segments shared by >=2 runs within a group produce a spread row (nothing to compare with one
run). `arm` (already excluded from scoring by scorecard.py) carries no metrics and is skipped here
too. No confidence intervals or hypothesis tests: with n=2..6 runs those would be misleading --
plain spread numbers only.

SUMMARY (--summary): the full table prints every metric of every segment of every group, which is
right for the 2-12 captures this was built for and is thousands of lines for the 100-450 an
unattended batch now produces. --summary collapses each segment to ONE line -- n, duration, the
worst rail, and three headline metrics as `mean +- stdev%` -- so ~40 (card, tag) pairs fit on a
screen and the question "which regime does the law fly worst" is answerable by reading down a
column. Nothing is recomputed for it: it is the same spread(), printed narrow.

A/B ARMS (v0.84): a capture's `# config` header may carry `arm=0|1` (which side of an ABBA-
interleaved batch this run flew) and `armKnob=<name>` (which Cfg toggle was being swept). Arm is part
of the group key -- an arm split is never pooled into its sibling any more than one airframe is
pooled into another, for the mirror-image reason: averaging arm 0 into arm 1 erases the exact effect
the interleaving exists to measure. A capture with no `arm=` at all (every one of the ~162 captures
predating v0.84) parses as arm `None` and groups exactly as before -- one group per airframe+card,
not per (airframe, card, None), since every legacy run shares that same None. When an (airframe,
card) has both arms present, an ARM COMPARISON section reports the A-vs-B mean difference per segment
per metric next to each arm's own within-group spread (the noise floor the difference must clear).
Runs that disagree about `armKnob` -- two different experiments wearing the same arm numbers -- are
warned about loudly rather than silently diffed, and so is an arm schedule unbalanced by SUM OF RUN
INDEX (equal per-arm COUNTS are not enough -- see ScenarioPlayer.cs's own ArmOf()/ToggleSuite
comment, e.g. ABBAAB has equal counts and is still confounded by a monotonic within-batch drift).
"""
import sys, os, re, json, statistics
import importlib.util as _ilu

# --- reuse scorecard.py's scoring (hyphenated filename => can't `import`; same pattern scorecard.py
# itself uses to reach analyze-wobble.py) ---------------------------------------------------------
_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = _ilu.spec_from_file_location("scorecard", os.path.join(_HERE, "scorecard.py"))
sc = _ilu.module_from_spec(_spec)
_spec.loader.exec_module(sc)

FULL_FRAC = 0.95  # a segment copy under this fraction of the group's longest copy is "truncated"


# --- scoring + grouping ---------------------------------------------------------------------------

def score_files(paths):
    """[(path, score_result), ...] via scorecard.score_run -- the only place this module touches disk."""
    return [(p, sc.score_run(p)) for p in paths]


def airframe_key(result, path):
    """Grouping key: prefer the sidecar's jsonKey (the game's stable class id), fall back to the CSV
    header's aircraft name, fall back to a per-file singleton so an unidentifiable run is NEVER
    silently pooled with anything else. Lowercased: real captures show the sidecar's jsonKey and the
    CSV's aircraft name differing only in case for the same airframe ("trainer" vs "Trainer"), and a
    batch where some runs are missing a sidecar must not fragment on that alone."""
    prov = result["provenance"]
    info = prov.get("airframeInfo") or {}
    if info.get("jsonKey"):
        return info["jsonKey"].lower()
    if prov.get("aircraft"):
        return prov["aircraft"].lower()
    return f"<unknown airframe: {os.path.basename(path)}>"


def card_key(result):
    """The card this capture flew, off the `# card` header line (scorecard.provenance parses it), or
    None for a hand-recorded free flight. Part of the group key because compare_group() keys segments
    by TAG ALONE and tags are only unique per card by convention -- `hover`/`bobup` are shared between
    the rotor disk cards and the built-in rotorcraft-v2, so a mixed batch pooled two stimuli into one
    spread. `or None` normalises an empty `# card ` line onto the same key as no line at all."""
    return result["provenance"].get("card") or None


def arm_key(result):
    """(arm, armKnob) parsed off this run's raw '# config' header line. arm comes from scorecard's
    own cfg_params() -- the same numeric-knob regex every other '# config' value already goes
    through, no reimplementation needed (arm=0/1 is a bare number). armKnob is read directly because
    that regex is numeric-only and armKnob=<name> is not. (None, None) for every capture before
    v0.84 -- no `arm=` on the header at all -- which is the intentional "single unnamed arm" case."""
    cfg = result["provenance"].get("config", "")
    arm = sc.cfg_params({"cfg": cfg}).get("arm")
    m = re.search(r"armKnob=(\S+)", cfg)
    return (int(arm) if arm is not None else None), (m.group(1) if m else None)


def group_runs(results):
    """results: [(path, score_result), ...] (already scored -- via score_files, or fabricated by a
    test). -> [((airframe_key, card, arm), [(path, score_result), ...]), ...], insertion-ordered by
    first appearance of each key so output order is stable and matches input order. card and arm are
    both None for a pre-v0.71 / pre-v0.84 capture respectively, so a legacy batch groups by airframe
    alone exactly as before -- every run in it shares those same Nones, and a constant never
    fragments a group."""
    groups, order = {}, []
    for path, result in results:
        arm, _ = arm_key(result)
        key = (airframe_key(result, path), card_key(result), arm)
        if key not in groups:
            groups[key] = []
            order.append(key)
        groups[key].append((path, result))
    return [(k, groups[k]) for k in order]


def _pool_warning(pairs):
    """None, or the "don't pool these" message, when the input spans more than one DISTINCT airframe.
    `pairs`: [(airframe, runs), ...] -- the same airframe can legitimately appear more than once (one
    entry per arm), so airframes are deduped and their run counts summed before deciding whether to
    warn. An arm split is never what this warning is about -- comparing arm 0 vs arm 1 of the SAME
    airframe is the entire point of interleaving, not the cross-airframe mistake this guard exists
    to catch."""
    counts, order = {}, []
    for af, runs in pairs:
        if af not in counts:
            counts[af] = 0
            order.append(af)
        counts[af] += len(runs)
    if len(order) < 2:
        return None
    bits = ", ".join(f"{af} x{counts[af]}" for af in order)
    return f"input spans {len(order)} airframes ({bits}) -- scored SEPARATELY per airframe, never pooled."


# --- per-group comparison --------------------------------------------------------------------------

def _segments_by_tag(runs):
    """{tag: [(path, seg), ...]} over non-excluded segments (i.e. not "arm"), across this group's runs."""
    by_tag = {}
    for path, result in runs:
        for seg in result["segments"]:
            if seg["excluded"]:
                continue
            by_tag.setdefault(seg["tag"], []).append((path, seg))
    return by_tag


def spread(values):
    """min/max/mean/stdev/stdev-as-%-of-mean over a list of floats. stdev needs n>=2 -- this reports
    a population of measurements, not a stats library, so n=1 gets None rather than a fake 0.0."""
    n = len(values)
    if n == 0:
        return None
    mean = statistics.fmean(values)
    sd = statistics.stdev(values) if n >= 2 else None
    return {
        "min": min(values), "max": max(values), "mean": mean, "stdev": sd,
        "stdevPctOfMean": (100.0 * sd / abs(mean)) if (sd is not None and mean != 0) else None,
        "n": n,
    }


def compare_group(runs):
    """[{"tag", "fullDurationS", "nFull", "truncatedRuns": {basename: durationS}, "metrics":
    {name: spread()}}] for one group's segments shared by >=2 of its runs. nFull is how many runs
    contributed (the full-duration ones) -- the row's own n, which --summary prints once instead of
    repeating each metric's."""
    out = []
    for tag, entries in _segments_by_tag(runs).items():
        if len(entries) < 2:
            continue  # nothing to compare with one run
        full = max(seg["durationS"] for _, seg in entries)
        ok = [(p, seg) for p, seg in entries if seg["durationS"] >= FULL_FRAC * full]
        truncated = {os.path.basename(p): seg["durationS"] for p, seg in entries
                     if seg["durationS"] < FULL_FRAC * full}
        row = {"tag": tag, "fullDurationS": full, "nFull": len(ok), "truncatedRuns": truncated,
               "metrics": {}}
        if len(ok) >= 2:
            names = set()
            for _, seg in ok:
                names.update(seg["metrics"].keys())
            for name in sorted(names):
                vals = [seg["metrics"][name]["value"] for _, seg in ok
                        if name in seg["metrics"] and seg["metrics"][name]["value"] is not None]
                sp = spread(vals)
                if sp:
                    row["metrics"][name] = sp
        out.append(row)
    return out


# --- A/B arm comparison -----------------------------------------------------------------------

def _group_knob(runs):
    """(knob, warning) -- the single armKnob name if every run in `runs` that names one agrees; a
    loud warning instead when two or more DIFFERENT non-null names are found (mixing two unrelated
    A/B experiments that happen to reuse the same arm numbers -- ScenarioPlayer.cs itself can never
    produce this from one suite, so it always means stray files got mixed into the batch). Runs with
    no armKnob at all (pre-v0.84) are simply ignored, not counted as disagreement."""
    knobs = {}
    for path, result in runs:
        _, knob = arm_key(result)
        if knob:
            knobs.setdefault(knob, []).append(os.path.basename(path))
    if len(knobs) > 1:
        bits = ", ".join(f"{k} ({len(v)} run(s): {', '.join(v)})" for k, v in knobs.items())
        return None, f"armKnob disagreement: {bits} -- these are NOT the same A/B experiment."
    if knobs:
        return next(iter(knobs)), None
    return None, None


def _run_order(runs):
    """[(path, result, idx)], idx 0-based by ascending `rec=` header value (the within-session
    recording counter, present since v0.63 -- before arm even existed) when every run in `runs` has
    one; falls back to input order otherwise so a missing/unparsable `rec` makes the balance check
    less exact rather than crashing it."""
    recs = []
    for _, result in runs:
        r = result["provenance"].get("rec")
        try:
            recs.append(int(r))
        except (TypeError, ValueError):
            recs.append(None)
    order = (sorted(range(len(runs)), key=lambda i: recs[i]) if recs and all(r is not None for r in recs)
              else range(len(runs)))
    return [(runs[i][0], runs[i][1], idx) for idx, i in enumerate(order)]


def _arm_balance_warning(runs):
    """None, or a warning, for one airframe's A/B schedule -- `runs` is every run carrying an arm (0
    and 1 together) for that airframe. Mirrors ScenarioPlayer.cs's own ToggleSuite check: balance is
    on the SUM OF RUN INDICES per arm, not the per-arm COUNT. Equal counts are not the point -- ABBA
    works by giving both arms the same average position in the batch so a trend linear in run order
    cancels; ABBAAB (n=6) has equal counts and still leans A early, and only the sum check catches it."""
    n = {0: 0, 1: 0}
    s = {0: 0, 1: 0}
    for path, result, idx in _run_order(runs):
        arm, _ = arm_key(result)
        if arm in (0, 1):
            n[arm] += 1
            s[arm] += idx
    if n[0] == 0 or n[1] == 0 or (n[0] == n[1] and s[0] == s[1]):
        return None
    return (f"arm schedule is UNBALANCED over {n[0] + n[1]} run(s): {n[0]} arm0 / {n[1]} arm1, "
            f"sum of run index {s[0]} vs {s[1]} (equal COUNTS alone would not have caught this -- "
            f"e.g. ABBAAB has equal counts and is still confounded). One arm sits earlier/later in "
            f"the batch, so a one-way session drift leans on it instead of cancelling out. Use a "
            f"replicate count that is a MULTIPLE OF 4.")


def arm_diff(runs_a, runs_b):
    """[{"tag", "metrics": {name: {"meanA","meanB","diff","stdevA","stdevB","nA","nB","aboveNoise"}}}]
    -- the A-vs-B difference per segment per metric, built directly on compare_group()'s own per-arm
    spread (the noise floor) rather than re-deriving it. Only a segment/metric present on BOTH arms
    (>=2 runs, at full duration, same "nothing to compare with one" discipline as compare_group())
    produces a row. aboveNoise is None when neither arm has n>=2 for that metric (no floor to compare
    against) -- true/false otherwise, comparing |diff| to the LARGER of the two arms' stdev (a plain
    eyeball rule, not a hypothesis test -- consistent with the rest of this module)."""
    rows_a = {r["tag"]: r["metrics"] for r in compare_group(runs_a)}
    rows_b = {r["tag"]: r["metrics"] for r in compare_group(runs_b)}
    out = []
    for tag, ma in rows_a.items():
        mb = rows_b.get(tag)
        if not mb:
            continue
        metrics = {}
        for name, spa in ma.items():
            spb = mb.get(name)
            if not spb:
                continue
            diff = spb["mean"] - spa["mean"]
            candidates = [v for v in (spa["stdev"], spb["stdev"]) if v is not None]
            noise = max(candidates) if candidates else None
            metrics[name] = {
                "meanA": spa["mean"], "meanB": spb["mean"], "diff": diff,
                "stdevA": spa["stdev"], "stdevB": spb["stdev"], "nA": spa["n"], "nB": spb["n"],
                "aboveNoise": (abs(diff) > noise) if noise is not None else None,
            }
        if metrics:
            out.append({"tag": tag, "metrics": metrics})
    return out


def _arm_comparisons(raw_groups):
    """[{"airframe", "card", "armA", "armB", "armKnob", "nA", "nB", "armKnobWarning",
    "balanceWarning", "segments"}] -- one entry per (airframe, CARD) that has BOTH arm 0 and arm 1
    present in the input (nothing to compare with only one side; more than two is outside what
    ArmOf()/the F1 toggle can even produce, so it's left unhandled rather than guessed at). Pairing
    on the card as well as the airframe for the same reason grouping does: arm 0 of one card against
    arm 1 of another is not an A/B of the knob, it is an A/B of the two cards. "segments" is None --
    not diffed -- when armKnobWarning fires, since arm 0 vs arm 1 would then be two different
    toggles."""
    by_af, order = {}, []
    for (af, card, arm), runs in raw_groups:
        if (af, card) not in by_af:
            by_af[(af, card)] = {}
            order.append((af, card))
        by_af[(af, card)][arm] = runs
    out = []
    for af, card in order:
        arms = by_af[(af, card)]
        if 0 not in arms or 1 not in arms:
            continue
        armed_runs = arms[0] + arms[1]
        knob, knob_warning = _group_knob(armed_runs)
        bal_warning = _arm_balance_warning(armed_runs)
        out.append({
            "airframe": af, "card": card, "armA": 0, "armB": 1, "armKnob": knob,
            "nA": len(arms[0]), "nB": len(arms[1]),
            "armKnobWarning": knob_warning, "balanceWarning": bal_warning,
            "segments": None if knob_warning else arm_diff(arms[0], arms[1]),
        })
    return out


def _run_warnings(runs):
    """{norm_warning key: (count, one real example)} over a group's runs -- scorecard's own per-run
    warnings (unknown segment tag, RAILED segment) carried through so they reach the page a batch is
    actually read on. Deduped by scorecard.norm_warning (every number masked) with a count, because 8
    replicates of one card emit 8 copies of the same finding differing only in "blendRailPct=100.0%"
    vs "=97.3%" -- and 8 copies of a warning is how a warning stops being read."""
    counts = {}
    for _, result in runs:
        for w in result.get("warnings", []):
            key = sc.norm_warning(w)
            n, example = counts.get(key, (0, w))
            counts[key] = (n + 1, example)
    return counts


def compare_all(paths):
    """(groups, armComparisons). groups: [{"airframe", "card", "arm", "armKnob", "armKnobWarning",
    "runWarnings", "runs": [basename...], "segments": compare_group(...)}, ...], one per (airframe,
    card, arm) group -- card is None for a hand-recorded capture, arm/armKnob None for a pre-v0.84
    one (the backward-compatible cases). armComparisons: see _arm_comparisons() -- the A-vs-B report
    for any (airframe, card) with both arms present, [] when nothing in the input is armed."""
    raw_groups = group_runs(score_files(paths))
    groups = []
    for (af, card, arm), runs in raw_groups:
        knob, knob_warning = _group_knob(runs)
        groups.append({
            "airframe": af, "card": card, "arm": arm, "armKnob": knob,
            "armKnobWarning": knob_warning, "runWarnings": _run_warnings(runs),
            "runs": [os.path.basename(p) for p, _ in runs], "segments": compare_group(runs),
        })
    # No printing here — print_table() emits warnings in table mode and main() does it on stderr for
    # --json. Warning from both paths double-printed it in the common case.
    return groups, _arm_comparisons(raw_groups)


# --- output ---------------------------------------------------------------------------------

def _fmt(sp):
    sd = f"{sp['stdev']:.3g}" if sp["stdev"] is not None else "n/a"
    pct = f"{sp['stdevPctOfMean']:.1f}%" if sp["stdevPctOfMean"] is not None else "n/a"
    return f"min={sp['min']:.3g} max={sp['max']:.3g} mean={sp['mean']:.3g} stdev={sd} ({pct}) n={sp['n']}"


def _noise_label(above):
    return "n/a (n=1 on at least one side)" if above is None else "ABOVE noise floor" if above else "within noise floor"


def _runs_label(runs):
    """"a.csv, b.csv, c.csv (+9 more)" -- the filenames of a group, truncated. A batch replicate
    count of 8-12 is now normal and the full list wrapped the heading off the screen; 4 or fewer
    still print in full, since that is where naming every file is genuinely useful."""
    if len(runs) <= 4:
        return ", ".join(runs)
    return ", ".join(runs[:3]) + f" (+{len(runs) - 3} more)"


def _group_label(g):
    label = f"airframe: {g['airframe']}"
    if g.get("card"):
        label += f"  card: {g['card']}"
    if g.get("arm") is not None:
        label += f"  arm {g['arm']}" + (f" ({g['armKnob']})" if g.get("armKnob") else "")
    return label


def _print_group_warnings(g):
    if g.get("armKnobWarning"):
        print(f"  WARNING: {g['armKnobWarning']}")
    n = len(g["runs"])
    for cnt, example in sorted(g.get("runWarnings", {}).values(), key=lambda v: -v[0]):
        print(f"  WARNING [{cnt}/{n} runs, e.g.]: {example}")


def print_table(groups, arm_comparisons=()):
    w = _pool_warning([(g["airframe"], g["runs"]) for g in groups])
    if w:
        print(f"WARNING: {w}")
    for g in groups:
        print(f"\n=== {_group_label(g)}  ({len(g['runs'])} runs: {_runs_label(g['runs'])})")
        _print_group_warnings(g)
        if len(g["runs"]) < 2:
            print("  only 1 run -- nothing to compare.")
            continue
        if not g["segments"]:
            print("  no segment shared by >=2 runs.")
            continue
        for seg in g["segments"]:
            trunc = seg["truncatedRuns"]
            flag = ("  [TRUNCATED: " + ", ".join(f"{k}={v:.1f}s" for k, v in trunc.items())
                    + f" vs full {seg['fullDurationS']:.1f}s]") if trunc else ""
            print(f"  {seg['tag']:<12s}{flag}")
            if not seg["metrics"]:
                print("      (fewer than 2 runs at full duration -- comparison skipped)")
                continue
            for name, sp in seg["metrics"].items():
                print(f"      {name:<28s} {_fmt(sp)}")

    for ac in arm_comparisons:
        knob = f" on {ac['armKnob']}" if ac["armKnob"] else ""
        card = f"  card: {ac['card']}" if ac.get("card") else ""
        print(f"\n=== airframe: {ac['airframe']}{card}  ARM COMPARISON{knob}  "
              f"(A=arm{ac['armA']} n={ac['nA']}, B=arm{ac['armB']} n={ac['nB']})")
        if ac["armKnobWarning"]:
            print(f"  WARNING: {ac['armKnobWarning']}")
        if ac["balanceWarning"]:
            print(f"  WARNING: {ac['balanceWarning']}")
        if ac["segments"] is None:
            continue
        if not ac["segments"]:
            print("  no segment/metric shared by both arms at full duration.")
            continue
        for seg in ac["segments"]:
            print(f"  {seg['tag']}")
            for name, d in seg["metrics"].items():
                print(f"      {name:<28s} A mean={d['meanA']:.3g} n={d['nA']}   "
                      f"B mean={d['meanB']:.3g} n={d['nB']}   diff(B-A)={d['diff']:+.3g}   "
                      f"[{_noise_label(d['aboveNoise'])}]")


# --- summary output --------------------------------------------------------------------------
# THREE metric columns, fixed width, chosen so every row has the same shape whatever the segment
# type: two that pointing_metrics computes for EVERY type, then the first present of the three
# type-specific headliners (a turn row shows its turn rate, a step row its settle time or its
# overshoot). More columns would not fit a terminal at ~40 rows, and the full table is one flag away
# for anything this points at.
SUMMARY_ALWAYS = (("term", "terminalOffDeg"), ("rms", "rmsPointingErrorDeg"))
SUMMARY_EITHER = (("turn", "meanTurnRateDegS"), ("settle", "settleTime"), ("oversh", "overshootDeg"))
# The rails, in one column: scorecard.RAIL_METRICS with short labels. Printed on every row, not only
# past scorecard's RAILED threshold, because "this card sits at 40% clamp" is the context that says
# whether the NEXT batch on it can move at all -- a threshold flag alone can't show that trend.
SUMMARY_RAILS = (("bank", "bankClampActivePct"), ("cap", "turnRateCapActivePct"),
                 ("blend", "blendRailPct"), ("aoa", "aoaAboveCeilingPct"))


def _cell(sp):
    """`mean+-stdev%` for one metric, or '-' when the metric is absent. stdev% (not absolute stdev)
    because the point of a summary row is comparing REGIMES against each other, and 8% spread means
    the same thing on a 3 deg terminal error as on a 19 deg/s turn rate."""
    if sp is None:
        return "-"
    # "n/a", not "0%", when there is no stdev: n=1, or a mean of 0 to divide by. A printed "+-0%"
    # must only ever mean "measured, and every run agreed".
    pct = f"{sp['stdevPctOfMean']:.0f}%" if sp["stdevPctOfMean"] is not None else "n/a"
    return f"{sp['mean']:.3g}+-{pct}"


def _sat_cell(metrics):
    """The WORST rail on this segment as `<name><pct>%`, or 'none' / '-'. One column: the four rails
    are alternatives, not additives -- any single one near 100% already means a gain change cannot
    move this segment (see scorecard.rail_warning, which thresholds the same numbers). The cases are
    kept distinct on purpose: a named rail, 'none' (rails measured, all idle) and '-' (nothing
    measured at all, e.g. a pre-v0.85 capture with no bWt column) are three different findings, and
    naming whichever rail happens to sort last when they are all 0.0% would read as the first.

    R40 removed the `auth<pct>%` fallback with the metric behind it: scorecard's authorityUsedFrac
    was mean|bank|/maxBank, and bank in a coordinated turn is pinned by atan(omega*V/g) before the
    law runs, so the column was reporting the CARD's demand in the rows where it had room to print.
    An empty half-column is better than a plausible wrong one."""
    live = [(metrics[k]["mean"], lbl) for lbl, k in SUMMARY_RAILS if k in metrics]
    if live:
        v, lbl = max(live)
        if v >= 0.5:
            return f"{lbl}{v:.0f}%"
    return "none" if live else "-"


def _trunc(s, w):
    return s if len(s) <= w else s[:w - 1] + "~"


def print_summary(groups):
    """One line per (card, segment tag) -- the mode that makes a 300-capture batch readable. Group
    order and within-group segment order are preserved (i.e. the order the card flies its segments),
    NOT sorted: a card read out of order stops being a story about a flight."""
    w = _pool_warning([(g["airframe"], g["runs"]) for g in groups])
    if w:
        print(f"WARNING: {w}")
    hdr = (f"{'airframe':<11} {'card':<20} {'tag':<10} {'arm':>3} {'n':>3} {'dur':>6} "
           f"{'rail/auth':>9}  " + "".join(f"{lbl:<16}" for lbl, _ in SUMMARY_ALWAYS) + "type-specific")
    print(hdr)
    print("-" * len(hdr))
    for g in groups:
        if not g["segments"]:
            # A group with nothing shared by >=2 runs produces no rows at all -- and in a 300-file
            # batch it would then simply not appear, which reads as "that card wasn't flown" instead
            # of "it was flown once". One line, so the absence is visible.
            print(f"{_trunc(g['airframe'], 11):<11} {_trunc(g.get('card') or '-', 20):<20} "
                  f"{'--':<10} {'-' if g.get('arm') is None else g['arm']:>3} "
                  f"{len(g['runs']):>3}  (no segment shared by >=2 of its runs)")
            continue
        for seg in g["segments"]:
            met = seg["metrics"]
            cells = [f"{lbl} {_cell(met.get(k))}" for lbl, k in SUMMARY_ALWAYS]
            pick = next(((lbl, k) for lbl, k in SUMMARY_EITHER if k in met), None)
            cells.append(f"{pick[0]} {_cell(met[pick[1]])}" if pick else "-")
            trunc = f" [{len(seg['truncatedRuns'])} TRUNC]" if seg["truncatedRuns"] else ""
            print(f"{_trunc(g['airframe'], 11):<11} {_trunc(g.get('card') or '-', 20):<20} "
                  f"{_trunc(seg['tag'], 10):<10} {'-' if g.get('arm') is None else g['arm']:>3} "
                  f"{seg.get('nFull', 0):>3} {seg['fullDurationS']:>5.1f}s {_sat_cell(met):>9}  "
                  + "".join(f"{c:<16}" for c in cells[:-1]) + cells[-1] + trunc)
    # Warnings AFTER the table and deduped across the whole batch: the railed segments are already
    # visible in the worstRail column, so what these add is the ones no column can carry (an
    # unrecognised tag = a metric set silently not computed).
    seen = {}
    for g in groups:
        for key, (cnt, example) in g.get("runWarnings", {}).items():
            n, ex = seen.get(key, (0, example))
            seen[key] = (n + cnt, ex)
    for cnt, example in sorted(seen.values(), key=lambda v: -v[0]):
        print(f"WARNING [{cnt} run(s), e.g.]: {example}")


# --- selftest ---------------------------------------------------------------------------------

def _fake_seg(tag, duration, **metrics):
    return {"tag": tag, "type": "x", "samples": 10, "durationS": duration, "excluded": False,
            "metrics": {k: {"value": v, "grade": None} for k, v in metrics.items()}, "skipped": {}}


def _fake_result(aircraft, json_key, segs, config="", rec=None, card=None, warnings=()):
    prov = {}
    if aircraft:
        prov["aircraft"] = aircraft
    if json_key:
        prov["airframeInfo"] = {"jsonKey": json_key}
    if config:
        prov["config"] = config
    if rec is not None:
        prov["rec"] = str(rec)
    if card:
        prov["card"] = card
    return {"provenance": prov, "segments": segs, "warnings": list(warnings)}


def _armed(rec, arm, tag="az10", val=2.0, knob="Lead", airframe=("X", "X"), card=None):
    """One fake (path, result) run carrying a v0.84 arm=/armKnob= header + rec= -- the shape the new
    arm-comparison/balance-check tests below build batches out of."""
    cfg = f"arm={arm} armKnob={knob}"
    return (f"r{rec}.csv", _fake_result(airframe[0], airframe[1], [_fake_seg(tag, 15.0, gPeak=val)],
                                         cfg, rec=rec, card=card))


def selftest():
    # airframe_key: sidecar jsonKey wins, then the CSV aircraft header, then a per-file singleton --
    # and case alone (the real "trainer" vs "Trainer" split seen in actual captures) must not matter.
    assert airframe_key(_fake_result("KR-67 Ifrit", "Multirole1", []), "a.csv") == "multirole1"
    assert airframe_key(_fake_result("Trainer", "trainer", []), "b.csv") == "trainer"
    assert airframe_key(_fake_result("Trainer", None, []), "b2.csv") == "trainer"   # header fallback
    assert airframe_key(_fake_result(None, None, []), "c.csv") == "<unknown airframe: c.csv>"

    # grouping never pools two different airframes, even interleaved in the input order (the
    # explicit requirement: this is the mistake that wasted a prior test session).
    r1 = ("r1.csv", _fake_result("KR-67 Ifrit", "Multirole1", [_fake_seg("az10", 15.0, gPeak=2.0)]))
    r2 = ("r2.csv", _fake_result("Trainer", "trainer", [_fake_seg("az10", 15.0, gPeak=1.0)]))
    r3 = ("r3.csv", _fake_result("KR-67 Ifrit", "Multirole1", [_fake_seg("az10", 15.0, gPeak=2.4)]))
    groups = group_runs([r1, r2, r3])
    assert [k for k, _ in groups] == [("multirole1", None, None), ("trainer", None, None)], groups
    gd = dict(groups)
    assert len(gd[("multirole1", None, None)]) == 2 and len(gd[("trainer", None, None)]) == 1, groups
    as_af_pairs = lambda gs: [(af, runs) for (af, _c, _arm), runs in gs]     # group_runs -> _pool_warning shape
    pw = _pool_warning(as_af_pairs(groups))
    assert pw is not None and "2 airframes" in pw, pw
    assert _pool_warning(as_af_pairs(group_runs([r1, r3]))) is None    # a single airframe: nothing to warn about

    # --- CARD in the group key ------------------------------------------------------------------
    # The leak this closes, in its real shape: `hover` is emitted by BOTH the rotor disk card and the
    # built-in rotorcraft-v2, so one airframe flying both used to pool two different stimuli into one
    # spread -- inflating the very noise floor the tool exists to measure. Two cards, same airframe,
    # same tag: two groups, and neither one's spread contains the other's value.
    c1 = ("c1.csv", _fake_result("Helo", "helo1", [_fake_seg("hover", 20.0, positionRMSM=1.0)],
                                 card="rotorcraft-v2"))
    c2 = ("c2.csv", _fake_result("Helo", "helo1", [_fake_seg("hover", 20.0, positionRMSM=1.2)],
                                 card="rotorcraft-v2"))
    c3 = ("c3.csv", _fake_result("Helo", "helo1", [_fake_seg("hover", 20.0, positionRMSM=9.0)],
                                 card="rotor-hover"))
    c4 = ("c4.csv", _fake_result("Helo", "helo1", [_fake_seg("hover", 20.0, positionRMSM=9.4)],
                                 card="rotor-hover"))
    cg = group_runs([c1, c3, c2, c4])                     # interleaved, as a real batch folder is
    assert [k for k, _ in cg] == [("helo1", "rotorcraft-v2", None), ("helo1", "rotor-hover", None)], cg
    spreads = {k[1]: compare_group(runs)[0]["metrics"]["positionRMSM"] for k, runs in cg}
    assert abs(spreads["rotorcraft-v2"]["mean"] - 1.1) < 1e-9, spreads     # 9.0/9.4 never leaked in
    assert abs(spreads["rotor-hover"]["mean"] - 9.2) < 1e-9, spreads
    assert spreads["rotorcraft-v2"]["n"] == 2 and spreads["rotor-hover"]["n"] == 2, spreads
    # ...and a capture with NO card at all (a hand-recorded free flight) groups sanely: card None is
    # shared by every such run, so they land in ONE group, exactly as before this key existed.
    assert card_key(_fake_result("X", "X", [])) is None
    assert card_key(_fake_result("X", "X", [], card="fixedwing-v2")) == "fixedwing-v2"
    nocard = group_runs([("f1.csv", _fake_result("Trainer", "trainer", [_fake_seg("fine", 20.0, gPeak=1.0)])),
                         ("f2.csv", _fake_result("Trainer", "trainer", [_fake_seg("fine", 20.0, gPeak=1.4)]))])
    assert [k for k, _ in nocard] == [("trainer", None, None)], nocard
    assert len(nocard[0][1]) == 2 and compare_group(nocard[0][1])[0]["metrics"]["gPeak"]["n"] == 2, nocard
    # A card is not an airframe: two cards on one airframe must NOT trip the don't-pool warning.
    assert _pool_warning(as_af_pairs(cg)) is None, _pool_warning(as_af_pairs(cg))

    # --- v0.84 A/B arm interleaving -----------------------------------------------------------
    # Two-arm grouping: same airframe, arm=0 and arm=1 split into two distinct groups, in
    # first-appearance order, and the split is NOT a "don't pool across airframes" warning (that
    # guard is about DIFFERENT airframes; comparing arm 0 vs arm 1 of the SAME one is the point).
    a1, a2 = _armed(1, 0, val=2.0), _armed(4, 0, val=2.2)
    b1, b2 = _armed(2, 1, val=3.0), _armed(3, 1, val=3.4)
    armed_groups = group_runs([a1, b1, b2, a2])
    assert [k for k, _ in armed_groups] == [("x", None, 0), ("x", None, 1)], armed_groups
    assert _pool_warning(as_af_pairs(armed_groups)) is None, _pool_warning(as_af_pairs(armed_groups))

    # arm_key: numeric arm via cfg_params (unchanged, reused), armKnob via its own regex, and
    # (None, None) for a capture with no '# config' arm= at all (every pre-v0.84 capture).
    assert arm_key(_fake_result("X", "X", [], "law=EvolvedLegacy arm=0 armKnob=RelativeTurnLead sens=3")) \
        == (0, "RelativeTurnLead")
    assert arm_key(_fake_result("X", "X", [], "arm=1 armKnob=RelativeTurnLead heliFwd=150")) == (1, "RelativeTurnLead")
    assert arm_key(_fake_result("X", "X", [])) == (None, None)

    # Backward compatibility: a batch with no arm= at all (every one of the 162 pre-v0.84 captures)
    # groups by airframe alone, exactly as before -- one group, not fragmented by a constant None.
    legacy = [("l1.csv", _fake_result("Trainer", "trainer", [_fake_seg("az10", 15.0, gPeak=1.0)])),
              ("l2.csv", _fake_result("Trainer", "trainer", [_fake_seg("az10", 15.0, gPeak=1.2)]))]
    lg = group_runs(legacy)
    assert [k for k, _ in lg] == [("trainer", None, None)], lg
    assert len(lg[0][1]) == 2, lg
    legacy_spread = compare_group(lg[0][1])
    assert abs(legacy_spread[0]["metrics"]["gPeak"]["mean"] - 1.1) < 1e-9, legacy_spread   # unaffected by arm work

    # armKnob disagreement: two runs claiming different toggles under the same arm numbers -- must
    # warn loudly, and _arm_comparisons must refuse to diff arm 0 vs arm 1 for it (not silently pool).
    mismatch_a = _armed(1, 0, knob="RelativeTurnLead")
    mismatch_b = _armed(2, 1, knob="MarkerRateFeedForward")
    knob, warn = _group_knob([mismatch_a, mismatch_b])
    assert knob is None and warn is not None, (knob, warn)
    assert "RelativeTurnLead" in warn and "MarkerRateFeedForward" in warn, warn
    mismatch_groups = group_runs([mismatch_a, mismatch_b])
    acs = _arm_comparisons(mismatch_groups)
    assert len(acs) == 1 and acs[0]["armKnobWarning"] is not None, acs
    assert acs[0]["segments"] is None, acs      # refused to diff, not silently pooled

    # index-balance: canonical ABBA (n=4) is balanced (equal counts AND equal sum of run index);
    # ABBAAB (n=6) has EQUAL COUNTS (3/3) but is still flagged -- the whole point of the sum check.
    balanced = [_armed(1, 0), _armed(2, 1), _armed(3, 1), _armed(4, 0)]
    assert _arm_balance_warning(balanced) is None, _arm_balance_warning(balanced)
    unbalanced = [_armed(1, 0), _armed(2, 1), _armed(3, 1), _armed(4, 0), _armed(5, 0), _armed(6, 1)]
    nA = sum(1 for _, r in unbalanced if arm_key(r)[0] == 0)
    assert nA == 3 and len(unbalanced) - nA == 3, nA        # equal counts...
    w = _arm_balance_warning(unbalanced)
    assert w is not None and "UNBALANCED" in w, w            # ...still flagged

    # end-to-end: a clean, balanced, agreeing-knob 2-arm batch produces a real A-vs-B diff, and the
    # diff clears the (tiny, synthetic) within-arm noise floor so aboveNoise reads True.
    clean_a = [_armed(1, 0, val=2.0), _armed(4, 0, val=2.2)]
    clean_b = [_armed(2, 1, val=3.0), _armed(3, 1, val=3.4)]
    clean_acs = _arm_comparisons(group_runs(clean_a + clean_b))
    assert len(clean_acs) == 1, clean_acs
    ac = clean_acs[0]
    assert ac["armKnobWarning"] is None and ac["balanceWarning"] is None, ac
    assert ac["armKnob"] == "Lead", ac
    m = ac["segments"][0]["metrics"]["gPeak"]
    assert abs(m["meanA"] - 2.1) < 1e-9 and abs(m["meanB"] - 3.2) < 1e-9, m
    assert abs(m["diff"] - 1.1) < 1e-9, m
    assert m["aboveNoise"] is True, m

    # print_table must survive an arm-comparison batch (labels + WARNING lines + the diff table)
    # without crashing, and a legacy dict missing "arm"/"armKnob" entirely (the pre-existing shape)
    # must still render via .get() fallbacks.
    import io, contextlib
    with contextlib.redirect_stdout(io.StringIO()) as buf:
        print_table([{"airframe": "x", "runs": ["l1.csv"], "segments": []}], clean_acs)
    out = buf.getvalue()
    assert "ARM COMPARISON" in out and "gPeak" in out and "diff(B-A)" in out, out

    # ...and an A/B is paired per CARD too: arm 0 of one card against arm 1 of another is an A/B of
    # the two cards, not of the knob. Two cards x two arms = TWO comparisons, never one crossed pair.
    two_cards = [_armed(1, 0, card="cardA"), _armed(2, 1, card="cardA"),
                 _armed(3, 0, card="cardB"), _armed(4, 1, card="cardB")]
    tc = _arm_comparisons(group_runs(two_cards))
    assert [x["card"] for x in tc] == ["cardA", "cardB"], tc
    assert _arm_comparisons(group_runs([_armed(1, 0, card="cardA"), _armed(2, 1, card="cardB")])) == []

    # spread(): known mean/stdev, and n=1/n=0 don't fake a number.
    sp = spread([2.0, 4.0, 6.0])
    assert abs(sp["mean"] - 4.0) < 1e-9 and sp["n"] == 3
    assert abs(sp["stdev"] - statistics.stdev([2.0, 4.0, 6.0])) < 1e-9
    assert abs(sp["stdevPctOfMean"] - 100.0 * sp["stdev"] / 4.0) < 1e-9
    assert spread([5.0])["stdev"] is None and spread([5.0])["n"] == 1
    assert spread([]) is None

    # compare_group: two full-duration runs of the same tag produce a real spread...
    good = [("g1.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, gPeak=2.0)])),
            ("g2.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, gPeak=3.0)]))]
    rows = compare_group(good)
    assert len(rows) == 1 and rows[0]["tag"] == "az10", rows
    assert rows[0]["truncatedRuns"] == {}, rows
    assert abs(rows[0]["metrics"]["gPeak"]["mean"] - 2.5) < 1e-9, rows

    # ...but a segment seen in only ONE run of the group is not "shared" -- no row for it at all.
    lone = good + [("g3.csv", _fake_result("X", "X", [_fake_seg("az30", 15.0, gPeak=1.0)]))]
    rows2 = compare_group(lone)
    assert [r["tag"] for r in rows2] == ["az10"], rows2

    # truncation: a short copy of the SAME segment is flagged and EXCLUDED from the spread, not
    # blended into it (the explicit requirement, and the exact mistake this tool exists to prevent).
    trio = [("t1.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, gPeak=2.0)])),
            ("t2.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, gPeak=2.2)])),
            ("t3.csv", _fake_result("X", "X", [_fake_seg("az10", 8.0, gPeak=99.0)]))]  # aborted early
    rows3 = compare_group(trio)
    assert len(rows3) == 1, rows3
    assert "t3.csv" in rows3[0]["truncatedRuns"], rows3
    gp = rows3[0]["metrics"]["gPeak"]
    assert gp["min"] != 99.0 and gp["max"] != 99.0, rows3       # the truncated run's value never leaks in
    assert abs(gp["mean"] - 2.1) < 1e-9, rows3                  # mean of ONLY the two full-duration runs

    # a metric present in only one of the "ok" (full-duration) runs still reports n=1 -> no stdev,
    # rather than silently vanishing or averaging n=1 into a fake spread.
    mixed = [("m1.csv", _fake_result("X", "X", [_fake_seg("fine", 20.0, rmsPointingErrorDeg=1.0)])),
             ("m2.csv", _fake_result("X", "X", [_fake_seg("fine", 20.0)]))]  # missing that one metric
    rows4 = compare_group(mixed)
    assert rows4[0]["metrics"]["rmsPointingErrorDeg"]["n"] == 1
    assert rows4[0]["metrics"]["rmsPointingErrorDeg"]["stdev"] is None

    # the new signed-overshoot metrics (scorecard.signed_overshoot) are None when that axis never
    # crossed the target. A None must be EXCLUDED from the spread -- reflected in n -- and never
    # coerced to 0.0, which would read as "perfect, no overshoot" when it means "never got there".
    nones = [("n1.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, overshootAzDeg=2.0,
                                                          overshootElDeg=None, entryAzSign=1)])),
             ("n2.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, overshootAzDeg=None,
                                                          overshootElDeg=None, entryAzSign=-1)])),
             ("n3.csv", _fake_result("X", "X", [_fake_seg("az10", 15.0, overshootAzDeg=3.0,
                                                          overshootElDeg=None, entryAzSign=1)]))]
    rows5 = compare_group(nones)
    mets = rows5[0]["metrics"]
    assert mets["overshootAzDeg"]["n"] == 2, mets                     # the None run is not counted
    assert abs(mets["overshootAzDeg"]["mean"] - 2.5) < 1e-9, mets     # 0.0 would have dragged this to 1.67
    assert mets["overshootAzDeg"]["min"] == 2.0, mets
    assert "overshootElDeg" not in mets, mets   # None in EVERY run -> no row at all, not a fake 0.0
    assert abs(mets["entryAzSign"]["mean"] - (1.0 / 3.0)) < 1e-9, mets  # int metric (the astern split)
    import io, contextlib
    with contextlib.redirect_stdout(io.StringIO()) as buf:            # _fmt must survive the int metric
        print_table([{"airframe": "x", "runs": ["n1.csv", "n2.csv", "n3.csv"], "segments": rows5}])
    assert "entryAzSign" in buf.getvalue() and "overshootAzDeg" in buf.getvalue(), buf.getvalue()

    # --- run-list truncation ---------------------------------------------------------------------
    # 4 files still print in full; the 5th is where a 12-replicate heading stops fitting a terminal.
    assert _runs_label(["a", "b", "c", "d"]) == "a, b, c, d"
    assert _runs_label(["a", "b", "c", "d", "e"]) == "a, b, c (+2 more)"
    assert _runs_label([f"r{i}.csv" for i in range(12)]).endswith("(+9 more)")

    # --- --summary ---------------------------------------------------------------------------
    # The mode that has to make 300 captures readable: ONE line per (card, tag), the headline metrics
    # as mean+-stdev%, the worst rail in its own column, and every line inside a terminal width.
    sum_runs = [(f"s{i}.csv", _fake_result("KR-67", "Multirole1", [
        _fake_seg("turn360", 30.0, terminalOffDeg=3.5 + 0.4 * i, rmsPointingErrorDeg=9.4,
                  meanTurnRateDegS=19.1, blendRailPct=100.0, bankClampActivePct=96.0),
        _fake_seg("az30", 8.0, terminalOffDeg=0.4, rmsPointingErrorDeg=6.0, settleTime=2.1),
    ], card="fixedwing-sweep")) for i in range(8)]
    sgroups = [{"airframe": "multirole1", "card": "fixedwing-sweep", "arm": None,
                "runs": [p for p, _ in sum_runs], "segments": compare_group(sum_runs),
                "runWarnings": _run_warnings(sum_runs)}]
    with contextlib.redirect_stdout(io.StringIO()) as buf:
        print_summary(sgroups)
    lines = [l for l in buf.getvalue().splitlines() if l.strip()]
    body = [l for l in lines if l.startswith("multirole1")]
    assert len(body) == 2, lines                                  # ONE row per segment, no metric list
    assert all(len(l) <= 130 for l in lines), max(lines, key=len)  # fits a terminal
    turn, az = body
    assert " 8 " in turn and "30.0s" in turn, turn                 # n and duration
    assert "blend100%" in turn, turn                               # the WORST rail, named, not 96% bank
    assert "term 4.9+-20%" in turn, turn        # mean of 3.5..6.3 with its spread as a % of the mean
    assert "rms 9.4+-0%" in turn, turn          # ...and a constant metric reads 0%, never "-"
    assert "turn 19.1" in turn and "settle" not in turn, turn      # type-specific pick: the turn rate
    assert "settle 2.1" in az and "turn " not in az, az            # ...and the step gets settleTime
    assert az.split()[6] == "-", az        # no rail column on that segment: NOT MEASURED...
    # ...which _sat_cell keeps distinct from "measured, all idle". Naming whichever rail sorts last
    # when every one of them reads 0.0% would look exactly like a rail that is actually active.
    assert _sat_cell({"blendRailPct": {"mean": 0.0}, "bankClampActivePct": {"mean": 0.0}}) == "none"
    assert _sat_cell({"blendRailPct": {"mean": 100.0}, "bankClampActivePct": {"mean": 96.0}}) == "blend100%"
    assert _sat_cell({}) == "-"
    # R40: the `auth<pct>%` fallback is GONE with authorityUsedFrac. A stray copy of the metric in
    # an archived score.json must not resurrect the column -- read the docstring before adding one.
    assert _sat_cell({"blendRailPct": {"mean": 0.0}, "authorityUsedFrac": {"mean": 0.31}}) == "none"
    assert _sat_cell({"authorityUsedFrac": {"mean": 0.9}}) == "-"
    # a lone run still gets a line: in a 300-file batch a group that prints nothing reads as "that
    # card was never flown", which is a different (and wrong) finding from "it was flown once".
    with contextlib.redirect_stdout(io.StringIO()) as buf:
        print_summary([{"airframe": "x", "card": "lonely-card", "arm": None, "runs": ["one.csv"],
                        "segments": [], "runWarnings": {}}])
    assert "lonely-card" in buf.getvalue() and "no segment shared" in buf.getvalue(), buf.getvalue()
    # scorecard's per-run warnings reach this page, deduped by norm_warning with a count -- otherwise
    # a railed batch is 8 copies of one line per card, which is how a warning stops being read.
    warned = [(f"w{i}.csv", _fake_result("X", "X", [_fake_seg("turn360", 30.0, gPeak=2.0)],
                                         warnings=[f"segment 'turn360' is RAILED: blendRailPct={99.0 + i * 0.1:.1f}%"]))
              for i in range(8)]
    rw = _run_warnings(warned)
    assert len(rw) == 1 and list(rw.values())[0][0] == 8, rw       # 8 near-identical texts -> one line
    with contextlib.redirect_stdout(io.StringIO()) as buf:
        print_summary([{"airframe": "x", "card": "c", "arm": None, "runs": [p for p, _ in warned],
                        "segments": compare_group(warned), "runWarnings": rw}])
        print_table([{"airframe": "x", "card": "c", "arm": None, "runs": [p for p, _ in warned],
                      "segments": compare_group(warned), "runWarnings": rw}])
    out = buf.getvalue()
    assert out.count("is RAILED") == 2, out                        # once per mode, not 8x per mode
    assert "[8 run(s), e.g.]" in out and "[8/8 runs, e.g.]" in out, out

    print("selftest OK")


def main(argv):
    if not argv:
        sys.exit(__doc__)
    if argv[0] == "--selftest":
        selftest()
        return
    json_path, files, i = None, [], 0
    summary = "--summary" in argv
    argv = [a for a in argv if a != "--summary"]
    while i < len(argv):
        if argv[i] == "--json":
            if i + 1 >= len(argv):
                sys.exit("--json requires a path")
            json_path = argv[i + 1]
            i += 2
        else:
            files.append(argv[i])
            i += 1
    if len(files) < 2:
        sys.exit("usage: compare-runs.py [--json out.json] [--summary] <recording.csv> <recording2.csv> [more.csv ...]\n"
                  "       compare-runs.py --selftest")
    groups, arm_comparisons = compare_all(files)
    if json_path:
        w = _pool_warning([(g["airframe"], g["runs"]) for g in groups])
        if w:                                    # stdout is the JSON path message; warn on stderr
            print(f"WARNING: {w}", file=sys.stderr)
        with open(json_path, "w", encoding="utf-8") as out:
            json.dump({"groups": groups, "armComparisons": arm_comparisons}, out, indent=2)
        print(f"wrote {json_path}")
    elif summary:
        print_summary(groups)
    else:
        print_table(groups, arm_comparisons)


if __name__ == "__main__":
    main(sys.argv[1:])
