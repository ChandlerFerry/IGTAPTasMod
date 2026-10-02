"""Move a courses.json target's start to the in-game checkpoint nearest a point.

    python tools/move-start.py bonus8 20208 13038          # dry run: prints the checkpoint and the change
    python tools/move-start.py bonus8 20208 13038 --write  # writes courses.json (keeps courses.json.bak)

The target is courses.json's name or id. Checkpoints come from the studio's cached maps (config enginesim/map-*.json
markers, the spawn's centre); courses.json stores the checkpoint itself, 10 u above it (bonus5: marker 4525, start
4535.015).
"""
import glob, json, shutil, sys
from gamepaths import CONFIG

target, px, py = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
seen = {}
for f in glob.glob(str(CONFIG / "enginesim" / "**" / "map-*.json"), recursive=True):
    try:
        data = json.load(open(f, encoding="utf-8-sig"))
        markers = data.get("markers", []) if isinstance(data, dict) else []
    except (ValueError, OSError):
        continue
    for m in markers:
        if m["kind"] == "checkpoint" and m.get("spawn"):
            seen[(m["name"], m["x"], m["y"])] = f
if not seen:
    sys.exit("No cached map has checkpoints: open the studio map on any Full-TAS run once.")
name, x, y = min(seen, key=lambda k: (k[1] - px) ** 2 + (k[2] - py) ** 2)
start = (x, round(y + 10.015, 3))
print(f"{name} at ({x}, {y}), {((x - px) ** 2 + (y - py) ** 2) ** .5:.0f} u from the point; start {start}")

path = CONFIG / "courses.json"
courses = json.load(open(path, encoding="utf-8-sig"))
course = next((c for c in courses if str(c.get("name") or c["id"]) == target or str(c["id"]) == target), None)
if course is None:
    sys.exit(f"{target} is not in courses.json.")
print(f"{target}: ({course['x']}, {course['y']}) -> {start}")
if "--write" in sys.argv:
    shutil.copy(path, str(path) + ".bak")
    course["x"], course["y"] = start
    json.dump(courses, open(path, "w", encoding="utf-8"), indent=2)
    print("written")
