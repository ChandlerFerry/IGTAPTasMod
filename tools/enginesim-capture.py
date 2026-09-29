"""Capture ground truth for the offline engine (enginesim/) from the running game.

Each capture is a turbo run from the solver's start state for a course/category, written by the plugin to
BepInEx/config/IGTAPTasMod/enginesim/<name>.json (full state after every tick) and <name>.world.json (the world as
that run saw it).

  enginesim-capture.py routes             every "<course>_<CAT> <time>" and "<course>_<CAT> farm <time>" route in the studio library
  enginesim-capture.py random [N]         N random-input runs per course/category (fidelity corpus)
  enginesim-capture.py start C CAT        just the start state (empty inputs) for course C, category CAT
  enginesim-capture.py route NAME C CAT   any studio route by name (e.g. a partial one), as a `solve --seed` trace

C is a course id or name from courses.json ("4", "bonus1"); CAT is a category id or label (a label's id is the label
lowercased without underscores, e.g. WJ_DS2_BS_ZIP_OM -> wjds2bszipom); the registry is the plugin's GET /api/state
"categories". A route whose category it doesn't know is skipped.
"""
import json
import random
import re
import sys
import time
import urllib.request

from gamepaths import CONFIG

API = "http://127.0.0.1:17426"
ROUTE_NAME = re.compile(r"^([^_\s]+)_([A-Z][A-Z0-9_]*) (farm )?([\d.]+)$")
COURSES = CONFIG / "courses.json"
COURSE_IDS = {str(c.get("name") or c["id"]): c["id"] for c in json.load(open(COURSES, encoding="utf-8-sig"))}
COURSE_IDS.update({str(c): c for c in COURSE_IDS.values()})
CATEGORIES = [(1, "wjdsdj"), (1, "wjds"), (1, "wj"), (1, "none"), (2, "wjdsdj"), (2, "wjds"), (2, "wj"),
              (3, "wjdsdj"), (3, "wjds"), (3, "wj"), (4, "wjdsdj"), (4, "wjds"), (5, "wjdsdj"), (COURSE_IDS["bonus1"], "wj"),
              (11, "wjdsdjbs"), (12, "wjdsdjbs"), (13, "wjdsdjbs"), (14, "wjdsdjbs"), (15, "wjdsdjbs")]

def category_id(label):
    return label.replace("_", "").lower()

def call(path, body=None, method="POST"):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(API + path, data=data, method=method)
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        return json.loads(error.read())

def trace(body):
    body = dict(body, world=True, freshStart=True)
    for _ in range(20):
        result = call("/api/enginesim/trace", body)
        if "loading" not in str(result.get("error", "")):
            return result
        time.sleep(3)
    return result

def random_inputs(rng, n, dash):
    ticks, x = [], 1.0
    while len(ticks) < n:
        if rng.random() < .35:
            x = rng.choice([-1.0, 0.0, 1.0, 0.5, -0.3])
        length, start = rng.randint(3, 20), len(ticks)
        ticks += [dict(x=x, held=False, press=False, release=False, dash=False, dashJump=0) for _ in range(length)]
        if rng.random() < .45:
            hold = rng.choice([1, 3, 6, 10, 16])
            ticks[start].update(press=True, held=True)
            for i in range(start + 1, min(len(ticks), start + hold)):
                ticks[i]["held"] = True
            if start + hold < len(ticks):
                ticks[start + hold]["release"] = True
        if dash and rng.random() < .3:
            d = ticks[start + rng.randint(0, min(length, 4) - 1)]
            d["dash"] = True
            if rng.random() < .4:
                d["dashJump"] = rng.randint(1, 10)
    return ticks

def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "routes"
    if mode == "routes":
        state = call("/api/state", method="GET")
        known = {c["id"] for c in state["categories"]}
        for route in state["routes"]:
            m = ROUTE_NAME.match(route["name"])
            if not m or m.group(1) not in COURSE_IDS:
                continue
            caps = category_id(m.group(2))
            if caps not in known:
                continue
            course = COURSE_IDS[m.group(1)]
            name = "route-c" + m.group(1) + "-" + caps + ("-farm" if m.group(3) else "") + "-" + m.group(4)
            r = trace({"course": course, "capabilities": caps, "routeId": route["id"], "name": name})
            print(name, {k: v for k, v in r.items() if k != "path"})
    elif mode == "random":
        count = int(sys.argv[2]) if len(sys.argv) > 2 else 3
        for course, caps in CATEGORIES:
            for k in range(count):
                rng = random.Random(course * 1000 + k * 7 + len(caps))
                name = f"rand-c{course}-{caps}-{k}"
                # "ds" is the dashes token; no other one holds it.
                r = trace({"course": course, "capabilities": caps, "ticks": random_inputs(rng, 300, "ds" in caps), "name": name})
                print(name, {k2: v for k2, v in r.items() if k2 != "path"})
    elif mode == "start":
        course, caps = COURSE_IDS[sys.argv[2]], category_id(sys.argv[3])
        r = trace({"course": course, "capabilities": caps, "ticks": [], "name": f"start-c{sys.argv[2]}-{caps}"})
        print({k: v for k, v in r.items()})
    elif mode == "route":
        name, course, caps = sys.argv[2], COURSE_IDS[sys.argv[3]], category_id(sys.argv[4])
        route = next((r for r in call("/api/state", method="GET")["routes"] if r["name"] == name), None)
        if route is None:
            sys.exit(f"no studio route named {name!r}")
        slug = re.sub(r"[^A-Za-z0-9.]+", "-", name).strip("-")
        r = trace({"course": course, "capabilities": caps, "routeId": route["id"], "name": f"seed-c{sys.argv[3]}-{caps}-{slug}"})
        print({k: v for k, v in r.items() if k != "path"})
    else:
        sys.exit(__doc__)

if __name__ == "__main__":
    main()
