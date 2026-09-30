"""Check the TAS editor in the running game (TasPlugin.Editor.cs, docs/superpowers/specs/2026-09-29-manual-tas-design.md).

  tas-check.py <route name or id> [--ticks 40,120]

Opens a TAS session on a raw-input route and, for each input tick T:
  - seeks to T (a greenzone snapshot, turbo to one tick short, the last at 1x) and reads the held state;
  - goes back to the start and plays to T at 1x only, and reads it again: the two must match exactly;
  - rewinds one physics tick and advances one: the state must come back.
Then checks the TAS text and a share code carry the session's inputs unchanged, and closes the session (the imported
copy is deleted again).
"""
import argparse
import sys
import time

from gamepaths import call

KEYS = ("x", "y", "vx", "vy", "momentumX", "momentumY", "onGround", "onWall", "dashActive", "dead", "cutscene")


def live():
    return call("/api/state/live")


def held(cursor=None, timeout=120):
    """The session's state once it holds (at `cursor` when given)."""
    end = time.time() + timeout
    while time.time() < end:
        tas = live().get("tas")
        if tas and tas["phase"] == "held" and (cursor is None or tas["cursor"] == cursor or tas.get("ended")):
            return tas
        time.sleep(0.1)
    sys.exit(f"the session didn't hold{'' if cursor is None else f' at input {cursor}'} within {timeout}s: {live().get('tas')}")


def ok(result, what):
    if not result.get("ok"):
        sys.exit(f"{what}: {result.get('error')}")
    return result


def state(tas):
    return {k: tas["player"][k] for k in KEYS} | {"physics": tas["physics"], "cursor": tas["cursor"]}


def diff(a, b):
    return {k: (a[k], b[k]) for k in a if a[k] != b[k]}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("route")
    parser.add_argument("--ticks", default="40,120")
    args = parser.parse_args()
    routes = call("/api/state/static")["routes"]
    route = next((r for r in routes if args.route in (r["id"], r["name"])), None) or sys.exit(f"no route {args.route}")
    ok(call("/api/tas/open", {"routeId": route["id"]}), "open")
    tas = held()
    print(f"session {tas['name']}: {tas['tickCount']} input ticks, held at input {tas['cursor']}")
    failures = 0
    for t in (int(v) for v in args.ticks.split(",")):
        t = min(t, tas["tickCount"])
        ok(call("/api/tas/seek", {"tick": t}), "seek")
        seeked = state(held(t))
        ok(call("/api/tas/seek", {"tick": 0}), "seek to the start")
        now = held()
        while now["cursor"] < t and not now.get("ended"):
            ok(call("/api/tas/advance", {"ticks": t - now["cursor"]}), "advance")
            time.sleep(0.05)
            now = held()
        played = state(now)
        d = diff(seeked, played)
        print(f"input {t}: seek {'==' if not d else '!='} 1x play" + (f"  {d}" if d else ""))
        failures += bool(d)
        ok(call("/api/tas/rewind", {"ticks": 1}), "rewind")
        held()
        ok(call("/api/tas/advance", {"ticks": 1}), "advance")
        time.sleep(0.05)
        back = state(held())
        d = diff(played, back)
        print(f"input {t}: rewind 1 + advance 1 {'==' if not d else '!='}" + (f"  {d}" if d else ""))
        failures += bool(d)

    before = call("/api/tas/ticks")["ticks"]
    text = call("/api/tas/text")["text"]
    ok(call("/api/tas/text", {"text": text}), "apply text")
    after = call("/api/tas/ticks")["ticks"]
    print(f"TAS text round trip: {'same inputs' if before == after else 'INPUTS CHANGED'}")
    failures += before != after

    exported = ok(call("/api/tas/share/export", {"routeId": tas["routeId"]}), "share export")
    ok(call("/api/tas/close", {}), "close")
    imported = ok(call("/api/tas/share/import", {"code": exported["code"]}), "share import")
    again = ok(call("/api/tas/share/export", {"routeId": imported["routeId"]}), "export the import")
    same = exported["text"].split("\n", 1)[1] == again["text"].split("\n", 1)[1]
    print(f"share code round trip ({imported['name']}): {'same inputs' if same else 'INPUTS CHANGED'}")
    failures += not same
    ok(call("/api/routes/delete", {"routeId": imported["routeId"]}), "delete the imported copy")
    print("OK" if failures == 0 else f"{failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
