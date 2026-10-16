#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["pillow"]
# ///
"""End-to-end checks for the tier list overlay against the running game.

Each scenario sets up a screen through the dev console (DevTools mod), inspects what is on
screen and asserts that every rated item shows the right tier badge and that no badge is
drawn on top of a hover tip. Screenshots go to .artifacts/ for a human look.

Needs the game running with the STS2_MCP, DevTools and TierListOverlay mods, in modded
profile 2 (the test profile). Run: tools/check.py [scenario ...]   (--list for names)
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image

MCP = "http://localhost:15526"
DEVTOOLS = "http://localhost:15527"
TEST_PROFILE = 2
REPO = Path(__file__).resolve().parent.parent
ARTIFACTS = REPO / ".artifacts"
GAME_APP = Path.home() / "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app"
MOD_DIR = GAME_APP / "Contents/MacOS/mods/TierListOverlay"
GAME_LOG = Path.home() / "Library/Application Support/SlayTheSpire2/logs/godot.log"


class CheckError(Exception):
    pass


# --- HTTP helpers -------------------------------------------------------------------------

def _request(url, body=None):
    data = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        raise CheckError(f"{url} -> HTTP {e.code}: {e.read().decode(errors='replace')[:500]}") from None
    except urllib.error.URLError as e:
        raise CheckError(f"{url} -> {e.reason}: is the game running with the mods? (tools/reload.sh)") from None


def state():
    return _request(f"{MCP}/api/v1/singleplayer")


def act(action, **params):
    result = _request(f"{MCP}/api/v1/singleplayer", {"action": action, **params})
    if result.get("status") == "error":
        raise CheckError(f"{action} failed: {result.get('error')}")
    return result


def console(command, timeout=30.0):
    # While a run is still loading, room commands throw a NullReferenceException in
    # RunManager.ClearScreens before doing anything, so retrying is safe.
    deadline = time.monotonic() + timeout
    while True:
        try:
            result = _request(f"{DEVTOOLS}/console", command.encode())
            break
        except CheckError as e:
            if "Object reference not set" not in str(e) or time.monotonic() > deadline:
                raise
            time.sleep(2)
    if not result["success"]:
        raise CheckError(f"console '{command}' failed: {result['message']}")
    return result


def call(node_type, method):
    return _request(f"{DEVTOOLS}/call?type={node_type}&method={method}", b"")


def inspect():
    return _request(f"{DEVTOOLS}/inspect")


def tree(node_type, depth=8):
    return _request(f"{DEVTOOLS}/tree?type={node_type}&depth={depth}")


def find_node(node, name):
    if node.get("name") == name:
        return node
    for child in node.get("children", []):
        if found := find_node(child, name):
            return found
    return None


def click(rect):
    x, y = centre(rect)
    _request(f"{DEVTOOLS}/mouse/click?x={x:.0f}&y={y:.0f}", b"")


def hover(x, y):
    _request(f"{DEVTOOLS}/mouse/move?x={x:.0f}&y={y:.0f}", b"")


def screenshot(name):
    ARTIFACTS.mkdir(exist_ok=True)
    path = ARTIFACTS / f"{name}.png"
    _request(f"{DEVTOOLS}/screenshot?path={path}")
    return path


def wait_for(predicate, what, timeout=20.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.5)
    raise CheckError(f"timed out waiting for {what}")


def wait_state(*types, timeout=20.0):
    seen = []

    def check():
        s = state()
        seen.append(s["state_type"])
        return s["state_type"] in types and s

    try:
        return wait_for(check, f"state {'/'.join(types)}", timeout)
    except CheckError as e:
        raise CheckError(f"{e} (last state: {seen[-1] if seen else '?'})") from None


# --- Game setup ---------------------------------------------------------------------------

def ensure_test_run():
    """Gets into a run in the test profile, never touching other profiles' runs."""
    profiles = _request(f"{MCP}/api/v1/profiles")
    s = state()
    if profiles["current_profile_id"] != TEST_PROFILE:
        if s["state_type"] != "menu":
            raise CheckError(f"a run is active in profile {profiles['current_profile_id']}; refusing to touch it")
        _request(f"{MCP}/api/v1/profiles", {"action": "switch", "profile_id": TEST_PROFILE})
    deadline = time.monotonic() + 60
    while (s := state())["state_type"] == "menu":
        if time.monotonic() > deadline:
            raise CheckError("could not get from the menu into a run")
        options = s.get("options", [])
        screen = s.get("menu_screen")
        try:
            if screen == "main" and "continue" in options:
                act("menu_select", option="continue")
            elif screen == "main" and "singleplayer" in options:
                act("menu_select", option="singleplayer")
            elif screen == "singleplayer":  # game mode, once the Timeline unlocks more than Standard
                act("menu_select", option="standard")
            elif screen == "character_select":
                act("menu_select", option="IRONCLAD")
                time.sleep(1.5)  # Embark becomes available once the selection has landed
                act("menu_select", option="embark")
            elif screen == "tutorial_prompt":
                act("menu_select", option="no")
        except CheckError as e:
            # A screen change between reading the state and acting on it; look again.
            if "Not on a menu screen" not in str(e) and "not available" not in str(e):
                raise
        time.sleep(1.5)
    wait_run_started()


def wait_run_started(timeout=60.0):
    """Waits until a run (started or continued) has finished loading. Jumping rooms with the
    dev console while the run is still being set up (a new run starts at Neow's event) throws
    in the game's event code and then crashes the run start."""
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        s = state()
        settled = s["state_type"] not in ("menu", "unknown") and _has_node("NTopBarPauseButton")
        if settled and s["state_type"] == last:
            return
        last = s["state_type"] if settled else None
        time.sleep(1.0)
    raise CheckError(f"the run did not finish loading (state {state()['state_type']})")


def _has_node(node_type):
    try:
        tree(node_type, 0)
        return True
    except CheckError as e:
        if f"no {node_type}" in str(e):
            return False
        raise


def new_test_run():
    """Abandons the test profile's run so the next scenario starts a fresh one (a long test
    run runs out of relics: shops and loot then only offer Circlets)."""
    profiles = _request(f"{MCP}/api/v1/profiles")
    if profiles["current_profile_id"] != TEST_PROFILE:
        raise CheckError(f"profile {profiles['current_profile_id']} is active; refusing to abandon its run")
    if state()["state_type"] != "menu":
        call("NOverlayStack", "Clear")
        wait_for(lambda: _try_call("NTopBarPauseButton", "OnRelease"), "the pause button")
        time.sleep(1.5)
        click(find_node(tree("NPauseMenu", 3), "SaveAndQuit")["rect"])
        wait_state("menu", timeout=30)
        time.sleep(2)
    # Right after a game start the main menu exists before its buttons do.
    s = wait_for(lambda: (s := state()).get("menu_screen") == "main" and s.get("options") and s, "the main menu")
    if "abandon_run" in s.get("options", []):
        act("menu_select", option="abandon_run")
        time.sleep(1.5)
        act("menu_select", option="yes")
        time.sleep(3)
    if any(b.get("reason") == "manual_epoch_reveal_required" for b in state().get("blocked_options") or []):
        reveal_epochs()


# EpochSlotState.Obtained: earned, waiting to be revealed by a click.
EPOCH_OBTAINED = "2"


def reveal_epochs():
    """Reveals the Timeline epochs a finished or abandoned run earned; until then the main
    menu offers no Singleplayer. STS2MCP refuses this (it logs unlock-state errors when
    driven through automation), but real mouse clicks work like a player's."""
    print("     ↻ revealing new Timeline epochs")
    if state().get("menu_screen") != "timeline":
        click(find_node(tree("NMainMenu", 4), "TimelineButton")["rect"])
        time.sleep(4)
    window = inspect()["window"]
    for _ in range(30):
        nodes = list(walk(tree("NGame", 30)))
        visible = [n for n in nodes if n.get("visible") and n.get("rect") and n["rect"][2] > 0]
        # Reveal and unlock screens end in a Continue/Confirm button.
        buttons = [n for n in visible if n["type"] in ("NCloseButton", "NConfirmButton", "NProceedButton")]
        slots = [n for n in visible if n["type"] == "NEpochSlot" and n.get("state") == EPOCH_OBTAINED
                 and fully_inside(n["rect"], window)]
        if buttons:
            click(buttons[0]["rect"])
        elif slots:
            click(slots[0]["rect"])
        else:
            break
        time.sleep(4)
    # The back button's node sits off screen (it only slides in visually); STS2MCP can leave.
    act("menu_select", option="back")
    time.sleep(2)
    blocked = [b for b in state().get("blocked_options") or [] if b.get("reason") == "manual_epoch_reveal_required"]
    if blocked:
        raise CheckError(f"Timeline epochs still unrevealed: {', '.join(blocked[0].get('pending_epoch_ids', []))}")


def _try_call(node_type, method):
    try:
        return call(node_type, method)
    except CheckError as e:
        if f"no {node_type}" not in str(e):
            raise
        return None


def enter_combat():
    console("room Monster")
    wait_state("monster", "elite", "boss")


# --- Expectations -------------------------------------------------------------------------

@dataclass
class Entry:
    tier: str
    color: str
    commentary: str
    good_upgrade: bool

    def __getitem__(self, i):  # (tier, color), as the checks unpack it
        return (self.tier, self.color)[i]


def read_settings():
    """The mod's settings.cfg (a Godot ConfigFile, INI-like); missing keys use the defaults."""
    values = {"use_jorbs": False, "card_choices": True, "deck_screens": True, "relics": True, "hover_tips": True}
    path = MOD_DIR / "settings.cfg"
    if path.exists():
        for line in path.read_text().splitlines():
            if "=" in line:
                key, value = (part.strip() for part in line.split("=", 1))
                values[key] = value == "true"
    return values


class TierLists:
    """The mod's cached tier lists, looked up like the mod does: the chosen author's lists,
    where the current character's list wins over the colorless list (lists rate colorless
    cards per character); relics and potions fall back to Baalorlord, who rates them."""

    AUTHORS = ("baalorlord", "jorbs")

    LISTS_PER_AUTHOR = 6  # five characters and colorless

    def __init__(self):
        self.lists = {}
        # Right after a game start the mod may still be downloading them.
        wait_for(lambda: all(len(list((MOD_DIR / "cache").glob(f"{a}-*.tierlist"))) >= self.LISTS_PER_AUTHOR for a in self.AUTHORS),
                 f"cached tier lists in {MOD_DIR / 'cache'}", timeout=60)
        time.sleep(1)  # the last file may still be being written
        for author in self.AUTHORS:
            for path in sorted((MOD_DIR / "cache").glob(f"{author}-*.tierlist")):
                entries = {}
                for tier in json.loads(path.read_text())["tier_list"]["tiers"]:
                    for group in ("cards", "relics", "potions"):
                        for item in tier.get(group) or []:
                            entries[item["item_id"]] = Entry(tier["name"], tier.get("color", "#808080"),
                                                             (item.get("commentary") or "").strip(), bool(item.get("good_upgrade")))
                self.lists[(author, path.stem.split("-", 1)[1])] = entries
        self.character = None

    @property
    def author(self):
        return "jorbs" if read_settings()["use_jorbs"] else "baalorlord"

    def _get(self, author, item_id):
        own = self.lists.get((author, (self.character or "").lower()), {})
        return own.get(item_id) or self.lists.get((author, "colorless"), {}).get(item_id)

    def get(self, item_id, default=None, card=True):
        found = self._get(self.author, item_id)
        if found is None and not card:
            found = self._get("baalorlord", item_id)
        return found or default


@dataclass
class Result:
    name: str
    failures: list = field(default_factory=list)
    notes: list = field(default_factory=list)
    screenshots: list = field(default_factory=list)

    def fail(self, msg):
        self.failures.append(msg)

    def note(self, msg):
        self.notes.append(msg)


def in_context(item, context_type):
    return context_type in item["context"]


def check_badges(result, view, tiers, kind, context_type, types=None):
    """Every fully visible item of `kind` below `context_type` (optionally only of node
    `types`) must show its tier badge."""
    tiers.character = view["character"]
    window = view["window"]
    items = [i for i in view[kind] if i["on_screen"] and in_context(i, context_type) and (types is None or i["type"] in types)]
    if not items:
        result.fail(f"no {kind} found on screen in {context_type}")
        return
    # Each badge belongs to the item whose badge spot is nearest, so badges of stacked items
    # (card bundles) are not counted for the item in front. Judge by position, not by ID: a
    # badge left over from another card must not count.
    owners = {}
    for badge in view["badges"]:
        # Badges of a screen underneath (e.g. an event behind the deck view) do not count.
        if not (badge["visible"] and badge["on_screen"] and in_context(badge, context_type)):
            continue
        nearest = min(items, key=lambda i: badge_distance(badge["rect"], i))
        if badge_belongs_to(badge["rect"], nearest["rect"]):
            owners.setdefault(id(nearest), []).append(badge)
    for item in items:
        if not fully_inside(item["rect"], window):
            continue
        expected = tiers.get(item["id"], card=kind == "cards")
        near = owners.get(id(item), [])
        if expected is None:
            result.note(f"{item['id']}: not in tier list")
            if near:
                result.fail(f"{item['id']}: unrated, but shows badge {near[0]['item_id']}/{near[0]['tier']}")
            continue
        if not near:
            result.fail(f"{item['id']}: missing badge (expected {expected[0]})")
        elif len(near) > 1:
            result.fail(f"{item['id']}: {len(near)} badges ({', '.join(b['item_id'] + str([round(v) for v in b['rect']]) for b in near)})")
        elif near[0]["item_id"] != item["id"] or near[0]["tier"] != expected[0]:
            result.fail(f"{item['id']}: badge shows {near[0]['item_id']}/{near[0]['tier']}, expected {expected[0]}")
        else:
            result.note(f"{item['id']}: {expected[0]} ✓")


def tier_label(tier):
    return f"{tier}-Tier" if len(tier) <= 2 else tier


def check_tip(result, tiers, item, shot_name=None):
    """Hovering a rated item shows the author's tip with its tier and commentary."""
    expected = tiers.get(item["id"], card=item["type"] == "NCard")
    if expected is None:
        return
    x, y = centre(item["rect"])
    hover(x, y)
    # The tip normally shows within a frame or two; allow for screens still animating in.
    # A synthetic mouse move now and then does not register as entering the item (no game
    # tips either); then leave and enter again once.
    start = time.monotonic()
    nudged = False
    while True:
        time.sleep(0.25)
        view = inspect()
        tips = [t for t in view["tier_tips"] if t["visible"] and t["item_id"] == item["id"]]
        if tips or time.monotonic() - start > 3.0:
            break
        if not view["hover_tips"] and not nudged and time.monotonic() - start > 1.0:
            hover(10, 10)
            time.sleep(0.25)
            hover(x, y)
            nudged = True
    if shot_name:
        result.screenshots.append(screenshot(shot_name))
    if len(tips) != 1:
        result.fail(f"{item['id']}: {len(tips)} tier tips while hovered (expected 1; "
                    f"{len(view['hover_tips'])} game tips visible, so the hover "
                    f"{'landed' if view['hover_tips'] else 'missed'})")
        return
    text = tips[0]["text"] or ""
    if tier_label(expected.tier) not in text:
        result.fail(f"{item['id']}: tip lacks tier {tier_label(expected.tier)!r}: {text[:80]!r}")
    elif " ".join(expected.commentary.split())[:40] not in " ".join(text.split()):
        result.fail(f"{item['id']}: tip lacks commentary {expected.commentary[:40]!r}: {text[:80]!r}")
    elif not any(overlaps(tips[0]["rect"], t["rect"]) for t in view["hover_tips"]):
        result.fail(f"{item['id']}: tip is not part of the game's hover tips")
    elif "  " in text or "\n\n" in text:
        result.fail(f"{item['id']}: tip has a double space or blank line: {text[:120]!r}")
    elif expected.good_upgrade != ("[color=#7fff00]good upgrade[/color]" in (tips[0]["bbcode"] or "").lower()):
        result.fail(f"{item['id']}: good_upgrade={expected.good_upgrade}, but tip reads {tips[0]['bbcode']!r}")
    elif not fully_inside(tips[0]["rect"], view["window"]):
        result.fail(f"{item['id']}: tip runs off screen at {[round(v) for v in tips[0]['rect']]}")
    else:
        result.note(f"{item['id']}: tip ✓")


def check_badges_on_border(result, view, shot, context_type):
    """A card badge's pointed ends (its vertical middle) must sit on the frame's light inner
    border line: the bright red rows between the description box and the frame."""
    image = Image.open(shot).convert("RGB")
    cards = [c for c in view["cards"] if in_context(c, context_type) and fully_inside(c["rect"], view["window"])]
    checked = 0
    for badge in view["badges"]:
        if not (badge["visible"] and in_context(badge, context_type)):
            continue
        bx, by = centre(badge["rect"])
        card = next((c for c in cards if c["rect"][0] <= bx <= c["rect"][0] + c["rect"][2]
                     and c["rect"][1] <= by <= c["rect"][1] + c["rect"][3]), None)
        if card is None:
            continue
        # A column beside the badge, inside the frame: look for the light line near the badge.
        x = int(badge["rect"][0] - card["rect"][2] * 0.06)
        rows = [y for y in range(int(by - badge["rect"][3]), int(by + badge["rect"][3]))
                if (p := image.getpixel((x, y)))[0] > 140 and p[0] - p[1] > 60 and p[1] > 60]
        if not rows:
            result.fail(f"{badge['item_id']}: no light border line found beside the badge")
            continue
        # The first run of light rows below the description box.
        run = [rows[0]]
        for y in rows[1:]:
            if y != run[-1] + 1:
                break
            run.append(y)
        line = (run[0] + run[-1]) / 2
        checked += 1
        if abs(by - line) > max(1.5, card["rect"][3] * 0.003):
            result.fail(f"{badge['item_id']}: badge middle at y={by:.1f}, border line at y={line:.1f}")
    result.note(f"badges on the inner border: {checked} checked")


def check_badge_rim(result, view, shot, context_type):
    """A badge's dark rim must be as thick on its slanted ends as on its top edge."""
    image = Image.open(shot).convert("RGB")
    lum = lambda px, py: sum(image.getpixel((int(px), int(py)))) / 3
    badge = next((b for b in view["badges"] if b["visible"] and in_context(b, context_type)
                  and fully_inside(b["rect"], view["window"])), None)
    if badge is None:
        return
    x, y, w, h = badge["rect"]
    face = lum(x + w / 2, y + h * 0.2)

    def rim(points):
        # The run of samples (0.25 px apart) in the rim's colour, the darkest along the way;
        # the background next to the badge can be nearly as dark, so match closely.
        values = [lum(*p) for p in points]
        darkest = min(values)
        run = best = 0
        for v in values:
            run = run + 1 if abs(v - darkest) <= 3 else 0
            best = max(best, run)
        return best / 4

    # Probe only the edge zone, not as far as the text and its dark outline.
    n = int(h * 0.3 * 4)
    top = rim([(x + w / 2, y - 1 + t / 4) for t in range(n)])
    # Through the left tip, horizontally: an even rim of thickness t is t·√2 wide there
    # (the inner tip sits t·√2 inside the outer one). Sampled at several rows around the
    # tip's height to average out the polygon's pixel steps.
    widths = [rim([(x - 1 + t / 4, y + h / 2 + dy) for t in range(n)]) for dy in (-0.5, 0, 0.5)]
    tip = sum(widths) / len(widths)
    slanted = tip / 2 ** 0.5
    if abs(top - slanted) > 0.6:
        result.fail(f"{badge['item_id']}: rim {top:.2f} px on top but {slanted:.2f} px on the slanted end")
    result.note(f"rim: {top:.2f} px top, {slanted:.2f} px slanted")


def check_badges_on_box_top(result, view, shot):
    """An ancient option badge's pointed ends (its vertical middle) must sit on the option
    box's visible top edge: where the dark box starts beside the badge."""
    image = Image.open(shot).convert("RGB")
    checked = 0
    for badge in view["badges"]:
        if not (badge["visible"] and in_context(badge, "NEventOptionButton")):
            continue
        bx, by = centre(badge["rect"])
        x = int(badge["rect"][0] - 20)
        lum = lambda y: sum(image.getpixel((x, y))) / 3
        rows = range(int(by - badge["rect"][3]), int(by + badge["rect"][3]))
        # The box top is where it gets darkest fastest, going down.
        drop, edge = max((lum(y - 3) - lum(y + 1), y) for y in rows)
        if drop < 8:
            result.fail(f"{badge['item_id']}: no box edge found beside the badge")
            continue
        checked += 1
        if abs(by - edge) > 3:
            result.fail(f"{badge['item_id']}: badge middle at y={by:.1f}, box top at y={edge}")
    result.note(f"badges on the option box tops: {checked} checked")


def check_tooltip_layering(result, view, tiers, shot):
    """A badge overlapping an open hover tip must be covered by it, i.e. not visible in the screenshot."""
    tiers.character = view["character"]
    image = Image.open(shot).convert("RGB")
    checked = 0
    for badge in view["badges"]:
        if not (badge["visible"] and badge["on_screen"]):
            continue
        colour = hex_rgb(tiers.get(badge["item_id"], ("", "#808080"))[1])
        for tip in view["hover_tips"]:
            region = intersection(badge["rect"], tip["rect"])
            if region is None:
                continue
            checked += 1
            share = colour_share(image, region, colour)
            if share > 0.3:
                result.fail(f"{badge['item_id']}: badge drawn over a hover tip ({share:.0%} badge colour in the overlap)")
    result.note(f"tooltip layering: {checked} badge/tooltip overlaps checked")


# --- Geometry and pixels ------------------------------------------------------------------

def badge_belongs_to(badge, item):
    """The badge's centre lies on the item or within a badge height of its edges."""
    bx, by = centre(badge)
    m = badge[3]
    return item[0] - m <= bx <= item[0] + item[2] + m and item[1] - m <= by <= item[1] + item[3] + m


# Where the mod puts badges, by the item's node type (see Placement in Badges.cs).
BUTTON_TYPES = {"NRewardButton", "NEventOptionButton"}
ICON_TYPES = {"NRelic", "NPotion"}


def badge_distance(badge, item):
    """Distance from the badge's centre to its spot on the item: inside the bottom edge of a
    card, across the bottom edge of an icon, across the top edge of an ancient option, inside
    the right edge of a loot button."""
    bx, by = centre(badge)
    x, y, w, h = item["rect"]
    if item["type"] == "NEventOptionButton":
        spot = (x + w / 2, y + h * 0.085)  # the visible box top
    elif item["type"] in BUTTON_TYPES:
        spot = (x + w - badge[2] / 2, y + h / 2)
    elif item["type"] in ICON_TYPES:
        spot = (x + w / 2, y + h)
    else:
        spot = (x + w / 2, y + h - badge[3])
    return abs(bx - spot[0]) + abs(by - spot[1])


def fully_inside(rect, window):
    return rect[0] >= 0 and rect[1] >= 0 and rect[0] + rect[2] <= window[0] and rect[1] + rect[3] <= window[1]


def overlaps(a, b, margin=0):
    return a[0] < b[0] + b[2] + margin and b[0] - margin < a[0] + a[2] and a[1] < b[1] + b[3] + margin and b[1] - margin < a[1] + a[3]


def intersection(a, b):
    x1, y1 = max(a[0], b[0]), max(a[1], b[1])
    x2, y2 = min(a[0] + a[2], b[0] + b[2]), min(a[1] + a[3], b[1] + b[3])
    return (x1, y1, x2, y2) if x2 - x1 > 4 and y2 - y1 > 4 else None


def hex_rgb(value):
    value = value.lstrip("#")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def colour_share(image, box, colour, tolerance=40):
    crop = image.crop(tuple(int(v) for v in box))
    pixels = list(crop.get_flattened_data())
    close = sum(1 for p in pixels if all(abs(p[i] - colour[i]) <= tolerance for i in range(3)))
    return close / max(1, len(pixels))


def centre(rect):
    return rect[0] + rect[2] / 2, rect[1] + rect[3] / 2


# --- Scenarios ----------------------------------------------------------------------------

def scenario_card_reward(result, tiers):
    """Card reward after a won fight, plus tooltip layering while hovering each card."""
    ensure_test_run()
    enter_combat()
    console("win")
    rewards = wait_state("rewards")
    card_reward = next(i for i in rewards["rewards"]["items"] if i["type"] == "card")
    act("claim_reward", index=card_reward["index"])
    wait_state("card_reward")
    time.sleep(1.5)
    view = inspect()
    result.screenshots.append(screenshot("card_reward"))
    check_badges(result, view, tiers, "cards", "NCardRewardSelectionScreen")
    cards = [c for c in view["cards"] if c["on_screen"] and in_context(c, "NCardRewardSelectionScreen")]
    for n, card in enumerate(cards):
        check_tip(result, tiers, card)
        time.sleep(1.0)  # tips fade in; judge layering once they are opaque
        shot = screenshot(f"card_reward_hover{n}")
        result.screenshots.append(shot)
        check_tooltip_layering(result, inspect(), tiers, shot)
    hover(10, 10)
    act("skip_card_reward")

    # Reopening reuses the pooled card holders, which once left stale badges behind.
    wait_state("rewards")
    act("claim_reward", index=card_reward["index"])
    wait_state("card_reward")
    time.sleep(1.5)
    result.note("reopened card reward:")
    check_badges(result, inspect(), tiers, "cards", "NCardRewardSelectionScreen")
    result.screenshots.append(screenshot("card_reward_reopened"))
    act("skip_card_reward")


def scenario_choose_a_card(result, tiers):
    """'Choose a card' screen, opened by a Colorless Potion in combat (colorless list)."""
    ensure_test_run()
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


def open_shop(name):
    ensure_test_run()
    leave_screens()
    console("room Shop")
    wait_state("shop")
    call("NMerchantRoom", "OpenInventory")
    time.sleep(1.5)
    view = inspect()
    return view, screenshot(name)


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


def walk(node):
    yield node
    for child in node.get("children", []):
        yield from walk(child)


def check_prices_clear(result):
    """Shop relic and potion badges must not cover the price below them."""
    checked = 0
    for slot in walk(tree("NMerchantInventory", 9)):
        if slot["type"] not in ("NMerchantRelic", "NMerchantPotion") or not slot.get("visible"):
            continue
        badge = find_node(slot, "TierListOverlayBadge")
        cost = find_node(slot, "Cost")
        if not (badge and cost and badge.get("visible")):
            continue
        checked += 1
        if intersection(badge["rect"], cost["rect"]):
            result.fail(f"{slot['name']}: badge covers the price ({[round(v) for v in badge['rect']]} vs {[round(v) for v in cost['rect']]})")
    result.note(f"prices clear of badges: {checked} slots checked")


class RunExhausted(CheckError):
    """The test run has used up its relics; main() starts a fresh run and retries."""


def exhausted_relics(result, view, context_type):
    relics = [r["id"] for r in view["relics"] if in_context(r, context_type)]
    if relics and all(r == "CIRCLET" for r in relics):
        raise RunExhausted("only Circlets on offer: this test run has used up its relics")


def scenario_treasure(result, tiers):
    """Treasure chest relic."""
    ensure_test_run()
    leave_screens()
    console("room Treasure")
    wait_state("treasure")
    time.sleep(3)  # the chest opens with an animation
    view = inspect()
    result.screenshots.append(screenshot("treasure"))
    check_badges(result, view, tiers, "relics", "NTreasureRoomRelicCollection")
    act("claim_treasure_relic", index=0)


def scenario_elite_loot(result, tiers):
    """Elite loot: relic and potion reward buttons."""
    ensure_test_run()
    leave_screens()
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
    ensure_test_run()
    leave_screens()
    console("ancient DARV")
    # A resumed run can show its saved loot screen or the map on top of the event.
    for _ in range(3):
        s = wait_state("event", "map", "rewards", "card_reward")
        if s["state_type"] == "event":
            break
        if s["state_type"] == "map":
            call("NTopBarMapButton", "OnRelease")
        else:
            call("NOverlayStack", "Clear")
        time.sleep(1.5)
    wait_state("event")
    time.sleep(2)
    hover(10, 10)
    time.sleep(0.5)
    view = inspect()
    shot = screenshot("ancient")
    result.screenshots.append(shot)
    check_badges(result, view, tiers, "relics", "NEventRoom", types=BUTTON_TYPES)
    check_badges_on_box_top(result, view, shot)
    options = [r for r in view["relics"] if r["type"] == "NEventOptionButton" and fully_inside(r["rect"], view["window"])]
    if options:
        check_tip(result, tiers, options[0], "ancient_tip")
    hover(10, 10)


def leave_screens():
    """Back to a neutral state: no overlay screen left open from an earlier scenario."""
    s = state()
    if s["state_type"] == "card_select" and s["card_select"].get("can_cancel"):
        act("cancel_selection")
    elif s["state_type"] in ("rewards", "card_reward"):
        # Console room jumps leave reward overlays open on top of the next room.
        call("NOverlayStack", "Clear")
        time.sleep(1)


def grid_from_relic(result, tiers, relic, context, picks, name):
    """Picks up a relic whose pickup opens a deck grid, checks the grid, then finishes the
    selection with the first `picks` cards and removes the relic again."""
    ensure_test_run()
    leave_screens()
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
    ensure_test_run()
    console("card BATTLE_TRANCE Deck")
    grid_from_relic(result, tiers, "PRECISE_SCISSORS", "NDeckCardSelectScreen", 1, "remove")


def scenario_simple_grid(result, tiers):
    """Card choice grid offered as a reward (Sea Glass: cards from another character)."""
    grid_from_relic(result, tiers, "SEA_GLASS", "NSimpleCardSelectScreen", 0, "simple_grid")


def scenario_upgrade(result, tiers):
    """Upgrade grid at a rest site (Smith)."""
    ensure_test_run()
    leave_screens()
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
    ensure_test_run()
    leave_screens()
    # The run's top bar may still be loading; the deck may still be open from a failed run.
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
    ensure_test_run()
    leave_screens()
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


SETTING_KEYS = ("use_jorbs", "card_choices", "deck_screens", "relics", "hover_tips")


def open_settings():
    """Opens the game's settings screen through the pause menu and returns its node tree."""

    # The run's top bar may still be loading.
    wait_for(lambda: _try_call("NTopBarPauseButton", "OnRelease"), "the pause button")
    time.sleep(1.5)
    click(find_node(tree("NPauseMenu", 3), "Settings")["rect"])
    time.sleep(1.5)
    return tree("NSettingsScreen", 9)


def close_settings():
    click(find_node(tree("NSettingsScreen", 2), "BackButton")["rect"])
    time.sleep(1.0)
    click(find_node(tree("NPauseMenu", 3), "Resume")["rect"])
    time.sleep(1.0)


def setting_row(screen, key):
    return find_node(screen, f"TierListOverlay_{key}")


def is_ticked(row):
    return find_node(row, "Ticked").get("visible", False)


def toggle_setting(key):
    screen = open_settings()
    click(find_node(setting_row(screen, key), "TickboxVisuals")["rect"])
    time.sleep(0.5)
    close_settings()


def scenario_settings(result, tiers):
    """The mod's settings in the game's settings screen: rows, saving, and their effect."""
    ensure_test_run()
    leave_screens()
    before = read_settings()
    screen = open_settings()
    result.screenshots.append(screenshot("settings"))
    for key in SETTING_KEYS:
        row = setting_row(screen, key)
        if row is None:
            result.fail(f"settings row for {key} missing")
        elif not row.get("visible"):
            result.fail(f"settings row for {key} not visible")
        elif is_ticked(row) != before[key]:
            result.fail(f"settings row for {key} shows {is_ticked(row)}, settings.cfg says {before[key]}")
    timer_before = is_ticked(find_node(screen, "ShowRunTimer"))
    close_settings()
    if result.failures:
        return
    try:
        # Relic badges off: shop cards keep theirs, relics lose them.
        toggle_setting("relics")
        if read_settings()["relics"] is not False:
            result.fail("unticking relics did not save relics = false")
        view, shot = open_shop("settings_no_relics")
        result.screenshots.append(shot)
        shop_relics = [b for b in view["badges"] if b["visible"] and in_context(b, "NMerchantRelic")]
        if shop_relics:
            result.fail(f"relic badges shown with relics off: {[b['item_id'] for b in shop_relics]}")
        check_badges(result, view, tiers, "cards", "NMerchantInventory")
        toggle_setting("relics")

        # Jorbs: cards use his lists, relics stay with Baalorlord's.
        toggle_setting("use_jorbs")
        if not read_settings()["use_jorbs"]:
            result.fail("ticking use_jorbs did not save use_jorbs = true")
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
        now = read_settings()
        for key in SETTING_KEYS:
            if now[key] != before[key]:
                toggle_setting(key)
        # The mod's rows are copies of the timer row; they must not drive the timer setting.
        screen = open_settings()
        if is_ticked(find_node(screen, "ShowRunTimer")) != timer_before:
            result.fail("toggling the mod's settings changed the game's timer setting")
        close_settings()


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
    "bundle": scenario_bundle,
    "deck_view": scenario_deck_view,
    "settings": scenario_settings,
    "lists": scenario_lists,
}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("scenarios", nargs="*", help="scenarios to run (default: all)")
    parser.add_argument("--list", action="store_true", help="list scenarios")
    parser.add_argument("-v", "--verbose", action="store_true", help="show notes for passing scenarios")
    parser.add_argument("--new-run", action="store_true", help="abandon the test run first and start a fresh one")
    args = parser.parse_args()
    if args.list:
        for name, fn in SCENARIOS.items():
            print(f"{name:15} {fn.__doc__.strip()}")
        return
    names = args.scenarios or list(SCENARIOS)
    unknown = [n for n in names if n not in SCENARIOS]
    if unknown:
        sys.exit(f"unknown scenario(s): {', '.join(unknown)}")

    tiers = TierLists()
    if args.new_run:
        new_test_run()
    results = []
    for name in names:
        result = Result(name)
        try:
            try:
                SCENARIOS[name](result, tiers)
            except RunExhausted as e:
                print(f"     ↻ {e}; starting a fresh test run")
                new_test_run()
                result = Result(name)
                SCENARIOS[name](result, tiers)
        except (CheckError, OSError, StopIteration, KeyError) as e:
            result.fail(f"setup error: {type(e).__name__}: {e}")
        results.append(result)
        status = "PASS" if not result.failures else "FAIL"
        print(f"{status} {name}")
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
