using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Features.Travel;

/// <summary>
/// The one rule every teleport follows (GPS, Magic Magnet, Teleport Scroll and Dragonflies, vehicle trips, activity teleports and the
/// Training Island): which maps may not be left or reached by teleport, and which states of the player keep them where they are. The client
/// checks the same (IsNoTeleportMapId, cannotTeleport, gpsRefusal, activityTeleportRefused), each path a part of it; this is their union. The
/// return spot keeps the client's own, narrower leave rule (<see cref="ReturnRefusal"/>) and this arrival rule.
/// </summary>
/// <param name="maps">The map data, for the scene flags, scene types and instance copies.</param>
/// <param name="options">The travel options, for the barred map list.</param>
public sealed class TravelRules(
    MapData maps,
    IOptions<TravelOptions> options)
{
    /// <summary>The scene flag bit (value 4) of the maps that may not be left or reached by teleport, nor left by the return spot.</summary>
    public const byte NoTeleportFlag = 4; // scene flag bit 4: no teleporting, no return

    /// <summary>The scene type of the sea scenes, reached only by submarine.</summary>
    public const byte SeaScene = 14; // scene type 14: water (sea) scene

    /// <summary>The map ids of the Prison and the Maze, which the client's return spot may not leave (returnToSpot).</summary>
    public const ushort Prison = 10004, Maze = 10009; // 10004 Prison, 10009 Maze

    /// <summary>The map ids of the Team PK floors, from which the client's return spot may not go to Carnie (chooseReturnSpot).</summary>
    public const ushort CapitolTeamPk = 12064, ActivityCentreTeamPk = 11073; // 12064 2F, the Capitol; 11073 2F, Activity Centre

    /// <summary>
    /// Tells whether no teleport may leave or reach a map: a map of the options' list, a scene with flag 4, a house, an instance copy, an event
    /// arena (the Challenge Rounds have no flag 4) or a castle map.
    /// </summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True when teleporting out of or into the map is barred.</returns>
    public bool IsBarred(int mapId) =>
        mapId is >= 0 and <= ushort.MaxValue && Array.IndexOf(options.Value.NoTeleportMaps, (ushort)mapId) >= 0
        || (maps.SceneFlags(mapId) & NoTeleportFlag) != 0
        || TentHandlers.IsHouseMap(mapId)
        || maps.InstanceOfMap(mapId) is not null
        || EventArenas.IsArena(mapId)
        || CastleRegistry.IsCastleMap(mapId)
        || CastleRegistry.IsTrojanRoom(mapId);

    /// <summary>Tells whether a map is a sea scene, by the scene of its source map for an instance copy.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True for a sea scene.</returns>
    public bool IsSea(int mapId) => maps.SceneType(maps.SourceMapId(mapId)) == SeaScene;

    /// <summary>
    /// Gives why the player's state keeps them from teleporting: a battle, watching one, an event, a game, a stall, a trade or fishing.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The reason, or null when nothing holds the player.</returns>
    public static string? BusyRefusal(GameSession session) =>
        session.Battle is not null ? "in a battle"
            : session.Watching is not null ? "watching a battle"
                : session.Event is not null ? "in an event"
                    : session.Minigame is not null ? "in a game"
                        : session.Stalls.Any ? "at a stall"
                            : session.Trades.Items is not null || session.Trades.Pets is not null ? "trading"
                                : session.Activities.Fishing is not null ? "fishing"
                                    : null;

    /// <summary>Gives why the player may not leave their map by teleport now: a busy state, or a map that cannot be left this way.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <returns>The reason, or null when they may leave.</returns>
    public string? LeaveRefusal(GameSession session, Character c) =>
        BusyRefusal(session) ?? (IsBarred(c.MapId) ? $"map {c.MapId} cannot be left this way" : null);

    /// <summary>
    /// Gives why the return spot (<c>5/17</c>) may not take the player off their map, by the client's own rules for it rather than the full
    /// leave rule (returnToSpot, chooseReturnSpot): a busy state, the Prison, the Maze or a scene with flag 4; and for Carnie, Carnie itself,
    /// the sea and the Team PK floors.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="toCarnie">True when the player chose Carnie.</param>
    /// <returns>The reason, or null when the return spot may go ahead (the arrival rule aside).</returns>
    public string? ReturnRefusal(GameSession session, Character c, bool toCarnie) =>
        BusyRefusal(session)
            ?? (c.MapId is Prison or Maze || (maps.SceneFlags(maps.SourceMapId(c.MapId)) & NoTeleportFlag) != 0
                ? $"map {c.MapId} cannot be left by the return spot"
                : toCarnie && (c.MapId is CarnieReturn.Carnie or CapitolTeamPk or ActivityCentreTeamPk || IsSea(c.MapId))
                    ? $"Carnie cannot be reached from map {c.MapId}"
                    : null);

    /// <summary>Gives why a teleport may not land on a map: a barred map or the sea.</summary>
    /// <param name="mapId">The map id of the destination.</param>
    /// <returns>The reason, or null when the map may be reached.</returns>
    public string? ArrivalRefusal(int mapId) =>
        IsBarred(mapId) || IsSea(mapId) ? $"map {mapId} cannot be reached this way" : null;

    /// <summary>Gives why the player may not teleport from their map to another: the leave rule, then the arrival rule.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="toMap">The map id of the destination.</param>
    /// <returns>The reason, or null when the teleport may go ahead.</returns>
    public string? TeleportRefusal(GameSession session, Character c, int toMap) => LeaveRefusal(session, c) ?? ArrivalRefusal(toMap);
}
