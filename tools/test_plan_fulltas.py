"""plan_fulltas.py's clone model on build 25630570 and its ladders: python tools/test_plan_fulltas.py"""
import plan_fulltas as pf

life = pf.clone_life(100)
assert pf.run_clones((0.0,), 100, 0, life) == 0 and pf.run_clones((0.0,), 100, 0, life + 0.01) == 1
assert pf.run_clones((0.0,), 100, life + 0.01, 2 * life + 0.01) == 1

pf.CLONE_PATH, pf.FAST_LAP, pf.LAP, pf.LEAD = 110, 100, 110, 28
s = pf.start_state(10 ** 9)
s.N, s.active = 10, tuple(float(s.t - i * 11) for i in range(10))
lap, ladder = s.lap(), s.ladder(3)
assert lap.P == 110 and ladder.P == 100 and s.ladder(11) is None
assert ladder.t - s.t == 102 + 28 + 101 + 28 + 100 and ladder.tokens == ("finish:2",) + ("start:2", "finish:2") * 2
# Three breaks pay every clone in flight three times over: well above three laps' worth of lap rewards and income.
laps = s
for _ in range(3):
    laps = laps.lap()
assert ladder.cash - s.cash > laps.cash - s.cash + 2 * s.N * s.pay2()
# A hold-back: the best path came from this plan's own lap at the fastest time, so the ladder holds it back to T + K.
held = s.child()
held.P, held.Pt = 100, held.t - 500
h = held.ladder(3)
assert h is not None and h.P == 100 and h.t - held.t == 3 + 102 + 28 + 101 + 28 + 100 and "held back +3" in h.what
held.Pt = None
assert held.ladder(3) is None
print("ok")
