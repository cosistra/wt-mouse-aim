#!/usr/bin/env python3
"""Navigation index over the ONE-FILE decompile of the game assembly. Stdlib only.

WHY THIS EXISTS: the 0.34.1 decompile is a single 183,315-line file, and every `:NNNNN` citation in
this repo's docs and comments is a line number INTO IT — so it must stay the authority: do not
split it, do not renumber it, do not move it. The cost is that finding anything means grepping
blind across 183k lines, which is expensive and quietly misses things. This parses it once and
answers the questions a grep answers badly:

    python debugtests/index-decompiled.py --type AeroPart        # one type's whole member index
    python debugtests/index-decompiled.py --member CheckAttachment   # WHO declares this?
    python debugtests/index-decompiled.py --grep "Detach|attachInfo"  # regex over names+signatures
    python debugtests/index-decompiled.py --at 61803             # what is AT this citation?
    python debugtests/index-decompiled.py --list --min-lines 800 # orientation, biggest first
    python debugtests/index-decompiled.py --json <path>          # dump the index
    python debugtests/index-decompiled.py --selftest

The file defaults to E:/Downloads/modNO/decompiled-0341/Assembly-CSharp.decompiled.cs, overridden by
$NUCLEAR_OPTION_DECOMPILE or a positional path. **A fresh checkout will not have it** — this tool is
never a hard dependency of anything; it exits with a named message and no traceback.

LICENSE: the decompiled source is game code and is NOT redistributable (LICENSE, CLAUDE.md). The
TOOL is committed; the index is NOT. The parse cache is written BESIDE THE DECOMPILE, never under
the repo, and `--json` warns loudly if you point it inside the repo.

CACHE: a cold parse of the 0.34 monolith is ~1.2 s and a warm load ~0.03 s (40x), so the index is
cached as JSON next to the decompile, keyed on (path, mtime, size, PARSER_VERSION) and silently
rebuilt when any of those move. Every command prints its own cold/warm timing on stderr, so the
next reader never has to guess what a query costs. `--rebuild` forces.

WHAT THE PARSER IS: brace-depth tracking plus anchored regexes, NOT a C# parser, and it does not
try to be one. ILSpy output is regular — Allman braces, one declaration per line, attributes on
their own lines, no `#region`, no verbatim strings, `where` clauses on the declaration line — and
that regularity is the whole reason this works. **Anything it cannot classify is COUNTED and
reported**, never dropped: `--list` prints the skip count, `--json` carries the skipped lines
verbatim, and `--selftest` asserts on it. A tool that quietly indexes 80% of the file is worse than
one that says what it missed. Two loud global checks back that up: brace depth must return to 0 at
EOF, and the frame stack must be empty — either failing means the string/comment stripper lost a
literal somewhere and the whole index is suspect. On 0.34 it currently skips **0** declarations,
which took four passes to reach: the four constructs it had to learn were the un-comma'd LAST enum
value, a ctor initializer on its own line (`: base(x)`), a ValueTuple inside a generic type
(`List<(Key k, string v)> f` is a FIELD, not a method), and ~170 Mirage codegen members whose ILSpy
names are not legal C# identifiers (``_Write_...List`1<T>``, `UserCode_RpcFoo_-1045112216`).

WHAT IT STILL GETS ONLY ROUGHLY, on purpose: `base` vs `interfaces` is split by an `^I[A-Z]` name
heuristic (display only, nothing keys off it), an explicit interface implementation keeps its
qualifier as its name (`IPlacingMenu.Place`), and a field's `type` is "the head minus modifiers
minus the name" rather than a parsed type.

WHAT IT DELIBERATELY DOES NOT DO: no method bodies, no call graph, no type resolution. A member's
`sig` is the raw declaration line, which is what you want to read anyway.
"""
import argparse
import json
import os
import re
import sys
import time

DEFAULT_DECOMPILE = r"E:/Downloads/modNO/decompiled-0341/Assembly-CSharp.decompiled.cs"
PARSER_VERSION = 3          # bump on any parser change; the cache key includes it
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Member/type modifiers, dropped when recovering a field's declared type. Parameter modifiers
# (in/out/ref/this/params) are deliberately absent: they never lead a member declaration.
MODIFIERS = {"public", "private", "protected", "internal", "static", "readonly", "const",
             "virtual", "override", "abstract", "sealed", "extern", "unsafe", "new", "partial",
             "async", "volatile", "event", "fixed", "implicit", "explicit", "required"}

_MOD = r"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|file|ref)\s+)*"
# A name is an identifier, OR a compiler-generated one like <EjectionSequence>d__207.
_NAME = r"(?:<[^<>]+>)?[A-Za-z_]\w*"
TYPE_RE = re.compile(rf"^{_MOD}(class|struct|interface|enum)\s+({_NAME})")
DELEGATE_RE = re.compile(rf"^{_MOD}delegate\s+.*?({_NAME})\s*(?:<[^()]*>)?\s*\(")
NS_RE = re.compile(r"^namespace\s+([\w\.]+)")
ENUM_VAL_RE = re.compile(r"^([A-Za-z_]\w*)\s*(?:=|,|$)")
TRAIL_GENERIC = re.compile(r"<[^<>]*>\s*$")


# --- lexing: blank out comments and literals so braces inside them do not move the depth --------

def clean_lines(lines):
    """Per-line copy with //, /* */, "..." and '...' blanked. Braces inside a string literal are
    the one thing that silently corrupts brace-depth tracking, and interpolated strings ($"{x}")
    are everywhere in this file — so literals are blanked WHOLE rather than parsed."""
    out, in_block = [], False
    for s in lines:
        if in_block:
            j = s.find("*/")
            if j < 0:
                out.append("")
                continue
            s, in_block = s[j + 2:], False
        res, i, n = [], 0, len(s)
        while i < n:
            c = s[i]
            if c in '"\'':
                j = i + 1
                while j < n:
                    if s[j] == "\\":
                        j += 2
                        continue
                    if s[j] == c:
                        break
                    j += 1
                res.append(c * 2)
                i = j + 1
                continue
            if c == "/" and i + 1 < n and s[i + 1] == "/":
                break
            if c == "/" and i + 1 < n and s[i + 1] == "*":
                j = s.find("*/", i + 2)
                if j < 0:
                    in_block = True
                    break
                res.append(" ")
                i = j + 2
                continue
            res.append(c)
            i += 1
        out.append("".join(res))
    return out


def first_assign(s):
    """Index of the first `=` that is an assignment (not ==, =>, <=, >=, !=), else -1."""
    for m in re.finditer(r"=", s):
        i = m.start()
        if s[i - 1:i] in ("=", "<", ">", "!") or s[i + 1:i + 2] == "=":
            continue
        return i
    return -1


def tail_name(head):
    """Last identifier in `head`, with trailing generic params dropped. `void Foo<T>` -> Foo.

    Falls back to the last whitespace-delimited token, which is what recovers the Mirage codegen
    members ILSpy renders with names no C# compiler would accept -- `_Write_System.Int32[]`,
    ``_Write_...List`1<T>``, `UserCode_RpcFoo_-1045112216`. Those are ~170 real members of this
    assembly; dropping them to keep a tidy identifier regex would be the silent-80% failure."""
    head = TRAIL_GENERIC.sub("", head).rstrip()
    m = re.search(rf"({_NAME})$", head)
    if m:
        return m.group(1)
    tok = head.split()[-1] if head.split() else ""
    return tok if re.fullmatch(r"[\w`.\[\]<>,\-]*[A-Za-z][\w`.\[\]<>,\-]*", tok) else None


def param_paren(s):
    """Index of the `(` that opens a PARAMETER LIST, or -1.

    Not `s.find("(")`: a ValueTuple lives inside the type (`List<(Key k, string v)> options = ...`
    is a FIELD, `(bool ok, X y) Place(bool shift)` is a method whose RETURN type opens with one).
    So: the first paren at angle-bracket depth 0 that is preceded by something a name can end with."""
    ang = 0
    for i, c in enumerate(s):
        if c == "<":
            ang += 1
        elif c == ">":
            ang = max(0, ang - 1)
        elif c == "(" and ang == 0:
            head = TRAIL_GENERIC.sub("", s[:i]).rstrip()
            if head and (head[-1].isalnum() or head[-1] in "_]`"):
                return i
    return -1


def decl_type(head, name):
    """Best-effort declared type of a field/property: the head minus modifiers minus the name."""
    words = [w for w in re.split(r"\s+", TRAIL_GENERIC.sub("", head).strip()) if w]
    if words and words[-1] == name:
        words = words[:-1]
    words = [w for w in words if w not in MODIFIERS]
    return " ".join(words) or None


# --- the parser ---------------------------------------------------------------------------------

def classify(s, owner_kind, owner_name):
    """(kind, name) for one declaration line, or None if it cannot be classified.

    `s` is the CLEANED, stripped declaration text. `owner_kind` is the enclosing container's kind
    ('enum' bodies hold values, nothing else), `owner_name` names constructors."""
    if owner_kind == "enum":
        m = ENUM_VAL_RE.match(s)
        return ("enum_value", m.group(1)) if m else None

    m = TYPE_RE.match(s)
    if m:
        return (m.group(1), m.group(2))
    m = DELEGATE_RE.match(s)
    if m:
        return ("delegate", m.group(1))

    body = s.rstrip(";").rstrip()
    b = body.find("{")
    if b >= 0 and re.search(r"\b(get|set|init)\b", body[b:]):
        name = tail_name(body[:b])               # `public T X { get; set; } = new T();`
        return ("property", name) if name else None
    if "operator" in body:                       # operator ==(...) / implicit operator Foo(...)
        m = re.search(r"\boperator\s+(\S+?)\s*\(", body)
        if m:
            return ("method", "operator " + m.group(1))
    if re.search(r"\bthis\s*\[", body):
        return ("property", "this[]")

    p = param_paren(body)
    q = first_assign(body)
    if q >= 0 and (p < 0 or q < p):              # initializer / expression body before any paren
        head = body[:q].rstrip()
        expr_bodied = body[q:q + 2] == "=>"
        name = tail_name(head)
        if not name:
            return None
        if expr_bodied:
            return ("property", name)            # `public T X => expr;`  (methods keep their ())
        return ("event" if re.search(r"\bevent\b", head) else "field", name)
    if p >= 0:
        name = tail_name(body[:p])
        if not name:
            return None
        return ("method", name)
    if "{" in body:                              # auto-property: `public int X { get; set; }`
        name = tail_name(body[:body.index("{")])
        return ("property", name) if name else None
    # No parens, no assignment: a bare field (`public float dragArea;`) or a property whose
    # get/set block opens on the next line. The caller decides which, by whether a body follows.
    name = tail_name(body)
    if not name:
        return None
    return ("event" if re.search(r"\bevent\b", body) else "field", name)


def parse(path):
    """Parse the monolith into {"types": [...], "skipped": [...], ...}. Loud on brace imbalance."""
    with open(path, encoding="utf-8", errors="replace") as f:
        raw = f.read().splitlines()
    clean = clean_lines(raw)

    types, skipped = [], []
    # One frame per open `{ }` body. Every frame carries `obj`: the type or member dict its closing
    # brace must stamp an `end` line onto (None for a namespace). One shape for both, so a member's
    # range and a type's range come from the same three lines of bookkeeping.
    stack = []
    pending = None      # a declaration awaiting its `{` on the following line
    depth = 0
    ns_of = [""]        # namespace name stack, parallel to the namespace frames

    TYPEKINDS = ("class", "struct", "interface", "enum")

    def top_container():
        for fr in reversed(stack):
            if fr["kind"] in ("namespace",) + TYPEKINDS:
                return fr
        return None

    def enclosing_type():
        for fr in reversed(stack):
            if fr["kind"] in TYPEKINDS:
                return fr["obj"]
            if fr["kind"] == "namespace":
                return None
        return None

    def open_type(kind, name, line, sig):
        parent = enclosing_type()
        ns = ns_of[-1]
        fq = (parent["fq"] + "." + name) if parent else ((ns + "." + name) if ns else name)
        t = {"name": name, "fq": fq, "ns": ns, "kind": kind, "base": None, "interfaces": [],
             "start": line, "end": line, "lines": 1, "parent": parent["fq"] if parent else None,
             "sig": sig, "members": []}
        m = re.search(r":\s*(.+)$", re.sub(r"\bwhere\b.*$", "", sig))
        if m:
            parts = [p.strip() for p in re.split(r",(?![^<>]*>)", m.group(1)) if p.strip()]
            if parts:
                # ILSpy always emits the base class first when there is one; anything starting
                # with `I` + uppercase is taken as an interface. Heuristic, and it is only used
                # for display -- nothing keys off it.
                if re.match(r"^I[A-Z]", parts[0]):
                    t["interfaces"] = parts
                else:
                    t["base"], t["interfaces"] = parts[0], parts[1:]
        types.append(t)
        if parent:
            parent["members"].append({"kind": "nested", "name": name, "line": line, "end": line,
                                      "sig": sig, "type": None})
        return t

    for i, cl in enumerate(clean, 1):
        s = cl.strip()
        if not s:
            continue

        if s.startswith("{"):
            depth += 1
            if pending is not None:
                pending["body_depth"] = depth
                stack.append(pending)
                pending = None
            rest = s[1:].strip()
            if not rest:
                continue
            s = rest                              # `{ get; set; }` etc. -- fall through to braces

        # Close any frames whose body just ended. Counted per closing brace on the line.
        for ch in s:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                while stack and depth < stack[-1]["body_depth"]:
                    fr = stack.pop()
                    if fr["kind"] == "namespace":
                        ns_of.pop()
                    elif fr["obj"] is not None:
                        fr["obj"]["end"] = i
        if s in ("{", "}", "};", "}", ";"):
            continue
        s2 = s.lstrip("{} \t")
        if not s2 or s2 in (";",):
            continue
        if s2.startswith("[") or s2.startswith("#"):      # attribute / preprocessor line
            continue
        if s2.startswith("using ") or s2.startswith("using("):
            continue
        if pending is not None and s2.startswith(":"):
            continue                              # ctor initializer on its own line: `: base(x)`
        if pending is not None:
            # A declaration is pending but this line is not its `{`. ILSpy never does this, so it
            # is a parse surprise worth counting rather than silently reinterpreting.
            skipped.append({"line": pending["start"],
                            "text": raw[pending["start"] - 1].strip()[:160],
                            "why": "declaration with no following brace"})
            pending = None

        cont = top_container()
        at_container = (depth == 0 and cont is None) or (cont is not None and depth == cont["body_depth"])
        if not at_container:
            continue                              # inside a member body: not our business

        m = NS_RE.match(s2)
        if m:
            pending = {"kind": "namespace", "name": m.group(1), "start": i, "obj": None}
            ns_of.append(m.group(1))
            continue

        owner = enclosing_type()
        got = classify(s2, owner["kind"] if owner else None, owner["name"] if owner else None)
        if got is None:
            skipped.append({"line": i, "text": raw[i - 1].strip()[:160], "why": "unclassified"})
            continue
        kind, name = got
        sig = raw[i - 1].strip()
        tail = s2.rstrip()

        if kind in TYPEKINDS:
            t = open_type(kind, name, i, sig)
            if not tail.endswith(";"):
                pending = {"kind": kind, "name": name, "start": i, "obj": t}
            continue
        if kind == "delegate":
            open_type("delegate", name, i, sig)["end"] = i
            continue
        if owner is None:
            skipped.append({"line": i, "text": sig[:160], "why": "member outside any type"})
            continue

        head = s2.rstrip(";").rstrip()
        mem = {"kind": kind, "name": name, "line": i, "end": i, "sig": sig, "type": None}
        if kind in ("field", "event", "property"):
            cut = first_assign(head)
            mem["type"] = decl_type(head[:cut] if cut > 0 else head, name)
        owner["members"].append(mem)
        if kind == "enum_value":
            continue                              # the last one carries no comma; never a body

        if not tail.endswith(";") and not tail.endswith(",") and not tail.endswith("}"):
            # A body (or a multi-line initializer) follows; that frame gives the member its end
            # line. A bare name with a body is a property whose get/set block opens next line --
            # the one case classify() cannot decide on its own, because it cannot look ahead.
            if kind == "field" and owner["kind"] != "enum" and first_assign(head) < 0:
                mem["kind"] = "property"          # ...but `int[] t = new int[3]` + `{ 1, 2, 3 };`
                                                  # is still a FIELD; the block is its initializer.
            pending = {"kind": "member", "name": name, "start": i, "obj": mem}

    problems = []
    if depth != 0:
        problems.append(f"brace depth ended at {depth}, not 0 — the literal stripper lost something")
    if stack:
        problems.append(f"{len(stack)} frame(s) never closed: "
                        + ", ".join(f"{f['kind']} {f.get('name')} @{f['start']}" for f in stack[:5]))
    for t in types:
        t["lines"] = t["end"] - t["start"] + 1

    return {"path": os.path.abspath(path), "n_lines": len(raw), "types": types,
            "skipped": skipped, "problems": problems, "parser": PARSER_VERSION}


# --- cache ---------------------------------------------------------------------------------------

def cache_path(src):
    """BESIDE THE DECOMPILE, never under the repo — the index is derived game code."""
    d, base = os.path.split(os.path.abspath(src))
    return os.path.join(d, os.path.splitext(base)[0] + ".index.json")


def load(src, rebuild=False, quiet=False):
    if not os.path.isfile(src):
        sys.exit(f"decompile not found: {src}\n"
                 "  A fresh checkout has none — nothing in this repo depends on it.\n"
                 "  Generate one (see CLAUDE.md 'Decompiling the game'), then pass the path, or set\n"
                 "  NUCLEAR_OPTION_DECOMPILE=<...>/Assembly-CSharp.decompiled.cs")
    st = os.stat(src)
    cp = cache_path(src)
    t0 = time.time()
    if not rebuild and os.path.isfile(cp):
        try:
            with open(cp, encoding="utf-8") as f:
                idx = json.load(f)
            k = idx.get("key") or {}
            if (k.get("path") == os.path.abspath(src) and k.get("size") == st.st_size
                    and abs(k.get("mtime", 0) - st.st_mtime) < 1e-6
                    and k.get("parser") == PARSER_VERSION):
                if not quiet:
                    print(f"index: {len(idx['types'])} types from cache in {time.time() - t0:.2f}s "
                          f"(warm)", file=sys.stderr)
                return idx
        except (OSError, ValueError, KeyError):
            pass                                  # a corrupt cache is a rebuild, not a crash
    idx = parse(src)
    idx["key"] = {"path": os.path.abspath(src), "mtime": st.st_mtime, "size": st.st_size,
                  "parser": PARSER_VERSION}
    try:
        with open(cp, "w", encoding="utf-8") as f:
            json.dump(idx, f)
    except OSError as e:
        print(f"index: could not write cache {cp}: {e}", file=sys.stderr)
    if not quiet:
        print(f"index: parsed {idx['n_lines']} lines -> {len(idx['types'])} types, "
              f"{sum(len(t['members']) for t in idx['types'])} members, {len(idx['skipped'])} skipped "
              f"in {time.time() - t0:.2f}s (cold; cached beside the decompile)", file=sys.stderr)
    for p in idx.get("problems", []):
        print(f"index: PROBLEM: {p}", file=sys.stderr)
    return idx


# --- queries --------------------------------------------------------------------------------------

def find_types(idx, needle):
    """Exact (case-insensitive) fq or short name wins outright; otherwise every substring match."""
    n = needle.lower()
    exact = [t for t in idx["types"] if t["fq"].lower() == n or t["name"].lower() == n]
    return exact or [t for t in idx["types"] if n in t["fq"].lower()]


def locate(idx, line):
    """Innermost (type, member) containing `line`. The reverse of a `:NNNNN` citation."""
    best_t = best_m = None
    for t in idx["types"]:
        if t["start"] <= line <= t["end"]:
            if best_t is None or t["lines"] < best_t["lines"]:
                best_t = t
    if best_t:
        for m in best_t["members"]:
            if m["line"] <= line <= m["end"]:
                if best_m is None or (m["end"] - m["line"]) < (best_m["end"] - best_m["line"]):
                    best_m = m
    return best_t, best_m


def show_type(t):
    head = f"{t['kind']} {t['fq']}"
    if t["base"]:
        head += f" : {t['base']}"
    if t["interfaces"]:
        head += ("," if t["base"] else " :") + " " + ", ".join(t["interfaces"])
    print(f"{head}   :{t['start']}-{t['end']}  ({t['lines']} lines"
          + (f", namespace {t['ns']}" if t["ns"] else "") + ")")
    order = {"nested": 0, "enum_value": 1, "field": 2, "event": 3, "property": 4, "method": 5}
    for m in sorted(t["members"], key=lambda m: (order.get(m["kind"], 9), m["line"])):
        rng = f":{m['line']}" + (f"-{m['end']}" if m["end"] > m["line"] else "")
        print(f"  {m['kind']:<10} {rng:<14} {m['sig']}")
    if not t["members"]:
        print("  (no members)")


def main(argv):
    ap = argparse.ArgumentParser(description="Navigation index over the one-file game decompile.",
                                 epilog="See the module docstring. The index is NEVER committed.")
    ap.add_argument("source", nargs="?", help="the decompiled .cs (default: $NUCLEAR_OPTION_DECOMPILE "
                                              f"or {DEFAULT_DECOMPILE})")
    ap.add_argument("--type", help="one type's full member index (substring/fuzzy)")
    ap.add_argument("--member", help="which type(s) declare a member by this name")
    ap.add_argument("--grep", help="regex over Type.Member and signatures")
    ap.add_argument("--at", type=int, metavar="LINE", help="what is at this line number?")
    ap.add_argument("--list", action="store_true", help="every type, biggest first")
    ap.add_argument("--min-lines", type=int, default=0, help="--list: only types at least this big")
    ap.add_argument("--json", metavar="PATH", help="dump the index (NOT into the repo — game code)")
    ap.add_argument("--rebuild", action="store_true", help="ignore the cache")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)

    if a.selftest:
        return selftest()
    src = a.source or os.environ.get("NUCLEAR_OPTION_DECOMPILE") or DEFAULT_DECOMPILE
    idx = load(src, rebuild=a.rebuild)
    did = False

    if a.type:
        did = True
        hits = find_types(idx, a.type)
        if not hits:
            print(f"no type matching {a.type!r}")
        elif len(hits) > 1 and not any(h["name"].lower() == a.type.lower() for h in hits):
            print(f"{len(hits)} types match {a.type!r} — name one:")
            for t in sorted(hits, key=lambda t: -t["lines"])[:40]:
                print(f"  {t['kind']:<10} {t['fq']:<50} :{t['start']}-{t['end']}  {t['lines']} lines")
        else:
            for t in sorted(hits, key=lambda t: -t["lines"]):
                show_type(t)

    if a.member:
        did = True
        n = a.member.lower()
        rows = [(t, m) for t in idx["types"] for m in t["members"] if m["name"].lower() == n]
        if not rows:
            rows = [(t, m) for t in idx["types"] for m in t["members"] if n in m["name"].lower()]
            if rows:
                print(f"(no exact match for {a.member!r}; {len(rows)} substring match(es))")
        for t, m in sorted(rows, key=lambda r: r[1]["line"]):
            rng = f":{m['line']}" + (f"-{m['end']}" if m["end"] > m["line"] else "")
            print(f"{t['fq']}.{m['name']:<28} {m['kind']:<10} {rng:<14} {m['sig']}")
        if not rows:
            print(f"no member named {a.member!r}")

    if a.grep:
        did = True
        rx = re.compile(a.grep, re.I)
        n = 0
        for t in idx["types"]:
            for m in t["members"]:
                if rx.search(f"{t['fq']}.{m['name']}") or rx.search(m["sig"]):
                    print(f"{t['fq']}.{m['name']:<28} :{m['line']:<7} {m['sig']}")
                    n += 1
        print(f"({n} member(s))")

    if a.at is not None:
        did = True
        t, m = locate(idx, a.at)
        if not t:
            print(f":{a.at} is outside every type (using directives, or a parse gap)")
        else:
            print(f":{a.at}  in  {t['kind']} {t['fq']}  :{t['start']}-{t['end']}")
            if m:
                print(f"          member  {t['fq']}.{m['name']}  ({m['kind']}) "
                      f":{m['line']}-{m['end']}\n          {m['sig']}")

    if a.list:
        did = True
        for t in sorted(idx["types"], key=lambda t: -t["lines"]):
            if t["lines"] < a.min_lines:
                continue
            print(f"{t['lines']:>6}  {t['kind']:<10} {t['fq']:<60} :{t['start']}-{t['end']}  "
                  f"{len(t['members'])} members")
        print(f"({len(idx['types'])} types, {sum(len(t['members']) for t in idx['types'])} members, "
              f"{len(idx['skipped'])} declaration(s) skipped)")

    if a.json:
        did = True
        if os.path.abspath(a.json).startswith(REPO + os.sep):
            print("WARNING: that path is inside the repo. The index carries decompiled signatures, "
                  "which are game code and NOT redistributable (LICENSE). Writing it anyway — "
                  "delete it or keep it out of git.", file=sys.stderr)
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(idx, f, indent=1)
        print(f"wrote {a.json}")

    if not did:
        print(f"{len(idx['types'])} types, {sum(len(t['members']) for t in idx['types'])} members, "
              f"{len(idx['skipped'])} skipped. Try --list, --type X, --member X, --grep RX, --at N.")
    return 0


# --- selftest --------------------------------------------------------------------------------------

FIXTURE = '''using System;
using UnityEngine;

public interface IHasThing
{
	int Thing { get; }
}
public class Outer : Base, IHasThing
{
	[SerializeField]
	private float wingArea;

	public float dragArea;

	private List<PartJoint> movingJoints = new List<PartJoint>();

	private static readonly int[] table = new int[3]
	{
		1,
		2,
		3
	};

	public event Action Boom;

	public int Thing => 3;

	public PartJoint[] Joints
	{
		get
		{
			return joints;
		}
	}

	public Outer(int x)
	{
		Debug.Log($"brace in a string: {{ and }} and \\" quote");
	}

	public void CheckAttachment()
	{
		if (attachInfo == null)
		{
			return;
		}
	}

	public static bool TryGet<T>(string k, out T v) where T : Base
	{
		v = default(T);
		return false;
	}

	public static implicit operator bool(Outer o)
	{
		return true;
	}

	private struct <Seq>d__7 : IAsyncStateMachine
	{
		public int state;
	}

	private enum Mode
	{
		Off,
		On = 2
	}
}
public delegate void Ping(int n);
namespace Deep.Space
{
	public class Inner
	{
		public string this[int i] => "x";

		public void Go()
		{
		}
	}
}
'''

# Verified BY HAND against the 0.34.1 decompile on 2026-08-01 (remapped from 0.34 the same day;
# every member below is BYTE-IDENTICAL across the update, only its line number moved). Every entry is a citation that is live
# in this repo's docs/comments today, so a failure here means a CITATION is wrong, not just the
# parser. Forms: ("Type.Member", start, end) asserts the declaration RANGE; ("Type.Member", line)
# asserts only that the line falls INSIDE that member (many citations point at an interesting line
# in a body, not at the declaration).
KNOWN_RANGES = [
    ("AeroPart.CheckAttachment", 74349, 74365),
    # 0.34 CITED THIS AS :59984-60005 and that end line was WRONG -- the `if` inside Check() closes
    # there; the class body closes 2 lines later. Corrected, then remapped +173 for 0.34.1.
    ("Aircraft.PartChecker", 60157, 60180),
]
KNOWN_DECLS = [
    ("AeroPart.Repair", 74231),
    ("UnitPart.Repair", 84262),
    ("UnitPart.IsDetached", 84113),
    ("PartDamageTracker.GetDetachedRatio", 79443),
    ("Aircraft.CheckPhysicsLod", 61992),
    # CITED AS "Aircraft.partLookup" -- the field is declared on the BASE, Unit, and inherited.
    # `--type Aircraft` will never show it; `--member partLookup` is the query that finds it.
    ("Unit.partLookup", 87740),
    # 0.34 CITED THIS AS "OriginShift :19361" -- off by one (a blank line), and on the wrong type:
    # it is FloatingOrigin, not Datum (Datum.AfterOriginShift is CALLED from inside it, :19383).
    ("FloatingOrigin.OriginShift", 19365),
]
KNOWN_INSIDE = [
    ("AeroPart.CreateRB", 74418),
    ("UnitPart.TakeDamage", 84304),
    ("Aircraft.LocalSimFixedUpdate", 61976),
    ("Aircraft.LocalSimFixedUpdate", 61977),      # cited as "Aircraft.gForce :61977"
    ("Aircraft.SetLocalSim", 61406),
    # The transform-write-then-sync span, :19380-19384 in 0.34.1 (:19377-19381 in 0.34). Both ends
    # land inside OriginShift, which is what this asserts.
    ("FloatingOrigin.OriginShift", 19380), ("FloatingOrigin.OriginShift", 19384),
]


def selftest():
    import tempfile
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
        p = os.path.join(d, "fixture.cs")
        with open(p, "w", encoding="utf-8") as f:
            f.write(FIXTURE)
        idx = parse(p)
        assert not idx["problems"], idx["problems"]          # braces balanced through the literals
        by = {t["fq"]: t for t in idx["types"]}
        assert set(by) >= {"IHasThing", "Outer", "Outer.Mode", "Outer.<Seq>d__7", "Ping",
                           "Deep.Space.Inner"}, sorted(by)
        assert by["Outer"]["kind"] == "class" and by["Outer"]["base"] == "Base"
        assert by["Outer"]["interfaces"] == ["IHasThing"], by["Outer"]["interfaces"]
        assert by["IHasThing"]["base"] is None and by["Deep.Space.Inner"]["ns"] == "Deep.Space"
        assert by["Outer.Mode"]["kind"] == "enum" and by["Ping"]["kind"] == "delegate"

        mem = {m["name"]: m for m in by["Outer"]["members"]}
        assert mem["wingArea"]["kind"] == "field" and mem["wingArea"]["type"] == "float"
        assert mem["movingJoints"]["kind"] == "field", mem["movingJoints"]
        assert mem["movingJoints"]["type"] == "List<PartJoint>", mem["movingJoints"]["type"]
        assert mem["table"]["kind"] == "field", mem["table"]      # multi-line array initializer
        assert mem["Boom"]["kind"] == "event", mem["Boom"]
        assert mem["Thing"]["kind"] == "property", mem["Thing"]   # expression-bodied
        assert mem["Joints"]["kind"] == "property", mem["Joints"] # get-block
        assert mem["Outer"]["kind"] == "method"                   # constructor
        assert mem["CheckAttachment"]["kind"] == "method"
        assert mem["TryGet"]["kind"] == "method"                  # generic + where clause
        assert "operator bool" in mem, sorted(mem)
        assert mem["Mode"]["kind"] == "nested" and mem["<Seq>d__7"]["kind"] == "nested"
        assert {m["name"] for m in by["Outer.Mode"]["members"]} == {"Off", "On"}
        assert all(m["kind"] == "enum_value" for m in by["Outer.Mode"]["members"])
        assert {m["name"] for m in by["Deep.Space.Inner"]["members"]} == {"this[]", "Go"}
        # Ranges: the method's declaration line through its closing brace.
        src = FIXTURE.splitlines()
        assert src[mem["CheckAttachment"]["line"] - 1].strip().startswith("public void CheckAttachment")
        assert src[mem["CheckAttachment"]["end"] - 1].strip() == "}"
        # ...and the brace inside the interpolated string did NOT move the depth.
        assert src[mem["Outer"]["end"] - 1].strip() == "}"
        # locate() is the reverse of a citation.
        t, m = locate(idx, mem["CheckAttachment"]["line"] + 2)
        assert t["fq"] == "Outer" and m["name"] == "CheckAttachment", (t["fq"], m)
        # A declaration the parser cannot classify must be COUNTED, never dropped.
        bad = os.path.join(d, "bad.cs")
        with open(bad, "w", encoding="utf-8") as f:
            f.write("public class Z\n{\n\t???\n}\n")
        assert len(parse(bad)["skipped"]) == 1, parse(bad)["skipped"]

    # --- the real file, when this machine has it -------------------------------------------------
    src = os.environ.get("NUCLEAR_OPTION_DECOMPILE") or DEFAULT_DECOMPILE
    if not os.path.isfile(src):
        n = len(KNOWN_RANGES) + len(KNOWN_DECLS) + len(KNOWN_INSIDE)
        print(f"index-decompiled selftest: OK (parser only — no decompile at {src}, "
              f"so the {n} citation checks were SKIPPED)")
        return 0
    idx = load(src, quiet=True)
    assert not idx["problems"], idx["problems"]
    bad = []
    for fq, a, b in KNOWN_RANGES:
        tn, mn = fq.rsplit(".", 1)
        hit = [m for t in idx["types"] if t["fq"] == tn for m in t["members"] if m["name"] == mn]
        hit += [t for t in idx["types"] if t["fq"] == fq]
        got = [(h.get("line", h.get("start")), h["end"]) for h in hit]
        if (a, b) not in got:
            bad.append(f"{fq}: expected :{a}-{b}, index has {got or 'nothing'}")
    for fq, ln in KNOWN_DECLS:
        tn, mn = fq.rsplit(".", 1)
        got = [m["line"] for t in idx["types"] if t["fq"] == tn
               for m in t["members"] if m["name"] == mn]
        if ln not in got:
            bad.append(f"{fq}: expected declaration at :{ln}, index has {got or 'nothing'}")
    for fq, ln in KNOWN_INSIDE:
        t, m = locate(idx, ln)
        name = f"{t['fq']}.{m['name']}" if (t and m) else (t["fq"] if t else "nothing")
        if name != fq and not (m and m["name"] == fq):
            bad.append(f":{ln}: expected to land inside {fq}, index says {name}")
    if bad:
        raise AssertionError("KNOWN-GOOD CITATIONS DID NOT REPRODUCE:\n  " + "\n  ".join(bad))
    print(f"index-decompiled selftest: OK  ({len(idx['types'])} types, "
          f"{sum(len(t['members']) for t in idx['types'])} members, "
          f"{len(idx['skipped'])} skipped; {len(KNOWN_RANGES) + len(KNOWN_DECLS) + len(KNOWN_INSIDE)}"
          " citation checks reproduced)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
