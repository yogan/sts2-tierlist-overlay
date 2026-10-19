# Tier List Overlay (Slay the Spire 2 mod)

- Test in modded **profile 2** only. Profile 1 holds the user's real progress and a saved run.
- Loop: `tools/reload.sh` (build, install, restart the game), then `tools/check.py [scenario ...]`. A change is done when `check.py` passes. Each new screen or visual requirement first gets a scenario or a measured check that goes red without the change.
- Both put the game in a window beside other apps, so the user can watch. When the work is done, `tools/dev.py window saved` puts back the user's display setting (usually fullscreen).
- Tools and their usage, in their docstrings: `tools/check.py` (scenarios), `tools/dev.py` (node trees, clicks, small cropped screenshots), `tools/DevTools/DevToolsMod.cs` (the HTTP endpoints both use).
- Game log: `~/Library/Application Support/SlayTheSpire2/logs/godot.log`.
- Once per clone: `git config core.hooksPath .githooks` (builds both mods before each commit).
- After a game update or for tier list data questions: `docs/UPDATING.md`.
