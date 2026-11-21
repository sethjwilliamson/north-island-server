using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.Features.Vehicles;

/// <summary>Handles placing a vehicle, boarding, getting off and taking it back.</summary>
/// <param name="items">The item records.</param>
/// <param name="world">The online sessions, used to tell the others on the map.</param>
/// <param name="maps">The map data, for the scene types that take no vehicle.</param>
/// <param name="options">The vehicle options, for the maps and scene types that take no vehicle.</param>
/// <param name="logger">Logger for the handler's messages.</param>
public sealed class VehicleHandlers(
    ItemData items,
    WorldState world,
    MapData maps,
    IOptions<VehicleFeatureOptions> options,
    ILogger<VehicleHandlers> logger)
{
    /// <summary>
    /// Gives why a vehicle may not be placed or boarded where the player stands (the client's placingRefusal): in or watching a battle, in a
    /// house, in a Quiz Zone, on a map or scene type (that of its source map for an instance copy) that takes no vehicle, or when the vehicle
    /// is worn out (its wear has reached the most it can take; it must be repaired first).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="vehicle">The vehicle's bag stack, or null when it is no longer in the bag.</param>
    /// <returns>The reason, or null when the vehicle may be used.</returns>
    private string? UseRefusal(GameSession session, Character c, BagItem? vehicle)
    {
        var o = options.Value.Vehicles;
        var sceneType = (int)maps.SceneType(maps.SourceMapId(c.MapId));
        return session.Battle is not null || session.Watching is not null ? "in a battle"
            : TentHandlers.IsHouseMap(c.MapId) ? "in a house"
                : EventArenas.IsQuizMap(c.MapId) || Array.IndexOf(o.NoVehicleMaps, c.MapId) >= 0
                    || Array.IndexOf(o.NoVehicleSceneTypes, sceneType) >= 0 ? $"no vehicles on map {c.MapId}"
                    : vehicle is null || items[vehicle.ItemId] is not { } record ? "not in the bag"
                        : vehicle.Used >= VehicleCareHandlers.MaxWear(record) ? "worn out"
                            : null;
    }

    /// <summary>Logs that a client saw a vehicle wreck to the end; nothing is answered.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The login id of the wreck's owner (i32).</param>
    internal ValueTask OnWreckSeen(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (body.Length >= 4)
            logger.LogDebug(
                "[{Session}] the wreck of {Owner} was seen to the end",
                session.Id,
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Span));
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles a click on the vehicle: a rider gets off, a placed vehicle is taken back, and the map is told.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnClick(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (VehicleItemOf(session, body) is not { } request)
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var riding = session.Vehicle is not { Riding: false };
        if (riding && BreakRaftOnLanding(session, request.BagSlot, request.ItemId))
            return ValueTask.CompletedTask;
        session.Vehicle = null;
        if (riding)
        {
            var packet = VehicleMessages.Dismounted(request.BagSlot, id);
            session.Send(packet);
            world.Broadcast(session, packet, sameMapOnly: true);
            world.ClearPassengers(session);
        }
        else
        {
            session.Send(VehicleMessages.TakenBack(request.BagSlot));
            world.Broadcast(session, VehicleMessages.Removed(id, request.ItemId), sameMapOnly: true);
        }
        logger.LogInformation("[{Session}] {Name} {Action} {Item}", session.Id, session.ActiveCharacter!.Name,
            riding ? "gets off" : "takes back", request.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Places the vehicle on the map at the player's position, boarding it at once when asked, and replaces an earlier placed vehicle.
    /// Refused (nothing is answered, so the client places nothing) where <see cref="UseRefusal"/> bars it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    /// <param name="board">True to board the vehicle as well.</param>
    internal ValueTask OnPlace(GameSession session, ReadOnlyMemory<byte> body, bool board)
    {
        if (VehicleItemOf(session, body) is not { } request)
            return ValueTask.CompletedTask;
        var character = session.ActiveCharacter!;
        var id = session.Account!.LoginId;
        if (UseRefusal(session, character, character.Bag.Find(b => b.Slot == request.BagSlot)) is { } refusal)
        {
            logger.LogWarning("[{Session}] {Name}: place {Item} refused: {Why}", session.Id, character.Name, request.ItemId, refusal);
            return ValueTask.CompletedTask;
        }
        if (session.Vehicle is { } old)
            world.Broadcast(session, VehicleMessages.Dismounted(old.BagSlot, id), sameMapOnly: true);

        session.Vehicle = new PlacedVehicle(request.BagSlot, request.ItemId, character.MapId, character.X, character.Y) { Riding = board };
        var placed = VehicleMessages.Placed(request.BagSlot, id, request.ItemId, character.X, character.Y, board);
        session.Send(placed);
        world.Broadcast(session, VehicleMessages.Placed(request.BagSlot, id, request.ItemId, character.X, character.Y), sameMapOnly: true);
        if (board)
        {
            world.Broadcast(session, VehicleMessages.Boarded(request.BagSlot, id, request.ItemId), sameMapOnly: true);
            if (character.Bag.Find(b => b.Slot == request.BagSlot) is { } boarded)
                VehicleCareHandlers.SendState(session, boarded);
        }
        logger.LogInformation("[{Session}] {Name} {Action} {Item} ({ItemName}) at ({X}, {Y})", session.Id, character.Name,
            board ? "places and boards" : "places", request.ItemId, items[request.ItemId]?.Name, character.X, character.Y);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Boards the vehicle the player has already placed on the map they stand on. Refused (nothing is answered) where
    /// <see cref="UseRefusal"/> bars it: a worn-out vehicle cannot be boarded again by placing it and boarding it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnBoard(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (VehicleItemOf(session, body) is not { } request)
            return ValueTask.CompletedTask;
        var character = session.ActiveCharacter!;
        if (session.Vehicle is not { } vehicle || vehicle.ItemId != request.ItemId || vehicle.MapId != character.MapId)
        {
            logger.LogWarning("[{Session}] board {Item}: not placed by this player on this map", session.Id, request.ItemId);
            return ValueTask.CompletedTask;
        }
        if (UseRefusal(session, character, vehicle.StackIn(character)) is { } refusal)
        {
            logger.LogWarning("[{Session}] {Name}: board {Item} refused: {Why}", session.Id, character.Name, request.ItemId, refusal);
            return ValueTask.CompletedTask;
        }
        vehicle.Riding = true;
        var packet = VehicleMessages.Boarded(vehicle.BagSlot, session.Account!.LoginId, vehicle.ItemId);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
        if (character.Bag.Find(b => b.Slot == vehicle.BagSlot) is { } boarded)
            VehicleCareHandlers.SendState(session, boarded);
        logger.LogInformation("[{Session}] {Name} boards {Item}", session.Id, character.Name, vehicle.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Gets the player off their vehicle and tells the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnPark(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (VehicleItemOf(session, body) is not { } request)
            return ValueTask.CompletedTask;
        var bagSlot = session.Vehicle?.BagSlot ?? request.BagSlot;
        if (BreakRaftOnLanding(session, bagSlot, request.ItemId))
            return ValueTask.CompletedTask;
        session.Vehicle = null;
        var packet = VehicleMessages.Dismounted(bagSlot, session.Account!.LoginId);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
        world.ClearPassengers(session);
        logger.LogInformation("[{Session}] {Name} gets off {Item}", session.Id, session.ActiveCharacter!.Name, request.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Item id of Robinson's Raft.</summary>
    public const ushort RobinsonsRaft = 48016; // Robinson's Raft

    /// <summary>Breaks Robinson's Raft when it lands at the starter's beach: the rider is put down, the wreck is shown and the raft leaves the bag.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="bagSlot">The bag slot of the vehicle.</param>
    /// <param name="itemId">The item id of the vehicle.</param>
    /// <returns>True when the raft broke.</returns>
    private bool BreakRaftOnLanding(GameSession session, byte bagSlot, ushort itemId)
    {
        if (itemId != RobinsonsRaft || session.ActiveCharacter is not { } character || character.MapId != WorldHandlers.StartersBeach.MapId)
            return false;
        var id = session.Account!.LoginId;
        session.Vehicle = null;
        var off = VehicleMessages.Dismounted(bagSlot, id);
        session.Send(off);
        world.Broadcast(session, off, sameMapOnly: true);
        world.ClearPassengers(session);
        var wreck = VehicleMessages.Destroyed(id, RobinsonsRaft);
        session.Send(wreck);
        world.Broadcast(session, wreck, sameMapOnly: true);
        if (character.Bag.Find(b => b.Slot == bagSlot && b.ItemId == RobinsonsRaft) is { } stack)
        {
            character.Bag.Remove(stack);
            session.Send(Protocol.Messages.Items.ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
        }
        logger.LogInformation("[{Session}] {Name} lands on the starter's beach: Robinson's Raft breaks", session.Id, character.Name);
        return true;
    }

    /// <summary>Reads a vehicle request and checks that the named bag slot holds that vehicle.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    /// <returns>The request, or null when the player is not in the world or the slot does not hold that vehicle.</returns>
    private VehicleRequest? VehicleItemOf(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is null || body.Length < 3)
            return null;
        var request = VehicleRequest.Parse(body.Span);
        var slot = request.BagSlot;
        var item = session.ActiveCharacter.Bag.Find(b => b.Slot == slot);
        if (item is null || item.ItemId != request.ItemId || items[request.ItemId]?.EquipSlot != 7)
        {
            logger.LogWarning("[{Session}] vehicle request for {Item} in bag slot {Slot}: no such vehicle there", session.Id, request.ItemId, slot);
            return null;
        }
        return request;
    }
}
