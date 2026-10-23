# Tier List Overlay

A Slay the Spire 2 mod that shows community tier list ratings in the game. It supports the
Baalorlord and Jorbs tier lists from Untapped.gg and was inspired by the Untapped.gg Companion.

## What it shows

- **Tier badges** on card rewards, "choose a card" screens, the shop, card grids (upgrade,
  transform, remove, …), the deck view, bundles, treasure and elite relics, potions and ancient
  options.
- **Commentary** from the list's author in an extra hover tip, and a mark on cards that are a
  good upgrade.
- **Other characters' cards** (Kaleidoscope, Prismatic Gem, Splash, …) get the tier from their
  own character's list. Their tip says whose list that is: rated for a Defect deck, a card may
  be worth less in yours.
- **Personal stats**, if you like: how often you took an item when it was offered in your own
  past runs, and how often you picked each option of an event (see below).
- **Settings** in a *Tier List* tab of the game's settings: what the badges show (the tier list,
  your stats or other players' stats) and on which items, your stats on events, tier badges on
  deck screens, Baalorlord or Jorbs as the author, the commentary, stats and event advice tips,
  and the Untapped.gg login. Jorbs rates cards only, so relics and potions always use
  Baalorlord's lists.

The lists are fetched for the installed game version at game start and cached next to the mod,
so it also works offline.

### Personal stats

Set the badges to *Personal stats* to see your own habits: how often you picked a card from a
card reward, picked Skip on a card reward, chose an ancient's relic, or bought a card, relic or
potion in the shop, and how often you paid for a card removal. The badge shows the share of the
offers you took ("30%"), or below 5 offers the counts ("2/4"). The hover tip spells out both,
and adds in how many of your wins the card or relic ended up in your deck or relics, and how
often you took it with your other characters. The numbers come from the game's own run history,
the same files its Run History screen reads, for the current profile (modded and unmodded runs
together). Runs count at Ascension 7+, in standard mode, alone, with the character you're
playing. An item you never took there reads *Unpicked*, to try something new. Its tip says how
often you passed it over, and whether you took it in runs below Ascension 7. A screen never
mixes your stats with tiers: screens without them (treasure, loot, upgrades) show no badges.
Deck screens keep their tier badges, which *Tier badges on deck screens* turns off. The mod
only reads the history. It never changes it, and it records nothing of its own. A finished run
counts from the next screen on.

Event options (not ancients, whose relics go with the relic badges) show in how many of your
visits you picked them ("71%", or "2/4" below 5 visits), from your Ascension 7+ runs with all
characters, as events barely depend on the character. The run history keeps only what you
picked, not what was on offer, so an option counts every visit that reached its page (on a page
that can come up again in one visit, like Abyssal Baths', every time it came up). An option an
event locks without a card to enchant or enough gold (Self-Help Book's, Wongo's) counts only
the visits it was open, which the mod works out from your deck and gold before the visit. Where
an option is offered at random (a dish on the conveyor), its tip says so. The badge sits at the
right end of the option, or on its top edge when an option's text reaches that far. Pages with
a single option get none. *Personal stats on events* turns them off.

*Stats in hover tips* (on by default) shows your stats, and other players' when logged in, in
an extra hover tip whatever the badges show.

### Optional: Untapped.gg stats

With an Untapped.gg account, the badges can show Untapped.gg's global stats instead of tiers:
how often players picked (or bought, or upgraded) an item when it was offered, like your own
stats. They're shown on card rewards (including Skip), ancient options, the shop (including
card removal) and the rest site's upgrade screen. The hover tips spell them out, below your own
stats, and add how the act's and the run's winrate changed when players took the item, with the
tier list rating below them. To turn it on, press *Log in* at the bottom of the *Tier List* tab
(it opens Untapped.gg's login in the browser), and the badges switch to *Global stats*. Items
with too little data show no badge, and screens without stats show none. The login is kept in
`account.cfg` next to the mod. *Log out* deletes it. Without a login, or when Untapped.gg is
unreachable, the badges show your own stats until it works again. The mod only reads these
stats. It sends none of your runs.

Logged in, event options also get Baalorlord's or Jorbs' advice on the event in an extra hover
tip: the chosen list's author's, or the other's where they wrote none. Untapped.gg has advice on
some of the events. A text too long for the screen scrolls with the mouse wheel or a two-finger
swipe while you hover the option. Moving onto the tip closes it, as the game's tips do. *Event
advice in hover tips* (on by default) turns it off. The advice is cached next to the mod and
refreshed once a day.

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
