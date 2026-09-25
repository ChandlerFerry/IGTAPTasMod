"""Plans the new-game chain's economy: which Course 1 boxes to buy, in what order, until wall jump (700 W).

    python tools/plan-newgame.py [--loops 10] [--write]

Game facts (obj/fulldecomp): Course 1 pays ceil(reward), reward = 1 + base-reward buys. Base reward box costs
1, then ceil(prev * 1.1); clone box 8, then ceil(prev * 1.153); wall jump 700. A clone spawns every lap/N (clonesScript
Update, one tick at 50 fps), walks the best run's path (one point per tick, P points) and pays ceil(0.1 * reward) when
it reaches the end. Every loop goes back through a quick restart, which puts the player at Course 1's quick-restart
point, so each segment kind starts from the same state.

Segment times are estimates in physics ticks (COURSE, TO, MOVE, REPEAT); replace them with solved times once the
chain's routes exist. With --write the plan replaces the `newgame` chain in chains.json (other chains untouched).
"""
import argparse
import json
import math
import os

CHAINS = os.path.join(os.environ.get("IGTAP_CONFIG", r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod"), "chains.json")

P = 84          # clone path points = best Course 1 run in ticks (1.68 s)
COURSE = 109    # restart -> Course 1 finish (1_NO_WJ 1.68)
TO = {"R": 50, "K": 70, "W": 90}                     # restart -> first bump at a box
MOVE = {("R", "K"): 25, ("K", "W"): 25, ("R", "W"): 45}  # box -> next box to the left, bump included
REPEAT = 20     # another bump at the same box
WJ_COST = 700
TOKEN = {"R": "buyreward", "K": "buyclone", "W": "buywj"}


def reward_cost(k):
    c = 1
    for _ in range(k - 1):
        c = math.ceil(c * 1.1)
    return c


def clone_cost(k):
    c = 8
    for _ in range(k - 1):
        c = math.ceil(c * 1.153)
    return c


class Eco:
    __slots__ = ("t", "cash", "n", "N", "since", "due", "clones", "path")

    def __init__(self):
        self.t, self.cash, self.n, self.N = 0, 0, 0, 0
        self.since, self.due, self.clones, self.path = 1e9, 0.0, [], False

    def copy(self):
        e = Eco()
        e.t, e.cash, e.n, e.N, e.since, e.due, e.path = self.t, self.cash, self.n, self.N, self.since, self.due, self.path
        e.clones = list(self.clones)
        return e

    def run(self, ticks):
        """`ticks` Updates at 50 fps; clones spawn and pay (clonesScript.Update)."""
        end = self.t + ticks
        while self.t < end:
            if not self.path or self.N == 0:
                self.since += end - self.t
                self.t = end
                return
            # next tick anything happens: a clone due to pay, or a spawn
            nxt = end
            if self.clones:
                nxt = min(nxt, self.clones[0] + P)
            if len(self.clones) < self.N:
                nxt = min(nxt, self.t + max(1, math.ceil(self.due - self.since)))
            step = max(1, nxt - self.t)
            self.t += step
            self.since += step
            if self.since >= self.due and len(self.clones) < self.N:
                self.clones.append(self.t)
                self.since = 0
            while self.clones and self.clones[0] + P <= self.t:
                self.clones.pop(0)
                self.cash += math.ceil(0.1 * (1 + self.n))

    def course(self):
        self.run(COURSE)
        self.cash += 1 + self.n
        self.path = True

    def buy(self, box):
        cost = reward_cost(self.n + 1) if box == "R" else clone_cost(self.N + 1) if box == "K" else WJ_COST
        if self.cash < cost and (self.N == 0 or not self.path):
            return False  # no income while standing: the buy never happens
        while self.cash < cost:  # stand under the box until it's affordable (a bump before resets its cooldown)
            self.run(1)
        self.cash -= cost
        if box == "R":
            self.n += 1
        elif box == "K":
            self.N += 1
            self.due = P / self.N
        return True

    def trip(self, boxes):
        prev = None
        for b in boxes:
            self.run(TO[b] if prev is None else REPEAT if prev == b else MOVE[(prev, b)])
            if not self.buy(b):
                return False
            prev = b
        return True


def finish(e):
    f = e.copy()
    return f.t if f.trip(["W"]) else float("inf")


def plan(loops, beam_width, depth):
    e = Eco()
    for _ in range(loops):
        e.course()
        e.trip(["R"])
    trips = [["R"] * r + ["K"] * k for r in range(4) for k in range(7) if r + k]
    best = (finish(e), [])
    beam = [(e, [])]
    for _ in range(depth):
        cand = []
        for s, hist in beam:
            c = s.copy()
            c.course()
            t = finish(c)
            if t < best[0]:
                best = (t, hist + [["1"]])
            for base, pre in ((s, []), (c, [["1"]])):
                for tr in trips:
                    x = base.copy()
                    if x.trip(tr) and x.t < best[0]:
                        cand.append((x, hist + pre + [tr]))
        if not cand:
            break

        def score(item):
            x = item[0]
            rate = x.N * math.ceil(0.1 * (1 + x.n)) / P + (1 + x.n) / (COURSE + TO["R"])
            return x.t + max(0, WJ_COST - x.cash) / max(rate, 1e-6)

        cand.sort(key=score)
        seen, beam = set(), []
        for item in cand:
            x = item[0]
            key = (x.n, x.N, x.cash // 4, x.t // 8)
            if key not in seen:
                seen.add(key)
                beam.append(item)
                if len(beam) >= beam_width:
                    break
    return e, best


def segments(loops, steps):
    # Every segment kind starts from the quick restart (Course 1's quick-restart point), except the first course.
    seg = ["newgame", "newgameto1", "1", "restart", "buyreward", "restart"]
    for _ in range(loops - 1):
        seg += ["1", "restart", "buyreward", "restart"]
    for step in steps:
        if seg[-1] != "restart":
            seg += ["restart"]
        seg += ["1"] if step == ["1"] else [TOKEN[b] for b in step]
    if seg[-1] != "restart":
        seg += ["restart"]
    return seg + ["buywj", "restart", "1", "1to2", "2", "2tobonus1", "bonus1", "restart", "bonus1to2"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--loops", type=int, default=10)
    ap.add_argument("--beam", type=int, default=200)
    ap.add_argument("--depth", type=int, default=40)
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()
    start, (t, steps) = plan(a.loops, a.beam, a.depth)
    print(f"after {a.loops} loops: tick {start.t}, {start.cash} W, reward {1 + start.n}")
    e = start.copy()
    for step in steps:
        if step == ["1"]:
            e.course()
            print(f"  tick {e.t:5d}  course 1          -> {e.cash:4d} W  reward {1 + e.n:2d}  clones {e.N}")
        else:
            e.trip(step)
            print(f"  tick {e.t:5d}  buy {''.join(step):12s}  -> {e.cash:4d} W  reward {1 + e.n:2d}  clones {e.N}")
    print(f"wall jump bought at tick {t} ({t * 0.02:.2f} s after the new game's first Course 1 segment, estimates)")
    seg = segments(a.loops, steps)
    if a.write:
        chains = json.load(open(CHAINS, encoding="utf-8")) if os.path.exists(CHAINS) else []
        chains = [c for c in chains if c.get("name") != "newgame"]
        chains.append({"name": "newgame", "capabilities": "none", "segments": seg})
        with open(CHAINS, "w", encoding="utf-8") as f:
            json.dump(chains, f, indent=2)
        print(f"wrote {len(seg)} entries to {CHAINS}")
    else:
        print(json.dumps(seg))


if __name__ == "__main__":
    main()
