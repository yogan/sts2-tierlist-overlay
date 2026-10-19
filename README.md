# Tier List Overlay

A Slay the Spire 2 mod that shows community tier list ratings in the game. It supports the
Baalorlord and Jorbs tier lists from Untapped.gg and was inspired by the Untapped.gg Companion.

## What it shows

- **Tier badges** on card rewards, "choose a card" screens, the shop, card grids (upgrade,
  transform, remove, …), the deck view, bundles, treasure and elite relics, potions and ancient
  options.
- **Commentary** from the list's author in an extra hover tip, and a mark on cards that are a
  good upgrade.
- **Settings** in the game's *General* tab: Jorbs' card ratings instead of Baalorlord's, and
  toggles for each kind of badge and for the commentary tips. Jorbs rates cards only, so relics
  and potions always use Baalorlord's lists.

The lists are fetched for the installed game version at game start and cached next to the mod,
so it also works offline.

## Requirements

- macOS on Apple Silicon (the only platform it is built and tested on)
- Slay the Spire 2 via Steam, tested with game version v0.107.1
- .NET SDK 9 or newer, to build

## Build and install

```sh
dotnet build -c Release -p:Install=true
```

This builds against the installed game and copies `TierListOverlay.dll` and
`TierListOverlay.json` to
`~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/TierListOverlay/`.
Pass `-p:STS2GameDir=…` if the game lives elsewhere.

For development, `tools/reload.sh` quits the game, builds and installs this mod and the
DevTools mod (`tools/DevTools/`), and starts the game again; `tools/check.py` then checks the
badges and tips in the running game. `docs/UPDATING.md` describes what to do after a game
update.

## Disclaimer

Not affiliated with or endorsed by Mega Crit or Untapped.gg. Tier ratings and commentary
belong to their authors.

## License

[MIT](LICENSE)
