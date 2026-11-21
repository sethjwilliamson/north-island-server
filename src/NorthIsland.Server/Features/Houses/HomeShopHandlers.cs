using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>Selling from furniture in the owner's house: the Shelf and the Coin Machine used as a vending machine.</summary>
/// <param name="items">The item data, for names and sizes.</param>
/// <param name="world">The world state, to find the sessions in a house.</param>
/// <param name="tents">The house and furniture handlers, for house owners and the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class HomeShopHandlers(
    ItemData items,
    WorldState world,
    TentHandlers tents,
    ILogger<HomeShopHandlers> logger)
{
    /// <summary>The item ids of the Shelf and of the vending machine.</summary>
    public const ushort Shelf = 39059, VendingMachine = 39058; // 39059 Shelf, 39058 Coin Machine

    /// <summary>
    /// The positions of a shelf, the rows of a vending machine, the offer ids available for each kind of piece, and the highest unit price.
    /// </summary>
    public const int ShelfPositions = 5, VendingRows = 5, ShelfIds = 50, VendingIds = 20, MaxPrice = 9999999;

    /// <summary>The item ids of the first and last magic crystal stones that cannot be passed on with the item they are set into.</summary>
    public const ushort FirstNonTransferable = 61063, LastNonTransferable = 61078; // 61063 Morning Star Crystal, 61078 Charm Crystal

    /// <summary>
    /// Returns why a bag stack cannot be sold from a house shop or egg machine, by the client's rules (StallDropRules, EggSellWindow), or null
    /// when it can: an item that cannot be traded or has a pose (<see cref="Bag.IsNoTrade"/>), a magic crystal stone that cannot be passed on, a
    /// vehicle or the ridden vehicle, or a tent.
    /// </summary>
    /// <param name="items">The item data, for the flags and types.</param>
    /// <param name="session">The owner's session, for the ridden vehicle.</param>
    /// <param name="stack">The bag stack.</param>
    /// <returns>The reason as log text; null when the stack may be sold.</returns>
    internal static string? SaleRefusal(ItemData items, GameSession session, BagItem stack) =>
        Bag.IsNoTrade(items, stack) ? "it cannot be traded"
            : stack.Insert is >= FirstNonTransferable and <= LastNonTransferable ? "its crystal stone cannot be passed on"
                : session.Vehicle?.BagSlot == stack.Slot || items[stack.ItemId]?.Type == Bag.VehicleType ? "it is a vehicle"
                    : items[stack.ItemId]?.Type == TentHandlers.TentItemType ? "it is a tent"
                        : null;

    /// <summary>
    /// Whether a bag stack is still the stack an offer was made from: the same item with every attribute the same, by the bag's own rule
    /// (<see cref="GridStore.Same"/>).
    /// </summary>
    /// <param name="bag">The bag stack.</param>
    /// <param name="offer">The offer.</param>
    /// <returns>True when they match.</returns>
    private static bool Matches(BagItem bag, StockItem offer) => GridStore.Same(bag, StackOf(offer));

    /// <summary>Makes a bag stack with the slot, item, count and attributes of an offer, to compare it with the bag stack it was made from.</summary>
    /// <param name="offer">The offer.</param>
    /// <returns>The stack, not in any grid.</returns>
    private static BagItem StackOf(StockItem offer) => new()
    {
        Slot = offer.BagCell,
        ItemId = offer.ItemId,
        Count = offer.Count,
        Wear = offer.Wear,
        Insert = offer.Insert,
        Fusion = offer.Fusion,
        Pose = offer.Pose,
        BoxItem = offer.BoxItem,
        Used = offer.Used,
        Extra = offer.Extra,
    };

    /// <summary>Hooks the house entry event so visitors are shown the offers.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>Whether a furniture piece is the kind of shop asked for.</summary>
    /// <param name="f">The furniture piece.</param>
    /// <param name="vending">True to ask for a vending machine, false for a shelf.</param>
    /// <returns>True when the piece is of that kind.</returns>
    private static bool IsShop(Furniture f, bool vending) => f.ItemId == (vending ? VendingMachine : Shelf);

    /// <summary>The active character of a session that is in the world and stands in their own house.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The character, or null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>Finds an unused offer id among the character's shelves or vending machines.</summary>
    /// <param name="c">The character.</param>
    /// <param name="vending">True for vending machines, false for shelves.</param>
    /// <returns>A free id, or 0 when all are used.</returns>
    private static ushort FreeId(Character c, bool vending)
    {
        var used = c.Furniture.Where(f => IsShop(f, vending)).SelectMany(f => f.Stock).Select(x => (int)x.Id).ToHashSet();
        for (ushort id = 1; id <= (vending ? VendingIds : ShelfIds); id++)
            if (!used.Contains(id))
                return id;
        return 0;
    }

    /// <summary>Builds the packet <c>62/21</c> that shows an offer.</summary>
    /// <param name="owner">The shop owner's login id.</param>
    /// <param name="f">The shelf or machine.</param>
    /// <param name="s">The offer.</param>
    /// <returns>The packet.</returns>
    private static byte[] OfferPacket(int owner, Furniture f, StockItem s) =>
        new PacketWriter(Action62.Code, Action62.ShelfPut).I32(owner).U16(s.Id).U16(f.Index).U8(s.Pos).U8(s.BagCell).U8(s.Count).I32(s.Price)
            .Bytes(HouseLifeMessages.CarItem(s.ItemId, s.Count, s.Wear, s.Insert, s.Fusion, s.Pose, s.BoxItem, s.Used, s.Extra, s.Price)).ToArray();

    /// <summary>Builds the packet <c>62/22</c> that removes an offer.</summary>
    /// <param name="owner">The shop owner's login id.</param>
    /// <param name="f">The shelf or machine.</param>
    /// <param name="s">The offer.</param>
    /// <returns>The packet.</returns>
    private static byte[] DownPacket(int owner, Furniture f, StockItem s) =>
        new PacketWriter(Action62.Code, Action62.ShelfStop).I32(owner).U16(s.Id).U16(f.Index).ToArray();

    /// <summary>Handles <c>62/21</c> (shelf) and <c>62/24</c> (vending machine): the owner puts a bag stack up for sale at a unit price.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index, item id, bag slot, count and unit price, and for a vending machine the row.</param>
    /// <param name="vending">True for a vending machine, false for a shelf.</param>
    internal ValueTask OnPut(GameSession session, ReadOnlyMemory<byte> body, bool vending)
    {
        if (InOwnHouse(session) is not { } c || body.Length < (vending ? 10 : 9))
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, itemId, bagSlot, count, price) = (r.U16(), r.U16(), r.U8(), r.U8(), r.I32());
        var row = vending ? r.U8() : (byte)0;
        var piece = c.Furniture.Find(f => f.Index == furn);
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        var positions = vending ? VendingRows : ShelfPositions;
        if (piece is null
            || !IsShop(piece, vending)
            || stack is null
            || stack.ItemId != itemId
            || count == 0
            || count > stack.Count
            || price is < 1 or > MaxPrice
            || items[itemId] is not { CellWidth: 1, CellHeight: 1 })
            return ValueTask.CompletedTask;
        // a slot already listed on a shelf or machine, or held by a trade, a stall, the pet express or the ridden vehicle, stays as it is
        if ((BagLocks.LockedBy(session, bagSlot) ?? SaleRefusal(items, session, stack)) is { } why)
        {
            logger.LogWarning("[{Session}] {Name}: offer bag slot {Slot} on {Furn}: {Why}", session.Id, c.Name, bagSlot, furn, why);
            return ValueTask.CompletedTask;
        }
        var pos = row is >= 1 and <= 5 && !piece.Stock.Exists(s => s.Pos == row) ? row
            : (byte)Enumerable.Range(1, positions).FirstOrDefault(p => !piece.Stock.Exists(s => s.Pos == p));
        var id = FreeId(c, vending);
        if (pos == 0 || id == 0)
        {
            session.Send(VendRefusal.Packet(vending ? VendRefusal.MachineFull : VendRefusal.AtLimit));
            return ValueTask.CompletedTask;
        }
        var offer = new StockItem
        {
            Id = (byte)id,
            Pos = pos,
            ItemId = stack.ItemId,
            Count = count,
            Wear = stack.Wear,
            Insert = stack.Insert,
            Fusion = stack.Fusion,
            Pose = stack.Pose,
            BoxItem = stack.BoxItem,
            Used = stack.Used, Extra = stack.Extra, Price = price, BagCell = bagSlot,
        };
        piece.Stock.Add(offer);
        var packet = OfferPacket(session.Account!.LoginId, piece, offer);
        foreach (var s in world.OnMap(c.MapId).Append(session).Distinct())
            s.Send(packet);
        logger.LogInformation(
            "[{Session}] {Name} offers {Item} x{Count} at {Price} on {Furn}",
            session.Id,
            c.Name,
            items[itemId]!.Name.Trim(),
            count,
            price,
            furn);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>62/22</c> (shelf) and <c>62/25</c> (vending machine): the owner takes an offer down.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The offer id.</param>
    /// <param name="vending">True for a vending machine, false for a shelf.</param>
    internal ValueTask OnStop(GameSession session, ReadOnlyMemory<byte> body, bool vending)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 2)
            return ValueTask.CompletedTask;
        var id = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        var piece = c.Furniture.Find(f => IsShop(f, vending) && f.Stock.Exists(s => s.Id == id));
        if (piece is null)
            return ValueTask.CompletedTask;
        TakeDown(session, c, piece, piece.Stock.Find(s => s.Id == id)!);
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes an offer and tells everybody in the house.</summary>
    /// <param name="owner">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    /// <param name="piece">The shelf or machine.</param>
    /// <param name="offer">The offer to remove.</param>
    private void TakeDown(GameSession owner, Character c, Furniture piece, StockItem offer)
    {
        piece.Stock.Remove(offer);
        var packet = DownPacket(owner.Account!.LoginId, piece, offer);
        foreach (var s in world.OnMap(c.MapId).Append(owner).Distinct())
            s.Send(packet);
    }

    /// <summary>
    /// Handles <c>62/23</c> (shelf) and <c>62/26</c> (vending machine): a visitor in the owner's house buys from an offer, paying the owner at once.
    /// The buyer gets the owner's bag stack as it is; when it is no longer the stack the offer was made from (another item, other attributes or
    /// fewer items), the offer is taken down instead. Everything after finding the owner runs under the owner's session gate.
    /// </summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="body">The owner's login id, the offer id, the count, the item id and the unit price the buyer saw.</param>
    /// <param name="vending">True for a vending machine, false for a shelf.</param>
    internal ValueTask OnBuy(GameSession session, ReadOnlyMemory<byte> body, bool vending)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } me || session.Account is null || body.Length < 13)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var id = BinaryPrimitives.ReadUInt16LittleEndian(body.Span[4..]);
        var count = body.Span[6];
        var itemId = BinaryPrimitives.ReadUInt16LittleEndian(body.Span[7..]);
        var price = BinaryPrimitives.ReadInt32LittleEndian(body.Span[9..]);
        if (ownerId == session.Account.LoginId || tents.HouseOwnerFor(me, ownerId) is not { Session: { } ownerSession } owner
            || owner.Character.MapId != me.MapId)
            return ValueTask.CompletedTask;
        if (!ownerSession.TryExclusiveFrom(session, out var held)) // the owner's own packet may be moving, selling or trading the listed stack
        {
            logger.LogWarning("[{Session}] {Name}: buy offer {Id} of {Owner} refused: the owner's session is busy", session.Id, me.Name, id, ownerId);
            return ValueTask.CompletedTask;
        }
        using (held)
        {
            if (owner.Character.MapId == me.MapId) // the owner may have left the house before their gate was free
                Buy(session, me, ownerSession, owner.Character, ownerId, id, count, itemId, price, vending);
            else
                logger.LogWarning("[{Session}] {Name}: buy offer {Id} of {Owner} refused: the owner left the house", session.Id, me.Name, id, ownerId);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Carries out a purchase from a shelf or vending machine offer under the owner's session gate.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="me">The buyer's character.</param>
    /// <param name="ownerSession">The owner's session, whose gate the caller holds.</param>
    /// <param name="oc">The owner's character.</param>
    /// <param name="ownerId">The owner's login id.</param>
    /// <param name="id">The offer id.</param>
    /// <param name="count">How many to buy.</param>
    /// <param name="itemId">The item the buyer saw.</param>
    /// <param name="price">The unit price the buyer saw.</param>
    /// <param name="vending">True for a vending machine, false for a shelf.</param>
    private void Buy(
        GameSession session,
        Character me,
        GameSession ownerSession,
        Character oc,
        int ownerId,
        ushort id,
        byte count,
        ushort itemId,
        int price,
        bool vending)
    {
        var piece = oc.Furniture.Find(f => IsShop(f, vending) && f.Stock.Exists(s => s.Id == id));
        var offer = piece?.Stock.Find(s => s.Id == id);
        if (piece is null || offer is null || offer.ItemId != itemId || count == 0 || count > offer.Count)
            return;
        if (offer.Price != price)
        {
            session.Send(VendRefusal.Packet(VendRefusal.PriceChanged));
            return;
        }
        var bag = oc.Bag.Find(b => b.Slot == offer.BagCell);
        if (bag is null || !Matches(bag, offer) || bag.Count < count || SaleRefusal(items, ownerSession, bag) is not null)
        {
            logger.LogWarning("[{Session}] {Name}: buy offer {Id} of {Owner}: the bag stack changed, the offer is taken down", session.Id, me.Name, id, oc.Name);
            TakeDown(ownerSession, oc, piece, offer);
            return;
        }
        var total = (long)price * count;
        if (me.Gold < total || oc.Gold + total > BankHandlers.MaxGold)
        {
            logger.LogWarning("[{Session}] {Name}: buy offer {Id} of {Owner} for {Total}: not enough gold or the seller's purse is full", session.Id, me.Name,
                id, oc.Name, total);
            return;
        }
        var trial = Bag.Trial(me.Bag);
        if (Bag.Add(trial, items, bag.ItemId, count, bag.Wear).Sum(x => x.Added) != count)
        {
            session.Send(VendRefusal.Packet(VendRefusal.Full));
            return;
        }
        me.Gold -= (int)total;
        session.Send(PlayerMessages.MoneyChange(-(int)total));
        oc.Gold += (int)total;
        ownerSession.Send(PlayerMessages.MoneyChange((int)total));
        bag.Count -= count;
        ownerSession.Send(ItemMessages.BagSlotDecrease(bag.Slot, count));
        if (bag.Count == 0)
            oc.Bag.Remove(bag);
        Bag.SendAdded(session, Bag.Add(me, items, bag.ItemId, count, bag.Wear, bag.Insert, bag.Fusion, bag.Pose, bag.BoxItem, bag.Used, bag.Extra));
        var sold = new PacketWriter(Action62.Code, Action62.ShelfSold).I32(ownerId).U16(offer.Id).U8(count).U16(piece.Index).ToArray();
        offer.Count -= count;
        if (offer.Count == 0)
            piece.Stock.Remove(offer);
        foreach (var s in world.OnMap(oc.MapId).Append(ownerSession).Distinct())
            s.Send(sold);
        logger.LogInformation(
            "[{Session}] {Name} buys {Count} x {Item} from {Owner} for {Total}",
            session.Id,
            me.Name,
            count,
            items[itemId]?.Name.Trim(),
            oc.Name,
            total);
    }

    /// <summary>Shows the offers of the house's shelves and vending machines to the player who entered.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        var host = owner?.ActiveCharacter ?? tents.OwnerOfHouse(session.ActiveCharacter?.MapId ?? 0)?.Character;
        var hostId = owner?.Account?.LoginId ?? tents.OwnerOfHouse(session.ActiveCharacter?.MapId ?? 0)?.Id ?? 0;
        if (host is null)
            return;
        foreach (var piece in host.Furniture.Where(f => f.ItemId is Shelf or VendingMachine))
            foreach (var s in piece.Stock)
                session.Send(OfferPacket(hostId, piece, s));
    }
}
