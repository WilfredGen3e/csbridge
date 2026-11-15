#!/bin/zsh
# Build the mod, close Cities: Skylines, start it again (directly, no Paradox Launcher).
# Usage: scripts/restart-game.sh [--no-launch]
# Note: unsaved progress in the running game is lost (quit is graceful, force-kill after 20s).
set -e
cd "$(dirname "$0")/.."

echo "==> Building"
dotnet build CSBridge.csproj -c Release -nologo -v q | grep -E "error|warn|Build succeeded" | sort -u
[[ ${pipestatus[1]} -eq 0 ]] || { echo "Build failed, game left running."; exit 1; }

if pgrep -x Cities >/dev/null; then
  echo "==> Closing game"
  osascript -e 'tell application "Cities" to quit' >/dev/null 2>&1 || true
  for i in {1..20}; do pgrep -x Cities >/dev/null || break; sleep 1; done
  if pgrep -x Cities >/dev/null; then echo "   not closed after 20s, force killing"; pkill -x Cities; sleep 2; fi
fi

if [[ "$1" != "--no-launch" ]]; then
  # Direct start of Cities.app fails now ("Platform service is not running"): the game needs the Paradox Launcher.
  # Launch via Steam; the launcher then needs a click on Resume (loads the last save).
  echo "==> Starting game via Steam -> click Resume in the Paradox Launcher"
  open "steam://rungameid/255710"
  echo "==> Waiting for API (last save loads automatically)"
  for i in {1..120}; do
    curl -s -m 2 http://127.0.0.1:8080/ping >/dev/null && { echo "API up."; exit 0; }
    sleep 2
  done
  echo "API did not respond after 4 min; check the game / ~/Library/Logs/Unity/Player.log"; exit 1
fi
