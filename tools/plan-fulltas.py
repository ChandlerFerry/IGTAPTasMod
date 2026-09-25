"""Plans Full-TAS run `main`'s economy after reward:bonus1: Course 2 up to buying dash (3 MW), the bonuses dash opens
(TAIL), then Course 3 to the statue, Courses 1 and 2 bought out, Course 3 to air jump, and Course 4's prestige box and
block swap. With --any-pct, run ANY% instead (below).

    python tools/plan-fulltas.py [--cash W] [--arrive T] [--lap T] [--lead T] [--first-lap T] [--run main] [--verbose]
                                 [--write]
    python tools/plan-fulltas.py --any-pct [--run main] [--out FILE] [--detour GOAL=T ...] [--transit A-B=T ...]
                                 [--verbose]

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

After dash (late_plan), phases on one course each, as the run is to go: Course 3 until "global clone speed" (the
GLOBALclonespeedmax box: +1 max clone speed tier) and "Awaken the statue's power" (enablePrestige: +1 global clone
multiplier, then BoostOldCourses: Courses 1 and 2 get reward tier Course 3's tier + their RewardPrestigeMod, 6.2 and
5.7, and x1.5 caps); Course 1 and Course 2 each bought to their new caps with a lap on each (a dash lap: a shorter clone
path); Course 3 until the air jump box (5.5e11 W); Course 4 lapped once and its "Boost all previous courses" box
(1.2e15 W: Courses 1-3 get tier 10 + their mod), then its block swap box (5e17 W). Course 3 may also buy its clone
speed chance, Course 4 its speed and size chances and max size tier: the plan counts their rolls at their average
(roll_mult; the run's own draws come from the seeded RNG, docs/fulltas.md). Each phase is planned greedily
(late_greedy); the other courses' clones pay at their last updated reward (their clone area going on or off screen,
their own purchases, a boost), and the boosts reach them at once. Never bought: the free global boxes nobody has a
route to yet (Course 3's GLOBALWatt at (6350, 6245); Course 4's at (15277, 11959), (10032, 14487), (-324, 14487)),
nor Course 4's 3e12 W global box up at (20731, 15314). Transitions 3 -> 1 and 3 -> 4 are guesses (TRANSIT).

Output: the goal tokens that follow reward:bonus1 (start:2 first), the estimated times, and `--write` replaces the
run's goals after reward:bonus1 in fulltas.json (everything up to and including it kept): Course 2's plan, TAIL, the
late phases.

ANY% (--any-pct): --run's goals in fulltas.json through its first buy:buyaj, replayed through the same model (each
step the soonest way to the next goal) for the state there, then: the optional global cashPerLoop boxes (ANY_OPTIONS,
+1 G each; free ones as reward:bonusN), 3 -> 5, Course 5 farmed to its prestige box (3.16e22 W; its "global cash "
boxes, cap 4, too), the trip breaker (Late.trip_breaker: Courses 1-5 reset, Cash 0; G stays), 5 -> 4, and Course 4
farmed to Course 5's SwapBlocksOnDash box on Course 4's row (9.49e34 W), the last goal. After the breaker Course 4's
tiers are 29.5 and Course 5's 16.5, so Course 4 is the farm (the tool prints the comparison). Which optional boxes:
any_choose simulates the plan with each one in or out (and 0-4 Course 5 global boxes before the prestige box) and
keeps the fastest. The phases plan greedily with laps counted as income (late_greedy lap_income). Guesses: the
detours (DETOUR: no bonus route exists) and transits 3 -> 5 and 5 -> 4 (TRANSIT); the block swap the breaker does
(blue blocks off, orange on, near Course 4 too) may change Course 4's lap and the way there. Output: the phases' ticks
and cash, the goals after buy:buyaj, and the whole goal list as JSON in --out.
"""
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
FOREVER = 10 ** 8   # a deadline no replayed purchase waits until


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

    def buy(self, b, walk_row, force=False):
        """Buy b: walk along the row from the box the player is under (walk_row), else restart and walk. None when the
        purchase would be no sooner than after a lap and the walk from the restart: that one has more cash then (unless
        force: replaying given goals)."""
        if self.level(b) >= CAP[b]:
            return None
        deadline = self.t + (FOREVER if force else (LAP if self.where == "gate" else LEAD + LAP) + TO[b])
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
        for x in stage1_options(s):
            heapq.heappush(heap, (x.t, next(order), x))
    raise SystemExit("dash is never affordable")


def stage1_options(s, force=False):
    """What a Course 2 state can do next: a lap, or buy a box (restart and walk, or along the row from the box it's under)."""
    options = [s.lap()]
    for b in "RCMGD":
        options.append(s.buy(b, False, force))
        if s.where in ROW:
            options.append(s.buy(b, True, force))
    return [x for x in options if x is not None]


def replay(s, goals, options):
    """Follows given goals from state s: each step is the soonest of options(s) whose goals come next (options(s, True)
    when none does: a purchase then waits as long as it takes)."""
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


# ======== After dash: Course 3 to the statue, Courses 1 and 2 bought out, Course 3 to air jump, Course 4's prestige
# and block swap (the goals after TAIL). Estimates in physics ticks; replace them with solved times as they come.

# A course's best lap (start gate -> RealEnd) with dash, and with dash + air jump: the saved category routes.
LATE_LAP = {1: (53, 47),     # 1_WJDASH 1.06, 1_WJ_DASH_AJ 0.94
            2: (89, 78),     # 2_WJDASH 1.78, 2_WJ_DASH_AJ 1.56
            3: (233, 217),   # 3_WJDASH 4.66, 3_WJ_DASH_AJ 4.34
            4: (751, 751),   # 4_WJ_DASH_AJ 15.02 (Course 4 needs air jump)
            5: (657, 657)}   # 5_WJ_DASH_AJ 13.14 (Course 5 too)
# TAIL with dash, step by step: (what, ticks, G it adds). 2tobonus2_WJDASH 6.96 + bonus2_WJDASH 1.06,
# bonus2to3_WJDASH 3.78, bonus3_WJDASH 3.40, then a restart and a Course 3 lap.
TAIL_STEPS = [("restart, Course 2 lap", LEAD + 89, 0), ("2tobonus2 + bonus2", 348 + 53, 1),
              ("bonus2to3 to Course 3's gate", 189, 0), ("bonus3", 170, 1), ("restart, Course 3 lap", 29 + 233, 0)]
# Course to course, ending at the next course's start gate (its start:N). 1to2_WJDASH 4.84 and 2tobonus2 + bonus2to3
# (10.74) are saved routes; 3 -> 1 (down through the 2-to-3 zone and 1to2 backwards) and 3 -> 4 have none yet, nor
# ANY%'s 3 -> 5 (3 -> 1, then ~5750 px left past Course 1 to Course 5's start gate at (-3516, -220): no 1 -> 5 or 4 -> 5
# route exists) and 5 -> 4 (5 -> 1 as long as 1 -> 5, then 1 -> 2, 2 -> 3, 3 -> 4).
TRANSIT = {(3, 1): 600, (1, 2): 242, (2, 3): 537, (3, 4): 1200, (3, 5): 600 + 620, (5, 4): 620 + 242 + 537 + 1200}


def guess_ticks(frm, to):
    """A route-less guess from a restart point to a box's buy trigger (157 px under the box): 9.2 px per tick across,
    6 up (wall jumps), 20 down, plus 20."""
    dx, dy = to[0] - frm[0], to[1] - frm[1]
    return round(20 + abs(dx) / 9.2 + max(dy, 0) / 6 + max(-dy, 0) / 20)


# ANY%'s optional global cashPerLoop boxes away from the rows (G + 1 each, kept through the trip breaker): ticks from
# their course's restart point (Course 3 (9505, 6971), 4 (20201, 10556), 5 (-3282, -294)) to the buy, the way back a
# restart. No route exists for any of them: guess_ticks until one does (--detour GOAL=TICKS overrides).
DETOUR = {"reward:bonus5": guess_ticks((9505, 6971), (6350, 6088)),       # Course 3's GLOBALWatt, free
          "reward:bonus4": guess_ticks((-3282, -294), (-3104, -2102)),    # Course 5's GLOBALCash (1), free
          "reward:bonus15": guess_ticks((-3282, -294), (-8477, 2532)),    # Course 5's GLOBALCash #9, free
          "buyfree5": guess_ticks((-3282, -294), (-3979, 4051)),          # Course 5's GLOBALCash #8, 3.16e19 W
          "buyglobalreward4": guess_ticks((20201, 10556), (20731, 15157)),  # Course 4's 3e12 W one (not reset)
          "reward:bonus7": guess_ticks((20201, 10556), (15277, 11802)),   # Course 4's free ones
          "reward:bonus6": guess_ticks((20201, 10556), (10032, 14330)),
          "reward:bonus16": guess_ticks((20201, 10556), (-325, 14330))}


@functools.lru_cache(maxsize=None)
def roll_mult(fast, big, max_fast, max_big):
    """What a clone earns on average against a plain one: E[(2j + 1) * (1 + 0.35 i) * 0.75^j] over clonesScript.SpawnClone's
    two roll loops. Each loop draws Random.Range(0, 101) and goes up a tier while the draw is below chance / (1 + tier)
    and below 50 + 5 * max tiers, at most 1 + max tiers times (chance: 5 per local chance buy; no global chance box is in
    the plan). A size tier pays 2j + 1 times and walks 0.75^j as fast; a speed tier walks 1 + 0.35 i as fast (on screen
    it finishes the path sooner, off screen its spawn interval, the pay's divisor, is shorter by as much)."""
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
    """An upgrade box of the plan: its goal token, x on its course's row, cost (world export: tier applied) and scaling,
    base cap, and what it does: R cashPerLoop, C cloneCount, M cloneMult, F / B clone speed / size chance, G global
    cashPerLoop (round), X anything else bought once (clone speed and size max tiers, statue, air jump, prestige, block
    swap)."""

    def __init__(self, token, x, cost, scale=1.0, add=0.0, cap=1, kind="X", off=None):
        """off: a box away from the row, the ticks from the course's restart to its bump (the way back: a restart)."""
        self.token, self.x, self.cost0, self.scale, self.add, self.cap0, self.kind = token, x, cost, scale, add, cap, kind
        self.goal = token if ":" in token else "buy:" + token
        self.off = off
        self._costs = [cost]

    def retier(self, dtier):
        """upgradeBox.onPrestige: TimesUsed 0, cost = base * 10^(the course's new tier + TierMod) (TierMod 0 here)."""
        self.cost0 *= math.pow(10.0, dtier)
        self._costs = [self.cost0]

    def cost(self, k):
        while len(self._costs) <= k:
            c = self._costs[-1] + self.add
            # upgradeBox.ScaleBoxCost (ceil) or DoBuyUpgrade's global branch (round); power scale 1 on every box here.
            self._costs.append(round(c * self.scale) if self.kind == "G" else math.ceil(math.ceil(c * self.scale)))
        return self._costs[k]


class Course:
    """A course's economy between phases: its boxes' levels and caps, reward tier, clone path and reward (as last
    updated: UpdateReward runs on its own purchases, its clone area going on or off screen, and prestige boosts)."""

    def __init__(self, number, base, tier, mod, reset_x, lead, boxes, breaker=(0.0, 0.0)):
        self.number, self.base, self.tier, self.rtier, self.mod, self.reset_x, self.lead = number, base, tier, tier, mod, reset_x, lead
        # courseScript.BreakerLayerTierMod, BreakerLayerRewardTierMod (the trip breaker's reset of Courses 1 to 3).
        self.breaker = breaker
        self.boxes = boxes
        self.level = {k: 0 for k in boxes}
        self.capmult = 1.0
        self.P = None
        self.reward = 0

    def cap(self, k):
        b = self.boxes[k]
        # BoostOldCourses: baseCapMult += 0.5, Cap = math.round(baseCap * baseCapMult) (banker's, like Python's round).
        return round(b.cap0 * self.capmult) if b.kind in "RCMFB" else b.cap0

    def update_reward(self, g):
        self.reward = reward(self.base, self.rtier, self.level.get("R", 0), g)

    def rate(self, glob):
        """W per tick its clones pay (on or off screen alike: N clones per path of P ticks, times the rolls' average)."""
        n = self.level.get("C", 0)
        if not n or not self.P:
            return 0.0
        roll = roll_mult(self.level.get("F", 0), self.level.get("B", 0), glob["max_fast"], glob["max_big"])
        return n * math.ceil(self.reward * (0.1 + glob["gm"] * 0.2 + self.level.get("M", 0) * 0.1)) * roll / self.P

    def on_row(self, k):
        return k in self.boxes and self.boxes[k].off is None

    def to(self, k):
        """Restart -> first bump at box k."""
        b = self.boxes[k]
        return b.off if b.off is not None else round((abs(self.reset_x - b.x) - 50) / 9.2)

    def move(self, a, b):
        """Box a -> box b along the row, bump included."""
        return REPEAT if a == b else round(5 + abs(self.boxes[a].x - self.boxes[b].x) / 9.2)

    def prestige(self, tier, rtier, reset_path):
        """localUpgrades.OnPrestige: the local upgrades (R, C, M, F, B) back to 0 and their boxes' costs at the new tier;
        courseScript.onPrestige: the new tiers, and with isResettingCloneTime no clone path until the next lap. The
        course's GLOBAL and Movement boxes keep their levels (upgradeBox.onPrestige skips them)."""
        for k, b in self.boxes.items():
            if b.kind in "RCMFB":
                self.level[k] = 0
                b.retier(tier - self.tier)
        self.tier, self.rtier = tier, rtier
        if reset_path:
            self.P = None


def late_courses(dash):
    """Courses 1 to 5 as the plan leaves them at dash (Course 1 and 2 from stage 1; 3 to 5 untouched). Course 4's V and
    lower-case keys and Course 5 are ANY%'s: off-row boxes (off) take DETOUR's ticks from the restart."""
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
                                            "J": Box("buyaj", 7951, 554712617361.3899),
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
                                               # Course 5's SwapBlocksOnDash (Movement unlockBlockSwap) on Course 4's row,
                                               # there after the trip breaker: 3e18 * 10^(Course 5's tier 16.5).
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
    """One course's stretch of the plan: the boxes it may buy, what it must have bought (and lapped) before it ends, and
    the income of the other courses (their rewards stay as last updated while the player is away)."""

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
        """What a clone pays per path on average: the plain pay times the rolls' average (roll_mult); this phase's own
        max tier boxes (S, Z) count once bought."""
        m = lv[self.mi] if self.mi >= 0 else 0
        a = self.at
        roll = roll_mult(lv[a["F"]] if "F" in a else self.c.level.get("F", 0), lv[a["B"]] if "B" in a else self.c.level.get("B", 0),
                         self.max_fast + (lv[a["S"]] if "S" in a else 0), self.max_big + (lv[a["Z"]] if "Z" in a else 0))
        return math.ceil(self.reward(lv, g) * (0.1 + self.gm * 0.2 + m * 0.1)) * roll


class LateState:
    """A phase's search state: tick, cash, the phase's box levels, G, this course's clone path and each of its clones'
    first pay tick, where the player is (gate, done after a lap, or a box), whether it lapped."""
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
        """W paid after tick t up to tick T: the other courses and this course's clones, each paying pay / P per tick
        from its first pay tick on (the sums are large enough that the per-path steps don't matter to the plan)."""
        w = self.ph.other_rate * (T - self.t)
        if self.clones and self.P:
            w += self.pay() / self.P * sum(T - max(a, self.t) for a in self.clones if a < T)
        return w

    def advance(self, T):
        self.cash += self.income(T)
        self.t = T

    def wait_for(self, cost, deadline):
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

    def done(self):
        return (self.lapped or not self.ph.need_lap) and all(l >= r for l, r in zip(self.lv, self.ph.req))

    def lap(self):
        ph = self.ph
        s = self.child()
        s.advance(s.t + (ph.lap if s.where == "gate" else ph.c.lead + ph.lap))
        s.cash += ph.reward(s.lv, s.g)
        if s.P is None or ph.lap < s.P:
            # clonesScript.updateClonePath: the faster path destroys the walking clones (paid) and restarts them.
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
        if not s.wait_for(cost, deadline):
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
    """What a phase state can do next: a lap, or buy a box (restart and walk, or along the row from the row box it's
    under to another row box)."""
    options = [s.lap()]
    c = s.ph.c
    for i, k in enumerate(s.ph.keys):
        options.append(s.buy(i, False, force))
        if c.on_row(s.where) and c.on_row(k):
            options.append(s.buy(i, True, force))
    return [x for x in options if x is not None]


def late_greedy(s0, lap_income=False):
    """A phase one step at a time: the next step is the one after which the phase projects to end soonest, the rest of
    what it needs paid from the income it then has (plus a bump per box still to buy, the walk to one away from the
    row, and a lap if it still needs one; with no income yet, the lap). lap_income: the income counts laps back to back
    too (the reward per restart + lap); ANY% plans with it, main's goals were planned without it (~1800 ticks slower)."""
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


# main's phases after TAIL: (course, boxes it may buy, what it must buy ("caps": each to its cap), whether it must lap).
MAIN_PHASES = [(3, "RCMGFSP", {"S": 1, "P": 1}, False, "clone speed max + statue"),
               (1, "RC", "caps", True, "Course 1 bought out"),
               (2, "RCM", "caps", True, "Course 2 bought out"),
               (3, "RCMGFJ", {"J": 1}, True, "air jump"),
               (4, "RCMFBZQ", {"Q": 1}, True, "Boost all previous courses"),
               (4, "RCMFBZW", {"W": 1}, False, "Activate orange blocks")]


class Late:
    """The run after dash as it goes (late_plan: main's phases; any_plan: ANY%'s): every course's economy, G, the global
    clone multiplier and max clone tiers, tick, cash, where the player is, the goals so far and a summary per step."""

    def __init__(self, dash, verbose, quiet=False):
        self.cs = late_courses(dash)
        self.g, self.aj = GLOBAL0 + dash.g, False
        # Global clone multiplier (the statue) and the max clone speed / size tiers (buyclonespeed, buymaxsize4).
        self.glob = {"gm": 0, "max_fast": 0, "max_big": 0}
        self.t, self.cash = dash.t, dash.cash
        self.tokens, self.here, self.where = [], 3, "done"
        self.verbose, self.quiet = verbose, quiet
        self.steps = []         # (what, first tick, last tick, cash at the end, goals)
        self.breaker = None     # (tick, G) at the trip breaker
        self.lap_income = False  # late_greedy's
        self.cs[2].update_reward(self.g)

    def say(self, line):
        if not self.quiet:
            print(line)

    def other_rate(self, here):
        return sum(c.rate(self.glob) for n, c in self.cs.items() if n != here)

    def tail(self):
        """TAIL: Course 2 left after its lap (its reward updated then), both bonus boxes raise G (their course: 3)."""
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
        """BoostOldCourses: reward tier = the box course's tier + the old course's prestige mod, fullUpdate, caps x(+0.5)."""
        for n in olds:
            c = self.cs[n]
            c.rtier = box_tier + c.mod
            c.capmult += 0.5
            c.update_reward(self.g)

    def trip_breaker(self):
        """Course 5's prestige box (tripBreaker): BoostOldCourses raises Courses 1 to 4's caps x(+0.5) (the reward tiers
        it sets are overwritten next), then localUpgrades.OnPrestige resets them and Course 5 (courseScript.onPrestige):
        Courses 1 to 3 as a breaker prestige, tier = 5's tier + their tier + BreakerLayerTierMod, reward tier that +
        BreakerLayerRewardTierMod; Course 4 tier and reward tier 5's tier + its tier - 1 + its RewardPrestigeMod, Course 5
        its own tiers - 1 + its mod (16.5 again), both without a clone path until their next lap; then TripBreaker sets
        Cash to 0 and shows SwapBlocksOnDash (its cost at Course 5's tier then). G and the GLOBAL boxes stay."""
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
        """What a one-off box does once a phase has bought it."""
        if token == "buystatue":        # +1 global clone multiplier, Courses 1 and 2 boosted by Course 3's tier
            self.glob["gm"] += 1
            self.boost(self.cs[3].tier, (1, 2))
        elif token == "buyaj":
            self.aj = True
        elif token == "buyprestige4":   # Courses 1 to 3 boosted by Course 4's tier
            self.boost(self.cs[4].tier, (1, 2, 3))
        elif token == "buyprestige5":
            self.trip_breaker()

    def travel(self, course):
        """Leaving updates the old course's reward (its clone area goes off screen); arriving, the new one's."""
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
        """A free global cashPerLoop box off the way (G + 1): ticks at what the clones pay meanwhile, back by a restart."""
        t0 = self.t
        self.cash += sum(x.rate(self.glob) for x in self.cs.values()) * ticks
        self.t += ticks
        self.g += 1
        self.tokens.append(goal)
        self.where = "done"
        self.say(f"  tick {self.t:6d}  {goal} ({ticks} ticks)" + " " * 20 + f"-> {self.cash:12.4g} W  G {self.g}")
        self.steps.append((goal, t0, self.t, self.cash, 1))

    def phase(self, course, keys, required, need_lap, name, given=None):
        """One course's stretch (travelling there first): planned greedily, or following `given` goals."""
        cs, c = self.cs, self.cs[course]
        if course != self.here:
            self.travel(course)
        keys = [k for k in keys if k in c.boxes]
        if required == "caps":
            required = {k: c.cap(k) for k in keys}
        ph = Phase(c, keys, required, need_lap, self.other_rate(course), self.glob, LATE_LAP[course][1 if self.aj else 0])
        s0 = LateState.__new__(LateState)
        t0 = self.t
        s0.ph, s0.t, s0.cash, s0.lv, s0.g, s0.P = ph, t0, self.cash, tuple(c.level[k] for k in keys), self.g, c.P
        n = c.level.get("C", 0)
        s0.clones = tuple(t0 + (i + 1) * c.P // n for i in range(n)) if n and c.P else ()
        s0.where, s0.lapped, s0.prev, s0.tokens, s0.what = self.where, False, None, (), ""
        if given is None:
            # Greedy: stage 1's time-ordered search doesn't finish on these phases (Course 3's seven boxes make too
            # many combinations to rule out).
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
    """The goals after TAIL and their estimated ticks (from reward:bonus1, like stage 1's)."""
    run = Late(dash, verbose)
    run.tail()
    for phase in MAIN_PHASES:
        run.phase(*phase)
    return run.tokens[len(TAIL):], run.t


# ======== ANY%: main's goals through its first buy:buyaj, then the global cashPerLoop boxes worth their detour, Course 5
# farmed to its prestige box (the trip breaker, 3.16e22 W), and Course 4 farmed to SwapBlocksOnDash (9.49e34 W): after
# the breaker Course 4's reward tier is 29.5 and Course 5's 16.5 again, so a Course 4 lap pays ~1e13 times a Course 5 one
# (any_plan prints the numbers), and SwapBlocksOnDash sits on Course 4's row.

# The optional global cashPerLoop boxes: goal -> (course, phase key; None: a detour on the way out of Course 3).
# any_choose picks them and G5, how many of Course 5's "global cash " boxes (cap 4) to buy before its prestige box.
ANY_OPTIONS = {"reward:bonus5": (3, None), "reward:bonus4": (5, "d"), "reward:bonus15": (5, "e"), "buyfree5": (5, "X"),
               "buyglobalreward4": (4, "H"), "reward:bonus7": (4, "a"), "reward:bonus6": (4, "b"),
               "reward:bonus16": (4, "c")}


def goal_course(cs, goal):
    kind, _, arg = goal.partition(":")
    if kind in ("start", "finish"):
        return int(arg)
    return next((n for n, c in cs.items() if any(b.goal == goal for b in c.boxes.values())), None)


def any_prefix(run_name, verbose):
    """Run `run_name`'s goals in fulltas.json through its first buy:buyaj, replayed through the economy model (stage 1's
    and the late phases' steps, each the soonest way to the next goal): those goals and the state ANY% goes on from."""
    runs = json.load(open(FULLTAS, encoding="utf-8"))
    run = next((r for r in runs if r.get("name") == run_name), None)
    if run is None or "buy:buyaj" not in run["goals"] or "reward:bonus1" not in run["goals"]:
        raise SystemExit(f"fulltas.json has no run {run_name} with reward:bonus1 and buy:buyaj")
    keep = run["goals"][:run["goals"].index("buy:buyaj") + 1]
    after = keep[keep.index("reward:bonus1") + 1:]
    if after[0] != "start:2" or "buy:buydash" not in after:
        raise SystemExit(f"run {run_name} doesn't go on from reward:bonus1 to Course 2 and buy:buydash")
    d = after.index("buy:buydash") + 1
    dash = replay(start_state(CASH), after[1:d], stage1_options)
    late = Late(dash, verbose, quiet=True)
    if after[d:d + len(TAIL)] != TAIL:
        raise SystemExit(f"run {run_name}'s goals after buy:buydash aren't TAIL")
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


def any_tail(base, choice, quiet, verbose=False, detour=None):
    """ANY%'s goals after buy:buyaj from the prefix's state `base` (a copy of it goes on): `choice` = which ANY_OPTIONS
    boxes, and G5; `detour` overrides DETOUR ticks (the break-even test)."""
    run = copy.deepcopy(base)
    run.quiet, run.verbose, run.tokens, run.steps, run.lap_income = quiet, verbose, [], [], True
    ticks = dict(DETOUR, **(detour or {}))
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
    """The ANY_OPTIONS boxes and G5 that end the run soonest: from none, the best single change (a box in or out, G5
    one up or down) while one helps. Returns the choice, its end tick and the plans simulated."""
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
    """The goals Full-TAS doesn't know: FullTasPlugin.cs's FullTasBoxes tokens, courses.json's bonuses."""
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
    """Plans ANY% (module docstring), prints its phases and why, and writes its goals to `out`."""
    keep, base = any_prefix(run_name, verbose)
    t_aj = base.t
    print(f"ANY%: {run_name}'s {len(keep)} goals through buy:buyaj, replayed: tick {t_aj} after reward:bonus1 (the run's"
          f" ~{BONUS1_TICK + t_aj}, {(BONUS1_TICK + t_aj) * 0.02 / 60:.1f} min), {base.cash:.4g} W, G {base.g},"
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

    print("\nPlan after buy:buyaj:")
    run = any_tail(base, choice, False, verbose)

    # Course 5 against Course 4 after the breaker, from the state right after it.
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
    print(f"  {'main through buy:buyaj':44s} {0:6d} -> {t_aj:6d} {t_aj:6d} ticks {t_aj * 0.02:6.1f} s"
          f"  {base.cash:10.4g} W  {len(keep)} goals")
    for what, a, b, cash, n in run.steps:
        print(f"  {what:44s} {a:6d} -> {b:6d} {b - a:6d} ticks {(b - a) * 0.02:6.1f} s  {cash:10.4g} W  {n} goals")
    total = BONUS1_TICK + run.t
    print(f"ANY% ends at buy:buyblockswap5: tick {run.t} after reward:bonus1, the run's ~{total} ({total * 0.02:.1f} s,"
          f" {total * 0.02 / 60:.1f} min; estimates); {len(goals)} goals, {len(run.tokens)} after buy:buyaj")
    bad = invalid_goals(goals)
    if bad:
        raise SystemExit(f"not Full-TAS goals: {bad}")
    print(json.dumps(run.tokens))
    if out:
        with open(out, "w", encoding="utf-8") as f:
            json.dump(goals, f, indent=2)
        print(f"wrote {len(goals)} goals to {out}")


def main():
    global LAP, LEAD, ARRIVE, FIRST_LAP
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", default="main", help="the fulltas.json run --write replaces, or --any-pct follows")
    ap.add_argument("--cash", type=int, default=CASH, help="W at reward:bonus1")
    ap.add_argument("--arrive", type=int, default=ARRIVE, help="ticks from reward:bonus1 to Course 2's start gate")
    ap.add_argument("--lap", type=int, default=LAP, help="Course 2 start gate -> finish, ticks")
    ap.add_argument("--lead", type=int, default=LEAD, help="restart -> Course 2's start gate, ticks")
    ap.add_argument("--first-lap", type=int, default=FIRST_LAP, help="Course 2's best run in ticks when the plan starts")
    ap.add_argument("--write", action="store_true", help="replace the run's goals after reward:bonus1 in fulltas.json")
    ap.add_argument("--verbose", action="store_true", help="print every step after dash, not only each phase's end")
    ap.add_argument("--any-pct", action="store_true", help="plan ANY%%: --run's goals through buy:buyaj, then its own")
    ap.add_argument("--out", help="--any-pct: write the whole goal list (JSON) here")
    ap.add_argument("--detour", action="append", default=[], metavar="GOAL=TICKS",
                    help="--any-pct: ticks from the course's restart to an optional box (DETOUR), e.g. reward:bonus4=150")
    ap.add_argument("--transit", action="append", default=[], metavar="A-B=TICKS",
                    help="ticks from course A to course B's start gate (TRANSIT), e.g. 5-4=2400")
    a = ap.parse_args()
    LAP, LEAD, ARRIVE, FIRST_LAP = a.lap, a.lead, a.arrive, a.first_lap
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
    late, end = late_plan(dash, a.verbose)
    print(f"block swap bought at tick {end} after reward:bonus1; the run's tick ~{BONUS1_TICK + end}"
          f" ({(BONUS1_TICK + end) * 0.02 / 60:.1f} min, estimates); {len(late)} goals after TAIL")

    # Course 1 against Course 2 while the plan runs: its laps and its maxed clones.
    mid = rows[len(rows) // 2]
    c2_lap_rate = mid.reward2() / (LEAD + LAP)
    c2_rate = c2_lap_rate + mid.N * mid.pay2() / mid.P
    c1_lap_rate = reward(1, 0, C1_BUYS, GLOBAL0 + mid.g) / 127
    c1_max = offscreen_pay(reward(1, 0, 40, GLOBAL0 + 2), 0, 35, C1_PATH) / OFFSCREEN
    print(f"Course 1 check at tick {mid.t}: a Course 1 lap earns {c1_lap_rate:.2f} W/tick against Course 2's {c2_lap_rate:.1f}"
          f" (plus {c2_rate - c2_lap_rate:.0f} from its clones); Course 1 maxed (40 reward buys, 35 clones, G 3) would pay"
          f" {c1_max:.1f} W/tick off screen, for ~1500 ticks of 1to2 both ways: never worth it.")

    goals = tokens + TAIL + late
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
