#!/usr/bin/env python3
"""Compare N scored recorder captures (instructor-feedback-loop M0 -- noise floor + A/B testing).

scorecard.py scores ONE run; this reports the SPREAD across several runs of (nominally) the same
test card, per segment x metric: min/max/mean/stdev/stdev-as-%-of-mean, n. That is what measuring
the score noise floor and A/B-ing a control-law change both need, and hand-comparing the ~50-run
batches now being generated is not viable.

    python debugtests/compare-runs.py <rec1.csv> <rec2.csv> [more.csv ...]   # table to stdout
    python debugtests/compare-runs.py --json out.json <rec1.csv> [...]      # write out.json
    python debugtests/compare-runs.py --selftest                            # in-memory asserts

Reuses scorecard.score_run() for the actual per-run scoring (imported, not reimplemented) -- this
file only aggregates already-scored results.

GROUPING: runs are grouped by airframe (the .airframe.json sidecar's "jsonKey", e.g. "Multirole1" /
"trainer" -- falling back to the CSV's "# aircraft" header when the sidecar is missing or that key
is absent, and to a per-file singleton group when neither is available). The comparison key is for
by-airframe grouping only. Pooling two different airframes into one spread is meaningless (a
heavier jet's turn rate is not noise around a trainer's) -- exactly the mistake that wasted a prior
test session -- so groups are NEVER merged; each is scored and printed separately, and a mixed
input warns loudly on stderr (and in the table) about the split rather than silently averaging
across it. Note: the sidecar's jsonKey and the CSV header's aircraft name can differ only in case
for the SAME airframe (observed on real captures: jsonKey "trainer" vs header "Trainer") -- the key
is lowercased before grouping so that difference alone can never fragment one airframe into two.

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
"""
import sys, os, json, statistics
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


def group_runs(results):
    """results: [(path, score_result), ...] (already scored -- via score_files, or fabricated by a
    test). -> [(airframe_key, [(path, score_result), ...]), ...], insertion-ordered by first
    appearance of each key so output order is stable and matches input order."""
    groups, order = {}, []
    for path, result in results:
        key = airframe_key(result, path)
        if key not in groups:
            groups[key] = []
            order.append(key)
        groups[key].append((path, result))
    return [(k, groups[k]) for k in order]


def _pool_warning(groups):
    """None, or the "don't pool these" message, when the input spans more than one airframe."""
    if len(groups) < 2:
        return None
    bits = ", ".join(f"{k} x{len(runs)}" for k, runs in groups)
    return f"input spans {len(groups)} airframes ({bits}) -- scored SEPARATELY per airframe, never pooled."


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
    """[{"tag", "fullDurationS", "truncatedRuns": {basename: durationS}, "metrics": {name: spread()}}]
    for one airframe group's segments shared by >=2 of its runs."""
    out = []
    for tag, entries in _segments_by_tag(runs).items():
        if len(entries) < 2:
            continue  # nothing to compare with one run
        full = max(seg["durationS"] for _, seg in entries)
        ok = [(p, seg) for p, seg in entries if seg["durationS"] >= FULL_FRAC * full]
        truncated = {os.path.basename(p): seg["durationS"] for p, seg in entries
                     if seg["durationS"] < FULL_FRAC * full}
        row = {"tag": tag, "fullDurationS": full, "truncatedRuns": truncated, "metrics": {}}
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


def compare_all(paths):
    """[{"airframe", "runs": [basename...], "segments": compare_group(...)}, ...], one per group."""
    groups = group_runs(score_files(paths))
    # No printing here — print_table() emits the warning in table mode and main() does it on stderr
    # for --json. Warning from both paths double-printed it in the common case.
    return [{"airframe": key, "runs": [os.path.basename(p) for p, _ in runs],
             "segments": compare_group(runs)}
            for key, runs in groups]


# --- output ---------------------------------------------------------------------------------

def _fmt(sp):
    sd = f"{sp['stdev']:.3g}" if sp["stdev"] is not None else "n/a"
    pct = f"{sp['stdevPctOfMean']:.1f}%" if sp["stdevPctOfMean"] is not None else "n/a"
    return f"min={sp['min']:.3g} max={sp['max']:.3g} mean={sp['mean']:.3g} stdev={sd} ({pct}) n={sp['n']}"


def print_table(groups):
    w = _pool_warning([(g["airframe"], g["runs"]) for g in groups])
    if w:
        print(f"WARNING: {w}")
    for g in groups:
        print(f"\n=== airframe: {g['airframe']}  ({len(g['runs'])} runs: {', '.join(g['runs'])})")
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


# --- selftest ---------------------------------------------------------------------------------

def _fake_seg(tag, duration, **metrics):
    return {"tag": tag, "type": "x", "samples": 10, "durationS": duration, "excluded": False,
            "metrics": {k: {"value": v, "grade": None} for k, v in metrics.items()}, "skipped": {}}


def _fake_result(aircraft, json_key, segs):
    prov = {}
    if aircraft:
        prov["aircraft"] = aircraft
    if json_key:
        prov["airframeInfo"] = {"jsonKey": json_key}
    return {"provenance": prov, "segments": segs, "warnings": []}


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
    assert [k for k, _ in groups] == ["multirole1", "trainer"], groups
    gd = dict(groups)
    assert len(gd["multirole1"]) == 2 and len(gd["trainer"]) == 1, groups
    assert _pool_warning(groups) is not None and "2 airframes" in _pool_warning(groups)
    assert _pool_warning(group_runs([r1, r3])) is None    # a single airframe: nothing to warn about

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
    if len(files) < 2:
        sys.exit("usage: compare-runs.py [--json out.json] <recording.csv> <recording2.csv> [more.csv ...]\n"
                  "       compare-runs.py --selftest")
    groups = compare_all(files)
    if json_path:
        w = _pool_warning([(g["airframe"], g["runs"]) for g in groups])
        if w:                                    # stdout is the JSON path message; warn on stderr
            print(f"WARNING: {w}", file=sys.stderr)
        with open(json_path, "w", encoding="utf-8") as out:
            json.dump(groups, out, indent=2)
        print(f"wrote {json_path}")
    else:
        print_table(groups)


if __name__ == "__main__":
    main(sys.argv[1:])
