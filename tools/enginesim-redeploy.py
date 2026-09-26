import filecmp
import json
import shutil
import subprocess
import time
import urllib.request
from pathlib import Path

from gamepaths import GAME

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "bin" / "Release" / "netstandard2.1" / "IGTAPTasMod.dll"
WEB = ROOT / "web" / "out"
TARGET = GAME / "BepInEx" / "plugins" / "IGTAPTasMod.dll"
TARGET_WEB = GAME / "BepInEx" / "plugins" / "IGTAPTasWeb"
API = "http://127.0.0.1:17426"


def post(path, body=None, timeout=120):
    request = urllib.request.Request(API + path, data=json.dumps(body or {}).encode(), method="POST")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read())


def main():
    subprocess.run(["dotnet", "build", str(ROOT / "IGTAPTasMod.csproj"), "-c", "Release", "-p:SkipDeploy=true",
                    "-nologo", "-v:q"], check=True)
    subprocess.run("npm run build", cwd=ROOT / "web", shell=True, check=True)
    exe = str(GAME / "IGTAPfullGame.exe").replace("'", "''")
    subprocess.run(["powershell", "-NoProfile", "-Command", "Get-Process IGTAPfullGame -ErrorAction SilentlyContinue"
                    f" | Where-Object {{ $_.Path -eq '{exe}' }} | Stop-Process -Force"], capture_output=True)
    subprocess.run(["taskkill", "/IM", "IGTAP.EngineSim.exe", "/F"], capture_output=True)
    for _ in range(30):
        try:
            shutil.copyfile(DLL, TARGET)
            break
        except OSError:
            time.sleep(1)
    if not filecmp.cmp(DLL, TARGET, shallow=False):
        raise SystemExit("deploy failed: plugin DLL is still locked")
    shutil.rmtree(TARGET_WEB, ignore_errors=True)
    shutil.copytree(WEB, TARGET_WEB)
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
