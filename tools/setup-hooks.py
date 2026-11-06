import subprocess
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
subprocess.run(["git", "config", "core.hooksPath", ".githooks"], cwd=root, check=True)
path = subprocess.run(["git", "config", "core.hooksPath"], cwd=root, check=True, capture_output=True, text=True).stdout.strip()
print(f"core.hooksPath = {path}")
sys.exit(0 if path == ".githooks" else 1)
