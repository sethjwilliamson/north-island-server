using NorthIsland.Protocol.IO;

namespace NorthIsland.Protocol.Messages.Items;

/// <summary>One item stack in the bank as sent in the bank packets.</summary>
/// <param name="Slot">Bank slot of the stack.</param>
/// <param name="ItemId">Item id.</param>
/// <param name="Count">Number of items in the stack.</param>
/// <param name="Wear">Wear value of the item.</param>
/// <param name="Insert">Id of the magic crystal stone set in the item, 0 for none.</param>
/// <param name="Fusion">Fusion level of the item.</param>
/// <param name="Pose">Item id of the pose look applied to the item, 0 for none.</param>
/// <param name="BoxItem">Item id of the box the item came in, 0 for none.</param>
/// <param name="Used">How much of the item is used up, as the bag keeps it.</param>
/// <param name="Extra">The extra amount of the item, as the bag keeps it.</param>
public sealed record BankEntry(
    byte Slot,
    ushort ItemId,
    byte Count,
    byte Wear,
    ushort Insert = 0,
    byte Fusion = 0,
    ushort Pose = 0,
    ushort BoxItem = 0,
    int Used = 0,
    ushort Extra = 0)
{
    /// <summary>Appends the 29-byte record to a packet.</summary>
    /// <param name="w">The packet being built.</param>
    public void WriteTo(PacketWriter w) =>
        w.U8(Slot).U16(ItemId).U8(Count).U8(Wear).U8(0).U8(0).U16(Insert).F64(0).U8(Fusion).U8(0).U16(Pose).U16(BoxItem).I32(Used).U16(Extra);
}
