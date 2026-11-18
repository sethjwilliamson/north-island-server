using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Protocol.Messages.Items;

/// <summary>Builders for the ground item packets (action 23).</summary>
public static class GroundMessages
{
    /// <summary>Builds the packet with every entry lying on the ground.</summary>
    /// <param name="entries">The items and gold piles.</param>
    /// <returns>The packet.</returns>
    public static byte[] List(IEnumerable<GroundEntry> entries)
    {
        var w = new PacketWriter(Action23.Code, Action23.GroundList);
        foreach (var e in entries)
        {
            if (e.Gold > 0)
                w.U8(2).U16(e.Index).I32(e.Gold).U16(e.X).U16(e.Y).I32(0);
            else
                w.U8(1).U16(e.Index).I32(e.ItemId).U16(e.X).U16(e.Y).I32(e.Extra);
        }
        return w.ToArray();
    }

    /// <summary>Builds the packet that adds one item to the ground.</summary>
    /// <param name="itemId">Id of the item.</param>
    /// <param name="x">X position.</param>
    /// <param name="y">Y position.</param>
    /// <param name="dropped">True to play the drop animation.</param>
    /// <param name="extra">Pixels added to Y when the client sorts the item among characters and map objects.</param>
    /// <returns>The packet.</returns>
    public static byte[] Add(ushort itemId, ushort x, ushort y, bool dropped = false, int extra = 0) =>
        new PacketWriter(Action23.Code, Action23.GroundAdd).U16(itemId).U16(x).U16(y).I32(extra).U8(dropped ? (byte)1 : (byte)0).ToArray();

    /// <summary>Builds the packet that adds a gold pile to the ground.</summary>
    /// <param name="gold">Amount of gold.</param>
    /// <param name="x">X position.</param>
    /// <param name="y">Y position.</param>
    /// <param name="dropped">True to play the drop animation.</param>
    /// <returns>The packet.</returns>
    public static byte[] AddGold(int gold, ushort x, ushort y, bool dropped = false)
    {
        var w = new PacketWriter(Action23.Code, Action23.GroundMoneyAdd).I32(gold).U16(x).U16(y);
        if (dropped)
            w.U8(1);
        return w.ToArray();
    }

    /// <summary>Builds the packet that removes an entry from the ground.</summary>
    /// <param name="index">Index of the entry.</param>
    /// <param name="animate">True to play the pick-up animation.</param>
    /// <returns>The packet.</returns>
    public static byte[] Remove(ushort index, bool animate)
    {
        var w = new PacketWriter(Action23.Code, Action23.GroundRemove).U16(index);
        if (animate)
            w.U8(1);
        return w.ToArray();
    }

    /// <summary>Builds the packet that tells the player an item was obtained.</summary>
    /// <param name="itemId">Id of the item.</param>
    /// <param name="count">Number of items.</param>
    /// <param name="wear">Wear value of the item.</param>
    /// <returns>The packet.</returns>
    public static byte[] ItemObtained(ushort itemId, ushort count, byte wear = 1) =>
        new PacketWriter(Action23.Code, Action23.ItemObtained)
            .U16(itemId).U16(count).U8(wear).U8(0).U8(0).I32(0).U16(0).U16(0).F64(0).U8(0).U8(0).U16(0).U16(0).ToArray();
}
