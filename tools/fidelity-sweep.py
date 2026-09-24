"""Replay every recorded native Course 2 run through the simulator and report where each first diverges.

Usage: python tools/fidelity-sweep.py [out.json|-] [extra-routes.json ...]
Matches each result to its route by id across every routes file on disk; when a route was
edited after the run, the version that tracks the native trace longest is used.
"""
import argparse, glob, json, re, subprocess, sys, tempfile
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

REPO = Path(__file__).resolve().parent.parent
CONFIG = Path(r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod")
MODEL = CONFIG / "simulation" / "live-simulation-model.json"
SIM = REPO / "simulator" / "bin" / "Release" / "net8.0" / "IGTAP.FastSim.exe"
ARCHIVE = REPO / "planning" / "fidelity-routes" / "routes-archive.json"
EXTRA = sys.argv[2:]  # optional extra routes files to fold into the archive
LINE = re.compile(r"native=(-?\d+); simulated=(-?\d+); peakError=([\d.]+)px\s+first >=1/5/10px: ([^/\s;]+)/([^/\s;]+)/([^/\s;]+)")


def load(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def route_versions():
    files = glob.glob(str(CONFIG / "routes*")) + glob.glob(str(REPO / "planning" / "**" / "routes*.json"), recursive=True) + EXTRA
    versions = {}
    for path in files:
        doc = load(path)
        for macro in (doc or {}).get("macros", []):
            key = json.dumps(macro["steps"], sort_keys=True)
            versions.setdefault(macro["id"], {})[key] = macro
    # Archive every route version ever seen, so deleting a route in the studio
    # does not drop its native runs from the regression corpus.
    ARCHIVE.parent.mkdir(parents=True, exist_ok=True)
    with open(ARCHIVE, "w", encoding="utf-8") as f:
        json.dump({"macros": [m for v in versions.values() for m in v.values()]}, f)
    return {rid: list(v.values()) for rid, v in versions.items()}


def audit(result_path, macro, tmp):
    routes_path = str(Path(tmp) / f"{os.getpid()}-{abs(hash((result_path, id(macro))))}.json")
    with open(routes_path, "w", encoding="utf-8") as f:
        json.dump({"macros": [macro]}, f)
    out = subprocess.run([SIM, "audit-reference", MODEL, "course 2", routes_path, macro["name"], result_path,
                          routes_path + ".audit.json"], capture_output=True, text=True, encoding="utf-8").stdout
    m = LINE.search(out)
    if not m:
        return None
    tick = lambda s: int(s) if s.lstrip("-").isdigit() else None
    return {"native": int(m[1]), "simulated": int(m[2]), "peakErrorPx": float(m[3]),
            "first1": tick(m[4]), "first5": tick(m[5]), "first10": tick(m[6])}


def main():
    versions = route_versions()
    jobs = []
    for path in sorted(glob.glob(str(CONFIG / "results" / "*.json"))):
        r = load(path)
        if not r or not r.get("earlyTrace") or not r.get("deterministicPhysics"):
            continue
        if not r["earlyTrace"].startswith("0,4783."):  # course 2 restart pose only
            continue
        if r.get("routeId") in versions:
            jobs.append((path, r, versions[r["routeId"]]))
    corpus = load(str(REPO / "planning" / "fidelity-routes" / "oracle-corpus.json")) or []
    for entry in corpus:
        r = load(entry["resultPath"])
        if r and r.get("earlyTrace"):
            jobs.append((entry["resultPath"], dict(r, routeName="[oracle] " + entry["name"]),
                         [{"id": r.get("routeId", "oracle"), "name": entry["name"], "steps": entry["steps"]}]))
    rows = []
    with tempfile.TemporaryDirectory() as tmp, ThreadPoolExecutor(os.cpu_count()) as pool:
        def run(job):
            path, r, macros = job
            scored = [(a, m) for m in macros if (a := audit(path, m, tmp))]
            if not scored:
                return None
            a, m = max(scored, key=lambda x: x[0]["first10"] or 10**9)
            return {"result": Path(path).name, "route": r["routeName"], "success": r["success"], **a}
        rows = [row for row in pool.map(run, jobs) if row]
    rows.sort(key=lambda x: x["first10"] or 10**9)
    for row in rows:
        print(f"{str(row['first10']):>5} {str(row['first5']):>5} {row['native']:>5} {row['simulated']:>5} {row['peakErrorPx']:>8.1f}  {'ok ' if row['success'] else 'die'} {row['route']}")
    print("first10 first5 native simulated peakErrorPx outcome route")
    exact_finish = [r for r in rows if r["success"] and r["native"] == r["simulated"]]
    print(f"runs={len(rows)} never>=10px={sum(r['first10'] is None for r in rows)} exact-finish={len(exact_finish)}/{sum(r['success'] for r in rows)}")
    if len(sys.argv) > 1 and sys.argv[1] != "-":
        with open(sys.argv[1], "w", encoding="utf-8") as f:
            json.dump(rows, f, indent=1)


if __name__ == "__main__":
    main()
