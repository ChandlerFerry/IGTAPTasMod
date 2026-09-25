"""Plans the new-game chain's economy: which Course 1 boxes to buy, in what order, until wall jump (700 W).

    python tools/plan-newgame.py [--keep N] [--write]

Game facts (obj/fulldecomp): Course 1 pays ceil(reward), reward = 1 + base-reward buys. Base reward box costs
1, then ceil(prev * 1.1); clone box 8, then ceil(prev * 1.153); wall jump 700. A clone spawns every lap/N (clonesScript
Update, one tick at 50 fps), walks the best run's path (one point per tick, P points) and pays ceil(0.1 * reward) when
it reaches the end. Every loop goes back through a quick restart, which puts the player at Course 1's quick-restart
point, so each segment kind starts from the same state.

The search goes in time order: from every state it tries a Course 1 run, a trip from the restart to a box, and the next
box from where the player stands; a box not affordable yet is waited for under it (the clones pay meanwhile). Of the
states with the same upgrades, clones and place, only one richer than every earlier one goes on. So a wait survives
only where it beats running the course for the money, and the first state to buy wall jump is the plan (~1.5 min).

Segment times are estimates in physics ticks (COURSE, TO, MOVE, REPEAT); replace them with solved times once the
chain's routes exist. `--keep N` keeps the current `newgame` chain's first N segments (the ones already solved) and plans
from where they end. With --write the plan replaces the `newgame` chain in chains.json (other chains untouched). After
wall jump the run goes on with the `wj` chain's segments (`chain:wj`), buys dash at Course 2's box (30000 W, the clones'
income meanwhile; not planned here) and goes on with the `dash` chain's (`chain:dash`).
"""
import argparse
import heapq
import itertools
import json
import math
import os

CHAINS = os.path.join(os.environ.get("IGTAP_CONFIG", r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod"), "chains.json")

P = 107         # clone path points = best Course 1 run in ticks (newgame/1_NO_WJ 2.14)
COURSE = 132    # restart -> Course 1 finish: 25-tick restart lead-in + 2.14 s (estimate; 1_NO_WJ 1.68 is stale)
TO = {"R": 50, "K": 70, "W": 90}                     # restart -> first bump at a box
MOVE = {("R", "K"): 25, ("K", "W"): 25, ("R", "W"): 45}  # box -> next box to the left, bump included
REPEAT = 20     # another bump at the same box
WJ_COST = 700
TOKEN = {"R": "buyreward", "K": "buyclone", "W": "buywj"}
BOX = {v: k for k, v in TOKEN.items()}
TAIL = ["restart", "chain:wj", "buydash", "restart", "chain:dash"]


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
            self.run(self.next_event())
        self.cash -= cost
        if box == "R":
            self.n += 1
        elif box == "K":
            self.N += 1
            self.due = P / self.N
        return True

    def next_event(self):
        """Ticks until a clone pays or spawns (at least 1)."""
        ticks = math.inf
        if self.clones:
            ticks = self.clones[0] + P - self.t
        if len(self.clones) < self.N:
            ticks = min(ticks, math.ceil(self.due - self.since))
        return max(1, ticks)

    def step(self, where, b):
        """Go from `where` (None: the quick-restart point, else the box the player is under) to box `b` and buy it."""
        self.run(TO[b] if where is None else REPEAT if where == b else MOVE[(where, b)])
        return self.buy(b)


# Where the player is: "gate" (Course 1's start gate, the new game's arrival), "done" (a Course 1 run's finish), a box.
def search(e0, where0, tokens0):
    """States in time order; each tries a Course 1 run, a trip from the restart to a box, and the next box from where
    the player stands. Of the states with the same upgrades, clones and place, only one richer than every earlier one
    goes on. The first to buy wall jump is the earliest."""
    order = itertools.count()
    heap = [(e0.t, next(order), e0, where0, tokens0)]
    richest = {}
    while heap:
        _, _, e, where, tokens = heapq.heappop(heap)
        if where == "W":
            return e, tokens
        key = (e.n, e.N, len(e.clones), where)
        if richest.get(key, -1) >= e.cash:
            continue
        richest[key] = e.cash
        restart = [] if where == "gate" else ["restart"]
        c = e.copy()
        c.course()
        heapq.heappush(heap, (c.t, next(order), c, "done", tokens + restart + ["1"]))
        for b in "RKW":
            x = e.copy()
            if where != "gate" and x.step(None, b):
                heapq.heappush(heap, (x.t, next(order), x, b, tokens + restart + [TOKEN[b]]))
            if where == b or (where, b) in MOVE:
                x = e.copy()
                if x.step(where, b):
                    heapq.heappush(heap, (x.t, next(order), x, b, tokens + [TOKEN[b]]))
    raise SystemExit("wall jump is never affordable")


def replay(tokens):
    """The model's state after `tokens` (a chain's entries from "newgame" on) and where the player is."""
    e, where = Eco(), "gate"
    restarted = False
    for token in tokens:
        if token in ("newgame", "newgameto1"):
            continue
        if token == "restart":
            restarted = True
            continue
        if token == "1":
            e.course()
            where = "done"
        else:
            for part in token.split("+"):
                b = BOX[part]
                if not e.step(None if restarted or where in ("gate", "done") else where, b):
                    raise SystemExit(f"{token} is not affordable in the model where the chain buys it")
                where, restarted = b, False
        restarted = False
    return e, where


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", type=int, default=1, help="keep the newgame chain's first N segments (1: just newgameto1)")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()
    chains = json.load(open(CHAINS, encoding="utf-8")) if os.path.exists(CHAINS) else []
    current = next((c for c in chains if c.get("name") == "newgame"), {"segments": ["newgame", "newgameto1"]})["segments"]
    kept, count = ["newgame"], 0
    for token in current[1:]:
        if token != "restart":
            if count == max(1, a.keep):
                break
            count += 1
        kept.append(token)
    while kept[-1] == "restart":
        kept.pop()
    e0, where0 = replay(kept)
    e, tokens = search(e0, where0, kept)
    seg = tokens + TAIL
    shown = e0.copy()
    where = where0
    for token in tokens[len(kept):]:
        if token == "restart":
            where = None
            continue
        if token == "1":
            shown.course()
            where = "done"
            what = "course 1"
        else:
            b = BOX[token]
            waited = shown.copy()
            waited.run(TO[b] if where in (None, "gate", "done") else REPEAT if where == b else MOVE[(where, b)])
            before = waited.t
            shown.step(None if where in (None, "gate", "done") else where, b)
            what = f"buy {token}" + (f" (waits {shown.t - before})" if shown.t > before else "")
            where = b
        print(f"  tick {shown.t:5d}  {what:28s} -> {shown.cash:4d} W  reward {1 + shown.n:2d}  clones {shown.N}")
    print(f"wall jump bought at tick {e.t} ({e.t * 0.02:.2f} s after the new game's first Course 1 segment, estimates)")
    if a.write:
        chains = [c for c in chains if c.get("name") != "newgame"]
        chains.append({"name": "newgame", "capabilities": "none", "segments": seg})
        with open(CHAINS, "w", encoding="utf-8") as f:
            json.dump(chains, f, indent=2)
        print(f"wrote {len(seg)} entries to {CHAINS}")
    else:
        print(json.dumps(seg))


if __name__ == "__main__":
    main()
