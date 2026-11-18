namespace NorthIsland.Server.Features.Travel;

/// <summary>
/// Remembers where a player came to Carnie from and sends them back there through the Carnie exit. Carnie (map 11094) is reached with the
/// return spot choice 3 (5/17) or through its entrance hall (map 11095), whose door 1 (convey 1, to map 60012) is the only way out in the
/// data (eve.Emg: Carnie's door 1 leads to the hall, the hall's door 2 back to Carnie).
/// </summary>
public static class CarnieReturn
{
    /// <summary>The map id of Carnie.</summary>
    public const ushort Carnie = 11094; // Carnie

    /// <summary>The map id of Carnie's entrance hall, between Carnie and map 60012.</summary>
    public const ushort EntranceHall = 11095; // Carnie entrance hall

    /// <summary>Returns whether a map belongs to Carnie: Carnie itself or its entrance hall.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True for Carnie and its entrance hall.</returns>
    public static bool IsCarnie(int mapId) => mapId is Carnie or EntranceHall;

    /// <summary>
    /// Runs before every map change: stores the player's map and spot when they come to Carnie from elsewhere, and forgets them when they
    /// leave Carnie.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="warp">The destination of the map change.</param>
    public static void Remember(GameSession session, DoorWarp warp)
    {
        if (session.ActiveCharacter is not { } character || IsCarnie(warp.MapId) == IsCarnie(character.MapId))
            return;
        // nothing is kept when the player walks into the entrance hall through its front door (the exit leads back there anyway, and the
        // spot is on the door) or comes from a house room (its room details cannot be restored): the exit then keeps the data's destination
        (character.CarnieReturnMapId, character.CarnieReturnX, character.CarnieReturnY) = warp.MapId == Carnie && session.HouseRoom == 0
            ? (character.MapId, character.X, character.Y)
            : ((ushort)0, (ushort)0, (ushort)0);
    }

    /// <summary>Returns where a warp out of the entrance hall to outside Carnie takes the player back to, if a place was remembered.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="fromMap">The map the warp starts on.</param>
    /// <param name="target">The map the data's convey leads to.</param>
    /// <returns>The warp back to the remembered place; null when the warp is not the Carnie exit or nothing was remembered.</returns>
    public static DoorWarp? Exit(Character character, int fromMap, ushort target) =>
        fromMap == EntranceHall && !IsCarnie(target) && character.CarnieReturnMapId != 0
            ? new DoorWarp(character.CarnieReturnMapId, character.CarnieReturnX, character.CarnieReturnY, 0)
            : null;
}
