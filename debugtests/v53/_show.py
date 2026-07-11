import csv, sys

def load(path):
    with open(path) as f:
        lines = f.readlines()
    hdr_idx = next(i for i,l in enumerate(lines) if l.startswith('t,'))
    header = lines[hdr_idx].strip().split(',')
    rows = []
    for l in lines[hdr_idx+1:]:
        l = l.strip()
        if not l or l.startswith('#'):
            continue
        parts = l.split(',')
        if len(parts) != len(header):
            continue
        rows.append(dict(zip(header, parts)))
    return header, rows

def show(path, t0, t1, cols):
    header, rows = load(path)
    print(','.join(cols))
    for r in rows:
        t = float(r['t'])
        if t0 <= t <= t1:
            print(','.join(r[c] for c in cols))

if __name__ == '__main__':
    path = sys.argv[1]
    t0 = float(sys.argv[2])
    t1 = float(sys.argv[3])
    cols = sys.argv[4].split(',') if len(sys.argv) > 4 else ['t','off','azErr','elevErr','bank','targetBank','outP','outR','outY','yawWeak','bankBlend','bankTR','phase']
    show(path, t0, t1, cols)
