using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Events;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>
    /// The Trojan War attacker rounds: the round map, the core NPC of its battles and the mark a win sets.
    /// </summary>
    /// <remarks>
    /// Inferred, not from the data: no event in any data version (2008 to 2014) sets 50155 (round 1 won) or 50157 (round 2 won);
    /// every result on them is "add 0" and the round maps only test them, so the original server set them from the battle.
    /// The core NPCs are the client's (GetCastleCoreName: Tower 12083 on 10065, The Status of Knight 12084 on 10066).
    /// </remarks>
    private static readonly (int Map, int Core, ushort Mark, ushort Convey)[] TrojanRounds =
    [
        (10065, 12083, 50155, 2), // round 1: then on through convey 2, the attackers' side of round 2, which no event uses
        (10066, 12084, 50157, 0), // round 2: its own event (10066 event 1 group 3) warps the winners to round 3
    ];

    /// <summary>
    /// Sets the round mark for a player who won, during the Trojan War, the event battle that holds the round's core NPC.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="battle">The battle that ended.</param>
    /// <param name="player">The player fighter.</param>
    /// <param name="outcome">How the battle ended for the player.</param>
    private void TrojanRoundWon(GameSession session, Battle battle, Fighter player, BattleOutcome outcome)
    {
        if (outcome != BattleOutcome.Won || battle.Standalone || !castles.WarOn || session.ActiveCharacter is not { } character)
            return;
        foreach (var (map, core, mark, convey) in TrojanRounds)
        {
            if (battle.MapId != map || character.MapId != map || !battle.Monsters.Any(m => m.Id == core))
                continue;
            logger.LogInformation("[{Session}] {Name} won Trojan round {Round} (core NPC {Core}): mark {Mark} set", session.Id, character.Name,
                map - 10064, core, mark);
            if (ReferenceEquals(player, battle.Player) && session.Event is { } run)
            {
                // the event engine applies them after the fight, as it would the data's own results (mark add 1, warp via convey)
                run.Pending.Enqueue(new EveScriptRecord(0, 5, mark, 1, 2, 0, 1, 0, 0, 0));
                if (convey != 0)
                    run.Pending.Enqueue(new EveScriptRecord(0, 1, 3, convey, 0, 0, 0, 0, 0, 0));
            }
            else
                marks.TrySetFlag(session, character, mark, on: true);
        }
    }
}
