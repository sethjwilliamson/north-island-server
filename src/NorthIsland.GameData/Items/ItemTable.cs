using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace NorthIsland.GameData.Items;

/// <summary>The items of Item.Dat, decoded and patched, looked up by id.</summary>
public sealed class ItemTable
{
    /// <summary>Size of one Item.Dat record in bytes.</summary>
    public const int RecordSize = 457;

    /// <summary>Format version the header record must carry.</summary>
    public const int FormatVersion = 9;

    /// <summary>The items by id.</summary>
    private readonly Dictionary<ushort, ItemRecord> _byId;

    /// <summary>Creates the table and indexes the items; throws <see cref="InvalidDataException"/> when an id occurs twice.</summary>
    /// <param name="items">The items.</param>
    private ItemTable(
        IReadOnlyList<ItemRecord> items)
    {
        Items = items;
        _byId = new Dictionary<ushort, ItemRecord>(items.Count);
        foreach (var item in items)
        {
            if (!_byId.TryAdd(item.Id, item))
                throw new InvalidDataException($"Item id {item.Id} occurs twice (the client refuses the file).");
        }
    }

    /// <summary>Creates a new table from this one with every item replaced by the result of a function.</summary>
    /// <param name="map">Function that returns the replacement for an item.</param>
    /// <returns>The new table.</returns>
    public ItemTable Map(Func<ItemRecord, ItemRecord> map) => new([.. Items.Select(map)]);

    /// <summary>All items in file order.</summary>
    public IReadOnlyList<ItemRecord> Items { get; }

    /// <summary>Looks up an item; null when the id is unknown.</summary>
    /// <param name="id">The item id.</param>
    public ItemRecord? this[ushort id] => _byId.GetValueOrDefault(id);

    /// <summary>Reads and parses an Item.Dat file.</summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>The parsed table.</returns>
    public static ItemTable Open(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Parses the contents of an Item.Dat file; throws <see cref="InvalidDataException"/> on a wrong size or format version.
    /// </summary>
    /// <param name="data">The raw file contents.</param>
    /// <returns>The parsed table.</returns>
    public static ItemTable Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < RecordSize || data.Length % RecordSize != 0)
            throw new InvalidDataException($"Item.Dat size {data.Length} is not a multiple of {RecordSize}.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(120, 4));
        if (version != FormatVersion)
            throw new InvalidDataException($"Item.Dat format {version}, expected {FormatVersion}.");

        var items = new List<ItemRecord>(data.Length / RecordSize - 1);
        for (var offset = RecordSize; offset < data.Length; offset += RecordSize)
        {
            var item = DecodeRecord(data.Slice(offset, RecordSize));
            if (item.Id != 0)
                items.Add(Patch(item));
        }
        return new ItemTable(items);
    }

    /// <summary>Decodes one 457-byte record.</summary>
    /// <param name="r">The record.</param>
    /// <returns>The item.</returns>
    public static ItemRecord DecodeRecord(ReadOnlySpan<byte> r)
    {
        return new ItemRecord
        {
            Name = ReversedShortString(r, 0, 20),
            Type = B(r, 21),
            Id = W(r, 22),
            IconId = W(r, 24),
            Words26 = Words(r, 26, 5),
            Words36 = Words(r, 36, 2),
            Bytes40 = Bytes(r, 40, 2),
            Ints42 = Ints(r, 42, 2),
            Unknown50 = B(r, 50),
            ShopColumn = B(r, 51),
            EquipSlot = B(r, 52),
            SubType = B(r, 53),
            ColourPattern1 = Ints(r, 54, 4),
            ColourPattern2 = Ints(r, 70, 4),
            ColourPattern3 = Ints(r, 86, 4),
            ColourPattern4 = Ints(r, 102, 4),
            Unknown118 = B(r, 118),
            RequiredLevel = B(r, 119),
            Price = I(r, 120),
            SellPrice = I(r, 124),
            UsableBy = B(r, 128),
            Flags = W(r, 129),
            Unknown131 = I(r, 131),
            Unknown135 = B(r, 135),
            Unknown136 = I(r, 136),
            LinkedSkillId = W(r, 140),
            Words142 = Words(r, 142, 5),
            Description = ReversedShortString(r, 152, 254),
            FurnitureSize = Bytes(r, 407, 3), // record offsets 407 to 409: furniture size x, y, z
            Unknown410 = W(r, 410),
            CellWidth = B(r, 412),
            CellHeight = B(r, 413),
            Unknown414 = B(r, 414),
            Words415 = Words(r, 415, 3),
            Bytes421 = Bytes(r, 421, 6),
            UsePeriod = W(r, 427),
            Words429 = Words(r, 429, 4),
            Ints437 = Ints(r, 437, 5),
        };
    }

    /// <summary>
    /// Applies the per-id fixes to a decoded item and parses its critical and no-encounter values from the description.
    /// </summary>
    /// <param name="item">The decoded item.</param>
    /// <returns>The fixed item.</returns>
    private static ItemRecord Patch(ItemRecord item)
    {
        var p = item;
        switch (item.Id)
        {
            case 34128 or 34129: // 34128 Electron Control, 34129 Big Vacuum Cleaner
                p = p with { UsePeriod = 720 };
                break;
            case 34132: // Loudspeaker
                p = p with { UsePeriod = 0 };
                break;
            case 34298: // HP Bottle
                p = p with { Flags = 28, Description = "Restores 2000000 HP" };
                break;
            case 34299: // SP Bottle
                p = p with { Flags = 28, Description = "Restores 2000000 MP" };
                break;
            case 32024: // Steamed Buns
                p = p with { Ints42 = [110, .. p.Ints42.Skip(1)] };
                break;
            case 61039: // Death Diamond
                p = p with { Type = 52 };
                break;
            case 60009: // Bottled Hot Water
                p = p with { Name = ReplaceFirst(p.Name, "Drink", "Boiled"), Description = ReplaceFirst(p.Description, "Drink", "Boiled") };
                break;
            case 47009: // Gas
                p = p with { Flags = 28 };
                break;
            case 42074: // Bluestone
                p = p with { Flags = 8 };
                break;
            case 21135 or (>= 21203 and <= 21206): // 21135 War Demon Armor, 21203 Red Monster Clothes, 21206 Blue Monster Clothes
                p = p with { Flags = 0 };
                break;
            case 30064 or 30065 or 30066 or 30067: // 30064 UFO Voucher, 30065 Turtle Car Voucher, 30066 Motorbike Ticket, 30067 Yacht Voucher
                p = p with { EquipSlot = 8 };
                break;
        }
        if (p.Words142.Length > 0 && p.Words142[0] == 62)
            p = p with { ShopColumn = 2, SellPrice = 2 };
        if (item.Id == 37247) // Organic Muck
            p = p with { SellPrice = 0 };
        return p with { CriticalPercent = ParseCriticalPercent(p.Description), NoEncounterSeconds = ParseNoEncounterSeconds(p.Description) };
    }

    /// <summary>Matches a percentage followed by the word crit in an item description.</summary>
    private static readonly Regex CriticalText = new(@"(\d+)\s*%\D*?crit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reads the critical hit percentage from an item description.</summary>
    /// <param name="description">The item description.</param>
    /// <returns>The percentage, 0 when the description has none.</returns>
    public static int ParseCriticalPercent(string description) =>
        CriticalText.Match(description) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var percent) ? percent : 0;

    /// <summary>Matches the text that grants a time without random encounters in an item description.</summary>
    private static readonly Regex NoEncounterText = new(
        @"no\s+(?:random\s+)?encounters?\s+for\s+(\d+|one|two|three|four|five|six|ten)\s+(second|minute|hour)s?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reads the no-encounter time from an item description.</summary>
    /// <param name="description">The item description.</param>
    /// <returns>The time in seconds, 0 when the description has none.</returns>
    public static int ParseNoEncounterSeconds(string description)
    {
        if (NoEncounterText.Match(description) is not { Success: true } m)
            return 0;
        var text = m.Groups[1].Value.ToLowerInvariant();
        var count = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n
            : Array.IndexOf(["", "one", "two", "three", "four", "five", "six", "", "", "", "ten"], text);
        var unit = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "second" => 1, // one second per unit
            "minute" => 60, // sixty seconds per unit
            _ => 3600 // any other unit counts as hours
        };
        return Math.Max(0, count) * unit;
    }

    /// <summary>Replaces the first occurrence of a text.</summary>
    /// <param name="s">The string to change.</param>
    /// <param name="old">The text to find.</param>
    /// <param name="replacement">The text to put in its place.</param>
    /// <returns>The changed string, or the original when the text is not found.</returns>
    private static string ReplaceFirst(string s, string old, string replacement)
    {
        var i = s.IndexOf(old, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), replacement, s.AsSpan(i + old.Length));
    }

    /// <summary>Decodes an obfuscated byte of a record.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset in the record.</param>
    /// <returns>The decoded byte.</returns>
    private static byte B(ReadOnlySpan<byte> r, int o) => unchecked((byte)((r[o] ^ 154) - 9));

    /// <summary>Decodes an obfuscated little-endian word of a record.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset in the record.</param>
    /// <returns>The decoded word.</returns>
    private static ushort W(ReadOnlySpan<byte> r, int o) =>
        unchecked((ushort)((BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(o, 2)) ^ 61379) - 9));

    /// <summary>Decodes an obfuscated little-endian integer of a record.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset in the record.</param>
    /// <returns>The decoded integer.</returns>
    private static int I(ReadOnlySpan<byte> r, int o) =>
        unchecked((BinaryPrimitives.ReadInt32LittleEndian(r.Slice(o, 4)) ^ 193000628) - 9);

    /// <summary>Decodes consecutive obfuscated bytes.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset of the first.</param>
    /// <param name="n">Number of bytes.</param>
    /// <returns>The decoded bytes.</returns>
    private static byte[] Bytes(ReadOnlySpan<byte> r, int o, int n)
    {
        var result = new byte[n];
        for (var i = 0; i < n; i++)
            result[i] = B(r, o + i);
        return result;
    }

    /// <summary>Decodes consecutive obfuscated words.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset of the first.</param>
    /// <param name="n">Number of words.</param>
    /// <returns>The decoded words.</returns>
    private static ushort[] Words(ReadOnlySpan<byte> r, int o, int n)
    {
        var result = new ushort[n];
        for (var i = 0; i < n; i++)
            result[i] = W(r, o + 2 * i);
        return result;
    }

    /// <summary>Decodes consecutive obfuscated integers.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset of the first.</param>
    /// <param name="n">Number of integers.</param>
    /// <returns>The decoded integers.</returns>
    private static int[] Ints(ReadOnlySpan<byte> r, int o, int n)
    {
        var result = new int[n];
        for (var i = 0; i < n; i++)
            result[i] = I(r, o + 4 * i);
        return result;
    }

    /// <summary>Reads a string stored as a length byte followed by its characters in reverse order.</summary>
    /// <param name="r">The record.</param>
    /// <param name="o">Byte offset of the length byte.</param>
    /// <param name="capacity">Number of character bytes that follow the length byte.</param>
    /// <returns>The text.</returns>
    private static string ReversedShortString(ReadOnlySpan<byte> r, int o, int capacity)
    {
        var chars = r.Slice(o + 1, capacity).ToArray();
        Array.Reverse(chars);
        var length = Math.Min((int)r[o], capacity);
        return Encoding.Latin1.GetString(chars, 0, length);
    }
}
