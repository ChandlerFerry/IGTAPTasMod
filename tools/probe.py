"""Run a 1x probe in the game and summarise its log (ProbeRunner.cs; enginesim/README.md#probe).

  probe.py <name> <route> <ticks.json> [--savestate ID] [--capture-at N] [--from-tick N] [--fps N] [--omni]
           [--pause-at N [--pause-offset K] --pause-frames F] [--fast] [--lean]
  probe.py <name> --tech ID [...]   a tech demo as its playback plays it (its start, inputs, savestate or lab map)
  probe.py --show <name>          summarise a probe log already written

route is a category prefix (1_WJ_DS). ticks.json is a list of input ticks ({x, y, held, press, release, dash, dashJump,
lockX, lockY, turnX, turnY}). --fps sets the playback's fixed clock (default 280, the rate turbo and enginesim model; 50 or
more, see enginesim/README.md#probe).
Plays at normal speed from the category's quick restart (or the savestate), waits for the log, and prints each
physics tick plus every rendered frame where the player moved with no physics tick since the frame before (the moves
only Update makes: a TimeStop's frames, the pause menu's, or several frames between two ticks).
--from-tick N (a Full-TAS route, fulltas/<run>_NONE, from its new game) plays its first N physics ticks in turbo, unlogged.
Zone rows (an activator's trigger, the zone load, the atom collider's OnEnable and Start, an upgrade box's first Start with
what it would change, a zip mover's OnEnable) print with k, the rendered frame
within their physics tick (0: the tick's own frame), as do the atom grid's calls and the RNG draws from the first zone row on.
--pause-at N opens the pause menu (the Pause button's menuButtonPressed) right after physics tick N (the log's `tick N`),
or --pause-offset K Movement.Update frames after it, and --pause-frames F closes it after F frames: time stands still
(timeScale 0), but Update's step-up runs every frame.
"""
import argparse
import json
import sys
import time

from gamepaths import CONFIG, call

def show(name):
    log = json.load(open(CONFIG / "probes" / (name + ".json"), encoding="utf-8"))
    frames = sum(1 for r in log["rows"] if r["kind"] == "frame")
    ticks = sum(1 for r in log["rows"] if r["kind"] == "tick")
    print(f"{log['name']}: {log['result']}; {ticks} ticks, {frames} frames; savestate {log.get('capturedSavestateId')}")
    last_frame, ticked, tick_frame, zoned = None, False, {}, False
    for row in log["rows"]:
        if row["kind"] in ("zone", "atom", "rng"):
            zoned = zoned or row["kind"] == "zone"
            if zoned:
                k = row["frame"] - tick_frame[row["physics"]] if row["physics"] in tick_frame else "?"
                what = row.get("what") or row.get("method") or f"waiting {row.get('waiting')}"
                extra = f" zone {row['zone']} active {row['activeZone']} loading {row['loadingText']} by {row['by']}" if row["kind"] == "zone" else ""
                print(f"  {row['kind']:4} {what:24} frame {row['frame']} tick {row['physics']} k={k} t {row.get('time', 0):.5f}"
                      f" {row.get('obj') or ''}{extra}")
            continue
        if row["kind"] == "tick":
            tick_frame[row["physics"]] = row["frame"]
            ticked = True
            print(f"tick {row['physics']:4} in {row['input']:4} pos {row['x']:9.2f},{row['y']:9.2f} v {row['vx']:8.2f},{row['vy']:8.2f}"
                  f" m {row['mx']:8.2f},{row['my']:8.2f} {row['mode']:5} dash={row['dash']} f={row['f']} wall={row['wall']} ground={row['ground']}")
            continue
        if row["kind"] != "frame":
            continue
        if last_frame is not None and not ticked and (abs(row["x"] - last_frame["x"]) > 0.01 or abs(row["y"] - last_frame["y"]) > 0.01):
            paused = " (paused)" if row.get("paused") else ""
            print(f"  frame {row['frame']} timeScale {row['timeScale']}{paused}: moved {row['x'] - last_frame['x']:+.2f},{row['y'] - last_frame['y']:+.2f}"
                  " with no physics tick")
        last_frame, ticked = row, False
    paused = [r for r in log["rows"] if r.get("paused")]
    if paused:
        print(f"paused {len(paused)} frames: y {paused[0]['y']:.2f} -> {paused[-1]['y']:.2f}"
              f" ({paused[-1]['y'] - paused[0]['y']:+.2f}), x {paused[0]['x']:.2f} -> {paused[-1]['x']:.2f}")

def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--show":
        show(sys.argv[2])
        return
    parser = argparse.ArgumentParser()
    parser.add_argument("name")
    parser.add_argument("route", nargs="?", default="")
    parser.add_argument("ticks", nargs="?")
    parser.add_argument("--tech", help="probe this tech's demo (route and ticks default to the demo's)")
    parser.add_argument("--savestate", default="")
    parser.add_argument("--capture-at", type=int)
    parser.add_argument("--from-tick", type=int, help="a Full-TAS probe: its first N physics ticks in turbo")
    parser.add_argument("--log-from", type=int, help="log only from this physics tick on")
    parser.add_argument("--omni", action="store_true", help="play with the omnidirectional dash too (restored after)")
    parser.add_argument("--fast", action="store_true", help="Fast playback whatever the setting (at the playback rate only)")
    parser.add_argument("--lean", action="store_true", help="tick rows only, what enginesim align-1x compares")
    parser.add_argument("--fps", type=int, default=0, help="playback fixed-clock frame rate (default 280), e.g. 60, 144, 240")
    parser.add_argument("--pause-at", type=int, help="open the pause menu right after this physics tick")
    parser.add_argument("--pause-offset", type=int, default=0, help="Movement.Update frames after --pause-at before pausing")
    parser.add_argument("--pause-frames", type=int, default=0, help="Movement.Update frames to keep the pause menu open")
    args = parser.parse_args()
    if not args.tech and not (args.route and args.ticks):
        parser.error("route and ticks.json are required without --tech")
    body = {"name": args.name, "route": args.route, "savestateId": args.savestate, "fps": args.fps, "omniDash": args.omni,
            "fast": args.fast, "lean": args.lean}
    if args.ticks:
        body["ticks"] = json.load(open(args.ticks))
    if args.tech:
        body["tech"] = args.tech
    if args.capture_at is not None:
        body["captureAt"] = args.capture_at
    if args.log_from is not None:
        body["logFrom"] = args.log_from
    if args.from_tick is not None:
        body["fromTick"] = args.from_tick
    if args.pause_at is not None:
        body["pauseAt"] = args.pause_at
        body["pauseFrames"] = args.pause_frames
        body["pauseOffset"] = args.pause_offset
    started = call("/api/probe/run", body, timeout=30)
    if not started.get("ok"):
        raise SystemExit(started)
    while call("/api/probe/status", timeout=30).get("running"):
        time.sleep(0.5)
    show(args.name)

if __name__ == "__main__":
    main()
