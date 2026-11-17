using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Crafting;

/// <summary>Handles fusion scrolls, which raise or lower the fusion level of a piece of equipment.</summary>
/// <param name="items">The item data.</param>
/// <param name="logger">The log.</param>
public sealed class FusionHandlers(
    ItemData items,
    ILogger<FusionHandlers> logger)
{
    /// <summary>The highest fusion level a piece can have.</summary>
    public const int MaxLevel = 100;

    /// <summary>The fusion scrolls by item id: chance of success (0 to 1), levels gained on success and levels lost on failure.</summary>
    public static readonly IReadOnlyDictionary<ushort, (double Rate, int Gain, int Loss)> Scrolls = new Dictionary<ushort, (double, int, int)>
    {
        [34238] = (1.00, 1, 0), // Seraph Scroll
        [34239] = (0.50, 1, 1), // Demon Scroll
        [34243] = (0.90, 1, 0), // Cherubim Scroll
        [34244] = (0.80, 1, 0), // Ofanim Scroll
        [34245] = (0.70, 1, 0), // Virtues Scroll
        [34246] = (0.60, 1, 0), // Principality Scroll
        [34247] = (0.50, 2, 1), // Small Demon Scroll
        [34255] = (0.65, 1, 0), // Archangel Scroll
    };

    /// <summary>Random source: returns a number from 0 up to but not including 1. Replaceable by tests.</summary>
    public Func<double> Roll { get; set; } = () => GameRandom.Shared.NextDouble();

    /// <summary>Handles <c>23/121</c>: rolls the scroll, changes the piece's fusion level, spends the scroll and sends the result.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the scroll bag slot, then the equipment bag slot.</param>
    internal ValueTask OnFusion(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 2 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var (scrollSlot, gearSlot) = (body.Span[0], body.Span[1]);
        var scroll = c.Bag.Find(b => b.Slot == scrollSlot);
        var gear = c.Bag.Find(b => b.Slot == gearSlot);
        var record = gear is null ? null : items[gear.ItemId];
        if (scroll is null || !Scrolls.TryGetValue(scroll.ItemId, out var rule) || gear is null || record is null || !Fits(record) || gear.Fusion >= MaxLevel)
        {
            logger.LogInformation("[{Session}] {Name}: fusion refused (scroll slot {Scroll}, gear slot {Gear})", session.Id, c.Name, scrollSlot, gearSlot);
            session.Send(FusionMessages.Failed());
            return ValueTask.CompletedTask;
        }
        var before = gear.Fusion;
        if (Roll() < rule.Rate)
            gear.Fusion = (byte)Math.Min(MaxLevel, gear.Fusion + rule.Gain);
        else
            gear.Fusion = (byte)Math.Max(0, gear.Fusion - rule.Loss);
        scroll.Count--;
        session.Send(ItemMessages.BagSlotDecrease(scroll.Slot, 1));
        if (scroll.Count == 0)
            c.Bag.Remove(scroll);
        session.Send(FusionMessages.Result(gear.Slot, gear.Fusion));
        logger.LogInformation("[{Session}] {Name}: fusion of {Item} {Before} -> {After}", session.Id, c.Name, record.Name.Trim(), before, gear.Fusion);
        return ValueTask.CompletedTask;
    }

    /// <summary>Tells whether a piece of equipment can be fused.</summary>
    /// <param name="r">The item record of the piece.</param>
    /// <returns>
    /// True for a piece of equipment slots 1 to 5 that gives an attribute, has no use period and is not marked with flag 0x200.
    /// </returns>
    public static bool Fits(NorthIsland.GameData.Items.ItemRecord r) =>
        r.EquipSlot is >= 1 and <= 5 && (r.Words415.Length < 3 || r.Words415[2] == 0) && (r.Flags & Gems.MallFlag) == 0 && r.UsePeriod == 0
            && !(r.Words36.Length >= 2 && r.Words36[0] is 0 or 250 && r.Words36[1] is 0 or 250); // 250 EquipmentClass, 250 EquipmentClass
}
