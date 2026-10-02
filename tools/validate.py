import json, sys

from gamepaths import CONFIG, ROUTE_NAME, call, call_retry, category_id, course_ids

ROUTES = CONFIG / "routes.json"

def check(route, body, label):
    r = call_retry("/api/enginesim/submit", dict(body, routeId=route["id"]), 30, 1, timeout=300)
    ok = r.get("finished")
    saved = "".join(f", saved as {kind}" for kind, key in (("record", "saved"), ("farm", "farmSaved")) if r.get(key))
    loop = r.get("loop")
    if loop:
        # A farm route is a loop (README ### Rules 30): the gate replays its lead and the loop four times.
        result = (f"OK  {loop['lap']:.4f}s a lap, loop of {loop['laps']}: {loop['ticks']} ticks, {loop['held']} held frames{saved}"
                  if ok else "FAIL no steady loop: " + json.dumps(loop))
    else:
        result = f"OK  {r['time']:.2f}s, run {r['runTicks']} ticks{saved}" if ok else "FAIL " + json.dumps(r)
    print(f"{route['name']:34} {label:16} {result}")

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    ids = course_ids()
    known = {c["id"] for c in call("/api/state", timeout=30)["categories"]}
    for m in json.load(open(ROUTES, encoding="utf-8-sig"))["macros"]:
        if m["name"].startswith("fulltas/"):
            print(f"{m['name']:34} {'Full-TAS':16} skipped")
            continue
        match = ROUTE_NAME.match(m["name"])
        # A category the registry doesn't know is skipped.
        caps = category_id(match[2]) if match else None
        if match and match[1] in ids and caps in known:
            check(m, {"course": ids[match[1]], "capabilities": caps}, f"C{ids[match[1]]} {caps}")
