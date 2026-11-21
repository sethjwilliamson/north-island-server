using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Stalls;

/// <summary>Handles the stall cases a player sets up on a Food Shelf in their own house.</summary>
/// <param name="items">Item data.</param>
/// <param name="world">World state.</param>
/// <param name="tents">House handlers, for house owners and entry.</param>
/// <param name="logger">Logger.</param>
public sealed class StallCaseHandlers(
    ItemData items,
    WorldState world,
    TentHandlers tents,
    ILogger<StallCaseHandlers> logger)
{
    /// <summary>Item id of the Food Shelf furniture that holds stall cases.</summary>
    public const ushort FoodShelf = 39086; // Food Shelf

    /// <summary>
    /// Limits of the stall cases: positions on one shelf (<c>Positions</c>), cases per player (<c>Cases</c>), stock per case (<c>MaxStock</c>) and
    /// highest price (<c>MaxPrice</c>).
    /// </summary>
    public const int Positions = 2, Cases = 4, MaxStock = 250, MaxPrice = 9999999;

    /// <summary>Shows the cases to anyone who enters a house.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>Gives the active character when they are in the world on their own house map.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null otherwise.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>Tells whether an item can be put in a case: item type 23 or 32 without flag 2.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>True when sellable.</returns>
    private bool Sellable(ushort itemId) => items[itemId] is { } r && r.Type is 23 or 32 && (r.Flags & 2) == 0;

    /// <summary>Builds the packet that shows or updates a case.</summary>
    /// <param name="owner">The owner's login id.</param>
    /// <param name="c">The case.</param>
    /// <returns>The packet bytes.</returns>
    private static byte[] Update(int owner, StallCase c) =>
        new PacketWriter(Action62.Code, Action62.CaseOwner)
            .U8(1)
            .I32(owner)
            .U8(c.Case)
            .U8(c.FurnIndex)
            .U8(c.Pos)
            .U16(c.ItemId)
            .U8((byte)Math.Min(255, c.Stock))
            .I32(c.Price)
            .ToArray();

    /// <summary>Builds the packet that removes a case.</summary>
    /// <param name="owner">The owner's login id.</param>
    /// <param name="c">The case.</param>
    /// <returns>The packet bytes.</returns>
    private static byte[] Gone(int owner, StallCase c) =>
        new PacketWriter(Action62.Code, Action62.CaseOwner).U8(1).I32(owner).U8(c.Case).U8(c.FurnIndex).U8(c.Pos).U16(0).U8(0).I32(0).ToArray();

    /// <summary>Builds the refusal packet.</summary>
    /// <param name="code">The refusal code.</param>
    /// <returns>The packet bytes.</returns>
    private static byte[] Refuse(byte code) => new PacketWriter(Action62.Code, Action62.VendRefused).U8(code).ToArray();

    /// <summary>Lists the sessions in an owner's house, the owner included.</summary>
    /// <param name="owner">The owner's character.</param>
    /// <param name="ownerSession">The owner's session.</param>
    /// <returns>The sessions to tell about case changes.</returns>
    private IEnumerable<GameSession> InHouse(Character owner, GameSession ownerSession) => world.OnMap(owner.MapId).Append(ownerSession).Distinct();

    /// <summary>Handles the owner's case commands: open, add, take, reprice.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The packet body: the option byte and its data.</param>
    internal ValueTask OnOwner(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 4)
            return ValueTask.CompletedTask;
        var option = body.Span[0];
        var r = new PacketReader(body.Span[1..]);
        switch (option)
        {
            case 1 when body.Length >= 7: // open
                Open(session, c, r.U8(), r.U8(), r.I32());
                break;
            case 2: // add
                Add(session, c, r.U8(), r.U8(), r.U8(), body.Length >= 5 ? r.U8() : (byte)0);
                break;
            case 3: // take
                Take(session, c, r.U8(), r.U8(), r.U8());
                break;
            case 4 when body.Length >= 7: // price
                Reprice(session, c, r.U8(), r.U8(), r.I32());
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Opens a case with a bag stack on a free position, if the shelf and room allow.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    /// <param name="furn">The shelf's furniture index.</param>
    /// <param name="bagSlot">The bag slot of the stack.</param>
    /// <param name="price">The price of one item.</param>
    private void Open(
        GameSession session,
        Character c,
        byte furn,
        byte bagSlot,
        int price)
    {
        var shelf = c.Furniture.Find(f => f.Index == furn && f.ItemId == FoodShelf);
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        if (shelf is null || stack is null || !Sellable(stack.ItemId) || price is < 1 or > MaxPrice)
            return;
        if (!GridStore.IsPlain(stack))
        {
            logger.LogWarning("[{Session}] {Name}: stall case refused for bag slot {Slot}: the stack carries more than a case keeps", session.Id, c.Name,
                bagSlot);
            return;
        }
        var freeCase = Enumerable.Range(1, Cases).Select(i => (byte)i).FirstOrDefault(i => !c.StallCases.Exists(x => x.Case == i));
        var pos = Enumerable.Range(1, Positions).Select(i => (byte)i).FirstOrDefault(i => !c.StallCases.Exists(x => x.FurnIndex == furn && x.Pos == i));
        if (freeCase == 0 || pos == 0)
        {
            session.Send(Refuse(1));
            return;
        }
        var stall = new StallCase
        {
            Case = freeCase,
            FurnIndex = furn,
            Pos = pos,
            ItemId = stack.ItemId,
            Stock = stack.Count,
            Price = price,
            Wear = stack.Wear
        };
        c.StallCases.Add(stall);
        c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
        Broadcast(session, c, Update(session.Account!.LoginId, stall));
    }

    /// <summary>Adds items from a bag stack to a case of the same item with the same wear, so taking them back cannot change their wear.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    /// <param name="furn">The shelf's furniture index.</param>
    /// <param name="bagSlot">The bag slot to take from.</param>
    /// <param name="caseNo">The case number.</param>
    /// <param name="room">The most to move; 0 for no limit.</param>
    private void Add(
        GameSession session,
        Character c,
        byte furn,
        byte bagSlot,
        byte caseNo,
        byte room)
    {
        var stall = c.StallCases.Find(x => x.Case == caseNo && x.FurnIndex == furn);
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        if (stall is null || stack is null || stack.ItemId != stall.ItemId)
            return;
        if (stack.Wear != stall.Wear || !GridStore.IsPlain(stack))
        {
            logger.LogWarning("[{Session}] {Name}: bag slot {Slot} not added to stall case {Case}: not the same wear or carries more than a case keeps",
                session.Id, c.Name, bagSlot, caseNo);
            return;
        }
        var moved = Math.Min(stack.Count, MaxStock - stall.Stock);
        if (room > 0)
            moved = Math.Min(moved, room);
        if (moved <= 0)
            return;
        stall.Stock += moved;
        stack.Count -= (byte)moved;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, (byte)moved));
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        Broadcast(session, c, Update(session.Account!.LoginId, stall));
    }

    /// <summary>Takes items from a case back into the bag; an empty case is removed.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    /// <param name="furn">The shelf's furniture index.</param>
    /// <param name="caseNo">The case number.</param>
    /// <param name="n">How many to take.</param>
    private void Take(
        GameSession session,
        Character c,
        byte furn,
        byte caseNo,
        byte n)
    {
        var stall = c.StallCases.Find(x => x.Case == caseNo && x.FurnIndex == furn);
        if (stall is null || n == 0)
            return;
        var want = Math.Min((int)n, stall.Stock);
        var got = 0;
        foreach (var (bagStack, added) in Bag.Add(c, items, stall.ItemId, want, stall.Wear))
        {
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = bagStack.Slot, ItemId = bagStack.ItemId, Count = added, Wear = bagStack.Wear }));
            got += added;
        }
        if (got == 0)
            return;
        stall.Stock -= got;
        if (stall.Stock == 0)
        {
            c.StallCases.Remove(stall);
            Broadcast(session, c, Gone(session.Account!.LoginId, stall));
        }
        else
            Broadcast(session, c, Update(session.Account!.LoginId, stall));
    }

    /// <summary>Sets a new price on a case.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    /// <param name="furn">The shelf's furniture index.</param>
    /// <param name="caseNo">The case number.</param>
    /// <param name="price">The new price.</param>
    private void Reprice(
        GameSession session,
        Character c,
        byte furn,
        byte caseNo,
        int price)
    {
        var stall = c.StallCases.Find(x => x.Case == caseNo && x.FurnIndex == furn);
        if (stall is null || price is < 1 or > MaxPrice)
            return;
        stall.Price = price;
        Broadcast(session, c, Update(session.Account!.LoginId, stall));
    }

    /// <summary>Sends a packet to everyone in the owner's house.</summary>
    /// <param name="ownerSession">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    /// <param name="packet">The packet bytes.</param>
    private void Broadcast(GameSession ownerSession, Character c, byte[] packet)
    {
        foreach (var s in InHouse(c, ownerSession))
            s.Send(packet);
    }

    /// <summary>
    /// Handles a purchase from a case: checks the buyer stands in the owner's house with the owner there, then, under the owner's session gate,
    /// price, gold and bag room, then moves items and gold and tells the house.
    /// </summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="body">The packet body: the owner's login id, case number, count and the price the buyer saw.</param>
    internal ValueTask OnBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } me || session.Account is null || body.Length < 11)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var caseNo = (byte)Math.Min(255, (int)BinaryPrimitives.ReadUInt16LittleEndian(body.Span[4..]));
        var count = body.Span[6];
        var price = BinaryPrimitives.ReadInt32LittleEndian(body.Span[7..]);
        if (ownerId == session.Account.LoginId || tents.HouseOwnerFor(me, ownerId) is not { Session: { } ownerSession } owner
            || owner.Character.MapId != me.MapId)
        {
            logger.LogWarning("[{Session}] {Name}: stall case buy refused, not in the house of {Owner} with its owner there", session.Id, me.Name,
                ownerId);
            return ValueTask.CompletedTask;
        }
        if (!ownerSession.TryExclusiveFrom(session, out var held)) // the owner's own packet may be changing the cases or the gold
        {
            logger.LogWarning("[{Session}] {Name}: stall case buy refused, {Owner}'s session is busy", session.Id, me.Name, ownerId);
            return ValueTask.CompletedTask;
        }
        using (held)
            Buy(session, me, ownerSession, owner.Character, ownerId, caseNo, count, price);
        return ValueTask.CompletedTask;
    }

    /// <summary>Carries out a purchase from a case under the owner's session gate: price, gold and bag room, then items and gold.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="me">The buyer's character.</param>
    /// <param name="ownerSession">The owner's session, whose gate the caller holds.</param>
    /// <param name="oc">The owner's character.</param>
    /// <param name="ownerId">The owner's login id.</param>
    /// <param name="caseNo">The case number.</param>
    /// <param name="count">How many to buy.</param>
    /// <param name="price">The unit price the buyer saw.</param>
    private void Buy(GameSession session, Character me, GameSession ownerSession, Character oc, int ownerId, byte caseNo, byte count, int price)
    {
        var stall = oc.StallCases.Find(x => x.Case == caseNo);
        if (stall is null || count == 0 || count > stall.Stock)
            return;
        if (stall.Price != price)
        {
            session.Send(Refuse(3));
            return;
        }
        var total = (long)price * count;
        if (me.Gold < total || oc.Gold + total > BankHandlers.MaxGold)
        {
            logger.LogWarning("[{Session}] {Name} cannot buy from {Owner}'s stall case: {Why}", session.Id, me.Name, oc.Name,
                me.Gold < total ? "not enough gold" : "the owner's purse is full");
            return;
        }
        var trial = me.Bag.Select(b => GridStore.Copy(b, b.Slot, b.Count)).ToList();
        if (Bag.Add(trial, items, stall.ItemId, count, stall.Wear).Sum(x => x.Added) != count)
        {
            session.Send(Refuse(5));
            return;
        }
        me.Gold -= (int)total;
        session.Send(PlayerMessages.MoneyChange(-(int)total));
        oc.Gold += (int)total;
        ownerSession.Send(PlayerMessages.MoneyChange((int)total));
        foreach (var (bagStack, added) in Bag.Add(me, items, stall.ItemId, count, stall.Wear))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = bagStack.Slot, ItemId = bagStack.ItemId, Count = added, Wear = bagStack.Wear }));
        var sold = new PacketWriter(Action62.Code, Action62.CaseOwner).U8(2).I32(ownerId).U16(stall.Case).U8(count).U16(stall.FurnIndex).ToArray();
        stall.Stock -= count;
        if (stall.Stock == 0)
            oc.StallCases.Remove(stall);
        foreach (var s in InHouse(oc, ownerSession))
            s.Send(sold);
        logger.LogInformation(
            "[{Session}] {Name} buys {Count} x {Item} from {Owner}'s stall case for {Total}",
            session.Id,
            me.Name,
            count,
            items[stall.ItemId]?.Name.Trim(),
            oc.Name,
            total);
    }

    /// <summary>Shows the cases of the house to a player who has entered it.</summary>
    /// <param name="session">The entering player's session.</param>
    /// <param name="owner">The owner's session, or null to look the owner up by the house map.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        var host = owner?.ActiveCharacter ?? tents.OwnerOfHouse(session.ActiveCharacter?.MapId ?? 0)?.Character;
        var hostId = owner?.Account?.LoginId ?? tents.OwnerOfHouse(session.ActiveCharacter?.MapId ?? 0)?.Id ?? 0;
        if (host is null)
            return;
        foreach (var stall in host.StallCases)
            session.Send(Update(hostId, stall));
    }
}
