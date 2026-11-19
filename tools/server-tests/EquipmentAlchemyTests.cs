using NorthIsland.GameData.Items;
using NorthIsland.Server.Features.Crafting;

namespace NorthIsland.Server.Tests;

/// <summary>Equipment takes part in alchemy through the classes of the equipment class tables.</summary>
public class EquipmentAlchemyTests
{
    /// <summary>Rolls 50 for the rank jump (plus 1 at skill level 1) and 0 for every other pick.</summary>
    private static int Roll(int n) => n == 100 ? 50 : 0;

    private static ItemRecord Item(ushort id, string name, byte rank, byte slot, params ushort[] classes) =>
        new() { Id = id, Name = name, ShopColumn = rank, EquipSlot = slot, Words142 = [.. classes, .. new ushort[5 - classes.Length]] };

    private static readonly ItemRecord CopperOre = Item(1, "Copper Ore", 1, 0, 17);
    private static readonly ItemRecord Wood = Item(2, "Wood", 1, 0, 4);
    private static readonly ItemRecord Adze = Item(3, "Adze", 2, 3, 17, 4);
    private static readonly ItemRecord BronzeAdze = Item(4, "Bronze Adze", 3, 3, 17, 4);
    private static readonly ItemRecord Unmatched = Item(5, "Sky Sword+1", 3, 3, Alchemy.EquipmentClass);
    private static readonly ItemRecord Stone = new() { Id = 6, Name = "Fire Stone", ShopColumn = 3, Type = Gems.StoneType, Words142 = [34, 0, 0, 0, 0] };

    [Fact]
    public void Equipment_with_classes_can_be_made()
    {
        Assert.True(Alchemy.CanBeMade(BronzeAdze));
        Assert.Equal(17, Alchemy.Class(BronzeAdze, 0));
        Assert.Equal(4, Alchemy.Class(BronzeAdze, 1));
    }

    [Fact]
    public void Equipment_comes_out_of_a_compound()
    {
        var result = Alchemy.Compound([CopperOre, Wood], 1, [CopperOre, Wood, Adze, BronzeAdze, Unmatched], Roll);

        Assert.Equal(AlchemyOutcome.Made, result.Outcome);
        Assert.Same(BronzeAdze, result.Product); // rank 1 + jump 2 = 3; class Copper, second class Wood is among the materials
    }

    [Fact]
    public void Equipment_second_class_must_be_among_the_materials()
    {
        var result = Alchemy.Compound([CopperOre, CopperOre], 1, [CopperOre, Wood, Adze, BronzeAdze], Roll);

        Assert.NotEqual(AlchemyOutcome.Made, result.Outcome); // no Wood among the materials, so neither adze qualifies
    }

    [Fact]
    public void Equipment_is_a_material_and_leads_with_its_first_class()
    {
        var result = Alchemy.Compound([Adze, Wood], 1, [CopperOre, Wood, Adze, BronzeAdze], Roll);

        Assert.Equal(17, result.MainClass);
        Assert.Equal(AlchemyOutcome.Made, result.Outcome);
        Assert.Same(BronzeAdze, result.Product); // lowest material rank 1 + jump 2 = 3
    }

    [Fact]
    public void Equipment_without_known_classes_keeps_no_class()
    {
        Assert.Equal(0, Alchemy.Class(Unmatched, 0));
        Assert.False(Alchemy.CanBeMade(Unmatched));
        var result = Alchemy.Compound([CopperOre, Wood], 1, [Unmatched], Roll);
        Assert.NotEqual(AlchemyOutcome.Made, result.Outcome);
    }

    [Fact]
    public void Equipment_and_a_stone_stay_a_mount()
    {
        Assert.True(CompoundHandlers.IsMount([BronzeAdze, Stone]));
        Assert.True(CompoundHandlers.IsMount([Stone, BronzeAdze]));
        Assert.False(CompoundHandlers.IsMount([BronzeAdze, Wood]));
    }

    [Fact]
    public void Class_tables_load_both_sources_without_overlap()
    {
        var all = EquipmentClasses.Load();
        Assert.Equal([17, 4, 0, 0, 0], all[10031].Words); // Copper Knife, 2010 data
        Assert.Equal([19, 27, 0, 0, 0], all[10083].Words); // Flaming Blade, WLOHub

        var assembly = typeof(EquipmentClasses).Assembly;
        var tables = EquipmentClasses.Files.Select(f =>
        {
            using var stream = assembly.GetManifestResourceStream("NorthIsland.Server.Data." + f)!;
            return EquipmentClasses.Parse(new StreamReader(stream, System.Text.Encoding.Latin1)).ToList();
        }).ToList();
        Assert.Equal(1323, tables[0].Count);
        Assert.Equal(132, tables[1].Count);
        Assert.Empty(tables[0].Select(r => r.Id).Intersect(tables[1].Select(r => r.Id)));
        Assert.All(tables.SelectMany(t => t), r =>
        {
            Assert.Equal(5, r.Entry.Words.Length);
            Assert.NotEqual(0, r.Entry.Words[0]);
            Assert.DoesNotContain(Alchemy.EquipmentClass, r.Entry.Words);
        });
    }

    [Fact]
    public void Class_tables_fill_in_the_real_item_data()
    {
        var path = Environment.GetEnvironmentVariable("NORTH_ISLAND_ITEM_DAT");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return; // needs the game's Item.dat; set NORTH_ISLAND_ITEM_DAT to run it

        var table = EquipmentClasses.Apply(ItemTable.Open(path), EquipmentClasses.Load(), out var given);

        Assert.Equal(1344, given); // 1,212 rows of the 2010 data and 132 of WLOHub match by name
        var bronzeAdze = table[17035]!;
        Assert.Equal("Bronze Adze", bronzeAdze.Name.Trim());
        Assert.Equal([17, 4], bronzeAdze.Words142[..2]); // Copper + Wood, as WLOHub records
        Assert.Equal(Alchemy.EquipmentClass, table[11103]!.Words142[0]); // Sky Sword+1 (mall gear) has no known bases
        var made = table.Items.Count(i => i.IsEquipment && Alchemy.CanBeMade(i));
        Assert.True(made > 1000, $"{made} pieces of equipment can be made");
    }
}
