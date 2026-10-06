"""The tree transitions (docs/superpowers/specs/2026-10-05-tree-transitions-design.md): from the checkpoint by the atom
room (bonus11's start) to Courses 6-10's start checkpoints (the course's reset point, a 32 u arrival box on it as a
transition to a bonus has), The Tree at each level that changes the world. Drops 6to10. Course 6 keeps only level 0
(tree0to6): its start is accessible on Course 0, so tree1to6..tree27to6 are dropped (2026-10-05). Course 7 keeps only
levels 0 and 3 (tree1to7, tree2to7 play as tree0to7; tree5to7 up are dropped, 2026-10-05). Course 8 keeps only
level 10 (tree10to8); every other tree*to8 level is dropped (2026-10-05). Course 9 keeps only levels 0 and 3
(2026-10-05). Course 10 keeps only levels 13 and up (tree0to10..tree12to10 are dropped, 2026-10-05).

  tree-transitions.py            what it would write
  tree-transitions.py --write    write courses.json (a .bak-tree copy first)
  tree-transitions.py --drop L C remove tree<L>to<C> (a target no category can finish)
"""
import json, shutil, sys

from gamepaths import CONFIG

COURSES = CONFIG / "courses.json"
# Each group of levels whose boxes change nothing in the world, by its highest (the spec's table).
LEVELS = [0, 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 22, 27]
# Course 6 keeps only level 0 (tree0to6): its start is accessible on Course 0, so the tree1to6..tree27to6 targets
# are dropped (2026-10-05). Course 7 keeps only levels 0 and 3 (2026-10-05). Course 8 keeps only level 10 (tree10to8); every other tree*to8 level is dropped
# (2026-10-05). Course 9 keeps only levels 0 and 3 (2026-10-05). Course 10 keeps only levels 13 and up (2026-10-05).
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
        # Its own grid ids are stripped -- the dropped Course 6 levels among them -- and the kept levels
        # re-added; a hand-made tree target stays.
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
