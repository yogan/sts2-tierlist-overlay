#!/usr/bin/env python3
"""Fight the current combat to the end through the STS2_MCP mod's HTTP API.

A deliberately simple strategy for reaching test screens (card rewards etc.), not for
playing well: block enough to cover the incoming attack, then spend the remaining energy
on attacks against the weakest enemy.

Usage: python3 -I tools/autopilot.py [--port 15526]
"""

import argparse
import json
import re
import sys
import time
import urllib.request

COMBAT_STATES = {"monster", "elite", "boss"}


def get_state(base):
    with urllib.request.urlopen(f"{base}/api/v1/singleplayer", timeout=10) as r:
        return json.load(r)


def post(base, body):
    req = urllib.request.Request(
        f"{base}/api/v1/singleplayer",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.load(r)


def incoming_damage(enemies):
    total = 0
    for enemy in enemies:
        for intent in enemy.get("intents", []):
            if intent.get("type") != "Attack":
                continue
            m = re.fullmatch(r"(\d+)(?:x(\d+))?", intent.get("label", ""))
            if m:
                total += int(m.group(1)) * int(m.group(2) or 1)
    return total


def block_value(card):
    m = re.search(r"Gain (\d+) Block", card.get("description", ""))
    return int(m.group(1)) if m else 0


def choose_card(state, blocked_so_far):
    player, battle = state.get("player"), state.get("battle")
    # The battle section disappears while the combat is ending.
    if not player or not battle:
        return None
    enemies = [e for e in battle["enemies"] if e["hp"] > 0]
    playable = [c for c in player["hand"] if c["can_play"]]
    if not playable or not enemies:
        return None
    need_block = incoming_damage(enemies) - player.get("block", 0) - blocked_so_far
    blockers = [c for c in playable if block_value(c) > 0]
    if need_block > 0 and blockers:
        return blockers[0], None
    attacks = [c for c in playable if c["type"] == "Attack"]
    target = min(enemies, key=lambda e: e["hp"])["entity_id"]
    if attacks:
        # Prefer the most expensive attack (Bash before Strike).
        card = max(attacks, key=lambda c: int(c["cost"]) if c["cost"].isdigit() else 0)
        return card, target if card["target_type"] == "AnyEnemy" else None
    return None


def fight(base):
    while True:
        state = get_state(base)
        if state["state_type"] not in COMBAT_STATES:
            print(f"Combat over, now in state '{state['state_type']}'")
            return
        battle = state.get("battle") or {}
        if not battle.get("is_play_phase") or battle.get("turn") != "player":
            time.sleep(0.5)
            continue
        blocked = 0
        while True:
            choice = choose_card(state, blocked)
            if choice is None:
                break
            card, target = choice
            body = {"action": "play_card", "card_index": card["index"]}
            if target:
                body["target"] = target
            post(base, body)
            blocked += block_value(card)
            print(f"Played {card['id']}" + (f" -> {target}" if target else ""))
            time.sleep(1.2)
            state = get_state(base)
            if state["state_type"] not in COMBAT_STATES:
                break
        if state["state_type"] in COMBAT_STATES:
            post(base, {"action": "end_turn"})
            print("End turn")
            time.sleep(2)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--port", type=int, default=15526)
    args = parser.parse_args()
    try:
        fight(f"http://localhost:{args.port}")
    except OSError as e:
        sys.exit(f"Cannot reach the STS2_MCP mod: {e}")


if __name__ == "__main__":
    main()
