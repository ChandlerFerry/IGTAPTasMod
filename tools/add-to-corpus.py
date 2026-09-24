"""Add an in-game run to the fidelity corpus: python tools/add-to-corpus.py <routeId> <resultPath> [name]"""
import json, sys
from pathlib import Path
CORPUS = Path(__file__).resolve().parent.parent / "planning" / "fidelity-routes" / "oracle-corpus.json"
ROUTES = Path(r"C:\Users\deafwave\Downloads\IGTAP_Real\BepInEx\config\IGTAPTasMod\routes.json")
route_id, result = sys.argv[1], sys.argv[2]
route = next(m for m in json.loads(ROUTES.read_text(encoding="utf-8"))["macros"] if m["id"] == route_id)
corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
corpus.append({"name": sys.argv[3] if len(sys.argv) > 3 else route["name"], "steps": route["steps"], "resultPath": result})
CORPUS.write_text(json.dumps(corpus), encoding="utf-8")
print(f"corpus={len(corpus)}")
