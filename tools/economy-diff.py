"""Compare the economy of two runs of the same inputs field by field, each field as the sequence of values it takes (1x's
physics count runs ahead through dash freezes, so ticks don't line up; enginesim/README.md#probe).

  economy-diff.py <a> <b> [--fields cash,gp,np,cd,rp,atoms,waiting,npTick,npCap,npBoost,cdMult,segments]

Each side is a 1x probe log (probes/<name>.json: its `kind: "tick"` rows), enginesim's `fulltas --check --ticks-out` or
`replay --ticks-out` lines, or a trace (`/api/enginesim/trace`: its ticks, turbo's); every row's economy is its `econ`
object (TasPlugin.EconomyNow, FullTas.EconomyRow). A probe row's `econPre` (taken at its tick's globalStats.FixedUpdate,
before the NP payout) is used when it has one: it is the point the others' rows are taken at, after the last tick's
rendered frames, so two changes in one tick (a merge's NP in a frame, the next tick's payout) group the same way. Both
sides are cut at the last physics tick they share (a 1x probe runs on past its inputs). Exits 1 when a field differs.
"""
import argparse
import json
import sys

FIELDS = "cash,gp,np,cd,rp,atoms,waiting,npTick,npCap,npBoost,cdMult,segments"


def rows(path):
    with open(path, encoding="utf-8") as f:
        head = f.read(1)
        f.seek(0)
        if head != "{" or path.endswith(".jsonl"):
            return [json.loads(line) for line in f if line.strip()]
        try:
            doc = json.load(f)
        except json.JSONDecodeError:
            f.seek(0)
            return [json.loads(line) for line in f if line.strip()]
    if "rows" in doc:
        out = []
        for r in doc["rows"]:
            if r.get("kind") != "tick" or "econPre" in r and not r["econPre"]:
                continue
            # econPre at physics tick P is what tick P - 1's frames left.
            out.append(dict(r, econ=r["econPre"], physics=r["physics"] - 1) if r.get("econPre") else r)
        return out
    return [dict(t, physics=i + 1) for i, t in enumerate(doc.get("ticks", []))]


def common(a, b):
    """Both tables cut at the last physics tick they share."""
    last = min(max((r.get("physics", 0) for r in a), default=0), max((r.get("physics", 0) for r in b), default=0))
    return [r for r in a if r.get("physics", 0) <= last], [r for r in b if r.get("physics", 0) <= last]


def seq(table, field):
    out = []
    for r in table:
        econ = r.get("econ") or {}
        if r.get("kind", "tick") != "tick" or field not in econ:
            continue
        v = econ[field]
        if not out or out[-1] != v:
            out.append(v)
    return out


def close(a, b):
    if isinstance(a, list) or isinstance(b, list):
        return isinstance(a, list) and isinstance(b, list) and len(a) == len(b) and all(close(x, y) for x, y in zip(a, b))
    return a == b or abs(a - b) <= 1e-12 * max(abs(a), abs(b))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--fields", default=FIELDS)
    args = ap.parse_args()
    a, b = common(rows(args.a), rows(args.b))
    bad = 0
    for f in args.fields.split(","):
        p, o = seq(a, f), seq(b, f)
        i = next((k for k, (x, y) in enumerate(zip(p, o)) if not close(x, y)), None)
        if i is None and len(p) == len(o):
            print(f"{f}: {len(p)} values, equal")
            continue
        bad += 1
        i = min(len(p), len(o)) if i is None else i
        print(f"{f}: first difference at value {i} of {len(p)}/{len(o)}: a {p[i] if i < len(p) else 'end'}"
              f" b {o[i] if i < len(o) else 'end'} (before: {p[i - 1] if i > 0 else '-'})")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    assert close(1.0, 1.0 + 1e-13) and not close(1.0, 1.0001) and close([1, 2], [1, 2]) and not close([1], [1, 2])
    assert seq([{"econ": {"np": 1}}, {"econ": {"np": 1}}, {"econ": {"np": 2}}], "np") == [1, 2]
    main()
