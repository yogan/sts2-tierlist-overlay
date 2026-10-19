#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["pillow"]
# ///
"""End-to-end checks for the tier list overlay against the running game.

Each scenario sets up a screen through the dev console (DevTools mod), inspects what is on
screen and asserts that every rated item shows the right badge (its tier, or the Untapped.gg
stats the API gives for the mod's own request), that tips and settings work, and that no
badge is drawn on top of a hover tip. Screenshots go to .artifacts/ for a human look.

Needs the game running with the STS2_MCP, DevTools and TierListOverlay mods, in modded
profile 2 (the test profile), and for the stats scenarios an Untapped.gg login (see
docs/UPDATING.md). Run: tools/check.py [scenario ...]   (--list for names)
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageChops, ImageStat

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


def window(mode):
    """"windowed": the game in a window beside other apps, to watch while tools drive it.
    "saved": back to the player's display setting. Neither changes the saved setting."""
    _request(f"{DEVTOOLS}/window/mode?mode={mode}", b"")
    time.sleep(1.5)  # the window settles


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
            raise CheckError(f"a run is active in profile {profiles['current_profile_id']}, refusing to touch it")
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
            elif screen == "profile_select":  # after a game start, sometimes
                act("menu_select", option=f"profile_{TEST_PROFILE}")
        except CheckError as e:
            # A screen change between reading the state and acting on it. Look again.
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
        raise CheckError(f"profile {profiles['current_profile_id']} is active, refusing to abandon its run")
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
    """Reveals the Timeline epochs a finished or abandoned run earned. Until then the main
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
    # The back button's node sits off screen (it only slides in visually), but STS2MCP can leave.
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


SETTING_DEFAULTS = {"cards": "tiers", "relics": "tiers", "potions": "tiers", "deck": "tiers", "author": "Baalorlord",
                    "commentary": True, "stats_samples": False}
# The mod's settings rows: tickboxes, and left/right choices with their options in order.
TOGGLES = ("commentary", "stats_samples")
# Saved value -> label shown, per choice, in the order the arrows page through them.
BADGES = {"none": "None", "tiers": "Tier list", "global": "Global stats"}
CHOICES = {"cards": BADGES, "relics": BADGES, "potions": BADGES, "deck": {"none": "None", "tiers": "Tier list"},
           "author": {"Baalorlord": "Baalorlord", "Jorbs": "Jorbs"}}
BADGE_KEYS = ("cards", "relics", "potions")


def read_settings():
    """The mod's settings.cfg (a Godot ConfigFile, INI-like). Missing keys use the defaults."""
    values = dict(SETTING_DEFAULTS)
    path = MOD_DIR / "settings.cfg"
    if path.exists():
        for line in path.read_text().splitlines():
            if "=" in line:
                key, value = (part.strip() for part in line.split("=", 1))
                values[key] = json.loads(value) if value.startswith('"') else value == "true"
    return values


class TierLists:
    """The mod's cached tier lists, looked up like the mod does: the chosen author's lists,
    where the current character's list wins over the colorless list (lists rate colorless
    cards per character). Relics and potions fall back to Baalorlord, who rates them."""

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
        return read_settings()["author"].lower()

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
    # The tip normally shows within a frame or two. Allow for screens still animating in.
    # A synthetic mouse move now and then does not register as entering the item (no game
    # tips either). If so, leave and enter again once.
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
        result.fail(f"{item['id']}: {len(tips)} tier tips while hovered (expected 1, "
                    f"{len(view['hover_tips'])} game tips visible, so the hover "
                    f"{'landed' if view['hover_tips'] else 'missed'})")
        return
    text = tips[0]["text"] or ""
    if tips[0]["badge"] != tier_label(expected.tier):
        result.fail(f"{item['id']}: tip badge reads {tips[0]['badge']!r}, expected {tier_label(expected.tier)!r}")
    elif " ".join(expected.commentary.split())[:40] not in " ".join(text.split()):
        result.fail(f"{item['id']}: tip lacks commentary {expected.commentary[:40]!r}: {text[:80]!r}")
    elif not any(overlaps(tips[0]["rect"], t["rect"]) for t in view["hover_tips"]):
        result.fail(f"{item['id']}: tip is not part of the game's hover tips")
    elif "  " in text or "\n\n" in text:
        result.fail(f"{item['id']}: tip has a double space or blank line: {text[:120]!r}")
    elif expected.good_upgrade != ("[color=#7fff00]good upgrade[/color]" in (tips[0]["bbcode"] or "").lower()):
        result.fail(f"{item['id']}: good_upgrade={expected.good_upgrade}, but tip reads {tips[0]['bbcode']!r}")
    elif share_covered(item["rect"], tips[0]["rect"]) > 0.05:
        result.fail(f"{item['id']}: tip covers {share_covered(item['rect'], tips[0]['rect']):.0%} of the item "
                    f"(tip {[round(v) for v in tips[0]['rect']]}, item {[round(v) for v in item['rect']]})")
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
    """A badge's dark rim must be as thick on its slanted ends as on its top edge. Measured on
    every badge in view and compared by the median, as the card behind a badge can blur one
    measure. Badges under 30 pixels tall (small windows) have a rim of about a pixel, too thin to
    compare, so they are skipped."""
    image = Image.open(shot).convert("RGB")
    lum = lambda px, py: sum(image.getpixel((int(px), int(py)))) / 3
    badges = [b for b in view["badges"] if b["visible"] and in_context(b, context_type)
              and fully_inside(b["rect"], view["window"])]
    if not badges:
        return
    if badges[0]["rect"][3] < 30:
        result.note(f"rim: badges {badges[0]['rect'][3]:.0f} px tall, too small to measure (tools/check.py --full-size)")
        return

    def rim(points):
        # The run of samples (0.25 px apart) in the rim's colour, the darkest along the way.
        # The background next to the badge can be nearly as dark, so match closely.
        values = [lum(*p) for p in points]
        darkest = min(values)
        run = best = 0
        for v in values:
            run = run + 1 if abs(v - darkest) <= 3 else 0
            best = max(best, run)
        return best / 4

    tops, slants = [], []
    for badge in badges:
        x, y, w, h = badge["rect"]
        # Probe only the edge zone, not as far as the text and its dark outline.
        n = int(h * 0.3 * 4)
        # The top edge just past the left corner, before the text, whose dark outline the badge's
        # middle would reach in a large window.
        tops.append(rim([(x + h * 0.8, y - 1 + t / 4) for t in range(n)]))
        # Through the left tip, horizontally: an even rim of thickness t is t·√2 wide there
        # (the inner tip sits t·√2 inside the outer one). Sampled at several rows around the
        # tip's height to average out the polygon's pixel steps.
        widths = [rim([(x - 1 + t / 4, y + h / 2 + dy) for t in range(n)]) for dy in (-0.5, 0, 0.5)]
        slants.append(sum(widths) / len(widths) / 2 ** 0.5)
    top, slanted = sorted(tops)[len(tops) // 2], sorted(slants)[len(slants) // 2]
    if abs(top - slanted) > 0.6:
        result.fail(f"rim {top:.2f} px on top but {slanted:.2f} px on the slanted end (median of {len(badges)} badges)")
    result.note(f"rim: {top:.2f} px top, {slanted:.2f} px slanted (median of {len(badges)} badges)")


def check_badges_on_box_top(result, view, shot):
    """An ancient option badge's pointed ends (its vertical middle) must sit on the option
    box's visible top edge: where the dark box starts beside the badge."""
    image = Image.open(shot).convert("RGB")
    checked = 0
    for badge in view["badges"]:
        if not (badge["visible"] and in_context(badge, "NEventOptionButton")):
            continue
        bx, by = centre(badge["rect"])
        # Close to the badge only: the ancient's speech bubble can end just above the options.
        rows = range(int(by - badge["rect"][3] * 0.6), int(by + badge["rect"][3] * 0.6))
        # The box top, where it gets darkest fastest going down, in columns on both sides of
        # the badge. The background shows through the box, so one column can mislead.
        bx0, bw = badge["rect"][0], badge["rect"][2]
        edges = []
        for x in (int(bx0 - 20), int(bx0 - 40), int(bx0 + bw + 20), int(bx0 + bw + 40)):
            lum = lambda y: sum(image.getpixel((x, y))) / 3
            drop, edge = max((lum(y - 3) - lum(y + 1), y) for y in rows)
            if drop >= 8:
                edges.append(edge)
        if not edges:
            result.fail(f"{badge['item_id']}: no box edge found beside the badge")
            continue
        edge = sorted(edges)[len(edges) // 2]
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
        # The tier tip's own badge, in the same colour, is not the item's badge showing through.
        tip_badges = [t["badge_rect"] for t in view["tier_tips"] if t["visible"] and t.get("badge_rect")]
        for tip in view["hover_tips"]:
            region = intersection(badge["rect"], tip["rect"])
            if region is None:
                continue
            checked += 1
            share = colour_share(image, region, colour, exclude=tip_badges)
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


def share_covered(rect, cover):
    """How much of rect lies under cover (0 to 1)."""
    region = intersection(rect, cover)
    return (region[2] - region[0]) * (region[3] - region[1]) / (rect[2] * rect[3]) if region and rect[2] * rect[3] else 0.0


def intersection(a, b):
    x1, y1 = max(a[0], b[0]), max(a[1], b[1])
    x2, y2 = min(a[0] + a[2], b[0] + b[2]), min(a[1] + a[3], b[1] + b[3])
    return (x1, y1, x2, y2) if x2 - x1 > 4 and y2 - y1 > 4 else None


def hex_rgb(value):
    value = value.lstrip("#")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def colour_share(image, box, colour, tolerance=40, exclude=()):
    """How much of box (x1, y1, x2, y2) is close to colour, leaving out the `exclude` rects."""
    x1, y1, x2, y2 = (int(v) for v in box)
    pixels = [image.getpixel((x, y)) for y in range(y1, y2) for x in range(x1, x2)
              if not any(r[0] <= x < r[0] + r[2] and r[1] <= y < r[1] + r[3] for r in exclude)]
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
        time.sleep(1.0)  # tips fade in, so judge layering once they are opaque
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
        if slot["type"] not in ("NMerchantRelic", "NMerchantPotion", "NMerchantCardRemoval") or not slot.get("visible"):
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
    """The test run has used up its relics, so main() starts a fresh run and retries."""


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
    # Each option: relics with keyword tips and without get their tips laid out differently.
    for i, option in enumerate(options):
        check_tip(result, tiers, option, f"ancient_tip_{i}")
        hover(10, 10)
        time.sleep(0.3)
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
    for row, (hx, hy) in spots:
        hover(hx, hy)
        time.sleep(0.6)
        tips = inspect()["hover_tips"]
        if not tips:
            # A synthetic mouse move now and then does not register as entering, so once more.
            hover(10, 10)
            time.sleep(0.3)
            hover(hx, hy)
            time.sleep(0.6)
            tips = inspect()["hover_tips"]
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


def box(rect):
    x, y, w, h = rect
    return int(x), int(y), int(x + w), int(y + h)


def scenario_settings(result, tiers):
    """The mod's settings tab: rows, saving, greyed-out rows, and their effect."""
    ensure_test_run()
    leave_screens()
    before = read_settings()
    screen = open_settings()
    result.screenshots.append(screenshot("settings"))
    for key in (*TOGGLES, *CHOICES):
        row = setting_row(screen, key)
        if row is None:
            result.fail(f"settings row for {key} missing")
        elif not row.get("visible"):
            result.fail(f"settings row for {key} not visible")
        elif shown_value(row) != shown_for(key, before[key]):
            result.fail(f"settings row for {key} shows {shown_value(row)!r}, settings.cfg says {before[key]!r}")
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
        global_on = any(read_settings()[k] == "global" for k in BADGE_KEYS)
        if row_enabled(setting_row(screen, "stats_samples")) != global_on:
            result.fail(f"sample sizes {'greyed out' if global_on else 'not greyed out'} with global stats {'on' if global_on else 'off'}")
        close_settings()

        # Relic badges off: shop cards keep their badges, relics lose them.
        set_setting("relics", "none")
        view, shot = open_shop("settings_no_relics")
        result.screenshots.append(shot)
        shop_relics = [b for b in view["badges"] if b["visible"] and in_context(b, "NMerchantRelic")]
        if shop_relics:
            result.fail(f"relic badges shown with relics off: {[b['item_id'] for b in shop_relics]}")
        check_badges(result, view, tiers, "cards", "NMerchantInventory")
        set_setting("relics", "tiers")

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
        now = read_settings()
        for key in (*TOGGLES, *CHOICES):
            if now[key] != before[key]:
                set_setting(key, before[key])
        # The mod's rows are copies of game rows. They must not drive the game's settings.
        screen = open_settings()
        if is_ticked(find_node(find_node(screen, "GeneralSettings"), "ShowRunTimer")) != timer_before:
            result.fail("the mod's settings changed the game's timer setting")
        close_settings()


# --- Stats -----------------------------------------------------------------------------------
# With stats on, badges on card rewards, ancient options, the shop and the upgrade screen show
# Untapped.gg's global stats instead of tiers. The checks ask the API the same question the mod
# asked (its request body is in the game log) and expect each badge and tip to show the answer.

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


def ensure_stats(on):
    """Card, relic and potion badges on global stats, or on the tier list."""
    for key in BADGE_KEYS:
        if read_settings()[key] != ("global" if on else "tiers"):
            set_setting(key, "global" if on else "tiers")


def last_stats_request(endpoint, since=0):
    """The body of the newest stats request the mod logged for `endpoint` after log line `since`."""
    marker = f"[TierListOverlay] Stats /api/v1/global-stats/{endpoint}/ for "
    lines = GAME_LOG.read_text(errors="replace").splitlines()[since:]
    found = [line for line in lines if line.startswith(marker)]
    if not found:
        return None
    return found[-1][len(marker):].rsplit("}: ", 1)[0] + "}"


def log_length():
    return len(GAME_LOG.read_text(errors="replace").splitlines())


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
    # Rounded half away from zero, like the mod (C# MidpointRounding.AwayFromZero).
    rounded = int(abs(value) + 0.5) * (1 if value >= 0 else -1)
    return ("+" if signed and rounded >= 0 else "") + f"{rounded}%"


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
    x, y = centre(item["rect"])
    hover(10, 10)
    time.sleep(0.3)
    hover(x, y)
    tips = []
    for _ in range(12):
        time.sleep(0.25)
        view = inspect()
        tips = [t for t in view["stats_tips"] if t["visible"] and t["item_id"] == item["id"]]
        if tips or (not has_data(entry) and view["hover_tips"]):
            break
    result.screenshots.append(screenshot(shot_name))
    expected = 1 if has_data(entry) else 0
    if len(tips) != expected:
        result.fail(f"{item['id']}: {len(tips)} stats tips while hovered (expected {expected})")
    if len(tips) != 1:
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
    elif (act < 3) != ("Act Winrate" in text):
        result.fail(f"{item['id']}: stats tip {'lacks' if act < 3 else 'has'} the act winrate in act {act}: {text[:120]!r}")
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
    ensure_test_run()
    leave_screens()
    ensure_stats(True)
    since = log_length()
    enter_combat()
    console("win")
    rewards = wait_state("rewards")
    card_reward = next(i for i in rewards["rewards"]["items"] if i["type"] == "card")
    act("claim_reward", index=card_reward["index"])
    wait_state("card_reward")
    request, groups = stats_answer(result, "card-reward-stats", since)
    time.sleep(1.5)
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
    cards = shown("cards")
    if cards and groups.get("cards", {}).get(cards[0]["id"]):
        check_stats_tip(result, cards[0], groups["cards"][cards[0]["id"]], "shop-stats", request["act"], "stats_shop_tip")
    relics = shown("relics")
    if relics and groups.get("relics", {}).get(relics[0]["id"]):
        check_stats_tip(result, relics[0], groups["relics"][relics[0]["id"]], "shop-stats", request["act"], "stats_shop_relic_tip")
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
    ensure_test_run()
    leave_screens()
    ensure_stats(True)
    since = log_length()
    console("ancient DARV")
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
    ensure_test_run()
    leave_screens()
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
    import shutil
    if not ACCOUNT.exists():
        raise CheckError(f"needs a login in {ACCOUNT}: log in through the mod's settings first")
    ensure_test_run()
    leave_screens()
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
        if row_enabled(setting_row(screen, "stats_samples")):
            result.fail("sample sizes are not greyed out without a login")
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
        set_setting("cards", "tiers")
        view, shot = open_shop("logged_out_shop")
        result.screenshots.append(shot)
        if any(b["kind"] == "stats" for b in view["badges"]):
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


def restart_game():
    """Quits and restarts the game (tools/reload.sh without building, which makes it windowed)
    and gets to the main menu."""
    import subprocess
    subprocess.run([str(REPO / "tools" / "reload.sh"), "--no-build"], check=True)
    wait_for(lambda: state()["state_type"] == "menu", "the menu after the restart", timeout=60)


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
    "stats_card_reward": scenario_stats_card_reward,
    "stats_shop": scenario_stats_shop,
    "stats_ancient": scenario_stats_ancient,
    "stats_upgrade": scenario_stats_upgrade,
    "stats_deck_view": scenario_stats_deck_view,
    "logged_out": scenario_logged_out,
}


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

    tiers = TierLists()
    window("saved" if args.full_size else "windowed")
    if args.new_run:
        new_test_run()
    results = []
    for name in names:
        result = Result(name)
        try:
            # The tier scenarios check tier badges. The stats ones turn stats on themselves.
            if not name.startswith("stats_") and name not in ("logged_out", "lists", "settings") and any(
                    read_settings()[k] == "global" for k in BADGE_KEYS):
                ensure_test_run()
                ensure_stats(False)
            try:
                SCENARIOS[name](result, tiers)
            except RunExhausted as e:
                print(f"     ↻ {e}, starting a fresh test run")
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
