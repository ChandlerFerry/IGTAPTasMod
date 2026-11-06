import json
import os
import re
import time
import urllib.error
import urllib.request
from pathlib import Path

GAME = Path(os.environ.get("IGTAP_GAME",
                           r"C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer"))
CONFIG = GAME / "BepInEx" / "config" / "IGTAPTasMod"
TRACES = CONFIG / "enginesim"
API = "http://127.0.0.1:17426"
ROUTE_NAME = re.compile(r"^([^_\s]+)_([A-Z][A-Z0-9_]*) (?:forwards |backwards )?(farm )?([\d.]+)$")


def call(path, body=None, method=None, timeout=600):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(API + path, data=data, method=method or ("GET" if body is None else "POST"))
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        return json.loads(error.read())


def call_retry(path, body, tries, wait, busy=("loading",), timeout=600):
    for _ in range(tries):
        result = call(path, body, timeout=timeout)
        if result.get("ok") or not any(b in str(result.get("error", "")) for b in busy):
            return result
        time.sleep(wait)
    return result


def course_ids():
    ids = {str(c.get("name") or c["id"]): c["id"] for c in json.load(open(CONFIG / "courses.json", encoding="utf-8-sig"))}
    ids.update({str(c): c for c in ids.values()})
    return ids


def category_id(label):
    return label.replace("_", "").lower()
