namespace NorthIsland.Server.World;

/// <summary>
/// Something on the ground or picked up from it: an item stack or an amount of gold. A dropped stack keeps every attribute of the bag stack it
/// came from, so picking it up gives back the very stack (a half-used potion stays half used).
/// </summary>
/// <param name="ItemId">The item id.</param>
/// <param name="Count">How many items.</param>
/// <param name="Gold">The gold amount, 0 for an item.</param>
/// <param name="Wear">The wear value of the item.</param>
/// <param name="Owner">The login id of the only player who may pick it up (a dropped item that cannot be transferred), 0 for anyone.</param>
/// <param name="Insert">The id of the magic crystal stone set into the item, 0 for none.</param>
/// <param name="Fusion">The fusion level of the item.</param>
/// <param name="Pose">The item id of the pose look of the item, 0 for none.</param>
/// <param name="BoxItem">The item id of the box the item came in, 0 for none.</param>
/// <param name="Used">The used amount of the item.</param>
/// <param name="Extra">The extra amount of the item.</param>
public sealed record GroundThing(
    ushort ItemId,
    int Count,
    int Gold,
    byte Wear = 1,
    int Owner = 0,
    ushort Insert = 0,
    byte Fusion = 0,
    ushort Pose = 0,
    ushort BoxItem = 0,
    int Used = 0,
    ushort Extra = 0)
{
    /// <summary>Whether the item carries an attribute beyond its wear, which the plain "obtained" packet (23/6) cannot tell the client.</summary>
    public bool HasAttributes => Insert != 0 || Fusion != 0 || Pose != 0 || BoxItem != 0 || Used != 0 || Extra != 0;
}
