# igtap-lab: every tech lab on one map, as a Recharge mod

Repo: `ChandlerFerry/igtap-lab` (local `..\igtap-lab`), MIT. A RechargeLoader mod (`IRechargeMod`, `mod.json` id
`chandlerferry.igtaplab`) that anyone with Recharge can install. IGTAPTasMod is unchanged; its `labmaps/` is the source.

## Why not recharge-maps

A recharge-maps map spawns only `Groups[0]` (one start/end gate), paints 32 u ground tiles, has no outline/text/script
objects, teleports the player to (50000, 50000) and writes a per-map course save. None of that fits 70 labs with sized
colliders, finish boxes and an overlay, so the mod builds its world itself. No PR to recharge or recharge-maps is needed;
listing on the hub (`content/index.json`) is optional and left to the author.

## Data: `tools/pack.py` -> `labs/world.json`

- Reads `IGTAPTasMod/labmaps/*.json` (70 labs) and their `*.demo.json` (154 demos).
- Bounds per lab: every shape (`LabShape.Bounds` rules: box `at`/`size`/`rotation`, polygon points), `hand.shapes`, the
  start and hand start (player hitbox 41 x 54 around them), the finish box; plus a 150 u margin. Ghost lanes are not
  built, so their offsets are ignored.
- Shelf-packs the boxes (tallest first, rows no wider than a fixed width) with a 300 u gap, and places the pack in an
  empty region found by `IGTAP.EngineSim lab space` (the same tool that found the current lab anchor). Each lab gets a
  new `anchor`; everything else is anchor-relative and copied verbatim.
- Demos: each demo's `path` is rewritten at the packed anchor by `lab replay --write-path` (this also writes the 24 that
  have none). A demo that no longer finishes at its packed anchor is reported and shipped without a ghost.
- `world.json`: `{labs: [{id, name, tech, category, anchor, box: [x, y, w, h], shapes, start, hand, finish,
  demos: [{name, inputs, path}]}]}`. `box` is the bounding box, anchor-relative.
- Check (`tools/pack.py --check`): no two boxes overlap; every shape, start and finish lies inside its box.

## Mod (C#, netstandard2.1, compiled against the game's `Managed` DLLs and `Recharge.ModApi.dll`)

- `LabMod`: `IRechargeMod`. Adds a pause-menu row **Labs** that opens a panel listing the labs (paged, the ModApi's
  `PauseMenuHelper.AddPanelRow`). Picking one enters the lab world at that lab.
- `LabWorld`: builds every lab once, one root per lab at `anchor` + the current floating origin, as
  `LabMapService.Inject` does (box/polygon/edge colliders, layers, tags, triggers, `spikeScript`, `SpringScript` fields
  by reflection, colour blocks). Colour blocks follow the game's swap state by polling each frame (no Harmony postfix).
  Draws each lab's bounding box as an outline (four thin sprites) and its name above it (TextMeshPro).
- `LabSession`: entering snapshots the player's position, velocity, respawn point and every unlock field the labs
  touch; it then places the player at the lab's hand start (or start) with the lab's category unlocks. The reset key
  (the game's quick restart) and leaving the lab's box return to that start. Leaving (the panel's **Exit lab**) restores
  the snapshot and destroys the world. A scene change restores too.
- Saves: while a lab session is on, `Saveloader.autosave`, `manualSave` and `delayedManualSave` are skipped. The mod
  first tries this without patching (the game's own autosave switch/timer, if `Saveloader` has one); otherwise it ships
  `0Harmony.dll` and prefixes those three methods, only while in a lab.
- `LabGhost`: plays the selected demo's `path` (`[x, y, facing, animation, animator speed]`, anchor-relative) as a
  translucent copy of the player's sprite, one entry per physics tick, looping. A lab with several demos cycles them
  from the panel.
- `LabTracker`: the input tracker on every lab, styled like the TAS mod's pauselift trainer (top-centre panel, chips
  grey/green/red, one message, a timeline, successes / attempts):
  - Chips: the demo's inputs, one chip per change (stick direction, jump press, jump release, dash, freeze turn,
    pause), labelled with the key/button the player has bound and the tick it comes on, counted from the first input.
  - Attempt: starts at the lab start (entering, reset, or leaving the box), advances per physics tick
    (`OnFixedUpdate`). Each tick the live input (the game's own `InputAction`s on `Movement`) is compared with the
    demo's tick: a chip turns green when its input comes on its tick, red when it comes early or late (with the ticks)
    or not at all.
  - Timeline: the demo's input ticks as marks, the player's as marks under them, a cursor for now.
  - Success: the player inside the finish box (`finish` padded by the hitbox, as `InLabFinish`).

## Build and layout

```
igtap-lab/
  README.md          what it is, install, build (minimal)
  LICENSE            MIT
  mod.json
  IgtapLab.csproj    ManagedDir = the game's *_Data/Managed (default: the Steam path), ModApiDir for Recharge.ModApi.dll
  src/*.cs
  labs/world.json
  tools/pack.py
```

`dotnet build -c Release` builds `IgtapLab.dll`; `tools/package.ps1` zips `mod.json`, the DLL(s) and `labs/` into
`igtap-lab.igtap`, the package Recharge installs.

## Verification

- `python tools/pack.py --check` passes.
- Every demo replays to its finish at its packed anchor (`lab replay`), or is listed as ghost-less.
- `dotnet build` is clean, with no warnings from our code.
- In-game (by the author, with Recharge installed): enter the Labs, walk into three labs, and check that the saves are
  skipped and the player is restored on exit. The game is not launched by the agent: a second instance would share the
  save and PlayerPrefs with the running TAS session.
