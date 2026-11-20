using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Events;

namespace NorthIsland.Server.Features.Events;

public sealed partial class EventEngine
{
    /// <summary>
    /// Starts the event of an encounter zone (a mine, 地雷塊) the player walks in, but only when one of its condition groups starts a battle and holds.
    /// The event then runs as usual, so the linked battle results are picked by the weights of the scene's groups and the results after the battle run.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="mine">The zone the player stands in.</param>
    /// <param name="source">The source name of the run.</param>
    /// <returns>True when an event run started; false when the zone would start no battle.</returns>
    public bool StartZone(GameSession session, EveMine mine, string source)
    {
        var character = session.ActiveCharacter!;
        if (maps.Scene(character.MapId) is not { } scene)
            return false;
        var run = NewRun(session, character, TriggerArea, mine.Index, source);
        foreach (var ev in scene.EventsOf(mine))
            foreach (var group in ConditionEvaluator.Groups(ev))
                run.Groups.Enqueue(group);
        if (!run.Groups.Any(g => g.Results.Any(r => r.Kind == 4 && r.SubKind == 1) && ConditionEvaluator.Holds(g, character, run, NullLogger.Instance)))
            return false;
        logger.LogInformation("[{Session}] {Name} meets a random fight in encounter zone {Mine} on map {Map}", session.Id, character.Name, mine.Index, character.MapId);
        session.Event = run;
        Advance(session);
        return true;
    }

    /// <summary>
    /// Picks a random fight for a field encounter from the field-monster events of the monsters met, and sends the battle to the client.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="monsters">The monsters met on the map.</param>
    /// <param name="rng">The random source that picks among the battles found.</param>
    /// <returns>True when a battle was chosen; false when none of the monsters has a usable battle.</returns>
    public bool StartField(GameSession session, IReadOnlyList<EveNpc> monsters, Random rng)
    {
        var character = session.ActiveCharacter!;
        if (maps.Scene(character.MapId) is not { } scene)
            return false;
        var run = NewRun(session, character, TriggerArea, 0, FieldSource);
        List<ushort> battles = [];
        foreach (var monster in monsters)
        {
            foreach (var ev in scene.EventsOf(monster).Where(e => e.IsFieldMonster))
            {
                foreach (var group in ConditionEvaluator.Groups(ev))
                {
                    if (!group.Results.Any(r => r.Kind == 4 && r.SubKind == 1) || !ConditionEvaluator.Holds(group, character, run, NullLogger.Instance))
                        continue;
                    battles.AddRange(group.Results.Where(r => r.Kind == 4 && r.SubKind == 1)
                        .Select(r => r.Arg1)
                        .Where(index => scene.Battles.FirstOrDefault(b => b.Index == index) is { } b && b.Left.Any(m => npcs[(ushort)m.Id] is not null)));
                }
            }
        }
        if (battles.Count == 0)
            return false;
        var chosen = battles[rng.Next(battles.Count)];
        logger.LogInformation("[{Session}] {Name} meets a random fight on map {Map}: battle {Battle}", session.Id, character.Name, character.MapId, chosen);
        session.Event = run;
        SendRecord(session, run, new EventResult(3, 0, 0, 0, 0, chosen)); // 3 Kind
        run.PendingBattle = chosen;
        run.BattleOutcome = null;
        return true;
    }

    /// <summary>The source name given to runs started by a field encounter.</summary>
    public const string FieldSource = "field encounter";
}
