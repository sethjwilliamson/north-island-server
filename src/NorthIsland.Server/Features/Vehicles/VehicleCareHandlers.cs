using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.Features.Vehicles;

/// <summary>Looks after the wear and fuel of the ridden vehicle: fuel items, refuelling and the drain tick.</summary>
/// <param name="items">The item records.</param>
/// <param name="world">The online sessions, used to tell the map when a rider is put down.</param>
/// <param name="options">The vehicle options (drain rate, fuel per item).</param>
/// <param name="logger">Logger for the handler's messages.</param>
public sealed class VehicleCareHandlers(
    ItemData items,
    WorldState world,
    IOptions<VehicleFeatureOptions> options,
    ILogger<VehicleCareHandlers> logger)
{
    /// <summary>Gives the wear at which a vehicle is worn out.</summary>
    /// <param name="vehicle">The vehicle's item record.</param>
    /// <returns>The maximum wear.</returns>
    public static int MaxWear(ItemRecord vehicle) => (int)Math.Ceiling(Math.Pow(Math.Max(1, (int)vehicle.ShopColumn), 1.3)) * 1800;

    /// <summary>Gives the fuel of a full tank: the client draws the fuel bar against the most wear; the fuel field holds at most 65535.</summary>
    /// <param name="vehicle">The vehicle's item record.</param>
    /// <returns>The fuel of a full tank.</returns>
    public static int FullTank(ItemRecord vehicle) => Math.Min(ushort.MaxValue, MaxWear(vehicle));

    /// <summary>Sends the vehicle's wear and fuel to its owner.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="vehicle">The vehicle's bag stack.</param>
    public static void SendState(GameSession session, BagItem vehicle) =>
        session.Send(VehicleMessages.Wear(vehicle.Slot, session.Account!.LoginId, vehicle.Used, vehicle.Extra));

    /// <summary>Uses a fuel item from the bag on the ridden vehicle and takes one from the stack when it was accepted.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="stack">The bag stack of the item.</param>
    /// <param name="item">The item's record.</param>
    /// <returns>True when the item is a fuel item (even if the vehicle did not take it), so the caller stops handling it.</returns>
    public bool TryUseFuel(GameSession session, Character c, BagItem stack, ItemRecord item)
    {
        if (item.SubType != 121)
            return false;
        if (!Refuel(session, c, item.Id))
            return true;
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.UsedSound());
        return true;
    }

    /// <summary>Adds fuel to the ridden vehicle when its record names that fuel and the tank is not full.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="fuelItem">The id of the fuel item.</param>
    /// <returns>True when fuel was added.</returns>
    public bool Refuel(GameSession session, Character c, ushort fuelItem)
    {
        if (session.Vehicle is not { Riding: true } riding
            || riding.StackIn(c) is not { } vehicle
            || items[vehicle.ItemId] is not { } record
            || (record.Ints42.Length < 2 || (record.Ints42[0] != fuelItem && record.Ints42[1] != fuelItem)))
            return false;
        var max = FullTank(record);
        if (vehicle.Extra >= max)
            return false;
        vehicle.Extra = (ushort)Math.Min(max, vehicle.Extra + Math.Max(1, max * options.Value.Vehicles.FuelPercentPerItem / 100));
        SendState(session, vehicle);
        logger.LogInformation("[{Session}] {Name} refuels {Vehicle}: {Fuel}", session.Id, c.Name, record.Name.Trim(), vehicle.Extra);
        return true;
    }

    /// <summary>Where each rider was at the last drain tick, and on which boarding, so only riding that moves is drained.</summary>
    private readonly ConditionalWeakTable<GameSession, RideSpot> _spots = new();

    /// <summary>The rider's spot at the last drain tick.</summary>
    private sealed class RideSpot
    {
        /// <summary>The boarding the spot belongs to; a new boarding starts afresh.</summary>
        public PlacedVehicle? Ride { get; set; }

        /// <summary>The map and position at the last tick.</summary>
        public (ushort Map, ushort X, ushort Y) Spot { get; set; }
    }

    /// <summary>Tells whether a vehicle is never drained: Robinson's Raft, which the intro breaks when it lands (<see cref="VehicleHandlers.RobinsonsRaft"/>).</summary>
    /// <param name="itemId">The vehicle's item id.</param>
    /// <returns>True when riding it costs nothing.</returns>
    public static bool NeverDrains(ushort itemId) => itemId == VehicleHandlers.RobinsonsRaft;

    /// <summary>
    /// Drains the ridden vehicle for the seconds since the last tick when the rider moved: the fuel goes first, then the health (the wear rises).
    /// Sends the new wear and fuel (15/14). A vehicle whose wear reaches the most it can take is destroyed: the rider is put down (15/11) and the
    /// map is shown the wreck (15/15, which also gives the owner "The vehicle has been destroyed"); the vehicle stays in the bag, worn out, for repair.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="seconds">The seconds since the last tick.</param>
    public void Tick(GameSession session, double seconds)
    {
        var o = options.Value.Vehicles;
        if (o.DrainPerSecond <= 0 || session.ActiveCharacter is not { } c || session.Vehicle is not { Riding: true } riding
            || riding.StackIn(c) is not { } vehicle || items[vehicle.ItemId] is not { } record || NeverDrains(vehicle.ItemId))
            return;
        var spot = _spots.GetOrCreateValue(session);
        var here = (c.MapId, c.X, c.Y);
        var moved = ReferenceEquals(spot.Ride, riding) && spot.Spot != here;
        spot.Ride = riding;
        spot.Spot = here;
        if (!moved)
            return;
        var drain = Math.Max(1, (int)Math.Round(o.DrainPerSecond * seconds));
        var fromFuel = Math.Min((int)vehicle.Extra, drain);
        vehicle.Extra = (ushort)(vehicle.Extra - fromFuel);
        var max = MaxWear(record);
        vehicle.Used = Math.Min(max, vehicle.Used + drain - fromFuel);
        SendState(session, vehicle);
        if (vehicle.Used < max)
            return;
        var id = session.Account!.LoginId;
        session.Vehicle = null;
        _spots.Remove(session);
        var off = VehicleMessages.Dismounted(vehicle.Slot, id);
        session.Send(off);
        world.Broadcast(session, off, sameMapOnly: true);
        world.ClearPassengers(session);
        var wreck = VehicleMessages.Destroyed(id, vehicle.ItemId);
        session.Send(wreck);
        world.Broadcast(session, wreck, sameMapOnly: true);
        logger.LogInformation("[{Session}] {Name}'s {Vehicle} is destroyed: worn out", session.Id, c.Name, record.Name.Trim());
    }
}
