"""Re-run studio routes in the game (turbo) under their category and print the game's own timer.

Uses the solver's own replay (/api/enginesim/submit with routeId), so a category means exactly what the solver and
the gate use (TasPlugin.CategoryAbilities). A route is never re-saved: submit only saves a strictly faster time.

Usage: python tools/validate.py   (checks every route named "<course>_<NO_WJ|WJ|WJDASH> <time>")
"""
import json, re, sys, urllib.request
ROUTES = r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod\routes.json"
COURSES = ROUTES.replace("routes.json", "courses.json")
LABELS = {"NO_WJ": "none", "WJ": "wj", "WJDASH": "wjdash"}


def check(route, course, caps):
    body = {"course": course, "capabilities": caps, "routeId": route["id"]}
    r = json.load(urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:17426/api/enginesim/submit",
                                                                 json.dumps(body).encode()), timeout=300))
    ok = r.get("finished")
    print(f"{route['name']:34} C{course} {caps:6} {'OK  ' + format(r['time'], '.2f') + 's' if ok else 'FAIL ' + json.dumps(r)}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    # The course of a category route is its courses.json id or name ("bonus1_WJ 13.56").
    ids = {str(c.get("name") or c["id"]): c["id"] for c in json.load(open(COURSES, encoding="utf-8-sig"))}
    for m in json.load(open(ROUTES, encoding="utf-8-sig"))["macros"]:
        match = re.fullmatch(r"([^_\s]+)_(NO_WJ|WJDASH|WJ) [\d.]+", m["name"])
        if match and match[1] in ids: check(m, ids[match[1]], LABELS[match[2]])
