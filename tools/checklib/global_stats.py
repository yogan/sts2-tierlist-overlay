"""Untapped.gg's global stats on badges and in tips, and logging out.

With stats on, badges on card rewards, ancient options, the shop and the upgrade screen show
Untapped.gg's global stats instead of tiers. The checks ask the API the same question the mod
asked (its request body is in the game log) and expect each badge and tip to show the answer."""

import json
import shutil
import time
import urllib.error
import urllib.request

from .game import (BADGE_KEYS, GAME_LOG, MOD_DIR, CheckError, act, click, console, ensure_test_run, find_node,
                   hover, hover_until, inspect, leave_screens, log_since, log_length, open_ancient, open_card_reward,
                   open_shop, read_settings, restart_game, screenshot, tree, wait_for, wait_state)
from .geometry import centre, fully_inside, share_covered
from .settings import (account_row, close_settings, ensure_stats, open_settings, row_enabled, set_setting, setting_row,
                       setting_tip, shown_value)
from .tiers import scenario_deck_view
from .verify import (ICON_TYPES, check_badges, check_badges_on_box_top, check_prices_clear, check_tooltip_layering,
                     exhausted_relics, in_context, wrapped_lines)

API = "https://api.sts2.untapped.gg"
ACCOUNT = MOD_DIR / "account.cfg"
USER_AGENT = "TierListOverlay check.py"

# Per screen kind: (badge label of the action, tip title, tip wording of the action).
STATS_KINDS = {
    "card-reward-stats": ("Pick", "Card Reward Stats", "Picked when offered"),
    "skip": ("Pick", "Skip Stats", "Picked instead of a card"),
    "ancient-choice-stats": ("Pick", "Ancient Choice Stats", "Picked when offered"),
    "shop-stats": ("Buy", "Shop Stats", "Bought when offered"),
    "card-upgrade-stats": ("Upgr.", "Card Upgrade Stats", "Picked to upgrade"),
}


def read_cfg(path):
    values = {}
    for line in path.read_text().splitlines():
        if "=" in line:
            key, value = (part.strip() for part in line.split("=", 1))
            values[key] = json.loads(value) if value.startswith('"') else value
    return values


def last_stats_request(endpoint, since=0):
    """The body of the newest stats request the mod logged for `endpoint` after log line `since`."""
    marker = f"[TierListOverlay] Stats /api/v1/global-stats/{endpoint}/ for "
    found = [line for line in log_since(since) if line.startswith(marker)]
    if not found:
        return None
    return found[-1][len(marker):].rsplit("}: ", 1)[0] + "}"


def ask_api(endpoint, body):
    token = read_cfg(ACCOUNT)["access_token"]
    req = urllib.request.Request(f"{API}/api/v1/global-stats/{endpoint}/", body.encode(), {
        "Content-Type": "application/json", "Accept": "application/json",
        "Authorization": f"Bearer {token}", "User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        raise CheckError(f"stats API {endpoint} -> HTTP {e.code}: {e.read()[:200]!r}") from None


def answer_groups(answer):
    """item group -> id -> entry, as the mod reads the answer (skip and removal under "")."""
    groups = {}
    for field, group, single in (("card_reward_stats", "cards", False), ("skip_button_stats", "skip", True),
                                 ("ancient_choice_stats", "relics", False), ("offered_cards_stats", "cards", False),
                                 ("offered_relics_stats", "relics", False), ("offered_potions_stats", "potions", False),
                                 ("remove_button_stats", "remove", True), ("card_stats", "cards", False)):
        if isinstance(answer.get(field), dict):
            groups.setdefault(group, {}).update({"": answer[field]} if single else answer[field])
    return groups


def action(entry):
    name = next((a for a in ("pick", "buy", "upgrade") if f"{a}_pct" in entry), "pick")
    return entry.get(f"{name}_pct")


def percent(value, signed=False):
    # Rounded half away from zero, like the mod (C# MidpointRounding.AwayFromZero). A share
    # that is neither none nor all never rounds to either.
    rounded = int(abs(value) + 0.5) * (1 if value >= 0 else -1)
    if not signed and rounded == 0 and value > 0:
        return "<1%"
    if not signed and rounded == 100 and value < 100:
        return ">99%"
    return ("+" if signed and rounded >= 0 else "") + f"{rounded}%"


WIDENED_SCOPE = 5  # the server's warning that it counted all characters


def all_characters(entry, item, view):
    """Whether the tip should say the stats are all characters': the server widened them, and
    other characters can get the item too (not in the character's own pools)."""
    widened = any(w.get("warning_enum") == WIDENED_SCOPE for w in entry.get("warnings") or [])
    group = {"NCard": "cards", "NPotion": "potions"}.get(item.get("type"), "relics" if item.get("type") else None)
    own = group is not None and item["id"] in (view.get("own_items") or {}).get(group, [])
    return widened and not own


def has_data(entry):
    return (entry.get("times_offered") or 0) > 0 and action(entry) is not None


def expected_label(kind, entry, act):
    """What the item's stats badge reads, or None for no badge (too few runs for stats)."""
    if not has_data(entry):
        return None
    parts = [f"{STATS_KINDS[kind][0]} {percent(action(entry))}"]
    for label, field, show in (("Act", "win_ca_pct_delta", act < 3), ("Run", "win_pct_delta", True)):
        if show:
            parts.append(f"{label} —" if entry.get(field) is None else f"{label} {percent(entry[field], True)}")
    return " | ".join(parts)


def check_stats_badges(result, view, endpoint, groups, act, context_type, items):
    """Each item (id, answer group, kind) in `context_type` must show a stats badge reading
    what the API answers for it."""
    badges = [b for b in view["badges"] if b["visible"] and in_context(b, context_type)]
    for item_id, group, kind in items:
        entry = groups.get(group, {}).get("" if group in ("skip", "remove") else item_id)
        mine = [b for b in badges if b["item_id"] == item_id]
        if entry is None:
            result.fail(f"{item_id}: the API answered nothing for it")
        elif not has_data(entry):
            if any(b["kind"] == "stats" for b in mine):
                result.fail(f"{item_id}: too few runs for stats, but shows {mine[0]['label']!r}")
            else:
                result.note(f"{item_id}: too few runs for stats, no stats badge ✓")
        elif not mine:
            result.fail(f"{item_id}: no badge (expected stats {expected_label(kind, entry, act)!r})")
        elif mine[0]["kind"] != "stats":
            result.fail(f"{item_id}: {mine[0]['kind']} badge {mine[0]['label']!r}, expected stats")
        elif mine[0]["label"] != (want := expected_label(kind, entry, act)):
            result.fail(f"{item_id}: badge reads {mine[0]['label']!r}, expected {want!r}")
        else:
            result.note(f"{item_id}: {want} ✓")


def check_stats_tip(result, item, entry, kind, act, shot_name):
    """Hovering the item shows the stats tip with the API's numbers, or none when there are too
    few runs for stats."""
    mine = lambda view: [t for t in view["stats_tips"] if t["visible"] and t["item_id"] == item["id"]]
    # Without data, any tip showing (a game tip) means the hover landed and none is coming.
    view = hover_until(centre(item["rect"]), lambda view: mine(view) or (not has_data(entry) and view["hover_tips"]))
    tips = mine(view)
    result.screenshots.append(screenshot(shot_name))
    if not has_data(entry):
        # Too few runs for global stats. A tip may still show the player's own ("Unpicked").
        if any("Other players'" in (t["text"] or "") for t in tips):
            result.fail(f"{item['id']}: too few runs for global stats, but the tip shows them")
        else:
            result.note(f"{item['id']}: too few runs for global stats, none in the tip ✓")
        return
    if len(tips) != 1:
        result.fail(f"{item['id']}: {len(tips)} stats tips while hovered (expected 1)")
        return
    tip, (_, title, wording) = tips[0], STATS_KINDS[kind]
    text = " ".join((tip["text"] or "").split())
    if tip["title"] != title:
        result.fail(f"{item['id']}: stats tip titled {tip['title']!r}, expected {title!r}")
    elif ("Based on" in text) != read_settings()["stats_samples"]:
        result.fail(f"{item['id']}: stats tip {'lacks' if read_settings()['stats_samples'] else 'shows'} the sample size: {text[:160]!r}")
    # The server's notes on thin data ("Insufficient Data Warning: ...") stay out of the tips.
    elif "Warning" in text or "Insufficient" in text:
        result.fail(f"{item['id']}: stats tip shows a data warning: {text[:160]!r}")
    elif f"{percent(action(entry))} {wording}" not in text:
        result.fail(f"{item['id']}: stats tip lacks {percent(action(entry))} {wording!r}: {text[:120]!r}")
    elif (entry.get("win_pct_delta") is None and (entry.get("win_ca_pct_delta") is None or act >= 3)) != ("Winrates: too little data" in text):
        result.fail(f"{item['id']}: stats tip says 'too little data' wrongly or not at all: {text[:200]!r}")
    elif "Winrates: too little data" not in text and (act < 3) != ("Act Winrate" in text):
        result.fail(f"{item['id']}: stats tip {'lacks' if act < 3 else 'has'} the act winrate in act {act}: {text[:120]!r}")
    elif "—" in text:
        result.fail(f"{item['id']}: stats tip shows a dash instead of saying there is too little data: {text[:200]!r}")
    elif all_characters(entry, item, view) != ("all characters" in text):
        result.fail(f"{item['id']}: stats tip {'lacks' if all_characters(entry, item, view) else 'has'} 'all characters' "
                    f"(warnings {[w.get('warning_enum') for w in entry.get('warnings') or []]}): {text[:200]!r}")
    elif wrapped := wrapped_lines(tip):
        result.fail(f"{item['id']}: stats tip lines wrap: {wrapped}")
    elif not fully_inside(tip["rect"], view["window"]):
        result.fail(f"{item['id']}: stats tip runs off screen at {[round(v) for v in tip['rect']]}")
    elif share_covered(item["rect"], tip["rect"]) > 0.05:
        result.fail(f"{item['id']}: stats tip covers {share_covered(item['rect'], tip['rect']):.0%} of the item")
    else:
        result.note(f"{item['id']}: stats tip ✓")


def stats_answer(result, endpoint, since):
    """The mod's request for the screen and the API's answer to it. An offer seen before in
    the last half hour is answered from the mod's cache, without a new request. Then the
    newest earlier one (checked against the screen by the caller)."""
    try:
        body = wait_for(lambda: last_stats_request(endpoint, since), f"the mod's {endpoint} request", timeout=10)
    except CheckError:
        body = last_stats_request(endpoint)
        if body is None:
            raise
        result.note("no new request (answered from the mod's cache), using the last one")
    request = json.loads(body)
    answer = answer_groups(ask_api(endpoint, body))
    result.note(f"request: {body[:160]}")
    return request, answer


def scenario_stats_card_reward(result, tiers):
    """Stats on: card reward badges and Skip show the API's pick and win rates, with tips."""
    ensure_stats(True)
    since = log_length()
    open_card_reward()
    request, groups = stats_answer(result, "card-reward-stats", since)
    view = inspect()
    result.screenshots.append(screenshot("stats_card_reward"))
    cards = [c for c in view["cards"] if c["on_screen"] and in_context(c, "NCardRewardSelectionScreen")]
    if {c["id"] for c in cards} != {c["id"] for c in request["offered_cards"]}:
        result.fail(f"request offers {[c['id'] for c in request['offered_cards']]}, the screen {[c['id'] for c in cards]}")
    check_stats_badges(result, view, "card-reward-stats", groups, request["act"], "NCardRewardSelectionScreen",
                       [(c["id"], "cards", "card-reward-stats") for c in cards] + [("SKIP", "skip", "skip")])
    for n, card in enumerate(cards):
        entry = groups.get("cards", {}).get(card["id"])
        if entry:
            check_stats_tip(result, card, entry, "card-reward-stats", request["act"], f"stats_card_reward_tip{n}")
            shot = screenshot(f"stats_card_reward_hover{n}")
            check_tooltip_layering(result, inspect(), tiers, shot)
    skip = next((b for b in view["badges"] if b["item_id"] == "SKIP" and b["visible"]), None)
    if skip and groups.get("skip"):
        # The badge sits on the button's top edge, so hover the button below it.
        x, y, w, h = skip["rect"]
        check_stats_tip(result, {"id": "SKIP", "rect": [x, y + h, w, h * 1.5]}, groups["skip"][""], "skip",
                        request["act"], "stats_skip_tip")
    hover(10, 10)
    act("skip_card_reward")


def scenario_stats_shop(result, tiers):
    """Stats on: shop cards, relics, potions and card removal show buy and win rates."""
    ensure_stats(True)
    since = log_length()
    view, shot = open_shop("stats_shop")
    result.screenshots.append(shot)
    exhausted_relics(result, view, "NMerchantInventory")
    request, groups = stats_answer(result, "shop-stats", since)
    view = inspect()
    shown = lambda kind: [i for i in view[kind] if in_context(i, "NMerchantInventory") and i["on_screen"]]
    items = ([(c["id"], "cards", "shop-stats") for c in shown("cards")]
             + [(r["id"], "relics", "shop-stats") for r in shown("relics")]
             + [(p["id"], "potions", "shop-stats") for p in shown("potions")])
    if request.get("remove_price"):
        items.append(("CARD_REMOVAL", "remove", "shop-stats"))
    check_stats_badges(result, view, "shop-stats", groups, request["act"], "NMerchantInventory", items)
    check_prices_clear(result)
    for group, shot_name in (("cards", "stats_shop_tip"), ("relics", "stats_shop_relic_tip"), ("potions", "stats_shop_potion_tip")):
        first = shown(group)[:1]
        if first and groups.get(group, {}).get(first[0]["id"]):
            check_stats_tip(result, first[0], groups[group][first[0]["id"]], "shop-stats", request["act"], shot_name)
    # Stats the server widened to all characters, if any: the tip says so unless only this
    # character gets the item.
    widened = [(i, groups[g][i["id"]]) for g in ("cards", "relics", "potions") for i in shown(g)
               if has_data(groups.get(g, {}).get(i["id"]) or {})
               and any(w.get("warning_enum") == WIDENED_SCOPE for w in groups[g][i["id"]].get("warnings") or [])]
    if widened:
        check_stats_tip(result, widened[0][0], widened[0][1], "shop-stats", request["act"], "stats_shop_widened_tip")
    else:
        result.note("no item with stats widened to all characters in this shop")
    hover(10, 10)

    # Sample sizes, off by default, add a line to the tips.
    samples = read_settings()["stats_samples"]
    set_setting("stats_samples", not samples)
    try:
        # A new shop, with its own offer.
        since = log_length()
        view, _ = open_shop("stats_shop_samples")
        request, groups = stats_answer(result, "shop-stats", since)
        cards = [c for c in view["cards"] if in_context(c, "NMerchantInventory") and c["on_screen"]
                 and has_data(groups.get("cards", {}).get(c["id"]) or {})]
        if cards:
            check_stats_tip(result, cards[0], groups["cards"][cards[0]["id"]], "shop-stats", request["act"], "stats_shop_samples_tip")
        else:
            result.note("sample sizes: no card with stats in the second shop to check")
        hover(10, 10)
    finally:
        set_setting("stats_samples", samples)


def scenario_stats_ancient(result, tiers):
    """Stats on: ancient options show pick and win rates."""
    ensure_stats(True)
    since = log_length()
    open_ancient()
    request, groups = stats_answer(result, "ancient-choice-stats", since)
    time.sleep(2)
    hover(10, 10)
    time.sleep(0.5)
    view = inspect()
    shot = screenshot("stats_ancient")
    result.screenshots.append(shot)
    options = [r for r in view["relics"] if r["type"] == "NEventOptionButton" and fully_inside(r["rect"], view["window"])]
    if sorted(o["id"] for o in options) != sorted(request["ancient_choices"]):
        result.fail(f"request offers {request['ancient_choices']}, the screen {[o['id'] for o in options]}")
    check_stats_badges(result, view, "ancient-choice-stats", groups, request["act"], "NEventRoom",
                       [(o["id"], "relics", "ancient-choice-stats") for o in options])
    check_badges_on_box_top(result, view, shot)
    for i, option in enumerate(options):
        entry = groups.get("relics", {}).get(option["id"])
        if entry:
            check_stats_tip(result, option, entry, "ancient-choice-stats", request["act"], f"stats_ancient_tip{i}")
    hover(10, 10)


def scenario_stats_upgrade(result, tiers):
    """Stats on: the rest site's upgrade grid shows upgrade and win rates."""
    ensure_stats(True)
    since = log_length()
    console("room RestSite")
    s = wait_state("rest_site")
    option = next(o for o in s["rest_site"]["options"] if o["id"] == "SMITH")
    act("choose_rest_option", index=option["index"])
    wait_state("card_select")
    request, groups = stats_answer(result, "card-upgrade-stats", since)
    time.sleep(1.5)
    view = inspect()
    result.screenshots.append(screenshot("stats_upgrade"))
    cards = [c for c in view["cards"] if in_context(c, "NDeckUpgradeSelectScreen") and fully_inside(c["rect"], view["window"])]
    check_stats_badges(result, view, "card-upgrade-stats", groups, request["act"], "NDeckUpgradeSelectScreen",
                       sorted({(c["id"], "cards", "card-upgrade-stats") for c in cards}))
    if cards and groups.get("cards", {}).get(cards[0]["id"]):
        check_stats_tip(result, cards[0], groups["cards"][cards[0]["id"]], "card-upgrade-stats", request["act"], "stats_upgrade_tip")
    hover(10, 10)
    act("cancel_selection")


def scenario_stats_deck_view(result, tiers):
    """Stats on: screens without stats (deck view) keep their tier badges."""
    ensure_stats(True)
    scenario_deck_view(result, tiers)
    view = inspect()
    stats = [b for b in view["badges"] if b["visible"] and b["kind"] == "stats"]
    if stats:
        result.fail(f"stats badges on a screen without stats: {[b['item_id'] for b in stats]}")


def scenario_logged_out(result, tiers):
    """Global stats chosen, then Log out in the settings: the login file goes, the choice stays
    saved but shows as the tier list and can't be picked, badges show tiers, and a game start
    without a login shows a hint. Restarts the game twice. The login comes back from a copy."""
    if not ACCOUNT.exists():
        raise CheckError(f"needs a login in {ACCOUNT}: log in through the mod's settings first")
    ensure_stats(True)
    backup = ACCOUNT.with_name("account.cfg.check-backup")
    shutil.copy(ACCOUNT, backup)
    try:
        screen = open_settings()
        click(find_node(setting_row(screen, "account"), "FeedbackButton")["rect"])
        time.sleep(1.0)
        screen = tree("NSettingsScreen", 12)
        if ACCOUNT.exists():
            result.fail("Log out left account.cfg behind")
        status, action = account_row(screen)
        if status != "Log in for global pick and win rates" or action != "Log in":
            result.fail(f"logged out, the account row reads {status!r} with button {action!r}")
        for key in BADGE_KEYS:
            if shown_value(setting_row(screen, key)) != "Tier list" or read_settings()[key] != "global":
                result.fail(f"logged out, {key} shows {shown_value(setting_row(screen, key))!r} "
                            f"(saved {read_settings()[key]!r}), expected 'Tier list', 'global' kept")
        if not row_enabled(setting_row(screen, "stats_samples")):
            result.fail("sample sizes are greyed out without a login (they apply to your own stats too)")
        tip = setting_tip("cards") or ""
        if "Global stats" in tip or "Personal stats:" not in tip:
            result.fail(f"logged out, the cards row's tip should explain all but global stats: {tip!r}")
        x, y, w, h = setting_row(screen, "account")["rect"]
        hover(x + w * 0.3, y + h / 2)
        time.sleep(0.8)
        result.screenshots.append(screenshot("logged_out_settings"))
        # Paging through a badge choice never offers global stats.
        seen = set()
        for _ in range(4):
            row = setting_row(tree("NSettingsScreen", 12), "cards")
            seen.add(shown_value(row))
            click(find_node(row, "RightArrow")["rect"])
            time.sleep(0.6)
        if "Global stats" in seen:
            result.fail(f"global stats offered without a login: {sorted(seen)}")
        close_settings()
        set_setting("cards", "tiers")
        view, shot = open_shop("logged_out_shop")
        result.screenshots.append(shot)
        if any(b["kind"] == "stats" and b["visible"] for b in view["badges"]):
            result.fail("stats badges without a login")
        check_badges(result, view, tiers, "cards", "NMerchantInventory")
        check_badges(result, view, tiers, "relics", "NMerchantInventory", types=ICON_TYPES)

        restart_game()
        hints = wait_for(lambda: inspect()["hints"], "the game start hint", timeout=30)
        result.note(f"hint: {hints[0]}")
        if "not logged in" not in hints[0]:
            result.fail(f"hint does not say the login is missing: {hints[0]!r}")
    finally:
        shutil.move(backup, ACCOUNT)
        restart_game()
    ensure_test_run()
    leave_screens()
    wait_for(lambda: "Account: Connected" in GAME_LOG.read_text(errors="replace"), "the saved login to check out", timeout=30)
    status, action = account_row(open_settings())
    close_settings()
    if "@" not in status or action != "Log out":
        result.fail(f"with the login back, the account row reads {status!r} with button {action!r}")
    ensure_stats(True)


SCENARIOS = {
    "stats_card_reward": scenario_stats_card_reward,
    "stats_shop": scenario_stats_shop,
    "stats_ancient": scenario_stats_ancient,
    "stats_upgrade": scenario_stats_upgrade,
    "stats_deck_view": scenario_stats_deck_view,
    "logged_out": scenario_logged_out,
}
