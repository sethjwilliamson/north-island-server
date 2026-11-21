using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.Shops;

/// <summary>Handles buying from and selling to an NPC store.</summary>
/// <param name="maps">Map data, to find the open store.</param>
/// <param name="items">Item data, for prices.</param>
/// <param name="logger">Logger.</param>
public sealed class ShopHandlers(
    MapData maps,
    ItemData items,
    ILogger<ShopHandlers> logger)
{
    /// <summary>
    /// Handles a sell request (<c>27/2</c>): removes the listed bag stacks and pays their sell price, or answers with a failure. Needs the sell
    /// window of the sale kind open (<see cref="OpenServices"/>), stacks of that kind not committed elsewhere (<see cref="BagLocks"/>), and room
    /// in the purse for the whole price.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the sale kind and the bag slots to sell.</param>
    internal ValueTask OnSell(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null)
            return ValueTask.CompletedTask;
        var request = SellRequest.Parse(body.Span);
        var window = request.SaleKind == 0 ? ServiceWindow.SellEquipment : ServiceWindow.SellGoods; // sale kind 0: equipment, else goods
        if (!OpenServices.IsOpen(session, character, window))
        {
            logger.LogWarning(
                "[{Session}] sell without an open {Window} window (map {Map}, window map {WindowMap}, in battle {Battle})",
                session.Id,
                window,
                character.MapId,
                OpenServices.MapOf(session, window),
                session.Battle is not null);
            session.Send(ShopMessages.SellResult(SellResult.Failed));
            return ValueTask.CompletedTask;
        }

        var stacks = new List<BagItem>();
        foreach (var slot in request.Slots.Distinct())
        {
            var stack = character.Bag.Find(b => b.Slot == slot);
            var record = stack is null ? null : items[stack.ItemId];
            var locked = BagLocks.LockedBy(session, slot);
            if (stack is null || record is null || record.IsEquipment != (request.SaleKind == 0) || locked is not null)
            {
                logger.LogWarning(
                    "[{Session}] sell: bag slot {Slot} cannot be sold here (kind {Kind}, {Locked})",
                    session.Id,
                    slot,
                    request.SaleKind,
                    locked ?? "empty or of the other kind");
                session.Send(ShopMessages.SellResult(SellResult.NotInInventory));
                return ValueTask.CompletedTask;
            }
            stacks.Add(stack);
        }
        if (stacks.Count == 0)
        {
            session.Send(ShopMessages.SellResult(SellResult.Failed));
            return ValueTask.CompletedTask;
        }

        var total = stacks.Sum(s => (long)s.Count * Math.Max(0, items[s.ItemId]!.SellPrice));
        if (character.Gold + total > BankHandlers.MaxGold)
        {
            logger.LogInformation(
                "[{Session}] {Name}: sale for {Total} gold does not fit the purse ({Gold})",
                session.Id,
                character.Name,
                total,
                character.Gold);
            session.Send(PlayerMessages.MoneyBagFull());
            session.Send(ShopMessages.SellResult(SellResult.Failed));
            return ValueTask.CompletedTask;
        }
        var sold = string.Join(", ", stacks.Select(s => $"{items[s.ItemId]!.Name} x{s.Count}"));
        foreach (var stack in stacks)
        {
            session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
            character.Bag.Remove(stack);
        }
        Purse.Credit(session, character, total);
        session.Send(ShopMessages.SellResult(SellResult.Ok));
        logger.LogInformation("[{Session}] {Name} sells {Items} for {Total} gold ({Gold} now)", session.Id, character.Name, sold, total, character.Gold);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles a buy request (<c>27/1</c>) and answers with the result.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the cart, rows and counts.</param>
    internal ValueTask OnBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null)
            return ValueTask.CompletedTask;

        var result = Buy(session, character, BuyRequest.Parse(body.Span));
        session.Send(ShopMessages.BuyResult(result));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Buys the cart: checks the open store, rows, gold and bag room, then charges and fills the bag, all or nothing. A row whose item has no
    /// price is refused, since it could be sold back for gold.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="request">The parsed cart.</param>
    /// <returns>The result code to send back.</returns>
    private BuyResult Buy(GameSession session, Character character, BuyRequest request)
    {
        var store = OpenStore(session, character);
        if (store is null)
        {
            logger.LogWarning("[{Session}] buy without an open store", session.Id);
            return BuyResult.Failed;
        }

        var rows = store.Entries.Where(e => e.Kind == 3).Select(e => e.Value).ToList();
        var lines = new List<(ushort ItemId, int Count, int Price)>();
        foreach (var (row, count) in request.Lines)
        {
            if (row < 1 || row > rows.Count || count == 0 || items[rows[row - 1]] is not { } item)
            {
                logger.LogWarning("[{Session}] buy: store {Store} has no row {Row}", session.Id, store.Index, row);
                return BuyResult.Failed;
            }
            if (item.Price <= 0)
            {
                logger.LogWarning("[{Session}] buy: row {Row} of store {Store} ({Item}) has no price", session.Id, row, store.Index, item.Name);
                return BuyResult.Failed;
            }
            lines.Add((item.Id, count, item.Price));
        }

        var total = lines.Sum(l => (long)l.Count * l.Price);
        if (total > character.Gold)
        {
            logger.LogInformation("[{Session}] {Name} cannot afford {Total} gold (has {Gold})", session.Id, character.Name, total, character.Gold);
            return BuyResult.NotEnoughMoney;
        }

        var trial = new Character
        {
            Bag = [
                .. character.Bag.Select(
                    b =>
                        new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
            ]
        };
        foreach (var (itemId, count, _) in lines)
        {
            var added = Bag.Add(trial, items, itemId, count).Sum(c => c.Added);
            if (added < count)
                return added == 0 && lines.Count == 1 ? BuyResult.InventoryFull : BuyResult.NotAllFit;
        }

        character.Gold -= (int)total;
        if (total > 0)
            session.Send(PlayerMessages.MoneyChange(-(int)total));
        foreach (var (itemId, count, _) in lines)
            foreach (var (stack, added) in Bag.Add(character, items, itemId, count))
                session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = itemId, Count = added, Wear = stack.Wear }));

        logger.LogInformation("[{Session}] {Name} buys {Lines} from store {Store} for {Total} gold ({Gold} left)", session.Id, character.Name,
            string.Join(", ", lines.Select(l => $"{items[l.ItemId]!.Name} x{l.Count}")), store.Index, total, character.Gold);
        return BuyResult.Ok;
    }

    /// <summary>
    /// Finds the store the player has open: the store dialog an NPC event last showed (<see cref="OpenServices"/>), looked up by its interact
    /// index on the map it was shown on, which must be the player's current map, out of battle.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <returns>The store interaction, or null when none is open.</returns>
    private EveInteract? OpenStore(GameSession session, Character character)
    {
        if (OpenServices.Of(session, character, ServiceWindow.Store) is not { } open)
            return null;
        var interact = maps.Scene(open.MapId)?.Interacts.FirstOrDefault(i => i.Index == open.Interact);
        return IsStore(interact) ? interact : null;
    }

    /// <summary>Whether an interaction of a map is a store dialog: an equipment store (kind 2) or a goods store (kind 3).</summary>
    /// <param name="interact">The interaction, or null.</param>
    /// <returns>True for a store.</returns>
    public static bool IsStore(EveInteract? interact) => interact is { Kind: 2 or 3 }; // 2 equipment store, 3 goods store
}
