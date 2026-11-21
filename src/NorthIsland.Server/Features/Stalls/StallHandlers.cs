using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Stalls;

/// <summary>Handles item stalls: opening, closing, browsing and buying.</summary>
/// <param name="world">World state.</param>
/// <param name="items">Item data.</param>
/// <param name="logger">Logger.</param>
public sealed class StallHandlers(
    WorldState world,
    ItemData items,
    ILogger<StallHandlers> logger)
{
    /// <summary>Most bag stacks one stall can offer.</summary>
    public const int MaxItems = 5;

    /// <summary>Closes the player's item stall and tells them and the map; nothing happens when none is open.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="world">World state, for the broadcast.</param>
    public static void Close(GameSession session, WorldState world)
    {
        if (session.Stalls.Items is null || session.Account is null)
            return;
        session.Stalls.Items = null;
        var packet = StallMessages.Closed(session.Account.LoginId);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
    }

    /// <summary>Opens an item stall from the player's list of priced bag slots.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: name, optional sign byte, then pairs of bag slot and price.</param>
    internal ValueTask OnOpen(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || session.Stalls.Any || session.Battle is not null || body.Length < 2)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var stall = new ItemStall { Name = r.PStr8(), Sign = r.Remaining > 0 ? r.U8() : (byte)0 };
        while (r.Remaining >= 5 && stall.Prices.Count < MaxItems)
        {
            var (slot, price) = (r.U8(), r.I32());
            var priced = character.Bag.Find(b => b.Slot == slot);
            if (price <= 0)
                continue;
            if ((StallStock.OpenRefusal(items, session, priced, slot) ?? StallStock.ItemStallRefusal(items, priced!)) is { } why)
            {
                logger.LogWarning("[{Session}] {Name}: bag slot {Slot} left out of the stall: {Why}", session.Id, character.Name, slot, why);
                continue;
            }
            stall.Prices[slot] = price;
            stall.Offered[slot] = StallStock.Snapshot(priced!);
        }
        if (stall.Prices.Count == 0 || stall.Name.Length == 0)
        {
            logger.LogWarning("[{Session}] {Name}: stall refused (no priced items or no name)", session.Id, character.Name);
            return ValueTask.CompletedTask;
        }
        session.Stalls.Items = stall;
        session.Send(StallMessages.Opened(stall.Name, stall.Sign, stall.Prices.Select(p => (p.Key, p.Value))));
        world.Broadcast(session, StallMessages.List([(session.Account!.LoginId, stall.Name, stall.Sign)]), sameMapOnly: true);
        logger.LogInformation("[{Session}] {Name} opens stall '{Stall}': {Items}", session.Id, character.Name, stall.Name,
            string.Join(", ", stall.Prices.Select(p => $"slot {p.Key} at {p.Value}")));
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the player's item stall.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body; not read.</param>
    internal ValueTask OnClose(GameSession session, ReadOnlyMemory<byte> body)
    {
        Close(session, world);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the items on offer in a stall, or a closed notice when there is none.</summary>
    /// <param name="session">The browsing player's session.</param>
    /// <param name="body">The packet body: the owner's login id.</param>
    internal ValueTask OnBrowse(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is null || body.Length < 4)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        if (world.SessionOf(ownerId) is not { } owner || owner.Stalls.Items is not { } stall || owner.ActiveCharacter is not { } seller
            || seller.MapId != session.ActiveCharacter!.MapId)
        {
            session.Send(StallMessages.Closed(ownerId));
            return ValueTask.CompletedTask;
        }
        lock (stall)
            Show(session, ownerId, stall, owner, seller);
        return ValueTask.CompletedTask;
    }

    /// <summary>Size in bytes of one line of a buy request: u8 stall slot, u8 count, u16 item id and the u32 key the stall list gave the line.</summary>
    private const int BuyLineSize = 8;

    /// <summary>
    /// Buys items from a stall on the buyer's map: under the seller's session gate, checks each line against the stack the stall opened with and
    /// the price the buyer was shown, then gold and bag room, then moves items and gold.
    /// </summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="body">
    /// The packet body: the owner's login id, then 8-byte lines of slot, count, item id and the u32 key echoed from the stall list. The key is the
    /// one <see cref="StallMessages.Items"/> wrote (the bag slot), not a price, so the price check uses the list this server last sent the buyer.
    /// </param>
    internal ValueTask OnBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } buyer || body.Length < 4)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        List<(byte Slot, byte Count, ushort ItemId)> wanted = [];
        for (var at = 4; at + BuyLineSize <= body.Length; at += BuyLineSize)
            wanted.Add((body.Span[at], body.Span[at + 1], BinaryPrimitives.ReadUInt16LittleEndian(body.Span[(at + 2)..]))); // 1 Count, 2 ItemId
        if (wanted.Count == 0 || wanted.Select(w => w.Slot).Distinct().Count() != wanted.Count // each stall slot at most once: two lines of one stack would sell it twice
            || world.SessionOf(ownerId) is not { } owner || ReferenceEquals(owner, session)
            || owner.Stalls.Items is not { } stall || owner.ActiveCharacter is not { } seller)
            return ValueTask.CompletedTask;
        if (seller.MapId != buyer.MapId)
            return Failed(session, $"{seller.Name} is on another map");

        if (!owner.TryExclusiveFrom(session, out var held)) // the seller's own packet may be selling, moving or trading the same stacks
            return Failed(session, $"{seller.Name}'s session is busy");
        using (held)
        {
            if (!ReferenceEquals(owner.Stalls.Items, stall))
                return Failed(session, "the stall closed");
            lock (stall)
            {
                var lines = new List<(BagItem Stack, BagItem Offered, byte Count, int Price)>();
                foreach (var (slot, count, itemId) in wanted)
                {
                    var priced = stall.Prices.TryGetValue(slot, out var price);
                    var offered = stall.Offered.GetValueOrDefault(slot);
                    var stack = offered is null ? null : StallStock.Find(seller, owner, slot, offered);
                    var why = !priced || offered is null ? "not on offer"
                        : stack is null ? "no longer the stack the stall opened with"
                            : stack.ItemId != itemId || count == 0 || count > stack.Count ? "not that many of that item"
                                : StallStock.ItemStallRefusal(items, stack) is { } unsellable ? unsellable
                                    : !StallView.Saw(session.Stalls.SeenItems, stall, slot, price) ? "not the price the buyer was shown"
                                        : null;
                    if (why is not null)
                    {
                        logger.LogWarning("[{Session}] stall buy: slot {Slot} x{Count} of {Item} refused: {Why}", session.Id, slot, count, itemId, why);
                        Show(session, ownerId, stall, owner, seller);
                        return ValueTask.CompletedTask;
                    }
                    lines.Add((stack!, offered!, count, price));
                }
                var total = lines.Sum(l => (long)l.Price * l.Count);
                if (total > buyer.Gold || (long)seller.Gold + total > BankHandlers.MaxGold)
                    return Failed(session, "gold");
                List<BagItem> trial = [.. buyer.Bag.Select(b => GridStore.Copy(b, b.Slot, b.Count))];
                if (!lines.All(l => Add(trial, l.Stack, l.Count).Sum(c => c.Added) == l.Count))
                    return Failed(session, "bag");

                long paid = 0;
                foreach (var (stack, offered, count, price) in lines)
                {
                    if (!seller.Bag.Contains(stack) || stack.Count < count) // checked above; never take more than the stack holds
                    {
                        logger.LogError("[{Session}] stall buy: slot {Slot} changed during the sale, line skipped", session.Id, stack.Slot);
                        continue;
                    }
                    var (slot, itemId) = (stack.Slot, stack.ItemId);
                    var sold = GridStore.Copy(stack, slot, count);
                    stack.Count -= count;
                    offered.Count -= count;
                    if (stack.Count == 0)
                    {
                        seller.Bag.Remove(stack);
                        stall.Prices.Remove(slot);
                        stall.Offered.Remove(slot);
                    }
                    owner.Send(ItemMessages.BagSlotDecrease(slot, count));
                    owner.Send(StallMessages.Sold(itemId, count, price * count, slot));
                    Bag.SendAdded(session, Add(buyer.Bag, sold, count));
                    paid += (long)price * count;
                    logger.LogInformation("[{Session}] {Buyer} buys {Item} x{Count} from {Seller}'s stall for {Gold}", session.Id, buyer.Name,
                        items[itemId]?.Name, count, seller.Name, price * count);
                }
                buyer.Gold -= (int)paid;
                session.Send(PlayerMessages.MoneyChange(-(int)paid));
                seller.Gold += (int)paid;
                owner.Send(PlayerMessages.MoneyChange((int)paid));
                if (stall.Prices.Count == 0)
                    Close(owner, world);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Puts items of a stack into a bag-like grid with all of the stack's attributes.</summary>
    /// <param name="bag">The grid: the buyer's bag or a trial copy of it.</param>
    /// <param name="from">The stack the items come from; it is not changed.</param>
    /// <param name="count">How many to add.</param>
    /// <returns>One entry per stack touched, with the number added to it.</returns>
    private List<(BagItem Item, byte Added)> Add(List<BagItem> bag, BagItem from, byte count) =>
        Bag.Add(bag, items, from.ItemId, count, from.Wear, from.Insert, from.Fusion, from.Pose, from.BoxItem, from.Used, from.Extra);

    /// <summary>Logs a refused purchase and tells the buyer the trade failed.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="why">The reason, for the log.</param>
    private ValueTask Failed(GameSession session, string why)
    {
        logger.LogWarning("[{Session}] stall buy refused: {Why}", session.Id, why);
        session.Send(new PacketWriter(Action26.Code, Action26.StallTradeFailed).U8(1).ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a buyer the items of a stall that are still in the seller's bag as the stall opened with them, and remembers what they saw.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="ownerId">The owner's login id.</param>
    /// <param name="stall">The stall, locked by the caller.</param>
    /// <param name="owner">The owner's session.</param>
    /// <param name="seller">The owner's character.</param>
    private void Show(GameSession session, int ownerId, ItemStall stall, GameSession owner, Character seller)
    {
        var shown = stall.Prices
            .Select(p => (Slot: p.Key, Price: p.Value,
                Stack: stall.Offered.TryGetValue(p.Key, out var offered) ? StallStock.Find(seller, owner, p.Key, offered) : null))
            .Where(x => x.Stack is not null && StallStock.ItemStallRefusal(items, x.Stack) is null)
            .ToList();
        session.Stalls.SeenItems = new StallView(stall, shown.ToDictionary(x => x.Slot, x => x.Price));
        session.Send(StallMessages.Items(ownerId, shown.Select(x => new StallItem(x.Slot, x.Stack!.ItemId, x.Stack.Count, x.Stack.Wear, x.Price)),
            session.Client));
    }

    /// <summary>Gives the active character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
