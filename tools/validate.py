"""Re-run studio routes in the game (turbo) under their category and print the game's own timer.

Uses the solver's own replay (/api/enginesim/submit with routeId), so a category means exactly what the solver and
the gate use (TasPlugin.CategoryAbilities). A route is never re-saved: submit only saves a strictly faster time.

Usage: python tools/validate.py   (checks every route named "<course>_<NO_WJ|WJ|WJDASH|WJ_DASH_AJ> <time>"; Full-TAS
routes "fulltas/<run>_NO_WJ <time>" are skipped: the Full-TAS gate judges them from a new game)
"""
import json, re, sys, time, urllib.error, urllib.request
ROUTES = r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod\routes.json"
COURSES = ROUTES.replace("routes.json", "courses.json")
LABELS = {"NO_WJ": "none", "WJ": "wj", "WJDASH": "wjdash", "WJ_DASH_AJ": "wjdashaj"}


def submit(body):
    request = urllib.request.Request("http://127.0.0.1:17426/api/enginesim/submit", json.dumps(body).encode())
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        return json.loads(error.read())


def check(route, body, label):
    body = dict(body, routeId=route["id"])
    # The plugin parks the player at a new course's start and answers "loading" until its zone has settled.
    for _ in range(30):
        r = submit(body)
        if "loading" not in str(r.get("error", "")):
            break
        time.sleep(1)
    ok = r.get("finished")
    print(f"{route['name']:34} {label:16} {'OK  ' + format(r['time'], '.2f') + 's' if ok else 'FAIL ' + json.dumps(r)}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    # The course of a category route is its courses.json id or name ("bonus1_WJ 13.56").
    ids = {str(c.get("name") or c["id"]): c["id"] for c in json.load(open(COURSES, encoding="utf-8-sig"))}
    for m in json.load(open(ROUTES, encoding="utf-8-sig"))["macros"]:
        if m["name"].startswith("fulltas/"):
            print(f"{m['name']:34} {'Full-TAS':16} skipped")
            continue
        match = re.fullmatch(r"([^_\s]+)_(NO_WJ|WJDASH|WJ_DASH_AJ|WJ) [\d.]+", m["name"])
        if match and match[1] in ids:
            check(m, {"course": ids[match[1]], "capabilities": LABELS[match[2]]}, f"C{ids[match[1]]} {LABELS[match[2]]}")
