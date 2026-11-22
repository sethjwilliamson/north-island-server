using NorthIsland.Server.Data;
using NorthIsland.Server.Features.PetCare;

namespace NorthIsland.Server.Tests;

/// <summary>A pet express trip delivers exactly the stacks loaded at its start, or nothing.</summary>
public class ExpressLoadTests
{
    private static ExpressJob Load(List<BagItem> bag, params (byte Slot, byte Count)[] stacks) =>
        new(1, 2, [.. stacks], DateTime.UtcNow, [.. stacks.Select(s => ExpressLoad.Of(bag.Single(b => b.Slot == s.Slot)))]);

    [Fact]
    public void An_untouched_bag_delivers()
    {
        List<BagItem> bag = [new() { Slot = 3, ItemId = 100, Count = 5 }, new() { Slot = 7, ItemId = 200, Count = 1, Insert = 9 }];
        var job = Load(bag, (3, 5), (7, 1));

        Assert.Null(job.Changed(bag));
    }

    [Fact]
    public void A_slot_emptied_and_refilled_by_another_item_is_refused()
    {
        List<BagItem> bag = [new() { Slot = 3, ItemId = 100, Count = 5 }];
        var job = Load(bag, (3, 5));

        bag.Clear(); // a battle item use or a quest takes the loaded stack without asking BagLocks
        bag.Add(new() { Slot = 3, ItemId = 555, Count = 5 }); // loot lands in the freed slot: same slot, same count

        Assert.Equal(((byte)3, (ushort)555), job.Changed(bag));
    }

    [Fact]
    public void A_slot_refilled_by_the_same_item_with_other_attributes_is_refused()
    {
        List<BagItem> bag = [new() { Slot = 4, ItemId = 300, Count = 1, Insert = 12, Fusion = 2 }];
        var job = Load(bag, (4, 1));

        bag[0] = new() { Slot = 4, ItemId = 300, Count = 1 }; // a plain copy of the item, without the stone and fusion

        Assert.Equal(((byte)4, (ushort)300), job.Changed(bag));
    }

    [Fact]
    public void An_emptied_slot_is_refused()
    {
        List<BagItem> bag = [new() { Slot = 3, ItemId = 100, Count = 5 }, new() { Slot = 8, ItemId = 101, Count = 1 }];
        var job = Load(bag, (3, 5), (8, 1));

        bag.RemoveAll(b => b.Slot == 8);

        Assert.Equal(((byte)8, (ushort)0), job.Changed(bag));
    }

    [Fact]
    public void A_stack_that_only_grew_still_delivers()
    {
        List<BagItem> bag = [new() { Slot = 3, ItemId = 100, Count = 5 }];
        var job = Load(bag, (3, 2));

        bag[0].Count = 9; // more of the same item stacked on: the count check in Check still applies

        Assert.Null(job.Changed(bag));
    }
}
