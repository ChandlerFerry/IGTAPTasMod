"""Every tech demo, turbo against the offline engine.

For each tech with a demo (the studio's Tech tab), the running game captures the demo in turbo from the start its playback
uses (`POST /api/enginesim/trace {tech}` -> enginesim/tech-<id>.json + world), then `IGTAP.EngineSim replay` runs the same
inputs offline and reports the first tick where they differ.

  tech-check.py                 every tech with a demo
  tech-check.py ID [ID ...]     just these
  --exe PATH                    the offline engine to use (default: enginesim/Cli/bin/Release/net10.0/IGTAP.EngineSim.exe)

A Full-TAS demo (fulltas/VMAN_NONE) needs that run's fresh new game in the game first (POST /api/session/newgame).
Exit code 1 when any demo differs or fails.
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from gamepaths import TRACES

API = "http://127.0.0.1:17426"
ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "enginesim" / "Cli" / "bin" / "Release" / "net10.0" / "IGTAP.EngineSim.exe"

def call(path, body=None, method="POST"):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(API + path, data=data, method=method)
    try:
        with urllib.request.urlopen(request, timeout=900) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        return json.loads(error.read())

def trace(tech):
    for _ in range(60):
        result = call("/api/enginesim/trace", {"tech": tech})
        if result.get("ok") or "loading" not in str(result.get("error", "")) and "Busy" not in str(result.get("error", "")):
            return result
        time.sleep(2)
    return result

def main():
    args = sys.argv[1:]
    exe = EXE
    if "--exe" in args:
        at = args.index("--exe")
        exe = Path(args[at + 1])
        del args[at:at + 2]
    techs = call("/api/state", method="GET")["techs"]["list"]
    wanted = [t for t in techs if t["confirmed"] and (not args or t["id"] in args)]
    bad = 0
    for tech in wanted:
        result = trace(tech["id"])
        if not result.get("ok"):
            bad += 1
            print(f"{tech['id']:34} TRACE FAILED: {result.get('error')}")
            continue
        path = TRACES / ("tech-" + tech["id"] + ".json")
        replay = subprocess.run([str(exe), "replay", str(path)], capture_output=True, text=True)
        lines = [l for l in replay.stdout.splitlines() if l.strip()] or [replay.stderr.strip()[:300]]
        bad += replay.returncode != 0
        print(f"{tech['id']:34} {lines[0].split(': ', 1)[-1]}")
        for line in lines[1:3]:
            print(" " * 36 + line.strip())
    sys.exit(1 if bad else 0)

if __name__ == "__main__":
    main()
