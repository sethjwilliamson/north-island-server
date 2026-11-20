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
            if (price > 0 && character.Bag.Find(b => b.Slot == slot) is { } priced && !Bag.IsNoTrade(items, priced) && session.Vehicle?.BagSlot != slot)
                stall.Prices[slot] = price;
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
        if (world.SessionOf(ownerId) is not { } owner || owner.Stalls.Items is not { } stall || owner.ActiveCharacter is not { } seller)
        {
            session.Send(StallMessages.Closed(ownerId));
            return ValueTask.CompletedTask;
        }
        session.Send(StallMessages.Items(ownerId, StallItemsOf(stall, seller), session.Client));
        return ValueTask.CompletedTask;
    }

    /// <summary>Buys items from a stall: checks each line, gold and bag room, then moves items and gold.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="body">The packet body: the owner's login id, then 8-byte lines of slot, count and item id.</param>
    internal ValueTask OnBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } buyer || body.Length < 4)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        List<(byte Slot, byte Count, ushort ItemId)> wanted = [];
        for (var at = 4; at + 8 <= body.Length; at += 8)
            wanted.Add((body.Span[at], body.Span[at + 1], BinaryPrimitives.ReadUInt16LittleEndian(body.Span[(at + 2)..]))); // 1 Count, 2 ItemId
        if (wanted.Count == 0 || wanted.Select(w => w.Slot).Distinct().Count() != wanted.Count // each stall slot at most once: two lines of one stack would sell it twice
            || world.SessionOf(ownerId) is not { } owner || ReferenceEquals(owner, session)
            || owner.Stalls.Items is not { } stall || owner.ActiveCharacter is not { } seller)
            return ValueTask.CompletedTask;

        lock (stall)
        {
            var lines = new List<(BagItem Stack, byte Count, int Price)>();
            foreach (var (slot, count, itemId) in wanted)
            {
                if (!stall.Prices.TryGetValue(slot, out var price) || seller.Bag.Find(b => b.Slot == slot) is not { } stack
                    || stack.ItemId != itemId || count == 0 || count > stack.Count)
                {
                    logger.LogWarning("[{Session}] stall buy: slot {Slot} x{Count} of {Item} not available", session.Id, slot, count, itemId);
                    session.Send(StallMessages.Items(ownerId, StallItemsOf(stall, seller), session.Client));
                    return ValueTask.CompletedTask;
                }
                lines.Add((stack, count, price));
            }
            var total = lines.Sum(l => (long)l.Price * l.Count);
            if (total > buyer.Gold || (long)seller.Gold + total > BankHandlers.MaxGold)
                return Failed(session, "gold");
            List<BagItem> trial = [
                .. buyer.Bag.Select(
                    b =>
                        new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
            ];
            if (!lines.All(l => Bag.Add(trial, items, l.Stack.ItemId, l.Count, l.Stack.Wear).Sum(c => c.Added) == l.Count))
                return Failed(session, "bag");

            foreach (var (stack, count, price) in lines)
            {
                var (slot, itemId, wear, insert, fusion, pose, boxItem, used, extra) =
                    (stack.Slot, stack.ItemId, stack.Wear, stack.Insert, stack.Fusion, stack.Pose, stack.BoxItem, stack.Used, stack.Extra);
                stack.Count -= count;
                if (stack.Count == 0)
                {
                    seller.Bag.Remove(stack);
                    stall.Prices.Remove(slot);
                }
                owner.Send(ItemMessages.BagSlotDecrease(slot, count));
                owner.Send(StallMessages.Sold(itemId, count, price * count, slot));
                Bag.SendAdded(session, Bag.Add(buyer, items, itemId, count, wear, insert, fusion, pose, boxItem, used, extra));
                logger.LogInformation("[{Session}] {Buyer} buys {Item} x{Count} from {Seller}'s stall for {Gold}", session.Id, buyer.Name,
                    items[itemId]?.Name, count, seller.Name, price * count);
            }
            buyer.Gold -= (int)total;
            session.Send(PlayerMessages.MoneyChange(-(int)total));
            seller.Gold += (int)total;
            owner.Send(PlayerMessages.MoneyChange((int)total));
            if (stall.Prices.Count == 0)
                Close(owner, world);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Logs a refused purchase and tells the buyer the trade failed.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="why">The reason, for the log.</param>
    private ValueTask Failed(GameSession session, string why)
    {
        logger.LogWarning("[{Session}] stall buy refused: {Why}", session.Id, why);
        session.Send(new PacketWriter(Action26.Code, Action26.StallTradeFailed).U8(1).ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Lists the items of a stall that are still in the seller's bag.</summary>
    /// <param name="stall">The stall.</param>
    /// <param name="seller">The owner's character.</param>
    /// <returns>The offered items with their prices.</returns>
    private static IEnumerable<StallItem> StallItemsOf(ItemStall stall, Character seller) =>
        stall.Prices.Select(p => (p, stack: seller.Bag.Find(b => b.Slot == p.Key)))
            .Where(x => x.stack is not null)
            .Select(x => new StallItem(x.p.Key, x.stack!.ItemId, x.stack.Count, x.stack.Wear, x.p.Value));

    /// <summary>Gives the active character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
