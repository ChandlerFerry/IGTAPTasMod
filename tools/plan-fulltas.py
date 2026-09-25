"""Plans Full-TAS run `main`'s Course 2 economy: the goals after reward:bonus1 up to buying dash (3 MW), then the rest.

    python tools/plan-fulltas.py [--cash W] [--arrive T] [--lap T] [--lead T] [--first-lap T] [--run main] [--write]

Game facts (obj/fulldecomp, obj/decomp/globalStats.cs; enginesim/Runtime/Economy.cs models the same):
- A course's reward (courseScript.UpdateReward, Economy.UpdateReward), in this double order:
  ceil(base * 1.0 * (1 + G * 40/100) * (1 + n * 100/100) * 1.0 * 1.0 * gameSpeed 1 * vmanMult 1 * 10^rewardTier),
  G = global cashPerLoop buys (globalUpgradeEffectDict: cashPerLoop 40), n = the course's cashPerLoop buys (their effect is
  globalUpgradeEffectDict[moreWatts] = 100 %). Course 1: base 1, tier 0. Course 2: base 4, tier 2.
- A finish pays the reward (stopTracking with the end gate's isEndOfCourse: course 2's RealEnd at (4810, 2474); End (1)
  and End (2), the latter on the reset point, cancel a run without paying).
- A clone on screen pays ceil(reward * (0.1 + 0.1 * clone multiplier buys)) each time it walks the course's best run
  (one path point per tick: P ticks); clones spawn every P / N (clonesScript, Economy.Step/DestroyClone). Off screen a
  course pays ceil(reward * mult / (pathSeconds / N)) every 51 ticks (SpawnClone's offscreen branch, 1 s in float steps).
  Course 2's clone area (x 3566..8066) covers the course and the box row except the dash box; Course 1's is far away,
  so its clones pay off screen the whole plan.
- A box costs ceil((cost + add) * scale) after each buy (upgradeBox.ScaleBoxCost; the global box: round(cost * scale)).
  Course 2's row at y 2143: reward (x 4199) 400 x1.14 cap 45, clone (4019) 800 x1.21 cap 35, clone multiplier (3839)
  3800 +1000 x2 cap 10, global reward (3659, the GLOBALcashPerLoop box with sibling index 3; +1 G, raising every
  course's reward) 55000 x5 cap 2, dash (3479) 3000000.
- bonus1's reward box (the other GLOBALcashPerLoop, sibling 5, cost 0) is the reward:bonus1 goal: G = 1 when the plan
  starts; Course 2 pays 560 from then on (400 before it).

Where the player is: a quick restart goes to the course's reset point (4775, 1884) while its timer runs, else to the
last checkpoint, which for Course 2 is its Checkpoint (4) at (4783, 1878) (the course passes no other while timing);
either way it lands in End (2), which cancels a running timer, so the next start gate crossing is a start:2 goal again.

Plan start (t = 0): the tick reward:bonus1 is reached. Then the route quick-restarts (to bonus1's checkpoint) and plays
bonus1to2 to Course 2's start gate (--arrive ticks, the solved bonus1to2 route's 8.36), which is start:2.
From there each step is one of: a Course 2 lap (from the gate: --lap; else restart, --lead to the gate, --lap), or a
purchase: restart and walk to the box, or walk along the row from the box the player stands under, then stand under
it bumping until the clones cover its cost (a wait that ends no sooner than a lap and the walk from the restart would
is dropped: that way has more cash). A search in time order tries all of these from every state; a state goes on only
if no earlier one with the same or one more level of one box (and the same or faster clone path, the same place) would
have as much cash by then standing still (clone phases are ignored in that test). The first state to buy dash is the
plan (~30 s).

Estimates (physics ticks): walks = (distance - 50 px) / 9.2 px per tick from the restart, a box to its neighbour
5 + 180 / 9.2, another bump at the same box 23 (Course 1's measured buys fit these; a solved first Course 2 reward buy
is 57 from the restart). A new clone first pays one path length after it is bought. Course 1's clones pay at the
reward they were captured with (C1_GLOBAL). Course 2's clones pay on screen at the dash box too (off screen it is 50/51
of that). Replace the estimates with solved times as they come.

Output: the goal tokens that follow reward:bonus1 (start:2 first), the estimated times, and `--write` replaces the
run's goals after reward:bonus1 in fulltas.json (everything up to and including it kept), ending with the tail after
dash (TAIL).
"""
import argparse
import bisect
import functools
import heapq
import itertools
import json
import math
import os
import struct

CONFIG = os.environ.get("IGTAP_CONFIG", r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod")
FULLTAS = os.path.join(CONFIG, "fulltas.json")

# ---- The state main arrives with (fulltas-main trace and enginesim/fulltas/main.json, the offline best).
C1_BUYS = 20        # Course 1 cashPerLoop buys (reward 21 in the trace)
C1_CLONES = 9       # Course 1 clones (off screen: 11 W per 51 ticks in the trace)
C1_PATH = 93        # Course 1's best run in ticks, its clones' path (1.86 s, the lap after wall jump)
FIRST_LAP = 327     # Course 2's best run so far: main's first Course 2 lap (6.54 s), its clones' path until a faster lap
# Cash at reward:bonus1: 713 W right after main's finish:2 (trace), then 2tobonus1 (490) + bonus1 (406) with Course 1's
# clones paying 11 W every 51 ticks.
CASH = 713 + (490 + 406) // 51 * 11
ARRIVE = 418        # restart after bonus1 -> Course 2's start gate (wj/bonus1to2_WJ 8.36)
BONUS1_TICK = 8182  # the run's physics tick at reward:bonus1: main's route touches bonus1's rect at 8181 (finish:2 7285)
GLOBAL0 = 1         # G after reward:bonus1 (bonus1's free GLOBALcashPerLoop box)
# Course 1's reward stays 21 the whole plan: a global box updates only its own course's reward (bonus1's box and the
# 55000 W one: course 2); other courses update on their 5 s UpdateReward repeat, which a turbo run (one frame) never
# reaches, or when their clone area goes on or off screen (Course 1's reaches x 3547: only the dash box is in it).
C1_GLOBAL = 0

# ---- Course 2.
LAP = 128           # start gate -> RealEnd: the best Course 2 run (2_WJ 2.56)
LEAD = 31           # restart -> start gate (267 px)
RESET_X = 4783
BOX_X = {"R": 4199, "C": 4019, "M": 3839, "G": 3659, "D": 3479}
TO = {b: round((RESET_X - x - 50) / 9.2) for b, x in BOX_X.items()}  # restart -> first bump at the box
ROW = {"D": 0, "G": 1, "M": 2, "C": 3, "R": 4}  # 180 px apart
REPEAT = 23         # another bump at the same box
CAP = {"R": 45, "C": 35, "M": 10, "G": 2, "D": 1}
TOKEN = {"R": "buyreward2", "C": "buyclone2", "M": "buyclonemult2", "G": "buyglobalreward2", "D": "buydash"}
OFFSCREEN = 51      # ticks between off-screen clone pays
TAIL = ["start:2", "finish:2", "reward:bonus2", "start:3", "reward:bonus3", "start:3", "finish:3"]


def f32(x):
    return struct.unpack("f", struct.pack("f", x))[0]


@functools.lru_cache(maxsize=None)
def reward(base, tier, n, g):
    """courseScript.UpdateReward with only cashPerLoop upgrades (global g, local n), in the game's double order."""
    r = base * (1.0 + 0.0 * (50.0 / 100.0) + 0.0 * (100.0 / 100.0)) * (1.0 + g * (40.0 / 100.0)) * (1.0 + n * (100.0 / 100.0))
    r = r * (1.0 + 0.0) * (1.0 + 0.0 * (100.0 / 100.0)) * 1.0 * 1.0 * math.pow(10.0, tier)
    return math.ceil(math.pow(math.ceil(r), 1.0 + 0.0))


def mult(m):
    """clonesScript's clone multiplier: 0.1 + global cloneMult * 0.2 + local cloneMult * 0.1."""
    return 0.1 + 0.0 * 0.2 + m * 0.1


@functools.lru_cache(maxsize=None)
def clone_pay(r, m):
    """DestroyClone: ceil(reward * (bigness * 2 + 1) * mult * boost), bigness 0, no comp boost."""
    return math.ceil(r * 1.0 * mult(m) * 1.0)


def offscreen_pay(r, m, clones, path_ticks):
    """SpawnClone off screen: ceil(reward * mult / nextOffscreenCloneDivisor), the divisor = (pathSeconds / N) in float."""
    if clones == 0:
        return 0
    return math.ceil(r * 1.0 * mult(m) * 1.0 * 1.0 / f32(f32(f32(path_ticks * 0.02) / clones) / 1.0))


@functools.lru_cache(maxsize=None)
def box_cost(b, k):
    """Course 2 box b's cost after k buys (upgradeBox.ScaleBoxCost; the global box's DoBuyUpgrade branch rounds)."""
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


# What Course 1's clones pay every 51 ticks the whole plan (11 W).
C1_PAY = offscreen_pay(reward(1, 0, C1_BUYS, C1_GLOBAL), 0, C1_CLONES, C1_PATH)


def move(a, b):
    """Box a -> box b along the row, bump included."""
    k = abs(ROW[a] - ROW[b])
    return REPEAT if k == 0 else round(5 + 180 * k / 9.2)


class State:
    __slots__ = ("t", "cash", "n", "N", "m", "g", "P", "clones", "c1next", "where", "prev", "tokens", "what")

    def child(self):
        s = State.__new__(State)
        s.t, s.cash, s.n, s.N, s.m, s.g, s.P = self.t, self.cash, self.n, self.N, self.m, self.g, self.P
        s.clones, s.c1next, s.where = self.clones, self.c1next, self.where
        s.prev, s.tokens, s.what = self, (), ""
        return s

    def level(self, b):
        return {"R": self.n, "C": self.N, "M": self.m, "G": self.g, "D": 0}[b]

    def reward2(self):
        return reward(4, 2, self.n, GLOBAL0 + self.g)

    def pay2(self):
        return clone_pay(self.reward2(), self.m)

    def pay1(self):
        return C1_PAY

    def rate(self):
        """W per tick the clones pay once they all walk (Course 2's on screen, Course 1's off screen)."""
        return len(self.clones) * self.pay2() / self.P + C1_PAY / OFFSCREEN

    def income(self, T):
        """W the clones pay after tick t up to tick T (clones: each one's next pay tick, sorted)."""
        w = ((T - self.c1next) // OFFSCREEN + 1) * C1_PAY if self.c1next <= T else 0
        c, P = self.clones, self.P
        if not c or c[0] > T:
            return w
        if c[-1] - c[0] < P:
            # All within one path length of the first: each pays q or q + 1 times.
            q, rem = divmod(T - c[0], P)
            k = len(c) * q + bisect.bisect_right(c, c[0] + rem)
        else:
            k = sum((T - a) // P + 1 for a in c if a <= T)
        return w + k * self.pay2()

    def advance(self, T):
        self.cash += self.income(T)
        if self.c1next <= T:
            self.c1next += ((T - self.c1next) // OFFSCREEN + 1) * OFFSCREEN
        self.clones = tuple(sorted(a if a > T else a + ((T - a) // self.P + 1) * self.P for a in self.clones))
        self.t = T

    def wait_for(self, cost, deadline):
        """Stand under the box until the cash covers `cost`; False when that takes until `deadline` or nothing pays."""
        need = cost - self.cash
        if need <= 0:
            return self.t < deadline
        if self.income(deadline - 1) < need:
            return False
        lo, hi = self.t, deadline - 1
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if self.income(mid) >= need:
                hi = mid
            else:
                lo = mid
        self.advance(hi)
        return True

    def lap(self):
        s = self.child()
        s.advance(s.t + (LAP if s.where == "gate" else LEAD + LAP))
        s.cash += s.reward2()
        s.P = min(s.P, LAP)
        s.tokens = ("finish:2",) if s.where == "gate" else ("start:2", "finish:2")
        s.what = "lap"
        s.where = "done"
        return s

    def buy(self, b, walk_row):
        """Buy b: walk along the row from the box the player is under (walk_row), else restart and walk. None when the
        purchase would be no sooner than after a lap and the walk from the restart: that one has more cash then."""
        if self.level(b) >= CAP[b]:
            return None
        deadline = self.t + (LAP if self.where == "gate" else LEAD + LAP) + TO[b]
        s = self.child()
        s.advance(s.t + (move(s.where, b) if walk_row else TO[b]))
        arrived = s.t
        cost = box_cost(b, s.level(b))
        if not s.wait_for(cost, deadline):
            return None
        s.cash -= cost
        if b == "R":
            s.n += 1
        elif b == "C":
            s.N += 1
            s.clones = s.clones + (s.t + s.P,)
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
    s.t, s.cash, s.n, s.N, s.m, s.g, s.P = 0, cash, 0, 0, 0, 0, FIRST_LAP
    s.clones, s.c1next, s.where = (), OFFSCREEN // 2, "gate"
    s.prev, s.tokens, s.what = None, (), ""
    s.advance(ARRIVE)
    s.tokens, s.what = ("start:2",), "restart, bonus1to2 to Course 2's start gate"
    return s


def dominated(best, s):
    """Whether an earlier state does at least as well as s: the same or one more level of one box, the same or the fast
    clone path, the same place or (s after a lap) a box, and as much cash by s's tick if it stood still meanwhile (it can
    then do whatever s does, skipping the purchase it is ahead by)."""
    levels = (s.n, s.N, s.m, s.g)
    paths = (s.P,) if s.P == LAP else (s.P, LAP)
    places = (s.where,) + (tuple(ROW) if s.where == "done" else ())
    for i in range(-1, 4):
        up = levels if i < 0 else levels[:i] + (levels[i] + 1,) + levels[i + 1:]
        for p in paths:
            for w in places:
                e = best.get(up + (p, w))
                if e is not None and e[0] + e[1] * s.t >= s.cash:
                    return True
    return False


def search(s0):
    """States in time order; the first to buy dash is the earliest. Returns it and the states expanded. Standing still
    is always possible, so a state goes on only when no earlier one dominates it (dominated). Each key keeps its latest
    state as (cash - rate * t, rate): the cash it projects to any later tick."""
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
        best[(s.n, s.N, s.m, s.g, s.P, s.where)] = (s.cash - rate * s.t, rate)
        expanded += 1
        options = [s.lap()]
        for b in "RCMGD":
            options.append(s.buy(b, False))
            if s.where in ROW:
                options.append(s.buy(b, True))
        for x in options:
            if x is not None:
                heapq.heappush(heap, (x.t, next(order), x))
    raise SystemExit("dash is never affordable")


def main():
    global LAP, LEAD, ARRIVE, FIRST_LAP
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", default="main")
    ap.add_argument("--cash", type=int, default=CASH, help="W at reward:bonus1")
    ap.add_argument("--arrive", type=int, default=ARRIVE, help="ticks from reward:bonus1 to Course 2's start gate")
    ap.add_argument("--lap", type=int, default=LAP, help="Course 2 start gate -> finish, ticks")
    ap.add_argument("--lead", type=int, default=LEAD, help="restart -> Course 2's start gate, ticks")
    ap.add_argument("--first-lap", type=int, default=FIRST_LAP, help="Course 2's best run in ticks when the plan starts")
    ap.add_argument("--write", action="store_true", help="replace the run's goals after reward:bonus1 in fulltas.json")
    a = ap.parse_args()
    LAP, LEAD, ARRIVE, FIRST_LAP = a.lap, a.lead, a.arrive, a.first_lap

    print("Formulas (cross-check with Economy.UpdateReward / DestroyClone / SpawnClone):")
    print("  Course 2 reward, n cashPerLoop buys x G global:", {g: [reward(4, 2, n, g) for n in range(5)] for g in (0, 1, 2, 3)})
    print("  Course 1 reward (20 buys) x G:", [reward(1, 0, C1_BUYS, g) for g in range(4)],
          " its 9 clones off screen per 51 ticks:", [offscreen_pay(reward(1, 0, C1_BUYS, g), 0, C1_CLONES, C1_PATH) for g in range(4)])
    print("  Course 2 clone pay at reward 560 x mult buys:", [clone_pay(560, m) for m in range(5)])
    print(f"Start: reward:bonus1 with {a.cash} W, G {GLOBAL0}; Course 2 at its start gate {ARRIVE} ticks later"
          f" (lap {LAP}, lead {LEAD}, first clone path {FIRST_LAP})")

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
              f"  mult {mult(r.m):.1f}  global {GLOBAL0 + r.g}  path {r.P}")
    buys = {b: sum(1 for t in tokens if t == "buy:" + TOKEN[b]) for b in "RCMG"}
    print(f"dash bought at tick {dash.t} after reward:bonus1 ({dash.t * 0.02:.2f} s, estimates; {expanded} states searched):"
          f" {laps} laps, buys {buys}; the run's tick ~{BONUS1_TICK + dash.t} ({(BONUS1_TICK + dash.t) * 0.02:.2f} s)")

    # Course 1 against Course 2 while the plan runs: its laps and its maxed clones.
    mid = rows[len(rows) // 2]
    c2_lap_rate = mid.reward2() / (LEAD + LAP)
    c2_rate = c2_lap_rate + mid.N * mid.pay2() / mid.P
    c1_lap_rate = reward(1, 0, C1_BUYS, GLOBAL0 + mid.g) / 127
    c1_max = offscreen_pay(reward(1, 0, 40, GLOBAL0 + 2), 0, 35, C1_PATH) / OFFSCREEN
    print(f"Course 1 check at tick {mid.t}: a Course 1 lap earns {c1_lap_rate:.2f} W/tick against Course 2's {c2_lap_rate:.1f}"
          f" (plus {c2_rate - c2_lap_rate:.0f} from its clones); Course 1 maxed (40 reward buys, 35 clones, G 3) would pay"
          f" {c1_max:.1f} W/tick off screen, for ~1500 ticks of 1to2 both ways: never worth it.")

    goals = tokens + TAIL
    print(json.dumps(goals))
    if a.write:
        runs = json.load(open(FULLTAS, encoding="utf-8"))
        run = next((r for r in runs if r.get("name") == a.run), None)
        if run is None or "reward:bonus1" not in run["goals"]:
            raise SystemExit(f"fulltas.json has no run {a.run} with reward:bonus1")
        keep = run["goals"][:run["goals"].index("reward:bonus1") + 1]
        run["goals"] = keep + goals
        with open(FULLTAS, "w", encoding="utf-8") as f:
            json.dump(runs, f, indent=2)
        print(f"wrote {len(run['goals'])} goals ({len(keep)} kept) to {FULLTAS} run {a.run}")


if __name__ == "__main__":
    main()
