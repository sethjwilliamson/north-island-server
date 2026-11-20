using NorthIsland.GameData.Items;
using NorthIsland.Server.Features.Crafting;

namespace NorthIsland.Server.Tests;

/// <summary>Items with flag bit 8, such as the instance dungeon rewards, go into compounds but do not come out of ordinary ones.</summary>
public class NotMadeFlagTests
{
    private const ushort Iron = 30;

    private static int Roll(int n) => n == 100 ? 50 : 0;

    private static ItemRecord Item(ushort id, string name, byte rank, ushort flags, params ushort[] classes) =>
        new() { Id = id, Name = name, ShopColumn = rank, Flags = flags, Words142 = [.. classes, .. new ushort[5 - classes.Length]] };

    // Sea Wing Helmet (flags 27) is an instance reward; Long Copper Boots stands in for an ordinary result at the same rank.
    private static readonly ItemRecord Ore = Item(1, "Ore", 24, 0, Iron), Helmet = Item(22219, "Sea Wing Helmet", 25, 27, Iron);
    private static readonly ItemRecord Boots = Item(3, "Long Copper Boots", 25, 0, Iron);

    [Fact]
    public void A_flagged_item_is_not_made()
    {
        Assert.False(Alchemy.CanBeMade(Helmet));
        Assert.Same(Boots, Alchemy.Compound([Ore, Ore], 1, [Ore, Helmet, Boots], Roll).Product);
        Assert.NotEqual(AlchemyOutcome.Made, Alchemy.Compound([Ore, Ore], 1, [Ore, Helmet], Roll).Outcome);
    }

    [Fact]
    public void A_flagged_item_is_still_a_material() =>
        Assert.Equal(AlchemyOutcome.Made, Alchemy.Compound([Helmet, Ore], 1, [Ore, Helmet, Item(4, "Iron Cap", 26, 0, Iron)], Roll).Outcome);

    [Fact]
    public void White_crystal_and_carrot_are_still_made()
    {
        Assert.True(Alchemy.CanBeMade(Item(61071, "White Crystal", 27, 27, Iron)));
        Assert.True(Alchemy.CanBeMade(Item(32102, "Carrot", 3, 27, Iron)));
    }

    [Fact]
    public void A_bless_compound_ignores_the_flag() =>
        Assert.True(Alchemy.CanBeMade(Item(5, "Fairy Crest", 4, 11, Iron), bless: true));
}
