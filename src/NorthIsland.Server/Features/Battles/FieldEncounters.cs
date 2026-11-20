using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Events;

namespace NorthIsland.Server.Features.Battles;

/// <summary>
/// Starts random fights as a player walks, from two sources: the encounter zone of the scene the player stands in (a mine, 地雷塊), whose event picks
/// the fight from its weighted battle list, and the field monsters around the player. When both are there, <see cref="BattleOptions.FieldEncounterZonePercent"/>
/// decides which one is tried first, and the other is tried when the first starts no battle. One roll and one cooldown cover both.
/// </summary>
public sealed class FieldEncounters
{
    /// <summary>Map data for the scenes.</summary>
    private readonly MapData _maps;

    /// <summary>NPC records.</summary>
    private readonly NpcData _npcs;

    /// <summary>The shared world state (teams, monsters out of play).</summary>
    private readonly WorldState _world;

    /// <summary>The registry of instance maps.</summary>
    private readonly InstanceRegistry _instances;

    /// <summary>The event engine that starts the monsters' own battle events.</summary>
    private readonly EventEngine _events;

    /// <summary>The battle options.</summary>
    private readonly IOptions<BattleOptions> _options;

    /// <summary>The world options.</summary>
    private readonly IOptions<WorldOptions> _worldOptions;

    /// <summary>The logger.</summary>
    private readonly ILogger<FieldEncounters> _logger;

    /// <summary>Creates the encounter service and starts the cooldown whenever a field event finishes.</summary>
    /// <param name="maps">Map data for the scenes.</param>
    /// <param name="npcs">NPC records.</param>
    /// <param name="world">The shared world state.</param>
    /// <param name="instances">The registry of instance maps.</param>
    /// <param name="events">The event engine that starts the battle events.</param>
    /// <param name="options">The battle options.</param>
    /// <param name="worldOptions">The world options.</param>
    /// <param name="logger">The logger.</param>
    public FieldEncounters(
        MapData maps,
        NpcData npcs,
        WorldState world,
        InstanceRegistry instances,
        EventEngine events,
        IOptions<BattleOptions> options,
        IOptions<WorldOptions> worldOptions,
        ILogger<FieldEncounters> logger)
    {
        (_maps, _npcs, _world, _instances, _events, _options, _worldOptions, _logger) = (maps, npcs, world, instances, events, options, worldOptions, logger);
        events.EventFinished += (session, run) =>
        {
            if (run.Source == EventEngine.FieldSource || run.Source.StartsWith(ZoneSourcePrefix, StringComparison.Ordinal))
                session.Encounters.FieldQuietUntil = GameClock.UtcNow.AddSeconds(options.Value.FieldEncounterCooldownSeconds);
        };
    }

    /// <summary>The prefix of the source name of a fight started by an encounter zone; it is how the end of such an event is recognised.</summary>
    public const string ZoneSourcePrefix = "encounter zone ";

    /// <summary>Rolls for a random fight after the player took a step.</summary>
    /// <param name="session">The player's session.</param>
    public void OnWalk(GameSession session) => TryStart(session, GameRandom.Shared.Next(100), GameRandom.Shared);

    /// <summary>Starts a field fight when the roll succeeds and the player is allowed one.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="roll">The roll from 0 to 99; the fight can start only when it is below <see cref="BattleOptions.FieldEncounterPercent"/>.</param>
    /// <param name="rng">The random source that decides between a zone and the monsters around, passed on to the event engine to pick a monster's battle.</param>
    /// <returns>True when a fight was started.</returns>
    public bool TryStart(GameSession session, int roll, Random rng)
    {
        var o = _options.Value;
        if (o.FieldEncounterPercent <= 0 || session.ActiveCharacter is not { } character || session.Account is null)
            return false;
        var now = GameClock.UtcNow;
        if (!BattleHandlers.IsFree(session) || now < session.Encounters.FieldQuietUntil || session.Encounters.Immune(now) || session.Trades.Items is not null
            || session.ArrivedUtc.AddSeconds(Math.Max(_worldOptions.Value.ArrivalGraceSeconds, o.FieldEncounterCooldownSeconds)) > now)
            return false;
        var id = session.Account.LoginId;
        if (_world.Teams.LeaderOf(id) is var leader and not 0 && leader != id)
            return false;
        var map = character.MapId;
        if (_instances.ByMap(map) is not null || EventArenas.IsArena(map) || _maps.SceneType(map) is 2 or 3)
            return false;
        if (TimedEffects.EncountersBlocked(character, TimedEffects.Now))
            return false;
        if (session.Vehicle is { Riding: true } ride && !PlacedVehicle.IsRobot(ride.ItemId))
            return false;
        if (roll >= o.FieldEncounterPercent)
            return false;
        var mine = o.FieldEncounterZones && _maps.Scene(map) is { } scene ? ZoneAt(scene, character.X, character.Y) : null;
        var near = Surrounding(session, character);
        var zoneFirst = mine is not null && ZoneFirst(near.Count > 0, rng.Next(100), o.FieldEncounterZonePercent);
        if (zoneFirst && StartZone(session, character, mine!))
            return true;
        if (near.Count > 0 && _events.StartField(session, near, rng))
        {
            session.Encounters.FieldQuietUntil = now.AddSeconds(o.FieldEncounterCooldownSeconds);
            _logger.LogInformation(
                "[{Session}] field encounter on map {Map} against one of {Count} monster(s) around ({X},{Y}); next one in {Seconds} s at the earliest",
                session.Id, map, near.Count, character.X, character.Y, o.FieldEncounterCooldownSeconds);
            return true;
        }
        return !zoneFirst && mine is not null && StartZone(session, character, mine);
    }

    /// <summary>Tells whether the encounter zone the player stands in is tried before the field monsters around.</summary>
    /// <param name="monstersNear">True when field monsters are close enough to be fought.</param>
    /// <param name="roll">A roll from 0 to 99.</param>
    /// <param name="zonePercent">The chance in percent that the zone goes first when monsters are near too.</param>
    /// <returns>True when the zone goes first: always when no monster is near, otherwise when the roll is below the chance.</returns>
    public static bool ZoneFirst(bool monstersNear, int roll, int zonePercent) => !monstersNear || roll < zonePercent;

    /// <summary>Starts the fight of an encounter zone and the cooldown, when the zone's event starts a battle.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="mine">The zone the player stands in.</param>
    /// <returns>True when a fight was started.</returns>
    private bool StartZone(GameSession session, Character character, EveMine mine)
    {
        if (!_events.StartZone(session, mine, ZoneSourcePrefix + mine.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            return false;
        var seconds = _options.Value.FieldEncounterCooldownSeconds;
        session.Encounters.FieldQuietUntil = GameClock.UtcNow.AddSeconds(seconds);
        _logger.LogInformation(
            "[{Session}] field encounter on map {Map} in encounter zone {Mine} at ({X},{Y}); next one in {Seconds} s at the earliest",
            session.Id, character.MapId, mine.Index, character.X, character.Y, seconds);
        return true;
    }

    /// <summary>Finds the encounter zone (mine) of a scene that holds a point.</summary>
    /// <param name="scene">The scene of the map.</param>
    /// <param name="x">The X coordinate in map pixels.</param>
    /// <param name="y">The Y coordinate in map pixels.</param>
    /// <returns>The first zone in index order whose rectangle holds the point, or null.</returns>
    public static EveMine? ZoneAt(EveScene scene, int x, int y) => scene.Mines.FirstOrDefault(m => m.Area.Contains(x, y));

    /// <summary>Collects the field monsters close enough to the player to start a fight.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <returns>The scene's monster NPCs in range, possibly empty.</returns>
    private List<EveNpc> Surrounding(GameSession session, Character character)
    {
        var o = _options.Value;
        if (_maps.Scene(character.MapId) is not { } scene)
            return [];
        var now = GameClock.UtcNow;
        List<EveNpc> near = [];
        for (var i = 0; i < scene.Npcs.Count; i++)
        {
            var npc = scene.Npcs[i];
            var role = (ushort)(i + 1);
            if (_npcs[(ushort)npc.NpcId] is not { IsMonster: true }
                || !scene.EventsOf(npc).Any(e => e.IsFieldMonster)
                || _world.MonsterIsOut(character.MapId, role)
                || session.HiddenNpcs.TryGetValue((character.MapId, role), out var hidden) && hidden > now
                || session.NpcStates.TryGetValue(role, out var state) && state is 2 or 4)
                continue;
            var (halfX, halfY) = NpcWander.Roams(npc) ? (npc.Unknown688, npc.Unknown692) : (0, 0);
            var dx = Math.Max(0, Math.Abs(character.X - npc.X) - halfX);
            var dy = Math.Max(0, Math.Abs(character.Y - npc.Y) - halfY);
            if ((long)dx * dx + (long)dy * dy <= (long)o.FieldEncounterRadius * o.FieldEncounterRadius)
                near.Add(npc);
        }
        return near;
    }
}
