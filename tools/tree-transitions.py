"""Usage: tree-transitions.py [--write] | tree-transitions.py --drop L C"""
import json, shutil, sys

from gamepaths import CONFIG

COURSES = CONFIG / "courses.json"
LEVELS = [0, 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 22, 27]
# Kept levels per course: RULES.md 21.
COURSE_LEVELS = {6: [0], 7: [0, 3], 8: [10], 9: [0, 3], 10: [l for l in LEVELS if l >= 13]}
START = (22643.9, -364.685)


def entry(level, course, start):
    return {"id": 1000 + 10 * level + course, "name": f"tree{level}to{course}", "x": START[0], "y": START[1],
            "endX": start["x"], "endY": start["y"] - 12, "endW": 32.0, "endH": 32.0, "from": f"tree{level}", "to": str(course),
            "toCourse": 0, "minCapabilities": "wjdsdjbs", "maxCapabilities": None, "overgrown": False, "tree": level}


def main():
    courses = json.load(open(COURSES, encoding="utf-8-sig"))
    if len(sys.argv) > 1 and sys.argv[1] == "--drop":
        name = f"tree{sys.argv[2]}to{sys.argv[3]}"
        kept = [c for c in courses if c.get("name") != name]
    else:
        ids = {1000 + 10 * level + course for level in LEVELS for course in range(6, 11)}
        kept = [c for c in courses if c.get("name") != "6to10" and c["id"] not in ids]
        starts = {c["id"]: c for c in courses if not c.get("to") and not c.get("endW")}
        kept += [entry(level, course, starts[course]) for level in reversed(LEVELS) for course in range(6, 11)
                 if level in COURSE_LEVELS.get(course, LEVELS)]
    print(f"{len(courses)} -> {len(kept)} targets; removed: {sorted({c.get('name') for c in courses} - {c.get('name') for c in kept})}")
    if "--write" in sys.argv or sys.argv[1:2] == ["--drop"]:
        shutil.copy(COURSES, COURSES.with_suffix(".json.bak-tree"))
        json.dump(kept, open(COURSES, "w", encoding="utf-8"), indent=2)


if __name__ == "__main__":
    main()
