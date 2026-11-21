using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Mall;

/// <summary>Handles the item mall: the catalog, buying with mall points and bonus points, and the point store.</summary>
/// <param name="items">Item definitions; rows of unknown items are dropped.</param>
/// <param name="options">Mall options.</param>
/// <param name="logger">Logger.</param>
public sealed class MallHandlers(
    ItemData items,
    IOptions<MallOptions> options,
    ILogger<MallHandlers> logger)
{
    /// <summary>Builds the second list of rows, bought with bonus points; numbered from 1 and without unknown items.</summary>
    /// <returns>The rows.</returns>
    public IReadOnlyList<MallRow> BonusCatalog() =>
        [.. options.Value.ItemMall.BonusItems.Where(i => items[i.Item] is not null)
            .Select((i, n) => new MallRow(i.Item, Math.Max((byte)1, i.Pack), i.Price, i.Discount, i.Hot, i.Category, (ushort)(n + 1)))];

    /// <summary>Builds the rows on sale now.</summary>
    /// <returns>The rows.</returns>
    public IReadOnlyList<MallRow> Catalog() => Catalog(GameClock.UtcNow);

    /// <summary>
    /// Builds the rows on sale at a time: a row with a rotation group is included only while its group is the current one (or rotation is off);
    /// unknown items are dropped and the rows are numbered from 1.
    /// </summary>
    /// <param name="utc">The time to build the catalog for (UTC).</param>
    /// <returns>The rows.</returns>
    public IReadOnlyList<MallRow> Catalog(DateTime utc)
    {
        var mall = options.Value.ItemMall;
        var group = mall.RotationGroupAt(utc);
        return [.. mall.Catalog.Where(i => items[i.Item] is not null && (i.Rotation == 0 || group == 0 || i.Rotation == group))
            .Select((i, n) => new MallRow(i.Item, Math.Max((byte)1, i.Pack), i.Price, i.Discount, i.Hot, i.Category, (ushort)(n + 1)))];
    }

    /// <summary>
    /// Gives the account its starting mall points once, then sends the catalog, the bonus list and bonus points, and the game tab as configured.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="account">The account that logged in.</param>
    public void SendOnLogin(GameSession session, Account account)
    {
        var mall = options.Value.ItemMall;
        if (!account.MallPointsGiven)
        {
            account.MallPoints += mall.StartingPoints;
            account.MallPointsGiven = true;
        }
        if (!mall.Enabled)
            return;
        session.Mall.Rows = Catalog();
        session.Send(MallMessages.Catalog([.. session.Mall.Rows!], session.Client));
        if (mall.BonusItems.Count > 0 || account.BonusPoints > 0)
        {
            if (BonusCatalog() is { Count: > 0 } second)
                session.Send(MallMessages.CatalogB(second));
            session.Send(MallMessages.Bonus(account.BonusPoints));
        }
        if (mall.GameTab)
            session.Send(MallMessages.GameTab(open: true));
    }

    /// <summary>Sends the rows of a point store to the client.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="rows">The rows on offer.</param>
    public void OpenPointStore(GameSession session, IReadOnlyList<PointStoreRow> rows)
    {
        session.Send(MallMessages.PointStore([.. rows]));
        logger.LogInformation("[{Session}] point store ({Rows} row(s))", session.Id, rows.Count);
    }

    /// <summary>Works out what a mall purchase of several packs of a row costs.</summary>
    /// <param name="row">The mall row.</param>
    /// <param name="count">How many packs are bought.</param>
    /// <returns>The cost in mall points.</returns>
    public static int MallCost(MallRow row, int count) =>
        (int)Math.Round(row.Price * row.Discount / 100.0, MidpointRounding.AwayFromZero) * count;

    /// <summary>Works out what buying from a point store row delivers and costs; a row sold in lots delivers whole lots.</summary>
    /// <param name="row">The point store row.</param>
    /// <param name="count">The count the client sent.</param>
    /// <returns>The quantity delivered and its cost in points.</returns>
    public static (int Quantity, int Cost) PointStoreLine(PointStoreRow row, int count) =>
        row.Count > 1
            ? (Math.Max(1, count / row.Count) * row.Count, Math.Max(1, count / row.Count) * row.Price)
            : (Math.Max(1, count), Math.Max(1, count) * row.Price);

    /// <summary>Answers a request for the account's mall points.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal static ValueTask OnAskPoints(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State == SessionState.InWorld && session.Account is { } account)
            session.Send(MallMessages.Points(account.MallPoints));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Buys the lines of a mall purchase request; each line is answered on its own and refused when the row is unknown, too expensive or does not fit
    /// the bag, and a bought line credits bonus points.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnMallBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Account is not { } account || session.ActiveCharacter is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        if (session.Battle is not null || !options.Value.ItemMall.Enabled)
        {
            session.Send(MallMessages.Refused(2)); // code
            return ValueTask.CompletedTask;
        }
        var r = new PacketReader(body.Span);
        var n = r.U8();
        var catalog = session.Mall.Rows ?? Catalog();
        for (var i = 0; i < n; i++)
        {
            var (itemId, category, count) = (r.U16(), r.U8(), r.U8());
            var key = session.Client == ClientGeneration.Client2014 ? r.U16() : (ushort)0;
            var row = catalog.FirstOrDefault(c => c.ItemId == itemId && c.Category == category && (key == 0 || c.Key == key));
            var quantity = row is null ? 0 : count * row.Pack;
            var cost = row is null ? 0 : MallCost(row, count);
            if (row is null || count == 0 || cost > account.MallPoints || !Deliver(session, character, itemId, quantity))
            {
                logger.LogInformation("[{Session}] {Name}: mall line {Item} x{Count} (category {Category}) refused ({Points} points)", session.Id,
                    character.Name, itemId, count, category, account.MallPoints);
                session.Send(MallMessages.Bought(itemId, category, count, key, ok: false, session.Client));
                continue;
            }
            account.MallPoints -= cost;
            var earned = cost * Math.Max(0, options.Value.ItemMall.BonusPercent) / 100;
            if (earned > 0)
            {
                account.BonusPoints = Math.Min(MaxBonus, account.BonusPoints + earned);
                session.Send(MallMessages.Bonus(account.BonusPoints));
            }
            session.Send(MallMessages.Bought(itemId, category, count, key, ok: true, session.Client));
            session.Send(MallMessages.Deducted(account.MallPoints, cost, itemId, (byte)Math.Min(quantity, byte.MaxValue)));
            logger.LogInformation("[{Session}] {Name} buys {Item} x{Quantity} in the mall for {Cost} points ({Points} left)", session.Id,
                character.Name, items[itemId]?.Name, quantity, cost, account.MallPoints);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Highest bonus points an account can hold.</summary>
    public const int MaxBonus = 60000;

    /// <summary>Buys one row of the second list with bonus points.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnBonusBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld
            || session.Account is not { } account
            || session.ActiveCharacter is not { } character
            || body.Length < 7
            || session.Battle is not null)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        _ = r.U8();
        var (itemId, category, _, key) = (r.U16(), r.U8(), r.U8(), r.U16());
        var row = BonusCatalog().FirstOrDefault(c => c.ItemId == itemId && c.Category == category && (key == 0 || c.Key == key));
        var cost = row is null ? 0 : MallCost(row, 1);
        if (row is null || cost > account.BonusPoints || !Deliver(session, character, itemId, row.Pack))
        {
            logger.LogInformation(
                "[{Session}] {Name}: bonus line {Item} refused ({Points} bonus points)",
                session.Id,
                character.Name,
                itemId,
                account.BonusPoints);
            session.Send(MallMessages.Refused(3)); // code
            return ValueTask.CompletedTask;
        }
        account.BonusPoints -= cost;
        session.Send(MallMessages.BonusDeducted(account.BonusPoints, cost, itemId, (byte)Math.Min(row.Pack, (byte)255)));
        logger.LogInformation(
            "[{Session}] {Name} buys {Item} with {Cost} bonus points ({Points} left)",
            session.Id,
            character.Name,
            items[itemId]?.Name,
            cost,
            account.BonusPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>Answers a forge request with a failure; forging is not built.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnForge(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character)
            return ValueTask.CompletedTask;
        var slot = body.Length > 0 ? body.Span[0] : (byte)0;
        logger.LogInformation("[{Session}] {Name}: mall forge of bag slot {Slot} ({Item}) refused (not built)", session.Id, character.Name,
            slot, character.Bag.Find(b => b.Slot == slot) is { } b ? items[b.ItemId]?.Name : null);
        session.Send(MallMessages.ForgeFailed());
        return ValueTask.CompletedTask;
    }

    /// <summary>Buys rows of the open point store with mall points; stops at the first row the account cannot afford.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnPointStoreBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Account is not { } account || session.ActiveCharacter is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        var rows = OpenServices.Of(session, character, ServiceWindow.PointStore)?.Rows ?? []; // the point store the server opened on this map, out of battle
        var r = new PacketReader(body.Span);
        var n = r.U8();
        for (var i = 0; i < n; i++)
        {
            var (index, count) = (r.U8(), r.U8());
            if (index < 1 || index > rows.Count)
            {
                logger.LogWarning("[{Session}] point store: no row {Row}", session.Id, index);
                continue;
            }
            var row = rows[index - 1];
            var (quantity, cost) = PointStoreLine(row, count);
            if (cost > account.MallPoints)
            {
                session.Send(MallMessages.PointStoreNotEnough());
                break;
            }
            if (!Deliver(session, character, row.ItemId, quantity))
                continue;
            account.MallPoints -= cost;
            session.Send(MallMessages.PointStoreBought(row.ItemId, cost, (byte)Math.Min(quantity, byte.MaxValue)));
            logger.LogInformation("[{Session}] {Name} buys {Item} x{Quantity} in the point store for {Cost} points ({Points} left)", session.Id,
                character.Name, items[row.ItemId]?.Name, quantity, cost, account.MallPoints);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Answers a request to buy a player or pet level with points as busy; not built.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnPointStoreBuyLevel(GameSession session, ReadOnlyMemory<byte> body)
    {
        logger.LogInformation("[{Session}] point store: buying a level with points is not built, answered busy", session.Id);
        session.Send(MallMessages.PointStoreBusy());
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds an item to the character's bag if it all fits and sends the bag changes.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The buyer's character.</param>
    /// <param name="itemId">The item to add.</param>
    /// <param name="quantity">How many to add.</param>
    /// <returns>True when the items were delivered; false when the item is unknown or does not fit.</returns>
    private bool Deliver(GameSession session, Character character, ushort itemId, int quantity)
    {
        List<BagItem> trial = [
            .. character.Bag.Select(
                b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
        ];
        if (items[itemId] is null || Bag.Add(trial, items, itemId, quantity).Sum(c => c.Added) != quantity)
            return false;
        foreach (var (stack, added) in Bag.Add(character, items, itemId, quantity))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = itemId, Count = added, Wear = stack.Wear }));
        return true;
    }
}
