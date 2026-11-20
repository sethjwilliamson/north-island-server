using System.Globalization;
using System.Reflection;

namespace NorthIsland.Server.Features.Items;

/// <summary>The gift card lists, read from an embedded table.</summary>
public static class GiftCards
{
    /// <summary>
    /// The version number sent with every card list. The client keeps the lists it was sent and asks again only when this differs, so raise it
    /// whenever a card's choices change (2: the Bronze, Silver, Golden and Free Cards became the event tier choice cards; 3: the Escape Button
    /// and the bottle potions joined the Bronze Card; 4: Alchemy Book 1 joined it).
    /// </summary>
    public const byte Flag = 4;

    /// <summary>Reads the embedded gift card table.</summary>
    /// <returns>The cards by card item id; empty when the table is missing.</returns>
    public static IReadOnlyDictionary<ushort, GiftCard> Load()
    {
        var rows = new Dictionary<ushort, List<GiftCardChoice>>();
        var whole = new Dictionary<ushort, bool>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NorthIsland.Server.Data.GiftCards.tsv");
        if (stream is null)
            return new Dictionary<ushort, GiftCard>();
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var p = line.Trim().Split((char)9);
            if (p.Length != 4 || !ushort.TryParse(p[0], CultureInfo.InvariantCulture, out var card))
                continue;
            if (!rows.TryGetValue(card, out var list))
                rows[card] = list = [];
            list.Add(new GiftCardChoice(ushort.Parse(p[1], CultureInfo.InvariantCulture), byte.Parse(p[2], CultureInfo.InvariantCulture)));
            whole[card] = p[3] == "1";
        }
        return rows.ToDictionary(kv => kv.Key, kv => new GiftCard(kv.Key, whole[kv.Key], kv.Value));
    }
}
