# Updating

## After a game update

Steam updates the game without asking; the mod keeps running as long as the game still has
what it uses. At startup the mod logs `Last tested on game vX, this is vY` when the installed
version differs from `TestedGameVersion` (`TierListOverlayMod.cs`), and `check.py lists`
fails until that constant is raised. The routine:

1. `tools/reload.sh` rebuilds both mods against the new `sts2.dll`. A compile error means
   the game renamed or removed a type or member the code names directly.
2. `tools/check.py --new-run`. What can break without a compile error, and the check that
   notices:
   - Reflection on private game members (`Tips.cs`: `NHoverTipSet._owner`,
     `_textHoverTipContainer`, `CorrectVerticalOverflow`, `CorrectHorizontalOverflow`): tips
     silently stop appearing or run off screen (`check_tip`).
   - Node names and scene paths (`SettingsUi.cs`: `%GeneralSettings`, `ShowRunTimer`,
     `SettingsTickbox`; `Badges.cs`: `TitleLabel`; the shop slot's `Cost`; `Tips.cs`:
     `res://scenes/ui/hover_tip.tscn`): the settings rows log "layout changed", other
     features quietly drop out (`settings`, `shop_relics`).
   - Constants measured on the game's textures (`Badges.cs`: the card border line at 28.6 of
     422 units, the ancient box edge at 8.5%; the shop slot scale 0.65): badges land off the
     border (`deck_view`, `ancient`, `shop_relics`).
3. When something fails, find out what changed in the game types the failing code uses.
4. `check.py` drives the game through the STS2MCP mod. If its endpoints fail, update
   it from its `main` branch (releases lag behind the game), build with
   `dotnet build -c Release -p:STS2GameDir="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"`
   and copy `STS2_MCP.dll` plus `mod_manifest.json` (as `STS2_MCP.json`) to `mods/STS2_MCP/`.
5. All green: raise `TestedGameVersion` to the new version and commit.

## Tier list data

- The mod fetches the lists at game start, for the installed game version, and caches them in
  `mods/TierListOverlay/cache/`. It refreshes when the cache is older than 6 hours, incomplete
  or from another game version; a running game never reloads. To force a refresh, delete
  `cache/fetched.cfg` and restart the game.
- Tiers, colours, commentary and new items come from the data; IDs the game does not know are
  logged (`Unknown IDs in ...`).
- In code (`TierLists.cs`, `Lists`): the 12 list UUIDs, one per author and character plus
  colorless. A new character, or a list Untapped replaces with a new UUID, needs an entry
  there.
