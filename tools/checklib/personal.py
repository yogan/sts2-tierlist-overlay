"""The player's own stats from the game's run history, on badges and in tips.

With badges on personal stats, card rewards (and Skip), ancient options and the shop (and card
removal) show how often the player took the item in their own runs. Real profiles are never
touched: the checks write synthetic runs to the mod's test-history folder, which the mod then
reads instead of the profile's history, and count them here the way the mod should. Only
personal_real reads a profile's history, to copy it there."""

import json
import shutil
import time
from pathlib import Path

from .game import (GAME_APP, MCP, MOD_DIR, CheckError, _request, act, hover, hover_until, inspect, log_length, log_since, open_ancient,
                   open_card_reward, open_event, open_shop, read_settings, screenshot, wait_for)
from .geometry import centre, fully_inside, share_covered
from .global_stats import percent
from .settings import ensure_stats, set_setting
from .verify import (check_badges, check_badges_on_box_top, check_prices_clear, check_tip, in_context,
                     wrapped_lines)

TEST_HISTORY = MOD_DIR / "test-history"
# The game's saves, one folder per Steam account, with profileN/ and modded/profileN/ in each.
SAVES = Path.home() / "Library/Application Support/SlayTheSpire2/steam"
# The run history schema_version that count_history (and History.cs) were checked against.
KNOWN_SCHEMA = 9
PREFIX = {"cards": "CARD.", "relics": "RELIC.", "potions": "POTION."}
# Per kind: the badge's label, the tip's line (taken, offered, share).
PERSONAL_KINDS = {
    "reward": ("Pick", "Picked {} of {} ({})"),
    "skip": ("Pick", "Picked Skip {} of {} ({})"),
    "ancient": ("Pick", "Picked {} of {} ({})"),
    "shop": ("Buy", "Bought {} of {} ({})"),
    "remove": ("Buy", "Removed {} of {} ({})"),
}


def count_history(runs, character):
    """The player's numbers from run history files: (kind, id) -> [taken, offered], won runs
    per final card or relic, runs and wins counted, per (kind, id) the runs below Ascension 7
    that took it, and (kind, id) -> [taken, offered] with the other characters. Runs count at
    Ascension 7+, in standard mode, alone, with `character`."""
    history = _count_character(runs, character)
    history["character"] = character
    history["others"] = {}
    for other in {r["players"][0]["character"].split(".", 1)[1] for r in runs if r.get("players")} - {character}:
        for key, (taken, offered) in _count_character(runs, other)["counts"].items():
            c = history["others"].setdefault(key, [0, 0])
            c[0] += taken
            c[1] += offered
    return history


def _count_character(runs, character):
    counts, in_wins, lower, counted, wins = {}, {}, {}, 0, 0
    for run in runs:
        players = run.get("players", [])
        if (len(players) != 1 or run.get("game_mode") != "standard" or "ascension" not in run
                or players[0]["character"] != f"CHARACTER.{character}"):
            continue
        offers = []

        def offer(kind, item_id, taken):
            offers.append(((kind, item_id.split(".", 1)[-1]), bool(taken)))

        for floor in (f for act_floors in run["map_point_history"] for f in act_floors):
            stats = (floor.get("player_stats") or [{}])[0]
            cards = stats.get("card_choices", [])
            if floor["map_point_type"] == "shop":
                for c in cards:
                    offer("shop", c["card"]["id"], False)
                for c in stats.get("cards_gained", []):
                    offer("shop", c["id"], True)
                for key in ("relic_choices", "potion_choices"):
                    for c in stats.get(key, []):
                        offer("shop", c["choice"], c["was_picked"])
                offer("remove", "", bool(stats.get("cards_removed")))
            elif cards:
                for c in cards:
                    offer("reward", c["card"]["id"], c["was_picked"])
                offer("skip", "", not any(c["was_picked"] for c in cards))
            for c in stats.get("ancient_choice", []):
                offer("ancient", c["TextKey"], c["was_chosen"])
        if run["ascension"] < 7:
            for key in {key for key, taken in offers if taken}:
                lower[key] = lower.get(key, 0) + 1
            continue
        counted += 1
        for key, taken in offers:
            c = counts.setdefault(key, [0, 0])
            c[0] += taken
            c[1] += 1
        if run.get("win"):
            wins += 1
            player = players[0]
            for item in {("cards", c["id"].split(".", 1)[-1]) for c in player["deck"]} | \
                        {("relics", r["id"].split(".", 1)[-1]) for r in player["relics"]}:
                in_wins[item] = in_wins.get(item, 0) + 1
    return {"counts": counts, "in_wins": in_wins, "lower": lower, "runs": counted, "wins": wins}


def fixture_runs(character, offers, counted=10):
    """Synthetic runs offering `offers` ((kind, group, id), kind one of reward/shop/ancient):
    item n in the first 4 + n counted runs, taken in every
    third, card removal at every other shop, every fourth run won. With three items or more,
    the last is never taken (but is in a lower run), with four or more the one before is never
    offered. Plus runs
    that must not count: Ascension 6 (which takes all it is offered), two players, another
    character, a custom game."""
    never_taken = len(offers) - 1 if len(offers) >= 3 else None
    never_offered = len(offers) - 2 if len(offers) >= 4 else None

    def run(j, character=character, ascension=10, mode="standard", players=1):
        floors, deck, relics = [], [{"id": f"CARD.STRIKE_{character}"}], []
        shop = {"card_choices": [], "cards_gained": [], "relic_choices": [], "potion_choices": []}
        ancient = []
        for n, (kind, group, item_id) in enumerate(offers):
            if j >= 4 + n or n == never_offered:
                continue
            taken = ascension < 7 or (j + n) % 3 == 0 and n != never_taken
            full_id = PREFIX[group] + item_id
            if kind == "reward":
                # Taken, or another card, or nothing (a skip). A reroll before it, unpicked.
                choices = [{"card": {"id": "CARD.FIXTURE_REROLLED"}, "was_picked": False}] if n == 1 else []
                choices += [{"card": {"id": full_id}, "was_picked": taken},
                            {"card": {"id": "CARD.FIXTURE_OTHER"}, "was_picked": not taken and (j + n) % 3 == 1}]
                floors.append({"map_point_type": "monster", "player_stats": [{"card_choices": choices}]})
                if taken:
                    deck.append({"id": full_id})
            elif kind == "ancient":
                ancient.append({"TextKey": item_id, "was_chosen": taken})
                if taken:
                    relics.append({"id": full_id})
            elif group == "cards":
                (shop["cards_gained"] if taken else shop["card_choices"]).append(
                    {"id": full_id} if taken else {"card": {"id": full_id}, "was_picked": False})
                if taken:
                    deck.append({"id": full_id})
            else:
                shop[f"{group[:-1]}_choices"].append({"choice": full_id, "was_picked": taken})
                if taken and group == "relics":
                    relics.append({"id": full_id})
        if ancient:
            floors.insert(0, {"map_point_type": "ancient", "player_stats": [{"ancient_choice": ancient}]})
        if j % 2 == 0:
            shop["cards_removed"] = [{"id": f"CARD.STRIKE_{character}"}]
        floors.append({"map_point_type": "shop", "player_stats": [shop]})
        player = {"character": f"CHARACTER.{character}", "deck": deck, "relics": relics}
        return {"schema_version": 9, "ascension": ascension, "game_mode": mode, "win": j % 4 == 0,
                "players": [player] * players, "map_point_history": [floors]}
    other = "SILENT" if character != "SILENT" else "IRONCLAD"
    return ([run(j) for j in range(counted)]
            + [run(0, ascension=6), run(0, players=2), run(0, character=other), run(0, mode="custom")])


# Events the event scenario visits, and an option each that the synthetic runs never pick. Tea
# Master's texts reach the right end of the boxes, so its badges go on the top edge.
EVENTS = ("SAPPHIRE_SEED", "SYMBIOTE", "TEA_MASTER")
NEVER_PICKED = {"SYMBIOTE": "SYMBIOTE.pages.INITIAL.options.KILL_WITH_FIRE"}
# Options the event locks, which the synthetic runs lock in every other run, as the game would:
# Symbiote's Approach without an Attack in the deck to take Corrupted (by the cards that open
# it), Tea Master's teas below 50 and 150 gold (Ember Tea is on screen only when the test run
# has 150). The mod counts only the visits that offered them.
ATTACK, SKILL = "CARD.STRIKE_IRONCLAD", "CARD.DEFEND_IRONCLAD"
DECK_GATES = {"SYMBIOTE.pages.INITIAL.options.APPROACH": {ATTACK}}
GOLD_GATES = {"TEA_MASTER.pages.INITIAL.options.BONE_TEA": 50, "TEA_MASTER.pages.INITIAL.options.EMBER_TEA": 150}
# Abyssal Baths: after Immerse, a page that comes up again with every Linger, whose options
# count per showing.
BATHS = "ABYSSAL_BATHS.pages."
BATHS_PAGE = BATHS + "ALL.options."


def baths_visit(j):
    """The picks of run j's visit to Abyssal Baths: Immerse and linger twice, Immerse and leave,
    or abstain."""
    return [[BATHS + "INITIAL.options.IMMERSE", BATHS_PAGE + "LINGER", BATHS_PAGE + "LINGER", BATHS_PAGE + "EXIT_BATHS"],
            [BATHS + "INITIAL.options.IMMERSE", BATHS_PAGE + "EXIT_BATHS"],
            [BATHS + "INITIAL.options.ABSTAIN"]][j % 3]


def event_fixture_runs(character, options):
    """Synthetic runs visiting events, `options`: event -> its option keys on the first page.
    Run j visits each event unless j % 4 == 3, and picks option (j % 3) % len(options) of those
    on offer, never the NEVER_PICKED one. Even runs lock the DECK_GATES and GOLD_GATES options:
    30 gold, and the Attack only gained after the events. Odd runs have 200 gold and the Attack
    until after the events, when it is removed. Every character counts: every other run is
    another character's. Plus runs that must not count: Ascension 6, two players, a custom
    game, all picking the first option."""
    other = "SILENT" if character != "SILENT" else "IRONCLAD"

    def run(j, ascension=10, mode="standard", players=1):
        locked = j % 2 == 0
        gold = 30 if locked else 200
        floors = []
        for event, keys in options.items():
            if j % 4 == 3:
                continue
            pickable = [k for k in keys if k != NEVER_PICKED.get(event) and not (locked and (k in DECK_GATES or k in GOLD_GATES))] or keys
            pick = pickable[0] if ascension < 7 or mode != "standard" or players > 1 else pickable[(j % 3) % len(pickable)]
            floors.append(event_floor(event, [pick], gold))
        if j % 4 != 3:
            floors.append(event_floor("ABYSSAL_BATHS", baths_visit(j), gold))
        # An ancient's choice is no event visit.
        floors.insert(0, {"map_point_type": "ancient", "rooms": [{"room_type": "event", "model_id": "EVENT.NEOW"}],
                          "player_stats": [{"current_gold": gold,
                                            "event_choices": [{"title": {"key": "LEAD_PAPERWEIGHT.title", "table": "relics"}}]}]})
        change = "cards_gained" if locked else "cards_removed"
        floors.append({"map_point_type": "monster", "player_stats": [{"current_gold": gold, change: [{"id": ATTACK}]}]})
        deck = [{"id": SKILL}] + ([{"id": ATTACK}] if locked else [])
        player = {"character": f"CHARACTER.{character if j % 2 == 0 else other}", "deck": deck, "relics": []}
        return {"schema_version": 9, "ascension": ascension, "game_mode": mode, "win": False,
                "players": [player] * players, "map_point_history": [floors]}
    return [run(j) for j in range(12)] + [run(0, ascension=6), run(0, players=2), run(0, mode="custom")]


def event_floor(event, picks, gold):
    return {"map_point_type": "unknown", "rooms": [{"room_type": "event", "model_id": f"EVENT.{event}"}],
            "player_stats": [{"current_gold": gold, "event_choices": [{"title": {"key": f"{k}.title", "table": "events"}} for k in picks]}]}


def deck_before(floors, n, deck):
    """The deck (card IDs) before floor n: the final `deck` with the cards removed, transformed
    and gained from floor n on undone, from the last floor back, as History.cs rebuilds it."""
    deck = list(deck)
    for floor in reversed(floors[n:]):
        stats = (floor.get("player_stats") or [{}])[0]
        deck += [c["id"] for c in stats.get("cards_removed", [])] + [t["original_card"]["id"] for t in stats.get("cards_transformed", [])]
        for card in [c["id"] for c in stats.get("cards_gained", [])] + [t["final_card"]["id"] for t in stats.get("cards_transformed", [])]:
            if card in deck:
                deck.remove(card)
    return deck


def offered(floors, n, key, deck):
    """Whether the visit on floor n offered a DECK_GATES or GOLD_GATES option, from the gold the
    floor before ended with, or the deck before it. True for other options."""
    if key in GOLD_GATES:
        return n == 0 or floors[n - 1]["player_stats"][0]["current_gold"] >= GOLD_GATES[key]
    if key in DECK_GATES:
        return bool(DECK_GATES[key] & set(deck_before(floors, n, deck)))
    return True


def check_starting_decks(result, runs):
    """Rewinding each solo run's final deck to before its first floor (deck_before) gives the
    character's starting deck: at most 15 cards, the same in most of the character's runs. A
    game update that renames the fields the mod rebuilds decks from (docs/UPDATING.md) leaves
    the final decks instead."""
    starts = {}
    for run in runs:
        if len(run.get("players", [])) != 1:
            continue
        floors = [f for act_floors in run["map_point_history"] for f in act_floors]
        deck = tuple(sorted(deck_before(floors, 0, [c["id"] for c in run["players"][0]["deck"]])))
        starts.setdefault(run["players"][0]["character"], []).append(deck)
    for character, decks in sorted(starts.items()):
        common = max(set(decks), key=decks.count)
        share = decks.count(common) / len(decks)
        if len(common) > 15 or len(decks) >= 5 and share < 0.8:
            result.fail(f"{character}: the rebuilt starting decks are not the starting deck ({share:.0%} alike, "
                        f"{len(common)} cards): check the deck fields in docs/UPDATING.md (Personal stats)")
        else:
            result.note(f"{character}: {share:.0%} of {len(decks)} runs rewind to the same {len(common)}-card deck ✓")


def count_events(runs):
    """From runs of every character at Ascension 7+, standard, alone: per option key, in how
    many visits it was picked ("picked") and how often ("times"), the visits per event, per
    gated option in how many of them it was locked ("locked"), and the runs counted."""
    counts, times, visits, locked, counted = {}, {}, {}, {}, 0
    for run in runs:
        if len(run.get("players", [])) != 1 or run.get("game_mode") != "standard" or run.get("ascension", 0) < 7:
            continue
        counted += 1
        floors = [f for act_floors in run["map_point_history"] for f in act_floors]
        deck = [c["id"] for c in run["players"][0]["deck"]]
        for n, floor in enumerate(floors):
            if floor["map_point_type"] == "ancient":
                continue
            for room in floor.get("rooms", []):
                if room["room_type"] == "event":
                    event = room["model_id"].split(".", 1)[1]
                    visits[event] = visits.get(event, 0) + 1
                    picked = [c["title"]["key"].removesuffix(".title") for c in floor["player_stats"][0].get("event_choices", [])]
                    for key in (*DECK_GATES, *GOLD_GATES):
                        if key.startswith(f"{event}.") and key not in picked and not offered(floors, n, key, deck):
                            locked[key] = locked.get(key, 0) + 1
                    for key in set(picked):
                        counts[key] = counts.get(key, 0) + 1
                    for key in picked:
                        times[key] = times.get(key, 0) + 1
    return {"picked": counts, "times": times, "visits": visits, "locked": locked, "runs": counted}


def first_page_counts(history, event, key):
    """An option on an event's first page: picked in how many of the event's visits that offered it."""
    picked, visits = history["picked"].get(key, 0), history["visits"].get(event, 0) - history["locked"].get(key, 0)
    return (f"Pick {picked}/{visits}" if picked else "Unpicked",
            f"Picked in {picked} of {visits} visits" if picked else f"Never picked in {visits} visits")


def baths_counts(history, event, key):
    """An option on Abyssal Baths' repeating page: picked how often of the times it came up."""
    taken = history["times"].get(key, 0)
    shown = sum(n for k, n in history["times"].items() if k.startswith(BATHS_PAGE))
    return f"Pick {taken}/{shown}", f"Picked {taken} of {shown} times"


def event_options(view):
    """The options on screen that can be picked."""
    return [e for e in view["events"] if not e["locked"] and fully_inside(e["rect"], view["window"])]


def check_event_placement(result, view, options, shot):
    """The options' badges sit all at the box's right end, vertically centred and clear of the
    option's text, or all on the box's top edge."""
    placed = []
    for option in options:
        badge = next((b for b in view["badges"] if b["visible"] and b["item_id"] == option["id"]), None)
        if badge:
            placed.append((option, badge, badge["rect"][1] < option["rect"][1] + 5))
    on_top = [p for p in placed if p[2]]
    if on_top and len(on_top) < len(placed):
        result.fail(f"badges on the box tops and at the right ends on one screen: {[p[0]['id'] for p in on_top]} on top")
    elif on_top:
        check_badges_on_box_top(result, view, shot, {o["id"]: o["rect"] for o in options})
        result.note("badges on the box tops (a text reaches the right end)")
    for option, badge, top in placed:
        if top:
            continue
        (ox, oy, ow, oh), (bx, by, bw, bh) = option["rect"], badge["rect"]
        text = option.get("text_rect")
        if abs(by + bh / 2 - (oy + oh / 2)) > 3:
            result.fail(f"{option['id']}: badge middle at y={by + bh / 2:.1f}, box middle at y={oy + oh / 2:.1f}")
        elif bx + bw > ox + ow:
            result.fail(f"{option['id']}: badge runs past the box's right end")
        elif text and bx < text[0] + text[2]:
            result.fail(f"{option['id']}: badge at x={bx:.0f} covers the text, which ends at x={text[0] + text[2]:.0f}")
        else:
            result.note(f"{option['id']}: badge at the right end, {bx - text[0] - text[2]:.0f} px clear of the text ✓")


def check_event(result, event, history, shot_name, expect=first_page_counts):
    """Each option of the event on screen shows "Pick n/m" or "Unpicked" from all characters'
    visits (`expect`: the badge and the tip's line), placed as check_event_placement says, and
    its tip spells that out."""
    view = inspect()
    shot = screenshot(shot_name)
    result.screenshots.append(shot)
    options = event_options(view)
    if len(options) < 2:
        result.fail(f"{event}: {len(options)} options to pick, expected two or more")
        return
    for option in options:
        key = option["id"]
        want = expect(history, event, key)[0]
        mine = [b for b in view["badges"] if b["visible"] and b["item_id"] == key]
        if not mine:
            result.fail(f"{key}: no badge (expected {want!r})")
        elif mine[0]["label"] != want:
            result.fail(f"{key}: badge reads {mine[0]['label']!r}, expected {want!r}")
        else:
            result.note(f"{key}: {want} ✓")
    check_event_placement(result, view, options, shot)
    for n, option in enumerate(options):
        key = option["id"]
        wanted = ["Your runs, all characters", expect(history, event, key)[1]]
        tips, view = hovered_stats_tips(option)
        result.screenshots.append(screenshot(f"{shot_name}_tip{n}"))
        if not tips:
            result.fail(f"{key}: no stats tip while hovered")
            continue
        text = " ".join((tips[0]["text"] or "").split())
        missing = [w for w in wanted if w not in text]
        if missing:
            result.fail(f"{key}: the tip lacks {missing}: {text[:200]!r}")
        elif "Locked" in text:
            result.fail(f"{key}: a note on locking, which the counts leave out: {text[:200]!r}")
        elif wrapped := wrapped_lines(tips[0]):
            result.fail(f"{key}: stats tip lines wrap: {wrapped}")
        elif not fully_inside(tips[0]["rect"], view["window"]):
            result.fail(f"{key}: stats tip runs off screen at {[round(v) for v in tips[0]['rect']]}")
        else:
            result.note(f"{key}: tip {' / '.join(wanted[1:])} ✓")
    hover(10, 10)


def wait_history_read(count, since):
    """Waits until the mod has rescanned a history of `count` runs (logged after line `since`)."""
    marker = f"[TierListOverlay] Run history: {count} runs"
    wait_for(lambda: any(line.startswith(marker) for line in log_since(since)), "the mod to read the test history", timeout=15)
    time.sleep(1.0)  # badges follow on the next frames


def write_test_history(runs):
    """Replaces the test-history folder's runs, under new names, so the mod reads them anew.
    Plus a backup file, which it must ignore. Returns the log line count before."""
    since = log_length()
    shutil.rmtree(TEST_HISTORY, ignore_errors=True)
    TEST_HISTORY.mkdir(parents=True)
    start = int(time.time() * 1000)
    for n, run in enumerate(runs):
        (TEST_HISTORY / f"{start + n}.run").write_text(json.dumps(run, indent=2))
    (TEST_HISTORY / f"{start}.run.backup").write_text(json.dumps(runs[0]))
    wait_history_read(len(runs), since)
    return since


def remove_test_history():
    shutil.rmtree(TEST_HISTORY, ignore_errors=True)


def personal_label(kind, c):
    return f"{PERSONAL_KINDS[kind][0]} {c[0]}/{c[1]}"


def personal_entry(history, kind, item_id):
    """What the mod shows of the player's numbers: ("unpicked", c), ("counts", c) or (None, c)."""
    single = kind in ("skip", "remove")
    c = history["counts"].get((kind, "" if single else item_id), [0, 0])
    if not single and c[0] == 0:
        return "unpicked", c
    return ("counts" if c[1] else None), c


def check_personal_badges(result, view, history, context_type, items):
    """Each item (id, kind, group) in `context_type` shows "Unpicked" when never taken, and
    otherwise the player's counts (Skip and card removal: none before the first offer). Never
    a tier beside the others' stats."""
    badges = [b for b in view["badges"] if b["visible"] and in_context(b, context_type)]
    for item_id, kind, group in items:
        shown, c = personal_entry(history, kind, item_id)
        mine = [b for b in badges if b["item_id"] == item_id]
        want = {"unpicked": "Unpicked", "counts": personal_label(kind, c)}.get(shown)
        if want is None:
            if mine:
                result.fail(f"{item_id}: never offered, but shows {mine[0]['label']!r}")
            else:
                result.note(f"{item_id}: {c[0]}/{c[1]}, no badge ✓")
        elif not mine:
            result.fail(f"{item_id}: no badge (expected {want!r})")
        elif mine[0]["label"] != want:
            result.fail(f"{item_id}: badge reads {mine[0]['label']!r}, expected {want!r}")
        else:
            result.note(f"{item_id}: {want} ✓")


def hovered_stats_tips(item):
    """Hovers the item and returns its visible stats tips, and the view."""
    mine = lambda view: [t for t in view["stats_tips"] if t["visible"] and t["item_id"] == item["id"]]
    view = hover_until(centre(item["rect"]), mine)
    return mine(view), view


def check_personal_tip(result, item, kind, group, history, shot_name):
    """Hovering the item shows one stats tip with the player's numbers first (5+ offers), then
    Untapped.gg's when logged in."""
    shown, c = personal_entry(history, kind, item["id"])
    enough = shown is not None
    tips, view = hovered_stats_tips(item)
    result.screenshots.append(screenshot(shot_name))
    if not tips:
        if enough:
            result.fail(f"{item['id']}: no stats tip while hovered")
        else:
            result.note(f"{item['id']}: never offered and no global stats, no stats tip ✓")
        return
    text = " ".join((tips[0]["text"] or "").split())
    if not enough:
        if text.startswith("Your "):
            result.fail(f"{item['id']}: never offered, but the tip shows yours: {text[:160]!r}")
        else:
            result.note(f"{item['id']}: never offered, only global stats in the tip ✓")
        return
    verb = {"shop": "bought", "remove": "removed"}.get(kind, "picked")
    key = (kind, "" if kind in ("skip", "remove") else item["id"])
    lower = history["lower"].get(key, 0)
    if shown == "unpicked":
        wanted = ["Never offered yet" if c[1] == 0 else f"Never {verb} in {c[1]} {'offer' if c[1] == 1 else 'offers'}"]
        if lower:
            wanted.append(f"{verb.capitalize()} in {lower} {'run' if lower == 1 else 'runs'} below A7")
    else:
        wanted = [PERSONAL_KINDS[kind][1].format(c[0], c[1], percent(100 * c[0] / c[1]))]
    if shown == "counts" and group in ("cards", "relics") and kind not in ("skip", "remove") and history["wins"]:
        w = history["wins"]
        wanted.append(f"In {history['in_wins'].get((group, item['id']), 0)} of your {w} {'win' if w == 1 else 'wins'}")
    if key in history["others"]:
        wanted.append("Other characters: {} of {}".format(*history["others"][key]))
    missing = [w for w in wanted if w not in text]
    heading = f"Your {history['character'].capitalize()} runs"
    if not text.startswith(heading):
        result.fail(f"{item['id']}: the tip does not start with {heading!r}: {text[:160]!r}")
    elif missing:
        result.fail(f"{item['id']}: the tip lacks {missing}: {text[:200]!r}")
    elif ("of your" in text) != any("of your" in w for w in wanted):
        result.fail(f"{item['id']}: the tip has a wins line, expected none: {text[:200]!r}")
    elif "Other players'" in text and text.index("Other players'") < text.index(heading):
        result.fail(f"{item['id']}: other players' stats before yours: {text[:200]!r}")
    elif ("Based on" in text) != read_settings()["stats_samples"]:
        result.fail(f"{item['id']}: the tip {'lacks' if read_settings()['stats_samples'] else 'shows'} the sample size: {text[:200]!r}")
    elif wrapped := wrapped_lines(tips[0]):
        result.fail(f"{item['id']}: stats tip lines wrap: {wrapped}")
    elif not fully_inside(tips[0]["rect"], view["window"]):
        result.fail(f"{item['id']}: stats tip runs off screen at {[round(v) for v in tips[0]['rect']]}")
    elif share_covered(item["rect"], tips[0]["rect"]) > 0.05:
        result.fail(f"{item['id']}: stats tip covers {share_covered(item['rect'], tips[0]['rect']):.0%} of the item")
    else:
        result.note(f"{item['id']}: tip {' / '.join(wanted)} ✓")


def reward_cards(view):
    return [c for c in view["cards"] if c["on_screen"] and in_context(c, "NCardRewardSelectionScreen")]


def scenario_personal_card_reward(result, tiers):
    """Personal stats: card reward badges and Skip show your counts, the tip spells them out.
    Then tier badges with the stats tips on, and off."""
    ensure_stats(True, "personal")
    open_card_reward()
    view = inspect()
    cards = reward_cards(view)
    runs = fixture_runs(view["character"], [("reward", "cards", c["id"]) for c in cards])
    try:
        write_test_history(runs)
        history = count_history(runs, view["character"])
        view = inspect()
        result.screenshots.append(screenshot("personal_card_reward"))
        items = [(c["id"], "reward", "cards") for c in cards] + [("SKIP", "skip", "skip")]
        check_personal_badges(result, view, history, "NCardRewardSelectionScreen", items)
        for n, card in enumerate(cards):
            check_personal_tip(result, card, "reward", "cards", history, f"personal_card_reward_tip{n}")
        skip = next((b for b in view["badges"] if b["item_id"] == "SKIP" and b["visible"]), None)
        if skip:
            x, y, w, h = skip["rect"]
            check_personal_tip(result, {"id": "SKIP", "rect": [x, y + h, w, h * 1.5]}, "skip", "skip", history, "personal_skip_tip")
        hover(10, 10)
        act("skip_card_reward")

        # Tier badges, your stats in the tips all the same.
        set_setting("cards", "tiers")
        open_card_reward()
        view = inspect()
        cards = reward_cards(view)
        runs = fixture_runs(view["character"], [("reward", "cards", c["id"]) for c in reversed(cards)])
        write_test_history(runs)
        history = count_history(runs, view["character"])
        view = inspect()
        result.note("tier badges, stats tips on:")
        if any(b["kind"] == "stats" and b["visible"] for b in view["badges"]):
            result.fail("stats badges with the badges on the tier list")
        check_badges(result, view, tiers, "cards", "NCardRewardSelectionScreen")
        enough = [c for c in cards if history["counts"].get(("reward", c["id"]), [0, 0])[1]]
        if enough:
            check_personal_tip(result, enough[0], "reward", "cards", history, "personal_tips_tier_badges")
        hover(10, 10)
        act("skip_card_reward")

        # Stats tips off: no stats tip, the tier list's tip stays.
        set_setting("stats_tips", False)
        open_card_reward()
        view = inspect()
        cards = reward_cards(view)
        runs = fixture_runs(view["character"], [("reward", "cards", c["id"]) for c in reversed(cards)])
        write_test_history(runs)
        history = count_history(runs, view["character"])
        enough = [c for c in cards if history["counts"].get(("reward", c["id"]), [0, 0])[1]]
        if enough:
            tips, _ = hovered_stats_tips(enough[0])
            result.screenshots.append(screenshot("personal_tips_off"))
            if tips:
                result.fail(f"{enough[0]['id']}: a stats tip with stats tips off")
            else:
                result.note("stats tips off: no stats tip ✓")
            check_tip(result, tiers, enough[0])
        hover(10, 10)
        act("skip_card_reward")
    finally:
        remove_test_history()
        set_setting("stats_tips", True)


def scenario_personal_shop(result, tiers):
    """Personal stats: shop cards, relics, potions and card removal show your counts."""
    ensure_stats(True, "personal")
    view, _ = open_shop("personal_shop_before")
    shown = lambda kind: [i for i in view[kind] if in_context(i, "NMerchantInventory") and i["on_screen"]]
    offers = ([("shop", "cards", c["id"]) for c in shown("cards")] + [("shop", "relics", r["id"]) for r in shown("relics")]
              + [("shop", "potions", p["id"]) for p in shown("potions")])
    runs = fixture_runs(view["character"], offers)
    try:
        write_test_history(runs)
        history = count_history(runs, view["character"])
        view = inspect()
        result.screenshots.append(screenshot("personal_shop"))
        items = [(i, "shop", g) for _, g, i in offers] + [("CARD_REMOVAL", "remove", "remove")]
        check_personal_badges(result, view, history, "NMerchantInventory", items)
        check_prices_clear(result)
        for group in ("cards", "relics", "potions"):
            # The second of a kind.
            items = shown(group)
            if len(items) > 1:
                check_personal_tip(result, items[1], "shop", group, history, f"personal_shop_{group}_tip")
        # The removal slot's coin, above its badge.
        removal = next((b for b in view["badges"] if b["item_id"] == "CARD_REMOVAL" and b["visible"]), None)
        if removal:
            x, y, w, h = removal["rect"]
            check_personal_tip(result, {"id": "CARD_REMOVAL", "rect": [x, y - 3 * h, w, 2 * h]}, "remove", "remove", history,
                               "personal_shop_removal_tip")
        hover(10, 10)

        # Sample sizes add how many of your runs the numbers come from.
        set_setting("stats_samples", True)
        view, _ = open_shop("personal_shop_samples")
        items = [c for c in view["cards"] if in_context(c, "NMerchantInventory") and c["on_screen"]]
        runs = fixture_runs(view["character"], [("shop", "cards", c["id"]) for c in items])
        write_test_history(runs)
        history = count_history(runs, view["character"])
        if len(items) > 1:
            check_personal_tip(result, items[1], "shop", "cards", history, "personal_shop_samples_tip")
            tips, _ = hovered_stats_tips(items[1])
            want = f"Based on {history['runs']} runs at Ascension 7+"
            if tips and want not in " ".join(tips[0]["text"].split()):
                result.fail(f"sample sizes: the tip lacks {want!r}: {tips[0]['text'][:200]!r}")
        hover(10, 10)
    finally:
        remove_test_history()
        set_setting("stats_samples", False)


def scenario_personal_ancient(result, tiers):
    """Personal stats: ancient options show your counts."""
    ensure_stats(True, "personal")
    open_ancient()
    time.sleep(2)
    hover(10, 10)
    view = inspect()
    options = [r for r in view["relics"] if r["type"] == "NEventOptionButton" and fully_inside(r["rect"], view["window"])]
    runs = fixture_runs(view["character"], [("ancient", "relics", o["id"]) for o in options])
    try:
        write_test_history(runs)
        history = count_history(runs, view["character"])
        view = inspect()
        shot = screenshot("personal_ancient")
        result.screenshots.append(shot)
        check_personal_badges(result, view, history, "NEventRoom", [(o["id"], "ancient", "relics") for o in options])
        check_badges_on_box_top(result, view, shot)
        for i, option in enumerate(options):
            check_personal_tip(result, option, "ancient", "relics", history, f"personal_ancient_tip{i}")
        hover(10, 10)
    finally:
        remove_test_history()


def scenario_personal_event(result, tiers):
    """Personal stats: event options show your counts from all characters. An option the event
    locks without a card to enchant or enough gold counts only the visits that offered it.
    Badges off: the tip stays."""
    set_setting("stats_tips", True)
    if read_settings()["events"] != "personal":
        set_setting("events", "personal")
    open_event(EVENTS[0])
    character = inspect()["character"]
    # The options of each event, from the screen: open each once.
    options = {EVENTS[0]: [o["id"] for o in event_options(inspect())]}
    for event in EVENTS[1:]:
        open_event(event)
        options[event] = [o["id"] for o in event_options(inspect())]
    runs = event_fixture_runs(character, options)
    try:
        write_test_history(runs)
        history = count_events(runs)
        result.note(f"{history['runs']} runs counted, visits {history['visits']}")
        check_event(result, EVENTS[2], history, "personal_event_top")
        open_event(EVENTS[1])
        if not history["locked"]:
            result.fail("the synthetic runs lock no option")
        check_event(result, EVENTS[1], history, "personal_event_locked")
        open_event(EVENTS[0])
        check_event(result, EVENTS[0], history, "personal_event")
        # Abyssal Baths' page after Immerse counts every time it comes up.
        open_event("ABYSSAL_BATHS")
        act("choose_event_option", index=0)
        time.sleep(2.0)
        check_event(result, "ABYSSAL_BATHS", history, "personal_event_repeat", baths_counts)

        # Badges off: none to see, the stats tip all the same.
        set_setting("events", "none")
        open_event(EVENTS[0])
        view = inspect()
        if [b for b in view["badges"] if b["visible"] and in_context(b, "NEventOptionButton")]:
            result.fail("event badges shown with event badges off")
        tips, _ = hovered_stats_tips(event_options(view)[0])
        result.screenshots.append(screenshot("personal_event_badges_off"))
        if tips:
            result.note("badges off: stats tip still there ✓")
        else:
            result.fail("badges off: no stats tip on the event option")
        hover(10, 10)
    finally:
        remove_test_history()
        set_setting("events", "personal")


def history_files(profile):
    """A profile's run history files, unmodded and modded (the mod reads both), as
    (copy name, path). Modded ones get a prefix, as both folders name files by start time."""
    return sorted((f"{prefix}{f.name}", f) for account in SAVES.iterdir() if account.is_dir()
                  for prefix, folder in (("", account / f"profile{profile}"), ("modded-", account / f"modded/profile{profile}"))
                  for f in (folder / "saves/history").glob("*.run"))


def scenario_personal_real(result, tiers):
    """Personal stats from a copy of the active profile's real run history: the mod reads every
    file, a card reward's badges and tips match the counts here, and the decks rebuilt for event
    options rewind to the starting decks. Notices a game update that
    changes the .run files, which the synthetic runs of the other personal_* scenarios can't."""
    profile = _request(f"{MCP}/api/v1/profiles")["current_profile_id"]
    files = history_files(profile)
    if not files:
        raise CheckError(f"profile {profile} has no run history yet: finish or abandon a run first (tools/check.py --new-run)")
    runs = [json.loads(path.read_text()) for _, path in files]
    schemas = sorted({run.get("schema_version") or 0 for run in runs})
    if schemas[-1] > KNOWN_SCHEMA:
        result.fail(f"run history schema {schemas[-1]}, the checks know {KNOWN_SCHEMA}: compare a new .run file with "
                    "the fields in docs/UPDATING.md (Personal stats), then raise KNOWN_SCHEMA in checklib/personal.py")
    game_version = json.loads((GAME_APP / "Contents/Resources/release_info.json").read_text())["version"]
    if not any(run.get("build_id") == game_version for run in runs):
        result.fail(f"no run from game {game_version} in profile {profile}'s history, so nothing in this game "
                    "version's format: tools/check.py --new-run abandons the test run, which makes one")
    check_starting_decks(result, runs)
    ensure_stats(True, "personal")
    open_card_reward()
    try:
        # Copies, read only: nothing is ever written to the profile's folders.
        since = log_length()
        shutil.rmtree(TEST_HISTORY, ignore_errors=True)
        TEST_HISTORY.mkdir(parents=True)
        for name, path in files:
            (TEST_HISTORY / name).write_bytes(path.read_bytes())
        wait_history_read(len(files), since)
        skipped = [line.split("] ", 1)[-1] for line in log_since(since) if "Skipping run history file" in line]
        if skipped:
            result.fail(f"the mod could not read {len(skipped)} of {len(files)} files: {skipped[:3]}")
        view = inspect()
        history = count_history(runs, view["character"])
        result.note(f"{len(files)} runs of profile {profile}, schema {schemas}, "
                    f"{history['runs']} counted with {view['character']}, {history['wins']} won")
        result.screenshots.append(screenshot("personal_real"))
        cards = reward_cards(view)
        check_personal_badges(result, view, history, "NCardRewardSelectionScreen",
                              [(c["id"], "reward", "cards") for c in cards] + [("SKIP", "skip", "skip")])
        for n, card in enumerate(cards):
            check_personal_tip(result, card, "reward", "cards", history, f"personal_real_tip{n}")
        hover(10, 10)
        act("skip_card_reward")
    finally:
        remove_test_history()


SCENARIOS = {
    "personal_card_reward": scenario_personal_card_reward,
    "personal_shop": scenario_personal_shop,
    "personal_ancient": scenario_personal_ancient,
    "personal_event": scenario_personal_event,
    "personal_real": scenario_personal_real,
}
