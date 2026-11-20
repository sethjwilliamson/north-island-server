namespace NorthIsland.Server.Features.Vehicles;

/// <summary>A vehicle a player has placed on the map, whether or not they have boarded it.</summary>
/// <param name="BagSlot">The bag slot of the vehicle item.</param>
/// <param name="ItemId">The item id of the vehicle.</param>
/// <param name="MapId">The map it was placed on.</param>
/// <param name="X">The x position it was placed at.</param>
/// <param name="Y">The y position it was placed at.</param>
public sealed record PlacedVehicle(
    byte BagSlot,
    ushort ItemId,
    ushort MapId,
    int X,
    int Y)
{
    /// <summary>Tells whether an item id is the Robot or its capsule.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>True for the Robot.</returns>
    public static bool IsRobot(ushort itemId) => itemId is 48014 or 48032; // 48014 Robot, 48032 Robot Capsule

    /// <summary>
    /// Finds the vehicle's stack in the owner's bag: the stack at its bag slot, when that still holds the vehicle. Null once the vehicle has
    /// left that slot, so that nothing else that comes to the slot is drained, fuelled or flown as if it were the vehicle.
    /// </summary>
    /// <param name="c">The owner's character.</param>
    /// <returns>The stack, or null.</returns>
    public BagItem? StackIn(Character c) => c.Bag.Find(b => b.Slot == BagSlot && b.ItemId == ItemId);

    /// <summary>True once the owner has boarded the vehicle.</summary>
    public bool Riding { get; set; }
}
