"""Local search on a route's per-tick inputs, judged only by the real game (turbo).

Usage: python tools/turbo-search.py "<route name>" --course 1|2|3 --abilities none|wj|wjdash
                                    [--budget 3000] [--patience 800] [--seed 1]

A route is a list of ticks (stick x, jump held, and press/release/dash events). Random moves: cut a
span, dash then cut shortly after (a dash only pays off with the run after it retimed), super jump late
in a dash (dashJumpFrame), hop, drop a press, re-aim a run of the stick. Improvements are kept; the search stops after `patience` misses.
A finish only counts if the game timed it. The best route is imported into the studio if it improved.
Every evaluation is appended to planning/turbo-search-log.jsonl.
"""
import argparse, json, random, sys, urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ROUTES = Path(r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod\routes.json")
API = "http://127.0.0.1:17426"
LOG = REPO / "planning" / "turbo-search-log.jsonl"
# Quick-restart points (the game places the player 12 px below).
STARTS = {1: {"x": 2241.0, "y": -292.0}, 2: {"x": 4783.43359, "y": 1878.2251}, 3: {"x": 9505.0, "y": 6967.01465}}
LABELS = {"none": "NO_WJ", "wj": "WJ", "wjdash": "WJDASH"}
ABILITIES = {
    "none": {"wallJumpUnlocked": False, "dashUnlocked": False},
    "wj": {"wallJumpUnlocked": True, "dashUnlocked": False},
    "wjdash": {"wallJumpUnlocked": True, "dashUnlocked": True},
}


def post(path, body):
    request = urllib.request.Request(API + path, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def to_ticks(steps):
    ticks = []
    for s in steps:
        for t in range(s["durationTicks"]):
            ticks.append({"x": s["rawMoveX"], "held": bool(s.get("rawJumpHeld")),
                          "press": t == 0 and bool(s.get("rawJumpPressed")),
                          "release": t == 0 and bool(s.get("rawJumpReleased")),
                          "dash": t == 0 and bool(s.get("rawDashPressed")),
                          "dashJump": s.get("dashJumpFrame", 0) if t == 0 and s.get("rawDashPressed") else 0})
    return ticks


def to_steps(ticks):
    steps = []
    for k in ticks:
        event = k["press"] or k["release"] or k["dash"]
        last = steps[-1] if steps else None
        if last and not event and last["rawMoveX"] == k["x"] and last["rawJumpHeld"] == k["held"]:
            last["durationTicks"] += 1
            last["durationMs"] += 20
            continue
        steps.append({"type": "RawInput", "enabled": True, "endCondition": "FixedTicks", "durationTicks": 1,
                      "durationMs": 20, "direction": (k["x"] > 0) - (k["x"] < 0), "rawMoveX": k["x"],
                      "rawJumpHeld": k["held"], "rawJumpPressed": k["press"], "rawJumpReleased": k["release"],
                      "rawDashPressed": k["dash"], "dashJumpFrame": k.get("dashJump", 0) if k["dash"] else 0})
    return steps


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("route")
    ap.add_argument("--course", type=int, required=True)
    ap.add_argument("--abilities", choices=ABILITIES, required=True)
    ap.add_argument("--budget", type=int, default=3000)
    ap.add_argument("--patience", type=int, default=800)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    seed = next(m for m in json.loads(ROUTES.read_text(encoding="utf-8"))["macros"] if m["name"] == args.route)
    context = {"start": STARTS[args.course], "abilities": ABILITIES[args.abilities], "maxTicks": 1500}
    dash = ABILITIES[args.abilities]["dashUnlocked"]
    cache = {}

    def evaluate(ticks):
        steps = to_steps(ticks)
        key = json.dumps(steps)
        if key not in cache:
            r = post("/api/turbo/evaluate", dict(context, steps=steps))
            ok = r.get("success") and r["courseStartRunTick"] >= 0
            # The game's own course timer (courseScript.currentPathTime), in 0.02 s ticks.
            cache[key] = round(r["completionTime"] / .02) if ok else None
            with open(LOG, "a", encoding="utf-8") as log:
                log.write(json.dumps({"route": args.route, "course": args.course, "abilities": args.abilities,
                                      "ticks": cache[key], "death": r.get("deathTick"), "steps": steps}) + "\n")
        return cache[key]

    def mutate(ticks):
        t = [dict(k) for k in ticks]
        n = len(t)
        p = rng.randrange(n)
        kind = rng.choice(["cut", "cut", "hop", "nojump", "stick", "insert"]
                          + (["dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump"] if dash else []))
        if kind == "insert":  # e.g. back up for a run-up: k ticks of a held stick direction
            x = rng.choice((-1.0, 0.0, 1.0))
            t[p:p] = [dict(t[p], x=x, press=False, release=False, dash=False) for _ in range(rng.choice((1, 2, 4, 8, 15)))]
        elif kind == "cut":
            del t[p:p + rng.choice((1, 1, 2, 3, 5, 8))]
        elif kind == "dash":
            t[p]["dash"] = True
            q = p + rng.randrange(1, 25)
            del t[q:q + rng.randrange(0, 20)]
        elif kind == "dashjump":
            t[p]["dash"] = True
            j = min(n - 1, p + rng.randrange(0, 7))
            hold = rng.choice((2, 5, 10, 16))
            t[j].update(press=True, held=True, release=False)
            for k in t[j + 1:j + hold]: k["held"] = True
            if j + hold < n: t[j + hold].update(held=False, release=True)
            q = j + rng.randrange(1, 25)
            del t[q:q + rng.randrange(0, 20)]
        elif kind == "latejump":  # super jump late in a ground dash (dashJumpFrame = dash frame of the press)
            t[p].update(dash=True, press=False, dashJump=rng.randrange(1, 11))
            for k in t[p + 1:p + 1 + rng.choice((0, 2, 5, 10))]: k["held"] = True
            q = p + rng.randrange(1, 25)
            del t[q:q + rng.randrange(0, 20)]
        elif kind == "hop":
            hold = rng.choice((1, 2, 4, 7, 11, 16))
            t[p].update(press=True, held=True, release=False)
            for k in t[p + 1:p + hold]: k["held"] = True
            if p + hold < n: t[p + hold].update(held=False, release=True)
            q = p + rng.randrange(1, 30)
            del t[q:q + rng.randrange(0, 10)]
        elif kind == "nojump":
            presses = [i for i, k in enumerate(t) if k["press"] or k["dash"]]
            if not presses: return None
            i = rng.choice(presses)
            t[i].update(press=False, dash=False, dashJump=0)
        elif kind == "stick":
            x = rng.choice((-1.0, 0.0, 1.0, round(t[p]["x"] + rng.choice((-.1, .1)), 2)))
            for k in t[p:p + rng.randrange(1, 15)]: k["x"] = max(-1.0, min(1.0, x))
        return (kind, p), t

    best = to_ticks(seed["steps"])
    best_ticks = start_ticks = evaluate(best)
    if best_ticks is None:
        sys.exit("seed route does not finish in the game with these settings")
    print(f"seed {args.route}: {best_ticks} course ticks ({best_ticks * .02:.2f}s)")
    evaluations = misses = 0
    while evaluations < args.budget and misses < args.patience:
        m = mutate(best)
        if m is None: continue
        move, trial = m
        evaluations += 1
        ticks = evaluate(trial)
        if ticks is not None and ticks < best_ticks:
            best, best_ticks, misses = trial, ticks, 0
            print(f"  {best_ticks} ({best_ticks * .02:.2f}s) after {evaluations} evals: {move}")
        else:
            misses += 1
    if best_ticks < start_ticks:
        name = f"{args.course}_{LABELS[args.abilities]} {best_ticks * .02:.2f}"
        post("/api/simulation/import-plan", {"name": name, "actions": to_steps(best)})
        print(f"best {best_ticks} course ticks ({best_ticks * .02:.2f}s) after {evaluations} evals -> '{name}'")
    else:
        print(f"no improvement after {evaluations} evals")


if __name__ == "__main__":
    main()
