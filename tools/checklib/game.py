"""Driving the running game: the DevTools and STS2_MCP endpoints, waiting for states, getting
into a run in the test profile and onto the screens the scenarios check."""

import json
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

from .geometry import centre, fully_inside

MCP = "http://localhost:15526"
DEVTOOLS = "http://localhost:15527"
TEST_PROFILE = 2
REPO = Path(__file__).resolve().parent.parent.parent
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
    """"windowed": the game in a window beside other apps, to watch while tools drive it, and
    muted. "saved": back to the player's display setting, with sound. Neither changes the saved
    settings."""
    _request(f"{DEVTOOLS}/window/mode?mode={mode}", b"")
    time.sleep(1.5)  # the window settles


def mute(on=True):
    """Mutes the game (or puts back the player's volume), without changing the saved setting."""
    _request(f"{DEVTOOLS}/mute?on={'true' if on else 'false'}", b"")


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


def _try_call(node_type, method):
    try:
        return call(node_type, method)
    except CheckError as e:
        if f"no {node_type}" not in str(e):
            raise
        return None


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


def walk(node):
    yield node
    for child in node.get("children", []):
        yield from walk(child)


def _has_node(node_type):
    try:
        tree(node_type, 0)
        return True
    except CheckError as e:
        if f"no {node_type}" in str(e):
            return False
        raise


def click(rect):
    x, y = centre(rect)
    _request(f"{DEVTOOLS}/mouse/click?x={x:.0f}&y={y:.0f}", b"")


def hover(x, y):
    _request(f"{DEVTOOLS}/mouse/move?x={x:.0f}&y={y:.0f}", b"")


def hover_until(point, done, timeout=3.0):
    """Hovers `point` and returns the view once `done(view)` holds, or the last view after
    `timeout`. Leaves first, so the move enters anew. A synthetic mouse move now and then does
    not register as entering (no game tips either): then it leaves and enters again once."""
    x, y = point
    hover(10, 10)
    time.sleep(0.3)
    hover(x, y)
    start = time.monotonic()
    nudged = False
    while True:
        time.sleep(0.25)
        view = inspect()
        if done(view) or time.monotonic() - start > timeout:
            return view
        if not nudged and not view["hover_tips"] and time.monotonic() - start > 1.0:
            hover(10, 10)
            time.sleep(0.25)
            hover(x, y)
            nudged = True


def screenshot(name, layout=True):
    """Saves the frame to .artifacts/NAME.png and, with `layout`, what is on it to NAME.json:
    the window size and the rects of the items, badges and tips, so images made from it can
    be cropped by what they show rather than by pixels."""
    ARTIFACTS.mkdir(exist_ok=True)
    path = ARTIFACTS / f"{name}.png"
    _request(f"{DEVTOOLS}/screenshot?path={path}")
    if layout:
        path.with_suffix(".json").write_text(json.dumps(screen_layout(inspect()), indent=1))
    return path


def screen_layout(view):
    """The parts of an inspect() view a showcase crop can name, visible and on screen only."""
    shown = lambda things: [{**t, "rect": [round(v, 1) for v in t["rect"]]} for t in things
                            if t.get("on_screen", True) and t.get("visible", True)]
    return {
        "taken": time.time(),
        "window": view["window"],
        "items": [{"kind": kind[:-1], "id": i["id"], "type": i["type"], "rect": i["rect"]}
                  for kind in ("cards", "relics", "potions", "events") for i in shown(view[kind])],
        "badges": [{"id": b["item_id"], "label": b["label"], "rect": b["rect"]} for b in shown(view["badges"])],
        # The game's tip panels, the mod's among them, and which of them are the mod's for an item.
        "tips": [{"rect": t["rect"]} for t in shown(view["hover_tips"])],
        "item_tips": [{"kind": kind.split("_")[0], "id": t["item_id"], "rect": t["rect"]}
                      for kind in ("tier_tips", "stats_tips", "setting_tips") for t in shown(view[kind])],
    }


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


def log_length():
    return len(GAME_LOG.read_text(errors="replace").splitlines())


def log_since(line):
    """The game log's lines from line number `line` on."""
    return GAME_LOG.read_text(errors="replace").splitlines()[line:]


# --- The mod's settings -------------------------------------------------------------------

SETTING_DEFAULTS = {"cards": "tiers", "relics": "tiers", "potions": "tiers", "events": "personal", "deck": "tiers", "author": "Baalorlord",
                    "commentary": True, "stats_tips": True, "stats_samples": False}
# The mod's settings rows: tickboxes, and left/right choices with their options in order.
TOGGLES = ("commentary", "stats_tips", "stats_samples")
# Saved value -> label shown, per choice, in the order the arrows page through them.
BADGES = {"none": "None", "tiers": "Tier list", "personal": "Personal stats", "global": "Global stats"}
CHOICES = {"cards": BADGES, "relics": BADGES, "potions": BADGES, "events": {"none": "None", "personal": "Personal stats"},
           "deck": {"none": "None", "tiers": "Tier list"},
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


# --- Game setup ---------------------------------------------------------------------------

def setup():
    """What every scenario but `lists` starts from: a run in the test profile, no overlay
    screen left open by the scenario before."""
    ensure_test_run()
    leave_screens()


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


def leave_screens():
    """Back to a neutral state: no overlay screen left open from an earlier scenario."""
    s = state()
    if s["state_type"] == "card_select" and s["card_select"].get("can_cancel"):
        act("cancel_selection")
    elif s["state_type"] in ("rewards", "card_reward"):
        # Console room jumps leave reward overlays open on top of the next room.
        call("NOverlayStack", "Clear")
        time.sleep(1)


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
    size = inspect()["window"]
    for _ in range(30):
        nodes = list(walk(tree("NGame", 30)))
        visible = [n for n in nodes if n.get("visible") and n.get("rect") and n["rect"][2] > 0]
        # Reveal and unlock screens end in a Continue/Confirm button.
        buttons = [n for n in visible if n["type"] in ("NCloseButton", "NConfirmButton", "NProceedButton")]
        slots = [n for n in visible if n["type"] == "NEpochSlot" and n.get("state") == EPOCH_OBTAINED
                 and fully_inside(n["rect"], size)]
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


def restart_game():
    """Quits and restarts the game (tools/reload.sh without building, which makes it windowed)
    and gets to the main menu."""
    subprocess.run([str(REPO / "tools" / "reload.sh"), "--no-build"], check=True)
    wait_for(lambda: state()["state_type"] == "menu", "the menu after the restart", timeout=60)


# --- Screens ------------------------------------------------------------------------------

def enter_combat():
    console("room Monster")
    wait_state("monster", "elite", "boss")


def open_card_reward():
    """Wins a fight and opens its card reward. Returns the reward's index on the loot screen."""
    enter_combat()
    console("win")
    rewards = wait_state("rewards")
    card_reward = next(i for i in rewards["rewards"]["items"] if i["type"] == "card")
    act("claim_reward", index=card_reward["index"])
    wait_state("card_reward")
    time.sleep(1.5)
    return card_reward["index"]


def open_shop(name):
    """Jumps to a new shop, opens its inventory, and returns the view and a screenshot."""
    leave_screens()
    console("room Shop")
    wait_state("shop")
    call("NMerchantRoom", "OpenInventory")
    time.sleep(1.5)
    view = inspect()
    return view, screenshot(name)


def open_event(name):
    """Jumps to an event (its ID, "SAPPHIRE_SEED") and waits for its options."""
    console(f"event {name}")
    wait_state("event")
    time.sleep(2.0)


def open_ancient():
    """Jumps to the ancient Darv's event, offering relics as options."""
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
