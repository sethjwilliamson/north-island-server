using System.Collections.Immutable;

namespace NorthIsland.Server.Features.Travel;

/// <summary>The 40 places the Teleport Scroll and the Dragonflies can take a player to, and the items that use them.</summary>
public static class MagicMap
{
    /// <summary>The item id of the Teleport Scroll.</summary>
    public const ushort TeleportScroll = 34163; // Teleport Scroll

    /// <summary>The item ids of the Dragonflies, which teleport once.</summary>
    public static readonly ImmutableArray<ushort> Dragonflies = [34169, 34332]; // 34169 Bamboo Dragonfly, 34332 Mini Dragonfly

    /// <summary>The places in order: name, map, x and y.</summary>
    private static readonly ImmutableArray<(string, ushort, ushort, ushort)> Rows =
        [
            ("Fishing Village", 12100, 1800, 1300), // Fishing Village
            ("Calcutta", 12300, 2062, 1735), // Calcutta
            ("Oslya", 60006, 5180, 4675), // Oslya
            ("Mayan Forest", 60005, 1002, 1295), // Mayan Forest
            ("Universal", 60007, 1040, 1335), // Universal
            ("Revival Isle", 60004, 5750, 1955), // Revival Isle
            ("Big Island of Hawaii", 60008, 1750, 3900), // Large Island
            ("South Pole", 12355, 690, 1090), // South Pole
            ("Korea", 60013, 3045, 5235), // Korea
            ("Iceberg", 12356, 1580, 1255), // Iceberg
            ("Koyota", 60011, 5145, 355), // Koyota
            ("Taiwan", 60012, 2845, 780),
            ("South Island", 60002, 2862, 4575), // South Island
            ("North Island", 60000, 2900, 3055), // North Island
            ("Stoowa Isle", 60003, 2142, 5175), // South Island
            ("Egypt", 12500, 3122, 1755), // Cairo
            ("Madagascar", 11127, 3040, 340), // Madagascar
            ("Polo Island", 10069, 1840, 2075), // Polo Island
            ("Hezrou Island", 10070, 3460, 1775), // Hezrou Island
            ("Snow Island", 12367, 674, 1352), // 1F, Snow Cave
            ("Inca", 60015, 4040, 355), // Inca
            ("Ghost Isle", 12286, 3180, 875), // Ghost Isle Cave
            ("Shayii Hill", 12281, 340, 415), // Shayii Hill
            ("Ancient Yazhou City", 12105, 2000, 1555), // Ancient Yazhou City
            ("Bangkok", 12601, 342, 415), // Harbor
            ("Lion Isle", 11145, 3362, 1795), // Lion Isle
            ("Centaur Island", 11135, 1900, 1495), // Centaur Island
            ("Sky City", 11152, 620, 470), // Barnya Sky City
            ("African Villages", 12700, 2500, 2110), // African Villages
            ("Yamataikoku", 12850, 2240, 1695), // Yamataikoku
            ("Athens", 12800, 1182, 2575), // Athens
            ("Small Island of Hawaii", 60009, 1442, 1315), // Small Island
            ("Training Island", 13050, 2632, 1344), // Training Island
            ("Rome", 13200, 2300, 1330), // Rome
            ("Persia", 13100, 930, 1870), // Persia
            ("Filipine", 12952, 1780, 1295), // Philippine
            ("Lost Jungle", 13261, 1900, 235), // Lost Jun
            ("Fog Jungle", 13164, 1765, 390), // Fog Jungle
            ("Chang'an", 12110, 1842, 1355), // Chang'an
            ("Deep Jungle", 12905, 2680, 270), // Deep Jungle
        ];

    /// <summary>Looks up a place by its number.</summary>
    /// <param name="n">The place number, 1 to 40.</param>
    /// <returns>The place, or null when the number is out of range.</returns>
    public static MagicMapPlace? Get(int n) =>
        n is >= 1 and <= 40 ? new MagicMapPlace(Rows[n - 1].Item1, Rows[n - 1].Item2, Rows[n - 1].Item3, Rows[n - 1].Item4) : null;
}
