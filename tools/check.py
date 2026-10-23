#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["pillow"]
# ///
"""End-to-end checks for the tier list overlay against the running game.

Each scenario sets up a screen through the dev console (DevTools mod), inspects what is on
screen and asserts that every rated item shows the right badge (its tier, the Untapped.gg stats
the API gives for the mod's own request, or the player's own counts from synthetic run history
files), that tips and settings work, and that no
badge is drawn on top of a hover tip. Screenshots go to .artifacts/ for a human look, each with
a JSON of what is where on it (game.screenshot).

Needs the game running with the STS2_MCP, DevTools and TierListOverlay mods, in modded
profile 2 (the test profile), and for the stats scenarios an Untapped.gg login (see
docs/UPDATING.md). Run: tools/check.py [scenario ...]   (--list for names)

The scenarios live in checklib/, one module per feature; this file runs them. Every scenario
but `lists` starts in a run of the test profile with no overlay screen open (game.setup), and
the tier ones with the badges on the tier list.
"""

import argparse
import sys

from checklib import advice, game, global_stats, personal, settings, tiers
from checklib.game import BADGE_KEYS, CheckError, read_settings
from checklib.verify import Result, RunExhausted, TierLists

SCENARIOS = {**tiers.SCENARIOS, **settings.SCENARIOS, **global_stats.SCENARIOS, **personal.SCENARIOS, **advice.SCENARIOS}
# Scenarios that need no run, only the game's files and log.
NO_SETUP = {"lists"}
# Scenarios that choose the badges themselves. The others check tier badges.
OWN_BADGES = {*settings.SCENARIOS, *global_stats.SCENARIOS, *personal.SCENARIOS}


def prepare(name):
    if name in NO_SETUP:
        return
    game.setup()
    if name not in OWN_BADGES and (read_settings()["badges"] != "tiers" or not all(read_settings()[k] for k in BADGE_KEYS)):
        settings.ensure_stats(False)


def run(name, tier_lists):
    result = Result(name)
    try:
        try:
            prepare(name)
            SCENARIOS[name](result, tier_lists)
        except RunExhausted as e:
            print(f"     ↻ {e}, starting a fresh test run")
            game.new_test_run()
            result = Result(name)
            prepare(name)
            SCENARIOS[name](result, tier_lists)
    except (CheckError, OSError, StopIteration, KeyError) as e:
        result.fail(f"setup error: {type(e).__name__}: {e}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("scenarios", nargs="*", help="scenarios to run (default: all)")
    parser.add_argument("--list", action="store_true", help="list scenarios")
    parser.add_argument("-v", "--verbose", action="store_true", help="show notes for passing scenarios")
    parser.add_argument("--new-run", action="store_true", help="abandon the test run first and start a fresh one")
    parser.add_argument("--full-size", action="store_true",
                        help="keep the player's display setting instead of a window (the badge rim check needs the size)")
    args = parser.parse_args()
    if args.list:
        for name, fn in SCENARIOS.items():
            print(f"{name:15} {fn.__doc__.strip()}")
        return
    names = args.scenarios or list(SCENARIOS)
    unknown = [n for n in names if n not in SCENARIOS]
    if unknown:
        sys.exit(f"unknown scenario(s): {', '.join(unknown)}")

    tier_lists = TierLists()
    game.window("saved" if args.full_size else "windowed")
    game.mute()
    if args.new_run:
        game.new_test_run()
    results = []
    for name in names:
        result = run(name, tier_lists)
        results.append(result)
        print(f"{'PASS' if not result.failures else 'FAIL'} {name}")
        for f in result.failures:
            print(f"     ✗ {f}")
        if result.failures or args.verbose:
            for n in result.notes:
                print(f"     · {n}")
        for s in result.screenshots:
            print(f"     📷 {s}")
    sys.exit(0 if all(not r.failures for r in results) else 1)


if __name__ == "__main__":
    main()
