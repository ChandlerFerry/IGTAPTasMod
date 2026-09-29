"""Every tech demo, turbo against the offline engine.

For each tech with a lab demo (the studio's Tech tab), the running game captures the demo in turbo from the start its playback
uses (`POST /api/enginesim/trace {tech}` -> enginesim/tech-<id>.json + world), then `IGTAP.EngineSim replay` runs the same
inputs offline and reports the first tick where they differ.

  tech-check.py                 every tech with a lab demo
  tech-check.py ID [ID ...]     just these
  --exe PATH                    the offline engine to use (default: enginesim/Cli/bin/Release/net10.0/IGTAP.EngineSim.exe)

Exit code 1 when any demo differs or fails.
"""
import subprocess
import sys
from pathlib import Path

from gamepaths import TRACES, call, call_retry

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "enginesim" / "Cli" / "bin" / "Release" / "net10.0" / "IGTAP.EngineSim.exe"

def trace(tech):
    return call_retry("/api/enginesim/trace", {"tech": tech}, 60, 2, busy=("loading", "Busy"), timeout=900)

def main():
    args = sys.argv[1:]
    exe = EXE
    if "--exe" in args:
        at = args.index("--exe")
        exe = Path(args[at + 1])
        del args[at:at + 2]
    techs = call("/api/state", timeout=900)["techs"]["list"]
    wanted = [t for t in techs if t.get("labs") and (not args or t["id"] in args)]
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
