"""Baalorlord's and Jorbs' advice on events, as a hover tip on the event's options.

The advice comes from Untapped.gg and shows only with a login (scenario_logged_out checks it
goes). The checks fetch the same advice and expect the tip to show the chosen author's text, or
the other author's where the chosen one wrote none."""

import json
import re
import time
import urllib.request

from .game import CheckError, hover, hover_until, inspect, open_event, pan, read_settings, screenshot, wheel
from .geometry import centre, fully_inside, overlaps
from .personal import event_options
from .settings import set_setting

API = "https://sts2.untapped.gg/api/v1/commentary/events"
# Advice from both authors, from Baalorlord only, from Jorbs only (and long enough to scroll),
# and none.
BOTH, BAALORLORD_ONLY, JORBS_LONG, NONE = "DOORS_OF_LIGHT_AND_DARK", "ZEN_WEAVER", "COLORFUL_PHILOSOPHERS", "SAPPHIRE_SEED"


def fetch_advice():
    """Event ID -> author -> text, as Untapped.gg has it now."""
    request = urllib.request.Request(API, headers={"User-Agent": "TierListOverlay check.py"})
    with urllib.request.urlopen(request, timeout=15) as response:
        advice = json.load(response)
    return {event: {e["author"]: e["text"] for e in entries} for event, entries in advice.items()}


def words(text):
    return " ".join((text or "").split())


def title_for(author):
    return f"{author}' Advice" if author.endswith("s") else f"{author}'s Advice"


def hovered_advice(option):
    """Hovers the option and returns its visible advice tips, and the view."""
    shown = lambda view: [t for t in view["advice_tips"] if t["visible"]]
    view = hover_until(centre(option["rect"]), shown)
    return shown(view), view


def check_advice(result, event, author, advice, shot_name):
    """Every option of the event on screen shows one advice tip with the author's text, inside
    the window and below the option's other tips. Returns the last option's tip."""
    open_event(event)
    options = event_options(inspect())
    if not options:
        result.fail(f"{event}: no options on screen")
        return None
    tip = None
    for i, option in enumerate(options):
        tips, view = hovered_advice(option)
        if i == 0:
            result.screenshots.append(screenshot(shot_name))
        if len(tips) != 1:
            result.fail(f"{event} option {i}: {len(tips)} advice tips, expected one")
            continue
        tip = tips[0]
        others = [t for t in view["stats_tips"] if t["visible"]]
        if tip["item_id"] != event:
            result.fail(f"{event} option {i}: the advice tip is for {tip['item_id']!r}")
        elif tip["title"] != title_for(author):
            result.fail(f"{event} option {i}: the advice tip's title is {tip['title']!r}, expected {title_for(author)!r}")
        elif words(tip["text"]) != words(advice[event][author]):
            result.fail(f"{event} option {i}: the advice tip reads {words(tip['text'])[:120]!r}, "
                        f"expected {author}'s {words(advice[event][author])[:120]!r}")
        elif not fully_inside(tip["rect"], view["window"]):
            result.fail(f"{event} option {i}: the advice tip runs off screen at {[round(v) for v in tip['rect']]}")
        elif overlaps(tip["rect"], option["rect"]):
            result.fail(f"{event} option {i}: the advice tip covers the option")
        elif any(t["rect"][1] > tip["rect"][1] for t in others):
            result.fail(f"{event} option {i}: the advice tip is above the stats tip")
        else:
            result.note(f"{event} option {i}: {tip['title']} ✓")
    hover(10, 10)
    return tip


def check_no_advice(result, event, why, shot_name=None):
    open_event(event)
    options = event_options(inspect())
    if not options:
        result.fail(f"{event}: no options on screen")
        return
    # Wait for the game's own tip set, then look for the advice in it.
    tips, _ = hovered_advice(options[0])
    if shot_name:
        result.screenshots.append(screenshot(shot_name))
    hover(10, 10)
    if tips:
        result.fail(f"{event}: advice tip {tips[0]['title']!r} shown {why}")
    else:
        result.note(f"{event}: no advice {why} ✓")


def scenario_event_advice(result, tiers):
    """Event options show the tier list author's advice on the event in a hover tip, or the
    other author's where there is none. A long text scrolls with the mouse wheel and stays on
    screen. Nothing on events without advice or with the setting off."""
    advice = fetch_advice()
    for event, authors in ((BOTH, {"Baalorlord", "Jorbs"}), (BAALORLORD_ONLY, {"Baalorlord"}), (JORBS_LONG, {"Jorbs"})):
        if set(advice.get(event, {})) != authors:
            raise CheckError(f"Untapped.gg now has advice on {event} from {sorted(advice.get(event, {}))}, "
                             f"the checks expect {sorted(authors)}: pick other events in checklib/advice.py")
    if NONE in advice:
        raise CheckError(f"Untapped.gg now has advice on {NONE}: pick another event without any in checklib/advice.py")
    before = read_settings()
    try:
        set_setting("event_advice", True)
        set_setting("author", "Baalorlord")
        check_advice(result, BOTH, "Baalorlord", advice, "advice")
        check_advice(result, BAALORLORD_ONLY, "Baalorlord", advice, "advice_own")

        # The other author's, and too long for the screen: it scrolls instead.
        tip = check_advice(result, JORBS_LONG, "Jorbs", advice, "advice_long")
        if tip is not None:
            check_scrolling(result, tip, JORBS_LONG)

        set_setting("author", "Jorbs")
        check_advice(result, BOTH, "Jorbs", advice, "advice_jorbs")
        check_advice(result, BAALORLORD_ONLY, "Baalorlord", advice, "advice_other")

        check_no_advice(result, NONE, "on an event without advice")
        set_setting("event_advice", False)
        check_no_advice(result, BOTH, "with the setting off", "advice_off")
    finally:
        for key in ("event_advice", "author"):
            if read_settings()[key] != before[key]:
                set_setting(key, before[key])


def check_scrolling(result, tip, event):
    """The long text scrolls: the mouse wheel and a trackpad swipe over the hovered option move it
    down and back up."""
    if tip["scroll"] is None or tip["scroll"][1] <= 0:
        result.fail(f"{event}: the long advice does not scroll (scroll {tip['scroll']})")
        return
    option = event_options(inspect())[-1]
    hovered_advice(option)
    x, y = centre(option["rect"])
    for _ in range(3):
        wheel(x, y)
        time.sleep(0.2)
    down = [t for t in inspect()["advice_tips"] if t["visible"]]
    result.screenshots.append(screenshot("advice_long_scrolled"))
    if not down or down[0]["scroll"][0] <= 0:
        result.fail(f"{event}: the mouse wheel did not scroll the advice ({down[0]['scroll'] if down else 'tip gone'})")
        return
    wheel(x, y, up=True)
    time.sleep(0.2)
    up = [t for t in inspect()["advice_tips"] if t["visible"]]
    if not up or up[0]["scroll"][0] >= down[0]["scroll"][0]:
        result.fail(f"{event}: the mouse wheel up did not scroll back ({up[0]['scroll'] if up else 'tip gone'})")
    else:
        result.note(f"{event}: the wheel scrolls {down[0]['scroll'][0]:.0f} of {down[0]['scroll'][1]:.0f} px down and back ✓")
    # A two-finger swipe on a trackpad comes as a pan gesture, not as wheel steps.
    before = up[0]["scroll"][0] if up else 0
    for dy in (2.0, 2.0):
        pan(x, y, dy)
        time.sleep(0.2)
    swiped = [t for t in inspect()["advice_tips"] if t["visible"]]
    if not swiped or swiped[0]["scroll"][0] <= before:
        result.fail(f"{event}: a trackpad swipe did not scroll the advice ({swiped[0]['scroll'] if swiped else 'tip gone'})")
    else:
        pan(x, y, -2.0)
        time.sleep(0.2)
        back = [t for t in inspect()["advice_tips"] if t["visible"]]
        if not back or back[0]["scroll"][0] >= swiped[0]["scroll"][0]:
            result.fail(f"{event}: a trackpad swipe up did not scroll back ({back[0]['scroll'] if back else 'tip gone'})")
        else:
            result.note(f"{event}: a trackpad swipe scrolls down and back ✓")
    hover(10, 10)


SCENARIOS = {
    "event_advice": scenario_event_advice,
}
