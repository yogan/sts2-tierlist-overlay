"""What the scenarios share to judge a screen: the tier lists the mod should show, the
result of a scenario, and the checks on badges, tier tips and their layout."""

import json
import time
from dataclasses import dataclass, field

from PIL import Image

from .game import MOD_DIR, CheckError, find_node, hover_until, read_settings, screenshot, tree, wait_for, walk
from .geometry import centre, colour_share, fully_inside, hex_rgb, intersection, overlaps, share_covered


@dataclass
class Entry:
    tier: str
    color: str
    commentary: str
    good_upgrade: bool

    def __getitem__(self, i):  # (tier, color), as the checks unpack it
        return (self.tier, self.color)[i]


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


class RunExhausted(CheckError):
    """The test run has used up its relics, so main() starts a fresh run and retries."""


def exhausted_relics(result, view, context_type):
    relics = [r["id"] for r in view["relics"] if in_context(r, context_type)]
    if relics and all(r == "CIRCLET" for r in relics):
        raise RunExhausted("only Circlets on offer: this test run has used up its relics")


def in_context(item, context_type):
    return context_type in item["context"]


def tier_label(tier):
    return f"{tier}-Tier" if len(tier) <= 2 else tier


# Where the mod puts badges, by the item's node type (see Placement in Badges.cs).
BUTTON_TYPES = {"NRewardButton", "NEventOptionButton"}
ICON_TYPES = {"NRelic", "NPotion"}


def badge_belongs_to(badge, item):
    """The badge's centre lies on the item or within a badge height of its edges."""
    bx, by = centre(badge)
    m = badge[3]
    return item[0] - m <= bx <= item[0] + item[2] + m and item[1] - m <= by <= item[1] + item[3] + m


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


def check_tip(result, tiers, item, shot_name=None):
    """Hovering a rated item shows the author's tip with its tier and commentary."""
    expected = tiers.get(item["id"], card=item["type"] == "NCard")
    if expected is None:
        return
    # The tip normally shows within a frame or two. Allow for screens still animating in.
    mine = lambda view: [t for t in view["tier_tips"] if t["visible"] and t["item_id"] == item["id"]]
    view = hover_until(centre(item["rect"]), mine)
    tips = mine(view)
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


# The colour of a tip's dim lines (Dim in Tips.cs): additional information, not a fact of its own.
DIM = "[color=#a0a8aa]"


def wrapped_lines(tip):
    """The lines of a stats tip that wrap on screen. One fact per line, short enough to fit
    (docs/UI_TEXT.md). Dim lines ("Based on ...", "Other characters: ...") may take two."""
    dim = [line.startswith(DIM) for line in (tip.get("bbcode") or "").split("\n")]
    return [text for n, (text, rows) in enumerate(tip.get("lines") or [])
            if rows > 1 and not (n < len(dim) and dim[n])]


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


def check_badges_on_box_top(result, view, shot, boxes=None):
    """An ancient option badge's pointed ends (its vertical middle) must sit on the option
    box's visible top edge: where the dark box starts beside the badge. `boxes`: the option
    boxes' rectangles by item ID, where boxes stack so close that the search must stay inside
    the badge's own box (other events)."""
    image = Image.open(shot).convert("RGB")
    checked = 0
    for badge in view["badges"]:
        if not (badge["visible"] and in_context(badge, "NEventOptionButton")):
            continue
        bx, by = centre(badge["rect"])
        # Close to the badge only: the ancient's speech bubble can end just above the options.
        rows = range(int(by - badge["rect"][3] * 0.6), int(by + badge["rect"][3] * 0.6))
        if boxes and badge["item_id"] in boxes:
            rows = range(max(rows.start, int(boxes[badge["item_id"]][1]) + 3), rows.stop)
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
                result.fail(f"{badge['item_id']}: badge drawn over a hover tip ({share:.0%} badge colour in the overlap "
                            f"{[round(v) for v in region]}, badge {[round(v) for v in badge['rect']]})")
    result.note(f"tooltip layering: {checked} badge/tooltip overlaps checked")


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
