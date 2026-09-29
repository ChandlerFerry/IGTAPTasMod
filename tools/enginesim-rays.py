import json
import os
import random
import subprocess
import sys
import tempfile
from pathlib import Path

from gamepaths import call

EXE = Path(__file__).resolve().parent.parent / "enginesim" / "Cli" / "bin" / "Release" / "net10.0" / "IGTAP.EngineSim.exe"

def probe_rays(x, y, ground, blocking):
    rays = []
    for facing in (1, -1):
        f = facing
        for dx, mask in ((-15, ground), (-9, blocking), (25, ground), (21, blocking), (6, ground)):
            rays.append([x + dx * f, y - 18, 0, -1, 22, mask])
        for dx in (-15, 25):
            rays.append([x + dx * f, y - 18, 0, -1, 5, ground])
            rays.append([x + dx * f, y - 18, 0, -1, -25, ground])
        for extra in (0, 3):
            for oy in (-18, 19):
                rays.append([x + 15 * f, y + oy, f, 0, 12 + extra, ground])
                rays.append([x + 15 * f, y + oy, f, 0, 12 + extra, blocking])
        for num, bonus in ((10, 0), (20, 2)):
            rays.append([x + 24 * f, y + num, f, 0, 6 + bonus, ground])
            rays.append([x + (27 + bonus) * f, y + num, 0, -1, 33 + num, ground])
        rays.append([x + 15 * f, y - 18, f, 0, 14, ground])
        rays.append([x + 15 * f, y + 19, f, 0, 14, ground])
    return rays

def main():
    trace = json.loads(Path(sys.argv[1]).read_text())
    exe = sys.argv[2] if len(sys.argv) > 2 else str(EXE)
    movement = {f[1]: f[2] for f in trace["start"]["movement"]}
    ground, blocking = movement["groundRaycastLayer"], movement["blockingGroundRaycastLayer"]
    rng = random.Random(1)
    rays = []
    for tick in trace["ticks"]:
        x, y = tick["position"]["x"], tick["position"]["y"]
        rays += probe_rays(x, y, ground, blocking)
        rays += probe_rays(x + rng.uniform(-40, 40), y + rng.uniform(-40, 40), ground, blocking)
    rays = [[float(v) if i < 5 else int(v) for i, v in enumerate(r)] for r in rays]
    unity = call("/api/enginesim/raycast", {"rays": rays})["hits"]
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
        json.dump({"rays": rays}, f)
    try:
        offline = json.loads(subprocess.run([exe, "rays", sys.argv[1], f.name], capture_output=True, text=True, check=True).stdout)["hits"]
    finally:
        os.unlink(f.name)
    bad = [(r, u, o) for r, u, o in zip(rays, unity, offline) if u != o]
    print(f"{len(rays)} rays, {len(rays) - len(bad)} identical, {len(bad)} different")
    for r, u, o in bad[:15]:
        print("  ray", r, "\n    unity  ", u, "\n    offline", o)
    sys.exit(1 if bad else 0)

if __name__ == "__main__":
    main()
