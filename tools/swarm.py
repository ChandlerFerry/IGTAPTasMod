"""Headless solver workers alongside the studio's game.

    python tools/swarm.py start 3 wjdash 8   # 8 workers solving course 3, wall jump + dash
    python tools/swarm.py status
    python tools/swarm.py stop

Each worker runs from its own folder under IGTAP_Workers/ (copied exe and loader, junctions to the game data,
plugins and config/IGTAPTasMod): BepInEx's disk log has 5 slots per folder and a 6th instance crashes on it.
Workers run on ports 17427+ with IGTAP_ORACLE=1 (no browser, no game saves, never write routes.json). Every
instance shares its best through config/IGTAPTasMod/solver-share/; the studio's game saves the routes, so start
its solver on the same course and capabilities too.
"""
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request

GAME = r"C:\Users\deafwave\Downloads\IGTAP_Real\IGTAPfullGame.exe"
ROOT = os.path.dirname(GAME)
WORKERS = os.path.join(os.path.dirname(ROOT), "IGTAP_Workers")
PIDS = os.path.join(ROOT, "BepInEx", "config", "IGTAPTasMod", "swarm-pids.json")
FIRST_PORT = 17427


def call(port, path, body=None, timeout=10):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data, method="GET" if body is None else "POST")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read() or b"null")


def wait_up(port, seconds=180):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            return call(port, "/api/state")
        except OSError:
            time.sleep(1)
    raise SystemExit(f"worker on {port} did not come up")


def junction(link, target):
    if not os.path.exists(link):
        subprocess.run(["cmd", "/c", "mklink", "/J", link, target], check=True, capture_output=True)


def worker_folder(port):
    folder = os.path.join(WORKERS, f"w{port}")
    os.makedirs(os.path.join(folder, "BepInEx", "config"), exist_ok=True)
    for name in ["IGTAPfullGame.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "winhttp.dll", "doorstop_config.ini", ".doorstop_version"]:
        shutil.copy2(os.path.join(ROOT, name), folder)
    for name in ["IGTAPfullGame_Data", "MonoBleedingEdge", "D3D12"]:
        junction(os.path.join(folder, name), os.path.join(ROOT, name))
    for name in ["core", "plugins", "patchers"]:
        junction(os.path.join(folder, "BepInEx", name), os.path.join(ROOT, "BepInEx", name))
    junction(os.path.join(folder, "BepInEx", "config", "IGTAPTasMod"), os.path.join(ROOT, "BepInEx", "config", "IGTAPTasMod"))
    with open(os.path.join(ROOT, "BepInEx", "config", "BepInEx.cfg"), encoding="utf-8") as f:
        config = f.read()
    disk = config.index("[Logging.Disk]")
    config = config[:disk] + config[disk:].replace("Enabled = true", "Enabled = false", 1)
    with open(os.path.join(folder, "BepInEx", "config", "BepInEx.cfg"), "w", encoding="utf-8") as f:
        f.write(config)
    return folder


def start(course, capabilities, count):
    workers = []
    for i in range(count):
        port = FIRST_PORT + i
        folder = worker_folder(port)
        env = dict(os.environ, IGTAP_ORACLE="1", IGTAP_TAS_PORT=str(port))
        process = subprocess.Popen([os.path.join(folder, "IGTAPfullGame.exe"), "-batchmode", "-nographics",
                                    "-logFile", os.path.join(folder, "Player.log")], env=env, cwd=folder,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        workers.append({"port": port, "pid": process.pid})
        with open(PIDS, "w") as f:
            json.dump(workers, f)
        # Start one at a time: instances starting together race on BepInEx's startup files.
        wait_up(port)
        print(port, "bootstrap:", call(port, "/api/oracle/bootstrap", {}, timeout=120).get("ok"))
        print(port, "solver:", call(port, "/api/solver/start", {"course": course, "capabilities": capabilities}))


def workers():
    try:
        with open(PIDS) as f:
            return json.load(f)
    except FileNotFoundError:
        return []


def status():
    for w in workers():
        try:
            s = call(w["port"], "/api/state")["solver"]
            print(w["port"], s["best"], s["evaluations"], "runs |", s["speed"], "|", (s["log"] or [""])[0])
        except OSError as e:
            print(w["port"], "down:", e)


def stop():
    for w in workers():
        subprocess.run(["taskkill", "/PID", str(w["pid"]), "/F"], capture_output=True)
    if os.path.exists(PIDS):
        os.remove(PIDS)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else "status"
    if command == "start":
        start(int(sys.argv[2]), sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 8)
    elif command == "stop":
        stop()
    else:
        status()
