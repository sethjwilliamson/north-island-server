# evescan

Queries the client's `data/eve.Emg` (the map event scenes) to find what to test against: the NPCs of a map, the NPCs whose
events open a dialog with choices, and where a quest mark or an item is given or taken. It reads the file with
`NorthIsland.GameData` and decodes the results the way `src/NorthIsland.Server/Features/Events/ResultMapper.cs` does.

```bash
dotnet run --project tools/evescan -- --data "$HOME/Downloads/Wonderland Online" --map 10037 npcs
# or, once built: dotnet out/bin/x64/evescan.dll ...
```

Options: `--data DIR` (the game folder; `$NI_GAME_DATA` by default) or `--eve FILE`, and `--map FILTER` to limit the maps:
`10037`, `10000-10099` or `10037,12003` (all maps by default).

| Command | Prints |
| --- | --- |
| `npcs` | every NPC: place index (the 1-based runtime index, as in the client's `clickobj npcindex N`), NPC id, position, event ids |
| `dialogs [MIN]` | NPCs with an event result that opens a dialog with at least MIN choices (default 1): result 1/4 (interact `Arg1`) or 2/x/6 (interact `Arg2`) on an interact of kind 1; `choices` counts its kind 3 entries, `answers` its kind 2 entries |
| `mark ID` | event results that add to (5/x/1), delete (5/x/2) or set (5/x/3) quest mark ID, with the NPCs that run the event |
| `item ID` | event results that give or take item ID (1/1/1, 1/1/7, prizes 1/10 and 1/11), with the NPCs that run the event |

Example:

```text
$ evescan --data "$HOME/Downloads/Wonderland Online" --map 10037 dialogs
map 10037 place 1 npc 12032 event 8 dialog 1 choices 3 answers 0 Npc
```

A dialog found here only opens when its event's conditions hold for the character (quest marks, items ...); the server's log
shows which step of an event held.
