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

    /// <summary>Builds the packet <c>62/25</c> that refuses a request.</summary>
    /// <param name="code">The refusal code.</param>
    /// <returns>The packet.</returns>
    private static byte[] Refuse(byte code) => new PacketWriter(Action62.Code, Action62.VendRefused).U8(code).ToArray();

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
            || items[itemId] is not { CellWidth: 1, CellHeight: 1 }
            || piece.Stock.Exists(s => s.BagCell == bagSlot)
            || c.Furniture.Any(f => f.Stock.Exists(s => s.BagCell == bagSlot)))
            return ValueTask.CompletedTask;
        var pos = row is >= 1 and <= 5 && !piece.Stock.Exists(s => s.Pos == row) ? row
            : (byte)Enumerable.Range(1, positions).FirstOrDefault(p => !piece.Stock.Exists(s => s.Pos == p));
        var id = FreeId(c, vending);
        if (pos == 0 || id == 0)
        {
            session.Send(Refuse(vending ? (byte)7 : (byte)1)); // code
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

    /// <summary>Handles <c>62/23</c> (shelf) and <c>62/26</c> (vending machine): a visitor buys from an offer, paying the owner at once.</summary>
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
        if (tents.OwnerById(ownerId) is not { Session: { } ownerSession } owner || owner.Character.MapId != me.MapId || ownerId == session.Account.LoginId)
            return ValueTask.CompletedTask;
        var oc = owner.Character;
        var piece = oc.Furniture.Find(f => IsShop(f, vending) && f.Stock.Exists(s => s.Id == id));
        var offer = piece?.Stock.Find(s => s.Id == id);
        if (piece is null || offer is null || offer.ItemId != itemId || count == 0 || count > offer.Count)
            return ValueTask.CompletedTask;
        if (offer.Price != price)
        {
            session.Send(Refuse(3)); // code
            return ValueTask.CompletedTask;
        }
        var bag = oc.Bag.Find(b => b.Slot == offer.BagCell);
        if (bag is null || bag.ItemId != offer.ItemId || bag.Count < count)
        {
            TakeDown(ownerSession, oc, piece, offer);
            return ValueTask.CompletedTask;
        }
        var total = (long)price * count;
        if (me.Gold < total || oc.Gold + total > int.MaxValue)
            return ValueTask.CompletedTask;
        var trial = Bag.Trial(me.Bag);
        if (Bag.Add(trial, items, offer.ItemId, count, offer.Wear).Sum(x => x.Added) != count)
        {
            session.Send(Refuse(5)); // code
            return ValueTask.CompletedTask;
        }
        me.Gold -= (int)total;
        session.Send(PlayerMessages.MoneyChange(-(int)total));
        oc.Gold += (int)total;
        ownerSession.Send(PlayerMessages.MoneyChange((int)total));
        bag.Count -= count;
        ownerSession.Send(ItemMessages.BagSlotDecrease(bag.Slot, count));
        if (bag.Count == 0)
            oc.Bag.Remove(bag);
        Bag.SendAdded(session, Bag.Add(me, items, offer.ItemId, count, offer.Wear, offer.Insert, offer.Fusion, offer.Pose, offer.BoxItem, offer.Used, offer.Extra));
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
        return ValueTask.CompletedTask;
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
