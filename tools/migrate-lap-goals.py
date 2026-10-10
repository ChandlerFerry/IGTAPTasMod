"""One-time migration of Full-TAS goal lists to lap goals: each `start:N` right before `finish:N` becomes `record:N` (one goal,
reached at the finish), each other `finish:N` becomes `farm:N`; a lone `start:N` stays. Everything indexed by goal is moved to
the new numbering: the run files (runs/), the traces' header goals (spliced, the rest byte for byte), the offline bests and their
copies (goals, goal ticks, idles, goalsFrom), the --auto references, the stretch bests (renamed to their new number), the
studio's splits, the piece library's finish pieces (`farm:N`), the buy tables' from/until and forced-paths.json's goal-numbered
avoid blocks. Close the game and stop every solve first.

    python tools/migrate-lap-goals.py [--dry-run]
"""
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gamepaths  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DRY = "--dry-run" in sys.argv
START, FINISH = re.compile(r"^start:(\d+)$"), re.compile(r"^finish:(\d+)$")


def collapse(labels):
    """The new labels and, per old goal, its new index (None: a lap's gate, now part of its record: goal)."""
    new, where, i = [], [], 0
    while i < len(labels):
        s = START.match(labels[i])
        if s and i + 1 < len(labels) and labels[i + 1] == "finish:" + s.group(1):
            where += [None, len(new)]
            new.append("record:" + s.group(1))
            i += 2
            continue
        f = FINISH.match(labels[i])
        where.append(len(new))
        new.append("farm:" + f.group(1) if f else labels[i])
        i += 1
    return new, where


def old_style(labels):
    return any(START.match(l) or FINISH.match(l) for l in labels)


def kept(values, where):
    return [v for v, w in zip(values, where) if w is not None]


def write(path, text):
    print(("would write " if DRY else "wrote ") + os.path.relpath(path))
    if not DRY:
        tmp = path + ".migrate.tmp"
        with open(tmp, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        os.replace(tmp, path)


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def dump(obj, like):
    """JSON as the file had it: indented when it was."""
    return json.dumps(obj, indent=2 if "\n  " in like[:4096] else None, separators=None if "\n  " in like[:4096] else (",", ":"),
                      ensure_ascii=False)


maps = {}  # run name -> (old labels, new labels, where)


def runs():
    for path in sorted(glob.glob(os.path.join(REPO, "runs", "*.json"))):
        text = open(path, encoding="utf-8").read()
        run = json.loads(text)
        if not old_style(run["goals"]):
            continue
        new, where = collapse(run["goals"])
        maps[run["name"]] = (run["goals"], new, where)
        run["goals"] = new
        write(path, dump(run, text) + ("\n" if text.endswith("\n") else ""))


def splice_goals(path):
    """A trace's header goals collapsed, spliced in as EconomyPlan.SpliceHeaderGoals does (the rest byte for byte)."""
    with open(path, "rb") as f:
        data = f.read()
    run = data.find(b'"fulltas":{')
    found = -1 if run < 0 else data.find(b'"goals":[', run)
    if found < 0:
        return
    start = found + len(b'"goals":')
    depth, text, end = 0, False, start
    while end < len(data):
        c = data[end:end + 1]
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
    goals = json.loads(data[start:end + 1])
    labels = [g["label"] for g in goals]
    if not old_style(labels):
        return
    new, where = collapse(labels)
    out = []
    for g, w in zip(goals, where):
        if w is None:
            continue
        g = dict(g)
        g["label"] = new[w]
        if g.get("kind") in ("start", "finish"):
            g["kind"] = new[w].split(":")[0]
        out.append(g)
    print(("would splice " if DRY else "spliced ") + os.path.relpath(path) + f" ({len(goals)} -> {len(out)} goals)")
    if not DRY:
        tmp = path + ".migrate.tmp"
        with open(tmp, "wb") as f:
            f.write(data[:start] + json.dumps(out, separators=(",", ":"), ensure_ascii=False).encode("utf-8") + data[end + 1:])
        os.replace(tmp, path)


def best(path, obj):
    """An offline best or a copy: goals, the per-goal ticks, idles, goalsFrom."""
    labels = obj["goals"]
    new, where = collapse(labels)
    obj["goals"] = new
    for key in ("goalInputTicks", "goalPhysicsTicks", "goalHeldFrames"):
        if isinstance(obj.get(key), list):
            obj[key] = kept(obj[key], where)
    for idle in obj.get("idles") or []:
        g = idle.get("goal")
        if isinstance(g, int) and 1 <= g <= len(where) and where[g - 1] is not None:
            idle["goal"] = where[g - 1] + 1
    if isinstance(obj.get("goalsFrom"), list):
        obj["goalsFrom"] = [collapse(l)[0] for l in obj["goalsFrom"]]
    return obj


def refs(obj):
    new, where = collapse(obj["goals"])
    obj["goals"] = new
    for r in obj.get("refs") or []:
        if isinstance(r.get("goalPhysicsTicks"), list):
            r["goalPhysicsTicks"] = kept(r["goalPhysicsTicks"], where)
        if isinstance(r.get("progress"), int):
            r["progress"] = len([w for w in where[:r["progress"]] if w is not None])
    return obj


def engine_dir():
    traces = str(gamepaths.TRACES)
    for path in sorted(glob.glob(os.path.join(traces, "fulltas-*.json"))):
        name = os.path.basename(path)
        if "-seed-" in name or name.endswith(".world.json") or name.endswith(".goals.json"):
            continue
        splice_goals(path)
    stretches = []
    for path in sorted(glob.glob(os.path.join(traces, "fulltas", "*.json"))):
        name = os.path.basename(path)
        if name.endswith(".auto-memory.json") or name.endswith(".report.json") or name.endswith(".auto-speeds.json"):
            continue  # memory and reports: dropped by the engine on a goal-list change, rewritten by the next solve
        text = open(path, encoding="utf-8").read()
        try:
            obj = json.loads(text)
        except ValueError:
            continue
        if not isinstance(obj, dict):
            continue
        if ".stretch-" in name:
            stretches.append((path, obj, text))
            continue
        if name.endswith(".auto-refs.json") and isinstance(obj.get("goals"), list) and old_style(obj["goals"]):
            write(path, dump(refs(obj), text))
        elif name.endswith(".buytable.json"):
            run = obj.get("run")
            if run in maps:
                old, new, where = maps[run]
                changed = False
                for key in ("from", "until"):
                    label = obj.get(key)
                    if label and (START.match(label) or FINISH.match(label)) and label in old:
                        w = where[old.index(label)]
                        obj[key] = new[w] if w is not None else label
                        changed = True
                if changed:
                    write(path, dump(obj, text))
        elif isinstance(obj.get("goals"), list) and all(isinstance(g, str) for g in obj["goals"]) and old_style(obj["goals"]):
            write(path, dump(best(path, obj), text))
    for path, obj, text in stretches:
        run = obj.get("run")
        if run not in maps or not isinstance(obj.get("goal"), int):
            continue
        old, new, where = maps[run]
        w = where[obj["goal"]] if obj["goal"] < len(where) else None
        if w is None:
            print("dropped " + os.path.relpath(path) + " (a lap's gate stretch)")
            if not DRY:
                os.remove(path)
            continue
        obj["goal"], obj["label"] = w, new[w]
        target = os.path.join(os.path.dirname(path), f"{run}.stretch-{w + 1}.json")
        write(target, dump(obj, text))
        if target != path and not DRY:
            os.remove(path)
    pieces = os.path.join(traces, "archive", "fulltas-pieces.json")
    if os.path.exists(pieces):
        text = open(pieces, encoding="utf-8").read()
        obj = json.loads(text)
        n = 0
        for p in obj.get("pieces", []):
            f = FINISH.match(p.get("target", ""))
            if f:
                p["target"] = "farm:" + f.group(1)
                n += 1
        if n:
            print(f"pieces: {n} finish pieces are farm: pieces")
            write(pieces, dump(obj, text))


def splits():
    path = os.path.join(str(gamepaths.CONFIG), "fulltas-splits.json")
    if not os.path.exists(path):
        return
    text = open(path, encoding="utf-8").read()
    obj = json.loads(text)
    changed = False
    for route, rows in obj.items():
        labels = [r["label"] for r in rows]
        if not old_style(labels):
            continue
        new, where = collapse(labels)
        obj[route] = [dict(r, label=new[w]) for r, w in zip(rows, where) if w is not None]
        changed = True
    if changed:
        write(path, dump(obj, text))


def forced_paths():
    """Each goal-numbered or goal-labelled avoid block of a migrated run, edited in the file's own text (its layout kept)."""
    path = os.path.join(REPO, "enginesim", "Cli", "forced-paths.json")
    text = open(path, encoding="utf-8").read()
    edits = []
    for p in json.loads(text).get("paths", []):
        for block in p.get("optional", []):
            for run in p.get("fulltas") or []:
                if run not in maps:
                    continue
                old, new, where = maps[run]
                g = block.get("forGoal")
                if isinstance(g, int):
                    w = where[g] if g < len(where) else None
                    if w is None:
                        print(f"forced-paths: {run}'s block for goal {g} ({old[g]}) heads for a lap's gate; it now cuts for the lap, goal {where[g + 1]}")
                        w = where[g + 1]
                    if w != g:
                        edits += [(f'"forGoal": {g}', f'"forGoal": {w}')]
                        edits += [(json.dumps(block[k]), json.dumps(block[k].replace(f"goal {g}", f"goal {w}"))) for k in ("name", "note") if isinstance(block.get(k), str)]
                for key in ("for", "after"):
                    label = block.get(key)
                    if isinstance(label, str) and label not in new:
                        hits = {new[where[i]] for i, l in enumerate(old) if l == label and where[i] is not None}
                        if len(hits) != 1:
                            print(f"forced-paths: {run}'s block {key} {label} has no single new goal: fix it by hand")
                            continue
                        hit = hits.pop()
                        edits += [(f'"{key}": "{label}"', f'"{key}": "{hit}"')]
                        edits += [(json.dumps(block[k]), json.dumps(block[k].replace(label, hit))) for k in ("name", "note") if isinstance(block.get(k), str)]
    # Every edit matched in the file as it was (a block renumbered onto another's old number isn't edited twice).
    for a, _ in edits:
        if text.count(a) != 1:
            sys.exit(f"forced-paths: {text.count(a)} matches for {a}: fix it by hand")
    for i, (a, _) in enumerate(edits):
        text = text.replace(a, f"\0{i}\0")
    for i, (_, b) in enumerate(edits):
        text = text.replace(f"\0{i}\0", b)
    if edits:
        write(path, text)


runs()
engine_dir()
splits()
forced_paths()
for run, (old, new, _) in sorted(maps.items()):
    print(f"{run}: {len(old)} -> {len(new)} goals")
