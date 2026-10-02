---
name: move-target-start
description: Use when asked to change where a course, bonus or transition starts ("make bonus8 start at the checkpoint near X, Y", "secret N should start at ..."). Moves a courses.json target's start to the nearest in-game checkpoint in one command.
---

# Move a target's start

`courses.json` is not in the repo: it lives in the game's config folder (`tools/gamepaths.py` `CONFIG`). A target's
start is its `x`, `y` (the checkpoint's own position, which the respawn uses).

1. Map the user's name to a token. "Secret N" / a bonus by description: `BONUS_WHERE` in `web/app/page.tsx` (search the
   description, e.g. `bonus8: "Far above Course 4 ..."`). Otherwise the token is the courses.json `name` or `id`.
2. Dry run, then write:
   ```
   python tools/move-start.py <token> <x> <y>
   python tools/move-start.py <token> <x> <y> --write
   ```
   It picks the nearest checkpoint from the cached studio maps (`config/enginesim/map-*.json`, markers of kind
   `checkpoint`) and stores marker y + 10.015, the convention bonus5's Checkpoint (2) start uses. Check the printed
   distance is small (tens of units); if not, ask.
3. Update the target's note in `web/app/page.tsx` (`Starts at ...`).
4. Routes solved from the old start are stale (see `docs/fidelity-log.md`, bonus5): say so; they need solving again.
5. Redeploy (`python tools/enginesim-redeploy.py`) so the studio picks up the note; courses.json is read live.

Don't: grep the whole `IGTAP` folder for coordinates (huge, times out), or chase `EngineSimMap`/`Map.cs` code: the
script already knows where the data is.
