#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["pillow"]
# ///
"""Poke at the running game while developing: look at nodes, click, take small screenshots.

Talks to the DevTools mod (port 15527) like tools/check.py does, and reuses its helpers
(checklib/game.py).
Coordinates are window pixels (screenshot pixels), as /inspect and /tree report them. The
window size varies (e.g. 1920x1080 or 3024x1898), so read positions off `tree`, never assume.

  dev.py tree NMerchantRelic [--depth 4] [--all]   node subtree: name, type, rect, text
  dev.py inspect [--kind badges]                   cards/relics/potions/badges/tips summary
  dev.py move X Y | click X Y                      mouse (real input path)
  dev.py key Escape                                press a key (Godot Key name)
  dev.py console 'room Shop'                       dev console command
  dev.py window windowed|saved                     game in a window beside other apps and muted /
                                                   back to the player's display setting and sound
                                                   (none is saved)
  dev.py shot [--node TYPE[:N]] [--crop X,Y,W,H] [--pad P] [--zoom Z] [--max PX] [NAME]
        screenshot to .artifacts/NAME.png, optionally cropped to a node's rect (Nth match)
        or a window rect, padded, zoomed (nearest) or shrunk to fit --max (default 1600),
        so a look at one badge costs a small image instead of a full frame
"""

import argparse
import json
import sys
import urllib.parse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from checklib import game  # noqa: E402  (shares the HTTP helpers and paths)

from PIL import Image  # noqa: E402


def print_tree(node, show_all, depth=0):
    if not node.get("visible", True) and not show_all:
        return
    rect = [round(v) for v in node.get("rect", [])]
    text = (node.get("text") or "").replace("\n", " ")[:50]
    print(f"{'  ' * depth}{node['name']} {node['type']} {rect} {text}".rstrip())
    for child in node.get("children", []):
        print_tree(child, show_all, depth + 1)


def drawn_rect(node):
    """Union of a node's rect and its visible descendants' rects: what it actually draws.
    A node's own rect can miss its content (a shop slot draws its relic outside it)."""
    boxes = []

    def walk(n):
        if not n.get("visible", True):
            return
        r = n.get("rect")
        if r and r[2] > 0 and r[3] > 0:
            boxes.append(r)
        for c in n.get("children", []):
            walk(c)

    for child in node.get("children", []):
        walk(child)
    # Its own rect only when nothing below it draws: a hover tip set sits at 0,0 and only
    # positions its children.
    if not boxes:
        return node.get("rect")
    x1, y1 = min(b[0] for b in boxes), min(b[1] for b in boxes)
    x2, y2 = max(b[0] + b[2] for b in boxes), max(b[1] + b[3] for b in boxes)
    return [x1, y1, x2 - x1, y2 - y1]


def node_rects(node_type):
    """Drawn rects of all visible nodes of a type, in tree order."""
    found = []

    def walk(n):
        if n.get("type") == node_type and n.get("visible", True):
            found.append(drawn_rect(n))
            return
        for c in n.get("children", []):
            walk(c)

    # /tree roots at the first node of the type. Its siblings hide under the parent, so
    # search from the scene root instead.
    walk(game.tree("NGame", 40))
    return found


def cmd_shot(args):
    box = None
    if args.node:
        node_type, _, index = args.node.partition(":")
        rects = node_rects(node_type)
        if len(rects) <= int(index or 0):
            sys.exit(f"no visible {args.node} ({len(rects)} {node_type} visible)")
        box = rects[int(index or 0)]
    elif args.crop:
        box = [float(v) for v in args.crop.split(",")]
    path = game.screenshot(args.name)
    image = Image.open(path).convert("RGB")
    if box:
        x, y, w, h = box
        p = args.pad
        image = image.crop((max(0, int(x - p)), max(0, int(y - p)),
                            min(image.width, int(x + w + p)), min(image.height, int(y + h + p))))
    if args.zoom != 1:
        image = image.resize((int(image.width * args.zoom), int(image.height * args.zoom)), Image.NEAREST)
    image.thumbnail((args.max, args.max))
    image.save(path)
    print(f"{path} {image.width}x{image.height}")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("tree")
    p.add_argument("type")
    p.add_argument("--depth", type=int, default=4)
    p.add_argument("--all", action="store_true", help="include hidden nodes")
    p = sub.add_parser("inspect")
    p.add_argument("--kind", choices=["cards", "relics", "potions", "badges", "hover_tips", "tier_tips", "stats_tips"])
    for name in ("move", "click"):
        p = sub.add_parser(name)
        p.add_argument("x", type=float)
        p.add_argument("y", type=float)
    p = sub.add_parser("key")
    p.add_argument("name")
    p = sub.add_parser("console")
    p.add_argument("command")
    p = sub.add_parser("window", help="windowed: watchable beside other apps, saved: the player's own setting")
    p.add_argument("mode", choices=["windowed", "saved"])
    p = sub.add_parser("shot")
    p.add_argument("name", nargs="?", default="dev")
    p.add_argument("--node", help="crop to a node type's rect, e.g. NMerchantRelic:1")
    p.add_argument("--crop", help="crop to X,Y,W,H in window pixels")
    p.add_argument("--pad", type=float, default=0)
    p.add_argument("--zoom", type=float, default=1)
    p.add_argument("--max", type=int, default=1600)
    args = parser.parse_args()

    try:
        if args.cmd == "tree":
            print_tree(game.tree(args.type, args.depth), args.all)
        elif args.cmd == "inspect":
            view = game.inspect()
            print("window", view["window"], "character", view["character"])
            for kind in [args.kind] if args.kind else ["cards", "relics", "potions", "badges", "hover_tips", "tier_tips", "stats_tips"]:
                for item in view[kind]:
                    if item.get("on_screen", True) and item.get("visible", True):
                        label = item.get("id") or item.get("item_id") or ""
                        extra = item.get("tier") or item.get("type") or ""
                        ctx = ",".join(item.get("context", [])[:3])
                        print(f"{kind[:-1]:9} {label:24} {extra:16} {[round(v) for v in item['rect']]} {ctx}")
        elif args.cmd in ("move", "click"):
            game._request(f"{game.DEVTOOLS}/mouse/{args.cmd}?x={args.x:.0f}&y={args.y:.0f}", b"")
        elif args.cmd == "key":
            game._request(f"{game.DEVTOOLS}/key?name={urllib.parse.quote(args.name)}", b"")
        elif args.cmd == "console":
            print(json.dumps(game.console(args.command)))
        elif args.cmd == "window":
            game.window(args.mode)
        elif args.cmd == "shot":
            cmd_shot(args)
    except game.CheckError as e:
        sys.exit(str(e))


if __name__ == "__main__":
    main()
