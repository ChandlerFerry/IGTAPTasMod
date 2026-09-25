"""Where the game and the plugin's config live. IGTAP_GAME overrides the install (Directory.Build.props: IgtapGame)."""
import os
from pathlib import Path

GAME = Path(os.environ.get("IGTAP_GAME",
                           r"C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer"))
CONFIG = GAME / "BepInEx" / "config" / "IGTAPTasMod"
TRACES = CONFIG / "enginesim"
