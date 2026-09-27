import argparse
import bisect
import copy
import functools
import heapq
import itertools
import json
import math
import os
import re
import struct

import gamepaths

CONFIG = os.environ.get("IGTAP_CONFIG", str(gamepaths.CONFIG))
FULLTAS = os.path.join(CONFIG, "fulltas.json")

C1_BUYS = 20
C1_CLONES = 9
C1_PATH = 82
CLONE_PATH = 110
CASH = 713 + (490 + 406) // 51 * 11
ARRIVE = 418
BONUS1_TICK = 8182
GLOBAL0 = 1
C1_GLOBAL = 0

LAP = 122
LEAD = 28
RESET_X = 4783
BOX_X = {"R": 4199, "C": 4019, "M": 3839, "G": 3659, "D": 3479}
TO = {b: round((RESET_X - x - 50) / 9.2) for b, x in BOX_X.items()}
ROW = {"D": 0, "G": 1, "M": 2, "C": 3, "R": 4}
REPEAT = 22
ROW_STEP = 7
CAP = {"R": 45, "C": 35, "M": 10, "G": 2, "D": 1}
TOKEN = {"R": "buyreward2", "C": "buyclone2", "M": "buyclonemult2", "G": "buyglobalreward2", "D": "buyds"}
OFFSCREEN = 51
TAIL = ["start:2", "finish:2", "reward:bonus2", "start:3", "reward:bonus3", "start:3", "finish:3"]
FOREVER = 10 ** 8

def f32(x):
    return struct.unpack("f", struct.pack("f", x))[0]

@functools.lru_cache(maxsize=None)
def reward(base, tier, n, g):
    r = base * (1.0 + 0.0 * (50.0 / 100.0) + 0.0 * (100.0 / 100.0)) * (1.0 + g * (40.0 / 100.0)) * (1.0 + n * (100.0 / 100.0))
    r = r * (1.0 + 0.0) * (1.0 + 0.0 * (100.0 / 100.0)) * 1.0 * 1.0 * math.pow(10.0, tier)
    return math.ceil(math.pow(math.ceil(r), 1.0 + 0.0))

def mult(m):
    return 0.1 + 0.0 * 0.2 + m * 0.1

@functools.lru_cache(maxsize=None)
def clone_pay(r, m):
    return math.ceil(r * 1.0 * mult(m) * 1.0)

def offscreen_pay(r, m, clones, path_ticks):
    if clones == 0:
        return 0
    return math.ceil(r * 1.0 * mult(m) * 1.0 * 1.0 / f32(f32(f32(path_ticks * 0.02) / clones) / 1.0))

@functools.lru_cache(maxsize=None)
def box_cost(b, k):
    c = {"R": 400, "C": 800, "M": 3800, "G": 55000, "D": 3000000}[b]
    for _ in range(k):
        if b == "R":
            c = math.ceil(c * 1.14)
        elif b == "C":
            c = math.ceil(c * 1.21)
        elif b == "M":
            c = math.ceil((c + 1000) * 2.0)
        elif b == "G":
            c = round(c * 5.0)
        else:
            c = math.ceil(c * 1.45)
    return c

C1_PAY = offscreen_pay(reward(1, 0, C1_BUYS, C1_GLOBAL), 0, C1_CLONES, C1_PATH)

def move(a, b):
    return REPEAT + ROW_STEP * max(abs(ROW[a] - ROW[b]) - 1, 0)

def since_table(n):
    acc, out = 0.0, [0.0]
    for _ in range(n):
        acc = f32(acc + f32(0.02))
        out.append(acc)
    return out

SINCE = since_table(4096)

def gate(clones):
    return bisect.bisect_left(SINCE, f32(SINCE[CLONE_PATH] / clones))

def run_clones(active, pool, last, n, t0, T):
    active, pool = list(active), list(pool)
    wait = gate(n) if n else FOREVER
    life = CLONE_PATH + 1
    pays, u = 0, t0
    while True:
        pay = active[0] + life if active else FOREVER
        spawn = max(u, pool[0], last + wait) if pool else FOREVER
        if spawn <= pay and spawn < T:
            pool.pop(0)
            active.append(spawn)
            last = u = spawn
        elif pay < T:
            active.pop(0)
            bisect.insort(pool, pay + 1)
            pays += 1
            u = pay
        else:
            return pays, tuple(active), tuple(pool), last

def wait_for(s, cost, deadline):
    need = cost - s.cash
    if need <= 0:
        return s.t < deadline
    if s.income(deadline - 1) < need:
        return False
    lo, hi = s.t, deadline - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if s.income(mid) >= need:
            hi = mid
        else:
            lo = mid
    s.advance(hi)
    return True

class State:
    __slots__ = ("t", "cash", "n", "N", "m", "g", "P", "active", "pool", "last", "c1next", "where", "prev", "tokens", "what")

    def child(self):
        s = State.__new__(State)
        s.t, s.cash, s.n, s.N, s.m, s.g, s.P = self.t, self.cash, self.n, self.N, self.m, self.g, self.P
        s.active, s.pool, s.last, s.c1next, s.where = self.active, self.pool, self.last, self.c1next, self.where
        s.prev, s.tokens, s.what = self, (), ""
        return s

    def level(self, b):
        return {"R": self.n, "C": self.N, "M": self.m, "G": self.g, "D": 0}[b]

    def reward2(self):
        return reward(4, 2, self.n, GLOBAL0 + self.g)

    def pay2(self):
        return clone_pay(self.reward2(), self.m)

    def rate(self):
        return self.N * self.pay2() / self.P + C1_PAY / OFFSCREEN

    def c1(self, T):
        return (T - 1 - self.c1next) // OFFSCREEN + 1 if self.c1next < T else 0

    def income(self, T):
        return self.c1(T) * C1_PAY + run_clones(self.active, self.pool, self.last, self.N, self.t, T)[0] * self.pay2()

    def advance(self, T):
        pays, self.active, self.pool, self.last = run_clones(self.active, self.pool, self.last, self.N, self.t, T)
        k = self.c1(T)
        self.cash += k * C1_PAY + pays * self.pay2()
        self.c1next += k * OFFSCREEN
        self.t = T

    def lap(self):
        s = self.child()
        s.advance(s.t + (LAP if s.where == "gate" else LEAD + LAP))
        s.cash += s.reward2()
        s.tokens = ("finish:2",) if s.where == "gate" else ("start:2", "finish:2")
        s.what = "lap"
        s.where = "done"
        return s

    def buy(self, b, walk_row, force=False):
        if self.level(b) >= CAP[b]:
            return None
        deadline = self.t + (FOREVER if force else (LAP if self.where == "gate" else LEAD + LAP) + TO[b])
        s = self.child()
        s.advance(s.t + (move(s.where, b) if walk_row else TO[b]))
        arrived = s.t
        cost = box_cost(b, s.level(b))
        if not wait_for(s, cost, deadline):
            return None
        s.cash -= cost
        if b == "R":
            s.n += 1
        elif b == "C":
            s.N += 1
            s.pool = tuple(sorted(s.pool + (s.t,)))
        elif b == "M":
            s.m += 1
        elif b == "G":
            s.g += 1
        s.tokens = ("buy:" + TOKEN[b],)
        s.what = ("" if walk_row else "restart, ") + TOKEN[b] + (f" (waits {s.t - arrived})" if s.t > arrived else "")
        s.where = b
        return s

def start_state(cash):
    s = State.__new__(State)
    s.t, s.cash, s.n, s.N, s.m, s.g, s.P = 0, cash, 0, 0, 0, 0, CLONE_PATH + 2
    s.active, s.pool, s.last, s.c1next, s.where = (), (), -FOREVER, OFFSCREEN // 2, "gate"
    s.prev, s.tokens, s.what = None, (), ""
    s.advance(ARRIVE)
    s.tokens, s.what = ("start:2",), "restart, bonus1to2 to Course 2's start gate"
    return s

def dominated(best, s):
    levels = (s.n, s.N, s.m, s.g)
    places = (s.where,) + (tuple(ROW) if s.where == "done" else ())
    for i in range(-1, 4):
        up = levels if i < 0 else levels[:i] + (levels[i] + 1,) + levels[i + 1:]
        for w in places:
            e = best.get(up + (w,))
            if e is not None and e[0] + e[1] * s.t >= s.cash:
                return True
    return False

def search(s0):
    order = itertools.count()
    heap = [(s0.t, next(order), s0)]
    best = {}
    expanded = 0
    while heap:
        _, _, s = heapq.heappop(heap)
        if s.where == "D":
            return s, expanded
        if dominated(best, s):
            continue
        rate = s.rate()
        best[(s.n, s.N, s.m, s.g, s.where)] = (s.cash - rate * s.t, rate)
        expanded += 1
        for x in stage1_options(s):
            heapq.heappush(heap, (x.t, next(order), x))
    raise SystemExit("dash is never affordable")

def stage1_options(s, force=False):
    options = [s.lap()]
    for b in "RCMGD":
        options.append(s.buy(b, False, force))
        if s.where in ROW:
            options.append(s.buy(b, True, force))
    return [x for x in options if x is not None]

def replay(s, goals, options):
    i = 0
    while i < len(goals):
        for force in (False, True):
            fits = [x for x in options(s, force) if tuple(goals[i:i + len(x.tokens)]) == x.tokens]
            if fits:
                break
        else:
            raise SystemExit(f"the model can't follow goal {goals[i]} ({i} of {len(goals)}) after {s.what or 'the start'}")
        s = min(fits, key=lambda x: x.t)
        i += len(s.tokens)
    return s

LATE_LAP = {1: (53, 47),
            2: (89, 78),
            3: (233, 217),
            4: (751, 751),
            5: (657, 657)}
TAIL_STEPS = [("restart, Course 2 lap", LEAD + 89, 0), ("2tobonus2 + bonus2", 348 + 53, 1),
              ("bonus2to3 to Course 3's gate", 189, 0), ("bonus3", 170, 1), ("restart, Course 3 lap", 29 + 233, 0)]
TRANSIT = {(3, 1): 600, (1, 2): 242, (2, 3): 537, (3, 4): 1200, (3, 5): 600 + 620, (5, 4): 620 + 242 + 537 + 1200}

def guess_ticks(frm, to):
    dx, dy = to[0] - frm[0], to[1] - frm[1]
    return round(20 + abs(dx) / 9.2 + max(dy, 0) / 6 + max(-dy, 0) / 20)

DETOUR = {"reward:bonus5": guess_ticks((9505, 6971), (6350, 6088)),
          "reward:bonus4": guess_ticks((-3282, -294), (-3104, -2102)),
          "reward:bonus15": guess_ticks((-3282, -294), (-8477, 2532)),
          "buyfree5": guess_ticks((-3282, -294), (-3979, 4051)),
          "buyglobalreward4": guess_ticks((20201, 10556), (20731, 15157)),
          "reward:bonus7": guess_ticks((20201, 10556), (15277, 11802)),
          "reward:bonus6": guess_ticks((20201, 10556), (10032, 14330)),
          "reward:bonus16": guess_ticks((20201, 10556), (-325, 14330))}

@functools.lru_cache(maxsize=None)
def roll_mult(fast, big, max_fast, max_big):
    def tiers(chance, max_tiers):
        p_reach, dist = 1.0, []
        for i in range(1 + max_tiers):
            below = min(chance * (1.0 / (1.0 + i)), 50.0 + max_tiers * 5.0)
            p = sum(1 for k in range(101) if k < below) / 101
            dist.append(p_reach * (1 - p))
            p_reach *= p
        return dist + [p_reach]
    return sum(pi * pj * (2 * j + 1) * (1 + 0.35 * i) * 0.75 ** j
               for i, pi in enumerate(tiers(fast * 5.0, max_fast)) for j, pj in enumerate(tiers(big * 5.0, max_big)))

class Box:

    def __init__(self, token, x, cost, scale=1.0, add=0.0, cap=1, kind="X", off=None):
        self.token, self.x, self.cost0, self.scale, self.add, self.cap0, self.kind = token, x, cost, scale, add, cap, kind
        self.goal = token if ":" in token else "buy:" + token
        self.off = off
        self._costs = [cost]

    def retier(self, dtier):
        self.cost0 *= math.pow(10.0, dtier)
        self._costs = [self.cost0]

    def cost(self, k):
        while len(self._costs) <= k:
            c = self._costs[-1] + self.add
            self._costs.append(round(c * self.scale) if self.kind == "G" else math.ceil(math.ceil(c * self.scale)))
        return self._costs[k]

class Course:

    def __init__(self, number, base, tier, mod, reset_x, lead, boxes, breaker=(0.0, 0.0)):
        self.number, self.base, self.tier, self.rtier, self.mod, self.reset_x, self.lead = number, base, tier, tier, mod, reset_x, lead
        self.breaker = breaker
        self.boxes = boxes
        self.level = {k: 0 for k in boxes}
        self.capmult = 1.0
        self.P = None
        self.reward = 0

    def cap(self, k):
        b = self.boxes[k]
        return round(b.cap0 * self.capmult) if b.kind in "RCMFB" else b.cap0

    def update_reward(self, g):
        self.reward = reward(self.base, self.rtier, self.level.get("R", 0), g)

    def rate(self, glob):
        n = self.level.get("C", 0)
        if not n or not self.P:
            return 0.0
        roll = roll_mult(self.level.get("F", 0), self.level.get("B", 0), glob["max_fast"], glob["max_big"])
        return n * math.ceil(self.reward * (0.1 + glob["gm"] * 0.2 + self.level.get("M", 0) * 0.1)) * roll / self.P

    def on_row(self, k):
        return k in self.boxes and self.boxes[k].off is None

    def to(self, k):
        b = self.boxes[k]
        return b.off if b.off is not None else round((abs(self.reset_x - b.x) - 50) / 9.2)

    def move(self, a, b):
        return REPEAT if a == b else round(5 + abs(self.boxes[a].x - self.boxes[b].x) / 9.2)

    def prestige(self, tier, rtier, reset_path):
        for k, b in self.boxes.items():
            if b.kind in "RCMFB":
                self.level[k] = 0
                b.retier(tier - self.tier)
        self.tier, self.rtier = tier, rtier
        if reset_path:
            self.P = None

def late_courses(dash):
    c1 = Course(1, 1, 0.0, 1.0, 2241, 29, {"R": Box("buyreward", 1744, 1, 1.1, cap=40, kind="R"),
                                            "C": Box("buyclone", 1547, 8, 1.153, cap=35, kind="C")}, breaker=(0.0, -0.85))
    c1.level.update(R=C1_BUYS, C=C1_CLONES)
    c1.P = C1_PATH
    c1.update_reward(C1_GLOBAL)
    c2 = Course(2, 4, 2.0, 0.5, RESET_X, LEAD, {"R": Box("buyreward2", 4199, 400, 1.14, cap=45, kind="R"),
                                                 "C": Box("buyclone2", 4019, 800, 1.21, cap=35, kind="C"),
                                                 "M": Box("buyclonemult2", 3839, 3800, 2.0, 1000.0, cap=10, kind="M")},
                breaker=(1.0, -0.7))
    c2.level.update(R=dash.n, C=dash.N, M=dash.m)
    c2.P = dash.P
    c3 = Course(3, 7, 5.2, 1.0, 9505, 29, {"R": Box("buyreward3", 9104, 1426403.8732150027, 1.19, cap=60, kind="R"),
                                            "C": Box("buyclone3", 8934, 3645254.3426605626, 1.27, cap=40, kind="C"),
                                            "M": Box("buyclonemult3", 8594, 12679145.539688913, 2.0, cap=12, kind="M"),
                                            "G": Box("buyglobalreward3", 8384, 110942523.47227798, 3.0, cap=4, kind="G"),
                                            "J": Box("buydj", 7951, 554712617361.3899),
                                            "P": Box("buystatue", 7308, 11094252347.227798),
                                            "S": Box("buyclonespeed", 6072, 475467957.73833424),
                                            "F": Box("buyfast3", 8764, 6339572.769844457, 1.45, cap=12, kind="F")},
                breaker=(1.2, 0.5))
    c4 = Course(4, 10, 10.0, 4.0, 20200, 29, {"R": Box("buyreward4", 19715, 3e11, 1.167, cap=60, kind="R"),
                                               "C": Box("buyclone4", 19545, 1.1e12, 1.22, cap=50, kind="C"),
                                               "M": Box("buyclonemult4", 19375, 2.2e12, 2.0, cap=10, kind="M"),
                                               "Q": Box("buyprestige4", 18586, 1.2e15),
                                               "W": Box("buyswap", 17767, 5e17),
                                               "F": Box("buyfast4", 18818, 2.6e12, 1.5, cap=15, kind="F"),
                                               "B": Box("buybig4", 19158, 1.5e12, 1.4, cap=15, kind="B"),
                                               "Z": Box("buymaxsize4", 18988.4, 2e14),
                                               "V": Box("buyblockswap5", 17768, 9.486832980505138e34),
                                               "H": Box("buyglobalreward4", 20731, 3e12, kind="G", off=DETOUR["buyglobalreward4"]),
                                               "a": Box("reward:bonus7", 15277, 0, kind="G", off=DETOUR["reward:bonus7"]),
                                               "b": Box("reward:bonus6", 10032, 0, kind="G", off=DETOUR["reward:bonus6"]),
                                               "c": Box("reward:bonus16", -325, 0, kind="G", off=DETOUR["reward:bonus16"])})
    c5 = Course(5, 7, 16.5, 1.0, -3282, 29, {"R": Box("buyreward5", -2825, 6.324555320336758e17, 1.1, cap=80, kind="R"),
                                              "C": Box("buyclone5", -2665, 1.4230249470757706e18, 1.09, cap=89, kind="C"),
                                              "M": Box("buyclonemult5", -2505, 7.905694150420948e18, 1.75, cap=14, kind="M"),
                                              "B": Box("buybig5", -2313, 8.221921916437786e18, 1.47, cap=20, kind="B"),
                                              "F": Box("buyfast5", -2153, 6.324555320336759e18, 1.4, cap=20, kind="F"),
                                              "G": Box("buyglobalreward5", -1967, 1.2649110640673517e20, 5.0, cap=4, kind="G"),
                                              "Q": Box("buyprestige5", -1807, 3.1622776601683792e22),
                                              "X": Box("buyfree5", -3979, 3.162277660168379e19, kind="G", off=DETOUR["buyfree5"]),
                                              "d": Box("reward:bonus4", -3104, 0, kind="G", off=DETOUR["reward:bonus4"]),
                                              "e": Box("reward:bonus15", -8477, 0, kind="G", off=DETOUR["reward:bonus15"])})
    return {1: c1, 2: c2, 3: c3, 4: c4, 5: c5}

class Phase:

    def __init__(self, course, keys, required, need_lap, other_rate, glob, lap):
        self.c, self.keys, self.required, self.need_lap = course, keys, required, need_lap
        self.other_rate, self.gm, self.lap = other_rate, glob["gm"], lap
        self.caps = tuple(course.cap(k) for k in keys)
        self.req = tuple(required.get(k, 0) for k in keys)
        self.ri = keys.index("R") if "R" in keys else -1
        self.mi = keys.index("M") if "M" in keys else -1
        self.at = {k: keys.index(k) for k in "FBSZ" if k in keys}
        self.max_fast, self.max_big = glob["max_fast"], glob["max_big"]

    def reward(self, lv, g):
        return reward(self.c.base, self.c.rtier, lv[self.ri] if self.ri >= 0 else 0, g)

    def pay(self, lv, g):
        m = lv[self.mi] if self.mi >= 0 else 0
        a = self.at
        roll = roll_mult(lv[a["F"]] if "F" in a else self.c.level.get("F", 0), lv[a["B"]] if "B" in a else self.c.level.get("B", 0),
                         self.max_fast + (lv[a["S"]] if "S" in a else 0), self.max_big + (lv[a["Z"]] if "Z" in a else 0))
        return math.ceil(self.reward(lv, g) * (0.1 + self.gm * 0.2 + m * 0.1)) * roll

class LateState:
    __slots__ = ("ph", "t", "cash", "lv", "g", "P", "clones", "where", "lapped", "prev", "tokens", "what")

    def child(self):
        s = LateState.__new__(LateState)
        s.ph, s.t, s.cash, s.lv, s.g, s.P, s.clones, s.where, s.lapped = self.ph, self.t, self.cash, self.lv, self.g, self.P, self.clones, self.where, self.lapped
        s.prev, s.tokens, s.what = self, (), ""
        return s

    def pay(self):
        return self.ph.pay(self.lv, self.g)

    def rate(self):
        return self.ph.other_rate + (len(self.clones) * self.pay() / self.P if self.P else 0.0)

    def income(self, T):
        w = self.ph.other_rate * (T - self.t)
        if self.clones and self.P:
            w += self.pay() / self.P * sum(T - max(a, self.t) for a in self.clones if a < T)
        return w

    def advance(self, T):
        self.cash += self.income(T)
        self.t = T

    def done(self):
        return (self.lapped or not self.ph.need_lap) and all(l >= r for l, r in zip(self.lv, self.ph.req))

    def lap(self):
        ph = self.ph
        s = self.child()
        s.advance(s.t + (ph.lap if s.where == "gate" else ph.c.lead + ph.lap))
        s.cash += ph.reward(s.lv, s.g)
        if s.P is None or ph.lap < s.P:
            s.P = ph.lap
            n = len(s.clones)
            s.clones = tuple(s.t + (i + 1) * s.P // max(n, 1) for i in range(n))
        n = ph.c.number
        s.tokens = (f"finish:{n}",) if s.where == "gate" else (f"start:{n}", f"finish:{n}")
        s.what, s.where, s.lapped = "lap", "done", True
        return s

    def buy(self, i, walk_row, force=False):
        ph = self.ph
        k = ph.keys[i]
        if self.lv[i] >= ph.caps[i]:
            return None
        c = ph.c
        deadline = self.t + (FOREVER if force else (ph.lap if self.where == "gate" else c.lead + ph.lap) + c.to(k))
        s = self.child()
        s.advance(s.t + (c.move(s.where, k) if walk_row else c.to(k)))
        arrived = s.t
        box = c.boxes[k]
        cost = box.cost(s.lv[i])
        if not wait_for(s, cost, deadline):
            return None
        s.cash -= cost
        s.lv = s.lv[:i] + (s.lv[i] + 1,) + s.lv[i + 1:]
        if box.kind == "C" and s.P:
            s.clones = s.clones + (s.t + s.P,)
        elif box.kind == "G":
            s.g += 1
        s.tokens = (box.goal,)
        s.what = ("" if walk_row else "restart, ") + box.token + (f" (waits {s.t - arrived})" if s.t > arrived else "")
        s.where = k
        return s

def late_options(s, force=False):
    options = [s.lap()]
    c = s.ph.c
    for i, k in enumerate(s.ph.keys):
        options.append(s.buy(i, False, force))
        if c.on_row(s.where) and c.on_row(k):
            options.append(s.buy(i, True, force))
    return [x for x in options if x is not None]

def late_greedy(s0, lap_income=False):
    ph = s0.ph
    s, steps = s0, 0
    while not s.done():
        def projected(x):
            need = sum(ph.c.boxes[k].cost(l) for k, lv, r in zip(ph.keys, x.lv, ph.req) for l in range(lv, r))
            walks = sum(max(0, r - lv) * (REPEAT if ph.c.on_row(k) else ph.c.to(k)) for k, lv, r in zip(ph.keys, x.lv, ph.req))
            rest = walks + (0 if x.lapped or not ph.need_lap else ph.c.lead + ph.lap)
            short = max(0.0, need - x.cash)
            rate = x.rate() + (ph.reward(x.lv, x.g) / (ph.c.lead + ph.lap) if lap_income else 0.0)
            return x.t + (short / rate if rate else math.inf if short else 0.0) + rest
        s = min(late_options(s), key=projected)
        steps += 1
    return s, steps

MAIN_PHASES = [(3, "RCMGFSP", {"S": 1, "P": 1}, False, "clone speed max + statue"),
               (1, "RC", "caps", True, "Course 1 bought out"),
               (2, "RCM", "caps", True, "Course 2 bought out"),
               (3, "RCMGFJ", {"J": 1}, True, "double jump"),
               (4, "RCMFBZQ", {"Q": 1}, True, "Boost all previous courses"),
               (4, "RCMFBZW", {"W": 1}, False, "Activate orange blocks")]

class Late:

    def __init__(self, dash, verbose, quiet=False):
        self.cs = late_courses(dash)
        self.g, self.dj = GLOBAL0 + dash.g, False
        self.glob = {"gm": 0, "max_fast": 0, "max_big": 0}
        self.t, self.cash = dash.t, dash.cash
        self.tokens, self.here, self.where = [], 3, "done"
        self.verbose, self.quiet = verbose, quiet
        self.steps = []
        self.breaker = None
        self.lap_income = False
        self.cs[2].update_reward(self.g)

    def say(self, line):
        if not self.quiet:
            print(line)

    def other_rate(self, here):
        return sum(c.rate(self.glob) for n, c in self.cs.items() if n != here)

    def tail(self):
        cs = self.cs
        self.say("After dash (TAIL):")
        for what, ticks, dg in TAIL_STEPS:
            self.cash += self.other_rate(0) * ticks
            self.t += ticks
            if what == "restart, Course 2 lap":
                self.cash += cs[2].reward
                cs[2].P = min(cs[2].P, LATE_LAP[2][0])
            self.g += dg
            self.say(f"  tick {self.t:6d}  {what:44s} -> {self.cash:12.4g} W  G {self.g}")
        cs[3].P = LATE_LAP[3][0]
        cs[3].update_reward(self.g)
        self.cash += cs[3].reward
        self.tokens += TAIL

    def boost(self, box_tier, olds):
        for n in olds:
            c = self.cs[n]
            c.rtier = box_tier + c.mod
            c.capmult += 0.5
            c.update_reward(self.g)

    def trip_breaker(self):
        cs, c5 = self.cs, self.cs[5]
        for n in (1, 2, 3, 4):
            cs[n].capmult += 0.5
        for n in (1, 2, 3):
            c = cs[n]
            tier = c5.tier + c.tier + c.breaker[0]
            c.prestige(tier, tier + c.breaker[1], False)
        c4 = cs[4]
        tier = c5.tier + c4.tier - 1.0 + c4.mod
        c4.prestige(tier, tier, True)
        c5.prestige(c5.tier - 1.0 + c5.mod, c5.rtier - 1.0 + c5.mod, True)
        for c in cs.values():
            c.update_reward(self.g)
        self.cash = 0.0
        self.breaker = (self.t, self.g)
        self.say(f"  tick {self.t:6d}  trip breaker: Cash 0; tier / reward tier " +
                 ", ".join(f"{n}: {c.tier:g} / {c.rtier:g}" for n, c in cs.items()))

    def bought(self, token):
        if token == "buystatue":
            self.glob["gm"] += 1
            self.boost(self.cs[3].tier, (1, 2))
        elif token == "buydj":
            self.dj = True
        elif token == "buyprestige4":
            self.boost(self.cs[4].tier, (1, 2, 3))
        elif token == "buyprestige5":
            self.trip_breaker()

    def travel(self, course):
        cs, t0 = self.cs, self.t
        cs[self.here].update_reward(self.g)
        ticks = TRANSIT[(self.here, course)]
        self.cash += sum(x.rate(self.glob) for x in cs.values()) * ticks
        self.t += ticks
        cs[course].update_reward(self.g)
        self.tokens.append(f"start:{course}")
        self.say(f"  tick {self.t:6d}  {self.here} -> {course} ({ticks} ticks)" + " " * 26 + f"-> {self.cash:12.4g} W")
        self.steps.append((f"{self.here} -> {course}", t0, self.t, self.cash, 1))
        self.here, self.where = course, "gate"

    def detour(self, goal, ticks):
        t0 = self.t
        self.cash += sum(x.rate(self.glob) for x in self.cs.values()) * ticks
        self.t += ticks
        self.g += 1
        self.tokens.append(goal)
        self.where = "done"
        self.say(f"  tick {self.t:6d}  {goal} ({ticks} ticks)" + " " * 20 + f"-> {self.cash:12.4g} W  G {self.g}")
        self.steps.append((goal, t0, self.t, self.cash, 1))

    def phase(self, course, keys, required, need_lap, name, given=None):
        cs, c = self.cs, self.cs[course]
        if course != self.here:
            self.travel(course)
        keys = [k for k in keys if k in c.boxes]
        if required == "caps":
            required = {k: c.cap(k) for k in keys}
        ph = Phase(c, keys, required, need_lap, self.other_rate(course), self.glob, LATE_LAP[course][1 if self.dj else 0])
        s0 = LateState.__new__(LateState)
        t0 = self.t
        s0.ph, s0.t, s0.cash, s0.lv, s0.g, s0.P = ph, t0, self.cash, tuple(c.level[k] for k in keys), self.g, c.P
        n = c.level.get("C", 0)
        s0.clones = tuple(t0 + (i + 1) * c.P // n for i in range(n)) if n and c.P else ()
        s0.where, s0.lapped, s0.prev, s0.tokens, s0.what = self.where, False, None, (), ""
        if given is None:
            end, steps = late_greedy(s0, self.lap_income)
        else:
            end, steps = replay(s0, given, late_options), len(given)
        rows = []
        s = end
        while s is not None:
            rows.append(s)
            s = s.prev
        rows.reverse()
        if self.verbose and not self.quiet:
            for r in rows[1:]:
                print(f"  tick {r.t:6d}  {r.what:44s} -> {r.cash:12.4g} W  levels {dict(zip(keys, r.lv))}  G {r.g}  path {r.P}")
        goals = [x for r in rows for x in r.tokens]
        self.tokens += goals
        self.t, self.cash, self.g, self.where = end.t, end.cash, end.g, end.where
        before = dict(c.level)
        for k, l in zip(keys, end.lv):
            c.level[k] = l
        c.P = end.P
        c.update_reward(self.g)
        self.glob["max_fast"], self.glob["max_big"] = cs[3].level["S"], cs[4].level["Z"]
        laps = sum(1 for r in rows if r.what == "lap")
        self.say(f"  tick {self.t:6d}  Course {course}: {name} ({laps} laps, levels {dict(zip(keys, end.lv))}, {steps} steps)"
                 f" -> {self.cash:12.4g} W  G {self.g}")
        self.steps.append((f"Course {course}: {name}", t0, self.t, self.cash, len(goals)))
        for k in keys:
            if c.boxes[k].kind == "X" and c.level[k] > before[k]:
                self.bought(c.boxes[k].token)

def late_plan(dash, verbose):
    run = Late(dash, verbose)
    run.tail()
    for phase in MAIN_PHASES:
        run.phase(*phase)
    return run.tokens[len(TAIL):], run.t

ANY_OPTIONS = {"reward:bonus5": (3, None), "reward:bonus4": (5, "d"), "reward:bonus15": (5, "e"), "buyfree5": (5, "X"),
               "buyglobalreward4": (4, "H"), "reward:bonus7": (4, "a"), "reward:bonus6": (4, "b"),
               "reward:bonus16": (4, "c")}

def goal_course(cs, goal):
    kind, _, arg = goal.partition(":")
    if kind in ("start", "finish"):
        return int(arg)
    return next((n for n, c in cs.items() if any(b.goal == goal for b in c.boxes.values())), None)

def any_prefix(run_name, verbose):
    runs = json.load(open(FULLTAS, encoding="utf-8"))
    run = next((r for r in runs if r.get("name") == run_name), None)
    if run is None or "buy:buydj" not in run["goals"] or "reward:bonus1" not in run["goals"]:
        raise SystemExit(f"fulltas.json has no run {run_name} with reward:bonus1 and buy:buydj")
    keep = run["goals"][:run["goals"].index("buy:buydj") + 1]
    after = keep[keep.index("reward:bonus1") + 1:]
    if after[0] != "start:2" or "buy:buyds" not in after:
        raise SystemExit(f"run {run_name} doesn't go on from reward:bonus1 to Course 2 and buy:buyds")
    d = after.index("buy:buyds") + 1
    dash = replay(start_state(CASH), after[1:d], stage1_options)
    late = Late(dash, verbose, quiet=True)
    if after[d:d + len(TAIL)] != TAIL:
        raise SystemExit(f"run {run_name}'s goals after buy:buyds aren't TAIL")
    late.tail()
    rest = after[d + len(TAIL):]
    for course, keys, required, need_lap, name in MAIN_PHASES:
        if not rest:
            break
        if course != late.here:
            if rest[0] != f"start:{course}":
                raise SystemExit(f"run {run_name}: {rest[0]} where main's phases go to Course {course}")
            rest = rest[1:]
        n = 0
        while n < len(rest) and goal_course(late.cs, rest[n]) == course:
            n += 1
        late.phase(course, keys, required, need_lap, name, given=rest[:n])
        rest = rest[n:]
    if rest:
        raise SystemExit(f"run {run_name}: {rest[0]} doesn't fit main's phases")
    s, stage1 = dash, []
    while s is not None:
        stage1 = list(s.tokens) + stage1
        s = s.prev
    assert stage1 + late.tokens == after, "the replay's goals differ from the run's"
    return keep, late

def any_tail(base, choice, quiet, verbose=False):
    run = copy.deepcopy(base)
    run.quiet, run.verbose, run.tokens, run.steps, run.lap_income = quiet, verbose, [], [], True
    ticks = DETOUR
    for goal, (course, k) in ANY_OPTIONS.items():
        if k is not None:
            run.cs[course].boxes[k].off = ticks[goal]
    if choice["reward:bonus5"]:
        run.detour("reward:bonus5", ticks["reward:bonus5"])
    extra = {n: {k: 1 for goal, (c, k) in ANY_OPTIONS.items() if c == n and choice[goal]} for n in (4, 5)}
    run.phase(5, "RCMBFGQ" + "".join(extra[5]), dict(Q=1, G=choice["G5"], **extra[5]), True,
              "prestige upgrade box (trip breaker)")
    run.phase(4, "RCMFBZV" + "".join(extra[4]), dict(V=1, **extra[4]), True, "SwapBlocksOnDash")
    return run

def any_choose(base):
    choice = dict({goal: False for goal in ANY_OPTIONS}, G5=0)
    end = any_tail(base, choice, True).t
    tried = 1
    while True:
        moves = [dict(choice, **{goal: not choice[goal]}) for goal in ANY_OPTIONS]
        moves += [dict(choice, G5=g) for g in (choice["G5"] - 1, choice["G5"] + 1) if 0 <= g <= 4]
        ends = [(any_tail(base, m, True).t, i, m) for i, m in enumerate(moves)]
        tried += len(moves)
        best = min(ends)
        if best[0] >= end:
            return choice, end, tried
        end, _, choice = best

def invalid_goals(goals):
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "..", "FullTasPlugin.cs"), encoding="utf-8") as f:
        boxes = set(re.findall(r'\("(\w+)", "Courses/', f.read()))
    bonuses = {c["name"] for c in json.load(open(os.path.join(CONFIG, "courses.json"), encoding="utf-8-sig")) if c.get("name")}
    bad = []
    for g in goals:
        kind, _, arg = g.partition(":")
        if not (kind in ("start", "finish") and arg.isdigit() and 1 <= int(arg) <= 10
                or kind == "buy" and all(p in boxes for p in arg.split("+"))
                or kind == "reward" and arg in bonuses):
            bad.append(g)
    return bad

def any_plan(run_name, out, verbose):
    keep, base = any_prefix(run_name, verbose)
    t_dj = base.t
    print(f"ANY%: {run_name}'s {len(keep)} goals through buy:buydj, replayed: tick {t_dj} after reward:bonus1 (the run's"
          f" ~{BONUS1_TICK + t_dj}, {(BONUS1_TICK + t_dj) * 0.02 / 60:.1f} min), {base.cash:.4g} W, G {base.g},"
          f" global clone mult {base.glob['gm']}, max clone speed tier {base.glob['max_fast']}")
    print("Guesses (no routes): detours from the course's restart " + ", ".join(f"{g} {t}" for g, t in DETOUR.items())
          + f"; transits 3 -> 5 {TRANSIT[(3, 5)]}, 5 -> 4 {TRANSIT[(5, 4)]}")

    choice, end, tried = any_choose(base)
    print(f"Choice ({tried} plans simulated): {', '.join(g for g in ANY_OPTIONS if choice[g]) or 'no detour box'};"
          f" {choice['G5']} of Course 5's global cash boxes before the prestige box. The plan ends at tick {end}.")
    print(f"  Each box flipped (end tick; the greedy phases move a few hundred ticks either way on any change). One G more"
          f" pays {0.4 / (1 + 0.4 * base.g):.1%} more at G {base.g}, {0.4 / (1 + 0.4 * (base.g + 5)):.1%} at G {base.g + 5}:")
    for goal in ANY_OPTIONS:
        flipped = any_tail(base, dict(choice, **{goal: not choice[goal]}), True).t
        print(f"    {goal:18s} {'in ' if choice[goal] else 'out'}  detour {DETOUR[goal]:5d}  flipped {flipped:6d}"
              f" ({flipped - end:+6d})")
    print("    G5 (end tick): " + ", ".join(f"{g}: {any_tail(base, dict(choice, G5=g), True).t}" for g in range(5)))

    print("\nPlan after buy:buydj:")
    run = any_tail(base, choice, False, verbose)

    c4, c5 = run.cs[4], run.cs[5]
    t_brk, g_brk = run.breaker
    target = c4.boxes["V"].cost0
    r5, r4 = reward(c5.base, c5.rtier, 0, g_brk), reward(c4.base, c4.rtier, 0, g_brk)
    lap5, lap4 = LATE_LAP[5][1], LATE_LAP[4][1]
    g_max = g_brk + c5.cap("G") - c5.level["G"]
    r5max = reward(c5.base, c5.rtier, c5.cap("R"), g_max)
    roll = roll_mult(c5.cap("F"), c5.cap("B"), run.glob["max_fast"], run.glob["max_big"])
    rate5 = (r5max / (c5.lead + lap5)
             + c5.cap("C") * math.ceil(r5max * (0.1 + run.glob["gm"] * 0.2 + c5.cap("M") * 0.1)) * roll / lap5)
    post = [x for x in run.steps if x[1] >= t_brk]
    t_post = run.t - t_brk
    print(f"\nAfter the breaker (tick {t_brk}, G {g_brk}): a Course 5 lap pays {r5:.3g} W ({r5 / (c5.lead + lap5):.3g} W/tick"
          f" from a restart), a Course 4 lap {r4:.3g} W ({r4 / (c4.lead + lap4):.3g} W/tick), {r4 / r5:.2g} times as much;"
          f" Course 4's cheapest box then costs {min(c4.boxes[k].cost0 for k in 'RCMFB'):.3g} W.")
    print(f"  Course 5 to {target:.3g} W: even with every box at its cap from the start (G {g_max}, {rate5:.3g} W/tick)"
          f" >= {target / rate5:.3g} ticks = {target / rate5 * 0.02 / 3.15576e7:.3g} years.")
    print(f"  Course 4: {' + '.join(f'{x[0]} {x[2] - x[1]}' for x in post)} = {t_post} ticks ({t_post * 0.02:.1f} s) from the"
          f" breaker to buy:buyblockswap5. No Course 5 lap after the breaker: {c5.lead + lap5} ticks for {r5:.3g} W.")

    goals = keep + run.tokens
    print(f"\nANY% phases (ticks after reward:bonus1; the run's tick is {BONUS1_TICK} more):")
    print(f"  {'main through buy:buydj':44s} {0:6d} -> {t_dj:6d} {t_dj:6d} ticks {t_dj * 0.02:6.1f} s"
          f"  {base.cash:10.4g} W  {len(keep)} goals")
    for what, a, b, cash, n in run.steps:
        print(f"  {what:44s} {a:6d} -> {b:6d} {b - a:6d} ticks {(b - a) * 0.02:6.1f} s  {cash:10.4g} W  {n} goals")
    total = BONUS1_TICK + run.t
    print(f"ANY% ends at buy:buyblockswap5: tick {run.t} after reward:bonus1, the run's ~{total} ({total * 0.02:.1f} s,"
          f" {total * 0.02 / 60:.1f} min; estimates); {len(goals)} goals, {len(run.tokens)} after buy:buydj")
    bad = invalid_goals(goals)
    if bad:
        raise SystemExit(f"not Full-TAS goals: {bad}")
    print(json.dumps(run.tokens))
    if out:
        with open(out, "w", encoding="utf-8") as f:
            json.dump(goals, f, indent=2)
        print(f"wrote {len(goals)} goals to {out}")

def same_inputs(a, b):
    def norm(x):
        return {k: round(v, 4) if isinstance(v, float) else v for k, v in x.items()}
    return len(a) == len(b) and all(norm(x) == norm(y) for x, y in zip(a, b))

def measure(run_name):
    best_path = os.path.join(str(gamepaths.TRACES), "fulltas", run_name + ".json")
    trace_path = os.path.join(str(gamepaths.TRACES), f"fulltas-{run_name}.json")
    best = json.load(open(best_path, encoding="utf-8"))
    goals, ticks = best["goals"], best["goalPhysicsTicks"]
    runs = json.load(open(FULLTAS, encoding="utf-8"))
    run = next((r for r in runs if r.get("name") == run_name), None)
    if run is None or run["goals"] != goals:
        raise SystemExit(f"{best_path} is not solved for {run_name}'s current goals: solve it first")
    if "reward:bonus1" not in goals or "buy:buyds" not in goals:
        raise SystemExit(f"{run_name} has no reward:bonus1 and buy:buyds to plan between")
    b, d = goals.index("reward:bonus1"), goals.index("buy:buyds")
    if ticks[d] < 0:
        raise SystemExit(f"{run_name}'s best doesn't reach buy:buyds yet: its timings aren't measured")
    print(f"reading {trace_path} ...")
    trace = json.load(open(trace_path, encoding="utf-8"))
    if not same_inputs(trace["inputs"], best["inputs"]):
        raise SystemExit(f"{trace_path} isn't a trace of the best: Capture start in the Full-TAS tab, then rerun")
    B = ticks[b]
    at = trace["ticks"][B - 1]
    c2 = next({f[1]: f[2] for f in c["fields"]} for c in at["courses"]
              if any(f[1] == "courseNumber" and f[2] == 2 for f in c["fields"]))
    if c2["bestPathTime"] >= 1e7:
        raise SystemExit("Course 2 has no best run at reward:bonus1: the clones have no path")
    row = {"buy:" + t: k for k, t in TOKEN.items()}
    steps = [(goals[i], goals[i + 1], ticks[i + 1] - ticks[i]) for i in range(b + 1, d)]
    def typical(pairs, what):
        found = sorted(dt for x, y, dt in steps if pairs(x, y))
        if not found:
            raise SystemExit(f"{run_name}'s best between reward:bonus1 and buy:buyds has no {what} to time")
        return max(found, key=found.count)
    m = {"cash": int(at["cash"]), "arrive": ticks[goals.index("start:2", b)] - B,
         "lap": typical(lambda x, y: x == "start:2" and y == "finish:2", "Course 2 lap"),
         "lead": typical(lambda x, y: x != "start:2" and y == "start:2", "restart to Course 2's start gate"),
         "repeat": typical(lambda x, y: x in row and x == y, "repeat buy"),
         "clone_path": round(c2["bestPathTime"] / 0.02), "bonus1_tick": B}
    two = [dt for x, y, dt in steps if x in row and y in row and abs(ROW[row[x]] - ROW[row[y]]) == 2]
    m["row_step"] = max(min(two) - m["repeat"], 0) if two else ROW_STEP
    print(f"measured on {run_name}'s best: " + ", ".join(f"{k} {v}" for k, v in m.items()))
    return m

def write_run(run_name, goals):
    runs = json.load(open(FULLTAS, encoding="utf-8"))
    run = next((r for r in runs if r.get("name") == run_name), None)
    if run is None or "reward:bonus1" not in run["goals"]:
        raise SystemExit(f"fulltas.json has no run {run_name} with reward:bonus1")
    old = run["goals"]
    keep = old[:old.index("reward:bonus1") + 1]
    after = old.index("buy:buyds") + 1 if "buy:buyds" in old else len(old)
    rest = [] if old[after:after + len(TAIL)] == TAIL else old[after:]
    if rest:
        goals = goals[:goals.index("buy:buyds") + 1]
    run["goals"] = keep + goals + rest
    with open(FULLTAS, "w", encoding="utf-8") as f:
        json.dump(runs, f, indent=2)
    changed = next((i for i, (x, y) in enumerate(zip(old, run["goals"])) if x != y), min(len(old), len(run["goals"])))
    print(f"wrote {len(run['goals'])} goals ({len(keep)} kept before, {len(rest)} after buy:buyds) to {FULLTAS} run {run_name};"
          + (" unchanged" if old == run["goals"] else f" first change at goal {changed + 1}"))

def main():
    global LAP, LEAD, ARRIVE, CLONE_PATH, REPEAT, ROW_STEP, BONUS1_TICK
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", default="main", help="the fulltas.json run --measure reads, --write replaces, or --any-pct follows")
    ap.add_argument("--measure", action="store_true",
                    help="take cash, arrive, lap, lead, repeat, row step and clone path from --run's offline best and its trace")
    ap.add_argument("--cash", type=int, default=CASH, help="W at reward:bonus1")
    ap.add_argument("--arrive", type=int, default=ARRIVE, help="ticks from reward:bonus1 to Course 2's start gate")
    ap.add_argument("--lap", type=int, default=LAP, help="Course 2 start gate -> finish, ticks")
    ap.add_argument("--lead", type=int, default=LEAD, help="restart -> Course 2's start gate, ticks")
    ap.add_argument("--clone-path", type=int, default=CLONE_PATH, help="Course 2's best run (game timer) in ticks: the clones' path")
    ap.add_argument("--write", action="store_true",
                    help="replace --run's goals after reward:bonus1 in fulltas.json (through buy:buyds when goals of its own follow)")
    ap.add_argument("--verbose", action="store_true", help="print every step after dash, not only each phase's end")
    ap.add_argument("--any-pct", action="store_true", help="plan ANY%%: --run's goals through buy:buydj, then its own")
    ap.add_argument("--out", help="--any-pct: write the whole goal list (JSON) here")
    ap.add_argument("--detour", action="append", default=[], metavar="GOAL=TICKS",
                    help="--any-pct: ticks from the course's restart to an optional box (DETOUR), e.g. reward:bonus4=150")
    ap.add_argument("--transit", action="append", default=[], metavar="A-B=TICKS",
                    help="ticks from course A to course B's start gate (TRANSIT), e.g. 5-4=2400")
    a = ap.parse_args()
    LAP, LEAD, ARRIVE, CLONE_PATH = a.lap, a.lead, a.arrive, a.clone_path
    if a.measure:
        m = measure(a.run)
        a.cash, ARRIVE, LAP, LEAD, CLONE_PATH = m["cash"], m["arrive"], m["lap"], m["lead"], m["clone_path"]
        REPEAT, ROW_STEP, BONUS1_TICK = m["repeat"], m["row_step"], m["bonus1_tick"]
    for d in a.detour:
        goal, _, ticks = d.rpartition("=")
        if goal not in DETOUR:
            raise SystemExit(f"--detour {d}: goals are {', '.join(DETOUR)}")
        DETOUR[goal] = int(ticks)
    for d in a.transit:
        pair, _, ticks = d.partition("=")
        x, _, y = pair.partition("-")
        if (int(x), int(y)) not in TRANSIT:
            raise SystemExit(f"--transit {d}: transits are {', '.join(f'{p}-{q}' for p, q in TRANSIT)}")
        TRANSIT[(int(x), int(y))] = int(ticks)
    if a.any_pct:
        if a.write:
            raise SystemExit("--any-pct writes its goals to --out; the run goes into fulltas.json by hand")
        any_plan(a.run, a.out, a.verbose)
        return

    print("Formulas (cross-check with Economy.UpdateReward / DestroyClone / SpawnClone):")
    print("  Course 2 reward, n cashPerLoop buys x G global:", {g: [reward(4, 2, n, g) for n in range(5)] for g in (0, 1, 2, 3)})
    print("  Course 1 reward (20 buys) x G:", [reward(1, 0, C1_BUYS, g) for g in range(4)],
          " its 9 clones off screen per 51 ticks:", [offscreen_pay(reward(1, 0, C1_BUYS, g), 0, C1_CLONES, C1_PATH) for g in range(4)])
    print("  Course 2 clone pay at reward 560 x mult buys:", [clone_pay(560, m) for m in range(5)])
    print(f"Start: reward:bonus1 with {a.cash} W, G {GLOBAL0}; Course 2 at its start gate {ARRIVE} ticks later"
          f" (lap {LAP}, lead {LEAD}, repeat buy {REPEAT} + {ROW_STEP} per extra row, clone path {CLONE_PATH})")

    s0 = start_state(a.cash)
    dash, expanded = search(s0)
    rows = []
    s = dash
    while s is not None:
        rows.append(s)
        s = s.prev
    rows.reverse()
    tokens = [t for r in rows for t in r.tokens]
    laps = 0
    for r in rows:
        if r.what == "lap":
            laps += 1
        print(f"  tick {r.t:5d}  {r.what:44s} -> {r.cash:9.0f} W  reward {r.reward2():6d}  clones {r.N:2d}"
              f"  mult {mult(r.m):.1f}  global {GLOBAL0 + r.g}")
    buys = {b: sum(1 for t in tokens if t == "buy:" + TOKEN[b]) for b in "RCMG"}
    print(f"dash bought at tick {dash.t} after reward:bonus1 ({dash.t * 0.02:.2f} s, estimates; {expanded} states searched):"
          f" {laps} laps, buys {buys}; the run's tick ~{BONUS1_TICK + dash.t} ({(BONUS1_TICK + dash.t) * 0.02:.2f} s)")
    late, end = late_plan(dash, a.verbose)
    print(f"block swap bought at tick {end} after reward:bonus1; the run's tick ~{BONUS1_TICK + end}"
          f" ({(BONUS1_TICK + end) * 0.02 / 60:.1f} min, estimates); {len(late)} goals after TAIL")

    mid = rows[len(rows) // 2]
    c2_lap_rate = mid.reward2() / (LEAD + LAP)
    c2_rate = c2_lap_rate + mid.N * mid.pay2() / mid.P
    c1_lap_rate = reward(1, 0, C1_BUYS, GLOBAL0 + mid.g) / (C1_PATH + 26)
    c1_max = offscreen_pay(reward(1, 0, 40, GLOBAL0 + 2), 0, 35, C1_PATH) / OFFSCREEN
    print(f"Course 1 check at tick {mid.t}: a Course 1 lap earns {c1_lap_rate:.2f} W/tick against Course 2's {c2_lap_rate:.1f}"
          f" (plus {c2_rate - c2_lap_rate:.0f} from its clones); Course 1 maxed (40 reward buys, 35 clones, G 3) would pay"
          f" {c1_max:.1f} W/tick off screen, for ~1500 ticks of 1to2 both ways: never worth it.")

    goals = tokens + TAIL + late
    print(json.dumps(goals))
    if a.write:
        write_run(a.run, goals)

if __name__ == "__main__":
    main()
