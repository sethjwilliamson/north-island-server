using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>Egg machines in a player's house: the owner fills the cells and sets a price, visitors pay to draw a random item.</summary>
/// <param name="items">The item data, for names.</param>
/// <param name="world">The world state, to find the sessions in a house.</param>
/// <param name="tents">The house and furniture handlers, for house owners and the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class EggMachineHandlers(
    ItemData items,
    WorldState world,
    TentHandlers tents,
    ILogger<EggMachineHandlers> logger)
{
    /// <summary>The item id of the Token Egg, which a machine needs for each play.</summary>
    public const ushort TokenEgg = 34059; // Token Egg

    /// <summary>The number of cells in a machine, and the least item count each cell needs before a price can be set.</summary>
    public const int Cells = 10, MinPerCell = 10;

    /// <summary>Hooks the house entry event so the price and token eggs of the machines are sent to everybody who enters.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>Whether an item is an egg machine.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>True for the Fortune Egg machine piece.</returns>
    public static bool IsEggMachine(ushort itemId) => itemId == 39057; // Fortune Egg

    /// <summary>The active character of a session that is in the world and stands in their own house.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The character, or null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>Finds an unused stock id among the character's furniture.</summary>
    /// <param name="c">The character.</param>
    /// <returns>A free id from 1 to 60, or 0 when all are used.</returns>
    private static byte FreeId(Character c)
    {
        var used = c.Furniture.SelectMany(f => f.Stock).Select(x => x.Id).ToHashSet();
        for (byte id = 1; id <= 60; id++)
            if (!used.Contains(id))
                return id;
        return 0;
    }

    /// <summary>Builds the packet <c>62/31</c> that shows what one cell of a machine holds.</summary>
    /// <param name="owner">The login id of the machine's owner.</param>
    /// <param name="f">The machine piece.</param>
    /// <param name="s">The cell's stock item.</param>
    /// <returns>The packet.</returns>
    private static byte[] CellPacket(int owner, Furniture f, StockItem s) =>
        new PacketWriter(Action62.Code, Action62.EggPut).I32(owner).U16(f.Index).U8(s.Pos).U16(s.ItemId).U8(s.Count).U8(s.Id).ToArray();

    /// <summary>Builds the packet <c>62/32</c> that gives the price of a machine.</summary>
    /// <param name="f">The machine piece.</param>
    /// <returns>The packet.</returns>
    private static byte[] PricePacket(Furniture f) => new PacketWriter(Action62.Code, Action62.EggPrice).U16(f.Index).I32(f.SalePrice).ToArray();

    /// <summary>Builds the packet <c>62/34</c> that gives the number of token eggs in a machine.</summary>
    /// <param name="f">The machine piece.</param>
    /// <returns>The packet.</returns>
    private static byte[] EggsPacket(Furniture f) => new PacketWriter(Action62.Code, Action62.EggFeed).U16(f.Index).U8(f.Eggs).ToArray();

    /// <summary>Adds items of a machine cell to a bag with every attribute the cell keeps, used and extra amounts included.</summary>
    /// <param name="bag">The bag, or a trial copy of it (<see cref="Bag.Trial"/>).</param>
    /// <param name="cell">The machine cell.</param>
    /// <param name="count">How many items to add.</param>
    /// <returns>The stacks touched and the number added to each, as Bag.Add returns them.</returns>
    private List<(BagItem Item, byte Added)> Give(List<BagItem> bag, StockItem cell, int count) =>
        Bag.Add(bag, items, cell.ItemId, count, cell.Wear, cell.Insert, cell.Fusion, cell.Pose, cell.BoxItem, cell.Used, cell.Extra);

    /// <summary>The sessions in the owner's house, including the owner.</summary>
    /// <param name="owner">The owner's character.</param>
    /// <param name="ownerSession">The owner's session.</param>
    /// <returns>The sessions.</returns>
    private IEnumerable<GameSession> InHouse(Character owner, GameSession ownerSession) =>
        world.OnMap(owner.MapId).Append(ownerSession).Distinct();

    /// <summary>
    /// Handles <c>62/31</c>: the owner puts a bag stack into a cell of a machine that has no price set. Only a stack that may be sold
    /// (<see cref="HomeShopHandlers.SaleRefusal"/>) and stacks, as the client's sell form asks, is taken.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes), the bag slot and the cell number.</param>
    internal ValueTask OnPut(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 4)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, bagSlot, cell) = (r.U16(), r.U8(), r.U8());
        var piece = c.Furniture.Find(f => f.Index == furn);
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        if (piece is null
            || !IsEggMachine(piece.ItemId)
            || stack is null
            || cell is < 1 or > Cells
            || piece.SalePrice > 0
            || piece.Stock.Exists(s => s.Pos == cell)
            || FreeId(c) == 0)
            return ValueTask.CompletedTask;
        var why = BagLocks.LockedBy(session, bagSlot)
            ?? HomeShopHandlers.SaleRefusal(items, session, stack) ?? (!Bag.IsStackable(items[stack.ItemId]) ? "it does not stack" : null);
        if (why is not null)
        {
            logger.LogWarning("[{Session}] {Name}: put bag slot {Slot} into egg machine {Furn}: {Why}", session.Id, c.Name, bagSlot, furn, why);
            return ValueTask.CompletedTask;
        }
        var stock = new StockItem
        {
            Id = FreeId(
                c), Pos = cell, ItemId = stack.ItemId, Count = stack.Count, Wear = stack.Wear, Insert = stack.Insert, Fusion = stack.Fusion, Pose = stack.Pose,
            BoxItem = stack.BoxItem, Used = stack.Used, Extra = stack.Extra,
        };
        piece.Stock.Add(stock);
        c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
        session.Send(CellPacket(session.Account!.LoginId, piece, stock));
        logger.LogInformation(
            "[{Session}] {Name} puts {Item} x{Count} into egg machine {Furn} cell {Cell}",
            session.Id,
            c.Name,
            items[stock.ItemId]?.Name.Trim(),
            stock.Count,
            furn,
            cell);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles <c>62/35</c>: the owner takes the stack of a cell back into the bag, if the machine has no price set and the bag has room.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes) and the cell number.</param>
    internal ValueTask OnRetake(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 3)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, cell) = (r.U16(), r.U8());
        var piece = c.Furniture.Find(f => f.Index == furn);
        if (piece is null || piece.SalePrice > 0 || piece.Stock.Find(s => s.Pos == cell) is not { } stock)
            return ValueTask.CompletedTask;
        if (Give(Bag.Trial(c.Bag), stock, stock.Count).Sum(x => x.Added) != stock.Count)
            return ValueTask.CompletedTask;
        Bag.SendAdded(session, Give(c.Bag, stock, stock.Count));
        piece.Stock.Remove(stock);
        session.Send(new PacketWriter(Action62.Code, Action62.EggPlayed).U16(furn).U8(cell).ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles <c>62/32</c>: the owner sets the price of a machine; a price needs all cells filled with the least count; 0 stops sales.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes) and the price (four bytes).</param>
    internal ValueTask OnPrice(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 6)
            return ValueTask.CompletedTask;
        var furn = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        var price = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(body.Span[2..]));
        var piece = c.Furniture.Find(f => f.Index == furn);
        if (piece is null || !IsEggMachine(piece.ItemId))
            return ValueTask.CompletedTask;
        if (price > 0 && (piece.Stock.Count < Cells || piece.Stock.Exists(s => s.Count < MinPerCell)))
            return ValueTask.CompletedTask;
        piece.SalePrice = price;
        var packet = PricePacket(piece);
        foreach (var s in InHouse(c, session))
            s.Send(packet);
        logger.LogInformation("[{Session}] {Name} sets egg machine {Furn} to {Price}", session.Id, c.Name, furn, price);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>62/34</c>: the owner puts a Token Egg from the bag into the first machine that has room for it.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The bag slot of the Token Egg.</param>
    internal ValueTask OnFeed(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 1)
            return ValueTask.CompletedTask;
        var stack = c.Bag.Find(b => b.Slot == body.Span[0]);
        var piece = c.Furniture.Find(f => IsEggMachine(f.ItemId) && f.Eggs < 255); // 255: the most token eggs a machine holds (one byte)
        if (stack is null || stack.ItemId != TokenEgg || piece is null || BagLocks.IsLocked(session, stack.Slot))
        {
            if (stack is not null && BagLocks.LockedBy(session, stack.Slot) is { } locked)
                logger.LogWarning("[{Session}] {Name}: token egg from bag slot {Slot}: it is {Why}", session.Id, c.Name, stack.Slot, locked);
            session.Send(VendRefusal.Packet(VendRefusal.AtLimit));
            return ValueTask.CompletedTask;
        }
        piece.Eggs++;
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        var packet = EggsPacket(piece);
        foreach (var s in InHouse(c, session))
            s.Send(packet);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>62/36</c>: sends the cells and token eggs of a machine to a player who opens its buy form.</summary>
    /// <param name="session">The visitor's session.</param>
    /// <param name="body">The furniture index (two bytes) and the owner's login id.</param>
    internal ValueTask OnOpen(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } me || body.Length < 6)
            return ValueTask.CompletedTask;
        var furn = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span[2..]);
        if (tents.HouseOwnerFor(me, ownerId) is not { } owner
            || owner.Character.MapId != me.MapId
            || owner.Character.Furniture.Find(f => f.Index == furn) is not { } piece
            || !IsEggMachine(piece.ItemId))
            return ValueTask.CompletedTask;
        foreach (var s in piece.Stock)
            session.Send(CellPacket(ownerId, piece, s));
        session.Send(EggsPacket(piece));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles <c>62/33</c>: a visitor standing in the owner's house (not the owner) pays the price and gets one item from a randomly picked
    /// filled cell; the owner is paid. The play runs under the owner's session gate.
    /// </summary>
    /// <param name="session">The visitor's session.</param>
    /// <param name="body">The owner's login id, the furniture index (two bytes) and the price the visitor saw.</param>
    internal ValueTask OnPlay(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } me || session.Account is null || body.Length < 10)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var furn = BinaryPrimitives.ReadUInt16LittleEndian(body.Span[4..]);
        var price = BinaryPrimitives.ReadInt32LittleEndian(body.Span[6..]);
        if (ownerId == session.Account.LoginId
            || tents.HouseOwnerFor(me, ownerId) is not { Session: { } ownerSession } owner
            || owner.Character.MapId != me.MapId)
        {
            logger.LogWarning("[{Session}] {Name}: play egg machine {Furn} of {Owner}: not a visitor in that house", session.Id, me.Name, furn, ownerId);
            return ValueTask.CompletedTask;
        }
        if (!ownerSession.TryExclusiveFrom(session, out var held)) // the owner's own packet may be filling, retaking or pricing the machine
        {
            logger.LogWarning("[{Session}] {Name}: play egg machine {Furn} of {Owner}: the owner's session is busy", session.Id, me.Name, furn,
                ownerId);
            return ValueTask.CompletedTask;
        }
        using (held)
        {
            if (owner.Character.MapId == me.MapId) // the owner may have left the house before their gate was free
                Play(session, me, ownerSession, owner.Character, ownerId, furn, price);
            else
                logger.LogWarning("[{Session}] {Name}: play egg machine {Furn} of {Owner}: the owner left the house", session.Id, me.Name, furn, ownerId);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Plays an egg machine under the owner's session gate: checks the price, the eggs, the gold and the bag, then gives one item.</summary>
    /// <param name="session">The visitor's session.</param>
    /// <param name="me">The visitor's character.</param>
    /// <param name="ownerSession">The owner's session, whose gate the caller holds.</param>
    /// <param name="oc">The owner's character.</param>
    /// <param name="ownerId">The owner's login id.</param>
    /// <param name="furn">The furniture index of the machine.</param>
    /// <param name="price">The price the visitor saw.</param>
    private void Play(GameSession session, Character me, GameSession ownerSession, Character oc, int ownerId, ushort furn, int price)
    {
        if (oc.Furniture.Find(f => f.Index == furn) is not { } piece || !IsEggMachine(piece.ItemId))
        {
            logger.LogWarning("[{Session}] {Name}: play egg machine {Furn} of {Owner}: no such machine", session.Id, me.Name, furn, ownerId);
            return;
        }
        if (piece.SalePrice != price)
        {
            session.Send(VendRefusal.Packet(VendRefusal.PriceChanged));
            return;
        }
        if (piece.Eggs == 0 || piece.SalePrice <= 0)
        {
            session.Send(VendRefusal.Packet(VendRefusal.NoEgg));
            return;
        }
        var filled = piece.Stock.Where(s => s.Count > 0).ToList();
        if (me.Gold < price || filled.Count == 0 || (long)oc.Gold + price > BankHandlers.MaxGold)
        {
            logger.LogWarning("[{Session}] {Name}: play egg machine {Furn} of {Owner}: not enough gold, empty, or the owner's purse is full", session.Id,
                me.Name, furn, ownerId);
            return;
        }
        var pick = filled[GameRandom.Shared.Next(filled.Count)];
        if (Give(Bag.Trial(me.Bag), pick, 1).Sum(x => x.Added) != 1)
        {
            session.Send(VendRefusal.Packet(VendRefusal.Full));
            return;
        }
        me.Gold -= price;
        session.Send(PlayerMessages.MoneyChange(-price));
        oc.Gold += price;
        ownerSession.Send(PlayerMessages.MoneyChange(price));
        Bag.SendAdded(session, Give(me.Bag, pick, 1));
        piece.Eggs--;
        pick.Count--;
        ownerSession.Send(new PacketWriter(Action62.Code, Action62.EggPlayed).U16(furn).U8(pick.Pos).ToArray());
        if (pick.Count == 0)
        {
            piece.Stock.Remove(pick);
            piece.SalePrice = 0;
        }
        var eggs = EggsPacket(piece);
        var priceNow = PricePacket(piece);
        foreach (var s in InHouse(oc, ownerSession))
        {
            s.Send(eggs);
            if (piece.SalePrice == 0)
                s.Send(priceNow);
        }
        logger.LogInformation(
            "[{Session}] {Name} plays {Owner}'s egg machine {Furn} and gets {Item}",
            session.Id,
            me.Name,
            oc.Name,
            furn,
            items[pick.ItemId]?.Name.Trim());
    }

    /// <summary>Sends the price and token eggs of the owner's machines to everybody who enters the house, and the cells to the owner.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        var host = owner?.ActiveCharacter;
        if (host is null || session.ActiveCharacter is null)
            return;
        foreach (var piece in host.Furniture.Where(f => IsEggMachine(f.ItemId)))
        {
            if (owner == session)
                foreach (var s in piece.Stock)
                    session.Send(CellPacket(session.Account!.LoginId, piece, s));
            session.Send(PricePacket(piece));
            session.Send(EggsPacket(piece));
        }
    }
}
