#!/usr/bin/env bash
# setup-game.sh GAME_DIR [OUT_DIR]: builds a game folder for the local server.
# OUT_DIR (default: tools/local-test/game) gets a symlink to every top-level entry of GAME_DIR except SERVER.INI,
# and its own SERVER.INI listing one server, Local at 127.0.0.1. GAME_DIR itself is never changed.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
game=${1:?usage: setup-game.sh GAME_DIR [OUT_DIR]}
out=${2:-$here/game}
game=$(cd "$game" && pwd)
[ -d "$game/data" ] || { echo "setup-game.sh: $game has no data folder; give the Wonderland Online folder" >&2; exit 1; }
mkdir -p "$out"
for entry in "$game"/*; do
    name=$(basename "$entry")
    [ "${name^^}" = "SERVER.INI" ] && continue
    ln -sfn "$entry" "$out/$name"
done
printf '01[Local]1\r\nLocal*127.0.0.1\r\n' > "$out/SERVER.INI"
echo "game folder: $out"
