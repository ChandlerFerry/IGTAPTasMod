import json, re, sys, time, urllib.error, urllib.request

from gamepaths import CONFIG

ROUTES = CONFIG / "routes.json"
COURSES = CONFIG / "courses.json"
LABELS = {"NONE": "none", "WJ": "wj", "WJ_DS": "wjds", "WJ_DS_DJ": "wjdsdj", "WJ_DS_DJ_BS": "wjdsdjbs"}

def submit(body):
    request = urllib.request.Request("http://127.0.0.1:17426/api/enginesim/submit", json.dumps(body).encode())
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        return json.loads(error.read())

def check(route, body, label):
    body = dict(body, routeId=route["id"])
    for _ in range(30):
        r = submit(body)
        if "loading" not in str(r.get("error", "")):
            break
        time.sleep(1)
    ok = r.get("finished")
    saved = "".join(f", saved as {kind}" for kind, key in (("record", "saved"), ("farm", "farmSaved")) if r.get(key))
    result = f"OK  {r['time']:.2f}s, run {r['runTicks']} ticks{saved}" if ok else "FAIL " + json.dumps(r)
    print(f"{route['name']:34} {label:16} {result}")

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    ids = {str(c.get("name") or c["id"]): c["id"] for c in json.load(open(COURSES, encoding="utf-8-sig"))}
    for m in json.load(open(ROUTES, encoding="utf-8-sig"))["macros"]:
        if m["name"].startswith("fulltas/"):
            print(f"{m['name']:34} {'Full-TAS':16} skipped")
            continue
        match = re.fullmatch(r"([^_\s]+)_(NONE|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ) (?:farm )?[\d.]+", m["name"])
        if match and match[1] in ids:
            check(m, {"course": ids[match[1]], "capabilities": LABELS[match[2]]}, f"C{ids[match[1]]} {LABELS[match[2]]}")
