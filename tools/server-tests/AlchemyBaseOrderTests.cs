using NorthIsland.GameData.Items;
using NorthIsland.Server.Features.Crafting;

namespace NorthIsland.Server.Tests;

/// <summary>A compound's result must match the ordered list of the materials' first classes, all of an item's classes counting.</summary>
public class AlchemyBaseOrderTests
{
    private const ushort Flower = 1, Grass = 2, Wood = 4, Gold = 15, Copper = 17, Iron = 18;

    /// <summary>Rolls 50 for the rank jump (plus 1 at skill level 1) and 0 for every other pick.</summary>
    private static int Roll(int n) => n == 100 ? 50 : 0;

    private static ItemRecord Item(ushort id, string name, byte rank, params ushort[] classes) =>
        new() { Id = id, Name = name, ShopColumn = rank, Words142 = [.. classes, .. new ushort[5 - classes.Length]] };

    private static ItemRecord Made(ItemRecord[] materials, ItemRecord[] all, int level = 1, AlchemyForm form = AlchemyForm.Primary)
    {
        var result = Alchemy.Compound(materials, level, all, Roll, form: form);
        Assert.Equal(AlchemyOutcome.Made, result.Outcome);
        return result.Product!;
    }

    // Red Dragon Gown (Flower / Grass / Gold) and Silk Gloves (Flower / Grass) share rank 33 in Item.dat.
    private static readonly ItemRecord Rose = Item(1, "Rose", 29, Flower), Reed = Item(2, "Reed", 29, Grass), Nugget = Item(3, "Nugget", 29, Gold);
    private static readonly ItemRecord Gown = Item(10, "Red Dragon Gown", 31, Flower, Grass, Gold), Gloves = Item(11, "Silk Gloves", 31, Flower, Grass);
    private static readonly ItemRecord[] Gear = [Rose, Reed, Nugget, Gown, Gloves];

    [Fact]
    public void A_third_class_must_be_among_the_materials()
    {
        Assert.Same(Gown, Made([Rose, Reed, Nugget], Gear));
        Assert.Same(Gloves, Made([Rose, Reed], Gear)); // the old two-class rule split these two
    }

    [Fact]
    public void An_item_with_a_class_the_materials_lack_is_not_made()
    {
        var copper = Item(1, "Copper Ore", 1, Copper);
        var wood = Item(2, "Oak", 1, Wood);
        var bronzeAdze = Item(3, "Bronze Adze", 3, Copper, Wood);
        var vineHat = Item(4, "Vine Hat", 3, Copper, Wood, Grass);

        Assert.Same(bronzeAdze, Made([copper, wood], [copper, wood, vineHat, bronzeAdze]));
        Assert.NotEqual(AlchemyOutcome.Made, Alchemy.Compound([copper, wood], 1, [copper, wood, vineHat], Roll).Outcome);
    }

    [Fact]
    public void Primary_follows_slot_order()
    {
        var copper = Item(1, "Copper Ore", 1, Copper);
        var wood = Item(2, "Oak", 1, Wood);
        var bronzeAdze = Item(3, "Bronze Adze", 3, Copper, Wood);
        var woodenThing = Item(4, "Wooden Club", 3, Wood);
        ItemRecord[] all = [copper, wood, bronzeAdze, woodenThing];

        Assert.Equal([Wood, Copper], Alchemy.BaseOrder([wood, copper], AlchemyForm.Primary));
        Assert.Same(woodenThing, Made([wood, copper], all)); // no Wood / Copper item: the Wood-only item
        Assert.Same(bronzeAdze, Made([copper, wood], all));
    }

    [Fact]
    public void Junior_puts_the_strongest_class_first()
    {
        var wood = Item(1, "Oak", 2, Wood);
        var copper = Item(2, "Copper Ore", 5, Copper);
        var bronzeAdze = Item(3, "Bronze Adze", 4, Copper, Wood);

        Assert.Equal([Copper, Wood], Alchemy.BaseOrder([wood, copper], AlchemyForm.Junior));
        Assert.Same(bronzeAdze, Made([wood, copper], [wood, copper, bronzeAdze], form: AlchemyForm.Junior));
    }

    [Fact]
    public void Without_an_exact_match_the_main_class_alone_is_made()
    {
        var copper = Item(1, "Copper Ore", 1, Copper);
        var iron = Item(2, "Iron Ore", 1, Iron);
        var copperBar = Item(3, "Copper Bar", 3, Copper);
        var copperIron = Item(4, "Copper Iron Thing", 3, Copper, Iron, Wood);

        Assert.Same(copperBar, Made([copper, iron], [copper, iron, copperBar, copperIron]));
    }

    [Fact]
    public void A_higher_rank_wins_over_an_exact_match()
    {
        var copper = Item(1, "Copper Ore", 1, Copper);
        var wood = Item(2, "Oak", 1, Wood);
        var adze = Item(3, "Adze", 2, Copper, Wood);
        var copperBar = Item(4, "Copper Bar", 3, Copper);

        Assert.Same(copperBar, Made([copper, wood], [copper, wood, adze, copperBar]));
    }

    [Fact]
    public void An_exact_match_wins_at_the_same_rank()
    {
        var copper = Item(1, "Copper Ore", 1, Copper);
        var wood = Item(2, "Oak", 1, Wood);
        var copperBar = Item(3, "Copper Bar", 3, Copper);
        var bronzeAdze = Item(4, "Bronze Adze", 3, Copper, Wood);

        Assert.Same(bronzeAdze, Made([copper, wood], [copper, wood, copperBar, bronzeAdze]));
    }
}
