using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Protocol.Messages.World;

/// <summary>Builders for packets about life in a house: furniture use, training, cupboards, door plates, boxes, music and sandglasses.</summary>
public static class HouseLifeMessages
{
    /// <summary>Builds the <c>23</c> packet that shows a player bathing at a piece of furniture.</summary>
    /// <param name="humId">Id of the bathing player.</param>
    /// <param name="furnIndex">Index of the furniture piece.</param>
    /// <param name="seat">Seat number at the furniture.</param>
    /// <returns>The packet.</returns>
    public static byte[] Bathing(int humId, ushort furnIndex, byte seat) =>
        new PacketWriter(Action23.Code, Action23.Bathing).I32(humId).U16(furnIndex).U8(seat).ToArray();

    /// <summary>Builds the packet that ends a bath.</summary>
    /// <param name="humId">Id of the player who stops bathing.</param>
    /// <param name="seat">Seat number the player used.</param>
    /// <returns>The packet.</returns>
    public static byte[] BatheEnd(int humId, byte seat) =>
        new PacketWriter(Action23.Code, Action23.BatheEnd).I32(humId).U8(seat).ToArray();

    /// <summary>Builds the packet that shows a player using a piece of furniture.</summary>
    /// <param name="humId">Id of the player.</param>
    /// <param name="furnIndex">Index of the furniture piece.</param>
    /// <param name="seat">Seat number at the furniture.</param>
    /// <returns>The packet.</returns>
    public static byte[] FurnitureUsed(int humId, ushort furnIndex, byte seat) =>
        new PacketWriter(Action23.Code, Action23.FurnitureUsed).I32(humId).U16(furnIndex).U8(seat).ToArray();

    /// <summary>Builds the packet that shows a player leaving the furniture they used.</summary>
    /// <param name="humId">Id of the player.</param>
    /// <returns>The packet.</returns>
    public static byte[] FurnitureLeft(int humId) => new PacketWriter(Action23.Code, Action23.FurnitureLeft).I32(humId).ToArray();

    /// <summary>Builds the <c>23/162</c> packet (type 1) that ends a training.</summary>
    /// <param name="humId">Id of the player.</param>
    /// <param name="flag">1 when the player finished, 2 when the pet finished.</param>
    /// <returns>The packet.</returns>
    public static byte[] TrainingOver(int humId, byte flag) =>
        new PacketWriter(Action23.Code, Action23.Training).U8(1).I32(humId).U8(flag).ToArray();

    /// <summary>Builds the <c>23/162</c> packet (type 2) with the training progress of the player.</summary>
    /// <param name="percent">Progress in percent.</param>
    /// <param name="expUp">True when the training just raised the experience.</param>
    /// <returns>The packet.</returns>
    public static byte[] TrainingPercent(byte percent, bool expUp) =>
        new PacketWriter(Action23.Code, Action23.Training).U8(2).U8(percent).U8((byte)(expUp ? 1 : 0)).ToArray();

    /// <summary>Builds the <c>23/162</c> packet (type 3) with the training progress of a pet.</summary>
    /// <param name="percent">Progress in percent.</param>
    /// <param name="petSlot">Slot of the pet.</param>
    /// <param name="expUp">True when the training just raised the experience.</param>
    /// <returns>The packet.</returns>
    public static byte[] PetTrainingPercent(byte percent, byte petSlot, bool expUp) =>
        new PacketWriter(Action23.Code, Action23.Training).U8(3).U8(percent).U8(petSlot).U8((byte)(expUp ? 1 : 0)).ToArray();

    /// <summary>Builds the <c>23/162</c> packet (type 4) that resets the training display of the player.</summary>
    /// <returns>The packet.</returns>
    public static byte[] TrainingReset() => new PacketWriter(Action23.Code, Action23.Training).U8(4).ToArray();

    /// <summary>Builds the <c>23/162</c> packet (type 5) that resets the training display of a pet.</summary>
    /// <param name="petSlot">Slot of the pet.</param>
    /// <returns>The packet.</returns>
    public static byte[] PetTrainingReset(byte petSlot) => new PacketWriter(Action23.Code, Action23.Training).U8(5).U8(0).U8(petSlot).ToArray();

    /// <summary>Builds the 50-byte item record used in cupboard, car case and stall packets.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="count">Stack count.</param>
    /// <param name="wear">Wear value.</param>
    /// <param name="insert">Id of the inserted stone.</param>
    /// <param name="fusion">Fusion byte.</param>
    /// <param name="pose">Pose item id.</param>
    /// <param name="boxItem">Item id of the box content.</param>
    /// <param name="used">Used amount.</param>
    /// <param name="extra">Extra value.</param>
    /// <param name="price">Unit price of a stall item (the client echoes it back when buying).</param>
    /// <returns>The 50-byte record.</returns>
    public static byte[] CarItem(
        ushort itemId,
        byte count,
        byte wear,
        ushort insert,
        byte fusion = 0,
        ushort pose = 0,
        ushort boxItem = 0,
        int used = 0,
        ushort extra = 0,
        int price = 0)
    {
        var b = new byte[50];
        BitConverter.TryWriteBytes(b.AsSpan(6), price);
        BitConverter.TryWriteBytes(b.AsSpan(13), used);
        BitConverter.TryWriteBytes(b.AsSpan(24), extra);
        b[1] = (byte)itemId;
        b[2] = (byte)(itemId >> 8);
        b[3] = count;
        b[4] = wear;
        b[26] = (byte)insert;
        b[27] = (byte)(insert >> 8);
        b[36] = fusion;
        b[38] = (byte)pose;
        b[39] = (byte)(pose >> 8);
        b[21] = (byte)boxItem;
        b[22] = (byte)(boxItem >> 8);
        return b;
    }

    /// <summary>Builds the <c>62</c> packet that puts an item into a cupboard slot.</summary>
    /// <param name="furnIndex">Index of the cupboard.</param>
    /// <param name="slot">Slot in the cupboard.</param>
    /// <param name="id">Entry id of the stored item.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="count">Stack count.</param>
    /// <param name="wear">Wear value.</param>
    /// <param name="insert">Id of the inserted stone.</param>
    /// <param name="fusion">Fusion byte.</param>
    /// <param name="pose">Pose item id.</param>
    /// <param name="boxItem">Item id of the box content.</param>
    /// <returns>The packet.</returns>
    public static byte[] CupboardAdd(
        ushort furnIndex,
        byte slot,
        byte id,
        ushort itemId,
        byte count,
        byte wear,
        ushort insert,
        byte fusion = 0,
        ushort pose = 0,
        ushort boxItem = 0) =>
            new PacketWriter(Action62.Code, Action62.CupboardStore)
                .U16(furnIndex)
                .U8(slot)
                .U8(id)
                .Bytes(CarItem(itemId, count, wear, insert, fusion, pose, boxItem))
                .ToArray();

    /// <summary>Builds the <c>62</c> packet that takes an item out of a cupboard slot.</summary>
    /// <param name="furnIndex">Index of the cupboard.</param>
    /// <param name="slot">Slot in the cupboard.</param>
    /// <param name="id">Entry id of the item.</param>
    /// <returns>The packet.</returns>
    public static byte[] CupboardRemove(ushort furnIndex, byte slot, byte id) =>
        new PacketWriter(Action62.Code, Action62.CupboardTake).U16(furnIndex).U8(slot).U8(id).ToArray();

    /// <summary>Builds the packet that shows a door plate text for a house owner.</summary>
    /// <param name="humId">Id of the house owner.</param>
    /// <param name="text">The door plate text.</param>
    /// <returns>The packet.</returns>
    public static byte[] Doorplate(int humId, string text)
    {
        var bytes = NorthIsland.Protocol.Text.GameEncoding.GetBytes(text);
        return new PacketWriter(Action62.Code, Action62.Doorplate).I32(humId).U8((byte)bytes.Length).Bytes(bytes).ToArray();
    }

    /// <summary>Builds the packet that removes the door plate of a house owner.</summary>
    /// <param name="humId">Id of the house owner.</param>
    /// <returns>The packet.</returns>
    public static byte[] DoorplateClear(int humId) => new PacketWriter(Action62.Code, Action62.DoorplateClear).I32(humId).ToArray();

    /// <summary>Builds the packet that shows an item in a slot of a house box.</summary>
    /// <param name="kind">Box kind.</param>
    /// <param name="slot">Slot in the box.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="count">Stack count.</param>
    /// <returns>The packet.</returns>
    public static byte[] BoxSlot(byte kind, byte slot, ushort itemId, byte count) =>
        new PacketWriter(Action62.Code, Action62.BoxUpdate).U8(KindOpt(kind, put: true)).U8(slot).U16(itemId).U8(count).ToArray();

    /// <summary>Builds the packet that empties a slot of a house box.</summary>
    /// <param name="kind">Box kind.</param>
    /// <param name="slot">Slot in the box.</param>
    /// <returns>The packet.</returns>
    public static byte[] BoxSlotCleared(byte kind, byte slot) =>
        new PacketWriter(Action62.Code, Action62.BoxUpdate).U8(KindOpt(kind, put: false)).U8(slot).ToArray();

    /// <summary>Maps a box kind to the option byte of the box update packet; odd values put an item, even values clear the slot.</summary>
    /// <param name="kind">Box kind.</param>
    /// <param name="put">True for the put option, false for the clear option.</param>
    /// <returns>The option byte.</returns>
    public static byte KindOpt(byte kind, bool put) => (byte)((kind switch
    {
        4 => 9, // box kind 4: option 9
        5 => 7, // box kind 5: option 7
        _ => 2 * kind - 1 // any other kind: option 2 * kind - 1
    }) + (put ? 0 : 1));

    /// <summary>Builds the <c>62/59</c> packet that makes the house play a song.</summary>
    /// <param name="index">Song index (0 for none).</param>
    /// <param name="file">File name of the song.</param>
    /// <returns>The packet.</returns>
    public static byte[] HouseMusic(int index, string file) =>
        new PacketWriter(Action62.Code, Action62.HouseMusic).U8(1).I32(index).RawString(file).ToArray();

    /// <summary>Builds the <c>62/59</c> packet that asks the owner's client for the house song it has saved; it answers with <c>62/61</c>.</summary>
    /// <returns>The packet.</returns>
    public static byte[] HouseMusicAsk() => new PacketWriter(Action62.Code, Action62.HouseMusic).U8(2).ToArray();

    /// <summary>Builds the <c>23</c> packet that plays one piano note.</summary>
    /// <param name="note">The note played.</param>
    /// <returns>The packet.</returns>
    public static byte[] PianoNote(byte note) => new PacketWriter(Action23.Code, Action23.PianoNote).U8(note).ToArray();

    /// <summary>Builds the <c>62/53</c> packet (type 1) that reports the use of a sandglass.</summary>
    /// <param name="furnIndex">Index of the furniture piece.</param>
    /// <param name="value">The sandglass value for that piece.</param>
    /// <returns>The packet.</returns>
    public static byte[] SandglassUsed(byte furnIndex, byte value) =>
        new PacketWriter(Action62.Code, Action62.Sandglass).U8(1).U8(furnIndex).U8(value).ToArray();

    /// <summary>Builds the <c>62/53</c> packet (type 2) with the sandglass flags.</summary>
    /// <param name="flags">Flag bits, one per job slot, plus bit 32 for a use no job has taken yet.</param>
    /// <returns>The packet.</returns>
    public static byte[] SandglassFlags(byte flags) => new PacketWriter(Action62.Code, Action62.Sandglass).U8(2).U8(flags).ToArray();
}
