import json
import sys
from collections import Counter

from gamepaths import CONFIG, TRACES

LOCAL = ["GLOBAL", "Movement", "cloneCount", "cashPerLoop", "fastCloneChance", "bigCloneChance", "prestige",
         "cloneMult", "DUMMY_cloneCountPlural", "GreenCloneRewardBase", "activateNextBreakerLights",
         "enableCloneDustGeneration", "moreWatts", "baseRP"]
GLOBAL = ("cashPerLoop fastCloneChance maxCloneFastness bigCloneChance maxCloneBigness cloneMult spawnNewAtom "
          "atomLevelChance greenCloneClance treeGrowth unlockPrestige openGate increasedWatts increasedGreenPower "
          "increasedNuclearPower area2DoorOpen zipMoversUnlocked exemptCourseFromAtomPrestige tripleThreatIncrease "
          "unlockJiggleDrops additionalCDdigits compBoostUnlocked compBoostTime compBoostStrength IncreasedCloneDust "
          "moreRefreshOrbs unlockTeleporters makeCourseBuyMax redCloneChance blueCloneChance NpCapIsSoftcap "
          "compBoostDoublesNP vmanTime startVmanSpeedrun moreWatts moreGP increasedRP moreRP RPcap "
          "GPAtomsDontResetCourses NpGenerationSpeed VmanStartsWithOmniDash wattPower MoreRpCap").split()
MOVEMENT = "dash wallJump doubleJump swapBlocksOnce unlockBlockSwap endDemo OmniDash freeVmanOmnidash".split()
CURRENCY = ["W", "GP", "NP", "number", "CD", "RP", "BP"]
ROW_BAND = 60


def load(path):
    world = json.load(open(path, encoding="utf-8-sig"))
    objects = world["objects"]
    ref = {}
    origin = (0.0, 0.0)
    for o in objects:
        ref[o["id"]] = ref[o["transformId"]] = o
        for c in o["components"]:
            ref[c["id"]] = o
            if c["type"] == "FloatingOrigin":
                p = fields(c)["currentOrigin"]
                origin = (p["x"], p["y"])
    return objects, ref, origin


def hierarchy(o, ref):
    names = []
    while o:
        names.append(o["name"])
        o = ref.get(o.get("parent"))
    return "/".join(reversed(names))


def component(o, kind):
    return next((c for c in o["components"] if c["type"] == kind), None)


def fields(c):
    return {f[1]: f[2] for f in c.get("fields", [])}


# The export's positions are in its capture's scene frame; courses.json's are the game world's (scene - FloatingOrigin).
def place(p, origin):
    return p["x"] - origin[0], p["y"] - origin[1]


def trigger(o, origin):
    for c in o["components"]:
        if c["type"] == "UnityEngine.BoxCollider2D" and c["props"]["isTrigger"]:
            b = c["props"]["bounds"]
            return (*place(b["m_Center"], origin), 2 * b["m_Extents"]["x"], 2 * b["m_Extents"]["y"])
    return None


def courses(objects, ref, origin):
    out = {}
    for o in objects:
        gate = component(o, "startGate")
        if not gate:
            continue
        course = o
        while course and not component(course, "courseScript"):
            course = ref.get(course.get("parent"))
        reset = place(ref[fields(gate)["resetPoint"]["$ref"]]["position"], origin)
        out[fields(component(course, "courseScript"))["courseNumber"]] = (hierarchy(course, ref), reset)
    return out


def boxes(objects, ref, course_paths, origin):
    out = []
    for o in objects:
        c = component(o, "upgradeBox")
        if not c:
            continue
        f = fields(c)
        local = LOCAL[f["upgrade"]]
        kind = (local + ":" + GLOBAL[f["globalUpgrade"]] if local == "GLOBAL"
                else local + ":" + MOVEMENT[f["movementUpgrade"]] if local == "Movement" else local)
        path = hierarchy(o, ref)
        course = next((n for n, p in course_paths.items() if path.startswith(p + "/localUpgrades/")), None)
        secret = f["isSecret"]
        x, y = place(o["position"], origin)
        out.append({"path": path, "sibling": o["siblingIndex"], "x": x, "y": y,
                    "active": o["activeInHierarchy"], "kind": kind, "cost": f["baseUpgradeCost"],
                    "currency": CURRENCY[f["upgradeCurrency"]], "cap": f["Cap"], "secret": secret,
                    "number": int(f["achievement"]) if secret and str(f["achievement"]).isdigit() else None,
                    "course": course, "end": trigger(o, origin)})
    return out


def classify(box, rows):
    if not box["active"]:
        return "not in the world yet"
    if box["cost"] == 0:
        return "bonus"
    row = rows.get(box["course"])
    return "course-side" if row is not None and abs(box["y"] - row) <= ROW_BAND else "out of the way"


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    world = args[0] if args else str(max(TRACES.glob("*.world.json"), key=lambda p: p.stat().st_mtime))
    objects, ref, origin = load(world)
    course_info = courses(objects, ref, origin)
    every = boxes(objects, ref, {n: p for n, (p, _) in course_info.items()}, origin)
    rows = {}
    for n in course_info:
        ys = Counter(round(b["y"]) for b in every if b["course"] == n and b["active"] and b["cap"] > 1 and not b["secret"])
        if ys:
            rows[n] = ys.most_common(1)[0][0]
    odd = [b for b in every if b["cap"] == 1 or b["secret"] or b["kind"].startswith("Movement:")]
    for b in odd:
        b["group"] = classify(b, rows)

    known = json.load(open(CONFIG / "courses.json", encoding="utf-8-sig"))
    starts = {c["id"]: (c["x"], c["y"]) for c in known if not c.get("name") and not c.get("to")}
    for n, (_, reset) in course_info.items():
        starts.setdefault(n, reset)
    bonus_ends = {c["name"]: (c["endX"], c["endY"]) for c in known if c.get("endW") and not c.get("to")}

    def solved_as(b):
        return next((name for name, (x, y) in bonus_ends.items()
                     if b["end"] and abs(x - b["end"][0]) < 2 and abs(y - b["end"][1]) < 2), None)

    def order(b):
        return (not b["secret"], b["number"] is None, b["number"] or 0, b["course"] or 99, b["x"])

    print(f"{world}: {len(every)} upgrade boxes, {len(odd)} one-off, {sum(b['secret'] for b in every)} secrets")
    for group in ("bonus", "course-side", "out of the way", "not in the world yet"):
        rows_in = sorted((b for b in odd if b["group"] == group), key=order)
        print(f"\n{group} ({len(rows_in)})")
        for b in rows_in:
            tag = (f"secret #{b['number']}" if b["number"] else "secret #?") if b["secret"] else ""
            cost = f"{b['cost']:g} {b['currency']}"
            where = solved_as(b) or ""
            print(f"  {where:8} {tag:11} {cost:>12}  {b['kind']:34} ({b['x']:.0f}, {b['y']:.0f})  {b['path']}#{b['sibling']}")

    if "--write" not in sys.argv:
        return
    added = []
    next_id = max([c["id"] for c in known if 101 <= c["id"] < 200] + [100]) + 1
    next_name = max(int(c["name"][5:]) for c in known if (c.get("name") or "").startswith("bonus") and c["name"][5:].isdigit()) + 1
    for b in sorted((b for b in odd if b["group"] == "bonus"), key=order):
        if solved_as(b) or not b["end"] or b["end"][2] <= 0:
            continue
        course = b["course"] or min(starts, key=lambda n: (starts[n][0] - b["x"]) ** 2 + (starts[n][1] - b["y"]) ** 2)
        x, y = starts[course]
        ex, ey, ew, eh = b["end"]
        known.append({"id": next_id, "name": f"bonus{next_name}", "x": x, "y": y, "endX": ex, "endY": ey, "endW": ew,
                      "endH": eh, "from": None, "to": None, "toCourse": 0, "minCapabilities": "wj"})
        added.append(f"bonus{next_name} (id {next_id}): {b['path']}#{b['sibling']}, from course {course}'s reset")
        next_id += 1
        next_name += 1
    json.dump(known, open(CONFIG / "courses.json", "w", encoding="utf-8"), indent=2)
    print("\ncourses.json: " + ("\n  ".join(["added"] + added) if added else "nothing new"))


if __name__ == "__main__":
    main()
