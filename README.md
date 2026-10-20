# Tier List Overlay

A Slay the Spire 2 mod that shows community tier list ratings in the game. It supports the
Baalorlord and Jorbs tier lists from Untapped.gg and was inspired by the Untapped.gg Companion.

## What it shows

- **Tier badges** on card rewards, "choose a card" screens, the shop, card grids (upgrade,
  transform, remove, …), the deck view, bundles, treasure and elite relics, potions and ancient
  options.
- **Commentary** from the list's author in an extra hover tip, and a mark on cards that are a
  good upgrade.
- **Settings** in a *Tier List* tab of the game's settings: what the badges on cards, relics,
  potions and deck screens show (none, the tier list, or global stats), Baalorlord's or Jorbs'
  list, the commentary tips, and the Untapped.gg login. Jorbs rates cards only, so relics and
  potions always use Baalorlord's lists.

The lists are fetched for the installed game version at game start and cached next to the mod,
so it also works offline.

### Optional: Untapped.gg stats

With an Untapped.gg account, the badges can show Untapped.gg's global stats instead of tiers:
how often players picked (or bought, or upgraded) an item, and how the act's and the run's
winrate changed when they did. They're shown on card rewards (including Skip), ancient
options, the shop (including card removal) and the rest site's upgrade screen. The hover tips
spell them out, with the tier list rating below. To turn it on, press *Log in* at the bottom of
the *Tier List* tab (it opens Untapped.gg's login in the browser), then set the badges to
*Global stats*. Screens without stats show the tier list. The login is kept in `account.cfg`
next to the mod. *Log out* deletes it. Without a login, or when Untapped.gg is unreachable, the
mod shows tiers as before. The mod only reads these stats. It sends none of your runs.

## Requirements

- Slay the Spire 2 via Steam, tested with game version v0.107.1
- .NET SDK 9 or newer, to build

## Platforms

Developed and tested on **macOS (Apple Silicon) only**. The mod is plain C# with no
platform-specific code, and the game loads mods the same way on every platform, so it should
also work on Windows and Linux, but nobody has tried yet. Reports are welcome.

## Build and install

```sh
dotnet build -c Release -p:Install=true
```

This builds against the installed game and copies `TierListOverlay.dll` and
`TierListOverlay.json` to `<mods folder>/TierListOverlay/`. The default paths, per OS, are in
`Directory.Build.props`:

| OS | Game folder (`STS2GameDir`) | Mods folder (`STS2ModsDir`) |
| --- | --- | --- |
| macOS | `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2` | `<game>/SlayTheSpire2.app/Contents/MacOS/mods` |
| Windows | `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2` | `<game>\mods` |
| Linux | `~/.local/share/Steam/steamapps/common/Slay the Spire 2` | `<game>/mods` |

The build references `sts2.dll` and `GodotSharp.dll` from the game's data folder
(`STS2GameDataDir`): `data_sts2_macos_arm64` inside the app on macOS, and presumably
`data_sts2_windows_x86_64` or `data_sts2_linuxbsd_x86_64` in the game folder on Windows and
Linux. If the game lives elsewhere, or a folder name differs, override the paths:

```sh
dotnet build -c Release -p:Install=true -p:STS2GameDir="D:/Games/Slay the Spire 2" -p:STS2GameDataDir="D:/Games/Slay the Spire 2/data_sts2_windows_x86_64"
```

Or build without `-p:Install=true` and copy `bin/Release/net9.0/TierListOverlay.dll` and
`TierListOverlay.json` into a `TierListOverlay` folder in the game's mods folder (create it if
needed. It sits next to the game's executable.)

For development (macOS only), `tools/reload.sh` quits the game, builds and installs this mod and
the DevTools mod (`tools/DevTools/`), and starts the game again. `tools/check.py` then checks
the badges and tips in the running game. `docs/UPDATING.md` describes what to do after a game
update.

## Disclaimer

Not affiliated with or endorsed by Mega Crit or Untapped.gg. Tier ratings and commentary
belong to their authors.

## License

[MIT](LICENSE)
