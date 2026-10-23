"""The mod's settings tab: driving it like a player, and the scenario that checks it."""

import time

from PIL import Image, ImageChops, ImageStat

from .game import (BADGE_KEYS, CHOICES, MOD_DIR, SOURCES, TOGGLES, CheckError, _try_call, click, ensure_test_run, find_node, hover,
                   hover_until, inspect, open_shop, read_settings, restart_game, screenshot, tree, wait_for, walk)
from .geometry import box, centre, fully_inside, overlaps
from .verify import ICON_TYPES, check_badges, check_tip, in_context


def open_settings():
    """Opens the game's settings screen through the pause menu, on the mod's tab, and returns
    its node tree."""
    # The run's top bar may still be loading.
    wait_for(lambda: _try_call("NTopBarPauseButton", "OnRelease"), "the pause button")
    time.sleep(1.5)
    click(find_node(tree("NPauseMenu", 3), "Settings")["rect"])
    time.sleep(1.5)
    click(find_node(tree("NSettingsTabManager", 1), "TierListOverlay")["rect"])
    time.sleep(1.0)
    return tree("NSettingsScreen", 12)


def close_settings():
    click(find_node(tree("NSettingsScreen", 2), "BackButton")["rect"])
    time.sleep(1.0)
    click(find_node(tree("NPauseMenu", 3), "Resume")["rect"])
    time.sleep(1.0)


def setting_row(screen, key):
    return find_node(screen, f"TierListOverlay_{key}")


def row_label(row):
    """A row's label, as shown."""
    return " ".join((find_node(row, "Label").get("text") or "").split())


def is_ticked(row):
    return find_node(row, "Ticked").get("visible", False)


def row_enabled(row):
    """Whether a row's tickbox or button can be clicked (the mod greys out rows that don't apply)."""
    control = find_node(row, "SettingsTickbox") or find_node(row, "FeedbackButton")
    return control.get("enabled") is not False


def account_row(screen):
    """The account row's status (after "Untapped.gg") and its button's label."""
    row = setting_row(screen, "account")
    label = " ".join(find_node(row, "Label")["text"].split())
    return label.removeprefix("Untapped.gg").strip(), find_node(find_node(row, "FeedbackButton"), "Label")["text"]


def shown_value(row):
    """What a row shows: ticked or not, or the choice's label."""
    paginator = find_node(row, "Paginator")
    return find_node(paginator, "Label")["text"] if paginator else is_ticked(row)


def shown_for(key, value):
    """What a row shows for a saved value."""
    return CHOICES[key][value] if key in CHOICES else value


def set_setting(key, value):
    """Sets one of the mod's settings by clicking its row like a player."""
    open_settings()
    for _ in range(len(CHOICES.get(key, (0, 0)))):
        row = setting_row(tree("NSettingsScreen", 12), key)
        if shown_value(row) == shown_for(key, value):
            break
        click(find_node(row, "RightArrow" if key in CHOICES else "TickboxVisuals")["rect"])
        time.sleep(0.6)
    close_settings()
    if read_settings()[key] != value:
        raise CheckError(f"setting {key} to {value!r} by clicking did not save it (settings.cfg: {read_settings()[key]!r})")


def ensure_stats(on, source="global"):
    """Badges on global (or personal) stats, or on the tier list, on cards, relics and potions."""
    want = source if on else "tiers"
    if read_settings()["badges"] != want:
        set_setting("badges", want)
    for key in BADGE_KEYS:
        if read_settings()[key] is not True:
            set_setting(key, True)


def setting_tip(key):
    """The text of a settings row's tip while hovered, or None."""
    x, y, w, h = setting_row(tree("NSettingsScreen", 12), key)["rect"]
    # Its own: another row's tip can still be up.
    visible = lambda view: [t for t in view["setting_tips"] if t["visible"] and t["item_id"] == f"TierListOverlay_{key}"]
    tips = visible(hover_until((x + w * 0.3, y + h / 2), visible))
    return " ".join((tips[0]["text"] or "").split()) if tips else None


def check_settings_hover(result, screen):
    """Each row explains itself in a tip that stays on screen, and hovering a tickbox lights up
    only that one (copied rows once shared the template's material)."""
    window = inspect()["window"]
    rows = [setting_row(screen, key) for key in (*CHOICES, *TOGGLES, "account")]
    controls = [c for r in rows for c in (find_node(r, n) for n in ("TickboxVisuals", "Paginator", "FeedbackButton")) if c]
    button = find_node(setting_row(screen, "account"), "FeedbackButton")
    # Each row on its label, and the account row on its button too, which takes the mouse.
    spots = [(row, (row["rect"][0] + row["rect"][2] * 0.3, row["rect"][1] + row["rect"][3] / 2)) for row in rows]
    spots.append(({**button, "name": "the account button"}, centre(button["rect"])))
    for row, point in spots:
        tips = hover_until(point, lambda view: view["hover_tips"])["hover_tips"]
        if not tips:
            result.fail(f"{row['name']}: no tip while hovered")
        elif not all(fully_inside(t["rect"], window) for t in tips):
            result.fail(f"{row['name']}: tip runs off screen at {[[round(v) for v in t['rect']] for t in tips]}")
        elif any(overlaps(c["rect"], t["rect"]) for c in controls for t in tips):
            result.fail(f"{row['name']}: its tip covers the settings' tickboxes or buttons")
    hover(10, 10)
    time.sleep(0.6)
    before = Image.open(screenshot("settings_unhovered")).convert("RGB")
    boxes = [find_node(r, "TickboxVisuals")["rect"] for r in rows if find_node(r, "TickboxVisuals")]
    hover(*centre(boxes[0]))
    time.sleep(0.6)
    view = inspect()
    after = Image.open(screenshot("settings_hovered")).convert("RGB")
    lit = [b for b in boxes[1:] if not any(overlaps(b, t["rect"]) for t in view["hover_tips"])
           and ImageStat.Stat(ImageChops.difference(before.crop(box(b)), after.crop(box(b)))).mean[0] > 3]
    if lit:
        result.fail(f"hovering one tickbox lit up {len(lit)} others")
    result.note(f"settings hover: {len(spots)} tips, {len(boxes) - 1} other tickboxes compared")
    hover(10, 10)


def scenario_settings(result, tiers):
    """The mod's settings tab: rows, saving, greyed-out rows, and their effect."""
    before = read_settings()
    screen = open_settings()
    result.screenshots.append(screenshot("settings"))
    for key in (*TOGGLES, *CHOICES):
        row = setting_row(screen, key)
        # A greyed-out tickbox shows unticked: it does nothing then.
        want = shown_for(key, before[key]) if key in CHOICES or row_enabled(row) else False
        if row is None:
            result.fail(f"settings row for {key} missing")
        elif not row.get("visible"):
            result.fail(f"settings row for {key} not visible")
        elif shown_value(row) != want:
            result.fail(f"settings row for {key} shows {shown_value(row)!r}, expected {want!r} (settings.cfg: {before[key]!r})")
    check_settings_hover(result, screen)
    general = find_node(screen, "GeneralSettings")
    if any(n["name"].startswith("TierListOverlay_") for n in walk(general)):
        result.fail("the mod's rows are in the General tab too")
    timer_before = is_ticked(find_node(general, "ShowRunTimer"))
    close_settings()
    if result.failures:
        return
    try:
        # Logged in, the account row offers to log out and the stats rows apply.
        screen = open_settings()
        status, action = account_row(screen)
        if "@" not in status or action != "Log out":
            result.fail(f"logged in, the account row reads {status!r} with button {action!r}, expected the email and 'Log out'")
        # The badges row's tip explains its options, without a word about logging in.
        tip = setting_tip("badges") or ""
        if not all(f"{o}:" in tip for o in SOURCES.values()) or "Log in" in tip:
            result.fail(f"logged in, the badges row's tip does not explain its options as it should: {tip!r}")
        hover(10, 10)
        # Paging through the source offers all three.
        seen = []
        for _ in SOURCES:
            row = setting_row(tree("NSettingsScreen", 12), "badges")
            seen.append(shown_value(row))
            click(find_node(row, "RightArrow")["rect"])
            time.sleep(0.6)
        if sorted(seen) != sorted(SOURCES.values()):
            result.fail(f"the badges row pages through {seen}, expected {list(SOURCES.values())}")
        if row_label(setting_row(screen, "author")) != "Author":
            result.fail(f"the author row reads {row_label(setting_row(screen, 'author'))!r}, expected 'Author'")
        tips_on = read_settings()["stats_tips"]
        if row_enabled(setting_row(screen, "stats_samples")) != tips_on:
            result.fail(f"sample sizes {'greyed out' if tips_on else 'not greyed out'} with stats tips {'on' if tips_on else 'off'}")
        close_settings()

        # Personal stats on events applies only with personal stats as the badges, and shows
        # unticked (but stays saved) otherwise.
        set_setting("badges", "personal")
        set_setting("events", True)
        for source in ("tiers", "global", "personal"):
            set_setting("badges", source)
            screen = open_settings()
            row = setting_row(screen, "events")
            if row_enabled(row) != (source == "personal") or is_ticked(row) != (source == "personal"):
                result.fail(f"with {source} badges, the events row is {'enabled' if row_enabled(row) else 'greyed out'} "
                            f"and {'ticked' if is_ticked(row) else 'unticked'}")
            if source == "tiers":
                result.screenshots.append(screenshot("settings_events_greyed"))
            close_settings()
        if read_settings()["events"] is not True:
            result.fail("showing the events row unticked changed its saved setting")
        set_setting("badges", "tiers")

        # Relic badges off: shop cards keep their badges, relics lose them.
        set_setting("relics", False)
        view, shot = open_shop("settings_no_relics")
        result.screenshots.append(shot)
        shop_relics = [b for b in view["badges"] if b["visible"] and in_context(b, "NMerchantRelic")]
        if shop_relics:
            result.fail(f"relic badges shown with relics off: {[b['item_id'] for b in shop_relics]}")
        check_badges(result, view, tiers, "cards", "NMerchantInventory")
        set_setting("relics", True)

        # Jorbs: cards use his lists, relics stay with Baalorlord's.
        set_setting("author", "Jorbs")
        view, shot = open_shop("settings_jorbs")
        result.screenshots.append(shot)
        result.note("with Jorbs:")
        check_badges(result, view, tiers, "cards", "NMerchantInventory")
        check_badges(result, view, tiers, "relics", "NMerchantInventory", types=ICON_TYPES)
        cards = [c for c in view["cards"] if in_context(c, "NMerchantInventory") and tiers.get(c["id"])]
        if cards:
            check_tip(result, tiers, cards[0], "settings_jorbs_tip")
            tips = [t for t in inspect()["tier_tips"] if t["visible"]]
            if tips and tips[0]["title"] != "Jorbs":
                result.fail(f"tip title is {tips[0]['title']!r} with Jorbs chosen")
        hover(10, 10)
    finally:
        # The source first: the events row can be set only with personal badges.
        for key in ("badges", "events", *TOGGLES, *CHOICES):
            if read_settings()[key] != before[key] and (key != "events" or read_settings()["badges"] == "personal"):
                set_setting(key, before[key])
        # The mod's rows are copies of game rows. They must not drive the game's settings.
        screen = open_settings()
        if is_ticked(find_node(find_node(screen, "GeneralSettings"), "ShowRunTimer")) != timer_before:
            result.fail("the mod's settings changed the game's timer setting")
        close_settings()


# A settings.cfg from mod 1.2 to 1.5: a source per kind of badge.
VERSION_2 = """[tierlist]

settings_version=2
cards="global"
relics="none"
potions="personal"
events="none"
deck="none"
author="Jorbs"
commentary=true
stats_samples=false
stats_tips=true
event_advice=true
"""
# What the mod makes of it: the strongest source, and a tickbox per kind.
FROM_VERSION_2 = {"badges": "global", "cards": True, "relics": False, "potions": True, "events": False, "deck": False, "author": "Jorbs"}


def scenario_settings_migration(result, tiers):
    """A settings.cfg from mod 1.5 (a source per kind of badge) carries over to one source and
    a tickbox per kind. Restarts the game twice. The settings come back from a copy."""
    path = MOD_DIR / "settings.cfg"
    backup = path.read_text() if path.exists() else None
    try:
        path.write_text(VERSION_2)
        restart_game()
        now = read_settings()
        result.note(f"carried over: { {k: now[k] for k in FROM_VERSION_2} }")
        for key, want in FROM_VERSION_2.items():
            if now[key] != want:
                result.fail(f"{key} carried over as {now[key]!r}, expected {want!r}")
        if "settings_version=3" not in path.read_text():
            result.fail("settings.cfg not marked version 3")
    finally:
        if backup is None:
            path.unlink(missing_ok=True)
        else:
            path.write_text(backup)
        restart_game()
        ensure_test_run()


SCENARIOS = {
    "settings": scenario_settings,
    "settings_migration": scenario_settings_migration,
}
