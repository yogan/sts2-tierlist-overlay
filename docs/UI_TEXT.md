# Text shown in the game

Rules for every word the mod puts on screen: badges, hover tips, settings rows and their tips,
the hint at game start. Apply all of them to each new or changed text, then run the scenarios
that show it (`tools/check.py --list`).

## Naming

- **Other players.** Global stats are "other players'" numbers. Untapped.gg is named only where
  the login is concerned: the account row in the settings and the login hint at game start.
- **Whose and what.** A stats tip's heading says whose numbers follow: "Your Ironclad runs",
  "Other players' Ironclad runs", "Other players, all characters". What was counted goes with
  them: runs at Ascension 7+ ("A7" where a line is short of room).

## Tips

- **One fact per line**, short enough to fit the tip's width: about 28 characters. The
  `stats_*` and `personal_*` checks fail when a line wraps (`wrapped_lines` in
  `tools/checklib/verify.py`).
- **Dim lines** (`Dim` in `Tips.cs`) add information to the facts above them: sample sizes,
  other characters, "too little data". They may wrap.
- **Short beats complete.** When a text grows, drop a detail rather than add a caveat. A tip
  states what is there now.
- **An intro earns its place**: the first line of a settings tip says something the row's label
  doesn't.
- **Only the options on offer.** A settings tip explains the choices the row offers right now
  (`Option.Tip` and `Available` in `SettingsUi.cs`): logged out, the badge rows' tips leave out
  global stats.

## Punctuation

- Full stops and commas, colons for a label and its value. Sentences that would need a
  semicolon become two. This holds for the mod's docs and code comments as well.
