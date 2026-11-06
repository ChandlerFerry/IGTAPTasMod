"""Usage: tree-check.py [engine.exe] | tree-check.py --door"""
import json, random, subprocess, sys
from pathlib import Path

from gamepaths import TRACES, call_retry, course_ids

IDS = course_ids()
CAPS = "wjdsdjbs"
GROWN = {0: 1, 13: 17, 22: 28, 27: 28}


def capture(token, name, ticks=()):
    r = call_retry("/api/enginesim/trace", {"course": IDS[token], "capabilities": CAPS, "ticks": list(ticks), "name": name,
                                            "world": True, "freshStart": True}, 20, 3)
    assert r.get("ok", True) is not False, r
    return TRACES / (name + ".json")


def tree_state(trace):
    objs = json.load(open(trace.with_suffix(".world.json"), encoding="utf-8"))["objects"]
    by_id = {o["id"]: o for o in objs}
    owner = {c["id"]: o for o in objs for c in o.get("components", [])}

    def path(o):
        names = []
        while o:
            names.append(o["name"])
            o = by_id.get(o.get("parent"))
        return "/".join(reversed(names))

    parts = sum(1 for o in objs if o["activeSelf"] and path(o).startswith("zone 2/TreeArea/The Tree/TreePart") and path(o).count("/") == 3)
    door = open_path = None
    for o in objs:
        for c in o.get("components", []):
            f = {k: v for _, k, v in c.get("fields", [])}
            if c["type"] == "upgradeBox" and o["name"] == "Area 2 door open" and f.get("doorToOpen"):
                d = owner.get(f["doorToOpen"]["$ref"]) or by_id.get(f["doorToOpen"]["$ref"])
                door = (round(d["localPosition"]["x"], 1), round(d["localPosition"]["y"], 1))
            if c["type"] == "Zone2to1TransitionController":
                open_path = by_id[f["openObject"]["$ref"]]["activeSelf"]
    return parts, door, open_path


def replay(exe, traces):
    out = subprocess.run([exe, "replay", *map(str, traces)], capture_output=True, text=True)
    lines = [l for l in out.stdout.splitlines() if l.endswith("]") and ": " in l]
    for l in lines:
        print(" ", l[:220])
    return {l.split(":")[0]: "first difference" not in l for l in lines}


def door(name, route):
    ticks = TRACES.parent / "probes" / "treecheck-idle.json"
    ticks.parent.mkdir(exist_ok=True)
    json.dump([dict(x=0, y=0, held=False, press=False, release=False, dash=False, dashJump=0)] * 15, open(ticks, "w"))
    subprocess.run([sys.executable, "probe.py", name, route, str(ticks)], capture_output=True, text=True, cwd=Path(__file__).parent)
    log = json.load(open(TRACES.parent / "probes" / (name + ".json"), encoding="utf-8"))
    return [r["econ"]["doorArea2"] for r in log["rows"] if r["kind"] == "tick" and r.get("econ")]


def check_door():
    opened, shut = door("treecheck-door13", "tree13to10_WJ_DS_DJ_BS"), door("treecheck-door3", "tree3to7_WJ_DS_DJ_BS")
    print("level 13", opened[:4], "level 3", shut[:4])
    assert len(set(opened)) == 1 and len(set(shut)) == 1 and opened[0] != shut[0], "the door must hold, open at 13 and shut at 3"
    print("tree-check --door: OK")


def main():
    if "--door" in sys.argv:
        return check_door()
    exe = sys.argv[1] if len(sys.argv) > 1 else r"enginesim\Cli\bin\Release\net10.0\IGTAP.EngineSim.exe"
    before = tree_state(capture("6", "treecheck-c6-before"))
    print("save", before)
    seen = {}
    # Only the kept tree targets exist (RULES.md 21).
    for level, token in ((27, "tree27to10"), (22, "tree22to10"), (13, "tree13to10"), (0, "tree0to6")):
        seen[level] = tree_state(capture(token, f"treecheck-tree{level}"))
        print(level, seen[level])
        assert seen[level][0] == GROWN[level], (level, seen[level][0], GROWN[level])
        assert seen[level][2] == (level >= 23), (level, "path", seen[level][2])
    assert seen[0][1] != seen[13][1] and seen[13][1] == seen[27][1], "the Area 2 door: shut at 0, open from 8"
    after = tree_state(capture("6", "treecheck-c6-after"))
    assert after[0] == before[0] and after[2] == before[2], ("the save's tree must be back", before, after)
    rng = random.Random(7)
    runs = []
    for token in ("6", "tree27to10", "tree13to10", "tree0to6"):
        ticks = []
        while len(ticks) < 300:
            x = rng.choice([-1.0, 0.0, 1.0, 0.5])
            hold = rng.random() < 0.45
            n = rng.randint(3, 20)
            ticks += [dict(x=x, held=hold and i > 0, press=hold and i == 0, release=False, dash=rng.random() < 0.02, dashJump=0) for i in range(n)]
            if hold:
                ticks.append(dict(x=x, held=False, press=False, release=True, dash=False, dashJump=0))
        runs.append(capture(token, f"treecheck-rand-{token}", ticks))
    ok = replay(exe, runs)
    base = ok.get("treecheck-rand-6.json", False)
    bad = [k for k, v in ok.items() if not v and k != "treecheck-rand-6.json"]
    assert not bad or not base, ("offline differs from turbo on tree starts only", bad)
    print("tree-check: OK" + ("" if base else " (offline differs on the Course 6 baseline too: not the tree's)"))


if __name__ == "__main__":
    main()
