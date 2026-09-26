"""Run a 1x probe in the game and summarise its log (ProbeRunner.cs; enginesim/README.md#probe).

  probe.py <name> <route> <ticks.json> [--savestate ID] [--capture-at N] [--interpolation game] [--fps N] [--omni]
  probe.py --show <name>          summarise a probe log already written

route is a category prefix (1_WJ_DS). ticks.json is a list of input ticks ({x, y, held, press, release, dash, dashJump,
lockX, lockY, turnX}). --fps sets the playback's frame rate (default 280; 50 or more, see enginesim/README.md#probe).
Plays at normal speed from the category's quick restart (or the savestate), waits for the log, and prints each
physics tick plus every rendered frame where the player moved with no physics tick since the frame before (the moves
only Update makes: a TimeStop's frames, or several frames between two ticks).
"""
import argparse
import json
import sys
import time
import urllib.request

from gamepaths import CONFIG

API = "http://127.0.0.1:17426"

def call(path, body=None):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(API + path, data=data, method="GET" if body is None else "POST")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        return json.loads(error.read())

def show(name):
    log = json.load(open(CONFIG / "probes" / (name + ".json"), encoding="utf-8"))
    frames = sum(1 for r in log["rows"] if r["kind"] == "frame")
    ticks = sum(1 for r in log["rows"] if r["kind"] == "tick")
    print(f"{log['name']}: {log['result']}; {ticks} ticks, {frames} frames; savestate {log.get('capturedSavestateId')}")
    last_frame, ticked = None, False
    for row in log["rows"]:
        if row["kind"] == "tick":
            ticked = True
            print(f"tick {row['physics']:4} in {row['input']:4} pos {row['x']:9.2f},{row['y']:9.2f} v {row['vx']:8.2f},{row['vy']:8.2f}"
                  f" m {row['mx']:8.2f},{row['my']:8.2f} {row['mode']:5} dash={row['dash']} f={row['f']} wall={row['wall']} ground={row['ground']}")
            continue
        if last_frame is not None and not ticked and (abs(row["x"] - last_frame["x"]) > 0.01 or abs(row["y"] - last_frame["y"]) > 0.01):
            print(f"  frame {row['frame']} timeScale {row['timeScale']}: moved {row['x'] - last_frame['x']:+.2f},{row['y'] - last_frame['y']:+.2f}"
                  " with no physics tick")
        last_frame, ticked = row, False

def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--show":
        show(sys.argv[2])
        return
    parser = argparse.ArgumentParser()
    parser.add_argument("name")
    parser.add_argument("route")
    parser.add_argument("ticks")
    parser.add_argument("--savestate", default="")
    parser.add_argument("--capture-at", type=int)
    parser.add_argument("--interpolation", choices=["off", "game"], default="off",
                        help="game: keep the player body's interpolation as the game sets it (the TAS forces it off)")
    parser.add_argument("--omni", action="store_true", help="play with the omnidirectional dash too (restored after)")
    parser.add_argument("--fps", type=int, default=0, help="playback frame rate (default 280), e.g. 60, 144, 240")
    args = parser.parse_args()
    body = {"name": args.name, "route": args.route, "savestateId": args.savestate, "ticks": json.load(open(args.ticks)),
            "interpolation": args.interpolation, "fps": args.fps,
            "omniDash": args.omni}
    if args.capture_at is not None:
        body["captureAt"] = args.capture_at
    started = call("/api/probe/run", body)
    if not started.get("ok"):
        raise SystemExit(started)
    while call("/api/probe/status").get("running"):
        time.sleep(0.5)
    show(args.name)

if __name__ == "__main__":
    main()
