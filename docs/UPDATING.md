# Updating

## After a game update

Steam updates the game without asking, but the mod keeps running as long as the game still has
what it uses. At startup the mod logs `Last tested on game vX, this is vY` when the installed
version differs from `TestedGameVersion` (`TierListOverlayMod.cs`), and `check.py lists`
fails until that constant is raised. The routine:

1. `tools/reload.sh` rebuilds both mods against the new `sts2.dll`. A compile error means
   the game renamed or removed a type or member the code names directly.
2. `tools/check.py --new-run`, then `tools/check.py --full-size deck_view` (in the window the
   tools use, the badge rims are too thin to measure). What can break without a compile
   error, and the check that notices:
   - Reflection on private game members: `NHoverTipSet._owner`, `_textHoverTipContainer`,
     `CorrectVerticalOverflow` and `CorrectHorizontalOverflow` in `Tips.cs` (tips stop
     appearing or run off screen, `check_tip`), `NSettingsTabManager._tabs` and `SwitchTabTo`
     and `NPaginator._options`, `_currentIndex` and `_label` in `SettingsUi.cs` (no Tier List
     tab, `settings`), `NCardRewardSelectionScreen._options` and `_extraOptions` and
     `NCardGridSelectionScreen._cards` in `TierListOverlayMod.cs` (no stats on card rewards,
     Skip or upgrades, `stats_card_reward`, `stats_upgrade`).
   - Node names and scene paths: the General tab rows the Tier List tab copies
     (`%GeneralSettings`, `ShowRunTimer`, `Screenshake`, `SendFeedback` with `FeedbackButton`,
     `ShowTimerDivider`, the Reset Gameplay button's `Image`) and the `Input` tab in
     `SettingsUi.cs`, `TitleLabel` in `Badges.cs`, the shop slot's `Cost` and the card removal
     slot's `Hitbox`, `res://scenes/ui/hover_tip.tscn` in `Tips.cs`. The settings log "layout
     changed", other features quietly drop out (`settings`, `shop_relics`, `stats_shop`).
   - Constants measured on the game's textures (the card border line at 28.6 of 422 units, the
     ancient box edge at 8.5% and the Skip button's top edge at 14% in `Badges.cs`, the shop
     slot scale 0.65): badges land off the border (`deck_view`, `ancient`, `shop_relics`,
     `stats_card_reward`).
3. When something fails, find out what changed in the game types the failing code uses.
4. `check.py` drives the game through the STS2MCP mod. If its endpoints fail, update
   it from its `main` branch (releases lag behind the game), build with
   `dotnet build -c Release -p:STS2GameDir="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"`
   and copy `STS2_MCP.dll` plus `mod_manifest.json` (as `STS2_MCP.json`) to `mods/STS2_MCP/`.
5. All green: raise `TestedGameVersion` to the new version and commit.

## Tier list data

- The mod fetches the lists at game start, for the installed game version, and caches them in
  `mods/TierListOverlay/cache/`. It refreshes when the cache is older than 6 hours, incomplete
  or from another game version. A running game never reloads. To force a refresh, delete
  `cache/fetched.cfg` and restart the game.
- Tiers, colours, commentary and new items come from the data. IDs the game does not know are
  logged (`Unknown IDs in ...`).
- In code (`TierLists.cs`, `Lists`): the 12 list UUIDs, one per author and character plus
  colorless. A new character, or a list Untapped replaces with a new UUID, needs an entry
  there.

## Personal stats

- The mod reads the game's run history: one JSON file per finished run,
  `<profile>/saves/history/<start time>.run`, under `steam/<id>/profileN/` for unmodded play
  and `steam/<id>/modded/profileN/` for modded play (in the game's user data folder). The game
  only reads the folder of the mode it runs in. The mod reads both, as plain JSON, and never
  writes there. Files ending in `.backup` or `.corrupt` are the game's spares.
- What it uses, per floor in `map_point_history` (`player_stats[0]`): `card_choices` with
  `was_picked` (card rewards, rerolled ones included, unpicked), `ancient_choice` with `TextKey`
  and `was_chosen`, and at shops (`map_point_type` `shop`) the unbought stock that leaving logs
  as unpicked `card_choices`, `relic_choices` and `potion_choices`, bought cards in
  `cards_gained`, bought relics and potions as picked entries, and `cards_removed`. Plus `win`,
  `ascension`, `game_mode`, `players` and the final `deck` and `relics`. The history does not
  log what the smith offered, so there are no personal upgrade stats.
- Tested with `schema_version` 9 (game v0.107.1). A new schema version can rename fields.
  `personal_real` notices: it copies the active profile's history (test profile 2) into
  `test-history/`, fails when the mod skips a file, when a file has a newer schema than
  `KNOWN_SCHEMA` (`tools/checklib/personal.py`) or when no run is from the installed game
  version, and compares a card reward's badges and tips with its own count. `tools/check.py
  --new-run` makes a run in the new version (it abandons the test run). With a newer schema,
  compare a fresh `.run` file with the fields above, then raise `KNOWN_SCHEMA`. Files the mod
  can't read are logged (`Skipping run history file`), and every rescan logs `Run history: N
  runs, M counted, L lower`.
- The other `personal_*` scenarios write synthetic runs to `mods/TierListOverlay/test-history/`,
  which the mod reads instead of the profile's history while it exists, and delete it after.
  If a check is interrupted, delete that folder by hand, or the badges keep showing the test
  numbers.

## Untapped.gg stats

- The `stats_*` scenarios and `logged_out` need a login: log in once in the Tier List tab of the
  game's settings (test profile 2). They compare the badges and tips with what the API answers
  to the mod's own request (from the game log), so they also notice changes to the API.
- `logged_out` logs out through the settings and restores the login from a copy of
  `account.cfg`. It restarts the game twice.
- The requests (`Stats.cs`) send the game's own IDs, act and room type. If an endpoint starts
  failing, the game log has the HTTP status and the start of the answer.
