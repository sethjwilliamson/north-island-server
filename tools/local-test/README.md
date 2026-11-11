# Local test environment

A ready-made setup for driving the north-island-client against a local server with a test script (`--script`), a party
leader and two party members included.

| File | What it is |
| --- | --- |
| `accounts/*.json` | Account files of `testlead` (GM), `testmem1` and `testmem2`, password `test1234`, each with a level-1 character in slot 1 on map 10037 at (1082,2275), next to NPC place 1 (npc 12032) |
| `seed.sh [SERVER_ROOT]` | Copies the account files into the server's `data/accounts` (default: this checkout) |
| `party.txt` | The client's `--accounts` file: the two members, slot 1, auto battle |
| `extra-settings.json` | Client settings with `game.partyLogin: true`; `run-client.sh` puts it in a new user folder |
| `setup-game.sh GAME_DIR [OUT_DIR]` | Builds `game/`: symlinks to the game folder plus a `SERVER.INI` that lists only `Local` at 127.0.0.1 |
| `run-client.sh USER_DIR SCRIPT [args]` | Runs the client on a script, converts the `shot` BMPs to PNG and prints the `script:` log lines |
| `scripts/party-mirror.txt` | Example: log in, turn the party NPC mirror on, talk to NPC place 1 and pick choice 3 |
| `scripts/broken-login.txt` | Example of a failing script: it exits with code 3 in a few seconds |

The passwords are test passwords only. The account files hold their hashes; `party.txt` and the scripts hold them in clear.

## The loop

```bash
# once: the game folder (the original install is only read, never changed)
tools/local-test/setup-game.sh "$HOME/Downloads/Wonderland Online"

# before each run, with the server stopped: fresh accounts
tools/local-test/seed.sh

# the server (from the checkout root; in another terminal or in the background)
dotnet run --project src/NorthIsland.Server -- --data "$HOME/Downloads/Wonderland Online"

# the client: CLIENT defaults to ../north-island-client/build/linux-x64/north-island-client next to this checkout
CLIENT=/path/to/north-island-client tools/local-test/run-client.sh tools/local-test/runs/mirror tools/local-test/scripts/party-mirror.txt \
    --accounts tools/local-test/party.txt --script-timeout 120
```

`run-client.sh` prints the exit code and time, the PNGs of the shots and the `script:` lines of the client's log
(`USER_DIR/north-island-client.log`; stdout goes to `USER_DIR/stdout.txt`). `RUN_TIMEOUT` (seconds, default 300) is a hard
limit around the client. The server logs every event step, so `grep "answer" <server log>` shows which sessions answered a dialog.

Exit codes of the client with `--script`: 0 the script ran to `quit`; 1 a start-up error (logged, no message box); 3 an error box
or form opened (refused login, disconnection); 4 `--script-timeout` ran out.

## Waiting instead of sleeping

The client's state commands (see the doc comment of `InputScript` in the client) wait for the game rather than for a time:
`login ACCOUNT PASSWORD [SLOT]` returns once the world and the party members are loaded, `waitdialog` once the talk box can
be answered, `choose N|Confirm|Cancel|Close|Next` answers it, `skiptalk` clicks words away until a question or the end of the
event, `waitevent` waits for the event to end and `button NAME` clicks a layout button. Keep `wait` for short settle delays.

## How the accounts were made

Each account was created through the real server (AutoRegister) with the client's character creation, then the characters were
put on map 10037 with the intro's quest flags (12035, 12047, 12051) and items from an earlier run through the intro, and the
leader talked to NPC place 1 once through the server, which added quest mark 12040 to all three: that is the state in which the
NPC opens its 3-choice dialog (`evescan --map 10037 dialogs`). Times and online seconds were then reset and `"Gm": true` set on
`testlead`. Picking choice 3 removes mark 12040 again, hence the seeding before each run.

To remake them: stop the server, delete its `data/accounts/test*.json`, create the characters with the client, play them to
the state wanted, stop the server and copy the files back here (then reset `CreatedUtc`, `LastLoginUtc`, `OnlineSeconds` and
the map position, and keep `"Gm": true` on the leader).
