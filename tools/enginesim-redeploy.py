import filecmp
import json
import shutil
import subprocess
import time
import urllib.request
from pathlib import Path

from gamepaths import API, GAME, call

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "plugin" / "bin" / "Release" / "netstandard2.1" / "IGTAPTasMod.dll"
WEB = ROOT / "web" / "out"
TARGET = GAME / "BepInEx" / "plugins" / "IGTAPTasMod.dll"
TARGET_WEB = GAME / "BepInEx" / "plugins" / "IGTAPTasWeb"
def main():
    subprocess.run(["dotnet", "build", str(ROOT / "plugin" / "IGTAPTasMod.csproj"), "-c", "Release", "-p:SkipDeploy=true",
                    "-nologo", "-v:q"], check=True)
    subprocess.run("npm run build", cwd=ROOT / "web", shell=True, check=True)
    exe = str(GAME / "IGTAPfullGame.exe").replace("'", "''")
    subprocess.run(["powershell", "-NoProfile", "-Command", "Get-Process IGTAPfullGame -ErrorAction SilentlyContinue"
                    f" | Where-Object {{ $_.Path -eq '{exe}' }} | Stop-Process -Force"], capture_output=True)
    # Only the solver the plugin launches (the shared build); other IGTAP.EngineSim copies (private search bins) keep running.
    solver = str(ROOT / "enginesim" / "Cli" / "bin" / "Release" / "net10.0" / "IGTAP.EngineSim.exe").replace("'", "''")
    subprocess.run(["powershell", "-NoProfile", "-Command", "Get-Process IGTAP.EngineSim -ErrorAction SilentlyContinue"
                    f" | Where-Object {{ $_.Path -eq '{solver}' }} | Stop-Process -Force"], capture_output=True)
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
    print(json.dumps(call("/api/oracle/bootstrap", {}, timeout=120))[:300])

if __name__ == "__main__":
    main()
