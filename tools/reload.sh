#!/bin/sh
# Quit Slay the Spire 2, build and install the mods, start the game again via Steam
# (so the launch options apply) and wait until the mods' HTTP endpoints answer.
# The game comes up in a window beside other apps (tools/dev.py window saved restores the
# player's display setting). --no-build: only restart the game.
set -eu

repo="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

is_running() { pgrep -f "SlayTheSpire2.app/Contents/MacOS" >/dev/null; }

if is_running; then
    osascript -e 'tell application id "com.megacrit.SlayTheSpire2" to quit' >/dev/null 2>&1 || true
    i=0
    while is_running; do
        i=$((i + 1))
        if [ "$i" -gt 30 ]; then
            echo "Game did not quit within 30s" >&2
            exit 1
        fi
        sleep 1
    done
    echo "Game quit"
fi

if [ "${1:-}" != "--no-build" ]; then
    for proj in "$repo/TierListOverlay.csproj" "$repo/tools/DevTools/DevTools.csproj"; do
        dotnet build "$proj" -c Release -p:Install=true 2>&1 | grep -E 'error|Installed' || true
    done
fi

open "steam://rungameid/2868840"
i=0
until curl -s -m 2 localhost:15526/ >/dev/null && curl -s -m 2 localhost:15527/window >/dev/null; do
    i=$((i + 1))
    if [ "$i" -gt 60 ]; then
        echo "Mods did not come up within 120s" >&2
        exit 1
    fi
    sleep 2
done
# The game applies its own display setting a moment after the mods answer, so window it until it
# stays windowed.
i=0
stayed=0
while [ "$stayed" -lt 2 ] && [ "$i" -lt 15 ]; do
    i=$((i + 1))
    if curl -s -m 5 localhost:15527/window | grep -q '"mode": "Windowed"'; then
        stayed=$((stayed + 1))
    else
        stayed=0
        curl -s -m 5 -d '' "localhost:15527/window/mode?mode=windowed" >/dev/null || true
    fi
    sleep 2
done
echo "Game is up"
