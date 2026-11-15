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
  # Start the app directly: going through steam://rungameid opens the Paradox Launcher, which needs a manual click on Play.
  echo "==> Starting game"
  open "$HOME/Library/Application Support/Steam/steamapps/common/Cities_Skylines/Cities.app"
  echo "Load a city; the API starts then (http://127.0.0.1:8080/ping)."
fi
