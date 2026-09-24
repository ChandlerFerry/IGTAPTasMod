"""Re-run studio routes in the game (turbo) under category settings and print the game's own timer.

Usage: python tools/validate.py   (checks every route named "<course>_<NO_WJ|WJ|WJDASH> <time>")
"""
import json, sys, urllib.request
ROUTES = r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod\routes.json"
STARTS = {1: {"x": 2241.0, "y": -292.0}, 2: {"x": 4783.43359, "y": 1878.2251}, 3: {"x": 9505.0, "y": 6967.01465}}
AB = {"none": {"wallJumpUnlocked": False, "dashUnlocked": False}, "wj": {"wallJumpUnlocked": True, "dashUnlocked": False},
      "wjdash": {"wallJumpUnlocked": True, "dashUnlocked": True}}


def check(name, course, abilities):
    route = next((m for m in json.load(open(ROUTES, encoding="utf-8"))["macros"] if m["name"] == name), None)
    if route is None: return print(f"{name:34} (not in studio)")
    body = {"routeId": route["id"], "start": STARTS[course], "abilities": AB[abilities], "maxTicks": 1500}
    r = json.load(urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:17426/api/turbo/evaluate",
                                                                 json.dumps(body).encode()), timeout=120))
    ok = r["success"] and r["courseStartRunTick"] >= 0
    print(f"{name:34} C{course} {abilities:6} {'OK  ' + format(r['completionTime'], '.2f') + 's' if ok else 'FAIL ' + str(r.get('failureReason'))}")


if __name__ == "__main__":
    import re
    sys.stdout.reconfigure(encoding="utf-8")
    labels = {"NO_WJ": "none", "WJ": "wj", "WJDASH": "wjdash"}
    for m in json.load(open(ROUTES, encoding="utf-8"))["macros"]:
        match = re.fullmatch(r"([123])_(NO_WJ|WJDASH|WJ) [\d.]+", m["name"])
        if match: check(m["name"], int(match[1]), labels[match[2]])
