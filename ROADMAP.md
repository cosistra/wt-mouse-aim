# Where we are in the loop — status, not backlog

**Rewritten 2026-08-06 (v1.0.5).** This file used to be a second copy of the ledger and the backlog
— ~85 % duplication, three refuted claims, and a result tree that resolved on R28–R32 while the
corpus stood at R44. Its one unique asset (the R28 per-airframe `flightscore` ranking) now lives in
`LAW-LEDGER.md` `G1`. What it does now is the one job nothing else in the repo does:

| question | file |
|---|---|
| what are we entitled to believe? | [`LAW-LEDGER.md`](LAW-LEDGER.md) |
| what is the numbered backlog? | [`LAW-CHARACTERIZATION.md`](LAW-CHARACTERIZATION.md) §7 |
| **where are we in the iterate → measure → improve loop, right now?** | **this file** |

Keep it under two screens. If a section here grows into an argument, the argument belongs in the
ledger and this file should keep only the verdict and the date.

---

## The loop, as it actually runs

```
    hypothesis ──► arm it (default unchanged) ──► card ──► ONE batch ──► ledger
        ▲                                                                  │
        └──────────── corpus mining / hand-flown report ◄──────────────────┘
```

**One turn of this loop is one evening.** The rate limiter is not analysis and never has been — it is
**flight time**, and the operator is the only one who can supply it. Everything else (arming a
hypothesis, writing a card, scoring a batch, routing the finding) is offline and parallelisable.

Two rules the corpus has already paid for:

1. **A hypothesis is armed, never shipped as a default.** Defaults change only for a bug we can
   prove. This is what keeps `G9`'s clean baseline usable as a regression detector — without it,
   a felt regression cannot be attributed.
2. **A batch that changes no standing doc is a result.** Say so in one line and move on.

---

## Cycle status — 2026-08-06

### Shipped and in the DLL (v1.0.5, deployed)

| | what | evidence |
|---|---|---|
| ✅ | **O13** — the tiltwing blend was inverted; `tBankE *= (1 − heliBlend)` was exactly 0 in forward flight | pre-registered 0.000 / 0.998; code computes 0.0000 / 0.9976 |
| ✅ | **The placement kill** — the deck spread was the sole manufacturer of the fatal anchor displacement | `I12`; 0/32 vs 31/31, Fisher p = 4.1e−5 |
| ✅ | **#20** — `>=` → `>` on the `PEffRevThresh` floor branch | `X5`; 0.45 % of rows, all at the boundary |

### Armed, defaults unchanged — **awaiting one flight each**

These are the whole point of the release. Each is a live hypothesis about why the aircraft feels
wrong, and each is now one card away from a verdict.

| lever | hypothesis | pre-registered pass |
|---|---|---|
| `PitchEffRelax` | `_pitchEff` is a **latch**, not an estimator: gate open on 5.3 % of rows, `pEff` < 0.95 on 97.4 %, pitch demand held at **0.47–0.80 permanently** | `pEff` → ~1 on arm B **with** `terminalOffDeg` down and `stickFlipRateP` not up. Cut DOWN vs UP, never pool |
| `OutputSlew` 6 vs 20 | the law's **only nonlinearity**, never varied in 3 327 captures; describing-function onset `R/(2πA)` = 1.91 Hz against a field report of ~2 Hz | railed-interval count and `stickFlipRateR` fall together; frequency scales ×3.3 |
| `AoaSchedFloorRelative` | `schedFloor = 0.3` is an absolute constant deciding an outcome across a 27° → 10° ceiling range | direction deliberately open — **both outcomes are informative** |
| `LeadFloorContinuous` | the `predFloor` clamp corner wants a smooth asymptote | `f'(0) = −0.9999993`, so only leads that hit the old clamp differ |
| `EvolvedAlignHoldDeg` 5 vs 15 | #21 — `lateralHold` rails at 7.5° and drops the bank pipeline to zero weight | **pre-registered as a predicted NULL** (< 1 % on a path `S1` measures at 2 % of authority). Only > 3 % is informative |

### Ready to fly — 56.8 min, written and validated

```
F2 fleet A:  oblique-6-c;ob-dwell-2;e6-pitch-eff;slew-r06;slew-r20;oblique-28     (43.3 min)
F2 fleet B:  sweep-r25;sweep-r45;sweep-r45c                                       (13.5 min)
```

`oblique-6-c` leads fleet A **unmodified** as the designated harness control. A valid re-fly lands
within 1–3 % of R44; outside that, everything downstream is on sand and the batch is re-flown rather
than analysed. **Index `--with-rows`** — six of the checks in this slate are row-level and cannot be
answered from `segments` at all.

Fleet C (`e4-aoa-floor`, `e5-lead-cont`, the two hold cards, 38 min) is designed but not written.

### The one input only the operator can supply

**Hand-flown captures of the two complaints.** Every capture in the corpus is a synthetic step
commanded by a script; not one is the maintainer saying *"there, that's the thing."* The corpus can
say which candidate is **measurable**; only a hand-flown pass can say which one is **the complaint**.
Ten minutes with recording on — a hard slew below the nose and its mirror above, a 20 s sustained
turn, and fine tracking on a slow-moving point — is worth more than another synthetic batch.

---

## The honest scorecard

**What the harness has bought.** One large law fix that no amount of flying-and-feeling would have
found (the rotorcraft branch had never executed for ~40 versions, `H1`). Thirty-five refutations —
the biggest section in the ledger, and every one is a law change we did *not* make and would have
been wrong. One architectural finding with a mechanism (`K3`: the law's entire response to a
non-responding plant is five terms that each reduce authority and nothing that ever increases it).
And, as of this cycle, the first mechanistic candidate for each of the two complaints.

**What it had not bought, until now.** A single measured fixed-wing improvement. `G9` reports no
regression across eleven releases; read honestly that also says no improvement. Every batch from R41
to R44 was a diagnostic or a self-audit — we built an A/B rig and used it exclusively to audit
itself. **v1.0.5 is the first release whose next batch is an A/B of law changes rather than another
measurement of the instrument.**

**The failure mode this project actually has**, seen three times now and worth naming: *a narrow,
correct null composing into a wrong belief.* `W8` measured the right rows and asked the wrong
question about `_pitchEff`; `GENERALITY-REVIEW` 14 was correct about the boundary and left the reader
thinking the term was fine; between them they closed the file on a latch cutting a quarter to a half
of pitch demand. Two more of the same shape: a dilution figure quoted for a card that never carried
an arm (`X36`), and a knob validated by an experiment structurally unable to see its effect (`X12`,
and `BelowAlignSuppress` again). **The check is cheap — before believing a null, ask what the null
card would have produced.**
