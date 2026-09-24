#!/usr/bin/env python3
"""Rebuild the plugin DLL (no web build), restart the game with it, and put the player on a course."""
import filecmp
import json
import shutil
import subprocess
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path(r"C:\Users\deafwave\Downloads\IGTAP_Real")
DLL = ROOT / "bin" / "Release" / "netstandard2.1" / "IGTAPTasMod.dll"
TARGET = GAME / "BepInEx" / "plugins" / "IGTAPTasMod.dll"
API = "http://127.0.0.1:17426"


def post(path, body=None, timeout=120):
    request = urllib.request.Request(API + path, data=json.dumps(body or {}).encode(), method="POST")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read())


def main():
    subprocess.run(["dotnet", "build", str(ROOT / "IGTAPTasMod.csproj"), "-c", "Release", "-p:SkipDeploy=true",
                    "-nologo", "-v:q"], check=True)
    subprocess.run(["taskkill", "/IM", "IGTAPfullGame.exe", "/F"], capture_output=True)
    for _ in range(30):
        try:
            shutil.copyfile(DLL, TARGET)
            break
        except OSError:
            time.sleep(1)
    if not filecmp.cmp(DLL, TARGET, shallow=False):
        raise SystemExit("deploy failed: plugin DLL is still locked")
    subprocess.Popen([str(GAME / "IGTAPfullGame.exe")], cwd=GAME)
    for _ in range(90):
        time.sleep(2)
        try:
            urllib.request.urlopen(API + "/api/oracle/status", timeout=2)
            break
        except OSError:
            continue
    print(json.dumps(post("/api/oracle/bootstrap"))[:300])


if __name__ == "__main__":
    main()
