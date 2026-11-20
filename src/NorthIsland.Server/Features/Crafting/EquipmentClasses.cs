using System.Reflection;
using NorthIsland.GameData.Items;

namespace NorthIsland.Server.Features.Crafting;

/// <summary>The embedded tables that give pieces of equipment their material classes, which Item.dat leaves as the placeholder 250.</summary>
public static class EquipmentClasses
{
    /// <summary>
    /// The embedded tables, most trusted first: <c>EquipmentClasses.tsv</c> holds the classes of the 2010 data, and
    /// <c>EquipmentClassesWloHub.tsv</c> the bases WLOHub recorded for equipment the 2010 data leaves out, matched by name and rank.
    /// </summary>
    public static readonly string[] Files = ["EquipmentClasses.tsv", "EquipmentClassesWloHub.tsv"];

    /// <summary>
    /// Reads the embedded tables: item id, name and comma separated class numbers on each line. An id in an earlier table wins over a later one.
    /// </summary>
    /// <returns>The rows by item id; empty when the resources are missing.</returns>
    public static IReadOnlyDictionary<ushort, EquipmentClassEntry> Load()
    {
        var result = new Dictionary<ushort, EquipmentClassEntry>();
        foreach (var file in Files)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NorthIsland.Server.Data." + file);
            if (stream is null)
                continue;
            foreach (var (id, entry) in Parse(new StreamReader(stream)))
                result.TryAdd(id, entry);
        }
        return result;
    }

    /// <summary>Parses the lines of one table, skipping any line that is not an id, a name and a class list.</summary>
    /// <param name="reader">The table text.</param>
    /// <returns>The rows in file order.</returns>
    public static IEnumerable<(ushort Id, EquipmentClassEntry Entry)> Parse(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Trim().Split((char)9);
            if (parts.Length == 3 && ushort.TryParse(parts[0], out var id))
                yield return (id, new EquipmentClassEntry(parts[1], [.. parts[2].Split(',').Select(ushort.Parse)]));
        }
    }

    /// <summary>
    /// Gives each piece of equipment that carries the placeholder class 250 the classes of its row, when the row's name matches the item's.
    /// Equipment without a row keeps the placeholder, which <see cref="Alchemy.Class"/> reads as no class.
    /// </summary>
    /// <param name="table">The item table read from Item.dat.</param>
    /// <param name="classes">The rows by item id, from <see cref="Load"/>.</param>
    /// <param name="given">How many items got their classes.</param>
    /// <returns>The table with the classes filled in.</returns>
    public static ItemTable Apply(ItemTable table, IReadOnlyDictionary<ushort, EquipmentClassEntry> classes, out int given)
    {
        var count = 0;
        var result = table.Map(i =>
        {
            if (i.Words142.Length == 0 || i.Words142[0] != Alchemy.EquipmentClass || !classes.TryGetValue(i.Id, out var c) || c.Name != i.Name.Trim())
                return i;
            count++;
            return i with { Words142 = c.Words };
        });
        given = count;
        return result;
    }
}
