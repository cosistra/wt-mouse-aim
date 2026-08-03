# ORIENTATION — start here if you know nothing about this repo

## 1. What this is

A War Thunder–style **mouse-aim** mod for *Nuclear Option* (BepInEx 5 / HarmonyX plugin). You point a
world-locked marker; a thin **"instructor"** flies the aircraft onto that vector, and the game's own
fly-by-wire still governs the envelope.

The governing goal is larger than "make the aim work": the instructor should **understand and exploit
every airframe's strengths** — jets, helicopters, VTOLs — from one law, and there should be a **real
feedback loop** that *scores* whether a change moved in the right direction rather than asking someone
whether it felt better. Most of this repo is that loop: an unattended drone harness, a card grid, a
scorer, a SQLite corpus of ~2,600 captures.

**Current state, honestly:** the instrument is now good; the law has barely moved. Most findings of the
last two weeks are instrument defects, several of which invalidated earlier law conclusions.

## 2. Read these in this order

| # | File | What it is FOR | Authoritative when |
|---|---|---|---|
| 1 | **`CLAUDE.md`** | *Where code lives.* Layout, paths, build/deploy, every offline tool, the conventions. | Always, for structure + build + sign conventions. Start your first edit here. |
| 2 | **`ARCHITECTURE.md`** | *How it works.* Subsystem map, frame timeline, the `Apply` pipeline in order, mod/game boundary. | Always, for behaviour and call order. Kept green by `debugtests/check-architecture.py`. |
| 3 | **`LAW-LEDGER.md`** | ESTABLISHED / PLAUSIBLE / REFUTED / OPEN, with a citation per line. **All per-batch findings live here now** — see note below. | The arbiter of **what we are entitled to believe**. Read the three corpus-wide invalidations in its header before quoting any pre-R40 number. If a claim is not in ESTABLISHED, do not build on it. |
| 4 | **`LAW-CHARACTERIZATION.md`** | The standing test plan — what to fly, in what order, and why. **§7 is the numbered backlog.** | Authoritative for the test plan and for what a `#n` means (see §6 below — it is currently behind). |
| 5 | **`LAW-WEAKNESS-MAP.md`** | Where the law is weak, ranked by effect × confidence × cost to settle. W1–W8. | For *ranking* what to attack. Each W now carries its own dated correction; read those, not the TL;DR table alone. |
| 6 | **`GENERALITY-REVIEW.md`** | The standing ONE-LAW audit — every constant that should be a probe. Findings 1–18. | For ONE-LAW compliance. Its per-finding verdicts win over its (stale, v0.65-era) summary blocks. |
| 7 | **`AIRFRAMES.md`** | The 14 real jsonKeys + Vstall/Vmax/corner/gLimit/mass, and six traps in the underlying fields. | **Before writing any card `airframe` list or `startSpeed`.** Nothing else in the repo records this data. |
| 8 | **`cards/README.md`** → then `python debugtests/check-card.py cards/*.json` | The card grid, the field table, the `sel[0]` rule, and the launch/preflight procedure. | Before adding or flying a card. Run the checker — it is the cheapest check in the repo. |
| 9 | **`debugtests/CAPTURES-DB.md`** | Column-by-column reference for `captures.db`, the metric × segment-type matrix, the traps — **and the batch index**. | **Before writing any SQL.** Every trap in that schema returns a plausible number instead of an error. |

> **There are no per-batch findings documents.** ~25 of them (`R21`…`R41`, `GATE-CHATTER`,
> `LOOP-AUDIT`, `SESSION-*`, `FLIGHT-PROTOCOL`, `cards/TOMORROW.md`) were consolidated into the
> standing docs above on 2026-08-02, because they went stale faster than they could be maintained and
> disagreed with each other. **A batch analysis now UPDATES a standing doc — it never mints a new
> `R##-*.md`.** To find where an old citation went — `R39-rotor 1d`, `R28-FINDINGS.md §3.2`, and the
> ~60 like them still in `.cs` / `.py` / `cards/*.json` comments — read
> **`debugtests/CAPTURES-DB.md` → *The batch index***. Raw evidence is `captures.db` plus
> `debugtests/archive/`; the deleted prose is in git history.

## 3. Non-negotiable constraints

- **ONE control law for ALL airframes, at all loads and speeds. No per-plane tuning.** Every gain,
  schedule and gate must key off either (a) a per-airframe parameter **probed from the game's own
  components** — FBW, canard, helo probes, **always fail-soft** ("could not read it" is never "the value
  is zero") — or (b) **live physical state** (dynamic pressure, AoA, measured rates and effectiveness).
  Loadout and mass must show up as *achieved-vs-commanded discrepancy*, never as a constant.
  **A fix that only works because a constant suits one plane is wrong even if it fixes the report.**
- **The four standing test cases.** Check every law change against all four: a **light jet at high q**;
  a **loaded jet mushing near its alpha limit above corner speed**; a **low-limit STOL trainer**; a
  **hovering helo**. **Two are still not covered, and the gap moved rather than closed:** the *loaded*
  jet has never been flown at all (a card cannot set stores), and the STOL condition is still unmet for
  the fast jets, whose corner-relative entry puts them at 128–160 m/s. Rotorcraft now fly the real
  branch (v1.0.0) but hover rests on **one** airframe — the other two never hovered. Do not claim
  coverage you do not have; the current state is `LAW-LEDGER.md` O9.
- **There is no G governor anywhere** — not in the mod, not in the game (`ControlsFilter.GLimiter` is
  dead code). Do not write one and do not assume something downstream is catching G.
- **Agents never run `release.ps1`.** It pushes and cuts a GitHub release. Bump `PluginVersion`, get a
  clean Release build, and hand it to the maintainer.

## 4. Working rules — the distilled failure modes of this project

**(a) A ZERO AND A NEVER-TESTED READING ARE INDISTINGUISHABLE, AND WE HAVE REPEATEDLY READ THE ZERO AS
A PASS.** This is the single most expensive recurring failure here. Every one of these produced
*believable numbers* from an apparatus that was not connected to the thing being measured:
- `bankClampActivePct` read `targetBank` — a column written by a control law **deleted 39 versions
  earlier** (v0.60). 27.5% of corpus segments moved > 5 pp when fixed.
- The wobble detector measured the **entry transient**, not the mode. 318 corpus "episodes" → **5**.
- `authorityUsedFrac`, a "fraction of authority used", read **0.977–1.084** — above 1.0. Deleted.
- `dmgFrac` is **structurally 0 on all 641,555 rows** because the row is written *after* the abort.
- The v0.58 **rotorcraft branch never executed** — `_heloOk` false on 48/48 captures, ~40 versions.
  (Fixed v1.0.0; establishing it had cost a row-by-row reconstruction, because the liveness column
  did not exist. It does now.)
- **Replicate 1 of every ABBA lane was a different flight condition and was always arm 0** — it turned
  a null into a publishable "30% win" before anyone noticed. `LAW-LEDGER.md` X27.
- `alpha-sweep` **physically could not reach** the alpha state it was named for (azimuth demand loads
  the wing only through bank, clamped at 72° ⇒ n = 3.24).

  *The rule:* before believing a 0, a null or a clean pass, prove the apparatus **can** produce a
  non-zero. `scorecard.py` now emits a **DEAD COLUMN** warning for exactly this; do not silence it.

**(b) NEVER ASSERT A FACT ABOUT THE CODE YOU HAVE NOT READ THIS SESSION.** Quote `file:line` or do not
claim it. Compaction preserves conclusions and drops their caveats, which produced four wrong factual
claims about this codebase in a single day. **When a subagent that just read the file contradicts you,
it is probably right.**

**(c) DO NOT SHIP A LAW CHANGE, A HARNESS CHANGE AND A METRIC CHANGE IN THE SAME VERSION.** Captures on
either side become incomparable on three axes at once and no amount of re-scoring recovers them.
v0.99.1 did exactly this (law: `RelativeTurnLead` deleted; harness: refcounted pins, abort scope, ABBA
index; metrics: three repaired, five deleted), so **R39 and R40 are not directly comparable.**

**(d) THE FLIGHT IS THE MOST EXPENSIVE STEP AND MUST NOT BE WHERE DESIGN ERRORS ARE DISCOVERED.** Three
cards in two days failed on arithmetic computable in advance — `alpha-sweep` (3.24 g ceiling vs the
4.8–24 g its lanes needed), `stol-*` (declared 90 m/s, flew 340–381), `rotor-*` (never hovered;
`startSpeed: 0` fell through to `DroneSpawnSpeed`). **`python debugtests/check-card.py cards/*.json`
exists to catch this class. Run it before every batch.**

## 5. Build / deploy / test

```bash
dotnet build NuclearOption-MouseAim.csproj -c Release     # 0 errors; MSB3277 is harmless
cp bin/Release/NuclearOption-MouseAim.dll "<game>/BepInEx/plugins/WTMouseAim/"   # game must be closed
python debugtests/check-architecture.py                   # exit 1 on drift — run after any code change
```
`<game>` is auto-discovered (Steam scan); nothing machine-specific is committed. Detail in `CLAUDE.md`.

> **DEPLOY HAZARD — read before editing any `.cs`.** The maintainer's `.claude/settings.local.json`
> registers a `PostToolUse` hook that **automatically builds and deploys on every `.cs` edit**, walking
> up from the edited file to its `.csproj` — so a **worktree** edit deploys *that worktree's* build into
> the one shared game folder. It is silent on success. **An agent editing C# while the maintainer is
> flying will silently replace the binary underneath a running batch**, invalidating it with no error
> anywhere. Coordinate before touching `.cs` during a flight session. (`.md`/`.py` edits are safe — the
> hook filters to `*.cs`.)

## 6. Where the backlog lives — and the one thing to know about numbering

**`LAW-CHARACTERIZATION.md` §7 is documented as the sole authority on what a `#n` means**, and its rules
(allocate `max+1`, never reuse a retired gap, `GENERALITY-REVIEW.md` findings are a separate namespace)
are correct and worth keeping.

**But it is currently behind, and there are three live numbering schemes that do not agree.** §7 tops out
at **#46**; the working task list reached **#80**; `CHANGELOG.md` cites
`#51`, `#61`, `#63`, `#70`, `#71` and "ledger #12", **none of which exist in §7**; and the findings docs
carry a third, document-local scheme (`#53a–c` = R36's follow-ups, `#54a–e` = R37's, `#55a–g` = R39's).
Worst case: **§7's `#46` is closed (SplitSpec, verified passing) while the task list's `#46` is open — the
same number means two different things.**

**So: read §7's "Reconciliation" block first — it states which numbers are trustworthy and which are
ambiguous.** When citing, write the **filename with the number** (`LAW-CHARACTERIZATION.md §7 #45`,
`GENERALITY-REVIEW.md finding 16`) or do not write the number at all.

## 7. Where findings go

**Do not create a new findings document.** A batch analysis lands as edits to the standing docs — a
claim to `LAW-LEDGER.md`, an action item to `LAW-CHARACTERIZATION.md` §7, a ONE-LAW violation to
`GENERALITY-REVIEW.md`, a schema trap to `debugtests/CAPTURES-DB.md`, a card verdict to the card's own
`note` — then one row in the batch index. The full convention, including which finding goes where, is
`debugtests/CAPTURES-DB.md` → *The doc convention*.
