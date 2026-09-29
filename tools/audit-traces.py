import json
import sys
from pathlib import Path

from gamepaths import TRACES
# The gate's windows (Solver.cs AuditTick), in physics ticks: the game's buffer windows +2 slack.
JUMP_WINDOW, DASH_WINDOW, CUT_WINDOW = 12, 8, 15
# Movement.getPlayerControlledInput holds a buffered dash through a wall jump's first 5 lock ticks (re-arming
# cancelDashBuffer at 0.06 s): after the last hold the buffer lives 3 ticks, +1 (the hold runs on the next tick) +2 slack.
DASH_HOLD_WINDOW = 6
NEVER = -1000000

def dash_held(m):
    return m["dashCooldown"] < 0 and m["dashUnlocked"] and not m["wallJumpMovementLockRemaining"] < m["wallJumpMovementLockFrames"] - 5

def press_stamps(trace):
    """Per physics tick: MacroEngine.Clock and its last jump press, dash press and jump release, as AuditTick reads them.

    Traces from before the stamps map each physics tick to the input tick it ran (the tick's `tick`, MacroEngine.RunTicks:
    a dash's lock ticks run no input) and stamp each input's press or release at the physics tick that ran it."""
    ticks, inputs = trace["ticks"], trace["inputs"]
    engine = [{e[1]: e[2] for e in t["engine"]} for t in ticks]
    if all("Clock" in e for e in engine):
        return [(e["Clock"], e["LastJumpPress"], e["LastDashPress"], e["LastJumpRelease"]) for e in engine]
    stamps, jump, dash, release, held, ran = [], NEVER, NEVER, NEVER, False, 0
    for p, t in enumerate(ticks):
        if t["tick"] != ran and 0 < t["tick"] <= len(inputs):
            ran = t["tick"]
            k = inputs[ran - 1]
            if k["press"]:
                jump = p
            if k["release"] or held and not k["held"]:
                release = p
            if k["dash"]:
                dash = p
            held = k["held"]
        # A dash's jump (dashJump) presses on a lock tick: MacroEngine clears _dashJumpFrame as it does.
        if p and engine[p - 1].get("_dashJumpFrame", 0) > 0 and engine[p].get("_dashJumpFrame", 0) == 0 and dash != p:
            jump, held = p, True
        stamps.append((p, jump, dash, release))
    return stamps

def violations(trace):
    found = []
    held_at = NEVER
    for p, (t, (now, jump, dash, release)) in enumerate(zip(trace["ticks"], press_stamps(trace))):
        m = {e[1]: e[2] for e in t["movement"]}
        tick = f"tick {p} (input tick {t['tick']})"
        if m["jumpBuffer"] and now - jump > JUMP_WINDOW:
            found.append(f"{tick}: stale jump buffer")
        if m["dashBuffer"]:
            if now - dash > DASH_WINDOW and now - held_at > DASH_HOLD_WINDOW:
                found.append(f"{tick}: stale dash buffer")
            elif dash_held(m):
                held_at = now
        if m["jumpCutBuffer"] and now - release > CUT_WINDOW:
            found.append(f"{tick}: stale jump cut")
    return found

def main():
    paths = [Path(p) for p in sys.argv[1:]] or sorted(p for p in TRACES.glob("*.json") if not p.name.endswith(".world.json"))
    bad = 0
    for path in paths:
        trace = json.loads(path.read_text(encoding="utf-8"))
        if "ticks" not in trace or "inputs" not in trace:
            continue
        found = violations(trace)
        bad += bool(found)
        print(f"{path.name:36} {'VIOLATES ' + '; '.join(found[:3]) if found else 'ok'}")
    sys.exit(1 if bad else 0)

if __name__ == "__main__":
    main()
