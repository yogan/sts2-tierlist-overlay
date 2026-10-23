"""Tier badges and tier tips on every screen that offers or shows items, and the tier lists
behind them."""

import json
import time

from .game import (GAME_APP, GAME_LOG, MOD_DIR, _try_call, act, call, console, enter_combat, hover, inspect,
                   open_ancient, open_card_reward, open_shop, screenshot, state, wait_for, wait_state)
from .geometry import fully_inside
from .verify import (BUTTON_TYPES, ICON_TYPES, check_badge_rim, check_badges, check_badges_on_border,
                     check_badges_on_box_top, check_prices_clear, check_tip, check_tooltip_layering, exhausted_relics,
                     in_context, tier_label)


def scenario_card_reward(result, tiers):
    """Card reward after a won fight, plus tooltip layering while hovering each card."""
    index = open_card_reward()
    view = inspect()
    result.screenshots.append(screenshot("card_reward"))
    check_badges(result, view, tiers, "cards", "NCardRewardSelectionScreen")
    cards = [c for c in view["cards"] if c["on_screen"] and in_context(c, "NCardRewardSelectionScreen")]
    for n, card in enumerate(cards):
        check_tip(result, tiers, card)
        time.sleep(1.0)  # tips fade in, so judge layering once they are opaque
        shot = screenshot(f"card_reward_hover{n}")
        result.screenshots.append(shot)
        check_tooltip_layering(result, inspect(), tiers, shot)
    hover(10, 10)
    act("skip_card_reward")

    # Reopening reuses the pooled card holders, which once left stale badges behind.
    wait_state("rewards")
    act("claim_reward", index=index)
    wait_state("card_reward")
    time.sleep(1.5)
    result.note("reopened card reward:")
    check_badges(result, inspect(), tiers, "cards", "NCardRewardSelectionScreen")
    result.screenshots.append(screenshot("card_reward_reopened"))
    act("skip_card_reward")


def scenario_kaleidoscope(result, tiers):
    """Other characters' cards (Kaleidoscope: two card rewards from other characters) show
    their character's tier, and the tip says whose list rated them."""
    console("relic add KALEIDOSCOPE")
    wait_state("rewards")
    time.sleep(1.5)
    # A skipped reward stays on the rewards screen.
    for n in range(2):
        act("claim_reward", index=n)
        wait_state("card_reward")
        time.sleep(1.5)
        view = inspect()
        result.screenshots.append(screenshot(f"kaleidoscope{n}"))
        cards = [c for c in view["cards"] if c["on_screen"] and in_context(c, "NCardRewardSelectionScreen")]
        if not any(tiers.rated_for(c.get("pool")) for c in cards):
            result.fail(f"no other character's card offered: {[(c['id'], c.get('pool')) for c in cards]}")
        check_badges(result, view, tiers, "cards", "NCardRewardSelectionScreen")
        for i, card in enumerate(cards):
            check_tip(result, tiers, card, f"kaleidoscope{n}_tip{i}")
        hover(10, 10)
        act("skip_card_reward")
        wait_state("rewards")
        time.sleep(1)
    console("relic remove KALEIDOSCOPE")


def scenario_choose_a_card(result, tiers):
    """'Choose a card' screen, opened by a Colorless Potion in combat (colorless list)."""
    enter_combat()
    console("potion COLORLESS_POTION")
    s = wait_for(lambda: (s := state()).get("battle", {}).get("is_play_phase") and s, "play phase")
    slot = next(p["slot"] for p in s["player"]["potions"] if p["id"] == "COLORLESS_POTION")
    act("use_potion", slot=slot)
    time.sleep(2.5)
    view = inspect()
    result.screenshots.append(screenshot("choose_a_card"))
    check_badges(result, view, tiers, "cards", "NChooseACardSelectionScreen")
    console("win")


def scenario_shop(result, tiers):
    """Shop inventory: cards."""
    view, shot = open_shop("shop")
    result.screenshots.append(shot)
    check_badges(result, view, tiers, "cards", "NMerchantInventory")
    cards = [c for c in view["cards"] if in_context(c, "NMerchantInventory") and tiers.get(c["id"])]
    if cards:
        check_tip(result, tiers, cards[0], "shop_tip")
    hover(10, 10)


def scenario_shop_relics(result, tiers):
    """Shop inventory: relics and potions."""
    view, shot = open_shop("shop_relics")
    result.screenshots.append(shot)
    exhausted_relics(result, view, "NMerchantInventory")
    check_prices_clear(result)
    check_badges(result, view, tiers, "relics", "NMerchantInventory", types=ICON_TYPES)
    check_badges(result, view, tiers, "potions", "NMerchantInventory", types=ICON_TYPES)
    relics = [r for r in view["relics"] if in_context(r, "NMerchantInventory") and tiers.get(r["id"], card=False)]
    if relics:
        check_tip(result, tiers, relics[0], "shop_relic_tip")
    hover(10, 10)


def scenario_treasure(result, tiers):
    """Treasure chest relic."""
    console("room Treasure")
    wait_state("treasure")
    time.sleep(3)  # the chest opens with an animation
    view = inspect()
    result.screenshots.append(screenshot("treasure"))
    check_badges(result, view, tiers, "relics", "NTreasureRoomRelicCollection")
    act("claim_treasure_relic", index=0)


def scenario_elite_loot(result, tiers):
    """Elite loot: relic and potion reward buttons."""
    console("room Elite")
    wait_state("elite")
    console("win")
    wait_state("rewards")
    time.sleep(1.5)
    view = inspect()
    result.screenshots.append(screenshot("elite_loot"))
    exhausted_relics(result, view, "NRewardsScreen")
    check_badges(result, view, tiers, "relics", "NRewardsScreen", types=BUTTON_TYPES)
    if any(i["type"] == "NRewardButton" for i in view["potions"]):
        check_badges(result, view, tiers, "potions", "NRewardsScreen", types=BUTTON_TYPES)


def scenario_ancient(result, tiers):
    """Ancient event (Darv) offering relics as options."""
    open_ancient()
    time.sleep(2)
    hover(10, 10)
    time.sleep(0.5)
    view = inspect()
    shot = screenshot("ancient")
    result.screenshots.append(shot)
    check_badges(result, view, tiers, "relics", "NEventRoom", types=BUTTON_TYPES)
    check_badges_on_box_top(result, view, shot)
    options = [r for r in view["relics"] if r["type"] == "NEventOptionButton" and fully_inside(r["rect"], view["window"])]
    # Each option: relics with keyword tips and without get their tips laid out differently.
    for i, option in enumerate(options):
        check_tip(result, tiers, option, f"ancient_tip_{i}")
    hover(10, 10)


def grid_from_relic(result, tiers, relic, context, picks, name):
    """Picks up a relic whose pickup opens a deck grid, checks the grid, then finishes the
    selection with the first `picks` cards and removes the relic again."""
    console(f"relic add {relic}")
    wait_state("card_select")
    time.sleep(1.5)
    view = inspect()
    result.screenshots.append(screenshot(name))
    check_badges(result, view, tiers, "cards", context)
    for i in range(picks):
        act("select_card", index=i)
    act("confirm_selection")
    time.sleep(1.5)
    if state()["state_type"] == "card_select":  # a confirmation preview
        act("confirm_selection")
    console(f"relic remove {relic}")


def scenario_transform(result, tiers):
    """Transform grid (Astrolabe: transform 3 cards)."""
    grid_from_relic(result, tiers, "ASTROLABE", "NDeckTransformSelectScreen", 3, "transform")


def scenario_enchant(result, tiers):
    """Enchant grid (Royal Stamp: enchant 1 card)."""
    grid_from_relic(result, tiers, "ROYAL_STAMP", "NDeckEnchantSelectScreen", 1, "enchant")


def scenario_remove(result, tiers):
    """Card removal grid (Precise Scissors). Adds a card first so the deck keeps its size."""
    console("card BATTLE_TRANCE Deck")
    grid_from_relic(result, tiers, "PRECISE_SCISSORS", "NDeckCardSelectScreen", 1, "remove")


def scenario_simple_grid(result, tiers):
    """Card choice grid offered as a reward (Sea Glass. Added by the console it offers Ironclad
    cards, the test character's own. Other characters' cards: kaleidoscope)."""
    grid_from_relic(result, tiers, "SEA_GLASS", "NSimpleCardSelectScreen", 0, "simple_grid")


def scenario_upgrade(result, tiers):
    """Upgrade grid at a rest site (Smith)."""
    console("room RestSite")
    s = wait_state("rest_site")
    option = next(o for o in s["rest_site"]["options"] if o["id"] == "SMITH")
    act("choose_rest_option", index=option["index"])
    wait_state("card_select")
    time.sleep(1.5)
    view = inspect()
    result.screenshots.append(screenshot("upgrade"))
    check_badges(result, view, tiers, "cards", "NDeckUpgradeSelectScreen")
    # Cards worth upgrading are marked with a "+".
    tiers.character = view["character"]
    for badge in view["badges"]:
        expected = tiers.get(badge["item_id"])
        if badge["visible"] and expected and in_context(badge, "NDeckUpgradeSelectScreen"):
            want = tier_label(expected.tier) + (" +" if expected.good_upgrade else "")
            if badge["label"] != want:
                result.fail(f"{badge['item_id']}: upgrade badge reads {badge['label']!r}, expected {want!r}")
    act("cancel_selection")


def scenario_deck_view(result, tiers):
    """Deck view from the top bar."""
    # The run's top bar may still be loading, and the deck may still be open from a failed run.
    if not any(in_context(c, "NDeckViewScreen") for c in inspect()["cards"]):
        wait_for(lambda: _try_call("NTopBarDeckButton", "OnRelease"), "the deck button")
        time.sleep(1.5)
    try:
        hover(10, 10)
        time.sleep(0.5)
        view = inspect()
        shot = screenshot("deck_view")
        result.screenshots.append(shot)
        check_badges(result, view, tiers, "cards", "NDeckViewScreen")
        check_badges_on_border(result, view, shot, "NDeckViewScreen")
        check_badge_rim(result, view, shot, "NDeckViewScreen")
    finally:
        call("NTopBarDeckButton", "OnRelease")


def scenario_bundle(result, tiers):
    """Bundle choice (Scroll Boxes: pick one of two card packs)."""
    console("relic add SCROLL_BOXES")
    time.sleep(2.5)
    view = inspect()
    result.screenshots.append(screenshot("bundle"))
    check_badges(result, view, tiers, "cards", "NChooseABundleSelectionScreen")
    act("select_bundle", index=0)
    time.sleep(1.5)
    result.note("pack preview:")
    result.screenshots.append(screenshot("bundle_preview"))
    check_badges(result, inspect(), tiers, "cards", "NChooseABundleSelectionScreen")
    act("confirm_bundle_selection")
    console("relic remove SCROLL_BOXES")


def version_tuple(v):
    return tuple(int(x) for x in v.lstrip("v").split("."))


def scenario_lists(result, tiers):
    """Tier lists are requested for the installed game version, cached, and their IDs checked."""
    game_version = json.loads((GAME_APP / "Contents/Resources/release_info.json").read_text())["version"]
    log = [line for line in GAME_LOG.read_text(errors="replace").splitlines() if "[TierListOverlay]" in line]
    if f"[TierListOverlay] Game version {game_version}" not in log:
        result.fail(f"mod did not log game version {game_version}")
    if any("Last tested on game" in line for line in log):
        result.fail(f"the mod was last tested on another game version than {game_version}: "
                    "run the routine in docs/UPDATING.md, then raise TestedGameVersion")
    stamp = (MOD_DIR / "cache" / "fetched.cfg").read_text() if (MOD_DIR / "cache" / "fetched.cfg").exists() else ""
    if f'game_version="{game_version}"' not in stamp:
        result.fail(f"cache was not fetched for {game_version}: {stamp.strip()!r}")
    for path in sorted((MOD_DIR / "cache").glob("*-*.tierlist")):
        list_version = json.loads(path.read_text())["tier_list"]["version"]
        if version_tuple(list_version) > version_tuple(game_version):
            result.fail(f"{path.name} is for {list_version}, newer than the game ({game_version})")
        else:
            result.note(f"{path.name}: {list_version}")
    if not any("Unknown IDs in" in line or "All tier list IDs are known" in line for line in log):
        result.fail("mod did not check the lists' IDs against the game")
    for line in log:
        if "Unknown IDs in" in line:
            result.note(line.split("] ", 1)[1])


SCENARIOS = {
    "card_reward": scenario_card_reward,
    "choose_a_card": scenario_choose_a_card,
    "shop": scenario_shop,
    "shop_relics": scenario_shop_relics,
    "treasure": scenario_treasure,
    "elite_loot": scenario_elite_loot,
    "ancient": scenario_ancient,
    "upgrade": scenario_upgrade,
    "transform": scenario_transform,
    "enchant": scenario_enchant,
    "remove": scenario_remove,
    "simple_grid": scenario_simple_grid,
    "kaleidoscope": scenario_kaleidoscope,
    "bundle": scenario_bundle,
    "deck_view": scenario_deck_view,
    "lists": scenario_lists,
}
