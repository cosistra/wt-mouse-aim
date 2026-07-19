#!/usr/bin/env python3
"""Verify ARCHITECTURE.md still describes the code. Stdlib only; no game install needed.

The diagram in ARCHITECTURE.md rots the moment code moves, and a stale map causes wrong-file
edits. This checks the parts that are mechanically checkable:

  1. every *.cs file at the repo root appears in the node index
  2. every top-level type (class/struct/enum) in those files appears in the node index
  3. the node index names no type that has disappeared
  4. every [HarmonyPatch(typeof(X), "M")] target is listed in the game-types table
  5. the <!-- ARCH-VERSION --> stamp matches PluginVersion in WTMouseAimPlugin.cs

It CANNOT check that the prose and arrows are still true. A reordered Apply stage or a law that
now does something different passes clean here and still needs a human/agent to re-read the L1
section they touched.

Usage:
    python debugtests/check-architecture.py                # verify (exit 1 on drift)
    python debugtests/check-architecture.py --fix-version   # rewrite the version stamp, then verify
    python debugtests/check-architecture.py --selftest      # in-memory asserts on the parsers
    python debugtests/check-architecture.py --hook          # Claude Code Stop-hook mode (see below)
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ARCH = REPO / "ARCHITECTURE.md"
PLUGIN = REPO / "WTMouseAimPlugin.cs"

# Types that live in the code but are deliberately not their own diagram node — nested helpers and
# attribute payloads. Keep this list SHORT: if something here starts carrying real behaviour, it
# has earned a row in the node index instead of an exemption.
EXEMPT_TYPES = {
    "AnFrame",                        # private nested struct: the anomaly ring-buffer frame
    "POINT",                          # Win32 interop struct
}


def strip_comments_and_strings(src: str) -> str:
    """Blank out //, /* */ and "..." so declarations inside them don't parse as real code."""
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", "", src)
    src = re.sub(r'"(?:\\.|[^"\\\n])*"', '""', src)
    return src


def top_level_types(src: str):
    """Top-level (non-nested) class/struct/enum names, i.e. brace depth 1 inside the namespace."""
    src = strip_comments_and_strings(src)
    found, depth = [], 0
    decl = re.compile(r"\b(?:class|struct|enum)\s+([A-Za-z_]\w*)")
    i = 0
    while i < len(src):
        c = src[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        else:
            m = decl.match(src, i)
            # namespace body is depth 1, so its direct children are declared at depth 1
            if m and depth == 1:
                found.append(m.group(1))
                i = m.end()
                continue
        i += 1
    return found


def harmony_targets(src: str):
    """(TypeName, "member") pairs from [HarmonyPatch(typeof(T), "m")] attributes."""
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", "", src)
    return re.findall(r'\[HarmonyPatch\(\s*typeof\(\s*([\w.]+)\s*\)\s*,\s*"([^"]+)"', src)


def plugin_version(src: str):
    m = re.search(r'PluginVersion\s*=\s*"([^"]+)"', src)
    return m.group(1) if m else None


def arch_version(src: str):
    m = re.search(r"<!--\s*ARCH-VERSION:\s*([^\s>]+)\s*-->", src)
    return m.group(1) if m else None


def check(fix_version: bool) -> int:
    if not ARCH.exists():
        print(f"FAIL  {ARCH.name} is missing — the architecture diagram is not optional.")
        return 1

    arch = ARCH.read_text(encoding="utf-8")
    cs_files = sorted(p for p in REPO.glob("*.cs"))
    if not cs_files:
        print("FAIL  no *.cs at the repo root — is this the right directory?")
        return 1

    problems = []

    # --- version stamp ------------------------------------------------------------------
    ver = plugin_version(PLUGIN.read_text(encoding="utf-8")) if PLUGIN.exists() else None
    stamp = arch_version(arch)
    if ver is None:
        problems.append("could not read PluginVersion from WTMouseAimPlugin.cs")
    elif stamp is None:
        problems.append("ARCHITECTURE.md has no <!-- ARCH-VERSION: x.y.z --> stamp")
    elif stamp != ver:
        if fix_version:
            arch = re.sub(r"(<!--\s*ARCH-VERSION:\s*)[^\s>]+(\s*-->)", rf"\g<1>{ver}\g<2>", arch)
            ARCH.write_text(arch, encoding="utf-8")
            print(f"fixed  ARCH-VERSION {stamp} -> {ver}")
        else:
            problems.append(
                f"ARCH-VERSION is {stamp} but PluginVersion is {ver} "
                f"— re-read the diagram, then run with --fix-version"
            )

    # --- files + types in the node index ------------------------------------------------
    all_types, patch_targets = set(), []
    for p in cs_files:
        src = p.read_text(encoding="utf-8")
        if f"`{p.name}`" not in arch:
            problems.append(f"{p.name} is not named in ARCHITECTURE.md (add a node-index row)")
        for t in top_level_types(src):
            if t in EXEMPT_TYPES:
                continue
            all_types.add(t)
            if f"`{t}`" not in arch:
                problems.append(f"type {t} ({p.name}) is not in the node index")
        patch_targets += harmony_targets(src)

    # --- Harmony targets in the game-types table ---------------------------------------
    for typ, member in patch_targets:
        if f"`{typ}.{member}`" not in arch:
            problems.append(
                f"Harmony patch {typ}.{member} is not listed in the "
                f"'Game types we patch or read' table"
            )

    # --- stale node-index rows ----------------------------------------------------------
    # Types the index claims exist. Parsed from the type(s) COLUMN only — scraping the whole
    # table also catches prose like `PluginVersion` in the role column, which is not a type.
    idx = re.search(r"\n## Node index\n(.*?)\n### Game types", arch, flags=re.S)
    if idx:
        claimed = set()
        for row in idx.group(1).splitlines():
            cells = [c.strip() for c in row.strip().strip("|").split("|")]
            if len(cells) < 4 or cells[0] in ("node id", "---") or set(cells[0]) <= {"-", " "}:
                continue
            claimed |= set(re.findall(r"`([A-Z]\w+)`", cells[2]))
        for t in sorted(claimed - all_types - EXEMPT_TYPES):
            problems.append(f"node index names `{t}`, which no longer exists in any *.cs")
    else:
        problems.append("could not find the '## Node index' section in ARCHITECTURE.md")

    if problems:
        print(f"ARCHITECTURE.md is out of date ({len(problems)} problem(s)):\n")
        for pr in problems:
            print(f"  - {pr}")
        print("\nUpdate the diagram + node index in the same change as the code.")
        return 1

    print(
        f"ok  ARCHITECTURE.md matches the code "
        f"({len(cs_files)} files, {len(all_types)} types, {len(patch_targets)} Harmony patches, v{ver})"
    )
    return 0


def hook_mode() -> int:
    """Claude Code Stop-hook entry point (wired up in .claude/settings.json).

    Runs when an agent finishes a turn — the moment the diagram *should* be current, rather than
    nagging on every intermediate edit of a refactor. Exit 2 feeds the message back to the agent as
    actionable feedback so it can fix the drift before handing back.

    Reads the hook payload on stdin. Honours `stop_hook_active`: if we already blocked once and the
    agent still could not resolve it, let the turn end rather than looping forever.
    """
    try:
        payload = json.loads(sys.stdin.read() or "{}")
    except (json.JSONDecodeError, ValueError):
        payload = {}
    if payload.get("stop_hook_active"):
        return 0

    # Only speak up in a repo that actually has the diagram (e.g. a partial checkout).
    if not ARCH.exists():
        return 0

    # Reuse the real check, but keep its stdout out of the transcript on success.
    devnull = open(os.devnull, "w")
    real_stdout, sys.stdout = sys.stdout, devnull
    try:
        rc = check(fix_version=False)
    finally:
        sys.stdout = real_stdout
        devnull.close()

    if rc == 0:
        return 0

    # Re-run visibly so the agent sees exactly which rows are missing.
    print(
        "ARCHITECTURE.md is out of date with the code. Update the affected L1 diagram and the "
        "node index before finishing (see CLAUDE.md 'Keeping the diagram current'). Details:",
        file=sys.stderr,
    )
    real_out, sys.stdout = sys.stdout, sys.stderr
    try:
        check(fix_version=False)
    finally:
        sys.stdout = real_out
    return 2  # exit 2 = feed stderr back to the agent


def selftest() -> int:
    src = """
    namespace N {
        // class CommentedOut
        internal enum Mode { A, B }
        internal static class Outer {
            private struct Nested { public float t; }
            private const string S = "class NotAType";
        }
        [HarmonyPatch(typeof(Foo), "Bar")]
        internal static class FooPatch { }
    }
    """
    types = top_level_types(src)
    assert types == ["Mode", "Outer", "FooPatch"], types
    assert "Nested" not in types
    assert "CommentedOut" not in types
    assert "NotAType" not in types
    assert harmony_targets(src) == [("Foo", "Bar")]
    assert plugin_version('public const string PluginVersion = "0.58.0";') == "0.58.0"
    assert arch_version("<!-- ARCH-VERSION: 0.58.0 -->") == "0.58.0"
    print("ok  selftest passed")
    return 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--fix-version", action="store_true", help="sync the ARCH-VERSION stamp to PluginVersion")
    ap.add_argument("--selftest", action="store_true", help="run in-memory asserts on the parsers")
    ap.add_argument("--hook", action="store_true", help="Claude Code Stop-hook mode (reads stdin JSON)")
    a = ap.parse_args()
    if a.selftest:
        sys.exit(selftest())
    sys.exit(hook_mode() if a.hook else check(a.fix_version))
