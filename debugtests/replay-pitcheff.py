"""Replay the mod's _pitchEff estimator against recorded fbwTgtPR/fbwPR traces.

The v0.64 change (ChaseController.cs, the pitch-effectiveness estimator) made the ratio SIGNED
instead of magnitude-only. This is the check that it does what it claims on real data and -- just
as important -- that it changes NOTHING on healthy captures.

    python debugtests/replay-pitcheff.py debugtests/V62/*.csv
    python debugtests/replay-pitcheff.py --selftest

Expected on the v62 FS-12 set: the 5 reversal captures collapse toward 0 (demand cut), the 12
healthy ones are unchanged. Any HEALTHY capture that moves means the signed ratio is firing on
normal flight and the change is wrong -- that is the regression this guards.

Pure stdlib. Does not import the mod; it reimplements the estimator's filter (attack 0.10 s /
release 1.0 s) -- keep those constants in lockstep with ChaseController.
"""
import sys
import os

ATK, REL = 0.10, 1.0          # pEffAtk / pEffRel in ChaseController
GATE = 0.05                   # rad/s noise gate on the COMMANDED rate
PROBE = 0.15                  # PEffRevThresh: v0.67 dead-command self-probe floor


def step(cur, inst, dt):
    """One filter step: fast attack when the estimate drops, slow release when it recovers."""
    tau = ATK if inst < cur else REL
    return min(1.0, max(0.0, cur + (dt / (tau + dt)) * (inst - cur)))


def replay(samples):
    """samples: iterable of (t, cmd, ach). Returns (v063, v064, v067) estimator series.
    v063 = magnitude-only (blind to reversal); v064 = signed, HOLDS on a dead command (shipped v0.65,
    the one that LATCHES at 0 — rec14); v067 = signed but floors a dead command at Max(cur, PROBE) so a
    latched-low estimate self-probes back up while a healthy one is left untouched."""
    old = new = fix = 1.0
    prev = None
    o, n, f = [], [], []
    for t, cmd, ach in samples:
        if prev is None:
            prev = t
            continue
        dt = t - prev
        prev = t
        if dt <= 0 or dt > 0.5:                      # skip pauses / bad stamps
            continue
        measured = abs(cmd) > GATE                   # gate on MAGNITUDE
        inst_old = min(1.0, max(0.0, abs(ach) / abs(cmd))) if measured else old   # v0.63 hold
        inst_new = min(1.0, max(0.0, ach / cmd)) if measured else new             # v0.64 hold
        inst_fix = min(1.0, max(0.0, ach / cmd)) if measured else max(fix, PROBE)  # v0.67 self-probe floor
        old = step(old, inst_old, dt)
        new = step(new, inst_new, dt)
        fix = step(fix, inst_fix, dt)
        o.append(old)
        n.append(new)
        f.append(fix)
    return o, n, f


def load(path):
    rows = []
    for line in open(path):
        if line.startswith('#'):
            continue
        r = line.strip().split(',')
        if len(r) < 38:
            continue
        try:
            rows.append((float(r[0]), float(r[36]), float(r[37])))
        except ValueError:
            pass
    return rows


def law_of(path):
    for line in open(path):
        if line.startswith('# config') and 'law=' in line:
            return line.split('law=')[1].split()[0]
    return '?'


def main(paths):
    # v0.67: the live estimator is v064 (shipped, LATCHES); v067 is the latch fix. Show both so a
    # reversal capture (v064 already collapsed) can be confirmed NOT re-armed by v067, and a latch
    # capture (v064 pinned at 0) is confirmed lifted off 0 by v067's self-probe.
    print(f"{'file':<18}{'law':<16}{'v0.64 (shipped)':>22}{'v0.67 (fixed)':>22}   note")
    print('-' * 100)
    for p in paths:
        o, n, f = replay(load(p))
        if not n:
            continue
        nm, fm = sorted(n)[len(n) // 2], sorted(f)[len(f) // 2]
        nlo, flo = min(n), min(f)
        # LATCH = v064 spends a real fraction of frames PINNED near 0; the fix is the self-probe pinning
        # FEWER (open-loop on a latched trace it can only lift the floor, not show the closed-loop recovery).
        pin_n = sum(1 for x in n if x < 0.05) / len(n)
        pin_f = sum(1 for x in f if x < 0.05) / len(f)
        anti = sum(1 for x in n if x < 0.5) / len(n)
        if pin_n > 0.15 and pin_n - pin_f > 0.05:
            note = f"** LATCH lifted: v064 pinned {pin_n*100:.0f}% of frames <0.05 -> v067 {pin_f*100:.0f}%"
        elif nm < 0.5:
            note = f"reversal/mush: {anti*100:.0f}% <0.5, pinned v064 {pin_n*100:.0f}% v067 {pin_f*100:.0f}% (not re-armed)"
        else:
            note = "-- unchanged"
        print(f"{os.path.basename(p)[-18:-4]:<18}{law_of(p):<16}"
              f"med {nm:.2f} min {nlo:.2f}{'':>6}med {fm:.2f} min {flo:.2f}{'':>6}{note}")


def selftest():
    dt = 0.0625
    # A plant tracking its command exactly: all three versions must sit at 1.0.
    good = [(i * dt, 0.5, 0.5) for i in range(200)]
    o, n, f = replay(good)
    assert abs(o[-1] - 1.0) < 1e-6 and abs(n[-1] - 1.0) < 1e-6 and abs(f[-1] - 1.0) < 1e-6, (o[-1], n[-1], f[-1])

    # A REVERSED plant, equal magnitude: v0.63 is fooled to 1.0, v0.64 AND v0.67 collapse to 0 (cmd is
    # ABOVE the gate here, so v0.67's dead-command floor never engages — a real reversal still collapses).
    rev = [(i * dt, 0.5, -0.5) for i in range(200)]
    o, n, f = replay(rev)
    assert abs(o[-1] - 1.0) < 1e-6, f"v0.63 should be blind to reversal, got {o[-1]}"
    assert n[-1] < 0.01, f"v0.64 should collapse on reversal, got {n[-1]}"
    assert f[-1] < 0.01, f"v0.67 must STILL collapse a real (cmd>gate) reversal, got {f[-1]}"

    # THE LATCH (rec14 shape): estimate driven to 0 by a reversed pull, THEN the command dies (|cmd|<gate).
    # v0.64 HOLDS at 0 forever (the freeze); v0.67 floors it back up toward PROBE (self-probe recovery).
    latch = ([(i * dt, 0.5, -0.5) for i in range(40)]                        # drive to 0 (cmd>gate)
             + [(40 * dt + i * dt, 0.0, 0.0) for i in range(120)])           # command dies -> gated out
    _, n, f = replay(latch)
    assert n[-1] < 0.02, f"v0.64 should LATCH at 0 on a dead command, got {n[-1]}"
    assert f[-1] > 0.10, f"v0.67 should self-probe off 0 toward PROBE, got {f[-1]}"
    assert f[-1] <= PROBE + 1e-6, f"v0.67 must not overshoot PROBE, got {f[-1]}"

    # A HEALTHY jet with a brief neutral stick must NOT be dragged down by the floor (Max keeps it high).
    healthy = [(i * dt, 0.5, 0.5) for i in range(40)] + [(40 * dt + i * dt, 0.0, 0.0) for i in range(20)]
    _, _, f = replay(healthy)
    assert f[-1] > 0.95, f"v0.67 floor must not pull a healthy estimate down, got {f[-1]}"

    # Attack is faster than release: a drop then a recovery is asymmetric.
    drop = [(i * dt, 0.5, -0.5) for i in range(20)] + [(20 * dt + i * dt, 0.5, 0.5) for i in range(20)]
    _, n, _ = replay(drop)
    assert n[19] < 0.25, f"attack too slow: {n[19]}"
    assert n[-1] < 0.9, f"release should be slow, got {n[-1]}"
    print("selftest OK")


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    if sys.argv[1] == '--selftest':
        selftest()
    else:
        main(sys.argv[1:])
