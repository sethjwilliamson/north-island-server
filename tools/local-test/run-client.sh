#!/usr/bin/env bash
# run-client.sh USER_DIR SCRIPT [client args...]: runs the client on a test script and shows what it did.
#   USER_DIR   the client's user folder (created; extra-settings.json from here is put in when it has none)
#   SCRIPT     the --script file
#   client args are passed on, e.g. --accounts tools/local-test/party.txt --script-timeout 120
# Environment: CLIENT (the north-island-client binary; default: build/linux-x64 of a north-island-client checkout beside this one),
#   GAME (game folder from setup-game.sh, default tools/local-test/game), RUN_TIMEOUT (hard limit in s, default 300).
# Prints the exit code, converts the shot BMPs to PNG and prints the "script:" lines of the client log.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
user=${1:?usage: run-client.sh USER_DIR SCRIPT [client args...]}
script=${2:?usage: run-client.sh USER_DIR SCRIPT [client args...]}
shift 2
client=${CLIENT:-$here/../../../north-island-client/build/linux-x64/north-island-client}
game=${GAME:-$here/game}
[ -x "$client" ] || { echo "run-client.sh: no client at $client (set CLIENT)" >&2; exit 2; }
[ -f "$game/SERVER.INI" ] || { echo "run-client.sh: no game folder at $game (run setup-game.sh)" >&2; exit 2; }

mkdir -p "$user"
[ -f "$user/extra-settings.json" ] || cp "$here/extra-settings.json" "$user/"
rm -f "$user"/*.bmp "$user"/*.png "$user/north-island-client.log"

start=$(date +%s.%N)
timeout "${RUN_TIMEOUT:-300}" "$client" --data "$game" --user "$user" --script "$script" "$@" > "$user/stdout.txt" 2>&1
code=$?
end=$(date +%s.%N)
printf 'exit %d after %.1f s\n' "$code" "$(echo "$end - $start" | bc)"

for bmp in "$user"/*.bmp; do
    [ -e "$bmp" ] || continue
    python3 -c 'import sys; from PIL import Image; Image.open(sys.argv[1]).save(sys.argv[2])' "$bmp" "${bmp%.bmp}.png" 2>/dev/null \
        || convert "$bmp" "${bmp%.bmp}.png" 2>/dev/null \
        || echo "run-client.sh: cannot convert $bmp (needs python3-pil or ImageMagick)" >&2
    [ -e "${bmp%.bmp}.png" ] && echo "shot: ${bmp%.bmp}.png"
done
grep -h "script: \|message box: " "$user/north-island-client.log" 2>/dev/null
exit "$code"
