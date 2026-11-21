using System.Buffers.Binary;
using NorthIsland.GameData.Events;

namespace NorthIsland.GameData.Npcs;

/// <summary>The NPCs and monsters of Npc.Dat, looked up by id.</summary>
public sealed class NpcTable
{
    /// <summary>Size of one Npc.Dat record in bytes.</summary>
    public const int RecordSize = 148;

    /// <summary>The NPCs by id.</summary>
    private readonly Dictionary<ushort, NpcRecord> _byId;

    /// <summary>Creates the table.</summary>
    /// <param name="byId">The NPCs by id.</param>
    private NpcTable(
        Dictionary<ushort, NpcRecord> byId) => _byId = byId;

    /// <summary>Number of NPCs.</summary>
    public int Count => _byId.Count;

    /// <summary>Looks up an NPC; null when the id is unknown.</summary>
    /// <param name="id">The NPC id.</param>
    public NpcRecord? this[ushort id] => _byId.GetValueOrDefault(id);

    /// <summary>All NPCs.</summary>
    public IEnumerable<NpcRecord> All => _byId.Values;

    /// <summary>Returns the type of an NPC.</summary>
    /// <param name="npcId">The NPC id.</param>
    /// <returns>The type, or null when the id is unknown.</returns>
    public byte? TypeOf(ushort npcId) => this[npcId]?.Type;

    /// <summary>Tells whether an NPC is a scene object (type 6, 9 or 10).</summary>
    /// <param name="npcId">The NPC id.</param>
    /// <returns>True when it is a scene object.</returns>
    public bool IsSceneObject(ushort npcId) => TypeOf(npcId) is 6 or 9 or 10;

    /// <summary>Reads and parses an Npc.Dat file.</summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>The parsed table.</returns>
    public static NpcTable Open(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Parses the contents of an Npc.Dat file; the first record is a header and is skipped.</summary>
    /// <param name="data">The raw file contents.</param>
    /// <returns>The parsed table.</returns>
    public static NpcTable Parse(ReadOnlySpan<byte> data)
    {
        var byId = new Dictionary<ushort, NpcRecord>();
        for (var offset = RecordSize; offset + RecordSize <= data.Length; offset += RecordSize)
        {
            var r = data.Slice(offset, RecordSize);
            var id = U16(r, 22);
            byId.TryAdd(id, new NpcRecord
            {
                Id = id,
                Name = Name(r),
                Type = U8(r, 21),
                Level = U8(r, 47),
                Hp = I32(r, 48),
                Sp = I32(r, 52),
                Strength = U16(r, 56),
                Constitution = U16(r, 58),
                Intelligence = U16(r, 60),
                Wisdom = U16(r, 62),
                Agility = U16(r, 64),
                Element = U8(r, 67),
                Skills = NonZero(r, 68, 3),
                Items = NonZero(r, 74, 5),
                SkillSlots = [U16(r, 68), U16(r, 70), U16(r, 72)],
                Uncatchable = U8(r, 44) == 1 && id is not (>= 17767 and <= 17770), // 17767 Butterfly, 17770 Butterfly
                NoPk = U8(r, 110) == 1,
                GrowKey = U8(r, 109),
                Key = U16(r, 100),
                DirectionKind = U8(r, 93), // record offset 93: direction kind
                Weapon = U8(r, 96), // record offset 96: weapon flag
                FixedSkill = U16(r, 106), // record offset 106: fixed skill
                Mercenary = U16(r, 120), // record offset 120: mercenary flag
            });
        }
        return new NpcTable(byId);
    }

    /// <summary>Reads consecutive words and keeps the non-zero ones.</summary>
    /// <param name="r">The record.</param>
    /// <param name="at">Byte offset of the first word.</param>
    /// <param name="count">Number of words.</param>
    /// <returns>The non-zero values in order.</returns>
    private static List<ushort> NonZero(ReadOnlySpan<byte> r, int at, int count)
    {
        var values = new List<ushort>(count);
        for (var i = 0; i < count; i++)
        {
            var v = U16(r, at + 2 * i);
            if (v != 0)
                values.Add(v);
        }
        return values;
    }

    /// <summary>Decodes an obfuscated byte.</summary>
    /// <param name="r">The record.</param>
    /// <param name="at">Byte offset in the record.</param>
    /// <returns>The decoded byte.</returns>
    private static byte U8(ReadOnlySpan<byte> r, int at) => (byte)((r[at] ^ 200) - 1);

    /// <summary>Decodes an obfuscated little-endian word.</summary>
    /// <param name="r">The record.</param>
    /// <param name="at">Byte offset in the record.</param>
    /// <returns>The decoded word.</returns>
    private static ushort U16(ReadOnlySpan<byte> r, int at) => (ushort)((BinaryPrimitives.ReadUInt16LittleEndian(r[at..]) ^ 21001) - 1);

    /// <summary>Decodes an obfuscated little-endian integer.</summary>
    /// <param name="r">The record.</param>
    /// <param name="at">Byte offset in the record.</param>
    /// <returns>The decoded integer.</returns>
    private static int I32(ReadOnlySpan<byte> r, int at) => (BinaryPrimitives.ReadInt32LittleEndian(r[at..]) ^ 195999510) - 1;

    /// <summary>Reads the NPC name, stored as a length byte followed by its characters in reverse order.</summary>
    /// <param name="r">The record.</param>
    /// <returns>The name.</returns>
    private static string Name(ReadOnlySpan<byte> r)
    {
        var chars = r.Slice(1, 20).ToArray();
        Array.Reverse(chars);
        return EveText.Current.GetString(chars, 0, Math.Min((int)r[0], 20)).TrimEnd('\0');
    }
}
