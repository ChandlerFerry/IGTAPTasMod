import glob
import json
import os
import random
import re
import shutil
import string
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gamepaths

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DRY = "--dry-run" in sys.argv
CONFIG, TRACES = str(gamepaths.CONFIG), str(gamepaths.TRACES)
LETTERS = string.ascii_lowercase + string.digits


def new_id(taken):
    while True:
        i = random.choice(string.ascii_lowercase) + "".join(random.choice(LETTERS) for _ in range(5))
        if i not in taken:
            taken.add(i)
            return i


def carry(old_ids, old_labels, labels):
    n, m = len(old_labels), len(labels)
    ids = [None] * m
    if n:
        lcs = [[0] * (m + 1) for _ in range(n + 1)]
        for i in range(n - 1, -1, -1):
            for j in range(m - 1, -1, -1):
                lcs[i][j] = lcs[i + 1][j + 1] + 1 if old_labels[i] == labels[j] else max(lcs[i + 1][j], lcs[i][j + 1])
        i = j = 0
        while i < n and j < m:
            if old_labels[i] == labels[j]:
                ids[j] = old_ids[i]
                i += 1
                j += 1
            elif lcs[i + 1][j] >= lcs[i][j + 1]:
                i += 1
            else:
                j += 1
    taken = set(old_ids)
    return [x if x is not None else new_id(taken) for x in ids]


def write(path, text, binary=False):
    print(("would write " if DRY else "wrote ") + path)
    if not DRY:
        tmp = path + ".migrate.tmp"
        with open(tmp, "wb" if binary else "w", **({} if binary else {"encoding": "utf-8", "newline": ""})) as f:
            f.write(text)
        os.replace(tmp, path)


def remove(path, why):
    print(("would delete " if DRY else "deleted ") + path + " (" + why + ")")
    if not DRY:
        os.remove(path)


def dump(obj, like):
    indented = "\n  " in like[:4096]
    return json.dumps(obj, indent=2 if indented else None, separators=None if indented else (",", ":"), ensure_ascii=False) + ("\n" if like.endswith("\n") else "")


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    return json.loads(text), text


lists = {}


def ref(key, labels):
    ids, old = lists.get(key, ([], []))
    return carry(ids, old, labels)


run_files = {}
for path in sorted(glob.glob(os.path.join(REPO, "data", "runs", "*.json"))):
    run, text = read(path)
    goals = run["goals"]
    if goals and isinstance(goals[0], dict):
        lists[run["name"]] = ([g["id"] for g in goals], [g["goal"] for g in goals])
        run_files[run["name"]] = (path, run, text, False)
        continue
    taken = set()
    run["goals"] = [{"id": new_id(taken), "goal": g} for g in goals]
    lists[run["name"]] = ([g["id"] for g in run["goals"]], goals)
    run_files[run["name"]] = (path, run, text, True)

fp_path = os.path.join(REPO, "enginesim", "Cli", "forced-paths.json")
fp, fp_text = read(fp_path)
kept_rules = []
for rule in fp["paths"]:
    if not rule.get("fulltas"):
        kept_rules.append(rule)
        continue
    for name in rule["fulltas"]:
        if name not in run_files:
            print(f"forced-paths: no runs/{name}.json; its rule ({len(rule.get('avoid') or [])} boxes, {len(rule.get('optional') or [])} blocks) is dropped")
            continue
        path, run, text, _ = run_files[name]
        ids, labels = lists[name]
        run_files[name] = (path, run, text, True)

        def block(boxes, b=None):
            out = {"boxes": boxes}
            if b and b.get("from"):
                out["from"] = b["from"]
            note = (b or rule).get("note")
            if note:
                out["note"] = note
            return out

        if rule.get("avoid"):
            run.setdefault("avoid", []).append(block(rule["avoid"]))
        for b in rule.get("optional") or []:
            boxes = b.get("avoid") or []
            if not boxes:
                continue
            if isinstance(b.get("forGoal"), int):
                g = b["forGoal"]
                if not 0 <= g < len(labels):
                    print(f"forced-paths: {name}'s block {b.get('name')} is for goal {g}, past its {len(labels)} goals: dropped")
                    continue
                run["goals"][g].setdefault("avoid", []).append(block(boxes, b))
            elif b.get("for"):
                at = [i for i, l in enumerate(labels) if l == b["for"]]
                if not at:
                    print(f"forced-paths: {name}'s block {b.get('name')} is for {b['for']}, which the run doesn't have: dropped")
                for i in at:
                    run["goals"][i].setdefault("avoid", []).append(block(boxes, b))
            elif b.get("after"):
                at = [i for i, l in enumerate(labels) if l == b["after"]]
                if not at:
                    print(f"forced-paths: {name}'s block {b.get('name')} is after {b['after']}, which the run doesn't have: dropped")
                    continue
                run["goals"][at[-1]].setdefault("avoidAfter", []).append(block(boxes, b))
            else:
                x = block(boxes, b)
                if b.get("offFor"):
                    x["offFor"] = [ids[i] for i, l in enumerate(labels) if l in b["offFor"]]
                run.setdefault("avoid", []).append(x)

for name, (path, run, text, changed) in run_files.items():
    if changed:
        goals = run.pop("goals")
        avoid = run.pop("avoid", None)
        if avoid:
            run["avoid"] = avoid
        run["goals"] = goals
        write(path, json.dumps(run, indent=2, ensure_ascii=False) + "\n")

if len(kept_rules) != len(fp["paths"]):
    fp["paths"] = kept_rules
    out = json.dumps(fp, indent=2, ensure_ascii=False)
    out = re.sub(r"\[\s*((?:-?[\d.eE+-]+|\"[^\"\[\]{},]*\")(?:\s*,\s*(?:-?[\d.eE+-]+|\"[^\"\[\]{},]*\"))*)\s*\]",
                 lambda m: "[" + ", ".join(s.strip() for s in m.group(1).split(",")) + "]", out)
    out = re.sub(r"\[\s*((?:\[[^\[\]]*\]\s*,\s*)*\[[^\[\]]*\])\s*\]", lambda m: "[" + ", ".join(re.findall(r"\[[^\[\]]*\]", m.group(1))) + "]", out)
    for p in [fp_path, os.path.join(REPO, "enginesim", "Cli", "bin", "Release", "net10.0", "forced-paths.json")]:
        if os.path.exists(p):
            write(p, out + "\n")


def header_goals(path):
    with open(path, "rb") as f:
        head = f.read(16 << 20)
    run = re.search(rb'"fulltas"\s*:\s*\{', head)
    found = None if run is None else re.compile(rb'"goals"\s*:\s*\[').search(head, run.end())
    if found is None:
        return None
    start = found.end() - 1
    depth, text, end = 0, False, start
    while end < len(head):
        c = head[end:end + 1]
        if text:
            if c == b"\\":
                end += 1
            elif c == b'"':
                text = False
        elif c == b'"':
            text = True
        elif c == b"[":
            depth += 1
        elif c == b"]":
            depth -= 1
            if depth == 0:
                break
        end += 1
    return head, start, end, json.loads(head[start:end + 1])


for path in sorted(glob.glob(os.path.join(TRACES, "fulltas-*.json"))):
    base = os.path.basename(path)
    if "-seed-" in base or base.endswith(".world.json") or base.endswith(".goals.json") or base.endswith("-record.json") or base.endswith("-classes.json"):
        continue
    found = header_goals(path)
    if found is None:
        continue
    head, start, end, goals = found
    if not goals or not isinstance(goals[0], dict) or "label" not in goals[0]:
        continue
    key = base[len("fulltas-"):-len(".json")]
    labels = [g["label"] for g in goals]
    if all("id" in g for g in goals):
        lists.setdefault(key, ([g["id"] for g in goals], labels))
        continue
    ids = ref(key, labels)
    lists.setdefault(key, (ids, labels))
    for g, i in zip(goals, ids):
        g["id"] = i
    goals = [dict([("id", g["id"])] + [(k, v) for k, v in g.items() if k != "id"]) for g in goals]
    print(("would splice " if DRY else "spliced ") + path + f" ({len(goals)} goals)")
    if not DRY:
        tmp = path + ".migrate.tmp"
        with open(tmp, "wb") as o, open(path, "rb") as f:
            o.write(head[:start])
            o.write(json.dumps(goals, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))
            f.seek(end + 1)
            shutil.copyfileobj(f, o, 1 << 24)
        os.replace(tmp, path)


def goal_list(key, labels):
    return [{"id": i, "goal": l} for i, l in zip(ref(key, labels), labels)]


def saved_route(key, obj):
    labels = obj["goals"]
    ids = ref(key, labels)
    n = len(labels)

    def col(name):
        v = obj.pop(name, None)
        return v if isinstance(v, list) else [-1] * n

    inputs, physics, held = col("goalInputTicks"), col("goalPhysicsTicks"), col("goalHeldFrames")
    idles = {i.get("goal"): i for i in (obj.pop("idles", None) or []) if isinstance(i, dict)}
    goals = []
    for k, (i, l) in enumerate(zip(ids, labels)):
        g = {"id": i, "label": l, "input": inputs[k] if k < len(inputs) else -1, "physics": physics[k] if k < len(physics) else -1,
             "held": held[k] if k < len(held) else -1}
        idle = idles.get(k + 1)
        if idle and idle.get("arrive") and idle.get("afford"):
            g["idle"] = {"arrive": idle["arrive"], "afford": idle["afford"]}
        goals.append(g)
    out = {}
    for k, v in obj.items():
        out[k] = goals if k == "goals" else v
    if isinstance(out.get("goalsFrom"), list):
        out["goalsFrom"] = [goal_list(key, l) for l in out["goalsFrom"]]
    return out


def strs(v):
    return isinstance(v, list) and all(isinstance(x, str) for x in v)


def id_of(ids):
    return lambda k: ids[k - 1] if isinstance(k, int) and 1 <= k <= len(ids) else ""


stretch_files = {}
offline = os.path.join(TRACES, "fulltas")
for path in sorted(glob.glob(os.path.join(offline, "*.json")) + glob.glob(os.path.join(offline, "candidates", "*", "*.json"))):
    base = os.path.basename(path)
    key = os.path.basename(os.path.dirname(path)) if os.sep + "candidates" + os.sep in path else base.split(".")[0]
    if base.endswith(".report.json") or base.endswith(".auto-speeds.json") or base.endswith(".moves.json") or base.endswith(".buytable.json") or base.endswith(".stretches.json"):
        continue
    try:
        obj, text = read(path)
    except (ValueError, UnicodeDecodeError):
        continue
    if not isinstance(obj, dict):
        continue
    if ".stretch-" in base:
        stretch_files.setdefault(key, []).append((path, obj))
        continue
    labels = lists.get(key, ([], []))[1]
    if base.endswith(".auto-memory.json"):
        if not strs(obj.get("goals")):
            continue
        if obj["goals"] != labels:
            remove(path, "memory of another goal list: the engine drops it")
            continue
        ids = lists[key][0]
        k = id_of(ids)
        obj["goals"] = ids
        obj["studied"] = [[k(s[0]), s[1]] for s in obj.get("studied", [])]
        obj["wallStudied"] = [[k(s[0]), s[1]] for s in obj.get("wallStudied", [])]
        obj["planDead"] = [k(x) for x in obj.get("planDead", [])]
        obj["polished"] = [k(x) for x in obj.get("polished", [])]
        obj["leads"] = [[k(x[0]), k(x[1]), x[2]] for x in obj.get("leads", [])]
        for name in ("grafted", "arms", "spots"):
            for x in obj.get(name, []):
                x["goal"] = k(x.get("goal"))
        write(path, dump(obj, text))
    elif base.endswith(".auto-refs.json"):
        if not strs(obj.get("goals")):
            continue
        if obj["goals"] != labels:
            remove(path, "references of another goal list: the engine drops them")
            continue
        obj["goals"] = lists[key][0]
        write(path, dump(obj, text))
    elif base.endswith(".closest.json"):
        if strs(obj.get("goals")):
            obj["goals"] = goal_list(key, obj["goals"])
            write(path, dump(obj, text))
    elif strs(obj.get("goals")) and ("goalInputTicks" in obj or "inputs" in obj):
        write(path, dump(saved_route(key, obj), text))
    elif strs(obj.get("goals")) and isinstance(obj.get("goalsFrom"), list):
        obj["goals"] = goal_list(key, obj["goals"])
        obj["goalsFrom"] = [goal_list(key, l) for l in obj["goalsFrom"]]
        write(path, dump(obj, text))

for key, files in stretch_files.items():
    ids, labels = lists.get(key, ([], []))
    target = os.path.join(offline, key + ".stretches.json")
    merged = read(target)[0] if os.path.exists(target) else {}
    for path, obj in files:
        g = obj.get("goal")
        if isinstance(g, int) and g < len(labels) and labels[g] == obj.get("label"):
            entry = {k: v for k, v in obj.items() if k not in ("run", "goal")}
            merged[ids[g]] = entry
        else:
            print(f"stretch best {os.path.basename(path)}: goal {g} ({obj.get('label')}) isn't {key}'s goal there any more: dropped")
    write(target, json.dumps(merged, separators=(",", ":"), ensure_ascii=False))
    for path, _ in files:
        remove(path, "now in " + os.path.basename(target))

for path in sorted(glob.glob(os.path.join(TRACES, "offline-best", "fulltas-*.json"))):
    try:
        obj, text = read(path)
    except (ValueError, UnicodeDecodeError):
        continue
    if not isinstance(obj, dict) or not isinstance(obj.get("fulltas"), str):
        continue
    key = obj["fulltas"]
    changed = False
    if strs(obj.get("goals")):
        obj["goals"] = goal_list(key, obj["goals"])
        changed = True
    if isinstance(obj.get("goalsFrom"), list) and obj["goalsFrom"] and strs(obj["goalsFrom"][0]):
        obj["goalsFrom"] = [goal_list(key, l) for l in obj["goalsFrom"]]
        changed = True
    if changed:
        write(path, dump(obj, text))

archive = os.path.join(TRACES, "archive")
for path in sorted(glob.glob(os.path.join(archive, "fulltas-*.visits.json"))):
    key = os.path.basename(path)[len("fulltas-"):-len(".visits.json")]
    obj, text = read(path)
    if not strs(obj.get("goals")):
        continue
    if key in lists and obj["goals"] == lists[key][1]:
        obj["goals"] = lists[key][0]
        write(path, dump(obj, text))
    else:
        remove(path, "visits of another goal list")
numbered = re.compile(r"^(\d+-\d+-[0-9a-f]{16}(-noreset)?|piece-\d+-[0-9a-f]{16})\.json$")
for d in sorted(glob.glob(os.path.join(archive, "fulltas-*"))):
    if os.path.isdir(d):
        for f in os.listdir(d):
            if numbered.match(f):
                remove(os.path.join(d, f), "a window cache named by goal numbers")
for path in sorted(glob.glob(os.path.join(TRACES, "map-fulltas-*.json")) + glob.glob(os.path.join(TRACES, "map-fulltas-*.key"))):
    remove(path, "a map drawn with goal numbers")

splits_path = os.path.join(CONFIG, "fulltas-splits.json")
if os.path.exists(splits_path):
    splits, text = read(splits_path)
    routes = {m.get("id"): m.get("name", "") for m in read(os.path.join(CONFIG, "routes.json"))[0].get("macros", [])}
    changed = False
    for route, rows in splits.items():
        if not rows or "id" in rows[0]:
            continue
        m = re.match(r"^fulltas/(.+?)_NONE", routes.get(route, ""))
        key = m.group(1) if m else None
        if key not in lists:
            continue
        for row, i in zip(rows, ref(key, [r["label"] for r in rows])):
            row["id"] = i
        changed = True
    if changed:
        write(splits_path, dump(splits, text))

bench_path = os.path.join(REPO, "enginesim", "Cli", "searchbench-grafts.json")
bench, text = read(bench_path)
changed = False
for case in bench.get("cases", []):
    g = case.get("goals")
    if isinstance(g, list) and len(g) == 2 and all(isinstance(x, int) for x in g):
        key = case["trace"][len("fulltas-"):-len(".json")]
        if key not in lists:
            print(f"searchbench: case {case['name']}'s trace {case['trace']} has no goal ids: left as it is")
            continue
        ids = lists[key][0]
        case["goals"] = [ids[g[0]], ids[g[1] - 1]]
        changed = True
if changed:
    write(bench_path, json.dumps(bench, indent=2, ensure_ascii=False) + "\n")

print(f"{len(lists)} goal lists with ids ({len(run_files)} runs)")
