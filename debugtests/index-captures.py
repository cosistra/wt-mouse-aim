#!/usr/bin/env python3
"""Index the flight-capture corpus into SQLite so cross-batch questions have a home.

Stdlib only (sqlite3/csv/json/glob/argparse/shutil), like the rest of debugtests/.

WHY THIS EXISTS: every analysis re-parses 344 MB from scratch, and "does this effect hold in R28,
R29 AND R30?" currently means running scorecard/compare-runs three times and stitching prose. This
builds a queryable index ONCE (~30 s for the whole corpus) and re-indexes incrementally after that.

    python index-captures.py <dir|glob|file>...            # index (default db: debugtests/captures.db)
    python index-captures.py --run R29                     # only that batch
    python index-captures.py --with-rows R30               # + materialize R30's raw rows (opt-in)
    python index-captures.py --archive ../archive --run R29 # copy R29's CSVs/sidecars/log out of <game>
    python index-captures.py --cards ../cards              # load the card grid as dimension tables
    python index-captures.py --stats                       # what is in here? (read this first)
    python index-captures.py --check R29                   # is that batch complete and intact?
    python index-captures.py --diff R30 R31                # per-cell mean +- stdev%, both runs, ratio
    python index-captures.py --query "select ..."          # run SQL (READ-ONLY unless --write)
    python index-captures.py --selftest                    # synthetic capture, no game folder needed

With no paths it falls back to $NUCLEAR_OPTION_PATH/BepInEx.

READ [CAPTURES-DB.md](CAPTURES-DB.md) BEFORE WRITING A QUERY. It is the column-by-column reference
(type + provenance), the metric-by-segment-type matrix -- metrics are SPARSE by type, so a corpus-wide
avg() silently averages a handful of rows -- the two NULL idioms, and a cookbook of tested queries.
The traps it documents are not obvious from the schema and every one of them returns a plausible
number rather than an error.

SCORECARD IS THE SOURCE OF TRUTH FOR METRICS. This module imports scorecard.py (the same importlib
trick scorecard uses on analyze-wobble.py) and stores what `score_run()` / `rail_warning()` return.
Nothing here re-derives a metric, a RAILED threshold or a tag->type rule; if a metric changes there,
re-run this and the index changes with it. What this file DOES parse itself is the `#` header lines
scorecard's provenance() does not expose (`# entry`, `# override`, `# drone`, `arm=`/`armKnob=`) --
header text, not measurements.

SCHEMA (two tables, plus an opt-in third):
  captures   one row per CSV. Fixed columns for identity/provenance; DYNAMIC columns added on
             demand for the sidecar's scalars (`sc_` prefix), the `# entry` line (`entry_`) and the
             `# override` line (`ov_`). Dynamic on purpose: a new sidecar field or entry field
             appears as a column on the next index run instead of silently vanishing, which is the
             drift this repo keeps getting bitten by.
  segments   one row per (capture, segment): tag/index/type/duration/samples, `railed` and `slack`
             as booleans, warnings, the `skipped` dict as JSON -- plus one DYNAMIC column per metric
             name scorecard emits, named exactly as scorecard names it.
  rows       raw recorder rows, ONE batch at a time via --with-rows. Not default: all ~1.1M rows is
             ~500 MB of mostly-unread steady state.

EXAMPLE QUERIES (sqlite3 debugtests/captures.db, or --query "...")

  -- 1. rank airframes by a metric within one batch
  SELECT c.airframe, count(*) n, round(avg(s.terminalOffDeg),3) off
    FROM segments s JOIN captures c ON c.id = s.capture_id
   WHERE c.run_tag = 'R29' AND s.type = 'oblique_step' AND s.railed = 0
   GROUP BY c.airframe ORDER BY off;

  -- 2. one metric across batches (does the effect hold in R28/R29/R30?)
  SELECT c.run_tag, c.mod_version, count(*) n, round(avg(s.rmsPointingErrorDeg),4) rms
    FROM segments s JOIN captures c ON c.id = s.capture_id
   WHERE s.tag = 'obUL05' GROUP BY c.run_tag ORDER BY min(c.started);

  -- 3. railed cells: where a gain change physically cannot move the metrics
  SELECT c.run_tag, c.airframe, s.tag, count(*) n
    FROM segments s JOIN captures c ON c.id = s.capture_id
   WHERE s.railed = 1 GROUP BY 1,2,3 ORDER BY n DESC LIMIT 20;

  -- 4. A/B one knob inside a batch, by arm (compare-runs' grouping, in SQL)
  SELECT c.arm_knob, c.arm, s.tag, count(*) n, round(avg(s.terminalOffDeg),4) off
    FROM segments s JOIN captures c ON c.id = s.capture_id
   WHERE c.run_tag = 'R31' AND c.arm IS NOT NULL
   GROUP BY 1,2,3 ORDER BY s.tag, c.arm;

IDEMPOTENT: keyed on the CSV's basename; a capture whose (mtime, size) is unchanged is skipped
without opening it, so re-running after every flight costs a stat() per file. --rebuild forces.
"""
import argparse, csv, glob, io, json, os, pathlib, re, shutil, sqlite3, statistics, sys, time
import importlib.util as _ilu
from contextlib import redirect_stderr

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load(name, filename):
    spec = _ilu.spec_from_file_location(name, os.path.join(_HERE, filename))
    mod = _ilu.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


sc = _load("scorecard", "scorecard.py")   # the metric source of truth; never reimplemented here

DEFAULT_DB = os.path.join(_HERE, "captures.db")
IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
# Arrays that are curve data, not capability scalars -- the task's "skip the Cl/Cd curves".
SIDECAR_SKIP = {"airfoils", "airfoilAlphaDeg"}

CAPTURE_DDL = """
CREATE TABLE IF NOT EXISTS captures (
  id          INTEGER PRIMARY KEY,
  file        TEXT UNIQUE NOT NULL,   -- basename: the idempotency key
  path        TEXT,
  mtime       REAL,
  size        INTEGER,
  run_tag     TEXT,      -- R29; NULL on a legacy capture whose filename carries no run
  mod_version TEXT,
  session     TEXT,
  rec         INTEGER,   -- per-process file counter; orders captures in time
  drone       INTEGER,   -- lane, from '# drone N'; NULL = hand-flown
  replicate   INTEGER,   -- DERIVED: ordinal within (session, drone, card) by rec. Not in any
                         -- artifact -- ScenarioPlayer.RunIndex is never written to the CSV.
  airframe    TEXT,      -- sidecar jsonKey (what compare-runs.py groups on); NULL if no sidecar
  aircraft    TEXT,      -- the '# aircraft' name
  card        TEXT,
  arm         INTEGER,   -- off '# config'; NULL when the capture is not part of an A/B
  arm_knob    TEXT,
  started     TEXT,      -- '# started' wall clock (local)
  utc         TEXT,      -- sidecar UTC
  n_rows      INTEGER,
  n_cols      INTEGER,
  stop        TEXT,
  aborted     INTEGER,
  config      TEXT,
  entry_note  TEXT,
  ov_note     TEXT,
  parse_warn  TEXT       -- scorecard's own stderr for this file (dropped rows etc.)
)"""

SEGMENT_DDL = """
CREATE TABLE IF NOT EXISTS segments (
  capture_id INTEGER NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
  seg_index  INTEGER NOT NULL,
  tag        TEXT,
  type       TEXT,
  samples    INTEGER,
  duration_s REAL,
  excluded   INTEGER,
  railed     INTEGER,
  slack      INTEGER,
  unknown_tag INTEGER,   -- scorecard's "tag matches no TAG_TYPE_RULES" warning fired
  warnings   TEXT,
  skipped    TEXT,
  PRIMARY KEY (capture_id, seg_index)
)"""

INDEX_DDL = [
    "CREATE INDEX IF NOT EXISTS ix_cap_run ON captures(run_tag)",
    "CREATE INDEX IF NOT EXISTS ix_cap_air ON captures(airframe)",
    "CREATE INDEX IF NOT EXISTS ix_cap_card ON captures(card)",
    "CREATE INDEX IF NOT EXISTS ix_cap_arm ON captures(arm_knob, arm)",
    "CREATE INDEX IF NOT EXISTS ix_cap_ver ON captures(mod_version)",
    "CREATE INDEX IF NOT EXISTS ix_seg_cap ON segments(capture_id)",
    "CREATE INDEX IF NOT EXISTS ix_seg_tag ON segments(tag)",
    "CREATE INDEX IF NOT EXISTS ix_seg_type ON segments(type)",
    "CREATE INDEX IF NOT EXISTS ix_seg_railed ON segments(railed)",
]

# Segment columns a metric name must not shadow. Every real metric name (terminalOffDeg,
# bankClampPct, ...) is well clear of these; the guard is here so a future one that isn't gets
# renamed loudly instead of overwriting a key column.
SEG_RESERVED = {"capture_id", "seg_index", "tag", "type", "samples", "duration_s", "excluded",
                "railed", "slack", "unknown_tag", "warnings", "skipped"}


# --- SQL aggregates SQLite does not ship --------------------------------------------------------
# WHY: SQLite has no stdev and no median, so every noise-floor question asked in SQL either got no
# spread at all or a hand-rolled population variance -- which DISAGREES with compare-runs.py, the
# tool this corpus is actually read through, and disagrees by a factor of sqrt(n/(n-1)): 6.9% at the
# n=8 replicate count the shipped grid flies. Two numbers for one quantity, no way to tell which
# report used which. These two are registered on every connection (read-only included) so `stdev(x)`
# in a --query means exactly what `+- %` means in a compare-runs table.


class _SampleStdev:
    """SAMPLE stdev (n-1). compare-runs.py's spread() uses statistics.stdev (sample), not pstdev --
    matched deliberately; see debugtests/compare-runs.py:184. NULL below n=2, again matching it:
    one replicate has no spread, and a printed 0 reads as "perfectly repeatable"."""

    def __init__(self):
        self.v = []

    def step(self, x):
        if x is None:
            return                    # NULL is "not applicable here", never a zero to average in
        try:
            self.v.append(float(x))
        except (TypeError, ValueError):
            pass                      # a TEXT metric (JSON-encoded value) is not a number; skip it
    def finalize(self):
        return statistics.stdev(self.v) if len(self.v) >= 2 else None


class _Median:
    """statistics.median (mean of the middle two at even n). NULL on an empty group, like avg()."""

    def __init__(self):
        self.v = []

    def step(self, x):
        if x is None:
            return
        try:
            self.v.append(float(x))
        except (TypeError, ValueError):
            pass

    def finalize(self):
        return statistics.median(self.v) if self.v else None


# --- schema helpers ---------------------------------------------------------------------------

def connect(db, readonly=False):
    """readonly=True opens `file:...?mode=ro` and runs NO DDL -- that is the point: a --query typo
    that happens to be a DELETE cannot touch a database that took ~30 s and 344 MB to build, and a
    read-only handle also cannot create the WAL sidecars in <game>-adjacent folders."""
    if readonly:
        cx = sqlite3.connect(pathlib.Path(db).absolute().as_uri() + "?mode=ro", uri=True)
    else:
        cx = sqlite3.connect(db)
        cx.execute("PRAGMA foreign_keys = ON")
        cx.execute("PRAGMA journal_mode = WAL")
        cx.execute(CAPTURE_DDL)
        cx.execute(SEGMENT_DDL)
        for stmt in INDEX_DDL:
            cx.execute(stmt)
    cx.create_aggregate("stdev", 1, _SampleStdev)
    cx.create_aggregate("median", 1, _Median)
    return cx


def cols_of(cx, table):
    return {r[1] for r in cx.execute(f"PRAGMA table_info({table})")}


def ensure_cols(cx, table, known, names, decl="NUMERIC"):
    """Add any missing column. `known` is the caller's cached set, mutated in place -- a PRAGMA per
    file over 1600 files is the only reason this is cached rather than re-read."""
    for n in names:
        if n in known:
            continue
        if not IDENT.match(n):
            continue                      # never interpolate something that isn't an identifier
        cx.execute(f'ALTER TABLE {table} ADD COLUMN "{n}" {decl}')
        known.add(n)


def insert(cx, table, d):
    keys = list(d)
    sql = (f'INSERT OR REPLACE INTO {table} ({",".join(chr(34) + k + chr(34) for k in keys)}) '
           f'VALUES ({",".join("?" * len(keys))})')
    cx.execute(sql, [d[k] for k in keys])


# --- header parsing (the bits scorecard's provenance() does not expose) -------------------------

def num(s):
    try:
        return float(s)
    except ValueError:
        return s


def head(path):
    """(list of leading '#' lines, the CSV header line). The `#` block is contiguous at the top of
    every capture; the '# stop' footer is scorecard's job (provenance already returns it)."""
    hashes, header = [], ""
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith("#"):
                hashes.append(line.rstrip("\n"))
            else:
                header = line.rstrip("\n")
                break
    return hashes, header


def kv_fields(prefix, note):
    """`v=152.2->171.0 alt=987.7->4000.0 ctrlReset=1` -> entry_v_from/entry_v_to/entry_alt_from/...

    Generic on purpose: v0.88 added `aoaTrim=` and v0.89 removed it again, and a hardcoded field
    list would have silently dropped it both times."""
    out = {}
    for k, v in re.findall(r"(\w+)=(\S+)", note):
        if "->" in v:
            a, b = v.split("->", 1)
            out[f"{prefix}_{k}_from"], out[f"{prefix}_{k}_to"] = num(a), num(b)
        else:
            out[f"{prefix}_{k}"] = num(v)
    return out


def flatten_sidecar(info):
    """`sc_`-prefixed scalars. Lists of numbers (fbwParameters) and the loadout keep their JSON --
    they are one value per capture, just not a scalar; the Cl/Cd curves are dropped outright."""
    out = {}
    for k, v in (info or {}).items():
        if k in SIDECAR_SKIP:
            continue
        if isinstance(v, bool):
            out["sc_" + k] = int(v)
        elif isinstance(v, (int, float, str)) or v is None:
            out["sc_" + k] = v
        elif k == "loadout" and isinstance(v, list):
            out["sc_loadout"] = json.dumps(v)
            out["sc_loadoutCount"] = len(v)
            out["sc_loadoutMassKg"] = sum(float(s.get("mass") or 0) for s in v if isinstance(s, dict))
        else:
            out["sc_" + k] = json.dumps(v)
    return out


# --- indexing ---------------------------------------------------------------------------------

def run_tag_of(prov, fname):
    r = prov.get("run")
    if r:
        return r if str(r).startswith("R") else f"R{r}"
    m = re.search(r"-(R\d+)-", fname)
    return m.group(1) if m else None


def index_one(cx, path, cap_cols, seg_cols, cap_id=None):
    fname = os.path.basename(path)
    hashes, header = head(path)
    err = io.StringIO()
    with redirect_stderr(err):
        result = sc.score_run(path)          # <- every metric in this database comes from here
    prov = result["provenance"]
    info = prov.get("airframeInfo") or {}

    row = {
        "file": fname,
        "path": os.path.abspath(path),
        "mtime": os.path.getmtime(path),
        "size": os.path.getsize(path),
        "run_tag": run_tag_of(prov, fname),
        "mod_version": prov.get("modVersion"),
        "session": prov.get("session"),
        "rec": int(prov["rec"]) if str(prov.get("rec", "")).isdigit() else None,
        "airframe": info.get("jsonKey"),
        "aircraft": prov.get("aircraft"),
        "card": prov.get("card"),
        "utc": info.get("utc"),
        "n_rows": sum(s["samples"] for s in result["segments"]),
        "n_cols": len(header.split(",")) if header else None,
        "stop": prov.get("stop"),
        "aborted": int(bool(prov.get("aborted"))),
        "config": prov.get("config"),
        "parse_warn": err.getvalue().strip() or None,
    }
    if cap_id is not None:
        row["id"] = cap_id

    for h in hashes:
        if h.startswith("# drone "):
            row["drone"] = int(h[8:].strip())
        elif h.startswith("# started "):
            row["started"] = h[10:].split("  t=")[0].strip()
        elif h.startswith("# entry "):
            row["entry_note"] = h[8:].strip()
            row.update(kv_fields("entry", row["entry_note"]))
        elif h.startswith("# override "):
            row["ov_note"] = h[11:].strip()
            row.update(kv_fields("ov", row["ov_note"].replace("/", "_")))

    m = re.search(r"\barm=(\d+)", prov.get("config", "") or "")
    if m:
        row["arm"] = int(m.group(1))
    m = re.search(r"\barmKnob=(\S+)", prov.get("config", "") or "")
    if m:
        row["arm_knob"] = m.group(1)

    row.update(flatten_sidecar(info))

    ensure_cols(cx, "captures", cap_cols, [k for k in row if k not in cap_cols])
    cur = cx.execute("SELECT id FROM captures WHERE file = ?", (fname,)).fetchone()
    if cur:
        # Re-index of a known capture: keep its id (so a --with-rows FK stays meaningful) and drop
        # everything derived from the old contents. INSERT OR REPLACE below is a DELETE+INSERT, so
        # any materialized rows for it would ON DELETE CASCADE away anyway -- dropping them here
        # makes that explicit rather than a surprise, and a changed CSV's old rows are stale by
        # definition. Re-run --with-rows after a --rebuild.
        row["id"] = cur[0]
        cx.execute("DELETE FROM segments WHERE capture_id = ?", (cur[0],))
        if _has_rows(cx):
            cx.execute("DELETE FROM rows WHERE capture_id = ?", (cur[0],))
    insert(cx, "captures", row)
    cid = row.get("id") or cx.execute("SELECT id FROM captures WHERE file = ?", (fname,)).fetchone()[0]

    for i, seg in enumerate(result["segments"]):
        warn = [w for w in (sc._tag_warning(seg["tag"], seg["type"]), sc.rail_warning(seg)) if w]
        joined = "\n".join(warn)
        s = {
            "capture_id": cid, "seg_index": i,
            "tag": seg["tag"], "type": seg["type"],
            "samples": seg["samples"], "duration_s": seg["durationS"],
            "excluded": int(bool(seg["excluded"])),
            # scorecard's own predicate, not a match on its prose: `railed` is the flag that decides
            # whether a number is a score or no signal, and a reworded warning must not silently mark
            # the whole corpus clean.
            "railed": int(sc.is_railed(seg)),
            "slack": int(" is SLACK:" in joined),
            "unknown_tag": int("does not match any known type" in joined),
            "warnings": joined or None,
            "skipped": json.dumps(seg["skipped"]) if seg["skipped"] else None,
        }
        for name, mv in seg["metrics"].items():
            col = name if name not in SEG_RESERVED else "m_" + name
            v = mv["value"]
            s[col] = v if v is None or isinstance(v, (int, float, str)) else json.dumps(v)
        ensure_cols(cx, "segments", seg_cols, [k for k in s if k not in seg_cols])
        insert(cx, "segments", s)
    return cid


def _has_rows(cx):
    return bool(cx.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='rows'").fetchone())


def renumber_replicates(cx):
    """`replicate` is the ordinal of a capture within (session, drone, card) by rec -- recomputed
    wholesale because adding one capture can renumber nothing or everything after it, and 1600 rows
    is far too cheap to bother being clever about."""
    seen, upd = {}, []
    for cid, ses, dr, card in cx.execute(
            "SELECT id, coalesce(session,''), coalesce(drone,-1), coalesce(card,'') "
            "FROM captures ORDER BY coalesce(session,''), coalesce(drone,-1), coalesce(card,''), "
            "coalesce(rec, id)"):
        key = (ses, dr, card)
        seen[key] = seen.get(key, 0) + 1
        upd.append((seen[key], cid))
    cx.executemany("UPDATE captures SET replicate = ? WHERE id = ?", upd)


def expand(paths):
    out = []
    for p in paths:
        if os.path.isdir(p):
            out += glob.glob(os.path.join(p, "mouseaim-rec-*.csv"))
        elif any(ch in p for ch in "*?["):
            out += glob.glob(p)
        else:
            out.append(p)
    return sorted(set(os.path.abspath(f) for f in out if f.endswith(".csv")))


def index_all(cx, files, rebuild=False, run=None):
    cap_cols, seg_cols = cols_of(cx, "captures"), cols_of(cx, "segments")
    known = {f: (m, s) for f, m, s in cx.execute("SELECT file, mtime, size FROM captures")}
    n_new = n_skip = n_fail = 0
    t0 = time.time()
    for path in files:
        fname = os.path.basename(path)
        if run and f"-{run}-" not in fname:
            continue
        st = os.stat(path)
        prev = known.get(fname)
        if prev and not rebuild and abs(prev[0] - st.st_mtime) < 1e-6 and prev[1] == st.st_size:
            n_skip += 1
            continue
        try:
            index_one(cx, path, cap_cols, seg_cols)
            n_new += 1
        except Exception as e:                      # one bad capture must not kill a 1600-file run
            n_fail += 1
            print(f"FAILED {fname}: {type(e).__name__}: {e}", file=sys.stderr)
        if n_new and n_new % 200 == 0:
            cx.commit()
            print(f"  ... {n_new} indexed", file=sys.stderr)
    renumber_replicates(cx)
    cx.commit()
    print(f"indexed {n_new}, skipped {n_skip} unchanged, {n_fail} failed  ({time.time() - t0:.1f}s)")
    return n_new, n_skip, n_fail


# --- opt-in raw rows --------------------------------------------------------------------------

def materialize_rows(cx, run):
    """One batch's raw recorder rows. Reuses scorecard's load_csv (typed rows, STRING_COLS split)
    rather than a second CSV parser."""
    caps = cx.execute("SELECT id, path FROM captures WHERE run_tag = ? ORDER BY rec", (run,)).fetchall()
    if not caps:
        sys.exit(f"--with-rows {run}: no indexed captures with that run tag (index the batch first)")
    cx.execute("CREATE TABLE IF NOT EXISTS rows ("
               "capture_id INTEGER NOT NULL REFERENCES captures(id) ON DELETE CASCADE, i INTEGER)")
    cx.execute("CREATE INDEX IF NOT EXISTS ix_rows_cap ON rows(capture_id)")
    row_cols = cols_of(cx, "rows")
    n = 0
    for cid, path in caps:
        if not os.path.isfile(path):
            print(f"  missing, skipped: {path}", file=sys.stderr)
            continue
        cx.execute("DELETE FROM rows WHERE capture_id = ?", (cid,))
        with redirect_stderr(io.StringIO()):
            _meta, data, _cols = sc.load_csv(path)
        for i, r in enumerate(data):
            d = {"capture_id": cid, "i": i}
            d.update(r)
            ensure_cols(cx, "rows", row_cols, [k for k in d if k not in row_cols])
            insert(cx, "rows", d)
            n += 1
        cx.commit()
    # Handy for the "every row where frameMs > 20" question this table exists for.
    if "frameMs" in row_cols:
        cx.execute("CREATE INDEX IF NOT EXISTS ix_rows_frame ON rows(frameMs)")
    cx.commit()
    print(f"materialized {n} row(s) from {len(caps)} capture(s) of {run}")


# --- archive ----------------------------------------------------------------------------------

def archive(cx, dest, run):
    """Filesystem copy out of <game>. LogOutput.log is overwritten every session, so a batch's log
    is gone the moment the next one starts -- R28's launch lines already are."""
    caps = cx.execute("SELECT path FROM captures WHERE run_tag = ? ORDER BY rec", (run,)).fetchall()
    if not caps:
        sys.exit(f"--archive: no indexed captures with run tag {run}")
    out = os.path.join(dest, f"{run}-{time.strftime('%Y%m%d')}")
    os.makedirs(out, exist_ok=True)
    n = 0
    src_dir = os.path.dirname(caps[0][0])
    for (p,) in caps:
        for f in (p, os.path.splitext(p)[0] + ".airframe.json"):
            if os.path.isfile(f):
                shutil.copy2(f, out)
                n += 1
    for extra in glob.glob(os.path.join(src_dir, f"LogOutput-{run}.log")) + \
                 glob.glob(os.path.join(src_dir, f"mouseaim-anomalies-*-{run}-*.log")):
        shutil.copy2(extra, out)
        n += 1
    print(f"archived {n} file(s) to {out}")


# --- query ------------------------------------------------------------------------------------

DEFAULT_LIMIT = 1000      # a --query that forgot its GROUP BY is 1.1M rows down a terminal


def table(names, data, count=True):
    """The one table printer. Shared by --query/--stats/--check/--diff so they line up on screen."""
    w = [max(len(n), *(len(str(r[i])) for r in data)) if data else len(n) for i, n in enumerate(names)]
    print("  ".join(n.ljust(w[i]) for i, n in enumerate(names)))
    print("  ".join("-" * w[i] for i in range(len(names))))
    for r in data:
        print("  ".join(str(v).ljust(w[i]) for i, v in enumerate(r)))
    if count:
        print(f"({len(data)} row(s))")


def query(cx, sql, fmt="table", limit=DEFAULT_LIMIT):
    try:
        cur = cx.execute(sql)
    except sqlite3.OperationalError as e:
        # A refusal is a line, not a traceback -- and the ONE refusal a correct query can hit here is
        # the read-only handle, so name the opt-out instead of making the reader guess.
        sys.exit(f"query failed: {e}" + ("\n  (the db is opened READ-ONLY; pass --write if you "
                                         "really mean to modify it)" if "readonly" in str(e) else ""))
    if cur.description is None:          # DDL/DML: only reachable with --write
        cx.commit()
        return
    names = [d[0] for d in cur.description]
    data = cur.fetchmany(limit + 1) if limit else cur.fetchall()
    capped = bool(limit) and len(data) > limit
    data = data[:limit] if capped else data
    if fmt == "csv":
        w = csv.writer(sys.stdout, lineterminator="\n")
        w.writerow(names)
        w.writerows(data)
    elif fmt == "json":
        json.dump([dict(zip(names, r)) for r in data], sys.stdout, indent=1, default=str)
        print()
    else:
        table(names, data)
    if capped:
        # Loud, on stderr, and it says how to lift it -- a silently truncated result set is a wrong
        # answer that looks like a right one, which is the failure mode this whole file exists to avoid.
        sys.stdout.flush()               # or the unbuffered stderr line lands above the table
        print(f"*** TRUNCATED at {limit} rows (--limit 0 for all, --limit N for another cap)",
              file=sys.stderr)


def summary(cx):
    query(cx, "SELECT run_tag, mod_version, count(*) captures, count(DISTINCT airframe) airframes, "
              "count(DISTINCT card) cards, sum(aborted) aborted, min(started) started "
              "FROM captures GROUP BY run_tag, mod_version ORDER BY min(coalesce(started,''))")


# --- orientation: --stats ------------------------------------------------------------------------

def stats(cx):
    """What is in this database? The first command an agent with no context should run.

    Everything here is a count, deliberately: the questions it answers are "is the batch I care
    about indexed", "which era is it from" (n_cols -- see CAPTURES-DB.md's era filter idiom) and
    "did anything fail to parse". No metric is averaged, because averaging across segment types is
    exactly the mistake this output exists to stop someone making blind."""
    tot = cx.execute("SELECT count(*), count(DISTINCT run_tag), count(DISTINCT airframe), "
                     "count(DISTINCT card), sum(aborted), sum(n_rows) FROM captures").fetchone()
    nseg = cx.execute("SELECT count(*) FROM segments").fetchone()[0]
    print(f"captures {tot[0]}   segments {nseg}   recorder rows {tot[5] or 0}   "
          f"runs {tot[1]}   airframes {tot[2]}   cards {tot[3]}   aborted {tot[4] or 0}")
    print(f"segment metric columns: {len(cols_of(cx, 'segments') - SEG_RESERVED)}   "
          f"capture columns: {len(cols_of(cx, 'captures'))}")

    print("\n-- batches (ordered as flown) " + "-" * 50)
    rows_by_run = {}
    if _has_rows(cx):
        rows_by_run = {r[0]: (r[1], r[2]) for r in cx.execute(
            "SELECT c.run_tag, count(DISTINCT r.capture_id), count(*) FROM rows r "
            "JOIN captures c ON c.id = r.capture_id GROUP BY 1")}
    q = cx.execute(
        "SELECT coalesce(run_tag,'(none)'), group_concat(DISTINCT mod_version), count(*), "
        "count(DISTINCT airframe), count(DISTINCT card), sum(aborted), "
        "min(coalesce(started,'')), group_concat(DISTINCT n_cols) "
        "FROM captures GROUP BY run_tag ORDER BY min(coalesce(started,''))").fetchall()
    out = []
    for run, ver, n, na, nc, ab, st, ncols in q:
        mat = rows_by_run.get(run)
        out.append((run, ver, n, na, nc, ab or 0, (st or "")[:16], ncols,
                    f"{mat[1]} rows/{mat[0]} caps" if mat else ""))
    table(["run", "modVersion", "caps", "airf", "cards", "abort", "started", "n_cols", "--with-rows"],
          out)

    print("\n-- n_cols eras (the recorder's column count; see the era filter in CAPTURES-DB.md) " + "-" * 5)
    table(["n_cols", "captures", "modVersions", "runs"],
          cx.execute("SELECT n_cols, count(*), group_concat(DISTINCT mod_version), "
                     "group_concat(DISTINCT run_tag) FROM captures GROUP BY n_cols "
                     "ORDER BY n_cols").fetchall())

    print("\n-- airframes " + "-" * 50)
    # Dates, not min/max(run_tag): run tags sort LEXICOGRAPHICALLY, so "R10" < "R2" and the newest
    # batch would be reported as the oldest.
    table(["airframe", "captures", "runs", "cards", "first seen", "last seen"],
          cx.execute("SELECT coalesce(airframe,'(no sidecar)'), count(*), count(DISTINCT run_tag), "
                     "count(DISTINCT card), min(substr(started,1,10)), max(substr(started,1,10)) "
                     "FROM captures GROUP BY 1 ORDER BY 2 DESC").fetchall())

    bad = cx.execute("SELECT count(*) FROM captures WHERE parse_warn IS NOT NULL").fetchone()[0]
    print(f"\nparse warnings: {bad} capture(s)" + ("  (--check names them)" if bad else "  -- clean"))


# --- integrity: --check ---------------------------------------------------------------------------

# A lane whose count is under this fraction of the batch's median lane is not "a bit short", it
# stopped. R29's Darkreach lane died at rec=90 with 9 captures against 48 on every other lane
# (0.19); a legitimately uneven batch (a card the airframe refuses on the envelope gate) skips WHOLE
# cells, which shows up in `cells` rather than here. 0.6 is a literal like scorecard's RAILED_PCT --
# blunt on purpose, and the row is printed either way so the number is always visible.
LANE_SHORT_FRAC = 0.6


def _rec_gaps(cx, run):
    """Missing `rec` numbers within a run, PER SESSION -- rec is a per-PROCESS counter (ManeuverRecorder
    `_recSeq` counts files opened this run), so it restarts when the game does and a run spanning two
    sessions would otherwise look like one enormous gap. A gap means a capture the corpus does not
    have: never written (a crash), deleted, or living outside the folder that was indexed."""
    out = []
    for (ses,) in cx.execute("SELECT DISTINCT coalesce(session,'') FROM captures WHERE "
                             "coalesce(run_tag,'') = ? ORDER BY 1", (run,)):
        recs = sorted(r[0] for r in cx.execute(
            "SELECT rec FROM captures WHERE coalesce(run_tag,'') = ? AND coalesce(session,'') = ? "
            "AND rec IS NOT NULL", (run, ses)))
        if len(recs) < 2:
            continue
        missing = sorted(set(range(recs[0], recs[-1] + 1)) - set(recs))
        if missing:
            out.append((ses, recs[0], recs[-1], len(missing), _ranges(missing)))
    return out


def _ranges(nums, cap=6):
    """[1,2,3,9] -> '1-3, 9'. Capped: a batch missing 300 recs must not print 300 numbers."""
    spans, start, prev = [], nums[0], nums[0]
    for n in nums[1:] + [None]:
        if n != prev + 1:
            spans.append(f"{start}-{prev}" if start != prev else f"{start}")
            start = n
        prev = n
    return ", ".join(spans[:cap]) + (f", +{len(spans) - cap} more" if len(spans) > cap else "")


def check_run(cx, run, detail=True):
    """Completeness and integrity of one batch. Returns the number of flagged lanes.

    The question is NOT "are the numbers good" (that is scorecard/compare-runs) but "is this batch
    the batch I think it is": every lane flew every cell, nothing died half way, nothing aborted
    unexplained. An analysis over a batch with a dead lane is not wrong, it is silently answering a
    smaller question -- and 9 captures vs 48 is invisible in every aggregate view there is."""
    hdr = cx.execute(
        "SELECT count(*), count(DISTINCT airframe), count(DISTINCT card), sum(aborted), "
        "min(rec), max(rec), min(coalesce(started,'')), group_concat(DISTINCT mod_version) "
        "FROM captures WHERE coalesce(run_tag,'') = ?", (run,)).fetchone()
    n, na, nc, ab, r0, r1, st, ver = hdr
    if not n:
        print(f"{run}: no captures indexed with that run tag")
        return 0
    print(f"\n=== {run or '(untagged)'} ===  captures {n}  airframes {na}  cards {nc}  aborted {ab or 0}  "
          f"rec {r0}..{r1}  v{ver}  {(st or '')[:16]}")

    lanes = cx.execute(
        "SELECT airframe, count(*), count(DISTINCT card), min(rec), max(rec), sum(aborted) "
        "FROM captures WHERE coalesce(run_tag,'') = ? GROUP BY airframe ORDER BY 2", (run,)).fetchall()
    med = statistics.median([l[1] for l in lanes]) if lanes else 0
    per_cell = cx.execute(
        "SELECT airframe, count(*) FROM captures WHERE coalesce(run_tag,'') = ? "
        "GROUP BY airframe, card", (run,)).fetchall()
    cellmin = {}
    cellmax = {}
    for af, k in per_cell:
        cellmin[af] = min(cellmin.get(af, k), k)
        cellmax[af] = max(cellmax.get(af, k), k)

    rows, flagged = [], 0
    for af, cnt, cells, mn, mx, lab in lanes:
        flag = ""
        if med and cnt < LANE_SHORT_FRAC * med:
            flag = f"** {cnt} vs median {med:g} ({cnt / med:.0%})"
        if r1 and mx is not None and mx < 0.5 * r1:
            flag = (flag + "  " if flag else "") + f"** STOPPED EARLY: last rec {mx} of {r1}"
        if flag:
            flagged += 1
        rows.append((af or "(no sidecar)", cnt, cells, cellmin.get(af, 0), cellmax.get(af, 0),
                     mn, mx, lab or 0, flag))
    if detail or flagged:
        table(["airframe", "caps", "cells", "min/cell", "max/cell", "firstRec", "lastRec",
               "abort", "flag"], rows, count=False)

    gaps = _rec_gaps(cx, run)
    if gaps:
        print("  rec GAPS (captures the index does not have):")
        table(["session", "from", "to", "missing", "ranges"], gaps, count=False)
    elif detail:
        print("  rec sequence: contiguous")

    if ab:
        print("  aborted captures:")
        table(["airframe", "card", "rec", "stop reason"],
              cx.execute("SELECT airframe, card, rec, stop FROM captures WHERE "
                         "coalesce(run_tag,'') = ? AND aborted = 1 ORDER BY rec", (run,)).fetchall(),
              count=False)

    warns = cx.execute("SELECT file, substr(parse_warn,1,90) FROM captures WHERE "
                       "coalesce(run_tag,'') = ? AND parse_warn IS NOT NULL LIMIT 20",
                       (run,)).fetchall()
    if warns:
        print("  PARSE WARNINGS (scorecard's stderr, per capture):")
        table(["file", "warning"], warns, count=False)

    unk = cx.execute("SELECT s.tag, count(*) FROM segments s JOIN captures c ON c.id = s.capture_id "
                     "WHERE coalesce(c.run_tag,'') = ? AND s.unknown_tag = 1 GROUP BY 1",
                     (run,)).fetchall()
    if unk:
        # scorecard's tag->metric table drifted from the cards once already (v0.71: 19 of 21 segments
        # scored as "unknown"), so an unrecognised tag in a batch means that batch's segments were
        # scored with the generic metric set only.
        print("  UNKNOWN SEGMENT TAGS (scored with the generic metric set only -- fix TAG_TYPE_RULES):")
        table(["tag", "segments"], unk, count=False)
    return flagged


def check(cx, run=None):
    runs = [run] if run else [r[0] or "" for r in cx.execute(
        "SELECT run_tag FROM captures GROUP BY run_tag ORDER BY min(coalesce(started,''))")]
    total = sum(check_run(cx, r, detail=bool(run)) for r in runs)
    print(f"\n{len(runs)} batch(es) checked, {total} flagged lane(s)")


# --- A/B across batches: --diff ---------------------------------------------------------------------

def diff(cx, run_a, run_b, metric="terminalOffDeg", tag=None):
    """Per (airframe, card, tag): mean +- stdev% in both runs, and B/A.

    Railed and arm segments are EXCLUDED, not merely flagged: a railed segment's metrics are no
    signal (scorecard's own predicate, stored as segments.railed), so a ratio built on them measures
    the limit rather than the law -- and the arm window has no metrics at all. Cells missing from
    either run are dropped rather than shown one-sided, because the whole point is the comparison.
    Grouping is compare-runs.py's: (airframe, card, tag), never pooled across airframes."""
    if metric not in cols_of(cx, "segments"):
        sys.exit(f"--diff: no metric column '{metric}' in this index "
                 f"(see CAPTURES-DB.md, or: --query \"SELECT * FROM segments LIMIT 0\" --format csv)")
    m = f's."{metric}"'
    sql = (f'SELECT c.airframe, c.card, s.tag, '
           f'count(CASE WHEN c.run_tag = ? THEN {m} END) nA, '
           f'avg(CASE WHEN c.run_tag = ? THEN {m} END) mA, '
           f'stdev(CASE WHEN c.run_tag = ? THEN {m} END) sA, '
           f'count(CASE WHEN c.run_tag = ? THEN {m} END) nB, '
           f'avg(CASE WHEN c.run_tag = ? THEN {m} END) mB, '
           f'stdev(CASE WHEN c.run_tag = ? THEN {m} END) sB '
           f'FROM segments s JOIN captures c ON c.id = s.capture_id '
           f'WHERE c.run_tag IN (?, ?) AND s.excluded = 0 AND s.railed = 0 AND {m} IS NOT NULL '
           + ("AND s.tag = ? " if tag else "") +
           f'GROUP BY 1,2,3 HAVING nA > 0 AND nB > 0 ORDER BY 1,2,3')
    args = [run_a] * 3 + [run_b] * 3 + [run_a, run_b] + ([tag] if tag else [])
    data = cx.execute(sql, args).fetchall()
    fmt = lambda mean, sd: (f"{mean:.4g} +-{100 * sd / abs(mean):.0f}%" if sd is not None and mean
                            else (f"{mean:.4g} +-n/a" if mean is not None else "-"))
    out = [(af, card, tg, nA, fmt(mA, sA), nB, fmt(mB, sB),
            f"{mB / mA:.3f}" if mA else "-")
           for af, card, tg, nA, mA, sA, nB, mB, sB in data]
    print(f"{metric}: {run_a} vs {run_b}   (railed and arm segments excluded; stdev is SAMPLE, "
          f"as in compare-runs.py)")
    table(["airframe", "card", "tag", f"n {run_a}", run_a, f"n {run_b}", run_b, "B/A"], out)


# --- the card grid: --cards --------------------------------------------------------------------

CARD_DDL = """
CREATE TABLE IF NOT EXISTS cards (
  card       TEXT PRIMARY KEY,   -- the FILE BASENAME: that is the card id the mod binds and the CSV
                                 -- header's '# card' carries, so it joins to captures.card
  name       TEXT,               -- the json's own `name` -- usually equal, not guaranteed
  cls        TEXT,
  note       TEXT,
  airframe   TEXT,               -- the raw comma list, verbatim
  fleet      INTEGER,            -- `count` (0 = "as many as `airframe` names, else Cfg.DroneCount")
  repeat     INTEGER,            -- 0 = use Cfg.ScenarioRepeat
  arm_toggle TEXT,
  start_alt  REAL,
  start_speed REAL,
  start_speed_corner REAL,       -- v0.93: multiple of the LANE airframe's corner speed; wins over speed
  n_segments INTEGER,
  tags       TEXT,               -- comma list of segment tags, in card order
  problems   TEXT,               -- scorecard.card_setup_problems(), joined; NULL = clean
  path       TEXT
)"""

CARD_AF_DDL = """
CREATE TABLE IF NOT EXISTS card_airframes (
  card TEXT NOT NULL, lane INTEGER NOT NULL, airframe TEXT NOT NULL, PRIMARY KEY (card, lane)
)"""


def load_cards(cx, d):
    """The card grid as dimension tables, so "which grid cells have we NEVER flown?" is a LEFT JOIN
    instead of a human reading cards/README.md against a query. Nothing else in the index needs
    them, which is why this is opt-in and separate from the capture pass.

    A card's airframe list is only expanded into card_airframes when it is a real jsonKey list: an
    empty field means "whatever Cfg.DroneAirframe says" and prose means the card predates v0.90 --
    neither is a grid cell, and inventing one would put an airframe in the never-flown report that
    the card never asked for. scorecard.card_setup_problems() is the arbiter, not a second copy of
    the rule."""
    cx.execute(CARD_DDL)
    cx.execute(CARD_AF_DDL)
    # ponytail: rows for a card DELETED from disk linger (upsert, no sweep). Harmless -- the only
    # consumer is the never-flown LEFT JOIN, where a deleted card reads as an unflown one -- and
    # `--query "DELETE FROM cards" --write` is the fix if it ever matters.
    files = sorted(glob.glob(os.path.join(d, "*.json")))
    if not files:
        sys.exit(f"--cards: no *.json under {d}")
    n_af = 0
    for p in files:
        card = os.path.splitext(os.path.basename(p))[0]
        try:
            with open(p, encoding="utf-8") as f:
                c = json.load(f)
        except Exception as e:
            print(f"  unreadable, skipped: {os.path.basename(p)}: {e}", file=sys.stderr)
            continue
        segs = c.get("segments") or []
        probs = sc.card_setup_problems(c)
        af = (c.get("airframe") or "").strip() if isinstance(c.get("airframe"), str) else ""
        insert(cx, "cards", {
            "card": card, "name": c.get("name"), "cls": c.get("cls"), "note": c.get("note"),
            "airframe": af or None, "fleet": c.get("count") or 0, "repeat": c.get("repeat") or 0,
            "arm_toggle": c.get("armToggle") or None, "start_alt": c.get("startAlt"),
            "start_speed": c.get("startSpeed"), "start_speed_corner": c.get("startSpeedCorner") or 0,
            "n_segments": len(segs),
            "tags": ",".join(str(s.get("tag")) for s in segs if isinstance(s, dict)) or None,
            "problems": "; ".join(probs) or None, "path": os.path.abspath(p)})
        cx.execute("DELETE FROM card_airframes WHERE card = ?", (card,))
        if af and not probs:
            for lane, key in enumerate(t.strip() for t in af.split(",")):
                if key:
                    insert(cx, "card_airframes", {"card": card, "lane": lane, "airframe": key})
                    n_af += 1
        if probs:
            print(f"  {card}: {probs[0]}", file=sys.stderr)
    cx.commit()
    print(f"loaded {len(files)} card(s), {n_af} declared lane(s) from {d}")


# --- selftest ---------------------------------------------------------------------------------

SELFTEST_CSV = """# mouseaim recording  v0.94.0  run=R99  rec=7  session=20260101-000000
# started 2026-01-01 00:00:00  t=1.000
# aircraft 'FS-12'
# drone 2
# card selftest-card
# override Control/BelowAlignSuppress=true Control/TurnLeadTime=0.35
# entry v=100.0->250.0 alt=900.0->4000.0 snapBackM=12.5 fuel=0.9->1.0 ctrlReset=1
# config law=EvolvedLegacy maxBank=72 fineAng=6 iCap=0.12 arm=1 armKnob=RelativeTurnLead
# fbw cornerSpeed=160 maxPitchAngVel=0.9 gLimit=9 alphaLimiter=27 alphaLimiterStrength=0.05
t,off,azErr,elevErr,bank,aoa,g,spd,alt,segTag,tSeg,frameMs
"""


def _selftest_rows():
    out, t = [], 0.0
    for i in range(40):                                        # arm window: excluded, no metrics
        out.append(f"{t:.3f},0.10,0.00,0.10,0.0,1.0,1.00,250.0,4000.0,arm,{t:.3f},16.7\n")
        t += 0.0667
    off = 5.0
    for i in range(120):                                       # a decaying az step
        off = max(0.05, off * 0.96)
        out.append(f"{t:.3f},{off:.3f},{off:.3f},0.00,10.0,3.0,2.00,250.0,4000.0,az05,"
                   f"{i * 0.0667:.3f},16.7\n")
        t += 0.0667
    for i in range(60):                                        # a tag nothing in TAG_TYPE_RULES knows
        out.append(f"{t:.3f},0.20,0.20,0.00,0.0,1.0,1.00,250.0,4000.0,zzNotATag,{i * 0.0667:.3f},16.7\n")
        t += 0.0667
    out.append(f"# stop t={t:.3f} dur={t:.1f} samples=220 reason=card 'selftest-card' complete\n")
    return "".join(out)


SELFTEST_SIDECAR = {
    "modVersion": "0.94.0", "session": "20260101-000000", "run": 99, "rec": 7,
    "jsonKey": "Fighter1", "unitName": "FS-12 Compass", "definitionName": "FS-12",
    "massKg": 12345.6, "cornerSpeed": 160.0, "fbwEnabled": True, "aeroPartCount": 21,
    "wingAreaTotal": 40.5, "infoStallSpeed": 75.0, "infoMaxSpeed": 300.0,
    "fbwParameters": [0, 0.9, 160, 300, 200], "verticalLanding": False,
    "loadout": [{"station": 1, "name": "x", "mass": 500.0}, {"station": 2, "name": "y", "mass": 250.0}],
    "airfoilAlphaDeg": [0, 1, 2], "airfoils": [{"name": "w", "cl": [0, 1], "cd": [0, 1]}],
}


def selftest():
    import tempfile
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
        base = os.path.join(d, "mouseaim-rec-v0.94.0-R99-d2-Fighter1-7-selftest-card-20260101-000000")
        with open(base + ".csv", "w", encoding="utf-8") as f:
            f.write(SELFTEST_CSV + _selftest_rows())
        with open(base + ".airframe.json", "w", encoding="utf-8") as f:
            json.dump(SELFTEST_SIDECAR, f)
        db = os.path.join(d, "t.db")
        cx = connect(db)
        n_new, n_skip, n_fail = index_all(cx, expand([d]))
        assert (n_new, n_skip, n_fail) == (1, 0, 0), (n_new, n_skip, n_fail)

        c = dict(zip([x[0] for x in cx.execute("SELECT * FROM captures LIMIT 0").description],
                     cx.execute("SELECT * FROM captures").fetchone()))
        assert c["run_tag"] == "R99", c["run_tag"]
        assert c["mod_version"] == "0.94.0"
        assert c["drone"] == 2 and c["rec"] == 7 and c["replicate"] == 1
        assert c["airframe"] == "Fighter1", c["airframe"]      # sidecar jsonKey, not the '# aircraft'
        assert c["aircraft"] == "FS-12"
        assert c["card"] == "selftest-card"
        assert c["arm"] == 1 and c["arm_knob"] == "RelativeTurnLead"
        assert c["n_rows"] == 220, c["n_rows"]
        assert c["n_cols"] == 12, c["n_cols"]
        assert c["aborted"] == 0 and "complete" in c["stop"]
        assert c["started"] == "2026-01-01 00:00:00", c["started"]
        assert c["entry_snapBackM"] == 12.5 and c["entry_v_from"] == 100.0 and c["entry_v_to"] == 250.0
        assert c["entry_ctrlReset"] == 1.0
        # '# override' (v0.90): the knobs THAT CARD pinned for itself. This path had never run on a
        # real capture -- no shipped card uses `config` -- so the synthetic one carries the line, and
        # a card that pins a knob is exactly the capture whose provenance an A/B most needs.
        assert c["ov_note"] == "Control/BelowAlignSuppress=true Control/TurnLeadTime=0.35", c["ov_note"]
        assert c["ov_Control_BelowAlignSuppress"] == "true"     # non-numeric value kept as text
        assert c["ov_Control_TurnLeadTime"] == 0.35             # ...numeric one as a number
        assert c["sc_massKg"] == 12345.6 and c["sc_cornerSpeed"] == 160.0
        assert c["sc_fbwEnabled"] == 1 and c["sc_verticalLanding"] == 0     # bools -> ints
        assert c["sc_loadoutCount"] == 2 and c["sc_loadoutMassKg"] == 750.0
        assert json.loads(c["sc_fbwParameters"]) == [0, 0.9, 160, 300, 200]
        assert "sc_airfoils" not in c and "sc_airfoilAlphaDeg" not in c     # curves skipped

        segs = cx.execute("SELECT seg_index, tag, type, samples, excluded, unknown_tag "
                          "FROM segments ORDER BY seg_index").fetchall()
        assert [s[1] for s in segs] == ["arm", "az05", "zzNotATag"], segs
        assert segs[0][2] == "arm" and segs[0][4] == 1          # arm window excluded
        assert segs[1][2] == "az_step" and segs[1][3] == 120
        assert segs[2][2] == "unknown" and segs[2][5] == 1      # scorecard's unrecognised-tag warning
        # Metrics are whatever scorecard emitted -- assert they arrived as real columns, not JSON.
        seg_cols = cols_of(cx, "segments")
        assert "terminalOffDeg" in seg_cols and "riseTime90" in seg_cols, sorted(seg_cols)
        off = cx.execute("SELECT terminalOffDeg FROM segments WHERE tag='az05'").fetchone()[0]
        ref = sc.score_run(base + ".csv")["segments"][1]["metrics"]["terminalOffDeg"]["value"]
        assert off == ref, (off, ref)                           # stored == what scorecard returned
        assert cx.execute("SELECT count(*) FROM segments WHERE tag='arm'").fetchone()[0] == 1

        # Idempotency: a second pass changes nothing and opens nothing.
        assert index_all(cx, expand([d])) == (0, 1, 0)
        assert cx.execute("SELECT count(*) FROM captures").fetchone()[0] == 1
        assert cx.execute("SELECT count(*) FROM segments").fetchone()[0] == 3
        # ...and --rebuild replaces rather than duplicates.
        assert index_all(cx, expand([d]), rebuild=True) == (1, 0, 0)
        assert cx.execute("SELECT count(*) FROM captures").fetchone()[0] == 1
        assert cx.execute("SELECT count(*) FROM segments").fetchone()[0] == 3

        materialize_rows(cx, "R99")
        assert cx.execute("SELECT count(*) FROM rows").fetchone()[0] == 220
        assert cx.execute("SELECT count(*) FROM rows WHERE frameMs > 20").fetchone()[0] == 0
        assert cx.execute("SELECT count(*) FROM rows WHERE segTag='az05'").fetchone()[0] == 120

        adir = os.path.join(d, "arch")
        archive(cx, adir, "R99")
        got = os.listdir(glob.glob(os.path.join(adir, "R99-*"))[0])
        assert len(got) == 2 and any(f.endswith(".airframe.json") for f in got), got

        # The SQL aggregates: stdev must be the SAMPLE one (n-1), i.e. what compare-runs.py prints.
        # A population stdev is 6.9% smaller at the n=8 the shipped grid flies -- a difference that
        # reads as "the noise floor moved" rather than as two tools disagreeing.
        cx.execute("CREATE TEMP TABLE agg(x)")
        cx.executemany("INSERT INTO agg VALUES (?)", [(2.0,), (4.0,), (6.0,), (None,)])
        sd, md = cx.execute("SELECT stdev(x), median(x) FROM agg").fetchone()
        assert abs(sd - statistics.stdev([2.0, 4.0, 6.0])) < 1e-12, sd
        assert md == 4.0, md
        assert cx.execute("SELECT stdev(x) FROM agg WHERE x = 2.0").fetchone()[0] is None   # n=1
        assert cx.execute("SELECT stdev(x), median(x) FROM agg WHERE 0").fetchone() == (None, None)

        # --stats/--check/--diff must at least run over a real (if tiny) index; --check on a
        # single-lane batch has no median to flag against and must not divide by zero.
        with redirect_stderr(io.StringIO()), io.StringIO() as out:
            from contextlib import redirect_stdout
            with redirect_stdout(out):
                stats(cx)
                check(cx, "R99")
                diff(cx, "R99", "R99", "terminalOffDeg")
            txt = out.getvalue()
        assert "Fighter1" in txt and "az05" in txt, txt[:400]
        assert "1.000" in txt, "diff of a run against itself must give ratio 1.000"

        # --cards over the repo's own grid (skipped if this checkout has no cards/ folder).
        cdir = os.path.join(os.path.dirname(_HERE), "cards")
        if os.path.isdir(cdir):
            with redirect_stderr(io.StringIO()), io.StringIO() as out:
                from contextlib import redirect_stdout
                with redirect_stdout(out):
                    load_cards(cx, cdir)
            n_cards = cx.execute("SELECT count(*) FROM cards").fetchone()[0]
            assert n_cards >= 16, n_cards
            assert cx.execute("SELECT count(*) FROM cards WHERE problems IS NOT NULL")\
                     .fetchone()[0] == 0, "a shipped card fails scorecard.card_setup_problems"
            # `airframe` is a comma list; every expanded lane must be one whitespace-free token.
            for (k,) in cx.execute("SELECT DISTINCT airframe FROM card_airframes"):
                assert k and not any(ch.isspace() for ch in k), k
        cx.close()

        # Read-only mode: the aggregates are registered there too (a --query is the main consumer),
        # and a write must be refused rather than quietly succeeding.
        ro = connect(db, readonly=True)
        assert ro.execute("SELECT stdev(terminalOffDeg) FROM segments").fetchone() is not None
        try:
            ro.execute("DELETE FROM segments")
            raise AssertionError("read-only connection accepted a DELETE")
        except sqlite3.OperationalError:
            pass
        ro.close()
    print("index-captures selftest: OK")


# --- cli --------------------------------------------------------------------------------------

def main(argv):
    ap = argparse.ArgumentParser(description="SQLite index over the flight-capture corpus.",
                                 epilog="See the module docstring for example queries.")
    ap.add_argument("paths", nargs="*", help="dirs, globs or CSVs (default: $NUCLEAR_OPTION_PATH/BepInEx)")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--run", help="only this run tag (e.g. R29); also selects --archive's batch")
    ap.add_argument("--rebuild", action="store_true", help="re-index even unchanged captures")
    ap.add_argument("--with-rows", metavar="RUNTAG", help="materialize one batch's raw rows")
    ap.add_argument("--archive", metavar="DIR", help="copy the --run batch's files out of <game>")
    ap.add_argument("--cards", metavar="DIR", help="load cards/*.json into the cards dimension tables")
    ap.add_argument("--query", metavar="SQL")
    ap.add_argument("--format", choices=("table", "csv", "json"), default="table",
                    help="--query output format (default table)")
    ap.add_argument("--limit", type=int, default=DEFAULT_LIMIT,
                    help=f"--query row cap (default {DEFAULT_LIMIT}; 0 = no cap)")
    ap.add_argument("--write", action="store_true",
                    help="open the db read-WRITE for --query (default is read-only)")
    ap.add_argument("--summary", action="store_true", help="one line per batch")
    ap.add_argument("--stats", action="store_true", help="what is in this database (start here)")
    ap.add_argument("--check", nargs="?", const="", metavar="RUNTAG",
                    help="completeness/integrity of one batch, or every batch if given no tag")
    ap.add_argument("--diff", nargs=2, metavar=("RUNA", "RUNB"),
                    help="per (airframe, card, tag) mean +- stdev%% in both runs, and the ratio")
    ap.add_argument("--metric", default="terminalOffDeg", help="--diff's metric (default terminalOffDeg)")
    ap.add_argument("--tag", help="--diff: restrict to one segment tag")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)

    if a.selftest:
        return selftest()

    # Read-only unless something is actually going to write. --query is read-only ON PURPOSE (--write
    # opts out): the db costs ~30 s over 344 MB to rebuild and a mistyped query should not be able to
    # cost that.
    writes = bool(a.paths or a.with_rows or a.archive or a.cards or a.write)
    if not writes and not os.path.isfile(a.db):
        ap.error(f"{a.db} does not exist — build it first: index-captures.py <game>/BepInEx")
    cx = connect(a.db, readonly=not writes)
    paths = a.paths
    if not paths and not (a.query or a.summary or a.stats or a.check is not None or a.diff
                          or a.with_rows or a.archive or a.cards):
        env = os.environ.get("NUCLEAR_OPTION_PATH")
        if not env:
            ap.error("no paths given and NUCLEAR_OPTION_PATH is not set — pass the BepInEx folder")
        paths = [os.path.join(env, "BepInEx")]
    if paths:
        files = expand(paths)
        if not files:
            ap.error(f"no mouseaim-rec-*.csv under {paths}")
        index_all(cx, files, rebuild=a.rebuild, run=a.run)
    if a.with_rows:
        materialize_rows(cx, a.with_rows)
    if a.archive:
        run = a.run or a.with_rows
        if not run:
            ap.error("--archive needs --run <tag> to say which batch")
        archive(cx, a.archive, run)
    if a.cards:
        load_cards(cx, a.cards)
    if a.summary:
        summary(cx)
    if a.stats:
        stats(cx)
    if a.check is not None:
        check(cx, a.check or None)
    if a.diff:
        diff(cx, a.diff[0], a.diff[1], a.metric, a.tag)
    if a.query:
        query(cx, a.query, a.format, a.limit)
    cx.close()


if __name__ == "__main__":
    main(sys.argv[1:])
