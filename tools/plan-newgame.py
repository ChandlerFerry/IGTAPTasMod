"""Plans the new-game chain's economy: Course 1's boxes up to wall jump (700 W), then Course 2's up to dash (3 MW).

    python tools/plan-newgame.py [--keep N] [--evals N] [--write]

Game facts (obj/fulldecomp): a box costs its base * 10^(course tier) (upgradeBox.Start), then after each buy
ceil(cost * scale) (ScaleBoxCost; a global box: round((cost + add) * scale)). A course pays its reward at its finish,
reward = ceil(baseReward * (1 + 0.4 * global cashPerLoop buys) * (1 + its cashPerLoop buys) * 10^rewardTier)
(courseScript.UpdateReward). A clone spawns every lap/N (clonesScript Update, one tick at 50 fps), walks the best run's
path (one point per tick, P points) and pays ceil(reward * (0.1 + 0.1 * clone multiplier buys)) when it reaches the
end; clones of a course the player is away from pay the same per second (SpawnClone's offscreen branch).

Phase 1, Course 1 (tier 0, baseReward 1): base reward box 1, then ceil(prev * 1.1); clone box 8, then
ceil(prev * 1.153); wall jump 700. Every loop goes back through a quick restart, which puts the player at Course 1's
quick-restart point, so each segment kind starts from the same state. The search goes in time order: from every state
it tries a Course 1 run, a trip from the restart to a box, and the next box from where the player stands; a box not
affordable yet is waited for under it (the clones pay meanwhile). Of the states with the same upgrades, clones and
place, only one richer than every earlier one goes on. So a wait survives only where it beats running the course for
the money, and the first state to buy wall jump is the plan (~1.5 min).

Then the run plays the `wj` chain (chain:wj: Course 1, 1to2, Course 2, bonus1, back to Course 2's start gate). bonus1
ends next to Course 2's free secret GLOBALcashPerLoop box (+40% on every course's reward) but the chain doesn't buy it:
the segment finishes at the reward rect before the box's trigger stay, and the restart after it leaves (BONUS1_BOX).

Phase 2, Course 2 (tier 2, baseReward 4): its boxes stand in a row left of the quick-restart point (x 4783): reward
(x 4199) 400, then ceil(prev * 1.14), cap 45; clone (4019) 800, then ceil(prev * 1.21), cap 35; clone multiplier (3839)
3800, then ceil((prev + 1000) * 2), cap 10; global reward (3659, +40% on every course) 55000, then 275000, cap 2; dash
(3479) 3000000 (30000 * 10^2). Course 1's clones keep paying from afar. A plan is the order of Course 2's purchases;
each is made as early as the model allows: after k laps (restart, Course 2) and a walk from the restart, or right after
the previous purchase along the row, standing under the box until the clones cover it. The order is searched from a
greedy one (best income gain per W first) by a seeded local search (swap / insert / delete / move one purchase, --evals
tries), keeping the fastest to buy dash. Clone income dominates by the middle: the plan laps Course 2 while the clones
are few, then stays at the row and buys as they pay, and laps again before dash.

Segment times are estimates in physics ticks (COURSE, TO, MOVE, REPEAT; WJ_CHAIN; LAP2, LEAD2, TO2, move2, REPEAT2);
replace them with solved times once the chain's routes exist. `--keep N` keeps the current `newgame` chain's first N
segments (the ones already solved; chain:wj counts its segments) and plans from where they end. With --write the plan
replaces the `newgame` chain in chains.json (other chains untouched). After dash the run goes on with the `dash`
chain's segments (`chain:dash`).
"""
import argparse
import heapq
import itertools
import json
import math
import os
import random

CHAINS = os.path.join(os.environ.get("IGTAP_CONFIG", r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod"), "chains.json")

# ---- Phase 1: Course 1 until wall jump.
P = 107         # clone path points = best Course 1 run in ticks (newgame/1_NO_WJ 2.14)
COURSE = 132    # restart -> Course 1 finish: 25-tick restart lead-in + 2.14 s (estimate; 1_NO_WJ 1.68 is stale)
TO = {"R": 50, "K": 70, "W": 90}                     # restart -> first bump at a box
MOVE = {("R", "K"): 25, ("K", "W"): 25, ("R", "W"): 45}  # box -> next box to the left, bump included
REPEAT = 20     # another bump at the same box
WJ_COST = 700
TOKEN = {"R": "buyreward", "K": "buyclone", "W": "buywj"}
BOX = {v: k for k, v in TOKEN.items()}

# ---- chain:wj, between the phases (the newgame chain's solved segments, physics ticks).
WJ_CHAIN = [("1", 118), ("1to2", 746), ("2", 327), ("2tobonus1", 490), ("bonus1", 406), ("bonus1to2", 418)]
P1_WJ = 93      # Course 1's best run after chain:wj's lap with wall jump (1.86 s): its clones' path
C2_TIER = 2
# Global reward buys bonus1 leaves: 0 (start-chain-newgame-72 captured the global dictionary at 0, Course 2 paying 400);
# 1 once bonus1's route buys the free box.
BONUS1_BOX = 0

# ---- Phase 2: Course 2 until dash.
P2 = 128        # Course 2's clone path = its best run (2.56 s)
LAP2 = 128      # Course 2 start gate -> finish
LEAD2 = 31      # restart -> Course 2's start gate (267 px)
# restart -> first bump at a box ((distance - 50 px) / 9.2 px per tick: Course 1's measured buys fit it)
TO2 = {"R": 58, "C": 78, "M": 97, "G": 117, "D": 136}
ROW = {"D": 0, "G": 1, "M": 2, "C": 3, "R": 4}   # 180 px apart
REPEAT2 = 23    # another bump at the same box (newgame/buyreward#13_NO_WJ 0.46)
DASH_COST = 3000000
CAP2 = {"R": 45, "C": 35, "M": 10, "G": 2, "D": 1}
TOKEN2 = {"R": "buyreward2", "C": "buyclone2", "M": "buyclonemult2", "G": "buyglobalreward2", "D": "buydash"}
BOX2 = {v: k for k, v in TOKEN2.items()}


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
    """The model's state after `tokens` (a chain's entries from "newgame" up to buywj) and where the player is."""
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


# ---- Phase 2 model.

def course2_reward(n, gt):
    """Course 2's reward with n cashPerLoop buys and gt global cashPerLoop buys (courseScript.UpdateReward)."""
    return math.ceil(4.0 * (1.0 + gt * 0.4) * (1.0 + n) * 10.0 ** C2_TIER)


def box2_cost(b, k):
    """Course 2 box b's cost after k buys."""
    c = {"R": 400, "C": 800, "M": 3800, "G": 55000, "D": DASH_COST}[b]
    for _ in range(k):
        c = {"R": math.ceil(c * 1.14), "C": math.ceil(c * 1.21), "M": math.ceil((c + 1000) * 2), "G": round(c * 5)}[b]
    return c


def move2(a, b):
    """Box a -> box b along the row, bump included."""
    k = abs(ROW[a] - ROW[b])
    return REPEAT2 if k == 0 else round(5 + 180 * k / 9.2)


class Eco2:
    """Course 2's economy; Course 1's clones pay from afar (every 50 ticks)."""
    __slots__ = ("t", "cash", "n", "N", "m", "g", "free", "clones", "c1next", "c1reward", "c1clones")

    def copy(self):
        e = Eco2.__new__(Eco2)
        for f in Eco2.__slots__:
            setattr(e, f, getattr(self, f))
        return e

    def gt(self):
        return self.g + self.free

    def clone_pay(self):
        return math.ceil(course2_reward(self.n, self.gt()) * (0.1 + 0.1 * self.m))

    def course1_pay(self):
        """What Course 1's clones pay per second away from it (SpawnClone offscreen: reward * 0.1 / (lap / N))."""
        if self.c1clones == 0:
            return 0
        reward = math.ceil(self.c1reward * (1.0 + self.gt() * 0.4))
        return math.ceil(reward * 0.1 / (P1_WJ / 50 / self.c1clones))

    def run(self, ticks):
        t0, t1 = self.t, self.t + ticks
        if self.c1next <= t1:
            k = (t1 - self.c1next) // 50 + 1
            self.cash += k * self.course1_pay()
            self.c1next += 50 * k
        paid = 0
        for a in self.clones:  # clone i pays at a, a + P2, ...
            if a <= t1:
                first = 0 if a > t0 else (t0 - a) // P2 + 1
                last = (t1 - a) // P2
                paid += max(0, last - first + 1)
        if paid:
            self.cash += paid * self.clone_pay()
        self.t = t1

    def rate(self):
        return len(self.clones) * self.clone_pay() / P2 + self.course1_pay() / 50

    def wait_for(self, cost, deadline):
        """Stand under the box until the clones cover `cost`; False once that would end past `deadline`."""
        while self.cash < cost:
            r = self.rate()
            if r <= 0 or self.t + (cost - self.cash) / r > deadline + 200:
                return False
            ahead = int((cost - self.cash) / r) - 2 * P2
            if ahead > 50:
                self.run(ahead)
                continue
            nxt = self.c1next
            for a in self.clones:
                nxt = min(nxt, a if a > self.t else a + ((self.t - a) // P2 + 1) * P2)
            self.run(max(1, nxt - self.t))
        return True

    def level(self, b):
        return {"R": self.n, "C": self.N, "M": self.m, "G": self.g, "D": 0}[b]

    def take(self, b):
        self.cash -= box2_cost(b, self.level(b))
        if b == "R":
            self.n += 1
        elif b == "C":
            self.N += 1
            self.clones = self.clones + (self.t + P2,)
        elif b == "M":
            self.m += 1
        elif b == "G":
            self.g += 1

    def lap(self, lead):
        self.run(lead + LAP2)
        self.cash += course2_reward(self.n, self.gt())


def after_wj_chain(e1):
    """Phase 1's state at wall jump through chain:wj: the player at Course 2's start gate."""
    e = Eco2.__new__(Eco2)
    e.t, e.cash, e.n, e.N, e.m, e.g, e.free, e.clones = 0, e1.cash, 0, 0, 0, 0, 0, ()
    e.c1reward, e.c1clones, e.c1next = 1 + e1.n, e1.N, 50
    for token, ticks in WJ_CHAIN:
        e.run(ticks)
        if token == "1":
            e.cash += e.c1reward
        elif token == "2":
            e.cash += course2_reward(0, 0)
        elif token == "bonus1":
            e.free = BONUS1_BOX
    e.c1next -= e.t
    e.t = 0
    return e


def buy_next(e, where, b):
    """Buying b as early as the model allows from `where`: k laps then the walk from the restart, or (k = 0 at a box)
    along the row; (state, laps, walked along the row)."""
    cost = box2_cost(b, e.level(b))
    best = None
    cur, w, laps = e.copy(), where, 0
    while True:
        for walked in ((True, False) if w in ROW else (False,)):
            x = cur.copy()
            x.run(move2(w, b) if walked else TO2[b])
            if x.wait_for(cost, best[0].t if best else math.inf):
                x.take(b)
                if best is None or x.t < best[0].t:
                    best = (x, laps, walked)
        cur.lap(0 if w == "gate" else LEAD2)
        w, laps = "done", laps + 1
        if best and cur.t > best[0].t:
            return best


def plan_states(e0, where0, seq, prefix=()):
    """The state after each purchase of seq (reusing `prefix`, the states of a sequence sharing its start)."""
    out = list(prefix)
    e, where = (out[-1], seq[len(out) - 1]) if out else (e0, where0)
    for b in seq[len(out):]:
        if e.level(b) >= CAP2[b]:
            return None
        e = buy_next(e, where, b)[0]
        out.append(e)
        where = b
    return out


def dash_time(e0, where0, seq, states):
    """Tick dash is bought after seq's purchases."""
    e, where = (states[-1], seq[-1]) if seq else (e0, where0)
    return buy_next(e, where, "D")[0].t


def greedy_order(e0):
    """Course 2's purchases by income gain per W (laps at full speed plus every clone paying)."""
    def income(n, N, m, g):
        reward = course2_reward(n, g + e0.free)
        return reward / (LEAD2 + LAP2) + N * math.ceil(reward * (0.1 + 0.1 * m)) / P2
    seq, lv = [], {"R": 0, "C": 0, "M": 0, "G": 0}
    while True:
        best = None
        for b in "RCMG":
            if lv[b] >= CAP2[b]:
                continue
            up = dict(lv, **{b: lv[b] + 1})
            gain = (income(up["R"], up["C"], up["M"], up["G"]) - income(lv["R"], lv["C"], lv["M"], lv["G"])) / box2_cost(b, lv[b])
            if best is None or gain > best[0]:
                best = (gain, b)
        if best is None:
            return seq
        seq.append(best[1])
        lv[best[1]] += 1


def search_dash(e0, where0, evals):
    """The purchase order before dash that buys it soonest: the greedy order's best prefix, then local search."""
    greedy = greedy_order(e0)
    states = plan_states(e0, where0, greedy)
    best_t, best_len = dash_time(e0, where0, [], []), 0
    for i in range(1, len(states) + 1):
        t = dash_time(e0, where0, greedy[:i], states[:i])
        if t < best_t:
            best_t, best_len = t, i
    best, states = greedy[:best_len], states[:best_len]
    rnd = random.Random(1)
    for _ in range(evals):
        s, op = list(best), rnd.random()
        if op < 0.3 and len(s) > 1:
            i, j = sorted(rnd.sample(range(len(s)), 2))
            s[i], s[j] = s[j], s[i]
        elif op < 0.55:
            i = rnd.randrange(len(s) + 1)
            s.insert(i, rnd.choice("RCMG"))
        elif op < 0.8 and s:
            i = rnd.randrange(len(s))
            del s[i]
        elif len(s) > 1:
            i, j = rnd.randrange(len(s)), rnd.randrange(len(s) - 1)
            s.insert(j, s.pop(i))
            i = min(i, j)
        else:
            continue
        st = plan_states(e0, where0, s, states[:i])
        if st is None:
            continue
        t = dash_time(e0, where0, s, st)
        if t < best_t:
            best_t, best, states = t, s, st
    return best


def dash_tokens(e0, where0, seq):
    """Chain entries for seq then dash, and the model's state after each purchase (for the printout)."""
    tokens, rows, e, where = [], [], e0, where0
    for b in seq + ["D"]:
        e, laps, walked = buy_next(e, where, b)
        for _ in range(laps):
            if where != "gate":
                tokens.append("restart")
            tokens.append("2")
            where = "done"
        if not walked:
            tokens.append("restart")
        tokens.append(TOKEN2[b])
        rows.append((laps, b, e))
        where = b
    return tokens, rows


def replay_dash(e, tokens):
    """The phase 2 model's state after `tokens` (a chain's entries after chain:wj) and where the player is."""
    where, restarted = "gate", False
    for token in tokens:
        if token == "restart":
            restarted = True
            continue
        if token == "2":
            e.lap(0 if where == "gate" and not restarted else LEAD2)
            where = "done"
        else:
            for part in token.split("+"):
                b = BOX2[part]
                if e.level(b) >= CAP2[b]:
                    raise SystemExit(f"{token}: the box is already at its cap in the model")
                e.run(TO2[b] if restarted or where not in ROW else move2(where, b))
                if not e.wait_for(box2_cost(b, e.level(b)), math.inf):
                    raise SystemExit(f"{token} is not affordable in the model where the chain buys it")
                e.take(b)
                where, restarted = b, False
        restarted = False
    return e, where


def segment_count(token, chains):
    """Segments a chain entry adds (chain:<name> adds its chain's)."""
    if token == "restart":
        return 0
    if token.startswith("chain:"):
        return sum(segment_count(t, chains) for t in chains[token[len("chain:"):]])
    return 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", type=int, default=1, help="keep the newgame chain's first N segments (1: just newgameto1)")
    ap.add_argument("--evals", type=int, default=30000, help="phase 2 local search tries (~2 min per 30000)")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()
    chains = json.load(open(CHAINS, encoding="utf-8")) if os.path.exists(CHAINS) else []
    parts = {c["name"]: c.get("segments", []) for c in chains}
    current = next((c for c in chains if c.get("name") == "newgame"), {"segments": ["newgame", "newgameto1"]})["segments"]
    # The first N segments; an included chain is kept whole once one of its segments is.
    kept, count = ["newgame"], 0
    for token in current[1:]:
        if token != "restart" and count >= max(1, a.keep):
            break
        count += segment_count(token, parts)
        kept.append(token)
    while kept[-1] == "restart":
        kept.pop()
    split = kept.index("chain:wj") + 1 if "chain:wj" in kept else len(kept)
    phase1, phase2 = kept[:split], kept[split:]

    if "buywj" in phase1:
        e1, _ = replay([t for t in phase1 if t != "chain:wj"])
        tokens1 = [t for t in phase1 if t != "chain:wj"]
    else:
        e0, where0 = replay(phase1)
        e1, tokens1 = search(e0, where0, phase1)
        shown, where = e0.copy(), where0
        for token in tokens1[len(phase1):]:
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
        print(f"wall jump bought at tick {e1.t} ({e1.t * 0.02:.2f} s after the new game's first Course 1 segment, estimates)")
    while tokens1[-1] == "restart":
        tokens1.pop()

    e2 = after_wj_chain(e1)
    print(f"chain:wj: {sum(t for _, t in WJ_CHAIN) * 0.02:.2f} s, Course 2's start gate with {e2.cash:.0f} W")
    if "buydash" in phase2:
        tokens2 = phase2
    else:
        e2, where2 = replay_dash(e2, phase2)
        seq = search_dash(e2, where2, a.evals)
        new, rows = dash_tokens(e2, where2, seq)
        tokens2 = phase2 + new
        for laps, b, e in rows:
            what = (f"{laps} lap{'s' if laps != 1 else ''}, " if laps else "") + TOKEN2[b]
            print(f"  tick {e.t:5d}  {what:26s} -> {e.cash:8.0f} W  reward {course2_reward(e.n, e.gt()):6d}"
                  f"  clones {e.N:2d}  mult {0.1 + 0.1 * e.m:.1f}  global {e.gt()}")
        print(f"dash bought at tick {rows[-1][2].t} ({rows[-1][2].t * 0.02:.2f} s after chain:wj, estimates)")

    seg = tokens1 + ["restart", "chain:wj"] + tokens2 + ["restart", "chain:dash"]
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
