"""The buy-order planner: the phase's buy sequence and grouping from the measured time-to-buy table.

Reads the table `fulltas-buytable` writes (fulltas/<run>.buytable.json: per place the run stands at in the phase, per
ordered box set and technique, the engine-measured ticks to buy the set in one go) and tools/plan_fulltas.py's economy
model of Course 2 (clone income, box costs, laps), and searches (Dijkstra over the model's states, time first, with the
model's dominance) for the buy sequence and grouping that buys the phase's target soonest. It emits a strict goal list:
one `buy:a+b` goal where one pass buys several boxes, plain `buy:<box>` goals otherwise, the laps as `start:N finish:N`.

The run's own goal list is replayed through the same model and walks (a buy it can't pay for on arrival waits there, as
the route did), so the two predictions compare like for like. The model is calibrated on the run's offline best: Cash
and Course 1/2's paths at the phase's start (read from the trace at that tick), the arrival, lap and restart lead.

  python tools/plan-buytable.py --table <buytable.json> --run FalseEnding-Any% [--out planned.json] [--verbose]

Course 2's stretch (reward:bonus1 .. buy:buyds) is the phase the economy model covers; docs/fulltas.md "Buy planner".
"""
import argparse
import difflib
import heapq
import itertools
import json
import os

import gamepaths
import plan_fulltas as pf

LETTER = {"buy:" + t: k for k, t in pf.TOKEN.items()}
TARGET = "D"
# A box's place before the run's first crossing of Course 2's gate (the quick restart still lands before the course).
PRE = "@"
# A replayed buy the route waited at: the longest wait followed (a longer one is a list the model can't play).
WAIT_LIMIT = 20000
# Laps a replayed buy may run on its walk before it (see Planner.replay).
HIDDEN_LAPS = 3


def label_of(letters):
    return "buy:" + "+".join(pf.TOKEN[b] for b in letters)


# --- the table -------------------------------------------------------------------------------------------------------

class Table:
    """Walk costs from the measured table. A place's kind names where the model's player is: `start` (the phase's
    start), `gate` (Course 2's start gate), `finish` (the model's "done"), `box` (the box's letter) and `restart` (a
    quick restart's landing, reached from anywhere after the restart's own ticks)."""

    def __init__(self, path):
        self.doc = json.load(open(path, encoding="utf-8"))
        self.places = {p["id"]: p for p in self.doc["places"]}
        self.best = {}
        self.misses = 0
        for e in self.doc["entries"]:
            if e.get("ticks") is None:
                self.misses += 1
                continue
            key = (e["place"], tuple(e["boxes"]))
            if key not in self.best or e["ticks"] < self.best[key]["ticks"]:
                self.best[key] = e
        self.at = {}
        for p in self.doc["places"]:
            where = {"start": "start", "gate": "gate", "finish": "done"}.get(p["kind"])
            if p["kind"] == "box":
                where = LETTER.get(p.get("box"))
            if where is not None:
                self.at.setdefault(where, []).append(p["id"])
        gates = [p for p in self.doc["places"] if p["kind"] == "gate"]
        restarts = [p for p in self.doc["places"] if p["kind"] == "restart"]
        # Course 2's own restart point: the landing nearest its gate (an earlier one lands at the last checkpoint before
        # the course's).
        if gates and restarts:
            g = gates[0]
            restarts.sort(key=lambda p: (p["x"] - g["x"]) ** 2 + (p["y"] - g["y"]) ** 2)
        self.restart = restarts[0] if restarts else None
        r = (self.restart or {}).get("restart") or {}
        self.restart_ticks = r["land"] - r["press"] if "land" in r and "press" in r else 1
        # Before the run first crosses Course 2's gate, a quick restart lands at the checkpoint before the course: the
        # phase's earliest landing, when it isn't Course 2's own.
        early = min(restarts, key=lambda p: p["tick"]) if restarts else None
        self.far = early if early is not None and early is not self.restart and early["tick"] < self.restart["tick"] else None
        r = (self.far or {}).get("restart") or {}
        self.far_ticks = r["land"] - r["press"] if "land" in r and "press" in r else 1

    def entry(self, pid, labels):
        return self.best.get((pid, tuple(labels)))

    def walk(self, where, labels):
        """(ticks, technique, place) of the cheapest measured way from where (without a restart), or None."""
        found = None
        for pid in self.at.get(where, ()):
            e = self.entry(pid, labels)
            if e and (found is None or e["ticks"] < found[0]):
                found = (e["ticks"], e["technique"], pid)
        return found

    def via_restart(self, labels, far=False):
        """A quick restart then the measured walk from its landing: Course 2's, or with far the landing before the
        course's checkpoint (None when the table has no such landing)."""
        place, ticks = (self.far, self.far_ticks) if far else (self.restart, self.restart_ticks)
        if place is None:
            return None
        e = self.entry(place["id"], labels)
        return (ticks + e["ticks"], e["technique"], place["id"]) if e else None


# --- calibration ------------------------------------------------------------------------------------------------------

def tick_state(path, n):
    """The trace's tick n object, found by streaming (a Full-TAS trace is hundreds of MB)."""
    key = '{"tick":%d,' % n
    with open(path, encoding="utf-8") as f:
        carry = ""
        while True:
            chunk = f.read(32_000_000)
            if not chunk:
                raise SystemExit(f"{path} has no tick {n}")
            buf = carry + chunk
            i = buf.find(key)
            if i < 0:
                carry = buf[-len(key):]
                continue
            s = buf[i:]
            depth, inside, escaped = 0, False, False
            k = 0
            while True:
                if k >= len(s):
                    more = f.read(8_000_000)
                    if not more:
                        raise SystemExit(f"{path}: tick {n} is cut off")
                    s += more
                c = s[k]
                if inside:
                    if escaped:
                        escaped = False
                    elif c == "\\":
                        escaped = True
                    elif c == '"':
                        inside = False
                elif c == '"':
                    inside = True
                elif c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        return json.loads(s[:k + 1])
                k += 1


def course_fields(state, number):
    return next({f[1]: f[2] for f in c["fields"]} for c in state["courses"]
                if any(f[1] == "courseNumber" and f[2] == number for f in c["fields"]))


def trace_goals(path):
    """The trace header's goal objects (label, kind, course, boxes), read from the file's head."""
    with open(path, encoding="utf-8") as f:
        head = f.read(8_000_000)
    i = head.find('"goals":[', head.find('"fulltas":{'))
    if i < 0:
        raise SystemExit(f"{path} has no fulltas.goals in its header")
    return json.JSONDecoder().raw_decode(head, i + len('"goals":'))[0]


def goal_objects(header, labels):
    """One goal object per label: the header's own, and for a `buy:a+b` goal the boxes of each part in order (the
    plugin's `ResolveFullTasGoal` grammar)."""
    by_label = {g["label"]: g for g in header}
    out = []
    for label in labels:
        if label in by_label:
            out.append(by_label[label])
            continue
        parts = label.split(":", 1)[1].split("+")
        missing = [p for p in parts if "buy:" + p not in by_label]
        if not label.startswith("buy:") or missing:
            raise SystemExit(f"goal {label}: the trace's header has no {', '.join(missing) or label} to build it from")
        one = dict(by_label["buy:" + parts[0]])
        one["label"] = label
        one["boxes"] = [box for p in parts for box in by_label["buy:" + p]["boxes"]]
        out.append(one)
    return out


def calibrate(best, trace_path, table):
    """Sets plan_fulltas.py's Course 2 constants from the run's best and its trace at reward:bonus1."""
    goals, ticks = best["goals"], best["goalPhysicsTicks"]
    b, d = goals.index("reward:bonus1"), goals.index("buy:buyds")
    B = ticks[b]
    at = tick_state(trace_path, B)
    c1, c2 = course_fields(at, 1), course_fields(at, 2)
    wj = goals.index("buy:buywj")
    steps = [(goals[i], goals[i + 1], ticks[i + 1] - ticks[i]) for i in range(b + 1, d)]

    def typical(pair):
        found = sorted(dt for x, y, dt in steps if pair(x, y))
        return max(found, key=found.count) if found else None

    # The clones run Course 2's best path as it is when they're bought: the first lap sets it (2.70 s -> 2.44 s on
    # FalseEnding-Any%'s best), and the first clone comes after it.
    first_clone = goals.index("buy:buyclone2", b)
    pf.CLONE_PATH = round(course_fields(tick_state(trace_path, ticks[first_clone]), 2)["bestPathTime"] / 0.02)
    pf.C1_PATH = round(c1["bestPathTime"] / 0.02)
    pf.C1_BUYS = sum(1 for x in goals[:wj] if x == "buy:buyreward")
    pf.C1_CLONES = sum(1 for x in goals[:wj] if x == "buy:buyclone")
    pf.C1_GLOBAL = 0
    while pf.reward(1, 0, pf.C1_BUYS, pf.C1_GLOBAL) < c1["reward"] and pf.C1_GLOBAL < 5:
        pf.C1_GLOBAL += 1
    pf.GLOBAL0 = 0
    while pf.reward(4, 2, 0, pf.GLOBAL0) < c2["reward"] and pf.GLOBAL0 < 5:
        pf.GLOBAL0 += 1
    pf.C1_PAY = pf.offscreen_pay(pf.reward(1, 0, pf.C1_BUYS, pf.C1_GLOBAL), 0, pf.C1_CLONES, pf.C1_PATH)
    pf.ARRIVE = ticks[goals.index("start:2", b)] - B
    pf.LAP = typical(lambda x, y: x == "start:2" and y == "finish:2")
    pf.LEAD = typical(lambda x, y: x != "start:2" and y == "start:2")
    gate = table.via_restart(["start:2"])
    lead = gate[0] if gate else pf.LEAD
    return {"cash": int(at["cash"]), "bonus1_tick": B, "buyds_tick": ticks[d], "arrive": pf.ARRIVE, "lap": pf.LAP,
            "lead": lead, "clone_path": pf.CLONE_PATH, "c1": f"{pf.C1_BUYS} reward, {pf.C1_CLONES} clones, path {pf.C1_PATH}",
            "global": pf.GLOBAL0}


# --- the model's moves over the table ---------------------------------------------------------------------------------

class Planner:
    def __init__(self, table, cash, lead, max_set):
        self.t, self.cash, self.lead = table, cash, lead
        # Before the first crossing of Course 2's gate (a box bought on the way from the phase's start: where "R@"), a
        # quick restart lands at the checkpoint before the course, so the way to the gate is that landing's walk.
        far = table.via_restart(["start:2"], far=True)
        self.far_lead = far[0] if far else None
        # The target stays a goal of its own (`buy:buyds`): the goals after the stretch, a best's harvest and the
        # other planners name it.
        self.sets = [s for n in range(1, max_set + 1) for s in itertools.permutations("RCMGD", n)
                     if TARGET not in s or s == (TARGET,)]
        self.missing = set()
        self.ways = {}

    def start(self):
        s = pf.State.__new__(pf.State)
        s.t, s.cash, s.n, s.N, s.m, s.g, s.P = 0, self.cash, 0, 0, 0, 0, pf.CLONE_PATH
        s.active, s.c1next, s.where = (), pf.OFFSCREEN // 2, "start"
        s.prev, s.tokens, s.what = None, (), ""
        return s

    def to_gate(self, s):
        """Ticks from where s stands to Course 2's start gate (None: no measured way)."""
        if s.where == "start":
            return pf.ARRIVE
        return self.far_lead if s.where.endswith(PRE) else self.lead

    def arrive(self, s):
        """To Course 2's start gate without a lap: from the phase's start the walk there, else a quick restart."""
        ticks = self.to_gate(s)
        if ticks is None:
            return None
        x = s.child()
        x.advance(x.t + ticks)
        x.tokens, x.what, x.where = ("start:2",), ("" if s.where == "start" else "restart, ") + "to Course 2's start gate", "gate"
        return x

    def lap(self, s):
        ticks = pf.LAP if s.where == "gate" else self.to_gate(s)
        if ticks is None:
            return None
        x = s.child()
        x.advance(x.t + (ticks if s.where == "gate" else ticks + pf.LAP))
        x.finish(pf.LAP)
        x.tokens = ("finish:2",) if s.where == "gate" else ("start:2", "finish:2")
        x.what, x.where = "lap", "done"
        return x

    def ladder(self, s, K):
        """K back-to-back record laps (plan_fulltas.State.ladder), each from a quick restart but the first from the gate."""
        if pf.FAST_LAP is None or pf.FAST_LAP + K - 1 >= s.P or s.where == "start":
            return None
        x = s.child()
        for k in range(K):
            path = pf.FAST_LAP + K - 1 - k
            lead = 0 if k == 0 and x.where == "gate" else self.to_gate(x)
            if lead is None:
                return None
            x.advance(x.t + lead + path)
            x.finish(path)
            x.where = "done"
        x.tokens = (("finish:2",) if s.where == "gate" else ("start:2", "finish:2")) + ("start:2", "finish:2") * (K - 1)
        x.what = f"ladder {K}"
        return x

    def way(self, where, letters):
        """(ticks, technique, how) of the cheapest way to buy letters in order from where: a walk from the place, or a
        quick restart then a walk. A single buy the table hasn't measured falls back to the model's estimate."""
        key = (where, letters)
        if key not in self.ways:
            self.ways[key] = self._way(where, letters)
        return self.ways[key]

    def _way(self, where, letters):
        labels = ["buy:" + pf.TOKEN[b] for b in letters]
        pre = where.endswith(PRE)
        here = where[:-len(PRE)] if pre else where
        options = []
        w = self.t.walk(here, labels)
        if w:
            options.append((w[0], w[1], "walk"))
        # Before Course 2's gate the checkpoint is still the last one before the course: a restart lands there.
        r = None if where == "start" else self.t.via_restart(labels, far=pre)
        if r:
            options.append((r[0], r[1], "restart"))
        if not options and len(letters) == 1 and where != "start" and not pre:
            self.missing.add((where, letters))
            b = letters[0]
            est = pf.move(where, b) if where in pf.ROW else self.t.restart_ticks + pf.TO[b]
            options.append((est, "estimate", "walk" if where in pf.ROW else "restart"))
        return min(options) if options else None

    def buy(self, s, letters, force=False):
        levels = {"R": s.n, "C": s.N, "M": s.m, "G": s.g, "D": 0}
        cost = 0
        for b in letters:
            if levels[b] >= pf.CAP[b]:
                return None
            cost += pf.box_cost(b, levels[b])
            levels[b] += 1
        got = self.way(s.where, letters)
        if got is None:
            return None
        ticks, technique, how = got
        x = s.child()
        x.advance(x.t + ticks)
        arrived = x.t
        if not pf.wait_for(x, cost, x.t + WAIT_LIMIT if force else None):
            return None
        x.cash -= cost
        x.n, x.N, x.m, x.g = levels["R"], levels["C"], levels["M"], levels["G"]
        # Each new clone's slot fills on the next rendered frame.
        x.active = x.active + (x.t,) * (levels["C"] - s.N)
        x.tokens = (label_of(letters),)
        x.what = (("restart, " if how == "restart" else "") + "+".join(pf.TOKEN[b] for b in letters)
                  + (f" [{technique}]" if technique != "walk" else "") + (f" (waits {x.t - arrived})" if x.t > arrived else ""))
        x.where = letters[-1] + (PRE if s.where == "start" or s.where.endswith(PRE) else "")
        return x

    def options(self, s, force=False, label=None):
        """Every move from s; with label, only the buys whose goal is that label (a replay's next goal)."""
        out = [self.arrive(s)] if s.where == "start" else [self.lap(s)] + ([self.arrive(s)] if s.where != "gate" else [])
        out += [self.ladder(s, k) for k in range(2, pf.LADDER_MAX + 1)]
        out = [x for x in out if x is not None]
        for letters in self.sets:
            if label is not None and label_of(letters) != label:
                continue
            x = self.buy(s, letters, force)
            if x is not None:
                out.append(x)
        return out

    def search(self):
        order = itertools.count()
        s0 = self.start()
        heap = [(s0.t, next(order), s0)]
        best = {}
        expanded = 0
        while heap:
            _, _, s = heapq.heappop(heap)
            if s.where.rstrip(PRE) == TARGET:
                return s, expanded
            if pf.dominated(best, s):
                continue
            rate = s.rate()
            best[pf.key(s)] = (s.cash - rate * s.t, rate)
            expanded += 1
            for x in self.options(s):
                heapq.heappush(heap, (x.t, next(order), x))
        raise SystemExit("the target is never affordable")

    def replay(self, labels):
        """The run's goal list through the same moves: each goal (or lap pair) the fastest option that plays it. A buy
        it can't pay for on arrival is the route running Course 2 on its walk (a finish that isn't the next goal pays and
        changes nothing else: FalseEnding-Any%'s goal 100 walk runs the course) or waiting at the box, whichever is
        sooner."""
        s, i = self.start(), 0
        while i < len(labels):
            fits = [x for x in self.options(s, False, labels[i]) if tuple(labels[i:i + len(x.tokens)]) == x.tokens]
            if not fits and labels[i].startswith("buy:"):
                fits = [x for x in self.options(s, True, labels[i]) if x.tokens == (labels[i],)]
                at = s
                for _ in range(HIDDEN_LAPS):
                    if at.where == "start":
                        break
                    at = self.lap(at)
                    at.tokens, at.what = (), "lap (on the next buy's walk)"
                    fits += [x for x in self.options(at, False, labels[i]) if x.tokens == (labels[i],)]
            if not fits:
                raise SystemExit(f"the model can't follow goal {labels[i]} ({i} of {len(labels)}) after {s.what or 'the start'}")
            s = min(fits, key=lambda x: x.t)
            i += len(s.tokens)
        return s


def chain(s):
    out = []
    while s is not None and s.prev is not None:
        out.append(s)
        s = s.prev
    return out[::-1]


def summary(steps):
    laps = sum(1 for x in steps if x.what.startswith("lap")) + sum(int(x.what.split()[1]) for x in steps if x.what.startswith("ladder"))
    buys = [x for x in steps if x.tokens and x.tokens[0].startswith("buy:")]
    grouped = [x for x in buys if "+" in x.tokens[0]]
    waits = sum(int(x.what.rsplit("(waits ", 1)[1].rstrip(")")) for x in buys if "(waits " in x.what)
    walked = sum(x.t - x.prev.t for x in buys) - waits
    return (f"{laps} laps, {len(buys)} buy goals ({len(grouped)} grouped, {sum(len(x.tokens[0].split('+')) for x in buys)}"
            f" boxes), {walked} ticks walking to boxes, {waits} waiting at boxes")


def milestones(steps):
    """The tick each box level is reached: (letter, level) -> tick."""
    out = {}
    for x in steps:
        for b, lv in (("R", x.n), ("C", x.N), ("M", x.m), ("G", x.g)):
            for k in range(1, lv + 1):
                out.setdefault((b, k), x.t)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--table", required=True, help="fulltas-buytable's table for the phase")
    ap.add_argument("--run", default=None, help="the run (default: the table's)")
    ap.add_argument("--goals", default=None, help="the goal list to plan against (a run file or {goals} json; default:"
                                                  " the best's)")
    ap.add_argument("--best", default=None, help="the run's offline best (default <traces>/fulltas/<run>.json)")
    ap.add_argument("--trace", default=None, help="the run's trace (default <traces>/<table's trace>)")
    ap.add_argument("--record", type=float, default=None, help="the record's total (s) the prediction is added to")
    ap.add_argument("--max-set", type=int, default=3)
    ap.add_argument("--out", default=None, help="write the whole planned goal list and the prediction here")
    ap.add_argument("--fast-lap", type=int, default=None,
                    help="the fastest known Course 2 lap (course timer, ticks): ladders run it slowed (default: none)")
    ap.add_argument("--ladder-max", type=int, default=pf.LADDER_MAX, help="the most record laps in one ladder")
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()
    pf.FAST_LAP, pf.LADDER_MAX = a.fast_lap, a.ladder_max

    table = Table(a.table)
    run = a.run or table.doc["run"]
    if table.doc["from"] != "reward:bonus1" or table.doc["until"] != "buy:buyds":
        raise SystemExit(f"the economy model plans Course 2 (reward:bonus1 .. buy:buyds), not {table.doc['from']} .. {table.doc['until']}")
    traces = str(gamepaths.TRACES)
    best = json.load(open(a.best or os.path.join(traces, "fulltas", run + ".json"), encoding="utf-8"))
    trace = a.trace or os.path.join(traces, table.doc["trace"])
    goals = best["goals"]
    if a.goals:
        doc = json.load(open(a.goals, encoding="utf-8"))
        goals = doc.get("labels") or doc["goals"] if isinstance(doc, dict) else doc
        goals = [x["label"] if isinstance(x, dict) else x for x in goals]
    m = calibrate(best, trace, table)
    print("calibrated on the best: " + ", ".join(f"{k} {v}" for k, v in m.items()))
    print(f"table: {len(table.best)} entries ({table.misses} misses), {len(table.places)} places; Course 2's restart"
          f" lands at {table.restart['x']:.0f},{table.restart['y']:.0f} ({table.restart_ticks} ticks)" if table.restart else "")

    i0, i1 = goals.index("reward:bonus1"), goals.index("buy:buyds")
    current = goals[i0 + 1:i1 + 1]
    planner = Planner(table, m["cash"], m["lead"], a.max_set)
    base = planner.replay(current)
    base_steps = chain(base)
    end, expanded = planner.search()
    steps = chain(end)
    planned = [t for x in steps for t in x.tokens]
    check = planner.replay(planned)

    print(f"current list: buy:buyds {base.t} ticks after reward:bonus1 in the model ({summary(base_steps)});"
          f" the best's own: {m['buyds_tick'] - m['bonus1_tick']}")
    print(f"planned list: buy:buyds {end.t} ticks after reward:bonus1 ({expanded} states; {summary(steps)});"
          f" replayed {check.t}")
    gain = base.t - end.t
    total = best.get("total")
    print(f"predicted gain {gain} ticks ({gain * 0.02:.2f} s)"
          + (f"; the best's total {total * 0.02:.2f} s -> {(total - gain) * 0.02:.2f} s" if total else "")
          + (f"; the record {a.record:.2f} s -> {a.record - gain * 0.02:.2f} s" if a.record else ""))
    if planner.missing:
        print(f"estimated (not in the table): {len(planner.missing)} single walks, e.g. "
              + ", ".join(f"{w}->{''.join(l)}" for w, l in sorted(planner.missing)[:8]))

    # Top differences: the box levels the plan reaches sooner or later, and the changed goal blocks.
    mb, mp = milestones(base_steps), milestones(steps)
    lead = sorted(((mb[k] - mp[k], k) for k in mb if k in mp), reverse=True)
    print("box levels reached sooner (ticks): " + ", ".join(f"{pf.TOKEN[b]} #{k} {d:+d}" for d, (b, k) in lead[:8]))
    only = [k for k in mb if k not in mp] + [k for k in mp if k not in mb]
    if only:
        print("levels in one list only: " + ", ".join(f"{pf.TOKEN[b]} #{k} ({'current' if (b, k) in mb else 'plan'})" for b, k in only))
    sm = difflib.SequenceMatcher(a=current, b=planned, autojunk=False)
    blocks = [(i1_ - i0_ + j1 - j0, tag, i0_, i1_, j0, j1) for tag, i0_, i1_, j0, j1 in sm.get_opcodes() if tag != "equal"]
    base_at = list(itertools.accumulate([len(x.tokens) for x in base_steps]))
    plan_at = list(itertools.accumulate([len(x.tokens) for x in steps]))

    def tick_at(idx, at, ss):
        k = next((n for n, c in enumerate(at) if c > idx), len(ss) - 1)
        return ss[k].t

    print("changed goal blocks (largest first):")
    for size, tag, x0, x1, y0, y1 in sorted(blocks, reverse=True)[:8]:
        print(f"  {tag:7} goals {i0 + 2 + x0}-{i0 + 1 + x1} (tick {tick_at(x0, base_at, base_steps)}):"
              f" {' '.join(current[x0:x1]) or '-'}  =>  {' '.join(planned[y0:y1]) or '-'}"
              f" (tick {tick_at(y0, plan_at, steps)})")
    if a.verbose:
        for tag, ss in (("current", base_steps), ("planned", steps)):
            print(f"{tag}:")
            for x in ss:
                print(f"  tick {x.t:5d}  {x.what:60s} -> {x.cash:10.0f} W  R{x.n} C{x.N} M{x.m} G{x.g}")
    print("planned goals:", " ".join(planned))
    if a.out:
        merged = goals[:i0 + 1] + planned + goals[i1 + 1:]
        objects = goal_objects(trace_goals(trace), merged)
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump({"run": run, "labels": merged, "goals": objects, "planned": planned, "from": "reward:bonus1",
                       "until": "buy:buyds", "predicted": {"current": base.t, "plan": end.t, "gain": gain},
                       "calibration": m}, f, indent=1)
        print(f"wrote {a.out}: {len(merged)} goals (their objects as the trace header holds them)")


if __name__ == "__main__":
    main()
