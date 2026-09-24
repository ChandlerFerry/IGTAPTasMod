"""Closed-loop route solver: offline optimizer proposes, the real game (turbo) decides.

Usage: python tools/solve.py "<route name>" [--rounds 5] [--top 200] [--max 2000000]

Each round runs `IGTAP.FastSim suite` from the current best route, then evaluates its candidates
fastest-first with /api/turbo/evaluate (the real engine, ~0.4 s per run). The first candidate that
finishes faster in the game is imported into the studio and becomes the next round's seed.
Needs the game running on the route's course (POST /api/oracle/bootstrap puts it on Course 2).
"""
import argparse, json, subprocess, sys, time, urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CONFIG = Path(r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod")
MODEL = CONFIG / "simulation" / "live-simulation-model.json"
ROUTES = CONFIG / "routes.json"
SIM = REPO / "simulator" / "bin" / "Release" / "net8.0" / "IGTAP.FastSim.exe"
API = "http://127.0.0.1:17426"
LOG = REPO / "planning" / "solve-log.jsonl"


def post(path, body):
    request = urllib.request.Request(API + path, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def turbo(body):
    r = post("/api/turbo/evaluate", dict(body, maxTicks=1500, start={"x": 4783.43359, "y": 1878.2251},
                                          abilities={"wallJumpUnlocked": True, "dashUnlocked": False}))
    course = r["completionTicks"] - r["courseStartRunTick"] if r.get("success") and r["courseStartRunTick"] >= 0 else None
    return r, course


def route_by_name(name):
    return next(m for m in json.loads(ROUTES.read_text(encoding="utf-8"))["macros"] if m["name"] == name)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("route")
    ap.add_argument("--rounds", type=int, default=5)
    ap.add_argument("--top", type=int, default=200)
    ap.add_argument("--max", type=int, default=2_000_000)
    args = ap.parse_args()
    name = args.route
    for round_no in range(1, args.rounds + 1):
        seed = route_by_name(name)
        result, best = turbo({"routeId": seed["id"]})
        if best is None:
            sys.exit(f"seed '{name}' does not finish in the game: {result.get('failureReason')}")
        print(f"round {round_no}: seed '{name}' = {best} course ticks ({best * .02:.2f}s) in game")
        out = REPO / "offline-solve" / f"round{round_no}"
        subprocess.run([str(SIM), "suite", str(MODEL), str(ROUTES), name, "--out", str(out), "--max", str(args.max),
                        "--top", str(args.top), "--result", result["resultPath"]], check=True, stdout=subprocess.DEVNULL)
        candidates = [c["candidate"] for c in json.loads((out / "summary.json").read_text(encoding="utf-8"))["candidates"]]
        candidates = sorted((c for c in candidates if c["finished"] and c["courseTicks"] < best), key=lambda c: c["courseTicks"])
        print(f"  offline proposes {len(candidates)} faster candidates ({candidates[0]['courseTicks'] if candidates else '-'} best)")
        winner = None
        started = time.time()
        for i, c in enumerate(candidates):
            spec = {k: c[k] for k in ("durations", "rawMoveX", "noJump", "hopStep", "hopTick", "hopHold")}
            r, ticks = turbo(dict(spec, routeId=seed["id"]))
            with open(LOG, "a", encoding="utf-8") as log:
                log.write(json.dumps({"seed": name, "predicted": c["courseTicks"], "game": ticks,
                                      "death": r.get("deathTick"), "shape": c["shapeChange"], "result": r.get("resultPath"),
                                      "spec": spec}) + "\n")
            if ticks is not None and ticks < best:
                winner = (spec, ticks, c)
                break
        print(f"  tested {i + 1 if candidates else 0} in game in {time.time() - started:.0f}s")
        if winner is None:
            print("  no faster candidate survived the game; stopping")
            return
        spec, ticks, c = winner
        new_name = f"2_WJ {ticks * .02:.2f}"
        post("/api/simulation/import-candidate", dict(spec, routeId=seed["id"], name=new_name))
        print(f"  NEW BEST {ticks} course ticks ({ticks * .02:.2f}s), predicted {c['courseTicks']}: {c['shapeChange']} -> '{new_name}'")
        name = new_name


if __name__ == "__main__":
    main()
