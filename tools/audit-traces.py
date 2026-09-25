import json
import sys
from pathlib import Path

from gamepaths import TRACES
JUMP_WINDOW, DASH_WINDOW, CUT_WINDOW = 12, 8, 15


def press_near(inputs, tick, window, jump):
    for i in range(max(0, tick - window - 16), min(len(inputs), tick + 3)):
        k = inputs[i]
        if jump:
            press = i if k["press"] else i + k["dashJump"] if k["dash"] and k["dashJump"] > 0 else -1
        else:
            press = i if k["dash"] else -1
        if press >= 0 and press - 2 <= tick <= press + window:
            return True
    return False


def release_near(inputs, tick, window):
    held = False
    for i in range(min(len(inputs), tick + 3)):
        k, p = inputs[i], inputs[i - 1] if i else None
        if p and not (k["press"] or k["release"] or k["dash"]) and k["x"] == p["x"] and k["held"] == p["held"]:
            continue
        release = k["release"] or held and not k["held"]
        held = k["held"] or k["dash"] and k["dashJump"] > 0
        if release and i - 2 <= tick <= i + window:
            return True
    return False


def violations(trace):
    found = []
    for i, t in enumerate(trace["ticks"]):
        m = {e[1]: e[2] for e in t["movement"]}
        if m["jumpBuffer"] and not press_near(trace["inputs"], i, JUMP_WINDOW, True):
            found.append(f"tick {i}: stale jump buffer")
        if m["dashBuffer"] and not press_near(trace["inputs"], i, DASH_WINDOW, False):
            found.append(f"tick {i}: stale dash buffer")
        if m["jumpCutBuffer"] and not release_near(trace["inputs"], i, CUT_WINDOW):
            found.append(f"tick {i}: stale jump cut")
    return found


def main():
    paths = [Path(p) for p in sys.argv[1:]] or sorted(p for p in TRACES.glob("*.json") if not p.name.endswith(".world.json"))
    bad = 0
    for path in paths:
        trace = json.loads(path.read_text(encoding="utf-8"))
        if "ticks" not in trace or "inputs" not in trace:
            continue
        found = violations(trace)
        bad += bool(found)
        print(f"{path.name:36} {'VIOLATES ' + '; '.join(found[:3]) if found else 'ok'}")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
