#!/usr/bin/env bash
# seed.sh [SERVER_ROOT]: copies the test accounts (accounts/*.json) into the server's data/accounts, replacing
# earlier copies, so each run starts from level-1 characters on map 10037. Stop the server first: it reads the
# account files when it starts and writes them when players leave.
# SERVER_ROOT defaults to this checkout; the server keeps its data in <root>/data (Server:DataDirectory).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=${1:-$here/../..}
mkdir -p "$root/data/accounts"
cp "$here"/accounts/*.json "$root/data/accounts/"
echo "seeded $(ls "$here"/accounts/*.json | wc -l) accounts into $(cd "$root/data/accounts" && pwd)"
